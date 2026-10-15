using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Resona.Models;

namespace Resona.Services;

/// <summary>The single persistent YouTube download queue. Exactly one worker processes it.</summary>
public sealed class ImportQueueService
{
    public static readonly ImportQueueService Current = new();

    private readonly TrackDownloadService _downloader = new();
    private readonly object _workerGate = new();
    private readonly object _phaseGate = new();
    private readonly Dictionary<int, ImportQueuePhase> _activePhases = [];
    private Task? _workerTask;

    public event Action<ImportQueueItem>? ItemUpdated;
    public event Action<MusicTrack, string?>? TrackImported;

    public void Initialize()
    {
        foreach (var track in MusicLibraryService.Current.RecoverInterruptedImports())
        {
            WorkflowLog.Info("import", $"Recovered completed import for track {track.Id}.");
            BackgroundAnalysisService.Current.EnqueueTrack(track.Id);
        }
        EnsureWorker();
    }

    public async Task<ImportPreview> PreviewAsync(IEnumerable<string> sourceUrls, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var previewItems = new List<ImportPreviewItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existingCount = 0;
        var duplicateCount = 0;
        var unavailableCount = 0;
        var alreadyQueued = MusicLibraryService.Current.GetActiveImportCanonicalUrls();

        foreach (var sourceUrl in sourceUrls.Select(url => url.Trim()).Where(url => url.Length > 0))
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report("Reading link…");
            using var sourceTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            sourceTimeout.CancelAfter(TimeSpan.FromSeconds(45));
            IReadOnlyList<YouTubePlaylistEntry> entries;
            string? readError;
            try
            {
                var result = await _downloader.GetPlaylistEntriesAsync(
                    sourceUrl,
                    sourceTimeout.Token);
                entries = result.Entries;
                readError = result.Error;
            }
            catch (OperationCanceledException) when (
                sourceTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                unavailableCount++;
                previewItems.Add(new ImportPreviewItem(sourceUrl, sourceUrl, "This link took too long to read", null, null,
                    ImportQueueStatus.Failed, "Timed out after 45 seconds"));
                continue;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                unavailableCount++;
                previewItems.Add(new ImportPreviewItem(sourceUrl, sourceUrl, "Reading this link was canceled", null, null,
                    ImportQueueStatus.Failed, "Canceled from Activity Center"));
                continue;
            }
            if (entries.Count == 0)
            {
                unavailableCount++;
                previewItems.Add(new ImportPreviewItem(sourceUrl, sourceUrl, "Could not read this link", null, null,
                    ImportQueueStatus.Failed, readError ?? "Unavailable or unsupported playlist"));
                continue;
            }
            foreach (var entry in entries)
            {
                if (!seen.Add(entry.CanonicalUrl))
                {
                    duplicateCount++;
                    previewItems.Add(new ImportPreviewItem(entry.SourceUrl, entry.CanonicalUrl, entry.Title, entry.DurationSeconds,
                        null, ImportQueueStatus.Skipped, "Duplicate in this import"));
                    continue;
                }
                if (MusicLibraryService.Current.TrackExistsByCanonicalUrl(entry.CanonicalUrl))
                {
                    existingCount++;
                    previewItems.Add(new ImportPreviewItem(entry.SourceUrl, entry.CanonicalUrl, entry.Title, entry.DurationSeconds,
                        null, ImportQueueStatus.Skipped, "Already in library"));
                    continue;
                }
                if (alreadyQueued.Contains(entry.CanonicalUrl))
                {
                    duplicateCount++;
                    previewItems.Add(new ImportPreviewItem(entry.SourceUrl, entry.CanonicalUrl, entry.Title, entry.DurationSeconds,
                        null, ImportQueueStatus.Skipped, "Already in the import queue"));
                    continue;
                }

                // Flat playlist output already contains the title and usually the duration. Avoiding a full
                // yt-dlp metadata request per candidate keeps large playlists responsive.
                // Exact audio size and metadata are fetched later, only for the item currently downloading.
                previewItems.Add(new ImportPreviewItem(entry.SourceUrl, entry.CanonicalUrl,
                    entry.Title, entry.DurationSeconds, null, ImportQueueStatus.Queued));
            }
        }

        var queued = previewItems.Where(item => item.Status == ImportQueueStatus.Queued).ToList();
        var sizes = queued.Where(item => item.EstimatedSizeBytes is not null).Select(item => item.EstimatedSizeBytes!.Value).ToList();
        var downloadTimes = queued.Select(item => MusicLibraryService.Current.EstimateDownloadDuration(item.DurationSeconds, item.EstimatedSizeBytes))
            .Where(time => time is not null).Select(time => time!.Value).ToList();
        var analysisTimes = queued.Select(item => MusicLibraryService.Current.EstimateAnalysisDuration(item.DurationSeconds, item.EstimatedSizeBytes))
            .Where(time => time is not null).Select(time => time!.Value).ToList();

        return new ImportPreview(previewItems, existingCount, duplicateCount, unavailableCount,
            sizes.Count == queued.Count && sizes.Count > 0 ? sizes.Sum() : null,
            downloadTimes.Count == queued.Count && downloadTimes.Count > 0 ? TimeSpan.FromTicks(downloadTimes.Sum(time => time.Ticks)) : null,
            analysisTimes.Count == queued.Count && analysisTimes.Count > 0 ? TimeSpan.FromTicks(analysisTimes.Sum(time => time.Ticks)) : null);
    }

    public void Queue(ImportPreview preview)
    {
        var queued = preview.Items.Where(item => item.Status == ImportQueueStatus.Queued).ToList();
        if (queued.Count == 0) return;
        MusicLibraryService.Current.CreateImportBatch(queued[0].SourceUrl, queued);
        EnsureWorker();
    }

    public void Queue(DownloadRequest request, string title)
    {
        var videoId = YouTubeUrlNormalizer.ExtractVideoId(request.RawUrl)
                      ?? throw new InvalidOperationException("Could not parse YouTube URL.");
        var canonicalUrl = YouTubeUrlNormalizer.GetCanonicalUrl(videoId);
        var item = new ImportPreviewItem(
            canonicalUrl,
            canonicalUrl,
            string.IsNullOrWhiteSpace(title) ? videoId : title.Trim(),
            null,
            null,
            ImportQueueStatus.Queued,
            IsOriginal: request.IsOriginal,
            ParentTrackId: request.ParentTrackId,
            EditTypes: request.EditTypes,
            Request: request);
        MusicLibraryService.Current.CreateImportBatch(canonicalUrl, [item], rejectDuplicates: true);
        EnsureWorker();
    }

    public void QueueVersion(MusicTrack original, TrackEditTypes type, string rawUrl)
    {
        var item = CreateVersionPreview(original, type, rawUrl);
        MusicLibraryService.Current.CreateImportBatch(item.SourceUrl, [item], rejectDuplicates: true);
        EnsureWorker();
    }

    internal static ImportPreviewItem CreateVersionPreview(MusicTrack original, TrackEditTypes type, string rawUrl)
    {
        if (!original.IsOriginal)
            throw new InvalidOperationException("Choose an original track before adding a version.");
        var allowedTypes = TrackVersions.Types.Aggregate(TrackEditTypes.None, (all, entry) => all | entry.Type);
        if (type == TrackEditTypes.None || (type & ~allowedTypes) != 0)
            throw new InvalidOperationException("Choose a version type first.");
        var source = YouTubeUrlNormalizer.ParseImportSource(rawUrl.Trim());
        if (source is not { Kind: YouTubeImportSourceKind.SingleTrack })
            throw new InvalidOperationException("Enter a YouTube link to a single track.");
        var label = TrackVersions.Label(original with { IsOriginal = false, EditTypes = type });
        return new ImportPreviewItem(source.Value.Url, source.Value.Url,
            $"{original.DisplayTitle} · {label}", null, null, ImportQueueStatus.Queued,
            IsOriginal: false, ParentTrackId: original.Id, EditTypes: type);
    }

    public ImportQueueSummary GetSummary() => MusicLibraryService.Current.GetImportQueueSummary();
    public IReadOnlyList<ImportQueueSource> GetSources() => MusicLibraryService.Current.GetImportQueueSources();
    public bool RemoveQueuedItem(int id) => MusicLibraryService.Current.RemoveQueuedImport(id);
    public ImportQueuePhase? GetActivePhase(int itemId)
    {
        lock (_phaseGate)
            return _activePhases.GetValueOrDefault(itemId);
    }

    private void EnsureWorker()
    {
        lock (_workerGate)
        {
            if (_workerTask is { IsCompleted: false }) return;
            _workerTask = Task.Run(ProcessQueueAsync);
        }
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            while (ClaimNextQueuedImport() is { } item)
                await ProcessQueueItemAsync(item);
        }
        finally
        {
            lock (_workerGate)
            {
                _workerTask = null;
                if (MusicLibraryService.Current.GetNextQueuedImport() is not null)
                    _workerTask = Task.Run(ProcessQueueAsync);
            }
        }
    }

    private ImportQueueItem? ClaimNextQueuedImport()
    {
        var item = MusicLibraryService.Current.ClaimNextQueuedImport();
        if (item is null)
            return null;

        UpdateActivePhase(item.Id, item.Status);
        RaiseItemUpdated(item);
        return item;
    }

    private async Task ProcessQueueItemAsync(ImportQueueItem item)
    {
        try
        {
            if (GetRecoverableTrack(item) is { } existingTrack)
            {
                CompleteImport(item, existingTrack, null);
                return;
            }

            var currentItem = item;
            var result = await MusicLibraryService.Current.ImportFromYouTubeAsync(item.CanonicalUrl,
                new InlineProgress<string>(message =>
                    Update(currentItem, ImportQueueStatus.Downloading, message)),
                trackId =>
                {
                    Update(currentItem, ImportQueueStatus.Downloading, "Downloaded; queued for analysis", trackId);
                    currentItem = currentItem with { TrackId = trackId };
                }, item.DownloadRequest);

            if (result.Success && result.Track is not null)
                CompleteImport(item, result.Track, result.Warning);
            else
                Fail(item, result.Error ?? "Import failed");
        }
        catch (Exception exception)
        {
            Fail(item, $"Import failed: {exception.Message}", exception);
        }
    }

    private static MusicTrack? GetRecoverableTrack(ImportQueueItem item)
    {
        if (item.TrackId is int existingTrackId
            && MusicLibraryService.Current.GetTrackById(existingTrackId) is { } existingTrack)
            return existingTrack;

        return MusicLibraryService.Current.GetTrackByCanonicalUrl(item.CanonicalUrl);
    }

    private void CompleteImport(ImportQueueItem item, MusicTrack track, string? warning)
    {
        MusicLibraryService.Current.CompleteImportQueueItem(item.Id, track.Id);
        ClearActivePhase(item.Id);
        WorkflowLog.Info("import", $"Completed queue item {item.Id} as track {track.Id}.");
        try
        {
            BackgroundAnalysisService.Current.EnqueueTrack(track.Id);
        }
        catch (Exception exception)
        {
            // The durable import is complete. Analysis is rediscovered at the next
            // startup, so a transient enqueue failure must not rewrite import state.
            WorkflowLog.Error("import", $"Could not enqueue analysis for completed track {track.Id}.", exception);
        }

        MusicTrack completedTrack;
        try
        {
            completedTrack = MusicLibraryService.Current.GetTrackById(track.Id) ?? track;
        }
        catch (Exception exception)
        {
            WorkflowLog.Error("import", $"Could not reload completed track {track.Id} for observers.", exception);
            completedTrack = track;
        }
        RaiseTrackImported(completedTrack, warning);
        RaiseItemUpdated(item with
        {
            Status = ImportQueueStatus.ReadyForReview,
            Detail = warning ?? "Ready for review",
            TrackId = track.Id
        });
    }

    private void Update(ImportQueueItem item, ImportQueueStatus status, string? detail, int? trackId = null)
    {
        UpdateActivePhase(item.Id, status);
        MusicLibraryService.Current.UpdateImportQueueItem(item.Id, status, detail, trackId);
        RaiseItemUpdated(item with { Status = status, Detail = detail, TrackId = trackId ?? item.TrackId });
    }

    private void Fail(ImportQueueItem item, string detail, Exception? exception = null)
    {
        WorkflowLog.Error("import", $"Queue item {item.Id} failed: {detail}", exception);
        try
        {
            Update(item, ImportQueueStatus.Failed, detail);
        }
        catch (InvalidOperationException transitionError)
        {
            // Completion may already have atomically removed the queue item. An
            // observer failure after that point must not resurrect the workflow.
            WorkflowLog.Error("import", $"Could not persist failure for queue item {item.Id}.", transitionError);
            ClearActivePhase(item.Id);
        }
    }

    private void RaiseItemUpdated(ImportQueueItem item)
    {
        if (ItemUpdated is null)
            return;
        foreach (Action<ImportQueueItem> handler in ItemUpdated.GetInvocationList())
        {
            try { handler(item); }
            catch (Exception exception) { WorkflowLog.Error("import", "ItemUpdated observer failed.", exception); }
        }
    }

    private void RaiseTrackImported(MusicTrack track, string? warning)
    {
        if (TrackImported is null)
            return;
        foreach (Action<MusicTrack, string?> handler in TrackImported.GetInvocationList())
        {
            try { handler(track, warning); }
            catch (Exception exception) { WorkflowLog.Error("import", "TrackImported observer failed.", exception); }
        }
    }

    private void UpdateActivePhase(int itemId, ImportQueueStatus status)
    {
        lock (_phaseGate)
        {
            if (status is not (ImportQueueStatus.Downloading or ImportQueueStatus.Analyzing))
            {
                _activePhases.Remove(itemId);
                return;
            }

            if (!_activePhases.TryGetValue(itemId, out var phase) || phase.Status != status)
                _activePhases[itemId] = new ImportQueuePhase(itemId, status, DateTime.UtcNow);
        }
    }

    private void ClearActivePhase(int itemId)
    {
        lock (_phaseGate)
            _activePhases.Remove(itemId);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
