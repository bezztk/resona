using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Resona.Models;
using Resona.Services;

namespace Resona.Views;

public partial class AddTrackOverlay : UserControl
{
    private List<Genre> _genres = [];
    private List<Rating> _ratings = [];
    private List<Style> _styles = [];

    // Each chip tuple pairs the domain object with its ToggleButton so we can read
    // the selection by ID rather than by parsing button content (which includes counts).
    private readonly List<(Genre Genre, ToggleButton Btn)> _genreChips = [];
    private readonly List<(Style Style, ToggleButton Btn)> _styleChips = [];

    private Dictionary<int, List<int>> _allTrackGenreIds = [];
    private Dictionary<int, List<int>> _allTrackStyleIds = [];

    private CancellationTokenSource? _urlValidationCts;
    private string? _validCanonicalUrl;
    private string? _remoteTitle;
    private bool _updatingUrlText;
    private bool _downloading;
    private bool _showAllGenres;
    private const int InitialGenreLimit = 10;
    private static readonly IBrush NeutralUrlBrush = new SolidColorBrush(Color.FromRgb(61, 70, 82));
    private static readonly IBrush ValidUrlBrush = new SolidColorBrush(Color.FromRgb(72, 194, 120));
    private static readonly IBrush InvalidUrlBrush = new SolidColorBrush(Color.FromRgb(224, 92, 92));

    public event Action<string?>? TrackDownloaded;
    public event Action? CloseRequested;

    public AddTrackOverlay()
    {
        InitializeComponent();
        BodyScroll.PropertyChanged += (_, _) => UpdateBodyWidth();
        UrlBox.TextChanged += (_, _) => ValidateUrl();
        RatingBox.SelectionChanged += (_, _) => UpdateDownloadButton();
    }

    public void Open()
    {
        SetBusy(false);
        _showAllGenres = false;
        LoadLookups();
        ClearForm();
        IsVisible = true;
        UpdateBodyWidth();
    }

    private void UpdateBodyWidth()
    {
        var width = BodyScroll.Bounds.Width - BodyScroll.Padding.Left - BodyScroll.Padding.Right;
        if (width > 0)
            BodyContent.Width = width;
    }

    private void LoadLookups()
    {
        _genres = [];
        _ratings = MusicLibraryService.Current.GetRatings();
        _styles = MusicLibraryService.Current.GetStyles();
        _allTrackGenreIds = [];
        _allTrackStyleIds = MusicLibraryService.Current.GetAllTrackStyleIds();

        GenresPanel.Children.Clear();
        _genreChips.Clear();

        RatingBox.ItemsSource = _ratings.Select(r => r.DisplayName).ToList();
        RatingBox.SelectedIndex = -1;
    }

    private void ClearForm()
    {
        UrlBox.Text = "";
        StatusText.Text = "";
        SetUrlState(UrlState.Empty);
        CloseBtn.IsEnabled = true;
        DownloadBtn.IsEnabled = false;
        BusyDetailText.Text = "";
        foreach (var (_, btn) in _genreChips) btn.IsChecked = false;
        StylesPanel.Children.Clear();
        _styleChips.Clear();
        StylesSection.IsVisible = false;
        RatingBox.SelectedIndex = -1;
    }

    // ─── Genre chips ─────────────────────────────────────────────────────────

    private void RebuildGenreChips()
    {
        var genreCounts = MetadataCountService.GenreCounts(_allTrackGenreIds);
        var sorted = _genres
            .OrderByDescending(g => genreCounts.GetValueOrDefault(g.Id, 0))
            .ThenBy(g => g.Name)
            .ToList();

        GenresPanel.Children.Clear();
        _genreChips.Clear();

        for (int i = 0; i < sorted.Count; i++)
        {
            var genre = sorted[i];
            var count = genreCounts.GetValueOrDefault(genre.Id, 0);
            var btn = MetadataChipFactory.Create(genre.Name, count);
            btn.IsVisible = _showAllGenres || i < InitialGenreLimit;
            btn.IsCheckedChanged += (_, _) => OnGenreSelectionChanged();
            _genreChips.Add((genre, btn));
            GenresPanel.Children.Add(btn);
        }

        ShowMoreGenresBtn.IsVisible = sorted.Count > InitialGenreLimit && !_showAllGenres;
    }

    private void OnShowMoreGenresClicked(object? sender, RoutedEventArgs e)
    {
        _showAllGenres = true;
        for (int i = InitialGenreLimit; i < _genreChips.Count; i++)
            _genreChips[i].Btn.IsVisible = true;
        ShowMoreGenresBtn.IsVisible = false;
    }

    // ─── Style chips (rebuilt whenever genre selection changes) ───────────────

    private void OnGenreSelectionChanged()
    {
        UpdateDownloadButton();
        RebuildStyleChips();
    }

    private void RebuildStyleChips()
    {
        var selectedGenreIds = _genreChips
            .Where(c => c.Btn.IsChecked == true)
            .Select(c => c.Genre.Id)
            .ToHashSet();

        StylesSection.IsVisible = selectedGenreIds.Count > 0 && _styles.Count > 0;

        if (selectedGenreIds.Count == 0)
        {
            StylesPanel.Children.Clear();
            _styleChips.Clear();
            return;
        }

        // Counts are scoped to tracks that match ALL selected genres (AND logic).
        var styleCounts = MetadataCountService.StyleCountsForGenres(
            _allTrackGenreIds, _allTrackStyleIds, selectedGenreIds);

        // Preserve which styles the user had already checked before the rebuild.
        var prevSelected = _styleChips
            .Where(c => c.Btn.IsChecked == true)
            .Select(c => c.Style.Id)
            .ToHashSet();

        var sorted = _styles
            .OrderByDescending(s => styleCounts.GetValueOrDefault(s.Id, 0))
            .ThenBy(s => s.Name)
            .ToList();

        StylesPanel.Children.Clear();
        _styleChips.Clear();

        foreach (var style in sorted)
        {
            var count = styleCounts.GetValueOrDefault(style.Id, 0);
            var btn = MetadataChipFactory.Create(style.Name, count, prevSelected.Contains(style.Id));
            btn.Opacity = count > 0 ? 1.0 : 0.48;
            btn.IsCheckedChanged += (_, _) => UpdateDownloadButton();
            _styleChips.Add((style, btn));
            StylesPanel.Children.Add(btn);
        }
    }

    // ─── Validation ───────────────────────────────────────────────────────────

    private void UpdateDownloadButton()
    {
        DownloadBtn.IsEnabled = _validCanonicalUrl != null
                               && RatingBox.SelectedIndex >= 0;
    }

    private void ValidateUrl()
    {
        if (_updatingUrlText)
            return;

        _urlValidationCts?.Cancel();
        _remoteTitle = null;

        var rawUrl = UrlBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            SetUrlState(UrlState.Empty);
            return;
        }

        var videoId = YouTubeUrlNormalizer.ExtractVideoId(rawUrl);
        if (videoId is null)
        {
            SetUrlState(UrlState.Invalid, "URL can not be parsed.");
            return;
        }

        var canonicalUrl = YouTubeUrlNormalizer.GetCanonicalUrl(videoId);
        if (MusicLibraryService.Current.TrackExistsByCanonicalUrl(canonicalUrl))
        {
            SetUrlState(UrlState.Invalid, "Track already exists.");
            return;
        }

        if (!string.Equals(UrlBox.Text, canonicalUrl, StringComparison.Ordinal))
        {
            _updatingUrlText = true;
            UrlBox.Text = canonicalUrl;
            UrlBox.CaretIndex = canonicalUrl.Length;
            _updatingUrlText = false;
        }

        SetUrlState(UrlState.Valid, "URL looks good. Reading title...");

        _urlValidationCts = new CancellationTokenSource();
        _ = LoadRemoteTitleAsync(canonicalUrl, _urlValidationCts.Token);
    }

    private async Task LoadRemoteTitleAsync(string canonicalUrl, CancellationToken token)
    {
        try
        {
            await Task.Delay(450, token);
            var title = await MusicLibraryService.Current.GetRemoteTitleAsync(canonicalUrl);
            if (token.IsCancellationRequested || _validCanonicalUrl != canonicalUrl)
                return;

            _remoteTitle = title;
            SetUrlState(UrlState.Valid, title is { Length: > 0 }
                ? $"Ready: {title}"
                : "URL looks good.");
        }
        catch (OperationCanceledException) { }
    }

    private void SetUrlState(UrlState state, string message = "")
    {
        _validCanonicalUrl = state == UrlState.Valid
            ? YouTubeUrlNormalizer.GetCanonicalUrl(YouTubeUrlNormalizer.ExtractVideoId(UrlBox.Text?.Trim() ?? "")!)
            : null;

        UrlBox.BorderBrush = state switch
        {
            UrlState.Valid => ValidUrlBrush,
            UrlState.Invalid => InvalidUrlBrush,
            _ => NeutralUrlBrush
        };

        UrlStateIcon.Text = state switch
        {
            UrlState.Valid => "✓",
            UrlState.Invalid => "!",
            _ => ""
        };
        UrlStateIcon.Foreground = state == UrlState.Valid ? ValidUrlBrush : InvalidUrlBrush;
        UrlValidationText.Text = message;
        UrlValidationText.Foreground = state == UrlState.Valid ? ValidUrlBrush : InvalidUrlBrush;

        UpdateDownloadButton();
    }

    // ─── Download ─────────────────────────────────────────────────────────────

    private void OnDownloadClicked(object? sender, RoutedEventArgs e)
    {
        if (_validCanonicalUrl is null)
            return;

        SetBusy(true, _remoteTitle ?? _validCanonicalUrl);
        StatusText.Text = "";

        var request = new DownloadRequest
        {
            RawUrl = _validCanonicalUrl,
            GenreIds = _genreChips
                .Where(c => c.Btn.IsChecked == true)
                .Select(c => c.Genre.Id)
                .ToList(),
            RatingId = _ratings[RatingBox.SelectedIndex].Id,
            StyleIds = _styleChips
                .Where(c => c.Btn.IsChecked == true)
                .Select(c => c.Style.Id)
                .ToList()
        };

        try
        {
            ImportQueueService.Current.Queue(request, _remoteTitle ?? _validCanonicalUrl);
        }
        catch (Exception exception)
        {
            SetBusy(false);
            StatusText.Text = exception.Message;
            UpdateDownloadButton();
            CloseBtn.IsEnabled = true;
            return;
        }

        TrackDownloaded?.Invoke("Track added to the download queue");
    }

    private void SetBusy(bool isBusy, string detail = "")
    {
        _downloading = isBusy;
        BusyLayer.IsVisible = isBusy;
        BusyTitleText.Text = "Adding to download queue";
        BusyDetailText.Text = detail;
        DownloadBtn.IsEnabled = !isBusy && DownloadBtn.IsEnabled;
        CloseBtn.IsEnabled = !isBusy;
        UrlBox.IsEnabled = !isBusy;
        RatingBox.IsEnabled = !isBusy;
        ShowMoreGenresBtn.IsEnabled = !isBusy;

        foreach (var (_, btn) in _genreChips)
            btn.IsEnabled = !isBusy;

        foreach (var (_, btn) in _styleChips)
            btn.IsEnabled = !isBusy;
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
    {
        if (_downloading) return;
        _urlValidationCts?.Cancel();
        CloseRequested?.Invoke();
    }

    private enum UrlState
    {
        Empty,
        Valid,
        Invalid
    }
}
