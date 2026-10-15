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
    string RatingText,
    IReadOnlyList<TrackGenreDisplay> GenreDisplays,
    IReadOnlyList<TrackTagDisplay> TagDisplays,
    string ChannelText)
{
    public bool ShowRatingBandArrow => Track.RatingBand is Models.RatingBand.Low or Models.RatingBand.High;
    public bool ShowRatingBandDot => Track.RatingBand == Models.RatingBand.Mid;
    public double RatingBandArrowRotation => Track.RatingBand == Models.RatingBand.Low ? 180 : 0;

    public IBrush RatingBandForeground => Track.RatingBand switch
    {
        Models.RatingBand.Low => new SolidColorBrush(Color.FromRgb(232, 92, 92)),
        Models.RatingBand.Mid => new SolidColorBrush(Color.FromRgb(245, 245, 238)),
        Models.RatingBand.High => new SolidColorBrush(Color.FromRgb(83, 210, 124)),
        _ => Brushes.Transparent
    };

    public bool NeedsReview { get; set; }
    public bool NeedsAnalysis { get; set; }
    public bool IsPlaying { get; set; }
    public bool ShowDownloadedDate { get; set; }
    public string CollectionDisplayText { get; set; } = string.Empty;
    public string CollectionOverflowText { get; set; } = string.Empty;
    public string CollectionTooltip { get; set; } = string.Empty;
    public bool HasCollectionDisplay => CollectionDisplayText.Length > 0;
    public Bitmap? Thumbnail { get; set; }
    public Color ArtworkPrimaryColor { get; private set; } = Colors.Transparent;
    public Color ArtworkSecondaryColor { get; private set; } = Colors.Transparent;
    public IBrush ArtworkRowBackground { get; private set; } = Brushes.Transparent;
    public IBrush ArtworkMetadataBrush { get; private set; } = new SolidColorBrush(Color.Parse("#D4CFB4"));
    public IBrush ArtworkBorderBrush { get; private set; } = new SolidColorBrush(Color.Parse("#46514D40"));
    public double TrackArtworkOpacity { get; private set; } = 0.18;
    public double TrackArtworkBlur { get; private set; } = 14;
    public double CoverHaloOpacity { get; private set; } = 0.34;
    public double CoverHaloBlur { get; private set; } = 9;
    public double TrackColorWashStrength { get; private set; } = 0.2535;
    public double TrackColorWashReach { get; private set; } = 40;

    private AppearanceSettings _appearance = AppearanceSettings.Balanced();

    public void SetArtworkPalette(Color primary, Color secondary)
    {
        ArtworkPrimaryColor = Color.FromRgb(primary.R, primary.G, primary.B);
        ArtworkSecondaryColor = Color.FromRgb(secondary.R, secondary.G, secondary.B);
        RebuildArtworkPresentation();
    }

    public void ApplyAppearance(AppearanceSettings appearance)
    {
        _appearance = appearance;
        TrackArtworkOpacity = _appearance.TrackArtworkStrength / 100d;
        TrackArtworkBlur = _appearance.TrackArtworkBlur;
        CoverHaloOpacity = _appearance.CoverHaloStrength / 100d;
        CoverHaloBlur = _appearance.CoverHaloBlur;
        TrackColorWashStrength = _appearance.TrackColorWashStrength / 100d;
        TrackColorWashReach = _appearance.TrackColorWashReach;
        RebuildArtworkPresentation();
    }

    private void RebuildArtworkPresentation()
    {
        if (ArtworkPrimaryColor.A == 0)
        {
            ArtworkRowBackground = Brushes.Transparent;
            return;
        }

        var strength = _appearance.TrackColorWashStrength / 100d;
        ArtworkRowBackground = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(_appearance.TrackColorWashReach / 100d, 0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(WithOpacity(ArtworkPrimaryColor, strength), 0),
                new GradientStop(WithOpacity(ArtworkSecondaryColor, strength * 0.6), 0.42),
                new GradientStop(Colors.Transparent, 1)
            }
        };
        ArtworkMetadataBrush = new SolidColorBrush(Mix(ArtworkPrimaryColor, Color.Parse("#E2DDCA"), 0.58));
        ArtworkBorderBrush = new SolidColorBrush(Color.FromArgb(68, ArtworkPrimaryColor.R, ArtworkPrimaryColor.G, ArtworkPrimaryColor.B));
    }

    private static Color WithOpacity(Color color, double opacity) => Color.FromArgb(
        (byte)Math.Clamp((int)Math.Round(opacity * 255), 0, 255), color.R, color.G, color.B);

    private static Color Mix(Color from, Color to, double amount) => Color.FromRgb(
        (byte)(from.R + (to.R - from.R) * amount),
        (byte)(from.G + (to.G - from.G) * amount),
        (byte)(from.B + (to.B - from.B) * amount));

    public string ChannelSeparatorText => string.IsNullOrWhiteSpace(ChannelText)
                                                  || string.IsNullOrWhiteSpace(GenreText)
        ? string.Empty
        : "  ·  ";

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

    public IBrush RatingBackground => RatingText switch
    {
        RatingNames.Timeless => new SolidColorBrush(Color.FromArgb(255, 156, 116, 28)),
        RatingNames.Amazing  => new SolidColorBrush(Color.FromArgb(255, 154, 78, 30)),
        "Great"              => new SolidColorBrush(Color.FromArgb(255, 28, 126, 60)),
        "Good"               => new SolidColorBrush(Color.FromArgb(255, 20, 100, 140)),
        "Okay"               => new SolidColorBrush(Color.FromArgb(255, 108, 111, 70)),
        RatingNames.Avoid    => new SolidColorBrush(Color.FromArgb(255, 150, 43, 36)),
        _                    => new SolidColorBrush(Color.FromArgb(255, 137, 91, 25))
    };

    public IBrush RatingBorder => RatingText switch
    {
        RatingNames.Timeless => new SolidColorBrush(Color.FromArgb(185, 219, 184, 85)),
        RatingNames.Amazing => new SolidColorBrush(Color.FromArgb(180, 221, 144, 78)),
        "Great" => new SolidColorBrush(Color.FromArgb(175, 83, 176, 105)),
        "Good" => new SolidColorBrush(Color.FromArgb(170, 76, 164, 139)),
        "Okay" => new SolidColorBrush(Color.FromArgb(150, 139, 144, 108)),
        RatingNames.Avoid => new SolidColorBrush(Color.FromArgb(170, 201, 82, 68)),
        _ => new SolidColorBrush(Color.FromArgb(160, 190, 139, 69))
    };

    public IBrush RatingForeground => RatingText switch
    {
        RatingNames.Timeless => new SolidColorBrush(Color.FromArgb(255, 255, 215, 64)),
        RatingNames.Amazing  => new SolidColorBrush(Color.FromArgb(255, 255, 132, 48)),
        "Great"              => new SolidColorBrush(Color.FromArgb(255, 55, 224, 105)),
        "Good"               => new SolidColorBrush(Color.FromArgb(255, 45, 190, 240)),
        "Okay"               => new SolidColorBrush(Color.FromArgb(255, 210, 205, 95)),
        RatingNames.Avoid    => new SolidColorBrush(Color.FromArgb(255, 255, 75, 75)),
        _                    => new SolidColorBrush(Color.FromArgb(255, 190, 145, 75))
    };
}
