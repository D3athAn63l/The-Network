# S20 — Abstract spatial routing and Last Known Location placement

**Verdict: NOT RUN — OWNER RUNTIME VALIDATION REQUIRED**

The environment that built Phase 2.5 cannot launch RimWorld. Nothing below is a runtime result.
Compiling, reading the decompiled source and the headless tests (against a synthetic grid graph)
are **not** a runtime pass.

## Question

Phase 2.5 gives every NPC contractor hidden spatial truth on the real world map
([SPATIAL](../SPATIAL.md)). In a real RimWorld 1.6 world:

1. Is every contractor anchored on a valid, passable surface tile, with nothing visible on the map?
2. Do journeys follow plausible routes (around seas and impassable terrain) without teleporting, and
   survive save/load without rerolling?
3. Do new procurement operations get a work region and still complete on their Phase 2 timeline,
   including delays?
4. Does a Missing / Stranded / Catastrophic consequence put its Last Known Location near where the
   contractor actually was?
5. Does the Field Log show the player's job, and only that, without locations?
6. Is the game's speed unaffected at ~100 contractors?

**PASS**: all of the above, no red errors or warnings from `TheNetwork` (including `[Spatial]`), no
vanilla pathing errors or "ran out of tiles" warnings caused by the Network, no world object, pawn
or caravan created for a contractor, and no contract stuck.

## Build

Code under test:
- `Integration/SpatialWorldAdapter.cs` (world grid, reachability, pathing, local search, settlements).
- `Integration/SiteAdapter.TryFindTileNear` (site placement near a tile).
- `Domain/Spatial/SpatialService.cs` (initialization, catch-up, ambient relocation, operation legs,
  incident tile, load reconciliation).
- `Domain/Contracts/FieldLog.cs`, the Contracts tab Field Log section.

Headless evidence (fake world graph, not vanilla behaviour): `Spatial.*`, `Migration.*` and
`FieldLog.*` tests, and the soak (1,080 days, ~100 contractors: zero teleports, zero invalid states,
zero blocked journeys, Last Known Locations placed near the incident).

## Owner steps

Setup: Development mode on. Actions are under **"The Network (Phase 2.5)"**; readouts go to the log.

A. **Hidden spatial contractor.** Start or load a world with The Network. Run "Dump all spatial
   states". Expect every active contractor `Idle` (or `Travelling`) with a tile id on the surface
   layer, spread over the world and not clustered at your colony. Look at the world map: **no**
   contractor icon, marker, route or caravan.

B. **Travel.** "Send contractor somewhere nearby now…" on one contractor. Note its tile ("Inspect
   contractor spatial state…"). Let a day or two pass (or "Catch up one contractor now…"). Expect
   the tile to change plausibly (a few tiles a day, around water, never across the planet in one
   step). No pawn, caravan or world object appears.

C. **Save and reload mid-journey.** Save while it travels; reload. Inspect again: same destination,
   same arrival tick; progress continues from the saved tile ("routes rebuilt" rises in
   "Spatial performance counters"). The destination never changes on its own.

D. **Procurement integration.** Post and accept a new procurement contract. "Dump contract…" shows
   the operation; "Inspect contractor spatial state…" shows it `Travelling` with purpose `Outbound`
   and the operation id. Advance with "Run next operation checkpoint now…": at Arrive it is at the
   work region, after Resolve it heads back, the contract completes (drop pods as in S13).

E. **Delay.** "Force 3-day delay on next resolution" (Phase 2 actions), then run the checkpoints. Expect the contract Delayed, the return journey's arrival equal to the (later) Return
   checkpoint, and the contract completing (not stuck).

F. **Last Known Location.** "Force Missing at the contractor's position on next resolution", run the
   checkpoints of an accepted contract to Resolve. A Last Known Location site appears within a few
   tiles of the contractor's tile at that moment (compare with "Inspect contractor spatial state…"),
   not near your colony by default. Also try "Create a Last Known Location near an operation's
   contractor…": the log reports the distance.

G. **Field Log.** On an accepted contract, open the Contracts tab: a "Field log" section lists short
   dated reports (accepted, set out, reached the area, …). No tile, coordinate or route text. A
   contract still taking quotes shows none. When the contract ends, the section is gone. Hire the
   same contractor again: a fresh log.

H. **Performance.** Play normally for a few in-game days at max speed with ~100 contractors; TPS is
   unchanged compared with the same save before Phase 2.5 (dev "Spatial performance counters" shows
   catch-ups and routes growing slowly).

Also check: a save from Phase 2 (format 2) with a contract under way loads without errors, the
contractors get anchored ("Anchored N contractors" in the log), and the running contract completes
unchanged.

## Result

Not run.

## Logs

—

## Consequence

Speed/range bands, the ambient cadence and the Last Known Location radius are `SpatialPolicy`
constants. If vanilla pathing or tile finding behaves unexpectedly, the fix is confined to
`SpatialWorldAdapter` / `SiteAdapter.TryFindTileNear`; the Domain only sees `ISpatialWorld`.
