using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Resona.Core;
using SkiaSharp;

namespace Resona.Companion.Views;

public partial class MainView : UserControl
{
    private readonly ICompanionAudioPlayer _audio = CompanionServices.AudioPlayer;
    private readonly ILibraryStorage _libraryStorage = CompanionServices.LibraryStorage;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _artworkTimer;
    private bool _systemBarsConfigured;
    private double _safeAreaTop;
    private double _safeAreaBottom;

    private LoadedMusicLibrary _loadedLibrary = new("", PortableMusicLibrary.Empty);

    private List<PortableTrack> _filteredTracks = [];
    private readonly HashSet<string> _selectedRatings = new(StringComparer.OrdinalIgnoreCase);
    private const int ThumbnailCacheCapacity = 192;
    private static readonly TimeSpan AutomaticArtworkTransitionDuration = TimeSpan.FromSeconds(6.5);
    private static readonly TimeSpan ManualArtworkTransitionDuration = TimeSpan.FromSeconds(1.8);
    private static readonly AmbientPalette DefaultAmbientPalette = new(
        Color.FromRgb(57, 86, 139),
        Color.FromRgb(70, 45, 124));

    private readonly Dictionary<string, Bitmap?> _thumbnailCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _thumbnailCacheOrder = new();
    private readonly Random _rng = new();
    private Bitmap? _activeArtwork;
    private Bitmap? _fadedArtwork;
    private string? _activeArtworkFileName;
    private DateTimeOffset _artworkTransitionStartedAt;
    private TimeSpan _artworkTransitionDuration = ManualArtworkTransitionDuration;
    private double _artworkTransitionProgress = 1;
    private AmbientPalette _activeAmbientPalette = DefaultAmbientPalette;
    private AmbientPalette _fadedAmbientPalette = DefaultAmbientPalette;
    private int _currentIndex = -1;
    private string? _currentTrackFileName;
    private bool _isSeeking;
    private bool _shuffle;
    private bool _manualRatingFilter;
    private MobileLibrarySource _activeLibrarySource = MobileLibrarySource.Default;
    private string? _activeLibrarySourceId;
    private DateTime _lastMediaUpdate = DateTime.MinValue;
    private CancellationTokenSource? _toastCts;

    private enum MobileLibrarySource
    {
        Default,
        Preset,
        Collection
    }

    private sealed class TrackRow
    {
        private static readonly IBrush TransparentBrush = Brushes.Transparent;
        private readonly MainView _owner;

        public TrackRow(MainView owner, PortableTrack track, bool isCurrent)
        {
            _owner = owner;
            Track = track;
            IsCurrent = isCurrent;
        }

        public PortableTrack Track { get; }
        public bool IsCurrent { get; }
        public Bitmap? Cover => _owner.LoadThumbnail(Track);

        public string Title => Track.Title;
        public IReadOnlyList<SubgenreDisplay> Subgenres
        {
            get
            {
                var genres = Track.Genres
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Take(3)
                    .Select(value => new SubgenreDisplay(SubgenreName(value), GenreBrush(value)))
                    .ToList();

                return genres
                    .Select((genre, index) => genre with
                    {
                        Text = index switch
                        {
                            _ when genres.Count == 1 || index == genres.Count - 1 => genre.Text,
                            _ when index == genres.Count - 2 => $"{genre.Text} and\u00A0",
                            _ => $"{genre.Text},\u00A0"
                        }
                    })
                    .ToList();
            }
        }
        public string MetadataText => string.Join(" · ", new[]
            {
                Track.ChannelName,
                Track.GenreText,
                Track.StyleText,
                string.Join("  ", (Track.Tags ?? []).Select(tag => $"# {tag}"))
            }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        public string DurationText => Track.DurationText;
        public string Rating => string.IsNullOrWhiteSpace(Track.Rating) ? "None" : Track.Rating;
        public IBrush RatingBackground => Rating switch
        {
            "Timeless" => new SolidColorBrush(Color.FromArgb(70, 132, 105, 36)),
            "Amazing" => new SolidColorBrush(Color.FromArgb(70, 120, 79, 42)),
            "Great" => new SolidColorBrush(Color.FromArgb(68, 35, 105, 58)),
            "Good" => new SolidColorBrush(Color.FromArgb(66, 31, 93, 82)),
            "Okay" => new SolidColorBrush(Color.FromArgb(62, 83, 84, 64)),
            "Avoid" => new SolidColorBrush(Color.FromArgb(66, 124, 47, 40)),
            _ => new SolidColorBrush(Color.FromArgb(60, 103, 76, 42))
        };
        public IBrush RatingBorder => Rating switch
        {
            "Timeless" => new SolidColorBrush(Color.FromArgb(185, 219, 184, 85)),
            "Amazing" => new SolidColorBrush(Color.FromArgb(180, 221, 144, 78)),
            "Great" => new SolidColorBrush(Color.FromArgb(175, 83, 176, 105)),
            "Good" => new SolidColorBrush(Color.FromArgb(170, 76, 164, 139)),
            "Okay" => new SolidColorBrush(Color.FromArgb(150, 139, 144, 108)),
            "Avoid" => new SolidColorBrush(Color.FromArgb(170, 201, 82, 68)),
            _ => new SolidColorBrush(Color.FromArgb(160, 190, 139, 69))
        };
        public IBrush RatingForeground => Rating switch
        {
            "Timeless" => new SolidColorBrush(Color.FromRgb(255, 230, 150)),
            "Amazing" => new SolidColorBrush(Color.FromRgb(247, 195, 132)),
            "Great" => new SolidColorBrush(Color.FromRgb(188, 242, 185)),
            "Good" => new SolidColorBrush(Color.FromRgb(176, 232, 212)),
            "Okay" => new SolidColorBrush(Color.FromRgb(226, 224, 194)),
            "Avoid" => new SolidColorBrush(Color.FromRgb(246, 175, 160)),
            _ => new SolidColorBrush(Color.FromRgb(243, 203, 128))
        };
        public IBrush CurrentBackground => IsCurrent
            ? new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0.9, 0.5, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#483E6591"), 0),
                    new GradientStop(Color.Parse("#24325A8D"), 0.42),
                    new GradientStop(Colors.Transparent, 1)
                }
            }
            : TransparentBrush;
        public IBrush CurrentAccent => IsCurrent
            ? new SolidColorBrush(Color.Parse("#78A9E6"))
            : TransparentBrush;
        public Thickness CurrentBorderThickness => IsCurrent
            ? new Thickness(3, 0, 0, 0)
            : new Thickness(0);

        private static string SubgenreName(string value)
        {
            var separator = value.IndexOf('→');
            return separator >= 0 ? value[(separator + 1)..].Trim() : value.Trim();
        }

        private static IBrush GenreBrush(string value)
        {
            var mainGenre = value.Split('→', 2)[0].Trim();
            var color = mainGenre switch
            {
                "Electronic" => "#86E0B0",
                "Rock" => "#FF826E",
                "Pop" => "#FF7FB6",
                "Hip Hop" => "#D58AFF",
                "Jazz" => "#53D6B6",
                "Funk / Soul" => "#FFAA5C",
                "Classical" => "#BFA3FF",
                "Reggae" => "#B5E85B",
                "Latin" => "#FF955C",
                "Folk, World, & Country" => "#9DDB72",
                "Blues" => "#6EA8FF",
                "Stage & Screen" => "#FFD166",
                _ => "#B5BDC7"
            };
            return new SolidColorBrush(Color.Parse(color));
        }
    }

    private sealed record SubgenreDisplay(string Text, IBrush Foreground);

    public MainView()
    {
        InitializeComponent();

        AttachedToVisualTree += (_, _) => ConfigureSystemBars();
        if (Application.Current?.ApplicationLifetime is IActivatableLifetime activatableLifetime)
            activatableLifetime.Activated += (_, _) => Dispatcher.UIThread.Post(ConfigureSystemBars);

        SearchBox.TextChanged += (_, _) =>
        {
            SearchButton.Opacity = SearchBox.IsVisible || !string.IsNullOrWhiteSpace(SearchBox.Text) ? 1 : 0.72;
            ApplyFilter();
        };
        ProgressSlider.AddHandler(PointerPressedEvent, OnProgressPressed, RoutingStrategies.Tunnel);
        ProgressSlider.AddHandler(PointerMovedEvent, OnProgressMoved, RoutingStrategies.Tunnel);
        ProgressSlider.AddHandler(PointerReleasedEvent, OnProgressReleased, RoutingStrategies.Tunnel);
        ProgressSlider.PointerCaptureLost += (_, _) => _isSeeking = false;

        _audio.PlaybackEnded += () => Dispatcher.UIThread.Post(() => PlayNext(isAutomaticTransition: true));
        CompanionServices.MediaControls.CommandRequested += OnMediaCommandRequested;
        CompanionServices.MediaControls.SeekRequested += OnMediaSeekRequested;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => UpdatePlaybackUi();
        _timer.Start();

        _artworkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _artworkTimer.Tick += (_, _) => UpdateArtworkTransition();

        _ = LoadLibraryAsync();
    }

    private void ConfigureSystemBars()
    {
        var insetsManager = TopLevel.GetTopLevel(this)?.InsetsManager;
        if (insetsManager is null)
            return;

        insetsManager.DisplayEdgeToEdgePreference = true;
        insetsManager.SystemBarColor = Colors.Transparent;
        ApplySafeArea(insetsManager.SafeAreaPadding);

        if (!_systemBarsConfigured)
        {
            _systemBarsConfigured = true;
            insetsManager.SafeAreaChanged += (_, args) => ApplySafeArea(args.SafeAreaPadding);
        }
    }

    private void ApplySafeArea(Thickness safeArea)
    {
        // Android can briefly report zero insets while system-bar colors are
        // changing. Keep the last real values so content does not jump behind
        // the status or gesture bar during artwork and activity transitions.
        if (safeArea.Top > 0)
            _safeAreaTop = safeArea.Top;
        if (safeArea.Bottom > 0)
            _safeAreaBottom = safeArea.Bottom;

        HeaderBar.Padding = new Thickness(14, 6 + _safeAreaTop, 12, 6);
        PlayerContent.Margin = new Thickness(10, 4, 10, 5 + _safeAreaBottom);
    }

    private async Task LoadLibraryAsync()
    {
        try
        {
            if (!_libraryStorage.IsConfigured && !await _libraryStorage.SelectLibraryAsync())
            {
                SetStatus("Choose the Data/Music folder to load your library.");
                return;
            }

            ClearThumbnailCache();
            _loadedLibrary = await _libraryStorage.LoadAsync();
            SetStatus();
        }
        catch (Exception ex)
        {
            _loadedLibrary = new LoadedMusicLibrary(
                _libraryStorage.LibraryDirectory,
                PortableMusicLibrary.Empty);
            SetStatus($"Could not load library: {ex.Message}");
        }

        PopulateFilters();
        ApplyFilter();
    }

    private void PopulateFilters()
    {
        ApplyDefaultRatingFilter();
        RebuildLibrarySourceRows();
    }

    private void ApplyDefaultRatingFilter()
    {
        var ratings = _loadedLibrary.Library.Ratings;
        _selectedRatings.Clear();
        _selectedRatings.UnionWith(ratings);
        _manualRatingFilter = false;
        RefreshRatingFilterControls();
    }

    private void RebuildLibrarySourceRows()
    {
        DefaultRows.Children.Clear();
        DefaultRows.Children.Add(CreateLibrarySourceButton(
            "Default",
            TrackCountText(CountTracksForSource(MobileLibrarySource.Default)),
            MobileLibrarySource.Default));

        PresetRows.Children.Clear();
        foreach (var preset in (_loadedLibrary.Library.FilterPresets ?? [])
                     .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            PresetRows.Children.Add(CreateLibrarySourceButton(
                preset.Name,
                TrackCountText(CountTracksForSource(MobileLibrarySource.Preset, preset.Name)),
                MobileLibrarySource.Preset,
                preset.Name));
        AddEmptyState(PresetRows, "No presets available.");

        CollectionRows.Children.Clear();
        foreach (var collection in (_loadedLibrary.Library.Collections ?? [])
                     .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            CollectionRows.Children.Add(CreateLibrarySourceButton(
                collection.Name,
                TrackCountText(CountTracksForSource(MobileLibrarySource.Collection, collection.StableId)),
                MobileLibrarySource.Collection,
                collection.StableId));
        AddEmptyState(CollectionRows, "No collections available.");
    }

    private int CountTracksForSource(MobileLibrarySource source, string? id = null)
    {
        IEnumerable<PortableTrack> tracks = _loadedLibrary.Library.Tracks.Where(track => !IsRejected(track));

        if (source == MobileLibrarySource.Collection)
        {
            var collection = (_loadedLibrary.Library.Collections ?? []).FirstOrDefault(item =>
                string.Equals(item.StableId, id, StringComparison.OrdinalIgnoreCase));
            var keys = collection?.TrackKeys.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
            tracks = tracks.Where(track => !string.IsNullOrWhiteSpace(track.TrackKey) && keys.Contains(track.TrackKey));
        }

        var groups = source == MobileLibrarySource.Preset
            ? (_loadedLibrary.Library.FilterPresets ?? []).FirstOrDefault(item =>
                string.Equals(item.Name, id, StringComparison.OrdinalIgnoreCase))?.Groups ?? []
            : [];

        return PortableTrackFilter.Apply(tracks, null, new HashSet<string>(), groups).Count;
    }

    private static string TrackCountText(int count) => $"{count} {(count == 1 ? "track" : "tracks")}";

    private Button CreateLibrarySourceButton(
        string title,
        string description,
        MobileLibrarySource source,
        string? id = null)
    {
        var selected = _activeLibrarySource == source
                       && string.Equals(_activeLibrarySourceId, id, StringComparison.OrdinalIgnoreCase);
        var labels = new StackPanel { Spacing = 1 };
        labels.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = new FontFamily("Consolas, monospace"),
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = CompanionTheme.Brush(selected
                ? "Mobile.Brush.TextStrong"
                : "Mobile.Brush.TextPrimary")
        });
        labels.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 9.5,
            Foreground = CompanionTheme.Brush("Mobile.Brush.TextMuted"),
            Opacity = 0.58
        });

        var button = new Button
        {
            Content = labels,
            MinHeight = 43,
            Padding = new Thickness(9, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = selected
                ? new SolidColorBrush(Color.Parse("#303E6591"))
                : CompanionTheme.Brush("Mobile.Brush.Surface"),
            BorderBrush = selected
                ? new SolidColorBrush(Color.Parse("#8A78A9E6"))
                : CompanionTheme.Brush("Mobile.Brush.BorderSubtle"),
            BorderThickness = selected ? new Thickness(3, 1, 1, 1) : new Thickness(1),
            CornerRadius = new CornerRadius(2)
        };
        button.Click += (_, _) => SelectLibrarySource(source, id);
        return button;
    }

    private static void AddEmptyState(StackPanel panel, string text)
    {
        if (panel.Children.Count > 0)
            return;
        panel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 10,
            Opacity = 0.46,
            Margin = new Thickness(2, 3)
        });
    }

    private void SelectLibrarySource(MobileLibrarySource source, string? id)
    {
        _activeLibrarySource = source;
        _activeLibrarySourceId = id;
        ApplyFilter();
        RebuildLibrarySourceRows();
    }

    private void ApplyFilter()
    {
        IEnumerable<PortableTrack> sourceTracks = _loadedLibrary.Library.Tracks;
        if (_activeLibrarySource == MobileLibrarySource.Collection)
        {
            var collection = (_loadedLibrary.Library.Collections ?? []).FirstOrDefault(item =>
                string.Equals(item.StableId, _activeLibrarySourceId, StringComparison.OrdinalIgnoreCase));
            var trackKeys = collection?.TrackKeys.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
            sourceTracks = sourceTracks.Where(track =>
                !string.IsNullOrWhiteSpace(track.TrackKey) && trackKeys.Contains(track.TrackKey));
        }

        var activePreset = _activeLibrarySource == MobileLibrarySource.Preset
            ? (_loadedLibrary.Library.FilterPresets ?? []).FirstOrDefault(item =>
                string.Equals(item.Name, _activeLibrarySourceId, StringComparison.OrdinalIgnoreCase))
            : null;
        IReadOnlyList<PortableFilterGroup> activeGroups = activePreset?.Groups ?? [];

        _filteredTracks = PortableTrackFilter.Apply(
            sourceTracks,
            SearchBox.Text,
            _manualRatingFilter ? _selectedRatings : new HashSet<string>(),
            activeGroups);

        _filteredTracks = _filteredTracks
            .Where(track => !IsRejected(track))
            .ToList();

        if (_manualRatingFilter && _selectedRatings.Count == 0)
            _filteredTracks.Clear();

        if (_shuffle)
            ShuffleFilteredTracks();

        _currentIndex = string.IsNullOrWhiteSpace(_currentTrackFileName)
            ? -1
            : _filteredTracks.FindIndex(track => string.Equals(
                track.FileName,
                _currentTrackFileName,
                StringComparison.OrdinalIgnoreCase));

        RefreshTrackRows();
        UpdatePlaylistSummary();
    }

    private static bool IsRejected(PortableTrack track) =>
        string.Equals(track.LibraryState, "Rejected", StringComparison.OrdinalIgnoreCase);

    private void RefreshTrackRows(bool scrollToCurrent = false)
    {
        var rows = _filteredTracks
            .Select((track, index) => new TrackRow(this, track, index == _currentIndex))
            .ToList();

        TrackList.ItemsSource = rows;
        TrackList.SelectedIndex = _currentIndex;

        if (scrollToCurrent && _currentIndex >= 0 && _currentIndex < rows.Count)
            Dispatcher.UIThread.Post(() => TrackList.ScrollIntoView(rows[_currentIndex]));
    }

    private async Task PlayTrackAtAsync(int index, bool isAutomaticTransition = false)
    {
        if (index < 0 || index >= _filteredTracks.Count)
            return;

        var track = _filteredTracks[index];
        var source = _libraryStorage.TrackSource(_loadedLibrary, track);
        if (string.IsNullOrWhiteSpace(source))
        {
            SetStatus("This track is not available in the selected Music folder.");
            return;
        }

        try
        {
            await _audio.PlayAsync(source);
            _currentTrackFileName = track.FileName;
            _currentIndex = index;
            RefreshTrackRows(scrollToCurrent: true);
            NowPlayingText.Text = track.Title;
            UpdateArtwork(track, isAutomaticTransition);
            UpdatePlayPauseIcon();
            UpdateMediaControls();
            SetStatus();
        }
        catch (Exception ex)
        {
            SetStatus($"Could not play {track.Title}: {ex.Message}");
            ShowToast("This track could not be played");
        }
    }

    private async void OnTrackTapped(object? sender, TappedEventArgs e)
    {
        await PlayTrackAtAsync(TrackList.SelectedIndex);
    }

    private async void OnTrackDoubleTapped(object? sender, RoutedEventArgs e)
    {
        await PlayTrackAtAsync(TrackList.SelectedIndex);
    }

    private async void OnPlayPauseClicked(object? sender, RoutedEventArgs e)
    {
        await TogglePlaybackAsync();
    }

    private async Task TogglePlaybackAsync()
    {
        if (CurrentTrack is null)
        {
            var index = TrackList.SelectedIndex >= 0 ? TrackList.SelectedIndex : 0;
            await PlayTrackAtAsync(index);
            return;
        }

        if (_audio.IsPlaying)
        {
            _audio.Pause();
            UpdatePlayPauseIcon();
            UpdateMediaControls();
        }
        else
        {
            _audio.Resume();
            UpdatePlayPauseIcon();
            UpdateMediaControls();
        }
    }

    private void OnPreviousClicked(object? sender, RoutedEventArgs e)
    {
        PlayPrevious();
    }

    private void PlayPrevious()
    {
        if (_filteredTracks.Count == 0)
            return;

        _ = PlayTrackAtAsync(Math.Max(0, _currentIndex - 1));
    }

    private void OnNextClicked(object? sender, RoutedEventArgs e)
    {
        PlayNext(isAutomaticTransition: false);
    }

    private void PlayNext(bool isAutomaticTransition = false)
    {
        if (_filteredTracks.Count == 0)
            return;

        var next = _currentIndex + 1;
        if (next >= _filteredTracks.Count)
        {
            _audio.Stop();
            _currentIndex = -1;
            _currentTrackFileName = null;
            NowPlayingText.Text = "Nothing playing";
            NowPlayingCover.Source = null;
            ClearArtworkBackground();
            RefreshTrackRows();
            UpdatePlayPauseIcon();
            CompanionServices.MediaControls.Stop();
            return;
        }

        _ = PlayTrackAtAsync(next, isAutomaticTransition);
    }

    private void OnToggleSearchClicked(object? sender, RoutedEventArgs e)
    {
        SearchBox.IsVisible = !SearchBox.IsVisible;
        SearchIcon.IsVisible = !SearchBox.IsVisible;
        CloseSearchIcon.IsVisible = SearchBox.IsVisible;
        SearchButton.Opacity = SearchBox.IsVisible || !string.IsNullOrWhiteSpace(SearchBox.Text) ? 1 : 0.72;
        ToolTip.SetTip(SearchButton, SearchBox.IsVisible ? "Close search" : "Search");

        if (SearchBox.IsVisible)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
        else
        {
            TrackList.Focus();
        }
    }

    private void OnToggleFiltersClicked(object? sender, RoutedEventArgs e)
    {
        FilterDrawer.IsVisible = !FilterDrawer.IsVisible;
    }

    private async void OnChooseLibraryFolderClicked(object? sender, RoutedEventArgs e)
    {
        if (await _libraryStorage.SelectLibraryAsync())
            await LoadLibraryAsync();
    }

    private void OnClearFiltersClicked(object? sender, RoutedEventArgs e)
    {
        _activeLibrarySource = MobileLibrarySource.Default;
        _activeLibrarySourceId = null;
        ApplyDefaultRatingFilter();
        RebuildLibrarySourceRows();
        ApplyFilter();
    }

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

    private void SetRatingFilterMode(bool manual)
    {
        _manualRatingFilter = manual;
        _selectedRatings.Clear();
        if (!manual)
            _selectedRatings.UnionWith(_loadedLibrary.Library.Ratings);
        RefreshRatingFilterControls();
        ApplyFilter();
    }

    private void RefreshRatingFilterControls()
    {
        RatingButtonsPanel.IsVisible = _manualRatingFilter;
        if (RatingModeIndicator.RenderTransform is TranslateTransform transform)
            transform.X = _manualRatingFilter ? 69 : 0;
        RatingModeIndicator.CornerRadius = new CornerRadius(0);
        AllRatingsText.Foreground = CompanionTheme.Brush(_manualRatingFilter
            ? "Mobile.Brush.TextMuted"
            : "Mobile.Brush.TextStrong");
        ManualRatingsText.Foreground = CompanionTheme.Brush(_manualRatingFilter
            ? "Mobile.Brush.TextStrong"
            : "Mobile.Brush.TextMuted");

        RatingButtonsPanel.Children.Clear();
        if (!_manualRatingFilter)
            return;

        foreach (var rating in _loadedLibrary.Library.RatingDefinitions
                     ?.OrderBy(item => item.SortOrder)
                     .Select(item => item.Name)
                 ?? _loadedLibrary.Library.Ratings)
        {
            var selected = _selectedRatings.Contains(rating);
            var accent = RatingAccentColor(rating);
            var button = new Button
            {
                Content = rating,
                Height = 28,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                FontFamily = new FontFamily("Consolas, monospace"),
                Background = selected
                    ? new SolidColorBrush(Color.FromArgb(76, accent.R, accent.G, accent.B))
                    : CompanionTheme.Brush("Mobile.Brush.Surface"),
                BorderBrush = selected
                    ? new SolidColorBrush(Color.FromArgb(210, accent.R, accent.G, accent.B))
                    : CompanionTheme.Brush("Mobile.Brush.BorderSubtle"),
                Foreground = selected
                    ? new SolidColorBrush(RatingForegroundColor(rating))
                    : CompanionTheme.Brush("Mobile.Brush.TextMuted"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(1),
                Opacity = selected ? 1 : 0.62
            };
            button.Click += (_, _) =>
            {
                if (!_selectedRatings.Add(rating))
                    _selectedRatings.Remove(rating);
                RefreshRatingFilterControls();
                ApplyFilter();
            };
            RatingButtonsPanel.Children.Add(button);
        }
    }

    private static Color RatingAccentColor(string ratingName) => ratingName switch
    {
        "Timeless" => Color.FromRgb(235, 194, 83),
        "Amazing" => Color.FromRgb(220, 145, 82),
        "Great" => Color.FromRgb(83, 190, 108),
        "Good" => Color.FromRgb(71, 177, 150),
        "Okay" => Color.FromRgb(151, 156, 116),
        "Avoid" => Color.FromRgb(211, 78, 65),
        _ => Color.FromRgb(205, 148, 67)
    };

    private static Color RatingForegroundColor(string ratingName) => ratingName switch
    {
        "Timeless" => Color.FromRgb(255, 230, 150),
        "Amazing" => Color.FromRgb(247, 195, 132),
        "Great" => Color.FromRgb(188, 242, 185),
        "Good" => Color.FromRgb(176, 232, 212),
        "Okay" => Color.FromRgb(226, 224, 194),
        "Avoid" => Color.FromRgb(246, 175, 160),
        _ => Color.FromRgb(243, 203, 128)
    };

    private Bitmap? LoadThumbnail(PortableTrack track)
    {
        var key = track.FileName;
        if (_thumbnailCache.TryGetValue(key, out var cached))
            return cached;

        Bitmap? bitmap = null;
        try
        {
            if (track.Thumbnail is { Length: > 0 } thumbnail)
            {
                using var stream = new MemoryStream(thumbnail, writable: false);
                bitmap = Bitmap.DecodeToWidth(stream, 96, BitmapInterpolationMode.MediumQuality);
            }
            else if (_libraryStorage.OpenCover(_loadedLibrary, track) is { } coverStream)
            {
                using (coverStream)
                    bitmap = Bitmap.DecodeToWidth(coverStream, 96, BitmapInterpolationMode.MediumQuality);
            }
        }
        catch
        {
            bitmap = null;
        }

        AddThumbnailToCache(key, bitmap);
        return bitmap;
    }

    private void AddThumbnailToCache(string key, Bitmap? bitmap)
    {
        while (_thumbnailCache.Count >= ThumbnailCacheCapacity && _thumbnailCacheOrder.TryDequeue(out var oldestKey))
        {
            if (_thumbnailCache.Remove(oldestKey, out var oldest))
                oldest?.Dispose();
        }

        _thumbnailCache[key] = bitmap;
        _thumbnailCacheOrder.Enqueue(key);
    }

    private void ClearThumbnailCache()
    {
        foreach (var cover in _thumbnailCache.Values)
            cover?.Dispose();

        _thumbnailCache.Clear();
        _thumbnailCacheOrder.Clear();
    }

    private void UpdateArtwork(PortableTrack track, bool isAutomaticTransition)
    {
        if (string.Equals(_activeArtworkFileName, track.FileName, StringComparison.OrdinalIgnoreCase))
        {
            NowPlayingCover.Source = _activeArtwork;
            return;
        }

        PrepareOutgoingArtwork();
        var loaded = LoadArtwork(track);
        _activeArtwork = loaded.Bitmap;
        _activeArtworkFileName = track.FileName;
        _activeAmbientPalette = loaded.Palette;

        AppArtworkBackground.Source = _activeArtwork;
        AppArtworkBackground.IsVisible = _activeArtwork is not null;
        AppArtworkBackground.Opacity = 0;
        NowPlayingCover.Source = _activeArtwork;

        _artworkTransitionDuration = isAutomaticTransition
            ? AutomaticArtworkTransitionDuration
            : ManualArtworkTransitionDuration;
        _artworkTransitionProgress = _fadedArtwork is null ? 1 : 0;
        _artworkTransitionStartedAt = DateTimeOffset.UtcNow;
        ApplyArtworkTransitionFrame();

        if (_fadedArtwork is not null)
            _artworkTimer.Start();
    }

    private void PrepareOutgoingArtwork()
    {
        AppArtworkPreviousBackground.Source = null;
        _fadedArtwork?.Dispose();

        _fadedArtwork = _activeArtwork;
        _activeArtwork = null;
        _fadedAmbientPalette = _activeAmbientPalette;

        AppArtworkPreviousBackground.Source = _fadedArtwork;
        AppArtworkPreviousBackground.IsVisible = _fadedArtwork is not null;
        AppArtworkPreviousBackground.Opacity = AppArtworkBackground.Opacity;
        AppArtworkBackground.Source = null;
        AppArtworkBackground.IsVisible = false;
        AppArtworkBackground.Opacity = 0;
    }

    private LoadedArtwork LoadArtwork(PortableTrack track)
    {
        try
        {
            Bitmap? bitmap = null;
            if (_libraryStorage.OpenCover(_loadedLibrary, track) is { } coverStream)
            {
                using (coverStream)
                    bitmap = Bitmap.DecodeToWidth(coverStream, 720, BitmapInterpolationMode.MediumQuality);
            }
            else if (track.Thumbnail is { Length: > 0 } thumbnail)
            {
                using var thumbnailStream = new MemoryStream(thumbnail, writable: false);
                bitmap = Bitmap.DecodeToWidth(thumbnailStream, 480, BitmapInterpolationMode.MediumQuality);
            }

            return new LoadedArtwork(bitmap, ExtractAmbientPalette(track.Thumbnail));
        }
        catch
        {
            return new LoadedArtwork(null, ExtractAmbientPalette(track.Thumbnail));
        }
    }

    private void UpdateArtworkTransition()
    {
        if (_fadedArtwork is null)
        {
            _artworkTimer.Stop();
            return;
        }

        _artworkTransitionProgress = Math.Clamp(
            (DateTimeOffset.UtcNow - _artworkTransitionStartedAt).TotalMilliseconds
            / _artworkTransitionDuration.TotalMilliseconds,
            0,
            1);
        ApplyArtworkTransitionFrame();

        if (_artworkTransitionProgress < 1)
            return;

        AppArtworkPreviousBackground.Source = null;
        AppArtworkPreviousBackground.IsVisible = false;
        _fadedArtwork.Dispose();
        _fadedArtwork = null;
        _artworkTimer.Stop();
    }

    private void ApplyArtworkTransitionFrame()
    {
        var progress = SmoothStep(_artworkTransitionProgress);
        var incoming = _fadedArtwork is null ? 1 : Math.Sin(progress * Math.PI / 2);
        var outgoing = _fadedArtwork is null ? 0 : Math.Cos(progress * Math.PI / 2);

        AppArtworkBackground.Opacity = AppArtworkBackground.IsVisible ? 0.46 * incoming : 0;
        AppArtworkPreviousBackground.Opacity = AppArtworkPreviousBackground.IsVisible ? 0.46 * outgoing : 0;
        var palette = new AmbientPalette(
            MixColor(_fadedAmbientPalette.Primary, _activeAmbientPalette.Primary, progress),
            MixColor(_fadedAmbientPalette.Secondary, _activeAmbientPalette.Secondary, progress));
        var appStops = ((LinearGradientBrush)AppAtmosphereTint.Background!).GradientStops;
        appStops[0].Color = WithAlpha(palette.Primary, 72);
        appStops[2].Color = WithAlpha(palette.Secondary, 56);
    }

    private void ClearArtworkBackground()
    {
        _artworkTimer.Stop();
        NowPlayingCover.Source = null;
        AppArtworkBackground.Source = null;
        AppArtworkBackground.IsVisible = false;
        AppArtworkPreviousBackground.Source = null;
        AppArtworkPreviousBackground.IsVisible = false;
        _activeArtwork?.Dispose();
        _fadedArtwork?.Dispose();
        _activeArtwork = null;
        _fadedArtwork = null;
        _activeArtworkFileName = null;
        _artworkTransitionProgress = 1;
    }

    private static AmbientPalette ExtractAmbientPalette(byte[]? thumbnail)
    {
        if (thumbnail is not { Length: > 0 })
            return DefaultAmbientPalette;

        try
        {
            using var bitmap = SKBitmap.Decode(thumbnail);
            if (bitmap is null || bitmap.Width == 0 || bitmap.Height == 0)
                return DefaultAmbientPalette;

            double red = 0, green = 0, blue = 0, weightTotal = 0;
            var step = Math.Max(1, Math.Min(bitmap.Width, bitmap.Height) / 24);
            for (var y = step / 2; y < bitmap.Height; y += step)
            for (var x = step / 2; x < bitmap.Width; x += step)
            {
                var color = bitmap.GetPixel(x, y);
                color.ToHsl(out _, out var sampleSaturation, out var lightness);
                if (color.Alpha < 150 || lightness < 8 || lightness > 90)
                    continue;

                var weight = 0.25 + sampleSaturation / 100d;
                red += color.Red * weight;
                green += color.Green * weight;
                blue += color.Blue * weight;
                weightTotal += weight;
            }

            if (weightTotal <= 0)
                return DefaultAmbientPalette;

            var average = new SKColor(
                (byte)Math.Clamp(red / weightTotal, 0, 255),
                (byte)Math.Clamp(green / weightTotal, 0, 255),
                (byte)Math.Clamp(blue / weightTotal, 0, 255));
            average.ToHsl(out var hue, out var averageSaturation, out _);
            var primary = SKColor.FromHsl(hue, Math.Clamp(averageSaturation * 1.28f, 58, 84), 50);
            var secondary = SKColor.FromHsl((hue + 48) % 360, Math.Clamp(averageSaturation * 1.12f, 48, 76), 43);
            return new AmbientPalette(
                Color.FromRgb(primary.Red, primary.Green, primary.Blue),
                Color.FromRgb(secondary.Red, secondary.Green, secondary.Blue));
        }
        catch
        {
            return DefaultAmbientPalette;
        }
    }

    private static double SmoothStep(double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        return progress * progress * (3 - 2 * progress);
    }

    private static Color MixColor(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(from.R + (to.R - from.R) * amount),
            (byte)Math.Round(from.G + (to.G - from.G) * amount),
            (byte)Math.Round(from.B + (to.B - from.B) * amount));
    }

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    private sealed record LoadedArtwork(Bitmap? Bitmap, AmbientPalette Palette);
    private sealed record AmbientPalette(Color Primary, Color Secondary);

    private async void OnShuffleClicked(object? sender, RoutedEventArgs e)
    {
        _shuffle = !_shuffle;
        ShuffleButton.Opacity = _shuffle ? 1.0 : 0.45;
        ToolTip.SetTip(ShuffleButton, _shuffle ? "Shuffle: On" : "Shuffle: Off");
        ApplyFilter();

        if (_filteredTracks.Count > 0)
            await PlayTrackAtAsync(0);
    }

    private async void ShowToast(string message)
    {
        _toastCts?.Cancel();
        _toastCts = new CancellationTokenSource();
        var token = _toastCts.Token;

        ToastText.Text = message;
        Toast.IsVisible = true;

        try
        {
            await Task.Delay(2400, token);
            if (!token.IsCancellationRequested)
                Toast.IsVisible = false;
        }
        catch (TaskCanceledException)
        {
        }
    }

    private void OnProgressPressed(object? sender, PointerPressedEventArgs e)
    {
        _isSeeking = true;
        e.Pointer.Capture(ProgressSlider);
        SeekFromPointer(e, commit: true);
        e.Handled = true;
    }

    private void OnProgressMoved(object? sender, PointerEventArgs e)
    {
        if (!_isSeeking)
            return;

        SeekFromPointer(e, commit: false);
        e.Handled = true;
    }

    private void OnProgressReleased(object? sender, PointerReleasedEventArgs e)
    {
        SeekFromPointer(e, commit: true);
        e.Pointer.Capture(null);
        _isSeeking = false;
        e.Handled = true;
    }

    private void SeekFromPointer(PointerEventArgs e, bool commit)
    {
        if (_audio.Duration.TotalSeconds <= 0 || ProgressSlider.Bounds.Width <= 0)
            return;

        var fraction = Math.Clamp(e.GetPosition(ProgressSlider).X / ProgressSlider.Bounds.Width, 0, 1);
        ProgressSlider.Value = fraction * 100;
        var position = TimeSpan.FromSeconds(_audio.Duration.TotalSeconds * fraction);
        if (commit)
            SeekTo(position);
        TimeText.Text = $"{Format(position)} / {Format(_audio.Duration)}";
    }

    private void SeekTo(TimeSpan position)
    {
        _audio.Seek(position);
        UpdatePlaybackUi();
        UpdateMediaControls();
    }

    private void UpdatePlaybackUi()
    {
        if (!_isSeeking && _audio.Duration.TotalSeconds > 0)
            ProgressSlider.Value = _audio.Position.TotalSeconds / _audio.Duration.TotalSeconds * 100.0;

        TimeText.Text = $"{Format(_audio.Position)} / {Format(_audio.Duration)}";
        if (CurrentTrack is not null)
        {
            UpdatePlayPauseIcon();
            if (_audio.IsPlaying && DateTime.UtcNow - _lastMediaUpdate > TimeSpan.FromSeconds(4))
                UpdateMediaControls();
        }

        UpdatePlaylistSummary();
    }

    private static string Format(TimeSpan time) =>
        $"{(int)time.TotalMinutes:D2}:{time.Seconds:D2}";

    private void UpdatePlaylistSummary()
    {
        var totalSeconds = _filteredTracks
            .Select(track => track.DurationSeconds ?? 0)
            .Sum();

        LibraryContextTitleText.Text = _activeLibrarySource switch
        {
            MobileLibrarySource.Preset => $"PRESET · {_activeLibrarySourceId?.ToUpperInvariant()}",
            MobileLibrarySource.Collection => $"COLLECTION · {(_loadedLibrary.Library.Collections ?? [])
                .FirstOrDefault(item => string.Equals(item.StableId, _activeLibrarySourceId, StringComparison.OrdinalIgnoreCase))
                ?.Name.ToUpperInvariant()}",
            _ => "LIBRARY"
        };
        PlaylistSummaryText.Text = $"{_filteredTracks.Count} tracks · {FormatPlaylistDuration(totalSeconds)}";
    }

    private static string FormatPlaylistDuration(int totalSeconds)
    {
        var time = TimeSpan.FromSeconds(totalSeconds);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:D2}h"
            : $"{time.Minutes}:{time.Seconds:D2}m";
    }

    private void SetStatus(string? message = null)
    {
        StatusText.Text = message ?? string.Empty;
        StatusText.IsVisible = !string.IsNullOrWhiteSpace(message);
    }

    private void ShuffleFilteredTracks()
    {
        for (var i = _filteredTracks.Count - 1; i > 0; i--)
        {
            var j = _rng.Next(i + 1);
            (_filteredTracks[i], _filteredTracks[j]) = (_filteredTracks[j], _filteredTracks[i]);
        }
    }

    private void UpdatePlayPauseIcon()
    {
        PlayIcon.IsVisible = !_audio.IsPlaying;
        PauseIcon.IsVisible = _audio.IsPlaying;
    }

    private void UpdateMediaControls()
    {
        var track = CurrentTrack;
        if (track is null)
        {
            CompanionServices.MediaControls.Stop();
            return;
        }

        CompanionServices.MediaControls.Update(
            track.Title,
            _libraryStorage.CoverSource(_loadedLibrary, track),
            _audio.IsPlaying,
            _audio.Position,
            _audio.Duration);
        _lastMediaUpdate = DateTime.UtcNow;
    }

    private PortableTrack? CurrentTrack => string.IsNullOrWhiteSpace(_currentTrackFileName)
        ? null
        : _loadedLibrary.Library.Tracks.FirstOrDefault(track => string.Equals(
            track.FileName,
            _currentTrackFileName,
            StringComparison.OrdinalIgnoreCase));

    private void OnMediaCommandRequested(MediaControlCommand command)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            switch (command)
            {
                case MediaControlCommand.Previous:
                    PlayPrevious();
                    break;
                case MediaControlCommand.PlayPause:
                    await TogglePlaybackAsync();
                    break;
                case MediaControlCommand.Play:
                    if (!_audio.IsPlaying)
                    {
                        _audio.Resume();
                        UpdatePlayPauseIcon();
                        UpdateMediaControls();
                    }
                    break;
                case MediaControlCommand.Pause:
                    if (_audio.IsPlaying)
                    {
                        _audio.Pause();
                        UpdatePlayPauseIcon();
                        UpdateMediaControls();
                    }
                    break;
                case MediaControlCommand.Next:
                    PlayNext();
                    break;
            }
        });
    }

    private void OnMediaSeekRequested(TimeSpan position)
    {
        Dispatcher.UIThread.Post(() => SeekTo(position));
    }
}
