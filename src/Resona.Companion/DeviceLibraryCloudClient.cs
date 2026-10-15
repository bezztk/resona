using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Globalization;
using Resona.Core;
using Resona.Models;

namespace Resona.Companion;

public sealed record CompanionCloudConnection(string ServerUrl);

public sealed record MissingDeviceAudio(
    string TrackKey,
    string FileName,
    long FileSizeBytes,
    string Sha256);

public sealed record DeviceAudioStatus(
    int TotalTracks,
    int AvailableInCloud,
    int LocalTracks,
    int MissingTracks,
    long MissingBytes,
    int WaitingForDesktopUpload);

public sealed record CloudMetadataRefreshResult(
    CloudDeviceLibrarySnapshot Snapshot,
    bool LibraryUpdated);

public sealed class CloudRevisionConflictException(CloudRevisionConflict conflict)
    : InvalidOperationException(
        $"{conflict.Entity} changed on another device (expected revision {conflict.ExpectedRevision}, current revision {conflict.CurrentRevision}).")
{
    public CloudRevisionConflict Conflict { get; } = conflict;
}

public sealed class DeviceLibraryCloudClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly HttpClient _httpClient;

    public DeviceLibraryCloudClient(HttpClient? httpClient = null) =>
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

    public string ConnectionPath => Path.Combine(
        CompanionServices.LibraryStorage.LibraryDirectory,
        "cloud-connection.json");

    public string SnapshotPath => Path.Combine(
        CompanionServices.LibraryStorage.LibraryDirectory,
        "device-library.json");

    public CompanionCloudConnection? LoadConnection()
    {
        if (!File.Exists(ConnectionPath))
            return null;
        try
        {
            var connection = JsonSerializer.Deserialize<CompanionCloudConnection>(
                File.ReadAllText(ConnectionPath), JsonOptions);
            if (connection is not null
                && Uri.TryCreate(connection.ServerUrl, UriKind.Absolute, out var uri)
                && uri.Host.Equals("api.resona.home.arpa", StringComparison.OrdinalIgnoreCase))
            {
                connection = new CompanionCloudConnection("https://192.168.178.102");
                SaveConnection(connection);
            }
            return connection;
        }
        catch
        {
            return null;
        }
    }

    public void SaveConnection(CompanionCloudConnection connection)
    {
        ValidateConnection(connection);
        Directory.CreateDirectory(CompanionServices.LibraryStorage.LibraryDirectory);
        var temporaryPath = ConnectionPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(connection, JsonOptions));
        File.Move(temporaryPath, ConnectionPath, overwrite: true);
    }

    public CompanionCloudConnection SaveConnectionCode(string code)
    {
        var payload = CloudConnectionCode.Decode(code);
        var connection = new CompanionCloudConnection(payload.ServerUrl);
        SaveConnection(connection);
        return connection;
    }

    public CompanionCloudConnection SaveServerUrl(string serverUrl)
    {
        var connection = new CompanionCloudConnection(serverUrl.Trim().TrimEnd('/'));
        SaveConnection(connection);
        return connection;
    }

    public CloudDeviceLibrarySnapshot? LoadCachedSnapshot()
    {
        if (!File.Exists(SnapshotPath))
            return null;
        try
        {
            return JsonSerializer.Deserialize<CloudDeviceLibrarySnapshot>(
                File.ReadAllText(SnapshotPath), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public async Task<CloudMetadataRefreshResult> RefreshMetadataAsync(
        CancellationToken cancellationToken = default)
    {
        var connection = LoadConnection()
                         ?? throw new InvalidOperationException("Cloud connection is not configured.");
        ValidateConnection(connection);
        using var request = CreateRequest(connection, HttpMethod.Get, "api/v1/device-library-snapshot");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var snapshot = await response.Content.ReadFromJsonAsync<CloudDeviceLibrarySnapshot>(
                           JsonOptions, cancellationToken)
                       ?? throw new InvalidDataException("Cloud device-library snapshot is empty.");

        var cachedSnapshot = LoadCachedSnapshot();
        var portableLibraryPath = Path.Combine(
            CompanionServices.LibraryStorage.LibraryDirectory,
            PortableLibraryStore.FileName);
        var libraryUpdated = cachedSnapshot is null
                             || !string.Equals(snapshot.UserId, cachedSnapshot.UserId, StringComparison.OrdinalIgnoreCase)
                             || HasNewerRevision(snapshot, cachedSnapshot)
                             || !File.Exists(portableLibraryPath);
        if (!libraryUpdated)
            return new CloudMetadataRefreshResult(
                MergeAudioAvailability(cachedSnapshot!, snapshot),
                false);

        await SaveSnapshotAsync(snapshot, cancellationToken);
        return new CloudMetadataRefreshResult(snapshot, true);
    }

    public async Task<CloudDeviceLibrarySnapshot> UpdateTrackAsync(
        CloudDeviceTrack track,
        CancellationToken cancellationToken = default)
    {
        var connection = LoadConnection()
                         ?? throw new InvalidOperationException("Cloud connection is not configured.");
        using var request = CreateRequest(
            connection,
            HttpMethod.Put,
            $"api/v1/device-library/tracks/{Uri.EscapeDataString(track.TrackKey)}");
        request.Content = JsonContent.Create(
            new CloudTrackUpdateRequest(track.Revision, track), options: JsonOptions);
        var snapshot = await SendMutationAsync(request, cancellationToken);
        await SaveSnapshotAsync(snapshot, cancellationToken);
        return snapshot;
    }

    public async Task<CloudDeviceLibrarySnapshot> UpdatePresetsAsync(
        IReadOnlyList<PortableFilterPreset> presets,
        CancellationToken cancellationToken = default)
    {
        var connection = LoadConnection()
                         ?? throw new InvalidOperationException("Cloud connection is not configured.");
        var current = LoadCachedSnapshot()
                      ?? throw new InvalidOperationException("Synchronize metadata before editing presets.");
        using var request = CreateRequest(connection, HttpMethod.Put, "api/v1/device-library/presets");
        request.Content = JsonContent.Create(
            new CloudPresetsUpdateRequest(current.PresetsRevision, presets), options: JsonOptions);
        var snapshot = await SendMutationAsync(request, cancellationToken);
        await SaveSnapshotAsync(snapshot, cancellationToken);
        return snapshot;
    }

    public static bool IsNewer(string candidateTimestamp, string currentTimestamp)
    {
        const DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
        return DateTimeOffset.TryParse(candidateTimestamp, CultureInfo.InvariantCulture, styles, out var candidate)
               && (!DateTimeOffset.TryParse(currentTimestamp, CultureInfo.InvariantCulture, styles, out var current)
                   || candidate > current);
    }

    private static bool HasNewerRevision(
        CloudDeviceLibrarySnapshot candidate,
        CloudDeviceLibrarySnapshot current) =>
        candidate.LibraryRevision > 0 && current.LibraryRevision > 0
            ? candidate.LibraryRevision > current.LibraryRevision
            : IsNewer(candidate.GeneratedAt, current.GeneratedAt);

    private async Task<CloudDeviceLibrarySnapshot> SendMutationAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var conflict = await response.Content.ReadFromJsonAsync<CloudRevisionConflict>(
                JsonOptions, cancellationToken)
                ?? new CloudRevisionConflict("library", 0, 0, new { });
            throw new CloudRevisionConflictException(conflict);
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CloudDeviceLibrarySnapshot>(
                   JsonOptions, cancellationToken)
               ?? throw new InvalidDataException("Cloud server returned an empty library snapshot.");
    }

    private async Task SaveSnapshotAsync(
        CloudDeviceLibrarySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(CompanionServices.LibraryStorage.LibraryDirectory);
        await PortableLibraryStore.SaveAsync(
            CompanionServices.LibraryStorage.LibraryDirectory,
            ToPortableLibrary(snapshot));
        var temporaryPath = SnapshotPath + ".tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(snapshot, JsonOptions),
            cancellationToken);
        File.Move(temporaryPath, SnapshotPath, overwrite: true);
    }

    public IReadOnlyList<MissingDeviceAudio> FindMissingAudio(CloudDeviceLibrarySnapshot snapshot)
    {
        var tracksDirectory = Path.Combine(CompanionServices.LibraryStorage.LibraryDirectory, "tracks");
        return snapshot.Tracks
            .Where(track => track.AudioAvailable
                            && track.AudioFileSizeBytes is > 0
                            && !LocalAudioMatches(tracksDirectory, track))
            .Select(track => new MissingDeviceAudio(
                track.TrackKey,
                track.FileName,
                track.AudioFileSizeBytes!.Value,
                track.AudioSha256 ?? string.Empty))
            .ToList();
    }

    public DeviceAudioStatus GetAudioStatus(CloudDeviceLibrarySnapshot snapshot)
    {
        var tracksDirectory = Path.Combine(CompanionServices.LibraryStorage.LibraryDirectory, "tracks");
        var available = snapshot.Tracks.Count(track => track.AudioAvailable && track.AudioFileSizeBytes is > 0);
        var local = snapshot.Tracks.Count(track => LocalAudioMatches(tracksDirectory, track));
        var missing = FindMissingAudio(snapshot);
        return new DeviceAudioStatus(
            snapshot.TrackCount,
            available,
            local,
            missing.Count,
            missing.Sum(track => track.FileSizeBytes),
            snapshot.Tracks.Count(track => !track.AudioAvailable
                                           && !LocalAudioMatches(tracksDirectory, track)));
    }

    public async Task DownloadMissingAudioAsync(
        CloudDeviceLibrarySnapshot snapshot,
        IProgress<(int Completed, int Total, string FileName)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var connection = LoadConnection()
                         ?? throw new InvalidOperationException("Cloud connection is not configured.");
        ValidateConnection(connection);
        var missing = FindMissingAudio(snapshot);
        var tracksDirectory = Path.Combine(CompanionServices.LibraryStorage.LibraryDirectory, "tracks");
        Directory.CreateDirectory(tracksDirectory);

        for (var index = 0; index < missing.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = missing[index];
            progress?.Report((index, missing.Count, item.FileName));
            await DownloadOneAsync(connection, tracksDirectory, item, cancellationToken);
            progress?.Report((index + 1, missing.Count, item.FileName));
        }
    }

    public async Task DownloadTrackAudioAsync(
        CloudDeviceTrack track,
        CancellationToken cancellationToken = default)
    {
        if (!track.AudioAvailable || track.AudioFileSizeBytes is not > 0)
            throw new InvalidOperationException("This track is not available on the server yet.");
        var connection = LoadConnection()
                         ?? throw new InvalidOperationException("Cloud connection is not configured.");
        var tracksDirectory = Path.Combine(CompanionServices.LibraryStorage.LibraryDirectory, "tracks");
        Directory.CreateDirectory(tracksDirectory);
        await DownloadOneAsync(
            connection,
            tracksDirectory,
            new MissingDeviceAudio(
                track.TrackKey,
                track.FileName,
                track.AudioFileSizeBytes.Value,
                track.AudioSha256 ?? string.Empty),
            cancellationToken);
    }

    public void RemoveLocalTrackAudio(CloudDeviceTrack track)
    {
        var tracksDirectory = Path.Combine(CompanionServices.LibraryStorage.LibraryDirectory, "tracks");
        var path = Path.GetFullPath(Path.Combine(tracksDirectory, Path.GetFileName(track.FileName)));
        if (!path.StartsWith(Path.GetFullPath(tracksDirectory) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Resolved track path is outside the local library.");
        if (File.Exists(path))
            File.Delete(path);
        if (File.Exists(path + ".part"))
            File.Delete(path + ".part");
    }

    private async Task DownloadOneAsync(
        CompanionCloudConnection connection,
        string tracksDirectory,
        MissingDeviceAudio item,
        CancellationToken cancellationToken)
    {
        var safeName = Path.GetFileName(item.FileName);
        if (!string.Equals(safeName, item.FileName, StringComparison.Ordinal))
            throw new InvalidDataException("Cloud media file name is invalid.");
        var destinationPath = Path.Combine(tracksDirectory, safeName);
        var temporaryPath = destinationPath + ".part";

        var existingBytes = File.Exists(temporaryPath) ? new FileInfo(temporaryPath).Length : 0;
        if (existingBytes > item.FileSizeBytes)
        {
            File.Delete(temporaryPath);
            existingBytes = 0;
        }

        if (existingBytes < item.FileSizeBytes)
        {
            using var request = CreateRequest(
                connection,
                HttpMethod.Get,
                $"api/v1/library-media/{Uri.EscapeDataString(item.TrackKey)}");
            if (existingBytes > 0)
                request.Headers.Range = new RangeHeaderValue(existingBytes, null);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            var append = existingBytes > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var destination = new FileStream(
                temporaryPath,
                append ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(destination, cancellationToken);
            await destination.FlushAsync(cancellationToken);
        }

        var info = new FileInfo(temporaryPath);
        if (info.Length != item.FileSizeBytes)
            throw new InvalidDataException($"Downloaded size for {item.FileName} is invalid.");
        if (!string.IsNullOrWhiteSpace(item.Sha256))
        {
            await using var stream = File.OpenRead(temporaryPath);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            if (!string.Equals(hash, item.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(temporaryPath);
                throw new InvalidDataException($"Downloaded checksum for {item.FileName} is invalid.");
            }
        }
        File.Move(temporaryPath, destinationPath, overwrite: true);
    }

    private static bool LocalAudioMatches(string tracksDirectory, CloudDeviceTrack track)
    {
        var safeName = Path.GetFileName(track.FileName);
        if (!string.Equals(safeName, track.FileName, StringComparison.Ordinal))
            return false;
        var path = Path.Combine(tracksDirectory, safeName);
        return File.Exists(path)
               && (track.AudioFileSizeBytes is null || new FileInfo(path).Length == track.AudioFileSizeBytes);
    }

    private static CloudDeviceLibrarySnapshot MergeAudioAvailability(
        CloudDeviceLibrarySnapshot cached,
        CloudDeviceLibrarySnapshot remote)
    {
        var remoteTracks = remote.Tracks.ToDictionary(track => track.TrackKey, StringComparer.Ordinal);
        return cached with
        {
            Tracks = cached.Tracks.Select(track => remoteTracks.TryGetValue(track.TrackKey, out var remoteTrack)
                ? track with
                {
                    AudioAvailable = remoteTrack.AudioAvailable,
                    AudioFileSizeBytes = remoteTrack.AudioFileSizeBytes,
                    AudioSha256 = remoteTrack.AudioSha256
                }
                : track).ToList()
        };
    }

    private static HttpRequestMessage CreateRequest(
        CompanionCloudConnection connection,
        HttpMethod method,
        string relativePath)
    {
        var baseUri = new Uri(connection.ServerUrl.EndsWith('/')
            ? connection.ServerUrl
            : connection.ServerUrl + "/");
        return new HttpRequestMessage(method, new Uri(baseUri, relativePath));
    }

    private static void ValidateConnection(CompanionCloudConnection connection)
    {
        if (!Uri.TryCreate(connection.ServerUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            throw new InvalidDataException("Cloud connection settings are invalid.");
    }

    private static PortableMusicLibrary ToPortableLibrary(CloudDeviceLibrarySnapshot snapshot) => new(
        snapshot.Tracks.Select(track => new PortableTrack(
            track.Title,
            track.FileName,
            track.DurationSeconds,
            track.Rating ?? string.Empty,
            track.Genres.ToList(),
            track.Styles.ToList(),
            NeedsReview: track.NeedsReview,
            Tags: track.Tags.ToList(),
            PlayCount: track.PlayCount,
            ListenedSeconds: track.ListenedSeconds,
            SkipCount: track.SkipCount,
            LastListenedAt: track.LastListenedAt,
            Thumbnail: track.Thumbnail,
            LanguageCode: track.LanguageCode,
            TrackKey: track.TrackKey,
            OriginalTitle: track.OriginalTitle,
            LibraryState: track.LibraryState,
            EmotionalCharacter: track.EmotionalCharacter.ToDictionary(item => item.Key, item => item.Value),
            IsOriginal: track.IsOriginal,
            ParentTrackKey: track.ParentTrackKey,
            EditTypes: track.EditTypes?.ToList())).ToList(),
        snapshot.FilterPresets.ToList(),
        ExportId: snapshot.GeneratedAt,
        ExportedAt: snapshot.GeneratedAt,
        MediaMode: "cloud",
        RatingDefinitions: snapshot.Ratings.ToList(),
        Collections: snapshot.Collections
            .Select(collection => new PortableCollection(
                collection.StableId,
                collection.Name,
                collection.TrackKeys.ToList()))
            .ToList());
}
