# S31 — Retained pawn exit reservation (the Free-world-pawn window)

**Verdict: NOT RUN — OWNER RUNTIME VALIDATION REQUIRED**

**Final mechanism:** `UNDECIDED` *(allowed values after the owner's run: `UNDECIDED` · `M1 ACCEPTED` · `M1 FAILED → M2 REQUIRED` ·
`M1 INCONCLUSIVE → RE-RUN`; only an owner-reviewed run may change this line, and `M1 ACCEPTED` also needs an ADR)*

This is a **runtime spike, not Phase 3.1**. It builds and instruments candidate **M1** only. It adopts no mechanism, implements no
production physical port and no production registry, and contains no Harmony and no C-4. **Phase 3.1 stays blocked** until this record
holds owner-reviewed runtime evidence ([PHYSICAL_LIFECYCLE § 7.6](../PHYSICAL_LIFECYCLE.md#76-the-vanilla-exit-window-an-open-mandatory-spike-s31)).

## 1. The question

> How do we guarantee that a retained named Network pawn is never exposed to vanilla as an ordinary reusable, redressable or
> discardable `Free` world pawn between a vanilla map exit and the point where Network authority may safely become `Stored`?

The invariant is frozen: **P3-INV-032 — a retained named pawn is never exposed to vanilla redress, discard, reuse or faction rewrite
between physical exit and `Stored` authority.** The mechanism is not. Candidates, tried in this order:

1. **M1** — reserve while still spawned (this pass builds and instruments it);
2. **M2** — reserve synchronously at a vanilla-supported callback (this pass adds **read-only observability** only);
3. **M3** — the narrow Harmony contingency C-4 (**not implemented**; reachable only after M1 and M2 fail, by ADR).

## 2. Build and environment

| Item | Value |
|---|---|
| Base | `main` at `6a478669c005faba90e426877bfc9445aa667ae4` (Phase 3.0 merged) |
| Spike branch | `claude/spike-s31-retained-pawn-exit` (draft PR, not for merge); the DLL's first `[TheNetwork]` log line prints the exact source commit |
| Game assemblies | `Assembly-CSharp 1.6.9676.17735` (owner-provided, external reference only) |
| Vanilla XML | the owner's `zRim_Source_XMLs` (Core, Royalty, Ideology, Biotech, Anomaly, Odyssey defs), read for the def audit in § 4 |
| Save format | **5, unchanged.** The spike adds no Network state; everything it leaves in a save is vanilla data carrying an S31 marker |
| Headless coverage | `S31.*` tests prove the arm, the markers, the checkpoint codec, the pass/fail rules and the source isolation. **They are not a runtime result.** |

## 3. What the 1.6 source says (read before building; still to be confirmed at runtime)

| # | Fact (1.6.9676) | Where | Consequence for S31 |
|---|---|---|---|
| 1 | `WorldPawns.GetSituation` returns `None` for a pawn not in `WorldPawns`; for a world pawn it returns `ReservedByQuest` when `QuestUtility.IsReservedByQuestOrQuestBeingGenerated` is true (after `FactionLeader`, `Kidnapped`, `CaravanMember`) | `WorldPawns.GetSituation` | the reservation can be **set** while spawned, but only **reads** as `ReservedByQuest` once the pawn is a world pawn |
| 2 | `Pawn.Suspended` = `Thing.Suspended` **or** `GetSituation == ReservedByQuest` | `Pawn.Suspended` | structurally, a reserved **spawned** pawn is not suspended; S31 must still observe it behaving normally (M1's first criterion) |
| 3 | `Pawn.ExitMap`: despawn → `WorldPawns.PassToWorld(this)` (`AddPawn` → `Notify_PassedToWorld`) → `SendQuestTargetSignals("LeftMap")` → `FactionManager.Notify_PawnLeftMap` | `Pawn.ExitMap` | a `LeftMap` handler runs after the pass and before the faction-removal check, in the same call |
| 4 | `Notify_PassedToWorld` rewrites the faction only of a **`Free`** humanlike pawn whose faction is null, the player's or Ancients' | `Pawn.Notify_PassedToWorld` | a pawn already reserved at the pass is never rewritten there |
| 5 | Map removal (`Game.DeinitAndRemoveMap` → `MapDeiniter.PassPawnsToWorld`) despawns and passes every pawn; `LeftMap` only for player/player-hosted pawns and colonists captured by a hostile parent; **no** `Notify_PawnLeftMap` | `MapDeiniter` | no callback for an M2 reserve on map removal; under M1 the reservation already exists |
| 6 | `FactionManager.FactionCanBeRemoved` checks temporary, a **faction** quest reservation, spawned pawns, caravans and world objects, **never world pawns**; `Remove` sets **every** pawn of the faction (world pawns included) to faction null | `FactionManager` | a pawn reservation does not keep the temporary faction; § 13.2 accepts the null faction **only while the pawn is not `Free`** |
| 7 | Redress candidates are `GetPawnsBySituation(Free)` (plus `FactionLeader` for leader kinds), filtered by faction unless `WorldPawnFactionDoesntMatter` | `PawnGenerator.GetValidCandidatesToRedress` | a `ReservedByQuest` pawn is never a candidate, on any path |
| 8 | The GC keeps a pawn with a critical reason; `ReservedByQuest` is one; `AccumulatePawnGCDataImmediate()` is public and only computes the kept set | `WorldPawnGC` | the spike reads the GC verdict without running a destructive pass |
| 9 | `Pawn.SpawnSetup` removes a world pawn from `WorldPawns` itself | `Pawn.SpawnSetup` | rematerialization needs no Network `PassToWorld` and no `RemovePawn` |
| 10 | A quest needs a non-null `root`: `QuestManager` drops root-less quests on load, and `CleanupQuestParts` reads `root.hideOnCleanup` | `QuestManager`, `Quest` | the fixture uses a vanilla root |

## 4. The harness (what was built)

**Code:** `Source/TheNetwork/Diagnostics/Spikes/S31/` (`S31Model.cs` pure rules and session; `S31World.cs` RimWorld side;
`S31Spike.cs` runs, status, cleanup; `S31DevActions.cs` the menu and the arm dialog), the def file
`1.6/Defs/Spikes/TheNetwork_S31_SpikeDefs.xml`, and one pump line in `NetworkWorldComponent.WorldComponentUpdate` (one static null
check while idle). The safe runtime suites cannot reach any of it (headless test and shell scan).

**M1 reservation fixture (vanilla, exactly):** `Quest.MakeRaw()`, `root = QuestScriptDefOf.Util_GetDefaultRewardValueFromPoints` (a
utility script no storyteller, dialog or quest-uniqueness check compares against), `hidden = true`, `hiddenInUI = true`, name "The Network
S31 spike reservation (dev only)", quest tag `TheNetwork_S31_Manifest`, one **vanilla `QuestPart_ReservePawns`**, `SetInitiallyAccepted()`
(state `Ongoing`), `QuestManager.Add`. Each probe is added to `QuestPart_ReservePawns.pawns` **while spawned, before any exit**. The save
therefore names no S31 type. *This does not settle S9r:* the Network-owned part versus the vanilla part, and their self-healing on mod
removal, remain S9r's comparison.

**Dedicated test map:** a **spike-only** `WorldObjectDef` `TheNetwork_S31_TestMap` (plain `MapParent`, no comps, not a player home, no
incident tags, the default `Encounter` generator), 100 × 100, on an empty temperate tile, no faction, fog cleared. **No vanilla def
qualifies** (from the 1.6 XML):

| Vanilla def(s) | Why not |
|---|---|
| `Debug_Arena` (the only exact-`MapParent` def) | its `DebugArena` comp errors when not set up as a fight, self-destructs after 10,000 ticks once set up, and loses its pawn lists on load |
| `DestroyedSettlement`, `Camp`, `Ambush`, `AttackedNonPlayerCaravan` | remove the map as soon as no player pawn is present (immediately, here); `TimedDetectionRaids` |
| `Settlement`, `EscapeShip` | player homes: raids, colony semantics, abandonment effects |
| `Site` | needs site parts; `Timeout` and other comps |
| `PocketMap`, Odyssey space/asteroid types | pocket or space maps (no edge exit), DLC-only |
| `AbandonedSettlement`, `PeaceTalks`, `AbandonedCamp` | no map |

**Fixture faction:** one **temporary hidden** faction per scenario, as § 13.2 designs it (`FactionGenerator.NewGeneratedFactionWithRelations(def,
neutral relations, hidden: true)`, `temporary = true`, `FactionManager.Add`; def `OutlanderRefugee` when Royalty is loaded, else
`OutlanderCivil`), named "S31 spike faction …". Hidden means no settlement is generated; its leader pawn is tagged
`TheNetwork_S31_FactionLeader`. **This does not settle S10.**

**Test pawns:** vanilla `Villager`, generated with `forceGenerateNewPawn: true` (never a redressed world pawn), no generated relations,
named `S31-Probe-<scenario>-<n>` (decoys `S31-Decoy-…`), quest tag `TheNetwork_S31_Probe` / `_Decoy`, spawned near the map centre.
Not a contractor projection; no generation policy is implied.

**AI:** vanilla only: `LordJob_DefendPoint` while holding, then `LordJob_ExitMapBest(Walk)`; the harness never issues a job and **never
calls `PassToWorld`**.

**M2 observability (read-only):** a runtime-only `ISignalReceiver` registered for the current game records every vanilla quest-target
signal sent for an S31 pawn (`TheNetwork_S31_Probe.LeftMap`, …) with a snapshot taken **synchronously** at that instant, and logs it.
It never reserves, unreserves or changes anything.

**Safety:** session arm (typed phrase `ARM S31`, runtime only, bound to the game object so it is false after every load, **spent by each
destructive action**); destructive actions refuse without it; the test map by default and only; every created object carries an S31
marker; the cleanup acts only on marked objects; a failed scenario is left as it is for inspection.

## 5. Scenarios and pass criteria

Every scenario prints a block `[TheNetwork][S31] ===== Scenario X … RESULT: PASS | FAIL | INCONCLUSIVE =====`. **INCONCLUSIVE is never
PASS.** Timings: hold 900 ticks, exit timeout 20,000, stored observation 900, a sample every 30 ticks (unpause; speed 3 is fine).

| | Scenario | Pass criteria (all required) |
|---|---|---|
| **A** | **Normal vanilla exit** (critical path): 1 probe, reserved while spawned, holds, then walks off the edge | while spawned and reserved: not suspended, situation `None`, keeps its Lord, biological age advances, needs change, it moves and has jobs, faction unchanged, ≥ 600 ticks observed · immediately after the pass (the synchronous `LeftMap` snapshot): same thing id, name, kind; in `WorldPawns`; still reserved; situation **`ReservedByQuest`** (never `Free`); faction unchanged at the pass; apparel unchanged; not dead/destroyed/discarded · stored ≥ 600 ticks: stays reserved and `ReservedByQuest`; faction only its own temporary one or **null** after vanilla removes that faction (§ 13.2), never another · no "already here" error |
| **B** | **Map removal** with the probe still on the map: `Game.DeinitAndRemoveMap` (the call `MapParent.CheckRemoveMapNow` makes) | as A, observed on the line after `DeinitAndRemoveMap` returns; **confirms no `LeftMap`** (an observed `LeftMap` is reported as an audit deviation); records that the temporary faction is not queued for removal (no `Notify_PawnLeftMap`). Afterwards re-create the test map |
| **C** | **Injured exit**: one vanilla `Bruise` (severity 3, no bleeding) on a leg before the exit | as A, plus the injury ages while reserved and spawned (health ticks) and survives the pass unchanged |
| **D** | **Save/load window** (owner-assisted): after A, write a checkpoint, save, quit to menu, reload | the arm is **false** when the reloaded game is first seen; exactly one S31 reservation quest, `Ongoing`, hidden, with its part; per checkpoint: exactly one pawn with that thing id and name, in `WorldPawns`, reserved, `ReservedByQuest`, same faction (or still null), same apparel |
| **E** | **Four probes leave together** | A's criteria for **each** probe independently; each is in the reservation part exactly once; no unmarked pawn in it; distinct thing ids |
| **F** | **Populated pool / redress pressure**: 1 reserved probe + 6 **unreserved** decoys of the same faction exit; then 9 `PawnGenerator.GeneratePawn` requests matching that faction with `minChanceToRedressWorldPawn = 1` | the probe is **never** returned and never appears in the `Free` set; **positive control:** at least one decoy is redressed (else INCONCLUSIVE); the GC's kept set keeps the probe (reason recorded) |
| **G** | **Rematerialize the same pawn** (from A's stored probe), then exit again | the **same object** returns (same thing id and name, one object with that id and that name: no twin), no generation call, `SpawnSetup` takes it out of `WorldPawns` (no second insertion), reserved and not suspended while spawned again, apparel unchanged, faction = the new episode's temporary faction; then A's criteria for the second exit |

**Deliberate limits** (recorded in the verdicts):

- F's faction is reserved with vanilla `QuestPart_ReserveFaction` so the faction-matched pressure can reach only S31 pawns; **F's faction data
  are not faction evidence** (A, B, C, E and G provide it).
- A **real GC pass is not forced** (`WorldPawnGC.RunGC` would discard unrelated world pawns of the save); the probe's GC verdict comes from
  `AccumulatePawnGCDataImmediate`, the accumulation the pass itself uses.
- The **"faction doesn't matter"** redress path is not driven with real requests (it would redress unrelated `Free` pawns); it draws from the
  `Free` set, which is checked for the probe after every request.
- The reservation fixture is the vanilla part (§ 4); the Network-owned part is S9r's.

## 6. Owner run checklist

1. Install the spike build (this branch's `1.6/Assemblies/TheNetwork.dll` and `1.6/Defs/Spikes/`). Use a **test save** (a fresh
   Dev Quicktest colony is fine). Check that the first `[TheNetwork]` line in `Player.log` names the spike's source commit.
2. Enable **Development mode**. Open the debug menu → **"The Network (PHYSICAL SPIKES — S31)"**.
3. **S31 — Arm physical spike…** → type `ARM S31`. *(Every action marked `[armed]` needs a fresh arm; one arm = one action.)*
4. Arm → **S31 — Create/ensure dedicated test map**. (Optional: open the world view to see "S31 test site (dev)".)
5. Arm → **S31 — Run M1 normal-exit probe (A)**. **Unpause** (speed 3). Wait for `Scenario A … RESULT:` in the log (≈ 1–2 in-game hours).
   Copy the whole `[TheNetwork][S31]` block.
6. Arm → **Run M1 injured-exit probe (C)**; wait for its result.
7. Arm → **Run M1 multi-pawn probe (E)**; wait.
8. Arm → **Run M1 populated-world/redress probe (F)**; wait.
9. Arm → **Prepare save/load checkpoint (D)**. Save the game, **quit to the main menu**, load that save.
10. Run **S31 — Show current spike state** (read-only): it must say **"not armed"**. Then run **S31 — Verify after load (D)**.
11. Arm → **Rematerialize same pawn (G)**; unpause; wait for its result.
12. Arm → **Run M1 map-removal probe (B)** *(it removes the test map)*; wait for its result.
13. Arm → **S31 — Cleanup**. Check the `CLEANUP` block: everything "removed", any "LEFTOVER" explained.
14. Attach `Player.log` (or every `[TheNetwork][S31]` line) and fill § 7.

**Reading the result:** each block says PASS, FAIL or INCONCLUSIVE, then lists every failure, gap and note. Any **FAIL** in A–G means
**M1 is not accepted** as tested; do not re-run with changes inside M1, record it and an M2 pass follows. **INCONCLUSIVE** means re-run
that scenario (for example longer, or after fixing a broken test map).

**Log lines to capture:** every line starting `[TheNetwork][S31]`, in particular the `===== … RESULT` blocks, the `vanilla signal
TheNetwork_S31_Probe.LeftMap` lines, the `exited:` and `after map removal:` lines, `temporary faction … was removed by vanilla`, the
`CLEANUP` block, and any red error mentioning `already here` or the S31 pawns.

## 7. Results (to fill after the owner's run)

| Field | Value |
|---|---|
| Date, RimWorld version, DLCs and mods loaded | |
| Spike build (source commit from the first `[TheNetwork]` line) | |
| Save used (test save? colony map untouched?) | |

| Scenario | Result (PASS / FAIL / INCONCLUSIVE) | Key observations (situation and reservation at the pass; faction transitions; anything unexpected) |
|---|---|---|
| A normal exit | NOT RUN | |
| B map removal | NOT RUN | |
| C injured exit | NOT RUN | |
| D save/load | NOT RUN | |
| E four together | NOT RUN | |
| F redress pressure | NOT RUN | |
| G same-pawn rematerialization | NOT RUN | |
| Cleanup leftovers | NOT RUN | |

**M1 decision rule.** M1 may be accepted only if **all** of these are green: reserved-while-spawned behaves normally (A, C) · no
unreserved `Free` window at a normal exit (A, C, E, G) · none at a map removal (B) · save/load keeps reservation and identity (D) ·
several pawns work (E) · redress pressure cannot select the pawn (F) · the same pawn rematerializes (G) · faction integrity holds for the
Phase 3.1 context (A, B, C, E, G: never a rewrite to another faction) · no duplicate `PassToWorld` and no "already here" error (all).
If any criterion fails, **M1 is not accepted**: record the failed criterion here and continue with an M2 pass; C-4 only after M1 and
M2 both fail, by ADR.

## 8. Uncertainties only the owner's run can settle

- Whether a reserved, spawned pawn's AI is fully normal in **this** mod list (other mods may read quest reservations).
- Whether `LordJob_ExitMapBest` reaches an edge on the generated test map within the timeout (a blocked map is INCONCLUSIVE, not FAIL).
- The real timing of the temporary faction's removal after the exit, and that no other system rewrites a null-faction **reserved** pawn.
- Whether the hidden quest shows anywhere in the UI, and whether the vanilla root behaves inertly for its whole life.
- Save/load of `QuestPart_ReservePawns` references to world pawns (by reference) and of the spike-only world object.
- **Removing the spike build:** run **S31 — Cleanup** first. A save that still holds the S31 test map cannot resolve the spike-only
  def without this build (vanilla drops such a world object on load).
