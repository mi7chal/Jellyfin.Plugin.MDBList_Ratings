using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MdbListRatings.Ratings.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MdbListRatings.Ratings;

internal sealed class TvMazeClient
{
    internal sealed class TvMazeLookupResult
    {
        public int? ShowId { get; init; }
        public double AverageRating { get; init; }
        public string? Url { get; init; }
    }

    internal sealed class TvMazeEpisodeLookupResult
    {
        public int? ShowId { get; init; }
        public double AverageRating { get; init; }
        public string? Url { get; init; }
    }

    private sealed class CacheBox<T>
    {
        public DateTimeOffset CachedAtUtc { get; init; }
        public T Value { get; init; } = default!;
    }

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<string, CacheBox<TvMazeLookupResult?>> _showLookupCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, CacheBox<IReadOnlyList<TvMazeEpisodeResponse>?>> _episodeListCache = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTimeOffset _nextAllowedUtc = DateTimeOffset.MinValue;
    private static readonly TimeSpan MinSpacing = TimeSpan.FromMilliseconds(550);
    private static readonly TimeSpan DefaultLookupCacheTtl = TimeSpan.FromHours(1);

    public TvMazeClient(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<TvMazeLookupResult?> LookupShowAsync(string? imdbId, string? tvdbId, CancellationToken cancellationToken)
    {
        imdbId = NormalizeImdbId(imdbId);
        if (!string.IsNullOrWhiteSpace(imdbId))
        {
            var cacheKey = $"imdb:{imdbId}";
            var cached = TryGetFresh(_showLookupCache, cacheKey, DefaultLookupCacheTtl);
            if (cached.HasValue)
            {
                return cached.Value;
            }

            var fetched = await LookupByUrlAsync(
                $"https://api.tvmaze.com/lookup/shows?imdb={Uri.EscapeDataString(imdbId)}",
                $"imdb={imdbId}",
                cancellationToken).ConfigureAwait(false);

            _showLookupCache[cacheKey] = new CacheBox<TvMazeLookupResult?>
            {
                CachedAtUtc = DateTimeOffset.UtcNow,
                Value = fetched
            };

            return fetched;
        }

        tvdbId = NormalizeDigits(tvdbId);
        if (!string.IsNullOrWhiteSpace(tvdbId))
        {
            var cacheKey = $"tvdb:{tvdbId}";
            var cached = TryGetFresh(_showLookupCache, cacheKey, DefaultLookupCacheTtl);
            if (cached.HasValue)
            {
                return cached.Value;
            }

            var fetched = await LookupByUrlAsync(
                $"https://api.tvmaze.com/lookup/shows?thetvdb={Uri.EscapeDataString(tvdbId)}",
                $"thetvdb={tvdbId}",
                cancellationToken).ConfigureAwait(false);

            _showLookupCache[cacheKey] = new CacheBox<TvMazeLookupResult?>
            {
                CachedAtUtc = DateTimeOffset.UtcNow,
                Value = fetched
            };

            return fetched;
        }

        return null;
    }

    public async Task<TvMazeEpisodeLookupResult?> LookupEpisodeAsync(
        string? imdbId,
        string? tvdbId,
        int seasonNumber,
        int episodeNumber,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        if (seasonNumber < 0 || episodeNumber <= 0)
        {
            return null;
        }

        var show = await LookupShowAsync(imdbId, tvdbId, cancellationToken).ConfigureAwait(false);
        if (show?.ShowId is not int showId || showId <= 0)
        {
            return null;
        }

        var episodes = await GetEpisodesForShowAsync(showId, ttl, cancellationToken).ConfigureAwait(false);
        if (episodes is null || episodes.Count == 0)
        {
            return null;
        }

        foreach (var entry in episodes)
        {
            if (entry is null)
            {
                continue;
            }

            if (entry.Season != seasonNumber || entry.Number != episodeNumber)
            {
                continue;
            }

            var avg = entry.Rating?.Average;
            if (!avg.HasValue || double.IsNaN(avg.Value) || double.IsInfinity(avg.Value) || avg.Value <= 0)
            {
                return null;
            }

            var url = string.IsNullOrWhiteSpace(entry.Url)
                ? (entry.Id.HasValue ? $"https://www.tvmaze.com/episodes/{entry.Id.Value}" : null)
                : entry.Url;

            return new TvMazeEpisodeLookupResult
            {
                ShowId = showId,
                AverageRating = avg.Value,
                Url = url
            };
        }

        return null;
    }

    private async Task<IReadOnlyList<TvMazeEpisodeResponse>?> GetEpisodesForShowAsync(int showId, TimeSpan ttl, CancellationToken cancellationToken)
    {
        var effectiveTtl = ttl <= TimeSpan.Zero ? DefaultLookupCacheTtl : ttl;
        var cached = TryGetFresh(_episodeListCache, showId, effectiveTtl);
        if (cached.HasValue)
        {
            return cached.Value;
        }

        var fetched = await GetEpisodesByUrlAsync(
            $"https://api.tvmaze.com/shows/{showId}/episodes?specials=1",
            $"showId={showId}",
            cancellationToken).ConfigureAwait(false);

        _episodeListCache[showId] = new CacheBox<IReadOnlyList<TvMazeEpisodeResponse>?>
        {
            CachedAtUtc = DateTimeOffset.UtcNow,
            Value = fetched
        };

        return fetched;
    }

    private async Task<TvMazeLookupResult?> LookupByUrlAsync(string url, string lookupLabel, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var wait = _nextAllowedUtc - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                }

                var http = _httpClientFactory.CreateClient();
                http.Timeout = TimeSpan.FromSeconds(20);

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("Jellyfin.Plugin.MdbListRatings/1.0 (+tvmaze)");

                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                _nextAllowedUtc = DateTimeOffset.UtcNow.Add(MinSpacing);

                if ((int)response.StatusCode == 429)
                {
                    var delay = TimeSpan.FromSeconds(3 + (attempt * 2));
                    _logger.LogWarning("TVMaze request rate limited for {Lookup}. Backing off for {Delay}.", lookupLabel, delay);
                    _nextAllowedUtc = DateTimeOffset.UtcNow.Add(delay);
                    continue;
                }

                if ((int)response.StatusCode == 404)
                {
                    return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("TVMaze request failed: {Status} {Reason} for {Lookup}", (int)response.StatusCode, response.ReasonPhrase, lookupLabel);
                    return null;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var data = await JsonSerializer.DeserializeAsync<TvMazeLookupResponse>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
                if (data is null)
                {
                    return null;
                }

                var average = data.Rating?.Average;
                var urlFromApi = string.IsNullOrWhiteSpace(data.Url) ? (data.Id.HasValue ? $"https://www.tvmaze.com/shows/{data.Id.Value}" : null) : data.Url;
                return new TvMazeLookupResult
                {
                    ShowId = data.Id,
                    AverageRating = average.HasValue && !double.IsNaN(average.Value) && !double.IsInfinity(average.Value) && average.Value > 0 ? average.Value : 0,
                    Url = urlFromApi
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TVMaze request error for {Lookup}", lookupLabel);
                return null;
            }
            finally
            {
                Gate.Release();
            }
        }

        return null;
    }

    private async Task<IReadOnlyList<TvMazeEpisodeResponse>?> GetEpisodesByUrlAsync(string url, string lookupLabel, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var wait = _nextAllowedUtc - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                }

                var http = _httpClientFactory.CreateClient();
                http.Timeout = TimeSpan.FromSeconds(25);

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("Jellyfin.Plugin.MdbListRatings/1.0 (+tvmaze)");

                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                _nextAllowedUtc = DateTimeOffset.UtcNow.Add(MinSpacing);

                if ((int)response.StatusCode == 429)
                {
                    var delay = TimeSpan.FromSeconds(3 + (attempt * 2));
                    _logger.LogWarning("TVMaze request rate limited for {Lookup}. Backing off for {Delay}.", lookupLabel, delay);
                    _nextAllowedUtc = DateTimeOffset.UtcNow.Add(delay);
                    continue;
                }

                if ((int)response.StatusCode == 404)
                {
                    return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("TVMaze episodes request failed: {Status} {Reason} for {Lookup}", (int)response.StatusCode, response.ReasonPhrase, lookupLabel);
                    return null;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var data = await JsonSerializer.DeserializeAsync<List<TvMazeEpisodeResponse>>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
                return data;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TVMaze episodes request error for {Lookup}", lookupLabel);
                return null;
            }
            finally
            {
                Gate.Release();
            }
        }

        return null;
    }

    private static Optional<T> TryGetFresh<TKey, T>(ConcurrentDictionary<TKey, CacheBox<T>> cache, TKey key, TimeSpan ttl)
        where TKey : notnull
    {
        if (cache.TryGetValue(key, out var box) && (DateTimeOffset.UtcNow - box.CachedAtUtc) <= ttl)
        {
            return new Optional<T>(box.Value, true);
        }

        return default;
    }

    private readonly struct Optional<T>
    {
        public Optional(T value, bool hasValue)
        {
            Value = value;
            HasValue = hasValue;
        }

        public T Value { get; }
        public bool HasValue { get; }
    }

    private static string? NormalizeImdbId(string? imdbId)
    {
        if (string.IsNullOrWhiteSpace(imdbId))
        {
            return null;
        }

        var trimmed = imdbId.Trim();
        if (trimmed.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
        {
            return "tt" + NormalizeDigits(trimmed[2..]);
        }

        var digits = NormalizeDigits(trimmed);
        return string.IsNullOrWhiteSpace(digits) ? null : "tt" + digits;
    }

    private static string? NormalizeDigits(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        var chars = s.Trim().ToCharArray();
        var buffer = new System.Text.StringBuilder(chars.Length);
        foreach (var ch in chars)
        {
            if (char.IsDigit(ch))
            {
                buffer.Append(ch);
            }
        }

        return buffer.Length == 0 ? null : buffer.ToString();
    }
}
