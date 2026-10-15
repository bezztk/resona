using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Resona.Models;
using Resona.Services;

namespace Resona.Views;

public partial class ChannelOverlay : UserControl
{
    private enum ChannelVideoFilter { Available, Review, InLibrary, Rejected, Issues }
    private enum ProfileTrackFilter { Available, Review, InLibrary, Rejected }

    private CancellationTokenSource? _refreshCts;
    private int? _refreshChannelId;
    private string _refreshMessage = string.Empty;
    private List<ChannelHubItem> _hubChannels = [];
    private ChannelHubItem? _selectedHubChannel;
    private List<ChannelVideoDisplay> _currentVideos = [];
    private int _selectedChannelId = -1;
    private bool _detailOpenedFromInbox;
    private bool _loadingVideos;
    private ChannelVideoFilter _videoFilter = ChannelVideoFilter.Available;
    private readonly Dictionary<int, ChannelVideoFilter> _videoFiltersByChannel = [];
    private readonly Dictionary<int, string> _videoSearchByChannel = [];
    private readonly ConcurrentDictionary<int, Bitmap> _channelArtworkCache = [];
    private int _channelSnapshotGeneration;
    private int _videoLoadGeneration;
    private int? _pendingOpenChannelId;
    private bool _processingPastedUrl;
    private int? _activePreviewTrackId;
    private int _channelSortIndex;
    private CancellationTokenSource? _profileLoadCts;
    private CancellationTokenSource? _profileImageLoadCts;
    private List<CloudProfileDisplay> _cloudProfiles = [];
    private CloudProfileDisplay? _selectedCloudProfile;
    private List<CloudProfileTrackDisplay> _cloudProfileTracks = [];
    private ProfileTrackFilter _profileTrackFilter = ProfileTrackFilter.Available;
    private int _profileTrackSortIndex;
    private readonly Avalonia.Threading.DispatcherTimer _channelStateRefreshTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(750)
    };

    public event Action? CloseRequested;
    public event Action<string>? ToastRequested;
    public event Action<MusicTrack>? PreviewRequested;
    public event Action? PreviewClosed;
    public event Action<int>? TrackChanged;
    public event Action<MusicTrack>? EditRequested;

    public ChannelOverlay()
    {
        InitializeComponent();
        _channelStateRefreshTimer.Tick += (_, _) =>
        {
            _channelStateRefreshTimer.Stop();
            RefreshChannelStates();
        };
        ChannelHubBackgroundService.Current.SnapshotChanged += OnChannelSnapshotChanged;
        SetVideoFilter(ChannelVideoFilter.Available, refresh: false);
    }

    public void Open()
    {
        IsVisible = true;
        HubView.IsVisible = true;
        ProfileHubView.IsVisible = false;
        ProfileDetailView.IsVisible = false;
        DetailView.IsVisible = false;
        InboxView.IsVisible = false;
        _detailOpenedFromInbox = false;
        var snapshot = ChannelHubBackgroundService.Current.Snapshot;
        if (snapshot.Count > 0)
            OnChannelSnapshotChanged(snapshot);
        RefreshChannels();
    }

    public void OpenChannel(int channelId)
    {
        _pendingOpenChannelId = channelId;
        Open();
        var channel = _hubChannels.FirstOrDefault(item => item.Id == channelId);
        if (channel is not null)
        {
            _pendingOpenChannelId = null;
            OpenChannelDetail(channel);
        }
    }

    public void UpdateDownloadSummary()
    {
        if (IsVisible)
        {
            _channelStateRefreshTimer.Stop();
            _channelStateRefreshTimer.Start();
        }
    }

    public void OnMetadataUpdated(int channelId, int videoId)
    {
        if (_selectedChannelId != channelId || !DetailView.IsVisible)
            return;
        _channelStateRefreshTimer.Stop();
        _channelStateRefreshTimer.Start();
    }

    public void RefreshChannels()
    {
        ChannelHubBackgroundService.Current.RequestRefresh();
        RefreshInboxBadge();
    }

    private void OnChannelSnapshotChanged(IReadOnlyList<ChannelHubItem> snapshot)
    {
        var generation = Interlocked.Increment(ref _channelSnapshotGeneration);
        _ = PrepareChannelSnapshotAsync(snapshot, generation);
    }

    private async Task PrepareChannelSnapshotAsync(IReadOnlyList<ChannelHubItem> snapshot, int generation)
    {
        var prepared = await Task.Run(() =>
        {
            var channels = snapshot.ToList();
            foreach (var channel in channels)
            {
                if (channel.Thumbnail is not { Length: > 0 })
                    continue;
                if (!_channelArtworkCache.TryGetValue(channel.Id, out var artwork))
                {
                    try
                    {
                        using var stream = new MemoryStream(channel.Thumbnail);
                        var decoded = new Bitmap(stream);
                        if (!_channelArtworkCache.TryAdd(channel.Id, decoded))
                            decoded.Dispose();
                        artwork = _channelArtworkCache.GetValueOrDefault(channel.Id);
                    }
                    catch
                    {
                        continue;
                    }
                }
                channel.Artwork = artwork;
            }
            return channels;
        });

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (generation != _channelSnapshotGeneration)
                return;

            _hubChannels = prepared;
            ApplyHubFilter();
            if (_pendingOpenChannelId is int pendingId
                && _hubChannels.FirstOrDefault(item => item.Id == pendingId) is { } pendingChannel)
            {
                _pendingOpenChannelId = null;
                OpenChannelDetail(pendingChannel);
                return;
            }

            if (_selectedChannelId < 0)
                return;
            var previousKnownVideoCount = _selectedHubChannel?.KnownVideoCount;
            var previousLocalTrackCount = _selectedHubChannel?.LocalTrackCount;
            _selectedHubChannel = _hubChannels.FirstOrDefault(channel => channel.Id == _selectedChannelId);
            if (DetailView.IsVisible && _selectedHubChannel is not null)
            {
                UpdateDetailHeader();
                if (previousKnownVideoCount != _selectedHubChannel.KnownVideoCount
                    || previousLocalTrackCount != _selectedHubChannel.LocalTrackCount)
                    RefreshVideos();
            }
        });
    }

    public void SetAtmosphereColors(Color primary, Color secondary)
    {
        if (ChannelAtmosphereTint.Fill is not LinearGradientBrush gradient
            || gradient.GradientStops.Count < 2)
            return;

        gradient.GradientStops[0].Color = primary;
        gradient.GradientStops[1].Color = secondary;
    }

    private void ApplyHubFilter()
    {
        var search = ChannelSearchBox.Text?.Trim() ?? string.Empty;
        IEnumerable<ChannelHubItem> matching = _hubChannels;
        if (search.Length > 0)
            matching = matching.Where(channel =>
                channel.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || channel.TopTracks.Any(title => title.Contains(search, StringComparison.OrdinalIgnoreCase)));

        var visible = matching.ToList();
        var following = visible
            .Where(channel => channel.IsFollowed)
            .OrderByDescending(channel => channel.AvailableVideoCount + channel.ReviewVideoCount)
            .ThenBy(channel => channel.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var all = (_channelSortIndex switch
        {
            1 => visible.OrderByDescending(channel => channel.AverageRating.HasValue)
                .ThenByDescending(channel => channel.AverageRating)
                .ThenByDescending(channel => channel.RatedTrackCount),
            2 => visible.OrderByDescending(channel => channel.LocalTrackCount)
                .ThenByDescending(channel => channel.RatedTrackCount),
            3 => visible.OrderByDescending(channel => channel.KnownVideoCount)
                .ThenByDescending(channel => channel.UncheckedVideoCount),
            4 => visible.OrderByDescending(channel => channel.LastDownloadedAt, StringComparer.Ordinal),
            5 => visible.OrderByDescending(channel => channel.PlayCount)
                .ThenByDescending(channel => channel.LocalTrackCount),
            _ => visible.OrderBy(channel => channel.Name, StringComparer.OrdinalIgnoreCase)
        }).ThenBy(channel => channel.Name, StringComparer.OrdinalIgnoreCase).ToList();

        FollowingItems.ItemsSource = following;
        AllChannelItems.ItemsSource = all;
        EmptyFollowingText.IsVisible = following.Count == 0;
        EmptySearchText.IsVisible = all.Count == 0;
        EmptySearchText.Text = search.Length > 0
            ? "No channels match this search."
            : "No library channels yet. Add a YouTube channel to get started.";
        var totalFollowing = _hubChannels.Count(channel => channel.IsFollowed);
        FollowingHeadingText.Text = $"FOLLOWING ({totalFollowing:N0})";
        AllChannelsHeadingText.Text = search.Length > 0
            ? $"Showing {all.Count:N0} of {_hubChannels.Count:N0} channels in your library."
            : $"You currently have {_hubChannels.Count:N0} channels in your library.";
    }

    private void OnChannelSearchChanged(object? sender, TextChangedEventArgs e) => ApplyHubFilter();

    private void OnChannelSearchToggleClicked(object? sender, RoutedEventArgs e) =>
        ToggleSearch(ChannelSearchBox, ChannelSearchToggleButton);

    private void OnChannelSearchKeyDown(object? sender, KeyEventArgs e) =>
        HandleSearchKeyDown(ChannelSearchBox, ChannelSearchToggleButton, e);

    private void OnChannelSearchLostFocus(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(
            () => UpdateSearchVisibility(ChannelSearchBox, ChannelSearchToggleButton),
            DispatcherPriority.Background);

    private void OnChannelSortOptionClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }
            || !int.TryParse(tag, out var index))
            return;

        _channelSortIndex = index;
        ChannelSortText.Text = index switch
        {
            1 => "Best rated",
            2 => "Most library tracks",
            3 => "Most channel videos",
            4 => "Recently added",
            5 => "Most played",
            _ => "Name"
        };
        RefreshChannelSortOptions();
        ChannelSortButton.Flyout?.Hide();
        ApplyHubFilter();
        AllChannelItems.SelectedItem = null;
        Dispatcher.UIThread.Post(() =>
        {
            var scrollViewer = AllChannelItems
                .GetVisualDescendants()
                .OfType<ScrollViewer>()
                .FirstOrDefault();
            if (scrollViewer is not null)
                scrollViewer.Offset = new Vector(scrollViewer.Offset.X, 0);
        }, DispatcherPriority.Background);
        e.Handled = true;
    }

    private void RefreshChannelSortOptions()
    {
        var options = new (Button Button, Image Check)[]
        {
            (ChannelSortNameOption, ChannelSortNameCheck),
            (ChannelSortRatingOption, ChannelSortRatingCheck),
            (ChannelSortLibraryOption, ChannelSortLibraryCheck),
            (ChannelSortVideosOption, ChannelSortVideosCheck),
            (ChannelSortRecentOption, ChannelSortRecentCheck),
            (ChannelSortPlayedOption, ChannelSortPlayedCheck)
        };

        for (var index = 0; index < options.Length; index++)
        {
            var (button, check) = options[index];
            button.Classes.Remove("selected");
            if (index == _channelSortIndex)
                button.Classes.Add("selected");
            check.Opacity = index == _channelSortIndex ? 0.9 : 0;
        }
    }

    private void OnAddChannelToggleClicked(object? sender, RoutedEventArgs e)
    {
        AddChannelPanel.IsVisible = !AddChannelPanel.IsVisible;
        if (AddChannelPanel.IsVisible)
            UrlBox.Focus();
    }

    private void OnOpenChannelClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ChannelHubItem channel })
            return;

        OpenChannelDetail(channel);
        e.Handled = true;
    }

    private void OnChannelCardTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: ChannelHubItem channel })
            return;
        if (e.Source is Visual source
            && (source is Button || source.GetVisualAncestors().OfType<Button>().Any()))
            return;

        OpenChannelDetail(channel);
        e.Handled = true;
    }

    private void OnProfilesTabClicked(object? sender, RoutedEventArgs e)
    {
        ShowProfileHub();
        if (_cloudProfiles.Count == 0)
            _ = LoadProfilesAsync();
    }

    private void OnChannelsTabClicked(object? sender, RoutedEventArgs e)
    {
        CancelProfileLoad();
        CancelProfileImageLoad();
        ProfileHubView.IsVisible = false;
        ProfileDetailView.IsVisible = false;
        DetailView.IsVisible = false;
        InboxView.IsVisible = false;
        HubView.IsVisible = true;
        ApplyHubFilter();
    }

    private void ShowProfileHub()
    {
        PreviewClosed?.Invoke();
        HubView.IsVisible = false;
        DetailView.IsVisible = false;
        InboxView.IsVisible = false;
        ProfileDetailView.IsVisible = false;
        ProfileHubView.IsVisible = true;
        ApplyProfileFilter();
    }

    private async Task LoadProfilesAsync(bool force = false)
    {
        if (!force && _cloudProfiles.Count > 0)
        {
            ApplyProfileFilter();
            return;
        }

        CancelProfileLoad();
        CancelProfileImageLoad();
        var cancellation = new CancellationTokenSource();
        _profileLoadCts = cancellation;
        ProfileHubEmptyPanel.IsVisible = true;
        ProfileHubEmptyTitle.Text = "Loading profiles";
        ProfileHubEmptyDescription.Text = "Reading public libraries from Resona Cloud…";
        ProfileList.ItemsSource = null;

        try
        {
            var profiles = await CloudPublicLibraryClient.Current.GetProfilesAsync(cancellation.Token);
            if (cancellation.IsCancellationRequested || !ProfileHubView.IsVisible)
                return;

            foreach (var profile in _cloudProfiles)
                profile.Dispose();
            _cloudProfiles = profiles.Select(profile => new CloudProfileDisplay(profile)).ToList();
            ApplyProfileFilter();
            _profileImageLoadCts = new CancellationTokenSource();
            _ = LoadProfileImagesAsync(_cloudProfiles, _profileImageLoadCts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (cancellation.IsCancellationRequested)
                return;
            ProfileHubEmptyPanel.IsVisible = true;
            ProfileHubEmptyTitle.Text = "Could not load profiles";
            ProfileHubEmptyDescription.Text = exception.Message;
        }
    }

    private async Task LoadProfileImagesAsync(
        IReadOnlyList<CloudProfileDisplay> profiles,
        CancellationToken cancellationToken)
    {
        foreach (var profile in profiles.Where(profile => profile.HasRemoteAvatar))
        {
            try
            {
                var image = await CloudPublicLibraryClient.Current.GetProfileImageAsync(
                    profile.UserId, cancellationToken);
                if (image is { Length: > 0 })
                {
                    profile.SetAvatar(image);
                    if (ProfileDetailView.IsVisible && _selectedCloudProfile?.UserId == profile.UserId)
                    {
                        ProfileDetailAvatar.Source = profile.Avatar;
                        ProfileDetailAvatar.IsVisible = profile.HasAvatar;
                        ProfileDetailMonogram.IsVisible = profile.ShowMonogram;
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch { }
        }
    }

    private void OnProfileSearchChanged(object? sender, TextChangedEventArgs e) => ApplyProfileFilter();

    private void ApplyProfileFilter()
    {
        if (!ProfileHubView.IsVisible)
            return;

        var search = ProfileSearchBox.Text?.Trim() ?? string.Empty;
        var visible = _cloudProfiles
            .Where(profile => search.Length == 0
                              || profile.Username.Contains(search, StringComparison.OrdinalIgnoreCase)
                              || profile.BioText.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList();
        ProfileList.ItemsSource = visible;
        ProfileHubEmptyPanel.IsVisible = visible.Count == 0;
        ProfileHubEmptyTitle.Text = search.Length > 0 ? "No matching profiles" : "No public profiles";
        ProfileHubEmptyDescription.Text = search.Length > 0
            ? "Try another name or clear the search."
            : "Profiles appear here after their first successful cloud synchronization.";
    }

    private void OnRefreshProfilesClicked(object? sender, RoutedEventArgs e) => _ = LoadProfilesAsync(force: true);

    private void OnCloudProfileTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: CloudProfileDisplay profile })
            return;
        OpenCloudProfile(profile);
        e.Handled = true;
    }

    private void OpenCloudProfile(CloudProfileDisplay profile)
    {
        CancelProfileLoad();
        _selectedCloudProfile = profile;
        ProfileHubView.IsVisible = false;
        ProfileDetailView.IsVisible = true;
        HubView.IsVisible = false;
        DetailView.IsVisible = false;
        InboxView.IsVisible = false;
        ProfileDetailName.Text = profile.Username;
        ProfileDetailAvatar.Source = profile.Avatar;
        ProfileDetailAvatar.IsVisible = profile.HasAvatar;
        ProfileDetailMonogram.Text = profile.Monogram;
        ProfileDetailMonogram.IsVisible = profile.ShowMonogram;
        ProfileTrackSearchBox.Text = string.Empty;
        SetProfileTrackFilter(ProfileTrackFilter.Available, refresh: false);
        _ = LoadProfileTracksAsync(profile);
    }

    private async Task LoadProfileTracksAsync(CloudProfileDisplay profile)
    {
        CancelProfileLoad();
        var cancellation = new CancellationTokenSource();
        _profileLoadCts = cancellation;
        _cloudProfileTracks = [];
        ProfileTrackList.ItemsSource = null;
        ProfileAvailableCountText.Text = "0";
        ProfileReviewCountText.Text = "0";
        ProfileLibraryCountText.Text = "0";
        ProfileRejectedCountText.Text = "0";
        ProfileTrackSummaryText.Text = "Loading public tracks…";
        ProfileTrackEmptyPanel.IsVisible = true;
        ProfileTrackEmptyTitle.Text = "Loading tracks";
        ProfileTrackEmptyDescription.Text = "Reading this profile's synchronized library…";

        try
        {
            var tracks = await CloudPublicLibraryClient.Current.GetTracksAsync(profile.UserId, cancellation.Token);
            if (cancellation.IsCancellationRequested
                || !ProfileDetailView.IsVisible
                || _selectedCloudProfile?.UserId != profile.UserId)
                return;

            var ratingSortOrders = MusicLibraryService.Current.GetRatings()
                .ToDictionary(rating => rating.Name, rating => rating.SortOrder, StringComparer.OrdinalIgnoreCase);
            _cloudProfileTracks = tracks
                .Select(track =>
                {
                    var ownerRatingSortOrder = !string.IsNullOrWhiteSpace(track.Rating)
                                               && ratingSortOrders.TryGetValue(track.Rating, out var sortOrder)
                        ? sortOrder
                        : (int?)null;
                    return new CloudProfileTrackDisplay(track, ownerRatingSortOrder);
                })
                .ToList();
            RefreshProfileTrackStates();
            ApplyProfileTrackFilter();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (cancellation.IsCancellationRequested)
                return;
            ProfileTrackSummaryText.Text = "Cloud unavailable";
            ProfileTrackEmptyPanel.IsVisible = true;
            ProfileTrackEmptyTitle.Text = "Could not load tracks";
            ProfileTrackEmptyDescription.Text = exception.Message;
        }
    }

    private void RefreshProfileTrackStates()
    {
        if (_cloudProfileTracks.Count == 0)
            return;

        var localTracks = MusicLibraryService.Current.GetTracksForLibraryView();
        var localByVideoId = localTracks
            .Select(track => (Track: track, VideoId: string.IsNullOrWhiteSpace(track.SourceVideoId)
                ? YouTubeUrlNormalizer.ExtractVideoId(track.CanonicalUrl)
                : track.SourceVideoId))
            .Where(item => !string.IsNullOrWhiteSpace(item.VideoId))
            .GroupBy(item => item.VideoId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Track, StringComparer.Ordinal);
        var localByCanonicalUrl = localTracks
            .Where(track => !string.IsNullOrWhiteSpace(track.CanonicalUrl))
            .GroupBy(track => track.CanonicalUrl, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var ratingNames = MusicLibraryService.Current.GetRatings()
            .ToDictionary(rating => rating.Id, rating => rating.Name);
        var activeUrls = MusicLibraryService.Current.GetActiveImportCanonicalUrls();

        foreach (var item in _cloudProfileTracks)
        {
            localByVideoId.TryGetValue(item.SourceVideoId, out var localTrack);
            if (localTrack is null)
                localByCanonicalUrl.TryGetValue(item.CanonicalUrl, out localTrack);
            var localRating = localTrack?.RatingId is int ratingId
                ? ratingNames.GetValueOrDefault(ratingId)
                : null;
            item.UpdateLocalState(localTrack, activeUrls.Contains(item.CanonicalUrl), localRating);
        }
    }

    private void OnProfileTrackSearchChanged(object? sender, TextChangedEventArgs e) => ApplyProfileTrackFilter();

    private void ApplyProfileTrackFilter()
    {
        if (!ProfileDetailView.IsVisible)
            return;

        var availableCount = _cloudProfileTracks.Count(track => track.IsAvailable);
        var reviewCount = _cloudProfileTracks.Count(track => track.IsReview);
        var libraryCount = _cloudProfileTracks.Count(track => track.IsInLibrary);
        var rejectedCount = _cloudProfileTracks.Count(track => track.IsRejected);
        ProfileAvailableCountText.Text = availableCount.ToString("N0");
        ProfileReviewCountText.Text = reviewCount.ToString("N0");
        ProfileLibraryCountText.Text = libraryCount.ToString("N0");
        ProfileRejectedCountText.Text = rejectedCount.ToString("N0");

        var search = ProfileTrackSearchBox.Text?.Trim() ?? string.Empty;
        IEnumerable<CloudProfileTrackDisplay> tracks = _cloudProfileTracks;
        if (search.Length > 0)
            tracks = tracks.Where(track => track.Matches(search));
        tracks = _profileTrackFilter switch
        {
            ProfileTrackFilter.Available => tracks.Where(track => track.IsAvailable),
            ProfileTrackFilter.Review => tracks.Where(track => track.IsReview),
            ProfileTrackFilter.InLibrary => tracks.Where(track => track.IsInLibrary),
            ProfileTrackFilter.Rejected => tracks.Where(track => track.IsRejected),
            _ => tracks.Where(track => track.IsAvailable)
        };
        tracks = _profileTrackSortIndex == 1
            ? tracks.OrderByDescending(track => track.HasOwnerRating)
                .ThenByDescending(track => track.OwnerRatingSortOrder)
                .ThenBy(track => track.Title, StringComparer.OrdinalIgnoreCase)
            : tracks.OrderBy(track => track.Title, StringComparer.OrdinalIgnoreCase);
        var visible = tracks.ToList();
        ProfileTrackList.ItemsSource = visible;
        ProfileTrackSummaryText.Text = $"{_cloudProfileTracks.Count:N0} public tracks · {libraryCount:N0} in your library";
        ProfileTrackEmptyPanel.IsVisible = visible.Count == 0;
        if (_cloudProfileTracks.Count == 0)
        {
            ProfileTrackEmptyTitle.Text = "No public tracks";
            ProfileTrackEmptyDescription.Text = "This profile has not synchronized any public tracks.";
            return;
        }
        if (search.Length > 0)
        {
            ProfileTrackEmptyTitle.Text = "No matching tracks";
            ProfileTrackEmptyDescription.Text = "Try another title, channel, genre or tag.";
            return;
        }

        (ProfileTrackEmptyTitle.Text, ProfileTrackEmptyDescription.Text) = _profileTrackFilter switch
        {
            ProfileTrackFilter.Available => ("No available tracks", "Every shared track is already part of your local workflow."),
            ProfileTrackFilter.Review => ("Nothing to review", "Downloaded tracks awaiting your rating will appear here."),
            ProfileTrackFilter.InLibrary => ("No shared tracks in your library", "Tracks you accept from this profile will appear here."),
            ProfileTrackFilter.Rejected => ("No rejected tracks", "Tracks you decline from this profile will appear here."),
            _ => ("No public tracks", "This profile has not synchronized any public tracks.")
        };
    }

    private void OnProfileAvailableFilterClicked(object? sender, RoutedEventArgs e) =>
        SetProfileTrackFilter(ProfileTrackFilter.Available);

    private void OnProfileReviewFilterClicked(object? sender, RoutedEventArgs e) =>
        SetProfileTrackFilter(ProfileTrackFilter.Review);

    private void OnProfileLibraryFilterClicked(object? sender, RoutedEventArgs e) =>
        SetProfileTrackFilter(ProfileTrackFilter.InLibrary);

    private void OnProfileRejectedFilterClicked(object? sender, RoutedEventArgs e) =>
        SetProfileTrackFilter(ProfileTrackFilter.Rejected);

    private void SetProfileTrackFilter(ProfileTrackFilter filter, bool refresh = true)
    {
        _profileTrackFilter = filter;
        var buttons = new Dictionary<ProfileTrackFilter, Button>
        {
            [ProfileTrackFilter.Available] = ProfileAvailableFilterButton,
            [ProfileTrackFilter.Review] = ProfileReviewFilterButton,
            [ProfileTrackFilter.InLibrary] = ProfileLibraryFilterButton,
            [ProfileTrackFilter.Rejected] = ProfileRejectedFilterButton
        };
        foreach (var pair in buttons)
        {
            var selected = pair.Key == filter;
            pair.Value.Background = selected
                ? new SolidColorBrush(Color.Parse("#293E6591"))
                : Brushes.Transparent;
            pair.Value.BorderBrush = selected
                ? new SolidColorBrush(Color.Parse("#6B83A9CA"))
                : new SolidColorBrush(Color.Parse("#26FFFFFF"));
            pair.Value.Foreground = ThemeResources.Brush(selected
                ? "Theme.Brush.TextPrimary"
                : "Theme.Brush.TextSecondary");
        }

        if (refresh)
            ApplyProfileTrackFilter();
    }

    private void OnProfileTrackSortOptionClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }
            || !int.TryParse(tag, out var index))
            return;

        _profileTrackSortIndex = index;
        ProfileTrackSortText.Text = index == 1 ? "Best rated" : "Name";
        RefreshProfileTrackSortOptions();
        ProfileTrackSortButton.Flyout?.Hide();
        ApplyProfileTrackFilter();
        ProfileTrackList.SelectedItem = null;
        Dispatcher.UIThread.Post(() =>
        {
            var scrollViewer = ProfileTrackList
                .GetVisualDescendants()
                .OfType<ScrollViewer>()
                .FirstOrDefault();
            if (scrollViewer is not null)
                scrollViewer.Offset = new Vector(scrollViewer.Offset.X, 0);
        }, DispatcherPriority.Background);
        e.Handled = true;
    }

    private void RefreshProfileTrackSortOptions()
    {
        var options = new (Button Button, Image Check)[]
        {
            (ProfileTrackSortNameOption, ProfileTrackSortNameCheck),
            (ProfileTrackSortRatingOption, ProfileTrackSortRatingCheck)
        };
        for (var index = 0; index < options.Length; index++)
        {
            var (button, check) = options[index];
            button.Classes.Remove("selected");
            if (index == _profileTrackSortIndex)
                button.Classes.Add("selected");
            check.Opacity = index == _profileTrackSortIndex ? 0.9 : 0;
        }
    }

    private async void OnProfileTrackDownloadClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: CloudProfileTrackDisplay item } || !item.CanDownload)
            return;

        item.MarkPreparing();
        try
        {
            var preview = await ImportQueueService.Current.PreviewAsync([item.CanonicalUrl]);
            if (preview.Items.All(previewItem => previewItem.Status != ImportQueueStatus.Queued))
            {
                RefreshProfileTrackStates();
                ApplyProfileTrackFilter();
                ToastRequested?.Invoke(preview.Items.FirstOrDefault()?.Detail ?? "Track could not be queued");
                return;
            }

            ImportQueueService.Current.Queue(preview);
            item.MarkImporting();
            ApplyProfileTrackFilter();
            ToastRequested?.Invoke("Track added to the normal import queue");
        }
        catch (Exception exception)
        {
            RefreshProfileTrackStates();
            ApplyProfileTrackFilter();
            ToastRequested?.Invoke($"Could not queue track: {exception.Message}");
        }
    }

    private async void OnProfileTrackReviewClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: CloudProfileTrackDisplay { CanReview: true, LocalTrackId: int trackId } })
            return;
        var track = await Task.Run(() => MusicLibraryService.Current.GetTrackById(trackId));
        if (track is null)
            return;
        EditRequested?.Invoke(track);
        ToastRequested?.Invoke("Choose a rating to accept this track or decline it");
    }

    private async void OnProfileTrackPlayClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: CloudProfileTrackDisplay { CanPlay: true, LocalTrackId: int trackId } })
            return;
        var track = await Task.Run(() => MusicLibraryService.Current.GetTrackById(trackId));
        if (track is null)
            return;
        _activePreviewTrackId = track.Id;
        PreviewRequested?.Invoke(track);
    }

    private async void OnProfileTrackTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: CloudProfileTrackDisplay { CanOpenEditor: true, LocalTrackId: int trackId } })
            return;
        if (e.Source is Visual source
            && (source is Button || source.GetVisualAncestors().OfType<Button>().Any()))
            return;
        var track = await Task.Run(() => MusicLibraryService.Current.GetTrackById(trackId));
        if (track is null)
            return;
        EditRequested?.Invoke(track);
        e.Handled = true;
    }

    private void OnRefreshProfileTracksClicked(object? sender, RoutedEventArgs e)
    {
        if (_selectedCloudProfile is { } profile)
            _ = LoadProfileTracksAsync(profile);
    }

    private void OnBackToProfilesClicked(object? sender, RoutedEventArgs e)
    {
        CancelProfileLoad();
        ProfileDetailView.IsVisible = false;
        ProfileHubView.IsVisible = true;
        _selectedCloudProfile = null;
        ApplyProfileFilter();
    }

    private void CancelProfileLoad()
    {
        _profileLoadCts?.Cancel();
        _profileLoadCts?.Dispose();
        _profileLoadCts = null;
    }

    private void CancelProfileImageLoad()
    {
        _profileImageLoadCts?.Cancel();
        _profileImageLoadCts?.Dispose();
        _profileImageLoadCts = null;
    }

    private void OpenChannelDetail(ChannelHubItem channel, bool fromInbox = false)
    {
        _selectedHubChannel = channel;
        _selectedChannelId = channel.Id;
        _detailOpenedFromInbox = fromInbox;
        VideoSearchBox.Text = _videoSearchByChannel.GetValueOrDefault(channel.Id, string.Empty);
        VideoSearchBox.IsVisible = !string.IsNullOrWhiteSpace(VideoSearchBox.Text);
        VideoSearchToggleButton.Opacity = VideoSearchBox.IsVisible ? 1 : 0.86;
        SetVideoFilter(
            _videoFiltersByChannel.GetValueOrDefault(channel.Id, ChannelVideoFilter.Available),
            refresh: false);
        HubView.IsVisible = false;
        ProfileHubView.IsVisible = false;
        ProfileDetailView.IsVisible = false;
        InboxView.IsVisible = false;
        DetailView.IsVisible = true;
        PreviewClosed?.Invoke();
        UpdateDetailHeader();
        RefreshVideos();
        if (channel.IsFollowed
            && string.IsNullOrWhiteSpace(channel.LastCheckedAt)
            && !string.IsNullOrWhiteSpace(channel.SourceUrl))
        {
            ChannelHubBackgroundService.Current.RequestEnrichment(channel);
        }
        else if (channel.IsFollowed)
        {
            ChannelMetadataService.Current.RequestChannel(channel.Id, 1);
        }
    }

    private void UpdateDetailHeader()
    {
        if (_selectedHubChannel is not { } channel)
            return;

        SelectedChannelText.Text = channel.Name;
        DetailMonogramText.Text = channel.Monogram;
        DetailChannelArtwork.Source = channel.Artwork;
        DetailChannelArtwork.IsVisible = channel.HasArtwork;
        DetailMonogramText.IsVisible = channel.ShowMonogram;
        DetailSubscribersText.Text = channel.FollowerCountText;
        DetailViewsText.Text = channel.TotalViewCountText;
        DetailPlayCountText.Text = channel.PlayCount.ToString("N0");
        DetailAverageRatingText.Text = channel.AverageRatingScaleText;
        ToolTip.SetTip(DetailAverageRatingText, channel.RatingText);
        DetailFollowButton.Content = channel.FollowGlyph;
        ToolTip.SetTip(DetailFollowButton, channel.FollowToolTip);
        DetailFollowButton.Classes.Remove("following");
        if (channel.IsFollowed)
            DetailFollowButton.Classes.Add("following");
        UpdateRefreshPresentation();
    }

    private void OnBackToHubClicked(object? sender, RoutedEventArgs e)
    {
        ClearActivePreview();
        PreviewClosed?.Invoke();
        DetailView.IsVisible = false;
        if (_detailOpenedFromInbox)
        {
            HubView.IsVisible = false;
            InboxView.IsVisible = true;
            _detailOpenedFromInbox = false;
            RefreshInbox();
            RefreshInboxBadge();
        }
        else
        {
            InboxView.IsVisible = false;
            ProfileHubView.IsVisible = false;
            ProfileDetailView.IsVisible = false;
            HubView.IsVisible = true;
            ApplyHubFilter();
        }
    }

    private async void RefreshInboxBadge()
    {
        var unread = await Task.Run(MusicLibraryService.Current.GetUnreadChannelNotificationCount);
        if (!IsVisible)
            return;
        InboxBadge.IsVisible = unread > 0;
        InboxBadgeText.Text = unread > 99 ? "99+" : unread.ToString();
        if (InboxView.IsVisible)
            RefreshInbox();
    }

    private async void RefreshInbox()
    {
        var notifications = await Task.Run(MusicLibraryService.Current.GetChannelNotifications);
        if (!IsVisible || !InboxView.IsVisible)
            return;
        InboxItems.ItemsSource = notifications;
        EmptyInboxText.IsVisible = notifications.Count == 0;
        var unread = notifications.Count(notification => !notification.IsRead);
        InboxSummaryText.Text = notifications.Count == 0
            ? "No pending channel updates"
            : $"{unread} unread · {notifications.Count} awaiting your review";
    }

    private void OnInboxClicked(object? sender, RoutedEventArgs e)
    {
        PreviewClosed?.Invoke();
        HubView.IsVisible = false;
        ProfileHubView.IsVisible = false;
        ProfileDetailView.IsVisible = false;
        DetailView.IsVisible = false;
        InboxView.IsVisible = true;
        RefreshInbox();
    }

    private void OnBackFromInboxClicked(object? sender, RoutedEventArgs e)
    {
        InboxView.IsVisible = false;
        DetailView.IsVisible = false;
        ProfileHubView.IsVisible = false;
        ProfileDetailView.IsVisible = false;
        HubView.IsVisible = true;
        ApplyHubFilter();
        RefreshInboxBadge();
    }

    private void OnOpenNotificationClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ChannelNotification notification })
            return;

        _ = Task.Run(() => MusicLibraryService.Current.MarkChannelNotificationRead(notification.Id));
        var channel = _hubChannels.FirstOrDefault(item => item.Id == notification.ChannelId);
        if (channel is null)
        {
            _pendingOpenChannelId = notification.ChannelId;
            _videoSearchByChannel[notification.ChannelId] = notification.Title;
            _videoFiltersByChannel[notification.ChannelId] = ChannelVideoFilter.Available;
            RefreshChannels();
            return;
        }

        _videoSearchByChannel[channel.Id] = notification.Title;
        _videoFiltersByChannel[channel.Id] = ChannelVideoFilter.Available;
        OpenChannelDetail(channel, fromInbox: true);
        RefreshInboxBadge();
        e.Handled = true;
    }

    private async void OnArchiveNotificationClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ChannelNotification notification })
            return;
        await Task.Run(() => MusicLibraryService.Current.ArchiveChannelNotification(notification.Id));
        RefreshInbox();
        RefreshInboxBadge();
        e.Handled = true;
    }

    private async void OnFollowChannelClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ChannelHubItem channel })
            return;
        var followed = !channel.IsFollowed;
        await Task.Run(() => MusicLibraryService.Current.SetChannelFollowed(channel.Id, followed));
        RefreshChannels();
        ToastRequested?.Invoke(followed ? "Channel followed" : "Channel removed from Following");
        e.Handled = true;
        if (followed && string.IsNullOrWhiteSpace(channel.LastCheckedAt))
            ChannelHubBackgroundService.Current.RequestEnrichment(channel);
    }

    private async void OnDetailFollowClicked(object? sender, RoutedEventArgs e)
    {
        if (_selectedHubChannel is not { } channel)
            return;
        var followed = !channel.IsFollowed;
        await Task.Run(() => MusicLibraryService.Current.SetChannelFollowed(channel.Id, followed));
        RefreshChannels();
        ToastRequested?.Invoke(followed ? "Channel followed" : "Channel removed from Following");
        if (followed && string.IsNullOrWhiteSpace(channel.LastCheckedAt))
            ChannelHubBackgroundService.Current.RequestEnrichment(channel);
        e.Handled = true;
    }

    private async void OnUrlTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_processingPastedUrl)
            return;

        var url = UrlBox.Text?.Trim();
        if (!LooksLikeYouTubeUrl(url))
            return;

        _processingPastedUrl = true;
        UrlBox.IsEnabled = false;
        try
        {
            await RunRefreshAsync(
                progress => MusicLibraryService.Current.AddOrRefreshChannelAsync(url!, progress, _refreshCts!.Token),
                "Channel added",
                channelId: null);
            UrlBox.Text = string.Empty;
        }
        finally
        {
            UrlBox.IsEnabled = true;
            _processingPastedUrl = false;
        }
    }

    private static bool LooksLikeYouTubeUrl(string? text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host.TrimStart('.');
        return (host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase))
               && uri.AbsolutePath.Length > 1;
    }

    private async void OnRefreshSelectedClicked(object? sender, RoutedEventArgs e)
    {
        if (_selectedHubChannel is not { } channel || string.IsNullOrWhiteSpace(channel.SourceUrl))
            return;

        await RunRefreshAsync(
            progress => MusicLibraryService.Current.AddOrRefreshChannelAsync(
                channel.SourceUrl,
                progress,
                _refreshCts!.Token),
            "Channel refreshed",
            channel.Id);
    }

    private async void OnDeleteChannelClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not ChannelSubscription channel)
            return;

        e.Handled = true;

        if (!await Task.Run(() => MusicLibraryService.Current.DeleteChannel(channel.Id)))
        {
            ToastRequested?.Invoke("Channel could not be deleted");
            return;
        }

        if (_selectedChannelId == channel.Id)
            _selectedChannelId = -1;
        RefreshChannels();
        ToastRequested?.Invoke("Channel deleted");
    }

    private async System.Threading.Tasks.Task RunRefreshAsync(
        Func<IProgress<string>, System.Threading.Tasks.Task<ChannelRefreshResult>> refresh,
        string successText,
        int? channelId)
    {
        if (_refreshCts is not null)
        {
            ToastRequested?.Invoke("Another channel refresh is already running");
            return;
        }

        var refreshCts = new CancellationTokenSource();
        _refreshCts = refreshCts;
        SetRefreshState(true, "Checking channel…", channelId);
        var progress = new Progress<string>(message => SetRefreshState(true, message, channelId));

        try
        {
            var result = await refresh(progress);
            if (!result.Success)
            {
                ToastRequested?.Invoke(result.Error ?? "Channel refresh failed");
                return;
            }

            RefreshChannels();
            if (channelId is int refreshedChannelId)
            {
                await Task.Run(() => MusicLibraryService.Current.ResetChannelMetadataIssues(refreshedChannelId));
                ChannelMetadataService.Current.RequestChannel(refreshedChannelId, 1);
            }
            ToastRequested?.Invoke(result.AddedCount > 0
                ? $"{successText}: {result.AddedCount} new videos"
                : $"{successText}: no new videos");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            ToastRequested?.Invoke($"Channel refresh failed: {exception.Message}");
        }
        finally
        {
            if (ReferenceEquals(_refreshCts, refreshCts))
            {
                _refreshCts = null;
                SetRefreshState(false, channelId: channelId);
                UpdateDownloadSummary();
            }
            refreshCts.Dispose();
        }
    }

    private void SetRefreshState(bool active, string? message = null, int? channelId = null)
    {
        _refreshChannelId = active ? channelId : null;
        _refreshMessage = active ? message ?? "Refreshing…" : string.Empty;
        UpdateRefreshPresentation();
    }

    private void UpdateRefreshPresentation()
    {
        var active = _refreshCts is not null;
        var selectedChannelIsRefreshing = active
                                          && (_refreshChannelId is null
                                              || _refreshChannelId == _selectedChannelId);
        DetailRefreshButton.IsEnabled = !active;
        DetailRefreshButton.Opacity = active ? 0.35 : 1;
        ToolTip.SetTip(DetailRefreshButton, active
            ? selectedChannelIsRefreshing
                ? _refreshMessage
                : "Another channel refresh is currently running"
            : "Refresh channel");
        DetailRefreshStatusText.IsVisible = active && DetailView.IsVisible;
        DetailRefreshStatusText.Text = !active
            ? string.Empty
            : selectedChannelIsRefreshing
                ? _refreshMessage
                : "Another channel is refreshing…";
        StatusText.Text = active ? _refreshMessage : string.Empty;
    }

    private void RefreshVideos()
    {
        if (_selectedChannelId < 0)
        {
            VideoList.ItemsSource = null;
            VideoSummaryText.Text = "No channel selected";
            return;
        }

        var channelId = _selectedChannelId;
        var generation = Interlocked.Increment(ref _videoLoadGeneration);
        VideoSummaryText.Text = "Loading videos…";
        _ = LoadVideosAsync(channelId, generation);
    }

    private async Task LoadVideosAsync(int channelId, int generation)
    {
        List<ChannelVideo> rawVideos;
        try
        {
            rawVideos = await Task.Run(() => MusicLibraryService.Current.GetChannelVideos(channelId));
        }
        catch (Exception exception)
        {
            if (generation == _videoLoadGeneration && channelId == _selectedChannelId)
            {
                VideoSummaryText.Text = "Could not load videos";
                EmptyVideoPanel.IsVisible = true;
                EmptyVideoTitleText.Text = "Videos temporarily unavailable";
                EmptyVideoDescriptionText.Text = exception.Message;
            }
            return;
        }

        if (generation != _videoLoadGeneration || channelId != _selectedChannelId)
            return;
        // ChannelVideoDisplay creates Avalonia brushes and therefore belongs on
        // the UI thread. Only the database read runs in the background.
        _currentVideos = rawVideos
            .Select(video => new ChannelVideoDisplay(video))
            .ToList();
        ApplyActivePreviewMarker();
        ApplyVideoView();
    }

    private void ApplyVideoView()
    {
        if (_selectedChannelId < 0)
            return;

        var availableCount = _currentVideos.Count(video => video.IsAvailable);
        var readyCount = _currentVideos.Count(video => video.IsPendingRating);
        var libraryCount = _currentVideos.Count(video => video.IsInLibrary);
        var rejectedCount = _currentVideos.Count(video => video.IsRejected);
        var missingMetadataCount = _currentVideos.Count(video => video.IsMissingMetadata);
        var issueCount = _currentVideos.Count(video => video.HasIssue);
        AvailableVideosCountText.Text = availableCount.ToString("N0");
        ReadyVideosCountText.Text = readyCount.ToString("N0");
        LibraryVideosCountText.Text = libraryCount.ToString("N0");
        RejectedVideosCountText.Text = rejectedCount.ToString("N0");
        IssueVideosCountText.Text = issueCount.ToString("N0");
        IssueVideosFilterButton.IsVisible = issueCount > 0 || _videoFilter == ChannelVideoFilter.Issues;
        LoadVideoDetailsButton.IsVisible = missingMetadataCount > 0;
        LoadVideoDetailsText.Text = missingMetadataCount == 1
            ? "Load video details · 1"
            : $"Load video details · {missingMetadataCount:N0}";

        var search = VideoSearchBox.Text?.Trim() ?? string.Empty;
        IEnumerable<ChannelVideoDisplay> videos = _currentVideos;
        if (search.Length > 0)
            videos = videos.Where(video => video.Title.Contains(search, StringComparison.OrdinalIgnoreCase));

        videos = _videoFilter switch
        {
            ChannelVideoFilter.Available => videos.Where(video => video.IsAvailable),
            ChannelVideoFilter.Review => videos.Where(video => video.IsPendingRating),
            ChannelVideoFilter.InLibrary => videos.Where(video => video.IsInLibrary),
            ChannelVideoFilter.Rejected => videos.Where(video => video.IsRejected),
            ChannelVideoFilter.Issues => videos.Where(video => video.HasIssue),
            _ => videos.Where(video => video.IsAvailable)
        };

        var visible = videos.ToList();

        _loadingVideos = true;
        try
        {
            VideoList.ItemsSource = visible;
        }
        finally
        {
            _loadingVideos = false;
        }
        UpdateEmptyVideoState(visible.Count, search);
        UpdateVideoSummary(visible.Count);
    }

    private void UpdateEmptyVideoState(int visibleCount, string search)
    {
        EmptyVideoPanel.IsVisible = visibleCount == 0;
        if (visibleCount > 0)
            return;

        if (search.Length > 0)
        {
            EmptyVideoTitleText.Text = "No matching videos";
            EmptyVideoDescriptionText.Text = "Try another title or clear the search.";
            return;
        }

        (EmptyVideoTitleText.Text, EmptyVideoDescriptionText.Text) = _videoFilter switch
        {
            ChannelVideoFilter.Available => ("No available videos", "Every discovered video has already moved through the workflow."),
            ChannelVideoFilter.Review => ("Nothing to review", "Downloaded videos awaiting a rating will appear here."),
            ChannelVideoFilter.InLibrary => ("No local tracks", "Downloaded channel tracks will appear here."),
            ChannelVideoFilter.Rejected => ("No rejected tracks", "Declined tracks with retained audio will appear here."),
            ChannelVideoFilter.Issues => ("No issues", "Metadata and downloads are currently healthy."),
            _ => ("No available videos", "Refresh the channel to retrieve its uploads.")
        };
    }

    private async void OnVideoCheckClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not ChannelVideoDisplay item)
            return;
        if (_loadingVideos || !item.CanReview)
            return;

        var track = item.TrackId is int trackId
            ? await Task.Run(() => MusicLibraryService.Current.GetTrackById(trackId))
            : null;
        if (track is null)
        {
            ToastRequested?.Invoke("Audio is not downloaded yet");
            return;
        }
        EditRequested?.Invoke(track);
        ToastRequested?.Invoke("Choose a rating to add this track to your library");
    }

    private async void OnVideoRowTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: ChannelVideoDisplay item }
            || !item.CanOpenEditor
            || item.TrackId is not int trackId)
            return;
        if (e.Source is Visual source
            && (source is Button || source.GetVisualAncestors().OfType<Button>().Any()))
            return;

        var track = await Task.Run(() => MusicLibraryService.Current.GetTrackById(trackId));
        if (track is null)
            return;

        EditRequested?.Invoke(track);
        if (item.IsPendingRating)
            ToastRequested?.Invoke("Choose a rating to add this track to your library");
        e.Handled = true;
    }

    private async void OnVideoDownloadClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not ChannelVideoDisplay item || !item.CanDownload)
            return;

        try
        {
            var preview = await ImportQueueService.Current.PreviewAsync([item.CanonicalUrl]);
            if (preview.Items.All(candidate => candidate.Status != ImportQueueStatus.Queued))
            {
                ToastRequested?.Invoke(preview.Items.FirstOrDefault()?.Detail ?? "Track could not be queued");
                return;
            }
            ImportQueueService.Current.Queue(preview);
            ToastRequested?.Invoke("Track added to the download queue");
        }
        catch (Exception exception)
        {
            ToastRequested?.Invoke($"Could not queue track: {exception.Message}");
        }
    }

    private async void OnVideoRetryIssueClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button
            || button.DataContext is not ChannelVideoDisplay item
            || !item.CanRetryError)
            return;

        button.IsEnabled = false;
        try
        {
            if (!await Task.Run(() => MusicLibraryService.Current.RetryChannelVideoIssue(item.Id)))
            {
                ToastRequested?.Invoke("This error is no longer active");
                RefreshVideos();
                return;
            }

            RefreshVideos();
            UpdateDownloadSummary();
            ToastRequested?.Invoke(item.RetryStartedText);
        }
        catch (Exception exception)
        {
            WorkflowLog.Error("channel-retry", $"Could not retry video {item.Id}.", exception);
            ToastRequested?.Invoke("Could not retry this error");
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async void OnVideoSkipClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not ChannelVideoDisplay item || !item.CanDismiss)
            return;

        if (item.TrackId is null)
        {
            if (!await Task.Run(() => MusicLibraryService.Current.DismissChannelVideo(item.Id)))
            {
                ToastRequested?.Invoke("Video could not be removed from pending");
                return;
            }

            item.IsChecked = true;
            RefreshChannelSummaries();
            RefreshVideos();
            ToastRequested?.Invoke("Removed from pending");
            return;
        }

        var track = await Task.Run(() => MusicLibraryService.Current.SkipChannelVideo(item.Id));
        if (track is null)
        {
            ToastRequested?.Invoke("Audio is not downloaded yet");
            return;
        }
        item.IsChecked = true;
        PreviewClosed?.Invoke();
        TrackChanged?.Invoke(track.Id);
        RefreshChannelSummaries();
        RefreshVideos();
        ToastRequested?.Invoke("Skipped · audio kept");
    }

    private async void OnVideoPlayClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not ChannelVideoDisplay item
            || item.TrackId is not int trackId)
            return;
        var track = await Task.Run(() => MusicLibraryService.Current.GetTrackById(trackId));
        if (track is null)
            return;
        _activePreviewTrackId = track.Id;
        ApplyActivePreviewMarker();
        PreviewRequested?.Invoke(track);
    }

    public void ClearActivePreview()
    {
        _activePreviewTrackId = null;
        ApplyActivePreviewMarker();
    }

    private void ApplyActivePreviewMarker()
    {
        foreach (var video in _currentVideos)
            video.IsActive = _activePreviewTrackId is int trackId && video.TrackId == trackId;
    }

    private void OnVideoSearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (_selectedChannelId < 0)
            return;
        _videoSearchByChannel[_selectedChannelId] = VideoSearchBox.Text ?? string.Empty;
        ApplyVideoView();
    }

    private void OnVideoSearchToggleClicked(object? sender, RoutedEventArgs e) =>
        ToggleSearch(VideoSearchBox, VideoSearchToggleButton);

    private void OnVideoSearchKeyDown(object? sender, KeyEventArgs e) =>
        HandleSearchKeyDown(VideoSearchBox, VideoSearchToggleButton, e);

    private void OnVideoSearchLostFocus(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(
            () => UpdateSearchVisibility(VideoSearchBox, VideoSearchToggleButton),
            DispatcherPriority.Background);

    private static void ToggleSearch(TextBox searchBox, Button toggleButton)
    {
        if (searchBox.IsVisible)
        {
            searchBox.Text = string.Empty;
            searchBox.IsVisible = false;
            toggleButton.Opacity = 0.86;
            toggleButton.Focus();
            return;
        }

        searchBox.IsVisible = true;
        toggleButton.Opacity = 1;
        Dispatcher.UIThread.Post(() => searchBox.Focus(), DispatcherPriority.Background);
    }

    private static void HandleSearchKeyDown(TextBox searchBox, Button toggleButton, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            searchBox.Text = string.Empty;
            searchBox.IsVisible = false;
            toggleButton.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            toggleButton.Focus();
            e.Handled = true;
        }
    }

    private static void UpdateSearchVisibility(TextBox searchBox, Button toggleButton)
    {
        var hasSearch = !string.IsNullOrWhiteSpace(searchBox.Text);
        if (toggleButton.IsKeyboardFocusWithin)
            return;
        if (!searchBox.IsKeyboardFocusWithin && !hasSearch)
            searchBox.IsVisible = false;
        toggleButton.Opacity = searchBox.IsVisible || hasSearch ? 1 : 0.86;
    }

    private void OnAvailableVideosFilterClicked(object? sender, RoutedEventArgs e) =>
        SetVideoFilter(ChannelVideoFilter.Available);

    private void OnReadyVideosFilterClicked(object? sender, RoutedEventArgs e) =>
        SetVideoFilter(ChannelVideoFilter.Review);

    private void OnLibraryVideosFilterClicked(object? sender, RoutedEventArgs e) =>
        SetVideoFilter(ChannelVideoFilter.InLibrary);

    private void OnRejectedVideosFilterClicked(object? sender, RoutedEventArgs e) =>
        SetVideoFilter(ChannelVideoFilter.Rejected);

    private void OnIssueVideosFilterClicked(object? sender, RoutedEventArgs e) =>
        SetVideoFilter(ChannelVideoFilter.Issues);

    private void OnLoadVideoDetailsClicked(object? sender, RoutedEventArgs e)
    {
        if (_selectedHubChannel is not { } channel)
            return;

        var queued = ChannelMetadataService.Current.RequestChannelDetails(channel.Id);
        if (queued <= 0)
        {
            ToastRequested?.Invoke("Video details are already loaded or queued");
            return;
        }

        RefreshVideos();
        ToastRequested?.Invoke(queued == 1
            ? "Loading details for 1 video"
            : $"Loading details for {queued:N0} videos");
        e.Handled = true;
    }

    private void SetVideoFilter(ChannelVideoFilter filter, bool refresh = true)
    {
        _videoFilter = filter;
        if (_selectedChannelId >= 0)
            _videoFiltersByChannel[_selectedChannelId] = filter;

        var buttons = new Dictionary<ChannelVideoFilter, Button>
        {
            [ChannelVideoFilter.Available] = AvailableVideosFilterButton,
            [ChannelVideoFilter.Review] = ReadyVideosFilterButton,
            [ChannelVideoFilter.InLibrary] = LibraryVideosFilterButton,
            [ChannelVideoFilter.Rejected] = RejectedVideosFilterButton,
            [ChannelVideoFilter.Issues] = IssueVideosFilterButton
        };
        foreach (var pair in buttons)
        {
            var selected = pair.Key == filter;
            pair.Value.Background = selected
                ? new SolidColorBrush(Color.Parse("#293E6591"))
                : Brushes.Transparent;
            pair.Value.BorderBrush = selected
                ? new SolidColorBrush(Color.Parse("#6B83A9CA"))
                : new SolidColorBrush(Color.Parse("#26FFFFFF"));
            pair.Value.Foreground = pair.Key == ChannelVideoFilter.Issues && !selected
                ? ThemeResources.Brush("Theme.Brush.Warning")
                : ThemeResources.Brush(selected
                    ? "Theme.Brush.TextPrimary"
                    : "Theme.Brush.TextSecondary");
        }

        if (refresh)
            ApplyVideoView();
    }

    private async void OnCopyVideoUrlClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not ChannelVideoDisplay item)
            return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            ToastRequested?.Invoke("Clipboard is not available");
            return;
        }

        await clipboard.SetTextAsync(item.CanonicalUrl);
        ToastRequested?.Invoke("Video URL copied");
    }

    private void RefreshChannelSummaries()
    {
        RefreshChannels();
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
    {
        CancelProfileLoad();
        CancelProfileImageLoad();
        ClearActivePreview();
        CloseRequested?.Invoke();
    }

    private void UpdateStatus()
    {
        ApplyHubFilter();
    }

    private void RefreshChannelStates()
    {
        if (!IsVisible)
            return;
        if (DetailView.IsVisible && _selectedChannelId >= 0)
            RefreshVideos();
        if (ProfileDetailView.IsVisible)
        {
            RefreshProfileTrackStates();
            ApplyProfileTrackFilter();
        }
        RefreshChannels();
    }

    private void UpdateVideoSummary(int? visibleCount = null)
    {
        if (_selectedChannelId < 0)
        {
            VideoSummaryText.Text = "No channel selected";
            return;
        }

        var availableCount = _currentVideos.Count(video => video.IsAvailable);
        var library = _currentVideos.Count(video => video.IsInLibrary);
        var issues = _currentVideos.Count(video => video.HasIssue);
        var shown = visibleCount ?? _currentVideos.Count;
        var shownPrefix = shown == _currentVideos.Count ? string.Empty : $"{shown:N0} shown · ";
        VideoSummaryText.Text = $"{shownPrefix}{_currentVideos.Count:N0} videos · {availableCount:N0} available · " +
                                $"{library:N0} in library · {issues:N0} issues";
    }

}

public sealed class ChannelVideoDisplay : INotifyPropertyChanged
{
    private bool _isChecked;
    private double _opacity;
    private double _checkOpacity;
    private IBrush _background = Brushes.Transparent;
    private IBrush _checkBackground = Brushes.Transparent;
    private IBrush _checkBorder = ThemeResources.Brush("Theme.Brush.Border");
    private TextDecorationCollection? _textDecorations;
    private int? _trackId;
    private string _statusText = string.Empty;
    private bool _canPlay;
    private bool _canReview;
    private double _actionOpacity;
    private bool _isActive;
    private bool _canDismiss;
    private bool _canDownload;
    private TrackLibraryState? _libraryState;
    private string _dismissToolTip = "Skip (keep audio)";
    private IBrush _borderBrush = Brushes.Transparent;
    private Thickness _borderThickness;
    private IBrush _titleBrush = ThemeResources.Brush("Theme.Brush.TextPrimary");

    public ChannelVideoDisplay(ChannelVideo video)
    {
        Id = video.Id;
        CanonicalUrl = video.CanonicalUrl;
        Title = video.Title;
        DurationText = video.DurationSeconds is int seconds ? FormatDuration(seconds) : "";
        UploadedText = FormatUploadDate(video.UploadedAt);
        MetadataSummaryText = BuildMetadataSummary(video, UploadedText);
        MetadataErrorDetails = TrimErrorDetails(video.MetadataError);
        MetadataErrorSummary = ErrorSummary(MetadataErrorDetails);
        MetadataStatus = video.MetadataStatus;
        RatingName = video.RatingName?.Trim() ?? string.Empty;
        LibraryDetailsText = BuildLibraryDetails(video);
        _libraryState = video.LibraryState;
        _trackId = video.TrackId;
        SetChecked(video.IsChecked);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Id { get; }
    public string CanonicalUrl { get; }
    public string Title { get; }
    public string UploadedText { get; }
    public bool HasUploadDate => UploadedText.Length > 0;
    public string MetadataSummaryText { get; }
    public bool HasMetadataSummary => MetadataSummaryText.Length > 0;
    public string MetadataErrorSummary { get; }
    public string MetadataErrorDetails { get; }
    public bool HasMetadataError => MetadataErrorSummary.Length > 0;
    public ChannelMetadataStatus MetadataStatus { get; }
    public string RatingName { get; }
    public string LibraryDetailsText { get; }
    public bool HasLibraryDetails => LibraryDetailsText.Length > 0;
    public bool IsChecked
    {
        get => _isChecked;
        set => SetChecked(value);
    }
    public string DurationText { get; }
    public int? TrackId
    {
        get => _trackId;
        private set => SetField(ref _trackId, value);
    }
    public bool IsPendingRating => TrackId is not null
        && !IsChecked
        && (_libraryState is null or TrackLibraryState.PendingRating);
    public bool IsInLibrary => TrackId is not null && _libraryState == TrackLibraryState.Active;
    public bool IsRejected => TrackId is not null && _libraryState == TrackLibraryState.Rejected;
    public bool HasIssue => TrackId is null && MetadataStatus == ChannelMetadataStatus.Failed;
    public bool IsAvailable => TrackId is null && !HasIssue;
    public bool IsMissingMetadata => !HasMetadataError
        && MetadataStatus == ChannelMetadataStatus.Pending;
    public bool CanOpenEditor => IsInLibrary || IsPendingRating || IsRejected;
    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }
    public bool HasStatusText => StatusText.Length > 0;
    public bool CanPlay
    {
        get => _canPlay;
        private set => SetField(ref _canPlay, value);
    }
    public bool CanReview
    {
        get => _canReview;
        private set => SetField(ref _canReview, value);
    }
    public bool CanDismiss
    {
        get => _canDismiss;
        private set => SetField(ref _canDismiss, value);
    }
    public bool CanDownload
    {
        get => _canDownload;
        private set => SetField(ref _canDownload, value);
    }
    public bool CanRetryError => MetadataStatus == ChannelMetadataStatus.Failed;
    public string RetryErrorToolTip => "Retry metadata";
    public string RetryStartedText => "Metadata queued again";
    public string DismissToolTip
    {
        get => _dismissToolTip;
        private set => SetField(ref _dismissToolTip, value);
    }
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value)
                return;
            SetField(ref _isActive, value);
            UpdateVisualState();
        }
    }
    public double ActionOpacity
    {
        get => _actionOpacity;
        private set => SetField(ref _actionOpacity, value);
    }
    public double Opacity
    {
        get => _opacity;
        private set => SetField(ref _opacity, value);
    }
    public IBrush Background
    {
        get => _background;
        private set => SetField(ref _background, value);
    }
    public IBrush BorderBrush
    {
        get => _borderBrush;
        private set => SetField(ref _borderBrush, value);
    }
    public Thickness BorderThickness
    {
        get => _borderThickness;
        private set => SetField(ref _borderThickness, value);
    }
    public double CheckOpacity
    {
        get => _checkOpacity;
        private set => SetField(ref _checkOpacity, value);
    }
    public IBrush CheckBackground
    {
        get => _checkBackground;
        private set => SetField(ref _checkBackground, value);
    }
    public IBrush CheckBorder
    {
        get => _checkBorder;
        private set => SetField(ref _checkBorder, value);
    }
    public TextDecorationCollection? TextDecorations
    {
        get => _textDecorations;
        private set => SetField(ref _textDecorations, value);
    }
    public IBrush TitleBrush
    {
        get => _titleBrush;
        private set => SetField(ref _titleBrush, value);
    }

    private void SetChecked(bool value)
    {
        SetField(ref _isChecked, value, nameof(IsChecked));
        CheckBackground = ThemeResources.Brush(value ? "Theme.Brush.Success" : "Theme.Brush.Surface");
        CheckBorder = ThemeResources.Brush(value ? "Theme.Brush.Accent" : "Theme.Brush.Border");
        CheckOpacity = value ? 1 : 0.58;
        TextDecorations = value && !IsInLibrary && !IsRejected
            ? Avalonia.Media.TextDecorations.Strikethrough
            : null;
        UpdateState();
        UpdateVisualState();
    }

    private void UpdateState()
    {
        var hasTrack = TrackId is not null;
        var pendingRating = hasTrack && !IsChecked
            && (_libraryState is null or TrackLibraryState.PendingRating);

        StatusText = hasTrack
            ? _libraryState switch
            {
                TrackLibraryState.Rejected => "Rejected",
                TrackLibraryState.Active => RatingName.Length > 0
                    ? $"In library · {RatingName}"
                    : "In library",
                _ => "Needs rating"
            }
            : string.Empty;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasStatusText)));

        CanDownload = !hasTrack
            && MetadataStatus is not ChannelMetadataStatus.Queued
                and not ChannelMetadataStatus.Loading;
        CanPlay = hasTrack;
        CanReview = pendingRating;
        CanDismiss = pendingRating;
        DismissToolTip = "Decline track (keep downloaded audio)";
        ActionOpacity = CanDownload || CanPlay || CanReview || CanDismiss ? 0.82 : 0.35;
    }

    private void UpdateVisualState()
    {
        Opacity = IsInLibrary || IsRejected || IsActive ? 1 : IsChecked ? 0.45 : 1;
        TitleBrush = IsInLibrary
            ? new SolidColorBrush(Color.Parse("#8FD19E"))
            : IsRejected
                ? new SolidColorBrush(Color.Parse("#E87878"))
            : IsPendingRating
                ? new SolidColorBrush(Color.Parse("#E6C65C"))
            : ThemeResources.Brush("Theme.Brush.TextPrimary");
        Background = IsActive
            ? ThemeResources.Brush("Theme.Brush.SurfaceSelected")
            : IsInLibrary
                ? new SolidColorBrush(Color.Parse("#142F6842"))
            : IsRejected
                ? new SolidColorBrush(Color.Parse("#182F1F22"))
            : IsChecked
                ? new SolidColorBrush(Color.Parse("#22192027"))
                : new SolidColorBrush(Color.Parse("#10203422"));
        BorderBrush = IsActive ? ThemeResources.Brush("Theme.Brush.Accent") : Brushes.Transparent;
        BorderThickness = IsActive ? new Thickness(1) : new Thickness(0);
    }

    private static string TrimErrorDetails(string? error)
    {
        var details = error?.Trim() ?? string.Empty;
        return details.Length > 4000 ? details[..4000] + "…" : details;
    }

    private static string BuildLibraryDetails(ChannelVideo video)
    {
        if (video.TrackId is null || video.LibraryState != TrackLibraryState.Active)
            return string.Empty;

        var parts = new List<string>();
        parts.Add(video.ListenCount == 1 ? "1 play" : $"{video.ListenCount:N0} plays");
        if (video.SkipCount > 0)
            parts.Add(video.SkipCount == 1 ? "1 skip" : $"{video.SkipCount:N0} skips");
        if (video.ListenedSeconds >= 60)
        {
            var listened = TimeSpan.FromSeconds(video.ListenedSeconds);
            parts.Add(listened.TotalHours >= 1
                ? $"{listened.TotalHours:0.#} h listened"
                : $"{Math.Max(1, (int)listened.TotalMinutes)} min listened");
        }

        return string.Join(" · ", parts);
    }

    private static string ErrorSummary(string details)
    {
        var lines = details.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var summary = lines.LastOrDefault(line => line.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                      ?? lines.LastOrDefault()
                      ?? string.Empty;
        return summary.Length > 260 ? summary[..260] + "…" : summary;
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static string FormatDuration(int seconds)
    {
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}"
            : $"{time.Minutes:D2}:{time.Seconds:D2}";
    }

    private static string FormatUploadDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        if (value.Length == 8
            && DateTime.TryParseExact(value, "yyyyMMdd", null,
                System.Globalization.DateTimeStyles.None, out var compactDate))
            return compactDate.ToString("dd MMM yyyy");
        return DateTime.TryParse(value, out var parsed)
            ? parsed.ToString("dd MMM yyyy")
            : value;
    }

    private static string BuildMetadataSummary(ChannelVideo video, string uploadedText)
    {
        var parts = new List<string>();
        if (uploadedText.Length > 0)
            parts.Add(uploadedText);
        if (video.ViewCount is long views)
            parts.Add($"{FormatCompactNumber(views)} views");
        if (video.LikeCount is long likes)
            parts.Add($"{FormatCompactNumber(likes)} likes");
        if (video.MetadataStatus is ChannelMetadataStatus.Queued or ChannelMetadataStatus.Loading)
            parts.Add("loading metadata…");
        return string.Join(" · ", parts);
    }

    private static string FormatCompactNumber(long value) => value switch
    {
        >= 1_000_000_000 => $"{value / 1_000_000_000d:0.#}B",
        >= 1_000_000 => $"{value / 1_000_000d:0.#}M",
        >= 1_000 => $"{value / 1_000d:0.#}K",
        _ => value.ToString()
    };

}

public sealed class CloudProfileDisplay : INotifyPropertyChanged, IDisposable
{
    private Bitmap? _avatar;

    public CloudProfileDisplay(CloudPublicProfileSummary profile)
    {
        UserId = profile.UserId;
        Username = profile.Username;
        BioText = string.IsNullOrWhiteSpace(profile.Bio) ? "No bio provided" : profile.Bio;
        HasRemoteAvatar = profile.HasProfileImage;
        TrackCountText = profile.TrackCount.ToString("N0");
        Monogram = string.IsNullOrWhiteSpace(profile.Username)
            ? "?"
            : profile.Username[..1].ToUpperInvariant();
        SynchronizedText = DateTimeOffset.TryParse(profile.SynchronizedAt, out var synchronizedAt)
            ? $"Synchronized {synchronizedAt.ToLocalTime():dd.MM.yyyy HH:mm}"
            : "Not synchronized yet";
    }

    public string UserId { get; }
    public string Username { get; }
    public string BioText { get; }
    public bool HasRemoteAvatar { get; }
    public string TrackCountText { get; }
    public string Monogram { get; }
    public string SynchronizedText { get; }
    public Bitmap? Avatar => _avatar;
    public bool HasAvatar => _avatar is not null;
    public bool ShowMonogram => _avatar is null;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetAvatar(byte[] image)
    {
        using var stream = new MemoryStream(image, writable: false);
        var avatar = new Bitmap(stream);
        var previous = _avatar;
        _avatar = avatar;
        previous?.Dispose();
        Notify(nameof(Avatar));
        Notify(nameof(HasAvatar));
        Notify(nameof(ShowMonogram));
    }

    public void Dispose()
    {
        _avatar?.Dispose();
        _avatar = null;
    }

    private void Notify([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class CloudProfileTrackDisplay : INotifyPropertyChanged
{
    private enum LocalProfileTrackState { Available, Review, InLibrary, Rejected }

    private string _localStatusText = "Available";
    private IBrush _localStatusBrush = ThemeResources.Brush("Theme.Brush.TextSecondary");
    private bool _canDownload = true;
    private bool _canReview;
    private int? _localTrackId;
    private LocalProfileTrackState _state = LocalProfileTrackState.Available;

    public CloudProfileTrackDisplay(CloudPublicLibraryTrack track, int? ownerRatingSortOrder = null)
    {
        SourceVideoId = track.SourceVideoId;
        CanonicalUrl = track.CanonicalUrl;
        Title = track.Title;
        OriginalTitle = track.OriginalTitle;
        DurationText = FormatDuration(track.DurationSeconds);
        ChannelText = string.IsNullOrWhiteSpace(track.ChannelName) ? "Unknown channel" : track.ChannelName;
        OwnerRatingText = $"Their rating: {track.Rating ?? "Not rated"}";
        OwnerRatingSortOrder = ownerRatingSortOrder;
        ClassificationText = BuildClassificationText(track);
        _searchText = string.Join(' ', new[]
        {
            track.Title,
            track.OriginalTitle,
            track.ChannelName,
            track.Rating,
            track.LanguageCode,
            string.Join(' ', track.Genres),
            string.Join(' ', track.Tags),
            string.Join(' ', track.EmotionalCharacter.Keys)
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private readonly string _searchText;

    public string SourceVideoId { get; }
    public string CanonicalUrl { get; }
    public string Title { get; }
    public string OriginalTitle { get; }
    public string DurationText { get; }
    public string ChannelText { get; }
    public string OwnerRatingText { get; }
    public int? OwnerRatingSortOrder { get; }
    public bool HasOwnerRating => OwnerRatingSortOrder is not null;
    public string ClassificationText { get; }
    public string LocalStatusText => _localStatusText;
    public IBrush LocalStatusBrush => _localStatusBrush;
    public bool CanDownload => _canDownload;
    public bool CanReview => _canReview;
    public bool CanPlay => _localTrackId is not null;
    public bool CanOpenEditor => _localTrackId is not null;
    public bool IsAvailable => _state == LocalProfileTrackState.Available;
    public bool IsReview => _state == LocalProfileTrackState.Review;
    public bool IsInLibrary => _state == LocalProfileTrackState.InLibrary;
    public bool IsRejected => _state == LocalProfileTrackState.Rejected;
    public int? LocalTrackId => _localTrackId;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool Matches(string search) =>
        _searchText.Contains(search, StringComparison.OrdinalIgnoreCase);

    public void UpdateLocalState(MusicTrack? localTrack, bool importing, string? localRating = null)
    {
        _localTrackId = localTrack?.Id;
        if (localTrack is not null)
        {
            switch (localTrack.LibraryState)
            {
                case TrackLibraryState.PendingRating:
                    _state = LocalProfileTrackState.Review;
                    SetState("Ready", "#E6B85C", canReview: true);
                    break;
                case TrackLibraryState.Active:
                    _state = LocalProfileTrackState.InLibrary;
                    SetState(string.IsNullOrWhiteSpace(localRating)
                        ? "In library"
                        : $"Your rating: {localRating}", "#75D49A");
                    break;
                case TrackLibraryState.Rejected:
                    _state = LocalProfileTrackState.Rejected;
                    SetState("Rejected", "#E87878");
                    break;
                default:
                    _state = LocalProfileTrackState.InLibrary;
                    SetState("In library", "#75D49A");
                    break;
            }
        }
        else if (importing)
        {
            _state = LocalProfileTrackState.Available;
            SetState("Importing…", "#79A9E8");
        }
        else
        {
            _state = LocalProfileTrackState.Available;
            SetState("Available", "#B9C1CA", canDownload: true);
        }
    }

    public void MarkPreparing() => SetState("Reading link…", "#79A9E8");
    public void MarkImporting() => SetState("Importing…", "#79A9E8");

    private void SetState(
        string text,
        string color,
        bool canDownload = false,
        bool canReview = false)
    {
        _localStatusText = text;
        _localStatusBrush = new SolidColorBrush(Color.Parse(color));
        _canDownload = canDownload;
        _canReview = canReview;
        Notify(nameof(LocalStatusText));
        Notify(nameof(LocalStatusBrush));
        Notify(nameof(CanDownload));
        Notify(nameof(CanReview));
        Notify(nameof(CanPlay));
        Notify(nameof(CanOpenEditor));
        Notify(nameof(IsAvailable));
        Notify(nameof(IsReview));
        Notify(nameof(IsInLibrary));
        Notify(nameof(IsRejected));
        Notify(nameof(LocalTrackId));
    }

    private static string BuildClassificationText(CloudPublicLibraryTrack track)
    {
        var parts = new List<string>();
        if (track.Genres.Count > 0)
            parts.Add($"Genres: {string.Join(", ", track.Genres)}");
        if (track.Tags.Count > 0)
            parts.Add($"Tags: {string.Join(", ", track.Tags)}");
        if (!string.IsNullOrWhiteSpace(track.LanguageCode))
            parts.Add($"Language: {track.LanguageCode}");
        return parts.Count == 0 ? "No shared classification" : string.Join(" · ", parts);
    }

    private static string FormatDuration(int? durationSeconds)
    {
        if (durationSeconds is not int seconds || seconds < 0)
            return "—";
        var duration = TimeSpan.FromSeconds(seconds);
        return duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");
    }

    private void Notify([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
