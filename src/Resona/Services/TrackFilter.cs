using System;
using System.Collections.Generic;
using System.Linq;
using Resona.Models;

namespace Resona.Services;

public record EmotionalCharacterRange(string SignalKey, double? MinimumPercent, double? MaximumPercent);
public record FilterGroup(
    IReadOnlySet<int> GenreIds,
    IReadOnlySet<int> StyleIds,
    IReadOnlySet<int> TagIds,
    IReadOnlySet<string> LanguageCodes,
    IReadOnlyList<EmotionalCharacterRange> EmotionalCharacters,
    bool Negate = false,
    IReadOnlySet<int>? AnyGenreIds = null);

public static class TrackFilter
{
    public static List<MusicTrack> Apply(
        IEnumerable<MusicTrack> tracks,
        IReadOnlyDictionary<int, List<int>> trackGenreIds,
        IReadOnlyDictionary<int, List<int>> trackStyleIds,
        IReadOnlyDictionary<int, List<int>> trackTagIds,
        IReadOnlyDictionary<int, Dictionary<string, double>> trackMirexScores,
        IReadOnlySet<int> ratingFilter,
        IReadOnlyList<FilterGroup> filterGroups,
        string? searchText)
    {
        IEnumerable<MusicTrack> query = tracks;

        if (ratingFilter.Count > 0)
            query = query.Where(t => t.RatingId is int ratingId && ratingFilter.Contains(ratingId));
        

        var term = searchText?.Trim();
        if (!string.IsNullOrEmpty(term))
            query = query.Where(t =>
                t.DisplayTitle.Contains(term, StringComparison.OrdinalIgnoreCase)
                || t.OriginalTitle.Contains(term, StringComparison.OrdinalIgnoreCase));
        
        // Apply filter groups: OR between positive groups, AND within a group.
        // Main genres are the deliberate exception: any mapped subgenre satisfies them.
        // Negated groups remove matching tracks after the positive groups are evaluated.
        // Empty groups (nothing selected in any dimension) are ignored.
        var activeGroups = filterGroups
            .Where(g => g.GenreIds.Count > 0 || (g.AnyGenreIds?.Count ?? 0) > 0 || g.StyleIds.Count > 0 || g.TagIds.Count > 0 || g.LanguageCodes.Count > 0 || g.EmotionalCharacters.Count > 0)
            .ToList();
        var includeGroups = activeGroups.Where(group => !group.Negate).ToList();
        var excludeGroups = activeGroups.Where(group => group.Negate).ToList();

        if (includeGroups.Count > 0)
            query = query.Where(track => includeGroups.Any(g =>
                MatchesGroup(track, g, trackGenreIds, trackStyleIds, trackTagIds, trackMirexScores)));

        if (excludeGroups.Count > 0)
            query = query.Where(track => !excludeGroups.Any(g =>
                MatchesGroup(track, g, trackGenreIds, trackStyleIds, trackTagIds, trackMirexScores)));

        var sorted = query.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase);

        return sorted.ToList();
    }

    // A track matches a group if it has all selected genres and all selected styles.
    private static bool MatchesGroup(
        MusicTrack track,
        FilterGroup group,
        IReadOnlyDictionary<int, List<int>> trackGenreIds,
        IReadOnlyDictionary<int, List<int>> trackStyleIds,
        IReadOnlyDictionary<int, List<int>> trackTagIds,
        IReadOnlyDictionary<int, Dictionary<string, double>> trackMirexScores)
    {
        if (group.GenreIds.Count > 0)
        {
            trackGenreIds.TryGetValue(track.Id, out var tGenres);
            tGenres ??= [];
            if (!group.GenreIds.All(id => tGenres.Contains(id))) return false;
        }

        if ((group.AnyGenreIds?.Count ?? 0) > 0)
        {
            trackGenreIds.TryGetValue(track.Id, out var tGenres);
            tGenres ??= [];
            if (!group.AnyGenreIds!.Any(tGenres.Contains)) return false;
        }

        if (group.StyleIds.Count > 0)
        {
            trackStyleIds.TryGetValue(track.Id, out var tStyles);
            tStyles ??= [];
            if (!group.StyleIds.All(id => tStyles.Contains(id))) return false;
        }

        if (group.TagIds.Count > 0)
        {
            trackTagIds.TryGetValue(track.Id, out var tTags);
            tTags ??= [];
            if (!group.TagIds.All(id => tTags.Contains(id))) return false;
        }

        if (group.LanguageCodes.Count > 0
            && (string.IsNullOrWhiteSpace(track.LanguageCode) || !group.LanguageCodes.Contains(track.LanguageCode)))
            return false;

        if (group.EmotionalCharacters.Count > 0)
        {
            if (!trackMirexScores.TryGetValue(track.Id, out var scores))
                return false;
            foreach (var range in group.EmotionalCharacters)
            {
                if (!scores.TryGetValue(range.SignalKey, out var score))
                    return false;
                var percent = score * 100d;
                if (range.MinimumPercent is double minimum && percent < minimum)
                    return false;
                if (range.MaximumPercent is double maximum && percent > maximum)
                    return false;
            }
        }

        return true;
    }
}
