using Microsoft.Data.Sqlite;
using Resona.Models;
using Resona.Services;

namespace Resona.Tests;

public sealed class CollectionDatabaseTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"resona-collections-{Guid.NewGuid():N}");
    private readonly string _databasePath;
    private readonly MusicDatabase _database;

    public CollectionDatabaseTests()
    {
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "collections.db");
        CreateSchema();
        _database = new MusicDatabase(_databasePath);
    }

    [Fact]
    public void Tracks_are_added_once_and_keep_their_manual_order()
    {
        var first = InsertTrack("First", 60, [1, 2]);
        var second = InsertTrack("Second", 90, [3, 4]);
        var collection = _database.CreateCollection("Favorites");

        Assert.True(_database.AddTrackToCollection(collection.Id, first));
        Assert.True(_database.AddTrackToCollection(collection.Id, second));
        Assert.False(_database.AddTrackToCollection(collection.Id, first));
        Assert.Equal([first, second], _database.GetCollectionTrackIds(collection.Id));

        Assert.True(_database.MoveCollectionTrack(collection.Id, second, -1));
        Assert.Equal([second, first], _database.GetCollectionTrackIds(collection.Id));
        var updated = Assert.Single(_database.GetCollections());
        Assert.Equal(2, updated.TrackCount);
        Assert.Equal(150, updated.DurationSeconds);
    }

    [Fact]
    public void Removing_a_track_compacts_order_and_resets_a_removed_track_cover()
    {
        var first = InsertTrack("First", 60, [1, 2]);
        var second = InsertTrack("Second", 90, [3, 4]);
        var collection = _database.CreateCollection("Road trip");
        _database.AddTrackToCollection(collection.Id, first);
        _database.AddTrackToCollection(collection.Id, second);
        _database.SetCollectionCoverTrack(collection.Id, first);

        Assert.True(_database.RemoveTrackFromCollection(collection.Id, first));

        Assert.Equal([second], _database.GetCollectionTrackIds(collection.Id));
        Assert.Equal(0, Assert.Single(_database.GetCollectionTracks(collection.Id)).Position);
        var updated = Assert.Single(_database.GetCollections());
        Assert.Equal(CollectionCoverKind.Automatic, updated.CoverKind);
        Assert.Null(updated.CoverTrackId);
        Assert.Equal(new byte[] { 3, 4 }, _database.GetCollectionCover(collection.Id));
    }

    [Fact]
    public void Deleting_a_collection_never_deletes_its_tracks()
    {
        var track = InsertTrack("Keep me", 30, null);
        var collection = _database.CreateCollection("Temporary");
        _database.AddTrackToCollection(collection.Id, track);

        _database.DeleteCollection(collection.Id);

        Assert.Empty(_database.GetCollections());
        Assert.Equal(1L, Scalar<long>("SELECT COUNT(*) FROM tracks WHERE id = $id", ("$id", track)));
    }

    [Fact]
    public void Deleting_a_track_removes_its_membership()
    {
        var track = InsertTrack("Disposable", 30, null);
        var collection = _database.CreateCollection("Keep collection");
        _database.AddTrackToCollection(collection.Id, track);

        Execute("DELETE FROM tracks WHERE id = $id", ("$id", track));

        Assert.Empty(_database.GetCollectionTrackIds(collection.Id));
        Assert.Equal(0, Assert.Single(_database.GetCollections()).TrackCount);
    }

    [Fact]
    public void Custom_cover_is_returned_verbatim()
    {
        var collection = _database.CreateCollection("Artwork");
        var cover = new byte[] { 8, 6, 7, 5, 3, 0, 9 };

        _database.SetCollectionCustomCover(collection.Id, cover);

        Assert.Equal(cover, _database.GetCollectionCover(collection.Id));
        Assert.Equal(CollectionCoverKind.Custom, Assert.Single(_database.GetCollections()).CoverKind);
    }

    private void CreateSchema() => Execute(@"
        CREATE TABLE tracks (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            title TEXT NOT NULL,
            duration_seconds INTEGER NULL,
            thumbnail BLOB NULL,
            is_archived INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE collections (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            stable_id TEXT NOT NULL UNIQUE,
            name TEXT NOT NULL COLLATE NOCASE UNIQUE,
            cover_kind TEXT NOT NULL DEFAULT 'Automatic',
            cover_track_id INTEGER NULL REFERENCES tracks(id) ON DELETE SET NULL,
            custom_cover BLOB NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        CREATE TABLE collection_tracks (
            collection_id INTEGER NOT NULL REFERENCES collections(id) ON DELETE CASCADE,
            track_id INTEGER NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
            position INTEGER NOT NULL,
            added_at TEXT NOT NULL,
            PRIMARY KEY (collection_id, track_id)
        );");

    private int InsertTrack(string title, int durationSeconds, byte[]? thumbnail)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO tracks (title, duration_seconds, thumbnail)
            VALUES ($title, $duration, $thumbnail);
            SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$duration", durationSeconds);
        command.Parameters.AddWithValue("$thumbnail", (object?)thumbnail ?? DBNull.Value);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON";
        command.ExecuteNonQuery();
        return connection;
    }

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); }
        catch { }
    }
}
