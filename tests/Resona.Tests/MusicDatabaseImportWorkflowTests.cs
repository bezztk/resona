using Microsoft.Data.Sqlite;
using Resona.Models;
using Resona.Services;

namespace Resona.Tests;

public sealed class MusicDatabaseImportWorkflowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"resona-tests-{Guid.NewGuid():N}");
    private readonly string _databasePath;
    private readonly MusicDatabase _database;

    public MusicDatabaseImportWorkflowTests()
    {
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "workflow.db");
        CreateMinimalSchema();
        _database = new MusicDatabase(_databasePath);
    }

    [Fact]
    public void Claim_changes_exactly_one_oldest_item_to_downloading()
    {
        var first = InsertQueueItem("Queued", "one", createdAt: "2026-01-01T00:00:00Z");
        var second = InsertQueueItem("Queued", "two", createdAt: "2026-01-02T00:00:00Z");

        var claimed = _database.ClaimNextQueuedImport();

        Assert.NotNull(claimed);
        Assert.Equal(first, claimed.Id);
        Assert.Equal(ImportQueueStatus.Downloading, claimed.Status);
        Assert.Equal("Downloading", Scalar<string>("SELECT status FROM import_queue_items WHERE id = $id", ("$id", first)));
        Assert.Equal("Queued", Scalar<string>("SELECT status FROM import_queue_items WHERE id = $id", ("$id", second)));
    }

    [Fact]
    public void Queue_preserves_the_complete_download_request()
    {
        var request = new DownloadRequest
        {
            RawUrl = "https://youtu.be/request",
            GenreIds = [2, 5],
            RatingId = 4,
            StyleIds = [7],
            IsOriginal = false,
            ParentTrackId = 42,
            EditTypes = TrackEditTypes.Remix,
            VersionName = "Club Mix"
        };
        var preview = new ImportPreviewItem(
            request.RawUrl,
            request.RawUrl,
            "Queued track",
            180,
            1234,
            ImportQueueStatus.Queued,
            Request: request);

        _database.CreateImportBatch(request.RawUrl, [preview]);
        var claimed = _database.ClaimNextQueuedImport();

        Assert.NotNull(claimed);
        Assert.Equal(request.RawUrl, claimed.DownloadRequest.RawUrl);
        Assert.Equal(request.GenreIds, claimed.DownloadRequest.GenreIds);
        Assert.Equal(request.RatingId, claimed.DownloadRequest.RatingId);
        Assert.Equal(request.StyleIds, claimed.DownloadRequest.StyleIds);
        Assert.Equal(request.IsOriginal, claimed.DownloadRequest.IsOriginal);
        Assert.Equal(request.ParentTrackId, claimed.DownloadRequest.ParentTrackId);
        Assert.Equal(request.EditTypes, claimed.DownloadRequest.EditTypes);
        Assert.Equal(request.VersionName, claimed.DownloadRequest.VersionName);
    }

    [Fact]
    public void Invalid_transition_is_rejected_without_mutating_item()
    {
        var itemId = InsertQueueItem("Queued", "one");

        Assert.Throws<InvalidOperationException>(() =>
            _database.UpdateImportQueueItem(itemId, ImportQueueStatus.ReadyForReview));

        Assert.Equal("Queued", Scalar<string>("SELECT status FROM import_queue_items WHERE id = $id", ("$id", itemId)));
    }

    [Fact]
    public void Completion_atomically_promotes_unrated_track_and_removes_empty_batch()
    {
        var itemId = InsertQueueItem("Downloading", "one");
        var batchId = Scalar<long>("SELECT batch_id FROM import_queue_items WHERE id = $id", ("$id", itemId));
        var trackId = InsertTrack(ratingId: null, state: "Active", needsReview: 0);

        _database.CompleteImportQueueItem(itemId, trackId);

        Assert.Equal("PendingRating", Scalar<string>("SELECT library_state FROM tracks WHERE id = $id", ("$id", trackId)));
        Assert.Equal(1L, Scalar<long>("SELECT needs_reevaluation FROM tracks WHERE id = $id", ("$id", trackId)));
        Assert.Equal(0L, Scalar<long>("SELECT COUNT(*) FROM import_queue_items WHERE id = $id", ("$id", itemId)));
        Assert.Equal(0L, Scalar<long>("SELECT COUNT(*) FROM import_batches WHERE id = $id", ("$id", batchId)));
    }

    [Fact]
    public void Missing_queue_item_rolls_back_track_transition()
    {
        var trackId = InsertTrack(ratingId: null, state: "Active", needsReview: 0);

        Assert.Throws<InvalidOperationException>(() => _database.CompleteImportQueueItem(999, trackId));

        Assert.Equal("Active", Scalar<string>("SELECT library_state FROM tracks WHERE id = $id", ("$id", trackId)));
        Assert.Equal(0L, Scalar<long>("SELECT needs_reevaluation FROM tracks WHERE id = $id", ("$id", trackId)));
    }

    [Fact]
    public void Removing_last_queued_item_also_removes_its_empty_batch()
    {
        var itemId = InsertQueueItem("Queued", "one");
        var batchId = Scalar<long>("SELECT batch_id FROM import_queue_items WHERE id = $id", ("$id", itemId));

        Assert.True(_database.RemoveQueuedImport(itemId));

        Assert.Equal(0L, Scalar<long>("SELECT COUNT(*) FROM import_queue_items WHERE id = $id", ("$id", itemId)));
        Assert.Equal(0L, Scalar<long>("SELECT COUNT(*) FROM import_batches WHERE id = $id", ("$id", batchId)));
    }

    [Fact]
    public void Disabling_analysis_preserves_review_and_library_state()
    {
        var pendingReviewId = InsertTrack(ratingId: null, state: "PendingRating", needsReview: 1);
        var reviewedId = InsertTrack(ratingId: 4, state: "Active", needsReview: 0);

        _database.SetTrackAnalysisDisabled(pendingReviewId, true);
        _database.SetTrackAnalysisDisabled(reviewedId, true);

        Assert.Equal(1L, Scalar<long>("SELECT analysis_disabled FROM tracks WHERE id = $id", ("$id", pendingReviewId)));
        Assert.Equal("PendingRating", Scalar<string>("SELECT library_state FROM tracks WHERE id = $id", ("$id", pendingReviewId)));
        Assert.Equal(1L, Scalar<long>("SELECT needs_reevaluation FROM tracks WHERE id = $id", ("$id", pendingReviewId)));
        Assert.Equal(1L, Scalar<long>("SELECT analysis_disabled FROM tracks WHERE id = $id", ("$id", reviewedId)));
        Assert.Equal("Active", Scalar<string>("SELECT library_state FROM tracks WHERE id = $id", ("$id", reviewedId)));
        Assert.Equal(0L, Scalar<long>("SELECT needs_reevaluation FROM tracks WHERE id = $id", ("$id", reviewedId)));
    }

    [Fact]
    public void Unchecked_channel_video_blocks_analysis_until_review_is_complete()
    {
        var trackId = InsertTrack(ratingId: null, state: "PendingRating", needsReview: 1);
        var canonicalUrl = $"https://youtu.be/{Guid.NewGuid():N}";
        using (var connection = Open())
        using (var transaction = connection.BeginTransaction())
        {
            using var updateTrack = connection.CreateCommand();
            updateTrack.Transaction = transaction;
            updateTrack.CommandText = "UPDATE tracks SET canonical_url = $url WHERE id = $id";
            updateTrack.Parameters.AddWithValue("$url", canonicalUrl);
            updateTrack.Parameters.AddWithValue("$id", trackId);
            updateTrack.ExecuteNonQuery();

            Insert(connection, transaction, @"
                INSERT INTO channel_videos
                    (canonical_url, is_checked, download_status, updated_at)
                VALUES ($url, 0, 'Ready', $now)",
                ("$url", canonicalUrl), ("$now", DateTime.UtcNow.ToString("O")));
            transaction.Commit();
        }

        Assert.True(_database.IsTrackWaitingForChannelReview(trackId));

        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE channel_videos SET is_checked = 1 WHERE canonical_url = $url";
            command.Parameters.AddWithValue("$url", canonicalUrl);
            command.ExecuteNonQuery();
        }

        Assert.False(_database.IsTrackWaitingForChannelReview(trackId));
    }

    [Fact]
    public void Production_guards_reject_invalid_track_states()
    {
        using var connection = Open();
        MusicDatabase.EnsureTrackWorkflowGuards(connection);

        var invalidInsert = connection.CreateCommand();
        invalidInsert.CommandText = @"INSERT INTO tracks
            (rating_id, title, file_name, downloaded_at, updated_at, library_state, needs_reevaluation)
            VALUES (NULL, 'Invalid', 'invalid.mp3', $now, $now, 'Active', 0)";
        invalidInsert.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        Assert.Throws<SqliteException>(() => invalidInsert.ExecuteNonQuery());

        var trackId = InsertTrack(ratingId: null, state: "PendingRating", needsReview: 1);
        var invalidUpdate = connection.CreateCommand();
        invalidUpdate.CommandText = "UPDATE tracks SET needs_reevaluation = 0 WHERE id = $id";
        invalidUpdate.Parameters.AddWithValue("$id", trackId);
        Assert.Throws<SqliteException>(() => invalidUpdate.ExecuteNonQuery());
    }

    [Fact]
    public void Production_normalization_repairs_legacy_rows_before_guards_are_created()
    {
        var unratedId = InsertTrack(ratingId: null, state: "Active", needsReview: 0);
        var ratedId = InsertTrack(ratingId: 4, state: "PendingRating", needsReview: 1);
        var rejectedId = InsertTrack(ratingId: 1, state: "Rejected", needsReview: 1);

        using var connection = Open();
        MusicDatabase.NormalizeTrackWorkflowState(connection);
        MusicDatabase.EnsureTrackWorkflowGuards(connection);

        Assert.Equal("PendingRating", Scalar<string>("SELECT library_state FROM tracks WHERE id = $id", ("$id", unratedId)));
        Assert.Equal(1L, Scalar<long>("SELECT needs_reevaluation FROM tracks WHERE id = $id", ("$id", unratedId)));
        Assert.Equal("Active", Scalar<string>("SELECT library_state FROM tracks WHERE id = $id", ("$id", ratedId)));
        Assert.Equal(0L, Scalar<long>("SELECT needs_reevaluation FROM tracks WHERE id = $id", ("$id", rejectedId)));
        Assert.Equal(1L, Scalar<long>("SELECT analysis_disabled FROM tracks WHERE id = $id", ("$id", rejectedId)));
    }

    [Fact]
    public void Retrying_metadata_failure_preserves_duration_skip()
    {
        var videoId = InsertChannelVideo("Skipped", "Failed", null, "metadata error", 0);

        Assert.True(_database.RetryChannelVideoIssue(videoId));

        Assert.Equal("Queued", Scalar<string>("SELECT metadata_status FROM channel_videos WHERE id = $id", ("$id", videoId)));
        Assert.Equal(200L, Scalar<long>("SELECT metadata_priority FROM channel_videos WHERE id = $id", ("$id", videoId)));
        Assert.Equal(0L, Scalar<long>("SELECT metadata_attempts FROM channel_videos WHERE id = $id", ("$id", videoId)));
        Assert.Equal(0L, Scalar<long>("SELECT COUNT(metadata_error) FROM channel_videos WHERE id = $id", ("$id", videoId)));
        Assert.Equal("Skipped", Scalar<string>("SELECT download_status FROM channel_videos WHERE id = $id", ("$id", videoId)));
    }

    [Fact]
    public void Failed_channel_metadata_stays_terminal_across_recovery_and_channel_refresh()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var channelId = Insert(connection, transaction, @"
            INSERT INTO channels
                (name, source_channel_id, source_url, subscribed, created_at, updated_at, last_checked_at)
            VALUES ('Restricted channel', 'channel-1', 'https://youtube.test/channel-1', 1, $now, $now, $now)",
            ("$now", "2026-01-01T00:00:00Z"));
        var videoId = Insert(connection, transaction, @"
            INSERT INTO channel_videos
                (channel_id, video_id, canonical_url, title, discovered_at, updated_at,
                 download_status, metadata_status, metadata_updated_at, metadata_error, metadata_attempts)
            VALUES ($channelId, 'restricted-1', 'https://youtube.test/watch/restricted-1', 'Restricted track',
                    $now, $now, 'NotQueued', 'Failed', $now, 'Sign in to confirm your age', 4)",
            ("$channelId", channelId), ("$now", "2026-01-01T00:00:00Z"));
        transaction.Commit();

        _database.RecoverChannelMetadataQueue();
        var refresh = _database.SaveChannelSnapshot(new YouTubeChannelSnapshot(
            "https://youtube.test/channel-1",
            "channel-1",
            "Restricted channel",
            "https://youtube.test/channel-1",
            [new YouTubeChannelVideoEntry(
                "restricted-1",
                "https://youtube.test/watch/restricted-1",
                "Restricted track",
                180,
                "2025-12-01")]));

        Assert.True(refresh.Success);
        Assert.Equal(0, _database.QueueBackgroundChannelVideoMetadata(10));
        Assert.Equal(0, _database.CountBackgroundChannelVideoMetadataWork());
        Assert.Equal("Failed", Scalar<string>("SELECT metadata_status FROM channel_videos WHERE id = $id", ("$id", videoId)));
        Assert.Equal("Sign in to confirm your age", Scalar<string>("SELECT metadata_error FROM channel_videos WHERE id = $id", ("$id", videoId)));
        Assert.Equal(4L, Scalar<long>("SELECT metadata_attempts FROM channel_videos WHERE id = $id", ("$id", videoId)));
    }

    [Fact]
    public void Explicit_video_details_can_be_loaded_for_an_unfollowed_channel()
    {
        using (var connection = Open())
        using (var transaction = connection.BeginTransaction())
        {
            var channelId = Insert(connection, transaction, @"
                INSERT INTO channels (name, subscribed, created_at, updated_at)
                VALUES ('Unfollowed channel', 0, $now, $now)",
                ("$now", "2026-01-01T00:00:00Z"));
            for (var index = 1; index <= 2; index++)
            {
                Insert(connection, transaction, @"
                    INSERT INTO channel_videos
                        (channel_id, video_id, canonical_url, title, discovered_at, updated_at,
                         download_status, metadata_status)
                    VALUES ($channelId, $videoId, $url, $title, $now, $now, 'NotQueued', 'Pending')",
                    ("$channelId", channelId),
                    ("$videoId", $"video-{index}"),
                    ("$url", $"https://youtube.test/watch/video-{index}"),
                    ("$title", $"Video {index}"),
                    ("$now", "2026-01-01T00:00:00Z"));
            }
            transaction.Commit();

            Assert.Equal(2, _database.QueueAllChannelVideoMetadata(channelId));
        }

        Assert.True(_database.HasQueuedChannelVideoMetadata());
        Assert.Equal(2, _database.CountBackgroundChannelVideoMetadataWork());
        var claimed = _database.ClaimNextChannelVideoMetadata();
        Assert.NotNull(claimed);
        Assert.Equal(ChannelMetadataStatus.Loading, claimed.MetadataStatus);
    }

    private int InsertQueueItem(string status, string suffix, string createdAt = "2026-01-01T00:00:00Z")
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var batchId = Insert(connection, transaction,
            "INSERT INTO import_batches(source_url, created_at) VALUES ($url, $createdAt)",
            ("$url", $"https://example.test/{suffix}"), ("$createdAt", createdAt));
        var itemId = Insert(connection, transaction, @"INSERT INTO import_queue_items
            (batch_id, source_url, canonical_url, title, status, created_at, updated_at)
            VALUES ($batchId, $url, $canonicalUrl, $title, $status, $createdAt, $createdAt)",
            ("$batchId", batchId), ("$url", $"https://example.test/{suffix}"),
            ("$canonicalUrl", $"https://youtu.be/{suffix}"), ("$title", suffix),
            ("$status", status), ("$createdAt", createdAt));
        transaction.Commit();
        return itemId;
    }

    private int InsertTrack(int? ratingId, string state, int needsReview)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"INSERT INTO tracks
            (rating_id, title, file_name, downloaded_at, updated_at, library_state, needs_reevaluation)
            VALUES ($ratingId, 'Track', $fileName, $now, $now, $state, $needsReview);
            SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$ratingId", ratingId is null ? DBNull.Value : ratingId.Value);
        command.Parameters.AddWithValue("$fileName", $"{Guid.NewGuid():N}.mp3");
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$needsReview", needsReview);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private int InsertChannelVideo(
        string downloadStatus,
        string metadataStatus,
        string? downloadError,
        string? metadataError,
        int downloadAttempts)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var suffix = Guid.NewGuid().ToString("N");
        var id = Insert(connection, transaction, @"INSERT INTO channel_videos
            (canonical_url, download_status, download_error, download_attempts,
             manual_download_requested, metadata_status, metadata_error,
             metadata_attempts, metadata_priority, updated_at)
            VALUES ($url, $downloadStatus, $downloadError, $downloadAttempts,
                    0, $metadataStatus, $metadataError, 4, 0, $now)",
            ("$url", $"https://youtu.be/{suffix}"),
            ("$downloadStatus", downloadStatus),
            ("$downloadError", (object?)downloadError ?? DBNull.Value),
            ("$downloadAttempts", downloadAttempts),
            ("$metadataStatus", metadataStatus),
            ("$metadataError", (object?)metadataError ?? DBNull.Value),
            ("$now", DateTime.UtcNow.ToString("O")));
        transaction.Commit();
        return id;
    }

    private void CreateMinimalSchema()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            PRAGMA foreign_keys = ON;
            CREATE TABLE channels (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                source_channel_id TEXT NULL UNIQUE,
                source_url TEXT NULL,
                inform_new_songs INTEGER NOT NULL DEFAULT 0,
                subscribed INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NULL,
                updated_at TEXT NULL,
                last_checked_at TEXT NULL,
                video_count INTEGER NOT NULL DEFAULT 0,
                follower_count INTEGER NULL,
                thumbnail_url TEXT NULL,
                thumbnail BLOB NULL,
                remote_metadata_updated_at TEXT NULL,
                basic_metadata_checked_at TEXT NULL
            );
            CREATE TABLE tracks (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                rating_id INTEGER NULL,
                canonical_url TEXT NULL UNIQUE,
                title TEXT NOT NULL,
                original_title TEXT NOT NULL DEFAULT '',
                file_name TEXT NOT NULL UNIQUE,
                downloaded_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                library_state TEXT NOT NULL,
                needs_reevaluation INTEGER NOT NULL,
                analysis_disabled INTEGER NOT NULL DEFAULT 0,
                channel_id INTEGER NULL REFERENCES channels(id) ON DELETE SET NULL,
                source_metadata_updated_at TEXT NULL
            );
            CREATE TABLE import_batches (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_url TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE TABLE import_queue_items (
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
                is_original INTEGER NOT NULL DEFAULT 1,
                parent_track_id INTEGER NULL REFERENCES tracks(id) ON DELETE SET NULL,
                edit_types INTEGER NOT NULL DEFAULT 0,
                request_json TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE channel_videos (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id INTEGER NULL REFERENCES channels(id) ON DELETE CASCADE,
                video_id TEXT NULL UNIQUE,
                canonical_url TEXT NOT NULL UNIQUE,
                title TEXT NOT NULL DEFAULT '',
                duration_seconds INTEGER NULL,
                uploaded_at TEXT NULL,
                discovered_at TEXT NOT NULL DEFAULT '',
                is_checked INTEGER NOT NULL DEFAULT 0,
                download_status TEXT NOT NULL,
                download_error TEXT NULL,
                download_attempts INTEGER NOT NULL DEFAULT 0,
                manual_download_requested INTEGER NOT NULL DEFAULT 0,
                metadata_status TEXT NOT NULL DEFAULT 'Pending',
                metadata_updated_at TEXT NULL,
                metadata_error TEXT NULL,
                metadata_attempts INTEGER NOT NULL DEFAULT 0,
                metadata_priority INTEGER NOT NULL DEFAULT 0,
                view_count INTEGER NULL,
                like_count INTEGER NULL,
                thumbnail_url TEXT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE channel_notifications (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
                channel_video_id INTEGER NULL REFERENCES channel_videos(id) ON DELETE CASCADE,
                kind TEXT NOT NULL,
                title TEXT NOT NULL,
                created_at TEXT NOT NULL,
                is_read INTEGER NOT NULL DEFAULT 0,
                is_archived INTEGER NOT NULL DEFAULT 0,
                UNIQUE(channel_video_id, kind)
            );";
        command.ExecuteNonQuery();
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

    private T Scalar<T>(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    private static int Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"{sql}; SELECT last_insert_rowid();";
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A failing test must remain the primary failure; temp cleanup is best effort.
        }
    }
}
