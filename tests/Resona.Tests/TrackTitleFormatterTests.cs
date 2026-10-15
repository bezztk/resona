using System.Collections.Generic;
using Resona.Models;
using Resona.Services;

namespace Resona.Tests;

public sealed class TrackTitleFormatterTests
{
    [Fact]
    public void Empty_optional_components_preserve_the_existing_title()
    {
        Assert.Equal("Rightfully", TrackTitleFormatter.Format(null, "Rightfully"));
    }

    [Fact]
    public void Artist_is_prefixed_to_the_title()
    {
        Assert.Equal(
            "Mili — Rightfully",
            TrackTitleFormatter.Format(" Mili ", " Rightfully "));
    }

    [Fact]
    public void Search_matches_artist_through_the_display_title()
    {
        var track = new MusicTrack(
            1, string.Empty, "Rightfully", "1.mp3", null, "2026-01-01T00:00:00Z", null,
            false, null, null, null, "2026-01-01T00:00:00Z",
            Artist: "Mili");

        List<MusicTrack> Search(string term) => TrackFilter.Apply(
            [track],
            new Dictionary<int, List<int>>(),
            new Dictionary<int, List<int>>(),
            new Dictionary<int, List<int>>(),
            new Dictionary<int, Dictionary<string, double>>(),
            new HashSet<int>(),
            [],
            term);

        Assert.Single(Search("Mili"));
    }
}

public sealed class ChannelNameFormatterTests
{
    [Theory]
    [InlineData("deadmau5 - Topic", "deadmau5")]
    [InlineData("deadmau5 - topic", "deadmau5")]
    [InlineData("  deadmau5 - Topic  ", "deadmau5")]
    public void Topic_suffix_is_removed_from_display_names(string channelName, string expected) =>
        Assert.Equal(expected, ChannelNameFormatter.Format(channelName));

    [Theory]
    [InlineData("Topic Records")]
    [InlineData("deadmau5-Topic")]
    [InlineData("deadmau5 - Topic Archive")]
    public void Similar_names_without_the_exact_suffix_are_preserved(string channelName) =>
        Assert.Equal(channelName, ChannelNameFormatter.Format(channelName));
}
