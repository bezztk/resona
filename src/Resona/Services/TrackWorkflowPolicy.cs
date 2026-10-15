using System.Collections.Generic;
using Resona.Models;

namespace Resona.Services;

/// <summary>
/// Defines the invariants shared by imports, review, rating and analysis.
/// Database transitions remain the source of truth; this policy keeps their meaning explicit
/// and gives workers and tests one place to make eligibility decisions.
/// </summary>
public static class TrackWorkflowPolicy
{
    public static bool ShouldAnalyze(
        TrackLibraryState libraryState,
        bool analysisDisabled,
        bool hasAnalysis) =>
        libraryState != TrackLibraryState.Rejected
        && !analysisDisabled
        && !hasAnalysis;

    public static IReadOnlyList<string> Validate(
        TrackLibraryState libraryState,
        int? ratingId,
        bool needsReview,
        bool analysisDisabled)
    {
        var issues = new List<string>();
        switch (libraryState)
        {
            case TrackLibraryState.PendingRating:
                if (ratingId is not null)
                    issues.Add("A pending-rating track cannot already have a rating.");
                if (!needsReview)
                    issues.Add("A pending-rating track must remain visible for review.");
                break;
            case TrackLibraryState.Active:
                if (ratingId is null)
                    issues.Add("An active track must have a rating.");
                break;
            case TrackLibraryState.Rejected:
                if (needsReview)
                    issues.Add("A rejected track cannot remain in the review queue.");
                if (!analysisDisabled)
                    issues.Add("A rejected track must not be analyzed.");
                break;
        }

        return issues;
    }
}
