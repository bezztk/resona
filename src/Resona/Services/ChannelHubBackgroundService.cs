using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Resona.Models;

namespace Resona.Services;

public sealed record ChannelHubWorkStatus(
    bool IsActive,
    string OverallText,
    string CurrentText,
    int Current,
    int Total)
{
    public double Progress => Total <= 0 ? 0 : Math.Clamp((double)(Current - 1) / Total, 0, 1);
    public static ChannelHubWorkStatus Idle { get; } = new(false, string.Empty, string.Empty, 0, 0);
}

/// <summary>
/// Owns Channel Hub cache refreshes and slow remote enrichment. Public methods
/// only enqueue work; database, yt-dlp and image downloads never run on the UI
/// thread.
/// </summary>
public sealed class ChannelHubBackgroundService
{
    public static ChannelHubBackgroundService Current { get; } = new();

    private readonly object _gate = new();
    private readonly SemaphoreSlim _remoteGate = new(1, 1);
    private readonly Queue<ChannelHubItem> _enrichmentQueue = new();
    private readonly HashSet<int> _queuedEnrichments = [];
    private readonly HashSet<int> _attemptedEnrichments = [];
    private Task? _cacheWorker;
    private Task? _enrichmentWorker;
    private Task? _followedRefreshWorker;
    private int _refreshRequested;
    private int _activeRemoteBatches;
    private int _enrichmentTotal;
    private int _enrichmentCompleted;
    private bool _initialized;
    private bool _startupFollowedRefreshQueued;
    private IReadOnlyList<ChannelHubItem> _snapshot = Array.Empty<ChannelHubItem>();
    private ChannelHubWorkStatus _status = ChannelHubWorkStatus.Idle;

    public event Action<IReadOnlyList<ChannelHubItem>>? SnapshotChanged;
    public event Action<ChannelHubWorkStatus>? StatusChanged;

    public IReadOnlyList<ChannelHubItem> Snapshot
    {
        get
        {
            lock (_gate)
                return _snapshot;
        }
    }

    public ChannelHubWorkStatus Status
    {
        get
        {
            lock (_gate)
                return _status;
        }
    }

    private ChannelHubBackgroundService() { }

    public void Initialize()
    {
        lock (_gate)
        {
            if (_initialized)
                return;
            _initialized = true;
        }

        RequestRefresh();
    }

    public void RequestRefresh()
    {
        Interlocked.Exchange(ref _refreshRequested, 1);
        lock (_gate)
        {
            if (_cacheWorker is { IsCompleted: false })
                return;
            _cacheWorker = Task.Run(RefreshCacheLoop);
        }
    }

    public void RequestEnrichment(ChannelHubItem channel)
    {
        if (string.IsNullOrWhiteSpace(channel.SourceUrl))
            return;

        lock (_gate)
        {
            if (_attemptedEnrichments.Contains(channel.Id) || !_queuedEnrichments.Add(channel.Id))
                return;
            _enrichmentQueue.Enqueue(channel);
            _enrichmentTotal++;
            if (_enrichmentWorker is { IsCompleted: false })
                return;
            _enrichmentWorker = Task.Run(ProcessEnrichmentQueueAsync);
        }
    }

    public void RequestFollowedChannelRefresh()
    {
        lock (_gate)
        {
            if (_followedRefreshWorker is { IsCompleted: false })
                return;
            _followedRefreshWorker = Task.Run(RefreshFollowedChannelsAsync);
        }
    }

    private void RefreshCacheLoop()
    {
        while (Interlocked.Exchange(ref _refreshRequested, 0) != 0)
        {
            try
            {
                var snapshot = MusicLibraryService.Current.GetChannelHubItems();
                lock (_gate)
                    _snapshot = snapshot;
                SnapshotChanged?.Invoke(snapshot);
                QueueMissingChannelData(snapshot);
                lock (_gate)
                {
                    if (!_startupFollowedRefreshQueued)
                    {
                        _startupFollowedRefreshQueued = true;
                        RequestFollowedChannelRefresh();
                    }
                }
            }
            catch
            {
                // A later request retries. Channel Hub cache failures must never
                // terminate the application or block its UI.
            }
        }
    }

    private void QueueMissingChannelData(IReadOnlyList<ChannelHubItem> channels)
    {
        lock (_gate)
        {
            foreach (var channel in channels.Where(NeedsRemoteEnrichment))
            {
                if (string.IsNullOrWhiteSpace(channel.SourceUrl)
                    || _attemptedEnrichments.Contains(channel.Id)
                    || !_queuedEnrichments.Add(channel.Id))
                    continue;
                _enrichmentQueue.Enqueue(channel);
                _enrichmentTotal++;
            }

            if (_enrichmentQueue.Count > 0 && _enrichmentWorker is not { IsCompleted: false })
                _enrichmentWorker = Task.Run(ProcessEnrichmentQueueAsync);
        }
    }

    private static bool NeedsRemoteEnrichment(ChannelHubItem channel) =>
        channel.BasicMetadataCheckedAt is null;

    private async Task ProcessEnrichmentQueueAsync()
    {
        Interlocked.Increment(ref _activeRemoteBatches);
        try
        {
            while (true)
            {
                ChannelHubItem channel;
                int current;
                int total;
                lock (_gate)
                {
                    if (_enrichmentQueue.Count == 0)
                    {
                        _enrichmentTotal = 0;
                        _enrichmentCompleted = 0;
                        return;
                    }
                    channel = _enrichmentQueue.Dequeue();
                    _queuedEnrichments.Remove(channel.Id);
                    _attemptedEnrichments.Add(channel.Id);
                    current = _enrichmentCompleted + 1;
                    total = _enrichmentTotal;
                }

                try
                {
                    await _remoteGate.WaitAsync();
                    try
                    {
                        PublishStatus(new ChannelHubWorkStatus(
                            true,
                            $"Loading channel data · {current} of {total}",
                            $"Reading {channel.Name}",
                            current,
                            total));
                        var progress = new CallbackProgress(message => PublishStatus(new ChannelHubWorkStatus(
                            true,
                            $"Loading channel data · {current} of {total}",
                            $"{message.TrimEnd('…', '.')} · {channel.Name}",
                            current,
                            total)));
                        await MusicLibraryService.Current.AddOrRefreshChannelAsync(
                            channel.SourceUrl,
                            progress);
                    }
                    finally
                    {
                        _remoteGate.Release();
                    }
                    RequestRefresh();
                }
                catch
                {
                    // Remote enrichment is best-effort and retried manually if needed.
                }
                finally
                {
                    try
                    {
                        MusicLibraryService.Current.MarkChannelBasicMetadataChecked(channel.Id);
                    }
                    catch
                    {
                        // A failed checkpoint is safe: the channel is retried next launch.
                    }
                    lock (_gate)
                        _enrichmentCompleted++;
                }
            }
        }
        finally
        {
            EndRemoteBatch();
        }
    }

    private async Task RefreshFollowedChannelsAsync()
    {
        Interlocked.Increment(ref _activeRemoteBatches);
        try
        {
            var followed = MusicLibraryService.Current.GetChannelSubscriptions()
                .Where(channel =>
                {
                    lock (_gate)
                        return !_queuedEnrichments.Contains(channel.Id)
                               && !_attemptedEnrichments.Contains(channel.Id);
                })
                .ToList();
            for (var index = 0; index < followed.Count; index++)
            {
                var channel = followed[index];
                await _remoteGate.WaitAsync();
                try
                {
                    var current = index + 1;
                    PublishStatus(new ChannelHubWorkStatus(
                        true,
                        $"Refreshing followed channels · {current} of {followed.Count}",
                        $"Reading uploads from {channel.Name}",
                        current,
                        followed.Count));
                    var progress = new CallbackProgress(message => PublishStatus(new ChannelHubWorkStatus(
                        true,
                        $"Refreshing followed channels · {current} of {followed.Count}",
                        $"{message.TrimEnd('…', '.')} · {channel.Name}",
                        current,
                        followed.Count)));
                    await MusicLibraryService.Current.RefreshChannelAsync(
                        channel,
                        progress);
                }
                catch
                {
                    // Continue with the remaining followed channels.
                }
                finally
                {
                    _remoteGate.Release();
                }
                RequestRefresh();
            }
        }
        catch
        {
            // Startup refresh is optional and must remain invisible to the UI.
        }
        finally
        {
            EndRemoteBatch();
        }
    }

    private void EndRemoteBatch()
    {
        if (Interlocked.Decrement(ref _activeRemoteBatches) == 0)
            PublishStatus(ChannelHubWorkStatus.Idle);
    }

    private void PublishStatus(ChannelHubWorkStatus status)
    {
        lock (_gate)
            _status = status;
        try
        {
            StatusChanged?.Invoke(status);
        }
        catch
        {
            // Progress presentation must never affect background work.
        }
    }

    private sealed class CallbackProgress(Action<string> callback) : IProgress<string>
    {
        public void Report(string value) => callback(value);
    }
}
