# Design Review — Option B Full-Inversion Refactor of `PlaylistService`

Reviewed document: `.agents/tasks/design.md` (iteration 3)
Reviewed source (worktree `f:\sxm-player\.worktrees\di-playlist-inversion`):
`SXMPlayer.Client\Services\PlaylistService.cs`, `SXMPlayer.Client\SiriusXMPlayer.cs`,
`SXMPlayer.Client\Services\MetadataService.cs`, `SXMPlayer.Client\Services\PlayerState.cs`,
`SXMPlayer.Client\Services\Icecast\HlsSegmentProducer.cs`, `SXMPlayer.Proxy\Program.cs`,
`SXMPlayer.Tests\PlaylistServiceTests.cs`, `SXMPlayer.Tests\HlsSegmentProducerTests.cs`.

## Verdict

**CHANGES_REQUESTED** — 1 HIGH, 1 MEDIUM, 2 NIT.

The architecture is sound and all six mandatory points are confirmed *in intent*, but the design
rests on one C#-language claim that is factually false (the now-playing interface would not compile
as described) and omits a third live caller of the channel-change path (`StreamIcecastAsync`). Both
must be corrected before implementation.

---

## Mandatory-point adjudication

**1. No constructor dependency cycle after inversion — CONFIRMED.**
Verified `MetadataService`'s only use of `PlaylistService` is the read-only `StreamTimeMap`
(`playlistService.StreamTimeMap.TryGetValue(...)` in `SetNowPlayingFromSegment`). The design breaks
the `PlaylistService ↔ MetadataService` construction cycle by dropping `PlaylistService` from
`MetadataService`'s constructor and deferring the reverse edge to a post-construction
`SetStreamTimeMap(IStreamTimeMap)` setter, called last in the composition root. The now-playing
enrichment path uses `INowPlayingProvider = MetadataService` as a one-way *constructor* edge
(`PlaylistService → MetadataService`); the reverse map edge is setter-injected, so the
`MetadataService ↔ PlaylistService` cycle is not reintroduced through now-playing. The design also
correctly recognizes (lines 486–494) that interfaces alone do not break a construction cycle when
both sides are constructor-injected, and that the setter is the actual break. `StreamTuner →
MetadataService` is one-way (`MetadataService` does not depend on `StreamTuner`). No constructor
references `SiriusXMPlayer`. The documented construction order is acyclic. **Confirmed.**

**2. Channel-changed event reproduces exact `channelChangedSource` Cancel()+replace semantics — CONFIRMED in intent, with the caveat in Finding 2.**
Verified today's `SiriusXMPlayer.SetCurrentChannel`: on `hasChanged` it runs, in order,
`logger.LogInformation` → `progressTimerManager.MarkChannelChanged()` → `ResetStreamUrlFallback()`
→ `channelChangedSource.Cancel()` → `channelChangedSource = new CancellationTokenSource()`. The
design's `OnChannelChanged` reproduces this exact body and order, keeps `channelChangedSource`,
`progressTimerManager`, and `ResetStreamUrlFallback()` owned by `SiriusXMPlayer`, and raises only on
`hasChanged == true` (matching the current `if (hasChanged)` guard). The event is awaited inside the
same semaphore-guarded `try` block where `setCurrentChannel` is awaited today, so the Cancel/replace
still completes before the fetch continues. The ordering `StartProgressTimer(isChannelChange)`
(player, before the fetch) then `MarkChannelChanged()` (event, during the fetch) matches today.
**Confirmed** — but see Finding 2: the design does not account for the second caller of the
channel-change path (`StreamIcecastAsync`), which affects whether this guarantee holds on that path.

**3. Producer-restart behavior unchanged — CONFIRMED.**
Verified `HlsSegmentProducer.IsProducerLive()` judges liveness against `_producerCombinedCt`
(linked to the caller's channel-changed token) and the stopping-task await in `StartProducer`;
these are untouched by the refactor. Verified `StreamIcecastAsync` snapshots
`var iterationChannelChanged = channelChangedSource;` per iteration and checks the snapshot (not the
live field) for the restart decision. Because the design keeps the Cancel/replace byte-for-byte in
`SiriusXMPlayer` and emits it from the same places (the channel-change handler and the unchanged
`RequestPlaylistRefresh`), the snapshot observes the same signal at the same moment. The
secondary-URL switch is still governed by `PlayerState` and emitted via `RequestPlaylistRefresh`'s
own Cancel/replace, which is unchanged; nothing reorders the switch after the producer stops.
**Confirmed.**

**4. Simplified `GetStreamPlaylistAsync` carries no `Func` delegates — CONFIRMED.**
The proposed signature is `GetStreamPlaylistAsync(string channelId, string currentId, string? alias,
bool useCache)` — no `Func<...>` and no `ChannelItemData? currentChannel`. Orchestration moves into
constructor-injected `ICurrentChannelService`, `IStreamTuner`, `IStreamHttpClient`,
`INowPlayingProvider`, and `PlayerState`. **Confirmed** (contingent on Finding 1, which concerns one
of those collaborators compiling).

**5. DI wiring stated, justified, compiles coherently; test-only path addressed — CONFIRMED in intent, contingent on Finding 1.**
Option B (manual wiring inside `SiriusXMPlayer`'s DI constructor, passing concrete references) is
stated and justified against the smaller blast radius versus a full container migration. The
wiring snippet preserves the `tokenSource` single-instance contract (verified: `tokenSource` is a
player field shared into `SxmSessionService`/`MetadataService`/`StreamTuner`, and the player is the
sole `Cancel()`-in-`Dispose()` owner). The test seam is addressed: verified the test-only
constructor `protected SiriusXMPlayer(ILogger<SiriusXMPlayer>, PlaylistService, PlayerState? = null)`
and the three test sites. The `SiriusXMPlayerStub : base(null!, null!, null!, null!, null!)` maps to
the 5-arg public DI constructor and is unaffected. **Confirmed in intent** — but Finding 1 means the
`PlaylistService`/`MetadataService` pair as described does not actually compile, so "compiles
coherently" is not yet met.

**6. Hard constraints preserved — CONFIRMED.**
Verified against source: `SegmentNotFoundException` (404), `ApiException` (403),
`InvalidOperationException` (other non-OK) all originate in the current `GetHttpResponseMessage`
and move verbatim into `StreamHttpClient`; the `GetStreamPlaylist` `catch (ApiException)` relogin
path still receives them. `_playlistSemaphore` 10s wait + 5s delay + cached-playlist fallback /
`TimeoutException` + `finally` release is kept (the `GetStreamPlaylistAsync` body is unchanged except
delegate→collaborator substitution). Playlist rewrite details (`/stream/{alias ?? channelId}/{version}`,
`#EXTINF` `0.###` InvariantCulture, `"{artist} - {title}"` → fallback → `"- - -"`,
`AverageSegmentDuration`, `EXT-X-ENDLIST` removal, `VOD`→`EVENT`, `_cachedPlaylist`, highest-BANDWIDTH
selection, `_sxmStreams` construction) all move verbatim. `PlayerState.SecondaryFallbackRefreshThreshold = 2`
is untouched. **Confirmed.**

---

## Findings

### 1. HIGH — `INowPlayingProvider.GetNowPlaying(string, DateTimeOffset?)` is NOT implicitly implemented by the three-parameter concrete method; it will not compile.

**Where:** Design section "3. `INowPlayingProvider`" (lines ~246–264) and "Finding 1 — ADDRESSED"
(lines ~867–875). The design states: *"because the concrete method's third parameter is defaulted,
the three-parameter concrete method is a valid implicit implementation of the two-parameter
interface method of the same name."*

**Problem:** This is false in C#. Optional/defaulted parameters are a call-site convenience; they do
not alter a method's signature for the purpose of interface implementation. Interface implementation
requires an exact parameter-list (arity) match. The concrete method verified in `MetadataService.cs`
is:

```csharp
public async Task<(string artist, string title, string? id)?> GetNowPlaying(
    string channelId, DateTimeOffset? ts, bool tryRefresh = true)
```

That is a **three-parameter** method. An interface member declared as the **two-parameter**
`Task<(string artist, string title, string? id)?> GetNowPlaying(string channelId, DateTimeOffset? ts)`
is a different signature and is **not** satisfied by the three-parameter method. `MetadataService :
INowPlayingProvider` would fail to compile with CS0535 ("does not implement interface member"). There
is no existing two-parameter overload on `MetadataService` (verified — only the three-parameter method
and the parameterless `GetNowPlaying()` exist). This defeats AC2 and FR8 (the whole solution would not
build), and it is the exact class of error the previous iteration flagged as HIGH, re-introduced with a
wrong fix.

**Concrete fix — pick one and state it:**

- **Option A (recommended): declare the interface method with the full three-parameter signature.**
  ```csharp
  public interface INowPlayingProvider
  {
      Task<(string artist, string title, string? id)?> GetNowPlaying(
          string channelId, DateTimeOffset? ts, bool tryRefresh = true);
      NowPlayingData? GetNowPlaying();
  }
  ```
  The concrete three-parameter method then implicitly implements it with no `MetadataService` body
  change. `PlaylistService` calls `_nowPlaying.GetNowPlaying(channelId, ts)`, relying on the
  interface's own `tryRefresh = true` default — identical observable behavior to today's call site.

- **Option B: keep the two-parameter interface method and add a two-parameter forwarder** on
  `MetadataService` that explicitly implements it:
  ```csharp
  Task<(string artist, string title, string? id)?> INowPlayingProvider.GetNowPlaying(
      string channelId, DateTimeOffset? ts) => GetNowPlaying(channelId, ts, tryRefresh: true);
  ```
  This is a real body change to `MetadataService` (contradicting the design's "no `MetadataService`
  body change" claim), so Option A is cleaner.

The sentence asserting that a defaulted third parameter makes the arity-mismatched method a valid
implicit implementation must be removed; it is the same category of false "already matches" claim the
design says it corrected.

### 2. MEDIUM — The channel-change cutover ignores `StreamIcecastAsync`'s direct `SetCurrentChannel(channelId)` call, and the fate of the player's `SetCurrentChannel` method is unspecified/contradictory.

**Where:** `SiriusXMPlayer.StreamIcecastAsync` (verified at the top of the file after the per-iteration
snapshot section):

```csharp
if (channelId != CURRENT_ID && current?.Entity.Id != channelId)
{
    await SetCurrentChannel(channelId);
}
```

The design's "Channel-changed event contract" and FR3 describe moving the `SetCurrentChannel` *body*
into `OnChannelChanged` (raised by `MetadataService.SetCurrentChannelAsync`), and the `GetStreamPlaylist`
path is re-pointed via `PlaylistService` calling `_currentChannel.SetCurrentChannelAsync`. But
`SetCurrentChannel` has **three** effective triggers today, not one: (a) the `setCurrentChannel`
delegate inside `GetStreamPlaylistAsync` (handled by the inversion), and (b) this **direct call in
`StreamIcecastAsync`**, which the design never mentions. (The now-removed delegate was the only one
the design accounts for.)

**Problem:** The design does not say whether the private `SiriusXMPlayer.SetCurrentChannel` method is
removed or kept:
- If it is **removed** (its body becomes `OnChannelChanged`), `StreamIcecastAsync` no longer compiles,
  and nothing re-points this call to `metadataService.SetCurrentChannelAsync` — so the channel-change
  side effects (`MarkChannelChanged`/`ResetStreamUrlFallback`/Cancel+replace) would not fire on the
  icecast tune path at all. That is a behavior regression on the primary streaming entry point.
- If it is **kept** as-is and `MetadataService.SetCurrentChannelAsync` *also* raises the event, then
  on the `GetStreamPlaylist` path the Cancel/replace could run twice (once from the retained method
  body if still wired, once from the event). The design's text is silent, leaving a contradiction.

This directly affects mandatory point 2 ("the same condition... still owned by SiriusXMPlayer and
firing on the same condition"): on the `StreamIcecastAsync` path the firing is currently guaranteed by
the direct call, and the design does not preserve it.

**Concrete fix:** State explicitly that the private `SetCurrentChannel` method is **removed**, that its
`hasChanged` side-effect body lives *only* in `OnChannelChanged` (fired by the event), and that
`StreamIcecastAsync`'s call site is re-pointed to raise the event through the same path:

```csharp
// StreamIcecastAsync, after the refactor:
if (channelId != CURRENT_ID && current?.Entity.Id != channelId)
{
    await metadataService.SetCurrentChannelAsync(channelId); // raises ChannelChanged -> OnChannelChanged
}
```

Add `StreamIcecastAsync` to the enumerated callers that are re-pointed (the design currently
enumerates HTTP callers for `GetHttpResponseMessage`/`GetSegment` but omits this one), and confirm that
after the change `MetadataService.SetCurrentChannelAsync` is the single raise site so the event fires
exactly once per genuine change on every path.

### 3. NIT — The event is declared `event Func<string, Task>?` but the subscription is shown as `metadataService.ChannelChanged += OnChannelChanged;` where `OnChannelChanged` returns `Task` via `return Task.CompletedTask;`.

**Where:** "DI wiring approach" snippet (lines ~556–564) and the `OnChannelChanged` body (lines ~432–444).

`OnChannelChanged` is `private Task OnChannelChanged(string channelId)` returning `Task.CompletedTask`,
which matches `Func<string, Task>` — fine. The NIT is purely documentary: the design should state that
`OnChannelChanged` must be the `Func<string, Task>`-shaped method (not an `async void` or
`EventHandler`), so an implementer does not reflexively write `async void`. One sentence pinning the
delegate shape at the subscription site avoids a subtle miswire. Non-blocking.

### 4. NIT — `GetProxyPlaylistUrlAsync` field-based overload default and the `GetSegment` call-site read of `UseSecondaryStreamUrl` are described in two places with slightly different emphasis.

**Where:** "`GetProxyPlaylistUrlAsync` public surface" (lines ~328–352) and "Secondary-URL gate"
(lines ~355–372).

Both are individually correct against source (verified: `GetSegment` reads
`playerState.UseSecondaryStreamUrl` at its own call site today; the main path reads it at the player
call site today and the design moves that single read inside `PlaylistService`). The NIT is that the
design keeps a `bool useSecondary = false` default on the single public overload *and* says the main
path always passes `_playerState.UseSecondaryStreamUrl` explicitly while the cold-start path passes it
explicitly too — so the default is never relied on by any caller. State that the default exists only
for source-compat/clarity and that both production callers pass the flag explicitly, so there is no
ambiguity about which reader governs each path. Non-blocking.

---

## Verified Assumptions

- `MetadataService.GetNowPlaying(string channelId, DateTimeOffset? ts, bool tryRefresh = true)` exists
  with that exact name (no `Async` suffix) and a defaulted third parameter. **Verified.** (This both
  confirms the design's corrected method name and exposes the HIGH arity error in Finding 1.)
- `MetadataService.GetNowPlaying()` parameterless is `public virtual` and returns `_nowPlaying`.
  **Verified** — matches the interface's parameterless member exactly.
- `MetadataService` constructor takes `PlaylistService playlistService` and uses it only via
  `playlistService.StreamTimeMap.TryGetValue(...)` in `SetNowPlayingFromSegment`. **Verified** — the
  cycle and the narrow `IStreamTimeMap` extraction are accurate.
- `MetadataService.SetCurrentChannelAsync` returns `(ChannelItemData channel, bool hasChanged)` and
  throws `InvalidOperationException` on channel-not-found. **Verified.**
- `MetadataService.GetChannelsAsync()` and `UpdateCutsFromStream(...)` are public and used by
  `SiriusXMPlayer.TuneSource`; no `IChannelCatalog` is needed. **Verified** — the design's corrected
  `StreamTuner → concrete MetadataService` dependency is accurate.
- `SiriusXMPlayer.TuneSource(string channelId, int retries = 0)` self-recurses with `retries + 1`,
  bails at `retries >= 5`, and callers pass only `channelId`. **Verified** — the "internal recursion
  knob" characterization is accurate.
- `SiriusXMPlayer.GetHttpResponseMessage` / `httpRetryPipeline` / `ConfigureRequest` /
  `getQueryParameters()` (empty dict) exist as described and throw `ApiException`/`SegmentNotFoundException`/
  `InvalidOperationException` for 403/404/other. **Verified.**
- `SiriusXMPlayer.GetSegment` performs its own `GetHttpResponseMessage(url, parameters)` call for the
  segment-byte fetch AND calls `GetProxyPlaylistUrlAsync(..., delegate overload ...)` on the cold-start
  path. **Verified** — the design's "three callers of `GetAsync`" and the `GetSegment` re-point are
  accurate.
- `SiriusXMPlayer.SetCurrentChannel` runs `MarkChannelChanged()` → `ResetStreamUrlFallback()` →
  `channelChangedSource.Cancel()` → replace, only on `hasChanged`. **Verified** — the `OnChannelChanged`
  reproduction is byte-for-byte.
- `RequestPlaylistRefresh` keeps its debounce, `PlayerState.RegisterStaleRefresh` escalation, pre-reset
  logged count, and `channelChangedSource.Cancel()` + replace. **Verified.**
- `StreamIcecastAsync` snapshots `iterationChannelChanged = channelChangedSource` per iteration and
  checks the snapshot. **Verified** — the NFR1 preservation argument holds.
- `HlsSegmentProducer.IsProducerLive()`/`StartProducer` liveness-and-await logic is independent of the
  inversion. **Verified** — untouched.
- `PlaylistServiceTests.GetProxyPlaylistUrlAsync_CachesStreamMetadata` calls the delegate overload
  (`tuneSource`, `getHttpResponse`, `useSecondary: false`). **Verified** — the design's statement that
  this test must be migrated in lockstep is correct.
- `PlaylistServiceTests.GetStreamPlaylistAsync_RewritesPlaylistAndBuildsTitles` passes
  `channelId: "channel"`, `currentId: SiriusXMPlayer.CURRENT_ID`, so the `channelId == currentId` branch
  (the only `currentChannel?.Entity.Id` dereference) is not taken. **Verified** — the design's Finding-4
  correction is accurate; the `ICurrentChannelService` mock may return null.
- `HlsSegmentProducerTests` stubs (`NotFoundPlayerStub`, `RealRefreshPlayerStub`, `IdlePlayerStub`) use
  `base(ILogger<SiriusXMPlayer>, CreatePlaylistService())`, and `SiriusXMPlayerStub` uses the 5-arg
  `base(null!, null!, null!, null!, null!)`. **Verified** — `CreatePlaylistService()` must be migrated to
  the new constructor; `SiriusXMPlayerStub` is unaffected.
- `Program.cs` registers `PlayerState` and `SiriusXMPlayer` as singletons and does not construct the
  collaborators itself. **Verified** — the "Program.cs unchanged" claim holds.
- `PlayerState` threshold is `const int SecondaryFallbackRefreshThreshold = 2`, thread-safe
  `UseSecondaryStreamUrl`, no dependency on any service. **Verified** — safe to inject into
  `PlaylistService`.

## Unverified / Wrong Assumptions

- **WRONG:** "the three-parameter concrete method is a valid implicit implementation of the
  two-parameter interface method of the same name" (because the third parameter is defaulted). C#
  requires exact arity for interface implementation; this does not compile. See Finding 1.
- **INCOMPLETE:** The design's enumeration of callers to re-point omits `StreamIcecastAsync`'s direct
  `SetCurrentChannel(channelId)` call, and does not state whether the private `SetCurrentChannel` method
  is removed or retained. See Finding 2.
- **UNVERIFIED (not blocking):** The claim that `StreamHttpClient`'s 403/404/non-OK mapping is
  "straightforward to cover" with a stub `HttpClient` was not validated against the test project's
  ability to substitute `APISession.GetHttpClient()`; the design itself labels this "integration-ish."
  Flagged only so the implementer confirms a seam exists before promising that unit coverage.
