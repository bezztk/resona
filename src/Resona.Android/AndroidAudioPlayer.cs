using Android.App;
using Android.Media;
using Android.OS;
using Resona.Companion;

namespace Resona.Android;

public sealed class AndroidAudioPlayer : ICompanionAudioPlayer, IDisposable
{
    private MediaPlayer? _player;

    public event Action? PlaybackEnded;

    public bool IsPlaying => _player?.IsPlaying == true;
    public TimeSpan Position => TimeSpan.FromMilliseconds(_player?.CurrentPosition ?? 0);
    public TimeSpan Duration => TimeSpan.FromMilliseconds(_player?.Duration ?? 0);

    public Task PlayAsync(string source)
    {
        Stop();

        var player = new MediaPlayer();
        try
        {
            if (source.StartsWith("content://", StringComparison.OrdinalIgnoreCase))
            {
                var uri = global::Android.Net.Uri.Parse(source)
                          ?? throw new IOException("The selected audio URI is invalid.");
                player.SetDataSource(Application.Context, uri);
            }
            else
            {
                // A file descriptor keeps local paths with Unicode or URI-reserved
                // characters from being interpreted as remote sources.
                using var descriptor = ParcelFileDescriptor.Open(
                    new Java.IO.File(source),
                    ParcelFileMode.ReadOnly)
                    ?? throw new IOException($"Could not open audio file '{source}'.");
                player.SetDataSource(descriptor.FileDescriptor);
            }
            player.Completion += OnPlaybackCompleted;
            player.Prepare();
            player.Start();
            _player = player;
        }
        catch
        {
            player.Completion -= OnPlaybackCompleted;
            player.Release();
            player.Dispose();
            throw;
        }

        return Task.CompletedTask;
    }

    public void Pause()
    {
        if (_player?.IsPlaying == true)
            _player.Pause();
    }

    public void Resume()
    {
        if (_player is { IsPlaying: false })
            _player.Start();
    }

    public void Seek(TimeSpan position)
    {
        _player?.SeekTo((int)position.TotalMilliseconds);
    }

    public void Stop()
    {
        if (_player == null)
            return;

        _player.Completion -= OnPlaybackCompleted;
        try { _player.Stop(); }
        catch { }
        _player.Release();
        _player.Dispose();
        _player = null;
    }

    public void Dispose()
    {
        Stop();
    }

    private void OnPlaybackCompleted(object? sender, EventArgs e)
    {
        PlaybackEnded?.Invoke();
    }
}
