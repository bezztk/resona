using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using Resona.Models;
using Resona.Services;

namespace Resona.Tests;

public sealed class RatingAndLanguageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"resona-rating-language-{Guid.NewGuid():N}");
    private readonly string _databasePath;

    public RatingAndLanguageTests()
    {
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "library.db");
        CreateLegacySchema();
    }

    [Fact]
    public void Existing_favorite_rating_is_migrated_and_track_assignment_is_preserved()
    {
        var database = new MusicDatabase(_databasePath);
        var favoriteId = Scalar<int>("SELECT id FROM ratings WHERE name = 'Favorite'");
        var trackId = InsertTrack(favoriteId, "migration.mp3");

        using (var connection = Open())
            MusicDatabase.EnsureCurrentRatings(connection);

        var ratings = database.GetRatings().OrderBy(rating => rating.SortOrder).ToList();
        Assert.Equal(
            ["Avoid", "Okay", "Good", "Great", "Amazing", "Timeless"],
            ratings.Select(rating => rating.Name));
        Assert.Equal([1, 2, 3, 4, 5, 6], ratings.Select(rating => rating.SortOrder));
        Assert.Equal("Timeless", Scalar<string>(@"
            SELECT ratings.name
            FROM tracks JOIN ratings ON ratings.id = tracks.rating_id
            WHERE tracks.id = $trackId", ("$trackId", trackId)));
    }

    [Fact]
    public void Language_is_validated_persisted_and_can_be_cleared()
    {
        var database = new MusicDatabase(_databasePath);
        var trackId = InsertTrack(null, "language.mp3");

        database.SetTrackLanguage(trackId, "ja");
        Assert.Equal("ja", Scalar<string>("SELECT language_code FROM tracks WHERE id = $id", ("$id", trackId)));

        Assert.Throws<ArgumentException>(() => database.SetTrackLanguage(trackId, "not-a-language"));
        Assert.Equal("ja", Scalar<string>("SELECT language_code FROM tracks WHERE id = $id", ("$id", trackId)));

        database.SetTrackLanguage(trackId, null);
        Assert.Equal(0L, Scalar<long>("SELECT COUNT(language_code) FROM tracks WHERE id = $id", ("$id", trackId)));
    }

    [Fact]
    public void Rating_band_is_preserved_for_the_same_rating_and_cleared_by_every_rating_change_path()
    {
        var database = new MusicDatabase(_databasePath);
        var goodId = Scalar<int>("SELECT id FROM ratings WHERE name = 'Good'");
        var greatId = Scalar<int>("SELECT id FROM ratings WHERE name = 'Great'");
        var trackId = InsertTrack(goodId, "rating-band.mp3");

        database.SetTrackRatingBand(trackId, RatingBand.High);
        database.SetTrackRating(trackId, goodId);
        Assert.Equal("High", Scalar<string>("SELECT rating_band FROM tracks WHERE id = $id", ("$id", trackId)));

        database.UpdateTrack(trackId, "Track", null, null, [], greatId, [], true);
        Assert.Equal(0L, Scalar<long>("SELECT COUNT(rating_band) FROM tracks WHERE id = $id", ("$id", trackId)));

        database.SetTrackRatingBand(trackId, RatingBand.Low);
        database.SetTrackRating(trackId, greatId);
        Assert.Equal("Low", Scalar<string>("SELECT rating_band FROM tracks WHERE id = $id", ("$id", trackId)));

        database.SetTrackRating(trackId, goodId);
        Assert.Equal(0L, Scalar<long>("SELECT COUNT(rating_band) FROM tracks WHERE id = $id", ("$id", trackId)));
    }

    [Fact]
    public void Rating_band_cannot_be_stored_without_a_rating()
    {
        var database = new MusicDatabase(_databasePath);
        var trackId = InsertTrack(null, "unrated-band.mp3");

        database.SetTrackRatingBand(trackId, RatingBand.Low);

        Assert.Equal(0L, Scalar<long>("SELECT COUNT(rating_band) FROM tracks WHERE id = $id", ("$id", trackId)));
    }

    [Fact]
    public void Language_filter_matches_any_selected_language_inside_a_condition()
    {
        var tracks = new[]
        {
            Track(1, "English", "en"),
            Track(2, "Japanese", "ja"),
            Track(3, "German", "de"),
            Track(4, "Unknown", null)
        };
        var group = new FilterGroup(
            new HashSet<int>(),
            new HashSet<int>(),
            new HashSet<int>(),
            new HashSet<string>(["en", "ja"], StringComparer.OrdinalIgnoreCase),
            []);

        var result = TrackFilter.Apply(
            tracks,
            new Dictionary<int, List<int>>(),
            new Dictionary<int, List<int>>(),
            new Dictionary<int, List<int>>(),
            new Dictionary<int, Dictionary<string, double>>(),
            new HashSet<int>(),
            [group],
            null);

        Assert.Equal(["English", "Japanese"], result.Select(track => track.Title));
    }

    [Fact]
    public void Main_genre_filter_matches_any_subgenre_inside_the_main_genre()
    {
        var tracks = new[]
        {
            Track(1, "Techno track", "en"),
            Track(2, "House track", "en"),
            Track(3, "Rock track", "en")
        };
        var group = new FilterGroup(
            new HashSet<int>(),
            new HashSet<int>(),
            new HashSet<int>(),
            new HashSet<string>(),
            [],
            AnyGenreIds: new HashSet<int> { 10, 11 });

        var result = TrackFilter.Apply(
            tracks,
            new Dictionary<int, List<int>>
            {
                [1] = [10],
                [2] = [11],
                [3] = [20]
            },
            new Dictionary<int, List<int>>(),
            new Dictionary<int, List<int>>(),
            new Dictionary<int, Dictionary<string, double>>(),
            new HashSet<int>(),
            [group],
            null);

        Assert.Equal(["House track", "Techno track"], result.Select(track => track.Title));
    }

    [Fact]
    public void Search_matches_editable_and_original_titles()
    {
        var tracks = new[]
        {
            Track(1, "My clean title", "en") with { OriginalTitle = "Uploader title (Official Video)" },
            Track(2, "Another track", "en") with { OriginalTitle = "Unrelated source title" }
        };

        List<MusicTrack> Search(string term) => TrackFilter.Apply(
            tracks,
            new Dictionary<int, List<int>>(),
            new Dictionary<int, List<int>>(),
            new Dictionary<int, List<int>>(),
            new Dictionary<int, Dictionary<string, double>>(),
            new HashSet<int>(),
            [],
            term);

        Assert.Equal([1], Search("clean").Select(track => track.Id));
        Assert.Equal([1], Search("official video").Select(track => track.Id));
    }

    private static MusicTrack Track(int id, string title, string? languageCode) => new(
        id, string.Empty, title, $"{id}.mp3", null, "2026-01-01T00:00:00Z", null,
        false, null, null, null, "2026-01-01T00:00:00Z", false, true,
        LanguageCode: languageCode);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        return connection;
    }

    private void CreateLegacySchema()
    {
        using var connection = Open();
        Execute(connection, @"
            CREATE TABLE ratings (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE,
                sort_order INTEGER NOT NULL UNIQUE
            );
            CREATE TABLE tracks (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                rating_id INTEGER NULL REFERENCES ratings(id),
                rating_band TEXT NULL,
                canonical_url TEXT NULL,
                title TEXT NOT NULL,
                original_title TEXT NOT NULL DEFAULT '',
                artist TEXT NULL,
                remix TEXT NULL,
                edits TEXT NULL,
                file_name TEXT NOT NULL UNIQUE,
                downloaded_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                language_code TEXT NULL,
                library_state TEXT NOT NULL DEFAULT 'Active',
                needs_reevaluation INTEGER NOT NULL DEFAULT 0,
                is_public INTEGER NOT NULL DEFAULT 1
            );
            INSERT INTO ratings(name, sort_order) VALUES
                ('Avoid', 1), ('Okay', 2), ('Good', 3), ('Great', 4), ('Favorite', 5);");
    }

    private int InsertTrack(int? ratingId, string fileName)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO tracks(rating_id, title, file_name, downloaded_at, updated_at)
            VALUES ($ratingId, 'Track', $fileName, $now, $now);
            SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$ratingId", ratingId is null ? DBNull.Value : ratingId.Value);
        command.Parameters.AddWithValue("$fileName", fileName);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
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
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: preserve a potential test failure as the primary error.
        }
    }
}
