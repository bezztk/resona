using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Platform;
using Microsoft.Data.Sqlite;
using Resona.Models;

namespace Resona.Services;

public class MusicDatabase
{
    private const string AssetBaseUri = "avares://Resona/Assets/";
    private const string RemovedMoodThemeModelName = "mtg_" + "jamen" + "do_" + "mood" + "theme";
    private static readonly string[] DefaultStyleNames = ["Nightcore", "Reverb", "Sped Up", "Slowed"];
    private readonly string _databasePath;
    private readonly string _connectionString;

    public MusicDatabase(string? databasePath = null)
    {
        _databasePath = string.IsNullOrWhiteSpace(databasePath) ? Values.DbPath : databasePath;
        _connectionString = $"Data Source={_databasePath};Default Timeout=30";
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON;";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "PRAGMA busy_timeout = 30000;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    public void Initialize()
    {
        if (File.Exists(_databasePath))
        {
            using var existingConnection = Open();
            EnableConcurrentReads(existingConnection);
            ApplyMigrations(existingConnection);
            return;
        }

        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        CreateSchema(conn, tx);
        SeedDefaultMetadata(conn, tx);
        tx.Commit();
        EnsureTrackWorkflowGuards(conn);
    }

    private static void EnableConcurrentReads(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL;";
        command.ExecuteScalar();
        command.CommandText = "PRAGMA synchronous = NORMAL;";
        command.ExecuteNonQuery();
    }

    private static void CreateSchema(SqliteConnection conn, SqliteTransaction tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            CREATE TABLE channels (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                name                TEXT NOT NULL,
                source_channel_id   TEXT NULL UNIQUE,
                source_url          TEXT NULL,
                inform_new_songs    INTEGER NOT NULL DEFAULT 0,
                subscribed          INTEGER NOT NULL DEFAULT 0,
                created_at          TEXT NULL,
                updated_at          TEXT NULL,
                last_checked_at     TEXT NULL,
                video_count         INTEGER NOT NULL DEFAULT 0,
                follower_count      INTEGER NULL,
                thumbnail_url       TEXT NULL,
                thumbnail           BLOB NULL,
                remote_metadata_updated_at TEXT NULL,
                basic_metadata_checked_at TEXT NULL
            );

            CREATE TABLE channel_videos (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id          INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
                video_id            TEXT NOT NULL UNIQUE,
                canonical_url       TEXT NOT NULL UNIQUE,
                title               TEXT NOT NULL,
                duration_seconds    INTEGER NULL,
                uploaded_at         TEXT NULL,
                discovered_at       TEXT NOT NULL,
                updated_at          TEXT NOT NULL,
                is_checked          INTEGER NOT NULL DEFAULT 0,
                download_status     TEXT NOT NULL DEFAULT 'NotQueued',
                download_error      TEXT NULL,
                download_attempts   INTEGER NOT NULL DEFAULT 0,
                manual_download_requested INTEGER NOT NULL DEFAULT 0,
                metadata_status     TEXT NOT NULL DEFAULT 'Pending',
                metadata_updated_at TEXT NULL,
                metadata_error      TEXT NULL,
                metadata_attempts   INTEGER NOT NULL DEFAULT 0,
                metadata_priority   INTEGER NOT NULL DEFAULT 0,
                view_count          INTEGER NULL,
                like_count          INTEGER NULL,
                thumbnail_url       TEXT NULL
            );

            CREATE TABLE channel_notifications (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id          INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
                channel_video_id    INTEGER NULL REFERENCES channel_videos(id) ON DELETE CASCADE,
                kind                TEXT NOT NULL,
                title               TEXT NOT NULL,
                created_at          TEXT NOT NULL,
                read_at             TEXT NULL,
                archived_at         TEXT NULL,
                UNIQUE(channel_video_id, kind)
            );
            CREATE INDEX ix_channel_notifications_inbox
                ON channel_notifications(archived_at, read_at, created_at DESC);

            CREATE TABLE ratings (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                name        TEXT NOT NULL UNIQUE,
                sort_order  INTEGER NOT NULL UNIQUE
            );

            CREATE TABLE tags (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                name         TEXT NOT NULL UNIQUE
            );

            CREATE TABLE style_definitions (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                name         TEXT NOT NULL COLLATE NOCASE UNIQUE
            );

            CREATE TABLE tracks (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id          INTEGER NULL REFERENCES channels(id),
                rating_id           INTEGER NULL REFERENCES ratings(id),
                canonical_url       TEXT NULL UNIQUE,
                title               TEXT NOT NULL,
                original_title      TEXT NOT NULL,
                artist              TEXT NULL,
                remix               TEXT NULL,
                is_original         INTEGER NOT NULL DEFAULT 1,
                parent_track_id     INTEGER NULL REFERENCES tracks(id) ON DELETE SET NULL,
                edit_types          INTEGER NOT NULL DEFAULT 0,
                rating_band         TEXT NULL,
                file_name           TEXT NOT NULL UNIQUE,
                duration_seconds    INTEGER NULL,
                uploaded_at         TEXT NULL,
                downloaded_at       TEXT NOT NULL,
                updated_at          TEXT NOT NULL,
                listen_count        INTEGER NOT NULL DEFAULT 0,
                listened_seconds    INTEGER NOT NULL DEFAULT 0,
                skip_count          INTEGER NOT NULL DEFAULT 0,
                last_listened_at    TEXT NULL,
                file_size_bytes     INTEGER NULL,
                download_duration_ms INTEGER NULL,
                thumbnail           BLOB NULL,
                library_state       TEXT NOT NULL DEFAULT 'Active',
                source_video_id     TEXT NULL,
                view_count          INTEGER NULL,
                like_count          INTEGER NULL,
                source_thumbnail_url TEXT NULL,
                source_metadata_updated_at TEXT NULL,
                analysis_disabled   INTEGER NOT NULL DEFAULT 0,
                is_public           INTEGER NOT NULL DEFAULT 1,
                needs_reevaluation  INTEGER NOT NULL DEFAULT 0,
                notes               TEXT NULL,
                language_code       TEXT NULL
            );
            CREATE INDEX ix_tracks_library_state ON tracks(library_state, downloaded_at DESC);

            CREATE TABLE collections (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                stable_id       TEXT NOT NULL UNIQUE,
                name            TEXT NOT NULL COLLATE NOCASE UNIQUE,
                cover_kind      TEXT NOT NULL DEFAULT 'Automatic',
                cover_track_id  INTEGER NULL REFERENCES tracks(id) ON DELETE SET NULL,
                custom_cover    BLOB NULL,
                created_at      TEXT NOT NULL,
                updated_at      TEXT NOT NULL
            );

            CREATE TABLE collection_tracks (
                collection_id   INTEGER NOT NULL REFERENCES collections(id) ON DELETE CASCADE,
                track_id        INTEGER NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
                position        INTEGER NOT NULL,
                added_at        TEXT NOT NULL,
                PRIMARY KEY (collection_id, track_id)
            );
            CREATE INDEX ix_collection_tracks_order ON collection_tracks(collection_id, position);
            CREATE INDEX ix_collection_tracks_track ON collection_tracks(track_id);

            CREATE TABLE track_genres (
                track_id                 INTEGER NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
                genre_id                 INTEGER NOT NULL REFERENCES model_subgenres(id),
                assigned_at              TEXT NOT NULL,
                is_enabled               INTEGER NOT NULL DEFAULT 1,
                is_manual                INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (track_id, genre_id)
            );

            CREATE TABLE track_tags (
                track_id     INTEGER NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
                tag_id       INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
                PRIMARY KEY (track_id, tag_id)
            );

            CREATE TABLE track_styles (
                track_id     INTEGER NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
                style_id     INTEGER NOT NULL REFERENCES style_definitions(id) ON DELETE CASCADE,
                PRIMARY KEY (track_id, style_id)
            );
            CREATE INDEX ix_track_styles_style_id ON track_styles(style_id);

            CREATE TABLE track_analysis (
                id                INTEGER PRIMARY KEY AUTOINCREMENT,
                track_id          INTEGER NOT NULL UNIQUE REFERENCES tracks(id) ON DELETE CASCADE,
                analyzed_at       TEXT NOT NULL,
                analyzer_name     TEXT NULL,
                analyzer_version  TEXT NULL,
                bpm               REAL NULL,
                integrated_loudness REAL NULL,
                loudness_range    REAL NULL,
                danceability      REAL NULL,
                analysis_duration_ms INTEGER NULL
            );

            CREATE TABLE model_genres (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                name    TEXT NOT NULL UNIQUE
            );

            CREATE TABLE model_subgenres (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                model_genre_id  INTEGER NOT NULL REFERENCES model_genres(id) ON DELETE CASCADE,
                name            TEXT NOT NULL,
                description     TEXT NULL,
                classification_hint TEXT NULL,
                bpm_min         INTEGER NULL,
                bpm_max         INTEGER NULL,
                UNIQUE (model_genre_id, name)
            );

            CREATE TABLE model_subgenre_distinctions (
                id                              INTEGER PRIMARY KEY AUTOINCREMENT,
                model_subgenre_id               INTEGER NOT NULL REFERENCES model_subgenres(id) ON DELETE CASCADE,
                distinguish_from_model_subgenre_id INTEGER NOT NULL REFERENCES model_subgenres(id) ON DELETE CASCADE,
                difference                      TEXT NOT NULL,
                UNIQUE (model_subgenre_id, distinguish_from_model_subgenre_id)
            );

            CREATE TABLE track_genre_predictions (
                id                 INTEGER PRIMARY KEY AUTOINCREMENT,
                track_analysis_id  INTEGER NOT NULL REFERENCES track_analysis(id) ON DELETE CASCADE,
                model_subgenre_id  INTEGER NOT NULL REFERENCES model_subgenres(id),
                score              REAL NOT NULL CHECK (score >= 0 AND score <= 1),
                UNIQUE (track_analysis_id, model_subgenre_id)
            );

            CREATE TABLE track_analysis_signals (
                id                 INTEGER PRIMARY KEY AUTOINCREMENT,
                track_analysis_id  INTEGER NOT NULL REFERENCES track_analysis(id) ON DELETE CASCADE,
                model_family       TEXT NOT NULL,
                category           TEXT NOT NULL,
                model_name         TEXT NOT NULL,
                model_type         TEXT NOT NULL,
                description        TEXT NOT NULL,
                signal_key         TEXT NOT NULL,
                score              REAL NOT NULL,
                UNIQUE (track_analysis_id, model_name, signal_key)
            );

            CREATE TABLE import_batches (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                source_url  TEXT NOT NULL,
                created_at  TEXT NOT NULL
            );

            CREATE TABLE import_queue_items (
                id                    INTEGER PRIMARY KEY AUTOINCREMENT,
                batch_id              INTEGER NOT NULL REFERENCES import_batches(id) ON DELETE CASCADE,
                source_url            TEXT NOT NULL,
                canonical_url         TEXT NOT NULL UNIQUE,
                title                 TEXT NOT NULL,
                duration_seconds      INTEGER NULL,
                estimated_size_bytes  INTEGER NULL,
                status                TEXT NOT NULL,
                detail                TEXT NULL,
                track_id              INTEGER NULL REFERENCES tracks(id) ON DELETE SET NULL,
                is_original           INTEGER NOT NULL DEFAULT 1,
                parent_track_id       INTEGER NULL REFERENCES tracks(id) ON DELETE SET NULL,
                edit_types            INTEGER NOT NULL DEFAULT 0,
                request_json          TEXT NULL,
                created_at            TEXT NOT NULL,
                updated_at            TEXT NOT NULL
            );

            CREATE INDEX ix_track_genres_genre_id ON track_genres(genre_id);
            CREATE INDEX ix_channel_videos_channel_checked ON channel_videos(channel_id, is_checked, uploaded_at);
            CREATE INDEX ix_channel_videos_metadata_queue
                ON channel_videos(metadata_status, metadata_updated_at, id DESC);
            CREATE INDEX ix_tracks_channel_state ON tracks(channel_id, library_state);
            CREATE INDEX ix_track_tags_tag_id ON track_tags(tag_id);
            CREATE INDEX ix_model_subgenres_model_genre_id ON model_subgenres(model_genre_id);
            CREATE INDEX ix_model_subgenre_distinctions_source ON model_subgenre_distinctions(model_subgenre_id);
            CREATE INDEX ix_track_genre_predictions_analysis_id ON track_genre_predictions(track_analysis_id);
            CREATE INDEX ix_track_analysis_signals_analysis_id ON track_analysis_signals(track_analysis_id);
            ";
        cmd.ExecuteNonQuery();
    }

    private static void ApplyMigrations(SqliteConnection conn)
    {
        EnsureColumn(conn, "tracks", "listened_seconds", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(conn, "tracks", "file_size_bytes", "INTEGER NULL");
        EnsureColumn(conn, "tracks", "download_duration_ms", "INTEGER NULL");
        EnsureColumn(conn, "tracks", "thumbnail", "BLOB NULL");
        EnsureColumn(conn, "tracks", "library_state", "TEXT NOT NULL DEFAULT 'Active'");
        EnsureColumn(conn, "tracks", "source_video_id", "TEXT NULL");
        EnsureColumn(conn, "tracks", "view_count", "INTEGER NULL");
        EnsureColumn(conn, "tracks", "like_count", "INTEGER NULL");
        EnsureColumn(conn, "tracks", "source_thumbnail_url", "TEXT NULL");
        EnsureColumn(conn, "tracks", "source_metadata_updated_at", "TEXT NULL");
        ExecuteNonQuery(conn,
            "CREATE INDEX IF NOT EXISTS ix_tracks_library_state ON tracks(library_state, downloaded_at DESC)");
        EnsureColumn(conn, "tracks", "analysis_disabled", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(conn, "tracks", "language_code", "TEXT NULL");
        EnsureColumn(conn, "tracks", "artist", "TEXT NULL");
        EnsureColumn(conn, "tracks", "remix", "TEXT NULL");
        EnsureTrackVersionSchema(conn);
        EnsureColumn(conn, "tracks", "rating_band", "TEXT NULL");
        EnsureCurrentRatings(conn);
        EnsureColumn(conn, "tracks", "is_public", "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(conn, "tracks", "needs_reevaluation", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(conn, "track_analysis", "analysis_duration_ms", "INTEGER NULL");
        EnsureColumn(conn, "model_subgenres", "description", "TEXT NULL");
        EnsureColumn(conn, "model_subgenres", "classification_hint", "TEXT NULL");
        EnsureColumn(conn, "model_subgenres", "bpm_min", "INTEGER NULL");
        EnsureColumn(conn, "model_subgenres", "bpm_max", "INTEGER NULL");
        EnsureTrackGenreSourceSchema(conn);
        RenameLegacySkipRating(conn);
        DropTrackWorkflowGuards(conn);
        NormalizeTrackWorkflowState(conn);
        EnsureTrackWorkflowGuards(conn);
        EnsureChannelSubscriptionSchema(conn);
        CreateImportQueueSchema(conn);
        CreateModelMetadataSchema(conn);
        CreateTagSchema(conn);
        SimplifyTagSchemaIfNeeded(conn);
        CreateStyleSchema(conn);
        RemoveLegacyNightcoreGenre(conn);
        DropLegacyEditsColumn(conn);
        CreateCollectionSchema(conn);
    }

    private static void CreateCollectionSchema(SqliteConnection conn)
    {
        ExecuteNonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS collections (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                stable_id TEXT NOT NULL UNIQUE,
                name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                cover_kind TEXT NOT NULL DEFAULT 'Automatic',
                cover_track_id INTEGER NULL REFERENCES tracks(id) ON DELETE SET NULL,
                custom_cover BLOB NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS collection_tracks (
                collection_id INTEGER NOT NULL REFERENCES collections(id) ON DELETE CASCADE,
                track_id INTEGER NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
                position INTEGER NOT NULL,
                added_at TEXT NOT NULL,
                PRIMARY KEY (collection_id, track_id)
            );
            CREATE INDEX IF NOT EXISTS ix_collection_tracks_order
                ON collection_tracks(collection_id, position);
            CREATE INDEX IF NOT EXISTS ix_collection_tracks_track
                ON collection_tracks(track_id);");
    }

    internal static void NormalizeTrackWorkflowState(SqliteConnection conn)
    {
        var now = DateTime.UtcNow.ToString("O");
        ExecuteNonQuery(conn, @"
            UPDATE tracks
            SET library_state = 'PendingRating', needs_reevaluation = 1, updated_at = $now
            WHERE rating_id IS NULL AND library_state <> 'Rejected';

            UPDATE tracks
            SET library_state = 'Active', updated_at = $now
            WHERE rating_id IS NOT NULL AND library_state = 'PendingRating';

            UPDATE tracks
            SET needs_reevaluation = 0, analysis_disabled = 1, updated_at = $now
            WHERE library_state = 'Rejected'
              AND (needs_reevaluation <> 0 OR analysis_disabled <> 1);

            UPDATE tracks
            SET library_state = CASE WHEN rating_id IS NULL THEN 'PendingRating' ELSE 'Active' END,
                needs_reevaluation = CASE WHEN rating_id IS NULL THEN 1 ELSE needs_reevaluation END,
                updated_at = $now
            WHERE library_state NOT IN ('PendingRating', 'Active', 'Rejected');",
            ("$now", now));
    }

    internal static void EnsureTrackWorkflowGuards(SqliteConnection conn)
    {
        ExecuteNonQuery(conn, @"
            CREATE TRIGGER IF NOT EXISTS trg_tracks_workflow_insert
            BEFORE INSERT ON tracks
            WHEN NOT (
                (NEW.library_state = 'PendingRating' AND NEW.rating_id IS NULL AND NEW.needs_reevaluation = 1)
                OR (NEW.library_state = 'Active' AND NEW.rating_id IS NOT NULL)
                OR (NEW.library_state = 'Rejected' AND NEW.needs_reevaluation = 0 AND NEW.analysis_disabled = 1)
            )
            BEGIN
                SELECT RAISE(ABORT, 'invalid track workflow state');
            END;

            CREATE TRIGGER IF NOT EXISTS trg_tracks_workflow_update
            BEFORE UPDATE OF library_state, rating_id, needs_reevaluation, analysis_disabled ON tracks
            WHEN NOT (
                (NEW.library_state = 'PendingRating' AND NEW.rating_id IS NULL AND NEW.needs_reevaluation = 1)
                OR (NEW.library_state = 'Active' AND NEW.rating_id IS NOT NULL)
                OR (NEW.library_state = 'Rejected' AND NEW.needs_reevaluation = 0 AND NEW.analysis_disabled = 1)
            )
            BEGIN
                SELECT RAISE(ABORT, 'invalid track workflow state');
            END;");
    }

    private static void DropTrackWorkflowGuards(SqliteConnection conn) =>
        ExecuteNonQuery(conn, @"
            DROP TRIGGER IF EXISTS trg_tracks_workflow_insert;
            DROP TRIGGER IF EXISTS trg_tracks_workflow_update;");

    private static void EnsureTrackGenreSourceSchema(SqliteConnection conn)
    {
        if (ColumnExists(conn, "track_genres", "is_manual"))
            return;

        using var tx = conn.BeginTransaction();
        ExecuteInsert(conn, tx, "ALTER TABLE track_genres ADD COLUMN is_manual INTEGER NOT NULL DEFAULT 0");
        ExecuteInsert(conn, tx, @"
            UPDATE track_genres
            SET is_manual = 1
            WHERE NOT EXISTS (
                SELECT 1
                FROM track_analysis analysis
                JOIN track_genre_predictions predictions
                  ON predictions.track_analysis_id = analysis.id
                WHERE analysis.track_id = track_genres.track_id
                  AND predictions.model_subgenre_id = track_genres.genre_id
                  AND predictions.score > 0.25
            )");
        tx.Commit();
    }

    private static void RenameLegacySkipRating(SqliteConnection conn)
    {
        ExecuteNonQuery(conn, @"
            UPDATE ratings
            SET name = $avoid
            WHERE name = 'Skip'
              AND NOT EXISTS (SELECT 1 FROM ratings WHERE name = $avoid)",
            ("$avoid", RatingNames.Avoid));
    }

    internal static void EnsureCurrentRatings(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();
        ExecuteInsert(conn, tx, @"
            UPDATE tracks
            SET rating_id = (SELECT id FROM ratings WHERE name = $timeless)
            WHERE rating_id = (SELECT id FROM ratings WHERE name = 'Favorite')
              AND EXISTS (SELECT 1 FROM ratings WHERE name = $timeless)",
            ("$timeless", RatingNames.Timeless));
        ExecuteInsert(conn, tx, @"
            DELETE FROM ratings
            WHERE name = 'Favorite'
              AND EXISTS (SELECT 1 FROM ratings WHERE name = $timeless)",
            ("$timeless", RatingNames.Timeless));
        ExecuteInsert(conn, tx, @"
            UPDATE ratings SET name = $timeless
            WHERE name = 'Favorite'
              AND NOT EXISTS (SELECT 1 FROM ratings WHERE name = $timeless)",
            ("$timeless", RatingNames.Timeless));
        ExecuteInsert(conn, tx, "UPDATE ratings SET sort_order = 1000 WHERE name = $timeless",
            ("$timeless", RatingNames.Timeless));
        ExecuteInsert(conn, tx, @"
            INSERT INTO ratings (name, sort_order) VALUES ($amazing, 5)
            ON CONFLICT(name) DO UPDATE SET sort_order = 5",
            ("$amazing", RatingNames.Amazing));
        ExecuteInsert(conn, tx, "UPDATE ratings SET sort_order = 6 WHERE name = $timeless",
            ("$timeless", RatingNames.Timeless));
        tx.Commit();
    }

    private static void CreateTagSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS tags (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE
            );
            CREATE TABLE IF NOT EXISTS track_tags (
                track_id INTEGER NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
                tag_id INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
                PRIMARY KEY (track_id, tag_id)
            );
            CREATE INDEX IF NOT EXISTS ix_track_tags_tag_id ON track_tags(tag_id);
            ";
        cmd.ExecuteNonQuery();
    }

    private static void CreateStyleSchema(SqliteConnection conn)
    {
        var migrateLegacyEdits = !TableExists(conn, "track_styles") && ColumnExists(conn, "tracks", "edits");
        ExecuteNonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS style_definitions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL COLLATE NOCASE UNIQUE
            );
            CREATE TABLE IF NOT EXISTS track_styles (
                track_id INTEGER NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
                style_id INTEGER NOT NULL REFERENCES style_definitions(id) ON DELETE CASCADE,
                PRIMARY KEY (track_id, style_id)
            );
            CREATE INDEX IF NOT EXISTS ix_track_styles_style_id ON track_styles(style_id);");
        using var tx = conn.BeginTransaction();
        SeedDefaultStyles(conn, tx);
        if (migrateLegacyEdits)
            ExecuteInsert(conn, tx, @"
                INSERT OR IGNORE INTO track_styles (track_id, style_id)
                SELECT tracks.id, styles.id
                FROM tracks
                CROSS JOIN style_definitions styles
                WHERE tracks.edits IS NOT NULL
                  AND (
                      INSTR(',' || LOWER(REPLACE(tracks.edits, ', ', ',')) || ',',
                            ',' || LOWER(styles.name) || ',') > 0
                      OR LOWER(styles.name) = 'sped up'
                         AND INSTR(',' || LOWER(REPLACE(tracks.edits, ', ', ',')) || ',', ',speed up,') > 0
                      OR LOWER(styles.name) = 'slowed'
                         AND INSTR(',' || LOWER(REPLACE(tracks.edits, ', ', ',')) || ',', ',slow,') > 0
                  )");
        tx.Commit();
    }

    private static void RemoveLegacyNightcoreGenre(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();
        ExecuteInsert(conn, tx, @"
            DELETE FROM track_genres
            WHERE genre_id IN (SELECT id FROM model_subgenres WHERE name = 'Nightcore' COLLATE NOCASE)");
        ExecuteInsert(conn, tx, @"
            DELETE FROM track_genre_predictions
            WHERE model_subgenre_id IN (SELECT id FROM model_subgenres WHERE name = 'Nightcore' COLLATE NOCASE)");
        ExecuteInsert(conn, tx, "DELETE FROM model_subgenres WHERE name = 'Nightcore' COLLATE NOCASE");
        tx.Commit();
    }

    private static void DropLegacyEditsColumn(SqliteConnection conn)
    {
        if (ColumnExists(conn, "tracks", "edits"))
            ExecuteNonQuery(conn, "ALTER TABLE tracks DROP COLUMN edits");
    }

    private static void SimplifyTagSchemaIfNeeded(SqliteConnection conn)
    {
        if (!TableExists(conn, "tags") || !ColumnExists(conn, "tags", "category_id"))
            return;

        using (var foreignKeyCommand = conn.CreateCommand())
        {
            foreignKeyCommand.CommandText = "PRAGMA foreign_keys = OFF;";
            foreignKeyCommand.ExecuteNonQuery();
        }

        try
        {
            using var tx = conn.BeginTransaction();
            ExecuteInsert(conn, tx, "DROP INDEX IF EXISTS ix_tags_category_id");
            ExecuteInsert(conn, tx, "DROP INDEX IF EXISTS ix_track_tags_tag_id");
            ExecuteInsert(conn, tx, "DROP INDEX IF EXISTS ix_tag_rules_tag_id");
            ExecuteInsert(conn, tx, "DROP INDEX IF EXISTS ix_track_tag_suggestions_track_id");

            ExecuteInsert(conn, tx, "DROP TABLE IF EXISTS track_tag_suggestions");
            ExecuteInsert(conn, tx, "DROP TABLE IF EXISTS tag_rules");
            ExecuteInsert(conn, tx, "ALTER TABLE track_tags RENAME TO track_tags_legacy");
            ExecuteInsert(conn, tx, "ALTER TABLE tags RENAME TO tags_legacy");

            ExecuteInsert(conn, tx, @"
                CREATE TABLE tags (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL UNIQUE
                )");
            ExecuteInsert(conn, tx, @"
                INSERT INTO tags (id, name)
                SELECT MIN(id), name
                FROM tags_legacy
                GROUP BY name");

            ExecuteInsert(conn, tx, @"
                CREATE TABLE track_tags (
                    track_id INTEGER NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
                    tag_id INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
                    PRIMARY KEY (track_id, tag_id)
                )");
            ExecuteInsert(conn, tx, @"
                INSERT OR IGNORE INTO track_tags (track_id, tag_id)
                SELECT legacy_links.track_id, tags.id
                FROM track_tags_legacy legacy_links
                JOIN tags_legacy legacy_tags ON legacy_tags.id = legacy_links.tag_id
                JOIN tags ON tags.name = legacy_tags.name");

            ExecuteInsert(conn, tx, "DROP TABLE track_tags_legacy");
            ExecuteInsert(conn, tx, "DROP TABLE tags_legacy");
            ExecuteInsert(conn, tx, "DROP TABLE IF EXISTS tag_categories");
            ExecuteInsert(conn, tx, "CREATE INDEX ix_track_tags_tag_id ON track_tags(tag_id)");
            tx.Commit();
        }
        finally
        {
            using var foreignKeyCommand = conn.CreateCommand();
            foreignKeyCommand.CommandText = "PRAGMA foreign_keys = ON;";
            foreignKeyCommand.ExecuteNonQuery();
        }
    }

    private static void CreateModelMetadataSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS model_subgenre_distinctions (
                id                              INTEGER PRIMARY KEY AUTOINCREMENT,
                model_subgenre_id               INTEGER NOT NULL REFERENCES model_subgenres(id) ON DELETE CASCADE,
                distinguish_from_model_subgenre_id INTEGER NOT NULL REFERENCES model_subgenres(id) ON DELETE CASCADE,
                difference                      TEXT NOT NULL,
                UNIQUE (model_subgenre_id, distinguish_from_model_subgenre_id)
            );
            CREATE INDEX IF NOT EXISTS ix_model_subgenre_distinctions_source
                ON model_subgenre_distinctions(model_subgenre_id);";
        cmd.ExecuteNonQuery();
    }

    private static void EnsureChannelSubscriptionSchema(SqliteConnection conn)
    {
        EnsureColumn(conn, "channels", "subscribed", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(conn, "channels", "created_at", "TEXT NULL");
        EnsureColumn(conn, "channels", "updated_at", "TEXT NULL");
        EnsureColumn(conn, "channels", "last_checked_at", "TEXT NULL");
        EnsureColumn(conn, "channels", "video_count", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(conn, "channels", "follower_count", "INTEGER NULL");
        EnsureColumn(conn, "channels", "thumbnail_url", "TEXT NULL");
        EnsureColumn(conn, "channels", "thumbnail", "BLOB NULL");
        EnsureColumn(conn, "channels", "remote_metadata_updated_at", "TEXT NULL");
        EnsureColumn(conn, "channels", "basic_metadata_checked_at", "TEXT NULL");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS channel_videos (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
                video_id TEXT NOT NULL UNIQUE,
                canonical_url TEXT NOT NULL UNIQUE,
                title TEXT NOT NULL,
                duration_seconds INTEGER NULL,
                uploaded_at TEXT NULL,
                discovered_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                is_checked INTEGER NOT NULL DEFAULT 0,
                download_status TEXT NOT NULL DEFAULT 'NotQueued',
                download_error TEXT NULL,
                download_attempts INTEGER NOT NULL DEFAULT 0,
                metadata_status TEXT NOT NULL DEFAULT 'Pending',
                metadata_updated_at TEXT NULL,
                metadata_error TEXT NULL,
                metadata_attempts INTEGER NOT NULL DEFAULT 0,
                metadata_priority INTEGER NOT NULL DEFAULT 0,
                view_count INTEGER NULL,
                like_count INTEGER NULL,
                thumbnail_url TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_channel_videos_channel_checked
                ON channel_videos(channel_id, is_checked, uploaded_at);
            CREATE TABLE IF NOT EXISTS channel_notifications (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
                channel_video_id INTEGER NULL REFERENCES channel_videos(id) ON DELETE CASCADE,
                kind TEXT NOT NULL,
                title TEXT NOT NULL,
                created_at TEXT NOT NULL,
                read_at TEXT NULL,
                archived_at TEXT NULL,
                UNIQUE(channel_video_id, kind)
            );
            CREATE INDEX IF NOT EXISTS ix_channel_notifications_inbox
                ON channel_notifications(archived_at, read_at, created_at DESC);";
        cmd.ExecuteNonQuery();
        EnsureColumn(conn, "channel_videos", "download_status", "TEXT NOT NULL DEFAULT 'NotQueued'");
        EnsureColumn(conn, "channel_videos", "download_error", "TEXT NULL");
        EnsureColumn(conn, "channel_videos", "download_attempts", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(conn, "channel_videos", "manual_download_requested", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(conn, "channel_videos", "metadata_status", "TEXT NOT NULL DEFAULT 'Pending'");
        EnsureColumn(conn, "channel_videos", "metadata_updated_at", "TEXT NULL");
        EnsureColumn(conn, "channel_videos", "metadata_error", "TEXT NULL");
        EnsureColumn(conn, "channel_videos", "metadata_attempts", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(conn, "channel_videos", "metadata_priority", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(conn, "channel_videos", "view_count", "INTEGER NULL");
        EnsureColumn(conn, "channel_videos", "like_count", "INTEGER NULL");
        EnsureColumn(conn, "channel_videos", "thumbnail_url", "TEXT NULL");
        // Follow is the single subscription switch. Keep legacy notification
        // flags aligned with it so non-followed channels cannot notify.
        ExecuteNonQuery(conn, @"
            UPDATE channels
            SET inform_new_songs = subscribed
            WHERE inform_new_songs <> subscribed");
        ExecuteNonQuery(conn, @"
            UPDATE channels
            SET basic_metadata_checked_at = COALESCE(remote_metadata_updated_at, last_checked_at)
            WHERE basic_metadata_checked_at IS NULL
              AND source_channel_id IS NOT NULL
              AND thumbnail IS NOT NULL
              AND length(thumbnail) > 0
              AND (remote_metadata_updated_at IS NOT NULL OR last_checked_at IS NOT NULL)");
        BackfillLibraryChannelVideos(conn);
        BackfillTrackSourceMetadata(conn);
    }

    public void PrepareLibraryMetadataBackfill()
    {
        using var conn = Open();
        BackfillLibraryTrackChannels(conn);
        BackfillLibraryChannelVideos(conn);
        BackfillTrackSourceMetadata(conn);
    }

    private static void BackfillLibraryTrackChannels(SqliteConnection conn)
    {
        ExecuteNonQuery(conn, @"
            INSERT INTO channels
                (name, source_url, subscribed, inform_new_songs, created_at, updated_at)
            SELECT MIN(TRIM(tracks.channel_name)), TRIM(tracks.channel_url), 0, 0,
                   MIN(tracks.downloaded_at), MAX(tracks.updated_at)
            FROM tracks
            WHERE tracks.channel_id IS NULL
              AND tracks.channel_url IS NOT NULL
              AND TRIM(tracks.channel_url) <> ''
              AND tracks.channel_name IS NOT NULL
              AND TRIM(tracks.channel_name) <> ''
              AND NOT EXISTS (
                  SELECT 1 FROM channels
                  WHERE TRIM(channels.source_url) = TRIM(tracks.channel_url))
            GROUP BY TRIM(tracks.channel_url);

            UPDATE tracks
            SET channel_id = (
                SELECT channels.id
                FROM channels
                WHERE TRIM(channels.source_url) = TRIM(tracks.channel_url)
                ORDER BY channels.subscribed DESC, channels.id
                LIMIT 1)
            WHERE tracks.channel_id IS NULL
              AND tracks.channel_url IS NOT NULL
              AND TRIM(tracks.channel_url) <> ''
              AND EXISTS (
                  SELECT 1 FROM channels
                  WHERE TRIM(channels.source_url) = TRIM(tracks.channel_url));");
    }

    private static void BackfillTrackSourceMetadata(SqliteConnection conn)
    {
        ExecuteNonQuery(conn, @"
            UPDATE tracks
            SET source_video_id = COALESCE(source_video_id, (
                    SELECT CASE WHEN videos.video_id LIKE 'library-track-%' THEN NULL ELSE videos.video_id END
                    FROM channel_videos videos WHERE videos.canonical_url = tracks.canonical_url LIMIT 1)),
                view_count = COALESCE(view_count, (
                    SELECT videos.view_count FROM channel_videos videos
                    WHERE videos.canonical_url = tracks.canonical_url LIMIT 1)),
                like_count = COALESCE(like_count, (
                    SELECT videos.like_count FROM channel_videos videos
                    WHERE videos.canonical_url = tracks.canonical_url LIMIT 1)),
                source_thumbnail_url = COALESCE(source_thumbnail_url, (
                    SELECT videos.thumbnail_url FROM channel_videos videos
                    WHERE videos.canonical_url = tracks.canonical_url LIMIT 1)),
                source_metadata_updated_at = COALESCE(source_metadata_updated_at, (
                    SELECT videos.metadata_updated_at FROM channel_videos videos
                    WHERE videos.canonical_url = tracks.canonical_url AND videos.metadata_status = 'Ready' LIMIT 1))
            WHERE canonical_url IS NOT NULL
              AND EXISTS (SELECT 1 FROM channel_videos videos
                          WHERE videos.canonical_url = tracks.canonical_url)");
    }

    private static void BackfillLibraryChannelVideos(SqliteConnection conn)
    {
        using var insert = conn.CreateCommand();
        insert.CommandText = @"
            INSERT OR IGNORE INTO channel_videos
                (channel_id, video_id, canonical_url, title, duration_seconds, uploaded_at,
                 discovered_at, updated_at, is_checked, download_status, metadata_status)
            SELECT tracks.channel_id,
                   'library-track-' || tracks.id,
                   tracks.canonical_url,
                   tracks.title,
                   tracks.duration_seconds,
                   tracks.uploaded_at,
                   tracks.downloaded_at,
                   tracks.updated_at,
                   1,
                   'Ready',
                   'Pending'
            FROM tracks
            WHERE tracks.channel_id IS NOT NULL
              AND tracks.canonical_url IS NOT NULL
              AND TRIM(tracks.canonical_url) <> ''";
        insert.ExecuteNonQuery();

        using var resolveExisting = conn.CreateCommand();
        resolveExisting.CommandText = @"
            UPDATE channel_videos
            SET is_checked = CASE
                    WHEN video_id LIKE 'library-track-%'
                         OR EXISTS (
                             SELECT 1 FROM tracks
                             WHERE tracks.canonical_url = channel_videos.canonical_url
                               AND tracks.downloaded_at <= channel_videos.discovered_at)
                        THEN 1
                    ELSE is_checked
                END,
                download_status = 'Ready',
                download_error = NULL,
                metadata_status = CASE
                    WHEN channel_videos.metadata_status = 'Ready' THEN 'Ready'
                    WHEN channel_videos.metadata_status = 'Failed'
                         AND channel_videos.metadata_updated_at IS NOT NULL THEN 'Failed'
                    ELSE 'Pending'
                END,
                metadata_error = CASE
                    WHEN channel_videos.metadata_status = 'Failed' THEN channel_videos.metadata_error
                    ELSE NULL
                END,
                metadata_attempts = CASE
                    WHEN channel_videos.metadata_status IN ('Ready', 'Failed') THEN channel_videos.metadata_attempts
                    ELSE 0
                END,
                metadata_priority = 0
            WHERE EXISTS (
                SELECT 1
                FROM tracks
                WHERE tracks.canonical_url = channel_videos.canonical_url)
              AND ((is_checked <> 1
                    AND (video_id LIKE 'library-track-%'
                         OR EXISTS (
                             SELECT 1 FROM tracks
                             WHERE tracks.canonical_url = channel_videos.canonical_url
                               AND tracks.downloaded_at <= channel_videos.discovered_at)))
                   OR download_status <> 'Ready'
                   OR metadata_status IN ('Queued', 'Loading')
                   OR (metadata_status <> 'Failed' AND metadata_error IS NOT NULL)
                   OR metadata_priority <> 0)";
        resolveExisting.ExecuteNonQuery();
    }

    private static void CreateImportQueueSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS import_batches (
                id INTEGER PRIMARY KEY AUTOINCREMENT, source_url TEXT NOT NULL, created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS import_queue_items (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                batch_id INTEGER NOT NULL REFERENCES import_batches(id) ON DELETE CASCADE,
                source_url TEXT NOT NULL,
                canonical_url TEXT NOT NULL UNIQUE,
                title TEXT NOT NULL,
                duration_seconds INTEGER NULL,
                estimated_size_bytes INTEGER NULL,
                status TEXT NOT NULL,
                detail TEXT NULL,
                track_id INTEGER NULL REFERENCES tracks(id) ON DELETE SET NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_import_queue_status ON import_queue_items(status, created_at);";
        cmd.ExecuteNonQuery();
        EnsureImportVersionSchema(conn);
    }

    internal static void EnsureImportVersionSchema(SqliteConnection conn)
    {
        EnsureColumn(conn, "import_queue_items", "is_original", "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(conn, "import_queue_items", "parent_track_id", "INTEGER NULL REFERENCES tracks(id) ON DELETE SET NULL");
        EnsureColumn(conn, "import_queue_items", "edit_types", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(conn, "import_queue_items", "request_json", "TEXT NULL");
    }

    private static void EnsureColumn(SqliteConnection conn, string table, string column, string definition)
    {
        if (ColumnExists(conn, table, column)) return;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        cmd.ExecuteNonQuery();
    }

    private static bool ColumnExists(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static bool TableExists(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $table";
        cmd.Parameters.AddWithValue("$table", table);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private static long TableRowCount(SqliteConnection conn, string table)
    {
        if (!TableExists(conn, table))
            return 0;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)cmd.ExecuteScalar()!;
    }

    public void RecordTrackPlaybackStarted(int trackId)
    {
        using var conn = Open();
        ExecuteNonQuery(conn,
            "UPDATE tracks SET listen_count = listen_count + 1, last_listened_at = $now WHERE id = $trackId",
            ("$now", DateTime.UtcNow.ToString("O")), ("$trackId", trackId));
    }

    public void AddTrackListenedSeconds(int trackId, int seconds)
    {
        if (seconds <= 0) return;
        using var conn = Open();
        ExecuteNonQuery(conn,
            "UPDATE tracks SET listened_seconds = listened_seconds + $seconds WHERE id = $trackId",
            ("$seconds", seconds), ("$trackId", trackId));
    }

    public void RecordTrackSkip(int trackId)
    {
        using var conn = Open();
        ExecuteNonQuery(conn,
            "UPDATE tracks SET skip_count = skip_count + 1 WHERE id = $trackId",
            ("$trackId", trackId));
    }

    public TrackUsageStats GetTrackUsageStats(int trackId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT listen_count, listened_seconds, skip_count, last_listened_at
                            FROM tracks WHERE id = $trackId";
        cmd.Parameters.AddWithValue("$trackId", trackId);
        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? new TrackUsageStats(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.IsDBNull(3) ? null : reader.GetString(3))
            : new TrackUsageStats(0, 0, 0, null);
    }

    public Dictionary<int, TrackUsageStats> GetAllTrackUsageStats()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT id, listen_count, listened_seconds, skip_count, last_listened_at
                            FROM tracks";
        using var reader = cmd.ExecuteReader();
        var result = new Dictionary<int, TrackUsageStats>();
        while (reader.Read())
        {
            result[reader.GetInt32(0)] = new TrackUsageStats(
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4));
        }
        return result;
    }

    public TimeSpan? EstimateAnalysisDuration(int? trackDurationSeconds, long? fileSizeBytes)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT tracks.duration_seconds, tracks.file_size_bytes, analysis.analysis_duration_ms
                            FROM track_analysis analysis JOIN tracks ON tracks.id = analysis.track_id
                            WHERE analysis.analysis_duration_ms IS NOT NULL
                            ORDER BY analysis.analyzed_at DESC LIMIT 30";
        using var reader = cmd.ExecuteReader();
        var samples = new List<(int? Duration, long? Size, int Milliseconds)>();
        while (reader.Read())
            samples.Add((reader.IsDBNull(0) ? null : reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetInt32(2)));
        if (samples.Count == 0) return null;

        var estimates = new List<double> { samples.Average(sample => (double)sample.Milliseconds) };
        if (trackDurationSeconds is > 0)
        {
            var durationRates = samples.Where(sample => sample.Duration is > 0)
                .Select(sample => sample.Milliseconds / (double)sample.Duration!.Value).ToList();
            if (durationRates.Count > 0) estimates.Add(durationRates.Average() * trackDurationSeconds.Value);
        }
        if (fileSizeBytes is > 0)
        {
            var sizeRates = samples.Where(sample => sample.Size is > 0)
                .Select(sample => sample.Milliseconds / (sample.Size!.Value / 1_000_000d)).ToList();
            if (sizeRates.Count > 0) estimates.Add(sizeRates.Average() * (fileSizeBytes.Value / 1_000_000d));
        }
        return TimeSpan.FromMilliseconds(Math.Clamp(estimates.Average(), 3_000, 15 * 60_000));
    }

    public TimeSpan? EstimateDownloadDuration(int? trackDurationSeconds, long? fileSizeBytes)
    {
        if (trackDurationSeconds is not > 0 && fileSizeBytes is not > 0) return null;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT duration_seconds, file_size_bytes, download_duration_ms FROM tracks
                            WHERE download_duration_ms IS NOT NULL AND download_duration_ms > 0
                            ORDER BY downloaded_at DESC LIMIT 30";
        using var reader = cmd.ExecuteReader();
        var samples = new List<(int? Duration, long? Size, int Milliseconds)>();
        while (reader.Read())
            samples.Add((reader.IsDBNull(0) ? null : reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetInt32(2)));
        if (samples.Count == 0) return null;

        var estimates = new List<(double Value, double Weight)> { (samples.Average(sample => (double)sample.Milliseconds), .15) };
        if (trackDurationSeconds is > 0)
        {
            var rates = samples.Where(sample => sample.Duration is > 0)
                .Select(sample => sample.Milliseconds / (double)sample.Duration!.Value).ToList();
            if (rates.Count > 0) estimates.Add((rates.Average() * trackDurationSeconds.Value, .7));
        }
        if (fileSizeBytes is > 0)
        {
            var rates = samples.Where(sample => sample.Size is > 0)
                .Select(sample => sample.Milliseconds / (sample.Size!.Value / 1_000_000d)).ToList();
            if (rates.Count > 0) estimates.Add((rates.Average() * (fileSizeBytes.Value / 1_000_000d), .15));
        }
        var estimate = estimates.Sum(item => item.Value * item.Weight) / estimates.Sum(item => item.Weight);
        return TimeSpan.FromMilliseconds(Math.Clamp(estimate, 1_000, 60 * 60_000));
    }

    public int CreateImportBatch(string sourceUrl, IReadOnlyList<ImportPreviewItem> items, bool rejectDuplicates = false)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var now = DateTime.UtcNow.ToString("O");
        var batchId = InsertAndGetId(conn, tx,
            "INSERT INTO import_batches (source_url, created_at) VALUES ($sourceUrl, $createdAt)",
            ("$sourceUrl", sourceUrl), ("$createdAt", now));

        foreach (var item in items.Where(item => item.Status == ImportQueueStatus.Queued))
        {
            if (rejectDuplicates)
            {
                using var check = conn.CreateCommand();
                check.Transaction = tx;
                check.CommandText = @"SELECT EXISTS(SELECT 1 FROM tracks WHERE canonical_url = $url)
                    OR EXISTS(SELECT 1 FROM import_queue_items WHERE canonical_url = $url
                        AND status NOT IN ('Failed', 'Skipped'))";
                check.Parameters.AddWithValue("$url", item.CanonicalUrl);
                if ((long)check.ExecuteScalar()! != 0)
                    throw new InvalidOperationException("This track is already in your library or Current Queue.");
            }
            if (item.ParentTrackId is int parent)
            {
                using var check = conn.CreateCommand();
                check.Transaction = tx;
                check.CommandText = "SELECT COUNT(*) FROM tracks WHERE id = $parent AND is_original = 1";
                check.Parameters.AddWithValue("$parent", parent);
                if (item.IsOriginal || (long)check.ExecuteScalar()! != 1)
                    throw new InvalidOperationException("The original track is no longer available. Please reopen Add version.");
            }
            ExecuteInsert(conn, tx, @"
                INSERT INTO import_queue_items
                    (batch_id, source_url, canonical_url, title, duration_seconds, estimated_size_bytes, status, detail, created_at, updated_at,
                     is_original, parent_track_id, edit_types, request_json)
                VALUES ($batchId, $sourceUrl, $canonicalUrl, $title, $duration, $size, $status, $detail, $createdAt, $updatedAt,
                        $original, $parent, $types, $requestJson)
                ON CONFLICT(canonical_url) DO UPDATE SET
                    batch_id = excluded.batch_id,
                    source_url = excluded.source_url,
                    title = excluded.title,
                    duration_seconds = excluded.duration_seconds,
                    estimated_size_bytes = excluded.estimated_size_bytes,
                    status = excluded.status,
                    detail = excluded.detail,
                    track_id = NULL,
                    is_original = excluded.is_original,
                    parent_track_id = excluded.parent_track_id,
                    edit_types = excluded.edit_types,
                    request_json = excluded.request_json,
                    updated_at = excluded.updated_at
                WHERE import_queue_items.status IN ('Failed', 'Skipped')",
                ("$batchId", batchId), ("$sourceUrl", item.SourceUrl), ("$canonicalUrl", item.CanonicalUrl),
                ("$title", item.Title), ("$duration", item.DurationSeconds), ("$size", item.EstimatedSizeBytes),
                ("$status", ImportQueueStatus.Queued.ToString()), ("$detail", item.Detail),
                ("$createdAt", now), ("$updatedAt", now),
                ("$original", item.IsOriginal ? 1 : 0), ("$parent", item.ParentTrackId), ("$types", (int)item.EditTypes),
                ("$requestJson", item.Request is null ? null : JsonSerializer.Serialize(item.Request)));
        }
        ExecuteInsert(conn, tx, @"DELETE FROM import_batches
                                  WHERE NOT EXISTS (
                                      SELECT 1 FROM import_queue_items
                                      WHERE import_queue_items.batch_id = import_batches.id
                                  )");
        tx.Commit();
        return (int)batchId;
    }

    public void RequeueInterruptedImports()
    {
        using var conn = Open();
        ExecuteNonQuery(conn, @"UPDATE import_queue_items SET status = $queued,
                                detail = 'Interrupted — cleaned up and queued again',
                                track_id = NULL,
                                updated_at = $now
                                WHERE status IN ($downloading, $analyzing)",
            ("$queued", ImportQueueStatus.Queued.ToString()),
            ("$downloading", ImportQueueStatus.Downloading.ToString()),
            ("$analyzing", ImportQueueStatus.Analyzing.ToString()),
            ("$now", DateTime.UtcNow.ToString("O")));
    }

    public List<ImportQueueItem> GetInterruptedImportQueueItems()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT id, batch_id, source_url, canonical_url, title, duration_seconds, estimated_size_bytes,
                                   status, detail, track_id, is_original, parent_track_id, edit_types, request_json
                            FROM import_queue_items
                            WHERE status IN ($downloading, $analyzing)
                            ORDER BY created_at, id";
        cmd.Parameters.AddWithValue("$downloading", ImportQueueStatus.Downloading.ToString());
        cmd.Parameters.AddWithValue("$analyzing", ImportQueueStatus.Analyzing.ToString());
        using var reader = cmd.ExecuteReader();
        var items = new List<ImportQueueItem>();
        while (reader.Read()) items.Add(ReadImportQueueItem(reader));
        return items;
    }

    public ImportQueueItem? GetNextQueuedImport()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT id, batch_id, source_url, canonical_url, title, duration_seconds, estimated_size_bytes,
                                   status, detail, track_id, is_original, parent_track_id, edit_types, request_json
                            FROM import_queue_items WHERE status = $status ORDER BY created_at, id LIMIT 1";
        cmd.Parameters.AddWithValue("$status", ImportQueueStatus.Queued.ToString());
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadImportQueueItem(reader) : null;
    }

    public ImportQueueItem? ClaimNextQueuedImport()
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        ImportQueueItem? item;
        using (var select = conn.CreateCommand())
        {
            select.Transaction = tx;
            select.CommandText = @"SELECT id, batch_id, source_url, canonical_url, title, duration_seconds,
                                          estimated_size_bytes, status, detail, track_id, is_original, parent_track_id, edit_types, request_json
                                   FROM import_queue_items
                                   WHERE status = $queued
                                   ORDER BY created_at, id
                                   LIMIT 1";
            select.Parameters.AddWithValue("$queued", ImportQueueStatus.Queued.ToString());
            using var reader = select.ExecuteReader();
            item = reader.Read() ? ReadImportQueueItem(reader) : null;
        }

        if (item is null)
        {
            tx.Commit();
            return null;
        }

        using var claim = conn.CreateCommand();
        claim.Transaction = tx;
        claim.CommandText = @"UPDATE import_queue_items
                              SET status = $downloading, detail = $detail, updated_at = $now
                              WHERE id = $id AND status = $queued";
        claim.Parameters.AddWithValue("$downloading", ImportQueueStatus.Downloading.ToString());
        claim.Parameters.AddWithValue("$detail", "Checking download details…");
        claim.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        claim.Parameters.AddWithValue("$id", item.Id);
        claim.Parameters.AddWithValue("$queued", ImportQueueStatus.Queued.ToString());
        var claimed = claim.ExecuteNonQuery() == 1;
        tx.Commit();
        return claimed
            ? item with
            {
                Status = ImportQueueStatus.Downloading,
                Detail = "Checking download details…"
            }
            : null;
    }

    public void UpdateImportQueueItem(int id, ImportQueueStatus status, string? detail = null, int? trackId = null)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        ImportQueueStatus currentStatus;
        using (var current = conn.CreateCommand())
        {
            current.Transaction = tx;
            current.CommandText = "SELECT status FROM import_queue_items WHERE id = $id";
            current.Parameters.AddWithValue("$id", id);
            var value = current.ExecuteScalar() as string
                ?? throw new InvalidOperationException($"Import queue item {id} no longer exists.");
            if (!Enum.TryParse(value, ignoreCase: true, out currentStatus))
                throw new InvalidOperationException($"Import queue item {id} has unknown status '{value}'.");
        }

        if (!ImportQueueStateMachine.CanTransition(currentStatus, status))
            throw new InvalidOperationException($"Invalid import transition {currentStatus} → {status} for item {id}.");

        ExecuteInsert(conn, tx, @"UPDATE import_queue_items SET status = $status, detail = $detail,
                                  track_id = COALESCE($trackId, track_id), updated_at = $updatedAt WHERE id = $id",
            ("$status", status.ToString()), ("$detail", detail), ("$trackId", trackId),
            ("$updatedAt", DateTime.UtcNow.ToString("O")), ("$id", id));
        tx.Commit();
    }

    public void CompleteImportQueueItem(int itemId, int trackId)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var now = DateTime.UtcNow.ToString("O");

        using (var track = conn.CreateCommand())
        {
            track.Transaction = tx;
            track.CommandText = @"UPDATE tracks
                                  SET library_state = CASE
                                          WHEN rating_id IS NULL THEN 'PendingRating'
                                          ELSE 'Active'
                                      END,
                                      needs_reevaluation = CASE
                                          WHEN rating_id IS NULL THEN 1
                                          ELSE needs_reevaluation
                                      END,
                                      updated_at = $now
                                  WHERE id = $trackId";
            track.Parameters.AddWithValue("$trackId", trackId);
            track.Parameters.AddWithValue("$now", now);
            if (track.ExecuteNonQuery() != 1)
                throw new InvalidOperationException($"Cannot complete import {itemId}: track {trackId} does not exist.");
        }

        int batchId;
        using (var queueItem = conn.CreateCommand())
        {
            queueItem.Transaction = tx;
            queueItem.CommandText = "SELECT batch_id FROM import_queue_items WHERE id = $id";
            queueItem.Parameters.AddWithValue("$id", itemId);
            var value = queueItem.ExecuteScalar();
            if (value is not long storedBatchId)
                throw new InvalidOperationException($"Cannot complete import: queue item {itemId} does not exist.");
            batchId = (int)storedBatchId;
        }

        ExecuteInsert(conn, tx, "DELETE FROM import_queue_items WHERE id = $id", ("$id", itemId));
        ExecuteInsert(conn, tx, @"DELETE FROM import_batches
                                  WHERE id = $batchId
                                    AND NOT EXISTS (SELECT 1 FROM import_queue_items WHERE batch_id = $batchId)",
            ("$batchId", batchId));
        tx.Commit();
    }

    public ImportQueueSummary GetImportQueueSummary()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT status, COUNT(*) FROM import_queue_items
                            WHERE status IN ($queued, $downloading, $analyzing) GROUP BY status";
        cmd.Parameters.AddWithValue("$queued", ImportQueueStatus.Queued.ToString());
        cmd.Parameters.AddWithValue("$downloading", ImportQueueStatus.Downloading.ToString());
        cmd.Parameters.AddWithValue("$analyzing", ImportQueueStatus.Analyzing.ToString());
        var counts = new Dictionary<string, int>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) counts[reader.GetString(0)] = reader.GetInt32(1);
        return new ImportQueueSummary(
            counts.GetValueOrDefault(ImportQueueStatus.Queued.ToString()),
            counts.GetValueOrDefault(ImportQueueStatus.Downloading.ToString()),
            counts.GetValueOrDefault(ImportQueueStatus.Analyzing.ToString()),
            0);
    }

    public HashSet<string> GetActiveImportCanonicalUrls()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT canonical_url FROM import_queue_items
                            WHERE status IN ($queued, $downloading, $analyzing)";
        cmd.Parameters.AddWithValue("$queued", ImportQueueStatus.Queued.ToString());
        cmd.Parameters.AddWithValue("$downloading", ImportQueueStatus.Downloading.ToString());
        cmd.Parameters.AddWithValue("$analyzing", ImportQueueStatus.Analyzing.ToString());
        using var reader = cmd.ExecuteReader();
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) urls.Add(reader.GetString(0));
        return urls;
    }

    public List<ImportQueueSource> GetImportQueueSources()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT id, batch_id, source_url, canonical_url, title, duration_seconds, estimated_size_bytes,
                                   status, detail, track_id, is_original, parent_track_id, edit_types, request_json
                            FROM import_queue_items
                            WHERE status IN ($queued, $downloading, $analyzing, $failed)
                            ORDER BY CASE status
                                         WHEN $downloading THEN 0
                                         WHEN $analyzing THEN 0
                                         WHEN $queued THEN 1
                                         ELSE 2
                                     END,
                                     batch_id DESC, created_at, id";
        cmd.Parameters.AddWithValue("$queued", ImportQueueStatus.Queued.ToString());
        cmd.Parameters.AddWithValue("$downloading", ImportQueueStatus.Downloading.ToString());
        cmd.Parameters.AddWithValue("$analyzing", ImportQueueStatus.Analyzing.ToString());
        cmd.Parameters.AddWithValue("$failed", ImportQueueStatus.Failed.ToString());
        using var reader = cmd.ExecuteReader();
        var items = new List<ImportQueueItem>();
        while (reader.Read()) items.Add(ReadImportQueueItem(reader));
        return items.GroupBy(item => item.SourceUrl, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ImportQueueSource(group.Key, group.ToList())).ToList();
    }

    public bool RemoveQueuedImport(int id)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var batchId = GetImportBatchId(
            conn,
            tx,
            id,
            "status IN ($queued, $failed)",
            ("$queued", ImportQueueStatus.Queued.ToString()),
            ("$failed", ImportQueueStatus.Failed.ToString()));
        if (batchId is null)
        {
            tx.Commit();
            return false;
        }

        DeleteImportQueueItem(conn, tx, id, batchId.Value);
        tx.Commit();
        return true;
    }

    public void DeleteImportQueueItem(int id)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var batchId = GetImportBatchId(conn, tx, id, "1 = 1");
        if (batchId is not null)
            DeleteImportQueueItem(conn, tx, id, batchId.Value);
        tx.Commit();
    }

    private static int? GetImportBatchId(
        SqliteConnection conn,
        SqliteTransaction tx,
        int itemId,
        string predicate,
        params (string Name, object? Value)[] parameters)
    {
        using var command = conn.CreateCommand();
        command.Transaction = tx;
        command.CommandText = $"SELECT batch_id FROM import_queue_items WHERE id = $id AND {predicate}";
        command.Parameters.AddWithValue("$id", itemId);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command.ExecuteScalar() is long batchId ? (int)batchId : null;
    }

    private static void DeleteImportQueueItem(
        SqliteConnection conn,
        SqliteTransaction tx,
        int itemId,
        int batchId)
    {
        ExecuteInsert(conn, tx, "DELETE FROM import_queue_items WHERE id = $id", ("$id", itemId));
        ExecuteInsert(conn, tx, @"DELETE FROM import_batches
                                  WHERE id = $batchId
                                    AND NOT EXISTS (
                                        SELECT 1 FROM import_queue_items WHERE batch_id = $batchId
                                    )",
            ("$batchId", batchId));
    }

    private static ImportQueueItem ReadImportQueueItem(SqliteDataReader reader) => new(
        reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetInt64(6),
        Enum.Parse<ImportQueueStatus>(reader.GetString(7)), reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetInt32(9),
        reader.GetInt32(10) != 0,
        reader.IsDBNull(11) ? null : reader.GetInt32(11),
        (TrackEditTypes)reader.GetInt32(12),
        reader.FieldCount > 13 && !reader.IsDBNull(13)
            ? JsonSerializer.Deserialize<DownloadRequest>(reader.GetString(13))
            : null);

    private static void SeedDefaultMetadata(SqliteConnection conn, SqliteTransaction tx)
    {
        foreach (var rating in ReadAsset<RatingSeedDocument>("default-ratings.json").Ratings)
        {
            ExecuteInsert(conn, tx,
                "INSERT INTO ratings (name, sort_order) VALUES ($name, $sortOrder)",
                ("$name", rating.Name), ("$sortOrder", rating.SortOrder));
        }

        SeedDefaultStyles(conn, tx);

        SynchronizeModelMetadata(conn, tx);
    }

    private static void SeedDefaultStyles(SqliteConnection conn, SqliteTransaction tx)
    {
        foreach (var name in DefaultStyleNames)
            ExecuteInsert(conn, tx,
                "INSERT OR IGNORE INTO style_definitions (name) VALUES ($name)",
                ("$name", name));
    }

    /// <summary>
    /// Imports the human-authored MAEST vocabulary unchanged for a newly created or not-yet-migrated database.
    /// </summary>
    private static void SynchronizeModelMetadata(SqliteConnection conn, SqliteTransaction tx)
    {
        var metadata = ReadAsset<List<ModelSubgenreMetadataSeed>>("Models/maest-genre-metadata.json");
        var genreIds = new Dictionary<string, long>(StringComparer.Ordinal);
        var subgenreIds = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var item in metadata)
        {
            if (!genreIds.TryGetValue(item.Genre, out var genreId))
            {
                ExecuteInsert(conn, tx, "INSERT OR IGNORE INTO model_genres (name) VALUES ($name)", ("$name", item.Genre));
                genreId = SelectId(conn, tx, "SELECT id FROM model_genres WHERE name = $name", ("$name", item.Genre));
                genreIds.Add(item.Genre, genreId);
            }

            ExecuteInsert(conn, tx, @"INSERT INTO model_subgenres
                    (model_genre_id, name, description, classification_hint, bpm_min, bpm_max)
                VALUES ($genreId, $name, $description, $hint, $bpmMin, $bpmMax)
                ON CONFLICT(model_genre_id, name) DO UPDATE SET
                    description = excluded.description,
                    classification_hint = excluded.classification_hint,
                    bpm_min = excluded.bpm_min,
                    bpm_max = excluded.bpm_max",
                ("$genreId", genreId), ("$name", item.Subgenre), ("$description", item.Description),
                ("$hint", item.ClassificationHint), ("$bpmMin", item.BpmMin), ("$bpmMax", item.BpmMax));

            subgenreIds[item.Label] = SelectId(conn, tx,
                "SELECT id FROM model_subgenres WHERE model_genre_id = $genreId AND name = $name",
                ("$genreId", genreId), ("$name", item.Subgenre));
        }

        ExecuteInsert(conn, tx, "DELETE FROM model_subgenre_distinctions");
        foreach (var item in metadata)
        {
            if (!subgenreIds.TryGetValue(item.Label, out var sourceId)) continue;
            foreach (var distinction in item.DistinguishFrom)
            {
                if (!subgenreIds.TryGetValue(distinction.Label, out var targetId)) continue;
                ExecuteInsert(conn, tx, @"INSERT OR REPLACE INTO model_subgenre_distinctions
                        (model_subgenre_id, distinguish_from_model_subgenre_id, difference)
                    VALUES ($sourceId, $targetId, $difference)",
                    ("$sourceId", sourceId), ("$targetId", targetId), ("$difference", distinction.Difference));
            }
        }
    }

    public bool TrackExists(string canonicalUrl)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM tracks WHERE canonical_url = $url";
        cmd.Parameters.AddWithValue("$url", canonicalUrl);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    public ChannelRefreshResult SaveChannelSnapshot(YouTubeChannelSnapshot snapshot)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var now = DateTime.UtcNow.ToString("O");
        var channelId = FindChannelId(conn, tx, snapshot.ChannelId, snapshot.SourceUrl);
        var hadSnapshot = channelId is long existingChannelId
            && SelectId(conn, tx, @"
                SELECT CASE WHEN last_checked_at IS NOT NULL
                                  AND EXISTS (SELECT 1 FROM channel_videos WHERE channel_id = channels.id)
                            THEN 1 ELSE 0 END
                FROM channels WHERE id = $id", ("$id", existingChannelId)) != 0;

        if (channelId is null)
        {
            channelId = InsertAndGetId(conn, tx, @"
                INSERT INTO channels
                    (name, source_channel_id, source_url, subscribed, created_at, updated_at,
                     last_checked_at, video_count, thumbnail_url, thumbnail, follower_count,
                     remote_metadata_updated_at, basic_metadata_checked_at)
                VALUES
                    ($name, $sourceChannelId, $sourceUrl, 1, $now, $now, $now, $videoCount, $thumbnailUrl, $thumbnail,
                     $followerCount, $now, $now)",
                ("$name", snapshot.Name),
                ("$sourceChannelId", snapshot.ChannelId),
                ("$sourceUrl", snapshot.ChannelUrl ?? snapshot.SourceUrl),
                ("$thumbnailUrl", snapshot.ThumbnailUrl),
                ("$thumbnail", snapshot.Thumbnail),
                ("$followerCount", snapshot.FollowerCount),
                ("$now", now),
                ("$videoCount", snapshot.Videos.Count));
        }
        else
        {
            ExecuteInsert(conn, tx, @"
                UPDATE channels
                SET name = $name,
                    source_channel_id = COALESCE($sourceChannelId, source_channel_id),
                    source_url = COALESCE($sourceUrl, source_url),
                    updated_at = $now,
                    last_checked_at = $now,
                    video_count = $videoCount
                    , thumbnail_url = COALESCE($thumbnailUrl, thumbnail_url)
                    , thumbnail = COALESCE($thumbnail, thumbnail)
                    , follower_count = COALESCE($followerCount, follower_count)
                    , remote_metadata_updated_at = $now
                    , basic_metadata_checked_at = $now
                WHERE id = $id",
                ("$id", channelId.Value),
                ("$name", snapshot.Name),
                ("$sourceChannelId", snapshot.ChannelId),
                ("$sourceUrl", snapshot.ChannelUrl ?? snapshot.SourceUrl),
                ("$thumbnailUrl", snapshot.ThumbnailUrl),
                ("$thumbnail", snapshot.Thumbnail),
                ("$followerCount", snapshot.FollowerCount),
                ("$now", now),
                ("$videoCount", snapshot.Videos.Count));
        }

        var added = 0;
        var updated = 0;
        foreach (var video in snapshot.Videos)
        {
            var existed = ChannelVideoExists(conn, tx, video.CanonicalUrl);
            ExecuteInsert(conn, tx, @"
                INSERT INTO channel_videos
                    (channel_id, video_id, canonical_url, title, duration_seconds, uploaded_at, discovered_at, updated_at,
                     is_checked, download_status)
                VALUES
                    ($channelId, $videoId, $canonicalUrl, $title, $duration, $uploadedAt, $now, $now, 0,
                     CASE WHEN EXISTS (SELECT 1 FROM tracks WHERE tracks.canonical_url = $canonicalUrl)
                          THEN 'Ready' ELSE 'NotQueued' END)
                ON CONFLICT(canonical_url) DO UPDATE SET
                    channel_id = excluded.channel_id,
                    video_id = excluded.video_id,
                    title = excluded.title,
                    duration_seconds = excluded.duration_seconds,
                    uploaded_at = COALESCE(excluded.uploaded_at, channel_videos.uploaded_at),
                    updated_at = excluded.updated_at,
                    metadata_status = channel_videos.metadata_status,
                    metadata_error = channel_videos.metadata_error,
                    metadata_attempts = channel_videos.metadata_attempts,
                    download_status = CASE
                        WHEN EXISTS (SELECT 1 FROM tracks WHERE tracks.canonical_url = excluded.canonical_url)
                            THEN 'Ready'
                        ELSE 'NotQueued'
                    END",
                ("$channelId", channelId.Value),
                ("$videoId", video.VideoId),
                ("$canonicalUrl", video.CanonicalUrl),
                ("$title", video.Title),
                ("$duration", video.DurationSeconds),
                ("$uploadedAt", video.UploadedAt),
                ("$now", now));
            if (existed) updated++;
            else
            {
                added++;
                if (hadSnapshot)
                {
                    ExecuteInsert(conn, tx, @"
                        INSERT OR IGNORE INTO channel_notifications
                            (channel_id, channel_video_id, kind, title, created_at)
                        SELECT $channelId, id, 'NewVideo', title, $now
                        FROM channel_videos
                        WHERE canonical_url = $canonicalUrl
                          AND EXISTS (SELECT 1 FROM channels
                                      WHERE id = $channelId AND inform_new_songs = 1)",
                        ("$channelId", channelId.Value), ("$canonicalUrl", video.CanonicalUrl), ("$now", now));
                }
            }
        }

        tx.Commit();
        return new ChannelRefreshResult(true, added, updated);
    }

    public List<ChannelSubscription> GetChannelSubscriptions()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT channels.id, channels.name, channels.source_url, channels.source_channel_id,
                   channels.last_checked_at, COALESCE(COUNT(channel_videos.id), 0),
                   COALESCE(SUM(CASE WHEN channel_videos.is_checked = 0 THEN 1 ELSE 0 END), 0)
            FROM channels
            LEFT JOIN channel_videos ON channel_videos.channel_id = channels.id
            WHERE channels.subscribed = 1
            GROUP BY channels.id
            ORDER BY channels.name COLLATE NOCASE";
        using var reader = cmd.ExecuteReader();
        var channels = new List<ChannelSubscription>();
        while (reader.Read())
            channels.Add(new ChannelSubscription(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                ReadInt32(reader, 5),
                ReadInt32(reader, 6)));
        return channels;
    }

    public List<ChannelHubItem> GetChannelHubItems()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            WITH track_stats AS (
                SELECT tracks.channel_id,
                       COUNT(*) AS track_count,
                       SUM(CASE WHEN tracks.rating_id IS NOT NULL THEN 1 ELSE 0 END) AS rated_count,
                       AVG(CASE WHEN tracks.rating_id IS NOT NULL THEN ratings.sort_order END) AS average_rating,
                       SUM(CASE WHEN ratings.name = 'Timeless' THEN 1 ELSE 0 END) AS timeless_count,
                       SUM(CASE WHEN ratings.sort_order >= 4 THEN 1 ELSE 0 END) AS great_count,
                       SUM(tracks.listen_count) AS play_count,
                       SUM(tracks.skip_count) AS skip_count,
                       MAX(tracks.downloaded_at) AS last_downloaded_at
                FROM tracks
                LEFT JOIN ratings ON ratings.id = tracks.rating_id
                WHERE tracks.channel_id IS NOT NULL
                  AND tracks.library_state <> 'Rejected'
                GROUP BY tracks.channel_id
            ),
            video_stats AS (
                SELECT videos.channel_id,
                       COUNT(*) AS video_count,
                       SUM(CASE WHEN videos.is_checked = 0 THEN 1 ELSE 0 END) AS unchecked_count,
                       SUM(CASE WHEN tracks.id IS NULL
                                     AND videos.download_status <> 'Failed'
                                     AND videos.metadata_status <> 'Failed'
                                THEN 1 ELSE 0 END) AS available_count,
                       SUM(CASE WHEN tracks.id IS NOT NULL
                                     AND tracks.library_state = 'PendingRating'
                                     AND videos.is_checked = 0
                                THEN 1 ELSE 0 END) AS review_count,
                       SUM(videos.view_count) AS total_view_count
                FROM channel_videos videos
                LEFT JOIN tracks ON tracks.canonical_url = videos.canonical_url
                GROUP BY videos.channel_id
            )
            SELECT channels.id,
                   channels.name,
                   COALESCE(channels.source_url, ''),
                   channels.source_channel_id,
                   channels.subscribed,
                   channels.inform_new_songs,
                   channels.last_checked_at,
                   COALESCE(track_stats.track_count, 0),
                   COALESCE(track_stats.rated_count, 0),
                   track_stats.average_rating,
                   COALESCE(track_stats.timeless_count, 0),
                   COALESCE(track_stats.great_count, 0),
                   COALESCE(track_stats.play_count, 0),
                   COALESCE(track_stats.skip_count, 0),
                   track_stats.last_downloaded_at,
                   COALESCE(video_stats.video_count, 0),
                   COALESCE(video_stats.unchecked_count, 0),
                   COALESCE(video_stats.available_count, 0),
                   COALESCE(video_stats.review_count, 0),
                   channels.follower_count,
                   video_stats.total_view_count,
                   channels.thumbnail,
                   channels.basic_metadata_checked_at
            FROM channels
            LEFT JOIN track_stats ON track_stats.channel_id = channels.id
            LEFT JOIN video_stats ON video_stats.channel_id = channels.id
            WHERE channels.subscribed = 1
               OR EXISTS (SELECT 1 FROM tracks
                          WHERE tracks.channel_id = channels.id
                            AND tracks.library_state <> 'Rejected')
            ORDER BY channels.name COLLATE NOCASE";

        using var reader = cmd.ExecuteReader();
        var rows = new List<ChannelHubItem>();
        while (reader.Read())
            rows.Add(new ChannelHubItem(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                ReadInt32(reader, 4) != 0,
                ReadInt32(reader, 5) != 0,
                reader.IsDBNull(6) ? null : reader.GetString(6),
                ReadInt32(reader, 7),
                ReadInt32(reader, 8),
                reader.IsDBNull(9) ? null : reader.GetDouble(9),
                ReadInt32(reader, 10),
                ReadInt32(reader, 11),
                ReadInt32(reader, 12),
                ReadInt32(reader, 13),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                ReadInt32(reader, 15),
                ReadInt32(reader, 16),
                ReadInt32(reader, 17),
                ReadInt32(reader, 18),
                reader.IsDBNull(19) ? null : reader.GetInt64(19),
                reader.IsDBNull(20) ? null : reader.GetInt64(20),
                [],
                reader.IsDBNull(21) ? null : (byte[])reader.GetValue(21),
                reader.IsDBNull(22) ? null : reader.GetString(22)));
        reader.Close();

        using var tracks = conn.CreateCommand();
        tracks.CommandText = @"
            SELECT tracks.channel_id, tracks.title
            FROM tracks
            LEFT JOIN ratings ON ratings.id = tracks.rating_id
            WHERE tracks.channel_id IS NOT NULL
              AND tracks.library_state = 'Active'
            ORDER BY tracks.channel_id,
                     COALESCE(ratings.sort_order, 0) DESC,
                     tracks.listen_count DESC,
                     tracks.downloaded_at DESC";
        using var trackReader = tracks.ExecuteReader();
        var topTracks = new Dictionary<int, List<string>>();
        while (trackReader.Read())
        {
            var channelId = trackReader.GetInt32(0);
            if (!topTracks.TryGetValue(channelId, out var titles))
                topTracks[channelId] = titles = [];
            if (titles.Count < 3)
                titles.Add(trackReader.GetString(1));
        }

        return rows
            .Select(channel => channel with
            {
                TopTracks = topTracks.TryGetValue(channel.Id, out var titles) ? titles : []
            })
            .ToList();
    }

    public void SetChannelFollowed(int channelId, bool followed)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var now = DateTime.UtcNow.ToString("O");
        ExecuteInsert(conn, tx, @"
            UPDATE channels
            SET subscribed = $followed,
                inform_new_songs = $followed,
                updated_at = $now
            WHERE id = $id",
            ("$id", channelId), ("$followed", followed ? 1 : 0), ("$now", now));
        if (!followed)
        {
            ExecuteInsert(conn, tx, @"
                UPDATE channel_videos
                SET metadata_status = 'Pending', metadata_error = NULL, metadata_priority = 0
                WHERE channel_id = $id AND metadata_status = 'Queued'",
                ("$id", channelId));
        }
        tx.Commit();
    }

    public void MarkChannelBasicMetadataChecked(int channelId)
    {
        using var conn = Open();
        ExecuteNonQuery(conn, @"
            UPDATE channels
            SET basic_metadata_checked_at = $now, updated_at = $now
            WHERE id = $id",
            ("$id", channelId), ("$now", DateTime.UtcNow.ToString("O")));
    }

    public List<ChannelNotification> GetChannelNotifications()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT notifications.id, notifications.channel_id, notifications.channel_video_id,
                   channels.name, notifications.title, notifications.created_at,
                   notifications.read_at IS NOT NULL, videos.canonical_url
            FROM channel_notifications notifications
            JOIN channels ON channels.id = notifications.channel_id
            LEFT JOIN channel_videos videos ON videos.id = notifications.channel_video_id
            WHERE notifications.archived_at IS NULL
            ORDER BY notifications.read_at IS NULL DESC, notifications.created_at DESC, notifications.id DESC";
        using var reader = cmd.ExecuteReader();
        var notifications = new List<ChannelNotification>();
        while (reader.Read())
            notifications.Add(new ChannelNotification(
                reader.GetInt32(0), reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetBoolean(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
        return notifications;
    }

    public int GetUnreadChannelNotificationCount()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT COUNT(*) FROM channel_notifications
                            WHERE archived_at IS NULL AND read_at IS NULL";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void MarkChannelNotificationRead(int notificationId)
    {
        using var conn = Open();
        ExecuteNonQuery(conn, @"UPDATE channel_notifications
                                SET read_at = COALESCE(read_at, $now) WHERE id = $id",
            ("$id", notificationId), ("$now", DateTime.UtcNow.ToString("O")));
    }

    public void ArchiveChannelNotification(int notificationId)
    {
        using var conn = Open();
        ExecuteNonQuery(conn, @"UPDATE channel_notifications
                                SET archived_at = $now, read_at = COALESCE(read_at, $now) WHERE id = $id",
            ("$id", notificationId), ("$now", DateTime.UtcNow.ToString("O")));
    }

    public List<ChannelVideo> GetChannelVideos(int channelId, bool uncheckedFirst = true)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT videos.id, videos.channel_id, videos.video_id, videos.canonical_url, videos.title,
                   videos.duration_seconds, videos.uploaded_at, videos.discovered_at, videos.is_checked,
                   videos.download_status, videos.download_error, videos.download_attempts, tracks.id,
                   videos.metadata_status, videos.metadata_updated_at, videos.metadata_error,
                   videos.metadata_attempts, videos.view_count, videos.like_count, videos.thumbnail_url,
                   tracks.library_state, channels.name, ratings.name, ratings.sort_order,
                   COALESCE(tracks.listen_count, 0), COALESCE(tracks.listened_seconds, 0),
                   COALESCE(tracks.skip_count, 0)
            FROM channel_videos videos
            JOIN channels ON channels.id = videos.channel_id
            LEFT JOIN tracks ON tracks.canonical_url = videos.canonical_url
            LEFT JOIN ratings ON ratings.id = tracks.rating_id
            WHERE videos.channel_id = $channelId
            ORDER BY {(uncheckedFirst ? "videos.is_checked ASC," : string.Empty)}
                     COALESCE(videos.uploaded_at, videos.discovered_at) DESC,
                     videos.id DESC";
        cmd.Parameters.AddWithValue("$channelId", channelId);
        using var reader = cmd.ExecuteReader();
        var videos = new List<ChannelVideo>();
        while (reader.Read())
            videos.Add(ReadChannelVideo(reader));
        return videos;
    }

    public void RecoverChannelMetadataQueue()
    {
        using var conn = Open();
        ExecuteNonQuery(conn, @"
            UPDATE channel_videos
            SET metadata_status = 'Pending', metadata_error = NULL, metadata_priority = 0
            WHERE metadata_status IN ('Queued', 'Loading')");
    }

    public void EnsureChannelMetadataQueueIndexes()
    {
        using var conn = Open();
        ExecuteNonQuery(conn, @"CREATE INDEX IF NOT EXISTS ix_channel_videos_metadata_queue
                                ON channel_videos(metadata_status, metadata_updated_at, id DESC)");
        ExecuteNonQuery(conn, @"CREATE INDEX IF NOT EXISTS ix_tracks_channel_state
                                ON tracks(channel_id, library_state)");
    }

    public int QueueChannelVideoMetadata(int channelId, int limit)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE channel_videos
            SET metadata_status = 'Queued', metadata_error = NULL, metadata_priority = 100
            WHERE id IN (
                SELECT videos.id
                FROM channel_videos videos
                LEFT JOIN tracks ON tracks.canonical_url = videos.canonical_url
                WHERE videos.channel_id = $channelId
                  AND EXISTS (SELECT 1 FROM channels
                              WHERE channels.id = videos.channel_id AND channels.subscribed = 1)
                  AND (tracks.id IS NULL OR tracks.source_metadata_updated_at IS NULL)
                  AND videos.metadata_status = 'Pending'
                  AND NOT EXISTS (
                      SELECT 1 FROM channel_videos active
                      WHERE active.channel_id = $channelId
                        AND active.metadata_status IN ('Queued', 'Loading'))
                ORDER BY COALESCE(videos.uploaded_at, videos.discovered_at) DESC,
                         videos.id DESC
                LIMIT $limit
            )";
        cmd.Parameters.AddWithValue("$channelId", channelId);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
        return cmd.ExecuteNonQuery();
    }

    public int QueueAllChannelVideoMetadata(int channelId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE channel_videos
            SET metadata_status = 'Queued', metadata_error = NULL, metadata_priority = 150
            WHERE channel_id = $channelId
              AND metadata_status = 'Pending'
              AND NOT EXISTS (
                  SELECT 1 FROM tracks
                  WHERE tracks.canonical_url = channel_videos.canonical_url
                    AND tracks.source_metadata_updated_at IS NOT NULL)";
        cmd.Parameters.AddWithValue("$channelId", channelId);
        return cmd.ExecuteNonQuery();
    }

    public bool QueueSpecificChannelVideoMetadata(int videoId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE channel_videos
            SET metadata_status = 'Queued', metadata_error = NULL, metadata_priority = 200
            WHERE id = $videoId AND metadata_status IN ('Pending', 'Failed')
              AND EXISTS (SELECT 1 FROM channels
                          WHERE channels.id = channel_videos.channel_id AND channels.subscribed = 1)";
        cmd.Parameters.AddWithValue("$videoId", videoId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public int QueueBackgroundChannelVideoMetadata(int limit)
    {
        limit = Math.Clamp(limit, 1, 10);
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var activeCommand = conn.CreateCommand();
        activeCommand.Transaction = tx;
        activeCommand.CommandText = @"SELECT COUNT(*) FROM channel_videos videos
                                      LEFT JOIN tracks ON tracks.canonical_url = videos.canonical_url
                                      WHERE videos.metadata_status IN ('Queued', 'Loading')
                                        AND (tracks.id IS NOT NULL OR EXISTS (
                                            SELECT 1 FROM channels
                                            WHERE channels.id = videos.channel_id
                                              AND channels.subscribed = 1))";
        var remaining = Math.Max(0, limit - Convert.ToInt32(activeCommand.ExecuteScalar()));
        if (remaining == 0)
        {
            tx.Commit();
            return 0;
        }

        const string followed = @"EXISTS (SELECT 1 FROM channels followed_channel
                                           WHERE followed_channel.id = videos.channel_id
                                             AND followed_channel.subscribed = 1)";
        const string missingLibraryMetadata = @"EXISTS (SELECT 1 FROM tracks library_track
                                                WHERE library_track.canonical_url = videos.canonical_url
                                                  AND library_track.source_metadata_updated_at IS NULL)";
        var eligible = $"({followed} OR {missingLibraryMetadata})";
        var queued = QueueMetadataTiers(conn, tx,
            $"videos.metadata_status = 'Pending' AND {eligible}", remaining);
        remaining -= queued;
        if (remaining > 0)
            queued += QueueMetadataTiers(conn, tx,
                $"videos.metadata_status = 'Ready' AND (videos.metadata_updated_at IS NULL OR videos.metadata_updated_at < datetime('now', '-30 days')) AND {followed}",
                remaining);

        tx.Commit();
        return queued;
    }

    private static int QueueMetadataTiers(
        SqliteConnection conn,
        SqliteTransaction tx,
        string candidateCondition,
        int limit)
    {
        var queued = 0;
        var tiers = new (int Priority, string Condition)[]
        {
            (20, @"EXISTS (SELECT 1 FROM tracks t
                           WHERE t.canonical_url = videos.canonical_url
                             AND t.source_metadata_updated_at IS NULL)"),
            (10, "EXISTS (SELECT 1 FROM channels c WHERE c.id = videos.channel_id AND c.subscribed = 1)"),
            (5, @"EXISTS (SELECT 1 FROM tracks t
                          WHERE t.channel_id = videos.channel_id AND t.library_state = 'Active')"),
            (1, "1 = 1")
        };

        foreach (var (priority, tierCondition) in tiers)
        {
            var remaining = limit - queued;
            if (remaining <= 0)
                break;
            using var command = conn.CreateCommand();
            command.Transaction = tx;
            command.CommandText = $@"
                UPDATE channel_videos
                SET metadata_status = 'Queued', metadata_error = NULL, metadata_priority = $priority
                WHERE id IN (
                    SELECT videos.id
                    FROM channel_videos videos
                    WHERE {candidateCondition} AND {tierCondition}
                    ORDER BY videos.id DESC
                    LIMIT $limit
                )";
            command.Parameters.AddWithValue("$priority", priority);
            command.Parameters.AddWithValue("$limit", remaining);
            queued += command.ExecuteNonQuery();
        }

        return queued;
    }

    public ChannelVideo? ClaimNextChannelVideoMetadata()
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var select = conn.CreateCommand();
        select.Transaction = tx;
        select.CommandText = @"
            SELECT videos.id, videos.channel_id, videos.video_id, videos.canonical_url, videos.title,
                   videos.duration_seconds, videos.uploaded_at, videos.discovered_at, videos.is_checked,
                   videos.download_status, videos.download_error, videos.download_attempts, tracks.id,
                   videos.metadata_status, videos.metadata_updated_at, videos.metadata_error,
                   videos.metadata_attempts, videos.view_count, videos.like_count, videos.thumbnail_url,
                   tracks.library_state, channels.name, NULL, NULL, 0, 0, 0
            FROM channel_videos videos
            JOIN channels ON channels.id = videos.channel_id
            LEFT JOIN tracks ON tracks.canonical_url = videos.canonical_url
            WHERE videos.metadata_status = 'Queued'
              AND (videos.metadata_priority >= 100 OR channels.subscribed = 1 OR tracks.id IS NOT NULL)
            ORDER BY videos.metadata_priority DESC,
                     videos.is_checked ASC,
                     COALESCE(videos.uploaded_at, videos.discovered_at) DESC,
                     videos.id DESC
            LIMIT 1";
        using var reader = select.ExecuteReader();
        if (!reader.Read())
        {
            tx.Commit();
            return null;
        }

        var video = ReadChannelVideo(reader);
        reader.Close();
        ExecuteInsert(conn, tx, @"
            UPDATE channel_videos
            SET metadata_status = 'Loading', metadata_error = NULL,
                metadata_attempts = metadata_attempts + 1
            WHERE id = $id",
            ("$id", video.Id));
        tx.Commit();
        return video with
        {
            MetadataStatus = ChannelMetadataStatus.Loading,
            MetadataAttempts = video.MetadataAttempts + 1
        };
    }

    public bool HasQueuedChannelVideoMetadata()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT EXISTS(
                                SELECT 1 FROM channel_videos videos
                                JOIN channels ON channels.id = videos.channel_id
                                LEFT JOIN tracks ON tracks.canonical_url = videos.canonical_url
                                WHERE videos.metadata_status = 'Queued'
                                  AND (videos.metadata_priority >= 100 OR channels.subscribed = 1 OR tracks.id IS NOT NULL))";
        return Convert.ToInt32(cmd.ExecuteScalar()) != 0;
    }

    public int CountBackgroundChannelVideoMetadataWork()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COUNT(*)
            FROM channel_videos videos
            JOIN channels ON channels.id = videos.channel_id
            LEFT JOIN tracks ON tracks.canonical_url = videos.canonical_url
            WHERE videos.metadata_status IN ('Queued', 'Loading')
               OR ((channels.subscribed = 1
                    OR (tracks.id IS NOT NULL AND tracks.source_metadata_updated_at IS NULL))
                   AND (videos.metadata_status = 'Pending'
                    OR (videos.metadata_status = 'Ready'
                        AND (videos.metadata_updated_at IS NULL
                             OR videos.metadata_updated_at < datetime('now', '-30 days')))))";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public int ResetChannelMetadataIssues(int channelId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE channel_videos
            SET metadata_status = 'Pending', metadata_error = NULL,
                metadata_attempts = 0, metadata_priority = 0
            WHERE channel_id = $channelId AND metadata_status = 'Failed'";
        cmd.Parameters.AddWithValue("$channelId", channelId);
        return cmd.ExecuteNonQuery();
    }

    public bool RetryChannelVideoIssue(int videoId)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            UPDATE channel_videos
            SET metadata_status = CASE
                    WHEN metadata_status = 'Failed' THEN 'Queued'
                    ELSE metadata_status
                END,
                metadata_error = CASE
                    WHEN metadata_status = 'Failed' THEN NULL
                    ELSE metadata_error
                END,
                metadata_priority = CASE
                    WHEN metadata_status = 'Failed' THEN 200
                    ELSE metadata_priority
                END,
                metadata_attempts = CASE
                    WHEN metadata_status = 'Failed' THEN 0
                    ELSE metadata_attempts
                END,
                updated_at = $now
            WHERE id = $videoId
              AND metadata_status = 'Failed'";
        cmd.Parameters.AddWithValue("$videoId", videoId);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        var changed = cmd.ExecuteNonQuery() == 1;
        tx.Commit();
        return changed;
    }

    public void CompleteChannelVideoMetadata(
        int videoId,
        YouTubeTrackMetadata? metadata,
        string? error)
    {
        using var conn = Open();
        var now = DateTime.UtcNow.ToString("O");
        if (metadata is null)
        {
            ExecuteNonQuery(conn, @"
                UPDATE channel_videos
                SET metadata_status = 'Failed', metadata_error = $error,
                    metadata_updated_at = $now, metadata_priority = 0
                WHERE id = $id",
                ("$id", videoId), ("$error", error ?? "Metadata unavailable"), ("$now", now));
            return;
        }

        ExecuteNonQuery(conn, @"
            UPDATE channel_videos
            SET title = COALESCE($title, title),
                duration_seconds = COALESCE($duration, duration_seconds),
                uploaded_at = COALESCE($uploadedAt, uploaded_at),
                view_count = $viewCount,
                like_count = $likeCount,
                thumbnail_url = COALESCE($thumbnailUrl, thumbnail_url),
                metadata_status = 'Ready', metadata_error = NULL,
                metadata_updated_at = $now, metadata_priority = 0
            WHERE id = $id",
            ("$id", videoId),
            ("$title", metadata.Title),
            ("$duration", metadata.DurationSeconds),
            ("$uploadedAt", metadata.UploadedAt),
            ("$viewCount", metadata.ViewCount),
            ("$likeCount", metadata.LikeCount),
            ("$thumbnailUrl", metadata.ThumbnailUrl),
            ("$now", now));
        ExecuteNonQuery(conn, @"
            UPDATE tracks
            SET duration_seconds = COALESCE(duration_seconds, $duration),
                uploaded_at = COALESCE(uploaded_at, $uploadedAt),
                view_count = $viewCount,
                like_count = $likeCount,
                source_thumbnail_url = COALESCE($thumbnailUrl, source_thumbnail_url),
                source_metadata_updated_at = $now
            WHERE canonical_url = (
                SELECT canonical_url FROM channel_videos WHERE id = $id)",
            ("$id", videoId),
            ("$duration", metadata.DurationSeconds), ("$uploadedAt", metadata.UploadedAt),
            ("$viewCount", metadata.ViewCount), ("$likeCount", metadata.LikeCount),
            ("$thumbnailUrl", metadata.ThumbnailUrl), ("$now", now));
        if (metadata.ChannelFollowerCount is long followerCount)
            ExecuteNonQuery(conn, @"
                UPDATE channels
                SET follower_count = $followerCount, remote_metadata_updated_at = $now
                WHERE id = (SELECT channel_id FROM channel_videos WHERE id = $videoId)",
                ("$videoId", videoId), ("$followerCount", followerCount), ("$now", now));
    }

    private static ChannelVideo ReadChannelVideo(SqliteDataReader reader) => new(
        reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetInt32(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.GetString(7), ReadInt32(reader, 8) != 0,
        Enum.TryParse<ChannelDownloadStatus>(reader.GetString(9), true, out var status)
            ? status
            : ChannelDownloadStatus.NotQueued,
        reader.IsDBNull(10) ? null : reader.GetString(10),
        ReadInt32(reader, 11),
        reader.IsDBNull(12) ? null : reader.GetInt32(12),
        reader.FieldCount > 13 && Enum.TryParse<ChannelMetadataStatus>(reader.GetString(13), true, out var metadataStatus)
            ? metadataStatus
            : ChannelMetadataStatus.Pending,
        reader.FieldCount > 14 && !reader.IsDBNull(14) ? reader.GetString(14) : null,
        reader.FieldCount > 15 && !reader.IsDBNull(15) ? reader.GetString(15) : null,
        reader.FieldCount > 16 ? ReadInt32(reader, 16) : 0,
        reader.FieldCount > 17 && !reader.IsDBNull(17) ? reader.GetInt64(17) : null,
        reader.FieldCount > 18 && !reader.IsDBNull(18) ? reader.GetInt64(18) : null,
        reader.FieldCount > 19 && !reader.IsDBNull(19) ? reader.GetString(19) : null,
        reader.FieldCount > 20 && !reader.IsDBNull(20)
            && Enum.TryParse<TrackLibraryState>(reader.GetString(20), true, out var libraryState)
                ? libraryState
                : null,
        reader.FieldCount > 21 && !reader.IsDBNull(21) ? reader.GetString(21) : null,
        reader.FieldCount > 22 && !reader.IsDBNull(22) ? reader.GetString(22) : null,
        reader.FieldCount > 23 && !reader.IsDBNull(23) ? ReadInt32(reader, 23) : null,
        reader.FieldCount > 24 ? ReadInt32(reader, 24) : 0,
        reader.FieldCount > 25 ? ReadInt32(reader, 25) : 0,
        reader.FieldCount > 26 ? ReadInt32(reader, 26) : 0);

    public bool DeleteChannel(int channelId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM channels WHERE id = $channelId";
        cmd.Parameters.AddWithValue("$channelId", channelId);
        return cmd.ExecuteNonQuery() > 0;
    }

    private static int ReadInt32(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal));

    private static long? FindChannelId(SqliteConnection conn, SqliteTransaction tx, string? sourceChannelId, string sourceUrl)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            SELECT id FROM channels
            WHERE ($sourceChannelId IS NOT NULL AND source_channel_id = $sourceChannelId)
               OR ($sourceUrl IS NOT NULL AND source_url = $sourceUrl)
            LIMIT 1";
        cmd.Parameters.AddWithValue("$sourceChannelId", (object?)sourceChannelId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sourceUrl", sourceUrl);
        return cmd.ExecuteScalar() is long id ? id : null;
    }

    private static bool ChannelVideoExists(SqliteConnection conn, SqliteTransaction tx, string canonicalUrl)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM channel_videos WHERE canonical_url = $canonicalUrl";
        cmd.Parameters.AddWithValue("$canonicalUrl", canonicalUrl);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    public int InsertTrack(string canonicalUrl, string title, string fileName,
        List<int> genreIds, int? ratingId, List<int> styleIds, int? durationSeconds, long? fileSizeBytes,
        int? downloadDurationMilliseconds, YouTubeTrackMetadata? metadata = null, byte[]? thumbnail = null,
        bool isOriginal = true, int? parentTrackId = null, TrackEditTypes editTypes = TrackEditTypes.None,
        string? versionName = null)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var now = DateTime.UtcNow.ToString("O");

        long? channelId = null;
        if (!string.IsNullOrWhiteSpace(metadata?.ChannelId))
        {
            ExecuteInsert(conn, tx, @"INSERT INTO channels
                    (name, source_channel_id, source_url, subscribed,
                     follower_count, remote_metadata_updated_at)
                VALUES ($name, $channelId, $channelUrl, 0, $followerCount, $metadataUpdatedAt)
                ON CONFLICT(source_channel_id) DO UPDATE SET
                    name = excluded.name,
                    source_url = COALESCE(excluded.source_url, channels.source_url),
                    follower_count = COALESCE(excluded.follower_count, channels.follower_count),
                    remote_metadata_updated_at = excluded.remote_metadata_updated_at",
                ("$name", metadata.ChannelName ?? "Unknown channel"), ("$channelId", metadata.ChannelId),
                ("$channelUrl", metadata.ChannelUrl), ("$followerCount", metadata.ChannelFollowerCount),
                ("$metadataUpdatedAt", now));
            channelId = InsertAndGetId(conn, tx, "SELECT id FROM channels WHERE source_channel_id = $channelId",
                ("$channelId", metadata.ChannelId));
        }

        var trackId = InsertAndGetId(conn, tx, @"
            INSERT INTO tracks
                (canonical_url, title, original_title, file_name, channel_id, rating_id, uploaded_at, downloaded_at, updated_at,
                 duration_seconds, file_size_bytes, download_duration_ms, thumbnail, is_public, library_state,
                 needs_reevaluation,
                 source_video_id, view_count, like_count, source_thumbnail_url, source_metadata_updated_at)
            VALUES
                ($url, $title, $title, $fileName, $channelId, $ratingId, $uploadedAt, $downloadedAt, $updatedAt,
                 $duration, $fileSizeBytes, $downloadDurationMs, $thumbnail, 1, $libraryState,
                 $needsReview,
                 $videoId, $viewCount, $likeCount, $thumbnailUrl, $metadataUpdatedAt)",
            ("$url", canonicalUrl),
            ("$title", title),
            ("$fileName", fileName),
            ("$channelId", channelId),
            ("$ratingId", ratingId),
            ("$uploadedAt", metadata?.UploadedAt),
            ("$downloadedAt", now),
            ("$updatedAt", now),
            ("$duration", durationSeconds),
            ("$fileSizeBytes", fileSizeBytes),
            ("$downloadDurationMs", downloadDurationMilliseconds),
            ("$thumbnail", thumbnail),
            ("$libraryState", ratingId is null ? "PendingRating" : "Active"),
            ("$needsReview", ratingId is null ? 1 : 0),
            ("$videoId", YouTubeUrlNormalizer.ExtractVideoId(canonicalUrl)),
            ("$viewCount", metadata?.ViewCount),
            ("$likeCount", metadata?.LikeCount),
            ("$thumbnailUrl", metadata?.ThumbnailUrl),
            ("$metadataUpdatedAt", metadata is null ? null : now));

        SynchronizeLibraryChannelVideo(conn, tx, trackId, now);
        SetTrackStyles(conn, tx, trackId, styleIds);
        // The original may have been deleted while this queued download was running.
        // Keep the downloaded edit, just as we do when deleting its parent afterward.
        if (!isOriginal && parentTrackId is int requestedParent)
        {
            using var parent = conn.CreateCommand();
            parent.Transaction = tx;
            parent.CommandText = "SELECT id FROM tracks WHERE id = $id AND is_original = 1";
            parent.Parameters.AddWithValue("$id", requestedParent);
            if (parent.ExecuteScalar() is null)
                parentTrackId = null;
        }
        SetTrackVersion(conn, tx, (int)trackId, isOriginal, parentTrackId, editTypes);
        if (!string.IsNullOrWhiteSpace(versionName))
            ExecuteInsert(conn, tx, "UPDATE tracks SET remix = $name WHERE id = $id",
                ("$name", versionName.Trim()), ("$id", trackId));

        tx.Commit();
        return (int)trackId;
    }

    private static void SynchronizeLibraryChannelVideo(
        SqliteConnection conn,
        SqliteTransaction tx,
        long trackId,
        string now)
    {
        ExecuteInsert(conn, tx, @"
            INSERT INTO channel_videos
                (channel_id, video_id, canonical_url, title, duration_seconds, uploaded_at,
                 discovered_at, updated_at, is_checked, download_status, metadata_status,
                 metadata_updated_at, view_count, like_count, thumbnail_url)
            SELECT channel_id, COALESCE(source_video_id, 'library-track-' || id), canonical_url, original_title,
                   duration_seconds, uploaded_at, downloaded_at, $now, 1, 'Ready',
                   CASE WHEN source_metadata_updated_at IS NULL THEN 'Pending' ELSE 'Ready' END,
                   source_metadata_updated_at, view_count, like_count, source_thumbnail_url
            FROM tracks
            WHERE id = $trackId AND channel_id IS NOT NULL
              AND canonical_url IS NOT NULL AND TRIM(canonical_url) <> ''
            ON CONFLICT(canonical_url) DO UPDATE SET
                channel_id = excluded.channel_id,
                title = excluded.title,
                duration_seconds = COALESCE(channel_videos.duration_seconds, excluded.duration_seconds),
                uploaded_at = COALESCE(channel_videos.uploaded_at, excluded.uploaded_at),
                metadata_status = CASE
                    WHEN excluded.metadata_status = 'Ready' THEN 'Ready'
                    ELSE channel_videos.metadata_status
                END,
                metadata_updated_at = COALESCE(excluded.metadata_updated_at, channel_videos.metadata_updated_at),
                view_count = COALESCE(excluded.view_count, channel_videos.view_count),
                like_count = COALESCE(excluded.like_count, channel_videos.like_count),
                thumbnail_url = COALESCE(excluded.thumbnail_url, channel_videos.thumbnail_url),
                is_checked = 1,
                download_status = 'Ready',
                download_error = NULL,
                metadata_error = CASE
                    WHEN excluded.metadata_status = 'Ready' THEN NULL
                    ELSE channel_videos.metadata_error
                END,
                metadata_priority = 0",
            ("$trackId", trackId), ("$now", now));
    }

    public int? CompleteChannelVideoReview(int videoId, bool skip)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        long? trackId;
        using (var find = conn.CreateCommand())
        {
            find.Transaction = tx;
            find.CommandText = @"
                SELECT tracks.id
                FROM channel_videos videos
                JOIN tracks ON tracks.canonical_url = videos.canonical_url
                WHERE videos.id = $videoId";
            find.Parameters.AddWithValue("$videoId", videoId);
            trackId = find.ExecuteScalar() as long?;
        }
        if (trackId is null)
        {
            tx.Commit();
            return null;
        }

        ExecuteInsert(conn, tx,
            "UPDATE channel_videos SET is_checked = 1, updated_at = $now WHERE id = $videoId",
            ("$videoId", videoId), ("$now", DateTime.UtcNow.ToString("O")));
        if (skip)
        {
            ExecuteInsert(conn, tx, @"
                UPDATE tracks
                SET rating_band = CASE
                        WHEN rating_id IS (SELECT id FROM ratings WHERE name = $avoidRating) THEN rating_band
                        ELSE NULL
                    END,
                    rating_id = (SELECT id FROM ratings WHERE name = $avoidRating),
                    library_state = 'Rejected', analysis_disabled = 1,
                    needs_reevaluation = 0, updated_at = $now
                WHERE id = $trackId",
                ("$trackId", trackId.Value), ("$avoidRating", RatingNames.Avoid),
                ("$now", DateTime.UtcNow.ToString("O")));
        }
        else
        {
            ExecuteInsert(conn, tx, @"
                UPDATE tracks
                SET library_state = CASE WHEN rating_id IS NULL THEN 'PendingRating' ELSE 'Active' END,
                    analysis_disabled = 0,
                    needs_reevaluation = 1, updated_at = $now
                WHERE id = $trackId",
                ("$trackId", trackId.Value), ("$now", DateTime.UtcNow.ToString("O")));
        }

        tx.Commit();
        return (int)trackId.Value;
    }

    public bool DismissChannelVideo(int videoId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE channel_videos
            SET is_checked = 1,
                download_status = 'NotQueued',
                download_error = NULL,
                updated_at = $now
            WHERE id = $videoId AND is_checked = 0";
        cmd.Parameters.AddWithValue("$videoId", videoId);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        return cmd.ExecuteNonQuery() > 0;
    }

    public HashSet<int> GetTrackIdsMissingAnalysis()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT tracks.id FROM tracks
            LEFT JOIN track_analysis analysis ON analysis.track_id = tracks.id
            WHERE analysis.id IS NULL
              AND tracks.analysis_disabled = 0
              AND tracks.library_state <> 'Rejected'";
        using var reader = cmd.ExecuteReader();
        var ids = new HashSet<int>();
        while (reader.Read()) ids.Add(reader.GetInt32(0));
        return ids;
    }

    public void SaveTrackAnalysis(int trackId, TrackAnalysisResult analysis, int? analysisDurationMilliseconds = null)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var now = DateTime.UtcNow.ToString("O");

        ExecuteInsert(conn, tx, @"
            INSERT INTO track_analysis (track_id, analyzed_at, analyzer_name, bpm, integrated_loudness, loudness_range, analysis_duration_ms)
            VALUES ($trackId, $analyzedAt, $analyzerName, $bpm, $loudness, $loudnessRange, $analysisDurationMs)
            ON CONFLICT(track_id) DO UPDATE SET
                analyzed_at = excluded.analyzed_at,
                analyzer_name = excluded.analyzer_name,
                bpm = excluded.bpm,
                integrated_loudness = excluded.integrated_loudness,
                loudness_range = excluded.loudness_range,
                analysis_duration_ms = excluded.analysis_duration_ms",
            ("$trackId", trackId),
            ("$analyzedAt", now),
            ("$analyzerName", analysis.AnalyzerName),
            ("$bpm", analysis.Bpm),
            ("$loudness", analysis.IntegratedLoudness),
            ("$loudnessRange", analysis.LoudnessRange),
            ("$analysisDurationMs", analysisDurationMilliseconds));

        var analysisId = GetTrackAnalysisId(conn, tx, trackId);
        ExecuteInsert(conn, tx,
            "DELETE FROM track_genre_predictions WHERE track_analysis_id = $analysisId",
            ("$analysisId", analysisId));

        foreach (var prediction in analysis.Predictions)
        {
            using var predictionCommand = conn.CreateCommand();
            predictionCommand.Transaction = tx;
            predictionCommand.CommandText = @"
                INSERT INTO track_genre_predictions (track_analysis_id, model_subgenre_id, score)
                SELECT $analysisId, model_subgenres.id, $score
                FROM model_subgenres
                JOIN model_genres ON model_genres.id = model_subgenres.model_genre_id
                WHERE model_genres.name = $modelGenre AND model_subgenres.name = $modelSubgenre";
            predictionCommand.Parameters.AddWithValue("$analysisId", analysisId);
            predictionCommand.Parameters.AddWithValue("$score", prediction.Score);
            predictionCommand.Parameters.AddWithValue("$modelGenre", prediction.ModelGenre);
            predictionCommand.Parameters.AddWithValue("$modelSubgenre", prediction.ModelSubgenre);
            predictionCommand.ExecuteNonQuery();
        }

        ExecuteInsert(conn, tx,
            "DELETE FROM track_analysis_signals WHERE track_analysis_id = $analysisId",
            ("$analysisId", analysisId));
        foreach (var model in analysis.ExperimentalModels ?? [])
        foreach (var value in model.Values)
        {
            ExecuteInsert(conn, tx, @"
                INSERT INTO track_analysis_signals
                    (track_analysis_id, model_family, category, model_name, model_type, description, signal_key, score)
                VALUES ($analysisId, $family, $category, $model, $type, $description, $key, $score)",
                ("$analysisId", analysisId), ("$family", model.Family), ("$category", model.Category),
                ("$model", model.Model), ("$type", model.Type), ("$description", model.Description),
                ("$key", value.Label), ("$score", value.Score));
        }

        RefreshModelGenres(conn, tx, trackId);
        ExecuteInsert(conn, tx,
            "UPDATE tracks SET analysis_disabled = 0, needs_reevaluation = 1 WHERE id = $trackId",
            ("$trackId", trackId));
        TouchTrack(conn, tx, trackId, now);

        tx.Commit();
    }

    public List<MusicTrack> GetAllTracks(bool includeRejected = false)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT tracks.id, tracks.canonical_url, tracks.title, tracks.file_name, tracks.rating_id, tracks.downloaded_at,
                                   tracks.duration_seconds, tracks.needs_reevaluation, channels.name, channels.source_url, tracks.uploaded_at,
                                   tracks.updated_at, tracks.analysis_disabled, tracks.is_public, tracks.library_state,
                                   tracks.source_video_id, tracks.view_count, tracks.like_count,
                                   tracks.source_thumbnail_url, tracks.source_metadata_updated_at, channels.id, tracks.language_code,
                                   tracks.original_title, tracks.artist, tracks.remix, tracks.rating_band, tracks.is_original, tracks.parent_track_id, tracks.edit_types
                            FROM tracks LEFT JOIN channels ON channels.id = tracks.channel_id
                            WHERE ($includeRejected = 1 OR tracks.library_state <> 'Rejected')
                            ORDER BY tracks.downloaded_at DESC";
        cmd.Parameters.AddWithValue("$includeRejected", includeRejected ? 1 : 0);
        using var reader = cmd.ExecuteReader();
        var tracks = new List<MusicTrack>();
        while (reader.Read())
        {
            tracks.Add(ReadMusicTrack(reader));
        }
        return tracks;
    }

    public MusicTrack? GetTrackById(int trackId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT tracks.id, tracks.canonical_url, tracks.title, tracks.file_name, tracks.rating_id, tracks.downloaded_at,
                                   tracks.duration_seconds, tracks.needs_reevaluation, channels.name, channels.source_url, tracks.uploaded_at,
                                   tracks.updated_at, tracks.analysis_disabled, tracks.is_public, tracks.library_state,
                                   tracks.source_video_id, tracks.view_count, tracks.like_count,
                                   tracks.source_thumbnail_url, tracks.source_metadata_updated_at, channels.id, tracks.language_code,
                                   tracks.original_title, tracks.artist, tracks.remix, tracks.rating_band, tracks.is_original, tracks.parent_track_id, tracks.edit_types
                            FROM tracks LEFT JOIN channels ON channels.id = tracks.channel_id
                            WHERE tracks.id = $trackId";
        cmd.Parameters.AddWithValue("$trackId", trackId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadMusicTrack(reader) : null;
    }

    public MusicTrack? GetTrackByCanonicalUrl(string canonicalUrl)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT tracks.id, tracks.canonical_url, tracks.title, tracks.file_name, tracks.rating_id, tracks.downloaded_at,
                                   tracks.duration_seconds, tracks.needs_reevaluation, channels.name, channels.source_url, tracks.uploaded_at,
                                   tracks.updated_at, tracks.analysis_disabled, tracks.is_public, tracks.library_state,
                                   tracks.source_video_id, tracks.view_count, tracks.like_count,
                                   tracks.source_thumbnail_url, tracks.source_metadata_updated_at, channels.id, tracks.language_code,
                                   tracks.original_title, tracks.artist, tracks.remix, tracks.rating_band, tracks.is_original, tracks.parent_track_id, tracks.edit_types
                            FROM tracks LEFT JOIN channels ON channels.id = tracks.channel_id
                            WHERE tracks.canonical_url = $canonicalUrl";
        cmd.Parameters.AddWithValue("$canonicalUrl", canonicalUrl);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadMusicTrack(reader) : null;
    }

    public byte[]? GetTrackThumbnail(int trackId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT thumbnail FROM tracks WHERE id = $trackId";
        cmd.Parameters.AddWithValue("$trackId", trackId);
        var value = cmd.ExecuteScalar();
        return value is DBNull or null ? null : (byte[])value;
    }

    public HashSet<int> GetAnalyzedTrackIds()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT track_id FROM track_analysis";
        using var reader = cmd.ExecuteReader();
        var result = new HashSet<int>();
        while (reader.Read())
            result.Add(reader.GetInt32(0));
        return result;
    }

    public List<MusicTrack> GetUnanalyzedTracks()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT tracks.id, tracks.canonical_url, tracks.title, tracks.file_name, tracks.rating_id, tracks.downloaded_at,
                                   tracks.duration_seconds, tracks.needs_reevaluation, channels.name, channels.source_url, tracks.uploaded_at,
                                   tracks.updated_at, tracks.analysis_disabled, tracks.is_public, tracks.library_state,
                                   tracks.source_video_id, tracks.view_count, tracks.like_count,
                                   tracks.source_thumbnail_url, tracks.source_metadata_updated_at, channels.id, tracks.language_code,
                                   tracks.original_title, tracks.artist, tracks.remix, tracks.rating_band, tracks.is_original, tracks.parent_track_id, tracks.edit_types
                            FROM tracks
                            LEFT JOIN channels ON channels.id = tracks.channel_id
                            LEFT JOIN track_analysis analysis ON analysis.track_id = tracks.id
                            WHERE analysis.id IS NULL
                              AND tracks.analysis_disabled = 0
                              AND tracks.library_state <> 'Rejected'
                              AND NOT EXISTS (
                                  SELECT 1
                                  FROM channel_videos videos
                                  WHERE videos.canonical_url = tracks.canonical_url
                                    AND videos.is_checked = 0
                              )
                            ORDER BY tracks.downloaded_at, tracks.id";
        using var reader = cmd.ExecuteReader();
        var tracks = new List<MusicTrack>();
        while (reader.Read())
        {
            tracks.Add(ReadMusicTrack(reader));
        }
        return tracks;
    }

    public bool IsTrackWaitingForChannelReview(int trackId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT EXISTS (
                SELECT 1
                FROM tracks
                JOIN channel_videos videos ON videos.canonical_url = tracks.canonical_url
                WHERE tracks.id = $trackId
                  AND videos.is_checked = 0
            )";
        cmd.Parameters.AddWithValue("$trackId", trackId);
        return Convert.ToInt32(cmd.ExecuteScalar()) == 1;
    }

    public int CountUnratedTracks()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT COUNT(*) FROM tracks
                            WHERE rating_id IS NULL AND library_state <> 'Rejected'";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<MusicTrack> GetUnratedTracks()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT tracks.id, tracks.canonical_url, tracks.title, tracks.file_name, tracks.rating_id, tracks.downloaded_at,
                                   tracks.duration_seconds, tracks.needs_reevaluation, channels.name, channels.source_url, tracks.uploaded_at,
                                   tracks.updated_at, tracks.analysis_disabled, tracks.is_public, tracks.library_state,
                                   tracks.source_video_id, tracks.view_count, tracks.like_count,
                                   tracks.source_thumbnail_url, tracks.source_metadata_updated_at, channels.id, tracks.language_code,
                                   tracks.original_title, tracks.artist, tracks.remix, tracks.rating_band, tracks.is_original, tracks.parent_track_id, tracks.edit_types
                            FROM tracks LEFT JOIN channels ON channels.id = tracks.channel_id
                            WHERE tracks.rating_id IS NULL AND tracks.library_state <> 'Rejected'
                            ORDER BY tracks.downloaded_at DESC";
        using var reader = cmd.ExecuteReader();
        var tracks = new List<MusicTrack>();
        while (reader.Read())
            tracks.Add(ReadMusicTrack(reader));
        return tracks;
    }

    private static MusicTrack ReadMusicTrack(SqliteDataReader reader)
    {
        var libraryState = TrackLibraryState.Active;
        if (!reader.IsDBNull(14)
            && Enum.TryParse<TrackLibraryState>(reader.GetString(14), ignoreCase: true, out var parsedState))
            libraryState = parsedState;

        RatingBand? ratingBand = null;
        if (!reader.IsDBNull(25)
            && Enum.TryParse<RatingBand>(reader.GetString(25), ignoreCase: true, out var parsedBand))
            ratingBand = parsedBand;

        return new MusicTrack(
            reader.GetInt32(0),
            reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetInt32(6),
            reader.GetInt32(7) != 0,
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.GetString(11),
            reader.GetInt32(12) != 0,
            reader.GetInt32(13) != 0,
            null,
            libraryState,
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetInt64(16),
            reader.IsDBNull(17) ? null : reader.GetInt64(17),
            reader.IsDBNull(18) ? null : reader.GetString(18),
            reader.IsDBNull(19) ? null : reader.GetString(19),
            reader.IsDBNull(20) ? null : reader.GetInt32(20),
            reader.IsDBNull(21) ? null : reader.GetString(21),
            reader.GetString(22),
            reader.IsDBNull(23) ? null : reader.GetString(23),
            reader.IsDBNull(24) ? null : reader.GetString(24),
            ratingBand,
            reader.GetInt32(26) != 0,
            reader.IsDBNull(27) ? null : reader.GetInt32(27),
            (TrackEditTypes)reader.GetInt32(28));
    }

    public void SetTrackLanguage(int trackId, string? languageCode)
    {
        var normalized = string.IsNullOrWhiteSpace(languageCode) ? null : languageCode.Trim();
        if (normalized is not null && !TrackLanguageCatalog.All.Any(language =>
                string.Equals(language.Code, normalized, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Unsupported track language '{languageCode}'.", nameof(languageCode));

        using var conn = Open();
        ExecuteNonQuery(conn, @"UPDATE tracks SET language_code = $languageCode, updated_at = $now WHERE id = $trackId",
            ("$languageCode", normalized), ("$now", DateTime.UtcNow.ToString("O")), ("$trackId", trackId));
    }

    public Dictionary<int, List<int>> GetAllTrackGenreIds()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT track_id, genre_id FROM track_genres WHERE is_enabled = 1";
        return ReadTrackIdMap(cmd);
    }

    public List<int> GetTrackGenreIds(int trackId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT genre_id FROM track_genres WHERE track_id = $trackId AND is_enabled = 1";
        cmd.Parameters.AddWithValue("$trackId", trackId);
        using var reader = cmd.ExecuteReader();
        var genreIds = new List<int>();
        while (reader.Read()) genreIds.Add(reader.GetInt32(0));
        return genreIds;
    }

    public List<TrackModelGenre> GetTrackModelGenres(int trackId)
    {
        using var conn = Open(); using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT track_genres.genre_id, msg.name, track_genres.is_enabled, track_genres.is_manual,
                                   mg.name, msg.name, predictions.score
                            FROM track_genres
                            JOIN model_subgenres msg ON msg.id = track_genres.genre_id
                            JOIN model_genres mg ON mg.id = msg.model_genre_id
                            LEFT JOIN track_analysis analysis ON analysis.track_id = track_genres.track_id
                            LEFT JOIN track_genre_predictions predictions ON predictions.track_analysis_id = analysis.id AND predictions.model_subgenre_id = msg.id
                            WHERE track_genres.track_id = $trackId
                            ORDER BY predictions.score IS NULL, predictions.score DESC, msg.name";
        cmd.Parameters.AddWithValue("$trackId", trackId);
        using var reader = cmd.ExecuteReader();
        var groups = new Dictionary<int, (string Name, bool Enabled, bool Manual, List<ModelGenreReason> Reasons)>();
        while (reader.Read())
        {
            var id = reader.GetInt32(0);
            if (!groups.TryGetValue(id, out var group))
                groups[id] = group = (reader.GetString(1), reader.GetInt32(2) != 0, reader.GetInt32(3) != 0, []);
            if (!reader.IsDBNull(6))
                group.Reasons.Add(new ModelGenreReason(reader.GetString(4), reader.GetString(5), reader.GetDouble(6)));
        }
        return groups.Select(x => new TrackModelGenre(
            x.Key, x.Value.Name, x.Value.Enabled, x.Value.Manual, x.Value.Reasons)).ToList();
    }

    public void SetTrackModelGenreEnabled(int trackId, int genreId, bool isEnabled)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        var now = DateTime.UtcNow.ToString("O");
        if (isEnabled)
        {
            cmd.CommandText = @"
                INSERT INTO track_genres (track_id, genre_id, assigned_at, is_enabled, is_manual)
                VALUES ($trackId, $genreId, $assignedAt, 1, 1)
                ON CONFLICT(track_id, genre_id) DO UPDATE SET
                    is_enabled = 1,
                    is_manual = 1,
                    assigned_at = excluded.assigned_at";
            cmd.Parameters.AddWithValue("$assignedAt", now);
        }
        else
        {
            cmd.CommandText = @"UPDATE track_genres SET is_enabled = 0 WHERE track_id = $trackId AND genre_id = $genreId";
        }
        cmd.Parameters.AddWithValue("$trackId", trackId);
        cmd.Parameters.AddWithValue("$genreId", genreId);
        cmd.ExecuteNonQuery();
        TouchTrack(conn, tx, trackId, now);
        tx.Commit();
    }

    // --- Collections ---

    public List<TrackCollection> GetCollections()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT collections.id, collections.stable_id, collections.name,
                   CASE WHEN collections.cover_kind = 'Track' AND collections.cover_track_id IS NULL
                        THEN 'Automatic' ELSE collections.cover_kind END,
                   collections.cover_track_id,
                   COUNT(collection_tracks.track_id),
                   COALESCE(SUM(tracks.duration_seconds), 0),
                   collections.created_at, collections.updated_at
            FROM collections
            LEFT JOIN collection_tracks ON collection_tracks.collection_id = collections.id
            LEFT JOIN tracks ON tracks.id = collection_tracks.track_id
            GROUP BY collections.id
            ORDER BY collections.name COLLATE NOCASE";
        using var reader = cmd.ExecuteReader();
        var collections = new List<TrackCollection>();
        while (reader.Read())
            collections.Add(ReadCollection(reader));
        return collections;
    }

    public TrackCollection? GetCollectionByStableId(string stableId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT collections.id, collections.stable_id, collections.name,
                   CASE WHEN collections.cover_kind = 'Track' AND collections.cover_track_id IS NULL
                        THEN 'Automatic' ELSE collections.cover_kind END,
                   collections.cover_track_id,
                   COUNT(collection_tracks.track_id),
                   COALESCE(SUM(tracks.duration_seconds), 0),
                   collections.created_at, collections.updated_at
            FROM collections
            LEFT JOIN collection_tracks ON collection_tracks.collection_id = collections.id
            LEFT JOIN tracks ON tracks.id = collection_tracks.track_id
            WHERE collections.stable_id = $stableId
            GROUP BY collections.id";
        cmd.Parameters.AddWithValue("$stableId", stableId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadCollection(reader) : null;
    }

    public TrackCollection CreateCollection(string name)
    {
        var normalizedName = NormalizeCollectionName(name);
        using var conn = Open();
        var now = DateTime.UtcNow.ToString("O");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO collections (stable_id, name, cover_kind, created_at, updated_at)
            VALUES ($stableId, $name, 'Automatic', $now, $now);
            SELECT last_insert_rowid();";
        var stableId = Guid.NewGuid().ToString("D");
        cmd.Parameters.AddWithValue("$stableId", stableId);
        cmd.Parameters.AddWithValue("$name", normalizedName);
        cmd.Parameters.AddWithValue("$now", now);
        _ = Convert.ToInt32(cmd.ExecuteScalar());
        return GetCollectionByStableId(stableId)
               ?? throw new InvalidOperationException("The collection could not be created.");
    }

    public void RenameCollection(int collectionId, string name)
    {
        using var conn = Open();
        ExecuteNonQuery(conn, @"
            UPDATE collections SET name = $name, updated_at = $now WHERE id = $id",
            ("$id", collectionId), ("$name", NormalizeCollectionName(name)),
            ("$now", DateTime.UtcNow.ToString("O")));
    }

    public void DeleteCollection(int collectionId)
    {
        using var conn = Open();
        ExecuteNonQuery(conn, "DELETE FROM collections WHERE id = $id", ("$id", collectionId));
    }

    public bool AddTrackToCollection(int collectionId, int trackId)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var now = DateTime.UtcNow.ToString("O");
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO collection_tracks (collection_id, track_id, position, added_at)
            SELECT $collectionId, $trackId,
                   COALESCE(MAX(existing.position) + 1, 0), $now
            FROM collections
            LEFT JOIN collection_tracks existing ON existing.collection_id = collections.id
            WHERE collections.id = $collectionId
              AND EXISTS (SELECT 1 FROM tracks WHERE id = $trackId)
            ON CONFLICT(collection_id, track_id) DO NOTHING";
        cmd.Parameters.AddWithValue("$collectionId", collectionId);
        cmd.Parameters.AddWithValue("$trackId", trackId);
        cmd.Parameters.AddWithValue("$now", now);
        var added = cmd.ExecuteNonQuery() == 1;
        if (added)
            ExecuteInsert(conn, tx,
                "UPDATE collections SET updated_at = $now WHERE id = $id",
                ("$id", collectionId), ("$now", now));
        tx.Commit();
        return added;
    }

    public bool RemoveTrackFromCollection(int collectionId, int trackId)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM collection_tracks WHERE collection_id = $collectionId AND track_id = $trackId";
        cmd.Parameters.AddWithValue("$collectionId", collectionId);
        cmd.Parameters.AddWithValue("$trackId", trackId);
        var removed = cmd.ExecuteNonQuery() == 1;
        if (removed)
        {
            var now = DateTime.UtcNow.ToString("O");
            ExecuteInsert(conn, tx, @"
                UPDATE collections
                SET cover_kind = CASE WHEN cover_track_id = $trackId THEN 'Automatic' ELSE cover_kind END,
                    cover_track_id = CASE WHEN cover_track_id = $trackId THEN NULL ELSE cover_track_id END,
                    updated_at = $now
                WHERE id = $collectionId",
                ("$collectionId", collectionId), ("$trackId", trackId), ("$now", now));
            NormalizeCollectionPositions(conn, tx, collectionId);
        }
        tx.Commit();
        return removed;
    }

    public bool MoveCollectionTrack(int collectionId, int trackId, int offset)
    {
        if (offset == 0)
            return false;

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var trackIds = ReadCollectionTrackIds(conn, tx, collectionId);
        var index = trackIds.IndexOf(trackId);
        if (index < 0)
            return false;
        var target = Math.Clamp(index + offset, 0, trackIds.Count - 1);
        if (target == index)
            return false;

        trackIds.RemoveAt(index);
        trackIds.Insert(target, trackId);
        WriteCollectionPositions(conn, tx, collectionId, trackIds);
        ExecuteInsert(conn, tx,
            "UPDATE collections SET updated_at = $now WHERE id = $id",
            ("$id", collectionId), ("$now", DateTime.UtcNow.ToString("O")));
        tx.Commit();
        return true;
    }

    public List<int> GetCollectionTrackIds(int collectionId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT track_id FROM collection_tracks
            WHERE collection_id = $collectionId
            ORDER BY position, added_at, track_id";
        cmd.Parameters.AddWithValue("$collectionId", collectionId);
        using var reader = cmd.ExecuteReader();
        var ids = new List<int>();
        while (reader.Read()) ids.Add(reader.GetInt32(0));
        return ids;
    }

    public List<CollectionTrack> GetCollectionTracks(int collectionId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT tracks.id, tracks.title, collection_tracks.position
            FROM collection_tracks
            JOIN tracks ON tracks.id = collection_tracks.track_id
            WHERE collection_tracks.collection_id = $collectionId
            ORDER BY collection_tracks.position, collection_tracks.added_at, tracks.id";
        cmd.Parameters.AddWithValue("$collectionId", collectionId);
        using var reader = cmd.ExecuteReader();
        var tracks = new List<CollectionTrack>();
        while (reader.Read())
            tracks.Add(new CollectionTrack(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2)));
        return tracks;
    }

    public List<TrackCollection> GetTrackCollections(int trackId) => GetCollectionsForTrack(trackId);

    public Dictionary<int, List<string>> GetAllTrackCollectionNames()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT collection_tracks.track_id, collections.name
            FROM collection_tracks
            JOIN collections ON collections.id = collection_tracks.collection_id
            ORDER BY collections.name COLLATE NOCASE";
        using var reader = cmd.ExecuteReader();
        var names = new Dictionary<int, List<string>>();
        while (reader.Read())
        {
            var trackId = reader.GetInt32(0);
            if (!names.TryGetValue(trackId, out var trackNames))
                names[trackId] = trackNames = [];
            trackNames.Add(reader.GetString(1));
        }
        return names;
    }

    public byte[]? GetCollectionCover(int collectionId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT cover_kind, cover_track_id, custom_cover FROM collections WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", collectionId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        var kind = reader.GetString(0);
        int? coverTrackId = reader.IsDBNull(1) ? null : reader.GetInt32(1);
        if (kind == nameof(CollectionCoverKind.Custom) && !reader.IsDBNull(2))
            return (byte[])reader[2];
        reader.Close();

        if (coverTrackId is int preferredTrackId)
        {
            using var preferred = conn.CreateCommand();
            preferred.CommandText = "SELECT thumbnail FROM tracks WHERE id = $trackId AND thumbnail IS NOT NULL";
            preferred.Parameters.AddWithValue("$trackId", preferredTrackId);
            if (preferred.ExecuteScalar() is byte[] preferredCover)
                return preferredCover;
        }

        using var cover = conn.CreateCommand();
        cover.CommandText = @"SELECT tracks.thumbnail
            FROM collection_tracks
            JOIN tracks ON tracks.id = collection_tracks.track_id
            WHERE collection_tracks.collection_id = $collectionId AND tracks.thumbnail IS NOT NULL
            ORDER BY collection_tracks.position, collection_tracks.added_at, tracks.id LIMIT 1";
        cover.Parameters.AddWithValue("$collectionId", collectionId);
        return cover.ExecuteScalar() as byte[];
    }

    public void SetCollectionCoverAutomatic(int collectionId) =>
        SetCollectionCover(collectionId, CollectionCoverKind.Automatic, null, null);

    public void SetCollectionCoverTrack(int collectionId, int trackId)
    {
        using var conn = Open();
        using var check = conn.CreateCommand();
        check.CommandText = @"
            SELECT COUNT(*) FROM collection_tracks
            WHERE collection_id = $collectionId AND track_id = $trackId";
        check.Parameters.AddWithValue("$collectionId", collectionId);
        check.Parameters.AddWithValue("$trackId", trackId);
        if (Convert.ToInt32(check.ExecuteScalar()) == 0)
            throw new InvalidOperationException("Choose a track that belongs to this collection.");
        SetCollectionCover(collectionId, CollectionCoverKind.Track, trackId, null);
    }

    public void SetCollectionCustomCover(int collectionId, byte[] cover)
    {
        if (cover.Length == 0)
            throw new ArgumentException("The collection cover is empty.", nameof(cover));
        SetCollectionCover(collectionId, CollectionCoverKind.Custom, null, cover);
    }

    private void SetCollectionCover(int collectionId, CollectionCoverKind kind, int? trackId, byte[]? customCover)
    {
        using var conn = Open();
        ExecuteNonQuery(conn, @"
            UPDATE collections
            SET cover_kind = $kind, cover_track_id = $trackId, custom_cover = $cover, updated_at = $now
            WHERE id = $id",
            ("$id", collectionId), ("$kind", kind.ToString()),
            ("$trackId", (object?)trackId ?? DBNull.Value),
            ("$cover", (object?)customCover ?? DBNull.Value),
            ("$now", DateTime.UtcNow.ToString("O")));
    }

    private List<TrackCollection> GetCollectionsForTrack(int trackId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT collections.id, collections.stable_id, collections.name,
                   CASE WHEN collections.cover_kind = 'Track' AND collections.cover_track_id IS NULL
                        THEN 'Automatic' ELSE collections.cover_kind END,
                   collections.cover_track_id,
                   (SELECT COUNT(*) FROM collection_tracks count_rows WHERE count_rows.collection_id = collections.id),
                   COALESCE((SELECT SUM(tracks.duration_seconds)
                             FROM collection_tracks duration_rows
                             JOIN tracks ON tracks.id = duration_rows.track_id
                             WHERE duration_rows.collection_id = collections.id), 0),
                   collections.created_at, collections.updated_at
            FROM collection_tracks
            JOIN collections ON collections.id = collection_tracks.collection_id
            WHERE collection_tracks.track_id = $trackId
            ORDER BY collections.name COLLATE NOCASE";
        cmd.Parameters.AddWithValue("$trackId", trackId);
        using var reader = cmd.ExecuteReader();
        var collections = new List<TrackCollection>();
        while (reader.Read()) collections.Add(ReadCollection(reader));
        return collections;
    }

    private static TrackCollection ReadCollection(SqliteDataReader reader)
    {
        var coverKind = Enum.TryParse<CollectionCoverKind>(reader.GetString(3), true, out var parsed)
            ? parsed
            : CollectionCoverKind.Automatic;
        return new TrackCollection(
            reader.GetInt32(0), reader.GetString(1), reader.GetString(2), coverKind,
            reader.IsDBNull(4) ? null : reader.GetInt32(4),
            Convert.ToInt32(reader.GetInt64(5)), Convert.ToInt32(reader.GetInt64(6)),
            reader.GetString(7), reader.GetString(8));
    }

    private static string NormalizeCollectionName(string name)
    {
        var normalized = name.Trim();
        if (normalized.Length == 0)
            throw new ArgumentException("Collection name cannot be empty.", nameof(name));
        if (normalized.Length > 80)
            throw new ArgumentException("Collection name cannot exceed 80 characters.", nameof(name));
        return normalized;
    }

    private static List<int> ReadCollectionTrackIds(
        SqliteConnection conn, SqliteTransaction tx, int collectionId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            SELECT track_id FROM collection_tracks
            WHERE collection_id = $collectionId
            ORDER BY position, added_at, track_id";
        cmd.Parameters.AddWithValue("$collectionId", collectionId);
        using var reader = cmd.ExecuteReader();
        var ids = new List<int>();
        while (reader.Read()) ids.Add(reader.GetInt32(0));
        return ids;
    }

    private static void NormalizeCollectionPositions(
        SqliteConnection conn, SqliteTransaction tx, int collectionId) =>
        WriteCollectionPositions(conn, tx, collectionId, ReadCollectionTrackIds(conn, tx, collectionId));

    private static void WriteCollectionPositions(
        SqliteConnection conn, SqliteTransaction tx, int collectionId, IReadOnlyList<int> trackIds)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            UPDATE collection_tracks SET position = $position
            WHERE collection_id = $collectionId AND track_id = $trackId";
        var collectionParameter = cmd.Parameters.Add("$collectionId", SqliteType.Integer);
        var trackParameter = cmd.Parameters.Add("$trackId", SqliteType.Integer);
        var positionParameter = cmd.Parameters.Add("$position", SqliteType.Integer);
        collectionParameter.Value = collectionId;
        for (var position = 0; position < trackIds.Count; position++)
        {
            trackParameter.Value = trackIds[position];
            positionParameter.Value = position;
            cmd.ExecuteNonQuery();
        }
    }

    public Dictionary<int, List<int>> GetAllTrackStyleIds()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT track_id, style_id FROM track_styles ORDER BY track_id, style_id";
        using var reader = cmd.ExecuteReader();
        var result = new Dictionary<int, List<int>>();
        while (reader.Read())
        {
            var trackId = reader.GetInt32(0);
            if (!result.TryGetValue(trackId, out var ids))
                result[trackId] = ids = [];
            ids.Add(reader.GetInt32(1));
        }
        return result;
    }

    public List<int> GetTrackStyleIds(int trackId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT style_id FROM track_styles WHERE track_id = $id ORDER BY style_id";
        cmd.Parameters.AddWithValue("$id", trackId);
        var result = new List<int>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add(reader.GetInt32(0));
        return result;
    }

    public List<Style> GetStyles()
    {
        using var conn = Open();
        return ReadStyles(conn);
    }

    public void AddStyle(string name)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO style_definitions (name) VALUES ($name)";
        cmd.Parameters.AddWithValue("$name", RequiredStyleName(name));
        cmd.ExecuteNonQuery();
    }

    public void RenameStyle(int id, string name)
    {
        var newName = RequiredStyleName(name);
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        string oldName;
        using (var find = conn.CreateCommand())
        {
            find.Transaction = tx;
            find.CommandText = "SELECT name FROM style_definitions WHERE id = $id";
            find.Parameters.AddWithValue("$id", id);
            oldName = find.ExecuteScalar() as string
                ?? throw new InvalidOperationException("Style no longer exists.");
        }

        ExecuteInsert(conn, tx,
            "UPDATE style_definitions SET name = $name WHERE id = $id",
            ("$name", newName), ("$id", id));
        tx.Commit();
    }

    public string? DeleteStyleIfUnused(int id)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        string? name;
        using (var find = conn.CreateCommand())
        {
            find.Transaction = tx;
            find.CommandText = "SELECT name FROM style_definitions WHERE id = $id";
            find.Parameters.AddWithValue("$id", id);
            name = find.ExecuteScalar() as string;
        }
        if (name is null)
            return null;

        var usageCount = SelectId(conn, tx,
            "SELECT COUNT(*) FROM track_styles WHERE style_id = $id", ("$id", id));
        if (usageCount > 0)
            return $"Cannot delete: used by {usageCount} track(s).";

        ExecuteInsert(conn, tx, "DELETE FROM style_definitions WHERE id = $id", ("$id", id));
        tx.Commit();
        return null;
    }

    private static List<Style> ReadStyles(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name FROM style_definitions ORDER BY name";
        return ReadLookupList(cmd, (id, name) => new Style(id, name));
    }

    private static string RequiredStyleName(string? value)
    {
        var name = value?.Trim() ?? string.Empty;
        if (name.Length == 0)
            throw new ArgumentException("Style name is required.", nameof(value));
        if (name.Contains(','))
            throw new ArgumentException("Style names cannot contain commas.", nameof(value));
        return name;
    }

    internal static void EnsureTrackVersionSchema(SqliteConnection conn)
    {
        EnsureColumn(conn, "tracks", "is_original", "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(conn, "tracks", "parent_track_id", "INTEGER NULL REFERENCES tracks(id) ON DELETE SET NULL");
        EnsureColumn(conn, "tracks", "edit_types", "INTEGER NOT NULL DEFAULT 0");
        ExecuteNonQuery(conn, "CREATE INDEX IF NOT EXISTS ix_tracks_parent ON tracks(parent_track_id)");
    }

    public void SetTrackVersion(int id, bool isOriginal, int? parentTrackId, TrackEditTypes editTypes)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        SetTrackVersion(conn, tx, id, isOriginal, parentTrackId, editTypes);
        tx.Commit();
    }

    private static void SetTrackVersion(SqliteConnection conn, SqliteTransaction tx,
        int id, bool isOriginal, int? parentTrackId, TrackEditTypes editTypes)
    {
        if (isOriginal)
        {
            parentTrackId = null;
            editTypes = TrackEditTypes.None;
        }
        if (parentTrackId is int parent)
        {
            using var command = conn.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "SELECT COUNT(*) FROM tracks WHERE id = $parent AND is_original = 1 AND id <> $id";
            command.Parameters.AddWithValue("$parent", parent);
            command.Parameters.AddWithValue("$id", id);
            if ((long)command.ExecuteScalar()! != 1)
                throw new InvalidOperationException("Choose an existing original track as parent.");
        }
        if (!isOriginal)
        {
            using var command = conn.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "SELECT COUNT(*) FROM tracks WHERE parent_track_id = $id";
            command.Parameters.AddWithValue("$id", id);
            if ((long)command.ExecuteScalar()! > 0)
                throw new InvalidOperationException("Reassign this original's versions before changing it to an edit.");
        }
        ExecuteInsert(conn, tx, @"UPDATE tracks SET is_original = $original,
            parent_track_id = $parent, edit_types = $types, updated_at = $now WHERE id = $id",
            ("$id", id), ("$original", isOriginal ? 1 : 0), ("$parent", parentTrackId),
            ("$types", (int)editTypes), ("$now", DateTime.UtcNow.ToString("O")));
    }

    public void UpdateTrack(
        int id,
        string title,
        string? artist,
        string? remix,
        List<int> genreIds,
        int? ratingId,
        List<int> styleIds,
        bool isPublic)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var now = DateTime.UtcNow.ToString("O");

        ExecuteInsert(conn, tx,
            @"UPDATE tracks
              SET title = $title,
                  artist = $artist,
                  remix = $remix,
                  rating_band = CASE WHEN rating_id IS $ratingId THEN rating_band ELSE NULL END,
                  rating_id = $ratingId,
                  library_state = CASE WHEN $ratingId IS NULL THEN 'PendingRating' ELSE 'Active' END,
                  is_public = $isPublic,
                  updated_at = $updatedAt,
                  needs_reevaluation = CASE WHEN $ratingId IS NULL THEN 1 ELSE 0 END
              WHERE id = $id",
            ("$id", id), ("$title", title), ("$artist", artist), ("$remix", remix),
            ("$ratingId", ratingId), ("$isPublic", isPublic ? 1 : 0), ("$updatedAt", now));

        SetTrackStyles(conn, tx, id, styleIds);

        if (ratingId is not null)
            ExecuteInsert(conn, tx, @"
                UPDATE channel_videos
                SET is_checked = 1, download_status = 'Ready', download_error = NULL, updated_at = $updatedAt
                WHERE canonical_url = (SELECT canonical_url FROM tracks WHERE id = $id)",
                ("$id", id), ("$updatedAt", now));

        tx.Commit();
    }

    public void SetTrackNeedsReview(int id, bool needsReview)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE tracks
                            SET needs_reevaluation = CASE
                                    WHEN library_state = 'PendingRating' THEN 1
                                    WHEN library_state = 'Rejected' THEN 0
                                    ELSE $needsReview
                                END,
                                updated_at = $updatedAt
                            WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$needsReview", needsReview ? 1 : 0);
        cmd.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void SetTrackAnalysisDisabled(int id, bool analysisDisabled)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE tracks
                            SET analysis_disabled = CASE
                                    WHEN library_state = 'Rejected' THEN 1
                                    ELSE $analysisDisabled
                                END,
                                updated_at = $updatedAt
                            WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$analysisDisabled", analysisDisabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void DeleteTrack(int id)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        DeleteTrack(conn, tx, id);
        tx.Commit();
    }

    private static void SetTrackStyles(
        SqliteConnection conn,
        SqliteTransaction tx,
        long trackId,
        IReadOnlyCollection<int> styleIds)
    {
        ExecuteInsert(conn, tx, "DELETE FROM track_styles WHERE track_id = $trackId", ("$trackId", trackId));
        foreach (var styleId in styleIds.Distinct())
            ExecuteInsert(conn, tx,
                "INSERT INTO track_styles (track_id, style_id) VALUES ($trackId, $styleId)",
                ("$trackId", trackId), ("$styleId", styleId));
    }

    public void SetTrackRating(int id, int ratingId)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var now = DateTime.UtcNow.ToString("O");
        ExecuteInsert(conn, tx, @"
            UPDATE tracks
            SET rating_band = CASE WHEN rating_id IS $ratingId THEN rating_band ELSE NULL END,
                rating_id = $ratingId, library_state = 'Active', needs_reevaluation = 0, updated_at = $now
            WHERE id = $id",
            ("$id", id), ("$ratingId", ratingId), ("$now", now));
        ExecuteInsert(conn, tx, @"
            UPDATE channel_videos
            SET is_checked = 1, download_status = 'Ready', download_error = NULL, updated_at = $now
            WHERE canonical_url = (SELECT canonical_url FROM tracks WHERE id = $id)",
            ("$id", id), ("$now", now));
        tx.Commit();
    }

    public void SetTrackRatingBand(int id, RatingBand? ratingBand)
    {
        using var conn = Open();
        ExecuteNonQuery(conn, @"
            UPDATE tracks
            SET rating_band = CASE WHEN rating_id IS NULL THEN NULL ELSE $ratingBand END,
                updated_at = $now
            WHERE id = $id",
            ("$id", id),
            ("$ratingBand", ratingBand?.ToString()),
            ("$now", DateTime.UtcNow.ToString("O")));
    }

    public void DeleteTracks(IReadOnlyCollection<int> ids)
    {
        if (ids.Count == 0)
            return;

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var hasImportQueue = TableExists(conn, "import_queue_items");
        var hasTrackTags = TableExists(conn, "track_tags");
        var hasTrackGenres = TableExists(conn, "track_genres");
        var hasTrackAnalysis = TableExists(conn, "track_analysis");
        var hasGenrePredictions = hasTrackAnalysis && TableExists(conn, "track_genre_predictions");
        var hasAnalysisSignals = hasTrackAnalysis && TableExists(conn, "track_analysis_signals");
        var hasDerivedAttributes = hasTrackAnalysis && TableExists(conn, "track_derived_attributes");
        foreach (var id in ids)
        {
            DeleteTrack(
                conn,
                tx,
                id,
                hasImportQueue,
                hasTrackTags,
                hasTrackGenres,
                hasTrackAnalysis,
                hasGenrePredictions,
                hasAnalysisSignals,
                hasDerivedAttributes);
        }
        tx.Commit();
    }

    private static void DeleteTrack(SqliteConnection conn, SqliteTransaction tx, int id)
    {
        var hasTrackAnalysis = TableExists(conn, "track_analysis");
        DeleteTrack(
            conn,
            tx,
            id,
            TableExists(conn, "import_queue_items"),
            TableExists(conn, "track_tags"),
            TableExists(conn, "track_genres"),
            hasTrackAnalysis,
            hasTrackAnalysis && TableExists(conn, "track_genre_predictions"),
            hasTrackAnalysis && TableExists(conn, "track_analysis_signals"),
            hasTrackAnalysis && TableExists(conn, "track_derived_attributes"));
    }

    private static void DeleteTrack(
        SqliteConnection conn,
        SqliteTransaction tx,
        int id,
        bool hasImportQueue,
        bool hasTrackTags,
        bool hasTrackGenres,
        bool hasTrackAnalysis,
        bool hasGenrePredictions,
        bool hasAnalysisSignals,
        bool hasDerivedAttributes)
    {
        ExecuteInsert(conn, tx, @"
            UPDATE channel_videos
            SET is_checked = 1, download_status = 'NotQueued', download_error = NULL, updated_at = $now
            WHERE canonical_url = (SELECT canonical_url FROM tracks WHERE id = $id)",
            ("$id", id), ("$now", DateTime.UtcNow.ToString("O")));

        // Older import_queue_items tables were created without ON DELETE SET NULL.
        // Detach queued/history rows explicitly so deleting a library track does not
        // fail with a foreign-key constraint.
        if (hasImportQueue)
        {
            ExecuteInsert(conn, tx,
                "UPDATE import_queue_items SET track_id = NULL WHERE track_id = $id",
                ("$id", id));
        }

        if (hasTrackTags)
            ExecuteInsert(conn, tx, "DELETE FROM track_tags WHERE track_id = $id", ("$id", id));
        if (hasTrackGenres)
            ExecuteInsert(conn, tx, "DELETE FROM track_genres WHERE track_id = $id", ("$id", id));
        if (hasTrackAnalysis)
        {
            if (hasGenrePredictions)
                ExecuteInsert(conn, tx, @"DELETE FROM track_genre_predictions
                    WHERE track_analysis_id IN (SELECT id FROM track_analysis WHERE track_id = $id)", ("$id", id));
            if (hasAnalysisSignals)
                ExecuteInsert(conn, tx, @"DELETE FROM track_analysis_signals
                    WHERE track_analysis_id IN (SELECT id FROM track_analysis WHERE track_id = $id)", ("$id", id));
            if (hasDerivedAttributes)
                ExecuteInsert(conn, tx, @"DELETE FROM track_derived_attributes
                    WHERE track_analysis_id IN (SELECT id FROM track_analysis WHERE track_id = $id)", ("$id", id));
            ExecuteInsert(conn, tx, "DELETE FROM track_analysis WHERE track_id = $id", ("$id", id));
        }

        ExecuteInsert(conn, tx, "DELETE FROM tracks WHERE id = $id", ("$id", id));
    }

    public List<Genre> GetGenres()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT model_subgenres.id, model_genres.name || ' → ' || model_subgenres.name
                            FROM model_subgenres
                            JOIN model_genres ON model_genres.id = model_subgenres.model_genre_id
                            ORDER BY model_genres.name, model_subgenres.name";
        return ReadLookupList(cmd, (id, name) => new Genre(id, name));
    }

    public List<Tag> GetTags()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name FROM tags ORDER BY name";
        return ReadTags(cmd);
    }

    public void AddTag(string name)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO tags (name) VALUES ($name)";
        cmd.Parameters.AddWithValue("$name", name.Trim());
        cmd.ExecuteNonQuery();
    }

    public void MarkTrackAnalysisFailed(int id)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        ExecuteInsert(conn, tx, @"UPDATE tracks
                                  SET analysis_disabled = 1,
                                      needs_reevaluation = 1,
                                      updated_at = $now
                                  WHERE id = $id AND library_state <> 'Rejected'",
            ("$id", id), ("$now", DateTime.UtcNow.ToString("O")));
        tx.Commit();
    }

    public void RenameTag(int id, string name)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE tags SET name = $name WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", name.Trim());
        cmd.ExecuteNonQuery();
    }

    public string? DeleteTagIfUnused(int id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM track_tags WHERE tag_id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        reader.Read();
        var tracks = reader.GetInt64(0);
        if (tracks > 0)
            return $"Cannot delete: used by {tracks} track(s).";
        reader.Close();

        cmd.CommandText = "DELETE FROM tags WHERE id = $id";
        cmd.ExecuteNonQuery();
        return null;
    }

    public Dictionary<int, List<int>> GetAllTrackTagIds()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT track_id, tag_id FROM track_tags";
        return ReadTrackIdMap(cmd);
    }

    public List<int> GetTrackTagIds(int trackId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT tag_id FROM track_tags WHERE track_id = $trackId";
        cmd.Parameters.AddWithValue("$trackId", trackId);
        using var reader = cmd.ExecuteReader();
        var ids = new List<int>();
        while (reader.Read()) ids.Add(reader.GetInt32(0));
        return ids;
    }

    public List<TrackTag> GetTrackTags(int trackId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT tags.id, tags.name
            FROM track_tags
            JOIN tags ON tags.id = track_tags.tag_id
            WHERE track_tags.track_id = $trackId
            ORDER BY tags.name";
        cmd.Parameters.AddWithValue("$trackId", trackId);
        using var reader = cmd.ExecuteReader();
        var tags = new List<TrackTag>();
        while (reader.Read())
            tags.Add(new TrackTag(reader.GetInt32(0), reader.GetString(1)));
        return tags;
    }

    public void SetTrackManualTags(int trackId, IReadOnlyCollection<int> tagIds)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var now = DateTime.UtcNow.ToString("O");

        ExecuteInsert(conn, tx, "DELETE FROM track_tags WHERE track_id = $trackId",
            ("$trackId", trackId));

        foreach (var tagId in tagIds.Distinct())
        {
            ExecuteInsert(conn, tx, @"
                INSERT INTO track_tags (track_id, tag_id)
                VALUES ($trackId, $tagId)
                ON CONFLICT(track_id, tag_id) DO NOTHING",
                ("$trackId", trackId), ("$tagId", tagId));
        }

        TouchTrack(conn, tx, trackId, now);
        tx.Commit();
    }

    public List<TagSignalSource> GetTagSignalSources()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT model_name, signal_key, MAX(description)
            FROM track_analysis_signals
            GROUP BY model_name, signal_key
            ORDER BY model_name, signal_key";
        using var reader = cmd.ExecuteReader();
        var sources = new List<TagSignalSource>();
        while (reader.Read())
            sources.Add(new TagSignalSource(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return sources;
    }

    public List<TagRuleGroup> GetTagRuleGroups()
    {
        return [];
    }

    public int CreateTagRuleGroup(int tagId, TagRuleMatchMode matchMode, string sourceType, string sourceKey, double threshold)
    {
        return 0;
    }

    public void AddTagRuleCondition(int groupId, string sourceType, string sourceKey, double threshold)
    {
    }

    public void DeleteTagRuleCondition(int conditionId)
    {
    }

    public void SetTagRuleGroupEnabled(int groupId, bool enabled)
    {
    }

    public void SetTagRuleGroupMatchMode(int groupId, TagRuleMatchMode matchMode)
    {
    }

    public void DeleteTagRuleGroup(int groupId)
    {
    }

    public void RefreshAllTagSuggestions()
    {
    }

    public List<TrackTagSuggestion> GetTrackTagSuggestions(int trackId)
    {
        return [];
    }

    public void AcceptTrackTagSuggestion(int trackId, int tagId, int ruleGroupId)
    {
    }

    public void RejectTrackTagSuggestion(int trackId, int ruleGroupId)
    {
    }

    public List<Rating> GetRatings()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, sort_order FROM ratings ORDER BY sort_order";
        using var reader = cmd.ExecuteReader();
        var ratings = new List<Rating>();
        while (reader.Read()) ratings.Add(new Rating(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2)));
        return ratings;
    }

    public List<ModelGenre> GetModelGenres()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name FROM model_genres ORDER BY name";
        return ReadLookupList(cmd, (id, name) => new ModelGenre(id, name));
    }

    public List<ModelSubgenre> GetModelSubgenres(int? modelGenreId = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT id, model_genre_id, name, description, classification_hint, bpm_min, bpm_max
                            FROM model_subgenres
                            WHERE $modelGenreId IS NULL OR model_genre_id = $modelGenreId
                            ORDER BY name";
        cmd.Parameters.AddWithValue("$modelGenreId", modelGenreId is null ? DBNull.Value : modelGenreId.Value);
        using var reader = cmd.ExecuteReader();
        var subgenres = new List<ModelSubgenre>();
        while (reader.Read()) subgenres.Add(new ModelSubgenre(
            reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetInt32(6)));
        return subgenres;
    }

    public void AddModelSubgenre(int modelGenreId, string name)
    {
        using var conn = Open();
        ExecuteNonQuery(conn, @"
            INSERT INTO model_subgenres (model_genre_id, name)
            VALUES ($modelGenreId, $name)",
            ("$modelGenreId", modelGenreId), ("$name", name.Trim()));
    }

    public void UpdateModelSubgenre(int id, string name, string? description, string? classificationHint, int? bpmMin, int? bpmMax)
    {
        using var conn = Open();
        ExecuteNonQuery(conn, @"
            UPDATE model_subgenres
            SET name = $name,
                description = $description,
                classification_hint = $hint,
                bpm_min = $bpmMin,
                bpm_max = $bpmMax
            WHERE id = $id",
            ("$id", id),
            ("$name", name.Trim()),
            ("$description", string.IsNullOrWhiteSpace(description) ? DBNull.Value : description.Trim()),
            ("$hint", string.IsNullOrWhiteSpace(classificationHint) ? DBNull.Value : classificationHint.Trim()),
            ("$bpmMin", bpmMin is null ? DBNull.Value : bpmMin.Value),
            ("$bpmMax", bpmMax is null ? DBNull.Value : bpmMax.Value));
    }

    public List<ModelSubgenreDistinction> GetModelSubgenreDistinctions()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT distinctions.model_subgenre_id, distinctions.distinguish_from_model_subgenre_id,
                   genres.name, subgenres.name, distinctions.difference
            FROM model_subgenre_distinctions distinctions
            JOIN model_subgenres subgenres ON subgenres.id = distinctions.distinguish_from_model_subgenre_id
            JOIN model_genres genres ON genres.id = subgenres.model_genre_id
            ORDER BY distinctions.model_subgenre_id, genres.name, subgenres.name";
        using var reader = cmd.ExecuteReader();
        var distinctions = new List<ModelSubgenreDistinction>();
        while (reader.Read()) distinctions.Add(new ModelSubgenreDistinction(
            reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        return distinctions;
    }

    public List<ManualModelGenreUsage> GetTopManualModelGenres(int limit = 10)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT model_subgenres.id, model_subgenres.model_genre_id, model_subgenres.name,
                   model_genres.name, COUNT(*) AS usage_count
            FROM track_genres
            JOIN model_subgenres ON model_subgenres.id = track_genres.genre_id
            JOIN model_genres ON model_genres.id = model_subgenres.model_genre_id
            WHERE track_genres.is_enabled = 1
              AND track_genres.is_manual = 1
            GROUP BY model_subgenres.id, model_subgenres.model_genre_id, model_subgenres.name, model_genres.name
            ORDER BY usage_count DESC, model_genres.name, model_subgenres.name
            LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", Math.Max(1, limit));
        using var reader = cmd.ExecuteReader();
        var usages = new List<ManualModelGenreUsage>();
        while (reader.Read())
        {
            usages.Add(new ManualModelGenreUsage(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                Convert.ToInt32(reader.GetInt64(4))));
        }
        return usages;
    }

    public List<StoredModelGenrePrediction> GetTrackGenrePredictions(int trackId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT model_genres.id, model_genres.name, model_subgenres.id, model_subgenres.name, predictions.score
            FROM track_genre_predictions predictions
            JOIN track_analysis analysis ON analysis.id = predictions.track_analysis_id
            JOIN model_subgenres ON model_subgenres.id = predictions.model_subgenre_id
            JOIN model_genres ON model_genres.id = model_subgenres.model_genre_id
            WHERE analysis.track_id = $trackId
            ORDER BY predictions.score DESC";
        cmd.Parameters.AddWithValue("$trackId", trackId);
        using var reader = cmd.ExecuteReader();
        var predictions = new List<StoredModelGenrePrediction>();
        while (reader.Read())
        {
            predictions.Add(new StoredModelGenrePrediction(
                reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3), reader.GetDouble(4)));
        }
        return predictions;
    }

    public TrackAudioAnalysis? GetTrackAudioAnalysis(int trackId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT bpm, integrated_loudness, loudness_range
            FROM track_analysis
            WHERE track_id = $trackId";
        cmd.Parameters.AddWithValue("$trackId", trackId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new TrackAudioAnalysis(
            reader.IsDBNull(0) ? null : reader.GetDouble(0),
            reader.IsDBNull(1) ? null : reader.GetDouble(1),
            reader.IsDBNull(2) ? null : reader.GetDouble(2));
    }

    public List<ExperimentalAnalysisModel> GetTrackAnalysisSignals(int trackId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT model_family, category, model_name, model_type, description, signal_key, score
            FROM track_analysis_signals signals
            JOIN track_analysis analysis ON analysis.id = signals.track_analysis_id
            WHERE analysis.track_id = $trackId
            ORDER BY model_family, category, model_name, score DESC";
        cmd.Parameters.AddWithValue("$trackId", trackId);
        using var reader = cmd.ExecuteReader();
        var grouped = new Dictionary<(string Family, string Category, string Model, string Type, string Description), List<ExperimentalAnalysisValue>>();
        while (reader.Read())
        {
            var key = (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));
            if (!grouped.TryGetValue(key, out var values)) grouped[key] = values = [];
            values.Add(new ExperimentalAnalysisValue(reader.GetString(5), reader.GetDouble(6)));
        }
        return grouped.Select(item => new ExperimentalAnalysisModel(
            item.Key.Family, item.Key.Category, item.Key.Model, item.Key.Type, item.Key.Description, item.Value)).ToList();
    }

    private static void TouchTrack(SqliteConnection conn, SqliteTransaction tx, int trackId, string updatedAt) =>
        ExecuteInsert(conn, tx, "UPDATE tracks SET updated_at = $updatedAt WHERE id = $trackId",
            ("$updatedAt", updatedAt), ("$trackId", trackId));

    private static void RefreshAllModelGenres(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();
        RefreshModelGenres(conn, tx, null);
        tx.Commit();
    }

    private static void RefreshModelGenres(SqliteConnection conn, SqliteTransaction tx, int? trackId)
    {
        ExecuteInsert(conn, tx, @"
            DELETE FROM track_genres WHERE ($trackId IS NULL OR track_id = $trackId)
            AND is_manual = 0
            AND NOT EXISTS (
                SELECT 1 FROM track_genre_predictions predictions JOIN track_analysis analysis ON analysis.id = predictions.track_analysis_id
                WHERE analysis.track_id = track_genres.track_id AND predictions.model_subgenre_id = track_genres.genre_id AND predictions.score > 0.25)",
            ("$trackId", trackId));
        ExecuteInsert(conn, tx, @"
            INSERT INTO track_genres (track_id, genre_id, assigned_at, is_enabled, is_manual)
            SELECT DISTINCT analysis.track_id, predictions.model_subgenre_id, $assignedAt, 1, 0
            FROM track_genre_predictions predictions
            JOIN track_analysis analysis ON analysis.id = predictions.track_analysis_id
            WHERE predictions.score > 0.25 AND ($trackId IS NULL OR analysis.track_id = $trackId)
            ON CONFLICT(track_id, genre_id) DO UPDATE SET assigned_at = excluded.assigned_at",
            ("$trackId", trackId), ("$assignedAt", DateTime.UtcNow.ToString("O")));
    }

    private static long GetTrackAnalysisId(SqliteConnection conn, SqliteTransaction tx, int trackId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id FROM track_analysis WHERE track_id = $trackId";
        cmd.Parameters.AddWithValue("$trackId", trackId);
        return (long)cmd.ExecuteScalar()!;
    }

    private static List<T> ReadLookupList<T>(SqliteCommand cmd, Func<int, string, T> create)
    {
        using var reader = cmd.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) rows.Add(create(reader.GetInt32(0), reader.GetString(1)));
        return rows;
    }

    private static List<Tag> ReadTags(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        var tags = new List<Tag>();
        while (reader.Read())
            tags.Add(new Tag(reader.GetInt32(0), reader.GetString(1)));
        return tags;
    }

    public Dictionary<int, TrackAudioAnalysis> GetAllTrackAudioAnalyses()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT track_id, bpm, integrated_loudness, loudness_range FROM track_analysis";
        using var reader = cmd.ExecuteReader();
        var results = new Dictionary<int, TrackAudioAnalysis>();
        while (reader.Read())
            results[reader.GetInt32(0)] = new TrackAudioAnalysis(
                reader.IsDBNull(1) ? null : reader.GetDouble(1),
                reader.IsDBNull(2) ? null : reader.GetDouble(2),
                reader.IsDBNull(3) ? null : reader.GetDouble(3));
        return results;
    }

    public Dictionary<int, Dictionary<string, double>> GetAllMirexScores()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT analysis.track_id, signals.signal_key, signals.score
            FROM track_analysis_signals signals
            JOIN track_analysis analysis ON analysis.id = signals.track_analysis_id
            WHERE signals.model_name = 'moods mirex'";
        using var reader = cmd.ExecuteReader();
        var results = new Dictionary<int, Dictionary<string, double>>();
        while (reader.Read())
        {
            var trackId = reader.GetInt32(0);
            if (!results.TryGetValue(trackId, out var scores))
                results[trackId] = scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            scores[reader.GetString(1)] = reader.GetDouble(2);
        }
        return results;
    }

    public int ApplyCloudDeviceSnapshot(CloudDeviceLibrarySnapshot snapshot)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var changed = 0;
        var trackIds = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var track in snapshot.Tracks)
        {
            var canonicalUrl = track.CanonicalUrl ?? YouTubeUrlNormalizer.GetCanonicalUrl(track.TrackKey);
            using var find = conn.CreateCommand();
            find.Transaction = tx;
            find.CommandText = """
                SELECT id, updated_at, source_video_id
                FROM tracks
                WHERE source_video_id = $trackKey
                   OR canonical_url = $canonicalUrl
                   OR (source_video_id IS NULL AND file_name = $fileName)
                ORDER BY CASE
                    WHEN source_video_id = $trackKey THEN 0
                    WHEN canonical_url = $canonicalUrl THEN 1
                    ELSE 2
                END
                LIMIT 1
                """;
            find.Parameters.AddWithValue("$trackKey", track.TrackKey);
            find.Parameters.AddWithValue("$canonicalUrl", canonicalUrl);
            find.Parameters.AddWithValue("$fileName", track.FileName);
            using var reader = find.ExecuteReader();
            int? trackId = null;
            string? localUpdatedAt = null;
            string? localTrackKey = null;
            if (reader.Read())
            {
                trackId = reader.GetInt32(0);
                localUpdatedAt = reader.IsDBNull(1) ? null : reader.GetString(1);
                localTrackKey = reader.IsDBNull(2) ? null : reader.GetString(2);
            }
            reader.Close();

            if (trackId is null)
            {
                trackId = (int)InsertAndGetId(conn, tx, """
                    INSERT INTO tracks
                        (canonical_url, title, original_title, artist, remix, file_name,
                         rating_id, duration_seconds, downloaded_at, updated_at,
                         needs_reevaluation, library_state, source_video_id, language_code,
                         is_public, is_original, edit_types, thumbnail,
                         listen_count, listened_seconds, skip_count, last_listened_at)
                    VALUES
                        ($canonicalUrl, $title, $originalTitle, $artist, $remix, $fileName,
                         (SELECT id FROM ratings WHERE name = $rating COLLATE NOCASE),
                         $duration, $downloadedAt, $updatedAt, $needsReview, $libraryState,
                         $trackKey, $language, $isPublic, $isOriginal, 0, $thumbnail,
                         $playCount, $listenedSeconds, $skipCount, $lastListenedAt)
                    """,
                    ("$canonicalUrl", canonicalUrl),
                    ("$title", track.Title), ("$originalTitle", track.OriginalTitle),
                    ("$artist", track.Artist), ("$remix", track.Remix), ("$fileName", track.FileName),
                    ("$rating", track.Rating), ("$duration", track.DurationSeconds),
                    ("$downloadedAt", track.UpdatedAt), ("$updatedAt", track.UpdatedAt),
                    ("$needsReview", track.NeedsReview ? 1 : 0), ("$libraryState", track.LibraryState),
                    ("$trackKey", track.TrackKey), ("$language", track.LanguageCode),
                    ("$isPublic", track.IsPublic ? 1 : 0), ("$isOriginal", track.IsOriginal ? 1 : 0),
                    ("$thumbnail", track.Thumbnail), ("$playCount", track.PlayCount),
                    ("$listenedSeconds", track.ListenedSeconds), ("$skipCount", track.SkipCount),
                    ("$lastListenedAt", track.LastListenedAt));
                changed++;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(localTrackKey))
                {
                    ExecuteInsert(conn, tx, """
                        UPDATE tracks
                        SET source_video_id = $trackKey,
                            canonical_url = COALESCE(canonical_url, $canonicalUrl)
                        WHERE id = $trackId AND source_video_id IS NULL
                        """,
                        ("$trackId", trackId.Value), ("$trackKey", track.TrackKey),
                        ("$canonicalUrl", canonicalUrl));
                }

                if (IsRemoteNewer(track.UpdatedAt, localUpdatedAt))
                {
                    ExecuteInsert(conn, tx, """
                    UPDATE tracks SET
                        title = $title,
                        original_title = $originalTitle,
                        artist = $artist,
                        remix = $remix,
                        rating_id = (SELECT id FROM ratings WHERE name = $rating COLLATE NOCASE),
                        rating_band = $ratingBand,
                        duration_seconds = COALESCE($duration, duration_seconds),
                        updated_at = $updatedAt,
                        needs_reevaluation = $needsReview,
                        library_state = $libraryState,
                        language_code = $language,
                        is_public = $isPublic,
                        is_original = $isOriginal,
                        thumbnail = COALESCE($thumbnail, thumbnail),
                        listen_count = $playCount,
                        listened_seconds = $listenedSeconds,
                        skip_count = $skipCount,
                        last_listened_at = $lastListenedAt
                    WHERE id = $trackId
                    """,
                    ("$trackId", trackId.Value), ("$title", track.Title),
                    ("$originalTitle", track.OriginalTitle), ("$artist", track.Artist),
                    ("$remix", track.Remix), ("$rating", track.Rating),
                    ("$ratingBand", track.RatingBand), ("$duration", track.DurationSeconds),
                    ("$updatedAt", track.UpdatedAt), ("$needsReview", track.NeedsReview ? 1 : 0),
                    ("$libraryState", track.LibraryState), ("$language", track.LanguageCode),
                    ("$isPublic", track.IsPublic ? 1 : 0), ("$isOriginal", track.IsOriginal ? 1 : 0),
                    ("$thumbnail", track.Thumbnail), ("$playCount", track.PlayCount),
                    ("$listenedSeconds", track.ListenedSeconds), ("$skipCount", track.SkipCount),
                    ("$lastListenedAt", track.LastListenedAt));
                    changed++;
                }
            }

            trackIds[track.TrackKey] = trackId.Value;
            if (!IsRemoteNewer(track.UpdatedAt, localUpdatedAt) && localUpdatedAt is not null)
                continue;

            ReplaceNamedLinks(conn, tx, "tags", "track_tags", "tag_id", trackId.Value, track.Tags, createMissing: true);
            ReplaceNamedLinks(conn, tx, "style_definitions", "track_styles", "style_id", trackId.Value, track.Styles, createMissing: true);
            ReplaceNamedLinks(conn, tx, "model_subgenres", "track_genres", "genre_id", trackId.Value, track.Genres, createMissing: false);

            if (track.Analysis is not null)
            {
                ExecuteInsert(conn, tx, """
                    INSERT INTO track_analysis
                        (track_id, analyzed_at, analyzer_name, analyzer_version,
                         bpm, integrated_loudness, loudness_range)
                    VALUES ($trackId, $updatedAt, 'Resona Server', 'cloud',
                            $bpm, $loudness, $range)
                    ON CONFLICT(track_id) DO UPDATE SET
                        analyzed_at = excluded.analyzed_at,
                        analyzer_name = excluded.analyzer_name,
                        analyzer_version = excluded.analyzer_version,
                        bpm = excluded.bpm,
                        integrated_loudness = excluded.integrated_loudness,
                        loudness_range = excluded.loudness_range
                    """,
                    ("$trackId", trackId.Value), ("$updatedAt", track.UpdatedAt),
                    ("$bpm", track.Analysis.Bpm), ("$loudness", track.Analysis.IntegratedLoudness),
                    ("$range", track.Analysis.LoudnessRange));
                var analysisId = GetTrackAnalysisId(conn, tx, trackId.Value);
                ExecuteInsert(conn, tx,
                    "DELETE FROM track_analysis_signals WHERE track_analysis_id = $analysisId AND model_name = 'moods mirex'",
                    ("$analysisId", analysisId));
                foreach (var score in track.EmotionalCharacter)
                    ExecuteInsert(conn, tx, """
                        INSERT INTO track_analysis_signals
                            (track_analysis_id, model_family, category, model_name, model_type,
                             description, signal_key, score)
                        VALUES ($analysisId, 'MusicNN', 'Mood', 'moods mirex', 'classification',
                                'MIREX mood cluster', $key, $score)
                        """,
                        ("$analysisId", analysisId), ("$key", score.Key), ("$score", score.Value));
            }
        }

        foreach (var track in snapshot.Tracks.Where(track => !track.IsOriginal))
        {
            if (!trackIds.TryGetValue(track.TrackKey, out var trackId))
                continue;
            var parentId = track.ParentTrackKey is { } parentKey
                ? trackIds.GetValueOrDefault(parentKey)
                : 0;
            var editTypes = TrackVersions.Types
                .Where(type => track.EditTypes?.Contains(type.Name, StringComparer.OrdinalIgnoreCase) == true)
                .Aggregate(TrackEditTypes.None, (current, type) => current | type.Type);
            ExecuteInsert(conn, tx, """
                UPDATE tracks SET parent_track_id = $parentId, edit_types = $editTypes WHERE id = $trackId
                """,
                ("$trackId", trackId), ("$parentId", parentId == 0 ? null : parentId),
                ("$editTypes", (int)editTypes));
        }

        foreach (var collection in snapshot.Collections)
        {
            var now = DateTime.UtcNow.ToString("O");
            ExecuteInsert(conn, tx, """
                INSERT INTO collections (stable_id, name, created_at, updated_at)
                VALUES ($stableId, $name, $now, $now)
                ON CONFLICT(stable_id) DO UPDATE SET name = excluded.name, updated_at = excluded.updated_at
                """, ("$stableId", collection.StableId), ("$name", collection.Name), ("$now", now));
            var collectionId = InsertAndGetId(conn, tx,
                "SELECT id FROM collections WHERE stable_id = $stableId", ("$stableId", collection.StableId));
            ExecuteInsert(conn, tx, "DELETE FROM collection_tracks WHERE collection_id = $collectionId",
                ("$collectionId", collectionId));
            for (var position = 0; position < collection.TrackKeys.Count; position++)
            {
                if (!trackIds.TryGetValue(collection.TrackKeys[position], out var trackId))
                    continue;
                ExecuteInsert(conn, tx, """
                    INSERT INTO collection_tracks (collection_id, track_id, position, added_at)
                    VALUES ($collectionId, $trackId, $position, $now)
                    """, ("$collectionId", collectionId), ("$trackId", trackId),
                    ("$position", position), ("$now", now));
            }
        }

        tx.Commit();
        return changed;
    }

    private static bool IsRemoteNewer(string remote, string? local) =>
        local is null
        || !DateTimeOffset.TryParse(local, out var localDate)
        || DateTimeOffset.TryParse(remote, out var remoteDate) && remoteDate > localDate;

    private static void ReplaceNamedLinks(
        SqliteConnection conn,
        SqliteTransaction tx,
        string lookupTable,
        string linkTable,
        string foreignKey,
        int trackId,
        IReadOnlyList<string> names,
        bool createMissing)
    {
        ExecuteInsert(conn, tx, $"DELETE FROM {linkTable} WHERE track_id = $trackId", ("$trackId", trackId));
        foreach (var name in names.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (createMissing)
                ExecuteInsert(conn, tx, $"INSERT OR IGNORE INTO {lookupTable} (name) VALUES ($name)", ("$name", name));
            var extra = linkTable == "track_genres" ? ", assigned_at, is_enabled, is_manual" : string.Empty;
            var values = linkTable == "track_genres" ? ", $now, 1, 1" : string.Empty;
            var source = linkTable == "track_genres"
                ? """
                  model_subgenres lookup
                  JOIN model_genres parent ON parent.id = lookup.model_genre_id
                  WHERE parent.name || ' → ' || lookup.name = $name COLLATE NOCASE
                  """
                : $"{lookupTable} lookup WHERE lookup.name = $name COLLATE NOCASE";
            ExecuteInsert(conn, tx, $"""
                INSERT OR IGNORE INTO {linkTable} (track_id, {foreignKey}{extra})
                SELECT $trackId, lookup.id{values} FROM {source}
                """, ("$trackId", trackId), ("$name", name), ("$now", DateTime.UtcNow.ToString("O")));
        }
    }

    private static Dictionary<int, List<int>> ReadTrackIdMap(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        var values = new Dictionary<int, List<int>>();
        while (reader.Read())
        {
            var trackId = reader.GetInt32(0);
            if (!values.TryGetValue(trackId, out var ids))
                values[trackId] = ids = [];
            ids.Add(reader.GetInt32(1));
        }
        return values;
    }

    private static void ExecuteInsert(SqliteConnection conn, SqliteTransaction tx, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        AddParameters(cmd, parameters);
        cmd.ExecuteNonQuery();
    }

    private static void ExecuteNonQuery(SqliteConnection conn, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        AddParameters(cmd, parameters);
        cmd.ExecuteNonQuery();
    }

    private static long InsertAndGetId(SqliteConnection conn, SqliteTransaction tx, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"{sql}; SELECT last_insert_rowid();";
        AddParameters(cmd, parameters);
        return (long)cmd.ExecuteScalar()!;
    }

    private static long SelectId(SqliteConnection conn, SqliteTransaction tx, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        AddParameters(cmd, parameters);
        return (long)cmd.ExecuteScalar()!;
    }

    private static void AddParameters(SqliteCommand cmd, IEnumerable<(string Name, object? Value)> parameters)
    {
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private static T ReadAsset<T>(string fileName)
    {
        using var stream = AssetLoader.Open(new Uri(AssetBaseUri + fileName));
        return JsonSerializer.Deserialize<T>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException($"Could not read asset '{fileName}'.");
    }

    private static (string Genre, string Subgenre) SplitModelClass(string value)
    {
        var separator = value.IndexOf("---", StringComparison.Ordinal);
        if (separator <= 0 || separator == value.Length - 3)
            throw new InvalidOperationException($"Invalid model class '{value}'. Expected 'Genre---Subgenre'.");

        return (value[..separator], value[(separator + 3)..]);
    }

    private sealed record RatingSeedDocument(List<RatingSeed> Ratings);
    private sealed record RatingSeed(string Name, int SortOrder);
    private sealed record ModelSubgenreMetadataSeed(
        string Label,
        string Genre,
        string Subgenre,
        string? Description,
        string? ClassificationHint,
        int? BpmMin,
        int? BpmMax,
        List<ModelSubgenreDistinctionSeed> DistinguishFrom);
    private sealed record ModelSubgenreDistinctionSeed(string Label, string Difference);
}
