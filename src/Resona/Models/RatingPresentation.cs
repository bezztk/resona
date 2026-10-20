using Avalonia.Media;

namespace Resona.Models;

public static class RatingPresentation
{
    public static Color Accent(string ratingName) => ratingName switch
    {
        RatingNames.Okay => Color.Parse("#94A3B8"),
        RatingNames.Good => Color.Parse("#34D399"),
        RatingNames.Great => Color.Parse("#38BDF8"),
        RatingNames.Amazing => Color.Parse("#A78BFA"),
        RatingNames.Timeless => Color.Parse("#FBBF24"),
        _ => Color.Parse("#94A3B8")
    };
}
