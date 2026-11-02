using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace DiscordBot;

public sealed record ConfidenceResult(float Score, string Path, string DisplayName);

public static class ImageAlgorithm
{
    private const float MainWeight = 1.8f;
    private const float CharacterWeight = 1.5f;
    private const float SideWeight = 1.0f;
    private const float ExactMatchBonus = 1.0f;

    private static readonly TimeSpan CacheExpire = TimeSpan.FromSeconds(5);

    /// <summary>Query picked in autocomplete → image path, so submitting the command gets the same image.</summary>
    private static readonly ConcurrentDictionary<string, (DateTime At, string Path)> _queryCache = new();

    private sealed record LabelFile
    {
        [JsonPropertyName("main-tag")] public string[]? MainTags { get; init; }
        [JsonPropertyName("character-tag")] public string[]? CharacterTags { get; init; }
        [JsonPropertyName("side-tag")] public string[]? SideTags { get; init; }
    }

    private sealed record LabelEntry(string ImagePath, string DisplayName, string[] Main, string[] Character, string[] Side);

    private sealed record LabelIndex(DateTime DirWriteTimeUtc, int FileCount, IReadOnlyList<LabelEntry> Entries);

    private static volatile LabelIndex? _index;
    private static readonly Lock _indexLock = new();

    public static void StartCacheCleanup()
    {
        BackgroundLoop.RunPeriodic("Image search cache cleanup", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), ct =>
        {
            var now = DateTime.UtcNow;
            foreach (var (key, value) in _queryCache)
                if (now - value.At >= CacheExpire)
                    _queryCache.TryRemove(key, out _);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Previously every keystroke (autocomplete) re-read all label JSON from disk, spawned one Thread per CPU core
    /// and pinned them via kernel32; a malformed label (e.g. empty main-tag array) threw on a bare Thread and killed the whole process.
    /// Now labels are read once and cached (re-read only when the folder changes); broken files are logged and skipped.
    /// </summary>
    private static LabelIndex GetIndex()
    {
        var dir = new DirectoryInfo(GlobalVariable.LabelsFolderPath);
        if (!dir.Exists)
            throw new DirectoryNotFoundException($"Label folder not found: {dir.FullName}");

        var files = dir.GetFiles("*.json");
        var current = _index;
        if (current is not null && current.DirWriteTimeUtc == dir.LastWriteTimeUtc && current.FileCount == files.Length)
            return current;

        lock (_indexLock)
        {
            current = _index;
            if (current is not null && current.DirWriteTimeUtc == dir.LastWriteTimeUtc && current.FileCount == files.Length)
                return current;

            var entries = new List<LabelEntry>(files.Length);
            foreach (var file in files)
            {
                try
                {
                    var label = Json.Read<LabelFile>(file.FullName);
                    string name = Path.GetFileNameWithoutExtension(file.Name);
                    string imagePath = Path.Combine(GlobalVariable.ImagesFolderPath, $"{name}.jpg");

                    if (!File.Exists(imagePath))
                    {
                        Log.Warn($"Image {imagePath} for label {file.Name} not found, skipping");
                        continue;
                    }

                    var main = label.MainTags ?? Array.Empty<string>();
                    entries.Add(new LabelEntry(
                        imagePath,
                        main.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? name,
                        main,
                        label.CharacterTags ?? Array.Empty<string>(),
                        label.SideTags ?? Array.Empty<string>()));
                }
                catch (Exception ex)
                {
                    Log.Warn($"Failed to read label {file.FullName}, skipping", ex);
                }
            }

            current = new LabelIndex(dir.LastWriteTimeUtc, files.Length, entries);
            _index = current;
            Log.Info($"Image label index built: {entries.Count}/{files.Length}");
            return current;
        }
    }

    private static float Score(LabelEntry entry, string query)
    {
        static double Segment(string[] tags, float weight, string query)
        {
            double sum = 0;
            foreach (var tag in tags)
            {
                if (string.IsNullOrEmpty(tag)) continue;
                float w = query == tag ? weight + ExactMatchBonus : weight;
                sum += w * Utils.HybridScoreSimilarity(query, tag);
            }
            return sum;
        }

        return (float)(Segment(entry.Main, MainWeight, query)
                     + Segment(entry.Character, CharacterWeight, query)
                     + Segment(entry.Side, SideWeight, query));
    }

    public static List<ConfidenceResult> SearchImage(string query, int maxResultCount = 25, bool acceptsCachedResult = false)
    {
        query ??= "";

        if (maxResultCount == 1 && acceptsCachedResult && _queryCache.TryGetValue(query, out var cached))
            return [new ConfidenceResult(0f, cached.Path, "")];

        return GetIndex().Entries
            .AsParallel()
            .Select(e => new ConfidenceResult(Score(e, query), e.ImagePath, e.DisplayName))
            .OrderByDescending(r => r.Score)
            .Take(maxResultCount)
            .ToList();
    }

    /// <summary>
    /// Converts user input into the actual image path to upload.
    /// ⚠ This used to be <c>if (File.Exists(query)) path = query;</c> — a user typing Data\env.json into /search
    /// would make the bot upload the config file containing the bot token. Now only files under the image folder are accepted.
    /// </summary>
    public static string? ResolveImagePath(string query)
    {
        if (IsInsideImagesFolder(query) && File.Exists(query))
            return query;

        return SearchImage(query, maxResultCount: 1, acceptsCachedResult: true).FirstOrDefault()?.Path;
    }

    private static bool IsInsideImagesFolder(string path)
    {
        try
        {
            string root = Path.GetFullPath(GlobalVariable.ImagesFolderPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public sealed class SearchImageAutocomplete : IAutocompleteProvider<AutocompleteInteractionContext>
    {
        public ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(ApplicationCommandInteractionDataOption option, AutocompleteInteractionContext context)
        {
            string current = option.Value ?? "";

            try
            {
                var results = SearchImage(current);

                if (!string.IsNullOrEmpty(current) && results.Count > 0)
                    _queryCache[current] = (DateTime.UtcNow, results[0].Path);

                IEnumerable<ApplicationCommandOptionChoiceProperties> choices = results
                    .Where(r => r.Path.Length <= 100)
                    .Select(r => new ApplicationCommandOptionChoiceProperties(Truncate(r.DisplayName, 100), r.Path));

                return ValueTask.FromResult<IEnumerable<ApplicationCommandOptionChoiceProperties>?>(choices.ToList());
            }
            catch (Exception ex)
            {
                Log.Error($"Image search autocomplete failed query={current}", ex);
                return ValueTask.FromResult<IEnumerable<ApplicationCommandOptionChoiceProperties>?>(Array.Empty<ApplicationCommandOptionChoiceProperties>());
            }
        }

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) ? "?" : s.Length <= max ? s : s[..max];
    }
}
