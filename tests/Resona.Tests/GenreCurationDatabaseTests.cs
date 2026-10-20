using Microsoft.Data.Sqlite;
using Resona.Services;
using System.Linq;

namespace Resona.Tests;

public sealed class GenreCurationDatabaseTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"resona-genre-curation-{Guid.NewGuid():N}");
    private readonly string _databasePath;
    private readonly MusicDatabase _database;

    public GenreCurationDatabaseTests()
    {
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "library.db");
        CreateLegacySchema();
        using (var connection = Open())
            MusicDatabase.EnsureGenreCurationSchema(connection);
        _database = new MusicDatabase(_databasePath);
    }

    [Fact]
    public void Preferred_status_is_migrated_with_false_default_and_persists()
    {
        var genre = _database.GetModelSubgenres().Single(item => item.Name == "Ambient");
        Assert.False(genre.IsPreferred);

        _database.SetModelSubgenrePreferred(genre.Id, true);

        Assert.True(_database.GetModelSubgenres().Single(item => item.Id == genre.Id).IsPreferred);
    }

    [Fact]
    public void Newly_created_subgenres_are_persisted_as_manual()
    {
        _database.AddModelSubgenre(1, "Handmade genre");

        var genre = _database.GetModelSubgenres().Single(item => item.Name == "Handmade genre");
        Assert.True(genre.IsManual);
    }

    [Fact]
    public void Hidden_status_is_migrated_and_hidden_genres_are_only_returned_on_request()
    {
        var genre = _database.GetModelSubgenres().Single(item => item.Name == "Ambient");
        Assert.False(genre.IsHidden);

        _database.SetModelSubgenreHidden(genre.Id, true);

        Assert.DoesNotContain(_database.GetModelSubgenres(), item => item.Id == genre.Id);
        Assert.True(_database.GetModelSubgenres(includeHidden: true).Single(item => item.Id == genre.Id).IsHidden);
        Assert.DoesNotContain(_database.GetGenres(), item => item.Id == genre.Id);
    }

    [Fact]
    public void Hiding_a_genre_preserves_its_assignment_but_removes_it_from_library_results()
    {
        var genreId = GenreId("Ambient");
        var trackId = InsertTrack("Hidden assignment", "hidden-assignment.mp3", RatingId("Good"));
        _database.SetTrackModelGenreEnabled(trackId, genreId, true);

        _database.SetModelSubgenreHidden(genreId, true);

        Assert.DoesNotContain(genreId, _database.GetTrackGenreIds(trackId));
        Assert.DoesNotContain(genreId, _database.GetAllTrackGenreIds().GetValueOrDefault(trackId, []));
        Assert.Empty(_database.GetTrackModelGenres(trackId));
        Assert.Equal(1, Scalar<int>(
            "SELECT COUNT(*) FROM track_genres WHERE track_id = $trackId AND genre_id = $genreId",
            ("$trackId", trackId), ("$genreId", genreId)));

        _database.SetModelSubgenreHidden(genreId, false);
        Assert.Contains(genreId, _database.GetTrackGenreIds(trackId));
    }

    [Fact]
    public void Reference_limit_is_enforced_and_disabling_assignment_clears_reference()
    {
        var genreId = GenreId("Ambient");
        var trackIds = Enumerable.Range(1, 6)
            .Select(index => InsertTrack($"Track {index}", $"track-{index}.mp3", ratingId: null))
            .ToList();
        foreach (var trackId in trackIds)
            _database.SetTrackModelGenreEnabled(trackId, genreId, true);

        foreach (var trackId in trackIds.Take(5))
            _database.SetTrackGenreReference(trackId, genreId, true);

        Assert.Throws<InvalidOperationException>(new Action(() =>
            _database.SetTrackGenreReference(trackIds[5], genreId, true)));
        Assert.Equal(5, _database.GetGenreReferenceTracks(genreId).Count);

        _database.SetTrackModelGenreEnabled(trackIds[0], genreId, false);
        _database.SetTrackGenreReference(trackIds[5], genreId, true);

        var references = _database.GetGenreReferenceTracks(genreId);
        Assert.Equal(5, references.Count);
        Assert.DoesNotContain(references, item => item.TrackId == trackIds[0]);
        Assert.Contains(references, item => item.TrackId == trackIds[5]);
    }

    [Fact]
    public void Track_counts_and_genre_cleanup_follow_assignments_preferences_and_favorites()
    {
        var ambientId = GenreId("Ambient");
        var technoId = GenreId("Techno");
        _database.SetModelSubgenrePreferred(technoId, true);

        var ordinary = InsertTrack("Ordinary", "ordinary.mp3", RatingId("Good"));
        var preferredOnly = InsertTrack("Preferred", "preferred.mp3", RatingId("Good"));
        var favorite = InsertTrack("Favorite", "favorite.mp3", RatingId("Timeless"));
        _database.SetTrackModelGenreEnabled(ordinary, ambientId, true);
        _database.SetTrackModelGenreEnabled(preferredOnly, technoId, true);
        _database.SetTrackModelGenreEnabled(favorite, ambientId, true);

        var counts = _database.GetModelSubgenreTrackCounts();
        Assert.Equal(2, counts[ambientId]);
        Assert.Equal(1, counts[technoId]);
        Assert.Equal(new[] { ordinary }, _database.GetGenreCleanupTrackIds());

        _database.SetModelSubgenrePreferred(ambientId, true);
        Assert.Empty(_database.GetGenreCleanupTrackIds());
    }

    private void CreateLegacySchema()
    {
        Execute(@"
            CREATE TABLE ratings (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE,
                sort_order INTEGER NOT NULL UNIQUE
            );
            INSERT INTO ratings (name, sort_order) VALUES ('Good', 1), ('Timeless', 2);

            CREATE TABLE channels (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL
            );

            CREATE TABLE model_genres (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE
            );
            INSERT INTO model_genres (name) VALUES ('Electronic');

            CREATE TABLE model_subgenres (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                model_genre_id INTEGER NOT NULL REFERENCES model_genres(id),
                name TEXT NOT NULL,
                description TEXT NULL,
                classification_hint TEXT NULL,
                bpm_min INTEGER NULL,
                bpm_max INTEGER NULL,
                UNIQUE (model_genre_id, name)
            );
            INSERT INTO model_subgenres (model_genre_id, name) VALUES (1, 'Ambient'), (1, 'Techno');

            CREATE TABLE tracks (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                rating_id INTEGER NULL REFERENCES ratings(id),
                channel_id INTEGER NULL REFERENCES channels(id),
                title TEXT NOT NULL,
                artist TEXT NULL,
                file_name TEXT NOT NULL UNIQUE,
                library_state TEXT NOT NULL DEFAULT 'Active',
                updated_at TEXT NOT NULL
            );
            CREATE TABLE track_genres (
                track_id INTEGER NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
                genre_id INTEGER NOT NULL REFERENCES model_subgenres(id),
                assigned_at TEXT NOT NULL,
                is_enabled INTEGER NOT NULL DEFAULT 1,
                is_manual INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (track_id, genre_id)
            );");
    }

    private int InsertTrack(string title, string fileName, int? ratingId)
    {
        Execute(@"
            INSERT INTO tracks (rating_id, title, file_name, updated_at)
            VALUES ($ratingId, $title, $fileName, $updatedAt)",
            ("$ratingId", ratingId), ("$title", title), ("$fileName", fileName),
            ("$updatedAt", DateTime.UtcNow.ToString("O")));
        return Scalar<int>("SELECT MAX(id) FROM tracks");
    }

    private int GenreId(string name) => Scalar<int>(
        "SELECT id FROM model_subgenres WHERE name = $name", ("$name", name));

    private int RatingId(string name) => Scalar<int>(
        "SELECT id FROM ratings WHERE name = $name", ("$name", name));

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON";
        command.ExecuteNonQuery();
        return connection;
    }

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch { }
    }
}
