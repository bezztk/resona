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
        Assert.Empty(settings.TrackBackdropFocus);
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
    public void Balanced_appearance_matches_the_release_configuration()
    {
        var appearance = AppearanceSettings.Balanced();

        Assert.Equal(24, appearance.PlayerArtworkStrength);
        Assert.Equal(24.5, appearance.PlayerArtworkBlur);
        Assert.Equal(13.2, appearance.PlayerBackgroundDarkening);
        Assert.Equal(0, appearance.PlayerColorAtmosphere);
        Assert.Equal(5, appearance.ArtworkFadeDuration);
        Assert.Equal(10, appearance.SongFadeDuration);
        Assert.Equal(65, appearance.PlayerAudioReaction);
        Assert.Equal(100, appearance.AudioResponseSpeed);
        Assert.Equal(175, appearance.AudioBassSensitivity);
        Assert.Equal(125, appearance.AudioTrebleSensitivity);
        Assert.Equal(150, appearance.AudioArtworkMotion);
        Assert.Equal(75, appearance.AudioBlurReaction);
        Assert.Equal(200, appearance.AudioColorReaction);
        Assert.True(appearance.SpectrumVisualizerEnabled);
        Assert.Equal(139.9, appearance.SpectrumVisualizerHeight);
        Assert.Equal(50, appearance.SpectrumVisualizerIntensity);
        Assert.Equal(108.25, appearance.SpectrumVisualizerSensitivity);
        Assert.Equal(64.7, appearance.SpectrumVisualizerSmoothing);
        Assert.Equal(14.7, appearance.LibraryBackdropStrength);
        Assert.Equal(20.25, appearance.LibraryBackdropBlur);
        Assert.Equal(30.25, appearance.TrackArtworkStrength);
        Assert.Equal(30, appearance.TrackArtworkBlur);
        Assert.Equal(30, appearance.TrackColorWashStrength);
        Assert.Equal(44.8, appearance.TrackColorWashReach);
        Assert.Equal(60, appearance.CoverHaloStrength);
        Assert.Equal(20, appearance.CoverHaloBlur);
    }
}
