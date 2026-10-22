using Microsoft.Data.Sqlite;
using Resona.Services;

namespace Resona.Tests;

public sealed class TrackArchiveDatabaseTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"resona-archive-{Guid.NewGuid():N}");
    private readonly string _databasePath;
    private readonly MusicDatabase _database;

    public TrackArchiveDatabaseTests()
    {
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "archive.db");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE channels (id INTEGER PRIMARY KEY, name TEXT, source_url TEXT);
            CREATE TABLE tracks (
                id INTEGER PRIMARY KEY, channel_id INTEGER NULL, canonical_url TEXT,
                title TEXT NOT NULL, file_name TEXT NOT NULL, rating_id INTEGER NULL,
                downloaded_at TEXT NOT NULL, duration_seconds INTEGER NULL,
                needs_reevaluation INTEGER NOT NULL DEFAULT 0, uploaded_at TEXT NULL,
                updated_at TEXT NOT NULL, analysis_disabled INTEGER NOT NULL DEFAULT 0,
                is_public INTEGER NOT NULL DEFAULT 1, library_state TEXT NOT NULL DEFAULT 'Active',
                source_video_id TEXT NULL, view_count INTEGER NULL, like_count INTEGER NULL,
                source_thumbnail_url TEXT NULL, source_metadata_updated_at TEXT NULL,
                language_code TEXT NULL, original_title TEXT NOT NULL, artist TEXT NULL,
                rating_band TEXT NULL, is_archived INTEGER NOT NULL DEFAULT 0);
            INSERT INTO tracks
                (id, canonical_url, title, file_name, rating_id, downloaded_at, updated_at, original_title)
            VALUES (1, 'https://example.test/track', 'Track', 'track.mp3', 3,
                    '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', 'Track');";
        command.ExecuteNonQuery();
        _database = new MusicDatabase(_databasePath);
    }

    [Fact]
    public void Archive_flag_round_trips_without_changing_track_workflow_state()
    {
        Assert.False(_database.GetTrackById(1)!.IsArchived);

        _database.SetTrackArchived(1, true);

        var archived = _database.GetTrackById(1)!;
        Assert.True(archived.IsArchived);
        Assert.Equal("Active", Scalar<string>("SELECT library_state FROM tracks WHERE id = 1"));
        Assert.Equal(3, Scalar<int>("SELECT rating_id FROM tracks WHERE id = 1"));

        _database.SetTrackArchived(1, false);
        Assert.False(_database.GetTrackById(1)!.IsArchived);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        return connection;
    }

    private T Scalar<T>(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); }
        catch { }
    }
}
