using System.Collections.Generic;
using Resona.Core;

namespace Resona.Models;

public sealed record CloudLibrarySnapshot(
    int SchemaVersion,
    CloudPublicProfile Profile,
    int TrackCount,
    string GeneratedAt,
    IReadOnlyList<CloudPublicTrack> Tracks);

public sealed record CloudPublicProfile(
    string UserId,
    string Username,
    string Bio,
    byte[]? ProfileImage,
    string UpdatedAt);

public sealed record CloudPublicTrack(
    string SourceVideoId,
    string CanonicalUrl,
    string Title,
    string OriginalTitle,
    string? ChannelName,
    string? ChannelUrl,
    int? DurationSeconds,
    string? UploadedAt,
    string? ThumbnailUrl,
    string? Rating,
    string? LanguageCode,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Genres,
    CloudPublicTrackAnalysis? Analysis,
    IReadOnlyDictionary<string, double> EmotionalCharacter,
    string UpdatedAt);

public sealed record CloudPublicTrackAnalysis(
    double? Bpm,
    double? IntegratedLoudness,
    double? LoudnessRange);

public sealed record CloudMediaFile(
    string TrackKey,
    string FileName,
    long FileSizeBytes,
    string Sha256,
    string UploadedAt);

public sealed record CloudMediaInventory(
    IReadOnlyList<CloudMediaFile> Files);

public sealed record CloudDeviceLibrarySnapshot(
    int SchemaVersion,
    string UserId,
    int TrackCount,
    string GeneratedAt,
    IReadOnlyList<PortableRating> Ratings,
    IReadOnlyList<PortableFilterPreset> FilterPresets,
    IReadOnlyList<CloudDeviceCollection> Collections,
    IReadOnlyList<CloudDeviceTrack> Tracks,
    long LibraryRevision = 0,
    long PresetsRevision = 0);

public sealed record CloudDeviceCollection(
    string StableId,
    string Name,
    IReadOnlyList<string> TrackKeys);

public sealed record CloudDeviceTrack(
    string TrackKey,
    string FileName,
    string Title,
    string OriginalTitle,
    string? Artist,
    string? Remix,
    int? DurationSeconds,
    string? Rating,
    string? RatingBand,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Styles,
    IReadOnlyList<string> Tags,
    string? LanguageCode,
    bool NeedsReview,
    string LibraryState,
    byte[]? Thumbnail,
    int PlayCount,
    int ListenedSeconds,
    int SkipCount,
    string? LastListenedAt,
    CloudPublicTrackAnalysis? Analysis,
    IReadOnlyDictionary<string, double> EmotionalCharacter,
    string UpdatedAt,
    bool AudioAvailable = false,
    long? AudioFileSizeBytes = null,
    string? AudioSha256 = null,
    bool IsOriginal = true,
    string? ParentTrackKey = null,
    IReadOnlyList<string>? EditTypes = null,
    long Revision = 0,
    string? CanonicalUrl = null,
    bool IsPublic = true);

public sealed record CloudTrackUpdateRequest(
    long ExpectedRevision,
    CloudDeviceTrack Track);

public sealed record CloudPresetsUpdateRequest(
    long ExpectedRevision,
    IReadOnlyList<PortableFilterPreset> Presets);

public sealed record CloudRevisionConflict(
    string Entity,
    long ExpectedRevision,
    long CurrentRevision,
    object CurrentValue);
