using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Resona.Models;

namespace Resona.Services;

public enum CloudSyncState { NotConfigured, Idle, Synchronizing, Succeeded, Failed }

public sealed record CloudSyncStatus(
    CloudSyncState State,
    string Message,
    int? TrackCount = null,
    string? CompletedAt = null,
    int? TotalAudioTracks = null,
    int? UploadedAudioTracks = null,
    int? PendingAudioTracks = null,
    int? FailedAudioTracks = null,
    int? PresetCount = null);

public sealed record CloudMediaSyncResult(
    int Total,
    int AlreadyUploaded,
    int Uploaded,
    int Failed)
{
    public int Available => AlreadyUploaded + Uploaded;
    public int Pending => Math.Max(0, Total - Available);
}

public sealed record CloudSyncCursor(
    int SchemaVersion,
    long LibraryRevision,
    long PresetsRevision,
    string PresetsHash,
    string SynchronizedAt);

public sealed class CloudLibrarySyncService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly object _requestGate = new();
    private CancellationTokenSource? _requestedSyncCts;
    private int _initialized;

    public static readonly CloudLibrarySyncService Current = new(new HttpClient
    {
        Timeout = TimeSpan.FromMinutes(30)
    });

    public CloudLibrarySyncService(HttpClient httpClient) => _httpClient = httpClient;

    public CloudSyncStatus Status { get; private set; } = new(
        CloudSyncState.NotConfigured, "Cloud server is not configured.");

    public event Action<CloudSyncStatus>? StatusChanged;

    public void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
            return;
        _ = SynchronizeAsync();
    }

    public void RequestSynchronization()
    {
        CancellationToken token;
        lock (_requestGate)
        {
            _requestedSyncCts?.Cancel();
            _requestedSyncCts?.Dispose();
            _requestedSyncCts = new CancellationTokenSource();
            token = _requestedSyncCts.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token);
                await SynchronizeAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
        }, token);
    }

    public async Task<CloudSyncStatus> SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        var configuredServerUrl = AppSettingsStore.Load().CloudServerUrl;
        if (string.IsNullOrWhiteSpace(configuredServerUrl))
            return SetStatus(new CloudSyncStatus(
                CloudSyncState.NotConfigured, "Cloud server is not configured."));
        if (!ServerUrlNormalizer.TryNormalize(configuredServerUrl, out var serverUrl)
            || !Uri.TryCreate(serverUrl, UriKind.Absolute, out var baseUri))
            return SetStatus(new CloudSyncStatus(
                CloudSyncState.Failed, "Cloud server address must be an absolute HTTP or HTTPS URL."));

        await _syncGate.WaitAsync(cancellationToken);
        try
        {
            SetProgressStatus("Preparing full public library snapshot…");
            var identity = CloudIdentityStore.Current.GetOrCreate();
            var snapshot = await Task.Run(
                () => CloudLibrarySnapshotBuilder.Build(MusicLibraryService.Current, identity),
                cancellationToken);
            // The public snapshot endpoint also performs first-device registration.
            await UploadSnapshotAsync(baseUri, identity, snapshot, cancellationToken);

            var remoteSnapshot = await GetDeviceSnapshotAsync(baseUri, identity, cancellationToken);
            var cursor = LoadCursor();
            var localPresets = FilterPresetStore.Load();
            var localPresetsHash = PresetsHash(localPresets);

            if (remoteSnapshot is not null)
            {
                if (cursor is not null
                    && remoteSnapshot.PresetsRevision > cursor.PresetsRevision
                    && !string.Equals(localPresetsHash, cursor.PresetsHash, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Presets changed on this device and on the server. Refresh the presets before saving them again.");

                if (cursor is not null
                    && remoteSnapshot.PresetsRevision > cursor.PresetsRevision
                    && string.Equals(localPresetsHash, cursor.PresetsHash, StringComparison.Ordinal))
                {
                    FilterPresetStore.Save(remoteSnapshot.FilterPresets.ToList());
                    localPresets = remoteSnapshot.FilterPresets.ToList();
                }

                var importedTracks = await Task.Run(
                    () => MusicLibraryService.Current.ApplyCloudDeviceSnapshot(remoteSnapshot),
                    cancellationToken);
                if (importedTracks > 0)
                {
                    snapshot = await Task.Run(
                        () => CloudLibrarySnapshotBuilder.Build(MusicLibraryService.Current, identity),
                        cancellationToken);
                    await UploadSnapshotAsync(baseUri, identity, snapshot, cancellationToken);
                }
            }

            SetProgressStatus("Uploading current device library metadata…");
            var deviceSnapshot = await Task.Run(
                () => CloudDeviceLibrarySnapshotBuilder.Build(MusicLibraryService.Current, identity),
                cancellationToken);
            if (remoteSnapshot is not null)
            {
                var revisions = remoteSnapshot.Tracks.ToDictionary(
                    track => track.TrackKey, track => track.Revision, StringComparer.Ordinal);
                deviceSnapshot = deviceSnapshot with
                {
                    LibraryRevision = remoteSnapshot.LibraryRevision,
                    PresetsRevision = remoteSnapshot.PresetsRevision,
                    Tracks = deviceSnapshot.Tracks
                        .Select(track => track with { Revision = revisions.GetValueOrDefault(track.TrackKey) })
                        .ToList()
                };
            }
            var acceptedSnapshot = await UploadDeviceSnapshotAsync(
                baseUri, identity, deviceSnapshot, cancellationToken);
            SaveCursor(new CloudSyncCursor(
                1,
                acceptedSnapshot.LibraryRevision,
                acceptedSnapshot.PresetsRevision,
                PresetsHash(acceptedSnapshot.FilterPresets),
                DateTime.UtcNow.ToString("O")));

            var mediaResult = await SynchronizeMediaAsync(baseUri, identity, cancellationToken);

            return SetStatus(new CloudSyncStatus(
                CloudSyncState.Succeeded,
                mediaResult.Failed == 0
                    ? $"Full sync complete · {deviceSnapshot.TrackCount} tracks · {deviceSnapshot.FilterPresets.Count} presets · {mediaResult.Uploaded} audio uploaded"
                    : $"Metadata and presets synchronized · {mediaResult.Failed} audio uploads failed",
                deviceSnapshot.TrackCount,
                DateTime.UtcNow.ToString("O"),
                mediaResult.Total,
                mediaResult.Available,
                mediaResult.Pending,
                mediaResult.Failed,
                deviceSnapshot.FilterPresets.Count));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            WorkflowLog.Error("cloud", "Full library snapshot synchronization failed.", exception);
            return SetStatus(new CloudSyncStatus(
                CloudSyncState.Failed,
                $"Cloud synchronization failed: {exception.Message}",
                Status.TrackCount,
                Status.CompletedAt,
                Status.TotalAudioTracks,
                Status.UploadedAudioTracks,
                Status.PendingAudioTracks,
                Status.FailedAudioTracks,
                Status.PresetCount));
        }
        finally
        {
            _syncGate.Release();
        }
    }

    public async Task UploadSnapshotAsync(
        Uri baseUri,
        CloudIdentity identity,
        CloudLibrarySnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        CloudLibrarySnapshotBuilder.Validate(snapshot, snapshot.TrackCount);
        var endpoint = new Uri(EnsureTrailingSlash(baseUri), "api/v1/library-snapshot");
        using var request = new HttpRequestMessage(HttpMethod.Put, endpoint)
        {
            Content = JsonContent.Create(snapshot, options: JsonOptions)
        };
        request.Headers.Add("X-Resona-User-Id", identity.UserId);
        request.Headers.Add("X-Resona-Device-Id", identity.DeviceId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Device", identity.DeviceKey);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
            throw new InvalidOperationException(
                "The public library snapshot exceeds the server upload limit. Increase the reverse proxy request-body limit.");
        response.EnsureSuccessStatusCode();
    }

    private async Task<CloudDeviceLibrarySnapshot> UploadDeviceSnapshotAsync(
        Uri baseUri,
        CloudIdentity identity,
        CloudDeviceLibrarySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var endpoint = new Uri(EnsureTrailingSlash(baseUri), "api/v1/device-library-snapshot");
        using var request = new HttpRequestMessage(HttpMethod.Put, endpoint)
        {
            Content = JsonContent.Create(snapshot, options: JsonOptions)
        };
        AddDeviceHeaders(request, identity);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
            throw new InvalidOperationException(
                "The server library changed during synchronization. Run synchronization again to load the current data.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CloudDeviceLibrarySnapshot>(JsonOptions, cancellationToken)
               ?? throw new InvalidDataException("Cloud server returned an empty library snapshot.");
    }

    private async Task<CloudDeviceLibrarySnapshot?> GetDeviceSnapshotAsync(
        Uri baseUri,
        CloudIdentity identity,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(EnsureTrailingSlash(baseUri), "api/v1/device-library-snapshot"));
        AddDeviceHeaders(request, identity);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CloudDeviceLibrarySnapshot>(
            JsonOptions, cancellationToken);
    }

    private async Task<CloudMediaSyncResult> SynchronizeMediaAsync(
        Uri baseUri,
        CloudIdentity identity,
        CancellationToken cancellationToken)
    {
        SetProgressStatus("Checking cloud audio inventory…");
        var inventory = await GetMediaInventoryAsync(baseUri, identity, cancellationToken);
        var serverFiles = inventory.Files.ToDictionary(file => file.TrackKey, StringComparer.Ordinal);
        var localFiles = new List<(string TrackKey, string FileName, string Path, long Size)>();
        var total = 0;
        var alreadyUploaded = 0;
        foreach (var track in MusicLibraryService.Current.GetTracks())
        {
            var trackKey = string.IsNullOrWhiteSpace(track.SourceVideoId)
                ? YouTubeUrlNormalizer.ExtractVideoId(track.CanonicalUrl)
                : track.SourceVideoId.Trim();
            if (string.IsNullOrWhiteSpace(trackKey))
                continue;
            var path = Path.Combine(Values.TracksDirectory, track.FileName);
            if (!File.Exists(path))
                continue;
            total++;
            var size = new FileInfo(path).Length;
            if (serverFiles.TryGetValue(trackKey, out var remote) && remote.FileSizeBytes == size)
            {
                alreadyUploaded++;
                continue;
            }
            localFiles.Add((trackKey, track.FileName, path, size));
        }

        var uploaded = 0;
        var failed = 0;
        SetStatus(new CloudSyncStatus(
            CloudSyncState.Synchronizing,
            localFiles.Count == 0 ? "All local audio is already in the cloud." : "Audio upload queue prepared.",
            TotalAudioTracks: total,
            UploadedAudioTracks: alreadyUploaded,
            PendingAudioTracks: localFiles.Count,
            FailedAudioTracks: 0));
        for (var index = 0; index < localFiles.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = localFiles[index];
            SetStatus(new CloudSyncStatus(
                CloudSyncState.Synchronizing,
                $"Uploading audio {index + 1} of {localFiles.Count} · {file.FileName}",
                TotalAudioTracks: total,
                UploadedAudioTracks: alreadyUploaded + uploaded,
                PendingAudioTracks: localFiles.Count - uploaded,
                FailedAudioTracks: failed));
            try
            {
                await UploadMediaAsync(baseUri, identity, file.TrackKey, file.FileName, file.Path, file.Size, cancellationToken);
                uploaded++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failed++;
                WorkflowLog.Error("cloud-media", $"Audio upload failed for {file.TrackKey}.", exception);
            }
        }

        return new CloudMediaSyncResult(total, alreadyUploaded, uploaded, failed);
    }

    private async Task<CloudMediaInventory> GetMediaInventoryAsync(
        Uri baseUri,
        CloudIdentity identity,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(EnsureTrailingSlash(baseUri), "api/v1/library-media"));
        AddDeviceHeaders(request, identity);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CloudMediaInventory>(JsonOptions, cancellationToken)
               ?? new CloudMediaInventory([]);
    }

    private async Task UploadMediaAsync(
        Uri baseUri,
        CloudIdentity identity,
        string trackKey,
        string fileName,
        string path,
        long size,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            new Uri(EnsureTrailingSlash(baseUri), $"api/v1/library-media/{Uri.EscapeDataString(trackKey)}"));
        AddDeviceHeaders(request, identity);
        request.Headers.Add("X-Resona-File-Name", fileName);
        request.Content = new StreamContent(stream, 128 * 1024);
        request.Content.Headers.ContentLength = size;
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static void AddDeviceHeaders(HttpRequestMessage request, CloudIdentity identity)
    {
        request.Headers.Add("X-Resona-User-Id", identity.UserId);
        request.Headers.Add("X-Resona-Device-Id", identity.DeviceId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Device", identity.DeviceKey);
    }

    private CloudSyncStatus SetStatus(CloudSyncStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
        return status;
    }

    private void SetProgressStatus(string message) => SetStatus(new CloudSyncStatus(
        CloudSyncState.Synchronizing,
        message,
        Status.TrackCount,
        Status.CompletedAt,
        Status.TotalAudioTracks,
        Status.UploadedAudioTracks,
        Status.PendingAudioTracks,
        Status.FailedAudioTracks,
        Status.PresetCount));

    private static Uri EnsureTrailingSlash(Uri uri) =>
        uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");

    private static string PresetsHash(IReadOnlyList<Resona.Core.PortableFilterPreset> presets) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(presets, JsonOptions))));

    private static CloudSyncCursor? LoadCursor()
    {
        try
        {
            return File.Exists(Values.CloudSyncStatePath)
                ? JsonSerializer.Deserialize<CloudSyncCursor>(
                    File.ReadAllText(Values.CloudSyncStatePath), JsonOptions)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveCursor(CloudSyncCursor cursor)
    {
        Directory.CreateDirectory(Values.LocalDirectory);
        var temporaryPath = Values.CloudSyncStatePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(cursor, JsonOptions));
        File.Move(temporaryPath, Values.CloudSyncStatePath, overwrite: true);
    }
}
