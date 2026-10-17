using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Resona.Models;

namespace Resona.Views;

/// <summary>
/// Displays each subgenre with the stable color of its main genre.
/// </summary>
public sealed class TrackGenreSummaryControl : Panel
{
    public static readonly StyledProperty<IReadOnlyList<TrackGenreDisplay>?> GenresProperty =
        AvaloniaProperty.Register<TrackGenreSummaryControl, IReadOnlyList<TrackGenreDisplay>?>(nameof(Genres));

    private readonly List<TextBlock> _genreLabels = [];
    private readonly List<TextBlock> _separatorLabels = [];
    private readonly TextBlock _moreLabel = new()
    {
        Text = "…",
        FontSize = 9.5,
        FontWeight = FontWeight.SemiBold,
        Foreground = ThemeResources.Brush("Theme.Brush.TextPrimary"),
        Opacity = 0,
        Margin = new Thickness(5, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Effect = CreateTextShadow()
    };

    public IReadOnlyList<TrackGenreDisplay>? Genres
    {
        get => GetValue(GenresProperty);
        set => SetValue(GenresProperty, value);
    }

    static TrackGenreSummaryControl()
    {
        GenresProperty.Changed.AddClassHandler<TrackGenreSummaryControl>((control, _) => control.Rebuild());
    }

    public TrackGenreSummaryControl()
    {
        ClipToBounds = true;
        Children.Add(_moreLabel);
    }

    private void Rebuild()
    {
        Children.Clear();
        _genreLabels.Clear();
        _separatorLabels.Clear();
        _moreLabel.Opacity = 0;

        var genres = Genres ?? [];
        for (var index = 0; index < genres.Count; index++)
        {
            var genre = genres[index];
            if (index > 0)
            {
                var separator = new TextBlock
                {
                    Text = index == genres.Count - 1 ? " and " : ", ",
                    Foreground = ThemeResources.Brush("Theme.Brush.TextPrimary"),
                    FontSize = 10.5,
                    FontWeight = FontWeight.Medium,
                    Opacity = 0.9,
                    VerticalAlignment = VerticalAlignment.Center,
                    Effect = CreateTextShadow()
                };
                _separatorLabels.Add(separator);
                Children.Add(separator);
            }

            var label = new TextBlock
            {
                Text = genre.Name,
                Foreground = genre.Foreground,
                FontSize = 10.5,
                FontWeight = FontWeight.Medium,
                Opacity = 0.9,
                VerticalAlignment = VerticalAlignment.Center,
                Effect = CreateTextShadow()
            };
            _genreLabels.Add(label);
            Children.Add(label);
        }

        Children.Add(_moreLabel);
        InvalidateMeasure();
        InvalidateArrange();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));

        var desiredWidth = WidthFor(_genreLabels.Count);
        var desiredHeight = Children
            .Where(child => child != _moreLabel)
            .Select(child => child.DesiredSize.Height)
            .DefaultIfEmpty(0)
            .Max();
        return new Size(
            double.IsInfinity(availableSize.Width) ? desiredWidth : Math.Min(desiredWidth, availableSize.Width),
            desiredHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var genres = Genres ?? [];
        if (genres.Count == 0)
        {
            _moreLabel.Opacity = 0;
            return finalSize;
        }

        var count = genres.Count;
        var isTruncated = WidthFor(count) > finalSize.Width;
        _moreLabel.Opacity = isTruncated ? 0.9 : 0;
        if (isTruncated)
        {
            while (count > 0 && WidthFor(count) + _moreLabel.DesiredSize.Width > finalSize.Width)
                count--;
        }

        var x = 0d;
        for (var index = 0; index < _genreLabels.Count; index++)
        {
            var label = _genreLabels[index];
            var labelIsVisible = index < count;
            label.Opacity = labelIsVisible ? 0.9 : 0;
            if (index > 0)
            {
                var separator = _separatorLabels[index - 1];
                var separatorIsVisible = index < count;
                separator.Opacity = separatorIsVisible ? 0.9 : 0;
                if (separatorIsVisible)
                {
                    var separatorY = Math.Max(0, (finalSize.Height - separator.DesiredSize.Height) / 2);
                    separator.Arrange(new Rect(x, separatorY, separator.DesiredSize.Width, separator.DesiredSize.Height));
                    x += separator.DesiredSize.Width;
                }
            }
            if (!labelIsVisible)
                continue;

            var y = Math.Max(0, (finalSize.Height - label.DesiredSize.Height) / 2);
            label.Arrange(new Rect(x, y, label.DesiredSize.Width, label.DesiredSize.Height));
            x += label.DesiredSize.Width;
        }

        if (isTruncated)
        {
            var y = Math.Max(0, (finalSize.Height - _moreLabel.DesiredSize.Height) / 2);
            _moreLabel.Arrange(new Rect(x, y, _moreLabel.DesiredSize.Width, _moreLabel.DesiredSize.Height));
        }

        return finalSize;
    }

    private double WidthFor(int genreCount) =>
        _genreLabels.Take(genreCount).Sum(label => label.DesiredSize.Width)
        + _separatorLabels.Take(Math.Max(0, genreCount - 1)).Sum(label => label.DesiredSize.Width);

    private static DropShadowEffect CreateTextShadow() => new()
    {
        OffsetX = 0,
        OffsetY = 0,
        BlurRadius = 1,
        Color = Colors.Black,
        Opacity = 0.9
    };
}
