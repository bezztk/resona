using System;
using System.Collections.Generic;
using System.Linq;

namespace Resona.Models;

public enum TrackLibraryState
{
    PendingRating,
    Active,
    Rejected
}

public enum RatingBand
{
    Low,
    Mid,
    High
}

public sealed record BulkTrackDeleteResult(int Deleted, int FailedFiles, string? Error = null);

public record MusicTrack(
    int Id, string CanonicalUrl, string Title, string FileName,
    int? RatingId, string DownloadedAt, int? DurationSeconds, bool NeedsReview,
    string? ChannelName, string? ChannelUrl, string? UploadedAt, string UpdatedAt,
    bool AnalysisDisabled = false, bool IsPublic = true, byte[]? Thumbnail = null,
    TrackLibraryState LibraryState = TrackLibraryState.Active,
    string? SourceVideoId = null,
    long? ViewCount = null,
    long? LikeCount = null,
    string? SourceThumbnailUrl = null,
    string? SourceMetadataUpdatedAt = null,
    int? ChannelId = null,
    string? LanguageCode = null,
    string OriginalTitle = "",
    string? Artist = null,
    RatingBand? RatingBand = null)
{
    public string DisplayTitle => TrackTitleFormatter.Format(Artist, Title);
    public string? DisplayChannelName => ChannelNameFormatter.Format(ChannelName);
}

public static class ChannelNameFormatter
{
    private const string TopicSuffix = " - Topic";

    public static string? Format(string? channelName)
    {
        if (string.IsNullOrWhiteSpace(channelName))
            return channelName;

        var trimmed = channelName.Trim();
        return trimmed.EndsWith(TopicSuffix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^TopicSuffix.Length].TrimEnd()
            : trimmed;
    }
}

public static class TrackTitleFormatter
{
    public static string Format(string? artist, string title)
    {
        var cleanTitle = title.Trim();
        var result = string.IsNullOrWhiteSpace(artist)
            ? cleanTitle
            : $"{artist.Trim()} — {cleanTitle}";

        return result;
    }
}

public sealed record TrackLanguage(string Code, string Name);
public static class TrackLanguageCatalog
{
    public static readonly IReadOnlyList<TrackLanguage> All =
    [
        new("mul", "Multilingual"), new("ar", "Arabic"), new("fa", "Persian"),
        new("en", "English"), new("de", "German"), new("es", "Spanish"),
        new("fr", "French"), new("ru", "Russian"), new("tr", "Turkish"),
        new("zh", "Chinese"), new("ja", "Japanese"), new("ko", "Korean")
    ];

    public static string? Name(string? code) => All
        .FirstOrDefault(language => string.Equals(language.Code, code, System.StringComparison.OrdinalIgnoreCase))
        ?.Name;
}

public record Genre(int Id, string Name);
public record Tag(int Id, string Name);
public record TrackTag(int Id, string Name);
public enum CollectionCoverKind { Automatic, Track, Custom }
public sealed record TrackCollection(
    int Id,
    string StableId,
    string Name,
    CollectionCoverKind CoverKind,
    int? CoverTrackId,
    int TrackCount,
    int DurationSeconds,
    string CreatedAt,
    string UpdatedAt);
public sealed record CollectionTrack(int TrackId, string Title, int Position);
public record TagSignalSource(string ModelName, string SignalKey, string Description)
{
    public string DisplayName => $"{ModelName} · {SignalKey}";
}
public record TagRuleCondition(
    int Id,
    string SourceType,
    string SourceKey,
    double Threshold);
public record TagRuleGroup(
    int Id,
    int TagId,
    string TagName,
    TagRuleMatchMode MatchMode,
    bool Enabled,
    IReadOnlyList<TagRuleCondition> Conditions);
public enum TagRuleMatchMode { All, Any }
public record TrackTagSuggestion(
    int RuleGroupId,
    int TagId,
    string TagName,
    TagRuleMatchMode MatchMode,
    string ConditionSummary,
    double Score,
    string State);
public record Style(int Id, string Name);
public record Rating(int Id, string Name, int SortOrder);
public static class RatingNames
{
    public const string Avoid = "Avoid";
    public const string Amazing = "Amazing";
    public const string Timeless = "Timeless";
}
public record TrackUsageStats(int PlayCount, int ListenedSeconds, int SkipCount, string? LastListenedAt);
public record YouTubeTrackMetadata(
    string? Title,
    string? ChannelId,
    string? ChannelName,
    string? ChannelUrl,
    string? UploadedAt,
    long? EstimatedAudioSizeBytes = null,
    int? DurationSeconds = null,
    long? ViewCount = null,
    long? LikeCount = null,
    long? ChannelFollowerCount = null,
    string? ThumbnailUrl = null);
public record ModelGenre(int Id, string Name);
public record ModelSubgenre(
    int Id,
    int ModelGenreId,
    string Name,
    string? Description = null,
    string? ClassificationHint = null,
    int? BpmMin = null,
    int? BpmMax = null,
    bool IsPreferred = false,
    bool IsManual = false);
public record ModelSubgenreDistinction(
    int ModelSubgenreId,
    int DistinguishFromModelSubgenreId,
    string ModelGenreName,
    string ModelSubgenreName,
    string Difference);
public record StoredModelGenrePrediction(
    int ModelGenreId,
    string ModelGenreName,
    int ModelSubgenreId,
    string ModelSubgenreName,
    double Score);
public record ManualModelGenreUsage(
    int ModelSubgenreId,
    int ModelGenreId,
    string ModelSubgenreName,
    string ModelGenreName,
    int UsageCount);
public record ModelGenreReason(string ModelGenreName, string ModelSubgenreName, double Score);
public record TrackModelGenre(
    int GenreId,
    string GenreName,
    bool IsEnabled,
    bool IsManual,
    bool IsPreferred,
    bool IsReference,
    IReadOnlyList<ModelGenreReason> Reasons);
public sealed record GenreReferenceTrack(
    int TrackId,
    string Title,
    string? Artist,
    string? ChannelName,
    string FileName)
{
    public string DisplayTitle => TrackTitleFormatter.Format(Artist, Title);
    public string DisplayArtist => !string.IsNullOrWhiteSpace(Artist)
        ? Artist.Trim()
        : ChannelNameFormatter.Format(ChannelName) ?? "Unknown artist";
}
