# Implementation Plan: Handle HLS segment 404 by refreshing the playlist instead of retrying

## Context and design decisions

Goal: when an HLS AAC segment fetch returns HTTP 404 NotFound, stop retrying that exact segment (currently 3 Polly attempts plus a 3x in-producer retry loop), treat the 404 as "the current playlist is stale", actively re-fetch the current channel's stream playlist bypassing all caches, and make the running producer pick up the fresh segment list immediately by reusing the existing `channelChangedSource` / "Playlist refresh requested, restarting producer." restart path. A burst of 404s must cause at most one immediate refresh (debounce). 403 (Forbidden / `ApiException` / relogin) behavior stays exactly as-is. Other transient errors (timeouts, `HttpRequestException`, 5xx, other non-success codes) keep their existing Polly retry behavior.

Key findings from the code (paths relative to repo root):

- `SXMPlayer.Client/SiriusXMPlayer.cs`
  - Polly pipeline built in the constructor (~lines 160-176). `ShouldHandle` currently retries on `!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Forbidden`. 404 falls into this and is retried 3x with 10s exponential backoff. We will also exclude `NotFound`.
  - `GetHttpResponseMessage(string url, Dictionary<string,string>)` (~lines 391-416) runs the pipeline; inside the delegate it throws `ApiException` for Forbidden and `InvalidOperationException($"Received status code {response.StatusCode} for url '{url}'")` for any other non-OK. 404 currently becomes that generic `InvalidOperationException`. We will throw a dedicated `SegmentNotFoundException` for 404 so callers can react specifically without parsing the message.
  - `SetCurrentChannel(...)` (~lines 268-279) is the existing refresh/restart signal: on a real channel change it calls `channelChangedSource.Cancel()` then replaces the CTS with a new one. `channelChangedSource` is a private field (line 80).
  - `StreamIcecastAsync(...)` producer loop (~lines 650-716): each iteration passes `channelChangedSource.Token` into `icecastStreamer.StartHLSReader(...)`. When that token cancels, the segment-queue reader stops; the loop then checks `channelChangedSource.IsCancellationRequested`, logs "Playlist refresh requested, restarting producer." and `continue`s, which starts a fresh producer. This is the restart path to reuse.
  - `GetSegmentInternal(...)` (~lines 309-359) wraps the fetch in a `try/catch (Exception ex)` that logs `LogError(ex, "Error fetching/decrypting segment {segmentId}")` and rethrows. This is the per-attempt error log seen 3x in the report; the NotFound case must not log an error stack trace here.

- `SXMPlayer.Client/Services/Icecast/HlsSegmentProducer.cs`
  - `FetchAndDecryptSegment(...)` (~lines 258-312) is a `for (attempt = 1..3)` loop calling `_player.GetSegment(...)`. On a non-cancellation exception with `attempt < maxAttempts` it logs the "Retrying segment {SegmentName} ... (attempt {Attempt}/{MaxAttempts})" warning and backs off. This is the second 3x retry layer; a 404 must break out of it immediately. The producer holds `_player` (the `SiriusXMPlayer`) and can call a public refresh method on it.
  - Note: `RunProducerAsync` declares `Func<Task<ChannelItemData?>> channelProvider` but passes it to `FetchAndDecryptSegment(Func<Task<ChannelItemData>> ...)` — an existing nullability warning (CS8620). Do not "fix" it as part of this task; leave the signatures as they are to keep the diff focused.

- `SXMPlayer.Client/Services/PlaylistService.cs`
  - `GetStreamPlaylistAsync(...)` stores the rewritten playlist in `_cachedPlaylist` (line 240) and, on semaphore timeout, returns `_cachedPlaylist` (lines 157-162). There is NO cache-invalidation method today. `RunProducerAsync` starts each (re)started producer with `useCache = true` and uses it on the first fetch, so after a `channelChangedSource` restart the first `GetStreamPlaylist` call could still return the stale `_cachedPlaylist`. Therefore the refresh trigger MUST invalidate `_cachedPlaylist` before cancelling, so the restarted producer fetches fresh data. This is the core of the user's clarification: actively refresh and bypass the cache, do not merely wait for the next natural poll.

Decisions:

1. New exception type `SegmentNotFoundException` (own file, `SXMPlayer` namespace) rather than reusing `InvalidOperationException` or the generated `ApiException`. Rationale: `ApiException` is NSwag-generated (`SXMPlayer.Client/Client.cs`) and is already overloaded for the 403/relogin path; parsing the `InvalidOperationException` message is brittle. A dedicated type lets `FetchAndDecryptSegment` catch exactly the 404 case.
2. Reuse the existing `channelChangedSource` restart path rather than inventing a new producer-restart mechanism — it already does exactly "stop current producer, restart against a fresh playlist". We add one new public method on `SiriusXMPlayer`, `RequestPlaylistRefresh(string reason)`, that (a) debounces, (b) invalidates the `PlaylistService` cache, then (c) cancels and replaces `channelChangedSource` (the same two lines `SetCurrentChannel` uses). Rationale: this is minimal and keeps the single refresh/restart code path.
3. Debounce in `RequestPlaylistRefresh` with a time guard (ignore requests within N seconds of the last refresh) plus a lock, so a burst of 404s across several segments in ~2s triggers exactly one refresh. Chosen window: 5 seconds (longer than the observed ~2s burst, shorter than a typical segment target duration cycle). Record the rationale in the commit message.
4. Cache invalidation IS needed — add `PlaylistService.InvalidatePlaylistCache()` that sets `_cachedPlaylist = null` under the same `_streamTimeMap`/semaphore-safe approach used elsewhere, and call it from `RequestPlaylistRefresh`.

Build/test commands (verified during exploration):
- Build: `dotnet build SXMPlayer.Client/SXMPlayer.csproj -c Release` (currently builds with 0 errors, ~20 warnings).
- Targeted tests: `dotnet test SXMPlayer.Tests/SXMPlayer.Tests.csproj --filter "FullyQualifiedName~HlsSegmentProducerTests"` (currently 13 passing).
- Note: `dotnet test` with no filter has 4 PRE-EXISTING failures in `SXMPlayer.Tests.LocalTests` ("username is missing" — they need live SXM credentials). These are unrelated to this task; do not treat them as regressions. Verify with the filter above and, for the full suite, confirm only those same 4 LocalTests fail.

---

# Implementation Plan

- [x] 1. Add the `SegmentNotFoundException` type.
      Create a small exception class in the `SXMPlayer` namespace carrying the request URL and status code, with constructors matching existing style (message + optional inner). Used to signal a 404 on a segment fetch distinctly from other non-success codes.
      Files: `SXMPlayer.Client/Services/SegmentNotFoundException.cs` (new)
      Verify: `dotnet build SXMPlayer.Client/SXMPlayer.csproj -c Release` compiles with 0 errors.

- [x] 2. Throw `SegmentNotFoundException` for 404 and exclude 404 from the Polly retry predicate, in `SiriusXMPlayer`.
      In `GetHttpResponseMessage` add a `response.StatusCode == HttpStatusCode.NotFound` branch (before the generic `!= OK` branch) that throws `new SegmentNotFoundException(url)`. In the Polly `ShouldHandle` `HandleResult` predicate, also exclude `NotFound` alongside `Forbidden` so neither is retried: `!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Forbidden && response.StatusCode != HttpStatusCode.NotFound`. Leave the `Forbidden`/`ApiException` branch and the `TaskCanceledException`/`HttpRequestException` handlers exactly as they are.
      Files: `SXMPlayer.Client/SiriusXMPlayer.cs`
      Verify: `dotnet build SXMPlayer.Client/SXMPlayer.csproj -c Release` compiles with 0 errors.

- [x] 3. Add `InvalidatePlaylistCache()` to `PlaylistService`.
      Add a public method that clears the stored playlist (`_cachedPlaylist = null`) and logs at debug, so the next fetch cannot return stale data. Match the structured-logging and locking conventions already in the file.
      Files: `SXMPlayer.Client/Services/PlaylistService.cs`
      Verify: `dotnet build SXMPlayer.Client/SXMPlayer.csproj -c Release` compiles with 0 errors.

- [x] 4. Add the debounced `RequestPlaylistRefresh(string reason)` method to `SiriusXMPlayer` and the fields it needs.
      Add a private `readonly object` lock and a `DateTimeOffset` field tracking the last refresh time. The method: under the lock, if `DateTimeOffset.Now - lastRefresh < TimeSpan.FromSeconds(5)` log at debug that the refresh was skipped (debounced) and return; otherwise update the timestamp, log a single `LogInformation` (reason + channel), call `playlistService.InvalidatePlaylistCache()`, then `channelChangedSource.Cancel()` and replace it with a new `CancellationTokenSource` (the same two-line signal `SetCurrentChannel` uses). Make it `public` so `HlsSegmentProducer` (which holds the `SiriusXMPlayer`) can call it; keep CancellationToken/nullable conventions consistent with the file.
      Files: `SXMPlayer.Client/SiriusXMPlayer.cs`
      Verify: `dotnet build SXMPlayer.Client/SXMPlayer.csproj -c Release` compiles with 0 errors.

- [x] 5. Keep the NotFound case out of the per-segment error-log path in `GetSegmentInternal`.
      In the `catch (Exception ex)` block that currently does `LogError(ex, "Error fetching/decrypting segment {segmentId}")` and rethrows, add a preceding `catch (SegmentNotFoundException)` that rethrows WITHOUT logging an error stack trace (the single informational log happens in the producer in step 6). This removes the per-attempt `fail:` stack traces for 404 while preserving existing logging for all other exceptions.
      Files: `SXMPlayer.Client/SiriusXMPlayer.cs`
      Verify: `dotnet build SXMPlayer.Client/SXMPlayer.csproj -c Release` compiles with 0 errors.

- [x] 6. Catch `SegmentNotFoundException` in `HlsSegmentProducer.FetchAndDecryptSegment` and trigger a single refresh instead of retrying.
      Inside the `for` loop, add a `catch (SegmentNotFoundException ex)` BEFORE the generic `catch (Exception ex) when (attempt < maxAttempts)` and the final `catch (Exception ex)`. It must: log once at warning/info with named placeholders, e.g. `"Segment {SegmentName} not found (404) for channel {ChannelId}; refreshing playlist."`; call `_player.RequestPlaylistRefresh($"segment {segmentName} 404")`; and `return null` immediately so the segment is not retried (so no "Retrying segment ... (attempt n/3)" warnings for 404). Leave the existing transient-retry and final-failure catches unchanged for all other exceptions. (`OperationCanceledException` must still be caught/rethrown first, as it is today.)
      Files: `SXMPlayer.Client/Services/Icecast/HlsSegmentProducer.cs`
      Verify: `dotnet build SXMPlayer.Client/SXMPlayer.csproj -c Release` compiles with 0 errors.

- [x] 7. Confirm the producer restarts against fresh data via the existing path (code review + manual trace, no code change expected).
      Trace that after step 6: `RequestPlaylistRefresh` invalidated `_cachedPlaylist` and cancelled `channelChangedSource`; the producer's `combinedCt` (linked to `channelChangedCt`) cancels, `RunProducerAsync` exits, `StreamIcecastAsync` sees `channelChangedSource.IsCancellationRequested`, logs "Playlist refresh requested, restarting producer." and restarts a producer whose first `GetStreamPlaylist(..., useCache: true)` now re-fetches because the cache was cleared. If the trace reveals the restarted producer could still read stale data (e.g. a timing gap where `_cachedPlaylist` is repopulated by an in-flight call), make the minimal fix — ordering invalidate-after-cancel, or passing `useCache:false` on the post-refresh fetch — and note it in the commit message. No behavioral change beyond what the task requires.
      Files: `SXMPlayer.Client/SiriusXMPlayer.cs`, `SXMPlayer.Client/Services/Icecast/HlsSegmentProducer.cs` (only if the trace finds a gap)
      Verify: `dotnet build SXMPlayer.Client/SXMPlayer.csproj -c Release` compiles with 0 errors; re-read the restart loop to confirm the fresh-fetch path.

- [x] 8. Add a focused test asserting a 404 triggers one refresh instead of three retries.
      This is natural to add. Extend the existing `SXMPlayer.Tests/HlsSegmentProducerTests.cs` patterns. The challenge: `HlsSegmentProducer.FetchAndDecryptSegment` is private and `SiriusXMPlayer` is hard to construct (the existing `SiriusXMPlayerStub : SiriusXMPlayer` passes `null!` deps and only works because the tested members are virtual). Prefer a stub subclass of `SiriusXMPlayer` that overrides `GetSegment` to throw `SegmentNotFoundException` and overrides/records `RequestPlaylistRefresh`; assert that after N (>=3) segment fetches the refresh was requested at most once within the debounce window and that `GetSegment` was NOT called 3 times for the same segment. To make this testable, mark `GetSegment` and `RequestPlaylistRefresh` `virtual` (consistent with the already-`virtual` `GetStreamPlaylist`/`GetDecryptionKey`), and invoke `FetchAndDecryptSegment` via `InternalsVisibleTo`/reflection or by extracting the single-segment attempt into an internal method if direct invocation proves necessary. Keep whichever seam is smallest; document the choice in the test.
      Files: `SXMPlayer.Tests/HlsSegmentProducerTests.cs`; possibly `SXMPlayer.Client/SiriusXMPlayer.cs` (add `virtual`), and `SXMPlayer.Client/SXMPlayer.csproj` (`InternalsVisibleTo` to `SXMPlayer.Tests`) if an internal seam is used.
      Verify: `dotnet test SXMPlayer.Tests/SXMPlayer.Tests.csproj --filter "FullyQualifiedName~HlsSegmentProducerTests"` — all tests pass including the new 404 test.

- [x] 9. Full verification and regression check.
      Build Release and run the full test suite; confirm the only failures are the 4 pre-existing `LocalTests` ("username is missing"), i.e. no new failures and the new 404 test passes.
      Files: none
      Verify: `dotnet build SXMPlayer.Client/SXMPlayer.csproj -c Release` → 0 errors; `dotnet test SXMPlayer.Tests/SXMPlayer.Tests.csproj` → the HlsSegmentProducer tests pass and failures are limited to the same 4 `LocalTests`.

## Implementation record (completed)

All steps implemented and committed as `026e303` on `main` (not pushed; the repo convention is to commit directly to main). `Client.cs` was regenerated by the Debug NSwag build (pure 1:1 line churn) and was reverted so it is NOT part of the commit.

Verification run:
- `dotnet build SXMPlayer.Client/SXMPlayer.csproj -c Release` → Build succeeded, 0 errors, 20 warnings (identical to baseline; none introduced by this change).
- `dotnet test SXMPlayer.Tests/SXMPlayer.Tests.csproj --filter "FullyQualifiedName~HlsSegmentProducerTests"` → 15 passed, including the two new 404 tests.
- `dotnet test sxmplayer.sln` → 86 passed, 4 failed. The 4 failures are the pre-existing `LocalTests` ("username is missing", require live SXM credentials); no new failures.

Test seam notes: added a `protected SiriusXMPlayer(ILogger, PlaylistService)` ctor for stubbing, made `GetSegment` and `RequestPlaylistRefresh` `virtual`, made `FetchAndDecryptSegment` `internal`, and added `InternalsVisibleTo("SXMPlayer.Tests")`. Debounce window = 5s (rationale in commit message).

Step 7 trace result: no extra code change was needed. `RequestPlaylistRefresh` invalidates `_cachedPlaylist` before cancelling `channelChangedSource`, and the producer loop is the only caller making playlist fetches, so the restarted producer's first `GetStreamPlaylist(useCache: true)` re-fetches fresh data.

## Notes / assumptions

- Debounce window is set to 5 seconds. If the team prefers collapsing concurrent requests instead of a time guard, a single-flight boolean under the lock is an acceptable alternative; the time guard is chosen because it also prevents a tight refresh/404 loop if the fresh playlist still 404s briefly. Record the chosen value/rationale in the commit message.
- The 404 path returns `null` from `FetchAndDecryptSegment`, matching the existing "give up on this segment" contract (the final catch already returns `null`); the producer then stops because the refresh cancels its token, so no further segments from the stale list are attempted.
- 403/Forbidden handling is untouched: the `ApiException` throw in `GetHttpResponseMessage`, its exclusion from the Polly predicate, and the relogin path in `GetStreamPlaylist`/`TuneSource` remain exactly as-is.
