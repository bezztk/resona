using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Resona.Models;
using Resona.Services;

namespace Resona;

public static class Values
{
    private const int LibraryLocationsSchemaVersion = 1;
    private static readonly JsonSerializerOptions LocationJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static readonly string SolutionDirectory = FindSolutionDirectory();
    public static readonly string ConfigurationDirectory = FindConfigurationDirectory();
    public static readonly string DefaultDataDirectory = FindDefaultDataDirectory();

    // Retained as the common application-state directory for existing callers.
    public static readonly string LocalDirectory = ConfigurationDirectory;
    public static readonly string WindowPlacementPath = Path.Combine(LocalDirectory, "window-placement.json");
    public static readonly string AppSettingsPath = Path.Combine(LocalDirectory, "app-settings.json");
    public static readonly string LibraryLocationsPath = Path.Combine(LocalDirectory, "library-locations.json");
    public static readonly string ToolsDirectory = Path.Combine(SolutionDirectory, "tools");
    public static readonly string ScriptsDirectory = Path.Combine(SolutionDirectory, "Scripts");
    public static readonly string ModelsDirectory = Path.Combine(SolutionDirectory, "models");
    public static readonly string TracksDirectory;
    public static readonly string DbPath;
    public static readonly string? LibraryLocationsLoadError;
    public const string AnalysisDockerImage = "essentia-tf-test";

    public static float Volume = 1f;
    public const int ManualFadeDurationSeconds = 2;
    public static bool UseYtDlpBrowserCookies;
    public static string YtDlpCookiesBrowser = "firefox";

    public static List<Genre> Genres = [];
    public static List<Tag> Tags = [];
    public static List<Style> Styles = [];
    public static List<Rating> Ratings = [];

    static Values()
    {
        var loadResult = LoadLibraryLocations();
        TracksDirectory = loadResult.Locations.TracksDirectory;
        DbPath = loadResult.Locations.DatabasePath;
        LibraryLocationsLoadError = loadResult.Error;
        if (loadResult.Error is null)
            EnsureLibraryLocationsFile(loadResult.Locations);
    }

    public static LibraryLocations GetLibraryLocations() => new(
        LibraryLocationsSchemaVersion,
        TracksDirectory,
        DbPath);

    public static LibraryLocations GetConfiguredLibraryLocations()
    {
        try
        {
            if (!File.Exists(LibraryLocationsPath))
                return GetLibraryLocations();

            var json = File.ReadAllText(LibraryLocationsPath);
            var locations = JsonSerializer.Deserialize<LibraryLocations>(json, LocationJsonOptions);
            return NormalizeLibraryLocations(locations);
        }
        catch
        {
            return GetLibraryLocations();
        }
    }

    public static void SaveLibraryLocations(string tracksDirectory, string databasePath)
    {
        if (string.IsNullOrWhiteSpace(tracksDirectory))
            throw new InvalidOperationException("Tracks folder is required.");
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new InvalidOperationException("Database path is required.");
        if (!Path.IsPathFullyQualified(tracksDirectory.Trim())
            || !Path.IsPathFullyQualified(databasePath.Trim()))
            throw new InvalidOperationException("Library locations must be absolute paths.");

        var locations = NormalizeLibraryLocations(new LibraryLocations(
            LibraryLocationsSchemaVersion,
            tracksDirectory,
            databasePath));

        EnsureWritableDirectory(locations.TracksDirectory);
        EnsureWritableDirectory(Path.GetDirectoryName(locations.DatabasePath)
                                ?? throw new InvalidOperationException("Database path must include a directory."));
        WriteLibraryLocations(locations);
    }

    private static LibraryLocationsLoadResult LoadLibraryLocations()
    {
        var defaults = DefaultLibraryLocations();
        try
        {
            if (!File.Exists(LibraryLocationsPath))
                return new LibraryLocationsLoadResult(defaults, null);

            var json = File.ReadAllText(LibraryLocationsPath);
            var locations = JsonSerializer.Deserialize<LibraryLocations>(json, LocationJsonOptions);
            if (locations is null)
                throw new InvalidDataException("The file does not contain library locations.");
            if (locations.SchemaVersion != LibraryLocationsSchemaVersion)
                throw new InvalidDataException(
                    $"Unsupported library location schema version {locations.SchemaVersion}.");

            return new LibraryLocationsLoadResult(NormalizeLibraryLocations(locations), null);
        }
        catch (Exception exception)
        {
            return new LibraryLocationsLoadResult(
                defaults,
                $"Could not load {LibraryLocationsPath}: {exception.Message}");
        }
    }

    private static LibraryLocations DefaultLibraryLocations() => new(
        LibraryLocationsSchemaVersion,
        Path.Combine(DefaultDataDirectory, "tracks"),
        Path.Combine(DefaultDataDirectory, "music.db"));

    private static LibraryLocations NormalizeLibraryLocations(LibraryLocations? locations)
    {
        var defaults = DefaultLibraryLocations();
        var tracksDirectory = string.IsNullOrWhiteSpace(locations?.TracksDirectory)
            ? defaults.TracksDirectory
            : Path.GetFullPath(locations.TracksDirectory.Trim());
        var databasePath = string.IsNullOrWhiteSpace(locations?.DatabasePath)
            ? defaults.DatabasePath
            : Path.GetFullPath(locations.DatabasePath.Trim());

        if (!Path.IsPathFullyQualified(tracksDirectory) || !Path.IsPathFullyQualified(databasePath))
            throw new InvalidOperationException("Library locations must be absolute paths.");
        if (Directory.Exists(databasePath))
            throw new InvalidOperationException("Database path points to a directory.");

        return new LibraryLocations(
            LibraryLocationsSchemaVersion,
            tracksDirectory,
            databasePath);
    }

    private static void EnsureLibraryLocationsFile(LibraryLocations locations)
    {
        try
        {
            if (!File.Exists(LibraryLocationsPath))
                WriteLibraryLocations(locations);
        }
        catch
        {
            // Database startup will surface an actionable error if the location is not writable.
        }
    }

    private static void WriteLibraryLocations(LibraryLocations locations)
    {
        Directory.CreateDirectory(ConfigurationDirectory);
        var json = JsonSerializer.Serialize(locations, LocationJsonOptions);
        var temporaryPath = LibraryLocationsPath + ".tmp";
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, LibraryLocationsPath, overwrite: true);
    }

    private static void EnsureWritableDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        var probePath = Path.Combine(directory, $".music-write-test-{Guid.NewGuid():N}");
        try
        {
            using var stream = new FileStream(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
            stream.WriteByte(0);
        }
        finally
        {
            if (File.Exists(probePath))
                File.Delete(probePath);
        }
    }

    private static string FindConfigurationDirectory()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                UserDataLayoutMigration.DataDirectoryName);

        return Path.Combine(
            AbsoluteEnvironmentPath("XDG_CONFIG_HOME")
            ?? Path.Combine(UserHomeDirectory(), ".config"),
            "music");
    }

    private static string FindDefaultDataDirectory()
    {
        if (OperatingSystem.IsWindows())
            return FindConfigurationDirectory();

        return Path.Combine(
            AbsoluteEnvironmentPath("XDG_DATA_HOME")
            ?? Path.Combine(UserHomeDirectory(), ".local", "share"),
            "music");
    }

    private static string? AbsoluteEnvironmentPath(string variableName)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        return !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value)
            ? value
            : null;
    }

    private static string UserHomeDirectory()
    {
        var path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("Could not determine the user home directory.");
        return path;
    }

    private static string FindSolutionDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Resona.sln")))
                return directory.FullName;
        }

        return AppContext.BaseDirectory;
    }

    private sealed record LibraryLocationsLoadResult(
        LibraryLocations Locations,
        string? Error);
}

public sealed record LibraryLocations(
    int SchemaVersion,
    string TracksDirectory,
    string DatabasePath);
