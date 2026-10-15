using System;
using System.Collections.Generic;

namespace Resona.Models;

public record ImportResult(
    bool Success,
    MusicTrack? Track = null,
    string? Error = null,
    string? Warning = null);

public enum ImportQueueStatus { Queued, Downloading, Analyzing, ReadyForReview, Failed, Skipped }

public record ImportPreviewItem(
    string SourceUrl,
    string CanonicalUrl,
    string Title,
    int? DurationSeconds,
    long? EstimatedSizeBytes,
    ImportQueueStatus Status,
    string? Detail = null,
    bool IsOriginal = true,
    int? ParentTrackId = null,
    TrackEditTypes EditTypes = TrackEditTypes.None,
    DownloadRequest? Request = null);

public record ImportPreview(
    IReadOnlyList<ImportPreviewItem> Items,
    int ExistingCount,
    int DuplicateCount,
    int UnavailableCount,
    long? TotalEstimatedSizeBytes,
    TimeSpan? EstimatedDownloadTime,
    TimeSpan? EstimatedAnalysisTime);

public record ImportQueueItem(
    int Id,
    int BatchId,
    string SourceUrl,
    string CanonicalUrl,
    string Title,
    int? DurationSeconds,
    long? EstimatedSizeBytes,
    ImportQueueStatus Status,
    string? Detail,
    int? TrackId,
    bool IsOriginal = true,
    int? ParentTrackId = null,
    TrackEditTypes EditTypes = TrackEditTypes.None,
    DownloadRequest? Request = null)
{
    public DownloadRequest DownloadRequest => Request ?? new()
    {
        RawUrl = CanonicalUrl,
        IsOriginal = IsOriginal,
        ParentTrackId = ParentTrackId,
        EditTypes = EditTypes
    };
}

public record ImportQueuePhase(int ItemId, ImportQueueStatus Status, DateTime StartedAtUtc);

public record ImportQueueSource(
    string SourceUrl,
    IReadOnlyList<ImportQueueItem> Items);

public record YouTubePlaylistEntry(string SourceUrl, string CanonicalUrl, string Title, int? DurationSeconds);
public record ImportQueueSummary(int Queued, int Downloading, int Analyzing, int ReadyForReview);
