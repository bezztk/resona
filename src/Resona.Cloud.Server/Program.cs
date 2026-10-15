using Microsoft.AspNetCore.Http.Features;
using Npgsql;
using Resona.Cloud.Server;
using Resona.Models;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = CloudMediaRepository.MaximumFileSizeBytes);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = CloudMediaRepository.MaximumFileSizeBytes);

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");
builder.Services.AddSingleton(_ => new NpgsqlDataSourceBuilder(connectionString).Build());
builder.Services.AddSingleton<CloudSnapshotRepository>();
builder.Services.AddSingleton<CloudPublicLibraryRepository>();
builder.Services.AddSingleton<CloudMediaRepository>();
builder.Services.AddSingleton<CloudDeviceLibraryRepository>();

var app = builder.Build();
await CloudSchema.InitializeAsync(app.Services.GetRequiredService<NpgsqlDataSource>());

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapPublicLibraryEndpoints();

app.MapPut("/api/v1/library-snapshot", async (
    HttpRequest request,
    CloudLibrarySnapshot snapshot,
    CloudSnapshotRepository repository,
    CancellationToken cancellationToken) =>
{
    var validationErrors = CloudSnapshotValidator.Validate(snapshot);
    if (validationErrors.Count > 0)
        return Results.ValidationProblem(validationErrors);

    if (!DeviceCredentialsReader.TryRead(request.Headers, out var credentials, out _))
        return Results.Unauthorized();
    if (!string.Equals(credentials.UserId.ToString("D"), snapshot.Profile.UserId, StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Authenticated user ID does not match snapshot profile." });

    var result = await repository.ReplaceSnapshotAsync(credentials, snapshot, cancellationToken);
    return result switch
    {
        SnapshotReplaceResult.Unauthorized => Results.Unauthorized(),
        SnapshotReplaceResult.DeviceConflict => Results.Conflict(new
        {
            error = "This device is not registered for the existing user. Device recovery is not implemented yet."
        }),
        SnapshotReplaceResult.EmptySnapshotRejected => Results.Conflict(new
        {
            error = "An empty snapshot cannot replace a previously populated library without explicit deletion support."
        }),
        _ => Results.Ok(new
        {
            userId = snapshot.Profile.UserId,
            trackCount = snapshot.TrackCount,
            synchronizedAt = DateTime.UtcNow
        })
    };
});

app.MapGet("/api/v1/library-media", async (
    CloudDeviceLibraryRepository deviceLibrary,
    CloudMediaRepository media,
    CancellationToken cancellationToken) =>
{
    var userId = await deviceLibrary.GetLibraryOwnerAsync(cancellationToken);
    if (userId is null)
        return Results.NotFound();

    return Results.Ok(await media.GetInventoryAsync(userId.Value, cancellationToken));
});

app.MapPut("/api/v1/library-media/{trackKey}", async (
    string trackKey,
    HttpRequest request,
    CloudDeviceLibraryRepository deviceLibrary,
    CloudMediaRepository media,
    CancellationToken cancellationToken) =>
{
    var userId = await deviceLibrary.GetLibraryOwnerAsync(cancellationToken);
    if (userId is null)
        return Results.NotFound();
    if (!CloudMediaRepository.IsValidTrackKey(trackKey))
        return Results.BadRequest(new { error = "Track key is invalid." });

    var fileName = request.Headers["X-Resona-File-Name"].ToString();
    if (string.IsNullOrWhiteSpace(fileName))
        return Results.BadRequest(new { error = "X-Resona-File-Name is required." });
    try
    {
        var stored = await media.StoreAsync(
            userId.Value,
            trackKey,
            fileName,
            request.ContentType ?? "application/octet-stream",
            request.Body,
            request.ContentLength,
            request.Headers["X-Resona-Sha256"].ToString(),
            cancellationToken);
        return Results.Ok(stored);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (InvalidDataException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});

app.MapGet("/api/v1/library-media/{trackKey}", async (
    string trackKey,
    CloudDeviceLibraryRepository deviceLibrary,
    CloudMediaRepository media,
    CancellationToken cancellationToken) =>
{
    var userId = await deviceLibrary.GetLibraryOwnerAsync(cancellationToken);
    if (userId is null)
        return Results.NotFound();
    if (!CloudMediaRepository.IsValidTrackKey(trackKey))
        return Results.BadRequest(new { error = "Track key is invalid." });

    var stored = await media.FindAsync(userId.Value, trackKey, cancellationToken);
    if (stored is null || !File.Exists(stored.StoragePath))
        return Results.NotFound();
    return Results.File(
        stored.StoragePath,
        stored.ContentType,
        stored.FileName,
        enableRangeProcessing: true);
});

app.MapPut("/api/v1/device-library-snapshot", async (
    CloudDeviceLibrarySnapshot snapshot,
    CloudDeviceLibraryRepository deviceLibrary,
    CancellationToken cancellationToken) =>
{
    var userId = await deviceLibrary.GetLibraryOwnerAsync(cancellationToken);
    if (userId is null)
        return Results.NotFound();
    if (snapshot.SchemaVersion != 1
        || snapshot.TrackCount != snapshot.Tracks.Count
        || snapshot.Tracks.Any(track => !CloudMediaRepository.IsValidTrackKey(track.TrackKey)))
        return Results.BadRequest(new { error = "Device library snapshot is invalid." });

    var result = await deviceLibrary.ReplaceAsync(
        userId.Value, snapshot with { UserId = userId.Value.ToString("D") }, cancellationToken);
    return result.Status == DeviceLibraryWriteStatus.Conflict
        ? Results.Conflict(result.Conflict)
        : Results.Ok(result.Snapshot);
});

app.MapGet("/api/v1/device-library-snapshot", async (
    CloudDeviceLibraryRepository deviceLibrary,
    CancellationToken cancellationToken) =>
{
    var userId = await deviceLibrary.GetLibraryOwnerAsync(cancellationToken);
    if (userId is null)
        return Results.NotFound();
    var snapshot = await deviceLibrary.GetCurrentAsync(userId.Value, cancellationToken);
    return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
});

app.MapPut("/api/v1/device-library/tracks/{trackKey}", async (
    string trackKey,
    CloudTrackUpdateRequest update,
    CloudDeviceLibraryRepository deviceLibrary,
    CancellationToken cancellationToken) =>
{
    var userId = await deviceLibrary.GetLibraryOwnerAsync(cancellationToken);
    if (userId is null)
        return Results.NotFound();
    if (!CloudMediaRepository.IsValidTrackKey(trackKey)
        || !string.Equals(trackKey, update.Track.TrackKey, StringComparison.Ordinal))
        return Results.BadRequest(new { error = "Track key is invalid or does not match the route." });
    if (update.ExpectedRevision <= 0)
        return Results.BadRequest(new { error = "A positive expectedRevision is required." });

    var result = await deviceLibrary.UpdateTrackAsync(
        userId.Value, trackKey, update, cancellationToken);
    return result.Status switch
    {
        DeviceLibraryWriteStatus.NotFound => Results.NotFound(),
        DeviceLibraryWriteStatus.Conflict => Results.Conflict(result.Conflict),
        _ => Results.Ok(result.Snapshot)
    };
});

app.MapPut("/api/v1/device-library/presets", async (
    CloudPresetsUpdateRequest update,
    CloudDeviceLibraryRepository deviceLibrary,
    CancellationToken cancellationToken) =>
{
    var userId = await deviceLibrary.GetLibraryOwnerAsync(cancellationToken);
    if (userId is null)
        return Results.NotFound();
    if (update.ExpectedRevision <= 0
        || update.Presets.Any(preset => string.IsNullOrWhiteSpace(preset.Name)))
        return Results.BadRequest(new { error = "A positive expectedRevision and named presets are required." });

    var result = await deviceLibrary.UpdatePresetsAsync(userId.Value, update, cancellationToken);
    return result.Status switch
    {
        DeviceLibraryWriteStatus.NotFound => Results.NotFound(),
        DeviceLibraryWriteStatus.Conflict => Results.Conflict(result.Conflict),
        _ => Results.Ok(result.Snapshot)
    };
});

app.Run();

public partial class Program;
