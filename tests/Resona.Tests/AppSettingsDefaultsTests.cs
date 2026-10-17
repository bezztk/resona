using Resona.Models;
using Resona.Services;

namespace Resona.Tests;

public sealed class AppSettingsDefaultsTests
{
    [Fact]
    public void Defaults_match_the_release_configuration_without_credentials_or_user_state()
    {
        var settings = new AppSettings();

        Assert.Equal(0.53030306f, settings.Volume);
        Assert.Equal("https://analyzer.resona-music.de", settings.MusicAnalysisServerUrl);
        Assert.True(settings.DiscordRichPresenceEnabled);
        Assert.Null(settings.DiscordStateText);
        Assert.Null(settings.DiscordLargeImageText);
        Assert.False(settings.UseYtDlpBrowserCookies);
        Assert.Equal("firefox", settings.YtDlpCookiesBrowser);
        Assert.Empty(settings.PlayerSession.QueueTrackIds);
    }

    [Theory]
    [InlineData("firefox")]
    [InlineData("chrome")]
    [InlineData("edge")]
    [InlineData("brave")]
    public void Supported_cookie_browsers_are_preserved(string browser)
    {
        Assert.Equal(browser, AppSettingsStore.NormalizeYtDlpCookiesBrowser(browser));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unsupported")]
    public void Invalid_cookie_browser_falls_back_to_firefox(string? browser)
    {
        Assert.Equal("firefox", AppSettingsStore.NormalizeYtDlpCookiesBrowser(browser));
    }

    [Fact]
    public void Playback_appearance_defaults_to_a_ten_second_song_fade()
    {
        var appearance = AppearanceSettings.Balanced();

        Assert.Equal(10, appearance.SongFadeDuration);
    }
}
