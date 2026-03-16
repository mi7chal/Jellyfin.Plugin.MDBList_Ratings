using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MdbListRatings.Ratings.Models;

public sealed class TvMazeLookupResponse
{
    [JsonPropertyName("id")]
    public int? Id { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("rating")]
    public TvMazeRatingInfo? Rating { get; set; }
}

public sealed class TvMazeRatingInfo
{
    [JsonPropertyName("average")]
    public double? Average { get; set; }
}

public sealed class TvMazeEpisodeResponse
{
    [JsonPropertyName("id")]
    public int? Id { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("season")]
    public int? Season { get; set; }

    [JsonPropertyName("number")]
    public int? Number { get; set; }

    [JsonPropertyName("rating")]
    public TvMazeRatingInfo? Rating { get; set; }
}
