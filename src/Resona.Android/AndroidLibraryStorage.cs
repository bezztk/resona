using Android.Content;
using Android.Database;
using Android.Provider;
using Resona.Companion;
using Resona.Core;
using AndroidUri = Android.Net.Uri;

namespace Resona.Android;

public sealed class AndroidLibraryStorage(MainActivity activity) : ILibraryStorage
{
    private const string PreferencesName = "resona-library";
    private const string TreeUriKey = "music-tree-uri";
    private readonly ContentResolver _resolver = activity.ContentResolver
        ?? throw new InvalidOperationException("Android content access is unavailable.");
    private Dictionary<string, AndroidUri> _trackUris = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, AndroidUri> _imageUris = new(StringComparer.OrdinalIgnoreCase);

    public string LibraryDirectory => TreeUri?.ToString() ?? string.Empty;
    public bool IsConfigured => TreeUri is not null;

    private AndroidUri? TreeUri
    {
        get
        {
            var value = activity.GetSharedPreferences(PreferencesName, FileCreationMode.Private)
                ?.GetString(TreeUriKey, null);
            return string.IsNullOrWhiteSpace(value) ? null : AndroidUri.Parse(value);
        }
    }

    public async Task<bool> SelectLibraryAsync()
    {
        var uri = await activity.SelectMusicFolderAsync();
        if (uri is null)
            return false;

        activity.GetSharedPreferences(PreferencesName, FileCreationMode.Private)
            ?.Edit()
            ?.PutString(TreeUriKey, uri.ToString())
            ?.Apply();
        return true;
    }

    public async Task<LoadedMusicLibrary> LoadAsync()
    {
        var treeUri = TreeUri ?? throw new InvalidOperationException("No Music folder has been selected.");
        var rootFiles = ReadChildren(treeUri);
        if (!rootFiles.TryGetValue(PortableLibraryStore.FileName, out var libraryUri))
            throw new FileNotFoundException($"{PortableLibraryStore.FileName} was not found in the selected folder.");
        if (!rootFiles.TryGetValue("Tracks", out var tracksUri))
            throw new DirectoryNotFoundException("The Tracks folder was not found.");

        _trackUris = ReadChildren(tracksUri);
        _imageUris = rootFiles.TryGetValue("Images", out var imagesUri)
            ? ReadChildren(imagesUri)
            : new Dictionary<string, AndroidUri>(StringComparer.OrdinalIgnoreCase);

        await using var stream = _resolver.OpenInputStream(libraryUri)
                                 ?? throw new IOException($"Could not open {PortableLibraryStore.FileName}.");
        var library = await PortableLibraryStore.LoadAsync(stream);
        return new LoadedMusicLibrary(treeUri.ToString() ?? string.Empty, library);
    }

    public string? TrackSource(LoadedMusicLibrary library, PortableTrack track) =>
        _trackUris.GetValueOrDefault(track.FileName)?.ToString();

    public Stream? OpenCover(LoadedMusicLibrary library, PortableTrack track)
    {
        if (string.IsNullOrWhiteSpace(track.CoverFileName)
            || !_imageUris.TryGetValue(track.CoverFileName, out var uri))
            return null;

        return _resolver.OpenInputStream(uri);
    }

    public string? CoverSource(LoadedMusicLibrary library, PortableTrack track) =>
        !string.IsNullOrWhiteSpace(track.CoverFileName)
        && _imageUris.TryGetValue(track.CoverFileName, out var uri)
            ? uri.ToString()
            : null;

    private Dictionary<string, AndroidUri> ReadChildren(AndroidUri directoryUri)
    {
        var documentId = DocumentId(directoryUri);
        var childrenUri = DocumentsContract.BuildChildDocumentsUriUsingTree(directoryUri, documentId)
                          ?? throw new InvalidDataException("The selected folder cannot be opened.");
        var result = new Dictionary<string, AndroidUri>(StringComparer.OrdinalIgnoreCase);
        var projection = new[]
        {
            DocumentsContract.Document.ColumnDocumentId,
            DocumentsContract.Document.ColumnDisplayName
        };

        using ICursor? cursor = _resolver.Query(childrenUri, projection, null, null, null);
        if (cursor is null)
            return result;

        var idIndex = cursor.GetColumnIndex(DocumentsContract.Document.ColumnDocumentId);
        var nameIndex = cursor.GetColumnIndex(DocumentsContract.Document.ColumnDisplayName);
        while (cursor.MoveToNext())
        {
            var id = cursor.GetString(idIndex);
            var name = cursor.GetString(nameIndex);
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                continue;

            var uri = DocumentsContract.BuildDocumentUriUsingTree(directoryUri, id);
            if (uri is not null)
                result[name] = uri;
        }

        return result;
    }

    private static string DocumentId(AndroidUri uri)
    {
        try
        {
            var documentId = DocumentsContract.GetDocumentId(uri);
            if (!string.IsNullOrWhiteSpace(documentId))
                return documentId;
        }
        catch
        {
            // Tree roots are not document URIs on every Android document provider.
        }

        return DocumentsContract.GetTreeDocumentId(uri)
               ?? throw new InvalidDataException("The selected folder URI is invalid.");
    }
}
