using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Rectangle = Avalonia.Controls.Shapes.Rectangle;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Resona.Core;
using Resona.Models;
using Resona.Services;
using SkiaSharp;

namespace Resona.Views;

public enum LibrarySortBy { Name, Rating, DownloadedAt, CollectionOrder }
public enum LibrarySortDirection { Ascending, Descending }

public partial class MusicView : UserControl
{
    // Engine
    private readonly PlaybackEngine _engine = new();
    private readonly PlaybackQueue _playbackQueue = new();
    private readonly GlobalMediaKeyListener _globalMediaKeys = new();
    private readonly WindowsMediaSession _windowsMediaSession = new();
    private readonly DiscordPresenceService _discordPresence = new();
    private static readonly AmbientPalette DefaultAmbientPalette = new(
        Color.Parse("#5865B8"),
        Color.Parse("#8051AE"));
    private bool _isSeeking;
    private TaskCompletionSource<bool>? _deleteTrackConfirmationCompletion;
    private ContextMenu? _activeTrackContextMenu;
    private TopLevel? _contextMenuDismissRoot;
    private AppearanceSettings _appearanceSettings = AppearanceSettings.Balanced();

    // Playback settings
    private bool _shuffle;
    private string _loopStatus = "None";
    private LibrarySortBy _sortBy = LibrarySortBy.Name;
    private LibrarySortDirection _sortDirection = LibrarySortDirection.Ascending;
    private readonly Dictionary<int, double> _shufflePriorities = [];

    // UI state
    private bool _filterPanelVisible;
    private CancellationTokenSource? _thumbLoadCts;
    private CancellationTokenSource? _toastCts;
    private bool _isDeletingTrack;
    private bool _libraryRefreshPending;
    private bool _isCreatingCollection;
    private readonly List<Bitmap> _collectionCardBitmaps = [];
    private Bitmap? _collectionContextBitmap;

    // Crossfade state
    private int _lastKnownActiveId = -1;
    private bool _crossfadeTriggered;
    private int _nextTrackIndex = -1;
    private int _listeningTrackId = -1;
    private double _lastListeningPositionSeconds;
    private double _unflushedListeningSeconds;
    private PlaybackEngineSnapshot? _previewPlaybackSnapshot;
    private bool _isTrackPreviewActive;
    private int _previewTrackId = -1;

    private readonly Random _rng = new();

    // Track list data
    private List<TrackDisplayItem> _allItems = [];
    private Dictionary<int, List<int>> _allTrackStyleIds = [];
    private Dictionary<int, List<int>> _allTrackGenreIds = [];
    private Dictionary<int, List<int>> _allTrackTagIds = [];
    private Dictionary<int, TrackAudioAnalysis> _allTrackAudioAnalyses = [];
    private Dictionary<int, Dictionary<string, double>> _allTrackMirexScores = [];
    private Dictionary<int, TrackUsageStats> _allTrackUsageStats = [];
    private Dictionary<int, string> _mainGenreNamesBySubgenreId = [];
    private Dictionary<string, string> _mainGenreNamesBySubgenreName = new(StringComparer.OrdinalIgnoreCase);
    private List<TrackDisplayItem> _filteredItems = [];
    private List<TrackDisplayItem> _visibleItems = [];
    private List<int> _loadedPlaylistSourceTrackIds = [];
    private List<PortableFilterPreset> _filterPresets = [];
    private List<TrackCollection> _collections = [];
    private TrackCollection? _activeCollection;
    private Dictionary<int, int> _activeCollectionOrder = [];
    private Dictionary<int, List<string>> _allTrackCollectionNames = [];
    private string? _activeFilterPresetName;
    private LibraryMode? _activeBuiltInView = LibraryMode.Library;
    private bool _isCreatingPreset;
    private bool _manualRatingFilter;
    private PlayerSessionSettings _restoredPlayerSession = new();
    private Dictionary<int, int>? _pendingRestoredQueueOrder;
    private int? _pendingRestoredTrackId;
    private bool _restoringPlayerSession = true;
    private bool _suppressPresetAutoSave;
    private bool _suppressSelectionSessionSave;
    private readonly HashSet<string> _selectedRatingNames = new(StringComparer.OrdinalIgnoreCase);
    private MultiSelectFilterControl? _conditionGenreCtrl;
    private MultiSelectFilterControl? _conditionStyleCtrl;
    private MultiSelectFilterControl? _conditionVersionCtrl;
    private MultiSelectFilterControl? _conditionTagCtrl;
    private MultiSelectFilterControl? _conditionLanguageCtrl;
    private bool _conditionNegate;
    private bool _updatingLibraryMode;
    private readonly Dictionary<string, EmotionalRangeState> _conditionEmotionalCharacters =
        EmotionalCharacterCatalog.All.ToDictionary(item => item.Adjectives, _ => new EmotionalRangeState(), StringComparer.OrdinalIgnoreCase);
    private FilterSection? _conditionGenreSection;
    private FilterSection? _conditionStyleSection;
    private FilterSection? _conditionTagSection;
    private FilterSection? _conditionLanguageSection;
    private FilterSection? _conditionEmotionalSection;

    private record FilterGroupControls(
        MultiSelectFilterControl GenreCtrl,
        MultiSelectFilterControl StyleCtrl,
        MultiSelectFilterControl TagCtrl,
        MultiSelectFilterControl LanguageCtrl,
        MultiSelectFilterControl VersionCtrl,
        Dictionary<string, EmotionalRangeState> EmotionalCharacters,
        HashSet<string> MainGenres,
        bool Negate,
        Action RefreshVisuals);
    private sealed class EmotionalRangeState
    {
        public double? MinimumPercent { get; set; }
        public bool IsActive => MinimumPercent is not null;
    }
    private sealed record FilterSection(Control Control, Action Refresh);
    private enum LibraryMode
    {
        Library,
        Review,
        Declined
    }
    private readonly List<FilterGroupControls> _filterGroups = [];

    public MusicView()
    {
        InitializeComponent();
        MoveFilterDrawerToRootOverlay();
        var appSettings = AppSettingsStore.Load();
        Values.UseYtDlpBrowserCookies = appSettings.UseYtDlpBrowserCookies;
        Values.YtDlpCookiesBrowser = AppSettingsStore.NormalizeYtDlpCookiesBrowser(appSettings.YtDlpCookiesBrowser);
        _appearanceSettings = appSettings.Appearance.Clone().Clamp();
        _restoredPlayerSession = appSettings.PlayerSession;
        _sortBy = Enum.TryParse<LibrarySortBy>(_restoredPlayerSession.SortBy, true, out var restoredSortBy)
            ? restoredSortBy
            : LibrarySortBy.Name;
        _sortDirection = Enum.TryParse<LibrarySortDirection>(_restoredPlayerSession.SortDirection, true, out var restoredDirection)
            ? restoredDirection
            : LibrarySortDirection.Ascending;
        _shuffle = _restoredPlayerSession.ShuffleEnabled;
        _pendingRestoredTrackId = _restoredPlayerSession.ActiveTrackId
                                  ?? _restoredPlayerSession.SelectedTrackId;
        _pendingRestoredQueueOrder = _restoredPlayerSession.QueueTrackIds
            .Distinct()
            .Select((trackId, index) => (trackId, index))
            .ToDictionary(item => item.trackId, item => item.index);
        if (_shuffle)
            foreach (var item in _pendingRestoredQueueOrder)
                _shufflePriorities[item.Key] = item.Value;
        ApplyAppearanceSettings(_appearanceSettings, refreshTrackRows: false);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AttachedToVisualTree += (_, _) => AttachContextMenuDismissHandler();
        _globalMediaKeys.Pressed += OnGlobalMediaKeyPressed;
        _globalMediaKeys.Start();
        _windowsMediaSession.Pressed += OnGlobalMediaKeyPressed;
        _windowsMediaSession.SeekRequested += OnSystemSeekRequested;
        _windowsMediaSession.PositionRequested += OnSystemPositionRequested;
        _windowsMediaSession.VolumeRequested += OnSystemVolumeRequested;
        _windowsMediaSession.ShuffleRequested += SetShuffle;
        _windowsMediaSession.LoopStatusRequested += OnSystemLoopStatusRequested;
        _windowsMediaSession.OpenUriRequested += OnSystemOpenUriRequested;
        DetachedFromVisualTree += (_, _) =>
        {
            DetachContextMenuDismissHandler();
            CloseActiveTrackContextMenu();
            PersistPlayerSession();
            _engine.Dispose();
            _globalMediaKeys.Dispose();
            _windowsMediaSession.Dispose();
            _discordPresence.Dispose();
            DisposeCollectionBitmaps();
        };

        // Engine events
        _engine.StateChanged += OnEngineStateChanged;
        _engine.TrackNaturallyEnded += OnTrackNaturallyEnded;
        _engine.ProgressUpdated += OnProgressUpdated;
        InitializeSortControls();

        // Seeking
        PlaybackSlider.AddHandler(PointerPressedEvent,
            OnSliderPointerPressed, RoutingStrategies.Tunnel);
        PlaybackSlider.AddHandler(PointerReleasedEvent,
            OnSliderPointerReleased, RoutingStrategies.Tunnel);

        SearchBox.TextChanged += (_, _) =>
        {
            ApplyFilter();
            UpdateSearchVisibility();
        };
        RefreshCompletionFilterVisuals();
        FileList.SelectionChanged += (_, _) =>
        {
            if (!_suppressSelectionSessionSave)
                PersistPlayerSession();
        };
        PlayerBar.SizeChanged += (_, _) => UpdateSettingsLayout();
        PlayerBar.SizeChanged += (_, _) => UpdateEditorBounds();
        PlayerBar.SizeChanged += (_, _) => UpdateImportBounds();

        // Volume
        Values.Volume = AppSettingsStore.Load().Volume;
        VolumeSlider.Value = Values.Volume * 100.0;
        _engine.ApplyVolume(Values.Volume);
        VolumeSlider.ValueChanged += (_, _) =>
        {
            Values.Volume = (float)VolumeSlider.Value / 100.0f;
            _engine.ApplyVolume(Values.Volume);
            AppSettingsStore.SaveVolume(Values.Volume);
            _windowsMediaSession.UpdateVolume(Values.Volume);
        };
        
        try { MusicLibraryService.Current.Initialize(); }
        catch (Exception ex) { StatusText.Text = $"Database error: {ex.Message}"; StatusText.IsVisible = true; return; }
        var backupResult = DatabaseBackupService.Current.EnsureTodayBackups();
        if (backupResult.Errors.Count > 0)
            ShowToast($"Database backup warning: {backupResult.Errors[0]}");
        UpdateImportBounds();

        LoadLookups();
        SetRatingFilterMode(
            _restoredPlayerSession.ManualRatingFilter,
            _restoredPlayerSession.SelectedRatingNames,
            applyFilter: false);
        InitializeFilterConditionBuilder();
        LoadCollections();
        LoadFilterPresets();
        RebuildFilterConditionsPanel();
        RefreshTrackList();
        RestorePlaybackQueueSession();
        ApplyPlaybackQueueToLoadedPlaylist();
        RefreshVisibleItemsSource(_pendingRestoredTrackId);
        UpdatePlaylistSummary();
        RefreshNextTrackPreview();

        // The first library read must win the startup race. Background workers
        // are intentionally started only after the initial UI data is ready.
        ImportQueueService.Current.Initialize();
        BackgroundAnalysisService.Current.Initialize();
        CloudLibrarySyncService.Current.Initialize();
        _restoringPlayerSession = false;
        UpdateShuffleButton();
        PersistPlayerSession();

        SettingsOverlay.PreloadGenreVocabulary();

        AddTrackOverlay.TrackDownloaded += warning =>
        {
            AddTrackOverlay.IsVisible = false;
            MarkLibraryRefreshPending();
            ShowToast(warning ?? "Track downloaded; analysis queued");
        };
        AddTrackOverlay.CloseRequested += () => AddTrackOverlay.IsVisible = false;
        AddVersionOverlay.Queued += () =>
        {
            ImportOverlay.RefreshQueue();
            ShowToast("Version added to Current Queue");
        };
        ImportOverlay.QueueSubmitted += count =>
        {
            ShowToast($"{count} track{(count == 1 ? string.Empty : "s")} added to the import queue");
        };
        ImportOverlay.ToastRequested += ShowToast;
        ImportQueueService.Current.ItemUpdated += _ => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (ImportOverlay.IsVisible) ImportOverlay.RefreshQueue();
        });
        ImportQueueService.Current.TrackImported += (track, warning) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            MarkLibraryRefreshPending();
            ShowToast(warning ?? $"Imported: {track.Title}");
        });
        BackgroundAnalysisService.Current.QueueChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (ImportOverlay.IsVisible) ImportOverlay.RefreshQueue();
        });
        BackgroundAnalysisService.Current.TrackAnalysisFinished += (track, error) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                MarkLibraryRefreshPending();
                if (ImportOverlay.IsVisible) ImportOverlay.RefreshQueue();
                ShowToast(error is null
                    ? $"Analysis completed: {track.Title}"
                    : error.Contains("cancelled", StringComparison.OrdinalIgnoreCase)
                        ? $"Analysis cancelled: {track.Title}"
                        : $"Analysis failed for {track.Title}: {error}");
                if (error is null && _engine.ActiveTrackId == track.Id)
                    PrepareTrackEditor(track, force: true);
            });
        EditTrackOverlay.TrackSaved += trackId =>
        {
            UpdateTrackInList(trackId);
        };
        EditTrackOverlay.CollectionsChanged += RefreshCollectionsAfterMembershipChange;
        EditTrackOverlay.PreviewRequested += StartTrackPreview;
        EditTrackOverlay.PreviewClosed += StopTrackPreview;
        EditTrackOverlay.ToastRequested += ShowToast;
        EditTrackOverlay.DeleteRequested += DeleteTrackFromEditorAsync;
        EditTrackOverlay.Closed += PrepareActiveTrackEditor;
        SettingsOverlay.ToastRequested += ShowToast;
        SettingsOverlay.AppearanceChanged += settings => ApplyAppearanceSettings(settings, refreshTrackRows: true);
        SettingsOverlay.DiscordPresenceChanged += () =>
        {
            _discordPresence.ReloadSettings();
            UpdateDiscordPresence();
        };
        SettingsOverlay.LibraryMetadataChanged += RefreshLibraryPresentation;
    }

    private void UpdateEditorBounds()
    {
        // The editor covers the toolbar and library, but deliberately stops above the player.
        EditTrackOverlay.Margin = new Thickness(0, 0, 0, PlayerBar.Bounds.Height);
    }

    private void UpdateImportBounds() =>
        ImportOverlay.Margin = new Thickness(0, 0, 0, PlayerBar.Bounds.Height);

    public void EnableSystemMediaControls()
    {
        _windowsMediaSession.Start();
        _windowsMediaSession.UpdateState(_engine.State);
        _windowsMediaSession.UpdateVolume(Values.Volume);
        _windowsMediaSession.UpdateShuffle(_shuffle);
        _windowsMediaSession.UpdateLoopStatus(_loopStatus);
        Dispatcher.UIThread.Post(() =>
        {
            if (_filteredItems.Count == 0 || _engine.State != EngineState.Stopped)
                return;

            var selectedIndex = GetSelectedFilteredIndex();
            if (selectedIndex < 0)
                selectedIndex = 0;
            EnsureVisibleWindowAround(selectedIndex);
            SetFilteredSelectedIndex(selectedIndex);
            StartPlayback();
            _engine.Pause();
            UpdatePlaybackPositionUi();
            UpdateButtonStates();
        }, DispatcherPriority.Background);
    }

    private void RefreshLibraryPresentation()
    {
        EditTrackOverlay.InvalidateLookups();
        LoadLookups();
        RefreshTrackList();
        PrepareActiveTrackEditor();
    }

    private void MarkLibraryRefreshPending()
    {
        _libraryRefreshPending = true;
        UpdateLibraryRefreshState();
    }

    private void ClearLibraryRefreshPending()
    {
        _libraryRefreshPending = false;
        UpdateLibraryRefreshState();
    }

    private void UpdateLibraryRefreshState()
    {
        RefreshLibraryButton.IsVisible = _libraryRefreshPending;
    }

    private void InitializeSortControls()
    {
        var selectedIndex = _sortBy switch
        {
            LibrarySortBy.Rating => 1,
            LibrarySortBy.DownloadedAt => 2,
            LibrarySortBy.CollectionOrder => 3,
            _ => 0
        };
        FilterSortText.Text = SortLabel(selectedIndex);
        RefreshFilterSortOptions(selectedIndex);
        UpdateSortDirectionButton();
    }

    private void OnFilterSortOptionClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !int.TryParse(tag, out var selectedIndex))
            return;

        var nextSort = selectedIndex switch
        {
            1 => LibrarySortBy.Rating,
            2 => LibrarySortBy.DownloadedAt,
            3 when _activeCollection is not null => LibrarySortBy.CollectionOrder,
            _ => LibrarySortBy.Name
        };
        if (nextSort == LibrarySortBy.DownloadedAt && _sortBy != LibrarySortBy.DownloadedAt)
        {
            _sortDirection = LibrarySortDirection.Descending;
            UpdateSortDirectionButton();
        }
        _sortBy = nextSort;
        FilterSortText.Text = SortLabel(selectedIndex);
        RefreshFilterSortOptions(selectedIndex);
        FilterSortButton.Flyout?.Hide();
        PrepareExplicitSort();
        ApplyFilter();
        ResetPlaybackQueueFromCurrentView();
        PersistPlayerSession();
        e.Handled = true;
    }

    private static string SortLabel(int index) => index switch
    {
        1 => "Rating",
        2 => "Downloaded at",
        3 => "Collection order",
        _ => "Name"
    };

    private void RefreshFilterSortOptions(int selectedIndex)
    {
        var options = new (Button Button, Image Check)[]
        {
            (FilterSortNameOption, FilterSortNameCheck),
            (FilterSortRatingOption, FilterSortRatingCheck),
            (FilterSortDownloadedOption, FilterSortDownloadedCheck),
            (FilterSortCollectionOption, FilterSortCollectionCheck)
        };

        for (var index = 0; index < options.Length; index++)
        {
            var (button, check) = options[index];
            button.Classes.Remove("selected");
            if (index == selectedIndex)
                button.Classes.Add("selected");
            check.Opacity = index == selectedIndex ? 0.9 : 0;
        }
    }

    private void OnSortDirectionClicked(object? sender, RoutedEventArgs e)
    {
        if (_sortBy == LibrarySortBy.CollectionOrder)
            return;

        _sortDirection = _sortDirection == LibrarySortDirection.Ascending
            ? LibrarySortDirection.Descending
            : LibrarySortDirection.Ascending;
        UpdateSortDirectionButton();
        PrepareExplicitSort();
        ApplyFilter();
        ResetPlaybackQueueFromCurrentView();
        PersistPlayerSession();
    }

    private void PrepareExplicitSort()
    {
        // A deliberate sort selection owns the queue order. Restored queue order
        // and shuffle would otherwise immediately overwrite the sorted result.
        _pendingRestoredQueueOrder = null;
        if (!_shuffle)
            return;

        _shuffle = false;
        _shufflePriorities.Clear();
        UpdateShuffleButton();
    }

    private void UpdateSortDirectionButton()
    {
        SortDirectionButton.Content = _sortDirection == LibrarySortDirection.Ascending ? "↑" : "↓";
        SortDirectionButton.IsEnabled = _sortBy != LibrarySortBy.CollectionOrder;
        ToolTip.SetTip(SortDirectionButton,
            _sortBy == LibrarySortBy.CollectionOrder
                ? "Collection order is edited from the track context menu"
                : _sortDirection == LibrarySortDirection.Ascending ? "Ascending" : "Descending");
    }

    private void OnResetCollectionViewClicked(object? sender, RoutedEventArgs e)
    {
        if (_activeCollection is null)
            return;

        _suppressPresetAutoSave = true;
        try
        {
            _activeFilterPresetName = null;
            _activeBuiltInView = LibraryMode.Library;
            SearchBox.Text = string.Empty;
            _updatingLibraryMode = true;
            ShowNeedsReviewCheckBox.IsChecked = false;
            ShowDeclinedCheckBox.IsChecked = false;
            _filterGroups.Clear();
            RebuildFilterConditionsPanel();
            ClearConditionBuilder();
            RefreshCompletionFilterVisuals();
            _shuffle = false;
            _shufflePriorities.Clear();
            _sortBy = LibrarySortBy.CollectionOrder;
            _sortDirection = LibrarySortDirection.Ascending;
        }
        finally
        {
            _updatingLibraryMode = false;
            _suppressPresetAutoSave = false;
        }

        UpdateShuffleButton();
        InitializeSortControls();
        RebuildPresetRows();
        ApplyFilter();
        ResetPlaybackQueueFromCurrentView();
        PersistPlayerSession();
    }

    // ─── Track list ──────────────────────────────────────────────────────────

    private void LoadLookups()
    {
        var selectedRatings = _selectedRatingNames.ToList();

        Values.Genres = MusicLibraryService.Current.GetGenres();
        Values.Tags = MusicLibraryService.Current.GetTags();
        Values.Styles = MusicLibraryService.Current.GetStyles();
        Values.Ratings = MusicLibraryService.Current.GetRatings();

        _selectedRatingNames.Clear();
        foreach (var rating in Values.Ratings)
            if (selectedRatings.Contains(rating.Name, StringComparer.OrdinalIgnoreCase))
                _selectedRatingNames.Add(rating.Name);
        RefreshRatingFilterControls();

        if (_conditionGenreCtrl is not null && _conditionStyleCtrl is not null && _conditionTagCtrl is not null && _conditionLanguageCtrl is not null)
        {
            _conditionGenreCtrl.SetItems(GenreFilterOptions());
            _conditionStyleCtrl.SetItems(StyleFilterOptions());
            _conditionTagCtrl.SetItems(TagFilterOptions());
            _conditionLanguageCtrl.SetItems(LanguageFilterOptions());
            RefreshConditionBuilder();
        }

        foreach (var fg in _filterGroups)
        {
            fg.GenreCtrl.SetItems(GenreFilterOptions());
            fg.StyleCtrl.SetItems(StyleFilterOptions());
            fg.TagCtrl.SetItems(TagFilterOptions());
            fg.LanguageCtrl.SetItems(LanguageFilterOptions());
        }
        RebuildFilterConditionsPanel();
    }

    private void RefreshTrackList()
    {
        _thumbLoadCts?.Cancel();
        _thumbLoadCts = new CancellationTokenSource();

        var previousItems = _allItems.ToDictionary(item => item.Track.Id);

        var tracks = MusicLibraryService.Current.GetTracksForLibraryView();
        _allTrackCollectionNames = MusicLibraryService.Current.GetAllTrackCollectionNames();
        var unanalyzedTrackIds = MusicLibraryService.Current.GetTrackIdsMissingAnalysis();
        _allTrackStyleIds = MusicLibraryService.Current.GetAllTrackStyleIds();
        _allTrackGenreIds = MusicLibraryService.Current.GetAllTrackGenreIds();
        _allTrackTagIds = MusicLibraryService.Current.GetAllTrackTagIds();
        _allTrackAudioAnalyses = MusicLibraryService.Current.GetAllTrackAudioAnalyses();
        _allTrackMirexScores = MusicLibraryService.Current.GetAllMirexScores();
        _allTrackUsageStats = MusicLibraryService.Current.GetAllTrackUsageStats();

        var mainGenreNamesById = MusicLibraryService.Current.GetModelGenres()
            .ToDictionary(genre => genre.Id, genre => genre.Name);
        var modelSubgenres = MusicLibraryService.Current.GetModelSubgenres();
        _mainGenreNamesBySubgenreId = modelSubgenres
            .Where(subgenre => mainGenreNamesById.ContainsKey(subgenre.ModelGenreId))
            .ToDictionary(subgenre => subgenre.Id, subgenre => mainGenreNamesById[subgenre.ModelGenreId]);
        _mainGenreNamesBySubgenreName = modelSubgenres
            .Where(subgenre => mainGenreNamesById.ContainsKey(subgenre.ModelGenreId))
            .GroupBy(subgenre => ShortGenreName(subgenre.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => mainGenreNamesById[group.First().ModelGenreId],
                StringComparer.OrdinalIgnoreCase);

        var genreMap = Values.Genres.ToDictionary(g => g.Id, g => g.Name);
        var tagMap = Values.Tags.ToDictionary(t => t.Id);
        var tagOrder = Values.Tags.Select((tag, index) => (tag.Id, index))
            .ToDictionary(item => item.Id, item => item.index);
        var ratingMap = Values.Ratings.ToDictionary(r => r.Id, r => r.Name);
        var styleMap = Values.Styles.ToDictionary(s => s.Id, s => s.Name);

        var newItems = new List<TrackDisplayItem>();
        foreach (var track in tracks)
        {
            var needsAnalysis = unanalyzedTrackIds.Contains(track.Id);
            if (previousItems.TryGetValue(track.Id, out var previous)
                && string.Equals(previous.Track.UpdatedAt, track.UpdatedAt, StringComparison.Ordinal)
                && previous.NeedsAnalysis == needsAnalysis)
            {
                ApplyCollectionDisplay(previous);
                newItems.Add(previous);
                continue;
            }

            var item = CreateTrackDisplayItem(track, needsAnalysis);
            ApplyCollectionDisplay(item);
            if (previous?.Thumbnail is not null)
            {
                item.Thumbnail = previous.Thumbnail;
                item.SetArtworkPalette(previous.ArtworkPrimaryColor, previous.ArtworkSecondaryColor);
                previous.Thumbnail = null;
            }
            newItems.Add(item);
        }

        foreach (var previous in previousItems.Values)
            if (!newItems.Contains(previous))
                previous.Thumbnail?.Dispose();

        _allItems = newItems;

        ApplyFilter();
        if (_engine.ActiveTrackId >= 0
            && _allItems.FirstOrDefault(item => item.Track.Id == _engine.ActiveTrackId)?.Track is { } activeTrack)
        ClearLibraryRefreshPending();
    }

    private void ApplyCollectionDisplay(TrackDisplayItem item)
    {
        var names = _allTrackCollectionNames.GetValueOrDefault(item.Track.Id, []);
        if (names.Count == 0)
        {
            item.CollectionDisplayText = string.Empty;
            item.CollectionOverflowText = string.Empty;
            item.CollectionTooltip = string.Empty;
            return;
        }

        var primary = _activeCollection is not null && names.Contains(_activeCollection.Name, StringComparer.OrdinalIgnoreCase)
            ? _activeCollection.Name
            : names[0];
        item.CollectionDisplayText = primary;
        item.CollectionOverflowText = names.Count > 1 ? $"+{names.Count - 1}" : string.Empty;
        item.CollectionTooltip = string.Join("\n", names);
    }

    private TrackDisplayItem CreateTrackDisplayItem(MusicTrack track, bool needsAnalysis)
    {
        var genreIds = _allTrackGenreIds.GetValueOrDefault(track.Id, []);
        var tagIds = _allTrackTagIds.GetValueOrDefault(track.Id, []);
        var styleIds = _allTrackStyleIds.GetValueOrDefault(track.Id, []);

        var genreMap = Values.Genres.ToDictionary(g => g.Id, g => g.Name);
        var tagMap = Values.Tags.ToDictionary(t => t.Id);
        var tagOrder = Values.Tags.Select((tag, index) => (tag.Id, index))
            .ToDictionary(item => item.Id, item => item.index);
        var ratingMap = Values.Ratings.ToDictionary(r => r.Id, r => r.Name);
        var styleMap = Values.Styles.ToDictionary(s => s.Id, s => s.Name);

        var modelGenreAssignments = MusicLibraryService.Current.GetTrackModelGenres(track.Id)
            .Where(assignment => assignment.IsEnabled)
            .ToList();
        var modelGenreStr = string.Join(", ", modelGenreAssignments
            .Where(assignment => !assignment.IsManual)
            .Select(assignment => ShortGenreName(assignment.GenreName))
            .Where(name => name.Length > 0)
            .Order());
        var manualGenreStr = string.Join(", ", modelGenreAssignments
            .Where(assignment => assignment.IsManual)
            .Select(assignment => ShortGenreName(assignment.GenreName))
            .Where(name => name.Length > 0)
            .Order());
        var activeGenres = modelGenreAssignments
            .Select(assignment => new
            {
                Name = ShortGenreName(assignment.GenreName),
                MainGenre = _mainGenreNamesBySubgenreId.GetValueOrDefault(assignment.GenreId)
            })
            .Where(genre => genre.Name.Length > 0)
            .GroupBy(genre => genre.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(genre => genre.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (activeGenres.Count == 0)
        {
            activeGenres = genreIds
                .Select(id => new
                {
                    Name = ShortGenreName(genreMap.GetValueOrDefault(id, "")),
                    MainGenre = _mainGenreNamesBySubgenreId.GetValueOrDefault(id)
                })
                .Where(genre => genre.Name.Length > 0)
                .GroupBy(genre => genre.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(genre => genre.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        var genreDisplays = activeGenres
            .Select(genre => new TrackGenreDisplay(
                genre.Name,
                MainGenrePalette.For(genre.MainGenre
                                     ?? _mainGenreNamesBySubgenreName.GetValueOrDefault(genre.Name))))
            .ToList();
        var activeGenreNames = genreDisplays.Select(genre => genre.Name).ToList();
        var genreStr = FormatNaturalList(activeGenreNames);
        var styleStr = string.Join(", ", styleIds
            .Select(id => styleMap.GetValueOrDefault(id, ""))
            .Where(n => n.Length > 0).Order());
        var trackTags = tagIds
            .Select(id => tagMap.GetValueOrDefault(id))
            .Where(tag => tag is not null)
            .Cast<Tag>()
            .OrderBy(tag => tagOrder.GetValueOrDefault(tag.Id, int.MaxValue))
            .ToList();
        var tagDisplays = trackTags
            .Select(tag => new TrackTagDisplay(
                tag.Name,
                CategoryBrush(null)))
            .ToList();
        var languageName = TrackLanguageCatalog.Name(track.LanguageCode);
        if (!string.IsNullOrWhiteSpace(languageName))
            tagDisplays.Add(new TrackTagDisplay(languageName, Brush("#A8CFF4")));
        var ratingName = track.RatingId is int ratingId ? ratingMap.GetValueOrDefault(ratingId, "") : "None";
        var durationText = track.DurationSeconds.HasValue ? FormatDuration(track.DurationSeconds.Value) : "";

        var item = new TrackDisplayItem(track, genreStr, modelGenreStr, manualGenreStr, styleStr, durationText, ratingName, genreDisplays, tagDisplays, track.DisplayChannelName ?? "")
        {
            NeedsReview = track.NeedsReview,
            NeedsAnalysis = needsAnalysis,
            IsPlaying = _engine.ActiveTrackId == track.Id
        };
        item.ApplyAppearance(_appearanceSettings);
        return item;
    }

    private void UpdateTrackInList(int trackId)
    {
        var selectedTrackId = (FileList.SelectedItem as TrackDisplayItem)?.Track.Id;
        var previous = _allItems.FirstOrDefault(item => item.Track.Id == trackId);
        var updatedTrack = MusicLibraryService.Current.GetTrackById(trackId);
        if (updatedTrack is null)
        {
            RefreshTrackList();
            return;
        }

        _allTrackStyleIds[trackId] = MusicLibraryService.Current.GetTrackStyleIds(trackId);
        _allTrackGenreIds[trackId] = MusicLibraryService.Current.GetTrackGenreIds(trackId);
        _allTrackTagIds[trackId] = MusicLibraryService.Current.GetTrackTagIds(trackId);

        var needsAnalysis = MusicLibraryService.Current.GetTrackAudioAnalysis(trackId) is null;
        var updatedItem = CreateTrackDisplayItem(updatedTrack, needsAnalysis);
        if (previous?.Thumbnail is not null)
        {
            updatedItem.Thumbnail = previous.Thumbnail;
            updatedItem.SetArtworkPalette(previous.ArtworkPrimaryColor, previous.ArtworkSecondaryColor);
            previous.Thumbnail = null;
        }

        var allIndex = _allItems.FindIndex(item => item.Track.Id == trackId);
        if (allIndex >= 0)
            _allItems[allIndex] = updatedItem;
        else
            _allItems.Insert(0, updatedItem);

        var filteredIndex = _filteredItems.FindIndex(item => item.Track.Id == trackId);
        if (filteredIndex >= 0)
            _filteredItems[filteredIndex] = updatedItem;

        if (_engine.ActiveTrackId == trackId)
        {
            NowPlayingText.Text = updatedTrack.DisplayTitle;
            UpdateDiscordPresence();
        }

        if (previous?.Track.IsOriginal != updatedTrack.IsOriginal
            || previous?.Track.ParentTrackId != updatedTrack.ParentTrackId
            || previous?.Track.EditTypes != updatedTrack.EditTypes)
        {
            ApplyFilterCore();
            return;
        }

        if (filteredIndex >= 0)
            EnsureVisibleWindowAround(filteredIndex);

        RefreshVisibleItemsSource(selectedTrackId);
        UpdatePlaylistSummary();
        RefreshNextTrackPreview();
        RestartVisibleThumbnailLoad();
    }

    private void ApplyRemoteMetadataToTrackList(MusicTrack updatedTrack)
    {
        var previous = _allItems.FirstOrDefault(item => item.Track.Id == updatedTrack.Id);
        if (previous is null)
            return;

        // Metadata backfill only changes source fields. Clone the presentation
        // item so Avalonia sees new derived texts, without running any database
        // queries on the UI thread for every completed YouTube request.
        var updated = previous with { Track = updatedTrack };
        ReplaceTrackDisplayItem(_allItems, previous, updated);
        ReplaceTrackDisplayItem(_filteredItems, previous, updated);
        ReplaceTrackDisplayItem(_visibleItems, previous, updated);

    }

    private static void ReplaceTrackDisplayItem(
        List<TrackDisplayItem> items,
        TrackDisplayItem previous,
        TrackDisplayItem updated)
    {
        var index = items.IndexOf(previous);
        if (index >= 0)
            items[index] = updated;
    }

    private List<FilterGroup> CurrentFilterGroups() =>
        _filterGroups
            .Select(fg => new FilterGroup(
                SelectedIds(fg.GenreCtrl.SelectedItems, Values.Genres, g => g.Name, g => g.Id),
                SelectedIds(fg.StyleCtrl.SelectedItems, Values.Styles, style => style.Name, style => style.Id),
                SelectedIds(fg.TagCtrl.SelectedItems, Values.Tags, TagFilterName, t => t.Id),
                fg.LanguageCtrl.SelectedItems.ToHashSet(StringComparer.OrdinalIgnoreCase),
                fg.EmotionalCharacters
                    .Where(pair => pair.Value.IsActive)
                    .Select(pair => new EmotionalCharacterRange(pair.Key, pair.Value.MinimumPercent, null))
                    .ToList(),
                fg.Negate,
                Values.Genres
                    .Where(genre => fg.MainGenres.Contains(MainGenreName(genre.Name)))
                    .Select(genre => genre.Id)
                    .ToHashSet(),
                fg.VersionCtrl.SelectedItems.ToHashSet()))
            .ToList();

    private static string ShortGenreName(string genreName)
    {
        var separator = genreName.LastIndexOf('→');
        return separator >= 0 && separator + 1 < genreName.Length
            ? genreName[(separator + 1)..].Trim()
            : genreName;
    }

    private static string MainGenreName(string genreName)
    {
        var parts = genreName.Split('→', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2 ? parts[0] : string.Empty;
    }

    private static string TagFilterName(Tag tag) => tag.Name;

    private static IEnumerable<MultiSelectFilterControl.FilterOption> GenreFilterOptions() =>
        Values.Genres.Select(genre =>
        {
            var parts = genre.Name.Split('→', 2, StringSplitOptions.TrimEntries);
            return parts.Length == 2
                ? new MultiSelectFilterControl.FilterOption(
                    genre.Name,
                    parts[1],
                    parts[0],
                    MainGenrePalette.For(parts[0]))
                : new MultiSelectFilterControl.FilterOption(genre.Name, genre.Name);
        });

    private static IEnumerable<MultiSelectFilterControl.FilterOption> TagFilterOptions() =>
        Values.Tags.Select(tag => new MultiSelectFilterControl.FilterOption(
            TagFilterName(tag),
            tag.Name));

    private static IEnumerable<MultiSelectFilterControl.FilterOption> StyleFilterOptions() =>
        Values.Styles.Select(style => new MultiSelectFilterControl.FilterOption(
            style.Name,
            style.Name));

    private static IEnumerable<MultiSelectFilterControl.FilterOption> LanguageFilterOptions() =>
        TrackLanguageCatalog.All.Select(language => new MultiSelectFilterControl.FilterOption(
            language.Code,
            language.Name));

    private static string DisplayLanguageFilterName(string languageCode) =>
        TrackLanguageCatalog.Name(languageCode) ?? languageCode;

    private static IBrush CategoryBrush(string? color)
    {
        try { return new SolidColorBrush(Color.Parse(string.IsNullOrWhiteSpace(color) ? "#C7E59F" : color)); }
        catch { return new SolidColorBrush(Color.Parse("#C7E59F")); }
    }

    private static IBrush SafeBrush(string? color, string fallback)
    {
        try { return new SolidColorBrush(Color.Parse(string.IsNullOrWhiteSpace(color) ? fallback : color)); }
        catch { return new SolidColorBrush(Color.Parse(fallback)); }
    }

    private async Task LoadThumbnailsAsync(CancellationToken ct)
    {
        var items = _visibleItems.ToList();

        Dictionary<int, LoadedTrackThumbnail?> artworkByTrackId;
        try
        {
            artworkByTrackId = await Task.Run(() =>
            {
                var result = new Dictionary<int, LoadedTrackThumbnail?>();
                foreach (var item in items)
                {
                    ct.ThrowIfCancellationRequested();
                    var artwork = MusicLibraryService.Current.GetTrackThumbnail(item.Track.Id);
                    result[item.Track.Id] = artwork is { Length: > 0 }
                        ? new LoadedTrackThumbnail(artwork, ExtractAmbientPalette(artwork))
                        : null;
                }
                return result;
            }, ct);
        }
        catch (OperationCanceledException) { return; }

        if (ct.IsCancellationRequested) return;

        bool any = false;
        foreach (var item in items)
        {
            if (item.Thumbnail is not null)
                continue;

            if (artworkByTrackId.TryGetValue(item.Track.Id, out var loaded) && loaded is not null)
            {
                try
                {
                    using var stream = new MemoryStream(loaded.Artwork);
                    item.Thumbnail = new Bitmap(stream);
                    item.SetArtworkPalette(loaded.Palette.Primary, loaded.Palette.Secondary);
                    any = true;
                }
                catch { }
            }
        }

        if (!any || ct.IsCancellationRequested) return;

        RefreshVisibleItemsSource((FileList.SelectedItem as TrackDisplayItem)?.Track.Id);
    }

    private void ApplyFilter()
    {
        var previousSuppression = _suppressSelectionSessionSave;
        _suppressSelectionSessionSave = true;
        try
        {
            ApplyFilterCore();
        }
        finally
        {
            _suppressSelectionSessionSave = previousSuppression;
        }
    }

    private IEnumerable<TrackDisplayItem> CollectionScopedItems()
    {
        if (_activeCollection is null)
            return _allItems;

        var trackIds = _activeCollectionOrder.Keys.ToHashSet();
        return _allItems.Where(item => trackIds.Contains(item.Track.Id));
    }

    private bool CanEditCollectionOrder() =>
        _activeCollection is not null
        && _sortBy == LibrarySortBy.CollectionOrder
        && !_shuffle
        && string.IsNullOrWhiteSpace(SearchBox.Text)
        && !_manualRatingFilter
        && _filterGroups.Count == 0
        && _activeFilterPresetName is null
        && _activeBuiltInView == LibraryMode.Library
        && ShowNeedsReviewCheckBox.IsChecked != true
        && ShowDeclinedCheckBox.IsChecked != true;

    private void RefreshActiveCollectionOrder()
    {
        _activeCollectionOrder = _activeCollection is null
            ? []
            : MusicLibraryService.Current.GetCollectionTrackIds(_activeCollection.Id)
                .Select((trackId, position) => (trackId, position))
                .ToDictionary(item => item.trackId, item => item.position);
    }

    private void ApplyFilterCore()
    {
        var itemById = _allItems.ToDictionary(i => i.Track.Id);
        var sourceItems = CollectionScopedItems().ToList();
        List<MusicTrack> filtered;

        if (ShowDeclinedCheckBox.IsChecked == true)
        {
            // Declined mode is a global work view, independent of the user's
            // musical filters. Turning it off restores those filters unchanged.
            filtered = sourceItems
                .Where(item => item.Track.LibraryState == TrackLibraryState.Rejected)
                .Select(item => item.Track)
                .ToList();
        }
        else
        {
            var selRatingIds = SelectedRatingIds();
            var groups = CurrentFilterGroups();
            var reviewOnly = ShowNeedsReviewCheckBox.IsChecked == true;

            filtered = TrackFilter.Apply(
                sourceItems
                    .Where(item => item.Track.LibraryState != TrackLibraryState.Rejected
                                   && (!reviewOnly || item.NeedsReview))
                    .Select(item => item.Track),
                _allTrackGenreIds,
                _allTrackStyleIds,
                _allTrackTagIds,
                _allTrackMirexScores,
                selRatingIds,
                groups,
                SearchBox.Text);

            if (_manualRatingFilter && selRatingIds.Count == 0)
                filtered.Clear();
        }

        _filteredItems = filtered
            .Where(t => itemById.ContainsKey(t.Id))
            .Select(t => itemById[t.Id])
            .ToList();

        if (ShowDeclinedCheckBox.IsChecked != true
            && ShowNeedsReviewCheckBox.IsChecked != true)
            _filteredItems = _filteredItems
                .Where(item => !item.NeedsReview
                               && (_activeBuiltInView != LibraryMode.Library || item.Track.RatingId is not null))
                .ToList();

        ApplyLibrarySort();

        if (_shuffle)
            ShuffleFilteredItems();

        RestoreSavedQueueOrder();

        SyncPlaybackQueueWithLoadedPlaylist();

        var selectedTrackId = (FileList.SelectedItem as TrackDisplayItem)?.Track.Id;

        RefreshVisibleItemsSource(selectedTrackId);
        RestoreOrInitializeSelection(selectedTrackId);
        UpdatePlaylistSummary();
        RefreshNextTrackPreview();
        UpdateFilterCounts();
        RestartVisibleThumbnailLoad();
    }

    private void ApplyLibrarySort()
    {
        foreach (var item in _filteredItems)
            item.ShowDownloadedDate = _sortBy == LibrarySortBy.DownloadedAt;

        var ratingSortById = Values.Ratings.ToDictionary(rating => rating.Id, rating => rating.SortOrder);
        var originalTitles = _allItems.Where(item => item.Track.IsOriginal)
            .ToDictionary(item => item.Track.Id, item => item.Track.Title);
        string GroupTitle(TrackDisplayItem item) => item.Track.ParentTrackId is int parent
            ? originalTitles.GetValueOrDefault(parent, item.Track.Title) : item.Track.Title;
        IOrderedEnumerable<TrackDisplayItem> sorted = _sortBy switch
        {
            LibrarySortBy.CollectionOrder when _activeCollection is not null =>
                _filteredItems
                    .OrderBy(item => _activeCollectionOrder.GetValueOrDefault(item.Track.Id, int.MaxValue)),
            LibrarySortBy.Rating when _sortDirection == LibrarySortDirection.Ascending =>
                _filteredItems
                    .OrderBy(item => item.Track.RatingId is int ratingId
                        ? ratingSortById.GetValueOrDefault(ratingId, int.MaxValue)
                        : int.MaxValue)
                    .ThenBy(item => item.Track.Title, StringComparer.OrdinalIgnoreCase),
            LibrarySortBy.Rating =>
                _filteredItems
                    .OrderByDescending(item => item.Track.RatingId is int ratingId
                        ? ratingSortById.GetValueOrDefault(ratingId, int.MinValue)
                        : int.MinValue)
                    .ThenBy(item => item.Track.Title, StringComparer.OrdinalIgnoreCase),
            LibrarySortBy.Name when _sortDirection == LibrarySortDirection.Descending =>
                _filteredItems.OrderByDescending(GroupTitle, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.Track.Title, StringComparer.OrdinalIgnoreCase),
            LibrarySortBy.DownloadedAt when _sortDirection == LibrarySortDirection.Ascending =>
                _filteredItems
                    .OrderBy(item => DownloadedAtSortValue(item.Track.DownloadedAt))
                    .ThenBy(item => item.Track.Title, StringComparer.OrdinalIgnoreCase),
            LibrarySortBy.DownloadedAt =>
                _filteredItems
                    .OrderByDescending(item => DownloadedAtSortValue(item.Track.DownloadedAt))
                    .ThenBy(item => item.Track.Title, StringComparer.OrdinalIgnoreCase),
            _ =>
                _filteredItems.OrderBy(GroupTitle, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.Track.Title, StringComparer.OrdinalIgnoreCase)
        };

        var sortedItems = sorted.ToList();
        var itemsById = sortedItems.ToDictionary(item => item.Track.Id);
        _filteredItems = TrackGrouping.Build(sortedItems.Select(item => item.Track).ToList(),
                _allItems.Select(item => item.Track))
            .Where(row => !row.IsContextOnly)
            .Select(row => itemsById[row.Track.Id]).ToList();
    }

    private static DateTimeOffset DownloadedAtSortValue(string downloadedAt) =>
        DateTimeOffset.TryParse(downloadedAt, out var value)
            ? value
            : DateTimeOffset.MinValue;

    private void RestoreOrInitializeSelection(int? previousSelectedTrackId)
    {
        if (_filteredItems.Count == 0)
        {
            SetFilteredSelectedIndex(-1);
            return;
        }

        var targetTrackId = _engine.ActiveTrackId >= 0
            ? _engine.ActiveTrackId
            : previousSelectedTrackId ?? _pendingRestoredTrackId;

        if (targetTrackId is int id)
        {
            var index = _filteredItems.FindIndex(item => item.Track.Id == id);
            if (index >= 0)
            {
                EnsureVisibleWindowAround(index);
                SetFilteredSelectedIndex(index);
                _pendingRestoredTrackId = null;
                return;
            }
        }

        if (FileList.SelectedIndex < 0 || FileList.SelectedIndex >= _visibleItems.Count)
            SetFilteredSelectedIndex(0);

        if (FileList.SelectedIndex >= 0 && FileList.SelectedIndex < _visibleItems.Count)
            FileList.ScrollIntoView(_visibleItems[FileList.SelectedIndex]);
    }

    private void RefreshVisibleItemsSource(int? selectedTrackId = null)
    {
        selectedTrackId ??= (FileList.SelectedItem as TrackDisplayItem)?.Track.Id;
        var itemById = _allItems.ToDictionary(item => item.Track.Id);
        _visibleItems = TrackGrouping.Build(_filteredItems.Select(item => item.Track).ToList(),
                _allItems.Select(item => item.Track))
            .Select(row =>
            {
                var item = itemById[row.Track.Id];
                item.IsContextOnly = row.IsContextOnly;
                item.IsVersionChild = row.IsChild;
                item.ShowDownloadedDate = _sortBy == LibrarySortBy.DownloadedAt;
                return item;
            }).ToList();

        FileList.ItemsSource = _visibleItems;
        if (selectedTrackId is int id)
        {
            var index = _visibleItems.FindIndex(item => item.Track.Id == id);
            if (index >= 0)
                FileList.SelectedIndex = index;
        }
    }

    private void EnsureVisibleWindowAround(int filteredIndex)
    {
        if (filteredIndex < 0 || filteredIndex >= _filteredItems.Count)
            return;

        var visible = _visibleItems.FirstOrDefault(item => item.Track.Id == _filteredItems[filteredIndex].Track.Id);
        if (visible is not null)
            FileList.ScrollIntoView(visible);
    }

    private void RestoreSavedQueueOrder()
    {
        if (_pendingRestoredQueueOrder is null || _filteredItems.Count == 0)
            return;

        var fallbackOrder = _filteredItems
            .Select((item, index) => (item.Track.Id, index))
            .ToDictionary(item => item.Id, item => item.index);
        var savedCount = _pendingRestoredQueueOrder.Count;

        _filteredItems = _filteredItems
            .OrderBy(item => _pendingRestoredQueueOrder.TryGetValue(item.Track.Id, out var savedIndex)
                ? savedIndex
                : savedCount + fallbackOrder[item.Track.Id])
            .ToList();

        if (_shuffle)
        {
            _shufflePriorities.Clear();
            for (var index = 0; index < _filteredItems.Count; index++)
                _shufflePriorities[_filteredItems[index].Track.Id] = index;
        }

        _pendingRestoredQueueOrder = null;
    }

    private void PersistPlayerSession()
    {
        if (_restoringPlayerSession)
            return;

        AppSettingsStore.SavePlayerSession(new PlayerSessionSettings
        {
            ActiveFilterPresetName = _activeFilterPresetName,
            ActiveCollectionStableId = _activeCollection?.StableId,
            ManualRatingFilter = _manualRatingFilter,
            SelectedRatingNames = SortedNames(_selectedRatingNames),
            ActiveTrackId = _engine.ActiveTrackId >= 0 ? _engine.ActiveTrackId : null,
            SelectedTrackId = (FileList.SelectedItem as TrackDisplayItem)?.Track.Id,
            ShuffleEnabled = _shuffle,
            SortBy = _sortBy.ToString(),
            SortDirection = _sortDirection.ToString(),
            QueueTrackIds = (_playbackQueue.IsInitialized
                    ? _playbackQueue.TrackIds
                    : _filteredItems.Select(item => item.Track.Id))
                .ToList()
        });
    }

    private void RestorePlaybackQueueSession()
    {
        var savedTrackIds = _restoredPlayerSession.QueueTrackIds
            .Where(trackId => _filteredItems.Any(item => item.Track.Id == trackId))
            .Distinct()
            .ToList();
        if (savedTrackIds.Count == 0)
            return;

        var currentTrackId = _restoredPlayerSession.ActiveTrackId
                             ?? _restoredPlayerSession.SelectedTrackId;
        if (currentTrackId is not int current || !savedTrackIds.Contains(current))
            current = savedTrackIds[0];
        _playbackQueue.Reset(savedTrackIds, current);
    }

    private void SyncPlaybackQueueWithLoadedPlaylist()
    {
        var filteredTrackIds = _filteredItems.Select(item => item.Track.Id).ToList();
        if (_loadedPlaylistSourceTrackIds.SequenceEqual(filteredTrackIds))
        {
            ApplyPlaybackQueueToLoadedPlaylist();
            return;
        }

        _loadedPlaylistSourceTrackIds = filteredTrackIds;
        if (filteredTrackIds.Count == 0)
        {
            if (_engine.ActiveTrackId >= 0)
                _playbackQueue.Reset([], _engine.ActiveTrackId);
            else
                _playbackQueue.Clear();
            return;
        }

        var currentTrackId = _engine.ActiveTrackId >= 0
            ? _engine.ActiveTrackId
            : filteredTrackIds[0];
        _playbackQueue.Reset(filteredTrackIds, currentTrackId);
    }

    private int GetSelectedFilteredIndex()
    {
        if (FileList.SelectedItem is not TrackDisplayItem selected)
            return -1;

        return _filteredItems.FindIndex(item => item.Track.Id == selected.Track.Id);
    }

    private void SetFilteredSelectedIndex(int filteredIndex)
    {
        FileList.SelectedIndex = filteredIndex >= 0 && filteredIndex < _filteredItems.Count
            ? _visibleItems.FindIndex(item => item.Track.Id == _filteredItems[filteredIndex].Track.Id)
            : -1;
    }

    private void RestartVisibleThumbnailLoad()
    {
        _thumbLoadCts?.Cancel();
        _thumbLoadCts = new CancellationTokenSource();
        _ = LoadThumbnailsAsync(_thumbLoadCts.Token);
    }

    private void UpdateFilterCounts()
    {
        foreach (var fg in _filterGroups)
        {
            var groupTracks = TracksMatchingSearchRatingAndGroup(fg);
            var groupTrackIds = groupTracks.Select(track => track.Id).ToList();

            var genreFacetCounts = MetadataCountService.FacetCounts(groupTrackIds, _allTrackGenreIds);
            var styleFacetCounts = MetadataCountService.FacetCounts(groupTrackIds, _allTrackStyleIds);
            var tagFacetCounts = MetadataCountService.FacetCounts(groupTrackIds, _allTrackTagIds);

            var genreCountByName = Values.Genres.ToDictionary(g => g.Name,
                g => genreFacetCounts.GetValueOrDefault(g.Id, 0));
            var styleCountByName = Values.Styles.ToDictionary(style => style.Name,
                style => styleFacetCounts.GetValueOrDefault(style.Id, 0));
            var tagCountByName = Values.Tags.ToDictionary(TagFilterName,
                t => tagFacetCounts.GetValueOrDefault(t.Id, 0));

            fg.GenreCtrl.UpdateCounts(genreCountByName);
            fg.StyleCtrl.UpdateCounts(styleCountByName);
            fg.TagCtrl.UpdateCounts(tagCountByName);
        }
    }

    private List<MusicTrack> TracksMatchingSearchRatingAndGroup(FilterGroupControls group)
    {
        if (ShowDeclinedCheckBox.IsChecked == true)
            return CollectionScopedItems()
                .Where(item => item.Track.LibraryState == TrackLibraryState.Rejected)
                .Select(item => item.Track)
                .ToList();

        var reviewOnly = ShowNeedsReviewCheckBox.IsChecked == true;
        IEnumerable<MusicTrack> query = CollectionScopedItems()
            .Where(item => item.Track.LibraryState != TrackLibraryState.Rejected
                           && (!reviewOnly || item.NeedsReview))
            .Select(item => item.Track);
        var selectedRatingIds = SelectedRatingIds();
        var selectedGenreIds = SelectedIds(group.GenreCtrl.SelectedItems, Values.Genres, g => g.Name, g => g.Id);
        var selectedStyleIds = SelectedIds(group.StyleCtrl.SelectedItems, Values.Styles, style => style.Name, style => style.Id);
        var anyGenreIds = Values.Genres
            .Where(genre => group.MainGenres.Contains(MainGenreName(genre.Name)))
            .Select(genre => genre.Id)
            .ToHashSet();
        var selectedTagIds = SelectedIds(group.TagCtrl.SelectedItems, Values.Tags, TagFilterName, t => t.Id);
        var selectedLanguages = group.LanguageCtrl.SelectedItems.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var term = SearchBox.Text?.Trim();

        if (group.VersionCtrl.SelectedItems.Count > 0)
            query = query.Where(track => TrackVersions.Matches(track, group.VersionCtrl.SelectedItems));

        if (!string.IsNullOrWhiteSpace(term))
            query = query.Where(track =>
                track.DisplayTitle.Contains(term, StringComparison.OrdinalIgnoreCase)
                || track.OriginalTitle.Contains(term, StringComparison.OrdinalIgnoreCase));

        if (_manualRatingFilter && selectedRatingIds.Count == 0)
            return [];
        else if (selectedRatingIds.Count > 0)
            query = query.Where(track => track.RatingId is int ratingId && selectedRatingIds.Contains(ratingId));

        if (ShowNeedsReviewCheckBox.IsChecked != true)
        {
            var reviewTrackIds = _allItems
                .Where(item => item.NeedsReview)
                .Select(item => item.Track.Id)
                .ToHashSet();
            query = query.Where(track => !reviewTrackIds.Contains(track.Id));
        }

        if (selectedGenreIds.Count > 0)
            query = query.Where(track => TrackHasAllTags(track.Id, _allTrackGenreIds, selectedGenreIds));

        if (anyGenreIds.Count > 0)
            query = query.Where(track => TrackHasAnyTag(track.Id, _allTrackGenreIds, anyGenreIds));

        if (selectedStyleIds.Count > 0)
            query = query.Where(track => TrackHasAllTags(track.Id, _allTrackStyleIds, selectedStyleIds));

        if (selectedTagIds.Count > 0)
            query = query.Where(track => TrackHasAllTags(track.Id, _allTrackTagIds, selectedTagIds));

        if (selectedLanguages.Count > 0)
            query = query.Where(track => track.LanguageCode is not null && selectedLanguages.Contains(track.LanguageCode));

        var emotionalRanges = group.EmotionalCharacters.Where(pair => pair.Value.IsActive).ToList();
        if (emotionalRanges.Count > 0)
            query = query.Where(track => emotionalRanges.All(range =>
                _allTrackMirexScores.TryGetValue(track.Id, out var scores)
                && scores.TryGetValue(range.Key, out var score)
                && (range.Value.MinimumPercent is not double minimum || score * 100d >= minimum)));

        return query.ToList();
    }

    private static bool TrackHasAllTags(
        int trackId,
        IReadOnlyDictionary<int, List<int>> trackTagIds,
        IReadOnlySet<int> selectedTagIds)
    {
        trackTagIds.TryGetValue(trackId, out var trackTags);
        trackTags ??= [];
        return selectedTagIds.All(trackTags.Contains);
    }

    private static bool TrackHasAnyTag(
        int trackId,
        IReadOnlyDictionary<int, List<int>> trackTagIds,
        IReadOnlySet<int> selectedTagIds)
    {
        trackTagIds.TryGetValue(trackId, out var trackTags);
        trackTags ??= [];
        return selectedTagIds.Any(trackTags.Contains);
    }

    private static HashSet<int> SelectedIds<T>(IReadOnlySet<string> selected, List<T> source,
        Func<T, string> nameOf, Func<T, int> idOf)
    {
        if (selected.Count == 0) return [];
        return source.Where(item => selected.Contains(nameOf(item))).Select(idOf).ToHashSet();
    }

    private HashSet<int> SelectedRatingIds() =>
        !_manualRatingFilter
            ? []
            : Values.Ratings
                .Where(rating => _selectedRatingNames.Contains(rating.Name))
                .Select(rating => rating.Id)
                .ToHashSet();

    private void OnAllRatingsPressed(object? sender, PointerPressedEventArgs e)
    {
        SetRatingFilterMode(manual: false);
        e.Handled = true;
    }

    private void OnManualRatingsPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_manualRatingFilter)
            SetRatingFilterMode(manual: true);
        e.Handled = true;
    }

    private void SetRatingFilterMode(
        bool manual,
        IEnumerable<string>? selectedRatings = null,
        bool applyFilter = true)
    {
        _manualRatingFilter = manual;
        _selectedRatingNames.Clear();
        if (manual && selectedRatings is not null)
        {
            var available = Values.Ratings
                .Select(rating => rating.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var rating in selectedRatings)
                if (available.Contains(rating))
                    _selectedRatingNames.Add(rating);
        }

        RefreshRatingFilterControls();
        if (applyFilter)
        {
            ApplyFilter();
            PersistPlayerSession();
        }
    }

    private void RefreshRatingFilterControls()
    {
        RatingButtonsPanel.IsVisible = _manualRatingFilter;
        if (RatingModeIndicator.RenderTransform is TranslateTransform transform)
            transform.X = _manualRatingFilter ? 69 : 0;
        RatingModeIndicator.CornerRadius = _manualRatingFilter
            ? new CornerRadius(0, 5, 5, 0)
            : new CornerRadius(5, 0, 0, 5);
        AllRatingsText.Foreground = ThemeResources.Brush(_manualRatingFilter
            ? "Theme.Brush.TextMuted"
            : "Theme.Brush.TextStrong");
        ManualRatingsText.Foreground = ThemeResources.Brush(_manualRatingFilter
            ? "Theme.Brush.TextStrong"
            : "Theme.Brush.TextMuted");
        RatingButtonsPanel.Children.Clear();
        if (!_manualRatingFilter)
            return;

        foreach (var rating in Values.Ratings.OrderBy(rating => rating.SortOrder))
        {
            var selected = _selectedRatingNames.Contains(rating.Name);
            var accent = RatingAccentColor(rating.Name);
            var button = new Button
            {
                Content = rating.Name,
                Height = 31,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Background = selected
                    ? new SolidColorBrush(Color.FromArgb(76, accent.R, accent.G, accent.B))
                    : ThemeResources.Brush("Theme.Brush.Surface"),
                BorderBrush = selected
                    ? new SolidColorBrush(Color.FromArgb(210, accent.R, accent.G, accent.B))
                    : ThemeResources.Brush("Theme.Brush.BorderSubtle"),
                Foreground = selected
                    ? new SolidColorBrush(RatingForegroundColor(rating.Name))
                    : ThemeResources.Brush("Theme.Brush.TextMuted"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Opacity = selected ? 1 : 0.62,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            button.Click += (_, _) =>
            {
                if (!_selectedRatingNames.Add(rating.Name))
                    _selectedRatingNames.Remove(rating.Name);
                RefreshRatingFilterControls();
                ApplyFilter();
                PersistPlayerSession();
            };
            RatingButtonsPanel.Children.Add(button);
        }
    }

    private static Color RatingAccentColor(string ratingName) => ratingName switch
    {
        RatingNames.Timeless => Color.FromRgb(235, 194, 83),
        RatingNames.Amazing => Color.FromRgb(220, 145, 82),
        "Great" => Color.FromRgb(83, 190, 108),
        "Good" => Color.FromRgb(71, 177, 150),
        "Okay" => Color.FromRgb(151, 156, 116),
        RatingNames.Avoid => Color.FromRgb(211, 78, 65),
        _ => Color.FromRgb(205, 148, 67)
    };

    private static Color RatingForegroundColor(string ratingName) => ratingName switch
    {
        RatingNames.Timeless => Color.FromRgb(255, 230, 150),
        RatingNames.Amazing => Color.FromRgb(247, 195, 132),
        "Great" => Color.FromRgb(188, 242, 185),
        "Good" => Color.FromRgb(176, 232, 212),
        "Okay" => Color.FromRgb(226, 224, 194),
        RatingNames.Avoid => Color.FromRgb(246, 175, 160),
        _ => Color.FromRgb(243, 203, 128)
    };

    private void OnCompletionFilterChanged(object? sender, RoutedEventArgs e)
    {
        if (_updatingLibraryMode)
            return;

        if (sender == ShowNeedsReviewCheckBox && ShowNeedsReviewCheckBox.IsChecked == true)
        {
            SetLibraryMode(LibraryMode.Review);
            return;
        }
        else if (sender == ShowDeclinedCheckBox && ShowDeclinedCheckBox.IsChecked == true)
        {
            SetLibraryMode(LibraryMode.Declined);
            return;
        }

        if (ShowNeedsReviewCheckBox.IsChecked != true && ShowDeclinedCheckBox.IsChecked != true)
        {
            SetLibraryMode(LibraryMode.Library);
            return;
        }

        RefreshCompletionFilterVisuals();
        ApplyFilterDefinitionChange();
    }

    private void RefreshCompletionFilterVisuals()
    {
        var reviewSelected = ShowNeedsReviewCheckBox.IsChecked == true;
        ShowNeedsReviewCheckBox.Background = reviewSelected ? Brush("#24FFD27A") : Brushes.Transparent;
        ShowNeedsReviewCheckBox.BorderBrush = Brushes.Transparent;

        var declinedSelected = ShowDeclinedCheckBox.IsChecked == true;
        ShowDeclinedCheckBox.Background = declinedSelected ? Brush("#2EEE5C5C") : Brushes.Transparent;
        ShowDeclinedCheckBox.BorderBrush = Brushes.Transparent;

    }

    private void SetLibraryMode(LibraryMode mode)
    {
        _updatingLibraryMode = true;
        try
        {
            ShowNeedsReviewCheckBox.IsChecked = mode == LibraryMode.Review;
            ShowDeclinedCheckBox.IsChecked = mode == LibraryMode.Declined;
        }
        finally
        {
            _updatingLibraryMode = false;
        }

        RefreshCompletionFilterVisuals();
        ApplyFilterDefinitionChange();
    }

    // ─── Toolbar / filter panel ───────────────────────────────────────────────

    private void OnToggleFiltersClicked(object? sender, RoutedEventArgs e)
    {
        if (_filterPanelVisible)
            CloseFilterDrawer();
        else
            OpenFilterDrawer();
    }

    private void OpenFilterDrawer()
    {
        _filterPanelVisible = true;
        UpdateSettingsLayout();
        FilterDrawer.Opacity = 1;
        FilterDrawer.IsVisible = true;
        FilterDrawer.IsHitTestVisible = true;
        FiltersToggleBtn.Opacity = 1;
    }

    private void CloseFilterDrawer()
    {
        if (!_filterPanelVisible)
            return;

        _filterPanelVisible = false;
        FilterDrawer.IsHitTestVisible = false;
        FilterDrawer.IsVisible = false;
        FiltersToggleBtn.Opacity = 0.86;
    }

    private void OnSearchToggleClicked(object? sender, RoutedEventArgs e)
    {
        if (SearchBox.IsVisible)
        {
            SearchBox.Text = string.Empty;
            SearchBox.IsVisible = false;
            SearchToggleBtn.Opacity = 0.86;
            SearchToggleBtn.Focus();
            return;
        }

        SearchBox.IsVisible = true;
        SearchToggleBtn.Opacity = 1.0;
        Dispatcher.UIThread.Post(() => SearchBox.Focus(), DispatcherPriority.Background);
    }

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SearchBox.Text = string.Empty;
            SearchBox.IsVisible = false;
            SearchToggleBtn.Focus();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            SearchToggleBtn.Focus();
            e.Handled = true;
        }
    }

    private void OnSearchBoxLostFocus(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(UpdateSearchVisibility, DispatcherPriority.Background);

    private void UpdateSearchVisibility()
    {
        var hasSearch = !string.IsNullOrWhiteSpace(SearchBox.Text);
        if (SearchToggleBtn.IsKeyboardFocusWithin)
            return;

        if (!SearchBox.IsKeyboardFocusWithin && !hasSearch)
            SearchBox.IsVisible = false;
        SearchToggleBtn.Opacity = SearchBox.IsVisible || hasSearch ? 1.0 : 0.86;
    }

    private void LoadCollections()
    {
        _collections = MusicLibraryService.Current.GetCollections();
        _activeCollection = string.IsNullOrWhiteSpace(_restoredPlayerSession.ActiveCollectionStableId)
            ? null
            : _collections.FirstOrDefault(collection => string.Equals(
                collection.StableId,
                _restoredPlayerSession.ActiveCollectionStableId,
                StringComparison.OrdinalIgnoreCase));
        RefreshActiveCollectionOrder();
        RebuildCollectionRows();
    }

    private void ReloadCollections(bool refreshTracks = false)
    {
        var activeStableId = _activeCollection?.StableId;
        _collections = MusicLibraryService.Current.GetCollections();
        _activeCollection = activeStableId is null
            ? null
            : _collections.FirstOrDefault(collection => string.Equals(
                collection.StableId, activeStableId, StringComparison.OrdinalIgnoreCase));
        RefreshActiveCollectionOrder();
        _allTrackCollectionNames = MusicLibraryService.Current.GetAllTrackCollectionNames();
        foreach (var item in _allItems)
            ApplyCollectionDisplay(item);
        RebuildCollectionRows();
        if (refreshTracks)
            ApplyFilter();
        else
            UpdatePlaylistSummary();
        EditTrackOverlay.RefreshCollections();
    }

    private void RebuildCollectionRows()
    {
        foreach (var bitmap in _collectionCardBitmaps)
            bitmap.Dispose();
        _collectionCardBitmaps.Clear();
        CollectionRows.Children.Clear();

        if (_isCreatingCollection)
            CollectionRows.Children.Add(CreateNewCollectionRow());

        foreach (var collection in _collections)
            CollectionRows.Children.Add(CreateCollectionCard(collection));

        if (!_isCreatingCollection && _collections.Count == 0)
            CollectionRows.Children.Add(new TextBlock
            {
                Text = "No collections created yet.",
                FontSize = 10.5,
                Opacity = 0.48,
                Margin = new Thickness(2, 3, 0, 1)
            });
        AddCollectionButton.IsEnabled = !_isCreatingCollection;
        FilterSortCollectionOption.IsVisible = _activeCollection is not null;
    }

    private Control CreateCollectionCard(TrackCollection collection)
    {
        var isSelected = _activeCollection?.Id == collection.Id;
        var coverBorder = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(7),
            ClipToBounds = true,
            Background = Brush("#30322E"),
            BorderBrush = Brush("#38FFFFFF"),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center
        };
        if (BitmapFromCollection(collection.Id) is { } bitmap)
        {
            _collectionCardBitmaps.Add(bitmap);
            coverBorder.Child = new Image { Source = bitmap, Stretch = Stretch.UniformToFill };
        }

        var labels = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock
        {
            Text = collection.Name,
            FontSize = 11.5,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = isSelected ? Brush("#D6F2EC") : ThemeResources.Brush("Theme.Brush.TextPrimary")
        });
        labels.Children.Add(new TextBlock
        {
            Text = $"{collection.TrackCount} tracks · {FormatPlaylistDuration(collection.DurationSeconds)}",
            FontSize = 9.5,
            Opacity = 0.5
        });

        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("38,*"), ColumnSpacing = 9 };
        content.Children.Add(coverBorder);
        Grid.SetColumn(labels, 1);
        labels.Margin = new Thickness(0, 0, 32, 0);
        content.Children.Add(labels);

        var card = new Button
        {
            Content = content,
            MinHeight = 56,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = isSelected ? Brush("#2C2F6E63") : ThemeResources.Brush("Theme.Brush.Surface"),
            BorderBrush = isSelected ? Brush("#9A52CBB4") : ThemeResources.Brush("Theme.Brush.BorderSubtle"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(8),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        card.Click += (_, _) => SelectCollection(collection.Id);

        var editButton = new Button
        {
            Content = CreateFilterSvgIcon("/Assets/pencil-simple.svg", 13),
            Width = 26,
            Height = 26,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 8, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Flyout = CreateCollectionEditorFlyout(collection)
        };
        ToolTip.SetTip(editButton, "Edit collection");
        var container = new Grid();
        container.Children.Add(card);
        container.Children.Add(editButton);
        return container;
    }

    private Control CreateNewCollectionRow()
    {
        var nameBox = new TextBox
        {
            Watermark = "Collection name",
            Height = 34,
            MaxLength = 80,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        nameBox.Classes.Add("theme-input");
        nameBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            CommitNewCollection(nameBox.Text);
            e.Handled = true;
        };
        var save = new Button { Content = "Create", Padding = new Thickness(10, 6), FontSize = 11 };
        save.Click += (_, _) => CommitNewCollection(nameBox.Text);
        var cancel = new Button
        {
            Content = "Cancel", Padding = new Thickness(10, 6), FontSize = 11,
            Background = Brushes.Transparent
        };
        cancel.Click += (_, _) =>
        {
            _isCreatingCollection = false;
            RebuildCollectionRows();
        };
        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
        actions.Children.Add(save);
        Grid.SetColumn(cancel, 1);
        actions.Children.Add(cancel);
        var panel = new StackPanel { Spacing = 8, Children = { nameBox, actions } };
        Dispatcher.UIThread.Post(() => nameBox.Focus());
        return new Border
        {
            BorderBrush = ThemeResources.Brush("Theme.Brush.BorderSubtle"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10),
            Child = panel
        };
    }

    private Flyout CreateCollectionEditorFlyout(TrackCollection collection)
    {
        var nameBox = new TextBox
        {
            Text = collection.Name,
            MaxLength = 80,
            Height = 32,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        nameBox.Classes.Add("theme-input");
        var saveName = new Button
        {
            Content = "Save",
            Height = 32,
            MinWidth = 58,
            Padding = new Thickness(12, 4),
            FontSize = 10.5
        };
        var nameRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        nameRow.Children.Add(nameBox);
        Grid.SetColumn(saveName, 1);
        nameRow.Children.Add(saveName);

        var automaticCover = new Button
        {
            Content = "Automatic cover",
            Height = 30,
            Padding = new Thickness(10, 4),
            FontSize = 10.5
        };
        automaticCover.Classes.Add("auto-toggle");
        if (collection.CoverKind == CollectionCoverKind.Automatic)
            automaticCover.Classes.Add("active");
        var chooseImage = new Button
        {
            Content = "Choose image…",
            Height = 30,
            Padding = new Thickness(10, 4),
            FontSize = 10.5,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        chooseImage.Classes.Add("auto-toggle");
        if (collection.CoverKind == CollectionCoverKind.Custom)
            chooseImage.Classes.Add("active");
        var coverActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            Children = { automaticCover, chooseImage }
        };

        var delete = new Button
        {
            Content = "Delete collection",
            Height = 28,
            Padding = new Thickness(9, 3),
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("#FFFF5364"),
            Background = Brushes.Transparent,
            BorderBrush = Brush("#66FF465B"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top
        };

        var coverPreview = new Border
        {
            Width = 46,
            Height = 46,
            Background = Brush("#30322E"),
            BorderBrush = ThemeResources.Brush("Theme.Brush.BorderSubtle"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            ClipToBounds = true
        };
        if (BitmapFromCollection(collection.Id) is { } previewBitmap)
        {
            _collectionCardBitmaps.Add(previewBitmap);
            coverPreview.Child = new Image { Source = previewBitmap, Stretch = Stretch.UniformToFill };
        }
        var heading = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = collection.Name, FontSize = 13, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "Collection", FontSize = 9.5, Opacity = 0.48 }
            }
        };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("46,*,Auto"), ColumnSpacing = 10 };
        header.Children.Add(coverPreview);
        Grid.SetColumn(heading, 1);
        header.Children.Add(heading);
        Grid.SetColumn(delete, 2);
        header.Children.Add(delete);

        var flyout = new Flyout
        {
            Placement = PlacementMode.RightEdgeAlignedTop,
            Content = new Border
            {
                Width = 320,
                Padding = new Thickness(3),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Child = new StackPanel
                {
                    Spacing = 11,
                    Children =
                    {
                        header,
                        nameRow,
                        new TextBlock { Text = "COVER", FontSize = 9, FontWeight = FontWeight.SemiBold, Opacity = 0.45 },
                        coverActions
                    }
                }
            }
        };
        saveName.Click += (_, _) =>
        {
            try
            {
                MusicLibraryService.Current.RenameCollection(collection.Id, nameBox.Text ?? string.Empty);
                flyout.Hide();
                ReloadCollections(refreshTracks: true);
            }
            catch (Exception exception) { ShowToast($"Could not rename collection: {exception.Message}"); }
        };
        automaticCover.Click += (_, _) =>
        {
            MusicLibraryService.Current.SetCollectionCoverAutomatic(collection.Id);
            flyout.Hide();
            ReloadCollections();
        };
        chooseImage.Click += async (_, _) =>
        {
            if (await ChooseCollectionCoverAsync(collection.Id))
            {
                flyout.Hide();
                ReloadCollections();
            }
        };
        delete.Flyout = CreateDeleteCollectionFlyout(collection, flyout);
        return flyout;
    }

    private Flyout CreateDeleteCollectionFlyout(TrackCollection collection, Flyout editorFlyout)
    {
        var confirm = new Button
        {
            Content = "Delete",
            Background = ThemeResources.Brush("Theme.Brush.DangerSurface"),
            Foreground = ThemeResources.Brush("Theme.Brush.DangerText"),
            Padding = new Thickness(10, 5)
        };
        var flyout = new Flyout
        {
            Placement = PlacementMode.RightEdgeAlignedTop,
            Content = new Border
            {
                Width = 220,
                Padding = new Thickness(12),
                Background = Brush("#FC191A1D"),
                BorderBrush = Brush("#3EFFFFFF"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = new StackPanel
                {
                    Spacing = 9,
                    Children =
                    {
                        new TextBlock { Text = $"Delete “{collection.Name}”?", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = "The tracks remain in your library.", FontSize = 10.5, Opacity = 0.58 },
                        confirm
                    }
                }
            }
        };
        confirm.Click += (_, _) =>
        {
            flyout.Hide();
            editorFlyout.Hide();
            MusicLibraryService.Current.DeleteCollection(collection.Id);
            if (_activeCollection?.Id == collection.Id)
            {
                _activeCollection = null;
                _activeCollectionOrder.Clear();
                if (_sortBy == LibrarySortBy.CollectionOrder)
                    _sortBy = LibrarySortBy.Name;
            }
            ReloadCollections(refreshTracks: true);
            InitializeSortControls();
            PersistPlayerSession();
        };
        return flyout;
    }

    private async Task<bool> ChooseCollectionCoverAsync(int collectionId)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return false;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose collection cover",
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.ImageAll]
        });
        if (files.Count == 0)
            return false;
        try
        {
            await using var input = await files[0].OpenReadAsync();
            if (input.CanSeek && input.Length > 12 * 1024 * 1024)
                throw new InvalidDataException("Collection cover must be smaller than 12 MB.");
            using var buffer = new MemoryStream();
            await input.CopyToAsync(buffer);
            var cover = ThumbnailService.CreateSquareArtwork(buffer.ToArray(), 256, 88)
                        ?? throw new InvalidDataException("The selected image could not be decoded.");
            MusicLibraryService.Current.SetCollectionCustomCover(collectionId, cover);
            return true;
        }
        catch (Exception exception)
        {
            ShowToast($"Could not use collection cover: {exception.Message}");
            return false;
        }
    }

    private Bitmap? BitmapFromCollection(int collectionId)
    {
        var bytes = MusicLibraryService.Current.GetCollectionCover(collectionId);
        if (bytes is not { Length: > 0 })
            return null;
        try
        {
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch { return null; }
    }

    private void UpdateCollectionContextCover()
    {
        _collectionContextBitmap?.Dispose();
        _collectionContextBitmap = null;
        CollectionContextCover.Source = null;
        CollectionContextCoverBorder.IsVisible = _activeCollection is not null;
        if (_activeCollection is null)
            return;
        _collectionContextBitmap = BitmapFromCollection(_activeCollection.Id);
        CollectionContextCover.Source = _collectionContextBitmap;
    }

    private void DisposeCollectionBitmaps()
    {
        foreach (var bitmap in _collectionCardBitmaps)
            bitmap.Dispose();
        _collectionCardBitmaps.Clear();
        _collectionContextBitmap?.Dispose();
        _collectionContextBitmap = null;
    }

    private void OnAddCollectionClicked(object? sender, RoutedEventArgs e)
    {
        _isCreatingCollection = true;
        RebuildCollectionRows();
    }

    private void CommitNewCollection(string? rawName)
    {
        try
        {
            var collection = MusicLibraryService.Current.CreateCollection(rawName ?? string.Empty);
            _isCreatingCollection = false;
            _collections = MusicLibraryService.Current.GetCollections();
            SelectCollection(collection.Id);
        }
        catch (Exception exception) { ShowToast($"Could not create collection: {exception.Message}"); }
    }

    private void SelectCollection(int collectionId)
    {
        if (_activeCollection?.Id == collectionId)
        {
            _activeCollection = null;
            _activeCollectionOrder.Clear();
            if (_sortBy == LibrarySortBy.CollectionOrder)
                _sortBy = LibrarySortBy.Name;
        }
        else
        {
            _activeCollection = _collections.FirstOrDefault(collection => collection.Id == collectionId);
            _sortBy = LibrarySortBy.CollectionOrder;
            _sortDirection = LibrarySortDirection.Ascending;
            RefreshActiveCollectionOrder();
        }

        foreach (var item in _allItems)
            ApplyCollectionDisplay(item);
        InitializeSortControls();
        RebuildCollectionRows();
        ApplyFilter();
        ResetPlaybackQueueFromCurrentView();
        PersistPlayerSession();
    }

    private void RefreshCollectionsAfterMembershipChange()
    {
        ReloadCollections(refreshTracks: true);
        EditTrackOverlay.InvalidatePreparedTrack();
    }

    private void LoadFilterPresets()
    {
        _filterPresets = FilterPresetStore.Load();
        var restoredPreset = _filterPresets.FirstOrDefault(preset =>
            string.Equals(
                preset.Name,
                _restoredPlayerSession.ActiveFilterPresetName,
                StringComparison.OrdinalIgnoreCase));

        _activeFilterPresetName = restoredPreset?.Name;
        _activeBuiltInView = restoredPreset is null ? LibraryMode.Library : null;
        if (restoredPreset is not null)
            ApplyFilterPreset(restoredPreset);
        else
            RebuildPresetRows();
    }

    private void RebuildPresetRows()
    {
        BuiltInPresetRows.Children.Clear();
        BuiltInPresetRows.Children.Add(CreateBuiltInPresetCard(
            LibraryMode.Library,
            "Default",
            "All rated tracks without review flags."));
        BuiltInPresetRows.Children.Add(CreateBuiltInPresetCard(
            LibraryMode.Review,
            "Needs review",
            "All tracks marked for review."));
        BuiltInPresetRows.Children.Add(CreateBuiltInPresetCard(
            LibraryMode.Declined,
            "Declined",
            "Declined channel downloads available for cleanup."));

        PresetRows.Children.Clear();

        if (_isCreatingPreset)
            PresetRows.Children.Add(CreateNewPresetRow());

        foreach (var preset in _filterPresets
                     .OrderBy(preset => string.Equals(preset.Name, "Default", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                     .ThenBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase))
            PresetRows.Children.Add(CreatePresetCard(preset));

        if (!_isCreatingPreset && _filterPresets.Count == 0)
            PresetRows.Children.Add(new TextBlock
            {
                Text = "No custom presets created yet.",
                FontSize = 10.5,
                Opacity = 0.48,
                Margin = new Thickness(2, 3, 0, 1),
                TextWrapping = TextWrapping.Wrap
            });

        AddPresetButton.IsEnabled = !_isCreatingPreset;
    }

    private Control CreateBuiltInPresetCard(LibraryMode mode, string title, string description)
    {
        var isSelected = _activeBuiltInView == mode;
        var labels = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 42, 0) };
        labels.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            FontSize = 12,
            Foreground = isSelected
                ? ThemeResources.Brush("Theme.Brush.TextStrong")
                : ThemeResources.Brush("Theme.Brush.TextPrimary")
        });
        labels.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 10,
            Opacity = 0.56,
            TextWrapping = TextWrapping.Wrap
        });

        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        content.Children.Add(labels);
        var fixedLabel = new TextBlock
        {
            Text = "FIXED",
            FontSize = 8.5,
            FontWeight = FontWeight.SemiBold,
            Opacity = 0.38,
            LetterSpacing = 0.6,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(fixedLabel, 1);
        content.Children.Add(fixedLabel);

        var accent = mode switch
        {
            LibraryMode.Review => "#A8C69A55",
            LibraryMode.Declined => "#A8A34D57",
            _ => "#A85F7894"
        };
        var button = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = isSelected ? Brush("#2BFFFFFF") : Brushes.Transparent,
            BorderBrush = isSelected ? Brush(accent) : ThemeResources.Brush("Theme.Brush.BorderSubtle"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10, 8),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        button.Click += (_, _) => SelectBuiltInView(mode);
        return button;
    }

    private void SelectBuiltInView(LibraryMode mode)
    {
        _suppressPresetAutoSave = true;
        try
        {
            _activeFilterPresetName = null;
            _activeBuiltInView = mode;
            _isCreatingPreset = false;
            SearchBox.Text = string.Empty;
            _updatingLibraryMode = true;
            ShowNeedsReviewCheckBox.IsChecked = mode == LibraryMode.Review;
            ShowDeclinedCheckBox.IsChecked = mode == LibraryMode.Declined;
            _updatingLibraryMode = false;
            _filterGroups.Clear();
            RebuildFilterConditionsPanel();
            ClearConditionBuilder();
            RefreshCompletionFilterVisuals();
        }
        finally
        {
            _updatingLibraryMode = false;
            _suppressPresetAutoSave = false;
        }

        RebuildPresetRows();
        ApplyFilter();
        PersistPlayerSession();
    }

    private Control CreatePresetCard(PortableFilterPreset preset)
    {
        var isSelected = string.Equals(preset.Name, _activeFilterPresetName, StringComparison.OrdinalIgnoreCase);
        var title = new TextBlock
        {
            Text = preset.Name,
            FontWeight = FontWeight.SemiBold,
            FontSize = 12,
            Foreground = isSelected
                ? Brush("#D7E7FF")
                : ThemeResources.Brush("Theme.Brush.TextPrimary"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 30, 0)
        };

        var card = new Button
        {
            Content = title,
            MinHeight = 50,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = isSelected
                ? Brush("#303E6591")
                : ThemeResources.Brush("Theme.Brush.Surface"),
            BorderBrush = isSelected
                ? Brush("#A078A9E6")
                : ThemeResources.Brush("Theme.Brush.BorderSubtle"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10, 8),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        card.Click += (_, _) => SelectFilterPreset(preset.Name);

        var deleteButton = new Button
        {
            Content = CreateFilterSvgIcon("/Assets/trash.svg", 14),
            Width = 26,
            Height = 26,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 5, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        deleteButton.Classes.Add("preset-delete");
        ToolTip.SetTip(deleteButton, $"Delete {preset.Name}");

        var cancelDeleteButton = new Button
        {
            Content = "Cancel",
            Padding = new Thickness(10, 5),
            Background = Brushes.Transparent,
            BorderBrush = ThemeResources.Brush("Theme.Brush.BorderStrong"),
            BorderThickness = new Thickness(1),
            FontSize = 10.5
        };
        var confirmDeleteButton = new Button
        {
            Content = "Delete",
            Padding = new Thickness(10, 5),
            Background = ThemeResources.Brush("Theme.Brush.DangerSurface"),
            BorderBrush = ThemeResources.Brush("Theme.Brush.DangerBorder"),
            BorderThickness = new Thickness(1),
            Foreground = ThemeResources.Brush("Theme.Brush.DangerText"),
            FontSize = 10.5
        };
        var deleteActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 7,
            Children = { cancelDeleteButton, confirmDeleteButton }
        };
        var deleteFlyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedRight,
            Content = new Border
            {
                Width = 230,
                Padding = new Thickness(13, 11),
                Background = Brush("#FA1A1B1E"),
                BorderBrush = Brush("#3EFFFFFF"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = new StackPanel
                {
                    Spacing = 9,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"Delete “{preset.Name}”?",
                            FontSize = 12,
                            FontWeight = FontWeight.SemiBold,
                            TextWrapping = TextWrapping.Wrap
                        },
                        new TextBlock
                        {
                            Text = "This preset will be permanently removed.",
                            FontSize = 10.5,
                            Opacity = 0.58,
                            TextWrapping = TextWrapping.Wrap
                        },
                        deleteActions
                    }
                }
            }
        };
        deleteButton.Flyout = deleteFlyout;
        cancelDeleteButton.Click += (_, _) => deleteFlyout.Hide();
        confirmDeleteButton.Click += (_, _) =>
        {
            deleteFlyout.Hide();
            DeleteFilterPreset(preset.Name);
        };

        var container = new Grid();
        container.Classes.Add("preset-card-container");
        container.Children.Add(card);
        container.Children.Add(deleteButton);
        return container;
    }

    private Control CreateNewPresetRow()
    {
        var nameBox = new TextBox
        {
            Watermark = "Preset name",
            Height = 34,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        nameBox.Classes.Add("theme-input");
        nameBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;

            CommitNewPreset(nameBox.Text);
            e.Handled = true;
        };

        var saveButton = new Button
        {
            Content = "Save",
            Background = ThemeResources.Brush("Theme.Brush.AccentSurface"),
            BorderBrush = ThemeResources.Brush("Theme.Brush.Accent"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 6),
            FontSize = 11
        };
        saveButton.Click += (_, _) => CommitNewPreset(nameBox.Text);

        var cancelButton = new Button
        {
            Content = "Cancel",
            Background = Brushes.Transparent,
            BorderBrush = ThemeResources.Brush("Theme.Brush.BorderStrong"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 6),
            FontSize = 11,
            Opacity = 0.75
        };
        cancelButton.Click += (_, _) =>
        {
            _isCreatingPreset = false;
            RebuildPresetRows();
        };

        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
        actions.Children.Add(saveButton);
        actions.Children.Add(cancelButton);
        Grid.SetColumn(cancelButton, 1);

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(nameBox);
        panel.Children.Add(actions);

        var border = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = ThemeResources.Brush("Theme.Brush.BorderSubtle"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10)
        };
        border.Child = panel;

        Dispatcher.UIThread.Post(() => nameBox.Focus());
        return border;
    }

    private void OnAddPresetClicked(object? sender, RoutedEventArgs e)
    {
        _isCreatingPreset = true;
        RebuildPresetRows();
    }

    private void CommitNewPreset(string? rawName)
    {
        var name = rawName?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return;

        name = UniquePresetName(name);
        var preset = CreatePreset(name);
        _filterPresets.Add(preset);

        FilterPresetStore.Save(_filterPresets);
        _filterPresets = FilterPresetStore.Load();
        _activeFilterPresetName = preset.Name;
        _activeBuiltInView = null;
        _isCreatingPreset = false;
        RebuildPresetRows();
        PersistPlayerSession();
    }

    private void SelectFilterPreset(string presetName)
    {
        var preset = _filterPresets.FirstOrDefault(p =>
            string.Equals(p.Name, presetName, StringComparison.OrdinalIgnoreCase));

        if (preset is null)
            return;

        _activeFilterPresetName = preset.Name;
        _activeBuiltInView = null;
        _isCreatingPreset = false;
        RebuildPresetRows();
        ApplyFilterPreset(preset);
        PersistPlayerSession();
    }

    private void SaveActivePresetFromCurrentFilters()
    {
        if (_suppressPresetAutoSave || _activeFilterPresetName is null)
            return;

        var preset = CreatePreset(_activeFilterPresetName);
        var index = _filterPresets.FindIndex(existing =>
            string.Equals(existing.Name, preset.Name, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
            return;

        _filterPresets[index] = preset;
        FilterPresetStore.Save(_filterPresets);
        _filterPresets = FilterPresetStore.Load();
        _activeFilterPresetName = preset.Name;
        RebuildPresetRows();
        PersistPlayerSession();
    }

    private void ApplyFilterDefinitionChange()
    {
        if (!_suppressPresetAutoSave && _activeBuiltInView is not null)
        {
            _activeBuiltInView = null;
            RebuildPresetRows();
        }
        ApplyFilter();
        if (_suppressPresetAutoSave)
            return;
        SaveActivePresetFromCurrentFilters();
        PersistPlayerSession();
    }

    private void DeleteFilterPreset(string presetName)
    {
        var wasSelected = string.Equals(
            presetName,
            _activeFilterPresetName,
            StringComparison.OrdinalIgnoreCase);

        _filterPresets.RemoveAll(preset =>
            string.Equals(preset.Name, presetName, StringComparison.OrdinalIgnoreCase));

        FilterPresetStore.Save(_filterPresets);
        _filterPresets = FilterPresetStore.Load();

        if (!wasSelected)
        {
            RebuildPresetRows();
            PersistPlayerSession();
            return;
        }

        SelectBuiltInView(LibraryMode.Library);
    }

    private PortableFilterPreset CreatePreset(string name)
    {
        var groups = _filterGroups
            .Select(group => new PortableFilterGroup(
                SortedNames(group.GenreCtrl.SelectedItems),
                SortedNames(group.StyleCtrl.SelectedItems),
                SortedNames(group.TagCtrl.SelectedItems),
                group.Negate,
                group.EmotionalCharacters
                    .Where(pair => pair.Value.IsActive)
                    .Select(pair => new PortableEmotionalCharacterFilter(pair.Key, pair.Value.MinimumPercent, null))
                    .ToList(),
                SortedNames(group.LanguageCtrl.SelectedItems),
                SortedNames(group.MainGenres),
                SortedNames(group.VersionCtrl.SelectedItems)))
            .Where(group => group.Genres.Count > 0 || group.Styles.Count > 0 || (group.MainGenres?.Count ?? 0) > 0 || (group.Tags?.Count ?? 0) > 0 || (group.EmotionalCharacters?.Count ?? 0) > 0 || (group.Languages?.Count ?? 0) > 0 || (group.Versions?.Count ?? 0) > 0)
            .ToList();

        return new PortableFilterPreset(
            name,
            groups,
            ShowNeedsReviewCheckBox.IsChecked == true,
            false);
    }

    private void ApplyFilterPreset(PortableFilterPreset preset)
    {
        _suppressPresetAutoSave = true;
        try
        {
            ShowDeclinedCheckBox.IsChecked = false;
            ShowNeedsReviewCheckBox.IsChecked = preset.ShowNeedsReview;

            _filterGroups.Clear();

            var groups = preset.Groups
                .Where(group => group.Genres.Count > 0 || group.Styles.Count > 0 || (group.MainGenres?.Count ?? 0) > 0 || (group.Tags?.Count ?? 0) > 0 || (group.EmotionalCharacters?.Count ?? 0) > 0 || (group.Languages?.Count ?? 0) > 0 || (group.Versions?.Count ?? 0) > 0)
                .ToList();

            foreach (var group in groups)
                _filterGroups.Add(CreateFilterCondition(group.Genres, group.Styles, group.Tags ?? new List<string>(), group.Negate, group.EmotionalCharacters, group.Languages, group.MainGenres, group.Versions));

            RebuildFilterConditionsPanel();
            ClearConditionBuilder();
        }
        finally
        {
            _suppressPresetAutoSave = false;
        }

        RebuildPresetRows();
        ApplyFilter();
    }

    private string UniquePresetName(string name)
    {
        var reserved = string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(name, "Needs review", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(name, "Declined", StringComparison.OrdinalIgnoreCase);
        if (!reserved && _filterPresets.All(preset => !string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase)))
            return name;

        var suffix = 2;
        string candidate;
        do
        {
            candidate = $"{name} {suffix++}";
        }
        while (_filterPresets.Any(preset => string.Equals(preset.Name, candidate, StringComparison.OrdinalIgnoreCase)));

        return candidate;
    }

    private static Avalonia.Svg.Skia.Svg CreateFilterSvgIcon(string path, double size) => new(new Uri("avares://Resona/"))
    {
        Path = path,
        Width = size,
        Height = size,
        Stretch = Stretch.Uniform,
        Opacity = 0.82,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center
    };

    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));

    private static List<string> SortedNames(IEnumerable<string> names) =>
        names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

    // ─── Filter groups ────────────────────────────────────────────────────────

    private void OnAddFilterGroupClicked(object? sender, RoutedEventArgs e)
    {
        AddConditionFromBuilder();
        ApplyFilterDefinitionChange();
    }

    private void InitializeFilterConditionBuilder()
    {
        _conditionVersionCtrl = CreateVersionFilterControl();
        _conditionVersionCtrl.SelectionChanged += (_, _) => UpdateCurrentSetSummary();
        _conditionGenreCtrl = new MultiSelectFilterControl { Placeholder = "All genres" };
        _conditionGenreCtrl.SetItems(GenreFilterOptions());
        _conditionGenreCtrl.SelectionChanged += (_, _) => UpdateCurrentSetSummary();

        _conditionStyleCtrl = new MultiSelectFilterControl { Placeholder = "All styles" };
        _conditionStyleCtrl.SetItems(StyleFilterOptions());
        _conditionStyleCtrl.SelectionChanged += (_, _) => UpdateCurrentSetSummary();

        _conditionTagCtrl = new MultiSelectFilterControl { Placeholder = "All tags" };
        _conditionTagCtrl.SetItems(TagFilterOptions());
        _conditionTagCtrl.SelectionChanged += (_, _) => UpdateCurrentSetSummary();

        _conditionLanguageCtrl = new MultiSelectFilterControl { Placeholder = "All languages" };
        _conditionLanguageCtrl.SetItems(LanguageFilterOptions());
        _conditionLanguageCtrl.SelectionChanged += (_, _) => UpdateCurrentSetSummary();

        _conditionGenreSection = CreateGenreFilterSection(_conditionGenreCtrl);
        _conditionStyleSection = CreateStyleFilterSection(_conditionStyleCtrl);
        _conditionTagSection = CreateTagFilterSection(_conditionTagCtrl);
        _conditionLanguageSection = CreateLanguageFilterSection(_conditionLanguageCtrl);
        _conditionEmotionalSection = CreateEmotionalCharacterFilterSection(_conditionEmotionalCharacters);

        FilterBuilderPanel.Children.Clear();
        FilterBuilderPanel.Children.Add(new TextBlock { Text = "Versions · match any selected", FontSize = 12, FontWeight = FontWeight.SemiBold });
        FilterBuilderPanel.Children.Add(_conditionVersionCtrl);
        FilterBuilderPanel.Children.Add(_conditionGenreSection.Control);
        FilterBuilderPanel.Children.Add(_conditionStyleSection.Control);
        FilterBuilderPanel.Children.Add(_conditionTagSection.Control);
        FilterBuilderPanel.Children.Add(_conditionEmotionalSection.Control);
        FilterBuilderPanel.Children.Add(_conditionLanguageSection.Control);
        SetConditionMode(exclude: false);
        UpdateCurrentSetSummary();
    }

    private void AddConditionFromBuilder()
    {
        if (_conditionGenreCtrl is null || _conditionStyleCtrl is null || _conditionTagCtrl is null || _conditionLanguageCtrl is null)
            return;

        var selectedGenres = SortedNames(_conditionGenreCtrl.SelectedItems);
        var selectedStyles = SortedNames(_conditionStyleCtrl.SelectedItems);
        var selectedTags = SortedNames(_conditionTagCtrl.SelectedItems);
        var selectedLanguages = SortedNames(_conditionLanguageCtrl.SelectedItems);
        var selectedVersions = SortedNames(_conditionVersionCtrl?.SelectedItems ?? new HashSet<string>());
        if (selectedVersions.Count == 0 && selectedGenres.Count == 0 && selectedStyles.Count == 0 && selectedTags.Count == 0 && selectedLanguages.Count == 0 && !_conditionEmotionalCharacters.Values.Any(value => value.IsActive))
            return;

        _filterGroups.Add(CreateFilterCondition(selectedGenres, selectedStyles, selectedTags, _conditionNegate, _conditionEmotionalCharacters
            .Where(pair => pair.Value.IsActive)
            .Select(pair => new PortableEmotionalCharacterFilter(pair.Key, pair.Value.MinimumPercent, null)),
            selectedLanguages, versions: selectedVersions));
        RebuildFilterConditionsPanel();
        ClearConditionBuilder();
    }

    private FilterGroupControls CreateFilterCondition(
        IEnumerable<string> genres,
        IEnumerable<string> styles,
        IEnumerable<string> tags,
        bool negate = false,
        IEnumerable<PortableEmotionalCharacterFilter>? emotionalCharacters = null,
        IEnumerable<string>? languages = null,
        IEnumerable<string>? mainGenres = null,
        IEnumerable<string>? versions = null)
    {
        var genreCtrl = new MultiSelectFilterControl { Placeholder = "All genres" };
        genreCtrl.SetItems(GenreFilterOptions());
        genreCtrl.SetSelectedItems(genres, notify: false);

        var styleCtrl = new MultiSelectFilterControl { Placeholder = "All styles" };
        styleCtrl.SetItems(StyleFilterOptions());
        styleCtrl.SetSelectedItems(styles, notify: false);

        var tagCtrl = new MultiSelectFilterControl { Placeholder = "All tags" };
        tagCtrl.SetItems(TagFilterOptions());
        tagCtrl.SetSelectedItems(tags, notify: false);

        var languageCtrl = new MultiSelectFilterControl { Placeholder = "All languages" };
        languageCtrl.SetItems(LanguageFilterOptions());
        languageCtrl.SetSelectedItems(languages ?? [], notify: false);
        var versionCtrl = CreateVersionFilterControl();
        versionCtrl.SetSelectedItems(versions ?? [], notify: false);

        var emotional = EmotionalCharacterCatalog.All.ToDictionary(
            item => item.Adjectives,
            _ => new EmotionalRangeState(),
            StringComparer.OrdinalIgnoreCase);
        foreach (var range in emotionalCharacters ?? [])
            if (emotional.TryGetValue(range.SignalKey, out var state))
                state.MinimumPercent = range.MinimumPercent;

        return new FilterGroupControls(
            genreCtrl,
            styleCtrl,
            tagCtrl,
            languageCtrl,
            versionCtrl,
            emotional,
            (mainGenres ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase),
            negate,
            () => { });
    }

    private void AddMainGenreCondition(string mainGenre)
    {
        if (string.IsNullOrWhiteSpace(mainGenre))
            return;

        if (_filterGroups.Any(group =>
                !group.Negate
                && group.MainGenres.Count == 1
                && group.MainGenres.Contains(mainGenre)
                && group.GenreCtrl.SelectedItems.Count == 0
                && group.StyleCtrl.SelectedItems.Count == 0
                && group.VersionCtrl.SelectedItems.Count == 0
                && group.TagCtrl.SelectedItems.Count == 0
                && group.LanguageCtrl.SelectedItems.Count == 0
                && !group.EmotionalCharacters.Values.Any(value => value.IsActive)))
            return;

        _filterGroups.Add(CreateFilterCondition([], [], [], mainGenres: [mainGenre]));
        RebuildFilterConditionsPanel();
        ApplyFilterDefinitionChange();
    }

    private void ClearConditionBuilder()
    {
        _conditionVersionCtrl?.SetSelectedItems(Array.Empty<string>(), notify: false);
        _conditionGenreCtrl?.SetSelectedItems(Array.Empty<string>(), notify: false);
        _conditionStyleCtrl?.SetSelectedItems(Array.Empty<string>(), notify: false);
        _conditionTagCtrl?.SetSelectedItems(Array.Empty<string>(), notify: false);
        _conditionLanguageCtrl?.SetSelectedItems(Array.Empty<string>(), notify: false);
        foreach (var range in _conditionEmotionalCharacters.Values)
            range.MinimumPercent = null;
        SetConditionMode(exclude: false);
        RefreshConditionBuilder();
    }

    private void OnIncludeConditionPressed(object? sender, PointerPressedEventArgs e)
    {
        SetConditionMode(exclude: false);
        e.Handled = true;
    }

    private void OnExcludeConditionPressed(object? sender, PointerPressedEventArgs e)
    {
        SetConditionMode(exclude: true);
        e.Handled = true;
    }

    private void SetConditionMode(bool exclude)
    {
        _conditionNegate = exclude;
        if (ConditionModeIndicator.RenderTransform is TranslateTransform transform)
            transform.X = exclude ? 52 : 0;
        ConditionModeIndicator.Background = exclude
            ? ThemeResources.Brush("Theme.Brush.DangerSurface")
            : ThemeResources.Brush("Theme.Brush.Success");
        ConditionModeIndicator.CornerRadius = new CornerRadius(1);
        IncludeConditionText.Foreground = ThemeResources.Brush(exclude
            ? "Theme.Brush.TextMuted"
            : "Theme.Brush.TextStrong");
        ExcludeConditionText.Foreground = ThemeResources.Brush(exclude
            ? "Theme.Brush.TextStrong"
            : "Theme.Brush.TextMuted");
    }

    private void RefreshConditionBuilder()
    {
        _conditionGenreSection?.Refresh();
        _conditionStyleSection?.Refresh();
        _conditionTagSection?.Refresh();
        _conditionLanguageSection?.Refresh();
        _conditionEmotionalSection?.Refresh();
        UpdateCurrentSetSummary();
    }

    private void UpdateCurrentSetSummary()
    {
        var genres = _conditionGenreCtrl?.SelectedItems
            .Select(DisplayGenreFilterName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
        var styles = _conditionStyleCtrl?.SelectedItems
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
        var tags = _conditionTagCtrl?.SelectedItems
            .Select(DisplayTagFilterName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
        var languages = _conditionLanguageCtrl?.SelectedItems
            .Select(DisplayLanguageFilterName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
        var emotional = _conditionEmotionalCharacters
            .Where(pair => pair.Value.IsActive)
            .Select(pair => EmotionalCharacterCatalog.Name(pair.Key))
            .ToList();

        CurrentSetGenresRow.IsVisible = genres.Count > 0;
        var versions = _conditionVersionCtrl?.SelectedItems.ToList() ?? [];
        CurrentSetVersionsRow.IsVisible = versions.Count > 0;
        CurrentSetVersionsText.Text = FormatNaturalList(versions);
        CurrentSetStylesRow.IsVisible = styles.Count > 0;
        CurrentSetTagsRow.IsVisible = tags.Count > 0;
        CurrentSetLanguagesRow.IsVisible = languages.Count > 0;
        CurrentSetEmotionalRow.IsVisible = emotional.Count > 0;
        CurrentSetGenresText.Text = FormatNaturalList(genres);
        CurrentSetStylesText.Text = FormatNaturalList(styles);
        CurrentSetTagsText.Text = FormatNaturalList(tags);
        CurrentSetLanguagesText.Text = FormatNaturalList(languages);
        CurrentSetEmotionalText.Text = FormatNaturalList(emotional);

        var hasSelection = versions.Count > 0 || genres.Count > 0 || styles.Count > 0 || tags.Count > 0 || languages.Count > 0 || emotional.Count > 0;
        CurrentSetEmptyText.IsVisible = !hasSelection;
        AddFilterGroupButton.IsEnabled = hasSelection;
    }

    private void RebuildFilterConditionsPanel()
    {
        FilterGroupsPanel.Children.Clear();

        if (_filterGroups.Count == 0)
        {
            FilterGroupsPanel.Children.Add(new TextBlock
            {
                Text = "No conditions yet. Select genres, styles, tags, languages or emotional ranges above, then add a condition.",
                FontSize = 11,
                Opacity = 0.52,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            });
            return;
        }

        var included = _filterGroups.Where(condition => !condition.Negate).ToList();
        var excluded = _filterGroups.Where(condition => condition.Negate).ToList();

        if (included.Count > 0)
            FilterGroupsPanel.Children.Add(CreateConditionSection("Include", included, exclude: false));
        if (excluded.Count > 0)
            FilterGroupsPanel.Children.Add(CreateConditionSection("Exclude", excluded, exclude: true));
    }

    private Control CreateConditionSection(
        string title,
        IReadOnlyList<FilterGroupControls> conditions,
        bool exclude)
    {
        var rows = new StackPanel { Spacing = 6 };
        foreach (var condition in conditions)
            rows.Children.Add(CreateConditionSetRow(condition, exclude));

        var accent = exclude
            ? ThemeResources.Brush("Theme.Brush.DangerText")
            : Brush("#78A9E6");

        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontSize = 12.5,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = accent,
                    Margin = new Thickness(2, 0, 0, 0)
                },
                rows
            }
        };
    }

    private Control CreateConditionSetRow(FilterGroupControls condition, bool exclude)
    {
        var names = condition.GenreCtrl.SelectedItems
            .Select(DisplayGenreFilterName)
            .Concat(condition.MainGenres)
            .Concat(condition.StyleCtrl.SelectedItems)
            .Concat(condition.VersionCtrl.SelectedItems)
            .Concat(condition.TagCtrl.SelectedItems.Select(DisplayTagFilterName))
            .Concat(condition.LanguageCtrl.SelectedItems.Select(DisplayLanguageFilterName))
            .Concat(condition.EmotionalCharacters
                .Where(pair => pair.Value.IsActive)
                .Select(pair => FormatEmotionalRange(pair.Key, pair.Value)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var values = new TextBlock
        {
            Text = FormatNaturalList(names),
            FontSize = 10.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeResources.Brush("Theme.Brush.TextSecondary"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };

        var removeBtn = new Button
        {
            Content = "×",
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            FontSize = 15,
            Opacity = 0.62,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(removeBtn, "Remove this condition set");
        removeBtn.Click += (_, _) => RemoveFilterGroup(condition);

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 7
        };
        row.Children.Add(new Border
        {
            Width = 5,
            Height = 5,
            CornerRadius = new CornerRadius(3),
            Background = exclude
                ? ThemeResources.Brush("Theme.Brush.DangerText")
                : Brush("#78A9E6"),
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(values, 1);
        row.Children.Add(values);
        Grid.SetColumn(removeBtn, 2);
        row.Children.Add(removeBtn);

        return new Border
        {
            Background = Brush("#12FFFFFF"),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(9, 5, 4, 5),
            Child = row
        };
    }

    private static string FormatNaturalList(IReadOnlyList<string> values)
    {
        return values.Count switch
        {
            0 => string.Empty,
            1 => values[0],
            2 => $"{values[0]} and {values[1]}",
            _ => $"{string.Join(", ", values.Take(values.Count - 1))} and {values[^1]}"
        };
    }

    private static string DisplayGenreFilterName(string genreName)
    {
        var parts = genreName.Split('→', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2 ? parts[1] : genreName;
    }

    private static string DisplayTagFilterName(string tagName)
        => tagName;

    private FilterSection CreateEmotionalCharacterFilterSection(Dictionary<string, EmotionalRangeState> ranges)
    {
        var rows = new StackPanel { Spacing = 10 };

        void NormalizeRanges()
        {
            var remainingMinimum = 100d;
            foreach (var definition in EmotionalCharacterCatalog.All)
            {
                var state = ranges[definition.Adjectives];
                var snapped = Math.Round(
                    Math.Clamp(state.MinimumPercent ?? 0d, 0d, 100d) / 10d,
                    MidpointRounding.AwayFromZero) * 10d;
                var minimum = Math.Min(snapped, remainingMinimum);
                state.MinimumPercent = minimum <= 0.001 ? null : minimum;
                remainingMinimum -= minimum;
            }
        }

        void Rebuild()
        {
            NormalizeRanges();
            rows.Children.Clear();
            foreach (var definition in EmotionalCharacterCatalog.All)
            {
                var state = ranges[definition.Adjectives];
                var minimumText = new TextBlock
                {
                    FontSize = 9,
                    Opacity = 0.58,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var slider = new RangeSliderControl();
                slider.SetValue(state.MinimumPercent ?? 0d);

                void UpdateLabel(double minimum)
                {
                    minimumText.Text = $"Min {minimum:0}%";
                }

                slider.ValueChanged += minimum =>
                {
                    var otherMinimums = ranges
                        .Where(pair => !string.Equals(pair.Key, definition.Adjectives, StringComparison.OrdinalIgnoreCase))
                        .Sum(pair => pair.Value.MinimumPercent ?? 0d);
                    var allowedMinimum = Math.Min(minimum, Math.Max(0d, 100d - otherMinimums));
                    if (Math.Abs(allowedMinimum - minimum) > 0.001)
                        slider.SetValue(allowedMinimum);

                    state.MinimumPercent = allowedMinimum <= 0.001 ? null : allowedMinimum;
                    UpdateLabel(allowedMinimum);
                    UpdateCurrentSetSummary();
                };
                UpdateLabel(slider.Value);

                var header = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                    ColumnSpacing = 9
                };
                var name = new TextBlock
                {
                    Text = definition.Name,
                    FontSize = 10.5,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Brush(definition.AccentColor),
                    VerticalAlignment = VerticalAlignment.Center
                };
                ToolTip.SetTip(name, definition.Adjectives);
                header.Children.Add(name);
                var adjectives = new TextBlock
                {
                    Text = definition.Adjectives,
                    FontSize = 8.8,
                    Opacity = 0.46,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };
                ToolTip.SetTip(adjectives, definition.Adjectives);
                Grid.SetColumn(adjectives, 1);
                header.Children.Add(adjectives);
                Grid.SetColumn(minimumText, 2);
                header.Children.Add(minimumText);

                rows.Children.Add(new StackPanel
                {
                    Spacing = 2,
                    Children = { header, slider }
                });
            }
        }

        Rebuild();
        var border = new Border
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Child = new StackPanel
            {
                Spacing = 9,
                Children =
                {
                    new TextBlock { Text = "Emotional character", FontSize = 12, FontWeight = FontWeight.SemiBold },
                    rows
                }
            }
        };
        return new FilterSection(border, Rebuild);
    }

    private static string FormatEmotionalRange(string signalKey, EmotionalRangeState state)
    {
        var name = EmotionalCharacterCatalog.Name(signalKey);
        return state.MinimumPercent is double minimum
            ? $"{name} ≥ {minimum:0}%"
            : name;
    }

    private FilterSection CreateGenreFilterSection(MultiSelectFilterControl genreCtrl)
    {
        string? selectedGroupName = null;
        var searchText = string.Empty;
        var choicesPanel = new UniformGrid
        {
            Columns = 2,
            ColumnSpacing = 7,
            RowSpacing = 7,
            Margin = new Thickness(0, 0, 0, 10)
        };
        var groupListPanel = new StackPanel { Spacing = 0 };
        var searchBox = new TextBox
        {
            Watermark = "Search",
            Width = 180,
            Height = 24,
            MinHeight = 24,
            MaxHeight = 24,
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0)
        };
        searchBox.Classes.Add("compact-search");

        var searchButton = new Button
        {
            Width = 28,
            Height = 28,
            Padding = new Thickness(6),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Opacity = 0.66,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = new PathIcon
            {
                Data = Geometry.Parse("M229.66,218.34l-50.07-50.06a88.11,88.11,0,1,0-11.31,11.31l50.06,50.07a8,8,0,0,0,11.32-11.32ZM40,112a72,72,0,1,1,72,72A72.08,72.08,0,0,1,40,112Z"),
                Width = 15,
                Height = 15,
                Foreground = ThemeResources.Brush("Theme.Brush.TextSecondary")
            }
        };
        ToolTip.SetTip(searchButton, "Search subgenres");

        var subgenreScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(10, 6),
            Content = choicesPanel
        };

        string GroupName(Genre genre)
        {
            var parts = genre.Name.Split('→', 2, StringSplitOptions.TrimEntries);
            return parts.Length == 2 ? parts[0] : string.Empty;
        }

        string SubgenreName(Genre genre)
        {
            var parts = genre.Name.Split('→', 2, StringSplitOptions.TrimEntries);
            return parts.Length == 2 ? parts[1] : genre.Name;
        }

        void FillChoices()
        {
            choicesPanel.Children.Clear();
            var selected = genreCtrl.SelectedItems;
            var normalizedSearch = searchText.Trim();
            var choices = Values.Genres
                .Where(genre => selectedGroupName is null || string.Equals(GroupName(genre), selectedGroupName, StringComparison.OrdinalIgnoreCase))
                .Where(genre => string.IsNullOrWhiteSpace(normalizedSearch)
                                || SubgenreName(genre).Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase)
                                || GroupName(genre).Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(genre => selected.Contains(genre.Name))
                .ThenBy(genre => GroupName(genre), StringComparer.OrdinalIgnoreCase)
                .ThenBy(genre => SubgenreName(genre), StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var genre in choices)
            {
                var isSelected = selected.Contains(genre.Name);
                var button = CreateGenreFilterChoiceButton(SubgenreName(genre), isSelected);
                button.Click += (_, _) =>
                {
                    var next = genreCtrl.SelectedItems.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (!next.Add(genre.Name))
                        next.Remove(genre.Name);
                    genreCtrl.SetSelectedItems(next);
                    FillChoices();
                };
                choicesPanel.Children.Add(button);
            }

            if (choices.Count == 0)
            {
                choicesPanel.Children.Add(new TextBlock
                {
                    Text = "No genres match this filter.",
                    FontSize = 11,
                    Opacity = 0.52,
                    Margin = new Thickness(0, 8, 0, 4)
                });
            }
        }

        void FillGroups()
        {
            groupListPanel.Children.Clear();
            var groups = new[] { new GenreGroupChoice(null, "All") }
                .Concat(Values.Genres
                    .Select(GroupName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .Select(name => new GenreGroupChoice(name, name)));

            foreach (var group in groups)
            {
                var selected = string.Equals(
                    group.GroupName,
                    selectedGroupName,
                    StringComparison.OrdinalIgnoreCase);
                var text = new TextBlock
                {
                    Text = group.Label,
                    FontSize = 11.5,
                    FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal,
                    Foreground = group.GroupName is null
                        ? selected
                            ? Brush("#78A9E6")
                            : ThemeResources.Brush("Theme.Brush.TextSecondary")
                        : MainGenrePalette.For(group.GroupName),
                    Opacity = selected ? 1 : 0.72,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var itemContent = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions(group.GroupName is null ? "*" : "16,*")
                };
                if (group.GroupName is not null)
                {
                    var addButton = new Button
                    {
                        Content = new TextBlock
                        {
                            Text = "+",
                            FontSize = 14,
                            Margin = new Thickness(0, -2, 0, 0),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                            TextAlignment = TextAlignment.Center
                        },
                        Width = 16,
                        Height = 25,
                        Padding = new Thickness(0),
                        FontWeight = FontWeight.Normal,
                        Background = Brushes.Transparent,
                        BorderBrush = Brushes.Transparent,
                        BorderThickness = new Thickness(0),
                        Foreground = ThemeResources.Brush("Theme.Brush.TextSecondary"),
                        HorizontalContentAlignment = HorizontalAlignment.Center,
                        VerticalContentAlignment = VerticalAlignment.Center
                    };
                    ToolTip.SetTip(addButton, $"Filter by any {group.GroupName} subgenre");
                    addButton.Click += (_, e) =>
                    {
                        AddMainGenreCondition(group.GroupName);
                        e.Handled = true;
                    };
                    itemContent.Children.Add(addButton);
                    Grid.SetColumn(text, 1);
                }
                itemContent.Children.Add(text);

                var item = new Border
                {
                    Height = 25,
                    Background = Brushes.Transparent,
                    Padding = new Thickness(group.GroupName is null ? 18 : 2, 0, 2, 0),
                    Cursor = new Cursor(StandardCursorType.Hand),
                    Child = itemContent
                };
                item.PointerPressed += (_, e) =>
                {
                    selectedGroupName = group.GroupName;
                    FillGroups();
                    FillChoices();
                    e.Handled = true;
                };
                groupListPanel.Children.Add(item);
            }

            subgenreScroll.MaxHeight = Math.Max(25, groupListPanel.Children.Count * 25);
        }

        searchButton.Click += (_, _) =>
        {
            searchBox.IsVisible = !searchBox.IsVisible;
            searchButton.Opacity = searchBox.IsVisible ? 1 : 0.66;
            if (searchBox.IsVisible)
                Dispatcher.UIThread.Post(() => searchBox.Focus());
            else
                searchBox.Text = string.Empty;
        };
        searchBox.TextChanged += (_, _) =>
        {
            searchText = searchBox.Text ?? string.Empty;
            FillChoices();
        };

        FillGroups();
        FillChoices();

        var titleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        titleRow.Children.Add(new TextBlock
        {
            Text = "Genres",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(searchBox, 1);
        titleRow.Children.Add(searchBox);
        Grid.SetColumn(searchButton, 2);
        titleRow.Children.Add(searchButton);

        var browser = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("118,13,*")
        };
        browser.Children.Add(groupListPanel);
        var separator = new Border
        {
            Width = 1,
            Background = ThemeResources.Brush("Theme.Brush.Divider"),
            Margin = new Thickness(6, 1)
        };
        Grid.SetColumn(separator, 1);
        browser.Children.Add(separator);
        Grid.SetColumn(subgenreScroll, 2);
        browser.Children.Add(subgenreScroll);

        var border = new Border
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    titleRow,
                    browser
                }
            }
        };
        return new FilterSection(border, () =>
        {
            FillGroups();
            FillChoices();
        });
    }

    private static MultiSelectFilterControl CreateVersionFilterControl()
    {
        var control = new MultiSelectFilterControl { Placeholder = "All versions" };
        control.SetItems(new[] { "Original", "Edit" }.Concat(TrackVersions.Types.Select(type => type.Name)));
        return control;
    }

    private FilterSection CreateStyleFilterSection(MultiSelectFilterControl styleCtrl)
    {
        var panel = new StackPanel { Spacing = 9 };
        panel.Children.Add(new TextBlock
        {
            Text = "Styles",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });

        void Toggle(Style style)
        {
            var next = styleCtrl.SelectedItems.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!next.Add(style.Name))
                next.Remove(style.Name);
            styleCtrl.SetSelectedItems(next);
            RebuildStyles();
        }

        void RebuildStyles()
        {
            while (panel.Children.Count > 1)
                panel.Children.RemoveAt(1);

            var styles = Values.Styles
                .OrderBy(style => style.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var rows = new StackPanel { Spacing = 7 };
            var rowCount = Math.Max(1, (int)Math.Ceiling(styles.Count / 4d));
            var styleIndex = 0;

            for (var rowIndex = 0; rowIndex < rowCount && styleIndex < styles.Count; rowIndex++)
            {
                var remainingStyles = styles.Count - styleIndex;
                var remainingRows = rowCount - rowIndex;
                var stylesInRow = (int)Math.Ceiling(remainingStyles / (double)remainingRows);
                var row = new Grid { ColumnSpacing = 7 };

                for (var column = 0; column < stylesInRow; column++)
                {
                    var style = styles[styleIndex++];
                    row.ColumnDefinitions.Add(new ColumnDefinition
                    {
                        Width = new GridLength(Math.Max(6, style.Name.Length + 4), GridUnitType.Star)
                    });
                    var button = CreateTagFilterChoiceButton(
                        style.Name,
                        styleCtrl.SelectedItems.Contains(style.Name));
                    button.Click += (_, _) => Toggle(style);
                    Grid.SetColumn(button, column);
                    row.Children.Add(button);
                }

                rows.Children.Add(row);
            }

            panel.Children.Add(rows);
        }

        RebuildStyles();
        return new FilterSection(new Border
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Child = panel
        }, RebuildStyles);
    }

    private FilterSection CreateTagFilterSection(MultiSelectFilterControl tagCtrl)
    {
        var panel = new StackPanel { Spacing = 9 };

        var header = new Grid();
        header.Children.Add(new TextBlock
        {
            Text = "Tags",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(header);

        void Toggle(Tag tag)
        {
            var value = TagFilterName(tag);
            var next = tagCtrl.SelectedItems.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!next.Add(value))
                next.Remove(value);
            tagCtrl.SetSelectedItems(next);
            RebuildTags();
        }

        void RebuildTags()
        {
            while (panel.Children.Count > 1)
                panel.Children.RemoveAt(1);

            var tags = Values.Tags
                .OrderBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var rows = new StackPanel { Spacing = 7 };
            var rowCount = Math.Max(1, (int)Math.Ceiling(tags.Count / 4d));
            var tagIndex = 0;

            for (var rowIndex = 0; rowIndex < rowCount && tagIndex < tags.Count; rowIndex++)
            {
                var remainingTags = tags.Count - tagIndex;
                var remainingRows = rowCount - rowIndex;
                var tagsInRow = (int)Math.Ceiling(remainingTags / (double)remainingRows);
                var row = new Grid { ColumnSpacing = 7 };

                for (var column = 0; column < tagsInRow; column++)
                {
                    var tag = tags[tagIndex++];
                    row.ColumnDefinitions.Add(new ColumnDefinition
                    {
                        Width = new GridLength(Math.Max(6, tag.Name.Length + 4), GridUnitType.Star)
                    });
                    var selected = tagCtrl.SelectedItems.Contains(TagFilterName(tag));
                    var button = CreateTagFilterChoiceButton(tag.Name, selected);
                    button.Click += (_, _) => Toggle(tag);
                    Grid.SetColumn(button, column);
                    row.Children.Add(button);
                }

                rows.Children.Add(row);
            }

            panel.Children.Add(rows);
        }

        RebuildTags();

        var border = new Border
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Child = panel
        };
        return new FilterSection(border, RebuildTags);
    }

    private FilterSection CreateLanguageFilterSection(MultiSelectFilterControl languageCtrl)
    {
        var expanded = false;
        var contentPanel = new StackPanel { Spacing = 7, IsVisible = false };
        var chevron = new Avalonia.Controls.Shapes.Path
        {
            Width = 5,
            Height = 9,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Data = Geometry.Parse("M 0 0 L 4 4.5 L 0 9"),
            Stroke = ThemeResources.Brush("Theme.Brush.TextSecondary"),
            StrokeThickness = 1,
            RenderTransformOrigin = RelativePoint.Center,
            RenderTransform = new RotateTransform(0)
        };
        var chevronContainer = new Border
        {
            Width = 14,
            Height = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = chevron
        };
        var selectedSummary = new TextBlock
        {
            FontSize = 9.5,
            Opacity = 0.5,
            VerticalAlignment = VerticalAlignment.Center
        };
        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 6
        };
        headerGrid.Children.Add(chevronContainer);
        var title = new TextBlock
        {
            Text = "Languages",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(title, 1);
        headerGrid.Children.Add(title);
        Grid.SetColumn(selectedSummary, 2);
        headerGrid.Children.Add(selectedSummary);
        var header = new Border
        {
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Padding = new Thickness(0, 3),
            Child = headerGrid
        };
        var panel = new StackPanel { Spacing = 7, Children = { header, contentPanel } };

        void Toggle(TrackLanguage language)
        {
            var next = languageCtrl.SelectedItems.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!next.Add(language.Code))
                next.Remove(language.Code);
            languageCtrl.SetSelectedItems(next);
            RebuildLanguages();
        }

        void RebuildLanguages()
        {
            contentPanel.Children.Clear();
            var selectedCount = languageCtrl.SelectedItems.Count;
            selectedSummary.Text = selectedCount == 0 ? string.Empty : $"{selectedCount} selected";

            var languages = TrackLanguageCatalog.All
                .OrderBy(language => language.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var rows = new StackPanel { Spacing = 7 };
            var rowCount = Math.Max(1, (int)Math.Ceiling(languages.Count / 4d));
            var languageIndex = 0;

            for (var rowIndex = 0; rowIndex < rowCount && languageIndex < languages.Count; rowIndex++)
            {
                var remainingLanguages = languages.Count - languageIndex;
                var remainingRows = rowCount - rowIndex;
                var languagesInRow = (int)Math.Ceiling(remainingLanguages / (double)remainingRows);
                var row = new Grid { ColumnSpacing = 7 };

                for (var column = 0; column < languagesInRow; column++)
                {
                    var language = languages[languageIndex++];
                    row.ColumnDefinitions.Add(new ColumnDefinition
                    {
                        Width = new GridLength(Math.Max(6, language.Name.Length + 4), GridUnitType.Star)
                    });
                    var button = CreateTagFilterChoiceButton(
                        language.Name,
                        languageCtrl.SelectedItems.Contains(language.Code));
                    button.Click += (_, _) => Toggle(language);
                    Grid.SetColumn(button, column);
                    row.Children.Add(button);
                }

                rows.Children.Add(row);
            }

            contentPanel.Children.Add(rows);
        }

        header.PointerPressed += (_, e) =>
        {
            expanded = !expanded;
            contentPanel.IsVisible = expanded;
            ((RotateTransform)chevron.RenderTransform!).Angle = expanded ? 90 : 0;
            e.Handled = true;
        };

        RebuildLanguages();
        var border = new Border
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Child = panel
        };
        return new FilterSection(border, RebuildLanguages);
    }

    private static Button CreateGenreFilterChoiceButton(string title, bool isSelected)
    {
        var text = new TextBlock
        {
            Text = title,
            FontSize = 10.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = isSelected
                ? ThemeResources.Brush("Theme.Brush.TextSecondary")
                : ThemeResources.Brush("Theme.Brush.TextPrimary"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };

        return new Button
        {
            Content = text,
            Height = 32,
            Padding = new Thickness(9, 4),
            CornerRadius = new CornerRadius(5),
            Background = isSelected ? Brush("#263E6591") : Brushes.Transparent,
            BorderBrush = isSelected ? Brush("#7A78A9E6") : Brush("#36FFFFFF"),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
    }

    private static Button CreateTagFilterChoiceButton(string title, bool isSelected)
    {
        var content = new Grid();
        content.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 10.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeResources.Brush(isSelected
                ? "Theme.Brush.TextStrong"
                : "Theme.Brush.TextPrimary"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center
        });
        if (isSelected)
        {
            var check = new TextBlock
            {
                Text = "✓",
                FontSize = 11,
                Foreground = ThemeResources.Brush("Theme.Brush.Accent"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            content.Children.Add(check);
        }

        return new Button
        {
            Content = content,
            Height = 34,
            Padding = new Thickness(9, 3),
            CornerRadius = new CornerRadius(5),
            Background = isSelected ? Brush("#263E6591") : Brushes.Transparent,
            BorderBrush = isSelected ? Brush("#7A78A9E6") : Brush("#36FFFFFF"),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center
        };
    }

    private sealed record GenreGroupChoice(string? GroupName, string Label)
    {
        public override string ToString() => Label;
    }

    private void RemoveFilterGroup(FilterGroupControls fg)
    {
        var idx = _filterGroups.IndexOf(fg);
        if (idx < 0) return;
        _filterGroups.RemoveAt(idx);
        RebuildFilterConditionsPanel();
        ApplyFilterDefinitionChange();
    }

    // ─── Dialogs ──────────────────────────────────────────────────────────────

    private void OnImportClicked(object? sender, RoutedEventArgs e)
    {
        ImportOverlay.Open();
    }

    private void OnRefreshLibraryClicked(object? sender, RoutedEventArgs e)
    {
        RefreshTrackList();
        if (ImportOverlay.IsVisible)
            ImportOverlay.RefreshQueue();
    }

    private void OnSettingsClicked(object? sender, RoutedEventArgs e)
    {
        UpdateSettingsLayout();
        SettingsOverlay.Open();
    }

    private void UpdateSettingsLayout()
    {
        var playerClearance = new Thickness(0, 0, 0, PlayerBar.Bounds.Height);
        SettingsOverlay.Margin = new Thickness(0);
        FilterDrawer.Margin = playerClearance;
    }

    private void MoveFilterDrawerToRootOverlay()
    {
        if (FilterDrawer.Parent is not Panel currentParent)
            return;

        currentParent.Children.Remove(FilterDrawer);
        RootSurface.Children.Add(FilterDrawer);
    }

    private void ApplyAppearanceSettings(AppearanceSettings settings, bool refreshTrackRows)
    {
        var updatedSettings = settings.Clone().Clamp();
        var trackAppearanceChanged = TrackAppearanceChanged(_appearanceSettings, updatedSettings);
        _appearanceSettings = updatedSettings;

        if (trackAppearanceChanged)
            foreach (var item in _allItems)
                item.ApplyAppearance(_appearanceSettings);

        if (refreshTrackRows && trackAppearanceChanged && _allItems.Count > 0)
            RefreshVisibleItemsSource((FileList.SelectedItem as TrackDisplayItem)?.Track.Id);
    }

    private static bool TrackAppearanceChanged(AppearanceSettings current, AppearanceSettings updated) =>
        current.TrackArtworkStrength != updated.TrackArtworkStrength
        || current.TrackArtworkBlur != updated.TrackArtworkBlur
        || current.TrackColorWashStrength != updated.TrackColorWashStrength
        || current.TrackColorWashReach != updated.TrackColorWashReach
        || current.CoverHaloStrength != updated.CoverHaloStrength
        || current.CoverHaloBlur != updated.CoverHaloBlur;

    private async void ShowToast(string message)
    {
        _toastCts?.Cancel();
        var cts = new CancellationTokenSource();
        _toastCts = cts;

        ToastText.Text = message;
        Toast.IsVisible = true;

        try
        {
            await Task.Delay(3500, cts.Token);
            if (!cts.IsCancellationRequested)
                Toast.IsVisible = false;
        }
        catch (OperationCanceledException) { }
    }

    private void OpenTrackEditor(MusicTrack track)
    {
        UpdateEditorBounds();
        EditTrackOverlay.Open(track);
    }

    private void PrepareActiveTrackEditor()
    {
        if (_engine.ActiveTrackId < 0)
            return;

        var track = _allItems.FirstOrDefault(item => item.Track.Id == _engine.ActiveTrackId)?.Track;
        if (track is not null)
            PrepareTrackEditor(track);
    }

    private void PrepareTrackEditor(MusicTrack track, bool force = false)
    {
        if (EditTrackOverlay.IsOpen || (!force && EditTrackOverlay.IsPreparedFor(track.Id)))
            return;

        EditTrackOverlay.InvalidatePreparedTrack();
        var currentTrack = _allItems.FirstOrDefault(item => item.Track.Id == track.Id)?.Track ?? track;
        EditTrackOverlay.Prepare(currentTrack);
    }

    private async Task<bool> DeleteTrackFromEditorAsync(MusicTrack track)
    {
        if (_isDeletingTrack || !await ConfirmTrackDeletionAsync(track))
            return false;

        _isDeletingTrack = true;
        var deleted = false;
        try
        {
            var deletingActivePlayback = !_isTrackPreviewActive && _engine.ActiveTrackId == track.Id;
            if (deletingActivePlayback && !_playbackQueue.IsInitialized)
                _playbackQueue.Reset(
                    _filteredItems.Select(item => item.Track.Id),
                    track.Id);

            if (_engine.ActiveTrackId == track.Id || _previewTrackId == track.Id)
            {
                FinishListeningSession(markSkipped: false);
                _previewPlaybackSnapshot = null;
                _isTrackPreviewActive = false;
                _previewTrackId = -1;
                _engine.Stop();
                NowPlayingText.Text = string.Empty;
                PlaybackInfoPanel.IsVisible = false;
                _nextTrackIndex = -1;
            }

            // Let Stop()/pointer routing finish before the backing file, database
            // record and bound row are removed.
            await Task.Yield();
            var error = await MusicLibraryService.Current.DeleteTrackAsync(track);
            if (error is not null)
            {
                ShowToast(error);
                return false;
            }

            deleted = true;
            MusicTrack? nextTrack = null;
            if (deletingActivePlayback)
            {
                var nextTrackId = _playbackQueue.RemoveCurrentAndAdvance(
                    loopPlaylist: _loopStatus == "Playlist");
                nextTrack = nextTrackId is int nextId
                    ? _allItems.FirstOrDefault(item => item.Track.Id == nextId)?.Track
                    : null;
            }

            try
            {
                RemoveTrackFromCurrentLists(track.Id);
            }
            catch (Exception presentationException)
            {
                // The track is already deleted. Recover the presentation from the
                // database instead of letting a stale hover/selection crash the app.
                try
                {
                    RefreshTrackList();
                }
                catch
                {
                    // The next regular refresh will repair the view.
                }
                ShowToast($"Track deleted; view refresh recovered from: {presentationException.Message}");
                return true;
            }

            if (nextTrack is not null)
                PlayTrack(nextTrack, isCrossfade: false);

            PersistPlayerSession();
            ShowToast("Track deleted");
            return true;
        }
        catch (Exception exception)
        {
            ShowToast(deleted
                ? $"Track deleted, but cleanup failed: {exception.Message}"
                : $"Could not delete track: {exception.Message}");
            return deleted;
        }
        finally
        {
            _isDeletingTrack = false;
        }
    }

    private Task<bool> ConfirmTrackDeletionAsync(MusicTrack track)
    {
        if (_deleteTrackConfirmationCompletion is not null)
            return Task.FromResult(false);

        _deleteTrackConfirmationCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        DeleteTrackConfirmationText.Text =
            $"\u201c{track.DisplayTitle}\u201d will be removed from the library and its local file will be permanently deleted.";
        DeleteTrackConfirmation.IsVisible = true;
        DeleteTrackConfirmation.Focus();
        CancelTrackDeleteButton.Focus();
        return _deleteTrackConfirmationCompletion.Task;
    }

    private void OnCancelTrackDeleteClicked(object? sender, RoutedEventArgs e)
    {
        CompleteTrackDeleteConfirmation(false);
        e.Handled = true;
    }

    private void OnConfirmTrackDeleteClicked(object? sender, RoutedEventArgs e)
    {
        CompleteTrackDeleteConfirmation(true);
        e.Handled = true;
    }

    private void OnDeleteTrackConfirmationKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        CompleteTrackDeleteConfirmation(false);
        e.Handled = true;
    }

    private void CompleteTrackDeleteConfirmation(bool confirmed)
    {
        var completion = _deleteTrackConfirmationCompletion;
        if (completion is null)
            return;

        _deleteTrackConfirmationCompletion = null;
        DeleteTrackConfirmation.IsVisible = false;
        completion.TrySetResult(confirmed);
    }

    private void RemoveTrackFromCurrentLists(int trackId)
    {
        var selectedId = (FileList.SelectedItem as TrackDisplayItem)?.Track.Id;
        var deletedIndex = _filteredItems.FindIndex(item => item.Track.Id == trackId);

        _allItems.RemoveAll(item => item.Track.Id == trackId);
        // SQLite detaches children when a parent is deleted; reflect that in cached tracks.
        foreach (var child in _allItems.Where(item => item.Track.ParentTrackId == trackId).ToList())
        {
            var detached = child with { Track = child.Track with { ParentTrackId = null } };
            ReplaceTrackDisplayItem(_allItems, child, detached);
            ReplaceTrackDisplayItem(_filteredItems, child, detached);
        }
        _filteredItems.RemoveAll(item => item.Track.Id == trackId);
        _visibleItems.RemoveAll(item => item.Track.Id == trackId);
        _allTrackStyleIds.Remove(trackId);
        _allTrackGenreIds.Remove(trackId);
        _allTrackTagIds.Remove(trackId);
        _allTrackAudioAnalyses.Remove(trackId);
        _allTrackMirexScores.Remove(trackId);
        _allTrackUsageStats.Remove(trackId);
        _playbackQueue.Retain(
            _allItems.Select(item => item.Track.Id),
            _engine.ActiveTrackId >= 0 ? _engine.ActiveTrackId : null);

        if (selectedId == trackId)
            FileList.SelectedIndex = -1;

        RefreshVisibleItemsSource(selectedId == trackId ? null : selectedId);

        if (_filteredItems.Count == 0)
        {
            SetFilteredSelectedIndex(-1);
        }
        else if (selectedId == trackId)
        {
            SetFilteredSelectedIndex(Math.Clamp(deletedIndex, 0, _filteredItems.Count - 1));
        }

        UpdatePlaylistSummary();
        RefreshNextTrackPreview();
        UpdateFilterCounts();
        RestartVisibleThumbnailLoad();
    }

    // ─── Playback control ─────────────────────────────────────────────────────

    private void OnTrackCardTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: TrackDisplayItem item } card)
            return;

        if (IsInteractiveTrackCardSource(e.Source as Visual, card))
            return;

        FileList.SelectedItem = item;
    }

    private void OnTrackCardDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: TrackDisplayItem item } card
            || IsInteractiveTrackCardSource(e.Source as Visual, card))
            return;

        FileList.SelectedItem = item;
        StartPlayback(resetQueue: true);
        e.Handled = true;
    }

    private static bool IsInteractiveTrackCardSource(Visual? source, Visual card)
    {
        for (var visual = source;
             visual is not null && visual != card;
             visual = visual.GetVisualParent())
            if (visual is Button or Slider)
                return true;

        return false;
    }

    private void OnPreviousClicked(object? sender, RoutedEventArgs e) => NavigatePrevious();
    private void OnNextClicked(object? sender, RoutedEventArgs e) => NavigateNext(isManual: true);

    private void OnPlayPauseClicked(object? sender, RoutedEventArgs e) => TogglePlayPause();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (IsTextEntry(e.Source)) return;
        var shortcut = e.Key switch
        {
            Key.Space or Key.MediaPlayPause => MediaShortcut.PlayPause,
            Key.MediaPreviousTrack => MediaShortcut.Previous,
            Key.MediaNextTrack => MediaShortcut.Next,
            _ => (MediaShortcut?)null
        };
        if (shortcut is null) return;
        HandleMediaShortcut(shortcut.Value);
        e.Handled = true;
    }

    private void OnGlobalMediaKeyPressed(MediaShortcut shortcut) => HandleMediaShortcut(shortcut);

    private void HandleMediaShortcut(MediaShortcut shortcut)
    {
        switch (shortcut)
        {
            case MediaShortcut.Previous: NavigatePrevious(); break;
            case MediaShortcut.PlayPause: TogglePlayPause(); break;
            case MediaShortcut.Next: NavigateNext(isManual: true); break;
            case MediaShortcut.Stop:
                FinishListeningSession(markSkipped: false);
                _engine.Stop();
                break;
        }
    }

    private void OnSystemSeekRequested(TimeSpan offset)
    {
        if (_engine.State == EngineState.Stopped || _engine.TotalTime <= TimeSpan.Zero)
            return;
        var position = _engine.CurrentTime + offset;
        OnSystemPositionRequested(position);
    }

    private void OnSystemPositionRequested(TimeSpan position)
    {
        if (_engine.State == EngineState.Stopped || _engine.TotalTime <= TimeSpan.Zero)
            return;

        var seconds = Math.Clamp(position.TotalSeconds, 0, _engine.TotalTime.TotalSeconds);
        _engine.Seek(seconds / _engine.TotalTime.TotalSeconds);
        UpdatePlaybackPositionUi();
        _windowsMediaSession.NotifySeeked(_engine.CurrentTime);
    }

    private void OnSystemVolumeRequested(double volume)
    {
        VolumeSlider.Value = Math.Clamp(volume, 0, 1) * 100;
    }

    private void OnSystemLoopStatusRequested(string status)
    {
        if (status is not ("None" or "Track" or "Playlist")) return;
        _loopStatus = status;
        _windowsMediaSession.UpdateLoopStatus(status);
        RefreshNextTrackPreview();
    }

    private void OnSystemOpenUriRequested(Uri uri)
    {
        if (!uri.IsFile) return;

        string requestedPath;
        try { requestedPath = Path.GetFullPath(uri.LocalPath); }
        catch { return; }

        var index = _filteredItems.FindIndex(item =>
        {
            try
            {
                var trackPath = Path.GetFullPath(Path.Combine(Values.TracksDirectory, item.Track.FileName));
                return string.Equals(trackPath, requestedPath, StringComparison.Ordinal);
            }
            catch { return false; }
        });
        if (index >= 0) PlayTrackAt(index, isCrossfade: false, resetQueue: true);
    }

    private static bool IsTextEntry(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
            if (visual is TextBox or ComboBox)
                return true;
        return false;
    }

    private void TogglePlayPause()
    {
        if (_engine.State == EngineState.Playing)
        {
            _engine.Pause();
            UpdateButtonStates();
        }
        else if (_engine.State == EngineState.Paused)
        {
            _engine.Resume();
            UpdateButtonStates();
        }
        else
        {
            if (_isTrackPreviewActive && _previewTrackId >= 0)
            {
                var previewTrack = _allItems.FirstOrDefault(item => item.Track.Id == _previewTrackId)?.Track;
                if (previewTrack is not null) StartTrackPreview(previewTrack);
            }
            else
                StartPlayback();
        }
    }

    private void OnShuffleToggleClicked(object? sender, RoutedEventArgs e) => SetShuffle(!_shuffle);

    private void SetShuffle(bool shuffle)
    {
        if (_shuffle == shuffle) return;
        _shuffle = shuffle;
        _shufflePriorities.Clear();
        ApplyFilter();
        SetFilteredSelectedIndex(_filteredItems.Count > 0 ? 0 : -1);
        if (_filteredItems.Count > 0)
            FileList.ScrollIntoView(_filteredItems[0]);
        ResetPlaybackQueueFromCurrentView(restartAfterCurrent: true);
        RefreshNextTrackPreview();
        UpdateUpcomingBar();
        UpdateShuffleButton();
        PersistPlayerSession();
    }

    private void ResetPlaybackQueueFromCurrentView(bool restartAfterCurrent = false)
    {
        _loadedPlaylistSourceTrackIds = _filteredItems.Select(item => item.Track.Id).ToList();
        if (_filteredItems.Count == 0)
        {
            _playbackQueue.Clear();
            return;
        }

        if (_engine.ActiveTrackId >= 0)
        {
            if (restartAfterCurrent)
                _playbackQueue.ResetUpcoming(
                    _engine.ActiveTrackId,
                    _filteredItems.Select(item => item.Track.Id));
            else
                _playbackQueue.Reset(
                    _filteredItems.Select(item => item.Track.Id),
                    _engine.ActiveTrackId);
            return;
        }

        var selectedTrackId = (FileList.SelectedItem as TrackDisplayItem)?.Track.Id;
        var current = selectedTrackId is int selected
                      && _filteredItems.Any(item => item.Track.Id == selected)
            ? selected
            : _filteredItems[0].Track.Id;
        _playbackQueue.Reset(_filteredItems.Select(item => item.Track.Id), current);
    }

    private void UpdateShuffleButton()
    {
        if (_shuffle)
        {
            if (!ShuffleBtn.Classes.Contains("active"))
                ShuffleBtn.Classes.Add("active");
        }
        else
        {
            ShuffleBtn.Classes.Remove("active");
        }
        _windowsMediaSession.UpdateShuffle(_shuffle);
    }

    private void StartPlayback(bool resetQueue = false)
    {
        var idx = GetSelectedFilteredIndex();
        if (idx < 0 || idx >= _filteredItems.Count) return;

        PlayTrackAt(
            idx,
            isCrossfade: false,
            resetQueue: resetQueue
                        || !_playbackQueue.IsInitialized
                        || !_playbackQueue.TrackIds.Contains(_filteredItems[idx].Track.Id));
    }

    private void PlayTrackAt(int filteredIndex, bool isCrossfade, bool resetQueue = false)
    {
        if (filteredIndex < 0 || filteredIndex >= _filteredItems.Count) return;

        PlayTrack(_filteredItems[filteredIndex].Track, isCrossfade, resetQueue);
    }

    private void PlayTrack(MusicTrack track, bool isCrossfade, bool resetQueue = false)
    {
        var filePath = Path.Combine(Values.TracksDirectory, track.FileName);

        if (_engine.ActiveTrackId >= 0 && _engine.ActiveTrackId != track.Id)
        {
            var totalSeconds = _engine.TotalTime.TotalSeconds;
            var playedFraction = totalSeconds > 0 ? _engine.CurrentTime.TotalSeconds / totalSeconds : 1;
            FinishListeningSession(markSkipped: !isCrossfade && playedFraction < .8);
        }

        bool wasPlaying = _engine.State != EngineState.Stopped;
        var songFadeDuration = (float)_appearanceSettings.SongFadeDuration;
        float fadeOut = isCrossfade ? songFadeDuration
                      : wasPlaying ? Values.ManualFadeDurationSeconds : 0f;
        float fadeIn = isCrossfade ? songFadeDuration
                     : wasPlaying ? Values.ManualFadeDurationSeconds : 0f;

        try
        {
            _isSeeking = false;
            _engine.Play(filePath, track.Id, fadeOut, fadeIn, LoudnessGainForTrack(track.Id));
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Playback failed: {ex.Message}";
            StatusText.IsVisible = true;
            return;
        }

        if (resetQueue)
            _playbackQueue.Reset(_filteredItems.Select(item => item.Track.Id), track.Id);
        else if (!_playbackQueue.SetCurrent(track.Id))
            _playbackQueue.Reset(_filteredItems.Select(item => item.Track.Id), track.Id);

        BeginListeningSession(track.Id);

        var filteredIndex = _filteredItems.FindIndex(item => item.Track.Id == track.Id);
        if (filteredIndex >= 0)
        {
            EnsureVisibleWindowAround(filteredIndex);
            SetFilteredSelectedIndex(filteredIndex);
        }

        NowPlayingText.Text = track.DisplayTitle;
        UpdateDiscordPresence();
        PlaybackInfoPanel.IsVisible = true;
        _nextTrackIndex = PeekNextTrackIndex(filteredIndex);
        UpdateUpcomingBar();
        UpdateButtonStates();
        RefreshPlayingMarkers();
        PersistPlayerSession();
        PrepareTrackEditor(track);
    }

    private static float LoudnessGainForTrack(int trackId)
    {
        var analysis = MusicLibraryService.Current.GetTrackAudioAnalysis(trackId);
        return PlaybackEngine.CalculateLoudnessGain(analysis?.IntegratedLoudness, analysis?.LoudnessRange);
    }

    // ─── Temporary track-information preview ─────────────────────────────────

    private void StartTrackPreview(MusicTrack track)
    {
        var filePath = Path.Combine(Values.TracksDirectory, track.FileName);
        if (!File.Exists(filePath))
        {
            ShowToast("The audio file for this track is not available.");
            return;
        }

        if (!_isTrackPreviewActive)
        {
            _previewPlaybackSnapshot = _engine.CaptureSnapshot();
            // A preview is neither a skip nor a separate full listen.
            FinishListeningSession(markSkipped: false);
            _isTrackPreviewActive = true;
        }

        try
        {
            _engine.Play(filePath, track.Id, 0, 0, LoudnessGainForTrack(track.Id));
        }
        catch (Exception exception)
        {
            ShowToast($"Preview could not start: {exception.Message}");
            return;
        }

        _previewTrackId = track.Id;
        _isSeeking = false;
        var index = _filteredItems.FindIndex(item => item.Track.Id == track.Id);
        if (index >= 0)
        {
            EnsureVisibleWindowAround(index);
            SetFilteredSelectedIndex(index);
        }
        NowPlayingText.Text = $"Preview · {track.DisplayTitle}";
        PlaybackInfoPanel.IsVisible = true;
        _nextTrackIndex = -1;
        _crossfadeTriggered = false;
        UpdateUpcomingBar();
        UpdateButtonStates();
        RefreshPlayingMarkers();
    }

    private void StopTrackPreview()
    {
        if (!_isTrackPreviewActive) return;

        var snapshot = _previewPlaybackSnapshot;
        _previewPlaybackSnapshot = null;
        _isTrackPreviewActive = false;
        _previewTrackId = -1;
        _isSeeking = false;

        try
        {
            _engine.RestoreSnapshot(snapshot);
        }
        catch (Exception exception)
        {
            _engine.Stop();
            ShowToast($"Previous playback could not be restored: {exception.Message}");
        }

        if (snapshot is null || _engine.ActiveTrackId < 0)
        {
            NowPlayingText.Text = string.Empty;
            PlaybackInfoPanel.IsVisible = false;
            _nextTrackIndex = -1;
        }
        else if (_allItems.FirstOrDefault(item => item.Track.Id == _engine.ActiveTrackId)?.Track is { } restoredTrack)
        {
            ResumeListeningSession(restoredTrack.Id, _engine.CurrentTime);
            var index = _filteredItems.FindIndex(item => item.Track.Id == restoredTrack.Id);
            if (index >= 0)
            {
                EnsureVisibleWindowAround(index);
                SetFilteredSelectedIndex(index);
            }
            NowPlayingText.Text = restoredTrack.DisplayTitle;
            PlaybackInfoPanel.IsVisible = true;
            _nextTrackIndex = PeekNextTrackIndex(index);
            _crossfadeTriggered = false;
        }

        UpdateUpcomingBar();
        UpdateButtonStates();
        RefreshPlayingMarkers();
    }

    // ─── Engine events ────────────────────────────────────────────────────────

    private void OnEngineStateChanged()
    {
        _windowsMediaSession.UpdateState(_engine.State);
        UpdateSystemMediaMetadata();
        UpdateButtonStates();
        UpdateDiscordPresence();
        if (_engine.State == EngineState.Stopped)
        {
            _nextTrackIndex = -1;
            _crossfadeTriggered = false;
            _lastKnownActiveId = -1;
            if (!_isTrackPreviewActive)
            _discordPresence.Clear();
            RefreshPlayingMarkers();
            UpdateUpcomingBar();
        }
    }

    private void OnTrackNaturallyEnded()
    {
        if (_isTrackPreviewActive)
        {
            _nextTrackIndex = -1;
            UpdateUpcomingBar();
            return;
        }
        FinishListeningSession(markSkipped: false);
        if (_loopStatus == "Track")
        {
            var currentIndex = GetCurrentPlayIndex();
            if (currentIndex >= 0)
            {
                PlayTrackAt(currentIndex, isCrossfade: false);
                return;
            }
        }
        NavigateNext(isManual: false);
    }

    private void OnProgressUpdated()
    {
        _windowsMediaSession.UpdatePosition(_engine.CurrentTime, _engine.TotalTime);
        RecordListeningProgress();
        if (_engine.ActiveTrackId != _lastKnownActiveId)
        {
            _lastKnownActiveId = _engine.ActiveTrackId;
            _crossfadeTriggered = false;
        }

        UpdatePlaybackPositionUi();

        if (!_isTrackPreviewActive && !_crossfadeTriggered && _nextTrackIndex >= 0 && _engine.State == EngineState.Playing)
        {
            var total = _engine.TotalTime.TotalSeconds;
            var current = _engine.CurrentTime.TotalSeconds;
            var songFadeDuration = _appearanceSettings.SongFadeDuration;
            if (songFadeDuration > 0 && total >= songFadeDuration + 2.0 && current >= 1.0)
            {
                var remaining = total - current;
                if (remaining > 0 && remaining <= songFadeDuration)
                {
                    _crossfadeTriggered = true;
                    PlayTrack(_allItems[_nextTrackIndex].Track, isCrossfade: true);
                    return;
                }
            }
        }

        UpdateUpcomingBar();
        UpdatePlaylistSummary();
    }

    private void UpdatePlaybackPositionUi()
    {
        if (!_isSeeking)
        {
            PlaybackSlider.Value = _engine.TotalTime.TotalSeconds > 0
                ? _engine.CurrentTime.TotalSeconds / _engine.TotalTime.TotalSeconds * 100
                : 0;
        }

        PlaybackTimeText.Text =
            $"{FormatDuration(_engine.CurrentTime)} / {FormatDuration(_engine.TotalTime)}";
    }

    // ─── Navigation ───────────────────────────────────────────────────────────

    private int GetCurrentPlayIndex() =>
        _engine.ActiveTrackId < 0
            ? -1
            : _filteredItems.FindIndex(i => i.Track.Id == _engine.ActiveTrackId);

    private int PeekNextTrackIndex(int currentFilteredIndex)
    {
        if (_allItems.Count == 0 || _loopStatus == "Track") return -1;
        if (!_playbackQueue.IsInitialized && currentFilteredIndex >= 0)
            _playbackQueue.Reset(
                _filteredItems.Select(item => item.Track.Id),
                _filteredItems[currentFilteredIndex].Track.Id);

        return _playbackQueue.PeekNext(_loopStatus == "Playlist") is int nextTrackId
            ? _allItems.FindIndex(item => item.Track.Id == nextTrackId)
            : -1;
    }

    private void RefreshNextTrackPreview()
    {
        if (_engine.ActiveTrackId < 0) { _nextTrackIndex = -1; UpdateUpcomingBar(); return; }
        var currentIdx = GetCurrentPlayIndex();
        if (!_playbackQueue.IsInitialized && currentIdx < 0)
        {
            _nextTrackIndex = -1;
            UpdateUpcomingBar();
            return;
        }
        _nextTrackIndex = PeekNextTrackIndex(currentIdx);
        UpdateUpcomingBar();
        UpdatePlaylistSummary();
    }

    private void UpdatePlaylistSummary()
    {
        var totalSeconds = _filteredItems
            .Select(item => item.Track.DurationSeconds ?? 0)
            .Sum();

        if (_activeCollection is null)
        {
            LibraryContextTitleText.Text = _activeFilterPresetName is null
                ? _activeBuiltInView switch
                {
                    LibraryMode.Review => "REVIEW",
                    LibraryMode.Declined => "DECLINED",
                    _ => "LIBRARY"
                }
                : $"PRESET · {_activeFilterPresetName.ToUpperInvariant()}";
            PlaylistSummaryText.Text = $"{_filteredItems.Count} tracks · {FormatPlaylistDuration(totalSeconds)}";
            CollectionOrderNotice.IsVisible = false;
            UpdateCollectionContextCover();
            return;
        }

        var totalTracks = _activeCollectionOrder.Count;
        LibraryContextTitleText.Text = $"COLLECTION · {_activeCollection.Name.ToUpperInvariant()}";
        var visible = _filteredItems.Count == totalTracks
            ? $"{totalTracks} tracks"
            : $"{_filteredItems.Count} of {totalTracks} tracks";
        var preset = _activeFilterPresetName is null ? string.Empty : $" · preset: {_activeFilterPresetName}";
        PlaylistSummaryText.Text = $"{visible} · {FormatPlaylistDuration(totalSeconds)}{preset}";
        CollectionOrderNotice.IsVisible = !CanEditCollectionOrder();
        UpdateCollectionContextCover();
    }

    private void NavigateNext(bool isManual)
    {
        if (_isTrackPreviewActive) return;
        if (_filteredItems.Count == 0) { FullStop(); return; }

        if (_engine.ActiveTrackId < 0)
        {
            var selIdx = GetSelectedFilteredIndex();
            var startIndex = selIdx >= 0 && selIdx < _filteredItems.Count ? selIdx : 0;
            PlayTrackAt(startIndex, isCrossfade: false, resetQueue: true);
            return;
        }

        if (!_playbackQueue.IsInitialized)
        {
            var currentIndex = GetCurrentPlayIndex();
            if (currentIndex < 0) { FullStop(); return; }
            _playbackQueue.Reset(
                _filteredItems.Select(item => item.Track.Id),
                _filteredItems[currentIndex].Track.Id);
        }

        var nextTrackId = _playbackQueue.PeekNext(_loopStatus == "Playlist");
        if (nextTrackId is null) { FullStop(); return; }
        var nextTrack = _allItems.FirstOrDefault(item => item.Track.Id == nextTrackId.Value)?.Track;
        if (nextTrack is null)
        {
            _playbackQueue.Retain(
                _allItems.Select(item => item.Track.Id),
                _engine.ActiveTrackId);
            nextTrackId = _playbackQueue.PeekNext(_loopStatus == "Playlist");
            nextTrack = nextTrackId is int retainedId
                ? _allItems.FirstOrDefault(item => item.Track.Id == retainedId)?.Track
                : null;
        }
        if (nextTrack is null) { FullStop(); return; }

        PlayTrack(nextTrack, isCrossfade: false);
    }

    private void NavigatePrevious()
    {
        if (_isTrackPreviewActive) return;
        if (_filteredItems.Count == 0) return;

        if (!_playbackQueue.IsInitialized)
        {
            var currentIndex = GetCurrentPlayIndex();
            if (currentIndex < 0) return;
            _playbackQueue.Reset(
                _filteredItems.Select(item => item.Track.Id),
                _filteredItems[currentIndex].Track.Id);
        }

        var previousTrackId = _playbackQueue.PeekPrevious();
        if (previousTrackId is null) return;
        var previousTrack = _allItems.FirstOrDefault(item => item.Track.Id == previousTrackId.Value)?.Track;
        if (previousTrack is not null)
            PlayTrack(previousTrack, isCrossfade: false);
    }

    private void FullStop()
    {
        var totalSeconds = _engine.TotalTime.TotalSeconds;
        var playedFraction = totalSeconds > 0 ? _engine.CurrentTime.TotalSeconds / totalSeconds : 1;
        FinishListeningSession(markSkipped: playedFraction < .8);
        _engine.Stop();
    }

    private static AmbientPalette ExtractAmbientPalette(byte[] artwork)
    {
        using var bitmap = SKBitmap.Decode(artwork);
        if (bitmap is null || bitmap.Width == 0 || bitmap.Height == 0)
            return DefaultAmbientPalette;

        const int hueBinCount = 18;
        var bins = new AmbientColorBin[hueBinCount];
        for (var index = 0; index < bins.Length; index++)
            bins[index] = new AmbientColorBin();

        var step = Math.Max(1, (int)Math.Sqrt((bitmap.Width * bitmap.Height) / 5000d));
        var opaqueSampleCount = 0;
        var chromaticSampleCount = 0;
        double sampledLightness = 0;
        for (var y = step / 2; y < bitmap.Height; y += step)
        {
            for (var x = step / 2; x < bitmap.Width; x += step)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Alpha < 160)
                    continue;

                pixel.ToHsl(out var hue, out var saturation, out var lightness);
                opaqueSampleCount++;
                sampledLightness += lightness;
                if (saturation < 18 || lightness < 7 || lightness > 92)
                    continue;

                chromaticSampleCount++;
                var midtoneWeight = 1 - Math.Abs((lightness / 100d) - 0.5) * 0.55;
                var weight = (0.25 + saturation / 100d) * midtoneWeight;
                var binIndex = Math.Clamp((int)(hue / (360d / hueBinCount)), 0, hueBinCount - 1);
                bins[binIndex].Add(pixel, weight);
            }
        }

        // A small warm accent must not define an otherwise white, black or
        // grayscale cover. In that case the artwork's overall brightness is
        // more representative than the strongest remaining hue.
        var chromaticCoverage = opaqueSampleCount > 0
            ? chromaticSampleCount / (double)opaqueSampleCount
            : 0;
        if (chromaticCoverage < 0.18)
            return CreateNeutralAmbientPalette(opaqueSampleCount > 0
                ? sampledLightness / opaqueSampleCount
                : 50);

        var primaryIndex = Array.FindIndex(bins, bin => ReferenceEquals(bin, bins.MaxBy(candidate => candidate.Weight)));
        if (primaryIndex < 0 || bins[primaryIndex].Weight <= 0)
            return CreateNeutralAmbientPalette(sampledLightness / Math.Max(1, opaqueSampleCount));

        var secondaryCandidates = Enumerable.Range(0, bins.Length)
            .Where(index => bins[index].Weight > 0 && HueBinDistance(index, primaryIndex, hueBinCount) >= 2)
            .ToList();
        var secondaryIndex = secondaryCandidates.Count > 0
            ? secondaryCandidates.MaxBy(index => bins[index].Weight * (1 + HueBinDistance(index, primaryIndex, hueBinCount) * 0.08))
            : primaryIndex;

        var primary = NormalizeAmbientColor(bins[primaryIndex].AverageColor());
        var secondary = secondaryIndex != primaryIndex && bins[secondaryIndex].Weight > 0
            ? NormalizeAmbientColor(bins[secondaryIndex].AverageColor())
            : RotateAmbientColor(primary, 46);
        return new AmbientPalette(primary, secondary);
    }

    private static AmbientPalette CreateNeutralAmbientPalette(double artworkLightness)
    {
        // Bright neutral covers produce a light silver graph, while dark
        // covers remain visible through a softer graphite-to-gray gradient.
        var primaryLightness = Math.Clamp(54 + artworkLightness * 0.36, 58, 88);
        var secondaryLightness = Math.Clamp(primaryLightness - 25, 36, 66);
        var primary = SKColor.FromHsl(215, 5, (float)primaryLightness);
        var secondary = SKColor.FromHsl(215, 8, (float)secondaryLightness);
        return new AmbientPalette(
            Color.FromRgb(primary.Red, primary.Green, primary.Blue),
            Color.FromRgb(secondary.Red, secondary.Green, secondary.Blue));
    }

    private static int HueBinDistance(int left, int right, int count)
    {
        var distance = Math.Abs(left - right);
        return Math.Min(distance, count - distance);
    }

    private static Color NormalizeAmbientColor(SKColor color)
    {
        color.ToHsl(out var hue, out var saturation, out var lightness);
        var normalized = SKColor.FromHsl(
            hue,
            Math.Clamp(saturation, 55, 82),
            Math.Clamp(lightness, 40, 56));
        return Color.FromRgb(normalized.Red, normalized.Green, normalized.Blue);
    }

    private static Color RotateAmbientColor(Color color, float degrees)
    {
        var source = new SKColor(color.R, color.G, color.B);
        source.ToHsl(out var hue, out var saturation, out var lightness);
        var rotated = SKColor.FromHsl((hue + degrees) % 360, saturation, lightness);
        return Color.FromRgb(rotated.Red, rotated.Green, rotated.Blue);
    }

    private sealed record LoadedTrackThumbnail(byte[] Artwork, AmbientPalette Palette);
    private sealed record AmbientPalette(Color Primary, Color Secondary);

    private static byte ToByte(double value) =>
        (byte)Math.Clamp((int)Math.Round(value), 0, 255);

    private sealed class AmbientColorBin
    {
        private double _red;
        private double _green;
        private double _blue;

        public double Weight { get; private set; }

        public void Add(SKColor color, double weight)
        {
            _red += color.Red * weight;
            _green += color.Green * weight;
            _blue += color.Blue * weight;
            Weight += weight;
        }

        public SKColor AverageColor() => Weight <= 0
            ? SKColors.Transparent
            : new SKColor(
                ToByte(_red / Weight),
                ToByte(_green / Weight),
                ToByte(_blue / Weight));
    }

    // ─── Listening telemetry ─────────────────────────────────────────────────

    private void BeginListeningSession(int trackId)
    {
        _listeningTrackId = trackId;
        _lastListeningPositionSeconds = 0;
        _unflushedListeningSeconds = 0;
        MusicLibraryService.Current.RecordTrackPlaybackStarted(trackId);
        EditTrackOverlay.RefreshUsageStats();
    }

    private void ResumeListeningSession(int trackId, TimeSpan position)
    {
        _listeningTrackId = trackId;
        _lastListeningPositionSeconds = position.TotalSeconds;
        _unflushedListeningSeconds = 0;
        EditTrackOverlay.RefreshUsageStats();
    }

    private void RecordListeningProgress()
    {
        if (_listeningTrackId < 0 || _listeningTrackId != _engine.ActiveTrackId)
            return;

        var position = _engine.CurrentTime.TotalSeconds;
        var delta = position - _lastListeningPositionSeconds;
        // Seeking is not listening time; accept only the small deltas emitted by the playback timer.
        if (delta is > 0 and <= 1.5)
            _unflushedListeningSeconds += delta;
        _lastListeningPositionSeconds = position;
        FlushListeningSeconds();
    }

    private void FinishListeningSession(bool markSkipped)
    {
        if (_listeningTrackId < 0) return;
        RecordListeningProgress();
        FlushListeningSeconds(force: true);
        if (markSkipped)
            MusicLibraryService.Current.RecordTrackSkip(_listeningTrackId);
        EditTrackOverlay.RefreshUsageStats();
        _listeningTrackId = -1;
        _lastListeningPositionSeconds = 0;
        _unflushedListeningSeconds = 0;
    }

    private void FlushListeningSeconds(bool force = false)
    {
        var wholeSeconds = (int)Math.Floor(_unflushedListeningSeconds);
        if (wholeSeconds < (force ? 1 : 5)) return;
        MusicLibraryService.Current.AddTrackListenedSeconds(_listeningTrackId, wholeSeconds);
        _unflushedListeningSeconds -= wholeSeconds;
        EditTrackOverlay.RefreshUsageStats();
    }

    // ─── Upcoming bar ─────────────────────────────────────────────────────────

    private void OnTrackContextMenuPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { DataContext: TrackDisplayItem item } trackCard
            || !e.GetCurrentPoint(trackCard).Properties.IsRightButtonPressed)
            return;

        CloseActiveTrackContextMenu();
        if (!item.IsContextOnly)
            EnsureLoadedPlaylistQueue(item.Track.Id);
        var trackId = item.Track.Id;
        var index = _playbackQueue.TrackIds.ToList().IndexOf(trackId);
        var currentIndex = _playbackQueue.CurrentTrackId is int currentTrackId
            ? _playbackQueue.TrackIds.ToList().IndexOf(currentTrackId)
            : -1;
        var isCurrent = _playbackQueue.CurrentTrackId == trackId;
        var menuItems = new List<object>
        {
            CreateTrackMenuItem("Open track on YouTube",
                IsValidExternalUrl(item.Track.CanonicalUrl),
                () => OpenExternalUrl(item.Track.CanonicalUrl)),
            CreateTrackMenuItem("Open channel on YouTube",
                IsValidExternalUrl(item.Track.ChannelUrl),
                () => OpenExternalUrl(item.Track.ChannelUrl)),
            new Separator { Classes = { "track-context-separator" } },
            CreateTrackMenuItem("Edit", true,
                () => OpenTrackEditor(item.Track)),
            CreateTrackMenuItem("Add version", item.Track.IsOriginal,
                () => AddVersionOverlay.Open(item.Track)),
            CreateAddToCollectionMenuItem(item.Track)
        };

        if (_activeCollection is not null)
        {
            menuItems.Add(CreateTrackMenuItem(
                "Use this artwork as collection cover",
                _activeCollection.CoverKind != CollectionCoverKind.Track
                || _activeCollection.CoverTrackId != trackId,
                () => UseTrackArtworkAsActiveCollectionCover(item.Track)));
        }

        menuItems.AddRange([
            new Separator { Classes = { "track-context-separator" } },
            CreateTrackMenuItem("Play next",
                !item.IsContextOnly && !isCurrent && index != currentIndex + 1,
                () => ApplyTrackQueueMutation(() => _playbackQueue.MoveNext(trackId))),
            CreateTrackMenuItem("Move up",
                !item.IsContextOnly && !isCurrent && index > 0 && index - 1 != currentIndex,
                () => ApplyTrackQueueMutation(() => _playbackQueue.Move(trackId, -1))),
            CreateTrackMenuItem("Move down", !item.IsContextOnly && !isCurrent && index >= 0
                && index < _playbackQueue.TrackIds.Count - 1 && index + 1 != currentIndex,
                () => ApplyTrackQueueMutation(() => _playbackQueue.Move(trackId, 1))),
            CreateTrackMenuItem("Remove from queue", !item.IsContextOnly && !isCurrent,
                () => ApplyTrackQueueMutation(() => _playbackQueue.Remove(trackId)))
        ]);

        if (_activeCollection is not null)
        {
            var collectionIndex = _activeCollectionOrder.GetValueOrDefault(trackId, -1);
            var canReorder = CanEditCollectionOrder();
            menuItems.Add(new Separator { Classes = { "track-context-separator" } });
            menuItems.Add(CreateTrackMenuItem(
                "Move up in collection",
                canReorder && collectionIndex > 0,
                () => MoveActiveCollectionTrack(trackId, -1)));
            menuItems.Add(CreateTrackMenuItem(
                "Move down in collection",
                canReorder && collectionIndex >= 0 && collectionIndex < _activeCollectionOrder.Count - 1,
                () => MoveActiveCollectionTrack(trackId, 1)));
        }

        var menu = new ContextMenu
        {
            Classes = { "track-context" },
            Placement = PlacementMode.Pointer,
            ItemsSource = menuItems
        };
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activeTrackContextMenu, menu))
                _activeTrackContextMenu = null;
        };
        _activeTrackContextMenu = menu;
        menu.Open(trackCard);
        e.Handled = true;
    }

    private static bool IsValidExternalUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private void OpenExternalUrl(string? url)
    {
        if (!IsValidExternalUrl(url))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(url!) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowToast($"Could not open YouTube: {ex.Message}");
        }
    }

    private void AttachContextMenuDismissHandler()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (ReferenceEquals(_contextMenuDismissRoot, topLevel))
            return;

        DetachContextMenuDismissHandler();
        _contextMenuDismissRoot = topLevel;
        _contextMenuDismissRoot?.AddHandler(
            PointerPressedEvent,
            OnApplicationPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
    }

    private void DetachContextMenuDismissHandler()
    {
        _contextMenuDismissRoot?.RemoveHandler(PointerPressedEvent, OnApplicationPointerPressed);
        _contextMenuDismissRoot = null;
    }

    private void OnApplicationPointerPressed(object? sender, PointerPressedEventArgs e) =>
        CloseActiveTrackContextMenu();

    private void CloseActiveTrackContextMenu()
    {
        var menu = _activeTrackContextMenu;
        _activeTrackContextMenu = null;
        menu?.Close();
    }

    private void EnsureLoadedPlaylistQueue(int preferredTrackId)
    {
        if (_playbackQueue.IsInitialized && _playbackQueue.TrackIds.Contains(preferredTrackId))
            return;

        var trackIds = _filteredItems.Select(item => item.Track.Id).ToList();
        if (trackIds.Count == 0)
            return;
        var currentTrackId = _engine.ActiveTrackId >= 0
            ? _engine.ActiveTrackId
            : preferredTrackId;
        _playbackQueue.Reset(trackIds, currentTrackId);
        _loadedPlaylistSourceTrackIds = trackIds;
    }

    private static MenuItem CreateTrackMenuItem(
        string header,
        bool isEnabled,
        Action action)
    {
        var item = new MenuItem
        {
            Header = header,
            IsEnabled = isEnabled
        };
        item.Classes.Add("track-context-item");
        item.Click += (_, _) => action();
        return item;
    }

    private MenuItem CreateAddToCollectionMenuItem(MusicTrack track)
    {
        var memberships = MusicLibraryService.Current.GetTrackCollections(track.Id)
            .Select(collection => collection.Id)
            .ToHashSet();
        var parent = new MenuItem
        {
            Header = "Add to collection",
            IsEnabled = true
        };
        parent.Classes.Add("track-context-item");

        var choices = _collections
            .Select(collection => (object)CreateTrackMenuItem(
                memberships.Contains(collection.Id)
                    ? $"{collection.Name}  ·  Already added"
                    : collection.Name,
                !memberships.Contains(collection.Id),
                () => AddTrackToCollection(collection, track)))
            .ToList();
        if (choices.Count == 0)
            choices.Add(CreateTrackMenuItem("No collections available", false, () => { }));
        parent.ItemsSource = choices;
        return parent;
    }

    private void UseTrackArtworkAsActiveCollectionCover(MusicTrack track)
    {
        if (_activeCollection is null)
            return;

        try
        {
            var collectionName = _activeCollection.Name;
            MusicLibraryService.Current.SetCollectionCoverTrack(_activeCollection.Id, track.Id);
            ReloadCollections();
            ShowToast($"Using {track.DisplayTitle} artwork for {collectionName}");
        }
        catch (Exception exception)
        {
            ShowToast($"Could not use track artwork: {exception.Message}");
        }
    }

    private void AddTrackToCollection(TrackCollection collection, MusicTrack track)
    {
        if (!MusicLibraryService.Current.AddTrackToCollection(collection.Id, track.Id))
            return;

        ReloadCollections(refreshTracks: true);
        ShowToast($"Added {track.DisplayTitle} to {collection.Name}");
    }

    private void MoveActiveCollectionTrack(int trackId, int offset)
    {
        if (_activeCollection is null || !CanEditCollectionOrder())
            return;
        if (!MusicLibraryService.Current.MoveCollectionTrack(_activeCollection.Id, trackId, offset))
            return;

        RefreshActiveCollectionOrder();
        ReloadCollections(refreshTracks: true);
        ResetPlaybackQueueFromCurrentView();
        PersistPlayerSession();
    }

    private void ApplyTrackQueueMutation(Func<bool> mutation)
    {
        if (!mutation())
            return;

        ApplyPlaybackQueueToLoadedPlaylist();
        RefreshVisibleItemsSource((FileList.SelectedItem as TrackDisplayItem)?.Track.Id);
        UpdatePlaylistSummary();
        RefreshNextTrackPreview();
        RestartVisibleThumbnailLoad();
        PersistPlayerSession();
    }

    private void ApplyPlaybackQueueToLoadedPlaylist()
    {
        if (!_playbackQueue.IsInitialized || _filteredItems.Count == 0)
            return;

        var itemById = _filteredItems.ToDictionary(item => item.Track.Id);
        _filteredItems = _playbackQueue.TrackIds
            .Where(itemById.ContainsKey)
            .Select(trackId => itemById[trackId])
            .ToList();
    }

    private void UpdateUpcomingBar()
    {
        if (_nextTrackIndex < 0 || _nextTrackIndex >= _allItems.Count)
        {
            UpcomingBar.IsVisible = false;
            return;
        }

        UpcomingBar.IsVisible = true;
        UpcomingTrackText.Text = _allItems[_nextTrackIndex].Track.DisplayTitle;
        CrossfadeStatusText.Text = string.Empty;
    }

    // ─── Progress / seeking ───────────────────────────────────────────────────

    private void OnSliderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_engine.State == EngineState.Stopped) return;
        _isSeeking = true;
    }

    private void OnSliderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_engine.State == EngineState.Stopped) { _isSeeking = false; return; }
        _engine.Seek(PlaybackSlider.Value / 100.0);
        PlaybackTimeText.Text =
            $"{FormatDuration(_engine.CurrentTime)} / {FormatDuration(_engine.TotalTime)}";
        _isSeeking = false;
        _windowsMediaSession.NotifySeeked(_engine.CurrentTime);
        UpdateDiscordPresence();
    }

    // ─── UI helpers ───────────────────────────────────────────────────────────

    private void UpdateDiscordPresence()
    {
        if (_engine.State == EngineState.Stopped)
        {
            _discordPresence.Clear();
            return;
        }

        var item = _filteredItems.FirstOrDefault(item => item.Track.Id == _engine.ActiveTrackId)
                   ?? _allItems.FirstOrDefault(item => item.Track.Id == _engine.ActiveTrackId);
        if (item is null)
            return;

        _discordPresence.Update(item, _engine.State, _engine.CurrentTime, _engine.TotalTime);
    }

    private void UpdateSystemMediaMetadata()
    {
        if (_engine.State == EngineState.Stopped)
            return;

        var item = _filteredItems.FirstOrDefault(item => item.Track.Id == _engine.ActiveTrackId)
                   ?? _allItems.FirstOrDefault(item => item.Track.Id == _engine.ActiveTrackId);
        if (item is null)
            return;

        var filePath = Path.Combine(Values.TracksDirectory, item.Track.FileName);
        var artworkUri = MprisArtworkCache.GetArtworkUri(item.Track, filePath);

        _windowsMediaSession.UpdateMetadata(
            item.Track.Id,
            TrackTitleFormatter.Format(null, item.Track.Title, item.Track.Remix),
            string.IsNullOrWhiteSpace(item.Track.Artist) ? item.ChannelText : item.Track.Artist,
            _engine.CurrentTime,
            _engine.TotalTime,
            filePath,
            artworkUri,
            canGoNext: PeekNextTrackIndex(GetCurrentPlayIndex()) >= 0,
            canGoPrevious: GetCurrentPlayIndex() > 0);
    }

    private void UpdateButtonStates()
    {
        var isPlaying = _engine.State == EngineState.Playing;
        PlayIcon.IsVisible = !isPlaying;
        PauseIcon.IsVisible = isPlaying;
    }

    private void RefreshPlayingMarkers()
    {
        var activeTrackId = _engine.ActiveTrackId;
        foreach (var item in _allItems)
            item.IsPlaying = activeTrackId >= 0 && item.Track.Id == activeTrackId;

        if (_filteredItems.Count == 0)
        {
            RefreshVisibleItemsSource();
            return;
        }

        var selectedId = (FileList.SelectedItem as TrackDisplayItem)?.Track.Id ?? -1;

        var targetId = selectedId >= 0 ? selectedId : _engine.ActiveTrackId;
        if (targetId >= 0)
        {
            var idx = _filteredItems.FindIndex(i => i.Track.Id == targetId);
            if (idx >= 0)
                EnsureVisibleWindowAround(idx);
        }

        RefreshVisibleItemsSource(selectedId >= 0 ? selectedId : null);
    }

    // ─── Formatting ───────────────────────────────────────────────────────────

    private static string FormatDuration(TimeSpan t) => FormatDuration((int)t.TotalSeconds);

    private static string FormatPlaylistDuration(int totalSeconds)
    {
        var time = TimeSpan.FromSeconds(totalSeconds);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:D2}h"
            : $"{time.Minutes}:{time.Seconds:D2}m";
    }

    private void ShuffleFilteredItems()
    {
        var startsNewSession = _shufflePriorities.Count == 0;
        var missingTracks = _filteredItems
            .Where(item => !_shufflePriorities.ContainsKey(item.Track.Id))
            .Select(item => item.Track)
            .ToList();

        if (missingTracks.Count > 0)
        {
            var usageByTrackId = MusicLibraryService.Current.GetAllTrackUsageStats();
            var generatedPriorities = TrackShuffleService.CreatePriorities(
                _filteredItems.Select(item => item.Track).ToList(),
                usageByTrackId,
                Values.Ratings,
                _rng,
                DateTimeOffset.UtcNow);

            foreach (var track in missingTracks)
                _shufflePriorities[track.Id] = generatedPriorities[track.Id];
        }

        if (startsNewSession && _engine.ActiveTrackId >= 0
                             && _shufflePriorities.ContainsKey(_engine.ActiveTrackId))
            _shufflePriorities[_engine.ActiveTrackId] = double.NegativeInfinity;

        _filteredItems = _filteredItems
            .OrderBy(item => _shufflePriorities[item.Track.Id])
            .ToList();
    }

    private static string FormatDuration(int seconds)
    {
        var m = seconds / 60;
        var s = seconds % 60;
        return $"{m:D2}:{s:D2}";
    }
}
