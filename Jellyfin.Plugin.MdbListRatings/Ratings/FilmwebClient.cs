using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MdbListRatings.Ratings;

internal sealed class FilmwebClient
{
    internal sealed class FilmwebLookupResult
    {
        public double AverageRating { get; init; }
        public int? Votes { get; init; }
        public string? Url { get; init; }
        public string? FilmwebId { get; init; }
    }

    private sealed class CacheBox
    {
        public DateTimeOffset CachedAtUtc { get; init; }
        public FilmwebLookupResult? Value { get; init; }
    }

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, CacheBox> _lookupCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTimeOffset _nextAllowedUtc = DateTimeOffset.MinValue;
    private static readonly TimeSpan MinSpacing = TimeSpan.FromMilliseconds(850);
    private static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromHours(8);
    private static readonly Regex ScriptJsonLdRegex = new("<script[^>]+type=\"application/ld\\+json\"[^>]*>(?<json>[\\s\\S]*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DataRateRegex = new("class=\"filmRating[^\\\"]*\"[^>]*data-rate=\"(?<value>[0-9]+(?:[\\.,][0-9]+)?)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DataCountRegex = new("class=\"filmRating[^\\\"]*\"[^>]*data-count=\"(?<value>[0-9\\s]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RatingValueFallbackRegex = new("\"ratingValue\"\\s*:\\s*\"?(?<value>[0-9]+(?:[\\.,][0-9]+)?)\"?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RatingCountFallbackRegex = new("\"ratingCount\"\\s*:\\s*\"?(?<value>[0-9\\s]+)\"?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FilmwebIdRegex = new("-(?<id>[0-9]+)(?:\\?|#|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public FilmwebClient(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<FilmwebLookupResult?> LookupAsync(string contentType, string? imdbId, string? title, int? year, TimeSpan ttl, CancellationToken cancellationToken)
    {
        var normalizedContentType = NormalizeContentType(contentType);
        if (normalizedContentType is null)
        {
            return null;
        }

        imdbId = NormalizeImdbId(imdbId);
        title = NormalizeTitle(title);
        var cacheKey = $"{normalizedContentType}:{imdbId ?? "-"}:{title ?? "-"}:{(year.HasValue ? year.Value.ToString(CultureInfo.InvariantCulture) : "-")}";
        var effectiveTtl = ttl > TimeSpan.Zero ? ttl : DefaultCacheTtl;
        if (_lookupCache.TryGetValue(cacheKey, out var cached) && (DateTimeOffset.UtcNow - cached.CachedAtUtc) <= effectiveTtl)
        {
            return cached.Value;
        }

        var result = await LookupCoreAsync(normalizedContentType, imdbId, title, year, cancellationToken).ConfigureAwait(false);
        _lookupCache[cacheKey] = new CacheBox
        {
            CachedAtUtc = DateTimeOffset.UtcNow,
            Value = result
        };
        return result;
    }

    private async Task<FilmwebLookupResult?> LookupCoreAsync(string contentType, string? imdbId, string? title, int? year, CancellationToken cancellationToken)
    {
        var queries = BuildSearchQueries(imdbId, title, year);
        foreach (var query in queries)
        {
            var hit = await SearchBestHitAsync(contentType, query, title, cancellationToken).ConfigureAwait(false);
            if (hit is null)
            {
                continue;
            }

            // Stable Filmweb resolver: /film?id=<id> redirects to canonical /film|/serial/... page.
            var candidateUrl = $"https://www.filmweb.pl/film?id={hit.Id.ToString(CultureInfo.InvariantCulture)}";
            var result = await TryParseRatingPageAsync(candidateUrl, hit.Id.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    private async Task<FilmwebLookupResult?> TryParseRatingPageAsync(string url, string? fallbackFilmwebId, CancellationToken cancellationToken)
    {
        var html = await GetStringWithThrottleAsync(url, cancellationToken, false).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        if (!TryExtractRating(html, out var rating, out var votes))
        {
            return null;
        }

        var filmwebId = TryExtractFilmwebId(url) ?? NormalizeDigits(fallbackFilmwebId);
        return new FilmwebLookupResult
        {
            AverageRating = rating,
            Votes = votes,
            Url = url,
            FilmwebId = filmwebId
        };
    }

    private async Task<string?> GetStringWithThrottleAsync(string url, CancellationToken cancellationToken, bool expectJson)
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
                request.Headers.UserAgent.ParseAdd("Jellyfin.Plugin.MdbListRatings/1.0 (+filmweb)");
                if (expectJson)
                {
                    request.Headers.Accept.ParseAdd("application/json,text/plain,*/*");
                }
                else
                {
                    request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                }

                request.Headers.AcceptLanguage.ParseAdd("pl-PL,pl;q=0.9,en-US;q=0.7,en;q=0.5");

                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                _nextAllowedUtc = DateTimeOffset.UtcNow.Add(MinSpacing);

                if ((int)response.StatusCode == 404)
                {
                    return null;
                }

                if ((int)response.StatusCode == 429)
                {
                    var delay = TimeSpan.FromSeconds(3 + (attempt * 2));
                    _nextAllowedUtc = DateTimeOffset.UtcNow.Add(delay);
                    _logger.LogWarning("Filmweb request rate limited for {Url}. Backing off for {Delay}.", url, delay);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Filmweb request failed: {Status} {Reason} for {Url}", (int)response.StatusCode, response.ReasonPhrase, url);
                    return null;
                }

                return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Filmweb request error for {Url}", url);
                return null;
            }
            finally
            {
                Gate.Release();
            }
        }

        return null;
    }

    private async Task<SearchHit?> SearchBestHitAsync(string contentType, string query, string? expectedTitle, CancellationToken cancellationToken)
    {
        var searchUrl = $"https://www.filmweb.pl/api/v1/search?q={Uri.EscapeDataString(query)}";
        var json = await GetStringWithThrottleAsync(searchUrl, cancellationToken, true).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<SearchResponse>(json, JsonOptions);
            if (payload?.SearchHits is null || payload.SearchHits.Count == 0)
            {
                return null;
            }

            var expectedType = contentType == "movie" ? "film" : "serial";
            var candidates = payload.SearchHits
                .Where(h => h is not null && h.Id > 0 && string.Equals(h.Type, expectedType, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (candidates.Count == 0)
            {
                return null;
            }

            var normalizedExpected = NormalizeTitle(expectedTitle);
            if (string.IsNullOrWhiteSpace(normalizedExpected))
            {
                return candidates[0];
            }

            SearchHit? best = null;
            var bestScore = int.MinValue;
            foreach (var candidate in candidates)
            {
                var score = ScoreTitleMatch(normalizedExpected!, candidate.MatchedTitle);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            return best ?? candidates[0];
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Filmweb search JSON parse failed for query '{Query}'", query);
            return null;
        }
    }

    private static bool TryExtractRating(string html, out double rating0To10, out int? votes)
    {
        rating0To10 = 0;
        votes = null;

        var dataRateMatch = DataRateRegex.Match(html);
        if (dataRateMatch.Success && TryParseDouble(dataRateMatch.Groups["value"].Value, out var dataRate) && dataRate > 0)
        {
            rating0To10 = dataRate;
            var dataCountMatch = DataCountRegex.Match(html);
            if (dataCountMatch.Success && TryParseInt(dataCountMatch.Groups["value"].Value, out var dataVotes))
            {
                votes = dataVotes;
            }
            return true;
        }

        foreach (Match match in ScriptJsonLdRegex.Matches(html))
        {
            var json = (match.Groups["json"].Value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(json))
            {
                continue;
            }

            if (TryExtractFromJsonLd(json, out rating0To10, out votes))
            {
                return true;
            }
        }

        var ratingMatch = RatingValueFallbackRegex.Match(html);
        if (ratingMatch.Success && TryParseDouble(ratingMatch.Groups["value"].Value, out var fallbackRating) && fallbackRating > 0)
        {
            rating0To10 = fallbackRating;
            var votesMatch = RatingCountFallbackRegex.Match(html);
            if (votesMatch.Success && TryParseInt(votesMatch.Groups["value"].Value, out var fallbackVotes))
            {
                votes = fallbackVotes;
            }
            return true;
        }

        return false;
    }

    private static bool TryExtractFromJsonLd(string json, out double rating0To10, out int? votes)
    {
        rating0To10 = 0;
        votes = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!TryFindAggregateRating(doc.RootElement, out var ratingValue, out var ratingCount))
            {
                return false;
            }

            if (!ratingValue.HasValue || ratingValue.Value <= 0)
            {
                return false;
            }

            rating0To10 = ratingValue.Value;
            votes = ratingCount > 0 ? ratingCount : null;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryFindAggregateRating(JsonElement element, out double? ratingValue, out int ratingCount)
    {
        ratingValue = null;
        ratingCount = 0;

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindAggregateRating(item, out ratingValue, out ratingCount))
                {
                    return true;
                }
            }

            return false;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (element.TryGetProperty("aggregateRating", out var agg) && agg.ValueKind == JsonValueKind.Object)
        {
            if (agg.TryGetProperty("ratingValue", out var rv))
            {
                ratingValue = ReadDouble(rv);
            }

            if (agg.TryGetProperty("ratingCount", out var rc))
            {
                ratingCount = ReadInt(rc);
            }

            if (ratingValue.HasValue && ratingValue.Value > 0)
            {
                return true;
            }
        }

        foreach (var prop in element.EnumerateObject())
        {
            if (TryFindAggregateRating(prop.Value, out ratingValue, out ratingCount))
            {
                return true;
            }
        }

        return false;
    }

    private static int ScoreTitleMatch(string expectedTitle, string? candidateTitle)
    {
        if (string.IsNullOrWhiteSpace(candidateTitle))
        {
            return 0;
        }

        var expected = NormalizeForMatch(expectedTitle);
        var candidate = NormalizeForMatch(candidateTitle);
        if (expected.Length == 0 || candidate.Length == 0)
        {
            return 0;
        }

        if (string.Equals(expected, candidate, StringComparison.Ordinal))
        {
            return 1000;
        }

        if (candidate.Contains(expected, StringComparison.Ordinal))
        {
            return 600;
        }

        if (expected.Contains(candidate, StringComparison.Ordinal))
        {
            return 500;
        }

        var commonPrefix = 0;
        var len = Math.Min(expected.Length, candidate.Length);
        while (commonPrefix < len && expected[commonPrefix] == candidate[commonPrefix])
        {
            commonPrefix++;
        }

        return commonPrefix;
    }

    private static List<string> BuildSearchQueries(string? imdbId, string? title, int? year)
    {
        var queries = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(imdbId))
        {
            queries.Add(imdbId!);
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            var titleOnly = title!;
            var titleWithYear = year.HasValue && year.Value > 1800
                ? $"{titleOnly} {year.Value.ToString(CultureInfo.InvariantCulture)}"
                : null;

            if (!string.IsNullOrWhiteSpace(titleWithYear))
            {
                queries.Add(titleWithYear!);
            }

            queries.Add(titleOnly);
        }

        return queries
            .Select(q => (q ?? string.Empty).Trim())
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeForMatch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var v = value.Trim().ToLowerInvariant();
        var sb = new System.Text.StringBuilder(v.Length);
        foreach (var ch in v)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
            else if (char.IsWhiteSpace(ch))
            {
                sb.Append(' ');
            }
        }

        return sb.ToString().Replace("  ", " ", StringComparison.Ordinal).Trim();
    }

    private static string? TryExtractFilmwebId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var match = FilmwebIdRegex.Match(url.Trim());
        return match.Success ? match.Groups["id"].Value : null;
    }

    private sealed class SearchResponse
    {
        public List<SearchHit> SearchHits { get; set; } = new();
    }

    private sealed class SearchHit
    {
        public int Id { get; set; }
        public string? Type { get; set; }
        public string? MatchedTitle { get; set; }
    }

    private static string? NormalizeContentType(string? contentType)
    {
        var ct = (contentType ?? string.Empty).Trim().ToLowerInvariant();
        return ct is "movie" or "show" ? ct : null;
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

    private static string? NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var t = title.Trim();
        return t.Length == 0 ? null : t;
    }

    private static string? NormalizeDigits(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var sb = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsDigit(ch))
            {
                sb.Append(ch);
            }
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    private static bool TryParseDouble(string? value, out double parsed)
    {
        parsed = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim().Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed);
    }

    private static bool TryParseInt(string? value, out int parsed)
    {
        parsed = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var compact = value.Replace(" ", string.Empty, StringComparison.Ordinal).Trim();
        return int.TryParse(compact, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed);
    }

    private static double? ReadDouble(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetDouble(out var d) ? d : null,
            JsonValueKind.String => TryParseDouble(el.GetString(), out var d2) ? d2 : null,
            _ => null
        };
    }

    private static int ReadInt(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetInt32(out var i) ? i : 0,
            JsonValueKind.String => TryParseInt(el.GetString(), out var i2) ? i2 : 0,
            _ => 0
        };
    }
}
