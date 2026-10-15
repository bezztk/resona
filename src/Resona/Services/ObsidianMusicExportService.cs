using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Resona.Core;
using Resona.Models;

namespace Resona.Services;

public sealed record ObsidianMusicExportResult(
    string MusicDirectory,
    int TrackCount,
    int CopiedTracks,
    int MissingTracks,
    int WrittenImages);

public static class ObsidianMusicExportService
{
    public static string MusicDirectory(string vaultPath) =>
        Path.Combine(Path.GetFullPath(vaultPath), "Data", "Music");

    public static async Task<ObsidianMusicExportResult> ExportAsync(
        string vaultPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(vaultPath) || !Path.IsPathFullyQualified(vaultPath))
            throw new InvalidOperationException("Choose an absolute Obsidian vault folder first.");

        var musicDirectory = MusicDirectory(vaultPath);
        var tracksDirectory = Path.Combine(musicDirectory, "Tracks");
        var imagesDirectory = Path.Combine(musicDirectory, "Images");
        Directory.CreateDirectory(tracksDirectory);
        Directory.CreateDirectory(imagesDirectory);

        var library = MusicLibraryService.Current;
        var sourceTracks = library.GetTracks();
        var ratings = library.GetRatings();
        var ratingNames = ratings.ToDictionary(item => item.Id, item => item.Name);
        var genres = library.GetGenres().ToDictionary(item => item.Id, item => item.Name);
        var styles = library.GetStyles().ToDictionary(item => item.Id, item => item.Name);
        var tags = library.GetTags().ToDictionary(item => item.Id, item => item.Name);
        var trackGenreIds = library.GetAllTrackGenreIds();
        var trackStyleIds = library.GetAllTrackStyleIds();
        var trackTagIds = library.GetAllTrackTagIds();
        var emotionalCharacters = library.GetAllMirexScores();
        var usage = library.GetAllTrackUsageStats();

        var trackKeys = sourceTracks.ToDictionary(track => track.Id, TrackKey);
        var portableTracks = new List<PortableTrack>(sourceTracks.Count);
        var copiedTracks = 0;
        var missingTracks = 0;
        var writtenImages = 0;

        foreach (var track in sourceTracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = Path.Combine(Values.TracksDirectory, track.FileName);
            var targetPath = Path.Combine(tracksDirectory, track.FileName);
            if (File.Exists(sourcePath))
            {
                if (!FilesMatch(sourcePath, targetPath))
                {
                    File.Copy(sourcePath, targetPath, overwrite: true);
                    copiedTracks++;
                }
            }
            else if (!File.Exists(targetPath))
            {
                missingTracks++;
            }

            string? coverFileName = null;
            var thumbnail = library.GetTrackThumbnail(track.Id);
            if (thumbnail is { Length: > 0 })
            {
                coverFileName = $"{trackKeys[track.Id]}.jpg";
                var imagePath = Path.Combine(imagesDirectory, coverFileName);
                if (!File.Exists(imagePath) || new FileInfo(imagePath).Length != thumbnail.Length)
                {
                    await File.WriteAllBytesAsync(imagePath, thumbnail, cancellationToken);
                    writtenImages++;
                }
            }

            usage.TryGetValue(track.Id, out var trackUsage);
            trackUsage ??= new TrackUsageStats(0, 0, 0, null);
            emotionalCharacters.TryGetValue(track.Id, out var emotional);
            portableTracks.Add(new PortableTrack(
                track.DisplayTitle,
                track.FileName,
                track.DurationSeconds,
                track.RatingId is int ratingId ? ratingNames.GetValueOrDefault(ratingId) ?? string.Empty : string.Empty,
                Names(trackGenreIds.GetValueOrDefault(track.Id, []), genres),
                Names(trackStyleIds.GetValueOrDefault(track.Id, []), styles),
                CoverFileName: coverFileName,
                NeedsReview: track.NeedsReview,
                Tags: Names(trackTagIds.GetValueOrDefault(track.Id, []), tags),
                DownloadedAt: track.DownloadedAt,
                ChannelName: track.DisplayChannelName,
                ChannelUrl: track.ChannelUrl,
                UploadedAt: track.UploadedAt,
                PlayCount: trackUsage.PlayCount,
                ListenedSeconds: trackUsage.ListenedSeconds,
                SkipCount: trackUsage.SkipCount,
                LastListenedAt: trackUsage.LastListenedAt,
                IsPublic: track.IsPublic,
                LanguageCode: track.LanguageCode,
                TrackKey: trackKeys[track.Id],
                OriginalTitle: track.OriginalTitle,
                LibraryState: track.LibraryState.ToString(),
                EmotionalCharacter: emotional is null
                    ? new Dictionary<string, double>()
                    : emotional.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(item => item.Key, item => item.Value)));
        }

        var collections = library.GetCollections()
            .Select(collection => new PortableCollection(
                collection.StableId,
                collection.Name,
                library.GetCollectionTrackIds(collection.Id)
                    .Where(trackKeys.ContainsKey)
                    .Select(trackId => trackKeys[trackId])
                    .ToList()))
            .ToList();
        var exportedAt = DateTime.UtcNow.ToString("O");
        var snapshot = new PortableMusicLibrary(
            portableTracks,
            FilterPresetStore.Load(),
            ExportId: Guid.NewGuid().ToString("N"),
            ExportedAt: exportedAt,
            MediaMode: "full",
            RatingDefinitions: ratings.OrderBy(item => item.SortOrder)
                .Select(item => new PortableRating(item.Name, item.SortOrder))
                .ToList(),
            Collections: collections);

        await PortableLibraryStore.SaveAsync(musicDirectory, snapshot);
        return new ObsidianMusicExportResult(
            musicDirectory,
            portableTracks.Count,
            copiedTracks,
            missingTracks,
            writtenImages);
    }

    private static string TrackKey(MusicTrack track)
    {
        var key = string.IsNullOrWhiteSpace(track.SourceVideoId)
            ? YouTubeUrlNormalizer.ExtractVideoId(track.CanonicalUrl)
            : track.SourceVideoId.Trim();
        return string.IsNullOrWhiteSpace(key) ? $"track-{track.Id}" : key;
    }

    private static List<string> Names(
        IEnumerable<int> ids,
        IReadOnlyDictionary<int, string> names) => ids
        .Select(id => names.GetValueOrDefault(id))
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Select(name => name!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static bool FilesMatch(string sourcePath, string targetPath) =>
        File.Exists(targetPath)
        && new FileInfo(sourcePath).Length == new FileInfo(targetPath).Length;
}
