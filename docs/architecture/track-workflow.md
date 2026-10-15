# Track workflow

This document defines the local source of truth for the critical path from a YouTube or ChannelHub URL to a usable library track. UI labels may change; persisted states and their invariants must not.

## Lifecycle

```text
URL / channel video
        |
        v
ImportQueue: Queued -> Downloading -----> Failed
                         |
                         v
                 track persisted
                         |
                         v
Track: PendingRating + NeedsReview
             |                 |
             |                 +--> analysis retry / permanent failure
             v
           rating
             |
             v
Track: Active --------------------------> Rejected
       (may still carry NeedsReview)       (analysis disabled)
```

Analysis and rating are deliberately independent after the media file is safely persisted. A new track is analyzable while it is still `PendingRating`; rating is a human review decision, not a prerequisite for technical analysis.

## Track invariants

| Library state | Rating | Review flag | Analysis |
| --- | --- | --- | --- |
| `PendingRating` | must be `NULL` | must be on | allowed unless explicitly disabled |
| `Active` | must exist | optional | allowed unless explicitly disabled |
| `Rejected` | optional historical value | must be off | must be disabled |

The rules are represented in `TrackWorkflowPolicy` and enforced again by SQLite triggers. Startup migration normalizes legacy rows before recreating the triggers. Code must update all coupled columns in one SQL statement or transaction; temporarily invalid intermediate states are not supported.

## Import queue transitions

`ImportQueueStateMachine` is the only definition of legal persisted transitions:

- `Queued -> Downloading | Failed | Skipped`
- `Downloading -> Analyzing | ReadyForReview | Failed`
- `Analyzing -> ReadyForReview | Failed`
- `Failed -> Queued`
- same-state updates are allowed for progress text

Claiming a queued item is a conditional database update. Completing an import updates the track to its valid review state, removes the queue item, and removes an empty batch in one transaction. A failure in any part rolls the whole completion back.

`Analyzing` remains readable for compatibility with interrupted imports from earlier versions. Current imports leave the durable queue after download persistence and use `BackgroundAnalysisService` for analysis ownership.

## Ownership and service boundaries

| Responsibility | Owner |
| --- | --- |
| URL normalization and media/metadata retrieval | `TrackDownloadService` |
| shared download-to-track persistence | `MusicLibraryService.DownloadAndPersistYouTubeTrackAsync` |
| durable queue transitions and track transactions | `MusicDatabase` |
| the single sequential download worker and UI notifications | `ImportQueueService` |
| analysis eligibility and execution queue | `TrackWorkflowPolicy` / `BackgroundAnalysisService` |
| bounded diagnostic history | `WorkflowLog` |

Manual imports and ChannelHub actions enter the same persistent queue. Its one worker is the only path that performs the check/download/insert sequence. The database unique constraint on `canonical_url` remains the final integrity boundary.

## Recovery rules

At startup, interrupted import items are handled by ownership rather than URL guesses:

| Situation | Recovery |
| --- | --- |
| queue item has `track_id` and its media file exists | atomically complete the queue item; enqueue analysis |
| another flow created a track with the same canonical URL | remove only the redundant queue item |
| queue item owns a partial track or media is missing | clean up only the explicitly owned track/file, then requeue |
| URL matches a track but the queue item has no `track_id` | never delete that track |

This prevents recovery from deleting a legitimate library track created concurrently by ChannelHub or a manual action.

## Retry and failure behavior

- A YouTube 403 gets one short retry in the shared download path.
- Failed downloads stay visible in the import queue and can be queued again explicitly.
- Transient analysis connection/time-out failures remain queued and pause until the server is reachable again.
- Permanent file/response analysis failures disable further automatic analysis and set the review flag atomically.
- Subscriber exceptions from UI events are isolated and logged; they cannot fail a completed background operation.
- If the media download succeeds but database persistence fails, the unowned downloaded file is removed on a best-effort basis.

## Diagnostics

Critical workers write compact entries to `workflow.log` below the application local-data directory. The log rotates at 2 MB to `workflow.previous.log`. It records internal IDs, stages, attempts and exceptions, but does not write source URLs. Logging failures never break the workflow.

## Required tests

The `Resona.Tests` project covers:

- track state invariants and analysis eligibility;
- allowed and forbidden import queue transitions;
- persisted download-request metadata and FIFO queue claiming;
- SQLite claim behavior, atomic completion and rollback.

Any new persisted state or transition must extend the policy/state-machine tests before it is used by a worker.

## Before cloud synchronization

The local workflow is now the authoritative baseline. Cloud work should build on it instead of adding another set of implicit flags. Before enabling multi-device writes, add:

1. stable globally unique track and operation IDs;
2. a transactional outbox/change journal written in the same database transaction as local mutations;
3. explicit conflict rules for rating, review, rejection and deletion;
4. idempotency keys for remote import and analysis operations;
5. schema/protocol versions and migration compatibility tests;
6. tombstones for deletions so another device cannot resurrect removed tracks;
7. end-to-end crash/restart tests around each outbox boundary.

Cloud state must never directly bypass the transition methods or SQLite invariants.
