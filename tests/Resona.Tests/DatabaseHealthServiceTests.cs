using Microsoft.Data.Sqlite;
using Resona.Services;
using System.Linq;

namespace Resona.Tests;

public sealed class DatabaseHealthServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"resona-health-{Guid.NewGuid():N}");
    private string DatabasePath => Path.Combine(_root, "library.db");
    private string TracksDirectory => Path.Combine(_root, "tracks");

    public DatabaseHealthServiceTests()
    {
        Directory.CreateDirectory(TracksDirectory);
        CreateSchema();
    }

    [Fact]
    public async Task Reports_healthy_library_without_mutating_it()
    {
        File.WriteAllBytes(Path.Combine(TracksDirectory, "healthy.m4a"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(TracksDirectory, "fresh-download.m4a"), [4, 5, 6]);
        Execute(@"
            INSERT INTO tracks
                (id, title, file_name, canonical_url, library_state, rating_id,
                 needs_reevaluation, analysis_disabled, channel_id)
            VALUES (1, 'Healthy track', 'healthy.m4a', NULL, 'Active', 4, 0, 0, NULL);");

        var before = File.ReadAllBytes(DatabasePath);
        var report = await new DatabaseHealthService(DatabasePath, TracksDirectory).CheckAsync();

        Assert.True(report.IsHealthy);
        Assert.Empty(report.Issues);
        Assert.Equal(1, report.TrackCount);
        Assert.Equal(1, report.ReferencedFileCount);
        Assert.Equal(2, report.AudioFileCount);
        Assert.Equal(before, File.ReadAllBytes(DatabasePath));
    }

    [Fact]
    public async Task Finds_relationship_workflow_and_file_problems()
    {
        File.WriteAllBytes(Path.Combine(TracksDirectory, "Same.mp3"), []);
        var unownedPath = Path.Combine(TracksDirectory, "old-unowned.mp3");
        File.WriteAllBytes(unownedPath, [1]);
        File.SetLastWriteTimeUtc(unownedPath, DateTime.UtcNow.AddMinutes(-20));

        Execute(@"
            PRAGMA foreign_keys = OFF;
            INSERT INTO channels (id, source_url) VALUES
                (1, 'https://youtube.com/@example'),
                (2, 'HTTPS://YOUTUBE.COM/@EXAMPLE');
            INSERT INTO tracks
                (id, title, file_name, canonical_url, library_state, rating_id,
                 needs_reevaluation, analysis_disabled, channel_id)
            VALUES
                (1, 'First', 'same.mp3', 'https://youtu.be/VIDEO', 'PendingRating', 3, 0, 0, 1),
                (2, 'Second', 'Same.MP3', 'HTTPS://YOUTU.BE/video', 'Active', 4, 0, 0, 2),
                (3, 'Missing', 'missing.m4a', NULL, 'Active', 5, 0, 0, 999);
            INSERT INTO import_queue_items (id, status) VALUES (1, 'UnknownQueueState');");

        var report = await new DatabaseHealthService(DatabasePath, TracksDirectory).CheckAsync();
        var codes = report.Issues.Select(issue => issue.Code).ToHashSet();

        Assert.False(report.IsHealthy);
        Assert.Contains("database.foreign_keys", codes);
        Assert.Contains("tracks.workflow", codes);
        Assert.Contains("tracks.duplicate_file", codes);
        Assert.Contains("tracks.duplicate_url", codes);
        Assert.Contains("channels.duplicate_url", codes);
        Assert.Contains("queue.status", codes);
        Assert.Contains("files.missing", codes);
        Assert.Contains("files.empty", codes);
        Assert.Contains("files.unowned", codes);
    }

    [Fact]
    public async Task Does_not_mark_files_unowned_when_track_table_is_unreadable()
    {
        Execute("DROP TABLE tracks;");
        var oldPath = Path.Combine(TracksDirectory, "old.mp3");
        File.WriteAllBytes(oldPath, [1]);
        File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddHours(-1));

        var report = await new DatabaseHealthService(DatabasePath, TracksDirectory).CheckAsync();

        Assert.Contains(report.Issues, issue => issue.Code == "database.tracks.read");
        Assert.DoesNotContain(report.Issues, issue => issue.Code == "files.unowned");
        Assert.Equal(1, report.AudioFileCount);
    }

    [Fact]
    public async Task Reports_missing_database_as_a_finding()
    {
        File.Delete(DatabasePath);

        var report = await new DatabaseHealthService(DatabasePath, TracksDirectory).CheckAsync();

        Assert.Contains(report.Issues, issue => issue.Code == "database.missing");
        Assert.Equal(1, report.ErrorCount);
    }

    private void CreateSchema()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            PRAGMA foreign_keys = OFF;
            CREATE TABLE channels (
                id INTEGER PRIMARY KEY,
                source_url TEXT NULL
            );
            CREATE TABLE tracks (
                id INTEGER PRIMARY KEY,
                title TEXT NOT NULL,
                file_name TEXT NOT NULL,
                canonical_url TEXT NULL,
                library_state TEXT NOT NULL,
                rating_id INTEGER NULL,
                needs_reevaluation INTEGER NOT NULL DEFAULT 0,
                analysis_disabled INTEGER NOT NULL DEFAULT 0,
                channel_id INTEGER NULL REFERENCES channels(id)
            );
            CREATE TABLE import_queue_items (
                id INTEGER PRIMARY KEY,
                status TEXT NOT NULL
            );";
        command.ExecuteNonQuery();
    }

    private void Execute(string sql)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
