# Investigation: secondary HLS stream URL only applies after a full player restart

Read-only investigation. No code was modified. Build verified green:
`dotnet build SXMPlayer.Client/SXMPlayer.csproj -c Release` → 0 errors, 19 warnings (all pre-existing).

---

## 1. Summary answer

The root cause is a **check-then-act race in `HlsSegmentProducer.StartProducer`** between the
consumer loop's restart (`continue`) and the still-running old producer task.

When `RequestPlaylistRefresh` fires the secondary fallback, it cancels the *shared*
`channelChangedSource`. That token is linked into the running producer's `combinedCts`
(`HlsSegmentProducer.cs:71`), so the old `RunProducerAsync` loop is signalled to stop — but
it does **not** stop synchronously. Its `finally` block (which sets `_producerTask = null`)
runs asynchronously after the loop unwinds. Meanwhile the consumer loop in
`StreamIcecastAsync` observes the same cancelled snapshot, logs "Playlist refresh requested,
restarting producer.", `continue`s, and calls `StartHLSReader → StartProducer` **while the
old `_producerTask` is still `IsCompleted == false`**.

`StartProducer` computes `wasAlreadyActive = _producerTask is { IsCompleted: false }`
(`HlsSegmentProducer.cs:64`). Because the old task has not finished its teardown yet, this is
`true`, so `StartProducer` registers the new writer to the fanout but **does not start a new
producer** (`HlsSegmentProducer.cs:68-75`). Moments later the old producer's `finally` runs:
it calls `_fanout.CompleteAll(...)` (completing/closing the just-registered writer), sets
`_producerTask = null`, and logs **"Shared HLS segment producer has stopped."**

Net result: no live producer, the consumer's channel reader is completed, and nothing ever
restarts production on the secondary URL. The stream is dead until the process is restarted.
This exactly matches the log: `...falling back to secondary stream URL` → `Shared HLS segment
producer has stopped.` with **no** following `Starting HLS segment producer.`

The reason a *single* earlier refresh in the same log recovered ("stopped" immediately
followed by "Starting...") is timing: the one-off refresh happened to let the old task reach
`_producerTask = null` before the consumer re-entered `StartProducer`, so `wasAlreadyActive`
was `false` and a new producer started. The two-refresh secondary path races tighter (cancel +
immediate re-entry) and loses.

**The secondary-URL plumbing itself is correct** — the problem is purely that no producer
restarts to exercise it. See §6 for confirmation that URL selection and `_sxmStreams` would
apply the secondary host *if* a producer did restart.

---

## 2. Timeline of the stall (file:line evidence)

Trigger — second 404 crosses the threshold:

1. `HlsSegmentProducer.FetchAndDecryptSegment` catches `SegmentNotFoundException`, logs
   "...not found (404)...refreshing playlist.", calls `_player.RequestPlaylistRefresh(...)`,
   returns `null` — no retry. `HlsSegmentProducer.cs:289-305`.
2. `SiriusXMPlayer.RequestPlaylistRefresh` (`SiriusXMPlayer.cs:369-408`), under
   `playlistRefreshLock`:
   - increments `consecutiveStaleRefreshes`; at `>= SecondaryFallbackRefreshThreshold (2)`
     sets `Volatile.Write(ref useSecondaryStreamUrl, true)` and resets the counter, logging
     "Repeated stale playlist refreshes (2); falling back to secondary stream URL".
     `SiriusXMPlayer.cs:390-399`.
   - `playlistService.InvalidatePlaylistCache()` — clears `_cachedPlaylist`.
     `SiriusXMPlayer.cs:401`.
   - `channelChangedSource.Cancel()` then **replaces** the field with a fresh
     `CancellationTokenSource`. `SiriusXMPlayer.cs:402-403`.

3. The running `RunProducerAsync` was linked to the *old* source via
   `CancellationTokenSource.CreateLinkedTokenSource(channelChangedCt, _producerStopCts.Token)`
   (`HlsSegmentProducer.cs:71`). The cancel makes `combinedCt.IsCancellationRequested` true;
   the `while (!combinedCt.IsCancellationRequested)` loop exits (or an in-flight
   `Task.Delay(..., combinedCt)` throws `OperationCanceledException`, caught at
   `HlsSegmentProducer.cs:197-200` → `break`). The loop body ends and control heads to the
   `finally` — but **the `finally` has not yet executed** when the consumer wakes.

4. Consumer loop in `StreamIcecastAsync` (`SiriusXMPlayer.cs:748-836`):
   - Each iteration snapshots `var iterationChannelChanged = channelChangedSource;`
     (`SiriusXMPlayer.cs:766`) and starts the reader with that token
     (`SiriusXMPlayer.cs:768`).
   - When the producer stops, `segmentQueue.Reader.WaitToReadAsync` completes (the writer is
     completed by the producer's `CompleteAll`), the inner read loop ends.
   - `ct.IsCancellationRequested` is false (client still connected), so it reaches the
     snapshot check `if (iterationChannelChanged.IsCancellationRequested)` — **true** —
     logs "Playlist refresh requested, restarting producer." and `continue`s.
     `SiriusXMPlayer.cs:811-818`.

5. Next consumer iteration calls `icecastStreamer.StartHLSReader(...)` →
   `_segmentProducer.StartProducer(...)` (`IcecastStreamer.cs:60`).
   `StartProducer` evaluates `wasAlreadyActive = _producerTask is { IsCompleted: false }`
   (`HlsSegmentProducer.cs:64`). **If the old task's `finally` has not run yet,
   `_producerTask` is still the old, not-yet-completed task → `wasAlreadyActive == true`.**
   So it only `_fanout.Register(...)` the new writer and returns, **without** starting a new
   `RunProducerAsync` (`HlsSegmentProducer.cs:66-76`).

6. The old producer's `finally` finally runs (`HlsSegmentProducer.cs:213-233`):
   - `_fanout.CompleteAll(completionError)` — completes/removes **all** subscribers including
     the writer just registered in step 5 (`SegmentFanoutHub.cs:95-101` →
     `SubscriberRegistration.Complete` → `Writer.TryComplete`).
   - under `_producerLock`: `_producerStopCts = null; _producerTask = null;`.
   - logs **"Shared HLS segment producer has stopped."** `HlsSegmentProducer.cs:232`.

7. Back in the consumer, the new `segmentQueue` reader observes its writer completed
   immediately with `receivedAnyData == false`. `ct` not cancelled and the snapshot
   `iterationChannelChanged` (the *new*, uncancelled source) not cancelled, so it falls into
   the `!receivedAnyData` branch and `WaitForProducerActivityAsync` (`SiriusXMPlayer.cs:820-834`).
   No producer exists to ever signal activity for this consumer, so it blocks until the next
   external event (client disconnect / process exit). Stream is dead. **This is the observed
   "need a full restart" behavior.**

### Answers to the specific questions

- **Q1 (confirm/refute the race):** **Confirmed.** The consumer's `continue` can re-enter
  `StartProducer` while the old `_producerTask` is still `IsCompleted == false`, yielding
  `wasAlreadyActive == true` and no new producer. `HlsSegmentProducer.cs:64-76`,
  `SiriusXMPlayer.cs:811-818`, `IcecastStreamer.cs:55-66`.

- **Q2 (ordering window):** **Confirmed.** `_producerTask = null` is only assigned in the
  `finally` of `RunProducerAsync` (`HlsSegmentProducer.cs:218-222`), which runs *after* the
  loop breaks on cancellation. There is a real "stopping-but-not-yet-completed" window during
  which `StartProducer` misreads a dying producer as "already active". The `_producerLock`
  does **not** close this window — both `StartProducer` and the `finally` take the lock, but
  the lock only serializes the field reads/writes; it does not make the consumer wait for the
  old task to complete.

- **Q3 (which branch fires):** For the consumer iteration that *detected* the refresh, the
  `iterationChannelChanged.IsCancellationRequested` branch fires and `continue`s
  (`SiriusXMPlayer.cs:811-818`) — the restart path *does* fire. The failure is on the **next**
  iteration: `StartProducer` no-ops, the writer is completed by the old producer's teardown,
  `receivedAnyData` stays false, and the consumer blocks in the
  `WaitForProducerActivityAsync` branch (`SiriusXMPlayer.cs:820-834`) with no producer to wake
  it. So both branches are involved across two iterations, and the stall lands in the
  wait-for-activity branch.

- **Q4 (can `GetStreamPlaylistAsync` skip the secondary-aware delegate?):** **No.**
  `GetStreamPlaylistAsync` always calls `await getProxyPlaylistUrl(channelId)` after acquiring
  the semaphore (`PlaylistService.cs:168-170`); the `useCache` parameter is logged but never
  gates a cached early-return in the normal path. The only early return of `_cachedPlaylist`
  is the semaphore-timeout fallback (`PlaylistService.cs:154-160`), which is not on this path.
  So whenever a fetch actually runs, the secondary-aware delegate (and thus
  `useSecondary: Volatile.Read(ref useSecondaryStreamUrl)`, `SiriusXMPlayer.cs:316-322`) is
  honored. This is **not** a contributing cause.

- **Q5 (does `_sxmStreams` retain the primary host after flip?):** **No, it is corrected on
  the next fetch — if one happens.** `GetProxyPlaylistUrlAsync` selects the secondary URL when
  `useSecondary` is true (`PlaylistService.cs:347-349`) and unconditionally overwrites
  `_sxmStreams[channelId]` with a new `SXMStream` whose `path` is derived from the selected
  (secondary) URL's host (`PlaylistService.cs:368-375`). `GetSegment` builds segment URLs from
  `stream.path` (`SiriusXMPlayer.cs:427`, segment URL built from `stream.path` + version + segment id).
  So once a producer restarts and re-runs the delegate, segment URLs use the secondary host.
  The stale-host concern is moot because the real defect prevents the restart from happening
  at all.

- **Q6 (single root cause + minimal fix):** see §3.

---

## 3. Root cause (single)

`HlsSegmentProducer.StartProducer` treats "a producer task exists and has not completed" as
"a producer is actively running" (`HlsSegmentProducer.cs:64`). During a refresh-driven
restart the old task is *cancelled and tearing down* but not yet completed, so the consumer's
immediate re-entry is misclassified as "already active" and no replacement producer is
started. The old task then completes the shared fanout, leaving the consumer with a completed
reader and no producer. The prior "snapshot the channelChangedSource" fix
(`SiriusXMPlayer.cs:760-766`) correctly made the consumer *detect* the refresh, but it cannot
help because the subsequent `StartProducer` call still races the dying task.

Everything else in the chain (secondary URL selection, cache invalidation, `_sxmStreams`
rewrite, token snapshot) is correct.

---

## 4. Recommended minimal fix (one approach)

**Make `StartProducer` wait for the stopping producer to finish before deciding, so a dying
producer is never mistaken for a live one, then start a fresh producer.** This closes the
race at its source and keeps all restart logic inside the producer, where the `_producerLock`
already guards the fields.

Concretely, in `HlsSegmentProducer.StartProducer` (`HlsSegmentProducer.cs:55-77`):

- Capture the current `_producerTask` and its linked `_producerStopCts` under `_producerLock`.
- Treat the producer as active **only** when the task exists, is not completed, **and** its
  stop token is not already cancelled. If `_producerStopCts` is already cancelled (or
  `channelChangedCt`/the snapshot that started it is cancelled), the producer is *stopping*,
  not active — do not count it as active.
- When it is stopping, start the new producer anyway. Because `_producerTask = null` and the
  fanout `CompleteAll` happen in the old task's `finally`, the new registration must survive
  that teardown. The simplest race-free way: have the consumer (or `StartProducer`) **await
  the old `_producerTask`'s completion before registering the new writer and starting the new
  producer**, so registration happens strictly after `CompleteAll`.

The cleanest shape that stays within one method and is provably race-free:

1. In `StartProducer`, read `(oldTask, oldStopCts)` under the lock and compute
   `isStopping = oldStopCts is null || oldStopCts.IsCancellationRequested || channelChangedCt.IsCancellationRequested`.
2. If a task exists and is **not** stopping and not completed → genuine "already active":
   register writer, return `true` (unchanged behavior for the attach-new-client case).
3. Otherwise (no task, completed, or stopping): if `oldTask` is non-null and not completed,
   `await oldTask` (outside the lock) so its `finally` runs `CompleteAll` and nulls the
   fields; then re-acquire the lock, create a new `_producerStopCts`, register the new writer,
   and start a fresh `RunProducerAsync`. Return `false`.

Because the new writer is registered **after** the old task's `CompleteAll`, step 6 of the
timeline can no longer complete the new writer. And because the "already active" classification
now excludes a cancelled/stopping producer, the consumer's post-refresh re-entry reliably
starts a new producer that re-runs `GetStreamPlaylist → GetProxyPlaylistUrlAsync` with
`useSecondary == true`.

Methods/lines to change:
- `SXMPlayer.Client/Services/Icecast/HlsSegmentProducer.cs` — `StartProducer`
  (`HlsSegmentProducer.cs:55-77`): refine the active-vs-stopping test and await the stopping
  task before registering + starting the replacement. (`StartProducer` would need to become
  `async`/return `Task<bool>`, or expose a small internal await point; the caller
  `IcecastStreamer.StartHLSReader` (`IcecastStreamer.cs:52-67`) and the consumer
  (`SiriusXMPlayer.cs:772`) would await it.)

### Why this is correct and race-free
- The misclassification is eliminated: a cancelled/stopping producer is never "already
  active", so the restart always starts a new producer.
- The ordering hazard is eliminated: awaiting the old task guarantees `CompleteAll` and
  `_producerTask = null` have already run before the new writer is registered, so the new
  writer cannot be swept into the old teardown.
- `_producerLock` continues to serialize all `_producerTask`/`_producerStopCts` field access;
  the only new work (`await oldTask`) is done **outside** the lock to avoid blocking it.

### Alternatives considered (and why not)
- **Loop inside `RunProducerAsync` instead of exiting on refresh:** would avoid the
  stop/restart entirely, but `channelChangedCt` is also the mechanism used for genuine channel
  changes and inactive-client teardown (`StopProducerIfIdle` cancels `_producerStopCts`,
  `SetCurrentChannel` cancels `channelChangedSource`). Distinguishing "refresh, keep looping"
  from "really stop" inside the producer re-introduces the same classification problem and is
  more invasive. Rejected.
- **Have the consumer await old-producer completion before `continue`:** viable, but it leaks
  producer lifecycle knowledge into `StreamIcecastAsync` and the consumer does not hold a
  handle to `_producerTask`. Keeping the fix inside `StartProducer` (which owns the fields and
  the lock) is smaller and more cohesive. Rejected in favor of the recommended fix.

---

## 5. Suggested regression test

Extend `SXMPlayer.Tests/HlsSegmentProducerTests.cs` (patterns already present there):
drive a producer, cancel its `channelChangedCt` to force teardown, and — *before* the old
task's `finally` completes — call `StartProducer` again with a new writer; assert that
`wasAlreadyActive == false` and that the new writer is **not** completed by the old teardown
(i.e. a segment can still be broadcast to it). Verify with:
`dotnet test SXMPlayer.Tests/SXMPlayer.Tests.csproj --filter "FullyQualifiedName~HlsSegmentProducerTests"`.
Note: the full suite has 4 pre-existing `LocalTests` failures ("username is missing", live
API, `Category=Local`) — those are unrelated and expected offline.

---

## 6. Verification performed

- Read all cited files end to end: `SiriusXMPlayer.cs`, `HlsSegmentProducer.cs`,
  `IcecastStreamer.cs`, `SegmentFanoutHub.cs`, `PlaylistService.cs`, and the existing
  `HlsSegmentProducerTests.cs`.
- Confirmed the build is green (`dotnet build SXMPlayer.Client/SXMPlayer.csproj -c Release`
  → 0 errors) so the recommended change starts from a clean base.
- No code was modified during this investigation.
