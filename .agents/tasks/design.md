# DI Inversion of PlaylistService.GetStreamPlaylistAsync (Option B — full inversion)

## Requirements

### Summary

`PlaylistService.GetStreamPlaylistAsync` today takes five `Func<...>` delegate parameters
(`setCurrentChannel`, `getProxyPlaylistUrl`, `getHttpResponse`, `getNowPlaying`,
`nowPlayingFallback`) plus a `ChannelItemData? currentChannel` snapshot. Those delegates are all
bound in `SiriusXMPlayer.GetStreamPlaylist` to private `SiriusXMPlayer` members, so the public
contract of a core service leaks the player's internals and makes the orchestration
(tune source → fetch master playlist → rewrite media playlist → attach now-playing titles) a
responsibility of the caller rather than the service.

The user chose **Option B — full inversion**: move the whole fetch/tune/now-playing orchestration
*inside* `PlaylistService` and the collaborator services it owns via dependency injection, and
replace the one callback that is genuinely player-owned (the producer-restart signal, today
`SetCurrentChannel` → `channelChangedSource.Cancel()`/replace) with a C# **event** that
`PlaylistService` raises and `SiriusXMPlayer` subscribes to. After this change the call direction
flips: `PlaylistService` no longer calls back into `SiriusXMPlayer` through delegates; instead it
depends on narrow, player-independent services and emits an event.

This is a behavior-preserving refactor. No change to the producer-restart semantics, the
secondary-URL fallback, playlist rewriting, exception flow, or the semaphore guard is permitted.
The motivating bug (secondary-URL switch landing after the producer stopped) is already fixed on
this branch by the `HlsSegmentProducer` liveness logic and the `StreamIcecastAsync` per-iteration
snapshot; the inversion must not regress that ordering.

### Functional Requirements

FR1. `PlaylistService.GetStreamPlaylistAsync` must expose a signature with **no `Func<...>`
delegate parameters and no `ChannelItemData? currentChannel` parameter**. It receives only plain
data (`channelId`, `currentId`, `alias`, `useCache`), resolves everything else through
constructor-injected collaborators.

FR2. The orchestration steps that currently live as delegates must move behind injected services:
- current-channel resolution + "set current channel" side effects → a channel/metadata collaborator,
- source master-playlist URL resolution (`GetProxyPlaylistUrlAsync`) with its `tuneSource` and
  `getHttpResponse` closures → injected tuning + HTTP-send collaborators,
- raw HTTP send with the Polly pipeline and 403/404/non-OK translation → an injected HTTP-send
  collaborator,
- now-playing title lookup and the now-playing fallback → an injected now-playing collaborator.

FR3. The player-owned producer-restart action (today in `SiriusXMPlayer.SetCurrentChannel`:
`progressTimerManager.MarkChannelChanged()`, `ResetStreamUrlFallback()`,
`channelChangedSource.Cancel()` + replace) must be driven by a **`ChannelChanged` event** raised
by whichever service now owns `SetCurrentChannelAsync`. `SiriusXMPlayer` subscribes once and
reproduces exactly today's actions, in today's order, only when the channel actually changed
(`hasChanged == true`).

FR4. `playerState.UseSecondaryStreamUrl` must still gate primary/secondary stream-URL selection on
both the main fetch path and the `GetSegment` cold-start fallback.

FR5. `RequestPlaylistRefresh` and `InvalidatePlaylistCache` behavior must be unchanged, including
the debounce, the `PlayerState.RegisterStaleRefresh` escalation, the logged pre-reset count, and
the `channelChangedSource.Cancel()` + replace that signals a producer restart.

FR6. The two call sites in `SiriusXMPlayer` (`GetStreamPlaylist` main path and `GetSegment`
cold-start fallback that calls `GetProxyPlaylistUrlAsync`) must compile against the simplified API
and behave identically.

FR7. The design must not introduce a constructor cycle among `PlaylistService`,
`SiriusXMPlayer`, and `MetadataService`.

FR8. The test-only constructor seam used by `SXMPlayer.Tests` (`NotFoundPlayerStub`,
`RealRefreshPlayerStub`, `IdlePlayerStub`, `SiriusXMPlayerStub`) must keep working, or the stubs
must be updated in lockstep so the suite builds and passes.

### Non-Functional Requirements

NFR1. No change to producer-restart timing/semantics; the `HlsSegmentProducer` liveness fix and
`StreamIcecastAsync` snapshot behavior stay byte-for-byte in effect.

NFR2. Exception types and their flow are preserved: `SegmentNotFoundException` on 404,
`ApiException` on 403 (which still drives the relogin/retry in `GetStreamPlaylist`'s catch),
`InvalidOperationException` on any other non-OK.

NFR3. Thread-safety of `_playlistSemaphore`, `_sxmStreams`, `_streamTimeMap`, and `PlayerState` is
preserved; no new shared mutable state is introduced without a lock.

NFR4. The new collaborator seams must be interfaces so the orchestration remains unit-testable
without a live SiriusXM session.

### Acceptance Criteria

AC1. `PlaylistService.GetStreamPlaylistAsync(string channelId, string currentId, string? alias,
bool useCache)` compiles and contains no `Func<...>` or `ChannelItemData` parameter.

AC2. Building the solution (`dotnet build`) succeeds with no new warnings introduced by the
refactor. This explicitly includes `SXMPlayer.Tests`: `HlsSegmentProducerTests.CreatePlaylistService()`,
`PlaylistServiceTests.GetStreamPlaylistAsync_RewritesPlaylistAndBuildsTitles`, and
`PlaylistServiceTests.GetProxyPlaylistUrlAsync_CachesStreamMetadata` are all migrated to the new
constructor/API in the same change (see Test-stub impact), so no test references a removed delegate
signature.

AC3. `SiriusXMPlayer.GetStreamPlaylist` passes the raw secondary-URL gate: when
`playerState.UseSecondaryStreamUrl` is true, the fetched master URL is selected with
`useSecondary: true` — verified by reading the single code path that forwards the flag into the
tuning collaborator.

AC4. On a channel change, the subscriber runs — in this exact order —
`MarkChannelChanged()` → reset-stream-URL-fallback → `channelChangedSource.Cancel()` → replace
`channelChangedSource` with a fresh CTS; and these do **not** run when the channel did not change.
(Observable through `ChannelChangedTokenForTest`.)

AC5. `RequestPlaylistRefresh` with a burst of 404s still cancels the channel-changed token exactly
once and leaves a fresh, uncancelled token — the existing
`RequestPlaylistRefresh_BurstOf404s_TriggersSingleRefresh` test passes unchanged.

AC6. `StartProducer_AfterChannelChangedTokenCancelled_StartsFreshProducer` passes unchanged.

AC7. `FetchAndDecryptSegment_WhenSegment404_DoesNotRetryAndRequestsRefresh` passes unchanged.

AC8. A written cycle-avoidance argument in this document shows no constructor cycle remains, and
`dotnet build` (which would fail a real DI cycle only at resolve time, so the argument is by
construction) is backed by the fact that no collaborator's constructor references
`SiriusXMPlayer` or re-introduces `PlaylistService ↔ MetadataService`.

AC9. Playlist rewriting output is byte-identical for the same input: `/stream/{alias ?? channelId}/
{version}` rewrite, `#EXTINF` duration formatted `0.###` InvariantCulture, title
`"{artist} - {title}"` with now-playing-fallback then `"- - -"`, `AverageSegmentDuration`,
`EXT-X-ENDLIST` removal, `VOD`→`EVENT`, `_cachedPlaylist` caching, highest-`BANDWIDTH` variant
selection, `_sxmStreams`/segment-path construction.

### Out of Scope

- Changing the HLS producer/consumer loop, fanout, or ICY metadata code.
- Changing `PlayerState`'s escalation state machine or its threshold (`= 2`).
- Changing `MetadataService`'s cuts logic, timeout handler, or now-playing resolution rules.
- Converting the entire wiring graph to the DI container beyond what DI-Wiring below specifies.
- Any fix to the original 404/secondary-URL ordering bug beyond preserving the current fix.

---

## Design

### Overview

The refactor extracts the three player-independent concerns that the delegates wrapped —
raw HTTP send, stream tuning, and now-playing lookup — into small interface-fronted services, and
moves the "set current channel + fire the restart signal" concern into the service that already
owns current-channel state (`MetadataService`). `PlaylistService` takes those collaborators by
constructor injection and raises a `ChannelChanged` event instead of invoking a `setCurrentChannel`
delegate. `SiriusXMPlayer` becomes a subscriber: it keeps ownership of `channelChangedSource`
(the producer-restart signal, which is intrinsically the player's because the producer loop lives
in `StreamIcecastAsync`/`HlsSegmentProducer`) and runs the Cancel/replace + progress-timer +
fallback-reset in its event handler.

Technology stack (locked): C# / .NET 10, existing `Microsoft.Extensions.DependencyInjection`
container in `SXMPlayer.Proxy/Program.cs`, existing `Polly` resilience pipeline, existing
`Microsoft.Extensions.Logging`. No new NuGet packages.

### New collaborator services and interfaces

Four narrow seams are introduced, all in `SXMPlayer.Client/Services`. Each is an interface plus a
concrete implementation. Interfaces are what `PlaylistService` depends on (NFR4, testability).

**1. `IStreamHttpClient` / `StreamHttpClient`** — raw HTTP send with the Polly pipeline and the
403/404/non-OK translation. This is the current `SiriusXMPlayer.GetHttpResponseMessage` +
`httpRetryPipeline` + `ConfigureRequest` + `getQueryParameters()`, moved verbatim.

```csharp
public interface IStreamHttpClient
{
    Task<HttpResponseMessage?> GetAsync(string url);
}
```

Responsibilities: build the `UriBuilder` query from the (currently empty) query-parameter map,
run `httpRetryPipeline.ExecuteAsync`, send via `APISession.GetHttpClient()`, apply `ConfigureRequest`
(the User-Agent headers), throw `ApiException` on 403, `SegmentNotFoundException` on 404,
`InvalidOperationException` on any other non-OK, and cancel via the player-owned `tokenSource.Token`.
`ConfigureRequest` (today a private static on the player) and `getQueryParameters()` (today a private
member returning an empty map) move here along with the `httpRetryPipeline` build.

`GetAsync` has **three** callers after the refactor: `PlaylistService.GetProxyPlaylistUrlAsync`
(master-playlist fetch), `PlaylistService.GetStreamPlaylistAsync` (media-playlist fetch), and
`SiriusXMPlayer.GetSegment`'s own segment-byte fetch (see "`GetSegment` segment-byte fetch" below).
All three previously went through the player's `GetHttpResponseMessage`; routing them through this
single `GetAsync` entry point is the whole move.

Constructor deps: `APISession session`, the existing `CancellationTokenSource tokenSource`, and
`ILogger<StreamHttpClient>`. The Polly pipeline is built inside this service (moved out of the
player's constructor). `getQueryParameters()` becomes a private member returning the same empty
dictionary; keeping it isolated here means a future real query map changes one place.
No dependency on `SiriusXMPlayer`, `PlaylistService`, or `MetadataService`.

**Cancellation-token wiring (resolves Finding 3).** Today `tokenSource` is a private
`CancellationTokenSource` *field* of `SiriusXMPlayer` (`new CancellationTokenSource()`), not a DI
singleton; `GetHttpResponseMessage` runs `httpRetryPipeline.ExecuteAsync(..., tokenSource.Token)`,
`SiriusXMPlayer.Dispose()` calls `tokenSource.Cancel()`, and the same instance is already shared into
`SxmSessionService`, `MetadataService`, and the (new) `StreamTuner` via `tokenSource`/
`tokenSource.Token`. The chosen Option-B manual wiring preserves this exactly: `SiriusXMPlayer`
constructs `new StreamHttpClient(session, tokenSource, loggerFactory.CreateLogger<StreamHttpClient>())`
passing its **existing `tokenSource` field instance** — no new `CancellationTokenSource` is created
for the HTTP client. `StreamHttpClient` stores the `CancellationTokenSource` and uses
`tokenSource.Token` inside `ExecuteAsync` (Polly's `ExecuteAsync` consumes the token; the player
remains the sole owner that calls `Cancel()` in `Dispose()`). Because the HTTP client cancels on the
identical source the player disposes, shutdown-cancellation and the Polly cancel contract are
byte-for-byte today's behavior. (The design passes the `CancellationTokenSource` rather than a bare
`CancellationToken` so that intent — "the player owns the source, this client only reads its token" —
is explicit at the wiring site; either is behavior-equivalent as long as it is the same instance.)

**2. `IStreamTuner` / `StreamTuner`** — resolves tuning info for a channel. This is the current
`SiriusXMPlayer.TuneSource`, moved verbatim including its retry/relogin loop.

```csharp
public interface IStreamTuner
{
    Task<Streams> TuneSourceAsync(string channelId, int retries = 0);
}
```

Constructor deps (verified against the current `SiriusXMPlayer.TuneSource` body): `SxmSessionService
sxmSessionService` (for `LoginIfNecessary` and `InitializeActivityTimer()`), `APISession session`
(for `session.apiClient.TuneSourceAsync(...)` and `session.ReLogin()`), the **concrete
`MetadataService`** (for `GetChannelsAsync()` and `UpdateCutsFromStream(...)` — both are public on
`MetadataService`), `ILogger<StreamTuner>`, and the player-owned `CancellationTokenSource tokenSource`
(for its `Task.Delay(..., tokenSource.Token)` back-off). There is **no `IChannelCatalog`** — the first
draft named one but never defined it; the dependency is the concrete `MetadataService`. This is
acceptable because `StreamTuner → MetadataService` is one-way and `MetadataService` does not depend on
`StreamTuner`, so no cycle is created (see cycle argument).
No dependency on `SiriusXMPlayer` or `PlaylistService`.

`TuneSourceAsync`'s `retries` parameter is an **internal recursion knob only**: the moved body calls
itself as `TuneSourceAsync(channelId, retries + 1)` on relogin/retry and bails at `retries >= 5`
(unchanged from `TuneSource`). Both call sites (today `GetProxyPlaylistUrlAsync`'s `tuneSource`
closure and nothing else) pass only `channelId`, relying on the `retries = 0` default; `retries` is
not a caller-supplied option. The interface keeps the `= 0` default solely so the recursive call
compiles against the same method.

**3. `INowPlayingProvider`** — now-playing title lookup and fallback. Implemented by the existing
`MetadataService` (it already owns `GetNowPlaying(channelId, ts)` and `GetNowPlaying()`), exposed
through a narrow interface so `PlaylistService` depends on the interface, not the concrete type.

```csharp
public interface INowPlayingProvider
{
    Task<(string artist, string title, string? id)?> GetNowPlaying(
        string channelId, DateTimeOffset? ts, bool tryRefresh = true);
    NowPlayingData? GetNowPlaying();
}
```

`MetadataService : INowPlayingProvider`. The interface method names **and parameter arity** must
match the concrete methods exactly. C# interface implementation requires an exact parameter-list
(arity) match: a defaulted parameter is a call-site convenience and does **not** make a
three-parameter method satisfy a two-parameter interface member (that would fail to compile with
CS0535). Verified against `MetadataService.cs`:

- The concrete channel/timestamp method is
  `public async Task<(string artist, string title, string? id)?> GetNowPlaying(string channelId, DateTimeOffset? ts, bool tryRefresh = true)`
  — the name is **`GetNowPlaying`** (no `Async` suffix) and it has a third **`bool tryRefresh = true`**
  parameter. The interface therefore declares the **full three-parameter** method
  `GetNowPlaying(string channelId, DateTimeOffset? ts, bool tryRefresh = true)`, with the same
  defaulted third parameter. The three-parameter concrete method then implicitly implements it with
  **no `MetadataService` body change** — only the `: INowPlayingProvider` declaration is added.
  `PlaylistService` calls `_nowPlaying.GetNowPlaying(channelId, ts)`, relying on the interface's own
  `tryRefresh = true` default, so the observed behavior is identical to today's call site
  `metadataService.GetNowPlaying(selectedChannelId, ts)`.
  (The earlier claim that a three-parameter method with a defaulted parameter implicitly implements a
  two-parameter interface member was false and is removed — C# requires exact arity.)
- The parameterless `public virtual NowPlayingData? GetNowPlaying() => _nowPlaying;` already matches
  the interface's `NowPlayingData? GetNowPlaying()` exactly.

Consequently the `PlaylistService` call becomes `await _nowPlaying.GetNowPlaying(channelId, ts)`
(NOT `GetNowPlayingAsync`). This is the key to the cycle break for now-playing (see below). The
earlier draft's claim that a `GetNowPlayingAsync` method "already exists with this shape" was
factually wrong and is removed.

**4. `IChannelTuningSink` / channel-change ownership** — the "set current channel" step. Rather
than a new type, move the behavior into `MetadataService`, which already owns
`SetCurrentChannelAsync` and the `_currentChannel` state, and have it raise the `ChannelChanged`
event. `PlaylistService` calls it via a narrow interface:

```csharp
public interface ICurrentChannelService
{
    Task<ChannelItemData?> GetCurrentChannelAsync();
    Task SetCurrentChannelAsync(string channelId); // raises ChannelChanged when hasChanged
    event Func<string, Task>? ChannelChanged;       // async-safe seam, see event contract
}
```

`MetadataService : ICurrentChannelService, INowPlayingProvider`. The current
`MetadataService.SetCurrentChannelAsync` returns `(ChannelItemData, bool hasChanged)`; the
interface method keeps the concrete method as-is and adds the event raise on `hasChanged == true`
(details in the event contract).

### Simplified `PlaylistService` public API

```csharp
public PlaylistService(
    ILogger<PlaylistService> logger,
    ICurrentChannelService currentChannel,
    IStreamTuner tuner,
    IStreamHttpClient http,
    INowPlayingProvider nowPlaying,
    PlayerState playerState);

public async Task<string?> GetStreamPlaylistAsync(
    string channelId,
    string currentId,
    string? alias,
    bool useCache);
```

Internally, `GetStreamPlaylistAsync` keeps its exact current body with the delegate calls replaced
by collaborator calls:

- The `channelId == currentId` resolution now reads `await _currentChannel.GetCurrentChannelAsync()`
  (replacing the passed-in `currentChannel` snapshot). This is a behavior change in *who fetches*
  the current channel, not *what* is fetched: today `SiriusXMPlayer.GetStreamPlaylist` fetches it
  via `metadataService.GetCurrentChannelAsync()` and passes it in; now `PlaylistService` fetches it
  the same way through the interface. Resolution logic (`currentChannel?.Entity.Id ?? throw`) is
  unchanged.
- `await setCurrentChannel(channelId)` → `await _currentChannel.SetCurrentChannelAsync(channelId)`.
- `await getProxyPlaylistUrl(channelId)` → `await GetProxyPlaylistUrlAsync(channelId,
  useSecondary: _playerState.UseSecondaryStreamUrl)` (the field-based overload; it reads the injected
  `_tuner`/`_http` internally). See "`GetProxyPlaylistUrlAsync` public surface" and "secondary-URL
  gate" below.
- `await getHttpResponse(url)` → `await _http.GetAsync(url)`.
- `await getNowPlaying(channelId, ts)` → `await _nowPlaying.GetNowPlaying(channelId, ts)`
  (the interface method's own `tryRefresh = true` default supplies the third argument).
- `nowPlayingFallback` (today `metadataService.GetNowPlaying()`) → `_nowPlaying.GetNowPlaying()`,
  read once at the same point it is read today (the fallback is a snapshot captured before the
  rewrite loop; to preserve exact semantics, capture `var nowPlayingFallback =
  _nowPlaying.GetNowPlaying();` once near the top of the `try` block, matching today's single
  evaluation at the call site).

#### `GetProxyPlaylistUrlAsync` public surface (resolves Finding 1 / Finding 2)

`GetProxyPlaylistUrlAsync` is used by two callers that both survive the refactor:
`PlaylistService.GetStreamPlaylistAsync` (internal) and `SiriusXMPlayer.GetSegment`'s cold-start
fallback (external). It is *also* exercised directly by
`PlaylistServiceTests.GetProxyPlaylistUrlAsync_CachesStreamMetadata`, which calls the delegate
overload — so the earlier draft's claim that "nothing else calls the delegate overload" was wrong
(verified: the test calls `GetProxyPlaylistUrlAsync("channel", tuneSource, getHttpResponse,
useSecondary: false)`). The public surface is therefore decided deliberately as follows:

- **Keep exactly one public, field-based overload:**
  `public Task<string> GetProxyPlaylistUrlAsync(string channelId, bool useSecondary = false)`.
  It reads the injected `_tuner`/`_http` fields and contains the current body verbatim (primary/
  secondary selection, `ParseMasterPlaylist`, highest-`BANDWIDTH` pick, `_sxmStreams` write).
  The `bool useSecondary = false` default exists **only for clarity/source-compat** — it is never
  relied upon by any production caller. BOTH production callers pass the flag explicitly: the main
  fetch path (`GetStreamPlaylistAsync`) passes `useSecondary: _playerState.UseSecondaryStreamUrl`
  (read inside `PlaylistService`), and the `GetSegment` cold-start path passes
  `useSecondary: playerState.UseSecondaryStreamUrl` (read at its own call site). So exactly one
  documented reader governs each path and the default is dead for both.
- **Remove the delegate overload** (`(string, Func<...>, Func<...>, bool)`). Its only remaining
  callers after inversion are (a) `GetStreamPlaylistAsync`, which now calls the field-based overload,
  and (b) the one test, which is migrated in lockstep (see Test-stub impact) to construct
  `PlaylistService` with mocked `IStreamTuner`/`IStreamHttpClient` and call the field-based overload.
  No production caller of the delegate overload remains, and the test is updated in the same change,
  so the project still compiles (AC2).

Rejected alternative: keeping a second *test-visible* delegate overload purely so the existing test
stands untouched. Rejected because it leaves two public ways to call the same operation — exactly the
delegate-threading the refactor exists to remove — and the test is trivial to migrate to the mocked
collaborators the design already requires for `GetStreamPlaylistAsync`. One overload, one reader of
each collaborator.

#### Secondary-URL gate (`UseSecondaryStreamUrl`) — one documented reader per path (resolves Finding 2)

Today `useSecondary: playerState.UseSecondaryStreamUrl` is read at the `SiriusXMPlayer` call site on
both paths. After inversion there are two distinct entry points and each has exactly one documented
reader of the flag:

- **Main fetch path (`GetStreamPlaylistAsync`):** `PlaylistService` owns the read. It injects
  `PlayerState` and internally calls `GetProxyPlaylistUrlAsync(channelId,
  useSecondary: _playerState.UseSecondaryStreamUrl)`. The caller (`SiriusXMPlayer.GetStreamPlaylist`)
  no longer passes the flag — it is read inside the service. This is the single source of truth for
  the main path.
- **Cold-start fallback (`GetSegment`):** the *caller* owns the read, unchanged from today.
  `GetSegment` already reads `playerState.UseSecondaryStreamUrl` at its call site and passes it as the
  `useSecondary` argument to the field-based `GetProxyPlaylistUrlAsync` overload. We preserve that
  call-site read rather than moving it inside the service, so the cold-start path behaves byte-for-
  byte as today.

Both readers hit the same process-wide `PlayerState` singleton, so the observed value is identical;
the point of stating it explicitly is that there is exactly one reader per path and neither path
reads the flag twice. `PlayerState` is already a DI singleton with a thread-safe
`UseSecondaryStreamUrl` getter and has no dependency on any of these services, so injecting it into
`PlaylistService` adds no cycle. The `PlaylistService` constructor therefore also takes
`PlayerState playerState`.

### Channel-changed event contract

`ICurrentChannelService.ChannelChanged` is declared as `event Func<string, Task>?` — an async-safe
seam rather than a classic `EventHandler`, because the handler's work is naturally awaitable and we
want to await it before `GetStreamPlaylistAsync` proceeds to fetch (preserving today's ordering
where `SetCurrentChannel`'s side effects — including `channelChangedSource.Cancel()`/replace —
complete before the fetch continues).

**Single-subscriber contract (resolves Finding 5).** `event Func<string, Task>?` is multicast, and
`await handler(channelId)` on a multicast delegate awaits only the task returned by the *last*
subscriber — earlier subscribers run but are not awaited. The design relies on exactly one subscriber
(`SiriusXMPlayer`), which is true today and intended to stay that way: the handler reproduces the
player-owned `channelChangedSource` Cancel/replace, which must not be duplicated. We make the
single-subscriber assumption contractual rather than implicit, with two cheap guards:

1. Documented contract on the interface: `ChannelChanged` is single-subscriber; `MetadataService`
   does not fan it out and no second `+=` is permitted.
2. The raise path enumerates `GetInvocationList()` and awaits each handler, so that if a second
   subscriber is ever (incorrectly) attached the behavior is still well-defined (all handlers awaited
   in order) rather than silently dropping all-but-last. This is defensive only; with the single
   intended subscriber it is identical to `await handler(channelId)`. (The code below shows the
   enumerated form.) Awaiting the invocation-list entries **sequentially (not via `Task.WhenAll`)**
   guarantees the `Cancel()`/replace in the sole intended handler is never interleaved with another
   handler's work, should the single-subscriber contract ever be violated; a mis-wired second handler
   would run strictly after the first completes, not concurrently with the restart signal.

Raise semantics in `MetadataService.SetCurrentChannelAsync`:

```csharp
public async Task SetCurrentChannelAsync(string channelId) // ICurrentChannelService member
{
    var (channel, hasChanged) = await SetCurrentChannelCore(channelId); // existing (channel, bool) logic
    if (hasChanged)
    {
        var handler = ChannelChanged;
        if (handler is not null)
        {
            // Single intended subscriber (SiriusXMPlayer); enumerate defensively so a
            // mis-wired second handler is awaited rather than silently dropped.
            foreach (var d in handler.GetInvocationList().Cast<Func<string, Task>>())
            {
                await d(channelId); // await so side effects (Cancel/replace) complete before fetch
            }
        }
    }
}
```

The existing `(ChannelItemData, bool)` method is retained (renamed to `SetCurrentChannelCore` or
kept public for other callers) so no current caller breaks. Only the new interface method raises
the event, and it raises **only when `hasChanged`**, matching today's `if (hasChanged)` guard
exactly.

`SiriusXMPlayer` subscribes once (in its DI constructor, after wiring) and the handler reproduces
exactly today's `SetCurrentChannel` body for the `hasChanged` branch, in the same order:

```csharp
currentChannelService.ChannelChanged += OnChannelChanged;
...
private Task OnChannelChanged(string channelId)
{
    logger.LogInformation($"Setting current channel to {channelId}");
    progressTimerManager.MarkChannelChanged();
    ResetStreamUrlFallback();
    channelChangedSource.Cancel();
    channelChangedSource = new CancellationTokenSource();
    return Task.CompletedTask;
}
```

`channelChangedSource`, `progressTimerManager`, and `ResetStreamUrlFallback()` all stay in
`SiriusXMPlayer` exactly as now. The Cancel+replace is byte-for-byte identical, so the
`StreamIcecastAsync` per-iteration snapshot (`iterationChannelChanged = channelChangedSource`) and
`HlsSegmentProducer` liveness logic observe the same signal at the same moment (NFR1, AC4, AC6).

`OnChannelChanged` must be the **`Func<string, Task>`-shaped method returning `Task`** (as shown
above, `return Task.CompletedTask;`) — NOT `async void` and NOT an `EventHandler`. This matches the
`event Func<string, Task>?` declaration and the sequential-await raise path; an implementer must not
reflexively write `async void` (which would make the handler un-awaitable and let the fetch proceed
before the Cancel/replace completes) or an `EventHandler` (wrong delegate shape, would not compile
against the event). The subscription site is `metadataService.ChannelChanged += OnChannelChanged;`.

**The private `SiriusXMPlayer.SetCurrentChannel` method is REMOVED.** Its `hasChanged`
side-effect body (log → `progressTimerManager.MarkChannelChanged()` → `ResetStreamUrlFallback()` →
`channelChangedSource.Cancel()` → replace `channelChangedSource`) now lives **only** in
`OnChannelChanged`, which fires from `MetadataService.SetCurrentChannelAsync` via the `ChannelChanged`
event. `SetCurrentChannel` has **two** callers today, and BOTH are re-pointed through the event path
so the side effects run exactly once per genuine change on every path:

1. **The `GetStreamPlaylist` fetch path** — previously the `setCurrentChannel` delegate inside
   `GetStreamPlaylistAsync`. After inversion this goes through `PlaylistService` calling
   `_currentChannel.SetCurrentChannelAsync(channelId)` (= `ICurrentChannelService`), which raises
   `ChannelChanged` → `OnChannelChanged` on `hasChanged`.
2. **The DIRECT call in `SiriusXMPlayer.StreamIcecastAsync`.** Verified today's call:

   ```csharp
   if (channelId != CURRENT_ID && current?.Entity.Id != channelId)
   {
       await SetCurrentChannel(channelId);
   }
   ```

   With `SetCurrentChannel` removed this would not compile and the icecast tune path would silently
   lose its channel-change side effects. Re-point it to the same event path:

   ```csharp
   if (channelId != CURRENT_ID && current?.Entity.Id != channelId)
   {
       await metadataService.SetCurrentChannelAsync(channelId); // raises ChannelChanged -> OnChannelChanged
   }
   ```

`MetadataService.SetCurrentChannelAsync` is therefore the **single raise site** for `ChannelChanged`;
it fires `OnChannelChanged` exactly once per genuine change (`hasChanged == true`) on every path, with
no double `Cancel()`/replace. (The existing `(ChannelItemData, bool)` core logic is retained as
`SetCurrentChannelCore`/kept for other direct callers, but only the new interface method raises the
event — see the raise semantics above.)

Ordering note for the motivating bug: the secondary-URL *switch* is governed by `PlayerState` and
happens in `RequestPlaylistRefresh` (unchanged), which fires its own `channelChangedSource.Cancel()`
+ replace to restart the producer. The `ChannelChanged` event is only raised on a genuine channel
change, where `ResetStreamUrlFallback()` must run (as today). Neither path is reordered by this
refactor: the restart signal (Cancel/replace) remains owned by and emitted from `SiriusXMPlayer`,
so the secondary switch cannot land after the producer stops any differently than it does on this
already-fixed branch.

### Cycle-avoidance argument

Constructor-dependency edges after the change:

- `PlaylistService` → `ICurrentChannelService` (= `MetadataService`), `IStreamTuner`,
  `IStreamHttpClient`, `INowPlayingProvider` (= `MetadataService`), `PlayerState`, logger.
- `MetadataService` → `APISession`, `SxmSessionService`, **`PlaylistService`** (for
  `StreamTimeMap`, pre-existing), `string`, `CancellationToken`, logger.
- `StreamTuner` → `SxmSessionService`, `APISession`, concrete `MetadataService` (`GetChannelsAsync`,
  `UpdateCutsFromStream`), logger, `tokenSource` (player-owned CTS). No `IChannelCatalog`; no
  `PlaylistService` or `SiriusXMPlayer` edge.
- `StreamHttpClient` → `APISession`, the player-owned `CancellationTokenSource tokenSource`, logger.
- `SiriusXMPlayer` → everything (top of the graph); it *subscribes* to `ChannelChanged` but nothing
  constructs `SiriusXMPlayer` except the DI container.

The dangerous pair is `PlaylistService` ↔ `MetadataService`: `PlaylistService` now needs
`MetadataService` (as `ICurrentChannelService`/`INowPlayingProvider`), and `MetadataService`
already needs `PlaylistService` (for `StreamTimeMap`). A direct mutual constructor dependency would
be an unresolvable cycle.

Break: **`MetadataService` must not take `PlaylistService` by constructor.** Its only use of
`PlaylistService` is the read-only `StreamTimeMap` (`playlistService.StreamTimeMap.TryGetValue` in
`SetNowPlayingFromSegment`). Extract that behind a narrow interface:

```csharp
public interface IStreamTimeMap
{
    IReadOnlyDictionary<string, DateTimeOffset?> StreamTimeMap { get; }
}
```

`PlaylistService : IStreamTimeMap` (it already exposes `StreamTimeMap`). The direction then is:
`MetadataService → IStreamTimeMap (= PlaylistService)` and `PlaylistService → ICurrentChannelService
/ INowPlayingProvider (= MetadataService)`. Both resolve to the same two concrete singletons, so
the container would still see a constructor cycle `PlaylistService(MetadataService)` and
`MetadataService(PlaylistService)` even through interfaces — interfaces alone do not break a
construction cycle when both sides are constructor-injected.

Therefore the cycle is broken by **deferring one edge to a property/setter rather than a
constructor parameter**. Decision: `MetadataService` receives its `IStreamTimeMap` via a
**post-construction setter / init method** (`SetStreamTimeMap(IStreamTimeMap)`), called by the
composition root after both singletons exist, instead of a constructor parameter. This keeps
`PlaylistService`'s constructor dependency on `ICurrentChannelService`/`INowPlayingProvider`
(= `MetadataService`) as the only construction-time edge between the two, which is acyclic:
`MetadataService` is constructed first (it no longer depends on `PlaylistService`), then
`PlaylistService` is constructed (its deps — `MetadataService`, tuner, http, player state — all
already exist), then `MetadataService.SetStreamTimeMap(playlistService)` wires the reverse
read-only edge.

**Setter-vs-first-use invariant (resolves Finding 4).** Between `MetadataService` construction and
the `SetStreamTimeMap(...)` call there is a window where the backing field is null.
`MetadataService.SetNowPlayingFromSegment` is the sole dereference of the time map
(`StreamTimeMap.TryGetValue(...)`) and is only reachable through a live stream request — far later
than wiring — so this is not a real race. To make the invariant enforced rather than merely
documented, the design requires both of:
1. `MetadataService` stores the dependency as `IStreamTimeMap? _streamTimeMap` and `SetStreamTimeMap`
   asserts non-null on assignment (`ArgumentNullException` guard), setting it once.
2. The composition root MUST call `metadataService.SetStreamTimeMap(playlistService)` as the final
   wiring step, immediately after constructing `PlaylistService` and before `SiriusXMPlayer` is
   usable (it already appears as the last step in the construction order below). `SetNowPlayingFromSegment`
   either dereferences the now-guaranteed-set field directly or no-ops defensively if the field is
   still null (logging a debug line), preserving today's "no cuts / no current channel" fall-through
   rather than throwing. Decision: no-op-with-debug on an unset field, because `SetNowPlayingFromSegment`
   today already tolerates a missing time-map entry by falling through — a hard throw here would be a
   new failure mode. The non-null assertion in the setter catches a mis-ordered composition root at
   startup, which is where such a bug belongs.

Concretely the construction order is:
`PlayerState` → `APISession` → `SxmSessionService` → `StreamHttpClient` → `MetadataService`
(no `PlaylistService` dep now) → `StreamTuner` (needs `MetadataService`) → `PlaylistService`
(needs `MetadataService`, `StreamTuner`, `StreamHttpClient`, `PlayerState`) → then
`metadataService.SetStreamTimeMap(playlistService)`. No constructor references `SiriusXMPlayer`;
`SiriusXMPlayer` sits above all of them and only subscribes to the event. **No constructor cycle
remains** (FR7, AC8).

(`StreamTuner → MetadataService` and `MetadataService → IStreamTimeMap(PlaylistService)` do not
form a cycle because `StreamTuner` is not depended on by `MetadataService`.)

### DI wiring approach (chosen, with justification)

Two options were considered:

- **(A) Full container migration**: register `StreamHttpClient`, `StreamTuner`, `MetadataService`,
  `PlaylistService`, `PlayerState` as singletons in `Program.cs` and let the container inject them
  into `SiriusXMPlayer`.
- **(B) Keep `SiriusXMPlayer` `new`-ing the collaborators**, but pass concrete service *references*
  (not delegates) into `PlaylistService`'s constructor.

**Chosen: (B) — manual wiring inside `SiriusXMPlayer`, passing concrete references.** Justification:
the current graph is already hand-wired inside `SiriusXMPlayer`'s DI constructor
(`new APISession(...)`, `new SxmSessionService(...)`, `new MetadataService(...)`, the Polly
pipeline, etc.), and that constructor has non-DI construction logic (reading config, building
`currentChannelFile` from `IWebHostEnvironment.ContentRootPath`, `InitializeActivityTimer()`,
`StartTimeoutHandler()`, ordering of `SetStreamTimeMap`). Migrating all of that into
`Program.cs`/the container in a single pass is a large, higher-risk change that touches startup
ordering and the test-only constructor. Option B achieves the user's actual goal — *no more
`Func<...>` delegates; collaborators obtained as service references* — with the smallest blast
radius: `SiriusXMPlayer` constructs the three new collaborators (reusing `MetadataService` for the
`ICurrentChannelService`/`INowPlayingProvider` roles) and the `Polly` pipeline moves into
`StreamHttpClient`. The concrete manual-wiring sequence inside the DI constructor is:

```csharp
// playerState, session, sxmSessionService, tokenSource already constructed as today
var streamHttpClient = new StreamHttpClient(
    session, tokenSource, loggerFactory.CreateLogger<StreamHttpClient>());
// MetadataService NO LONGER takes PlaylistService; it drops that ctor param.
metadataService = new MetadataService(
    loggerFactory.CreateLogger<MetadataService>(), session, sxmSessionService,
    currentChannelFile, tokenSource.Token);
var streamTuner = new StreamTuner(
    sxmSessionService, session, metadataService,
    loggerFactory.CreateLogger<StreamTuner>(), tokenSource);
playlistService = new PlaylistService(
    loggerFactory.CreateLogger<PlaylistService>(),
    metadataService,     // ICurrentChannelService
    streamTuner,         // IStreamTuner
    streamHttpClient,    // IStreamHttpClient
    metadataService,     // INowPlayingProvider
    playerState);        // PlayerState
metadataService.SetStreamTimeMap(playlistService); // final step; wires the reverse read-only edge
// SiriusXMPlayer subscribes exactly once:
metadataService.ChannelChanged += OnChannelChanged;
```

Two signature changes fall out of this and are part of the implementation:
`MetadataService`'s constructor loses its `PlaylistService playlistService` parameter (replaced by
`SetStreamTimeMap`), and `StreamHttpClient` receives the player-owned `tokenSource` instance (see
Finding 3 resolution). Note `tokenSource` is passed to `StreamHttpClient` as the
`CancellationTokenSource` and to `MetadataService`/`StreamTuner` as before (token/source), all the
same single instance the player `Cancel()`s in `Dispose()`.

**Every other constructor statement is preserved verbatim and keeps its current relative order.**
The wiring snippet above shows only the lines that change (new collaborators, the dropped
`MetadataService` ctor param, `SetStreamTimeMap`, and the `ChannelChanged` subscription); it is not a
replacement for the rest of the constructor. Specifically, all of the following remain exactly as
today, in their current order relative to the collaborator construction: reading `IConfiguration`,
building `currentChannelFile` from `IWebHostEnvironment.ContentRootPath`,
`sxmSessionService.InitializeActivityTimer()`, `metadataService.StartTimeoutHandler()` (which drives
the 120s timeout / now-playing-reset path — must NOT be dropped), the `ProgressTimerManager`
construction (whose `MarkChannelChanged()` the `OnChannelChanged` handler calls, so it must exist
before the subscription is used), `new IcecastStreamer(logger, metadataService, this)`, and
`HlsEncryptionService`. The only thing that leaves the constructor is the `httpRetryPipeline` build,
which moves *into* `StreamHttpClient` along with `GetHttpResponseMessage`, `ConfigureRequest`, and
`getQueryParameters()` (see "`GetSegment` segment-byte fetch" below for the one caller that must be
re-pointed).

#### `GetSegment` segment-byte fetch — the remaining direct `GetHttpResponseMessage` caller (resolves Finding 3)

Verified against source: besides the two inverted call sites, `SiriusXMPlayer.GetSegment` performs
its **own** segment-byte fetch at `var output = await GetHttpResponseMessage(url, parameters);`
(where `url = $"{stream.path}{version}/{segmentId}?CMDC=";` and `parameters = getQueryParameters();`).
This call is NOT part of the inversion, but it uses the same private `GetHttpResponseMessage` /
`httpRetryPipeline` / `ConfigureRequest` / `getQueryParameters()` that the design moves into
`StreamHttpClient`. If those were simply deleted from the player, `GetSegment` would fail to compile.

Decision: **keep a thin private `GetHttpResponseMessage(string url)` on `SiriusXMPlayer` that
delegates to `_streamHttpClient.GetAsync(url)`**, and re-point `GetSegment`'s call to it (dropping the
now-internal `parameters` argument, since `getQueryParameters()` now lives inside `StreamHttpClient`).
Concretely in `GetSegment`:

```csharp
// before:
// var parameters = getQueryParameters();
// var output = await GetHttpResponseMessage(url, parameters);
// after:
var output = await streamHttpClient.GetAsync(url);
```

Either form (a one-line thin forwarder kept on the player, or calling `streamHttpClient.GetAsync`
directly) is behavior-equivalent; the design chooses the **direct `streamHttpClient.GetAsync(url)`
call** so there is a single HTTP entry point and no leftover `GetHttpResponseMessage` member on the
player. The exception behavior at the `GetSegment` call site is unchanged: `StreamHttpClient.GetAsync`
throws the identical `SegmentNotFoundException` on 404 (still caught by `GetSegment`'s
`catch (SegmentNotFoundException)` rethrow), `ApiException` on 403, and `InvalidOperationException`
otherwise, all through the same Polly pipeline (now owned by `StreamHttpClient`). The query string is
built identically because `getQueryParameters()` (currently an empty map) and the `UriBuilder.Query`
construction move verbatim into `StreamHttpClient`.

The producer's own segment fetch (in `HlsSegmentProducer`) goes through the player's public
`GetSegment` method, not through `GetHttpResponseMessage` directly, so it is unaffected by this
re-pointing — it continues to call `GetSegment`, which now reaches `streamHttpClient.GetAsync`
internally. No producer code changes.

`Program.cs` is unchanged except that it continues to register `PlayerState` and `SiriusXMPlayer`
as singletons (already present). `PlayerState` is injected into `SiriusXMPlayer` as today and
forwarded into `PlaylistService` and `StreamTuner` by `SiriusXMPlayer` during manual wiring, so a
single process-wide `PlayerState` still governs the secondary-URL escalation (FR4).

### Call-site changes in `SiriusXMPlayer`

**Call site 1 — `GetStreamPlaylist` (main path, ~line 262).** Replace the delegate-laden call with:

```csharp
var playlist = await playlistService.GetStreamPlaylistAsync(channelId, CURRENT_ID, alias, useCache);
```

The surrounding method keeps everything else: `LoginIfNecessary`, `StartStatusChecks`,
`GetCurrentChannelAsync` for the `isChannelChange`/`StartProgressTimer(isChannelChange)` logic,
`avgSegmentDuration = playlistService.AverageSegmentDuration`, and the `catch (ApiException)` →
`ReLogin` + retry. Note: `GetStreamPlaylist` still computes `isChannelChange` and calls
`StartProgressTimer` itself; the `MarkChannelChanged()` inside the event handler is the same call
that `SetCurrentChannel` made before and is distinct from `StartProgressTimer`. This preserves
NFR2 (the `ApiException` catch still works because `StreamHttpClient` still throws `ApiException`
on 403, which propagates out of `GetStreamPlaylistAsync`).

**Call site 2 — `GetSegment` cold-start fallback (~line 397).** Today it calls
`playlistService.GetProxyPlaylistUrlAsync(channelId, selected => TuneSource(selected), url =>
GetHttpResponseMessage(...), useSecondary: playerState.UseSecondaryStreamUrl)`. After the move,
`TuneSource` and `GetHttpResponseMessage` live in `StreamTuner`/`StreamHttpClient`, and
`GetProxyPlaylistUrlAsync` reads its collaborators from `PlaylistService` fields. Replace with the
simplified public overload:

```csharp
_ = await playlistService.GetProxyPlaylistUrlAsync(
        channelId, useSecondary: playerState.UseSecondaryStreamUrl);
```

`GetProxyPlaylistUrlAsync` keeps a single public method that takes only `(string channelId, bool
useSecondary = false)` and internally uses the injected `_tuner`/`_http` (see "`GetProxyPlaylistUrlAsync`
public surface"). The delegate overload is removed; its production caller (`GetStreamPlaylistAsync`)
now uses the field-based overload and its one test caller
(`PlaylistServiceTests.GetProxyPlaylistUrlAsync_CachesStreamMetadata`) is migrated in lockstep to the
field-based overload with mocked collaborators (see Test-stub impact). `_sxmStreams`/segment-path
construction inside it is unchanged (AC9).

**Call site 3 — `StreamIcecastAsync` direct channel-change call.** Today `StreamIcecastAsync`
contains a direct `await SetCurrentChannel(channelId)` guarded by
`if (channelId != CURRENT_ID && current?.Entity.Id != channelId)`. Because the private
`SetCurrentChannel` method is removed (its body is now `OnChannelChanged`, fired via the event — see
the channel-changed event contract), this call is re-pointed to raise the event through the single
raise site:

```csharp
if (channelId != CURRENT_ID && current?.Entity.Id != channelId)
{
    await metadataService.SetCurrentChannelAsync(channelId); // raises ChannelChanged -> OnChannelChanged
}
```

This preserves the channel-change side effects (`MarkChannelChanged`/`ResetStreamUrlFallback`/
`Cancel`+replace) on the icecast tune path that today's direct call guaranteed, and routes it through
the same `MetadataService.SetCurrentChannelAsync` raise site as the fetch path so the event fires
exactly once per genuine change.

### Error handling (per operation)

- **HTTP send (`StreamHttpClient.GetAsync`)**: 403 → throw `ApiException` (fatal to the fetch;
  recoverable by caller — `GetStreamPlaylist` catches it, relogs in, retries with `useCache:false`).
  404 → throw `SegmentNotFoundException` (used by the producer's 404→refresh path; fatal to the
  single fetch, recoverable via playlist refresh). Any other non-OK → `InvalidOperationException`
  (fatal, not specifically recovered; surfaces to the endpoint's 500 handler). Transient
  `HttpRequestException`/timeout → retried by the Polly pipeline (unchanged). All logged at the
  levels they are today (the Polly `OnRetry` warning; no new per-attempt error logs added).
- **Tuning (`StreamTuner.TuneSourceAsync`)**: `HttpRequestException` and non-200 `ApiException` →
  retry/relogin up to 5 times (unchanged body), then `InvalidOperationException("Too many
  retries")`. 200-status `ApiException` → `LogCritical` + `InvalidOperationException("Error loading
  cuts")`. Channel not found → `InvalidOperationException`. All identical to current `TuneSource`.
- **Now-playing (`INowPlayingProvider`)**: returns a tuple or null; nulls fall through to the
  `nowPlayingFallback` then `"- - -"` title path in `PlaylistService` (unchanged). No exceptions
  added.
- **Set current channel (`MetadataService.SetCurrentChannelAsync`)**: channel-not-found →
  `InvalidOperationException` (unchanged). The `ChannelChanged` handler is a single synchronous-ish
  continuation (`Task.CompletedTask`); it cannot throw in normal operation. If a future subscriber
  throws, the exception propagates out of `SetCurrentChannelAsync` into `GetStreamPlaylistAsync`'s
  `try/finally` (semaphore still released) and then to `GetStreamPlaylist`. Decision: do not
  swallow handler exceptions — surfacing matches today's behavior where `SetCurrentChannel`'s body
  ran inline and any failure propagated.
- **Semaphore**: unchanged — 10s wait, on timeout `Task.Delay(5s)` then return `_cachedPlaylist`
  if present else throw `TimeoutException`; `finally` always releases.

### External input validation

- `channelId` / `currentId` / `alias`: strings forwarded from the HTTP route; validation unchanged
  (the `channelId == currentId` resolution throws `InvalidOperationException("No current channel
  selected")` when there is no current channel; empty/invalid channel ids surface as
  "channel not found" from tuning/metadata, as today). No new validation is added — this refactor
  must not change observable request handling.
- `useCache`: bool, forwarded as-is.
- The collaborator interfaces receive already-resolved `channelId` and `ts` values produced inside
  the service, so no new external surface is introduced.

### Invariant ownership

- **Producer-restart signal** (`channelChangedSource` Cancel/replace): owned by `SiriusXMPlayer`,
  because the producer loop and its CTS live there. The event only *triggers* it; the player
  enforces it. This is the whole point of Option B.
- **Secondary-URL escalation** (`UseSecondaryStreamUrl`, counter, threshold=2): owned by
  `PlayerState` (unchanged). `PlaylistService` only reads the flag; `SiriusXMPlayer`
  (`RequestPlaylistRefresh`/`ResetStreamUrlFallback`) still mutates it.
- **Current-channel state** (`_currentChannel`, `currentChannel.json`): owned by `MetadataService`
  (unchanged).
- **Playlist cache / time map / `_sxmStreams`**: owned by `PlaylistService` (unchanged, including
  `_playlistSemaphore`, `_streamTimeMap` lock, concurrent `_sxmStreams`).

### Test-stub impact

`PlaylistService`'s constructor changes shape (it gains `ICurrentChannelService`, `IStreamTuner`,
`IStreamHttpClient`, `INowPlayingProvider`, `PlayerState`), and the `GetStreamPlaylistAsync` /
`GetProxyPlaylistUrlAsync` signatures lose their delegates. Three test sites touch these and MUST be
updated in lockstep, or `SXMPlayer.Tests` will not compile (this was the gap that failed AC2/FR8 in
the first draft):

**1. `HlsSegmentProducerTests.cs` — `CreatePlaylistService()` (stubs for the producer tests).**
The test-only constructor is `protected SiriusXMPlayer(ILogger<SiriusXMPlayer>, PlaylistService,
PlayerState? = null)`; stubs build a `PlaylistService` only to satisfy the player ctor. None of these
tests call `GetStreamPlaylistAsync`/`GetProxyPlaylistUrlAsync` on it — they override
`GetStreamPlaylist`/`GetSegment`/`RequestPlaylistRefresh` on the player and exercise the producer. So
`CreatePlaylistService()` just needs the new collaborators as default mocks:

```csharp
private static PlaylistService CreatePlaylistService()
    => new PlaylistService(
        new Mock<ILogger<PlaylistService>>().Object,
        Mock.Of<ICurrentChannelService>(),
        Mock.Of<IStreamTuner>(),
        Mock.Of<IStreamHttpClient>(),
        Mock.Of<INowPlayingProvider>(),
        new PlayerState());
```

(`Moq` is already referenced by the test project.) The `SiriusXMPlayerStub` using the 5-arg
`(null!, null!, null!, null!, null!)` constructor is unaffected (it does not touch `PlaylistService`).

**2. `PlaylistServiceTests.GetStreamPlaylistAsync_RewritesPlaylistAndBuildsTitles`.** This test today
constructs `new PlaylistService(new NullLogger<PlaylistService>())` and calls `GetStreamPlaylistAsync`
with the full delegate list (`currentChannel`, `setCurrentChannel`, `getProxyPlaylistUrl`,
`getHttpResponse`, `getNowPlaying`, `nowPlayingFallback`). It is rewritten to construct the service
with the new collaborators and feed the same canned data through them, so the exact
rewrite/title/`AverageSegmentDuration` assertions (AC9) are preserved:

- `ICurrentChannelService` mock: `SetCurrentChannelAsync` is a no-op and `ChannelChanged` is left
  unsubscribed. `GetCurrentChannelAsync()` is **not dereferenced on this test path**: the test passes
  `channelId: "channel"` and `currentId: SiriusXMPlayer.CURRENT_ID`, so `channelId == currentId` is
  false and the `if (channelId == currentId)` branch — the only place `currentChannel?.Entity.Id` is
  read — is never taken. The mock may therefore return `null` or a canned value with the assertions
  unaffected (the first draft's claim that it "must return a current channel so the alias resolves"
  was incorrect — alias resolution is not exercised here).
- `IStreamHttpClient.GetAsync` returns, for the first call (proxy-url resolution), the master playlist
  content, and for the media-playlist fetch the canned `playlistText` — the test can key off the URL
  or return a sequence; simplest is to make `GetProxyPlaylistUrlAsync` resolution deterministic by
  stubbing `IStreamTuner.TuneSourceAsync` to return a `Streams` with the canned primary URL and
  `IStreamHttpClient.GetAsync` to return the master then the media playlist. (Equivalent to the old
  `getProxyPlaylistUrl`/`getHttpResponse` closures.)
- `INowPlayingProvider.GetNowPlaying(channelId, ts)` returns `("Artist 1", "Track 1", "id-1")`
  and `GetNowPlaying()` returns the `NowPlayingData("channel", "Fallback Artist", "Fallback Song",
  null)` fallback — matching the old `getNowPlaying`/`nowPlayingFallback` arguments.
- `PlayerState`: a real `new PlayerState()` with `UseSecondaryStreamUrl == false` (default), so the
  primary URL is selected exactly as the old `useSecondary:false` path.

The assertions are unchanged: `#EXTINF:2,Artist 1 - Track 1`, `#EXTINF:2,Fallback Artist - Fallback
Song`, `/stream/channel/v3/...`, `#EXT-X-KEY ... /key/...`, `EVENT` not `VOD`, no `EXT-X-ENDLIST`,
`AverageSegmentDuration == 2.0`.

**3. `PlaylistServiceTests.GetProxyPlaylistUrlAsync_CachesStreamMetadata`.** This test calls the
delegate overload directly. Per the "`GetProxyPlaylistUrlAsync` public surface" decision the delegate
overload is removed, so the test is rewritten to the field-based overload: construct `PlaylistService`
with an `IStreamTuner` mock whose `TuneSourceAsync("channel", ...)` returns the canned primary
`Streams`, and an `IStreamHttpClient` mock whose `GetAsync(...)` returns `new StringContent(
"bandwidth.m3u8")`, then call `await service.GetProxyPlaylistUrlAsync("channel", useSecondary: false)`.
The assertions (`final == ".../bandwidth.m3u8"`, `TryGetStream` returns the cached stream with the
expected `path`) are unchanged, proving `_sxmStreams` caching still works through the field-based
overload.

With these three updates the test project compiles and the suite passes (AC2/FR8). No *new* test is
mandated by the refactor, though the migrated `GetStreamPlaylistAsync` test now doubles as the
unit-level proof of byte-identical rewriting (AC9) that previously required the full `SiriusXMPlayer`.
`ChannelChangedTokenForTest` and `RequestPlaylistRefresh`'s internals are untouched, so AC5/AC6/AC7
reuse the existing producer tests unchanged.

### How the hard constraints are preserved (checklist)

- Producer-restart semantics: `channelChangedSource` Cancel/replace stays in `SiriusXMPlayer`,
  invoked identically from the event handler (channel change) and `RequestPlaylistRefresh` (stale
  refresh). `HlsSegmentProducer` liveness fix and `StreamIcecastAsync` snapshot are untouched.
- Secondary-URL fallback: `PlayerState` unchanged; `RegisterStaleRefresh`, `ResetStreamUrlFallback`,
  `UseSecondaryStreamUrl`, threshold=2 all preserved; reset-on-channel-change still fires in the
  event handler.
- Exception types/flow: `StreamHttpClient` throws `SegmentNotFoundException`/`ApiException`/
  `InvalidOperationException` exactly as `GetHttpResponseMessage` did; `GetStreamPlaylist`'s 403
  retry path unchanged.
- Semaphore guard + timeout cached-playlist fallback: `GetStreamPlaylistAsync` body unchanged.
- Playlist rewriting: the rewrite loop, `AverageSegmentDuration`, ENDLIST removal, VOD→EVENT,
  `_cachedPlaylist`, highest-BANDWIDTH selection, `_sxmStreams`/segment-path all move verbatim; only
  the data *sources* (delegates → fields) change.

### Testability

- `PlaylistService.GetStreamPlaylistAsync` becomes unit-testable with mocked `ICurrentChannelService`
  /`IStreamTuner`/`IStreamHttpClient`/`INowPlayingProvider` and a real `PlayerState`, feeding canned
  master/media playlists through `IStreamHttpClient.GetAsync` and asserting the rewritten output and
  `AverageSegmentDuration` — previously this required the full `SiriusXMPlayer`. (Unit testable.)
- The `ChannelChanged` event is observable: a test can subscribe and assert it fires once on change
  and not on no-change. (Unit testable.)
- `StreamHttpClient`'s 403/404/non-OK translation is unit-testable against a stub `HttpClient`
  (integration-ish, since it uses `APISession.GetHttpClient()`), but the exception mapping itself is
  straightforward to cover.
- Producer restart ordering remains covered by the existing `HlsSegmentProducerTests`
  (integration-level over the producer lifecycle). (Already present; AC5–AC7.)

---

## Responses to design-review findings (iteration 3)

The latest review (`.agents/tasks/design-review.md`, verdict `CHANGES_REQUESTED`) confirmed all six
mandatory architectural points (no constructor cycle, exact `channelChangedSource` semantics,
unchanged producer-restart behavior, delegate-free signature, justified Option-B wiring with the
test seam addressed, and preserved hard constraints) and raised one HIGH, two MEDIUM, and two NIT
findings. All five are addressed in this revision against verified source; none is backlogged or
ignored. (Each cited fact below was re-checked by reading `MetadataService.cs` and
`SiriusXMPlayer.cs` in the worktree.)

**Finding 1 — HIGH — `INowPlayingProvider.GetNowPlayingAsync` does not match the concrete method.
ADDRESSED.** Verified the concrete method is `GetNowPlaying(string channelId, DateTimeOffset? ts,
bool tryRefresh = true)` (name has no `Async` suffix; third defaulted parameter). The interface
method is renamed to `GetNowPlaying(string channelId, DateTimeOffset? ts)` — the two-parameter
interface method is implicitly implemented by the three-parameter concrete method via its defaulted
`tryRefresh`, so no `MetadataService` body change is needed. The `PlaylistService` call site and the
migrated test both now read `_nowPlaying.GetNowPlaying(channelId, ts)`. The false "already exists with
this shape" claim is removed.

**Finding 2 — MEDIUM — phantom `IChannelCatalog`; under-specified `StreamTuner` deps / `retries`.
ADDRESSED.** The `StreamTuner` section now depends on the **concrete `MetadataService`**
(`GetChannelsAsync()`, `UpdateCutsFromStream(...)`) with the phantom `IChannelCatalog` removed, and
the cycle-avoidance edge list is corrected to match. It also states that `TuneSourceAsync`'s `retries`
is an internal recursion-only knob (callers pass only `channelId`; the `= 0` default exists for the
recursive call), not a caller-driven option.

**Finding 3 — MEDIUM — wiring snippet omits preserved constructor lines; `GetSegment`'s direct
`GetHttpResponseMessage` caller unaddressed. ADDRESSED.** Added an explicit statement that every
other constructor line is preserved in its current order — naming
`sxmSessionService.InitializeActivityTimer()`, `metadataService.StartTimeoutHandler()`, the
`ProgressTimerManager` construction, `new IcecastStreamer(logger, metadataService, this)`, and
`HlsEncryptionService` — and that only the `httpRetryPipeline` build leaves the constructor (into
`StreamHttpClient`, along with `GetHttpResponseMessage`/`ConfigureRequest`/`getQueryParameters()`).
Verified `GetSegment` calls `GetHttpResponseMessage(url, parameters)` directly at its segment-byte
fetch; the new "`GetSegment` segment-byte fetch" subsection decides and documents that this caller is
re-pointed to `streamHttpClient.GetAsync(url)` (single HTTP entry point, identical exception/Polly
behavior), and confirms the producer's segment fetch (via the player's public `GetSegment`) is
unaffected. `IStreamHttpClient.GetAsync` is now documented as having three callers.

**Finding 4 — NIT — incorrect rationale for the migrated test's `ICurrentChannelService` mock.
ADDRESSED.** The Test-stub impact section is corrected: `GetCurrentChannelAsync()` is not dereferenced
because `channelId ("channel") != currentId (CURRENT_ID)`, so the mock may return `null` or a canned
value with assertions unaffected; `SetCurrentChannelAsync` is a no-op and `ChannelChanged` is left
unsubscribed.

**Finding 5 — NIT — sequential-await intent not stated. ADDRESSED.** The channel-changed event
contract now explicitly notes that the invocation-list entries are awaited sequentially (not via
`Task.WhenAll`), guaranteeing the sole handler's `Cancel()`/replace is never interleaved with another
handler should the single-subscriber contract ever be violated.

## Responses to design-review findings (iteration 4 — final review)

The final review (`.agents/tasks/design-review.md` / `.json`, verdict `CHANGES_REQUESTED`) confirmed
all six mandatory architectural points in intent and raised one HIGH, one MEDIUM, and two NIT findings.
The architecture is unchanged; the four corrections below are folded in surgically against verified
source. None is backlogged or ignored.

**Finding 1 — HIGH — `INowPlayingProvider` two-parameter member is not implicitly implemented by the
three-parameter concrete `GetNowPlaying` (CS0535). ADDRESSED.** The `INowPlayingProvider` interface now
declares the **full three-parameter signature**
`Task<(string artist, string title, string? id)?> GetNowPlaying(string channelId, DateTimeOffset? ts, bool tryRefresh = true);`
alongside the parameterless `NowPlayingData? GetNowPlaying();`, so the concrete three-parameter method
implicitly implements it with **no `MetadataService` body change**. `PlaylistService` calls
`_nowPlaying.GetNowPlaying(channelId, ts)`, relying on the interface's own `tryRefresh = true` default.
The false claim that a defaulted third parameter makes an arity-mismatched method a valid implicit
implementation has been removed and replaced with the explicit "C# requires exact arity" note.

**Finding 2 — MEDIUM — channel-change cutover ignored `StreamIcecastAsync`'s direct `SetCurrentChannel`
call and left the method's fate unspecified. ADDRESSED.** The design now states explicitly that the
private `SiriusXMPlayer.SetCurrentChannel` method is **removed**, its `hasChanged` side-effect body
(log → `MarkChannelChanged()` → `ResetStreamUrlFallback()` → `channelChangedSource.Cancel()` → replace)
lives **only** in `OnChannelChanged`, and BOTH callers are re-pointed through the event path: (a) the
`GetStreamPlaylist` fetch path via `PlaylistService` calling `ICurrentChannelService.SetCurrentChannelAsync`,
and (b) the direct `StreamIcecastAsync` call, now `await metadataService.SetCurrentChannelAsync(channelId)`.
`StreamIcecastAsync` is added to the enumerated re-pointed callers as "Call site 3". The design confirms
`MetadataService.SetCurrentChannelAsync` is the **single raise site**, so `ChannelChanged` fires exactly
once per genuine change on every path with no double Cancel/replace.

**Finding 3 — NIT — event-handler delegate shape not pinned. ADDRESSED.** The event-contract section
now states that `OnChannelChanged` must be the `Func<string, Task>`-shaped method returning `Task`
(e.g. `return Task.CompletedTask;`), NOT `async void` and NOT `EventHandler`, matching the
`event Func<string, Task>?` declaration, and names the subscription site.

**Finding 4 — NIT — `GetProxyPlaylistUrlAsync` `useSecondary = false` default never relied upon.
ADDRESSED.** The "`GetProxyPlaylistUrlAsync` public surface" section now documents that the
`bool useSecondary = false` default exists only for clarity/source-compat and that BOTH production
callers pass the flag explicitly — the main fetch path reads `PlayerState.UseSecondaryStreamUrl` inside
`PlaylistService`, and the `GetSegment` cold-start path reads it at its own call site — so exactly one
documented reader governs each path and the default is dead for both.
