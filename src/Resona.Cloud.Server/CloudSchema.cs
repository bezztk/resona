using Npgsql;

namespace Resona.Cloud.Server;

public static class CloudSchema
{
    public static async Task InitializeAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(SchemaSql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS users (
            id                  uuid PRIMARY KEY,
            username            varchar(40) NOT NULL,
            bio                 varchar(500) NOT NULL DEFAULT '',
            profile_image       bytea NULL,
            profile_updated_at  timestamptz NOT NULL,
            created_at          timestamptz NOT NULL DEFAULT now(),
            updated_at          timestamptz NOT NULL DEFAULT now()
        );
        CREATE TABLE IF NOT EXISTS user_devices (
            id                  uuid PRIMARY KEY,
            user_id             uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            device_key_hash     bytea NOT NULL,
            registered_at       timestamptz NOT NULL DEFAULT now(),
            last_seen_at        timestamptz NOT NULL DEFAULT now()
        );
        CREATE INDEX IF NOT EXISTS ix_user_devices_user ON user_devices(user_id);

        CREATE TABLE IF NOT EXISTS public_tracks (
            owner_user_id       uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            source_video_id     varchar(64) NOT NULL,
            canonical_url       varchar(2048) NOT NULL,
            title               varchar(500) NOT NULL,
            original_title      varchar(500) NOT NULL,
            channel_name        varchar(300) NULL,
            channel_url         varchar(2048) NULL,
            duration_seconds    integer NULL,
            uploaded_at         text NULL,
            thumbnail_url       varchar(2048) NULL,
            rating              varchar(50) NULL,
            language_code       varchar(20) NULL,
            source_updated_at   timestamptz NOT NULL,
            synchronized_at     timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (owner_user_id, source_video_id)
        );
        CREATE INDEX IF NOT EXISTS ix_public_tracks_title ON public_tracks(owner_user_id, lower(title));
        CREATE INDEX IF NOT EXISTS ix_public_tracks_rating ON public_tracks(owner_user_id, rating);

        CREATE TABLE IF NOT EXISTS public_track_tags (
            owner_user_id       uuid NOT NULL,
            source_video_id     varchar(64) NOT NULL,
            name                varchar(100) NOT NULL,
            PRIMARY KEY (owner_user_id, source_video_id, name),
            FOREIGN KEY (owner_user_id, source_video_id)
                REFERENCES public_tracks(owner_user_id, source_video_id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS public_track_genres (
            owner_user_id       uuid NOT NULL,
            source_video_id     varchar(64) NOT NULL,
            name                varchar(100) NOT NULL,
            PRIMARY KEY (owner_user_id, source_video_id, name),
            FOREIGN KEY (owner_user_id, source_video_id)
                REFERENCES public_tracks(owner_user_id, source_video_id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS public_track_analysis (
            owner_user_id       uuid NOT NULL,
            source_video_id     varchar(64) NOT NULL,
            bpm                 double precision NULL,
            integrated_loudness double precision NULL,
            loudness_range      double precision NULL,
            PRIMARY KEY (owner_user_id, source_video_id),
            FOREIGN KEY (owner_user_id, source_video_id)
                REFERENCES public_tracks(owner_user_id, source_video_id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS public_track_emotional_character (
            owner_user_id       uuid NOT NULL,
            source_video_id     varchar(64) NOT NULL,
            name                varchar(100) NOT NULL,
            score               double precision NOT NULL,
            PRIMARY KEY (owner_user_id, source_video_id, name),
            FOREIGN KEY (owner_user_id, source_video_id)
                REFERENCES public_tracks(owner_user_id, source_video_id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS library_snapshots (
            user_id             uuid PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
            schema_version      integer NOT NULL,
            track_count         integer NOT NULL CHECK (track_count >= 0),
            generated_at        timestamptz NOT NULL,
            synchronized_at     timestamptz NOT NULL DEFAULT now()
        );

        CREATE TABLE IF NOT EXISTS library_media (
            owner_user_id       uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            track_key           varchar(128) NOT NULL,
            file_name           varchar(255) NOT NULL,
            content_type        varchar(100) NOT NULL,
            file_size_bytes     bigint NOT NULL CHECK (file_size_bytes >= 0),
            sha256              char(64) NOT NULL,
            storage_path        text NOT NULL,
            uploaded_at         timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (owner_user_id, track_key)
        );
        CREATE INDEX IF NOT EXISTS ix_library_media_owner_uploaded
            ON library_media(owner_user_id, uploaded_at);

        CREATE TABLE IF NOT EXISTS device_library_snapshots (
            user_id             uuid PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
            schema_version      integer NOT NULL,
            track_count         integer NOT NULL CHECK (track_count >= 0),
            generated_at        timestamptz NOT NULL,
            snapshot_json       jsonb NOT NULL,
            synchronized_at     timestamptz NOT NULL DEFAULT now(),
            library_revision    bigint NOT NULL DEFAULT 1 CHECK (library_revision > 0),
            presets_revision    bigint NOT NULL DEFAULT 1 CHECK (presets_revision > 0)
        );

        ALTER TABLE device_library_snapshots
            ADD COLUMN IF NOT EXISTS library_revision bigint NOT NULL DEFAULT 1;
        ALTER TABLE device_library_snapshots
            ADD COLUMN IF NOT EXISTS presets_revision bigint NOT NULL DEFAULT 1;

        CREATE TABLE IF NOT EXISTS device_library_track_revisions (
            user_id             uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            track_key           varchar(128) NOT NULL,
            revision            bigint NOT NULL DEFAULT 1 CHECK (revision > 0),
            updated_at          timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (user_id, track_key)
        );

        """;
}
