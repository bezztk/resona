using System;
using System.Threading;
using System.Threading.Tasks;
using Resona.Models;

namespace Resona.Services;

public sealed record ChannelMetadataWorkStatus(
    bool IsActive,
    string OverallText,
    string CurrentText,
    int Current,
    int Total)
{
    public double Progress => Total <= 0 ? 0 : Math.Clamp((double)Current / Total, 0, 1);
    public static ChannelMetadataWorkStatus Idle { get; } = new(false, string.Empty, string.Empty, 0, 0);
}

public sealed class ChannelMetadataService
{
    private const int BackgroundBatchSize = 1;
    public static readonly ChannelMetadataService Current = new();

    private readonly object _workerGate = new();
    private Task? _workerTask;
    private int _initialized;
    private ChannelMetadataWorkStatus _status = ChannelMetadataWorkStatus.Idle;

    public event Action<int, int, MusicTrack?>? MetadataUpdated;
    public event Action? QueueChanged;
    public event Action<ChannelMetadataWorkStatus>? StatusChanged;

    public ChannelMetadataWorkStatus Status
    {
        get
        {
            lock (_workerGate)
                return _status;
        }
    }

    public void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                // Let the main window and library finish their first render
                // before touching the metadata queue.
                await Task.Delay(TimeSpan.FromSeconds(2));
                MusicLibraryService.Current.EnsureChannelMetadataQueueIndexes();
                MusicLibraryService.Current.RecoverChannelMetadataQueue();
                MusicLibraryService.Current.PrepareLibraryMetadataBackfill();
                RequestAllChannels();
            }
            catch (Exception exception)
            {
                // Metadata preparation is optional startup work. A later
                // channel refresh or explicit request starts it again.
                WorkflowLog.Error("channel-metadata", "Startup preparation failed.", exception);
            }
        });
    }

    public void RequestChannel(int channelId, int limit = 1)
    {
        MusicLibraryService.Current.QueueChannelVideoMetadata(channelId, limit);
        EnsureWorker();
        RaiseQueueChanged();
    }

    public int RequestChannelDetails(int channelId)
    {
        var queued = MusicLibraryService.Current.QueueAllChannelVideoMetadata(channelId);
        if (queued <= 0)
            return 0;

        EnsureWorker();
        RaiseQueueChanged();
        return queued;
    }

    public void RequestVideo(int videoId)
    {
        MusicLibraryService.Current.QueueSpecificChannelVideoMetadata(videoId);
        EnsureWorker();
        RaiseQueueChanged();
    }

    public void RequestAllChannels(int limit = BackgroundBatchSize)
    {
        MusicLibraryService.Current.QueueBackgroundChannelVideoMetadata(limit);
        EnsureWorker();
        RaiseQueueChanged();
    }

    public void NotifyQueueChanged()
    {
        EnsureWorker();
        RaiseQueueChanged();
    }

    private void EnsureWorker()
    {
        lock (_workerGate)
        {
            if (_workerTask is { IsCompleted: false })
                return;
            _workerTask = Task.Run(ProcessQueueAsync);
        }
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            var total = Math.Max(1, MusicLibraryService.Current.CountBackgroundChannelVideoMetadataWork());
            var current = 0;

            while (true)
            {
                var video = MusicLibraryService.Current.ClaimNextChannelVideoMetadata();
                if (video is null)
                {
                    var queued = MusicLibraryService.Current.QueueBackgroundChannelVideoMetadata(BackgroundBatchSize);
                    if (queued <= 0)
                        break;

                    continue;
                }

                current++;
                if (current > total)
                    total = current;
                var channelName = video.ChannelName?.Trim() ?? string.Empty;
                var title = video.Title.Trim();
                PublishStatus(new ChannelMetadataWorkStatus(
                    true,
                    video.TrackId is not null
                        ? $"Librarying track metadata · {current:N0} of {total:N0}"
                        : $"Loading channel metadata · {current:N0} of {total:N0}",
                    video.TrackId is not null
                        ? channelName.Length == 0 ? $"Librarying · {title}" : $"Librarying · {channelName} · {title}"
                        : channelName.Length == 0 ? title : $"{channelName} · {title}",
                    current,
                    total));
                RaiseQueueChanged();
                YouTubeTrackMetadata? metadata = null;
                string? error = null;
                try
                {
                    metadata = await MusicLibraryService.Current.GetChannelVideoMetadataAsync(video.CanonicalUrl);
                    if (metadata is null)
                        error = "YouTube metadata unavailable";
                }
                catch (Exception exception)
                {
                    error = exception.Message;
                }

                MusicLibraryService.Current.CompleteChannelVideoMetadata(video.Id, metadata, error);
                if (metadata is null)
                    WorkflowLog.Error("channel-metadata", $"Video {video.Id} metadata failed: {error}");
                else
                    WorkflowLog.Info("channel-metadata", $"Video {video.Id} metadata updated.");
                var updatedTrack = video.TrackId is int trackId
                    ? MusicLibraryService.Current.GetTrackById(trackId)
                    : null;
                RaiseMetadataUpdated(video.ChannelId, video.Id, updatedTrack);
                RaiseQueueChanged();
            }
        }
        finally
        {
            lock (_workerGate)
                _workerTask = null;
            PublishStatus(ChannelMetadataWorkStatus.Idle);
            if (MusicLibraryService.Current.HasQueuedChannelVideoMetadata())
                EnsureWorker();
            RaiseQueueChanged();
        }
    }

    private void PublishStatus(ChannelMetadataWorkStatus status)
    {
        lock (_workerGate)
            _status = status;
        if (StatusChanged is null)
            return;
        foreach (Action<ChannelMetadataWorkStatus> handler in StatusChanged.GetInvocationList())
        {
            try { handler(status); }
            catch (Exception exception) { WorkflowLog.Error("channel-metadata", "StatusChanged observer failed.", exception); }
        }
    }

    private void RaiseQueueChanged()
    {
        if (QueueChanged is null)
            return;
        foreach (Action handler in QueueChanged.GetInvocationList())
        {
            try { handler(); }
            catch (Exception exception) { WorkflowLog.Error("channel-metadata", "QueueChanged observer failed.", exception); }
        }
    }

    private void RaiseMetadataUpdated(int channelId, int videoId, MusicTrack? track)
    {
        if (MetadataUpdated is null)
            return;
        foreach (Action<int, int, MusicTrack?> handler in MetadataUpdated.GetInvocationList())
        {
            try { handler(channelId, videoId, track); }
            catch (Exception exception) { WorkflowLog.Error("channel-metadata", "MetadataUpdated observer failed.", exception); }
        }
    }
}
