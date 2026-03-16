using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MdbListRatings.Ratings;

/// <summary>
/// Downloads and maintains a merged IMDb Top 250 cache based on the user-provided movie/show JSON feeds.
/// The merged cache is stored locally as top_250.json under the plugin cache directory.
/// </summary>
internal sealed class ImdbTop250Dataset
{
    internal const string MoviesUrl = "https://raw.githubusercontent.com/Druidblack/IMDB_Top_250/main/IMDb_top_250_movies.json";
    internal const string ShowsUrl = "https://raw.githubusercontent.com/Druidblack/IMDB_Top_250/main/IMDb_top_250_tv_shows.json";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _cacheDir;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private Dictionary<string, Top250Entry>? _entriesByImdb;
    private DateTimeOffset? _loadedFromUtc;

    public ImdbTop250Dataset(IHttpClientFactory httpClientFactory, string cacheDir, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _cacheDir = cacheDir;
        _logger = logger;

        Directory.CreateDirectory(_cacheDir);
    }

    private string CombinedPath => Path.Combine(_cacheDir, "top_250.json");

    public async Task EnsureReadyAsync(TimeSpan ttl, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(CombinedPath) && IsWithinTtl(CombinedPath, ttl))
            {
                await EnsureLoadedFromDiskAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            try
            {
                var merged = await DownloadAndMergeAsync(cancellationToken).ConfigureAwait(false);
                await SaveCombinedAsync(merged, cancellationToken).ConfigureAwait(false);
                SetMemoryCache(merged);
                _logger.LogInformation("IMDb Top 250: refreshed merged cache with {Count} entries.", merged.Items.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "IMDb Top 250: failed to refresh merged cache.");
                if (File.Exists(CombinedPath))
                {
                    await EnsureLoadedFromDiskAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                throw;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ImdbTop250Snapshot?> TryGetSnapshotAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(CombinedPath))
            {
                _entriesByImdb = new Dictionary<string, Top250Entry>(StringComparer.OrdinalIgnoreCase);
                _loadedFromUtc = null;
                return null;
            }

            await EnsureLoadedFromDiskAsync(cancellationToken).ConfigureAwait(false);
            return new ImdbTop250Snapshot
            {
                CachedAtUtc = _loadedFromUtc,
                Ids = (_entriesByImdb?.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()) ?? new List<string>()
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task EnsureLoadedFromDiskAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(CombinedPath))
        {
            _entriesByImdb = new Dictionary<string, Top250Entry>(StringComparer.OrdinalIgnoreCase);
            _loadedFromUtc = null;
            return;
        }

        var lastWriteUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(CombinedPath));
        if (_entriesByImdb is not null && _loadedFromUtc.HasValue && _loadedFromUtc.Value == lastWriteUtc)
        {
            return;
        }

        await using var fs = new FileStream(CombinedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var doc = await JsonDocument.ParseAsync(fs, cancellationToken: cancellationToken).ConfigureAwait(false);

        var env = ParseCombined(doc.RootElement);
        _entriesByImdb = env.Items
            .Where(x => !string.IsNullOrWhiteSpace(x.ImdbId))
            .GroupBy(x => x.ImdbId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        _loadedFromUtc = lastWriteUtc;
    }

    private async Task<Top250CacheEnvelope> DownloadAndMergeAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("IMDb Top 250: downloading source lists.");

        var movies = await DownloadEntriesAsync(MoviesUrl, "movies", cancellationToken).ConfigureAwait(false);
        var shows = await DownloadEntriesAsync(ShowsUrl, "tv_shows", cancellationToken).ConfigureAwait(false);

        var byImdb = new Dictionary<string, Top250Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in movies.Concat(shows))
        {
            if (string.IsNullOrWhiteSpace(entry.ImdbId))
            {
                continue;
            }

            if (!byImdb.ContainsKey(entry.ImdbId))
            {
                byImdb[entry.ImdbId] = entry;
            }
        }

        return new Top250CacheEnvelope
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Count = byImdb.Count,
            Items = byImdb.Values.OrderBy(x => x.ImdbId, StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    private async Task<List<Top250Entry>> DownloadEntriesAsync(string url, string expectedArrayProperty, CancellationToken cancellationToken)
    {
        var http = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(2);

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        return ParseSourceEntries(doc.RootElement, expectedArrayProperty);
    }

    private static List<Top250Entry> ParseSourceEntries(JsonElement root, string expectedArrayProperty)
    {
        var result = new List<Top250Entry>();
        foreach (var item in EnumerateItems(root, expectedArrayProperty))
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var imdbId = GetString(item, "imdb_id", "imdbId");
            if (string.IsNullOrWhiteSpace(imdbId))
            {
                continue;
            }

            imdbId = imdbId.Trim();
            if (!imdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Add(new Top250Entry
            {
                ImdbId = imdbId,
                Title = GetString(item, "title") ?? string.Empty,
                Year = GetString(item, "year") ?? string.Empty,
                Rating = GetString(item, "rating") ?? string.Empty
            });
        }

        return result;
    }

    private static IEnumerable<JsonElement> EnumerateItems(JsonElement root, string expectedArrayProperty)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                yield return item;
            }

            yield break;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        var candidates = new[]
        {
            expectedArrayProperty,
            "items",
            "movies",
            "tv_shows",
            "tvShows",
            "shows"
        };

        foreach (var name in candidates)
        {
            if (!TryGetProperty(root, name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in arr.EnumerateArray())
            {
                yield return item;
            }

            yield break;
        }
    }

    private static Top250CacheEnvelope ParseCombined(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return new Top250CacheEnvelope
            {
                GeneratedAtUtc = null,
                Count = 0,
                Items = ParseSourceEntries(root, "items")
            };
        }

        var env = new Top250CacheEnvelope
        {
            GeneratedAtUtc = TryGetDateTimeOffset(root, "generated_at_utc", "generatedAtUtc"),
            Count = TryGetInt32(root, "count") ?? 0,
            Items = new List<Top250Entry>()
        };

        if (TryGetProperty(root, "items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            env.Items = ParseSourceEntries(items, "items");
        }

        if (env.Count <= 0)
        {
            env.Count = env.Items.Count;
        }

        return env;
    }

    private async Task SaveCombinedAsync(Top250CacheEnvelope envelope, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_cacheDir);
        var tmpPath = CombinedPath + ".tmp";

        try
        {
            await using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(fs, new
                {
                    generated_at_utc = envelope.GeneratedAtUtc,
                    count = envelope.Count,
                    items = envelope.Items.Select(x => new
                    {
                        imdb_id = x.ImdbId,
                        title = x.Title,
                        year = x.Year,
                        rating = x.Rating
                    })
                }, new JsonSerializerOptions { WriteIndented = true }, cancellationToken).ConfigureAwait(false);
            }

            ReplaceFile(tmpPath, CombinedPath);
        }
        finally
        {
            TryDelete(tmpPath);
        }
    }

    private void SetMemoryCache(Top250CacheEnvelope envelope)
    {
        _entriesByImdb = envelope.Items
            .Where(x => !string.IsNullOrWhiteSpace(x.ImdbId))
            .GroupBy(x => x.ImdbId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        _loadedFromUtc = File.Exists(CombinedPath) ? new DateTimeOffset(File.GetLastWriteTimeUtc(CombinedPath)) : envelope.GeneratedAtUtc;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }

            if (value.ValueKind == JsonValueKind.Number || value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            {
                return value.ToString();
            }
        }

        return null;
    }

    private static int? TryGetInt32(JsonElement element, params string[] names)
    {
        var raw = GetString(element, names);
        return int.TryParse(raw, out var value) ? value : null;
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement element, params string[] names)
    {
        var raw = GetString(element, names);
        return DateTimeOffset.TryParse(raw, out var value) ? value : null;
    }

    private static bool IsWithinTtl(string path, TimeSpan ttl)
    {
        try
        {
            var lastWrite = new DateTimeOffset(File.GetLastWriteTimeUtc(path));
            return (DateTimeOffset.UtcNow - lastWrite) <= ttl;
        }
        catch
        {
            return false;
        }
    }

    private static void ReplaceFile(string src, string dst)
    {
        try
        {
            if (File.Exists(dst))
            {
                File.Delete(dst);
            }
        }
        catch
        {
            // ignore
        }

        File.Move(src, dst, true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore
        }
    }

    private sealed class Top250CacheEnvelope
    {
        public DateTimeOffset? GeneratedAtUtc { get; set; }
        public int Count { get; set; }
        public List<Top250Entry> Items { get; set; } = new();
    }

    private sealed class Top250Entry
    {
        public string ImdbId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Year { get; set; } = string.Empty;
        public string Rating { get; set; } = string.Empty;
    }
}

internal sealed class ImdbTop250Snapshot
{
    public DateTimeOffset? CachedAtUtc { get; init; }
    public List<string> Ids { get; init; } = new();
}
