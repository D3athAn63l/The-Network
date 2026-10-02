# S20 — Abstract spatial routing, charter transport and Last Known Location placement

**Verdict: NOT RUN — OWNER RUNTIME VALIDATION REQUIRED**

The environment that built Phase 2.5 (and its correction pass) cannot launch RimWorld. Nothing below
is a runtime result. Compiling, reading the decompiled source and the headless tests (against a
synthetic grid graph) are **not** a runtime pass.

## Question

Phase 2.5 gives every NPC contractor hidden spatial truth on the real world map
([SPATIAL](../SPATIAL.md)). In a real RimWorld 1.6 world:

1. Is every contractor anchored on a valid, passable surface tile (around settlements, not on them),
   with nothing visible on the map?
2. Do journeys follow plausible routes (around seas and impassable terrain) at the contractor's own
   pace, never arriving without a route, and survive save/load without rerolling?
3. Do new procurement operations get a work region they can actually walk to in time, and still
   complete on their Phase 2 timeline, including delays?
4. Where the work is on disconnected land (an island), does a high-tech provider (Spacer or better:
   the Empire with Royalty, or a modded high-tech faction) give an abstract two-way charter, with no
   craft, no world object and no extra silver?
5. Do returns follow the Phase 2 lifecycle (a Disaster with survivors comes home; a recovered Troubled
   group is home; a dead Solo never moves)?
6. Does a Missing / Stranded / Catastrophic consequence put its Last Known Location near where the
   contractor actually was?
7. Does the Field Log show the player's job, and only that, without locations?
8. Is the game's speed unaffected at ~100 contractors?

**PASS**: all of the above, no red errors or warnings from `TheNetwork` (including `[Spatial]`), no
vanilla pathing errors or "ran out of tiles" warnings caused by the Network, no world object, pawn,
caravan, shuttle or map marker created for a contractor, no silver taken for transport, and no
contract stuck.

## Build

Code under test:
- `Integration/SpatialWorldAdapter.cs` (world grid, reachability, pathing, local search, the
  guaranteed last-resort search, settlements and their `canProvideCharterTransport` fact from the
  faction's `TechLevel`).
- `Integration/SiteAdapter.TryFindTileNear` (site placement near a tile).
- `Domain/Spatial/SpatialService.cs` (initialization, proof-before-arrival catch-up, ambient
  relocation, operation legs, charter planning and reconciliation, lifecycle reconciliation,
  incident tile, load reconciliation).
- `Domain/Contracts/FieldLog.cs`, the Contracts tab Field Log section.

Headless evidence (fake world graph, not vanilla behaviour): `Spatial.*`, `Migration.*`, `FieldLog.*`
and `Charter.*` tests, the 1,080-day soak on a charter world (two islands, providers coming and going:
zero teleports, zero contractors outpacing themselves, zero ended contractors moving, zero invalid
states, zero blocked journeys, Last Known Locations near the incident) and the archipelago stress
soak.

## Owner steps

Setup: Development mode on. Actions are under **"The Network (Phase 2.5)"** (and "The Network
(Phase 2)"); readouts go to the log. Run checkpoints by letting the game run to them where the step
says so: "Run next operation checkpoint now…" runs a checkpoint **early**, and spatial never hurries to
match an early checkpoint (the contractor keeps travelling; that is correct).

A. **Hidden spatial contractor.** Start or load a world with The Network. Run "Dump all spatial
   states". Expect every active contractor `Idle` (or `Travelling`) with a tile id on the surface
   layer, spread over the world, not clustered at your colony, and not on a settlement's own tile.
   Look at the world map: **no** contractor icon, marker, route or caravan.

B. **Travel (ordinary ground journey, check 1).** "Send contractor somewhere nearby now…" on one
   contractor. Note its tile ("Inspect contractor spatial state…"). Let a day or two pass (or "Catch up
   one contractor now…"). Expect the tile to change plausibly (a few tiles a day, around water, never
   across the planet in one step). No pawn, caravan or world object appears.

C. **Save and reload mid-journey.** Save while it travels; reload. Inspect again: same destination,
   same arrival tick; progress continues from the saved tile ("routes rebuilt" rises in "Spatial
   performance counters"). The destination never changes on its own.

D. **Procurement integration (check 8).** Post and accept a new procurement contract. "Inspect an
   operation's spatial plan…" shows `ground` (or `CHARTER`) with the work region; "Inspect contractor
   spatial state…" shows it `Travelling`, purpose `Outbound`, with the operation id. Let the game run:
   at the Arrive checkpoint it is at the work region; after Resolve it heads back; the contract
   completes (drop pods as in S13).

E. **Delay.** "Force 3-day delay on next resolution" (Phase 2 actions), then let the checkpoints run.
   Expect the contract Delayed, the return journey's arrival equal to (or, if it cannot walk that
   fast, later than) the delayed Return checkpoint, and the contract completing (not stuck).

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

### Correction-pass and charter checks

Charter needs a Spacer-or-better faction's settlement: with Royalty, the Empire; otherwise a modded
high-tech faction. In a Core-only world no vanilla faction qualifies: then steps 2–7 must show **no**
charter at all and soft degradation (still a pass for Core-only).

1. **Ordinary ground journey.** As B and D: a ground plan, walked at a plausible pace.
2. **Disconnected island.** Accept a contract, then "Send a running operation across water (charter
   test)…" on it (it moves the work region to land with no ground route from the contractor). Expect
   "Inspect an operation's spatial plan…" to show `CHARTER` with a hub (a high-tech faction's
   settlement) and a landing near the new work region, or, with no provider in reach, the leg
   `Blocked` where the contractor stood (never a jump onto the island).
3. **Provider detection.** "Dump all spatial states" / the plan readout: the hub is a settlement of a
   Spacer-or-better faction (check its faction in the world view); never a tribal, medieval or
   industrial one, never your colony.
4. **Charter save/reload.** Save while it walks to the hub (segment "on foot to the charter hub");
   reload. Same hub, same landing, same work region, same arrival; it then crosses and arrives.
5. **No physical things.** Throughout: no shuttle, pod, caravan, world object or map marker appears
   anywhere for the charter.
6. **No extra silver.** Note your silver before accepting; after the chartered contract completes,
   the only money moved is the contract's own terms (deposit, balance, refunds as in Phase 2).
7. **Field Log charter line.** The contract card shows one line "… has arranged charter transport
   for the part of the journey that cannot be made overland." (no hub, landing, island or route), and
   only once.
8. **Normal operation completes.** Let the chartered contract run: it resolves, they are picked up
   again (return segment via the same landing and hub), and the contract completes.
9. **Disaster, not Troubled.** "Force Disaster, not Troubled, on next resolution", let the contract
   resolve: the contractor (with survivors) heads back ("Inspect contractor spatial state…": purpose
   `Return`) and reaches home at the Return checkpoint; the plan still shows the incident.
10. **Troubled → recovered.** "Force Troubled (missing) on next resolution" (Phase 2), let it resolve,
    then "Force the next Troubled deadline: group found" and "Run next operation checkpoint now…" on
    it: the contractor's tile is its return point (home), not the incident. (Also try "…: written
    off": it stays at the incident.)
11. **Solo death.** With a Solo on a running contract, after Arrive use "Kill contractor (Solo) or its
    leader…" (Phase 2). Let the checkpoints run: the dead Solo's tile never changes again, it never
    starts home, and the contract ends as Phase 2 decides.
12. **No post-load teleport** (if practical). Save while a contractor travels on an operation; reload;
    "Invalidate an operation's charter hub…" (for a charter) or "Invalidate a contractor's
    destination…" (ground), then "Catch up one contractor now…": it replans from where it is or stays
    `Blocked` there; it never appears at the destination.

### Final-pass checks

13. **Delayed charter return** (final pass A). If practical, create a long-detour charter (a work region
    across a bay: "Send a running operation across water…" works when the far shore has no ground
    route; a real long detour is better). After the outbound crossing, "Force 3-day delay on next
    resolution" (or longer) and let it resolve: "Inspect contractor spatial state…" shows the return
    **via charter** (segment "on foot to the pickup" / crossing), the plan still `CHARTER (round trip
    in force; used on the way out)`, even though walking home would now fit.
14. **Provider lost after the outbound charter** (final pass B). After the outbound crossing, "Invalidate
    an operation's charter hub…": expect a replacement charter, or `ground now (charter used on the
    way out; return degraded to foot: …)` in "Inspect an operation's spatial plan…"; never a live
    `CHARTER` claim without a live crossing, and no teleport.
15. **Blocked arrival narration** (final pass C). Before the Arrive checkpoint, "Invalidate a
    contractor's destination…" (or send the operation somewhere unreachable), let the checkpoint
    run: the contract continues, and its Field Log does **not** say "has reached the area".

Also check: a save from Phase 2 (format 2) with a contract under way loads without errors, the
contractors get anchored ("Anchored N contractors" in the log), and the running contract completes
unchanged.

## Result

Not run.

**Related owner observation (not an S20 result).** After Phase 2.75 the owner reported a real-game run of a
*legacy* active procurement contract (a pre-update save; 10,000 Plasteel, about 200,000 silver, a team reached
through a Fixer) that continued after the update, went on payment hold when the balance (58,335 silver) exceeded the
available silver (about 56,000), resumed as the **same** contract when funds were available, and delivered
10,000 / 10,000 by vanilla drop pods with no error, duplicate or stuck state. That is recorded as an
**owner-observed runtime pass** of that scenario ([RUNTIME_TESTING § 15](../RUNTIME_TESTING.md#15-owner-observed-runtime-evidence),
suggested id RT-PROC-LEGACY-001). It did not exercise the checklist above (charters, the Field Log's beats, Last
Known Location placement), so **S20 stays NOT RUN**.

## Logs

—

## Consequence

Speed/range bands, the ambient cadence, the charter time and landing radius, and the Last Known
Location radius are `SpatialPolicy` constants. The charter provider threshold is one line in the
adapter (`TechLevel.Spacer`). If vanilla pathing or tile finding behaves unexpectedly, the fix is
confined to `SpatialWorldAdapter` / `SiteAdapter.TryFindTileNear`; the Domain only sees
`ISpatialWorld`.
