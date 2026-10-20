using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Media;

namespace Resona.Models;

public record TrackTagDisplay(string Name, IBrush Foreground);

public record TrackGenreDisplay(string Name, IBrush Foreground);

public static class MainGenrePalette
{
    private static readonly IBrush Fallback = Create("#B5BDC7");

    private static readonly IReadOnlyDictionary<string, IBrush> Brushes =
        new Dictionary<string, IBrush>(StringComparer.OrdinalIgnoreCase)
        {
            ["Blues"] = Create("#6EA8FF"),
            ["Brass & Military"] = Create("#D7A86E"),
            ["Children's"] = Create("#FF9FD2"),
            ["Classical"] = Create("#BFA3FF"),
            ["Electronic"] = Create("#86E0B0"),
            ["Folk, World, & Country"] = Create("#9DDB72"),
            ["Funk / Soul"] = Create("#FFAA5C"),
            ["Hip Hop"] = Create("#D58AFF"),
            ["Jazz"] = Create("#53D6B6"),
            ["Latin"] = Create("#FF955C"),
            ["Non-Music"] = Create("#AEB7C2"),
            ["Pop"] = Create("#FF7FB6"),
            ["Reggae"] = Create("#B5E85B"),
            ["Rock"] = Create("#FF826E"),
            ["Stage & Screen"] = Create("#FFD166")
        };

    public static IBrush For(string? mainGenre) =>
        !string.IsNullOrWhiteSpace(mainGenre) && Brushes.TryGetValue(mainGenre.Trim(), out var brush)
            ? brush
            : Fallback;

    private static IBrush Create(string color) => new SolidColorBrush(Color.Parse(color));
}

public record TrackDisplayItem(
    MusicTrack Track,
    string GenreText,
    string ModelGenreText,
    string ManualGenreText,
    string StyleText,
    string DurationText,
    string RatingName,
    string RatingText,
    IReadOnlyList<TrackGenreDisplay> GenreDisplays,
    IReadOnlyList<TrackTagDisplay> TagDisplays,
    string ChannelText)
{
    public bool NeedsReview { get; set; }
    public bool NeedsAnalysis { get; set; }
    public bool IsPlaying { get; set; }
    public bool ShowDownloadedDate { get; set; }
    public string CollectionDisplayText { get; set; } = string.Empty;
    public string CollectionOverflowText { get; set; } = string.Empty;
    public string CollectionTooltip { get; set; } = string.Empty;
    public bool HasCollectionDisplay => CollectionDisplayText.Length > 0;
    public Bitmap? Thumbnail { get; set; }

    public string ChannelSeparatorText => string.IsNullOrWhiteSpace(ChannelText)
                                                  || string.IsNullOrWhiteSpace(GenreText)
        ? string.Empty
        : "  ·  ";

    public bool HasStyleText => !string.IsNullOrWhiteSpace(StyleText);
    public string StyleSeparatorText => HasStyleText && GenreDisplays.Count > 0 ? "  ·  " : string.Empty;
    public string TrackTitleText => Track.Title.Trim();
    public string ByArtistText => string.IsNullOrWhiteSpace(Track.Artist)
        ? string.Empty
        : $" by {Track.Artist.Trim()}";

    public string DownloadedDateText
    {
        get
        {
            if (!DateTimeOffset.TryParse(Track.DownloadedAt, out var downloadedAt))
                return string.Empty;
            return downloadedAt.ToLocalTime().ToString("dd.MM.yyyy");
        }
    }
    public IBrush TitleBrush => NeedsAnalysis
        ? new SolidColorBrush(Color.FromRgb(238, 92, 92))
        : NeedsReview
        ? new SolidColorBrush(Color.FromRgb(255, 210, 122))
        : new SolidColorBrush(Color.FromRgb(240, 239, 229));

    private Color RatingAccent => RatingPresentation.Accent(RatingName);
    public IBrush RatingBackground => new SolidColorBrush(Color.FromArgb(
        64, RatingAccent.R, RatingAccent.G, RatingAccent.B));
    public IBrush RatingBorder => new SolidColorBrush(Color.FromArgb(
        180, RatingAccent.R, RatingAccent.G, RatingAccent.B));
    public IBrush RatingForeground => new SolidColorBrush(RatingAccent);
}
