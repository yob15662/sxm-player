# Verification — DI Inversion of PlaylistService.GetStreamPlaylistAsync (Option B)

Iteration: 1 (no `review.json` present at start).
Worktree: `f:\sxm-player\.worktrees\di-playlist-inversion`
Branch: `refactor/di-playlist-inversion`

## Commands run (from the worktree root)

1. `dotnet build SXMPlayer.Client\SXMPlayer.csproj`
   - Result: **Build succeeded. 0 Error(s), 19 Warning(s).**
2. `dotnet build SXMPlayer.Proxy\SXMPlayer.Proxy.csproj`
   - Result: **Build succeeded. 0 Error(s), 17 Warning(s).**
3. `dotnet build SXMPlayer.Tests\SXMPlayer.Tests.csproj`
   - Result: **Build succeeded. 0 Error(s), 37 Warning(s).**
4. `dotnet test SXMPlayer.Tests\SXMPlayer.Tests.csproj --filter "Category!=Local"`
   - Result: **Passed! - Failed: 0, Passed: 96, Skipped: 0, Total: 96.** (matches the baseline)

## Warnings note

All warnings are pre-existing (NU1510 package-pruning advisories, CS8602/CS8604/CS8618/CS8625/CS0219
nullable/unused advisories elsewhere in the codebase). The one warning that "moved" with the refactor
is `StreamHttpClient.cs(69): CS8625 Cannot convert null literal to non-nullable reference type` on the
`new ApiException("Unexpected error", (int)response.StatusCode, responseData_, null, null)` call — this
line was moved **verbatim** from the former `SiriusXMPlayer.GetHttpResponseMessage`, which produced the
identical warning before the refactor. No net new warning category was introduced.

## Files changed

New collaborator interfaces/implementations under `SXMPlayer.Client\Services\`:
- `IStreamHttpClient.cs`, `StreamHttpClient.cs` (verbatim move of `GetHttpResponseMessage` +
  `ConfigureRequest` + `getQueryParameters()` + the Polly `httpRetryPipeline` build; takes the
  player-owned `CancellationTokenSource`, no new CTS).
- `IStreamTuner.cs`, `StreamTuner.cs` (verbatim move of `TuneSource`, including the retry/relogin loop).
- `INowPlayingProvider.cs` (3-param `GetNowPlaying(channelId, ts, tryRefresh=true)` + `GetNowPlaying()`).
- `ICurrentChannelService.cs` (`GetCurrentChannelAsync`, `SetCurrentChannelAsync`,
  `event Func<string,Task>? ChannelChanged` — single-subscriber contract documented).
- `IStreamTimeMap.cs` (read-only `StreamTimeMap`, used to break the construction cycle).

Modified:
- `SXMPlayer.Client\Services\PlaylistService.cs` — delegate-free API
  `GetStreamPlaylistAsync(channelId, currentId, alias, useCache)` and
  `GetProxyPlaylistUrlAsync(channelId, useSecondary=false)`; constructor now takes
  `ICurrentChannelService`, `IStreamTuner`, `IStreamHttpClient`, `INowPlayingProvider`, `PlayerState`;
  implements `IStreamTimeMap`. Rewrite loop, semaphore guard, timeout cached-playlist fallback,
  `AverageSegmentDuration`, ENDLIST removal, VOD→EVENT, `_cachedPlaylist`, highest-BANDWIDTH selection,
  `_sxmStreams`/segment-path construction all preserved verbatim.
- `SXMPlayer.Client\Services\MetadataService.cs` — implements `ICurrentChannelService` +
  `INowPlayingProvider`; dropped the `PlaylistService` ctor param; added `SetStreamTimeMap(IStreamTimeMap)`
  with null guard; `SetNowPlayingFromSegment` reads `_streamTimeMap` and no-ops with a debug log when
  unset; renamed old `SetCurrentChannelAsync` → `SetCurrentChannelCore`; new interface
  `SetCurrentChannelAsync` raises `ChannelChanged` (sequential invocation-list await) only on
  `hasChanged`.
- `SXMPlayer.Client\SiriusXMPlayer.cs` — DI constructor wires `StreamHttpClient`/`StreamTuner`/
  `PlaylistService` with concrete references, calls `SetStreamTimeMap(playlistService)` LAST, then
  `metadataService.ChannelChanged += OnChannelChanged`. Removed `GetHttpResponseMessage`,
  `ConfigureRequest`, `getQueryParameters()`, the `httpRetryPipeline` field, `TuneSource`, and the
  private `SetCurrentChannel`. Added `private Task OnChannelChanged(string)` (returns
  `Task.CompletedTask`; reproduces the former `hasChanged` body in order:
  log → MarkChannelChanged → ResetStreamUrlFallback → Cancel → replace CTS). Re-pointed call sites:
  main fetch to the delegate-free `GetStreamPlaylistAsync`; `GetSegment` cold-start to the
  field-based `GetProxyPlaylistUrlAsync`; `GetSegment` segment-byte fetch to `streamHttpClient.GetAsync`;
  `StreamIcecastAsync` direct call to `metadataService.SetCurrentChannelAsync`.
- `SXMPlayer.Tests\HlsSegmentProducerTests.cs` — `CreatePlaylistService()` builds the service with
  default mocked collaborators + a real `PlayerState`.
- `SXMPlayer.Tests\PlaylistServiceTests.cs` — both methods migrated to the delegate-free API with
  mocked `IStreamTuner`/`IStreamHttpClient`/`INowPlayingProvider`/`ICurrentChannelService` and a real
  `PlayerState`; assertions unchanged.
- `SXMPlayer.Tests\ProgressTimerManagerTests.cs` and `SXMPlayer.Tests\IcyTests.cs` — updated the
  `MetadataService` construction sites (dropped the removed `PlaylistService` ctor param) so the suite
  still builds.

`SXMPlayer.Proxy\Program.cs` — unchanged (the composition root is `SiriusXMPlayer`'s DI constructor
under Option B; `Program.cs` already registers the `PlayerState`/`SiriusXMPlayer` singletons).
