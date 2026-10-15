using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Resona.Cloud.Server;
using Resona.Core;
using Resona.Models;
using Xunit;

namespace Resona.Cloud.Tests;

public sealed class CloudServerContractTests
{
    [Fact]
    public void Valid_snapshot_is_accepted()
    {
        var errors = CloudSnapshotValidator.Validate(Snapshot());

        Assert.Empty(errors);
    }

    [Fact]
    public void Mismatched_count_and_duplicate_video_ids_are_rejected()
    {
        var first = Track("abcdefghijk");
        var snapshot = Snapshot() with { TrackCount = 1, Tracks = [first, first] };

        var errors = CloudSnapshotValidator.Validate(snapshot);

        Assert.Contains("trackCount", errors.Keys);
        Assert.Contains("tracks[1].sourceVideoId", errors.Keys);
    }

    [Fact]
    public void Device_credentials_are_parsed_and_key_is_hashed()
    {
        var userId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var key = RandomNumberGenerator.GetBytes(32);
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Resona-User-Id"] = userId.ToString("D");
        context.Request.Headers["X-Resona-Device-Id"] = deviceId.ToString("D");
        context.Request.Headers.Authorization = $"Device {Convert.ToBase64String(key)}";

        var success = DeviceCredentialsReader.TryRead(
            context.Request.Headers, out var credentials, out var error);

        Assert.True(success, error);
        Assert.Equal(userId, credentials.UserId);
        Assert.Equal(deviceId, credentials.DeviceId);
        Assert.True(CryptographicOperations.FixedTimeEquals(SHA256.HashData(key), credentials.DeviceKeyHash));
    }

    [Fact]
    public void Device_credentials_reject_short_or_malformed_keys()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Resona-User-Id"] = Guid.NewGuid().ToString("D");
        context.Request.Headers["X-Resona-Device-Id"] = Guid.NewGuid().ToString("D");
        context.Request.Headers.Authorization = "Device not-base64";

        Assert.False(DeviceCredentialsReader.TryRead(
            context.Request.Headers, out _, out _));
    }

    [Fact]
    public void Android_connection_code_round_trips_private_device_credentials()
    {
        var payload = new CloudConnectionPayload(
            CloudConnectionCode.CurrentSchemaVersion,
            "https://api.resona-music.de",
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

        var decoded = CloudConnectionCode.Decode(CloudConnectionCode.Encode(payload));

        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void Android_connection_code_rejects_untrusted_text()
    {
        Assert.Throws<InvalidDataException>(() => CloudConnectionCode.Decode("not-a-connection-code"));
    }

    [Theory]
    [InlineData(null, null, null, "", 0, PublicLibraryQuery.DefaultLimit)]
    [InlineData("  trance  ", 25, 10, "trance", 25, 10)]
    public void Public_library_query_normalizes_valid_parameters(
        string? search,
        int? offset,
        int? limit,
        string expectedSearch,
        int expectedOffset,
        int expectedLimit)
    {
        var success = PublicLibraryQuery.TryCreate(search, offset, limit, out var query, out var error);

        Assert.True(success, error);
        Assert.Equal(expectedSearch, query.Search);
        Assert.Equal(expectedOffset, query.Offset);
        Assert.Equal(expectedLimit, query.Limit);
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(0, 0)]
    [InlineData(0, PublicLibraryQuery.MaximumLimit + 1)]
    public void Public_library_query_rejects_invalid_pagination(int offset, int limit)
    {
        var success = PublicLibraryQuery.TryCreate(null, offset, limit, out _, out var error);

        Assert.False(success);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Public_library_query_rejects_overlong_search_text()
    {
        var success = PublicLibraryQuery.TryCreate(
            new string('x', PublicLibraryQuery.MaximumSearchLength + 1),
            null,
            null,
            out _,
            out var error);

        Assert.False(success);
        Assert.Contains("Search", error);
    }

    private static CloudLibrarySnapshot Snapshot() => new(
        1,
        new CloudPublicProfile(
            Guid.NewGuid().ToString("D"),
            "Listener",
            "Bio",
            null,
            "2026-08-13T00:00:00Z"),
        1,
        "2026-08-13T00:00:00Z",
        [Track("abcdefghijk")]);

    private static CloudPublicTrack Track(string videoId) => new(
        videoId,
        $"https://www.youtube.com/watch?v={videoId}",
        "Edited title",
        "Original title",
        "Channel",
        "https://www.youtube.com/channel/example",
        180,
        "2026-01-01T00:00:00Z",
        "https://i.ytimg.com/vi/example/hqdefault.jpg",
        "Amazing",
        "en",
        ["Shared"],
        ["Trance"],
        new CloudPublicTrackAnalysis(150, -7.2, 5.4),
        new Dictionary<string, double> { ["Intense"] = 0.82 },
        "2026-08-13T00:00:00Z");
}
