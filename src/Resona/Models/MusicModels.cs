using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia.Media;
using Avalonia.Media.Imaging;

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
    string? Remix = null,
    RatingBand? RatingBand = null,
    bool IsOriginal = true,
    int? ParentTrackId = null,
    TrackEditTypes EditTypes = TrackEditTypes.None)
{
    public string DisplayTitle => TrackTitleFormatter.Format(Artist, Title, Remix);
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
    public static string Format(string? artist, string title, string? remix)
    {
        var cleanTitle = title.Trim();
        var result = string.IsNullOrWhiteSpace(artist)
            ? cleanTitle
            : $"{artist.Trim()} — {cleanTitle}";

        if (!string.IsNullOrWhiteSpace(remix))
            result += $" ({remix.Trim()})";

        return result;
    }
}

public sealed record TrackLanguage(string Code, string Name);
public static class TrackLanguageCatalog
{
    public static readonly IReadOnlyList<TrackLanguage> All =
    [
        new("zxx", "Instrumental"), new("mul", "Mixed / multilingual"),
        new("en", "English"), new("de", "German"), new("es", "Spanish"),
        new("pt-BR", "Portuguese (Brazil)"), new("fr", "French"), new("it", "Italian"),
        new("nl", "Dutch"), new("ru", "Russian"), new("uk", "Ukrainian"),
        new("pl", "Polish"), new("tr", "Turkish"), new("ar", "Arabic"),
        new("fa", "Persian"), new("hi", "Hindi"), new("bn", "Bengali"),
        new("zh", "Mandarin Chinese"), new("ja", "Japanese"), new("ko", "Korean"),
        new("vi", "Vietnamese"), new("th", "Thai"), new("id", "Indonesian"),
        new("sv", "Swedish"), new("no", "Norwegian"), new("fi", "Finnish"),
        new("el", "Greek"), new("he", "Hebrew"), new("ro", "Romanian"),
        new("cs", "Czech"), new("sr", "Serbian")
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
public record YouTubeChannelSnapshot(
    string SourceUrl,
    string? ChannelId,
    string Name,
    string? ChannelUrl,
    IReadOnlyList<YouTubeChannelVideoEntry> Videos,
    string? ThumbnailUrl = null,
    byte[]? Thumbnail = null,
    long? FollowerCount = null);
public record YouTubeChannelVideoEntry(
    string VideoId,
    string CanonicalUrl,
    string Title,
    int? DurationSeconds,
    string? UploadedAt);
public record ChannelSubscription(
    int Id,
    string Name,
    string SourceUrl,
    string? SourceChannelId,
    string? LastCheckedAt,
    int VideoCount,
    int UncheckedCount);
public sealed record ChannelHubItem(
    int Id,
    string Name,
    string SourceUrl,
    string? SourceChannelId,
    bool IsFollowed,
    bool NotificationsEnabled,
    string? LastCheckedAt,
    int LocalTrackCount,
    int RatedTrackCount,
    double? AverageRating,
    int TimelessCount,
    int GreatOrBetterCount,
    int PlayCount,
    int SkipCount,
    string? LastDownloadedAt,
    int KnownVideoCount,
    int UncheckedVideoCount,
    int AvailableVideoCount,
    int ReviewVideoCount,
    long? FollowerCount,
    long? TotalViewCount,
    IReadOnlyList<string> TopTracks,
    byte[]? Thumbnail = null,
    string? BasicMetadataCheckedAt = null)
{
    private static readonly IBrush[] RatingProgressBrushes =
    [
        new SolidColorBrush(Color.FromRgb(255, 75, 75)),
        new SolidColorBrush(Color.FromRgb(210, 205, 95)),
        new SolidColorBrush(Color.FromRgb(45, 190, 240)),
        new SolidColorBrush(Color.FromRgb(55, 224, 105)),
        new SolidColorBrush(Color.FromRgb(255, 132, 48)),
        new SolidColorBrush(Color.FromRgb(255, 215, 64))
    ];

    public Bitmap? Artwork { get; set; }
    public bool HasArtwork => Artwork is not null;
    public bool ShowMonogram => Artwork is null;
    public string Monogram
    {
        get
        {
            foreach (var character in Name.EnumerateRunes())
                if (!Rune.IsWhiteSpace(character))
                    return character.ToString().ToUpperInvariant();
            return "?";
        }
    }

    public string TrackCountText => LocalTrackCount == 1 ? "1 track in library" : $"{LocalTrackCount:N0} tracks in library";
    public string LibrarySummaryText => LocalTrackCount == 1
        ? RatedTrackCount == 1 ? "1 track · rated" : "1 track · unrated"
        : $"{LocalTrackCount:N0} tracks · {RatedTrackCount:N0} rated";
    public string LibraryDetailText
    {
        get
        {
            if (!System.DateTime.TryParse(LastDownloadedAt, out var downloadedAt))
                return RecommendationReason;
            return $"{RecommendationReason} · latest {downloadedAt.ToLocalTime():dd MMM yyyy}";
        }
    }
    public string RatingText => AverageRating is double average
        ? $"{average:0.0} avg · {RatedTrackCount} out of {LocalTrackCount} tracks rated"
        : "No ratings yet";
    public string QualitySummaryText => AverageRating is double average
        ? $"{average:0.0} average rating"
        : "No ratings yet";
    public string QualityCompactText => AverageRating is double average
        ? $"{average:0.0} avg · {RatedTrackCount:N0}/{LocalTrackCount:N0} rated"
        : "No ratings yet";
    public string QualityDetailText => AverageRating is null
        ? "No rated tracks"
        : $"{TimelessCount:N0} timeless · {GreatOrBetterCount:N0} great+";
    public string ActivityText => PlayCount == 0 ? "Not played yet" : $"{PlayCount} plays · {SkipCount} skips";
    public string ListeningSignalText => PlayCount <= 0
        ? "No listening history"
        : $"{System.Math.Clamp(SkipCount / (double)PlayCount * 100d, 0d, 100d):0}% skip rate";
    public string VideoSummaryText => KnownVideoCount == 1 ? "1 known video" : $"{KnownVideoCount:N0} known videos";
    public string LibraryVideoSummaryText =>
        $"{LocalTrackCount:N0} {(LocalTrackCount == 1 ? "track" : "tracks")} in library · " +
        $"{KnownVideoCount:N0} {(KnownVideoCount == 1 ? "video" : "videos")} found on channel";
    public string AverageRatingScaleText => AverageRating is double average
        ? $"{average.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} / 6.0"
        : "— / 6.0";
    public double AverageRatingScore => System.Math.Clamp(AverageRating ?? 0d, 0d, 6d);
    public IBrush AverageRatingProgressBrush
    {
        get
        {
            var level = System.Math.Clamp(
                (int)System.Math.Round(AverageRating ?? 0d, MidpointRounding.AwayFromZero),
                1,
                RatingProgressBrushes.Length);
            return RatingProgressBrushes[level - 1];
        }
    }
    public string VideoQueueText => UncheckedVideoCount == 0
        ? "No tracks awaiting a decision"
        : UncheckedVideoCount == 1 ? "1 track awaiting a decision" : $"{UncheckedVideoCount:N0} tracks awaiting a decision";
    public string NewVideoText => UncheckedVideoCount == 1 ? "1 to review" : $"{UncheckedVideoCount:N0} to review";
    public bool HasNewVideos => UncheckedVideoCount > 0;
    public string FollowingWorkflowText =>
        $"{AvailableVideoCount:N0} available · {ReviewVideoCount:N0} to review";
    public bool HasTopTracks => TopTracks.Count > 0;
    public string TopTracksText => string.Join("\n", TopTracks.Take(3));
    public string TopTracksInlineText => TopTracks.Count == 0
        ? "No local tracks yet"
        : string.Join("  ·  ", TopTracks.Take(3));
    public string FollowActionText => IsFollowed ? "Following" : "Follow";
    public string FollowGlyph => IsFollowed ? "×" : "+";
    public string FollowToolTip => IsFollowed ? "Unfollow channel" : "Follow channel";
    public bool CanSubscribe => !IsFollowed;
    public string FollowerText => FollowerCount is long followers
        ? $"{FormatCompactNumber(followers)} subscribers"
        : string.Empty;
    public string FollowerCountText => FollowerCount is long followers
        ? FormatCompactNumber(followers)
        : "—";
    public string TotalViewText => TotalViewCount is long views
        ? $"{FormatCompactNumber(views)} views on known videos"
        : "View count unavailable";
    public string TotalViewCountText => TotalViewCount is long views
        ? FormatCompactNumber(views)
        : "—";
    public string AudienceText => FollowerText.Length > 0
        ? $"{FollowerText} · {TotalViewText}"
        : $"Subscribers unavailable · {TotalViewText}";
    public string ChannelMetadataText
    {
        get
        {
            var followerText = AudienceText;
            if (!System.DateTime.TryParse(LastCheckedAt, out var checkedAt))
                return $"{followerText} · not checked yet";
            return $"{followerText} · checked {checkedAt.ToLocalTime():dd MMM yyyy}";
        }
    }
    public string RecommendationReason => TimelessCount > 0
        ? TimelessCount == 1 ? "1 timeless track in your library" : $"{TimelessCount} timeless tracks in your library"
        : GreatOrBetterCount > 0
            ? GreatOrBetterCount == 1 ? "1 highly rated track" : $"{GreatOrBetterCount} highly rated tracks"
            : LocalTrackCount == 1 ? "1 track in your library" : $"{LocalTrackCount} tracks in your library";

    private static string FormatCompactNumber(long value) => value switch
    {
        >= 1_000_000_000 => $"{value / 1_000_000_000d:0.#}B",
        >= 1_000_000 => $"{value / 1_000_000d:0.#}M",
        >= 1_000 => $"{value / 1_000d:0.#}K",
        _ => value.ToString()
    };
}
public sealed record ChannelNotification(
    int Id,
    int ChannelId,
    int? ChannelVideoId,
    string ChannelName,
    string Title,
    string CreatedAt,
    bool IsRead,
    string? CanonicalUrl)
{
    public string StateText => IsRead ? "Seen" : "New";
    public string CreatedText
    {
        get
        {
            if (!System.DateTime.TryParse(CreatedAt, out var created)) return string.Empty;
            var local = created.ToLocalTime();
            var age = System.DateTime.Now - local;
            if (age.TotalMinutes < 1) return "Just now";
            if (age.TotalHours < 1) return $"{System.Math.Max(1, (int)age.TotalMinutes)} min ago";
            if (age.TotalDays < 1) return $"{System.Math.Max(1, (int)age.TotalHours)} h ago";
            if (age.TotalDays < 7) return $"{System.Math.Max(1, (int)age.TotalDays)} d ago";
            return local.ToString("dd MMM yyyy");
        }
    }
}
public enum ChannelDownloadStatus { NotQueued, Queued, Downloading, Ready, Failed, Skipped }
public enum ChannelMetadataStatus { Pending, Queued, Loading, Ready, Failed }
public record ChannelVideo(
    int Id,
    int ChannelId,
    string VideoId,
    string CanonicalUrl,
    string Title,
    int? DurationSeconds,
    string? UploadedAt,
    string DiscoveredAt,
    bool IsChecked,
    ChannelDownloadStatus DownloadStatus,
    string? DownloadError,
    int DownloadAttempts,
    int? TrackId,
    ChannelMetadataStatus MetadataStatus = ChannelMetadataStatus.Pending,
    string? MetadataUpdatedAt = null,
    string? MetadataError = null,
    int MetadataAttempts = 0,
    long? ViewCount = null,
    long? LikeCount = null,
    string? ThumbnailUrl = null,
    TrackLibraryState? LibraryState = null,
    string? ChannelName = null,
    string? RatingName = null,
    int? RatingSortOrder = null,
    int ListenCount = 0,
    int ListenedSeconds = 0,
    int SkipCount = 0);
public record ChannelRefreshResult(bool Success, int AddedCount, int UpdatedCount, string? Error = null);
public record ModelGenre(int Id, string Name);
public record ModelSubgenre(
    int Id,
    int ModelGenreId,
    string Name,
    string? Description = null,
    string? ClassificationHint = null,
    int? BpmMin = null,
    int? BpmMax = null);
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
    IReadOnlyList<ModelGenreReason> Reasons);
