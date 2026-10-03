# Spatial continuity (Phase 2.5)

> Every contractor has a lightweight, hidden geographical continuity, but the player is not managing
> dots on a map. Spatial answers **WHERE**; operations still answer **WHAT HAPPENED**.
> Decisions: [ADR-041](DECISIONS.md) (philosophy), ADR-042 (movement and routing), ADR-043
> (operations and consequences), ADR-044 (the Field Log), ADR-045 (abstract charter transport).
> Runtime check: spike [S20](spikes/S20-abstract-spatial-routing.md).

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
- no cross-layer, orbital, gravship or planet-to-planet travel, and no physical vehicle of any kind:
  the only non-ground mode is the **abstract same-layer charter** of § 6.1, for operation travel only.

## 2. Spatial truth and ownership

`ContractorSimulation` owns two separate concepts:

| | Answers | Kind |
|---|---|---|
| `MobilityProfile` | what can this contractor do? (range, speed, lift, modes) | capability, unchanged |
| `SpatialState` | where is it approximately, and where is it travelling? | world truth (new) |

`SpatialState` belongs to the contractor **actor**, the stable identity; never to a Known
Character, a Fixer, a faction, a world object, a pawn or a contract.

`Idle` is the technical state, not a statement that nothing is happening: in the fiction an available
contractor is maintaining contacts, looking for work, relocating occasionally and preparing for
commissions. There are no hidden NPC contracts or fake jobs behind it (NPC-issued work is Phase 4).

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
  bridgeFrom, bridgeTo: TileRef? // a chartered leg: the two committed ends of the crossing (§ 6.1)
  bridged:          bool         // the crossing of the current leg is done
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
   no home base, no ownership); a settlement with no free tile around it is passed over for the next
   of a short list;
2. otherwise another non-player settlement, the same way;
3. otherwise any passable tile of the main surface: a few seeded probes, then a guaranteed
   deterministic scan of every tile from a seeded offset (`SpatialSearch.FirstPassable`), so a
   contractor is never left `Uninitialized` just because the probes missed sparse land. This last
   fallback is an abstract area anchor only; if it happens to fall next to (or, on a tiny world, on) a
   settlement, that implies no ownership or home base.

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
- settlements: `Find.WorldObjects.Settlements`, non-space layers, cached for an in-game hour, each with
  one generic fact for charter transport: `canProvideCharterTransport`, derived from the settlement's
  faction's real `TechLevel` (Spacer or better, never the player). The Domain never names a faction
  or a tech level;
- last-resort tile: the guaranteed search of § 3 over the surface layer.

It is read-only: it creates no world object, caravan, pawn or lasting path. Headless tests and the
soak harness use `Diagnostics/GridWorldGraph`, a synthetic grid with a sea band and settlements (and,
for the charter world, sealed islands and high-tech providers).

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
(`lastUpdateTick` is set to the moment the last whole step was reached). Daily updates may advance
several tiles; that is intended. It never moves backward and never skips geography: every
intermediate anchor is a tile of the route.

Three rules keep it honest:

- **Proof before arrival.** The remaining journey is proven before any progress, the final arrival
  included: the route cached this session, or one rebuilt from the persisted anchor. A destination
  that is still a valid tile is no evidence it can still be reached (a cache lost at load, a world
  that changed). Without a route there is no arrival: an operation leg is first planned again from
  current truth (on foot, else by charter, § 6.1); otherwise the contractor stays at its last valid
  anchor, `Blocked`. It is never snapped to the destination because the clock passed `arrivalTick`.
- **Never faster than it can walk.** A committed arrival is never sooner than walking the route takes
  at the contractor's speed band; if a rebuilt route is longer (a changed world), the journey becomes
  later (`lateArrivals`), never quicker. Spatial may lag behind a squeezed operation timeline; it
  never catches up by moving faster.
- **An ended contractor never moves.** A contractor that died or dissolved is caught up to the moment
  it ended, then frozen there (§ 6).

### 5.2 Routing and the route cache

A route is computed only when a journey needs one: when it starts, when the destination changes, or
when the runtime cache has none (after a load, or when dropped). The cache (actor → steps from a
start tile to the destination, and how many are consumed) is runtime-only and disposable. A missing
or stale cache is rebuilt **from the persisted anchor** to the persisted destination (for a chartered
leg not yet crossed: anchor → hub, the crossing, landing → destination; a pending crossing also
re-checks that its ends and its provider are still there).

**Approximate distance only discovers.** `ApproxDistance` is used to find candidates cheaply; a
destination is committed only when its **real route, in steps,** fits: for an operation, what the
contractor can walk in the travel window and within its range band; for ambient movement, the ambient
range. A short hop across a bay can be a fifty-step detour on land.

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
destination within about 60 % of its range band (around a non-player settlement, never on it, weighted
×3 toward its origin faction; else a passable tile in that ring), otherwise it stays and decides again
in 10–30 days. The destination is committed only if its real route is no longer than that ambient
range in steps. Ambient movement is **ground-only**: it never charters (§ 6.1). After an ambient
arrival it rests 10–40 days; after an operation, 3–10. The choice is seeded by the contractor, its
journey count and the day, and committed once made. Ambient relocation publishes nothing.

### 5.4 Failure and invalid tiles

Spatial failure never corrupts or bricks a contract:

- a destination that no longer resolves (mod removal, a layer change) or lies on another layer: the
  journey is dropped, the contractor stays at its last valid anchor (`Blocked`, reason recorded);
- no route: the same (an ambient journey is simply not started or dropped), including a route that
  disappears after the journey was committed, even when its arrival tick has already passed;
- an operation leg that can no longer be proven (its route, a charter end or its provider gone) is
  first planned again from the contractor's current truth, on foot or by charter; only if neither
  works is it `Blocked`;
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
OperationSpatialPlan { origin, workRegion, returnTo, incident: TileRef; detached: bool; fallbackKey;
                       hub, landing: TileRef?   // a committed two-way charter (§ 6.1), else null }
```

The existing checkpoints stay the only timeline; spatial conforms to them:

| Moment | Spatial |
|---|---|
| Award / operation start | catch up; the contractor's **real anchor** becomes `origin`; a hidden work region is committed from the operation's seed; the main body is bound to the operation |
| Preparing (start → Prep) | at the origin; the journey is committed but has not set out |
| Transit (Prep → Arrive) | travelling toward the work region (on foot, or on foot + charter + on foot); departure at Prep, arrival at Arrive |
| Arrive | normally at the work region (the leg was committed to arrive now); never snapped there: a longer route or a checkpoint run early from the dev menu leaves them still travelling. The Field Log's "reached the area" is told only if they really are there (a late group is told when it gets there; a Blocked one never) |
| Engaged / Resolve | the resolver decides WHAT; spatial records nothing but WHERE |
| after Resolve | a Troubled or Disaster outcome records `incident` = where they are; **only a Troubled outcome keeps them out**. Everyone else, a Disaster with survivors included, heads for `returnTo` (the origin), arriving when the **Return checkpoint** is due or later if they cannot walk that fast (never sooner) |
| Return | caught up on the way back (a late group keeps walking; never snapped home) |
| Troubled deadline | Phase 2 finds the group: spatial is **reconciled** to `returnTo` (Phase 2 declared the return done; no new journey). Phase 2 writes it off: the last truth stays at the incident |
| Delivering / Done | Phase 2 drop-pod delivery is unchanged (nobody walks into the colony); on finish or abort the contractor is released where it is and becomes idle (a group still walking home keeps going) |

**Work region.** An approximate area where the abstract work happens, not a shop, stash, site or
world object. Its intended distance is scaled by the travel time the committed ETA leaves (the
tighter of Prep → Arrive and the planned Resolve → Return, divided by ticks per tile, × (0.5 + 0.5 ×
sourcing difficulty)), capped by the range band. 70 % of the time it is around a non-player
settlement in that band (a short list of up to three, found by approximate distance), otherwise a
passable tile in the ring. Each candidate is weighed in turn: **on foot** if its real route fits what
the contractor can walk in the window and its range; else **by charter** for that same candidate
(§ 6.1); else the next candidate. Nothing fits: the work happens where they are (`NoWorkRegion`). The
quote, ETA and economics are **not** changed by spatial in this phase.

**Delays.** Phase 2's delay moves the Return checkpoint; the return journey's arrival is that
checkpoint. There is no separate spatial delay and no reroll.

**Abort.** The journey stops where it is (never snapped to the work region or back to the origin).

**Concurrent jobs.** An organization's second or third concurrent job is a *detachment*: it gets a
plan (origin, work region) but the main body's anchor does not move for it; its incident is its work
region.

**Troubled.** Missing, stranded or captured groups stay at the incident; the contractor's last truth
remains there (never reset to an arbitrary idle place) until Phase 2's Troubled deadline decides:
found (reconciled to `returnTo`) or written off (it stays at the incident).

**Ended contractors.** A contractor that dies or dissolves (a Solo killed during Resolve included) is
caught up to that moment and frozen: last anchor kept, no destination, no purpose, no route. Only an
active contractor is ever moved for an operation, so no later checkpoint, return or upkeep moves it;
the operation itself still ends however Phase 2 decides.

### 6.1 Abstract charter transport (ADR-045)

A contractor may need to reach a same-layer region with no ground route (an island, a sealed-off
landmass) or with only an impractically long detour. In a high-tech world it can travel to a
high-tech settlement, charter a reusable transport craft, cross, work, and be picked up again. The
charter is **abstract**: a causal explanation for disconnected geography, not a vehicle.

- **When.** Only for **operation** legs, only after walking was tried: a work-region candidate out of
  reach on foot in the travel window, the return of a chartered plan when walking home does not fit
  its window, and the reconciliation of a leg that can no longer be proven. Never for ambient
  movement. Never for an invalid, impassable or cross-layer destination (no charter ever "fixes" a
  bad tile or another layer).
- **Provider.** A settlement whose faction `canProvideCharterTransport` (Spacer technology or better,
  from the game's own `TechLevel`; vanilla's Empire, modded high-tech factions), on the same layer,
  reachable **on foot** from the contractor's side within its range and the window. The committed hub
  is preferred; otherwise only a short list of the nearest providers (by approximate distance) is
  proven with real routes. No provider in reach: no charter is invented; the plan degrades softly.
- **Landing and pickup.** A valid, passable, same-layer tile within `LandingRadius` (2) of the work
  region, reachable on foot to it (no settlement needed there: a reusable craft sets down in the
  field). The **same** landing is the pickup for the way back, and the same hub the drop-off: one
  two-way charter, never a one-way pod.
- **Journey.** Out: walk to the hub → cross → walk from the landing to the work region. Back: walk to
  the pickup → cross → walk from the hub home. **A committed round trip returns by charter**: once
  the charter has carried them out, the way back uses the same pickup and hub even if a delay later
  leaves enough time to walk (the extra time does not cancel the booking). `SpatialState` saves only the two ends of the current
  leg's crossing (`bridgeFrom`, `bridgeTo`) and whether it is done (`bridged`); the crossing is one
  step between them, so a route rebuilt after a load is exactly the journey that was left. No craft,
  flight path, fuel, passengers or air tiles are stored.
- **Timing.** The existing checkpoints stay the timeline: walking plus the crossing
  (`CharterTicks` = a quarter day to arrange and fly) must fit the travel window when the plan is
  made. There is no second scheduler, no pickup deadline and no new outcome: a promised pickup window
  is a deferred story hook, not a mechanic.
- **Reconciliation.** A provider or charter end that disappears (a destroyed settlement, a removed
  mod) before the outbound crossing: the leg is planned again from where they are (on foot if a route
  now exists, else another provider), else `Blocked`; never a teleport, and the operation goes on. For
  the return of a charter already used: the committed pickup first, then a replacement charter from
  where they are, and only when no charter can exist any more a return on foot.
- **History versus the live leg.** The live leg is always `SpatialState.bridgeFrom`/`bridgeTo`. The
  plan keeps what was committed and what happened: `hub` and `landing`; `charterUsed` (the outbound
  crossing happened, never cleared); `charterLost` (why a used charter can no longer carry the return:
  hub and landing kept as history, the return on foot). `Charter` means "a committed round trip is in
  force". A charter that was **never used** (the outbound leg went on foot before the crossing, or they
  never got across) is **dropped**: hub and landing cleared, `fallbackKey` `CharterDropped` or
  `CharterUnused`. So the plan never claims a charter the live journey is not using or cannot use.
- **Money.** None. The charter is part of the contractor's operational expenses, already in its
  quote: no invoice, fee, deposit, insurance, transport contract or vendor (a source scan and a test
  check it). Quotes may account for difficult geography in later tuning, not now.
- **Player view.** One Field Log beat on the player's own contract ("… has arranged charter
  transport for the part of the journey that cannot be made overland"), told once; no hub, landing,
  island, route or provider details, no letter.
- **Future (not implemented).** The committed hub, landing and pickup make later stories possible
  (a crew withdrawing toward extraction as the pickup approaches, cargo abandoned, people who do not
  make it back). They need the resolver to consume a pickup window; nothing like it exists yet.

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
  choice, delivery retried or held, payment due, handover; one "arranged charter transport" beat when a
  chartered crossing is part of the job; a Solo is "captured", an organization's "people have been
  taken". "Reached the area" is told only when the group really is at its work region (on time at the
  Arrive checkpoint, or late when it gets there; never while it is still travelling or Blocked
  elsewhere; Phase 2 operations without a plan and detachments are told by the checkpoint as before).
  Never a tile, a route, a distance, a
  hidden number, a daily "nothing happened" or off-contract travel. A result is reported only when
  the contractor reports it (ADR-037).
- **Storage.** `FieldLogEntry { tick, key, args }`: a translation key and the snapshotted words,
  fixed when written (no rewording on UI open), at most 24 per contract, written in the same step as
  the state change (no duplicate after reload; a line equal to the previous one is not repeated).
- **UI.** A compact "Field log" section (latest six lines) on the contract's card in the Contracts
  tab. No new tab, no letters: a tracking page, not a notification stream.
- **Not History.** History keeps durable meaningful events; the Field Log is not copied into it.

## 9. Persistence, migration and load reconciliation

Persisted: each contractor's `SpatialState` (with the ends of a chartered leg), each new operation's
`OperationSpatialPlan` (with a committed charter's hub and landing), the pending follow-up's `near`,
and the live Field Log entries. Not persisted: routes, caches, presence areas, anything about a craft.
The charter fields are additive with null/false defaults inside save format 3.

Save format **3** (`V2ToV3SpatialContinuity`, a logged no-op):

- a Phase 2 contractor has no spatial node: it loads `Uninitialized` and is anchored at start-up
  (deterministically, as a new world would); no past journey is invented;
- an operation already running keeps its Phase 2 lifecycle: no plan, no binding, untouched quote,
  ETA, frozen inputs, outcome, cargo, casualties, money and checkpoints; only operations started from
  now on are spatially coupled;
- Field Logs start with the next contract accepted.

On every load the validator: anchors Uninitialized contractors, anchors again those whose anchor no
longer resolves, drops invalid or cross-layer destinations where they stand, and releases a binding
to an operation that has ended. A charter whose ends or provider no longer resolve is reconciled at the
next catch-up (§ 6.1). Ended and quarantined contractors are left alone.

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
destination changes; never for stationary contractors. Charter planning happens only when an
operation plan is made, a committed leg needs reconciling, or a dev action asks, and proves at most a
short list of providers with real routes (never an all-settlement search, never daily). The soak
(1,080 simulated days, ~100 contractors, a charter world with two sealed islands and provider
settlements that come and go, ambient relocation, simulated loads dropping every route cache)
measured about 5,400 routes built, ~2,900 ambient journeys, ~1,950 operation plans of which 95 by
charter (78 chartered legs, 78 crossings), zero blocked journeys, zero spatial faults, and zero
correctness violations (teleports, walking faster than a contractor's own pace, ended contractors
moving, recovered groups out of place, ambient charters, a live leg disagreeing with its plan, a used
charter's return on foot, a false "reached the area", invalid states, Field Log leaks), with about
127 KB of added save data after 18 in-game years. A separate archipelago soak (the world's halves with
no ground connection) stresses the charter paths ([PERFORMANCE](PERFORMANCE.md)).

## 12. Phase 3 handoff and future consumers

Phase 3 (abstract ↔ physical) will materialize contractors *around* their spatial truth, let them
act physically, reconcile the pawns, and write back where they actually ended. Spatial state does
**not** mean a pawn exists: travelling, arriving or working never generates one. *Phase 3 design:* while a person is physical
its spatial entry is **frozen**, and the anchor is written **once, by a direct assignment inside the atomic reconciliation
commit** (the facade methods here contain their own faults, so they are not used inside the commit);
[PHYSICAL_LIFECYCLE § 12, § 15.6](PHYSICAL_LIFECYCLE.md#12-spatial-integration).

Future invariants recorded now:

- **No random teleport visits.** Once spatial continuity exists, ambient contractor visits (later
  phases) should respect it: a contractor known by world truth to be far away does not appear
  tomorrow without a mobility or cause explanation.
- **Relay stopovers** (a working Comms Console as Network infrastructure) and **intersections**
  (contractor–contractor or with player caravans) may consume spatial truth later. A daily travel
  corridor is deliberately deferred: no consumer needs it yet, and at ~100 contractors proximity is
  cheap to compute when one does.
- **Spatial overlap creates opportunity, not automatic encounter** *(Phase 3.3 design direction)*. A physical handoff
  rendezvous is placed from a contractor's anchor and route; a future rival may interfere only with spatial opportunity,
  plausible knowledge, motive, availability and capability: no omniscient actors, no detection radius, no teleport ambush
  ([PHYSICAL_LIFECYCLE § 27.7, § 27.9](PHYSICAL_LIFECYCLE.md#277-the-rendezvous)). Nothing of it is implemented.

## 13. Diagnostics

Dev actions under "The Network (Phase 2.5)" ([DEBUGGING](DEBUGGING.md)): inspect or dump spatial
states (exact tiles, the current segment of a chartered leg), initialize, send somewhere, force
ambient relocation, catch up, invalidate a destination, drop route caches, move a work region, inspect
an operation's plan (ground or charter, hub, landing), send a running operation across water (a
charter test), invalidate a charter hub and reconcile, force a Disaster that is not Troubled, force
the next Troubled deadline to find or write off the group, force Missing at the contractor's position,
create a Last Known Location near an operation's contractor, inspect a Field Log, performance
counters (including charter plans, crossings, replans and failures). Logs go to the
`[TheNetwork][Spatial]` category.
