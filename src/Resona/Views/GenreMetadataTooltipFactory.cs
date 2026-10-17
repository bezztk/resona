using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Resona.Models;

namespace Resona.Views;

internal sealed record GenreMetadataTooltipEntry(
    ModelSubgenre Subgenre,
    string MainGenreName,
    IReadOnlyList<ModelSubgenreDistinction> Distinctions);

internal static class GenreMetadataTooltipFactory
{
    public static Control Create(IEnumerable<GenreMetadataTooltipEntry> source)
    {
        var entries = source.ToList();
        var content = new StackPanel
        {
            Width = 330,
            Spacing = 12
        };

        for (var index = 0; index < entries.Count; index++)
        {
            if (index > 0)
                content.Children.Add(new Rectangle
                {
                    Height = 1,
                    Fill = ThemeResources.Brush("Theme.Brush.Divider")
                });
            content.Children.Add(CreateEntry(entries[index]));
        }

        return new ScrollViewer
        {
            Width = 334,
            MaxHeight = 300,
            Content = content
        };
    }

    private static Control CreateEntry(GenreMetadataTooltipEntry entry)
    {
        var subgenre = entry.Subgenre;
        var panel = new StackPanel { Spacing = 5 };
        panel.Children.Add(new TextBlock
        {
            Text = subgenre.Name,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = MainGenrePalette.For(entry.MainGenreName),
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = entry.MainGenreName,
            FontSize = 9.5,
            Opacity = 0.5
        });

        if (!string.IsNullOrWhiteSpace(subgenre.Description))
            panel.Children.Add(new TextBlock
            {
                Text = subgenre.Description,
                FontSize = 10.5,
                Foreground = ThemeResources.Brush("Theme.Brush.TextSecondary"),
                Opacity = 0.84,
                TextWrapping = TextWrapping.Wrap
            });

        return panel;
    }
}
