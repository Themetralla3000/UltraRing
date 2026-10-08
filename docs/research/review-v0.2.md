# UltraRing v0.2 guest review (after the first live runs)

Scope: `src/UltraRing.Ultrakill/**` and `src/UltraRing.Link/**` (GuestLink, GuestFrames, MappedFile, Protocol), read against
`docs/research/host-contract.md`, `docs/research/ultrakill-internals.md` and the decompile (`uk-decomp`). Nothing was run or
edited. Line numbers are per file, as in the working tree. Only things that follow from the code are listed; items that depend on
host behaviour I could not see are labelled "verify".

## Summary

No blocker found. Two majors, then minors, then dead code.

| # | Sev | Where | Problem |
|---|-----|-------|---------|
| M1 | major | `BridgeSession.cs:116-165` (and every call in it) | `LateUpdate` has no exception guard. One exception in any subsystem skips `Drive`/`ReleaseControl` for that frame, every frame, so the last control block stays live. The host never times out `COMPOSITE`, so a frozen ULTRAKILL frame stays drawn over Elden Ring and the stand-in stays pinned |
| M2 | major | `FrameCapture.cs:162-166, 215-218, 404-418` | One exception in `Submit`/`Tick`, or 8 failed readbacks in a row, disables compositing for the rest of the process, silently. No retry, no recovery after the rig is rebuilt |
| m1-m15 | minor | section 3 | |

Verified correct (no finding): seqlock order in `ReadState`/`WriteControl`/`ReadEntities`/`ReadHunterEvents`; the damage ring producer
(`write - read >= 256` refuse, continues from the existing `write`); `SubmitRays` clamps (`MaxRays` 8192 > `MaxBatchRays` 4096, so
`hits[i]` is always inside the hit array); `Cancel()`/`Callbacks` job lifetime in `FrameCapture` (a cancelled job is only reused after
its three callbacks fired); staging `NativeArray`s are disposed only after `WaitAllRequests`; `TerrainCache` thread safety (see 4);
negative coordinates in `Key()`/region file names; `Evict` radius > cache live radius (no load/evict thrash); `HostInteraction`
ack ordering matches contract section 7; Harmony targets and parameter names; `_recallAtMs = long.MinValue` is never subtracted
before `DoRecall` has run (`ready` needs `!_recallPending`).

---

## 1. Major

### M1. `LateUpdate` is unguarded: an exception leaves COMPOSITE / MOVE_HUNTER live

`BridgeSession.cs:116-165`; callees: `DoRecall` (`:228-243`, `V1.Teleport`, `V1.cs:74-106`), `_terrain.Tick` (`:154`),
`_enemies.Tick` (`:155`), `ApplyHostDamage` (`:295-316`, `nm.GetHurt`), `_interaction.Tick` (`:163`).

Scenario: V1 is driving with COMPOSITE (`Drive` wrote or `FrameCapture` landed a control block). `TerrainManager.BuildChunk` (or
`EnemyProxyManager.Flush`, `HostInteraction.UpdateLabel`, `V1.Teleport` mid-revive, `GetHurt` with a null `StyleHUD`...) throws once.
Unity logs it and continues, but the exception repeats every frame. `Drive` (`:161`) and `ReleaseControl` (`:162`) are after the
throwing call, so neither runs. The last control stays in the shm. Contract section 2.4/line 24: the compositor reads the control with
no staleness check, so COMPOSITE keeps the last frame on screen forever and the Tarnished stays pinned to the last `hunterPos`
(the host's 1 s timeout only covers the camera/stand-in, not the compositor). `Shutdown` also never runs (the process is alive).
`DoRecall` is the worst case: it throws before `_recallPending = false` (`:236`), so it retries and throws each frame.

Fix: isolate each subsystem and make the tail unconditional.

```csharp
private void LateUpdate()
{
    try { LateUpdateCore(); }
    catch (Exception e)
    {
        Plugin.Log.LogError("Bridge frame failed: " + e);          // rate-limit this in practice
        try { ReleaseControl(); _capture.Cancel(); } catch { }
    }
}

private static void Guard(string what, Action a)
{
    try { a(); } catch (Exception e) { if (_logged.Add(what)) Plugin.Log.LogError(what + ": " + e); }
}
// in LateUpdateCore:  Guard("terrain", () => _terrain.Tick(...));  Guard("enemies", () => _enemies.Tick(...));
//                     Guard("interaction", ...); DoRecall wrapped so a failure clears _recallPending or aborts the recall.
```

### M2. Frame capture is disabled for the whole process after one transient failure

`FrameCapture.cs:162-166` (`Submit` catch), `:215-218` (`Tick` catch), `:404-409` (`OnError`, 8 in a row), `:411-418` (`Disable`).

Scenario: a window resize / scene reload / camera recreation makes `Camera.Render`, `RequestIntoNativeArray` or a readback fail a few
times (each timeout costs `PendingTimeoutMs` = 2 s, so 8 timeouts are only 16 s). `_disabled = true` is never cleared; `Submit`
returns false forever, `Drive` publishes controls without COMPOSITE, and the player has the host camera and the stand-in but no
viewmodel, HUD or effects until ULTRAKILL is restarted. The log has one line.

Fix: make it a back-off, not a latch.

```csharp
private long _retryAtMs;
private void Disable(string why) { /* ... */ _disabled = true; _retryAtMs = _nowMs + 5000; Teardown(); }
public bool Submit(...)
{
    if (_disabled) { if (link.NowMs < _retryAtMs) return false; _disabled = false; _errors = 0; }
    ...
}
```

Keep a hard latch only for "cannot create frames.shm" (`:261`) and for a second failure within, say, 10 s of the retry.

---

## 2. Blocker

None.

---

## 3. Minor

### m1. Terrain: wall edges are committed as pending before the batch is known to be sent

`TerrainManager.cs:270-281` with `WallWork` (`:798-807`): `BuildBatch` has already set `EState = WPending`, `EPend`, `Queued` for
every ray in the batch when `SubmitRays` is called. `SubmitRays` can still return 0 (`GuestLink.cs:229`: `!Alive` can flip between
`Poll` and the call at the 2000 ms boundary). `_inflight` stays false, nothing resets those edges, and `WallWork` skips
`WPending` edges (`:727`), so those walls are never cast again until `Invalidate` or `Reset`.

Fix: after a failed submit, run the same cleanup as `AbandonBatch`:

```csharp
uint seq = link.SubmitRays(_rays, count);
if (seq != 0) { ...existing... }
else { _inflightCount = count; AbandonBatch(); _inflightCount = 0; _nextScan = now + 0.1f; }
```

### m2. Terrain cache: data loss when a new `TerrainCache` is created right after `Reset()`

`TerrainManager.cs:181-186` (`Reset` flushes, then `_tc = null`) and `:1364-1368` (next `CacheTick` builds a new `TerrainCache`);
`TerrainCache.cs:146-163` lists the zone directory once, in the constructor. The old instance's saves are still queued on the
worker. Any region that had no file yet is missing from the new instance's `_files`, so `Ensure` (`:194`) treats it as empty
(`Loaded = true`, no load job). The next `Save()` of that region then replaces the file written moments before, losing those
samples. Triggers: scene reload, zone change and re-entry, `TerrainCell` change, F8 round trips that re-anchor. Only cache data
is lost (it is re-sampled), hence minor.

Fix: in `Reset`, `TerrainCacheWorker.WaitIdle(500)` after `Flush()` (cheap, the queue is short), or keep one `TerrainCache` per zone in a
dictionary instead of dropping it.

### m3. Free fall before the host is up: `PinUntilFirstRecall` is unreachable without a host

`BridgeSession.cs:124-131` returns before `PinUntilFirstRecall` (`:150`, def `:246-257`). The shell removed every floor and disabled
`OutOfBounds`/`DeathZone` (`LevelShell.cs:104-111`). If ULTRAKILL boots before the host (or the host is lost while
`!_everRecalled`), V1 free-falls for as long as the host is missing, with speed growing without bound. `Teleport` zeroes velocity and
`heavyFall`, so recovery works, but `nm.fallSpeed`/camera shake/fall effects run meanwhile.

Fix: call `PinUntilFirstRecall(V1.Movement)` in the `!_alive || !haveState` branch too.

### m4. `HostMode` is not cleared when the host disappears

`BridgeSession.cs:383-389, 391-397`; the only exit is `mcSwitchReq` changing, read in `ReadCounters` (`:202-206`), which needs a live host.
Scenario: F8, then the host crashes or is restarted. `WindowOverlay` restores the normal ULTRAKILL window (`WindowOverlay.cs:189-192`,
host window gone), `HostMode` stays true: `Update` ignores F8 (`:113`), `ready` is false (`:160`), the overlay will not re-apply
(`WindowOverlay.cs:200`). The user sees a plain ULTRAKILL window that does nothing until the new host's F8 is pressed (works, since
`mcSwitchReq` changes, but nothing says so).

Fix: `if (!_alive && HostMode && Link.NowMs - _hostGoneSinceMs > 2000) ExitHostMode();`

### m5. Counters are baselined once per process, not per host lifetime

`BridgeSession.cs:171-184`, `:298-304`. If the host process restarts and its `hostDeaths`/`hostLife`/`mcSwitchReq` restart from a lower
value (verify: depends on whether the host increments the shm field in place or copies a process-local counter), `deaths != _last`
(`:191`) fires, and V1 is killed (`GetHurt(99999)`, `:199`) for a death that never happened; `hostLife` goes backwards and recalls
(harmless); `sw` triggers `ExitHostMode` (harmless). `ApplyHostDamage` already handles a decreasing `hitCount`.

Fix: `if (!_alive) { _countersInit = false; _hunterInit = false; }` before returning at `:124`, and only treat `deaths > _lastHostDeaths`
(unsigned compare) as a death.

### m6. `_deathFromHost` can stick

`BridgeSession.cs:198-199`: set before `GetHurt`, cleared only when V1 transitions to dead (`:324-330`). `GetHurt` returns without
effect when `levelOver`, or when the `Invincibility` cheat is on (`NewMovement.cs:1906, 1914`; "Cheats Enabler" is kept by the
shell). The flag then stays true and V1's next real death is logged as host-caused and `BumpGuestDeaths` is skipped
(`:324`), so the host stand-in does not die.

Fix: set the flag only if `nm.dead` afterwards: `nm.GetHurt(...); _deathFromHost = nm.dead;`.

### m7. Host damage that lands inside V1's hurt invincibility is consumed but lost

`BridgeSession.cs:298-315` advances `_lastHits/_lastTotalDamage` before `nm.GetHurt(damage, true)`. `GetHurt(.., invincible: true)` sets
`layer = 15` and `hurtInvincibility = 0.5 s` (0.8 s for >= 50 damage) (`NewMovement.cs:1906, 1932, 742-744`) and returns early while the layer
is 15. A 3-hit ER combo inside that window deals only the first hit; DoT ticks (`totalDamage` events every few ticks) do nothing after the
first. Intentional per the comment on `:314`, but the host-side counters make it permanent loss. Also `Mathf.Max(1, ...)` (`:313`) turns any
sub-1 % event into a full point.

Fix (if wanted): keep a `_pendingDamage` accumulator, drain it when `nm.gameObject.layer != 15`, and use `invincible:false` for chip damage:

```csharp
_pending += damage;
if (nm.gameObject.layer != 15 && _pending > 0) { nm.GetHurt(_pending, true); _pending = 0; }
```

### m8. Parry source search compares box centres with an attacker's feet

`ParrySystem.cs:214-217`: `Nearest(map.ToUk(lastHitFrom), 6 m)` measures distance from the source (the attacker's **feet**, contract
line 465) to `CenterWorld` (the box centre, `boxHalf.y` above the feet, `EnemyProxy.cs:417`). For a large entity with `boxHalf.y` > ~4 m
the attacker is further than `SourceSearchMetres` from its own foot position, so a boss hit never parries. With two enemies close
together the wrong one can win.

Fix: measure to the box surface: `RootCol.ClosestPoint(src)` inside `Nearest`, or compare against `TargetFeet`.

### m9. `Flush` on proxy destruction is dead; pending damage is dropped on rebuild/clear

`EnemyProxyManager.Destroy(p, link, map)` (`:298-315`) is only ever called with `null, null` (`:89, 118, 268, 329`), so its flush branch
never runs. `:118` (revive-rebuild, 0.6 s after a local kill) and `:329`/`Clear` discard `PendingFraction` that could not be sent (ring full).
Only `RemoveStale` (`:264`) flushes. Practically rare. Either pass `link, map` or delete the parameters.

### m10. `_movedLayers` grows without bound and new HUD objects flash on the hand layer

`CaptureRig.cs:478, 633-642`: keys are every HUD transform ever moved to layer 3 (style-HUD items are instantiated and destroyed
all the time); destroyed `Transform`s are never removed until the rig is destroyed. A long session accumulates thousands of entries and
`MoveTree` walks the whole HUD every 30 frames (`:624`, allocates via `GetComponentsInChildren`). A freshly instantiated item is on
layer 13 until the next rescan, so it appears in the viewmodel layer for up to 30 frames.

Fix: prune dead keys at each rescan (`_movedLayers.Where(k => k.Key == null)` into a list, remove), and rescan on
`StyleHUD`/canvas child-count change instead of a fixed 30 frames if the glitch matters.

### m11. Stale COMPOSITE is only cleared once the bridge scene is running; crashes are not covered

`BridgeSession.cs:133-139` is after `if (!InBridgeScene) return;` (`:122`). If the first scene is the menu (`StartInSandbox = false`) or
a previous ULTRAKILL died with COMPOSITE set, the host keeps drawing the dead frame until the sandbox is loaded. `OnApplicationQuit`
(`:399`) does not run on a kill, so the host stays composited until the next start.

Fix: clear the control right after the first successful `Poll()` (in `Plugin`/`GuestLink`), and also write the release from
`AppDomain.CurrentDomain.ProcessExit` and `Application.quitting`.

### m12. Config values that break things

`BridgeConfig.cs:43, 47`, `CoordMap.cs:29`, `LevelShell.cs:72`. `MetresPerUnit <= 0` (or NaN) makes `1.0 / MetresPerUnit`
infinite and every `ToUk` NaN: V1 teleports to NaN, the physics engine rejects it. Negative mirrors the world. Tiny values
(0.001) and huge ones give absurd scales (`WallParams.For`, `FloorGrowMetres / mpu`). `TargetFrameRate <= 0` other than -1 is not a defined Unity
value. `TerrainCell` has only a lower clamp (`TerrainManager.cs:397`; NaN passes `Mathf.Max`), `HostDamageScale` <= 0 still deals 1 damage
per event (`:313`), `InteractKey == SwitchKey` makes F8 also fire the interaction.

Fix: clamp once in `BridgeConfig.Bind` (or `Plugin.Awake`):

```csharp
MetresPerUnit.Value = Mathf.Clamp(float.IsNaN(MetresPerUnit.Value) ? 0.5f : MetresPerUnit.Value, 0.05f, 5f);
TargetFrameRate.Value = TargetFrameRate.Value <= 0 ? -1 : Mathf.Clamp(TargetFrameRate.Value, 20, 1000);
```

### m13. Open-world travel pushes V1 far from the Unity origin

`BridgeSession.cs:211-226`: one `CoordMap` per zone, anchored at the first position seen, never re-based. The open-world zones
(`0x3C000000`/`0x3D000000`) are ~15 km across: at 0.5 m/unit that is ~30000 units from the anchor. Single-precision physics, camera
and terrain mesh vertices jitter noticeably past ~10000 units. Terrain/cache are host-metre based, so a re-anchor is cheap
(`TerrainManager.Reset` only drops the colliders): when `|V1| > 5000` units, build a new `CoordMap` with the anchor moved to V1 and
shift (or recall) V1 to the origin.

### m14. Smaller leaks and robustness

- `FrameCapture.cs:190-195`: a readback that times out keeps its job `InUse` until its callbacks fire; if they never do (device lost), the
  pool (4 jobs) is exhausted and capture silently starves (`Submit` just increments `_skipped`, `:128-130`). Reclaim a cancelled job after, say, 10 s.
- `FrameCapture.cs:229-239` / `Submit` `:128-130`: when frames are skipped (queue full / no free job), no control is published and
  the keepalive (`:200-206`) re-sends the old pose, so the host camera and stand-in freeze for the duration. Stale-pose keepalive is
  right for liveness but should not outlive ~100 ms; consider dropping COMPOSITE for the keepalive when the last landed frame is older than 250 ms.
- `FrameCapture.Land` (`:379-386`): if a layer copy throws after `BeginSlot`, the slot stays odd (open) forever; `Abort(slot)` belongs in a `catch`.
- `HostInteraction.cs:53-60`: after the 2 s timeout `_pending` is cleared; an ack that arrives later (result 1) never schedules the terrain
  resample (door opened, collision stale).
- `HostInteraction.cs:103`: `$"[{InteractKey}]  {_prompt}"` allocates (and `KeyCode.ToString()`) every frame while a prompt is shown. Cache by `_prompt`.
- `EnemyProxyManager.cs:99-143`: proxies are created for every hostile entity in the table with no distance cut; each carries an
  `EnemyIdentifier` and three colliders. In dense areas the 256-entity table means up to 256 proxies. Consider a radius (e.g. 80 m) with hysteresis.
- `EnemyProxyManager.cs:107-114`: a destroyed proxy is left in `_byId` when `RemoveStale` hits a Unity-null `p` (`:266` skips the removal). Remove by id from the stale list instead.
- `TerrainManager.cs:172-176`: `Application.quitting` is subscribed per `TerrainManager` and never removed (one instance today, so fine).
- `Create` failing for an entity (`EnemyProxyManager.cs:236-241`) retries every frame with a new exception each time (logs once, but still throws/allocates 60-144x/s).
- `BridgeSession.cs:85-106`: `_holdingForGround`, `HostMode`, `_hostGoneSinceMs` survive a scene change; `_holdingForGround` is harmlessly overwritten by the next recall, `HostMode` is not (see m4).

### m15. Hostile-entity table has no zone field

`GuestLink.ReadEntities` (`:268-284`) and `ApplyEntity` use positions in the stable frame of the zone in force (contract section 3.2). For a few host
frames after a zone change the table may still hold the old zone's entities; `UpdateAnchor` makes the new map first, so proxies are
created at wrong positions and then removed/teleported (`EnemyProxy.FixedUpdate` snaps beyond 6 units). Suppress `_enemies.Tick` for ~300 ms after
`UpdateAnchor` creates a map.

---

## 4. Checked, no bug: terrain cache threading

`TerrainCache`/`TerrainCacheWorker` (`TerrainCache.cs`): the worker touches only `File`, `DeflateStream`, `Interlocked` statics, a
`ConcurrentQueue` and `Log` (a BepInEx log call, not Unity). The records it reads are immutable (`MergeIn`/`Apply` clone before
overlaying, `SnapshotChunk` hands over a new record and never touches it again), `Dirty/Loaded/Pending/_regions` are main-thread only,
`SavesPending` is `Interlocked`, a region with `SavesPending > 0` or `Dirty` or `Pending` is never dropped, jobs run FIFO so a reload
always sees an earlier save of the same instance. Files: written to `.tmp` then `File.Replace`/`Move`, payload fnv1a + length +
region/cell/version checked on load; a crash leaves the old file or a `.tmp` that `*.bin` does not match. Memory is bounded by
`CacheUnloadRadius` (~35 regions at most with the defaults). Residual risks: m2 above, the delete+move fallback in `WriteAtomic`
(`:354-358`) is non-atomic if the process dies between the two calls, and disk use per zone is unbounded.

---

## 5. Dead code and leftovers

- `GuestLink.WriteCollision` (`GuestLink.cs:135-150`), `ReadContacts` (`:309-330`), `WriteEnvironment` (`:152-163`): no caller in the guest
  (contract 5.2: host never reads/writes these). With them: `ErmcTerrainContact`, `ErmcTerrainContactsHeader`, `ErmcCollisionControl`,
  `ErmcEnvironment`, `ErmcPassage`, `ErmcPlatformCell` and the `Off*`/`Max*` constants for passages, platforms, contacts, collision control, environment.
- `HostLink.cs` (682 lines, host side) is compiled into the guest plugin through the shared project; only `GuestLink` is used.
- `WindowOverlay.cs`: `_lastFocused` (`:83, 572`, write only), `_lastAvailableMs` (`:53, 184`, write only), `_hostWasAvailable`
  (`:52, 192, 196`, write only), property `OverlayActive` (`:95`, no reader).
- `TerrainManager.cs`: `_totalBatches` (write only); `Entry.Ver` is used, `_lastSeq` only for status.
- `EnemyProxyManager.cs`: `_hostileCount` (write only); the `link, map` parameters of `Destroy` (m9).
- `FrameCapture.cs:221`: orphaned `/// <summary>Drop pending captures ...` above `ReleaseRig`; `ReleaseRig` and `Teardown` are the same body.
- `Plugin.cs`: nothing unused; `StartupPatches.cs` fine.

---

## Resolution

Build (`dotnet build UltraRing.sln -c Release`): 0 errors. `UltraRing.Link.Tests`: ALL OK.

- **M1**: `BridgeSession.LateUpdate` now wraps `LateUpdateCore` in try/catch (log once, `ReleaseControl` + `_capture.Cancel`). Each subsystem (counters, anchor, host damage, recall, pin, rebase, terrain, enemies, ground hold, interaction, capture tick) runs through `Guard` (logged once). `DoRecall` failure clears `_recallPending` so it does not retry every frame.
- **M2**: `FrameCapture` failure is a 5 s back-off with retry; hard latch only for frames.shm creation failure or a failure within 10 s of a retry.
- **m1**: failed `SubmitRays` runs `AbandonBatch` and retries after 0.1 s.
- **m2**: `TerrainManager.Reset` waits for the cache worker (500 ms) before dropping the cache instance.
- **m3**: `PinUntilFirstRecall` also runs while the host is missing.
- **m4**: host gone > 2 s exits `HostMode`.
- **m5**: counters/hunter baselines reset when the host is gone; a host death needs `deaths > last`.
- **m6**: `_deathFromHost = nm.dead` after `GetHurt`.
- **m7**: host damage accumulates in `_pendingDamage` (fractions kept, no forced minimum of 1), is applied once hurt invincibility ends; a dash (layer 15 without hurt invincibility) still dodges.
- **m8**: `Nearest` measures to the proxy box surface (`RootCol.ClosestPoint`).
- **m9**: `Destroy` lost its unused `link, map` parameters; revive-rebuild flushes pending damage first. `Clear` still discards (no map/link at that point).
- **m10**: dead keys are pruned from `_movedLayers` at each HUD rescan (rescan interval unchanged).
- **m11**: stale control is cleared on the first successful poll in any scene; release also on `AppDomain.ProcessExit`.
- **m12**: `BridgeConfig.Validate` clamps MetresPerUnit, HostHpPerUkHp, HostDamageScale, CaptureScale, Terrain Radius/CellSize/StepHeight, TargetFrameRate and separates InteractKey from SwitchKey, with warnings; `CoordMap` also guards its scale.
- **m13**: V1 further than 5000 units from the origin triggers a re-anchor of the `CoordMap` at V1 plus a recall.
- **m14**: stuck cancelled capture jobs are replaced after 10 s (old buffers leaked on purpose); keepalive drops COMPOSITE when the last landed frame is older than 250 ms; `Land` aborts the slot on exception; late interaction ack (result 1) still schedules the terrain resample; interaction prompt string is cached; proxies only spawn within 80 m of V1 (dropped beyond 100 m); stale proxies removed by id; failed proxy creation retries after 5 s per entity. Not changed: the `Application.quitting` subscription (single instance), scene-change survival of `_holdingForGround`.
- **m15**: `_enemies.Tick` suppressed for 300 ms after a new `CoordMap` is created.
- **Dead code**: removed `WriteCollision`, `ReadContacts`, `WriteEnvironment` (and their test smoke call), `_lastFocused`, `_lastAvailableMs`, `_hostWasAvailable`, `OverlayActive`, `_totalBatches`, `_hostileCount`, the orphan summary in `FrameCapture` (`Teardown` now calls `ReleaseRig`). Protocol structs and `HostLink.cs` kept.
