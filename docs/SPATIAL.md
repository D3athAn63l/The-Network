# Spatial continuity (Phase 2.5)

> Every contractor has a lightweight, hidden geographical continuity, but the player is not managing
> dots on a map. Spatial answers **WHERE**; operations still answer **WHAT HAPPENED**.
> Decisions: [ADR-041](DECISIONS.md) (philosophy), ADR-042 (movement and routing), ADR-043
> (operations and consequences), ADR-044 (the Field Log). Runtime check: spike
> [S20](spikes/S20-abstract-spatial-routing.md).

## Contents

1. [Purpose and boundaries](#1-purpose-and-boundaries)
2. [Spatial truth and ownership](#2-spatial-truth-and-ownership)
3. [Initialization](#3-initialization)
4. [The world graph (port and adapter)](#4-the-world-graph-port-and-adapter)
5. [Movement](#5-movement)
6. [Operation integration](#6-operation-integration)
7. [Consequence integration (Last Known Location)](#7-consequence-integration-last-known-location)
8. [The Field Log](#8-the-field-log)
9. [Persistence, migration and load reconciliation](#9-persistence-migration-and-load-reconciliation)
10. [Awareness and privacy](#10-awareness-and-privacy)
11. [Performance](#11-performance)
12. [Phase 3 handoff and future consumers](#12-phase-3-handoff-and-future-consumers)
13. [Diagnostics](#13-diagnostics)

---

## 1. Purpose and boundaries

The Network already knows *who* contractors are and *what* happens on their jobs. Phase 2.5 lets it
know approximately *where* they are, so later stories have geographical causes: a job took a while
because there was distance to cover; the recovery site is where the contractor actually disappeared;
a Phase 3 contractor can physically appear in a region because the abstract state already put them
there.

It is supporting infrastructure, never the point of the mod:

- no world-map icons, routes, arrows, presence areas or tracking;
- no persistent WorldObjects, caravans or pawns for contractors;
- no per-tick movement and no global scan of contractors per tick;
- no hidden contract economy: ambient movement creates no contract, payment, inventory, casualty,
  history record, letter, relationship change or social event;
- no social intersections (meetings, gossip, fights, rivalries), player-caravan encounters, relay
  stopovers or visits; no route-intersection corridors (see § 12);
- no cross-layer, orbital, gravship or shuttle travel.

## 2. Spatial truth and ownership

`ContractorSimulation` owns two separate concepts:

| | Answers | Kind |
|---|---|---|
| `MobilityProfile` | what can this contractor do? (range, speed, lift, modes) | capability, unchanged |
| `SpatialState` | where is it approximately, and where is it travelling? | world truth (new) |

`SpatialState` belongs to the contractor **actor**, the stable identity; never to a Known
Character, a Fixer, a faction, a world object, a pawn or a contract.

```
SpatialState {
  status:           Uninitialized | Idle | Travelling | OnAssignment | Blocked
  anchor:           TileRef      // the one tile that means "approximately around here"
  destination:      TileRef?     // committed; never rerolled
  journeyOrigin:    TileRef?     // where the current journey began
  purpose:          None | Ambient | Outbound | Return
  operation:        OperationId  // the operation the main body is on (none when idle or detached)
  journeyStartTick, lastUpdateTick, arrivalTick   // the timing catch-up needs
  nextAmbientTick:  int          // when an idle contractor next considers moving (committed)
  journeys:         int          // seeds the next ambient choice
  initializedTick, blockedReason
}
```

**Presence semantics.** A contractor has one anchor tile. It means *around here*, not a precise
position. A local presence area, when a later system needs one, is derived from world-graph
distance (about `PresenceRadius` = 2 tiles); it is never persisted. Nothing persists presence tile
sets, route corridors, nearby-contractor lists, occupancy maps, pathfinder objects, `WorldPath`s,
player awareness or UI descriptors.

**Status.** `Blocked` is the explicit degraded state: a journey could not be represented, so the
contractor stays at its last valid anchor (with `blockedReason`). It is never a contract state and
never stops an operation.

## 3. Initialization

Every active NPC contractor eventually gets one valid anchor. Initialization is deterministic (the
contractor's seed and the world only), happens when world data exists, is committed once, and never
depends on the camera, the UI or ModSettings. Evidence, in order:

1. a settlement of its **live origin faction**: a passable tile 1–3 tiles *around* it (never on it:
   no home base, no ownership);
2. otherwise another non-player settlement, the same way;
3. otherwise any passable tile of the main surface.

The player's colonies are never used. New world-generated contractors are anchored when created.
A contractor created before world data exists stays `Uninitialized` and is anchored at the next
start-up, validation or upkeep. The first ambient decision is 5–25 days away.

## 4. The world graph (port and adapter)

Domain code sees the world only through `ISpatialWorld` (`Domain/Ports/Ports.cs`): tile validity and
passability, settlements, a bounded local search (`TryFindPassableNear`), a last-resort tile, same-layer
routes (`TryRoute`, returning the step tile ids or a reason key: `InvalidTile`, `CrossLayer`,
`Unreachable`, `TooFar`, `NoPath`) and an approximate distance.

`Integration/SpatialWorldAdapter` implements it over the RimWorld world grid:

- routes: `WorldReachability.CanReach` first (so vanilla never logs for an impossible journey), then
  the layer's `WorldPathing.FindPath` with no caravan; the path is copied and returned to the pool;
- local search: `TileFinder.TryFindPassableTileWithTraversalDistance` under `Rand.PushState(seed)`;
- validity: `TileRef.IsValidNow` (layer id **and** layer def), impassability from `World.Impassable`;
- settlements: `Find.WorldObjects.Settlements`, non-space layers, cached for an in-game hour.

It is read-only: it creates no world object, caravan, pawn or lasting path. Headless tests and the
soak harness use `Diagnostics/GridWorldGraph`, a synthetic grid with a sea band and settlements.

A Last Known Location near a tile uses a separate site-port method, `ISiteAdapter.TryFindTileNear`
(`TileFinder.TryFindNewSiteTile` with a near tile, never a space layer).

## 5. Movement

### 5.1 Cadence: lazy catch-up

There is no movement system. Position is **caught up** from committed timing only when something
needs it:

- the contractor's existing staggered daily `contractor.upkeep` job (no new job kind, no extra job
  per contractor);
- operation checkpoints that read position (Prep, Arrive, Resolve, Return) and operation end;
- consequences, dev actions and load reconciliation.

A catch-up of an idle contractor costs a few comparisons. A travelling contractor advances along its
route in proportion to the time elapsed since the last update toward the committed arrival:
`steps = remaining × (now − lastUpdate) / (arrival − lastUpdate)`, with the fractional remainder kept
(`lastUpdateTick` is set to the moment the last whole step was reached). At or after the arrival tick
it is at the destination. Daily updates may advance several tiles; that is intended. It never moves
backward and never skips geography: every intermediate anchor is a tile of the route.

### 5.2 Routing and the route cache

A route is computed only when a journey needs one: when it starts, when the destination changes, or
when the runtime cache has none (after a load, or when dropped). The cache (actor → steps from a
start tile to the destination, and how many are consumed) is runtime-only and disposable. A missing
or stale cache is rebuilt **from the persisted anchor** to the persisted destination.

The exact path is **softer truth** than a committed operation outcome: if the world's topology or the
installed mods change, a rebuilt route may differ. That is acceptable as long as the persisted anchor
is respected, the destination is respected while valid, nothing teleports, and no contract or outcome
is rerolled.

Speed and range come from `MobilityProfile` bands: tiles per day VeryLow 5, Low 7, Medium 9, High 12,
VeryHigh 16; range VeryLow 6, Low 10, Medium 16, High 26, VeryHigh 40 tiles. Routes longer than
`MaxRouteSteps` (300) are not represented.

### 5.3 Ambient relocation

An **idle** contractor (no destination, no operation, no commitments, not recovering or ended) may,
when its committed `nextAmbientTick` comes, relocate its operating area: with a 55 % chance it picks a
destination within about 60 % of its range band (a non-player settlement area, weighted ×3 toward its
origin faction, else a passable tile), otherwise it stays and decides again in 10–30 days. After an
ambient arrival it rests 10–40 days; after an operation, 3–10. The choice is seeded by the contractor,
its journey count and the day, and committed once made. Ambient relocation publishes nothing.

### 5.4 Failure and invalid tiles

Spatial failure never corrupts or bricks a contract:

- a destination that no longer resolves (mod removal, a layer change) or lies on another layer: the
  journey is dropped, the contractor stays at its last valid anchor (`Blocked`, reason recorded);
- no route: the same (an ambient journey is simply not started or dropped);
- an anchor that no longer resolves: the contractor is anchored again deterministically (no
  contractor is deleted);
- for an operation, the operation's timeline continues unchanged and the plan records
  `fallbackKey`; a diagnostic is logged;
- a fault (an exception from the adapter or the spatial layer itself): every entry point that Phase 2
  code, start-up or the load validator calls (`EnsureInitialized`, `InitializeAll`, `Upkeep`, the
  operation hooks, `IncidentTile`, `Validate`) catches it, logs it once, counts it (`faults`) and
  returns. The calling upkeep, checkpoint, consequence or load carries on without spatial (a Last
  Known Location falls back to the Phase 2 placement); whatever was left half-updated is repaired by
  the load validator. Spatial logic can therefore never stall a Phase 2 contract.

## 6. Operation integration

New procurement operations (created after Phase 2.5) get an `OperationSpatialPlan`:

```
OperationSpatialPlan { origin, workRegion, returnTo, incident: TileRef; detached: bool; fallbackKey }
```

The existing checkpoints stay the only timeline; spatial conforms to them:

| Moment | Spatial |
|---|---|
| Award / operation start | catch up; the contractor's **real anchor** becomes `origin`; a hidden work region is committed from the operation's seed; the main body is bound to the operation |
| Preparing (start → Prep) | at the origin; the journey is committed but has not set out |
| Transit (Prep → Arrive) | travelling toward the work region; departure at Prep, arrival at Arrive |
| Arrive | at the work region (snapped if the timeline says they are there) |
| Engaged / Resolve | the resolver decides WHAT; spatial records nothing but WHERE |
| after Resolve | trouble or disaster: `incident` = where they are, and they stay; otherwise they head for `returnTo` (the origin), arriving when the **Return checkpoint** is due |
| Return | back where they were heading |
| Delivering / Done | Phase 2 drop-pod delivery is unchanged (nobody walks into the colony); on finish or abort the contractor is released where it is and becomes idle |

**Work region.** An approximate area where the abstract work happens, not a shop, stash, site or
world object. Its distance is scaled by the travel time the committed ETA leaves
(`(Arrive − Prep) / ticksPerTile × (0.5 + 0.5 × sourcing difficulty)`), capped by the range band;
70 % of the time it is next to a non-player settlement in that band, otherwise any reachable
passable tile. The quote, ETA and economics are **not** changed by spatial in this phase.

**Delays.** Phase 2's delay moves the Return checkpoint; the return journey's arrival is that
checkpoint. There is no separate spatial delay and no reroll.

**Abort.** The journey stops where it is (never snapped to the work region or back to the origin).

**Concurrent jobs.** An organization's second or third concurrent job is a *detachment*: it gets a
plan (origin, work region) but the main body's anchor does not move for it; its incident is its work
region.

**Troubled.** Missing, stranded or captured groups stay at the incident; the contractor's last truth
remains there (never reset to an arbitrary idle place). A contractor that dies or dissolves keeps its
last anchor.

## 7. Consequence integration (Last Known Location)

When the Last Known Location rule fires, it commits `near` on the pending follow-up: the operation's
recorded incident, else where the bound contractor is now, else its work region. The follow-up job
then places the site with `TryFindTileNear(near, 0, IncidentSiteRadius = 4, seed)`; when that finds
nothing, the Phase 2 placement is used. Cargo truth (committed secured payload, bounded counts) is
unchanged. Reloading between firing and generation never moves it.

The generated site is the information reveal: *hidden truth → consequence → player-visible clue*.
It says "this is where the Network thinks something happened"; it does not reveal the contractor's
anchor or anything else about its movements.

## 8. The Field Log

A temporary activity journal for work a contractor is doing **for the player** (ADR-044).

- **Scope.** Owned by the player-issued contract (`Contract.fieldLog`), never by the contractor.
  It starts when the player accepts a quote, is visible only while that contract runs, and is
  cleared when it closes. A later contract with the same contractor starts a new log. Contracts of
  other issuers never have one (relevant from Phase 4).
- **Content.** Meaningful beats only, in reporting language: accepted or taken over, set out (or
  working nearby), reached the area, worse than expected and the answer, running late, missing /
  stranded / captured, turned up, secured all or part (with the quantities), the partial-result
  choice, delivery retried or held, payment due, handover. Never a tile, a route, a distance, a
  hidden number, a daily "nothing happened" or off-contract travel. A result is reported only when
  the contractor reports it (ADR-037).
- **Storage.** `FieldLogEntry { tick, key, args }`: a translation key and the snapshotted words,
  fixed when written (no rewording on UI open), at most 24 per contract, written in the same step as
  the state change (no duplicate after reload; a line equal to the previous one is not repeated).
- **UI.** A compact "Field log" section (latest six lines) on the contract's card in the Contracts
  tab. No new tab, no letters: a tracking page, not a notification stream.
- **Not History.** History keeps durable meaningful events; the Field Log is not copied into it.

## 9. Persistence, migration and load reconciliation

Persisted: each contractor's `SpatialState`, each new operation's `OperationSpatialPlan`, the pending
follow-up's `near`, and the live Field Log entries. Not persisted: routes, caches, presence areas.

Save format **3** (`V2ToV3SpatialContinuity`, a logged no-op):

- a Phase 2 contractor has no spatial node: it loads `Uninitialized` and is anchored at start-up
  (deterministically, as a new world would); no past journey is invented;
- an operation already running keeps its Phase 2 lifecycle: no plan, no binding, untouched quote,
  ETA, frozen inputs, outcome, cargo, casualties, money and checkpoints; only operations started from
  now on are spatially coupled;
- Field Logs start with the next contract accepted.

On every load the validator: anchors Uninitialized contractors, anchors again those whose anchor no
longer resolves, drops invalid or cross-layer destinations where they stand, and releases a binding
to an operation that has ended. Ended and quarantined contractors are left alone.

Removing The Network leaves nothing spatial behind: no contractor world objects, markers, routes or
icons ever existed.

## 10. Awareness and privacy

**World truth ≠ player knowledge.** Normal UI never shows a contractor's tile, destination, route,
movement state, presence or neighbours: the read models carry no location field (a test checks),
and the Field Log speaks in reports. Dev diagnostics may show exact tile ids. Future Intel, rumors or
visits may reveal approximate information through their own rules; nothing does in Phase 2.5.

## 11. Performance

Idle per tick: nothing beyond the scheduler's due-job comparison. Daily: the ~100 existing upkeep
calls each add a tiny catch-up. Routing happens only when a journey starts, a cache is rebuilt, or a
destination changes; never for stationary contractors. The soak (1,080 simulated days, ~100
contractors, 2,160 procurement contracts, ambient relocation, simulated loads dropping every route
cache) measured about 5,200 routes built, ~2,800 ambient journeys and ~1,900 operation plans, zero
blocked journeys, zero spatial faults, zero teleports, zero invalid states, Field Logs only on live
player contracts, and about 108 KB of added save data after 18 in-game years
([PERFORMANCE](PERFORMANCE.md)).

## 12. Phase 3 handoff and future consumers

Phase 3 (abstract ↔ physical) will materialize contractors *around* their spatial truth, let them
act physically, reconcile the pawns, and write back where they actually ended. Spatial state does
**not** mean a pawn exists: travelling, arriving or working never generates one.

Future invariants recorded now:

- **No random teleport visits.** Once spatial continuity exists, ambient contractor visits (later
  phases) should respect it: a contractor known by world truth to be far away does not appear
  tomorrow without a mobility or cause explanation.
- **Relay stopovers** (a working Comms Console as Network infrastructure) and **intersections**
  (contractor–contractor or with player caravans) may consume spatial truth later. A daily travel
  corridor is deliberately deferred: no consumer needs it yet, and at ~100 contractors proximity is
  cheap to compute when one does.

## 13. Diagnostics

Dev actions under "The Network (Phase 2.5)" ([DEBUGGING](DEBUGGING.md)): inspect or dump spatial
states (exact tiles), initialize, send somewhere, force ambient relocation, catch up, invalidate a
destination, drop route caches, move a work region, force Missing at the contractor's position,
create a Last Known Location near an operation's contractor, inspect a Field Log, performance
counters. Logs go to the `[TheNetwork][Spatial]` category.
