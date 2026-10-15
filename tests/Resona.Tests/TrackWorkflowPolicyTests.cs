using Resona.Models;
using Resona.Services;

namespace Resona.Tests;

public sealed class TrackWorkflowPolicyTests
{
    [Theory]
    [InlineData(TrackLibraryState.PendingRating, false, false, true)]
    [InlineData(TrackLibraryState.Active, false, false, true)]
    [InlineData(TrackLibraryState.Rejected, false, false, false)]
    [InlineData(TrackLibraryState.PendingRating, true, false, false)]
    [InlineData(TrackLibraryState.Active, false, true, false)]
    public void Analysis_eligibility_is_derived_from_persisted_state(
        TrackLibraryState state,
        bool analysisDisabled,
        bool hasAnalysis,
        bool expected)
    {
        Assert.Equal(expected, TrackWorkflowPolicy.ShouldAnalyze(state, analysisDisabled, hasAnalysis));
    }

    [Fact]
    public void Valid_workflow_states_have_no_issues()
    {
        Assert.Empty(TrackWorkflowPolicy.Validate(TrackLibraryState.PendingRating, null, true, false));
        Assert.Empty(TrackWorkflowPolicy.Validate(TrackLibraryState.Active, 2, false, false));
        Assert.Empty(TrackWorkflowPolicy.Validate(TrackLibraryState.Active, 2, true, true));
        Assert.Empty(TrackWorkflowPolicy.Validate(TrackLibraryState.Rejected, 1, false, true));
    }

    [Fact]
    public void Invalid_workflow_states_report_every_broken_invariant()
    {
        Assert.Equal(2, TrackWorkflowPolicy.Validate(TrackLibraryState.PendingRating, 2, false, false).Count);
        Assert.Single(TrackWorkflowPolicy.Validate(TrackLibraryState.Active, null, false, false));
        Assert.Equal(2, TrackWorkflowPolicy.Validate(TrackLibraryState.Rejected, 1, true, false).Count);
    }
}
