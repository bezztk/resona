using Android.App;
using Android.Content;
using Android.Content.PM;
using Avalonia;
using Avalonia.Android;
using Resona.Companion;
using AndroidUri = Android.Net.Uri;

namespace Resona.Android;

[Activity(
    Label = "Resona",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@mipmap/ic_launcher",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity<App>
{
    private const int SelectMusicFolderRequestCode = 5268;
    private TaskCompletionSource<AndroidUri?>? _folderSelection;

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        CompanionServices.AudioPlayer = new AndroidAudioPlayer();
        CompanionServices.LibraryStorage = new AndroidLibraryStorage(this);
        CompanionServices.MediaControls = new AndroidMediaControls(this);

        return base.CustomizeAppBuilder(builder);
    }

    public Task<AndroidUri?> SelectMusicFolderAsync()
    {
        if (_folderSelection is not null)
            return _folderSelection.Task;

        _folderSelection = new TaskCompletionSource<AndroidUri?>();
        var intent = new Intent(Intent.ActionOpenDocumentTree);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission
                        | ActivityFlags.GrantPersistableUriPermission
                        | ActivityFlags.GrantPrefixUriPermission);
        StartActivityForResult(intent, SelectMusicFolderRequestCode);
        return _folderSelection.Task;
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != SelectMusicFolderRequestCode || _folderSelection is null)
            return;

        var completion = _folderSelection;
        _folderSelection = null;
        var uri = resultCode == Result.Ok ? data?.Data : null;
        if (uri is not null)
        {
            try
            {
                ContentResolver?.TakePersistableUriPermission(uri, ActivityFlags.GrantReadUriPermission);
            }
            catch
            {
                uri = null;
            }
        }

        completion.TrySetResult(uri);
    }
}
