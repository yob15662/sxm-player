# Implementation Plan — DI-registered global singleton `PlayerState`

## Goal

Introduce a concrete `PlayerState` class (no interface) registered as a DI singleton in the proxy container, owning the process-wide stale-refresh escalation state currently held as raw fields in `SiriusXMPlayer`, with thread-safe accessors. Replace the `Volatile.Read`/`Volatile.Write` call sites with calls into `PlayerState`, preserving runtime behavior and cross-thread memory visibility exactly.

## Exploration findings (ground truth)

- Build/test discovered from manifests: solution targets `net10.0`; tests use **xunit 2.9** + **Moq 4.20**. `SXMPlayer.Client.csproj` declares `<InternalsVisibleTo Include="SXMPlayer.Tests" />`, so `internal` members of the client are visible to the test project.
- DI wiring (`SXMPlayer.Proxy/Program.cs`): `builder.Services.AddSingleton<SiriusXMPlayer>();` — `SiriusXMPlayer` is constructed by DI via its full constructor `SiriusXMPlayer(IConfiguration, ILogger<SiriusXMPlayer>, ILoggerFactory, IWebHostEnvironment)`. No other registration touches it.
- The escalation state in `SiriusXMPlayer.cs` is a cohesive unit, all guarded by `playlistRefreshLock` on the write path:
  - `private int consecutiveStaleRefreshes;` (counter, mutated only inside `lock (playlistRefreshLock)` in `RequestPlaylistRefresh` and reset in `ResetStreamUrlFallback`).
  - `private bool useSecondaryStreamUrl;` — written with `Volatile.Write` inside the lock (lines ~344, ~370), read with `Volatile.Read` outside the lock on fetch paths (lines ~273, ~401, passed as `useSecondary:` into `playlistService.GetProxyPlaylistUrlAsync`).
  - `private const int SecondaryFallbackRefreshThreshold = 2;` — the escalation threshold.
- The threshold logic lives entirely inside `RequestPlaylistRefresh`: increment counter; if `!useSecondaryStreamUrl && consecutiveStaleRefreshes >= 2`, set the flag true and reset the counter to 0. `ResetStreamUrlFallback` resets counter to 0 and flag to false. Both run under `playlistRefreshLock`.
- Orchestration that is NOT global state and stays in `SiriusXMPlayer`: `playlistRefreshLock`, `lastPlaylistRefresh`, `PlaylistRefreshDebounce` (debounce timing), `playlistService.InvalidatePlaylistCache()`, and the `channelChangedSource` cancel/replace. These are per-player orchestration, not cross-thread shared player state.
- Confirmed exclusions remain untouched: `HlsSegmentProducer.cs` (`Volatile.Read(ref _activitySignal)` — per-producer signal) and `ProgressTimerManagerTests.cs` (test-local `Volatile.Read`).
- No existing test constructs `SiriusXMPlayer` or exercises the escalation logic; the test-only `protected SiriusXMPlayer(ILogger<SiriusXMPlayer>, PlaylistService)` ctor is currently unused by tests.
- Baseline build: `dotnet build SXMPlayer.Tests\SXMPlayer.Tests.csproj` succeeds (0 errors). Full-solution `dotnet build` fails ONLY with MSB3027/MSB3021 file-lock errors because a running `SXMPlayer.Proxy` process (and Visual Studio) hold `SXMPlayer.dll`. This is an environment constraint, not a compile error. **Verification should build/test the test project (which transitively builds the client), or the running Proxy must be stopped first.**

## Design decisions

- **Fields to move into `PlayerState`:** both `useSecondaryStreamUrl` and `consecutiveStaleRefreshes`. They are one cohesive escalation state machine mutated together under the same lock; splitting them would spread the invariant (`flag flips when counter crosses threshold`) across two owners. `SecondaryFallbackRefreshThreshold` moves in as a `public const int` on `PlayerState` because it is the threshold that governs that state. **Rationale for what stays behind:** `playlistRefreshLock`, `lastPlaylistRefresh`, `PlaylistRefreshDebounce`, cache invalidation and `channelChangedSource` are debounce/orchestration local to a single `SiriusXMPlayer` instance, not process-wide shared state — moving them would pull unrelated concerns into a global singleton.
- **Lock/escalation design — chosen option: move the cohesive escalation state AND its transition logic into `PlayerState`** (the task's second option), rather than exposing raw primitives and keeping the branching in `SiriusXMPlayer`. Reason: the increment-and-maybe-escalate decision is the whole invariant; keeping it in one method on the owner of the state keeps the transition atomic and prevents a torn read-modify-write if the fetch path ever changes. `SiriusXMPlayer` keeps `playlistRefreshLock` for its debounce window and simply calls `PlayerState` methods; `PlayerState` is independently thread-safe so the `UseSecondaryStreamUrl` read on the lock-free fetch path is correct.
- **Thread-safety primitive:** a single private `readonly object _gate = new();` inside `PlayerState`; every accessor locks on it. A C# `lock` provides a full memory barrier on entry/exit, giving the same cross-thread visibility guarantee that `Volatile.Read`/`Volatile.Write` provided — reads on the fetch path (under the lock) observe writes made on the escalation path (under the lock). This keeps the read-modify-write in `RegisterStaleRefresh` atomic, which raw `Volatile`/`volatile` fields cannot. Behavior is identical: the flag flips exactly when the counter reaches the threshold, counter resets on escalation, and a full reset returns to primary.
- **`PlayerState` public surface (keep it minimal, mirror existing behavior):**
  - `public const int SecondaryFallbackRefreshThreshold = 2;`
  - `public bool UseSecondaryStreamUrl { get; }` — thread-safe read for the fetch path (replaces `Volatile.Read(ref useSecondaryStreamUrl)`).
  - `public bool RegisterStaleRefresh()` — atomically increments the counter and, if not already on secondary and the counter has reached the threshold, flips to secondary and resets the counter; returns `true` when this call caused the escalation (so the caller can log the existing warning). Encapsulates the body of the current `if (!useSecondaryStreamUrl && consecutiveStaleRefreshes >= threshold)` block plus the increment.
  - `public int ConsecutiveStaleRefreshes { get; }` — thread-safe read, used for the existing `consecutiveStaleRefreshes={Count}` log line.
  - `public void ResetStreamUrlFallback()` — resets counter to 0 and flag to false; returns `void`. To preserve the existing "only log when the flag was actually set" behavior, provide `public bool ResetStreamUrlFallback()` returning whether the flag had been true (so `SiriusXMPlayer` logs the reset message only when it actually changed). Keep the counter-reset unconditional, matching current code.
- **DI registration & constructor threading:**
  - `Program.cs`: add `builder.Services.AddSingleton<PlayerState>();` before `AddSingleton<SiriusXMPlayer>();`. DI will inject it into the full constructor.
  - Full ctor gains a `PlayerState playerState` parameter (null-checked like the others) stored in a `private readonly PlayerState playerState;` field.
  - Test-only `protected` ctor: add an optional `PlayerState? playerState = null` parameter and assign `this.playerState = playerState ?? new PlayerState();`, so existing/future subclasses keep compiling and always have a usable instance.

## Plan items

- [ ] 1. Create the `PlayerState` class.
      Add a concrete, no-interface class owning the escalation state with a private `readonly object _gate` and all accessors locking on it: `const int SecondaryFallbackRefreshThreshold = 2`, `bool UseSecondaryStreamUrl { get; }`, `int ConsecutiveStaleRefreshes { get; }`, `bool RegisterStaleRefresh()` (atomic increment-and-maybe-escalate, returns true when this call caused escalation), and `bool ResetStreamUrlFallback()` (resets counter unconditionally and flag, returns whether the flag had been set). Namespace `SXMPlayer` to match the client assembly.
      Files: `f:\sxm-player\SXMPlayer.Client\Services\PlayerState.cs`
      Verify: `dotnet build f:\sxm-player\SXMPlayer.Tests\SXMPlayer.Tests.csproj` — succeeds with 0 errors (transitively builds the client).

- [ ] 2. Add the `PlayerState` unit tests.
      New xunit test class covering the full state machine (see test list below). Follow the existing style in `ProgressTimerManagerTests.cs` (xunit `[Fact]`/`[Theory]`, `Interlocked` for concurrency counters). No mocks needed since `PlayerState` has no dependencies.
      Files: `f:\sxm-player\SXMPlayer.Tests\PlayerStateTests.cs`
      Verify: `dotnet test f:\sxm-player\SXMPlayer.Tests\SXMPlayer.Tests.csproj --filter FullyQualifiedName~PlayerStateTests` — all new tests pass.

- [ ] 3. Thread `PlayerState` into `SiriusXMPlayer` and replace the escalation logic + `Volatile` call sites.
      Add `private readonly PlayerState playerState;` field. Add `PlayerState playerState` to the full ctor (with `ArgumentNullException` null-check) and an optional `PlayerState? playerState = null` to the test-only ctor (`this.playerState = playerState ?? new PlayerState();`). In `RequestPlaylistRefresh`, replace the counter increment + threshold branch with `playerState.RegisterStaleRefresh()`, keeping the existing debounce/lock/cache-invalidate/channel-change-cancel orchestration and preserving the existing log lines (use the return value to log the escalation warning, and `playerState.ConsecutiveStaleRefreshes` for the count log). In `ResetStreamUrlFallback`, call `playerState.ResetStreamUrlFallback()` and log only when it returns true. Replace both `useSecondary: Volatile.Read(ref useSecondaryStreamUrl)` sites (lines ~273 and ~401) with `useSecondary: playerState.UseSecondaryStreamUrl`. Remove the now-dead fields `consecutiveStaleRefreshes`, `useSecondaryStreamUrl`, and the `SecondaryFallbackRefreshThreshold` const from `SiriusXMPlayer`. Keep `playlistRefreshLock`, `lastPlaylistRefresh`, and `PlaylistRefreshDebounce` in place.
      Files: `f:\sxm-player\SXMPlayer.Client\SiriusXMPlayer.cs`
      Verify: `dotnet build f:\sxm-player\SXMPlayer.Tests\SXMPlayer.Tests.csproj` — 0 errors; no remaining `Volatile`/`useSecondaryStreamUrl`/`consecutiveStaleRefreshes` references in `SiriusXMPlayer.cs`.

- [ ] 4. Register `PlayerState` in the proxy DI container.
      Add `builder.Services.AddSingleton<PlayerState>();` immediately before `builder.Services.AddSingleton<SiriusXMPlayer>();` so DI injects the shared singleton into the full constructor.
      Files: `f:\sxm-player\SXMPlayer.Proxy\Program.cs`
      Verify: `dotnet build f:\sxm-player\SXMPlayer.Proxy\SXMPlayer.Proxy.csproj` — 0 errors. NOTE: if this fails with MSB3027/MSB3021 file-lock errors, stop the running `SXMPlayer.Proxy` process (and close/stop the build in Visual Studio) first, then rebuild — the lock is an environment constraint, not a code error.

- [ ] 5. Run the full test suite and confirm green.
      Files: none (verification only).
      Verify: `dotnet test f:\sxm-player\SXMPlayer.Tests\SXMPlayer.Tests.csproj` — all tests pass (existing + new `PlayerStateTests`).

## `PlayerState` unit tests to add (item 2)

Behavior-preservation is the point, so tests pin the exact escalation contract:

1. **Default state is primary.** New `PlayerState`: `UseSecondaryStreamUrl` is `false`, `ConsecutiveStaleRefreshes` is `0`.
2. **First stale refresh does not escalate.** One `RegisterStaleRefresh()` call returns `false`, `UseSecondaryStreamUrl` stays `false`, `ConsecutiveStaleRefreshes` is `1`.
3. **Escalation at exactly the threshold (2).** Second `RegisterStaleRefresh()` returns `true`, `UseSecondaryStreamUrl` becomes `true`, and the counter resets to `0` (matches current `consecutiveStaleRefreshes = 0` on escalation).
4. **No double escalation.** After escalation, further `RegisterStaleRefresh()` calls return `false` and leave `UseSecondaryStreamUrl` `true` (mirrors the `!useSecondaryStreamUrl` guard); the counter still increments from 0.
5. **Reset after escalation returns to primary and reports change.** With the flag set, `ResetStreamUrlFallback()` returns `true`, `UseSecondaryStreamUrl` is `false`, `ConsecutiveStaleRefreshes` is `0`.
6. **Reset when already primary reports no change.** On a fresh/primary instance (counter possibly > 0 but flag false), `ResetStreamUrlFallback()` returns `false` and clears the counter to `0` (preserves "only log when flag was set" behavior while still zeroing the counter).
7. **Threshold constant value.** `PlayerState.SecondaryFallbackRefreshThreshold == 2` (guards the behavior contract against accidental change).
8. **Thread-safety / atomicity under contention.** Spin up N parallel tasks hammering `RegisterStaleRefresh()` and assert exactly one call reports escalation (`true`) and the final `UseSecondaryStreamUrl` is `true` — proving the read-modify-write is atomic and no escalation is lost or duplicated. Use `Interlocked`/`Parallel.For` as in `ProgressTimerManagerTests`.
9. **Visibility across threads.** Write (escalate) on one task, read `UseSecondaryStreamUrl` on another after a join point, assert the reader observes the write (the memory-visibility guarantee the original `Volatile` usage provided).

## Open assumptions

- `ResetStreamUrlFallback()` returns `bool` (whether the flag had been set) solely to preserve the existing conditional log in `SiriusXMPlayer.ResetStreamUrlFallback`. If the reviewer prefers `void`, drop test 6's return-value assertion and keep the state assertions; behavior is unchanged either way.
- `PlayerState.cs` is placed under `SXMPlayer.Client\Services\` to sit alongside the other services (`PlaylistService.cs`, etc.); the client csproj globs all `.cs` so no csproj edit is needed.
