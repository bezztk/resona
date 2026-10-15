using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Resona.Models;

namespace Resona.Services;

public class MusicLibraryService
{
    public static readonly MusicLibraryService Current = new();
    private readonly MusicDatabase _db = new();
    private readonly TrackDownloadService _downloader = new();
    private readonly TrackAnalysisService _analysis = new();
    private readonly Dictionary<int, IReadOnlyList<ExperimentalAnalysisModel>> _experimentalAnalysis = [];

    public void Initialize()
    {
        if (Values.LibraryLocationsLoadError is not null)
            throw new InvalidOperationException(Values.LibraryLocationsLoadError);

        _db.Initialize();
        var fileNameMigration = TrackFileNameMigration.Run(Values.DbPath, Values.TracksDirectory);
        if (fileNameMigration.Renamed > 0 || fileNameMigration.Recovered > 0)
            WorkflowLog.Info(
                "migration",
                $"Canonicalized {fileNameMigration.Renamed} track file names and recovered {fileNameMigration.Recovered} interrupted moves.");
    }

    // --- Tracks ---

    public List<MusicTrack> GetTracks() => _db.GetAllTracks();
    public List<MusicTrack> GetTracksForLibraryView() => _db.GetAllTracks(includeRejected: true);
    public int CountUnratedTracks() => _db.CountUnratedTracks();
    public List<MusicTrack> GetUnratedTracks() => _db.GetUnratedTracks();
    public byte[]? GetTrackThumbnail(int trackId) => _db.GetTrackThumbnail(trackId);
    public List<MusicTrack> GetUnanalyzedTracks() => _db.GetUnanalyzedTracks();
    public MusicTrack? GetTrackById(int id) => _db.GetTrackById(id);
    public bool ShouldAnalyzeTrack(int id) =>
        GetTrackById(id) is { } track
        && TrackWorkflowPolicy.ShouldAnalyze(
            track.LibraryState,
            track.AnalysisDisabled,
            GetTrackAudioAnalysis(id) is not null);
    public MusicTrack? GetTrackByCanonicalUrl(string canonicalUrl) =>
        _db.GetTrackByCanonicalUrl(canonicalUrl);

    public Dictionary<int, List<int>> GetAllTrackStyleIds() => _db.GetAllTrackStyleIds();
    public List<int> GetTrackStyleIds(int trackId) => _db.GetTrackStyleIds(trackId);

    public Dictionary<int, List<int>> GetAllTrackGenreIds() => _db.GetAllTrackGenreIds();
    public List<int> GetTrackGenreIds(int trackId) => _db.GetTrackGenreIds(trackId);
    public List<TrackModelGenre> GetTrackModelGenres(int trackId) => _db.GetTrackModelGenres(trackId);
    public void SetTrackModelGenreEnabled(int trackId, int genreId, bool isEnabled) => _db.SetTrackModelGenreEnabled(trackId, genreId, isEnabled);
    public void SetTrackVersion(int id, bool isOriginal, int? parentTrackId, TrackEditTypes editTypes)
    {
        _db.SetTrackVersion(id, isOriginal, parentTrackId, editTypes);
        CloudLibrarySyncService.Current.RequestSynchronization();
    }

    public void UpdateTrack(
        int id,
        string title,
        string? artist,
        string? remix,
        List<int> genreIds,
        int? ratingId,
        List<int> styleIds,
        bool isPublic)
    {
        _db.UpdateTrack(id, title, artist, remix, genreIds, ratingId, styleIds, isPublic);
        if (ratingId is not null)
            BackgroundAnalysisService.Current.EnqueueTrack(id);
    }
    public void SetTrackRating(int id, int ratingId)
    {
        _db.SetTrackRating(id, ratingId);
        BackgroundAnalysisService.Current.EnqueueTrack(id);
    }
    public void SetTrackRatingBand(int id, RatingBand? ratingBand) => _db.SetTrackRatingBand(id, ratingBand);
    public void SetTrackNeedsReview(int id, bool needsReview) => _db.SetTrackNeedsReview(id, needsReview);
    public void SetTrackAnalysisDisabled(int id, bool analysisDisabled)
    {
        _db.SetTrackAnalysisDisabled(id, analysisDisabled);
        if (!analysisDisabled)
            BackgroundAnalysisService.Current.EnqueueTrack(id);
    }
    public void RecordTrackPlaybackStarted(int trackId) => _db.RecordTrackPlaybackStarted(trackId);
    public void AddTrackListenedSeconds(int trackId, int seconds) => _db.AddTrackListenedSeconds(trackId, seconds);
    public void RecordTrackSkip(int trackId) => _db.RecordTrackSkip(trackId);
    public TrackUsageStats GetTrackUsageStats(int trackId) => _db.GetTrackUsageStats(trackId);
    public Dictionary<int, TrackUsageStats> GetAllTrackUsageStats() => _db.GetAllTrackUsageStats();
    public int ApplyCloudDeviceSnapshot(CloudDeviceLibrarySnapshot snapshot) =>
        _db.ApplyCloudDeviceSnapshot(snapshot);
    public TimeSpan? EstimateAnalysisDuration(int? trackDurationSeconds, long? fileSizeBytes) =>
        _db.EstimateAnalysisDuration(trackDurationSeconds, fileSizeBytes);
    public TimeSpan? EstimateDownloadDuration(int? trackDurationSeconds, long? fileSizeBytes) =>
        _db.EstimateDownloadDuration(trackDurationSeconds, fileSizeBytes);
    public int CreateImportBatch(string sourceUrl, IReadOnlyList<ImportPreviewItem> items, bool rejectDuplicates = false) =>
        _db.CreateImportBatch(sourceUrl, items, rejectDuplicates);
    public IReadOnlyList<MusicTrack> RecoverInterruptedImports()
    {
        var recoveredTracks = new List<MusicTrack>();
        foreach (var item in _db.GetInterruptedImportQueueItems())
        {
            var ownedTrack = item.TrackId is int trackId ? _db.GetTrackById(trackId) : null;
            if (ownedTrack is not null)
            {
                if (TrackFileExists(ownedTrack))
                {
                    _db.CompleteImportQueueItem(item.Id, ownedTrack.Id);
                    recoveredTracks.Add(_db.GetTrackById(ownedTrack.Id) ?? ownedTrack);
                }
                else
                {
                    WorkflowLog.Error("import", $"Owned track {ownedTrack.Id} has no media; cleaning queue item {item.Id} for retry.");
                    CleanupInterruptedImport(item);
                }
                continue;
            }

            // A track with the same URL may have been committed by another flow after
            // this queue item was created. It is not owned by this item and must never
            // be deleted during recovery.
            if (_db.GetTrackByCanonicalUrl(item.CanonicalUrl) is not null)
            {
                WorkflowLog.Info("import", $"Removed redundant queue item {item.Id}; its URL already belongs to another track.");
                _db.DeleteImportQueueItem(item.Id);
                continue;
            }

            if (item.Status is ImportQueueStatus.Downloading or ImportQueueStatus.Analyzing)
                CleanupInterruptedImport(item);
        }
        _db.RequeueInterruptedImports();
        return recoveredTracks;
    }
    public ImportQueueItem? GetNextQueuedImport() => _db.GetNextQueuedImport();
    public ImportQueueItem? ClaimNextQueuedImport() => _db.ClaimNextQueuedImport();
    public void UpdateImportQueueItem(int id, ImportQueueStatus status, string? detail = null, int? trackId = null) =>
        _db.UpdateImportQueueItem(id, status, detail, trackId);
    public ImportQueueSummary GetImportQueueSummary() => _db.GetImportQueueSummary();
    public HashSet<string> GetActiveImportCanonicalUrls() => _db.GetActiveImportCanonicalUrls();
    public List<ImportQueueSource> GetImportQueueSources() => _db.GetImportQueueSources();
    public bool RemoveQueuedImport(int id) => _db.RemoveQueuedImport(id);
    public void DeleteImportQueueItem(int id) => _db.DeleteImportQueueItem(id);
    public void CompleteImportQueueItem(int itemId, int trackId) => _db.CompleteImportQueueItem(itemId, trackId);

    public void CleanupInterruptedImport(ImportQueueItem item)
    {
        var videoId = YouTubeUrlNormalizer.ExtractVideoId(item.CanonicalUrl);
        if (!string.IsNullOrWhiteSpace(videoId))
            _downloader.DeleteDownloadArtifacts(videoId);

        // Only a track id persisted on this queue item establishes ownership.
        // Falling back to canonical URL can delete a legitimate track created by
        // another worker or ChannelHub during the interrupted import.
        var track = item.TrackId is int trackId ? GetTrackById(trackId) : null;

        if (track is null)
            return;

        try
        {
            var filePath = Path.Combine(Values.TracksDirectory, track.FileName);
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
        catch { }

        try { _db.DeleteTrack(track.Id); }
        catch { }
    }

    private static bool TrackFileExists(MusicTrack track) =>
        File.Exists(Path.Combine(Values.TracksDirectory, track.FileName));

    public Task<string?> DeleteTrackAsync(MusicTrack track)
    {
        try
        {
            var filePath = Path.Combine(Values.TracksDirectory, track.FileName);
            if (File.Exists(filePath))
                File.Delete(filePath);

            _db.DeleteTrack(track.Id);
            _experimentalAnalysis.Remove(track.Id);
            return Task.FromResult<string?>(null);
        }
        catch (Exception exception)
        {
            return Task.FromResult<string?>($"Could not delete track: {exception.Message}");
        }
    }

    public async Task<BulkTrackDeleteResult> DeleteAllUnratedTracksAsync()
    {
        var tracks = GetUnratedTracks();
        if (tracks.Count == 0)
            return new BulkTrackDeleteResult(0, 0);

        try
        {
            await Task.Run(() => _db.DeleteTracks(tracks.Select(track => track.Id).ToArray()));
        }
        catch (Exception exception)
        {
            return new BulkTrackDeleteResult(0, 0, exception.Message);
        }

        var failedFiles = await Task.Run(() =>
        {
            var failures = 0;
            foreach (var track in tracks)
            {
                try
                {
                    var filePath = Path.Combine(Values.TracksDirectory, track.FileName);
                    if (File.Exists(filePath))
                        File.Delete(filePath);
                }
                catch
                {
                    failures++;
                }
            }
            return failures;
        });

        foreach (var track in tracks)
            _experimentalAnalysis.Remove(track.Id);

        return new BulkTrackDeleteResult(tracks.Count, failedFiles);
    }

    // --- Lookups ---
    public List<Genre> GetGenres() => _db.GetGenres();
    public List<Tag> GetTags() => _db.GetTags();
    public void AddTag(string name) => _db.AddTag(name);
    public void RenameTag(int id, string name) => _db.RenameTag(id, name);
    public string? DeleteTagIfUnused(int id) => _db.DeleteTagIfUnused(id);
    public Dictionary<int, List<int>> GetAllTrackTagIds() => _db.GetAllTrackTagIds();
    public List<int> GetTrackTagIds(int trackId) => _db.GetTrackTagIds(trackId);
    public List<TrackTag> GetTrackTags(int trackId) => _db.GetTrackTags(trackId);
    public void SetTrackManualTags(int trackId, IReadOnlyCollection<int> tagIds) => _db.SetTrackManualTags(trackId, tagIds);
    public void SetTrackLanguage(int trackId, string? languageCode) => _db.SetTrackLanguage(trackId, languageCode);
    public List<TagSignalSource> GetTagSignalSources() => _db.GetTagSignalSources();
    public List<TagRuleGroup> GetTagRuleGroups() => _db.GetTagRuleGroups();
    public int CreateTagRuleGroup(int tagId, TagRuleMatchMode matchMode, string sourceType, string sourceKey, double threshold) =>
        _db.CreateTagRuleGroup(tagId, matchMode, sourceType, sourceKey, threshold);
    public void AddTagRuleCondition(int groupId, string sourceType, string sourceKey, double threshold) =>
        _db.AddTagRuleCondition(groupId, sourceType, sourceKey, threshold);
    public void DeleteTagRuleCondition(int conditionId) => _db.DeleteTagRuleCondition(conditionId);
    public void SetTagRuleGroupEnabled(int groupId, bool enabled) => _db.SetTagRuleGroupEnabled(groupId, enabled);
    public void SetTagRuleGroupMatchMode(int groupId, TagRuleMatchMode matchMode) =>
        _db.SetTagRuleGroupMatchMode(groupId, matchMode);
    public void DeleteTagRuleGroup(int groupId) => _db.DeleteTagRuleGroup(groupId);
    public void RefreshAllTagSuggestions() => _db.RefreshAllTagSuggestions();
    public List<TrackTagSuggestion> GetTrackTagSuggestions(int trackId) => _db.GetTrackTagSuggestions(trackId);
    public void AcceptTrackTagSuggestion(int trackId, int tagId, int ruleGroupId) =>
        _db.AcceptTrackTagSuggestion(trackId, tagId, ruleGroupId);
    public void RejectTrackTagSuggestion(int trackId, int ruleGroupId) =>
        _db.RejectTrackTagSuggestion(trackId, ruleGroupId);
    public List<Style> GetStyles() => _db.GetStyles();
    public void AddStyle(string name) => _db.AddStyle(name);
    public void RenameStyle(int id, string name) => _db.RenameStyle(id, name);
    public string? DeleteStyleIfUnused(int id) => _db.DeleteStyleIfUnused(id);
    public List<Rating> GetRatings() => _db.GetRatings();
    public List<ModelGenre> GetModelGenres() => _db.GetModelGenres();
    public List<ModelSubgenre> GetModelSubgenres(int? modelGenreId = null) => _db.GetModelSubgenres(modelGenreId);
    public void AddModelSubgenre(int modelGenreId, string name) => _db.AddModelSubgenre(modelGenreId, name);
    public void UpdateModelSubgenre(int id, string name, string? description, string? classificationHint, int? bpmMin, int? bpmMax) =>
        _db.UpdateModelSubgenre(id, name, description, classificationHint, bpmMin, bpmMax);
    public List<ModelSubgenreDistinction> GetModelSubgenreDistinctions() => _db.GetModelSubgenreDistinctions();
    public List<ManualModelGenreUsage> GetTopManualModelGenres(int limit = 10) => _db.GetTopManualModelGenres(limit);
    public List<StoredModelGenrePrediction> GetTrackGenrePredictions(int trackId) => _db.GetTrackGenrePredictions(trackId);
    public TrackAudioAnalysis? GetTrackAudioAnalysis(int trackId) => _db.GetTrackAudioAnalysis(trackId);
    public Dictionary<int, TrackAudioAnalysis> GetAllTrackAudioAnalyses() => _db.GetAllTrackAudioAnalyses();
    public Dictionary<int, Dictionary<string, double>> GetAllMirexScores() => _db.GetAllMirexScores();
    public IReadOnlyList<ExperimentalAnalysisModel> GetExperimentalAnalysis(int trackId) =>
        _db.GetTrackAnalysisSignals(trackId);

    public bool TrackExistsByCanonicalUrl(string canonicalUrl) => _db.TrackExists(canonicalUrl);

    // --- Collections ---

    public List<TrackCollection> GetCollections() => _db.GetCollections();
    public TrackCollection? GetCollectionByStableId(string stableId) => _db.GetCollectionByStableId(stableId);
    public TrackCollection CreateCollection(string name) => _db.CreateCollection(name);
    public void RenameCollection(int collectionId, string name) => _db.RenameCollection(collectionId, name);
    public void DeleteCollection(int collectionId) => _db.DeleteCollection(collectionId);
    public bool AddTrackToCollection(int collectionId, int trackId) => _db.AddTrackToCollection(collectionId, trackId);
    public bool RemoveTrackFromCollection(int collectionId, int trackId) => _db.RemoveTrackFromCollection(collectionId, trackId);
    public bool MoveCollectionTrack(int collectionId, int trackId, int offset) =>
        _db.MoveCollectionTrack(collectionId, trackId, offset);
    public List<int> GetCollectionTrackIds(int collectionId) => _db.GetCollectionTrackIds(collectionId);
    public List<CollectionTrack> GetCollectionTracks(int collectionId) => _db.GetCollectionTracks(collectionId);
    public List<TrackCollection> GetTrackCollections(int trackId) => _db.GetTrackCollections(trackId);
    public Dictionary<int, List<string>> GetAllTrackCollectionNames() => _db.GetAllTrackCollectionNames();
    public byte[]? GetCollectionCover(int collectionId) => _db.GetCollectionCover(collectionId);
    public void SetCollectionCoverAutomatic(int collectionId) => _db.SetCollectionCoverAutomatic(collectionId);
    public void SetCollectionCoverTrack(int collectionId, int trackId) => _db.SetCollectionCoverTrack(collectionId, trackId);
    public void SetCollectionCustomCover(int collectionId, byte[] cover) => _db.SetCollectionCustomCover(collectionId, cover);

    public Task<string?> GetRemoteTitleAsync(string canonicalUrl) => _downloader.GetTitleAsync(canonicalUrl);

    public HashSet<int> GetTrackIdsMissingAnalysis() => _db.GetTrackIdsMissingAnalysis();

    public async Task<ImportResult> ImportFromYouTubeAsync(
        string rawUrl,
        IProgress<string>? progress = null,
        Action<int>? trackCreated = null,
        DownloadRequest? version = null)
    {
        var videoId = YouTubeUrlNormalizer.ExtractVideoId(rawUrl);
        if (videoId is null)
            return new ImportResult(false, Error: "Could not parse YouTube URL.");

        var canonicalUrl = YouTubeUrlNormalizer.GetCanonicalUrl(videoId);
        if (_db.TrackExists(canonicalUrl))
            return new ImportResult(false, Error: "Track already exists.");

        var result = await DownloadAndPersistYouTubeTrackAsync(
            videoId,
            canonicalUrl,
            version?.GenreIds ?? [],
            version?.RatingId,
            version?.StyleIds ?? [],
            progress,
            version);
        if (result.Success && result.Track is not null)
            trackCreated?.Invoke(result.Track.Id);
        return result;
    }

    private async Task<ImportResult> DownloadAndPersistYouTubeTrackAsync(
        string videoId,
        string canonicalUrl,
        List<int> genreIds,
        int? ratingId,
        List<int> styleIds,
        IProgress<string>? progress,
        DownloadRequest? version = null)
    {
        _downloader.DeleteDownloadArtifacts(videoId);

        progress?.Report("Checking audio details…");
        var previewMetadata = await _downloader.GetMetadataAsync(canonicalUrl);
        var downloadEstimate = EstimateDownloadDuration(
            previewMetadata?.DurationSeconds,
            previewMetadata?.EstimatedAudioSizeBytes);
        var downloadStopwatch = Stopwatch.StartNew();
        var (success, errorOutput) = await DownloadAudioAsync(
            canonicalUrl,
            progress,
            downloadEstimate);
        if (!success)
            return new ImportResult(false, Error: $"Download failed:\n{errorOutput}");

        var filePath = _downloader.FindDownloadedFile(videoId);
        if (filePath is null)
            return new ImportResult(false, Error: "Download finished but file not found.");

        try
        {
            var canonicalFilePath = Path.Combine(
                Values.TracksDirectory,
                TrackFileNameMigration.CanonicalFileName(videoId, Path.GetFileName(filePath)));
            if (!string.Equals(filePath, canonicalFilePath, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(canonicalFilePath))
                    throw new IOException($"Canonical track file already exists: {Path.GetFileName(canonicalFilePath)}");
                File.Move(filePath, canonicalFilePath);
                filePath = canonicalFilePath;
            }
            var fileName = Path.GetFileName(filePath);
            var duration = await _downloader.GetDurationAsync(filePath);
            var metadata = previewMetadata ?? await _downloader.GetMetadataAsync(canonicalUrl);
            var fileSizeBytes = new FileInfo(filePath).Length;
            var thumbnail = ThumbnailService.ReadEmbeddedArtworkThumbnail(filePath) ?? [];
            var trackId = _db.InsertTrack(
                canonicalUrl,
                metadata?.Title ?? _downloader.TitleFromFileName(fileName),
                fileName,
                genreIds,
                ratingId,
                styleIds,
                duration,
                fileSizeBytes,
                (int)downloadStopwatch.ElapsedMilliseconds,
                metadata,
                thumbnail,
                version?.IsOriginal ?? true,
                version?.ParentTrackId,
                version?.EditTypes ?? TrackEditTypes.None,
                version?.VersionName);
            var track = GetTrackById(trackId)
                ?? throw new InvalidOperationException($"Track {trackId} was inserted but could not be reloaded.");
            CloudLibrarySyncService.Current.RequestSynchronization();
            WorkflowLog.Info("download", $"Persisted track {trackId} from a YouTube download.");
            return new ImportResult(true, track);
        }
        catch (Exception exception)
        {
            WorkflowLog.Error("download", "Could not persist downloaded track.", exception);
            try
            {
                File.Delete(filePath);
            }
            catch (Exception cleanupException)
            {
                WorkflowLog.Error("download", "Could not remove an unowned download after persistence failed.", cleanupException);
            }

            return new ImportResult(false, Error: "The audio was downloaded but could not be added to the library.");
        }
    }

    public async Task<(string? Error, bool Retryable)> AnalyzeTrackAsync(
        MusicTrack track,
        CancellationToken cancellationToken = default)
    {
        var filePath = Path.Combine(Values.TracksDirectory, track.FileName);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await _analysis.AnalyzeTrackAsync(filePath, cancellationToken);
            var analysis = TrackAnalysisService.ToTrackAnalysisResult(response);
            _db.SaveTrackAnalysis(track.Id, analysis, (int)stopwatch.ElapsedMilliseconds);
            CacheExperimentalAnalysis(track.Id, analysis);
            return (null, false);
        }
        catch (MusicAnalysisException exception)
        {
            if (exception.Kind is MusicAnalysisErrorKind.FileError or MusicAnalysisErrorKind.InvalidResponse)
                _db.MarkTrackAnalysisFailed(track.Id);
            return (exception.Message,
                exception.Kind is MusicAnalysisErrorKind.ConnectionError or MusicAnalysisErrorKind.Timeout
                || exception.Kind == MusicAnalysisErrorKind.ServerError
                && exception.StatusCode is { } statusCode
                && (int)statusCode >= 500);
        }
    }

    private void CacheExperimentalAnalysis(int trackId, TrackAnalysisResult analysis) =>
        _experimentalAnalysis[trackId] = analysis.ExperimentalModels ?? [];

    private string AnalysisProgressText(int? durationSeconds, long fileSizeBytes)
    {
        var estimate = EstimateAnalysisDuration(durationSeconds, fileSizeBytes);
        return estimate is null
            ? "Analyzing track…"
            : $"Analyzing track… usually about {FormatEstimate(estimate.Value)}";
    }

    private static string FormatEstimate(TimeSpan duration) => duration.TotalMinutes >= 1
        ? $"{Math.Ceiling(duration.TotalMinutes):0} min"
        : $"{Math.Max(1, Math.Round(duration.TotalSeconds)):0} sec";

    private async Task<(bool Success, string ErrorOutput)> DownloadAudioAsync(
        string canonicalUrl,
        IProgress<string>? progress,
        TimeSpan? estimate)
    {
        progress?.Report(estimate is null
            ? "Downloading audio…"
            : $"Downloading audio… usually about {FormatEstimate(estimate.Value)}");
        return await _downloader.RunYtDlpAsync(canonicalUrl);
    }

}
