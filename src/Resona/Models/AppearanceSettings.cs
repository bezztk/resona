using System;

namespace Resona.Models;

public sealed class AppearanceSettings
{
    public double SongFadeDuration { get; set; } = 10;

    public AppearanceSettings Clone() => new()
    {
        SongFadeDuration = SongFadeDuration
    };

    public AppearanceSettings Clamp()
    {
        SongFadeDuration = Math.Clamp(SongFadeDuration, 0, 30);
        return this;
    }

    public static AppearanceSettings Balanced() => new();
}
