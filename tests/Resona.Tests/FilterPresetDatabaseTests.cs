using Microsoft.Data.Sqlite;
using Resona.Core;
using Resona.Services;
using System.Linq;

namespace Resona.Tests;

public sealed class FilterPresetDatabaseTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"resona-filter-presets-{Guid.NewGuid():N}");
    private readonly string _databasePath;
    private readonly MusicDatabase _database;

    public FilterPresetDatabaseTests()
    {
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "presets.db");
        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        MusicDatabase.EnsureFilterPresetSchema(connection);
        _database = new MusicDatabase(_databasePath);
    }

    [Fact]
    public void Presets_round_trip_through_sqlite_and_replace_removed_rows()
    {
        var first = new PortableFilterPreset(
            "Night energy",
            [new PortableFilterGroup(
                Genres: ["Electronic → Synthwave"],
                Styles: ["Nightcore"],
                Tags: [],
                Negate: false,
                EmotionalCharacters: [],
                Languages: ["zh"],
                MainGenres: [])]);
        var second = new PortableFilterPreset("Calm", []);

        _database.SaveFilterPresets([first, second]);

        var loaded = _database.GetFilterPresets();
        Assert.Equal(["Calm", "Night energy"], loaded.Select(item => item.Name));
        Assert.Equal(["zh"], loaded.Single(item => item.Name == "Night energy").Groups[0].Languages);

        _database.SaveFilterPresets([first]);
        Assert.Equal("Night energy", Assert.Single(_database.GetFilterPresets()).Name);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); }
        catch { }
    }
}
