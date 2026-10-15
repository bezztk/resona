using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Media;
using Android.Media.Session;
using Android.OS;
using Resona.Companion;

namespace Resona.Android;

[Service(Name = "com.beran.music.MusicPlaybackService", Exported = false)]
public sealed class MusicPlaybackService : Service
{
    private const int NotificationId = 1001;
    private const string ChannelId = "music_playback_v2";
    private const string ActionUpdate = "com.beran.music.UPDATE_NOTIFICATION";
    private const string ActionStop = "com.beran.music.STOP_NOTIFICATION";
    private const string ActionPrevious = "com.beran.music.PREVIOUS";
    private const string ActionPlayPause = "com.beran.music.PLAY_PAUSE";
    private const string ActionNext = "com.beran.music.NEXT";
    private const string ExtraTitle = "title";
    private const string ExtraCoverPath = "coverPath";
    private const string ExtraIsPlaying = "isPlaying";
    private const string ExtraPositionMs = "positionMs";
    private const string ExtraDurationMs = "durationMs";

    private MediaSession? _mediaSession;

    public static event Action<MediaControlCommand>? CommandReceived;
    public static event Action<TimeSpan>? SeekReceived;

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnDestroy()
    {
        CompanionServices.AudioPlayer.Stop();
        _mediaSession?.Release();
        _mediaSession = null;
        base.OnDestroy();
    }

    public override void OnTaskRemoved(Intent? rootIntent)
    {
        CompanionServices.AudioPlayer.Stop();
        DeactivateMediaSession();
        StopForeground(true);
        StopSelf();
        base.OnTaskRemoved(rootIntent);
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        switch (intent?.Action)
        {
            case ActionPrevious:
                CommandReceived?.Invoke(MediaControlCommand.Previous);
                return StartCommandResult.NotSticky;
            case ActionPlayPause:
                CommandReceived?.Invoke(MediaControlCommand.PlayPause);
                return StartCommandResult.NotSticky;
            case ActionNext:
                CommandReceived?.Invoke(MediaControlCommand.Next);
                return StartCommandResult.NotSticky;
            case ActionStop:
                DeactivateMediaSession();
                StopForeground(true);
                StopSelf();
                return StartCommandResult.NotSticky;
        }

        var title = intent?.GetStringExtra(ExtraTitle) ?? "Resona";
        var coverPath = intent?.GetStringExtra(ExtraCoverPath);
        var isPlaying = intent?.GetBooleanExtra(ExtraIsPlaying, false) == true;
        var positionMs = intent?.GetLongExtra(ExtraPositionMs, 0) ?? 0;
        var durationMs = intent?.GetLongExtra(ExtraDurationMs, 0) ?? 0;
        var largeIcon = LoadCover(coverPath);

        CreateNotificationChannel();
        UpdateMediaSession(title, largeIcon, isPlaying, positionMs, durationMs);
        StartForeground(NotificationId, BuildNotification(title, largeIcon, isPlaying, positionMs, durationMs));
        return StartCommandResult.NotSticky;
    }

    public static void StartOrUpdate(
        Context context,
        string title,
        string? coverPath,
        bool isPlaying,
        TimeSpan position,
        TimeSpan duration)
    {
        var intent = new Intent(context, typeof(MusicPlaybackService));
        intent.SetAction(ActionUpdate);
        intent.PutExtra(ExtraTitle, title);
        intent.PutExtra(ExtraIsPlaying, isPlaying);
        intent.PutExtra(ExtraPositionMs, (long)Math.Max(0, position.TotalMilliseconds));
        intent.PutExtra(ExtraDurationMs, (long)Math.Max(0, duration.TotalMilliseconds));
        if (!string.IsNullOrWhiteSpace(coverPath))
            intent.PutExtra(ExtraCoverPath, coverPath);

        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            context.StartForegroundService(intent);
        else
            context.StartService(intent);
    }

    public static void StopNotification(Context context)
    {
        var intent = new Intent(context, typeof(MusicPlaybackService));
        intent.SetAction(ActionStop);
        context.StartService(intent);
    }

    private Notification BuildNotification(
        string title,
        Bitmap? largeIcon,
        bool isPlaying,
        long positionMs,
        long durationMs)
    {
        var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
            ? new Notification.Builder(this, ChannelId)
            : new Notification.Builder(this);

        var openAppIntent = new Intent(this, typeof(MainActivity));
        openAppIntent.SetAction(Intent.ActionMain);
        openAppIntent.AddCategory(Intent.CategoryLauncher);
        openAppIntent.SetFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);

        var contentIntent = PendingIntent.GetActivity(
            this,
            0,
            openAppIntent,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        builder
            .SetSmallIcon(Resource.Drawable.ic_notification)
            .SetContentTitle(title)
            .SetContentText(isPlaying ? "Playing" : "Paused")
            .SetSubText("Resona")
            .SetOngoing(isPlaying)
            .SetShowWhen(false)
            .SetContentIntent(contentIntent)
            .SetVisibility(NotificationVisibility.Public)
            .SetOnlyAlertOnce(true)
            .SetCategory(Notification.CategoryTransport)
            .SetPriority((int)NotificationPriority.Max);

        if (largeIcon != null)
            builder.SetLargeIcon(largeIcon);

        if (durationMs > 0)
            builder.SetProgress((int)Math.Min(int.MaxValue, durationMs), (int)Math.Min(int.MaxValue, positionMs), false);

        builder.AddAction(BuildAction(Resource.Drawable.ic_media_previous, "Previous", ActionPrevious));
        builder.AddAction(BuildAction(isPlaying ? Resource.Drawable.ic_media_pause : Resource.Drawable.ic_media_play, isPlaying ? "Pause" : "Play", ActionPlayPause));
        builder.AddAction(BuildAction(Resource.Drawable.ic_media_next, "Next", ActionNext));

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Lollipop)
        {
            var style = new Notification.MediaStyle()
                .SetShowActionsInCompactView(0, 1, 2);

            if (_mediaSession != null)
                style.SetMediaSession(_mediaSession.SessionToken);

            builder.SetStyle(style);
        }

        return builder.Build();
    }

    private void UpdateMediaSession(string title, Bitmap? cover, bool isPlaying, long positionMs, long durationMs)
    {
        if (_mediaSession == null)
        {
            _mediaSession = new MediaSession(this, "Resona");
            _mediaSession.SetCallback(new PlaybackSessionCallback());
        }

        _mediaSession.Active = true;

        var metadata = new MediaMetadata.Builder()
            .PutString(MediaMetadata.MetadataKeyTitle, title)
            .PutString(MediaMetadata.MetadataKeyArtist, "Resona")
            .PutLong(MediaMetadata.MetadataKeyDuration, Math.Max(0, durationMs));

        if (cover != null)
        {
            metadata.PutBitmap(MediaMetadata.MetadataKeyAlbumArt, cover);
            metadata.PutBitmap(MediaMetadata.MetadataKeyArt, cover);
        }

        _mediaSession.SetMetadata(metadata.Build());

        var state = isPlaying
            ? PlaybackStateCode.Playing
            : PlaybackStateCode.Paused;

        var actions =
            PlaybackState.ActionPlay |
            PlaybackState.ActionPause |
            PlaybackState.ActionPlayPause |
            PlaybackState.ActionSkipToPrevious |
            PlaybackState.ActionSkipToNext |
            PlaybackState.ActionSeekTo;

        _mediaSession.SetPlaybackState(new PlaybackState.Builder()
            .SetActions(actions)
            .SetState(state, Math.Max(0, positionMs), isPlaying ? 1.0f : 0.0f)
            .Build());
    }

    private void DeactivateMediaSession()
    {
        _mediaSession?.SetPlaybackState(new PlaybackState.Builder()
            .SetState(PlaybackStateCode.Stopped, 0, 0)
            .Build());
        if (_mediaSession != null)
            _mediaSession.Active = false;
    }

    private Notification.Action BuildAction(int icon, string title, string action)
    {
        var intent = new Intent(this, typeof(MusicPlaybackService));
        intent.SetAction(action);

        var pendingIntent = PendingIntent.GetService(
            this,
            action.GetHashCode(),
            intent,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        return new Notification.Action.Builder(icon, title, pendingIntent).Build();
    }

    private void CreateNotificationChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O)
            return;

        var manager = (NotificationManager?)GetSystemService(NotificationService);
        if (manager?.GetNotificationChannel(ChannelId) != null)
            return;

        var channel = new NotificationChannel(
            ChannelId,
            "Resona playback",
            NotificationImportance.Default)
        {
            Description = "Resona playback controls"
        };
        manager?.CreateNotificationChannel(channel);
    }

    private Bitmap? LoadCover(string? coverPath)
    {
        if (string.IsNullOrWhiteSpace(coverPath))
            return null;

        try
        {
            if (coverPath.StartsWith("content://", StringComparison.OrdinalIgnoreCase))
            {
                var uri = global::Android.Net.Uri.Parse(coverPath);
                if (uri is null)
                    return null;
                using var stream = ContentResolver.OpenInputStream(uri);
                return stream is null ? null : BitmapFactory.DecodeStream(stream);
            }

            if (!File.Exists(coverPath))
                return null;
            return BitmapFactory.DecodeFile(coverPath);
        }
        catch
        {
            return null;
        }
    }

    private sealed class PlaybackSessionCallback : MediaSession.Callback
    {
        public override void OnSkipToPrevious()
        {
            CommandReceived?.Invoke(MediaControlCommand.Previous);
        }

        public override void OnPlay()
        {
            CommandReceived?.Invoke(MediaControlCommand.Play);
        }

        public override void OnPause()
        {
            CommandReceived?.Invoke(MediaControlCommand.Pause);
        }

        public override void OnSkipToNext()
        {
            CommandReceived?.Invoke(MediaControlCommand.Next);
        }

        public override void OnSeekTo(long pos)
        {
            SeekReceived?.Invoke(TimeSpan.FromMilliseconds(Math.Max(0, pos)));
        }
    }
}
