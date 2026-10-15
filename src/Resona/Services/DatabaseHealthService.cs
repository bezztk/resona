using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Resona.Services;

public enum DatabaseHealthSeverity
{
    Info,
    Warning,
    Error
}

public sealed record DatabaseHealthIssue(
    string Code,
    DatabaseHealthSeverity Severity,
    string Category,
    string Title,
    string Detail,
    int Count = 1,
    IReadOnlyList<string>? Examples = null);

public sealed record DatabaseHealthReport(
    DateTime StartedAtUtc,
    DateTime FinishedAtUtc,
    string DatabasePath,
    string TracksDirectory,
    int TrackCount,
    int ReferencedFileCount,
    int AudioFileCount,
    IReadOnlyList<DatabaseHealthIssue> Issues)
{
    public int ErrorCount => Issues.Count(issue => issue.Severity == DatabaseHealthSeverity.Error);
    public int WarningCount => Issues.Count(issue => issue.Severity == DatabaseHealthSeverity.Warning);
    public bool IsHealthy => ErrorCount == 0 && WarningCount == 0;
}

/// <summary>Read-only consistency checks for the active local library.</summary>
public sealed class DatabaseHealthService
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".aac", ".flac", ".m4a", ".mp3", ".ogg", ".opus", ".wav", ".webm", ".wma"
    };
    private static readonly TimeSpan RecentUnownedFileGracePeriod = TimeSpan.FromMinutes(10);

    public static readonly DatabaseHealthService Current = new(Values.DbPath, Values.TracksDirectory);

    private readonly string _databasePath;
    private readonly string _tracksDirectory;

    public DatabaseHealthService(string databasePath, string tracksDirectory)
    {
        _databasePath = Path.GetFullPath(databasePath);
        _tracksDirectory = Path.GetFullPath(tracksDirectory);
    }

    public Task<DatabaseHealthReport> CheckAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Check(cancellationToken), cancellationToken);

    private DatabaseHealthReport Check(CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        var issues = new List<DatabaseHealthIssue>();
        var tracks = new List<HealthTrack>();
        var audioFileCount = 0;
        var tracksReadSuccessfully = false;

        if (!File.Exists(_databasePath))
        {
            issues.Add(Issue("database.missing", DatabaseHealthSeverity.Error, "Database",
                "Database file is missing", _databasePath));
            return Report();
        }

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 30
            }.ToString();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            cancellationToken.ThrowIfCancellationRequested();

            CheckIntegrity(connection, issues, cancellationToken);
            CheckForeignKeys(connection, issues, cancellationToken);
            (tracks, tracksReadSuccessfully) = ReadTracks(connection, issues, cancellationToken);
            CheckTrackWorkflow(tracks, issues);
            CheckDuplicateTrackKeys(tracks, issues);
            CheckChannelMappings(connection, issues, cancellationToken);
            CheckPersistedStatuses(connection, issues, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            issues.Add(Issue("database.open", DatabaseHealthSeverity.Error, "Database",
                "Database could not be checked", exception.Message));
        }

        audioFileCount = CheckAudioFiles(tracks, tracksReadSuccessfully, issues, cancellationToken);
        return Report();

        DatabaseHealthReport Report() => new(
            startedAt,
            DateTime.UtcNow,
            _databasePath,
            _tracksDirectory,
            tracks.Count,
            tracks.Select(track => track.FileName).Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            audioFileCount,
            issues.OrderByDescending(issue => issue.Severity).ThenBy(issue => issue.Category).ThenBy(issue => issue.Title).ToList());
    }

    private static void CheckIntegrity(
        SqliteConnection connection,
        List<DatabaseHealthIssue> issues,
        CancellationToken cancellationToken)
    {
        TryDatabaseCheck(issues, "database.integrity.check", "Database", "SQLite integrity check could not run", () =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check";
            using var reader = command.ExecuteReader();
            var failures = new List<string>();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = reader.GetString(0);
                if (!result.Equals("ok", StringComparison.OrdinalIgnoreCase))
                    failures.Add(result);
            }
            if (failures.Count > 0)
                issues.Add(Issue("database.integrity", DatabaseHealthSeverity.Error, "Database",
                    "SQLite reported structural damage",
                    "Restore from a known-good backup before making further changes.",
                    failures.Count,
                    failures.Take(5).ToList()));
        });
    }

    private static void CheckForeignKeys(
        SqliteConnection connection,
        List<DatabaseHealthIssue> issues,
        CancellationToken cancellationToken)
    {
        TryDatabaseCheck(issues, "database.foreign_keys.check", "Relations", "Foreign-key check could not run", () =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_key_check";
            using var reader = command.ExecuteReader();
            var violations = new List<string>();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var table = reader.IsDBNull(0) ? "unknown table" : reader.GetString(0);
                var rowId = reader.IsDBNull(1) ? "unknown row" : reader.GetInt64(1).ToString();
                var parent = reader.IsDBNull(2) ? "unknown parent" : reader.GetString(2);
                violations.Add($"{table} row {rowId} → {parent}");
            }
            if (violations.Count > 0)
                issues.Add(Issue("database.foreign_keys", DatabaseHealthSeverity.Error, "Relations",
                    "Orphaned database relationships found",
                    "Rows reference records that no longer exist.",
                    violations.Count,
                    violations.Take(5).ToList()));
        });
    }

    private static (List<HealthTrack> Tracks, bool Succeeded) ReadTracks(
        SqliteConnection connection,
        List<DatabaseHealthIssue> issues,
        CancellationToken cancellationToken)
    {
        var tracks = new List<HealthTrack>();
        var succeeded = false;
        TryDatabaseCheck(issues, "database.tracks.read", "Database", "Track table could not be read", () =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT id, title, file_name, canonical_url, library_state, rating_id,
                       needs_reevaluation, analysis_disabled, channel_id
                FROM tracks";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                tracks.Add(new HealthTrack(
                    reader.GetInt32(0),
                    reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    !reader.IsDBNull(6) && reader.GetInt32(6) != 0,
                    !reader.IsDBNull(7) && reader.GetInt32(7) != 0,
                    reader.IsDBNull(8) ? null : reader.GetInt32(8)));
            }
            succeeded = true;
        });
        return (tracks, succeeded);
    }

    private static void CheckTrackWorkflow(List<HealthTrack> tracks, List<DatabaseHealthIssue> issues)
    {
        var invalid = tracks.Where(track => track.LibraryState switch
        {
            "PendingRating" => track.RatingId is not null || !track.NeedsReview,
            "Active" => track.RatingId is null,
            "Rejected" => track.NeedsReview || !track.AnalysisDisabled,
            _ => true
        }).ToList();
        if (invalid.Count > 0)
            issues.Add(Issue("tracks.workflow", DatabaseHealthSeverity.Error, "Workflow",
                "Tracks have invalid workflow states",
                "Rating, review, rejection and analysis flags do not satisfy the persisted track invariants.",
                invalid.Count,
                TrackExamples(invalid)));
    }

    private static void CheckDuplicateTrackKeys(List<HealthTrack> tracks, List<DatabaseHealthIssue> issues)
    {
        var duplicateFiles = tracks
            .Where(track => !string.IsNullOrWhiteSpace(track.FileName))
            .GroupBy(track => track.FileName.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .ToList();
        if (duplicateFiles.Count > 0)
            issues.Add(Issue("tracks.duplicate_file", DatabaseHealthSeverity.Error, "Files",
                "Multiple tracks reference the same audio file",
                "Each library track must own one unique audio filename.",
                duplicateFiles.Count,
                TrackExamples(duplicateFiles)));

        var duplicateUrls = tracks
            .Where(track => !string.IsNullOrWhiteSpace(track.CanonicalUrl))
            .GroupBy(track => track.CanonicalUrl!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .ToList();
        if (duplicateUrls.Count > 0)
            issues.Add(Issue("tracks.duplicate_url", DatabaseHealthSeverity.Error, "Database",
                "Duplicate canonical track URLs found",
                "The same source video is attached to more than one track.",
                duplicateUrls.Count,
                TrackExamples(duplicateUrls)));
    }

    private static void CheckChannelMappings(
        SqliteConnection connection,
        List<DatabaseHealthIssue> issues,
        CancellationToken cancellationToken)
    {
        TryDatabaseCheck(issues, "channels.duplicates.check", "Channels", "Channel duplicates could not be checked", () =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT LOWER(TRIM(source_url)), COUNT(*)
                FROM channels
                WHERE source_url IS NOT NULL AND TRIM(source_url) <> ''
                GROUP BY LOWER(TRIM(source_url))
                HAVING COUNT(*) > 1";
            using var reader = command.ExecuteReader();
            var duplicates = new List<string>();
            var count = 0;
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                count += reader.GetInt32(1);
                duplicates.Add($"{reader.GetString(0)} · {reader.GetInt32(1)} records");
            }
            if (count > 0)
                issues.Add(Issue("channels.duplicate_url", DatabaseHealthSeverity.Warning, "Channels",
                    "Duplicate channel URLs found",
                    "Multiple channel records refer to the same normalized source URL.",
                    count,
                    duplicates.Take(5).ToList()));
        });
    }

    private static void CheckPersistedStatuses(
        SqliteConnection connection,
        List<DatabaseHealthIssue> issues,
        CancellationToken cancellationToken)
    {
        CheckUnknownStatus(connection, issues, cancellationToken,
            "queue.status", "Import queue", "import_queue_items", "status",
            ["Queued", "Downloading", "Analyzing", "ReadyForReview", "Failed", "Skipped"]);
    }

    private static void CheckUnknownStatus(
        SqliteConnection connection,
        List<DatabaseHealthIssue> issues,
        CancellationToken cancellationToken,
        string code,
        string title,
        string table,
        string column,
        IReadOnlyList<string> allowed)
    {
        TryDatabaseCheck(issues, $"{code}.check", "Workflow", $"{title} states could not be checked", () =>
        {
            using var command = connection.CreateCommand();
            var parameters = allowed.Select((_, index) => $"$allowed{index}").ToList();
            command.CommandText = $@"
                SELECT {column}, COUNT(*) FROM {table}
                WHERE {column} NOT IN ({string.Join(",", parameters)})
                GROUP BY {column}";
            for (var index = 0; index < allowed.Count; index++)
                command.Parameters.AddWithValue(parameters[index], allowed[index]);
            using var reader = command.ExecuteReader();
            var invalid = new List<string>();
            var count = 0;
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var value = reader.IsDBNull(0) ? "NULL" : reader.GetString(0);
                var rows = reader.GetInt32(1);
                count += rows;
                invalid.Add($"{value} · {rows} rows");
            }
            if (count > 0)
                issues.Add(Issue(code, DatabaseHealthSeverity.Error, "Workflow",
                    $"Unknown {title.ToLowerInvariant()} states found",
                    "Persisted state values are outside the supported state machine.",
                    count,
                    invalid));
        });
    }

    private int CheckAudioFiles(
        List<HealthTrack> tracks,
        bool tracksReadSuccessfully,
        List<DatabaseHealthIssue> issues,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_tracksDirectory))
        {
            issues.Add(Issue("files.directory_missing", DatabaseHealthSeverity.Error, "Files",
                "Tracks folder is missing", _tracksDirectory));
            return 0;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_tracksDirectory));
        var referencedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<HealthTrack>();
        var empty = new List<HealthTrack>();
        var unsafePaths = new List<HealthTrack>();
        foreach (var track in tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(track.FileName))
            {
                missing.Add(track);
                continue;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(Path.Combine(root, track.FileName));
            }
            catch
            {
                unsafePaths.Add(track);
                continue;
            }
            if (!IsWithinRoot(root, fullPath))
            {
                unsafePaths.Add(track);
                continue;
            }

            referencedFiles.Add(Path.GetFileName(fullPath));
            if (!File.Exists(fullPath))
                missing.Add(track);
            else
            {
                try
                {
                    if (new FileInfo(fullPath).Length == 0)
                        empty.Add(track);
                }
                catch (Exception exception)
                {
                    issues.Add(Issue("files.read", DatabaseHealthSeverity.Warning, "Files",
                        "An audio file could not be inspected", $"{track.FileName}: {exception.Message}"));
                }
            }
        }

        if (unsafePaths.Count > 0)
            issues.Add(Issue("files.unsafe_path", DatabaseHealthSeverity.Error, "Files",
                "Track filenames escape the tracks folder",
                "Absolute paths and parent-directory traversal are not valid track filenames.",
                unsafePaths.Count,
                TrackExamples(unsafePaths)));
        if (missing.Count > 0)
            issues.Add(Issue("files.missing", DatabaseHealthSeverity.Error, "Files",
                "Audio files are missing",
                "Database tracks refer to files that do not exist in the configured tracks folder.",
                missing.Count,
                TrackExamples(missing)));
        if (empty.Count > 0)
            issues.Add(Issue("files.empty", DatabaseHealthSeverity.Error, "Files",
                "Empty audio files found",
                "These referenced files have a size of zero bytes.",
                empty.Count,
                TrackExamples(empty)));

        List<string> audioFiles;
        try
        {
            audioFiles = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
                .Where(path => AudioExtensions.Contains(Path.GetExtension(path)))
                .ToList();
        }
        catch (Exception exception)
        {
            issues.Add(Issue("files.scan", DatabaseHealthSeverity.Error, "Files",
                "Tracks folder could not be scanned", exception.Message));
            return 0;
        }

        if (!tracksReadSuccessfully)
            return audioFiles.Count;

        var cutoff = DateTime.UtcNow - RecentUnownedFileGracePeriod;
        var unowned = new List<string>();
        foreach (var path in audioFiles.Where(path => !referencedFiles.Contains(Path.GetFileName(path))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                    unowned.Add(Path.GetFileName(path));
            }
            catch (Exception exception)
            {
                issues.Add(Issue("files.read", DatabaseHealthSeverity.Warning, "Files",
                    "An audio file could not be inspected", $"{Path.GetFileName(path)}: {exception.Message}"));
            }
        }
        if (unowned.Count > 0)
            issues.Add(Issue("files.unowned", DatabaseHealthSeverity.Warning, "Files",
                "Audio files are not owned by database tracks",
                "The files are older than ten minutes and are not referenced by the library. No files were changed.",
                unowned.Count,
                unowned.Take(5).ToList()));
        return audioFiles.Count;
    }

    private static bool IsWithinRoot(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative)
               && !relative.Equals("..", StringComparison.Ordinal)
               && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
               && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static void TryDatabaseCheck(
        List<DatabaseHealthIssue> issues,
        string code,
        string category,
        string title,
        Action check)
    {
        try { check(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            issues.Add(Issue(code, DatabaseHealthSeverity.Error, category, title, exception.Message));
        }
    }

    private static DatabaseHealthIssue Issue(
        string code,
        DatabaseHealthSeverity severity,
        string category,
        string title,
        string detail,
        int count = 1,
        IReadOnlyList<string>? examples = null) =>
        new(code, severity, category, title, detail, count, examples);

    private static IReadOnlyList<string> TrackExamples(IEnumerable<HealthTrack> tracks) =>
        tracks.Take(5).Select(track => $"#{track.Id} · {track.Title}").ToList();

    private sealed record HealthTrack(
        int Id,
        string Title,
        string FileName,
        string? CanonicalUrl,
        string LibraryState,
        int? RatingId,
        bool NeedsReview,
        bool AnalysisDisabled,
        int? ChannelId);
}
