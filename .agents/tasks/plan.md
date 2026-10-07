# Implementation Plan — DI Inversion of PlaylistService.GetStreamPlaylistAsync (Option B)

Source of truth: `.agents/tasks/design.md` (APPROVED). This plan sequences that design; it does not
re-decide architecture. All paths are absolute under the worktree
`f:\sxm-player\.worktrees\di-playlist-inversion`. The cwd of the implementer is the PARENT workspace,
so always use absolute paths and `git -C f:\sxm-player\.worktrees\di-playlist-inversion` for git.

Build/test facts verified during exploration:
- Solution is C#/.NET 10. The test project transitively builds `SXMPlayer.Client` and `SXMPlayer.Proxy`
  is a separate build target. `Moq` and `Microsoft.Extensions.Logging.Abstractions` are already
  referenced by `SXMPlayer.Tests`.
- Baseline (pre-change) `dotnet test SXMPlayer.Tests/SXMPlayer.Tests.csproj --filter "Category!=Local"`
  = 96 passing, 0 failing. This is the number to preserve. `Category=Local` tests hit the live
  SiriusXM API and are expected to fail offline — do not treat their offline failure as a regression.
- `Program.cs` does NOT manually wire the collaborator graph: it registers `PlayerState` and
  `SiriusXMPlayer` as DI singletons, and `SiriusXMPlayer`'s DI constructor is the real composition
  root that `new`s `APISession`/`SxmSessionService`/`MetadataService`/`PlaylistService`/the Polly
  pipeline. Per the design's Option B, the manual wiring (new collaborators, `SetStreamTimeMap` last,
  `ChannelChanged` subscription) therefore lands in `SiriusXMPlayer`'s DI constructor. `Program.cs`
  needs NO change (it already registers the two singletons). See item 7 note.

Ordering rationale: build the new seams and their implementations first (items 1–4), each leaving the
code compiling because nothing consumes them yet; then flip `MetadataService` onto the interfaces and
the setter (item 5); then rewrite `PlaylistService`'s API against the collaborators (item 6); then
re-wire and re-point all `SiriusXMPlayer` call sites in one coherent pass (item 7) — items 6 and 7 are
mutually dependent (the signature change and its callers), so they land together as the compile-break
window and are verified by item 9's build; migrate the tests (item 8); verify (item 9).

---

- [ ] 1. Create `IStreamHttpClient` + `StreamHttpClient` (raw HTTP send with the Polly pipeline and
      the 403/404/non-OK translation). Move `SiriusXMPlayer.GetHttpResponseMessage`,
      `ConfigureRequest`, `getQueryParameters()`, and the `httpRetryPipeline` build verbatim into
      `StreamHttpClient`. Interface: `Task<HttpResponseMessage?> GetAsync(string url)`. Constructor
      deps: `APISession session`, the player-owned `CancellationTokenSource tokenSource` (store the
      source; use `tokenSource.Token` inside `ExecuteAsync` — do NOT create a new CTS), and
      `ILogger<StreamHttpClient>`. Preserve exactly: `UriBuilder` query from `getQueryParameters()`
      (empty map today), `httpRetryPipeline.ExecuteAsync(..., tokenSource.Token)`,
      `ConfigureRequest` User-Agent headers, `ApiException` on 403, `SegmentNotFoundException` on 404,
      `InvalidOperationException` on any other non-OK.
      Files: `f:\sxm-player\.worktrees\di-playlist-inversion\SXMPlayer.Client\Services\IStreamHttpClient.cs`,
      `f:\sxm-player\.worktrees\di-playlist-inversion\SXMPlayer.Client\Services\StreamHttpClient.cs`
      (do not yet delete the originals from `SiriusXMPlayer.cs` — that happens in item 7).
      Verify: `dotnet build f:\sxm-player\.worktrees\di-playlist-inversion\SXMPlayer.Client\SXMPlayer.csproj`
      succeeds (new files compile; no consumer yet).

- [ ] 2. Create `IStreamTuner` + `StreamTuner` by moving `SiriusXMPlayer.TuneSource` verbatim
      (including the retry/relogin loop and `retries` recursion knob). Interface:
      `Task<Streams> TuneSourceAsync(string channelId, int retries = 0)` (the `= 0` default exists only
      so the recursive call compiles; callers pass only `channelId`). Constructor deps verified against
      the current `TuneSource` body: `SxmSessionService sxmSessionService`, `APISession session`,
      concrete `MetadataService metadataService` (for `GetChannelsAsync()` and `UpdateCutsFromStream`),
      `ILogger<StreamTuner>`, and the player-owned `CancellationTokenSource tokenSource` (for the
      `Task.Delay(..., tokenSource.Token)` back-off). No `IChannelCatalog`; no `SiriusXMPlayer`/
      `PlaylistService` dep. Preserve exception flow exactly: `HttpRequestException`/non-200
      `ApiException` → retry up to 5 then `InvalidOperationException("Too many retries")`; 200-status
      `ApiException` → `LogCritical` + `InvalidOperationException("Error loading cuts")`; channel not
      found → `InvalidOperationException`.
      Files: `f:\sxm-player\.worktrees\di-playlist-inversion\SXMPlayer.Client\Services\IStreamTuner.cs`,
      `f:\sxm-player\.worktrees\di-playlist-inversion\SXMPlayer.Client\Services\StreamTuner.cs`
      (do not yet remove `TuneSource` from `SiriusXMPlayer.cs` — item 7).
      Verify: `dotnet build .../SXMPlayer.Client\SXMPlayer.csproj` succeeds.

- [ ] 3. Create `INowPlayingProvider` interface only (no new implementation; `MetadataService`
      implements it in item 5). Declare BOTH members matching the concrete methods with EXACT arity:
      `Task<(string artist, string title, string? id)?> GetNowPlaying(string channelId,
      DateTimeOffset? ts, bool tryRefresh = true);` (three parameters — note no `Async` suffix) and
      `NowPlayingData? GetNowPlaying();`. The three-parameter concrete method implicitly implements the
      three-parameter interface member; callers rely on the interface's own `tryRefresh = true` default.
      Files: `f:\sxm-player\.worktrees\di-playlist-inversion\SXMPlayer.Client\Services\INowPlayingProvider.cs`.
      Verify: `dotnet build .../SXMPlayer.Client\SXMPlayer.csproj` succeeds.

- [ ] 4. Create `ICurrentChannelService` and `IStreamTimeMap` interfaces only (implemented in items 5/6).
      `ICurrentChannelService`: `Task<ChannelItemData?> GetCurrentChannelAsync();`
      `Task SetCurrentChannelAsync(string channelId);` (raises `ChannelChanged` when `hasChanged`);
      `event Func<string, Task>? ChannelChanged;`. Document on the interface that `ChannelChanged` is
      single-subscriber (`SiriusXMPlayer` only; no fan-out, no second `+=`). `IStreamTimeMap`:
      `IReadOnlyDictionary<string, DateTimeOffset?> StreamTimeMap { get; }`.
      Files: `f:\sxm-player\.worktrees\di-playlist-inversion\SXMPlayer.Client\Services\ICurrentChannelService.cs`,
      `f:\sxm-player\.worktrees\di-playlist-inversion\SXMPlayer.Client\Services\IStreamTimeMap.cs`.
      Verify: `dotnet build .../SXMPlayer.Client\SXMPlayer.csproj` succeeds.

- [ ] 5. Make `MetadataService` implement `ICurrentChannelService` and `INowPlayingProvider`, break the
      `PlaylistService` constructor cycle, and raise `ChannelChanged`. Specifically:
      (a) change the class declaration to `: IDisposable, ICurrentChannelService, INowPlayingProvider`;
      (b) REMOVE the `PlaylistService playlistService` constructor parameter and its stored field;
      add `private IStreamTimeMap? _streamTimeMap;` and
      `public void SetStreamTimeMap(IStreamTimeMap streamTimeMap)` that assigns once with an
      `ArgumentNullException` null guard; (c) in `SetNowPlayingFromSegment`, replace
      `playlistService.StreamTimeMap.TryGetValue(...)` with a read of `_streamTimeMap` that no-ops with
      a debug log when `_streamTimeMap` is null (preserving today's "no cuts / no current channel"
      fall-through — do NOT throw); (d) RENAME the existing
      `public async Task<(ChannelItemData channel, bool hasChanged)> SetCurrentChannelAsync(string)`
      to `SetCurrentChannelCore` (its only caller, `SiriusXMPlayer.SetCurrentChannel`, is removed in
      item 7); (e) add the new interface method
      `public async Task SetCurrentChannelAsync(string channelId)` that calls `SetCurrentChannelCore`
      and, when `hasChanged`, raises `ChannelChanged` by enumerating `GetInvocationList()` and awaiting
      each handler SEQUENTIALLY (not `Task.WhenAll`); add `public event Func<string, Task>? ChannelChanged;`.
      `GetNowPlaying(channelId, ts, tryRefresh)` and `GetNowPlaying()` already match the interface, so no
      body change there.
      Files: `f:\sxm-player\.worktrees\di-playlist-inversion\SXMPlayer.Client\Services\MetadataService.cs`.
      Verify: `dotnet build .../SXMPlayer.Client\SXMPlayer.csproj` — expected to FAIL only at the
      `SiriusXMPlayer` constructor call `new MetadataService(..., playlistService, ...)` and
      `metadataService.SetCurrentChannelAsync(...)` usage, because that call site is not updated until
      item 7. If `MetadataService.cs` itself reports CS errors (interface not satisfied, cycle field,
      event shape), fix them here; the only acceptable remaining errors are in `SiriusXMPlayer.cs`.

- [ ] 6. Simplify `PlaylistService`: delegate-free API + implement `IStreamTimeMap`. Change the
      constructor to `PlaylistService(ILogger<PlaylistService> logger, ICurrentChannelService
      currentChannel, IStreamTuner tuner, IStreamHttpClient http, INowPlayingProvider nowPlaying,
      PlayerState playerState)` storing each in a field. Add `: IStreamTimeMap` to the class (it already
      exposes `StreamTimeMap`). Rewrite `GetStreamPlaylistAsync` to the signature
      `(string channelId, string currentId, string? alias, bool useCache)` — remove the five `Func<...>`
      params and the `ChannelItemData? currentChannel` param — keeping the body byte-for-byte except:
      `channelId == currentId` resolution reads `await _currentChannel.GetCurrentChannelAsync()`;
      `await _currentChannel.SetCurrentChannelAsync(channelId)` (returns `Task`);
      `await GetProxyPlaylistUrlAsync(channelId, useSecondary: _playerState.UseSecondaryStreamUrl)`;
      `await _http.GetAsync(url)`; `await _nowPlaying.GetNowPlaying(channelId, ts)`; and capture
      `var nowPlayingFallback = _nowPlaying.GetNowPlaying();` once near the top of the `try` (matching
      today's single evaluation). Replace the delegate `GetProxyPlaylistUrlAsync` overload with the
      single field-based overload
      `public async Task<string> GetProxyPlaylistUrlAsync(string channelId, bool useSecondary = false)`
      that reads injected `_tuner`/`_http`; keep its body (primary/secondary selection,
      `ParseMasterPlaylist`, highest-`BANDWIDTH` pick, `_sxmStreams` write) verbatim. Preserve the
      semaphore guard, timeout cached-playlist fallback, rewrite loop, `AverageSegmentDuration`,
      ENDLIST removal, VOD→EVENT, `_cachedPlaylist`, and `_streamTimeMap` lock exactly.
      Files: `f:\sxm-player\.worktrees\di-playlist-inversion\SXMPlayer.Client\Services\PlaylistService.cs`.
      Verify: build still fails only in `SiriusXMPlayer.cs` (its call sites and test-only ctor pass
      the old shapes); no CS errors inside `PlaylistService.cs`. Full green comes at item 9.

- [ ] 7. Re-wire `SiriusXMPlayer`'s DI constructor and re-point all call sites to the new seams.
      In the DI constructor: construct `streamHttpClient = new StreamHttpClient(session, tokenSource,
      loggerFactory.CreateLogger<StreamHttpClient>())`; drop the `playlistService` arg from the
      `new MetadataService(...)` call; construct `streamTuner = new StreamTuner(sxmSessionService,
      session, metadataService, loggerFactory.CreateLogger<StreamTuner>(), tokenSource)`; construct
      `playlistService = new PlaylistService(loggerFactory.CreateLogger<PlaylistService>(),
      metadataService, streamTuner, streamHttpClient, metadataService, playerState)`; then as the FINAL
      wiring step call `metadataService.SetStreamTimeMap(playlistService)`; then subscribe once
      `metadataService.ChannelChanged += OnChannelChanged`. Preserve every other constructor line in its
      current order (`sxmSessionService.InitializeActivityTimer()`, `metadataService.StartTimeoutHandler()`,
      `ProgressTimerManager` construction, `new IcecastStreamer(logger, metadataService, this)`,
      `HlsEncryptionService`); only the `httpRetryPipeline` build LEAVES the constructor (now in
      `StreamHttpClient`). Remove `GetHttpResponseMessage`, `ConfigureRequest`, `getQueryParameters()`,
      `httpRetryPipeline` field, and `TuneSource` from `SiriusXMPlayer`. REMOVE the private
      `SetCurrentChannel` method and add
      `private Task OnChannelChanged(string channelId)` (shape `Func<string,Task>` returning
      `Task.CompletedTask` — NOT `async void`, NOT `EventHandler`) reproducing the old `hasChanged` body
      in order: log → `progressTimerManager.MarkChannelChanged()` → `ResetStreamUrlFallback()` →
      `channelChangedSource.Cancel()` → replace `channelChangedSource` with a new CTS. Call-site changes:
      (1) `GetStreamPlaylist` main path →
      `var playlist = await playlistService.GetStreamPlaylistAsync(channelId, CURRENT_ID, alias, useCache);`
      (keep `isChannelChange`/`StartProgressTimer`/`avgSegmentDuration`/`catch (ApiException)` logic);
      (2) `GetSegment` cold-start fallback →
      `_ = await playlistService.GetProxyPlaylistUrlAsync(channelId, useSecondary: playerState.UseSecondaryStreamUrl);`
      and the `GetSegment` segment-byte fetch → `var output = await streamHttpClient.GetAsync(url);`
      (drop the `parameters`/`getQueryParameters()` local); (3) `StreamIcecastAsync` direct
      `await SetCurrentChannel(channelId)` → `await metadataService.SetCurrentChannelAsync(channelId)`.
      Note: `Program.cs` needs NO change — it already registers `PlayerState` and `SiriusXMPlayer`
      singletons; the composition root is this constructor. (If a reviewer expects a `Program.cs` edit
      per the task text, confirm none is needed: the manual-wiring composition root is `SiriusXMPlayer`'s
      DI constructor under Option B.)
      Files: `f:\sxm-player\.worktrees\di-playlist-inversion\SXMPlayer.Client\SiriusXMPlayer.cs`
      (and only if truly required, `f:\sxm-player\.worktrees\di-playlist-inversion\SXMPlayer.Proxy\Program.cs`).
      Verify: `dotnet build .../SXMPlayer.Client\SXMPlayer.csproj` AND
      `dotnet build .../SXMPlayer.Proxy\SXMPlayer.Proxy.csproj` both succeed with no new warnings.

- [ ] 8. Migrate the three test sites to the new constructor/API in lockstep (per design "Test-stub
      impact"). (a) `HlsSegmentProducerTests.CreatePlaylistService()` → build `PlaylistService` with
      `new Mock<ILogger<PlaylistService>>().Object, Mock.Of<ICurrentChannelService>(),
      Mock.Of<IStreamTuner>(), Mock.Of<IStreamHttpClient>(), Mock.Of<INowPlayingProvider>(),
      new PlayerState()`. (b) `PlaylistServiceTests.GetStreamPlaylistAsync_RewritesPlaylistAndBuildsTitles`
      → construct the service with mocked collaborators: `IStreamTuner.TuneSourceAsync("channel", ...)`
      returns a `Streams` with the canned primary URL, `IStreamHttpClient.GetAsync` returns the master
      then the media `playlistText` (sequence/URL-keyed), `INowPlayingProvider.GetNowPlaying(channelId,
      ts)` returns `("Artist 1","Track 1","id-1")` and `GetNowPlaying()` returns the
      `NowPlayingData("channel","Fallback Artist","Fallback Song", null)` fallback,
      `ICurrentChannelService` is a no-op mock (its `GetCurrentChannelAsync` is not dereferenced because
      `channelId != currentId`), and a real `new PlayerState()` (default `UseSecondaryStreamUrl == false`);
      call `GetStreamPlaylistAsync("channel", SiriusXMPlayer.CURRENT_ID, "channel", false)`. Keep ALL
      existing assertions unchanged (`#EXTINF:2,Artist 1 - Track 1`, fallback line, `/stream/channel/v3/...`,
      key redirect, `EVENT`, no `ENDLIST`, `AverageSegmentDuration == 2.0`). (c)
      `PlaylistServiceTests.GetProxyPlaylistUrlAsync_CachesStreamMetadata` → rewrite to the field-based
      overload: construct `PlaylistService` with an `IStreamTuner` mock whose `TuneSourceAsync("channel",
      ...)` returns the canned primary `Streams` and an `IStreamHttpClient` mock whose `GetAsync` returns
      `new HttpResponseMessage(OK){ Content = new StringContent("bandwidth.m3u8") }`, then call
      `await service.GetProxyPlaylistUrlAsync("channel", useSecondary: false)`; keep the `final ==
      ".../bandwidth.m3u8"` and `TryGetStream` path assertions unchanged.
      Files: `f:\sxm-player\.worktrees\di-playlist-inversion\SXMPlayer.Tests\HlsSegmentProducerTests.cs`,
      `f:\sxm-player\.worktrees\di-playlist-inversion\SXMPlayer.Tests\PlaylistServiceTests.cs`.
      Verify: `dotnet build .../SXMPlayer.Tests\SXMPlayer.Tests.csproj` succeeds with no new warnings.

- [ ] 9. Full verification. Build all three projects and run the non-Local suite.
      Files: none (verification only).
      Verify: from `f:\sxm-player\.worktrees\di-playlist-inversion` run
      `dotnet build SXMPlayer.Client/SXMPlayer.csproj`,
      `dotnet build SXMPlayer.Proxy/SXMPlayer.Proxy.csproj`,
      `dotnet build SXMPlayer.Tests/SXMPlayer.Tests.csproj` (all succeed, no new warnings), then
      `dotnet test SXMPlayer.Tests/SXMPlayer.Tests.csproj --filter "Category!=Local"` →
      expect `Passed! - Failed: 0, Passed: 96` (the exact baseline). `Category=Local` tests are expected
      to fail offline (live API) and must NOT be run as a gate.

---

## Notes / assumptions

- The task text lists `SXMPlayer.Proxy/Program.cs` as a composition-root edit. Exploration shows the
  real composition root is `SiriusXMPlayer`'s DI constructor (Option B keeps `SiriusXMPlayer` `new`-ing
  the collaborators; `Program.cs` only registers the `PlayerState`/`SiriusXMPlayer` singletons). The
  plan therefore places the wiring and `SetStreamTimeMap(...)`-last in `SiriusXMPlayer.cs` and treats a
  `Program.cs` change as not required. If a reviewer insists on a `Program.cs` edit, the correct action
  is to confirm no behavior change is needed there, not to migrate the graph into the container (that
  full container migration is explicitly out of scope / the rejected Option A).
- Behavior-preserving refactor: no change to producer-restart timing, secondary-URL fallback, playlist
  rewriting, exception types/flow, or the semaphore guard is permitted. Items 1, 2, 6, and 7 move code
  verbatim; the only intended behavioral delta is WHO fetches the current channel (now `PlaylistService`
  via the interface, same `GetCurrentChannelAsync` call as before).
- Single-subscriber contract for `ChannelChanged`: only `SiriusXMPlayer` subscribes; `MetadataService`
  awaits invocation-list handlers sequentially so a mis-wired second handler cannot interleave with the
  sole handler's `Cancel()`/replace.
