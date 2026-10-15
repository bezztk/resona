using System.Text.Json;
using System.Text.Json.Serialization;

namespace Resona.Core;

public sealed record PortableMusicLibrary(
    List<PortableTrack> Tracks,
    List<PortableFilterPreset>? FilterPresets = null,
    int SchemaVersion = PortableMusicLibrary.CurrentSchemaVersion,
    string? ExportId = null,
    string? ExportedAt = null,
    string MediaMode = "full",
    List<PortableRating>? RatingDefinitions = null,
    List<PortableCollection>? Collections = null)
{
    public const int CurrentSchemaVersion = 4;

    public static PortableMusicLibrary Empty { get; } = new([]);

    public IReadOnlyList<string> Ratings
    {
        get
        {
            var exportedOrder = (RatingDefinitions ?? [])
                .GroupBy(rating => rating.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().SortOrder, StringComparer.OrdinalIgnoreCase);
            var fallbackOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Avoid"] = 1,
                ["Okay"] = 2,
                ["Good"] = 3,
                ["Great"] = 4,
                ["Amazing"] = 5,
                ["Timeless"] = 6
            };

            return Tracks
                .Select(track => track.Rating)
                .Where(rating => !string.IsNullOrWhiteSpace(rating))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(rating => exportedOrder.GetValueOrDefault(
                    rating,
                    fallbackOrder.GetValueOrDefault(rating, int.MaxValue)))
                .ThenBy(rating => rating, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public IReadOnlyList<string> Genres => Tracks
        .SelectMany(t => t.Genres)
        .Where(v => !string.IsNullOrWhiteSpace(v))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public IReadOnlyList<string> Styles => Tracks
        .SelectMany(t => t.Styles)
        .Where(v => !string.IsNullOrWhiteSpace(v))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public IReadOnlyList<string> Tags => Tracks
        .SelectMany(t => t.Tags ?? [])
        .Where(v => !string.IsNullOrWhiteSpace(v))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToList();
}

public sealed record PortableRating(string Name, int SortOrder);

public sealed record PortableCollection(
    string StableId,
    string Name,
    List<string> TrackKeys);

public sealed record PortableFilterPreset(
    string Name,
    List<PortableFilterGroup> Groups,
    bool ShowNeedsReview = false,
    bool ShowNeedsAnalysis = false);

public sealed record PortableTrack(
    string Title,
    string FileName,
    int? DurationSeconds,
    string Rating,
    List<string> Genres,
    List<string> Styles,
    string? CoverFileName = null,
    bool NeedsReview = false,
    List<string>? Tags = null,
    string? DownloadedAt = null,
    string? ChannelName = null,
    string? ChannelUrl = null,
    string? UploadedAt = null,
    int PlayCount = 0,
    int ListenedSeconds = 0,
    int SkipCount = 0,
    string? LastListenedAt = null,
    byte[]? Thumbnail = null,
    bool IsPublic = false,
    string? LanguageCode = null,
    string? TrackKey = null,
    string? OriginalTitle = null,
    string LibraryState = "Active",
    Dictionary<string, double>? EmotionalCharacter = null)
{
    public string GenreText => string.Join(", ", Genres);
    public string StyleText => string.Join(", ", Styles);
    public string TagText => string.Join(", ", Tags ?? []);

    public string DurationText => DurationSeconds is int seconds
        ? $"{seconds / 60:D2}:{seconds % 60:D2}"
        : "";
}

public sealed record LoadedMusicLibrary(string RootDirectory, PortableMusicLibrary Library)
{
    public string TrackPath(PortableTrack track)
    {
        var tracksPath = Path.Combine(RootDirectory, "Tracks", track.FileName);
        return File.Exists(tracksPath)
            ? tracksPath
            : Path.Combine(RootDirectory, track.FileName);
    }

    public string? CoverPath(PortableTrack track)
    {
        if (string.IsNullOrWhiteSpace(track.CoverFileName))
            return null;

        var path = Path.Combine(RootDirectory, "Images", track.CoverFileName);
        return File.Exists(path) ? path : null;
    }
}

public static class PortableLibraryStore
{
    public const string FileName = "Library.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<LoadedMusicLibrary> LoadAsync(string rootDirectory)
    {
        Directory.CreateDirectory(rootDirectory);
        var path = Path.Combine(rootDirectory, FileName);
        if (!File.Exists(path))
            return new LoadedMusicLibrary(rootDirectory, PortableMusicLibrary.Empty);

        await using var stream = File.OpenRead(path);
        var library = await LoadAsync(stream);
        return new LoadedMusicLibrary(rootDirectory, library);
    }

    public static async Task<PortableMusicLibrary> LoadAsync(Stream stream) =>
        await JsonSerializer.DeserializeAsync<PortableMusicLibrary>(stream, JsonOptions)
        ?? PortableMusicLibrary.Empty;

    public static async Task SaveAsync(string rootDirectory, PortableMusicLibrary library)
    {
        Directory.CreateDirectory(rootDirectory);
        var path = Path.Combine(rootDirectory, FileName);
        var temporaryPath = path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, library, JsonOptions);
        File.Move(temporaryPath, path, overwrite: true);
    }
}

public sealed record PortableEmotionalCharacterFilter(string SignalKey, double? MinimumPercent, double? MaximumPercent);
public sealed record PortableFilterGroup(
    List<string> Genres,
    List<string> Styles,
    List<string>? Tags = null,
    bool Negate = false,
    List<PortableEmotionalCharacterFilter>? EmotionalCharacters = null,
    List<string>? Languages = null,
    List<string>? MainGenres = null);

public static class PortableTrackFilter
{
    public static List<PortableTrack> Apply(
        IEnumerable<PortableTrack> tracks,
        string? searchText,
        IReadOnlySet<string> ratings,
        IReadOnlyList<PortableFilterGroup> filterGroups)
    {
        IEnumerable<PortableTrack> query = tracks;
        var term = searchText?.Trim();

        if (!string.IsNullOrWhiteSpace(term))
            query = query.Where(t => t.Title.Contains(term, StringComparison.OrdinalIgnoreCase));

        if (ratings.Count > 0)
            query = query.Where(t => ratings.Contains(t.Rating));

        var activeGroups = filterGroups
            .Where(g => g.Genres.Count > 0 || (g.MainGenres?.Count ?? 0) > 0 || g.Styles.Count > 0 || (g.Tags?.Count ?? 0) > 0 || (g.Languages?.Count ?? 0) > 0 || (g.EmotionalCharacters?.Count ?? 0) > 0)
            .ToList();
        var includeGroups = activeGroups.Where(group => !group.Negate).ToList();
        var excludeGroups = activeGroups.Where(group => group.Negate).ToList();

        if (includeGroups.Count > 0)
            query = query.Where(track => includeGroups.Any(group => MatchesGroup(track, group)));

        if (excludeGroups.Count > 0)
            query = query.Where(track => !excludeGroups.Any(group => MatchesGroup(track, group)));

        return query
            .OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool MatchesGroup(PortableTrack track, PortableFilterGroup group)
    {
        if (group.Genres.Count > 0 && !group.Genres.All(genre => track.Genres.Contains(genre, StringComparer.OrdinalIgnoreCase)))
            return false;

        if ((group.MainGenres?.Count ?? 0) > 0
            && !track.Genres.Any(genre => group.MainGenres!.Any(mainGenre =>
                genre.StartsWith($"{mainGenre} →", StringComparison.OrdinalIgnoreCase))))
            return false;

        if (group.Styles.Count > 0 && !group.Styles.All(style => track.Styles.Contains(style, StringComparer.OrdinalIgnoreCase)))
            return false;

        if ((group.Tags?.Count ?? 0) > 0 && !group.Tags!.All(tag => (track.Tags ?? []).Contains(tag, StringComparer.OrdinalIgnoreCase)))
            return false;

        if ((group.Languages?.Count ?? 0) > 0
            && (string.IsNullOrWhiteSpace(track.LanguageCode)
                || !group.Languages!.Contains(track.LanguageCode, StringComparer.OrdinalIgnoreCase)))
            return false;

        if ((group.EmotionalCharacters?.Count ?? 0) > 0)
        {
            if (track.EmotionalCharacter is null)
                return false;
            foreach (var filter in group.EmotionalCharacters!)
            {
                if (!track.EmotionalCharacter.TryGetValue(filter.SignalKey, out var score))
                    return false;
                var percent = score * 100d;
                if (filter.MinimumPercent is double minimum && percent < minimum)
                    return false;
                if (filter.MaximumPercent is double maximum && percent > maximum)
                    return false;
            }
        }

        return true;
    }
}
