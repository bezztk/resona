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
/// A quiet, single-line metadata row that shows every tag which fits and summarizes the remainder as "+N".
/// </summary>
public sealed class TrackTagSummaryControl : Panel
{
    public static readonly StyledProperty<IReadOnlyList<TrackTagDisplay>?> TagsProperty =
        AvaloniaProperty.Register<TrackTagSummaryControl, IReadOnlyList<TrackTagDisplay>?>(nameof(Tags));

    private readonly List<TextBlock> _tagLabels = [];
    private readonly TextBlock _moreLabel = new()
    {
        FontSize = 9.5,
        LineHeight = 17,
        FontWeight = FontWeight.SemiBold,
        Foreground = ThemeResources.Brush("Theme.Brush.TextMuted"),
        Opacity = 0,
        Margin = new Thickness(0, 0, 5, 0),
        VerticalAlignment = VerticalAlignment.Center
    };

    public IReadOnlyList<TrackTagDisplay>? Tags
    {
        get => GetValue(TagsProperty);
        set => SetValue(TagsProperty, value);
    }

    static TrackTagSummaryControl()
    {
        TagsProperty.Changed.AddClassHandler<TrackTagSummaryControl>((control, _) => control.Rebuild());
    }

    public TrackTagSummaryControl()
    {
        ClipToBounds = true;
        MinHeight = 17;
        VerticalAlignment = VerticalAlignment.Center;
        Children.Add(_moreLabel);
    }

    private void Rebuild()
    {
        Children.Clear();
        _tagLabels.Clear();
        _moreLabel.Opacity = 0;

        foreach (var tag in Tags ?? [])
        {
            var label = new TextBlock
            {
                Text = $"#{tag.Name}",
                Foreground = tag.Foreground,
                FontSize = 9.5,
                LineHeight = 17,
                FontWeight = FontWeight.Medium,
                Opacity = 0.82,
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            _tagLabels.Add(label);
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

        var desiredWidth = _tagLabels.Sum(label => label.DesiredSize.Width);
        return new Size(
            double.IsInfinity(availableSize.Width) ? desiredWidth : Math.Min(desiredWidth, availableSize.Width),
            _tagLabels.Count == 0 ? 0 : _tagLabels.Max(label => label.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var tags = Tags ?? [];
        if (tags.Count == 0)
        {
            _moreLabel.Opacity = 0;
            return finalSize;
        }

        var count = tags.Count;
        while (count > 0 && !Fits(count, tags.Count - count, finalSize.Width))
            count--;

        if (count == 0 && !Fits(0, tags.Count, finalSize.Width))
            _moreLabel.Opacity = 0;
        else
            _moreLabel.Opacity = count < tags.Count ? 0.72 : 0;

        var x = 0d;
        for (var index = 0; index < _tagLabels.Count; index++)
        {
            var label = _tagLabels[index];
            var labelIsVisible = index < count;
            label.Opacity = labelIsVisible ? 0.82 : 0;
            if (!labelIsVisible) continue;
            var y = Math.Max(0, (finalSize.Height - label.DesiredSize.Height) / 2);
            label.Arrange(new Rect(x, y, label.DesiredSize.Width, label.DesiredSize.Height));
            x += label.DesiredSize.Width;
        }

        if (count < tags.Count && _moreLabel.Opacity > 0)
        {
            _moreLabel.Text = $"+{tags.Count - count}";
            _moreLabel.Measure(new Size(double.PositiveInfinity, finalSize.Height));
            var y = Math.Max(0, (finalSize.Height - _moreLabel.DesiredSize.Height) / 2);
            _moreLabel.Arrange(new Rect(x, y, _moreLabel.DesiredSize.Width, _moreLabel.DesiredSize.Height));
        }

        return finalSize;
    }

    private bool Fits(int shownTagCount, int hiddenTagCount, double availableWidth)
    {
        var width = _tagLabels.Take(shownTagCount).Sum(label => label.DesiredSize.Width);
        if (hiddenTagCount <= 0)
            return width <= availableWidth;

        _moreLabel.Text = $"+{hiddenTagCount}";
        _moreLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return width + _moreLabel.DesiredSize.Width <= availableWidth;
    }
}
