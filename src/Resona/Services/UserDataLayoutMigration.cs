using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Resona.Services;

public static class UserDataLayoutMigration
{
    public const string DataDirectoryName = "ResonaData";

    private const string LegacyDirectoryName = "Resona";
    private const string MigrationMarkerName = ".legacy-resona-data-migrated";
    private const string MigrationInProgressName = ".legacy-resona-data-migration-in-progress";
    private const int LibraryLocationsSchemaVersion = 1;

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "packages",
        "VelopackTemp"
    };

    private static readonly HashSet<string> ExcludedRootFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Update.exe",
        ".betaId",
        MigrationMarkerName,
        MigrationInProgressName
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void RunForCurrentUser()
    {
        if (!OperatingSystem.IsWindows() || !IsInstalledApplication())
            return;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData) || !Path.IsPathFullyQualified(localAppData))
            throw new InvalidOperationException("Could not determine the local application data directory.");

        Migrate(
            Path.Combine(localAppData, LegacyDirectoryName),
            Path.Combine(localAppData, DataDirectoryName));
    }

    internal static void Migrate(string legacyDirectory, string dataDirectory)
    {
        legacyDirectory = Path.GetFullPath(legacyDirectory);
        dataDirectory = Path.GetFullPath(dataDirectory);
        if (string.Equals(legacyDirectory, dataDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Legacy and destination data directories must be different.");

        Directory.CreateDirectory(dataDirectory);
        var markerPath = Path.Combine(dataDirectory, MigrationMarkerName);
        var inProgressPath = Path.Combine(dataDirectory, MigrationInProgressName);

        if (File.Exists(markerPath))
        {
            RewriteLegacyDefaultLocations(dataDirectory, legacyDirectory);
            return;
        }

        if (File.Exists(inProgressPath))
        {
            if (!ContainsUserData(legacyDirectory))
                throw new IOException("The legacy data migration was interrupted and its source is no longer available.");

            CopyDirectoryContents(legacyDirectory, dataDirectory, isRoot: true);
            RewriteLegacyDefaultLocations(dataDirectory, legacyDirectory);
            WriteMarker(
                markerPath,
                legacyDirectory,
                "Legacy data was copied. The source directory was not modified or deleted.");
            File.Delete(inProgressPath);
            return;
        }

        if (ContainsUserData(dataDirectory))
        {
            RewriteLegacyDefaultLocations(dataDirectory, legacyDirectory);
            WriteMarker(
                markerPath,
                legacyDirectory,
                "Existing data in ResonaData was adopted. The legacy directory was not modified or deleted.");
            return;
        }

        if (!ContainsUserData(legacyDirectory))
            return;

        WriteMarker(
            inProgressPath,
            legacyDirectory,
            "Migration is in progress. This marker allows an interrupted copy to continue safely.");
        CopyDirectoryContents(legacyDirectory, dataDirectory, isRoot: true);
        RewriteLegacyDefaultLocations(dataDirectory, legacyDirectory);
        WriteMarker(
            markerPath,
            legacyDirectory,
            "Legacy data was copied. The source directory was not modified or deleted.");
        File.Delete(inProgressPath);
    }

    private static bool IsInstalledApplication() =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "sq.version"));

    private static bool ContainsUserData(string directory)
    {
        if (!Directory.Exists(directory))
            return false;

        return File.Exists(Path.Combine(directory, "music.db"))
               || File.Exists(Path.Combine(directory, "app-settings.json"))
               || File.Exists(Path.Combine(directory, "library-locations.json"))
               || Directory.Exists(Path.Combine(directory, "tracks"));
    }

    private static void CopyDirectoryContents(string source, string destination, bool isRoot)
    {
        var sourceInfo = new DirectoryInfo(source);
        if ((sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Migration cannot safely copy the linked directory {source}.");

        Directory.CreateDirectory(destination);

        foreach (var filePath in Directory.EnumerateFiles(source))
        {
            var fileName = Path.GetFileName(filePath);
            if (isRoot && ExcludedRootFiles.Contains(fileName))
                continue;

            CopyFileSafely(filePath, Path.Combine(destination, fileName));
        }

        foreach (var childSource in Directory.EnumerateDirectories(source))
        {
            var directoryName = Path.GetFileName(childSource);
            if (isRoot && ExcludedDirectories.Contains(directoryName))
                continue;

            CopyDirectoryContents(
                childSource,
                Path.Combine(destination, directoryName),
                isRoot: false);
        }
    }

    private static void CopyFileSafely(string sourcePath, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            if (FilesMatch(sourcePath, destinationPath))
                return;

            throw new IOException(
                $"Migration stopped because destination file already exists with different content: {destinationPath}");
        }

        var temporaryPath = $"{destinationPath}.migrating-{Guid.NewGuid():N}";
        try
        {
            File.Copy(sourcePath, temporaryPath, overwrite: false);
            File.SetLastWriteTimeUtc(temporaryPath, File.GetLastWriteTimeUtc(sourcePath));
            File.Move(temporaryPath, destinationPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static bool FilesMatch(string firstPath, string secondPath)
    {
        var first = new FileInfo(firstPath);
        var second = new FileInfo(secondPath);
        if (first.Length != second.Length)
            return false;
        if (first.LastWriteTimeUtc == second.LastWriteTimeUtc)
            return true;

        using var firstStream = File.OpenRead(firstPath);
        using var secondStream = File.OpenRead(secondPath);
        var firstHash = SHA256.HashData(firstStream);
        var secondHash = SHA256.HashData(secondStream);
        return CryptographicOperations.FixedTimeEquals(firstHash, secondHash);
    }

    private static void RewriteLegacyDefaultLocations(string dataDirectory, string legacyDirectory)
    {
        var locationsPath = Path.Combine(dataDirectory, "library-locations.json");
        if (!File.Exists(locationsPath))
            return;

        var locations = JsonSerializer.Deserialize<StoredLibraryLocations>(
            File.ReadAllText(locationsPath),
            JsonOptions);
        if (locations is null || locations.SchemaVersion != LibraryLocationsSchemaVersion)
            throw new InvalidDataException($"Could not migrate {locationsPath}: unsupported contents.");

        var legacyTracks = Path.Combine(legacyDirectory, "tracks");
        var legacyDatabase = Path.Combine(legacyDirectory, "music.db");
        var tracksDirectory = SamePath(locations.TracksDirectory, legacyTracks)
            ? Path.Combine(dataDirectory, "tracks")
            : locations.TracksDirectory;
        var databasePath = SamePath(locations.DatabasePath, legacyDatabase)
            ? Path.Combine(dataDirectory, "music.db")
            : locations.DatabasePath;

        if (string.Equals(tracksDirectory, locations.TracksDirectory, StringComparison.Ordinal)
            && string.Equals(databasePath, locations.DatabasePath, StringComparison.Ordinal))
            return;

        var updated = locations with
        {
            TracksDirectory = tracksDirectory,
            DatabasePath = databasePath
        };
        WriteJsonAtomically(locationsPath, updated);
    }

    private static bool SamePath(string? first, string second)
    {
        if (string.IsNullOrWhiteSpace(first))
            return false;

        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteMarker(string markerPath, string legacyDirectory, string note)
    {
        var marker = new MigrationMarker(
            DateTimeOffset.UtcNow,
            legacyDirectory,
            note);
        WriteJsonAtomically(markerPath, marker);
    }

    private static void WriteJsonAtomically<T>(string path, T value)
    {
        var temporaryPath = $"{path}.migrating-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value, JsonOptions));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private sealed record StoredLibraryLocations(
        int SchemaVersion,
        string TracksDirectory,
        string DatabasePath);

    private sealed record MigrationMarker(
        DateTimeOffset MigratedAt,
        string SourceDirectory,
        string Note);
}
