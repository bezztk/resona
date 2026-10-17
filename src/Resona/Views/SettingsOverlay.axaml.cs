using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.Svg.Skia;
using Resona.Models;
using Resona.Services;

namespace Resona.Views;

public partial class SettingsOverlay : UserControl
{
    private List<ModelGenre> _modelGenres = [];
    private List<ModelSubgenre> _modelSubgenres = [];
    private Dictionary<int, int> _genreUsageCounts = [];
    private Dictionary<int, int> _genreReferenceCounts = [];
    private readonly HashSet<int> _expandedSubgenreIds = [];
    private List<Tag> _tags = [];
    private Dictionary<int, int> _tagUsageCounts = [];
    private List<Style> _styles = [];
    private Dictionary<int, int> _styleUsageCounts = [];
    private List<TagSignalSource> _tagSignalSources = [];
    private List<TagRuleGroup> _tagRuleGroups = [];
    private bool _isLoading;
    private bool _genreVocabularyLoaded;
    private int? _selectedModelGenreId;
    private SettingsPage _selectedPage;
    private readonly DispatcherTimer _songFadeSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private double _songFadeDuration = 10;
    private bool _loadingSongFadeDuration;
    private bool _songFadeSavePending;
    private bool _loadingDiscordPresenceSettings;
    private bool _loadingBrowserCookieSettings;
    private bool _hasStartedReferencePreview;

    public event Action<string>? ToastRequested;
    public event Action? LibraryMetadataChanged;
    public event Action<double>? SongFadeDurationChanged;
    public event Action? DiscordPresenceChanged;
    public event Action<MusicTrack>? ReferencePreviewRequested;
    public event Action? ReferencePreviewClosed;

    public SettingsOverlay()
    {
        InitializeComponent();
        _songFadeSaveTimer.Tick += (_, _) => PersistPendingSongFadeDuration();
        SearchBox.TextChanged += (_, _) => RebuildGenreVocabularyRows();
        AppUpdateService.Current.StateChanged += OnAppUpdateStateChanged;
        RefreshUpdatePage(AppUpdateService.Current.State);
    }

    public void Open()
    {
        var appSettings = AppSettingsStore.Load();
        MusicAnalysisServerUrlBox.Text = appSettings.MusicAnalysisServerUrl;
        ObsidianVaultPathBox.Text = appSettings.ObsidianVaultPath;
        _loadingDiscordPresenceSettings = true;
        DiscordPresenceToggle.IsChecked = appSettings.DiscordRichPresenceEnabled;
        DiscordLargeImageTextBox.Text = appSettings.DiscordLargeImageText;
        _loadingDiscordPresenceSettings = false;
        _loadingSongFadeDuration = true;
        _songFadeDuration = appSettings.Appearance.SongFadeDuration;
        SongFadeDurationSlider.Value = _songFadeDuration;
        SongFadeDurationValueText.Text = SecondsValue(_songFadeDuration);
        _loadingSongFadeDuration = false;
        AnalysisServerStatusText.IsVisible = false;
        _loadingBrowserCookieSettings = true;
        var cookiesBrowser = AppSettingsStore.NormalizeYtDlpCookiesBrowser(appSettings.YtDlpCookiesBrowser);
        BrowserCookiesBrowserBox.SelectedItem = BrowserCookiesBrowserBox.Items
            .OfType<ComboBoxItem>()
            .First(item => string.Equals(item.Tag as string, cookiesBrowser, StringComparison.OrdinalIgnoreCase));
        BrowserCookiesToggle.IsChecked = appSettings.UseYtDlpBrowserCookies;
        _loadingBrowserCookieSettings = false;
        ApplyBrowserCookieSettings(save: false);
        RefreshLinuxDependencies();
        SelectPage(SettingsPage.General);
        IsVisible = true;
    }

    private void OnRefreshLinuxDependenciesClicked(object? sender, RoutedEventArgs e) =>
        RefreshLinuxDependencies();

    private void RefreshLinuxDependencies()
    {
        LinuxDependenciesPanel.IsVisible = OperatingSystem.IsLinux();
        if (!OperatingSystem.IsLinux())
            return;

        LinuxDependenciesText.Text = string.Join(
            Environment.NewLine,
            LinuxRuntimeDependencies.Inspect().Select(dependency =>
                $"{(dependency.IsAvailable ? "OK" : "MISSING"),-7} {dependency.Name,-9} {dependency.Detail}"));
    }

    private void OnDiscordPresenceTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_loadingDiscordPresenceSettings)
            return;

        AppSettingsStore.SaveDiscordPresence(
            DiscordPresenceToggle.IsChecked == true,
            null,
            DiscordLargeImageTextBox.Text);
        DiscordPresenceChanged?.Invoke();
    }

    private void OnDiscordPresenceEnabledChanged(object? sender, RoutedEventArgs e)
    {
        if (_loadingDiscordPresenceSettings)
            return;

        AppSettingsStore.SaveDiscordPresence(
            DiscordPresenceToggle.IsChecked == true,
            null,
            DiscordLargeImageTextBox.Text);
        DiscordPresenceChanged?.Invoke();
    }

    public void PreloadGenreVocabulary() => EnsureGenreVocabularyLoaded();

    private void EnsureGenreVocabularyLoaded()
    {
        if (_genreVocabularyLoaded)
            return;

        _isLoading = true;
        _modelGenres = MusicLibraryService.Current.GetModelGenres();
        _modelSubgenres = MusicLibraryService.Current.GetModelSubgenres();
        _genreUsageCounts = MusicLibraryService.Current.GetModelSubgenreTrackCounts();
        _genreReferenceCounts = MusicLibraryService.Current.GetGenreReferenceCounts();
        _selectedModelGenreId = null;
        SearchBox.Text = string.Empty;
        _genreVocabularyLoaded = true;
        _isLoading = false;
        RebuildGenreGroupRows();
        RebuildGenreVocabularyRows();
    }

    private void RebuildGenreVocabularyRows()
    {
        if (_isLoading || !_genreVocabularyLoaded) return;

        var search = SearchBox.Text?.Trim() ?? string.Empty;
        GenreVocabularyRows.Children.Clear();
        var choices = _modelSubgenres
            .Where(subgenre => _selectedModelGenreId is null || subgenre.ModelGenreId == _selectedModelGenreId)
            .Where(subgenre => search.Length == 0
                               || subgenre.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(subgenre => _genreUsageCounts.GetValueOrDefault(subgenre.Id))
            .ThenBy(subgenre => subgenre.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var subgenre in choices)
            GenreVocabularyRows.Children.Add(CreateGenreVocabularyChoice(subgenre));

        if (choices.Count == 0)
            GenreVocabularyRows.Children.Add(new TextBlock
            {
                Text = "No genres match this filter.",
                FontSize = 11,
                Opacity = 0.52,
                Margin = new Avalonia.Thickness(0, 8, 0, 4)
            });
        UpdateSummary();
    }

    private void RebuildGenreGroupRows()
    {
        GenreGroupRows.Children.Clear();
        var groupIds = _modelGenres.Select(item => item.Id).ToHashSet();
        if (_selectedModelGenreId is int selectedId && !groupIds.Contains(selectedId))
            _selectedModelGenreId = null;

        var groups = new[] { (Id: (int?)null, Name: "All") }
            .Concat(_modelGenres
                .OrderBy(genre => genre.Name, StringComparer.OrdinalIgnoreCase)
                .Select(genre => (Id: (int?)genre.Id, genre.Name)));
        foreach (var group in groups)
        {
            var selected = group.Id == _selectedModelGenreId;
            var item = new Border
            {
                MinHeight = 34,
                Background = selected
                    ? new SolidColorBrush(Color.Parse("#262D3640"))
                    : Brushes.Transparent,
                BorderBrush = selected
                    ? ThemeResources.Brush("Theme.Brush.Accent")
                    : Brushes.Transparent,
                BorderThickness = new Avalonia.Thickness(2, 0, 0, 0),
                Padding = new Avalonia.Thickness(10, 5),
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new TextBlock
                {
                    Text = group.Name,
                    FontSize = 11,
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
                _selectedModelGenreId = group.Id;
                RebuildGenreGroupRows();
                RebuildGenreVocabularyRows();
                e.Handled = true;
            };
            GenreGroupRows.Children.Add(item);
        }
    }

    private Control CreateGenreVocabularyChoice(ModelSubgenre subgenre)
    {
        var expanded = _expandedSubgenreIds.Contains(subgenre.Id);
        var panel = new StackPanel();
        var header = new Border
        {
            Background = subgenre.IsPreferred
                ? new SolidColorBrush(Color.Parse("#24364A5E"))
                : ThemeResources.Brush("Theme.Brush.SurfaceTranslucent"),
            BorderBrush = subgenre.IsPreferred
                ? new SolidColorBrush(Color.Parse("#9678A9E6"))
                : ThemeResources.Brush("Theme.Brush.BorderSubtle"),
            BorderThickness = new Avalonia.Thickness(subgenre.IsPreferred ? 2 : 1, 1, 1, 1),
            Padding = new Avalonia.Thickness(10, 8),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        var headerContent = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("18,*,Auto,32"),
            ColumnSpacing = 8
        };
        headerContent.Children.Add(new TextBlock
        {
            Text = expanded ? "⌄" : "›",
            FontSize = 14,
            Opacity = 0.62,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        var title = new TextBlock
        {
            Text = subgenre.Name,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeResources.Brush("Theme.Brush.TextPrimary"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(title, 1);
        headerContent.Children.Add(title);
        var usageCount = _genreUsageCounts.GetValueOrDefault(subgenre.Id);
        var usage = new TextBlock
        {
            Text = usageCount == 1 ? "1 track" : $"{usageCount} tracks",
            FontSize = 10,
            Opacity = 0.52,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(usage, 2);
        headerContent.Children.Add(usage);
        var preferred = new Button
        {
            Content = subgenre.IsPreferred ? "★" : "☆",
            Width = 28,
            Height = 24,
            Padding = new Avalonia.Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0),
            Foreground = subgenre.IsPreferred
                ? ThemeResources.Brush("Theme.Brush.AccentStrong")
                : ThemeResources.Brush("Theme.Brush.TextMuted"),
            FontSize = 14,
            Opacity = subgenre.IsPreferred ? 1 : 0.54,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(preferred, 3);
        headerContent.Children.Add(preferred);
        ToolTip.SetTip(preferred, subgenre.IsPreferred ? "Remove from preferred genres" : "Add to preferred genres");
        preferred.Click += (_, e) =>
        {
            MusicLibraryService.Current.SetModelSubgenrePreferred(subgenre.Id, !subgenre.IsPreferred);
            ReloadGenreVocabulary();
            e.Handled = true;
        };
        header.Child = headerContent;
        header.PointerPressed += (_, e) =>
        {
            if (!_expandedSubgenreIds.Add(subgenre.Id))
                _expandedSubgenreIds.Remove(subgenre.Id);
            RebuildGenreVocabularyRows();
            e.Handled = true;
        };
        panel.Children.Add(header);

        if (expanded)
            panel.Children.Add(CreateExpandedSubgenreContent(subgenre));

        return new Border
        {
            BorderBrush = ThemeResources.Brush("Theme.Brush.BorderSubtle"),
            BorderThickness = new Avalonia.Thickness(0, 0, 0, 1),
            Child = panel
        };
    }

    private Control CreateExpandedSubgenreContent(ModelSubgenre subgenre)
    {
        var nameBox = CreateSettingsTextBox(subgenre.Name, "Subgenre name");
        var descriptionBox = new TextBox
        {
            Text = subgenre.Description ?? string.Empty,
            Watermark = "Add a concise description…",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 80,
            MinHeight = 70,
            Padding = new Avalonia.Thickness(9, 7),
            FontSize = 11
        };
        descriptionBox.Classes.Add("settings-input");

        var details = new StackPanel { Spacing = 10 };
        details.Children.Add(CreateLabeledField("Name", nameBox));
        details.Children.Add(CreateLabeledField("Description", descriptionBox));

        var referenceSection = new StackPanel { Spacing = 5 };
        referenceSection.Children.Add(new TextBlock
        {
            Text = $"REFERENCE TRACKS · {_genreReferenceCounts.GetValueOrDefault(subgenre.Id)}/5",
            FontSize = 9.5,
            FontWeight = FontWeight.SemiBold,
            LetterSpacing = 0.8,
            Opacity = 0.46
        });
        var references = MusicLibraryService.Current.GetGenreReferenceTracks(subgenre.Id);
        if (references.Count == 0)
        {
            referenceSection.Children.Add(new TextBlock
            {
                Text = "No reference tracks selected.",
                FontSize = 10.5,
                Opacity = 0.48,
                Margin = new Avalonia.Thickness(1, 2)
            });
        }
        else
        {
            foreach (var reference in references)
                referenceSection.Children.Add(CreateReferenceTrackButton(reference));
        }
        details.Children.Add(referenceSection);

        var save = new Button
        {
            Content = "Save genre",
            HorizontalAlignment = HorizontalAlignment.Right
        };
        save.Classes.Add("settings-action");
        save.Click += (_, _) =>
        {
            try
            {
                MusicLibraryService.Current.UpdateModelSubgenreDetails(
                    subgenre.Id, nameBox.Text ?? subgenre.Name, descriptionBox.Text);
                ToastRequested?.Invoke($"Saved {nameBox.Text?.Trim()}.");
                ReloadGenreVocabulary();
            }
            catch (Exception exception)
            {
                ToastRequested?.Invoke($"Could not save genre: {exception.Message}");
            }
        };
        details.Children.Add(save);

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#B8121315")),
            BorderBrush = ThemeResources.Brush("Theme.Brush.BorderSubtle"),
            BorderThickness = new Avalonia.Thickness(1, 0, 1, 0),
            Padding = new Avalonia.Thickness(12, 11, 12, 12),
            Child = details
        };
    }

    private Button CreateReferenceTrackButton(GenreReferenceTrack reference)
    {
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("18,*"), ColumnSpacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = "▶",
            FontSize = 9,
            Foreground = ThemeResources.Brush("Theme.Brush.AccentStrong"),
            VerticalAlignment = VerticalAlignment.Center
        });
        var labels = new StackPanel { Spacing = 1 };
        labels.Children.Add(new TextBlock
        {
            Text = reference.Title,
            FontSize = 10.5,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        labels.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(reference.Artist) ? "Unknown artist" : reference.Artist,
            FontSize = 9.5,
            Opacity = 0.5,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(labels, 1);
        content.Children.Add(labels);
        var button = new Button
        {
            Content = content,
            Padding = new Avalonia.Thickness(8, 5),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent,
            BorderBrush = ThemeResources.Brush("Theme.Brush.BorderSubtle"),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(1)
        };
        button.Click += (_, _) =>
        {
            var track = MusicLibraryService.Current.GetTrackById(reference.TrackId);
            if (track is null)
            {
                ToastRequested?.Invoke("The reference track is no longer available.");
                return;
            }
            _hasStartedReferencePreview = true;
            ReferencePreviewRequested?.Invoke(track);
        };
        return button;
    }

    private static TextBox CreateSettingsTextBox(string text, string watermark)
    {
        var box = new TextBox
        {
            Text = text,
            Watermark = watermark,
            Height = 32,
            FontSize = 11.5,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        box.Classes.Add("settings-input");
        return box;
    }

    private static Avalonia.Svg.Skia.Svg CreateSvgIcon(string path, double size) => new(new Uri("avares://Resona/"))
    {
        Path = path,
        Width = size,
        Height = size,
        Stretch = Stretch.Uniform,
        Opacity = 0.82,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center
    };

    private static StackPanel CreateLabeledField(string label, Control field, int? gridColumn = null)
    {
        var panel = new StackPanel { Spacing = 3 };
        panel.Children.Add(new TextBlock
        {
            Text = label.ToUpperInvariant(),
            FontSize = 9.5,
            Opacity = 0.45,
            FontWeight = FontWeight.SemiBold
        });
        panel.Children.Add(field);
        if (gridColumn is int column)
            Grid.SetColumn(panel, column);
        return panel;
    }

    private void ReloadGenreVocabulary()
    {
        _genreVocabularyLoaded = true;
        _isLoading = true;
        _modelGenres = MusicLibraryService.Current.GetModelGenres();
        _modelSubgenres = MusicLibraryService.Current.GetModelSubgenres();
        _genreUsageCounts = MusicLibraryService.Current.GetModelSubgenreTrackCounts();
        _genreReferenceCounts = MusicLibraryService.Current.GetGenreReferenceCounts();
        _isLoading = false;
        CreateSubgenreOverlay.IsVisible = false;
        RebuildGenreGroupRows();
        RebuildGenreVocabularyRows();
        LibraryMetadataChanged?.Invoke();
    }

    private void OnAddSubgenreClicked(object? sender, RoutedEventArgs e)
    {
        if (_selectedModelGenreId is not int categoryId)
        {
            ToastRequested?.Invoke("Select a main genre before creating a subgenre.");
            return;
        }
        var name = NewSubgenreBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ToastRequested?.Invoke("Subgenre name is required.");
            return;
        }

        try
        {
            MusicLibraryService.Current.AddModelSubgenre(categoryId, name);
            NewSubgenreBox.Text = string.Empty;
            ToastRequested?.Invoke($"Added {name}.");
            ReloadGenreVocabulary();
        }
        catch (Exception exception)
        {
            ToastRequested?.Invoke($"Could not add genre: {exception.Message}");
        }
    }

    private void OnSettingsNavigationClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string value }) return;
        var page = value switch
        {
            "general" => SettingsPage.General,
            "tags" => SettingsPage.Tags,
            "styles" => SettingsPage.Styles,
            "tag_rules" => SettingsPage.TagRules,
            _ => SettingsPage.GenreVocabulary
        };
        SelectPage(page);
    }

    private void SelectPage(SettingsPage page)
    {
        if (page == SettingsPage.GenreVocabulary)
            EnsureGenreVocabularyLoaded();

        _selectedPage = page;
        var isGenreVocabularyPage = page == SettingsPage.GenreVocabulary;
        var isGeneralPage = page == SettingsPage.General;
        GenreVocabularyPage.IsVisible = isGenreVocabularyPage;
        GenreToolbar.IsVisible = isGenreVocabularyPage;
        PageDescriptionText.IsVisible = !isGenreVocabularyPage;
        SummaryBar.IsVisible = !isGenreVocabularyPage;
        GeneralPage.IsVisible = isGeneralPage;
        TagsPage.IsVisible = page == SettingsPage.Tags;
        StylesPage.IsVisible = page == SettingsPage.Styles;
        TagRulesPage.IsVisible = page == SettingsPage.TagRules;
        GenreVocabularyNavButton.IsChecked = isGenreVocabularyPage;
        GeneralNavButton.IsChecked = isGeneralPage;
        TagsNavButton.IsChecked = page == SettingsPage.Tags;
        StylesNavButton.IsChecked = page == SettingsPage.Styles;
        TagRulesNavButton.IsChecked = page == SettingsPage.TagRules;

        PageTitleText.Text = page switch
        {
            SettingsPage.General => "General",
            SettingsPage.Tags => "Tags",
            SettingsPage.Styles => "Styles",
            SettingsPage.TagRules => "Tag rules",
            _ => "Genres"
        };
        PageDescriptionText.Text = isGeneralPage
                    ? "Configure downloads, analysis, Discord integration and application updates."
                            : page == SettingsPage.Tags
                                ? "Maintain your curated labels. Tags can describe mood, themes, situations or workflow states without turning them into genres."
                                : page == SettingsPage.Styles
                                    ? "Maintain descriptive styles such as Nightcore, Reverb, Sped Up or Slowed."
                            : page == SettingsPage.TagRules
                                    ? "Turn model signals into reviewable tag suggestions. Rules never assign tags automatically in this first version."
                                    : "Compare current system interpretations before turning them into filters.";
        SummaryText.Text = string.Empty;
        if (isGenreVocabularyPage) RebuildGenreVocabularyRows();
        if (page == SettingsPage.Tags) ReloadTagManagement();
        if (page == SettingsPage.Styles) ReloadStyleManagement();
        if (page == SettingsPage.TagRules) ReloadTagRules();
        if (isGeneralPage) RefreshUpdatePage(AppUpdateService.Current.State);
    }

    private void OnShowCreateSubgenreClicked(object? sender, RoutedEventArgs e)
    {
        if (_selectedModelGenreId is not int categoryId)
        {
            ToastRequested?.Invoke("Select a main genre before creating a subgenre.");
            return;
        }

        var genreName = _modelGenres.FirstOrDefault(genre => genre.Id == categoryId)?.Name ?? "main genre";
        CreateSubgenreTitleText.Text = $"Create subgenre in {genreName}";
        NewSubgenreBox.Text = string.Empty;
        CreateSubgenreOverlay.IsVisible = true;
        Dispatcher.UIThread.Post(() => NewSubgenreBox.Focus());
    }

    private void OnCancelCreateSubgenreClicked(object? sender, RoutedEventArgs e)
    {
        NewSubgenreBox.Text = string.Empty;
        CreateSubgenreOverlay.IsVisible = false;
    }

    private void OnNewSubgenreKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            OnAddSubgenreClicked(sender, new RoutedEventArgs());
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            OnCancelCreateSubgenreClicked(sender, new RoutedEventArgs());
        }
    }

    private async void OnCheckForUpdatesClicked(object? sender, RoutedEventArgs e)
        => await AppUpdateService.Current.CheckForUpdatesAsync(force: true);

    private async void OnDownloadUpdateClicked(object? sender, RoutedEventArgs e)
        => await AppUpdateService.Current.DownloadUpdateAsync();

    private void OnInstallUpdateClicked(object? sender, RoutedEventArgs e)
        => AppUpdateService.Current.ApplyUpdateAndRestart();

    private void OnAppUpdateStateChanged(AppUpdateState state)
        => Dispatcher.UIThread.Post(() => RefreshUpdatePage(state));

    private void RefreshUpdatePage(AppUpdateState state)
    {
        InstalledVersionText.Text = state.CurrentVersion;
        AvailableVersionText.Text = state.AvailableVersion is null
            ? string.Empty
            : $"Version {state.AvailableVersion} available";
        UpdateStatusText.Text = state.Message;
        UpdateProgressBar.Value = state.ProgressPercent;
        UpdateProgressBar.IsVisible = state.Phase == AppUpdatePhase.Downloading;

        var operationInProgress = state.Phase is AppUpdatePhase.Checking or AppUpdatePhase.Downloading;
        CheckForUpdatesButton.IsEnabled = !operationInProgress && state.Phase != AppUpdatePhase.NotInstalled;
        DownloadUpdateButton.IsVisible = state.Phase == AppUpdatePhase.UpdateAvailable;
        InstallUpdateButton.IsVisible = state.Phase == AppUpdatePhase.ReadyToInstall;
    }

    private void OnSaveAnalysisServerClicked(object? sender, RoutedEventArgs e)
    {
        if (!TrackAnalysisService.TryNormalizeServerUrl(MusicAnalysisServerUrlBox.Text, out var serverUrl))
        {
            ShowAnalysisServerStatus("Invalid address", isSuccess: false);
            return;
        }

        AppSettingsStore.SaveMusicAnalysisServerConfiguration(serverUrl);
        BackgroundAnalysisService.Current.NotifyServerConfigurationChanged();
        MusicAnalysisServerUrlBox.Text = serverUrl;
        ShowAnalysisServerStatus("Configuration saved", isSuccess: true);
        ToastRequested?.Invoke("Analysis server configuration saved.");
    }

    private async void OnTestAnalysisServerClicked(object? sender, RoutedEventArgs e)
    {
        if (!TrackAnalysisService.TryNormalizeServerUrl(MusicAnalysisServerUrlBox.Text, out var serverUrl))
        {
            ShowAnalysisServerStatus("Invalid address", isSuccess: false);
            return;
        }

        TestAnalysisServerButton.IsEnabled = false;
        ShowAnalysisServerStatus("Testing connection…", isSuccess: null);
        try
        {
            using var service = new TrackAnalysisService(serverUrlProvider: () => serverUrl);
            var isHealthy = await service.CheckHealthAsync();
            if (isHealthy)
            {
                AppSettingsStore.SaveMusicAnalysisServerConfiguration(serverUrl);
                MusicAnalysisServerUrlBox.Text = serverUrl;
                BackgroundAnalysisService.Current.NotifyServerConfigurationChanged();
            }
            ShowAnalysisServerStatus(
                isHealthy ? "Connection successful; address saved" : "Server returned an unhealthy status",
                isHealthy);
        }
        catch (MusicAnalysisException exception)
        {
            var message = exception.Kind switch
            {
                MusicAnalysisErrorKind.Timeout => "Request timed out",
                MusicAnalysisErrorKind.ConnectionError => "Server not reachable",
                MusicAnalysisErrorKind.ServerError => exception.Message,
                MusicAnalysisErrorKind.InvalidResponse => "Server returned an invalid response",
                _ => exception.Message
            };
            ShowAnalysisServerStatus(message, isSuccess: false);
        }
        finally
        {
            TestAnalysisServerButton.IsEnabled = true;
        }
    }

    private void ShowAnalysisServerStatus(string message, bool? isSuccess)
    {
        AnalysisServerStatusText.Text = message;
        AnalysisServerStatusText.Foreground = new SolidColorBrush(Color.Parse(isSuccess switch
        {
            true => "#73D59B",
            false => "#E87878",
            _ => "#C7D2AD"
        }));
        AnalysisServerStatusText.IsVisible = true;
    }

    private void OnBrowserCookiesSettingChanged(object? sender, RoutedEventArgs e)
    {
        if (!_loadingBrowserCookieSettings)
            ApplyBrowserCookieSettings(save: true);
    }

    private void OnBrowserCookiesBrowserChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_loadingBrowserCookieSettings)
            ApplyBrowserCookieSettings(save: true);
    }

    private void ApplyBrowserCookieSettings(bool save)
    {
        var selectedItem = BrowserCookiesBrowserBox.SelectedItem as ComboBoxItem;
        var browser = AppSettingsStore.NormalizeYtDlpCookiesBrowser(selectedItem?.Tag as string);
        var browserName = selectedItem?.Content?.ToString() ?? "Firefox";
        var enabled = BrowserCookiesToggle.IsChecked == true;

        Values.UseYtDlpBrowserCookies = enabled;
        Values.YtDlpCookiesBrowser = browser;
        BrowserCookiesWarningText.Text =
            $"Uses your signed-in {browserName} YouTube session. This stops working if you sign out or the browser session expires.";
        BrowserCookiesWarningText.IsVisible = enabled;

        if (save)
            AppSettingsStore.SaveYtDlpBrowserCookies(enabled, browser);
    }

    private async void OnChooseObsidianVaultClicked(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the Obsidian vault",
            AllowMultiple = false
        });
        if (folders.Count == 0)
            return;

        var path = folders[0].Path.LocalPath;
        AppSettingsStore.SaveObsidianVaultPath(path);
        ObsidianVaultPathBox.Text = path;
        await ExportObsidianLibraryAsync(path);
    }

    private async void OnExportObsidianLibraryClicked(object? sender, RoutedEventArgs e)
    {
        var path = AppSettingsStore.Load().ObsidianVaultPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            ObsidianExportStatusText.Text = "Choose the Obsidian vault first.";
            ObsidianExportStatusText.IsVisible = true;
            return;
        }

        await ExportObsidianLibraryAsync(path);
    }

    private async Task ExportObsidianLibraryAsync(string path)
    {
        ExportObsidianLibraryButton.IsEnabled = false;
        ObsidianExportStatusText.Text = "Exporting library and media…";
        ObsidianExportStatusText.IsVisible = true;
        try
        {
            var result = await Task.Run(() => ObsidianMusicExportService.ExportAsync(path));
            ObsidianExportStatusText.Text = result.MissingTracks == 0
                ? $"Exported {result.TrackCount:N0} tracks to {result.MusicDirectory}."
                : $"Exported {result.TrackCount:N0} tracks; {result.MissingTracks:N0} audio files are missing.";
            ToastRequested?.Invoke("Obsidian music library exported.");
        }
        catch (Exception exception)
        {
            ObsidianExportStatusText.Text = $"Export failed: {exception.Message}";
        }
        finally
        {
            ExportObsidianLibraryButton.IsEnabled = true;
        }
    }

    private void ReloadTagManagement()
    {
        if (!IsInitialized) return;
        _tags = MusicLibraryService.Current.GetTags();
        _tagUsageCounts = MusicLibraryService.Current.GetAllTrackTagIds()
            .Values
            .SelectMany(tagIds => tagIds)
            .GroupBy(tagId => tagId)
            .ToDictionary(group => group.Key, group => group.Count());
        CreateTagOverlay.IsVisible = false;
        NewTagBox.Text = string.Empty;
        TagVocabularyHintText.Text = _tags.Count == 1 ? "1 tag" : $"{_tags.Count} tags";
        RebuildTagRows();
    }

    private void ReloadStyleManagement()
    {
        if (!IsInitialized) return;
        _styles = MusicLibraryService.Current.GetStyles();
        _styleUsageCounts = MusicLibraryService.Current.GetAllTrackStyleIds()
            .Values
            .SelectMany(styleIds => styleIds)
            .GroupBy(styleId => styleId)
            .ToDictionary(group => group.Key, group => group.Count());
        CreateStyleOverlay.IsVisible = false;
        NewStyleBox.Text = string.Empty;
        StyleVocabularyHintText.Text = _styles.Count == 1 ? "1 style" : $"{_styles.Count} styles";
        RebuildStyleRows();
    }

    private void ReloadTagRules()
    {
        if (!IsInitialized) return;
        _tags = MusicLibraryService.Current.GetTags();
        _tagSignalSources = MusicLibraryService.Current.GetTagSignalSources();
        _tagRuleGroups = MusicLibraryService.Current.GetTagRuleGroups();

        TagRuleTagBox.ItemsSource = _tags
            .Select(tag => new TagRuleTagChoice(tag.Id, tag.Name))
            .ToList();
        TagRuleModelBox.ItemsSource = _tagSignalSources
            .Select(source => source.ModelName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(TagRuleModelDisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(model => new TagRuleModelChoice(model))
            .ToList();
        TagRuleMatchModeBox.ItemsSource = new[]
        {
            new TagRuleMatchModeChoice(TagRuleMatchMode.All, "ALL"),
            new TagRuleMatchModeChoice(TagRuleMatchMode.Any, "ANY")
        };

        if (TagRuleTagBox.SelectedIndex < 0 && _tags.Count > 0)
            TagRuleTagBox.SelectedIndex = 0;
        if (TagRuleModelBox.SelectedIndex < 0 && _tagSignalSources.Count > 0)
            TagRuleModelBox.SelectedIndex = 0;
        if (TagRuleMatchModeBox.SelectedIndex < 0)
            TagRuleMatchModeBox.SelectedIndex = 0;
        RebuildTagRuleSignalChoices();

        RebuildTagRuleRows();
    }

    private void OnTagRuleModelSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        RebuildTagRuleSignalChoices();

    private void RebuildTagRuleSignalChoices()
    {
        if (TagRuleModelBox.SelectedItem is not TagRuleModelChoice model)
        {
            TagRuleSignalBox.ItemsSource = Array.Empty<TagRuleSignalChoice>();
            return;
        }

        TagRuleSignalBox.ItemsSource = _tagSignalSources
            .Where(source => string.Equals(source.ModelName, model.ModelName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(source => source.SignalKey, StringComparer.OrdinalIgnoreCase)
            .Select(source => new TagRuleSignalChoice(source.ModelName, source.SignalKey, source.Description))
            .ToList();
        if (TagRuleSignalBox.SelectedIndex < 0)
            TagRuleSignalBox.SelectedIndex = 0;
    }

    private void RebuildTagRuleRows()
    {
        if (!IsInitialized) return;
        TagRuleRows.Children.Clear();
        if (_tagSignalSources.Count == 0)
        {
            TagRuleRows.Children.Add(new TextBlock
            {
                Text = "Analyze at least one track first. Its available model signals will appear here as possible rule sources.",
                Opacity = .52, TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 18, 0, 0)
            });
            return;
        }

        foreach (var group in _tagRuleGroups)
        {
            var accent = CategoryBrush(null);
            var card = new Border
            {
                Background = ThemeResources.Brush("Theme.Brush.Surface"),
                BorderBrush = ThemeResources.Brush("Theme.Brush.BorderSubtle"),
                BorderThickness = new Avalonia.Thickness(1),
                CornerRadius = new Avalonia.CornerRadius(5),
                Padding = new Avalonia.Thickness(10, 8)
            };
            var panel = new StackPanel { Spacing = 7 };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("180,110,*,Auto,Auto"), ColumnSpacing = 10 };
            var tag = new TextBlock
            {
                Text = group.TagName,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = accent,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var matchMode = new ComboBox
            {
                ItemsSource = new[]
                {
                    new TagRuleMatchModeChoice(TagRuleMatchMode.All, "ALL"),
                    new TagRuleMatchModeChoice(TagRuleMatchMode.Any, "ANY")
                },
                SelectedItem = new TagRuleMatchModeChoice(group.MatchMode, group.MatchMode == TagRuleMatchMode.All ? "ALL" : "ANY"),
                Height = 28,
                FontSize = 10
            };
            matchMode.SelectionChanged += (_, _) =>
            {
                if (matchMode.SelectedItem is not TagRuleMatchModeChoice selected || selected.Mode == group.MatchMode)
                    return;
                MusicLibraryService.Current.SetTagRuleGroupMatchMode(group.Id, selected.Mode);
                MusicLibraryService.Current.RefreshAllTagSuggestions();
                ReloadTagRules();
            };
            var enabled = new ToggleSwitch
            {
                IsChecked = group.Enabled,
                OnContent = "On",
                OffContent = "Off",
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center
            };
            enabled.IsCheckedChanged += (_, _) =>
            {
                MusicLibraryService.Current.SetTagRuleGroupEnabled(group.Id, enabled.IsChecked == true);
                MusicLibraryService.Current.RefreshAllTagSuggestions();
            };
            var remove = new Button
            {
                Content = "Delete",
                FontSize = 10,
                Padding = new Avalonia.Thickness(10, 4),
                Opacity = .75
            };
            remove.Click += (_, _) =>
            {
                MusicLibraryService.Current.DeleteTagRuleGroup(group.Id);
                MusicLibraryService.Current.RefreshAllTagSuggestions();
                ReloadTagRules();
                ToastRequested?.Invoke("Tag rule group deleted.");
            };
            Grid.SetColumn(matchMode, 1);
            Grid.SetColumn(enabled, 3);
            Grid.SetColumn(remove, 4);
            grid.Children.Add(tag);
            grid.Children.Add(matchMode);
            grid.Children.Add(enabled);
            grid.Children.Add(remove);
            panel.Children.Add(grid);

            foreach (var condition in group.Conditions)
                panel.Children.Add(CreateTagRuleConditionRow(group, condition, accent));

            panel.Children.Add(CreateTagRuleConditionEditor(group, accent));
            card.Child = panel;
            TagRuleRows.Children.Add(card);
        }

        if (_tagRuleGroups.Count == 0)
            TagRuleRows.Children.Add(new TextBlock { Text = "No rule groups yet. Select a tag, choose ALL or ANY, then add the first condition.", Opacity = .52, Margin = new Avalonia.Thickness(0, 18, 0, 0) });
    }

    private void OnAddTagRuleClicked(object? sender, RoutedEventArgs e)
    {
        if (TagRuleTagBox.SelectedItem is not TagRuleTagChoice tag
            || TagRuleSignalBox.SelectedItem is not TagRuleSignalChoice source
            || TagRuleMatchModeBox.SelectedItem is not TagRuleMatchModeChoice matchMode)
            return;

        if (!TryParseTagRuleThreshold(TagRuleThresholdBox.Text, out var threshold))
        {
            ToastRequested?.Invoke("Threshold must be a valid number.");
            return;
        }

        try
        {
            MusicLibraryService.Current.CreateTagRuleGroup(tag.Id, matchMode.Mode, source.ModelName, source.SignalKey, threshold);
            MusicLibraryService.Current.RefreshAllTagSuggestions();
            ReloadTagRules();
            ToastRequested?.Invoke($"Rule group created for {tag.Name}.");
        }
        catch (Exception exception) { ToastRequested?.Invoke($"Could not add tag rule: {exception.Message}"); }
    }

    private Control CreateTagRuleConditionRow(TagRuleGroup group, TagRuleCondition condition, IBrush accent)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,76,Auto"), ColumnSpacing = 10, Margin = new Avalonia.Thickness(8, 0, 0, 0) };
        var source = new TextBlock
        {
            Text = $"{TagRuleModelDisplayName(condition.SourceType)} · {condition.SourceKey}",
            FontSize = 10.5,
            Opacity = .72,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var threshold = new TextBlock
        {
            Text = $"≥ {condition.Threshold:0.###}",
            FontSize = 10.5,
            Foreground = accent,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var remove = new Button
        {
            Content = "×",
            FontSize = 12,
            Padding = new Avalonia.Thickness(7, 1),
            Background = new SolidColorBrush(Colors.Transparent),
            Opacity = .6
        };
        ToolTip.SetTip(remove, "Remove condition");
        remove.Click += (_, _) =>
        {
            MusicLibraryService.Current.DeleteTagRuleCondition(condition.Id);
            MusicLibraryService.Current.RefreshAllTagSuggestions();
            ReloadTagRules();
        };
        Grid.SetColumn(threshold, 1);
        Grid.SetColumn(remove, 2);
        row.Children.Add(source);
        row.Children.Add(threshold);
        row.Children.Add(remove);
        return row;
    }

    private Control CreateTagRuleConditionEditor(TagRuleGroup group, IBrush accent)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("165,*,78,Auto"),
            ColumnSpacing = 8,
            Margin = new Avalonia.Thickness(8, 1, 0, 0)
        };
        var modelBox = new ComboBox
        {
            ItemsSource = _tagSignalSources
                .Select(source => source.ModelName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(TagRuleModelDisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(model => new TagRuleModelChoice(model))
                .ToList(),
            Height = 29,
            FontSize = 10
        };
        var signalBox = new ComboBox { Height = 29, FontSize = 10 };
        var thresholdBox = new TextBox { Text = "0.30", Height = 29, FontSize = 10, VerticalContentAlignment = VerticalAlignment.Center };
        var add = new Button
        {
            Content = "+ condition",
            FontSize = 10,
            Padding = new Avalonia.Thickness(9, 3),
            BorderBrush = accent
        };

        void RebuildSignals()
        {
            if (modelBox.SelectedItem is not TagRuleModelChoice model)
            {
                signalBox.ItemsSource = Array.Empty<TagRuleSignalChoice>();
                return;
            }

            signalBox.ItemsSource = _tagSignalSources
                .Where(source => string.Equals(source.ModelName, model.ModelName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(source => source.SignalKey, StringComparer.OrdinalIgnoreCase)
                .Select(source => new TagRuleSignalChoice(source.ModelName, source.SignalKey, source.Description))
                .ToList();
            signalBox.SelectedIndex = 0;
        }

        modelBox.SelectionChanged += (_, _) => RebuildSignals();
        add.Click += (_, _) =>
        {
            if (signalBox.SelectedItem is not TagRuleSignalChoice signal
                || !TryParseTagRuleThreshold(thresholdBox.Text, out var threshold))
            {
                ToastRequested?.Invoke("Threshold must be a valid number.");
                return;
            }

            MusicLibraryService.Current.AddTagRuleCondition(group.Id, signal.ModelName, signal.SignalKey, threshold);
            MusicLibraryService.Current.RefreshAllTagSuggestions();
            ReloadTagRules();
        };
        modelBox.SelectedIndex = 0;
        RebuildSignals();

        Grid.SetColumn(signalBox, 1);
        Grid.SetColumn(thresholdBox, 2);
        Grid.SetColumn(add, 3);
        row.Children.Add(modelBox);
        row.Children.Add(signalBox);
        row.Children.Add(thresholdBox);
        row.Children.Add(add);
        return row;
    }

    private static bool TryParseTagRuleThreshold(string? value, out double threshold)
    {
        var parsed = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out threshold)
            || double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out threshold);
        return parsed && double.IsFinite(threshold) && threshold >= 0;
    }

    private void RebuildTagRows()
    {
        if (!IsInitialized) return;
        TagRows.Children.Clear();
        var tags = _tags
            .OrderBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var tag in tags)
        {
            TagRows.Children.Add(CreateTagVocabularyRow(tag));
        }

        if (tags.Count == 0)
            TagRows.Children.Add(new TextBlock { Text = "No tags yet.", Opacity = .52, Margin = new Avalonia.Thickness(0, 18, 0, 0) });
    }

    private Control CreateTagVocabularyRow(Tag tag)
    {
        var row = new Border
        {
            Background = ThemeResources.Brush("Theme.Brush.SurfaceTranslucent"),
            BorderBrush = ThemeResources.Brush("Theme.Brush.BorderSubtle"),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(7)
        };
        var panel = new StackPanel();
        var current = tag;
        StackPanel? editorPanel = null;

        var header = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0),
            Padding = new Avalonia.Thickness(12, 9),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        var headerContent = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*") };
        var titleText = new TextBlock
        {
            Text = tag.Name,
            FontSize = 13.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeResources.Brush("Theme.Brush.TextPrimary"),
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleText, 1);
        headerContent.Children.Add(titleText);
        var usageCount = _tagUsageCounts.GetValueOrDefault(tag.Id);
        var usageText = new TextBlock
        {
            Text = usageCount == 1 ? "1 track" : $"{usageCount} tracks",
            FontSize = 10,
            Opacity = 0.5,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(usageText, 2);
        headerContent.Children.Add(usageText);
        header.Content = headerContent;
        panel.Children.Add(header);

        header.Click += (_, _) =>
        {
            if (editorPanel is null)
            {
                editorPanel = CreateTagVocabularyEditor(
                    current,
                    updated =>
                    {
                        current = updated;
                        titleText.Text = updated.Name;
                    });
                panel.Children.Add(editorPanel);
            }

            editorPanel.IsVisible = !editorPanel.IsVisible;
            row.Background = ThemeResources.Brush(editorPanel.IsVisible
                ? "Theme.Brush.SurfaceSelected"
                : "Theme.Brush.SurfaceTranslucent");
        };

        row.Child = panel;
        return row;
    }

    private StackPanel CreateTagVocabularyEditor(Tag tag, Action<Tag> onSaved)
    {
        var nameBox = CreateSettingsTextBox(tag.Name, "Tag name");
        var editor = new StackPanel
        {
            Spacing = 8,
            IsVisible = false,
            Margin = new Avalonia.Thickness(12, 0, 12, 12)
        };

        var fields = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        fields.Children.Add(CreateLabeledField("Tag", nameBox));
        var save = new Button
        {
            Content = CreateSvgIcon("/Assets/save.svg", 15),
            Width = 30,
            Height = 30,
            Padding = new Avalonia.Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0),
            VerticalAlignment = VerticalAlignment.Bottom
        };
        ToolTip.SetTip(save, "Save tag");
        var remove = new Button
        {
            Content = CreateSvgIcon("/Assets/trash.svg", 15),
            Width = 30,
            Height = 30,
            Padding = new Avalonia.Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0),
            VerticalAlignment = VerticalAlignment.Bottom,
            Opacity = .7
        };
        ToolTip.SetTip(remove, "Delete tag");
        Grid.SetColumn(save, 1);
        Grid.SetColumn(remove, 2);
        fields.Children.Add(save);
        fields.Children.Add(remove);
        editor.Children.Add(fields);

        save.Click += (_, _) =>
        {
            try
            {
                MusicLibraryService.Current.RenameTag(tag.Id, nameBox.Text ?? tag.Name);
                var updated = tag with { Name = (nameBox.Text ?? tag.Name).Trim() };
                var index = _tags.FindIndex(item => item.Id == tag.Id);
                if (index >= 0) _tags[index] = updated;
                onSaved(updated);
                ToastRequested?.Invoke("Tag updated.");
                RebuildTagRows();
                LibraryMetadataChanged?.Invoke();
            }
            catch (Exception exception) { ToastRequested?.Invoke($"Could not update tag: {exception.Message}"); }
        };
        remove.Click += (_, _) =>
        {
            var error = MusicLibraryService.Current.DeleteTagIfUnused(tag.Id);
            ToastRequested?.Invoke(error ?? "Tag deleted.");
            ReloadTagManagement();
            if (error is null) LibraryMetadataChanged?.Invoke();
        };

        return editor;
    }

    private static bool TryNormalizeHexColor(string? value, out string? color)
    {
        color = null;
        var text = value?.Trim() ?? string.Empty;
        if (text.Length == 0)
            return true;

        if (!text.StartsWith('#')) text = "#" + text;
        if (text.Length != 7)
            return false;

        for (var i = 1; i < text.Length; i++)
            if (!Uri.IsHexDigit(text[i]))
                return false;

        color = text.ToUpperInvariant();
        return true;
    }

    private void OnAddTagClicked(object? sender, RoutedEventArgs e)
    {
        var name = NewTagBox.Text?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            ToastRequested?.Invoke("Tag name is required.");
            return;
        }
        try
        {
            MusicLibraryService.Current.AddTag(name);
            NewTagBox.Text = string.Empty;
            ReloadTagManagement();
            ToastRequested?.Invoke("Tag added.");
            LibraryMetadataChanged?.Invoke();
        }
        catch (Exception exception) { ToastRequested?.Invoke($"Could not add tag: {exception.Message}"); }
    }

    private void RebuildStyleRows()
    {
        if (!IsInitialized) return;
        StyleRows.Children.Clear();
        var styles = _styles
            .OrderBy(style => style.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var style in styles)
            StyleRows.Children.Add(CreateStyleVocabularyRow(style));

        if (styles.Count == 0)
            StyleRows.Children.Add(new TextBlock
            {
                Text = "No styles yet.",
                Opacity = .52,
                Margin = new Thickness(0, 18, 0, 0)
            });
    }

    private Control CreateStyleVocabularyRow(Style style)
    {
        var row = new Border
        {
            Background = ThemeResources.Brush("Theme.Brush.SurfaceTranslucent"),
            BorderBrush = ThemeResources.Brush("Theme.Brush.BorderSubtle"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7)
        };
        var panel = new StackPanel();
        var current = style;
        StackPanel? editorPanel = null;
        var header = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 9),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        var headerContent = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*") };
        var titleText = new TextBlock
        {
            Text = style.Name,
            FontSize = 13.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeResources.Brush("Theme.Brush.TextPrimary"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleText, 1);
        headerContent.Children.Add(titleText);
        var usageCount = _styleUsageCounts.GetValueOrDefault(style.Id);
        var usageText = new TextBlock
        {
            Text = usageCount == 1 ? "1 track" : $"{usageCount} tracks",
            FontSize = 10,
            Opacity = 0.5,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(usageText, 2);
        headerContent.Children.Add(usageText);
        header.Content = headerContent;
        panel.Children.Add(header);

        header.Click += (_, _) =>
        {
            if (editorPanel is null)
            {
                editorPanel = CreateStyleVocabularyEditor(
                    current,
                    updated =>
                    {
                        current = updated;
                        titleText.Text = updated.Name;
                    });
                panel.Children.Add(editorPanel);
            }

            editorPanel.IsVisible = !editorPanel.IsVisible;
            row.Background = ThemeResources.Brush(editorPanel.IsVisible
                ? "Theme.Brush.SurfaceSelected"
                : "Theme.Brush.SurfaceTranslucent");
        };

        row.Child = panel;
        return row;
    }

    private StackPanel CreateStyleVocabularyEditor(Style style, Action<Style> onSaved)
    {
        var nameBox = CreateSettingsTextBox(style.Name, "Style name");
        var editor = new StackPanel
        {
            Spacing = 8,
            IsVisible = false,
            Margin = new Thickness(12, 0, 12, 12)
        };
        var fields = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        fields.Children.Add(CreateLabeledField("Style", nameBox));
        var save = new Button
        {
            Content = CreateSvgIcon("/Assets/save.svg", 15),
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Bottom
        };
        ToolTip.SetTip(save, "Save style");
        var remove = new Button
        {
            Content = CreateSvgIcon("/Assets/trash.svg", 15),
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Bottom,
            Opacity = .7
        };
        ToolTip.SetTip(remove, "Delete style");
        Grid.SetColumn(save, 1);
        Grid.SetColumn(remove, 2);
        fields.Children.Add(save);
        fields.Children.Add(remove);
        editor.Children.Add(fields);

        save.Click += (_, _) =>
        {
            try
            {
                MusicLibraryService.Current.RenameStyle(style.Id, nameBox.Text ?? style.Name);
                var updated = style with { Name = (nameBox.Text ?? style.Name).Trim() };
                var index = _styles.FindIndex(item => item.Id == style.Id);
                if (index >= 0) _styles[index] = updated;
                onSaved(updated);
                ToastRequested?.Invoke("Style updated.");
                ReloadStyleManagement();
                LibraryMetadataChanged?.Invoke();
            }
            catch (Exception exception) { ToastRequested?.Invoke($"Could not update style: {exception.Message}"); }
        };
        remove.Click += (_, _) =>
        {
            var error = MusicLibraryService.Current.DeleteStyleIfUnused(style.Id);
            ToastRequested?.Invoke(error ?? "Style deleted.");
            ReloadStyleManagement();
            if (error is null) LibraryMetadataChanged?.Invoke();
        };
        return editor;
    }

    private void OnAddStyleClicked(object? sender, RoutedEventArgs e)
    {
        var name = NewStyleBox.Text?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            ToastRequested?.Invoke("Style name is required.");
            return;
        }
        try
        {
            MusicLibraryService.Current.AddStyle(name);
            NewStyleBox.Text = string.Empty;
            ReloadStyleManagement();
            ToastRequested?.Invoke("Style added.");
            LibraryMetadataChanged?.Invoke();
        }
        catch (Exception exception) { ToastRequested?.Invoke($"Could not add style: {exception.Message}"); }
    }

    private void OnShowCreateStyleClicked(object? sender, RoutedEventArgs e)
    {
        NewStyleBox.Text = string.Empty;
        CreateStyleOverlay.IsVisible = true;
        Dispatcher.UIThread.Post(() => NewStyleBox.Focus());
    }

    private void OnCancelCreateStyleClicked(object? sender, RoutedEventArgs e)
    {
        NewStyleBox.Text = string.Empty;
        CreateStyleOverlay.IsVisible = false;
    }

    private void OnNewStyleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            OnAddStyleClicked(sender, new RoutedEventArgs());
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            OnCancelCreateStyleClicked(sender, new RoutedEventArgs());
        }
    }

    private void OnShowCreateTagClicked(object? sender, RoutedEventArgs e)
    {
        NewTagBox.Text = string.Empty;
        CreateTagOverlay.IsVisible = true;
        Dispatcher.UIThread.Post(() => NewTagBox.Focus());
    }

    private void OnCancelCreateTagClicked(object? sender, RoutedEventArgs e)
    {
        NewTagBox.Text = string.Empty;
        CreateTagOverlay.IsVisible = false;
    }

    private void OnNewTagKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            OnAddTagClicked(sender, new RoutedEventArgs());
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            OnCancelCreateTagClicked(sender, new RoutedEventArgs());
        }
    }

    private void UpdateSummary()
    {
        if (_selectedPage != SettingsPage.GenreVocabulary) return;
        SummaryText.Text = BuildSummaryText();
    }

    private string BuildSummaryText()
    {
        var relevantSubgenres = _selectedModelGenreId is int modelGenreId
            ? _modelSubgenres.Where(subgenre => subgenre.ModelGenreId == modelGenreId).ToList()
            : _modelSubgenres;
        var scope = _selectedModelGenreId is int selectedId
            ? _modelGenres.FirstOrDefault(genre => genre.Id == selectedId)?.Name ?? "Selected genre"
            : "All categories";
        return $"{scope}: {relevantSubgenres.Count} genres.";
    }

    private static IBrush CategoryBrush(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
            return ThemeResources.Brush("Theme.Brush.TextSecondary");

        try { return new SolidColorBrush(Color.Parse(color)); }
        catch { return ThemeResources.Brush("Theme.Brush.TextSecondary"); }
    }

    private static string TagRuleModelDisplayName(string modelName) => modelName switch
    {
        "mood happy" => "Mood happy",
        "mood sad" => "Mood sad",
        "mood relaxed" => "Mood relaxed",
        "mood aggressive" => "Mood aggressive",
        "mood party" => "Mood party",
        "moods mirex" => "MIREX mood clusters",
        "genre electronic" => "Electronic character",
        "danceability classifier" => "Danceability",
        "voice/instrumental classifiers" => "Voice / instrumental",
        _ => modelName
    };

    private void OnSongFadeDurationChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        _songFadeDuration = Math.Clamp(SongFadeDurationSlider.Value, 0, 30);
        SongFadeDurationValueText.Text = SecondsValue(_songFadeDuration);
        if (_loadingSongFadeDuration)
            return;

        SongFadeDurationChanged?.Invoke(_songFadeDuration);
        _songFadeSavePending = true;
        _songFadeSaveTimer.Stop();
        _songFadeSaveTimer.Start();
    }

    private void OnSongFadeDurationDoubleTapped(object? sender, TappedEventArgs e) =>
        SongFadeDurationSlider.Value = AppearanceSettings.Balanced().SongFadeDuration;

    private void PersistPendingSongFadeDuration()
    {
        _songFadeSaveTimer.Stop();
        if (!_songFadeSavePending)
            return;

        AppSettingsStore.SaveSongFadeDuration(_songFadeDuration);
        _songFadeSavePending = false;
    }

    private static string SecondsValue(double value) => $"{value:0} s";

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
    {
        PersistPendingSongFadeDuration();
        if (_hasStartedReferencePreview)
        {
            _hasStartedReferencePreview = false;
            ReferencePreviewClosed?.Invoke();
        }
        IsVisible = false;
    }

    private sealed record TagRuleTagChoice(int Id, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed record TagRuleModelChoice(string ModelName)
    {
        public override string ToString() => TagRuleModelDisplayName(ModelName);
    }

    private sealed record TagRuleSignalChoice(string ModelName, string SignalKey, string Description)
    {
        public override string ToString() => SignalKey;
    }

    private sealed record TagRuleMatchModeChoice(TagRuleMatchMode Mode, string Name)
    {
        public override string ToString() => Name;
    }

    private enum SettingsPage { General, GenreVocabulary, Tags, Styles, TagRules }
}
