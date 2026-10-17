using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Resona.Models;
using Resona.Services;

namespace Resona.Views;

public partial class EditTrackOverlay : UserControl
{
    private sealed record RatingButtonVisual(
        Rating Rating,
        Button Button,
        TextBlock NameText);
    private MusicTrack? _track;
    private List<Tag> _tags = [];
    private List<Rating> _ratings = [];
    private List<Style> _styles = [];

    private readonly List<(Tag Tag, ToggleButton Btn)> _tagChips = [];
    private readonly List<(TrackLanguage Language, ToggleButton Btn)> _languageChips = [];
    private readonly List<(Style Style, ToggleButton Btn)> _styleChips = [];
    private readonly List<RatingButtonVisual> _ratingButtons = [];

    private HashSet<int> _modelGenreIds = [];
    private Dictionary<int, ModelSubgenre> _modelSubgenresById = [];
    private Dictionary<int, string> _modelGenreNamesById = [];
    private Dictionary<int, List<ModelSubgenreDistinction>> _distinctionsBySubgenreId = [];
    private List<StoredModelGenrePrediction> _trackGenrePredictions = [];
    private bool _areDetectedGenresExpanded;
    private bool _areFrequentManualGenresExpanded;
    private bool _areAllGenresExpanded;
    private bool _areLanguagesExpanded;
    private bool _updatingLanguageSelection;
    private int? _modelGenreFilterId;
    private string _modelGenreSearchText = string.Empty;
    private readonly DispatcherTimer _analysisElapsedTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _analysisStartedAt;
    private bool _isPlayingPreview;
    private bool _loadingTrack;
    private bool _isPublic;
    private bool _initialIsPublic;
    private string _initialTitle = string.Empty;
    private string? _initialArtist;
    private bool _isEditingInformation;
    private int _openGeneration;
    private bool _isDeletingTrack;
    private bool _isOpen;
    private bool _lookupsLoaded;
    private int? _preparedTrackId;
    private int? _selectedRatingId;
    private int? _initialRatingId;
    private RatingBand? _selectedRatingBand;
    private RatingBand? _initialRatingBand;
    private string? _initialLanguageCode;
    private HashSet<int> _initialTagIds = [];
    private HashSet<int> _initialStyleIds = [];
    private HashSet<int> _initialEnabledModelGenreIds = [];
    private HashSet<int> _pendingEnabledModelGenreIds = [];

    public event Action<int>? TrackSaved;
    public event Action? CollectionsChanged;
    public event Action<MusicTrack>? PreviewRequested;
    public event Action? PreviewClosed;
    public event Action<string>? ToastRequested;
    public event Func<MusicTrack, Task<bool>>? DeleteRequested;
    public event Action? Closed;

    public bool IsOpen => _isOpen;
    public bool IsPreparedFor(int trackId) => _preparedTrackId == trackId && _track?.Id == trackId;

    public EditTrackOverlay()
    {
        InitializeComponent();
        TitleBox.LostFocus += (_, _) => CommitInformationEdit();
        ArtistBox.LostFocus += (_, _) => CommitInformationEdit();
        _analysisElapsedTimer.Tick += (_, _) => UpdateAnalysisElapsedTime();
    }

    public void SetAtmosphereColors(Color primary, Color secondary)
    {
        if (EditorAtmosphereTint.Fill is not LinearGradientBrush gradient
            || gradient.GradientStops.Count < 2)
            return;

        gradient.GradientStops[0].Color = primary;
        gradient.GradientStops[1].Color = secondary;
    }

    public void Open(MusicTrack track, bool analyzeAfterOpening = false)
    {
        var openGeneration = ++_openGeneration;
        _isOpen = true;
        Opacity = 1;
        EditorSurface.Opacity = 1;
        IsVisible = true;
        IsHitTestVisible = true;

        if (IsPreparedFor(track.Id))
        {
            if (analyzeAfterOpening)
                _ = AnalyzeAfterOpeningAsync(track, openGeneration);
            return;
        }

        _loadingTrack = true;
        Dispatcher.UIThread.Post(
            () => LoadTrackAfterOpening(track, analyzeAfterOpening, openGeneration),
            DispatcherPriority.Background);
    }

    public void Prepare(MusicTrack track)
    {
        if (_isOpen)
            return;

        PrepareContent(track);
        IsHitTestVisible = false;
        IsVisible = false;
    }

    public void InvalidatePreparedTrack()
    {
        if (_isOpen)
            return;

        _preparedTrackId = null;
        _track = null;
        IsVisible = false;
    }

    public void InvalidateLookups()
    {
        _lookupsLoaded = false;
        _modelSubgenresById.Clear();
        _modelGenreNamesById.Clear();
        _distinctionsBySubgenreId.Clear();
        InvalidatePreparedTrack();
    }

    private void PrepareContent(MusicTrack track)
    {
        _track = track;
        _isPlayingPreview = false;
        _isDeletingTrack = false;
        DeleteButton.IsEnabled = true;
        _areDetectedGenresExpanded = false;
        _areFrequentManualGenresExpanded = false;
        _areAllGenresExpanded = false;
        _areLanguagesExpanded = false;
        LoadLookups();
        Prefill(track);
        _preparedTrackId = track.Id;
    }

    private void LoadTrackAfterOpening(MusicTrack track, bool analyzeAfterOpening, int openGeneration)
    {
        if (openGeneration != _openGeneration || !_isOpen)
            return;

        PrepareContent(track);
        if (analyzeAfterOpening)
            _ = AnalyzeImportedTrackAsync(track);
    }

    private async Task AnalyzeAfterOpeningAsync(MusicTrack track, int openGeneration)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        if (openGeneration == _openGeneration && _isOpen)
            await AnalyzeImportedTrackAsync(track);
    }

    public void RequestClose() => CloseOverlay();

    private void LoadLookups()
    {
        if (_lookupsLoaded)
            return;

        _tags = MusicLibraryService.Current.GetTags();
        _ratings = MusicLibraryService.Current.GetRatings();
        _styles = MusicLibraryService.Current.GetStyles();

        BuildRatingButtons();
        _lookupsLoaded = true;
    }

    private void Prefill(MusicTrack track)
    {
        _loadingTrack = true;
        TitleBox.Text = track.Title;
        ArtistBox.Text = track.Artist;
        SetInformationEditing(false);
        UpdateInformationDisplay(track);
        SetPublicSelection(track.IsPublic);
        UpdateReviewVisual(track.NeedsReview);
        UpdateAnalysisPolicyVisual(track.AnalysisDisabled);

        _selectedRatingId = track.RatingId;
        _selectedRatingBand = track.RatingBand;
        UpdateRatingVisual();
        UpdateRatingBandVisual();

        var selectedTagIds = MusicLibraryService.Current.GetTrackTagIds(track.Id).ToHashSet();
        var selectedStyleIds = MusicLibraryService.Current.GetTrackStyleIds(track.Id).ToHashSet();
        var selectedModelGenreIds = ResetModelGenreSelectionFromDatabase(track);
        _trackGenrePredictions = MusicLibraryService.Current.GetTrackGenrePredictions(track.Id);

        ShowModelSelectedGenres(track);
        ShowDetectedGenres(track);
        RebuildModelGenreChoices();
        BuildFrequentManualGenreChoices();
        RebuildStyleChips(selectedStyleIds);
        RebuildTagChips(selectedTagIds);
        RebuildLanguageChips(track.LanguageCode);
        RebuildCollectionMemberships(track);
        ShowAudioAnalysis(track);
        ShowExperimentalAnalysis(track);
        ShowUsageStats(track);
        CaptureChangeSnapshot(track, selectedTagIds, selectedStyleIds, selectedModelGenreIds);
        _loadingTrack = false;
    }

    public void RefreshUsageStats()
    {
        if (_track is not null)
            ShowUsageStats(_track);
    }

    private void ShowUsageStats(MusicTrack track)
    {
        var usage = MusicLibraryService.Current.GetTrackUsageStats(track.Id);
        var listened = usage.ListenedSeconds >= 60
            ? $"{usage.ListenedSeconds / 60} min listened"
            : $"{usage.ListenedSeconds} sec listened";
        TrackUsageText.Text = $"{usage.PlayCount} plays  ·  {listened}  ·  {usage.SkipCount} skips";
    }

    private void OnEditInformationClicked(object? sender, RoutedEventArgs e)
    {
        if (_isEditingInformation)
            CommitInformationEdit();
        SetInformationEditing(!_isEditingInformation);
    }

    public void RefreshCollections()
    {
        if (_track is not null)
            RebuildCollectionMemberships(_track);
    }

    private void RebuildCollectionMemberships(MusicTrack track)
    {
        CollectionMembershipPanel.Children.Clear();
        var collections = MusicLibraryService.Current.GetTrackCollections(track.Id);
        CollectionMembershipSection.IsVisible = collections.Count > 0;
        foreach (var collection in collections)
        {
            var label = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,Auto"),
                ColumnSpacing = 7,
                VerticalAlignment = VerticalAlignment.Center
            };
            label.Children.Add(new TextBlock
            {
                Text = collection.Name,
                FontSize = 10.5,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            var removeIcon = new TextBlock
            {
                Text = "×",
                FontSize = 14,
                Opacity = 0.62,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(removeIcon, 1);
            label.Children.Add(removeIcon);
            var removeButton = new Button
            {
                Content = label,
                Height = 26,
                Padding = new Thickness(8, 2),
                Margin = new Thickness(0, 0, 6, 4),
                Background = ThemeResources.Brush("Theme.Brush.Surface"),
                BorderBrush = ThemeResources.Brush("Theme.Brush.BorderSubtle"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            ToolTip.SetTip(removeButton, $"Remove from {collection.Name}");
            removeButton.Click += (_, _) =>
            {
                try
                {
                    if (!MusicLibraryService.Current.RemoveTrackFromCollection(collection.Id, track.Id))
                        return;
                    RebuildCollectionMemberships(track);
                    CollectionsChanged?.Invoke();
                    ToastRequested?.Invoke($"Removed from {collection.Name}");
                }
                catch (Exception exception)
                {
                    ToastRequested?.Invoke($"Could not update collection: {exception.Message}");
                }
            };
            CollectionMembershipPanel.Children.Add(removeButton);
        }
    }

    private void CommitInformationEdit()
    {
        if (_track is null || _loadingTrack)
            return;

        if (string.IsNullOrWhiteSpace(TitleBox.Text))
            TitleBox.Text = _track.Title;
        AutoSaveChanges();
        UpdateInformationDisplay(_track);
    }

    private void SetInformationEditing(bool isEditing)
    {
        _isEditingInformation = isEditing;
        TitleBox.IsVisible = isEditing;
        ArtistBox.IsVisible = isEditing;
        TitleDisplayText.IsVisible = !isEditing;
        ArtistDisplayText.IsVisible = !isEditing;
        EditInformationButton.Background = isEditing
            ? ThemeResources.Brush("Theme.Brush.AccentSurface")
            : Brushes.Transparent;
        ToolTip.SetTip(EditInformationButton, isEditing ? "Finish editing information" : "Edit information");
        if (isEditing)
            TitleBox.Focus();
    }

    private void UpdateInformationDisplay(MusicTrack track)
    {
        var originalTitle = OriginalTitle(track);
        ArtistDisplayText.Text = DisplayValue(ArtistBox.Text);
        TitleDisplayText.Text = DisplayValue(TitleBox.Text);
        OriginalTitleDisplayText.Text = DisplayValue(originalTitle);
        ChannelDisplayText.Text = DisplayValue(track.DisplayChannelName);
        YouTubeUrlDisplayText.Text = DisplayValue(track.CanonicalUrl);
        ChannelUrlDisplayText.Text = DisplayValue(track.ChannelUrl);
        UploadedDisplayText.Text = FormatMetadataDate(track.UploadedAt);
        YouTubeActivityDisplayText.Text = track.ViewCount is null && track.LikeCount is null
            ? "—"
            : $"{FormatMetric(track.ViewCount)} views  ·  {FormatMetric(track.LikeCount)} likes";
        MetadataUpdatedDisplayText.Text = FormatMetadataDate(track.SourceMetadataUpdatedAt, includeTime: true);
        CopyYouTubeUrlButton.IsEnabled = !string.IsNullOrWhiteSpace(track.CanonicalUrl);
        CopyChannelUrlButton.IsEnabled = !string.IsNullOrWhiteSpace(track.ChannelUrl);
        CopyOriginalTitleButton.IsEnabled = !string.IsNullOrWhiteSpace(originalTitle);
        ToolTip.SetTip(TitleDisplayText, TitleBox.Text);
        ToolTip.SetTip(ArtistDisplayText, ArtistBox.Text);
        ToolTip.SetTip(OriginalTitleDisplayText, originalTitle);
        ToolTip.SetTip(ChannelDisplayText, track.DisplayChannelName);
        ToolTip.SetTip(YouTubeUrlDisplayText, track.CanonicalUrl);
        ToolTip.SetTip(ChannelUrlDisplayText, track.ChannelUrl);
    }

    private static string DisplayValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

    private static string FormatMetric(long? value) => value is long number
        ? number.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)
        : "—";

    private static string FormatMetadataDate(string? value, bool includeTime = false)
    {
        if (!DateTime.TryParse(value, out var parsed))
            return "—";
        var local = parsed.ToLocalTime();
        return includeTime ? local.ToString("dd MMM yyyy · HH:mm") : local.ToString("dd MMM yyyy");
    }

    private async void OnCopyYouTubeUrlClicked(object? sender, RoutedEventArgs e) =>
        await CopyUrlAsync(_track?.CanonicalUrl, "YouTube URL");

    private async void OnCopyChannelUrlClicked(object? sender, RoutedEventArgs e) =>
        await CopyUrlAsync(_track?.ChannelUrl, "Channel URL");

    private async void OnCopyOriginalTitleClicked(object? sender, RoutedEventArgs e) =>
        await CopyUrlAsync(_track is null ? null : OriginalTitle(_track), "Original title");

    private static string OriginalTitle(MusicTrack track) =>
        string.IsNullOrWhiteSpace(track.OriginalTitle) ? track.Title : track.OriginalTitle;

    private async System.Threading.Tasks.Task CopyUrlAsync(string? url, string label)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            ToastRequested?.Invoke($"No {label.ToLowerInvariant()} available");
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            ToastRequested?.Invoke("Clipboard is not available");
            return;
        }

        await clipboard.SetTextAsync(url.Trim());
        ToastRequested?.Invoke($"{label} copied");
    }

    private static ToggleButton CreateStyleButton(string style, bool isSelected)
    {
        var label = new TextBlock
        {
            Text = style,
            FontSize = 10.5,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };
        var button = new ToggleButton
        {
            Content = label,
            IsChecked = isSelected,
            Height = 34,
            Padding = new Thickness(8, 3),
            CornerRadius = new CornerRadius(5),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Tag = label
        };
        button.Classes.Add("edit-chip");
        ApplyTagVisual(button);
        return button;
    }

    private void RebuildTagChips(IReadOnlySet<int> selectedTagIds)
    {
        TagsPanel.Children.Clear();
        _tagChips.Clear();

        var tags = _tags
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
                var btn = CreateTagButton(tag, selectedTagIds.Contains(tag.Id));
                btn.IsCheckedChanged += (_, _) =>
                {
                    ApplyTagVisual(btn);
                    AutoSaveChanges();
                };

                _tagChips.Add((tag, btn));
                Grid.SetColumn(btn, column);
                row.Children.Add(btn);
            }

            rows.Children.Add(row);
        }

        TagsPanel.Children.Add(rows);
    }

    private static ToggleButton CreateTagButton(Tag tag, bool isSelected)
    {
        var label = new TextBlock
        {
            Text = tag.Name,
            FontSize = 10.5,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };

        var button = new ToggleButton
        {
            Content = label,
            IsChecked = isSelected,
            Height = 34,
            Padding = new Avalonia.Thickness(9, 3),
            CornerRadius = new Avalonia.CornerRadius(5),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Tag = label
        };
        button.Classes.Add("edit-chip");
        ApplyTagVisual(button);
        return button;
    }

    private static void ApplyTagVisual(ToggleButton button)
    {
        var selected = button.IsChecked == true;
        button.Background = selected
            ? new SolidColorBrush(Color.FromArgb(46, 255, 255, 255))
            : Brushes.Transparent;
        button.BorderBrush = new SolidColorBrush(selected
            ? Color.FromArgb(160, 255, 255, 255)
            : Color.FromArgb(52, 255, 255, 255));
        button.BorderThickness = new Avalonia.Thickness(1);
        if (button.Tag is TextBlock label)
            label.Foreground = ThemeResources.Brush(selected
                ? "Theme.Brush.TextStrong"
                : "Theme.Brush.TextPrimary");
    }

    private void RebuildLanguageChips(string? selectedLanguageCode)
    {
        LanguagesPanel.Children.Clear();
        _languageChips.Clear();

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
                var button = CreateClassificationButton(
                    language.Name,
                    string.Equals(language.Code, selectedLanguageCode, StringComparison.OrdinalIgnoreCase));
                button.IsCheckedChanged += (_, _) => OnLanguageSelectionChanged(language, button);
                _languageChips.Add((language, button));
                Grid.SetColumn(button, column);
                row.Children.Add(button);
            }

            rows.Children.Add(row);
        }

        LanguagesPanel.Children.Add(rows);
        UpdateLanguageSummary();
        SetLanguagesExpanded(false);
    }

    private static ToggleButton CreateClassificationButton(string name, bool isSelected) =>
        CreateTagButton(new Tag(0, name), isSelected);

    private void OnLanguageSelectionChanged(TrackLanguage language, ToggleButton button)
    {
        if (_updatingLanguageSelection)
            return;

        _updatingLanguageSelection = true;
        try
        {
            if (button.IsChecked == true)
                foreach (var chip in _languageChips)
                    if (!string.Equals(chip.Language.Code, language.Code, StringComparison.OrdinalIgnoreCase))
                    {
                        chip.Btn.IsChecked = false;
                        ApplyTagVisual(chip.Btn);
                    }
            ApplyTagVisual(button);
        }
        finally
        {
            _updatingLanguageSelection = false;
        }

        UpdateLanguageSummary();
        AutoSaveChanges();
    }

    private string? SelectedLanguageCode() => _languageChips
        .FirstOrDefault(chip => chip.Btn.IsChecked == true)
        .Language?.Code;

    private void OnLanguagesHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        SetLanguagesExpanded(!_areLanguagesExpanded);
        e.Handled = true;
    }

    private void SetLanguagesExpanded(bool expanded)
    {
        _areLanguagesExpanded = expanded;
        LanguagesPanel.IsVisible = expanded;
        if (LanguagesChevron.RenderTransform is RotateTransform rotation)
            rotation.Angle = expanded ? 90 : 0;
    }

    private void UpdateLanguageSummary()
    {
        LanguagesSummaryText.Text = _languageChips
            .FirstOrDefault(chip => chip.Btn.IsChecked == true)
            .Language?.Name ?? "Not set";
    }

    private void ShowTagSuggestions(MusicTrack track)
    {
        SuggestedTagsSection.IsVisible = false;
        SuggestedTagsPanel.Children.Clear();
    }

    private void RebuildStyleChips(IReadOnlySet<int>? selectedStyleIds = null)
    {
        selectedStyleIds ??= _styleChips
            .Where(c => c.Btn.IsChecked == true)
            .Select(c => c.Style.Id)
            .ToHashSet();

        var sorted = _styles
            .OrderByDescending(s => selectedStyleIds.Contains(s.Id))
            .ThenBy(s => s.Name)
            .ToList();

        EditStylesPanel.Children.Clear();
        _styleChips.Clear();

        foreach (var style in sorted)
        {
            var selected = selectedStyleIds.Contains(style.Id);
            var btn = CreateStyleButton(style.Name, selected);
            btn.IsCheckedChanged += (_, _) =>
            {
                ApplyTagVisual(btn);
                AutoSaveChanges();
            };
            _styleChips.Add((style, btn));
            EditStylesPanel.Children.Add(btn);
        }
    }

    private HashSet<int> SelectedTagIds() =>
        _tagChips
            .Where(c => c.Btn.IsChecked == true)
            .Select(c => c.Tag.Id)
            .ToHashSet();

    private HashSet<int> SelectedStyleIds() =>
        _styleChips
            .Where(item => item.Btn.IsChecked == true)
            .Select(item => item.Style.Id)
            .ToHashSet();

    private int? SelectedRatingId() => _selectedRatingId;

    private void CaptureChangeSnapshot(
        MusicTrack track,
        IReadOnlySet<int> selectedTagIds,
        IReadOnlySet<int> selectedStyleIds,
        IReadOnlySet<int> selectedModelGenreIds)
    {
        _initialTitle = track.Title;
        _initialArtist = NormalizeOptionalText(track.Artist);
        _initialRatingId = track.RatingId;
        _initialRatingBand = track.RatingBand;
        _initialLanguageCode = track.LanguageCode;
        _initialIsPublic = track.IsPublic;
        _initialTagIds = selectedTagIds.ToHashSet();
        _initialStyleIds = selectedStyleIds.ToHashSet();
        _initialEnabledModelGenreIds = selectedModelGenreIds.ToHashSet();
        _pendingEnabledModelGenreIds = selectedModelGenreIds.ToHashSet();
    }

    private HashSet<int> ResetModelGenreSelectionFromDatabase(MusicTrack track)
    {
        var selectedModelGenreIds = MusicLibraryService.Current.GetTrackModelGenres(track.Id)
            .Where(assignment => assignment.IsEnabled)
            .Select(assignment => assignment.GenreId)
            .ToHashSet();
        _initialEnabledModelGenreIds = selectedModelGenreIds.ToHashSet();
        _pendingEnabledModelGenreIds = selectedModelGenreIds.ToHashSet();
        return selectedModelGenreIds;
    }

    private void UpdateRatingVisual()
    {
        var selected = _ratings.FirstOrDefault(rating => rating.Id == _selectedRatingId);

        foreach (var visual in _ratingButtons)
        {
            var accent = RatingAccentColor(visual.Rating.Name);
            var isSelected = visual.Rating.Id == _selectedRatingId;
            visual.Button.Background = new SolidColorBrush(Color.FromArgb(
                isSelected ? (byte)82 : (byte)8,
                accent.R,
                accent.G,
                accent.B));
            visual.Button.BorderBrush = new SolidColorBrush(Color.FromArgb(
                isSelected ? (byte)235 : (byte)40,
                accent.R,
                accent.G,
                accent.B));
            visual.Button.BorderThickness = new Thickness(1);
            visual.NameText.Foreground = new SolidColorBrush(Color.FromArgb(
                isSelected ? (byte)255 : (byte)145,
                accent.R,
                accent.G,
                accent.B));
            visual.Button.Opacity = isSelected ? 1 : 0.72;
        }

        if (selected is null)
        {
            RatingInformationText.Text = "Unrated";
            RatingInformationText.Foreground = new SolidColorBrush(Color.FromArgb(155, 255, 255, 255));
            ClearRatingButton.IsEnabled = false;
            ClearRatingButton.Opacity = 0.35;
            return;
        }

        var selectedColor = RatingAccentColor(selected.Name);
        RatingInformationText.Text = selected.Name;
        RatingInformationText.Foreground = new SolidColorBrush(selectedColor);
        ClearRatingButton.IsEnabled = true;
        ClearRatingButton.Opacity = 0.68;
    }

    private static Color RatingAccentColor(string ratingName) => ratingName switch
    {
        RatingNames.Avoid => Color.FromRgb(255, 75, 75),
        "Okay" => Color.FromRgb(210, 205, 95),
        "Good" => Color.FromRgb(45, 190, 240),
        "Great" => Color.FromRgb(55, 224, 105),
        RatingNames.Amazing => Color.FromRgb(255, 132, 48),
        RatingNames.Timeless => Color.FromRgb(255, 215, 64),
        _ => Color.FromRgb(190, 145, 75)
    };

    private void UpdateRatingBandVisual()
    {
        var enabled = _selectedRatingId is not null;
        RatingBandPanel.IsEnabled = enabled;
        RatingBandPanel.Opacity = enabled ? 1 : 0.25;

        UpdateRatingBandButton(RatingBandLowButton, RatingBand.Low);
        UpdateRatingBandButton(RatingBandMidButton, RatingBand.Mid);
        UpdateRatingBandButton(RatingBandHighButton, RatingBand.High);
    }

    private void UpdateRatingBandButton(Button button, RatingBand band)
    {
        var selected = _selectedRatingBand == band;
        button.Background = Brushes.Transparent;
        button.BorderThickness = new Thickness(0);
        button.Foreground = selected
            ? band switch
            {
                RatingBand.Low => new SolidColorBrush(Color.FromRgb(255, 67, 67)),
                RatingBand.High => new SolidColorBrush(Color.FromRgb(48, 235, 105)),
                _ => new SolidColorBrush(Color.FromRgb(245, 245, 238))
            }
            : new SolidColorBrush(Color.FromArgb(92, 255, 255, 255));
        button.Opacity = selected ? 1 : 0.72;
        button.Cursor = new Cursor(StandardCursorType.Hand);
    }

    private void ToggleRatingBand(RatingBand ratingBand)
    {
        if (_selectedRatingId is null)
            return;

        _selectedRatingBand = _selectedRatingBand == ratingBand ? null : ratingBand;
        UpdateRatingBandVisual();
        AutoSaveChanges();
    }

    private void OnRatingBandLowClicked(object? sender, RoutedEventArgs e) => ToggleRatingBand(RatingBand.Low);
    private void OnRatingBandMidClicked(object? sender, RoutedEventArgs e) => ToggleRatingBand(RatingBand.Mid);
    private void OnRatingBandHighClicked(object? sender, RoutedEventArgs e) => ToggleRatingBand(RatingBand.High);

    private void BuildRatingButtons()
    {
        RatingOptionsPanel.Children.Clear();
        _ratingButtons.Clear();

        var orderedRatings = _ratings.OrderBy(item => item.SortOrder).ToList();
        for (var index = 0; index < orderedRatings.Count; index++)
        {
            var rating = orderedRatings[index];
            var nameText = new TextBlock
            {
                Text = rating.Name.ToUpperInvariant(),
                FontSize = 9,
                FontWeight = FontWeight.SemiBold,
                FontFamily = new FontFamily("Inter"),
                LetterSpacing = 0.15,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center
            };
            var button = new Button
            {
                Content = nameText,
                Width = 76,
                Height = 32,
                Padding = new Thickness(5, 2),
                CornerRadius = new CornerRadius(1),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            button.Click += (_, _) =>
            {
                if (_selectedRatingId != rating.Id)
                    _selectedRatingBand = null;
                _selectedRatingId = rating.Id;
                UpdateRatingVisual();
                UpdateRatingBandVisual();
                AutoSaveChanges();
            };
            ToolTip.SetTip(button, $"Level {index + 1} — {rating.Name}");
            _ratingButtons.Add(new RatingButtonVisual(rating, button, nameText));
            RatingOptionsPanel.Children.Add(button);
        }

        UpdateRatingVisual();
        UpdateRatingBandVisual();
    }

    private void OnClearRatingClicked(object? sender, RoutedEventArgs e)
    {
        if (_selectedRatingId is null)
            return;

        _selectedRatingId = null;
        _selectedRatingBand = null;
        UpdateRatingVisual();
        UpdateRatingBandVisual();
        AutoSaveChanges();
    }

    private void OnVisibilityClicked(object? sender, RoutedEventArgs e)
    {
        SetPublicSelection(!_isPublic);
    }

    private void SetPublicSelection(bool isPublic)
    {
        _isPublic = isPublic;
        PublicVisibilityIcon.IsVisible = isPublic;
        PrivateVisibilityIcon.IsVisible = !isPublic;
        ToolTip.SetTip(VisibilityButton, isPublic
            ? "Public — click to make private"
            : "Private — click to make public");
        AutoSaveChanges();
    }

    private void OnReviewClicked(object? sender, RoutedEventArgs e)
    {
        if (_track is null || _isDeletingTrack)
            return;

        try
        {
            var needsReview = !_track.NeedsReview;
            MusicLibraryService.Current.SetTrackNeedsReview(_track.Id, needsReview);
            _track = _track with { NeedsReview = needsReview };
            UpdateReviewVisual(needsReview);
            TrackSaved?.Invoke(_track.Id);
            ToastRequested?.Invoke(needsReview ? "Marked for review" : "Review mark removed");
        }
        catch (Exception exception)
        {
            ToastRequested?.Invoke($"Could not update review mark: {exception.Message}");
        }
    }

    private void UpdateReviewVisual(bool needsReview)
    {
        ReviewButton.Opacity = needsReview ? 1 : 0.68;
        ReviewButton.Background = Brushes.Transparent;
        ReviewInactiveIcon.IsVisible = !needsReview;
        ReviewActiveIcon.IsVisible = needsReview;
        ToolTip.SetTip(ReviewButton, needsReview ? "Remove review mark" : "Mark for review");
    }

    private void OnAnalysisPolicyClicked(object? sender, RoutedEventArgs e)
    {
        if (_track is null)
            return;

        var disabled = !_track.AnalysisDisabled;
        MusicLibraryService.Current.SetTrackAnalysisDisabled(_track.Id, disabled);
        _track = _track with { AnalysisDisabled = disabled };
        UpdateAnalysisPolicyVisual(disabled);
        TrackSaved?.Invoke(_track.Id);
        ToastRequested?.Invoke(disabled
            ? "Automatic analysis permanently disabled for this track"
            : "Automatic analysis enabled for this track");
    }

    private void UpdateAnalysisPolicyVisual(bool disabled)
    {
        AnalysisPolicyButton.IsVisible = _track is not null
            && MusicLibraryService.Current.GetTrackAudioAnalysis(_track.Id) is null;
        AnalysisPolicyButton.Opacity = disabled ? 1 : 0.45;
        AnalysisPolicyButton.Background = disabled
            ? new SolidColorBrush(Color.FromArgb(40, 238, 92, 92))
            : Brushes.Transparent;
        ToolTip.SetTip(AnalysisPolicyButton, disabled
            ? "Allow automatic analysis"
            : "Disable automatic analysis permanently");
    }

    private async void OnDeleteClicked(object? sender, RoutedEventArgs e)
    {
        if (_track is null || _isDeletingTrack || DeleteRequested is null)
            return;

        var track = _track;
        _isDeletingTrack = true;
        DeleteButton.IsEnabled = false;
        try
        {
            // Continue after the routed click has completed so no hovered/clicked
            // control is torn down while Avalonia is still dispatching its event.
            await Task.Yield();
            if (!await DeleteRequested.Invoke(track))
            {
                _isDeletingTrack = false;
                DeleteButton.IsEnabled = true;
                return;
            }

            _isPlayingPreview = false;
            _isEditingInformation = false;
            _track = null;
            _preparedTrackId = null;
            CloseOverlay();
        }
        catch (Exception exception)
        {
            _isDeletingTrack = false;
            DeleteButton.IsEnabled = true;
            ToastRequested?.Invoke($"Could not delete track: {exception.Message}");
        }
    }

    private void AutoSaveChanges()
    {
        if (_track is null || _loadingTrack || string.IsNullOrWhiteSpace(TitleBox.Text))
            return;

        var title = TitleBox.Text.Trim();
        var artist = NormalizeOptionalText(ArtistBox.Text);
        var tagIds = SelectedTagIds();
        var styleIds = SelectedStyleIds().ToList();
        var languageCode = SelectedLanguageCode();
        var coreChanged = !string.Equals(title, _initialTitle, StringComparison.Ordinal)
            || !string.Equals(artist, _initialArtist, StringComparison.Ordinal)
            || SelectedRatingId() != _initialRatingId
            || _isPublic != _initialIsPublic
            || !styleIds.ToHashSet().SetEquals(_initialStyleIds);
        var ratingBandChanged = _selectedRatingBand != _initialRatingBand;
        var tagsChanged = !tagIds.SetEquals(_initialTagIds);
        var languageChanged = !string.Equals(languageCode, _initialLanguageCode, StringComparison.OrdinalIgnoreCase);
        var disabledGenreIds = _initialEnabledModelGenreIds.Except(_pendingEnabledModelGenreIds).ToList();
        var enabledGenreIds = _pendingEnabledModelGenreIds.Except(_initialEnabledModelGenreIds).ToList();
        if (!coreChanged && !ratingBandChanged && !tagsChanged && !languageChanged && disabledGenreIds.Count == 0 && enabledGenreIds.Count == 0)
            return;

        if (coreChanged)
        {
            MusicLibraryService.Current.UpdateTrack(
                _track.Id,
                title,
                artist,
                [],
                SelectedRatingId(),
                styleIds,
                _isPublic);
        }

        if (ratingBandChanged)
            MusicLibraryService.Current.SetTrackRatingBand(_track.Id, _selectedRatingBand);

        if (tagsChanged)
            MusicLibraryService.Current.SetTrackManualTags(_track.Id, tagIds);

        if (languageChanged)
            MusicLibraryService.Current.SetTrackLanguage(_track.Id, languageCode);

        foreach (var genreId in disabledGenreIds)
            MusicLibraryService.Current.SetTrackModelGenreEnabled(_track.Id, genreId, false);
        foreach (var genreId in enabledGenreIds)
            MusicLibraryService.Current.SetTrackModelGenreEnabled(_track.Id, genreId, true);

        _track = _track with
        {
            Title = title,
            Artist = artist,
            RatingId = SelectedRatingId(),
            RatingBand = _selectedRatingBand,
            LibraryState = SelectedRatingId() is null
                ? _track.LibraryState
                : TrackLibraryState.Active,
            IsPublic = _isPublic,
            LanguageCode = languageCode
        };
        CaptureChangeSnapshot(_track, tagIds, styleIds.ToHashSet(), _pendingEnabledModelGenreIds);
        TrackSaved?.Invoke(_track.Id);
    }

    private static string? NormalizeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void OnPreviewClicked(object? sender, RoutedEventArgs e)
    {
        if (_track is null) return;
        _isPlayingPreview = true;
        PreviewRequested?.Invoke(_track);
    }

    private async System.Threading.Tasks.Task AnalyzeImportedTrackAsync(MusicTrack track)
    {
        AnalysisBusyLayer.IsVisible = true;
        _analysisStartedAt = DateTime.UtcNow;
        AnalysisElapsedText.Text = "0:00";
        var filePath = System.IO.Path.Combine(Values.TracksDirectory, track.FileName);
        var fileSize = System.IO.File.Exists(filePath) ? new System.IO.FileInfo(filePath).Length : 0;
        var estimate = MusicLibraryService.Current.EstimateAnalysisDuration(track.DurationSeconds, fileSize);
        AnalysisEstimateText.Text = estimate is null
            ? "Building an estimate from completed analyses."
            : $"Typical time for similar tracks: about {FormatEstimate(estimate.Value)}";
        _analysisElapsedTimer.Start();
        var error = await MusicLibraryService.Current.AnalyzeTrackAsync(track);
        _analysisElapsedTimer.Stop();
        AnalysisBusyLayer.IsVisible = false;
        ResetModelGenreSelectionFromDatabase(track);
        _trackGenrePredictions = MusicLibraryService.Current.GetTrackGenrePredictions(track.Id);
        ShowModelSelectedGenres(track);
        ShowDetectedGenres(track);
        ShowAudioAnalysis(track);
        ShowExperimentalAnalysis(track);
    }

    private static string FormatEstimate(TimeSpan duration) => duration.TotalMinutes >= 1
        ? $"{Math.Ceiling(duration.TotalMinutes):0} min"
        : $"{Math.Max(1, Math.Round(duration.TotalSeconds)):0} sec";

    private void ShowAudioAnalysis(MusicTrack track)
    {
        var analysis = MusicLibraryService.Current.GetTrackAudioAnalysis(track.Id);
        AudioAnalysisSection.IsVisible = analysis is not null;
        if (analysis is null) return;

        BpmText.Text = analysis.Bpm is double bpm ? $"{bpm:0.#} BPM" : "—";
        BpmText.Foreground = analysis.Bpm is double bpmForColor
            ? AnalysisColorScale.Tempo(bpmForColor)
            : Brushes.White;
        IntegratedLoudnessText.Text = analysis.IntegratedLoudness is double loudness
            ? $"{loudness:0.#} LUFS"
            : "—";
        IntegratedLoudnessText.Foreground = analysis.IntegratedLoudness is double loudnessForColor
            ? AnalysisColorScale.IntegratedLoudness(loudnessForColor)
            : Brushes.White;
        LoudnessRangeText.Text = analysis.LoudnessRange is double range
            ? $"{range:0.#} LU"
            : "—";
        LoudnessRangeText.Foreground = analysis.LoudnessRange is double detectedRange
            ? AnalysisColorScale.LoudnessRange(detectedRange)
            : Brushes.White;

        var tempoInsight = analysis.Bpm is double detectedBpm
            ? GetTempoInsight(detectedBpm)
            : "Tempo could not be determined.";
        var loudnessInsight = analysis.IntegratedLoudness is double detectedLoudness
            ? GetIntegratedLoudnessInsight(detectedLoudness)
            : "Average loudness could not be determined.";
        var dynamicsInsight = analysis.LoudnessRange is double loudnessRange
            ? GetLoudnessRangeInsight(loudnessRange)
            : "Loudness variation could not be determined.";
        BpmInsightText.Text = tempoInsight;
        LoudnessInsightText.Text = loudnessInsight;
        DynamicsInsightText.Text = dynamicsInsight;
        ToolTip.SetTip(BpmMetricCard,
            $"Tempo measures the track's speed in beats per minute.\n\n{tempoInsight}");
        ToolTip.SetTip(LoudnessMetricCard,
            $"Loudness describes the track's average perceived volume.\n\n{loudnessInsight}");
        ToolTip.SetTip(DynamicsMetricCard,
            $"Dynamics describe the contrast between quiet and loud passages.\n\n{dynamicsInsight}");
    }

    private void ShowExperimentalAnalysis(MusicTrack track)
    {
        var models = MusicLibraryService.Current.GetExperimentalAnalysis(track.Id);
        var values = models.FirstOrDefault(model => model.Model == "moods mirex")?.Values
            .OrderByDescending(value => value.Score)
            .ToList() ?? [];

        EmotionalCharacterSection.IsVisible = values.Count > 0;
        EmotionalCharacterResultsPanel.Children.Clear();

        foreach (var value in values)
            EmotionalCharacterResultsPanel.Children.Add(CreateMirexCharacterRow(value));
    }

    private static Control CreateMirexCharacterRow(ExperimentalAnalysisValue value)
    {
        var brush = AnalysisColorScale.MoodModel(value.Score);
        var panel = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            RowSpacing = 4
        };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,40"), ColumnSpacing = 8 };
        var label = new TextBlock
        {
            Text = EmotionalCharacterCatalog.Display(value.Label),
            FontSize = 10.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse(EmotionalCharacterCatalog.Color(value.Label))),
            TextWrapping = TextWrapping.Wrap
        };
        ToolTip.SetTip(label, MirexExplanation(value.Label));
        row.Children.Add(label);
        var score = new TextBlock
        {
            Text = $"{value.Score * 100d:0}%",
            FontSize = 10.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = brush,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        Grid.SetColumn(score, 1);
        row.Children.Add(score);
        panel.Children.Add(row);

        var scoreBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 1,
            Value = value.Score,
            Height = 4,
            Foreground = brush,
            Background = ThemeResources.Brush("Theme.Brush.Surface")
        };
        Grid.SetRow(scoreBar, 1);
        panel.Children.Add(scoreBar);
        return panel;
    }

    private static string MirexExplanation(string label) => label switch
    {
        var text when text.StartsWith("literate", StringComparison.OrdinalIgnoreCase) =>
            "Poignant bedeutet emotional berührend; wistful bedeutet sehnsüchtig oder nostalgisch; bittersweet bedeutet zugleich schön und traurig; brooding bedeutet nach innen gekehrt und düster.",
        var text when text.StartsWith("passionate", StringComparison.OrdinalIgnoreCase) =>
            "A forceful, confident and energetic emotional character.",
        var text when text.StartsWith("rollicking", StringComparison.OrdinalIgnoreCase) =>
            "A cheerful, light-hearted and playful emotional character.",
        var text when text.StartsWith("humorous", StringComparison.OrdinalIgnoreCase) =>
            "A quirky, humorous or whimsical emotional character.",
        var text when text.StartsWith("aggressive", StringComparison.OrdinalIgnoreCase) =>
            "A tense, fiery, anxious or forceful emotional character.",
        _ => "A broad MIREX mood cluster derived from several descriptive terms."
    };

    private static string GetTempoInsight(double bpm) => bpm switch
    {
        < 80 => "Slow tempo — unhurried pace.",
        < 120 => "Moderate tempo — steady mid-tempo pace.",
        < 140 => "Medium-fast tempo — forward-moving pace.",
        < 175 => "Fast tempo — energetic pace.",
        _ => "Very fast tempo — high-energy pace."
    };

    private static string GetIntegratedLoudnessInsight(double lufs) => lufs switch
    {
        >= -8 => "Very loud overall — tightly mastered.",
        >= -11 => "Loud overall — modern, present master.",
        >= -14 => "Moderate loudness — more breathing room.",
        _ => "Relatively quiet overall — softer master."
    };

    private static string GetLoudnessRangeInsight(double lu) => lu switch
    {
        <= 3 => "Very even level — little contrast between sections.",
        <= 6 => "Controlled dynamics — moderate variation.",
        <= 10 => "Noticeable dynamics — clear quiet/loud contrast.",
        _ => "High dynamics — strong contrast across the track."
    };

    private void ShowModelSelectedGenres(MusicTrack track)
    {
        LoadModelMetadata();
        var assignments = CurrentModelGenreAssignments(track);
        var referenceCounts = MusicLibraryService.Current.GetGenreReferenceCounts();
        _modelGenreIds = assignments.Select(assignment => assignment.GenreId).ToHashSet();
        ModelSelectedGenresSection.IsVisible = _modelSubgenresById.Count > 0;
        ModelSelectedGenresPanel.Children.Clear();
        foreach (var assignment in assignments)
        {
            var hasPrediction = assignment.Reasons.Count > 0;
            var confidence = hasPrediction ? assignment.Reasons.Max(reason => reason.Score) : 0;
            var confidenceBrush = AnalysisColorScale.GenreConfidence(confidence);
            var enabled = assignment.IsEnabled;
            var container = new Border
            {
                Background = Brushes.Transparent,
                BorderThickness = new Avalonia.Thickness(0),
                Padding = new Avalonia.Thickness(0, 4),
                Opacity = enabled ? 1 : 0.48
            };
            var content = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions(hasPrediction ? "180,*,48,24,22" : "*,Auto,24,22"),
                ColumnSpacing = hasPrediction ? 12 : 10,
                VerticalAlignment = VerticalAlignment.Center
            };
            var genreName = new TextBlock
            {
                Text = assignment.IsPreferred ? $"★  {assignment.GenreName}" : assignment.GenreName,
                FontSize = 12,
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
                Foreground = ThemeResources.Brush("Theme.Brush.TextStrong"),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            content.Children.Add(genreName);

            if (!hasPrediction)
            {
                var detail = new TextBlock
                {
                    Text = "manually added",
                    FontSize = 10.5,
                    Foreground = ThemeResources.Brush("Theme.Brush.TextStrong"),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Opacity = 0.58
                };
                Grid.SetColumn(detail, 1);
                content.Children.Add(detail);
            }
            else
            {
                var confidenceBar = new ProgressBar
                {
                    Minimum = 0,
                    Maximum = 1,
                    Value = confidence,
                    Height = 4,
                    Foreground = confidenceBrush,
                    Background = ThemeResources.Brush("Theme.Brush.Surface"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(confidenceBar, 1);
                content.Children.Add(confidenceBar);

                var detail = new TextBlock
                {
                    Text = $"{confidence:P0}",
                    FontSize = 10.5,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = confidenceBrush,
                    TextAlignment = TextAlignment.Right,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Opacity = 0.94
                };
                Grid.SetColumn(detail, 2);
                content.Children.Add(detail);
            }

            var referenceCount = referenceCounts.GetValueOrDefault(assignment.GenreId);
            var referenceAtLimit = referenceCount >= 5 && !assignment.IsReference;
            var reference = new Border
            {
                Width = 22,
                Height = 22,
                Background = Brushes.Transparent,
                Opacity = referenceAtLimit ? 0.24 : assignment.IsReference ? 1 : 0.5,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new Avalonia.Controls.Shapes.Ellipse
                {
                    Width = 9,
                    Height = 9,
                    Fill = assignment.IsReference
                        ? new SolidColorBrush(Color.Parse("#FF78A9E6"))
                        : Brushes.Transparent,
                    Stroke = assignment.IsReference
                        ? new SolidColorBrush(Color.Parse("#FFD5E8FF"))
                        : new SolidColorBrush(Color.Parse("#90AEB7C2")),
                    StrokeThickness = 1,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            Grid.SetColumn(reference, hasPrediction ? 3 : 2);
            content.Children.Add(reference);
            ToolTip.SetTip(reference, assignment.IsReference
                ? $"Remove as reference track · {referenceCount}/5 references"
                : referenceAtLimit
                    ? "Reference limit reached · 5/5 references"
                    : $"Mark as reference track · {referenceCount}/5 references");
            reference.PointerPressed += (_, e) =>
            {
                if (referenceAtLimit)
                {
                    ToastRequested?.Invoke("This genre already has five reference tracks.");
                    e.Handled = true;
                    return;
                }

                try
                {
                    MusicLibraryService.Current.SetTrackGenreReference(
                        track.Id, assignment.GenreId, !assignment.IsReference);
                    ShowModelSelectedGenres(track);
                }
                catch (Exception exception)
                {
                    ToastRequested?.Invoke(exception.Message);
                }
                e.Handled = true;
            };

            var remove = new Border
            {
                Width = 22,
                Height = 22,
                Background = Brushes.Transparent,
                Opacity = 0.56,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new Avalonia.Svg.Skia.Svg(new Uri("avares://Resona/"))
                {
                    Path = "/Assets/close.svg",
                    Width = 10,
                    Height = 10,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            Grid.SetColumn(remove, hasPrediction ? 4 : 3);
            content.Children.Add(remove);
            container.Child = content;
            remove.PointerPressed += (_, e) =>
            {
                _pendingEnabledModelGenreIds.Remove(assignment.GenreId);
                ShowModelSelectedGenres(track);
                ShowDetectedGenres(track);
                RebuildModelGenreChoices();
                AutoSaveChanges();
                e.Handled = true;
            };
            IEnumerable<int> tooltipIds = hasPrediction
                ? assignment.Reasons
                    .Select(reason => FindModelSubgenreId(reason.ModelGenreName, reason.ModelSubgenreName))
                    .Where(id => id is not null)
                    .Select(id => id!.Value)
                : new[] { assignment.GenreId };
            ToolTip.SetTip(container, CreateModelMetadataTooltip(tooltipIds));
            ModelSelectedGenresPanel.Children.Add(container);
        }
    }

    private List<TrackModelGenre> CurrentModelGenreAssignments(MusicTrack track)
    {
        var predictionReasonsBySubgenreId = _trackGenrePredictions
            .GroupBy(prediction => prediction.ModelSubgenreId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ModelGenreReason>)group
                    .Select(prediction => new ModelGenreReason(
                        prediction.ModelGenreName,
                        prediction.ModelSubgenreName,
                        prediction.Score))
                    .ToList());

        var assignments = MusicLibraryService.Current.GetTrackModelGenres(track.Id)
            .ToDictionary(assignment => assignment.GenreId);

        foreach (var genreId in _pendingEnabledModelGenreIds)
        {
            if (assignments.ContainsKey(genreId) || !_modelSubgenresById.TryGetValue(genreId, out var subgenre))
                continue;

            assignments[genreId] = new TrackModelGenre(
                genreId,
                subgenre.Name,
                true,
                true,
                subgenre.IsPreferred,
                false,
                predictionReasonsBySubgenreId.GetValueOrDefault(genreId, []));
        }

        return assignments.Values
            .Where(assignment => _pendingEnabledModelGenreIds.Contains(assignment.GenreId))
            .Select(assignment => assignment with
            {
                IsEnabled = true,
                IsManual = assignment.IsManual || !_initialEnabledModelGenreIds.Contains(assignment.GenreId)
            })
            .OrderByDescending(assignment => assignment.Reasons.Count == 0 ? 0 : assignment.Reasons.Max(reason => reason.Score))
            .ThenBy(assignment => assignment.GenreName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void ShowDetectedGenres(MusicTrack track)
    {
        LoadModelMetadata();
        var detected = _trackGenrePredictions
            .Where(prediction => !_modelGenreIds.Contains(prediction.ModelSubgenreId))
            .GroupBy(prediction => prediction.ModelSubgenreId)
            .Select(group => group.OrderByDescending(prediction => prediction.Score).First())
            .OrderByDescending(prediction => prediction.Score)
            .ThenBy(prediction => prediction.ModelSubgenreName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        DetectedGenresSection.IsVisible = detected.Count > 0;
        DetectedGenresCountText.Text = $"({detected.Count})";
        ((RotateTransform)DetectedGenresChevron.RenderTransform!).Angle = _areDetectedGenresExpanded ? 90 : 0;
        DetectedGenresPanel.IsVisible = _areDetectedGenresExpanded;
        DetectedGenresPanel.Children.Clear();
        if (!_areDetectedGenresExpanded)
            return;

        foreach (var prediction in detected)
        {
            var container = new Border
            {
                Background = Brushes.Transparent,
                BorderThickness = new Avalonia.Thickness(0),
                Padding = new Avalonia.Thickness(0, 4),
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            var content = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("180,*,48,20"),
                ColumnSpacing = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            var confidenceBrush = AnalysisColorScale.GenreConfidence(prediction.Score);
            content.Children.Add(new TextBlock
            {
                Text = _modelSubgenresById.GetValueOrDefault(prediction.ModelSubgenreId)?.IsPreferred == true
                    ? $"★  {prediction.ModelSubgenreName}"
                    : prediction.ModelSubgenreName,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeResources.Brush("Theme.Brush.TextStrong"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            var confidenceBar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 1,
                Value = prediction.Score,
                Height = 4,
                Foreground = confidenceBrush,
                Background = ThemeResources.Brush("Theme.Brush.Surface"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(confidenceBar, 1);
            content.Children.Add(confidenceBar);
            var score = new TextBlock
            {
                Text = $"{prediction.Score:P0}",
                FontSize = 10.5,
                Foreground = confidenceBrush,
                FontWeight = FontWeight.SemiBold,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(score, 2);
            content.Children.Add(score);
            var add = new TextBlock
            {
                Text = "+",
                Width = 20,
                FontSize = 16,
                Foreground = ThemeResources.Brush("Theme.Brush.TextStrong"),
                Opacity = 0.58,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(add, 3);
            content.Children.Add(add);
            container.Child = content;
            container.PointerPressed += (_, _) =>
            {
                _pendingEnabledModelGenreIds.Add(prediction.ModelSubgenreId);
                ShowModelSelectedGenres(track);
                ShowDetectedGenres(track);
                RebuildModelGenreChoices();
                AutoSaveChanges();
            };
            ToolTip.SetTip(container, CreateModelMetadataTooltip([prediction.ModelSubgenreId]));
            DetectedGenresPanel.Children.Add(container);
        }
    }

    private void OnDetectedGenresHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_track is null)
            return;

        _areDetectedGenresExpanded = !_areDetectedGenresExpanded;
        ShowDetectedGenres(_track);
        e.Handled = true;
    }

    private void OnFrequentManualGenresHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_track is null)
            return;

        _areFrequentManualGenresExpanded = !_areFrequentManualGenresExpanded;
        BuildFrequentManualGenreChoices();
        e.Handled = true;
    }

    private void RebuildModelGenreChoices()
    {
        if (_modelSubgenresById.Count == 0)
        {
            AddModelGenreSection.IsVisible = false;
            return;
        }

        AddModelGenreSection.IsVisible = true;
        var availableGenres = _modelSubgenresById.Values.ToList();
        AllGenresCountText.Text = $"({availableGenres.Count})";
        ((RotateTransform)AllGenresChevron.RenderTransform!).Angle = _areAllGenresExpanded ? 90 : 0;
        AllGenresPanel.IsVisible = _areAllGenresExpanded;

        ModelGenreGroupsPanel.Children.Clear();
        ModelGenreChoicesPanel.Children.Clear();
        if (!_areAllGenresExpanded)
            return;

        var availableGroupIds = availableGenres
            .Select(subgenre => subgenre.ModelGenreId)
            .ToHashSet();
        if (_modelGenreFilterId is int selectedGroupId && !availableGroupIds.Contains(selectedGroupId))
            _modelGenreFilterId = null;

        var groups = new[] { (Id: (int?)null, Name: "All") }
            .Concat(_modelGenreNamesById
                .Where(item => availableGroupIds.Contains(item.Key))
                .OrderBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
                .Select(item => (Id: (int?)item.Key, Name: item.Value)));
        foreach (var group in groups)
        {
            var selected = group.Id == _modelGenreFilterId;
            var item = new Border
            {
                Height = 25,
                Background = Brushes.Transparent,
                Padding = new Avalonia.Thickness(2, 0),
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new TextBlock
                {
                    Text = group.Name,
                    FontSize = 10.5,
                    FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal,
                    Foreground = group.Id is null
                        ? ThemeResources.Brush(selected ? "Theme.Brush.Accent" : "Theme.Brush.TextSecondary")
                        : MainGenrePalette.For(group.Name),
                    Opacity = selected ? 1 : 0.72,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            item.PointerPressed += (_, e) =>
            {
                _modelGenreFilterId = group.Id;
                RebuildModelGenreChoices();
                e.Handled = true;
            };
            ModelGenreGroupsPanel.Children.Add(item);
        }
        ModelGenreChoicesScroll.MaxHeight = Math.Max(25, ModelGenreGroupsPanel.Children.Count * 25);

        var search = _modelGenreSearchText.Trim();
        var choices = availableGenres
            .Where(subgenre => _modelGenreFilterId is null || subgenre.ModelGenreId == _modelGenreFilterId)
            .Where(subgenre => string.IsNullOrWhiteSpace(search)
                || subgenre.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || _modelGenreNamesById.GetValueOrDefault(subgenre.ModelGenreId, "")
                    .Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(subgenre => _modelGenreNamesById.GetValueOrDefault(subgenre.ModelGenreId, ""), StringComparer.OrdinalIgnoreCase)
            .ThenBy(subgenre => subgenre.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var subgenre in choices)
            ModelGenreChoicesPanel.Children.Add(CreateModelGenreChoiceButton(subgenre));
        if (choices.Count == 0)
        {
            ModelGenreChoicesPanel.Children.Add(new TextBlock
            {
                Text = "No genres match this filter.",
                FontSize = 11,
                Opacity = 0.52,
                Margin = new Avalonia.Thickness(0, 8, 0, 4)
            });
        }
    }

    private void BuildFrequentManualGenreChoices()
    {
        FrequentManualGenresPanel.Children.Clear();
        if (_track is null)
        {
            FrequentManualGenresSection.IsVisible = false;
            return;
        }

        ((RotateTransform)FrequentManualGenresChevron.RenderTransform!).Angle =
            _areFrequentManualGenresExpanded ? 90 : 0;
        FrequentManualGenresPanel.IsVisible = _areFrequentManualGenresExpanded;
        if (!_areFrequentManualGenresExpanded)
        {
            FrequentManualGenresSection.IsVisible = _modelSubgenresById.Count > 0;
            FrequentManualGenresCountText.Text = "(8)";
            return;
        }

        var manualGenreUsages = MusicLibraryService.Current
            .GetTopManualModelGenres(Math.Max(8, _modelSubgenresById.Count))
            .Where(usage => !_modelGenreIds.Contains(usage.ModelSubgenreId))
            .Where(usage => _modelSubgenresById.ContainsKey(usage.ModelSubgenreId))
            .ToList();
        var frequentGenres = manualGenreUsages
            .Take(8)
            .ToList();

        FrequentManualGenresSection.IsVisible = frequentGenres.Count > 0;
        FrequentManualGenresCountText.Text = $"({frequentGenres.Count})";
        if (frequentGenres.Count == 0)
            return;

        foreach (var usage in frequentGenres)
            FrequentManualGenresPanel.Children.Add(CreateFrequentManualGenreChoice(usage));
    }

    private Control CreateFrequentManualGenreChoice(ManualModelGenreUsage usage)
    {
        var container = new Border
        {
            Background = Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0),
            Padding = new Avalonia.Thickness(0, 4),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        var content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,48,20"),
            ColumnSpacing = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        content.Children.Add(new TextBlock
        {
            Text = _modelSubgenresById.GetValueOrDefault(usage.ModelSubgenreId)?.IsPreferred == true
                ? $"★  {usage.ModelSubgenreName}"
                : usage.ModelSubgenreName,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeResources.Brush("Theme.Brush.TextStrong"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var metric = new TextBlock
        {
            Text = $"{usage.UsageCount}×",
            FontSize = 10.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeResources.Brush("Theme.Brush.TextSecondary"),
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(metric, 1);
        content.Children.Add(metric);

        var add = new TextBlock
        {
            Text = "+",
            Width = 20,
            FontSize = 16,
            Foreground = ThemeResources.Brush("Theme.Brush.TextStrong"),
            Opacity = 0.58,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(add, 2);
        content.Children.Add(add);
        container.Child = content;
        container.PointerPressed += (_, _) =>
        {
            if (_track is null) return;
            _pendingEnabledModelGenreIds.Add(usage.ModelSubgenreId);
            ShowModelSelectedGenres(_track);
            ShowDetectedGenres(_track);
            RebuildModelGenreChoices();
            AutoSaveChanges();
        };
        ToolTip.SetTip(container, CreateModelMetadataTooltip([usage.ModelSubgenreId]));
        return container;
    }

    private Button CreateModelGenreChoiceButton(ModelSubgenre subgenre)
    {
        var selected = _pendingEnabledModelGenreIds.Contains(subgenre.Id);
        var text = new TextBlock
        {
            Text = subgenre.IsPreferred ? $"★  {subgenre.Name}" : subgenre.Name,
            FontSize = 10.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeResources.Brush(selected
                ? "Theme.Brush.TextStrong"
                : "Theme.Brush.TextPrimary"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };

        var button = new Button
        {
            Content = text,
            Height = 32,
            Padding = new Avalonia.Thickness(9, 4),
            CornerRadius = new Avalonia.CornerRadius(5),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ApplyModelGenreChoiceVisual(button, text, selected);
        button.Classes.Add("edit-choice");
        ToolTip.SetTip(button, CreateModelMetadataTooltip([subgenre.Id]));
        button.Click += (_, _) =>
        {
            if (_track is null) return;
            if (!_pendingEnabledModelGenreIds.Add(subgenre.Id))
                _pendingEnabledModelGenreIds.Remove(subgenre.Id);
            AutoSaveChanges();
            ApplyModelGenreChoiceVisual(
                button,
                text,
                _pendingEnabledModelGenreIds.Contains(subgenre.Id));
            ShowModelSelectedGenres(_track);
            ShowDetectedGenres(_track);
        };
        return button;
    }

    private static void ApplyModelGenreChoiceVisual(Button button, TextBlock text, bool selected)
    {
        button.Background = selected
            ? new SolidColorBrush(Color.FromArgb(46, 255, 255, 255))
            : Brushes.Transparent;
        button.BorderBrush = selected
            ? new SolidColorBrush(Color.FromArgb(160, 255, 255, 255))
            : ThemeResources.Brush("Theme.Brush.BorderSubtle");
        text.Foreground = ThemeResources.Brush(selected
            ? "Theme.Brush.TextStrong"
            : "Theme.Brush.TextPrimary");
    }

    private void OnAllGenresHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_track is null)
            return;

        _areAllGenresExpanded = !_areAllGenresExpanded;
        RebuildModelGenreChoices();
        e.Handled = true;
    }

    private void OnModelGenreSearchChanged(object? sender, TextChangedEventArgs e)
    {
        _modelGenreSearchText = ModelGenreSearchBox.Text ?? string.Empty;
        RebuildModelGenreChoices();
    }

    private void LoadModelMetadata()
    {
        if (_modelSubgenresById.Count > 0)
            return;

        var subgenres = MusicLibraryService.Current.GetModelSubgenres();
        _modelSubgenresById = subgenres.ToDictionary(item => item.Id);
        _modelGenreNamesById = MusicLibraryService.Current.GetModelGenres().ToDictionary(item => item.Id, item => item.Name);
        _distinctionsBySubgenreId = MusicLibraryService.Current.GetModelSubgenreDistinctions()
            .GroupBy(item => item.ModelSubgenreId)
            .ToDictionary(group => group.Key, group => group.ToList());
    }

    private int? FindModelSubgenreId(string genreName, string subgenreName) => _modelSubgenresById.Values
        .FirstOrDefault(item => item.Name == subgenreName
            && _modelGenreNamesById.GetValueOrDefault(item.ModelGenreId) == genreName)?.Id;

    private Control CreateModelMetadataTooltip(IEnumerable<int> subgenreIds)
    {
        var entries = subgenreIds
            .Distinct()
            .Where(_modelSubgenresById.ContainsKey)
            .Select(id =>
            {
                var subgenre = _modelSubgenresById[id];
                var genreName = _modelGenreNamesById.GetValueOrDefault(subgenre.ModelGenreId, "Model genre");
                var distinctions = _distinctionsBySubgenreId.GetValueOrDefault(subgenre.Id, []);
                return new GenreMetadataTooltipEntry(subgenre, genreName, distinctions);
            });
        return GenreMetadataTooltipFactory.Create(entries);
    }


    private void OnCloseClicked(object? sender, RoutedEventArgs e) => CloseOverlay();

    private void CloseOverlay()
    {
        if (!_isOpen)
            return;

        if (_isEditingInformation)
        {
            CommitInformationEdit();
            SetInformationEditing(false);
        }

        if (_isPlayingPreview)
        {
            _isPlayingPreview = false;
            PreviewClosed?.Invoke();
        }
        _analysisElapsedTimer.Stop();
        ++_openGeneration;
        _loadingTrack = false;
        _isOpen = false;
        IsHitTestVisible = false;
        IsVisible = false;
        Closed?.Invoke();
    }

    private void UpdateAnalysisElapsedTime()
    {
        var elapsed = DateTime.UtcNow - _analysisStartedAt;
        AnalysisElapsedText.Text = elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes}:{elapsed.Seconds:00}";
    }
}
