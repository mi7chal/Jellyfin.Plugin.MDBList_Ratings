using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;

namespace Jellyfin.Plugin.MdbListRatings.Awards;

/// <summary>
/// Loads award datasets and resolves award matches by IMDb id.
/// Built-in datasets are embedded into the plugin, while custom datasets/icons can be dropped
/// into the plugin data directory without recompiling the plugin.
/// Supported formats:
///  - normalized JSON datasets (legacy)
///  - IMDb event YAML files (*.yml / *.yaml)
/// </summary>
public sealed class AwardCatalogService
{
    private static readonly TimeSpan ReloadInterval = TimeSpan.FromMinutes(5);
    private static readonly string[] SupportedDataExtensions = [".json", ".yml", ".yaml"];
    private static readonly Dictionary<string, KnownYamlDataset> KnownYamlDatasets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ev0000003"] = new KnownYamlDataset
        {
            Key = "oscar",
            Name = "Oscar",
            IconFile = "academy_awards_usa.png"
        },
        ["ev0000010"] = new KnownYamlDataset
        {
            Key = "adult_video_news_awards",
            Name = "Adult Video News Awards",
            IconFile = "adult_video_news_awards.png"
        },
        ["ev0000032"] = new KnownYamlDataset
        {
            Key = "annie_awards",
            Name = "Annie Awards",
            IconFile = "annie_awards.png"
        },
        ["ev0000091"] = new KnownYamlDataset
        {
            Key = "berlin_international_film_festival",
            Name = "Berlin International Film Festival",
            IconFile = "berlin_international_film_festival.png"
        },
        ["ev0000123"] = new KnownYamlDataset
        {
            Key = "bafta_awards",
            Name = "BAFTA Awards",
            IconFile = "bafta_awards.png"
        },
        ["ev0000133"] = new KnownYamlDataset
        {
            Key = "critics_choice_awards",
            Name = "Critics Choice Awards",
            IconFile = "critics_choice_awards.png"
        },
        ["ev0000147"] = new KnownYamlDataset
        {
            Key = "cannes_film_festival",
            Name = "Cannes Film Festival",
            IconFile = "cannes_film_festival.png"
        },
        ["ev0000157"] = new KnownYamlDataset
        {
            Key = "cesar_awards_france",
            Name = "César Awards, France",
            IconFile = "cesar_awards_france.png"
        },
        ["ev0000223"] = new KnownYamlDataset
        {
            Key = "primetime_emmy_awards",
            Name = "Primetime Emmy Awards",
            IconFile = "primetime_emmy_awards.png"
        },
        ["ev0000245"] = new KnownYamlDataset
        {
            Key = "filmfare_awards",
            Name = "Filmfare Awards",
            IconFile = "filmfare_awards.png"
        },
        ["ev0000280"] = new KnownYamlDataset
        {
            Key = "german_film_awards",
            Name = "German Film Awards",
            IconFile = "german_film_awards.png"
        },
        ["ev0000292"] = new KnownYamlDataset
        {
            Key = "golden_globes_usa",
            Name = "Golden Globes, USA",
            IconFile = "golden_globes_usa.png"
        },
        ["ev0000329"] = new KnownYamlDataset
        {
            Key = "hong_kong_film_awards",
            Name = "Hong Kong Film Awards",
            IconFile = "hong_kong_film_awards.png"
        },
        ["ev0000349"] = new KnownYamlDataset
        {
            Key = "film_independent_spirit_awards",
            Name = "Film Independent Spirit Awards",
            IconFile = "film_independent_spirit_awards.png"
        },
        ["ev0000453"] = new KnownYamlDataset
        {
            Key = "mtv_movie_tv_awards",
            Name = "MTV Movie + TV Awards",
            IconFile = "mtv_movie_tv_awards.png"
        },
        ["ev0000468"] = new KnownYamlDataset
        {
            Key = "national_film_preservation_board_usa",
            Name = "National Film Preservation Board, USA",
            IconFile = "national_film_preservation_board_usa.png"
        },
        ["ev0000530"] = new KnownYamlDataset
        {
            Key = "peoples_choice_awards_usa",
            Name = "People's Choice Awards, USA",
            IconFile = "peoples_choice_awards_usa.png"
        },
        ["ev0000558"] = new KnownYamlDataset
        {
            Key = "razzie_awards",
            Name = "Razzie Awards",
            IconFile = "razzie_awards.png"
        },
        ["ev0000598"] = new KnownYamlDataset
        {
            Key = "actor_awards",
            Name = "Actor Awards",
            IconFile = "actor_awards.png"
        },
        ["ev0000631"] = new KnownYamlDataset
        {
            Key = "sundance_film_festival",
            Name = "Sundance Film Festival",
            IconFile = "sundance_film_festival.png"
        },
        ["ev0000659"] = new KnownYamlDataset
        {
            Key = "toronto_international_film_festival",
            Name = "Toronto International Film Festival",
            IconFile = "toronto_international_film_festival.png"
        },
        ["ev0000681"] = new KnownYamlDataset
        {
            Key = "venice_film_festival",
            Name = "Venice Film Festival",
            IconFile = "venice_film_festival.png"
        },
        ["ev0000714"] = new KnownYamlDataset
        {
            Key = "x_rated_critics_organization_usa",
            Name = "X-Rated Critics' Organization, USA",
            IconFile = "x_rated_critics_organization_usa.png"
        },
        ["ev0002080"] = new KnownYamlDataset
        {
            Key = "xbiz_awards",
            Name = "XBIZ Awards",
            IconFile = "xbiz_awards.png"
        },
        ["ev0025711"] = new KnownYamlDataset
        {
            Key = "crunchyroll",
            Name = "Crunchyroll Anime Awards",
            IconFile = "crunchyroll_anime_awards.png"
        },
        ["ev0057191"] = new KnownYamlDataset
        {
            Key = "iconic_gold_awards",
            Name = "Iconic Gold Awards",
            IconFile = "iconic_gold_awards.png"
        },
        ["ev0073196"] = new KnownYamlDataset
        {
            Key = "xbiz_europa_awards",
            Name = "XBIZ Europa Awards",
            IconFile = "xbiz_europa_awards.png"
        }
    };

    private readonly string _pluginDataPath;
    private readonly string _customAwardsDirectory;
    private readonly string _customIconsDirectory;
    private readonly ILogger<AwardCatalogService> _logger;
    private readonly object _sync = new();

    private Snapshot? _snapshot;
    private DateTimeOffset _lastLoadedUtc = DateTimeOffset.MinValue;

    public AwardCatalogService(string pluginDataPath, ILogger<AwardCatalogService> logger)
    {
        _pluginDataPath = pluginDataPath ?? throw new ArgumentNullException(nameof(pluginDataPath));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _customAwardsDirectory = Path.Combine(_pluginDataPath, "awards");
        _customIconsDirectory = Path.Combine(_customAwardsDirectory, "icons");

        Directory.CreateDirectory(_customAwardsDirectory);
        Directory.CreateDirectory(_customIconsDirectory);
    }

    public string CustomAwardsDirectory => _customAwardsDirectory;

    public string CustomIconsDirectory => _customIconsDirectory;

    public IReadOnlyList<AwardDefinitionInfo> GetDefinitions()
    {
        var snapshot = GetSnapshot();
        return snapshot.Definitions
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => new AwardDefinitionInfo
            {
                Key = x.Key,
                Name = x.Name,
                IconFile = x.IconFile,
                EntryCount = x.EntriesByImdb.Count,
                IsBuiltIn = x.IsBuiltIn
            })
            .ToList();
    }

    public IReadOnlyList<AwardBadgeMatch> FindAwardsByImdb(string imdbId, IEnumerable<string>? selectedKeys = null)
    {
        imdbId = NormalizeImdbId(imdbId);
        if (string.IsNullOrWhiteSpace(imdbId))
        {
            return Array.Empty<AwardBadgeMatch>();
        }

        var filter = selectedKeys?
            .Select(x => NormalizeKey(x))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var snapshot = GetSnapshot();
        var results = new List<AwardBadgeMatch>();

        foreach (var definition in snapshot.Definitions.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (filter is not null && filter.Count > 0 && !filter.Contains(definition.Key))
            {
                continue;
            }

            if (!definition.EntriesByImdb.TryGetValue(imdbId, out var entry) || !HasWins(entry))
            {
                continue;
            }

            results.Add(new AwardBadgeMatch
            {
                Key = definition.Key,
                Name = definition.Name,
                IconFile = definition.IconFile,
                AwardYears = entry.AwardYears,
                WonCategories = entry.WonCategories,
                Tooltip = BuildTooltip(definition.Name, entry)
            });
        }

        return results;
    }

    public AwardNominationSummaryMatch? GetNominationSummaryByImdb(string imdbId, IEnumerable<string>? selectedKeys = null)
    {
        imdbId = NormalizeImdbId(imdbId);
        if (string.IsNullOrWhiteSpace(imdbId))
        {
            return null;
        }

        var filter = selectedKeys?
            .Select(x => NormalizeKey(x))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var snapshot = GetSnapshot();
        var awards = new List<AwardNominationSummaryAward>();

        foreach (var definition in snapshot.Definitions.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (filter is not null && filter.Count > 0 && !filter.Contains(definition.Key))
            {
                continue;
            }

            if (!definition.EntriesByImdb.TryGetValue(imdbId, out var entry) || !HasNominations(entry))
            {
                continue;
            }

            var grouped = GroupNominationsByYear(entry);
            if (grouped.Count == 0)
            {
                continue;
            }

            awards.Add(new AwardNominationSummaryAward
            {
                AwardName = definition.Name,
                Groups = grouped
            });
        }

        if (awards.Count == 0)
        {
            return null;
        }

        return new AwardNominationSummaryMatch
        {
            Name = "Nominations",
            Tooltip = BuildNominationSummaryTooltip(awards),
            AwardCount = awards.Count,
            CategoryCount = awards.Sum(x => x.Groups.Sum(g => g.Categories.Count))
        };
    }

    private Snapshot GetSnapshot()
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            if (_snapshot is not null && now - _lastLoadedUtc < ReloadInterval)
            {
                return _snapshot;
            }

            _snapshot = LoadSnapshot();
            _lastLoadedUtc = now;
            return _snapshot;
        }
    }

    private Snapshot LoadSnapshot()
    {
        var definitions = new Dictionary<string, AwardDefinition>(StringComparer.OrdinalIgnoreCase);

        LoadEmbeddedDefinitions(definitions);
        LoadCustomDefinitions(definitions);

        return new Snapshot
        {
            Definitions = definitions.Values
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private void LoadEmbeddedDefinitions(IDictionary<string, AwardDefinition> definitions)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var resourceNames = asm
                .GetManifestResourceNames()
                .Where(x => x.Contains(".AwardData.", StringComparison.Ordinal))
                .Where(x => SupportedDataExtensions.Contains(Path.GetExtension(x), StringComparer.OrdinalIgnoreCase))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var resourceName in resourceNames)
            {
                using var stream = asm.GetManifestResourceStream(resourceName);
                if (stream is null)
                {
                    continue;
                }

                using var reader = new StreamReader(stream, Encoding.UTF8);
                var content = reader.ReadToEnd();
                var sourceName = ExtractEmbeddedSourceName(resourceName, ".AwardData.");
                var def = ParseDefinition(sourceName, content, isBuiltIn: true);
                if (def is null)
                {
                    continue;
                }

                definitions[def.Key] = def;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MDBListRatings: failed to load embedded award datasets.");
        }
    }

    private void LoadCustomDefinitions(IDictionary<string, AwardDefinition> definitions)
    {
        try
        {
            if (!Directory.Exists(_customAwardsDirectory))
            {
                return;
            }

            foreach (var filePath in EnumerateSupportedDataFiles(_customAwardsDirectory))
            {
                try
                {
                    var content = File.ReadAllText(filePath, Encoding.UTF8);
                    var def = ParseDefinition(Path.GetFileName(filePath), content, isBuiltIn: false);
                    if (def is null)
                    {
                        continue;
                    }

                    // Custom dataset overrides built-in dataset with the same key.
                    definitions[def.Key] = def;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "MDBListRatings: failed to load custom award dataset from {Path}.", filePath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MDBListRatings: failed to scan custom award datasets.");
        }
    }

    private static IEnumerable<string> EnumerateSupportedDataFiles(string directory)
    {
        return Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
            .Where(x => SupportedDataExtensions.Contains(Path.GetExtension(x), StringComparer.OrdinalIgnoreCase))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
    }

    private AwardDefinition? ParseDefinition(string sourceName, string content, bool isBuiltIn)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var extension = Path.GetExtension(sourceName).ToLowerInvariant();
        return extension switch
        {
            ".yml" or ".yaml" => ParseYamlDefinition(sourceName, content, isBuiltIn),
            _ => ParseJsonDefinition(sourceName, content, isBuiltIn)
        };
    }

    private AwardDefinition? ParseJsonDefinition(string sourceName, string json, bool isBuiltIn)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var baseSourceName = Path.GetFileNameWithoutExtension(sourceName);
        var key = NormalizeKey(GetString(root, "award_key")
            ?? GetString(root, "awardKey")
            ?? GetString(root, "key")
            ?? InferKeyFromSourceName(baseSourceName));

        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var name = GetString(root, "award_name")
            ?? GetString(root, "awardName")
            ?? GetString(root, "name")
            ?? InferNameFromKey(key);

        var iconFile = GetString(root, "icon_file")
            ?? GetString(root, "iconFile")
            ?? GetString(root, "icon")
            ?? InferDefaultIconFile(key, baseSourceName);

        var array = GetFirstArray(root, "entries", "items", "films", "anime", "titles");
        if (array is null)
        {
            return null;
        }

        var entries = new Dictionary<string, AwardEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in array.Value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var imdbId = NormalizeImdbId(
                GetString(item, "imdb_id")
                ?? GetString(item, "imdbId")
                ?? GetNestedString(item, "ids", "imdb"));

            if (string.IsNullOrWhiteSpace(imdbId))
            {
                continue;
            }

            var entry = new AwardEntry
            {
                ImdbId = imdbId,
                Title = GetString(item, "title") ?? GetString(item, "name") ?? string.Empty,
                AwardYears = ReadStringArray(item, "award_years", "awardYears"),
                WonCategories = ReadStringArray(item, "won_categories", "wonCategories"),
                Wins = ReadWins(item),
                NominatedYears = ReadStringArray(item, "nominated_years", "nominatedYears"),
                NominatedCategories = ReadStringArray(item, "nominated_categories", "nominatedCategories"),
                Nominations = ReadNominations(item)
            };

            if (entry.AwardYears.Count == 0 && entry.Wins.Count > 0)
            {
                entry.AwardYears = entry.Wins
                    .Select(x => x.AwardYear)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            if (entry.WonCategories.Count == 0 && entry.Wins.Count > 0)
            {
                entry.WonCategories = entry.Wins
                    .Select(x => x.Category)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            if (entry.NominatedYears.Count == 0 && entry.Nominations.Count > 0)
            {
                entry.NominatedYears = entry.Nominations
                    .Select(x => x.AwardYear)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            if (entry.NominatedCategories.Count == 0 && entry.Nominations.Count > 0)
            {
                entry.NominatedCategories = entry.Nominations
                    .Select(x => x.Category)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            entries[imdbId] = entry;
        }

        return new AwardDefinition
        {
            Key = key,
            Name = string.IsNullOrWhiteSpace(name) ? key : name.Trim(),
            IconFile = string.IsNullOrWhiteSpace(iconFile) ? null : iconFile.Trim(),
            IsBuiltIn = isBuiltIn,
            EntriesByImdb = entries
        };
    }

    private AwardDefinition? ParseYamlDefinition(string sourceName, string yaml, bool isBuiltIn)
    {
        var metadata = ResolveYamlMetadata(sourceName, yaml);

        var deserializer = new DeserializerBuilder().Build();
        var rootObject = deserializer.Deserialize(new StringReader(yaml));
        if (rootObject is not IDictionary<object, object> rootMap)
        {
            return null;
        }

        if (LooksLikeNormalizedAwardDataset(rootMap))
        {
            return ParseNormalizedYamlDefinition(rootMap, metadata, isBuiltIn);
        }

        var entries = new Dictionary<string, AwardEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var yearPair in rootMap)
        {
            var awardYear = NormalizeAwardYear(ToScalarString(yearPair.Key));
            if (string.IsNullOrWhiteSpace(awardYear))
            {
                continue;
            }

            if (!TryGetObjectMap(yearPair.Value, out var yearMap))
            {
                continue;
            }

            foreach (var groupPair in yearMap)
            {
                var groupName = ToScalarString(groupPair.Key);
                if (string.IsNullOrWhiteSpace(groupName))
                {
                    continue;
                }

                CollectYamlAwards(groupPair.Value, [groupName], awardYear, metadata, entries);
            }
        }

        return new AwardDefinition
        {
            Key = metadata.Key,
            Name = metadata.Name,
            IconFile = metadata.IconFile,
            IsBuiltIn = isBuiltIn,
            EntriesByImdb = entries
        };
    }

    private AwardDefinition? ParseNormalizedYamlDefinition(IDictionary<object, object> rootMap, AwardDatasetMetadata metadata, bool isBuiltIn)
    {
        var key = NormalizeKey(GetMapString(rootMap, "award_key")
            ?? GetMapString(rootMap, "awardKey")
            ?? GetMapString(rootMap, "key")
            ?? metadata.Key);

        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var name = GetMapString(rootMap, "award_name")
            ?? GetMapString(rootMap, "awardName")
            ?? GetMapString(rootMap, "name")
            ?? metadata.Name;

        var iconFile = GetMapString(rootMap, "icon_file")
            ?? GetMapString(rootMap, "iconFile")
            ?? GetMapString(rootMap, "icon")
            ?? metadata.IconFile;

        var items = GetFirstSequence(rootMap, "entries", "items", "films", "anime", "titles");
        if (items is null)
        {
            return null;
        }

        var entries = new Dictionary<string, AwardEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            if (!TryGetObjectMap(item, out var itemMap))
            {
                continue;
            }

            var imdbId = NormalizeImdbId(
                GetMapString(itemMap, "imdb_id")
                ?? GetMapString(itemMap, "imdbId")
                ?? GetNestedMapString(itemMap, "ids", "imdb"));

            if (string.IsNullOrWhiteSpace(imdbId))
            {
                continue;
            }

            var entry = new AwardEntry
            {
                ImdbId = imdbId,
                Title = GetMapString(itemMap, "title") ?? GetMapString(itemMap, "name") ?? string.Empty,
                AwardYears = ReadMapStringList(itemMap, "award_years", "awardYears"),
                WonCategories = ReadMapStringList(itemMap, "won_categories", "wonCategories"),
                Wins = ReadYamlWins(itemMap),
                NominatedYears = ReadMapStringList(itemMap, "nominated_years", "nominatedYears"),
                NominatedCategories = ReadMapStringList(itemMap, "nominated_categories", "nominatedCategories"),
                Nominations = ReadYamlNominations(itemMap)
            };

            if (entry.AwardYears.Count == 0 && entry.Wins.Count > 0)
            {
                entry.AwardYears = entry.Wins
                    .Select(x => x.AwardYear)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            if (entry.WonCategories.Count == 0 && entry.Wins.Count > 0)
            {
                entry.WonCategories = entry.Wins
                    .Select(x => x.Category)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            if (entry.NominatedYears.Count == 0 && entry.Nominations.Count > 0)
            {
                entry.NominatedYears = entry.Nominations
                    .Select(x => x.AwardYear)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            if (entry.NominatedCategories.Count == 0 && entry.Nominations.Count > 0)
            {
                entry.NominatedCategories = entry.Nominations
                    .Select(x => x.Category)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            entries[imdbId] = entry;
        }

        return new AwardDefinition
        {
            Key = key,
            Name = string.IsNullOrWhiteSpace(name) ? key : name.Trim(),
            IconFile = string.IsNullOrWhiteSpace(iconFile) ? null : iconFile.Trim(),
            IsBuiltIn = isBuiltIn,
            EntriesByImdb = entries
        };
    }

    private static bool LooksLikeNormalizedAwardDataset(IDictionary<object, object> rootMap)
    {
        return ContainsMapKey(rootMap, "entries")
            || ContainsMapKey(rootMap, "award_key")
            || ContainsMapKey(rootMap, "awardName");
    }

    private static void CollectYamlAwards(
        object? node,
        List<string> pathSegments,
        string awardYear,
        AwardDatasetMetadata metadata,
        IDictionary<string, AwardEntry> entries)
    {
        if (!TryGetObjectMap(node, out var nodeMap))
        {
            return;
        }

        if (LooksLikeWinnerLeaf(nodeMap))
        {
            var categoryLabel = BuildYamlCategoryLabel(metadata, pathSegments);

            var nomineeIds = GetImdbIdList(nodeMap, "nominee");
            foreach (var imdbId in nomineeIds)
            {
                if (!entries.TryGetValue(imdbId, out var entry))
                {
                    entry = new AwardEntry
                    {
                        ImdbId = imdbId
                    };
                    entries[imdbId] = entry;
                }

                AddNomination(entry, awardYear, categoryLabel);
            }

            var winnerIds = GetImdbIdList(nodeMap, "winner");
            foreach (var imdbId in winnerIds)
            {
                if (!entries.TryGetValue(imdbId, out var entry))
                {
                    entry = new AwardEntry
                    {
                        ImdbId = imdbId
                    };
                    entries[imdbId] = entry;
                }

                AddWin(entry, awardYear, categoryLabel);
            }

            return;
        }

        foreach (var pair in nodeMap)
        {
            var segment = ToScalarString(pair.Key);
            if (string.IsNullOrWhiteSpace(segment))
            {
                continue;
            }

            var nextPath = new List<string>(pathSegments) { segment };
            CollectYamlAwards(pair.Value, nextPath, awardYear, metadata, entries);
        }
    }

    private static void AddWin(AwardEntry entry, string awardYear, string categoryLabel)
    {
        if (!string.IsNullOrWhiteSpace(awardYear) && !entry.AwardYears.Contains(awardYear, StringComparer.OrdinalIgnoreCase))
        {
            entry.AwardYears.Add(awardYear);
        }

        if (!string.IsNullOrWhiteSpace(categoryLabel) && !entry.WonCategories.Contains(categoryLabel, StringComparer.OrdinalIgnoreCase))
        {
            entry.WonCategories.Add(categoryLabel);
        }

        if (!entry.Wins.Any(x => string.Equals(x.AwardYear, awardYear, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Category, categoryLabel, StringComparison.OrdinalIgnoreCase)))
        {
            entry.Wins.Add(new AwardWin
            {
                AwardYear = awardYear,
                Category = categoryLabel
            });
        }
    }

    private static void AddNomination(AwardEntry entry, string awardYear, string categoryLabel)
    {
        if (!string.IsNullOrWhiteSpace(awardYear) && !entry.NominatedYears.Contains(awardYear, StringComparer.OrdinalIgnoreCase))
        {
            entry.NominatedYears.Add(awardYear);
        }

        if (!string.IsNullOrWhiteSpace(categoryLabel) && !entry.NominatedCategories.Contains(categoryLabel, StringComparer.OrdinalIgnoreCase))
        {
            entry.NominatedCategories.Add(categoryLabel);
        }

        if (!entry.Nominations.Any(x => string.Equals(x.AwardYear, awardYear, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Category, categoryLabel, StringComparison.OrdinalIgnoreCase)))
        {
            entry.Nominations.Add(new AwardNomination
            {
                AwardYear = awardYear,
                Category = categoryLabel
            });
        }
    }

    private static bool LooksLikeWinnerLeaf(IDictionary<object, object> nodeMap)
    {
        return ContainsMapKey(nodeMap, "winner") || ContainsMapKey(nodeMap, "nominee");
    }

    private static List<string> GetImdbIdList(IDictionary<object, object> map, string key)
    {
        if (!TryGetMapValue(map, key, out var value))
        {
            return new List<string>();
        }

        if (value is IEnumerable<object> sequence)
        {
            return sequence
                .Select(ToScalarString)
                .Select(NormalizeImdbId)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var single = NormalizeImdbId(ToScalarString(value));
        return string.IsNullOrWhiteSpace(single)
            ? new List<string>()
            : new List<string> { single };
    }

    private static AwardDatasetMetadata ResolveYamlMetadata(string sourceName, string yaml)
    {
        var fileBaseName = Path.GetFileNameWithoutExtension(sourceName);
        var titleFromComment = ExtractFirstYamlComment(yaml);

        if (KnownYamlDatasets.TryGetValue(fileBaseName, out var known))
        {
            return new AwardDatasetMetadata
            {
                Key = NormalizeKey(known.Key),
                Name = known.Name,
                IconFile = known.IconFile,
                SourceFileBaseName = fileBaseName,
                SourceTitle = titleFromComment ?? known.Name
            };
        }

        var friendlyName = !string.IsNullOrWhiteSpace(titleFromComment)
            ? titleFromComment!
            : HumanizeKey(fileBaseName);

        var derivedKey = !string.IsNullOrWhiteSpace(titleFromComment)
            ? NormalizeAsciiKey(titleFromComment)
            : NormalizeKey(fileBaseName);

        if (string.IsNullOrWhiteSpace(derivedKey))
        {
            derivedKey = NormalizeKey(fileBaseName);
        }

        return new AwardDatasetMetadata
        {
            Key = derivedKey,
            Name = friendlyName,
            IconFile = derivedKey + ".png",
            SourceFileBaseName = fileBaseName,
            SourceTitle = friendlyName
        };
    }

    private static string? ExtractFirstYamlComment(string yaml)
    {
        using var reader = new StringReader(yaml ?? string.Empty);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (trimmed.StartsWith('#'))
            {
                var value = trimmed.TrimStart('#').Trim();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }

            break;
        }

        return null;
    }

    private static string BuildYamlCategoryLabel(AwardDatasetMetadata metadata, IEnumerable<string> rawSegments)
    {
        var segments = new List<string>();
        foreach (var rawSegment in rawSegments)
        {
            var segment = NormalizePathSegment(rawSegment);
            if (string.IsNullOrWhiteSpace(segment))
            {
                continue;
            }

            if (ShouldSkipCategorySegment(metadata, segment))
            {
                continue;
            }

            if (segments.Count > 0 && string.Equals(segments[^1], segment, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            segments.Add(segment);
        }

        if (segments.Count == 0)
        {
            return metadata.Name;
        }

        return string.Join(" — ", segments);
    }

    private static bool ShouldSkipCategorySegment(AwardDatasetMetadata metadata, string segment)
    {
        var normalizedSegment = NormalizeKey(segment);
        if (string.IsNullOrWhiteSpace(normalizedSegment))
        {
            return true;
        }

        if (string.Equals(normalizedSegment, metadata.Key, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(normalizedSegment, NormalizeKey(metadata.Name), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(metadata.SourceTitle)
            && string.Equals(normalizedSegment, NormalizeKey(metadata.SourceTitle), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static string NormalizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = value.Trim().Replace('_', ' ');
        text = Regex.Replace(text, @"\s+", " ");

        if (text.Any(char.IsUpper))
        {
            return text;
        }

        return string.Join(" ", text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(HumanizeToken));
    }

    private static string HumanizeToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return string.Empty;
        }

        var parts = token.Split('-', StringSplitOptions.None)
            .Select(part => string.Join('/', part.Split('/', StringSplitOptions.None).Select(HumanizeSingleTokenPart)));
        return string.Join("-", parts);
    }

    private static string HumanizeSingleTokenPart(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return string.Empty;
        }

        if (token.All(ch => !char.IsLetter(ch) || char.IsUpper(ch)))
        {
            return token;
        }

        if (token.Contains('.', StringComparison.Ordinal))
        {
            var chars = token.Select(ch => char.IsLetter(ch) ? char.ToUpperInvariant(ch) : ch).ToArray();
            return new string(chars);
        }

        var lower = token.ToLowerInvariant();
        for (var i = 0; i < lower.Length; i++)
        {
            if (char.IsLetter(lower[i]))
            {
                var chars = lower.ToCharArray();
                chars[i] = char.ToUpperInvariant(chars[i]);
                return new string(chars);
            }
        }

        return token;
    }

    private static string NormalizeAsciiKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);

        foreach (var ch in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (ch == '\'' || ch == '\u2019' || ch == '`' || ch == '\u00B4')
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_');
        }

        var slug = builder.ToString();
        while (slug.Contains("__", StringComparison.Ordinal))
        {
            slug = slug.Replace("__", "_", StringComparison.Ordinal);
        }

        return slug.Trim('_');
    }

    private static string HumanizeKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        return string.Join(" ", key
            .Replace('_', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(HumanizeToken));
    }

    private static string NormalizeAwardYear(string value)
    {
        var trimmed = (value ?? string.Empty).Trim().Trim('\'', '"');
        return trimmed;
    }

    private static string ExtractEmbeddedSourceName(string resourceName, string marker)
    {
        var idx = resourceName.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
        {
            return resourceName;
        }

        return resourceName[(idx + marker.Length)..];
    }

    private static JsonElement? GetFirstArray(JsonElement root, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (root.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Array)
            {
                return prop;
            }
        }

        return null;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
        {
            return null;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static string? GetNestedString(JsonElement element, string objectPropertyName, string propertyName)
    {
        if (!element.TryGetProperty(objectPropertyName, out var obj) || obj.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return GetString(obj, propertyName);
    }

    private static List<string> ReadStringArray(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            return prop.EnumerateArray()
                .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.ToString())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return new List<string>();
    }

    private static List<AwardWin> ReadWins(JsonElement element)
    {
        var result = new List<AwardWin>();
        if (!element.TryGetProperty("wins", out var wins) || wins.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in wins.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var awardYear = GetString(item, "award_year") ?? GetString(item, "awardYear") ?? string.Empty;
            var category = GetString(item, "category") ?? string.Empty;

            if (string.IsNullOrWhiteSpace(awardYear) && string.IsNullOrWhiteSpace(category))
            {
                continue;
            }

            result.Add(new AwardWin
            {
                AwardYear = awardYear.Trim(),
                Category = category.Trim()
            });
        }

        return result;
    }

    private static List<AwardNomination> ReadNominations(JsonElement element)
    {
        var result = new List<AwardNomination>();
        if (!element.TryGetProperty("nominations", out var nominations) || nominations.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in nominations.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var awardYear = GetString(item, "award_year") ?? GetString(item, "awardYear") ?? string.Empty;
            var category = GetString(item, "category") ?? string.Empty;

            if (string.IsNullOrWhiteSpace(awardYear) && string.IsNullOrWhiteSpace(category))
            {
                continue;
            }

            result.Add(new AwardNomination
            {
                AwardYear = awardYear.Trim(),
                Category = category.Trim()
            });
        }

        return result;
    }

    private static bool TryGetObjectMap(object? value, out IDictionary<object, object> map)
    {
        if (value is IDictionary<object, object> typedMap)
        {
            map = typedMap;
            return true;
        }

        map = null!;
        return false;
    }

    private static bool ContainsMapKey(IDictionary<object, object> map, string key)
    {
        return map.Keys.Any(k => string.Equals(ToScalarString(k), key, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryGetMapValue(IDictionary<object, object> map, string key, out object? value)
    {
        foreach (var pair in map)
        {
            if (string.Equals(ToScalarString(pair.Key), key, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static string? GetMapString(IDictionary<object, object> map, string key)
    {
        return TryGetMapValue(map, key, out var value) ? ToScalarString(value) : null;
    }

    private static string? GetNestedMapString(IDictionary<object, object> map, string objectKey, string key)
    {
        if (!TryGetMapValue(map, objectKey, out var objValue) || !TryGetObjectMap(objValue, out var objMap))
        {
            return null;
        }

        return GetMapString(objMap, key);
    }

    private static List<object>? GetFirstSequence(IDictionary<object, object> map, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!TryGetMapValue(map, key, out var value) || value is not IEnumerable<object> sequence)
            {
                continue;
            }

            return sequence.ToList();
        }

        return null;
    }

    private static List<string> ReadMapStringList(IDictionary<object, object> map, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!TryGetMapValue(map, key, out var value) || value is not IEnumerable<object> sequence)
            {
                continue;
            }

            return sequence
                .Select(ToScalarString)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return new List<string>();
    }

    private static List<AwardWin> ReadYamlWins(IDictionary<object, object> map)
    {
        var result = new List<AwardWin>();
        if (!TryGetMapValue(map, "wins", out var value) || value is not IEnumerable<object> sequence)
        {
            return result;
        }

        foreach (var item in sequence)
        {
            if (!TryGetObjectMap(item, out var itemMap))
            {
                continue;
            }

            var awardYear = GetMapString(itemMap, "award_year") ?? GetMapString(itemMap, "awardYear") ?? string.Empty;
            var category = GetMapString(itemMap, "category") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(awardYear) && string.IsNullOrWhiteSpace(category))
            {
                continue;
            }

            result.Add(new AwardWin
            {
                AwardYear = awardYear.Trim(),
                Category = category.Trim()
            });
        }

        return result;
    }

    private static List<AwardNomination> ReadYamlNominations(IDictionary<object, object> map)
    {
        var result = new List<AwardNomination>();
        if (!TryGetMapValue(map, "nominations", out var value) || value is not IEnumerable<object> sequence)
        {
            return result;
        }

        foreach (var item in sequence)
        {
            if (!TryGetObjectMap(item, out var itemMap))
            {
                continue;
            }

            var awardYear = GetMapString(itemMap, "award_year") ?? GetMapString(itemMap, "awardYear") ?? string.Empty;
            var category = GetMapString(itemMap, "category") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(awardYear) && string.IsNullOrWhiteSpace(category))
            {
                continue;
            }

            result.Add(new AwardNomination
            {
                AwardYear = awardYear.Trim(),
                Category = category.Trim()
            });
        }

        return result;
    }

    private static string ToScalarString(object? value)
    {
        return value switch
        {
            null => string.Empty,
            string s => s,
            bool b => b ? "true" : "false",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };
    }

    private static bool HasWins(AwardEntry entry)
    {
        return entry is not null && (entry.Wins.Count > 0 || entry.AwardYears.Count > 0 || entry.WonCategories.Count > 0);
    }

    private static bool HasNominations(AwardEntry entry)
    {
        return entry is not null && (entry.Nominations.Count > 0 || entry.NominatedYears.Count > 0 || entry.NominatedCategories.Count > 0);
    }

    private static string BuildNominationSummaryTooltip(List<AwardNominationSummaryAward> awards)
    {
        var lines = new List<string> { "Nominations" };
        foreach (var award in awards)
        {
            if (award is null || string.IsNullOrWhiteSpace(award.AwardName))
            {
                continue;
            }

            lines.Add(string.Empty);
            lines.Add(award.AwardName.Trim());
            AppendGroupedCategories(lines, award.Groups, "Nominations");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static void AppendGroupedCategories(List<string> lines, List<GroupedAwardWins> groups, string label)
    {
        if (lines is null || groups is null || groups.Count == 0)
        {
            return;
        }

        foreach (var group in groups)
        {
            var year = (group.AwardYear ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(year))
            {
                lines.Add(year);
            }

            if (group.Categories.Count > 0)
            {
                lines.Add(label + ":");
                foreach (var category in group.Categories)
                {
                    if (!string.IsNullOrWhiteSpace(category))
                    {
                        lines.Add(category.Trim());
                    }
                }
            }
        }
    }

    private static List<GroupedAwardWins> GroupNominationsByYear(AwardEntry entry)
    {
        var result = new List<GroupedAwardWins>();
        if (entry is null)
        {
            return result;
        }

        if (entry.Nominations.Count > 0)
        {
            foreach (var nomination in entry.Nominations)
            {
                var awardYear = (nomination.AwardYear ?? string.Empty).Trim();
                var category = (nomination.Category ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(awardYear) && string.IsNullOrWhiteSpace(category))
                {
                    continue;
                }

                var group = result.FirstOrDefault(x => string.Equals(x.AwardYear, awardYear, StringComparison.OrdinalIgnoreCase));
                if (group is null)
                {
                    group = new GroupedAwardWins
                    {
                        AwardYear = awardYear,
                        Categories = new List<string>()
                    };
                    result.Add(group);
                }

                if (!string.IsNullOrWhiteSpace(category) && !group.Categories.Contains(category, StringComparer.OrdinalIgnoreCase))
                {
                    group.Categories.Add(category);
                }
            }

            return result;
        }

        if (entry.NominatedYears.Count == 1 && entry.NominatedCategories.Count > 0)
        {
            result.Add(new GroupedAwardWins
            {
                AwardYear = entry.NominatedYears[0],
                Categories = entry.NominatedCategories
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            });
        }

        return result;
    }

    private static string BuildTooltip(string awardName, AwardEntry entry)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(awardName))
        {
            lines.Add(awardName.Trim());
        }

        var groupedWins = GroupWinsByYear(entry);
        if (groupedWins.Count > 0)
        {
            AppendGroupedCategories(lines, groupedWins, "Wins");
            return string.Join(Environment.NewLine, lines);
        }

        if (entry.AwardYears.Count > 0)
        {
            lines.Add("Years:");
            foreach (var year in entry.AwardYears)
            {
                if (!string.IsNullOrWhiteSpace(year))
                {
                    lines.Add(year.Trim());
                }
            }
        }

        if (entry.WonCategories.Count > 0)
        {
            lines.Add("Wins:");
            foreach (var category in entry.WonCategories)
            {
                if (!string.IsNullOrWhiteSpace(category))
                {
                    lines.Add(category.Trim());
                }
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static List<GroupedAwardWins> GroupWinsByYear(AwardEntry entry)
    {
        var result = new List<GroupedAwardWins>();
        if (entry is null)
        {
            return result;
        }

        if (entry.Wins.Count > 0)
        {
            foreach (var win in entry.Wins)
            {
                var awardYear = (win.AwardYear ?? string.Empty).Trim();
                var category = (win.Category ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(awardYear) && string.IsNullOrWhiteSpace(category))
                {
                    continue;
                }

                var group = result.FirstOrDefault(x => string.Equals(x.AwardYear, awardYear, StringComparison.OrdinalIgnoreCase));
                if (group is null)
                {
                    group = new GroupedAwardWins
                    {
                        AwardYear = awardYear,
                        Categories = new List<string>()
                    };
                    result.Add(group);
                }

                if (!string.IsNullOrWhiteSpace(category) && !group.Categories.Contains(category, StringComparer.OrdinalIgnoreCase))
                {
                    group.Categories.Add(category);
                }
            }

            return result;
        }

        if (entry.AwardYears.Count == 1 && entry.WonCategories.Count > 0)
        {
            result.Add(new GroupedAwardWins
            {
                AwardYear = entry.AwardYears[0],
                Categories = entry.WonCategories
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            });
        }

        return result;
    }

    private static string NormalizeImdbId(string? value)
    {
        var s = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(s))
        {
            return string.Empty;
        }

        return s.ToLowerInvariant();
    }

    private static string NormalizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var chars = value.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray();

        var normalized = new string(chars);
        while (normalized.Contains("__", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("__", "_", StringComparison.Ordinal);
        }

        return normalized.Trim('_');
    }

    private static string InferKeyFromSourceName(string sourceName)
    {
        var lower = (sourceName ?? string.Empty).ToLowerInvariant();
        if (lower.Contains("oscar", StringComparison.Ordinal) || lower.Contains("academy", StringComparison.Ordinal))
        {
            return "oscar";
        }

        if (lower.Contains("crunchyroll", StringComparison.Ordinal))
        {
            return "crunchyroll";
        }

        return NormalizeKey(Path.GetFileNameWithoutExtension(sourceName));
    }

    private static string InferNameFromKey(string key)
    {
        return key switch
        {
            "oscar" => "Oscar",
            "crunchyroll" => "Crunchyroll Anime Awards",
            "berlin_international_film_festival" => "Berlin International Film Festival",
            _ => key.Replace('_', ' ').Trim()
        };
    }

    private static string? InferDefaultIconFile(string key, string sourceName)
    {
        return key switch
        {
            "oscar" => "academy_awards_usa.png",
            "crunchyroll" => "crunchyroll_anime_awards.png",
            "berlin_international_film_festival" => "berlin_international_film_festival.png",
            _ => Path.GetFileNameWithoutExtension(sourceName) + ".png"
        };
    }

    private sealed class Snapshot
    {
        public List<AwardDefinition> Definitions { get; set; } = new();
    }

    private sealed class KnownYamlDataset
    {
        public string Key { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string? IconFile { get; set; }
    }

    private sealed class AwardDatasetMetadata
    {
        public string Key { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string? IconFile { get; set; }

        public string SourceFileBaseName { get; set; } = string.Empty;

        public string SourceTitle { get; set; } = string.Empty;
    }
}

public sealed class AwardDefinitionInfo
{
    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? IconFile { get; set; }

    public int EntryCount { get; set; }

    public bool IsBuiltIn { get; set; }
}

public sealed class AwardBadgeMatch
{
    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? IconFile { get; set; }

    public string Tooltip { get; set; } = string.Empty;

    public List<string> AwardYears { get; set; } = new();

    public List<string> WonCategories { get; set; } = new();
}

public sealed class AwardNominationSummaryMatch
{
    public bool HasNominations => AwardCount > 0;

    public string Name { get; set; } = string.Empty;

    public string Tooltip { get; set; } = string.Empty;

    public int AwardCount { get; set; }

    public int CategoryCount { get; set; }
}

internal sealed class AwardDefinition
{
    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? IconFile { get; set; }

    public bool IsBuiltIn { get; set; }

    public Dictionary<string, AwardEntry> EntriesByImdb { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class AwardEntry
{
    public string ImdbId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public List<string> AwardYears { get; set; } = new();

    public List<string> WonCategories { get; set; } = new();

    public List<AwardWin> Wins { get; set; } = new();

    public List<string> NominatedYears { get; set; } = new();

    public List<string> NominatedCategories { get; set; } = new();

    public List<AwardNomination> Nominations { get; set; } = new();
}

internal sealed class AwardWin
{
    public string AwardYear { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;
}

internal sealed class AwardNomination
{
    public string AwardYear { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;
}

internal sealed class GroupedAwardWins
{
    public string AwardYear { get; set; } = string.Empty;

    public List<string> Categories { get; set; } = new();
}

internal sealed class AwardNominationSummaryAward
{
    public string AwardName { get; set; } = string.Empty;

    public List<GroupedAwardWins> Groups { get; set; } = new();
}
