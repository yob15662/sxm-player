# Issue: client stream dies when every HLS segment 404s on both primary and secondary URLs

Diagnosed from production logs (channel `e6333906-2e89-6c07-59d6-36d09248b8dc`, client
`10.0.0.27`, UA `Music Player Daemon 0.23.14`). This is a read-of-logs + code-trace
investigation, not a reproduction against live SiriusXM.

---

## 1. Summary

A single client's icecast stream terminated after ~12.5 seconds and never recovered. The
user described it as "the service died for this host." The process did **not** crash; only
this client's stream ended because **no audio was ever produced** for the session.

The trigger logged in the output is `MetadataService Timeout detected for segments`, but that
is a **downstream symptom**, not the cause. The actual cause: **every HLS AAC segment fetch
returned HTTP 404 — on the primary stream URL and, after the fallback fired, on the secondary
stream URL as well.** With zero playable segments flowing, the client (MPD) received a 200
response body with no audio and disconnected. Once the client was gone, the shared producer
stopped.

This is distinct from the earlier `secondary-fallback-no-restart-findings.md` race. That race
(the producer failing to restart after the secondary fallback) is already fixed, and the logs
confirm the restart **did** happen here (`Starting HLS segment producer.` appears right after
the fallback). The remaining problem is that **both stream URLs are stale**, which no
restart, refresh, or re-login can fix.

---

## 2. Timeline from the logs

All times are `2026-10-11`.

| Time | Event | Meaning |
|------|-------|---------|
| 04:07:27.901 | `Received icecast request for channel 'e633...'` | MPD connects. |
| 04:07:27.906 | `Adding new client 10.0.0.27 - Primary: True` | First/only client, becomes primary. |
| 04:07:27.946 | `Skipping progress timer - no current channel or nowPlaying id` | **No valid now-playing state** (repeats throughout). |
| 04:07:28.663 | `Injecting ICY metadata every 8162 bytes ...` | Response body started (headers sent, HTTP 200 in flight). |
| 04:07:28.668 | `Starting HLS segment producer.` | Producer starts (primary URL). |
| 04:07:29.135 | `MetadataService no cuts or no current channel` | Metadata lookup finds nothing. |
| 04:07:29.680 | `Segment 0704_178.aac not found (404) ... refreshing playlist.` | **404 #1.** |
| 04:07:29.681 | `Playlist refresh requested - reason=segment 0704_178.aac 404 consecutiveStaleRefreshes=1` | First stale refresh. |
| 04:07:29.685 | `Starting HLS segment producer.` | Producer restarts (still primary). |
| 04:07:29.847 | `Segment 0713_930.aac not found (404) ... refreshing playlist.` | **404 #2** (debounced, no immediate escalation log). |
| 04:07:39.462 | `Segment 0723_683.aac not found (404) ... refreshing playlist.` | **404 #3.** |
| 04:07:39.462 | `Playlist refresh requested - reason=segment 0723_683.aac 404 consecutiveStaleRefreshes=2` | Threshold reached. |
| 04:07:39.462 | `Repeated stale playlist refreshes (2); falling back to secondary stream URL` | **Secondary fallback fires.** |
| 04:07:39.463 | `Shared HLS segment producer has stopped.` | Old (primary) producer tears down. |
| 04:07:39.463 | `Playlist refresh requested, restarting producer.` | Consumer detects refresh, loops. |
| 04:07:39.463 | `Starting HLS segment producer.` | **Producer restarts on the secondary URL — the restart race is NOT occurring.** |
| 04:07:40.402 | `Shared HLS segment producer has stopped.` | Producer stops ~1s later, **no error logged**, no segments produced on secondary. |
| 04:07:40.403 | `Icecast stream ended for client 10.0.0.27` | Consumer caught `OperationCanceledException` on the client token → **MPD disconnected.** |
| 04:07:40.405 | `HTTP GET /icecast/e633... => 200 in 12505.12 ms` | The whole session lasted 12.5s and delivered no audio. |
| 04:09:41.972 | `MetadataService Timeout detected for segments` | Downstream metadata timeout (symptom, not cause). |
| 10:59:07 (hrs later) | `Removing inactive client 10.0.0.27` / `Deactivating inactive client` | Unrelated cleanup when a different client connected. |

Key read of the data:
- Segment numbers advance (`0704 → 0713 → 0723`) but **every one 404s**. The player keeps
  getting a playlist and keeps asking for segments that are not there.
- `receivedAnyData` never became true: there is no `WriteWithIcyAsync`/audio-write activity,
  so `listener.LastActivity` was never refreshed past connect time.
- The producer on the secondary URL logged **nothing** between start (39.463) and stop
  (40.402) — no segments, no error. It simply produced nothing before the client left.

---

## 3. Root cause

Both the primary and the secondary SiriusXM stream URLs for this channel were serving
playlists whose referenced AAC segments return **404 Not Found**. The player's 404-handling
machinery worked exactly as designed:

1. `HlsSegmentProducer.FetchAndDecryptSegment` catches `SegmentNotFoundException`, logs
   "not found (404) ... refreshing playlist", calls `RequestPlaylistRefresh`, returns `null`
   (no per-segment retry). — `HlsSegmentProducer.cs`
2. `SiriusXMPlayer.RequestPlaylistRefresh` debounces, invalidates the playlist cache, and
   cancels/replaces `channelChangedSource` to restart the producer against a fresh playlist.
   On the 2nd consecutive stale refresh it escalates to the secondary stream URL. —
   `SiriusXMPlayer.cs`
3. The producer restarted on the secondary URL (confirmed in the log).

The machinery cannot help because the problem is upstream of the player: **the segment list
itself is stale/invalid on every available host.** Refreshing the playlist returns the same
unavailable segments; switching to the secondary host hits the same 404s; and — as the user
notes — re-login does not help, because a fresh session returns the same channel/playlist
that is itself stale. There is no code path that recovers audio when the content origin has
no servable segments.

### Why MPD disconnected (the "service died" symptom)
`StreamIcecastAsync` sends HTTP 200 and begins the body as soon as the client connects, before
any audio exists. For 12.5s no segment was ever written. MPD, receiving a stalled/empty audio
body, closed the connection; the consumer observed this as `OperationCanceledException` on the
client-disconnect token and logged `Icecast stream ended`. With the only client gone, the
shared producer had no subscribers and stopped. Nothing restarts a producer with no clients,
so to the operator it looked like the host's stream had died.

---

## 4. Likely upstream reasons the segments 404 (needs confirmation)

The logs do not contain the actual segment URLs (that line is logged at `Debug`), so these
are hypotheses ordered by likelihood:

1. **Live-edge lag / expired segments.** The player is consistently requesting segments that
   have already rolled off the live window. The advancing-but-always-404 sequence
   (`0704 → 0713 → 0723` over ~10s) is consistent with asking for segments slightly behind or
   ahead of what the origin currently serves.
2. **Stale/invalid channel GUID or playlist mapping.** The playlist is returned, but its
   segment references point at a path/version the origin no longer serves for this channel.
3. **Account/geo/entitlement edge case** returning a playlist shell without valid media on
   either host.

The accompanying "no current channel or nowPlaying id" / "no cuts or no current channel" lines
throughout suggest the player never established a valid now-playing/metadata state for this
channel, which may be correlated with the stale playlist.

---

## 5. What has already been done

A diagnostic was added so this condition is no longer silent (commit on `main`):

- `PlayerState.RegisterStaleRefresh()` now also reports `BothStreamsStale` — set when the
  player is already on the secondary URL and the secondary's own stale refreshes reach
  `SecondaryFallbackRefreshThreshold` (2). The counter resets so the signal re-fires on a
  cadence. — `Services/PlayerState.cs`
- `SiriusXMPlayer.RequestPlaylistRefresh()` logs an **Error** when `BothStreamsStale` is true:
  "Both primary and secondary stream URLs are stale - ... Audio cannot recover without a fresh
  playlist/session." — `SiriusXMPlayer.cs`
- Tests: `PlayerStateTests.SecondaryAlsoStale_SignalsBothStreamsStale_AtThreshold_AndResetsCounter`
  plus updates to the existing tuple destructures. Full suite green.

This makes the "both stale" state **observable**. It does **not** recover audio — by design,
because recovery is not possible from inside the player when the origin has no servable
segments.

---

## 6. Open questions / recommended next steps

Recovery is not achievable via re-login (confirmed by the user) or via playlist refresh/host
switch, since all return the same stale content. Recommended directions, smallest first:

1. **Surface the 404 segment URL at INFO/WARN** (currently `Debug`) so the next occurrence
   records the exact primary-vs-secondary host and path that 404s. This is the single most
   useful missing piece of evidence for distinguishing the §4 hypotheses. (Low risk.)
2. **Keep the stream alive / fail loudly instead of silently ending.** Options:
   - Hold the client connection open with a defined "no audio available" behavior (e.g. a
     short silence/filler or an explicit retry window) so MPD does not immediately drop, and
     so operators see a sustained ERROR rather than a vanished stream.
   - Or deliberately end the response with a clear server-side log that the channel has no
     servable segments, rather than a 200 that stalls.
3. **Investigate the live-edge/sequence math** (`lastMediaSequence` initialization and the
   `segmentSequence > lastMediaSequence` gate in `RunProducerAsync`) against a captured stale
   playlist, to rule out the player systematically requesting off-window segments
   (hypothesis §4.1). This is the only hypothesis with a potential in-player fix; the others
   are origin/account issues outside the player's control.

Any in-player "recovery" beyond diagnostics should be gated on first confirming §4 with the
URL-level logging from step 1 — otherwise we risk adding retry logic that cannot help because
the content genuinely is not being served.

---

## 7. Evidence / files reviewed

- Logs: provided in the issue report (channel `e633...`, client `10.0.0.27`).
- `SXMPlayer.Client/SiriusXMPlayer.cs` — `RequestPlaylistRefresh`, `StreamIcecastAsync`
  consumer loop, client activity/deactivation.
- `SXMPlayer.Client/Services/Icecast/HlsSegmentProducer.cs` — `RunProducerAsync`,
  `FetchAndDecryptSegment`, `StartProducer`/`IsProducerLive`.
- `SXMPlayer.Client/Services/PlayerState.cs` — stale-refresh escalation state machine.
- `SXMPlayer.Client/Services/MetadataService.cs` — "Timeout detected for segments".
- Prior docs: `secondary-fallback-no-restart-findings.md`, `404-refresh-plan.md`.
- Build verified green: `dotnet build SXMPlayer.Client/SXMPlayer.csproj -c Release` → 0 errors.
