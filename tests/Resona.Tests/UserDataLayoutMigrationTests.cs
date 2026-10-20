using System.Text.Json;
using Resona.Services;

namespace Resona.Tests;

public sealed class UserDataLayoutMigrationTests
{
    [Fact]
    public void MigrateCopiesUserDataRewritesDefaultsAndLeavesLegacyDataUntouched()
    {
        using var temporary = new TemporaryDirectory();
        var legacy = Path.Combine(temporary.Path, "Resona");
        var destination = Path.Combine(temporary.Path, "ResonaData");
        Directory.CreateDirectory(Path.Combine(legacy, "tracks"));
        Directory.CreateDirectory(Path.Combine(legacy, "packages"));
        File.WriteAllText(Path.Combine(legacy, "music.db"), "database");
        File.WriteAllText(Path.Combine(legacy, "tracks", "track.m4a"), "audio");
        File.WriteAllText(Path.Combine(legacy, "packages", "release.nupkg"), "package");
        File.WriteAllText(Path.Combine(legacy, "Update.exe"), "updater");
        WriteLocations(legacy, legacy);

        UserDataLayoutMigration.Migrate(legacy, destination);

        Assert.Equal("database", File.ReadAllText(Path.Combine(destination, "music.db")));
        Assert.Equal("audio", File.ReadAllText(Path.Combine(destination, "tracks", "track.m4a")));
        Assert.False(Directory.Exists(Path.Combine(destination, "packages")));
        Assert.False(File.Exists(Path.Combine(destination, "Update.exe")));
        Assert.True(File.Exists(Path.Combine(legacy, "music.db")));
        Assert.True(File.Exists(Path.Combine(legacy, "tracks", "track.m4a")));

        var locations = ReadLocations(destination);
        Assert.Equal(Path.Combine(destination, "tracks"), locations.TracksDirectory);
        Assert.Equal(Path.Combine(destination, "music.db"), locations.DatabasePath);
    }

    [Fact]
    public void MigrateAdoptsAnExistingRestoredDestinationWithoutOverwritingIt()
    {
        using var temporary = new TemporaryDirectory();
        var legacy = Path.Combine(temporary.Path, "Resona");
        var destination = Path.Combine(temporary.Path, "ResonaData");
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(legacy, "music.db"), "new-empty-database");
        File.WriteAllText(Path.Combine(destination, "music.db"), "restored-database");
        WriteLocations(destination, legacy);

        UserDataLayoutMigration.Migrate(legacy, destination);

        Assert.Equal("restored-database", File.ReadAllText(Path.Combine(destination, "music.db")));
        var locations = ReadLocations(destination);
        Assert.Equal(Path.Combine(destination, "tracks"), locations.TracksDirectory);
        Assert.Equal(Path.Combine(destination, "music.db"), locations.DatabasePath);
    }

    [Fact]
    public void MigratePreservesExplicitExternalLibraryLocations()
    {
        using var temporary = new TemporaryDirectory();
        var legacy = Path.Combine(temporary.Path, "Resona");
        var destination = Path.Combine(temporary.Path, "ResonaData");
        var externalTracks = Path.Combine(temporary.Path, "ExternalTracks");
        var externalDatabase = Path.Combine(temporary.Path, "ExternalDatabase", "library.db");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "app-settings.json"), "{}");
        WriteLocations(legacy, externalTracks, externalDatabase);

        UserDataLayoutMigration.Migrate(legacy, destination);

        var locations = ReadLocations(destination);
        Assert.Equal(externalTracks, locations.TracksDirectory);
        Assert.Equal(externalDatabase, locations.DatabasePath);
    }

    private static void WriteLocations(string directory, string legacyDirectory) =>
        WriteLocations(
            directory,
            Path.Combine(legacyDirectory, "tracks"),
            Path.Combine(legacyDirectory, "music.db"));

    private static void WriteLocations(string directory, string tracksDirectory, string databasePath)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "library-locations.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                tracksDirectory,
                databasePath
            }));
    }

    private static StoredLocations ReadLocations(string directory)
    {
        var json = File.ReadAllText(Path.Combine(directory, "library-locations.json"));
        return JsonSerializer.Deserialize<StoredLocations>(
                   json,
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidDataException("Locations file is empty.");
    }

    private sealed record StoredLocations(
        int SchemaVersion,
        string TracksDirectory,
        string DatabasePath);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"resona-user-data-migration-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
