# Technical Risk Register

> The highest technical risks, how each is mitigated, and the phase in which it must be proven
> **experimentally** (by a spike or a soak test), not just argued. Severity and likelihood are
> judged after mitigation is designed but before it is proven.
> Scale: **Severity** Critical / High / Medium / Low · **Likelihood** High / Medium / Low.

| ID | Risk | Sev. | Lik. | Proven in |
|---|---|---|---|---|
| R-01 | Abstract ↔ physical pawn identity (duplication, resurrection, lost identity) | Critical | Medium | Phase 3 (S9r, S11, S17, S31 passed) |
| R-02 | Save/load during physical contractor encounters | High | Medium | Phase 3 (S9r, S11, S14) |
| R-03 | Vanilla quest and site cleanup semantics | High | Medium | Phase 1 (S1, S2), Phase 3 (S9r) |
| R-04 | Third-party mods destroying or modifying generated sites | Medium | Medium | Phase 1 (S2), ongoing |
| R-05 | External Def removal | High | High | Phase 1 (S7) |
| R-06 | Optional DLC APIs (Odyssey, Royalty) | Medium | Medium | Phase 7 (S15, S16) |
| R-07 | Duplicate rewards or cargo | High | Low | Phase 1 (A2), Phase 2 (S13) |
| R-08 | Deterministic background outcomes | High | Medium | Phase 1 (S8), Phase 2 soak |
| R-09 | Unbounded history and save growth | High | Medium | Phase 1 (S18), Phase 2 and 6 soaks |
| R-10 | Faction relation changes during active contracts | Medium | High | Phase 2 |
| R-11 | Contractor pawns recruited or captured by the player | High | High | Phase 3 |
| R-12 | World-object deletion | Medium | Medium | Phase 1 (S1, S2) |
| R-13 | Cross-mod quest interactions | Medium | Medium | Phase 3 (S9r) |
| R-14 | Performance of large, long-running contractor populations | High | Low | Phase 2 soak, Phase 3 soak |
| R-15 | Registry-quest custody unworkable at runtime | High | Medium | Phase 3 (S9r) |
| R-16 | Master design brief unavailable during Phase 0 | — | — | **Resolved** in the Phase 0 review |
| R-17 | Stories feel like transactions (narrative under-investment) | High | Medium | Phases 1–4 playtests |
| R-18 | Claim accounting inaccurate (non-caravan departures) | Low | Medium | Phase 1 |
| R-19 | RimWorld 1.6.x updates changing internals we rely on | Medium | Medium | every release |
| R-20 | Economic exploits (refund loops, cancel abuse, market-value and arbitrage exploits) | Medium | Medium | Phases 1–2, Phase 4 |
| R-21 | Vanilla site composition forces extermination before loot can leave | High | Medium | Phase 1 (S19) |
| R-22 | Source-context misattribution (source-mod evidence overweighted, or stance forced) | Medium | Medium | Phase 1 (A11), playtests |
| R-23 | Global cast settings lost, corrupted or leaking into saves | Medium | Medium | Phase 1 (A15) |
| R-24 | Hidden spatial continuity costs, misleads or leaks | Medium | Medium | Phase 2.5 (S20) |
| R-25 | Abstract charter transport becomes a teleport, a hidden economy or a crutch | Low | Medium | Phase 2.5 (S20) |
| R-26 | Contractor careers run away, double-credit, contradict themselves or bloat the save | Medium | Medium | Phase 2.75 soaks |
| R-27 | The in-game runtime test framework damages a live colony, leaks overrides, hangs, lies, or becomes a second architecture | High | Low | Phase 2.9 (headless `Runner.*`, mutation checks) + the owner's in-game runs in two environments (0 FAILs) |
| R-28 | An abstract writer keeps simulating a physical person (an authority leak) | Critical | Medium | Phase 3.0 (`RT-PHYS-011`, the gate) |
| R-29 | Exactly-once reconciliation fails under duplicate, late or missing wake-ups, a throw mid-commit, or a retried publication | High | Medium | Phase 3.0 (`RT-PHYS-003/013/014`) |
| R-30 | The registry reservation is unworkable or too costly (*R* × *W* per tick) | High | Medium | Phase 3.1 (S9r) |
| R-31 | Observation gaps (downed, caravan join, resurrection, map-removal pawns, dropped signals) lose a person | High | Medium | Phase 3.1/3.2 (S21) |
| R-32 | The physical test tier damages a real colony | Critical | Low | Phase 3.1 (S22) |
| R-33 | Pawn creation on a heavily modded list fails, is slow, spams relations or yields a wrong race | Medium | High | Phase 3.1 (S23) |
| R-34 | An unprepared removal strands reserved, suspended pawns (only if the vanilla-only registry is chosen) | Medium | Low | Phase 3.1 (S9r, S6) |
| R-35 | A first projection contradicts established truth (a role), the role machinery over-constrains generation, or a role / composition depends on when it was first observed | High | Medium | Phase 3.1 (S25) |
| R-36 | Identity vs retention: progressive concretization outgrows the retained-pawn cap, a company is promoted by mere presence, or the cap replaces a person the player met | Medium | Medium | Phase 3.2 (S27) |
| R-37 | The reconciliation commit crosses services and is half-applied, or a post-commit stage is inferred complete from its side effects | Critical | Medium | Phase 3.0 (`RT-PHYS-026/027/028/029`) |
| R-38 | Truthful aging has side effects (chronic conditions, an unfit person, an unsafe catch-up API) | Medium | Medium | Phase 3.1 (S12) |
| R-39 | Team cohesion is infeasible, over-trusted, or drifts into sanitizing real social history | Low | Medium | Phase 3.2 (S26) |
| R-40 | Reputation, fame and capability stay conflated and leak into projection | Medium | Medium | design now; a later focused phase |
| R-41 | Handoff exploits: cargo duplication, ownership ambiguity, a "reform caravan" loophole, a double charge | High | Medium | Phase 3.3 (design; S28–S30) |
| R-42 | A retained pawn becomes temporarily `Free` during a vanilla map exit and vanilla redresses, discards or reuses it before the Network's reservation takes effect | High | Medium → **Low (mitigated by M1; S31 passed in the owner's runtime, ADR-053); the 3.1 regressions are the owner's pending run** | Phase 3.1 (S31 **done**) |
| R-43 | The physical test tier is run on a save that matters, or its cleanup touches what it does not own | High | Low | Phase 3.1 (the session arm, the fact-only guard, ownership-proven disposal) |
| R-44 | Role-constrained creation cannot satisfy a role on a heavily modded race or kind list (repeated contained aborts) | Medium | Medium | Phase 3.1 (`RT-PHYX-011` on the owner's mod list) |
| R-45 | A temporary encounter faction leaves residue (its vanilla leader world pawn, or the faction itself after an unusual exit) | Low | Medium | Phase 3.1 (`RT-PHYX-008`; sentinel notes) |
| R-46 | **The retained registry does not survive a load:** it is built before the load's cross-references resolve, so retained people are `Free` on the first tick and vanilla discards them | **High** | **Observed** | Phase 3.1 (owner run on `29f31dd`; corrected; `RT-PHYX-010` A and B with a reload) |
| R-47 | A first projection borrows a special-purpose kind (boss, royal, ancient, cultist) because its combat stats fit the role | High | **Observed** | Phase 3.1 (owner run on `29f31dd`; corrected; `RT-PHYX-011` provenance) |
| R-48 | A person reaches first materialization with their operational role unknown (`Unset`) | Medium | **Observed** | Phase 3.1 (owner run on `29f31dd`; corrected; `RT-PHYX-001`) |

---

## R-01 · Abstract ↔ physical pawn identity
- **Failure modes.** The same character instantiated twice. A dead character reappears. A
  character's pawn is discarded by vanilla GC. A pawn is reused by vanilla in a random raid.
  Another mod copies a pawn along with our tags.
- **Mitigation.** The normative invariants are **P3-INV-001…036** ([PHYSICAL_LIFECYCLE Appendix B](PHYSICAL_LIFECYCLE.md#appendix-b-formal-invariants);
  the Phase 0 I-1 to I-10 are historical): bind-once, a reverse map, episode exclusivity, a dead-is-final guard, handlers
  that act only on bound objects, reconciliation from observed pawn state, no second `PassToWorld` for a pawn vanilla
  already passed, and registry reservation (no GC, no redress) with the exit window closed by M1 (spike S31 passed, ADR-053); after
  binding, a pawn's physical state is authoritative, so a failed placement never strands a person (P3-INV-033). A validator checks
  one-to-one maps on every load.
- **Proven by.** **S9r** (reservation effects; it revises S9), S11 (holder), S17 (copies), **S31** (the exit window); the
  `RT-PHYS` / `RT-PHYX` suites and the Phase 3 soak with forced scenarios (dev actions).
- **Phase 3 design review.** Refined and re-audited against the 1.6.9676 assemblies in
  [PHYSICAL_LIFECYCLE](PHYSICAL_LIFECYCLE.md): identity is `Actor ≠ Person ≠ Pawn`, a named person keeps one pawn for
  life, the invariants are P3-INV-001…032 ([Appendix B](PHYSICAL_LIFECYCLE.md#appendix-b-formal-invariants)); the
  corrections to this entry's premises are in [Appendix E](PHYSICAL_LIFECYCLE.md#appendix-e-what-the-audit-changed-from-the-phase-0-design).

## R-02 · Save/load during physical encounters
- **Failure modes.** Deployment state and pawn locations disagree after load. Signals are lost
  (receivers are not persisted). Reservations are lost.
- **Mitigation.** All state is persisted. Pawns are saved by vanilla wherever they are. The
  first-tick reconciliation re-derives fates. The registry reserved set is rebuilt from our
  stores. Signals are re-registered. The watchdog backstops missed signals.
- **Proven by.** Phase 3 test matrix: save/load at each deployment state × each anchor type.
- **Phase 3 design review.** The save matrix is [PHYSICAL_LIFECYCLE § 16.1](PHYSICAL_LIFECYCLE.md#161-save-at-every-point):
  load never generates, spawns or destroys a pawn; a reference to a dead pawn needs `saveDestroyedThings: true`; the
  minimum new persisted truth and the one-way version bump are in § 16.4–16.5.

## R-03 · Vanilla quest and site cleanup
- **Failure modes.** Vanilla removes a site or map at an unexpected time. Timeout destroys the
  site while the player is en route. Our registry quest is cleaned up or dropped.
- **Mitigation.** No Network state is owned by vanilla quests. Sites use vanilla semantics, which
  players already understand, and our comp mirrors each lifecycle callback. The registry quest
  has a non-null root, is `Ongoing` and never ends. On load, a missing registry quest is
  recreated.
- **Proven by.** S1, S2 (sites) and S9r (registry).

## R-04 · Third-party mods destroying or modifying generated sites
- **Failure modes.** A world cleaner deletes sites. Map-generation overhauls change the stash
  GenStep. A site loses its parts.
- **Mitigation.** Comp `PostDestroy` plus reconciliation for vanished world objects. Stash
  validation at engagement (was the payload placed?). If not, the opportunity is `Invalidated`
  with a partial refund of the Intel fee (policy) and an explanatory letter.
- **Proven by.** S2 plus ad-hoc compatibility testing with popular map-gen mods (tracked per
  release).

## R-05 · External Def removal
- **Failure modes.** A red-error cascade. Broken entities. Lost history.
- **Mitigation.** DefRef strings with silent resolution. Invalidate once, with a refund. Snapshot
  labels. Aggregated logging. No `Scribe_Defs` on external defs. The only unavoidable errors are
  vanilla's, for already-materialized Things of the removed def inside live sites.
- **Proven by.** S7 (Phase 1), extended in each phase to new reference kinds.

## R-06 · Optional DLC APIs
- **Failure modes.** A hard dependency by accident (touching a DLC type or def without a gate).
  Orbit tiles persisted without Odyssey.
- **Mitigation.** `Compat.<Dlc>` modules with gates. `[MayRequire]` DefOfs. `TileRef` layer-def
  check. CI-style check: a "no-DLC" test profile must pass all acceptance tests.
- **Proven by.** Every phase runs its acceptance tests with DLCs disabled. S15 and S16 for the
  specific adapters.

## R-07 · Duplicate rewards or cargo
- **Failure modes.** A stash is materialized twice. A delivery drops twice. A refund is paid
  twice. Items duplicate after save/load.
- **Mitigation.** Every external side effect happens inside a single synchronous transition that
  also persists the state change (`Materialized`, `Delivered`, ledger entry). Jobs are removed
  in the same step they run. Transitions check the current state (idempotent guards). There is
  no replay of events on load.
- **Proven by.** Phase 1 A2 (save/load at every state); Phase 2 delivery tests (S13), including
  a save immediately before and after the drop.

## R-08 · Deterministic background outcomes
- **Failure modes.** Reloading rerolls an outcome. The UI changes outcomes. The mod list changes
  past results.
- **Mitigation.** Per-entity seeds, a private PRNG, frozen inputs, commit points
  ([SIMULATION § 6](SIMULATION.md#6-determinism-and-rng)). Vanilla generators run only under
  PushState, and their results are committed at once.
- **Proven by.** S8 (Phase 1). Phase 2 soak: run the same save twice to the same tick and diff
  the Network XML.

## R-09 · Unbounded history and save growth
- **Failure modes.** Multi-megabyte saves after long play. Slow loads.
- **Mitigation.** Tiered retention with caps, tombstones, compaction of terminal entities, a
  bounded journal, capped bound pawns ([EVENTS_AND_HISTORY § 4, § 11](EVENTS_AND_HISTORY.md#4-retention-pruning-and-aggregation)).
- **Proven by.** S18 (Phase 1). 20-year fast-forward soak (Phase 2). 30-year soak with the
  lifecycle features (Phase 6).

## R-10 · Faction relation changes during active contracts
- **Failure modes.** A contract issued by a faction that is now hostile. A delivery to an enemy.
  Payment from a vanished faction.
- **Mitigation.** Political checks at every contract checkpoint (award, checkpoint, delivery,
  payment), with kind-defined responses (renegotiate, void, fail with `PoliticalCollapse`).
  No reliance on goodwill-change events.
- **Proven by.** Phase 2 tests using dev actions to flip relations at each state.

## R-11 · Contractor pawns recruited or captured by the player
- **Failure modes.** The org keeps counting a recruited member. A captured leader is also
  "leading" off-map. A double death when an executed prisoner was already counted.
- **Mitigation.** Reconciliation priority order (captured and defected before returned).
  Set-once fates. Custody `OutOfCustody` with reservation released. Succession triggered by
  `Captured` (leader). Recruited characters stay Known with a defected status.
- **Proven by.** The Phase 3 scenario matrix (recruit, arrest, execute, release, sell, enslave,
  rescue).
- **Phase 3 design review.** Held people are a persisted custody state with a bounded *custody watch*
  ([PHYSICAL_LIFECYCLE § 9](PHYSICAL_LIFECYCLE.md#9-custody-model)); implementation is subphase 3.2, and 3.1 fails safe
  into `Quarantined(UnsupportedCustody)` rather than faking capture support. Vanilla recruits kidnapped pawns into the
  captor's faction with MTB ≈ 30 days; the watch expects it.

## R-12 · World-object deletion
- **Failure modes.** A site is deleted by vanilla timeout, the player, a mod or a map-settling
  path without our knowledge. Dangling `WorldObjectRef`.
- **Mitigation.** Comp `PostDestroy`, the `Destroyed` signal, validation on load and at
  checkpoints (unresolvable ID means `Vanished`). `MapSettled` is handled as Claimed.
- **Proven by.** S1 and S2 variants: timeout, abandon, settle, dev-destroy.

## R-13 · Cross-mod quest interactions
- **Failure modes.** Quest mods iterate all quests and act on ours. They expect a quest root
  with generation nodes. They show hidden quests. The quest tab is cluttered.
- **Mitigation.** A single hidden registry quest. The root def is flagged so it is never
  generated or offered. Its parts do nothing in generic callbacks. No Network contracts are
  modelled as quests.
- **Proven by.** S9r with a sample of popular quest-related mods.
- **Phase 3 design review.** S9 is revised as **S9r**: it also compares a registry built only of vanilla classes
  (`QuestPart_ReservePawns`) against the Network-owned part ([PHYSICAL_LIFECYCLE § 7.4](PHYSICAL_LIFECYCLE.md#74-the-registry-reservation-retained-pawns-only)).

## R-14 · Performance of large, long-running contractor populations
- **Failure modes.** TPS degradation from many orgs, pawns or events over decades.
- **Mitigation.** Abstract orgs (headcounts). Suspended reserved pawns with a cap. Daily
  staggered upkeep. O(1) summaries. Budgeted scheduler. The banned-pattern list
  ([PERFORMANCE § 4](PERFORMANCE.md#4-banned-patterns-and-what-replaces-them)).
- **Proven by.** Phase 2 soak (the default ~100 contractor identities and a 300-identity stress
  run × 20 years). Phase 3 soak (150 stored pawns + 5 deployments), with an A/B TPS comparison.

## R-15 · Registry-quest custody unworkable at runtime
- **Failure modes.** Suspension causes oddities: pawns frozen in bad states, or vanilla code
  that assumes reserved pawns belong to real quests. Quest mods break it.
- **Mitigation.** Spike S9r before Phase 3 content. A documented fallback (`KeepForever` plus
  factionless storage) and a contingency patch C-1 with its analysis done in advance
  ([RIMWORLD_INTEGRATION § 3.3](RIMWORLD_INTEGRATION.md#33-contingency-patches-analysed-not-adopted)).
- **Proven by.** S9.
- **Phase 3 design review (R-30).** The reserved set is only the *stored named people* (vanilla already keeps spawned,
  held, caravan and kidnapped pawns); a `Free` retained pawn is redressed with chance up to 0.8 per generation, so the
  reservation is **required**; its cost is a vanilla per-tick check multiplied by the list length. The fallback ladder is
  in [PHYSICAL_LIFECYCLE § 7.4](PHYSICAL_LIFECYCLE.md#74-the-registry-reservation-retained-pawns-only).

## R-16 · Master design brief unavailable during Phase 0 (resolved)
- **Status.** **Resolved and closed.** Kept here as history, not deleted.
- **Original risk.** The first Phase 0 pass was written without the master design, so an
  architectural assumption could conflict with the canonical design. Twelve assumptions were
  listed for review.
- **Resolution.** The master design (*The Network — Full Mod Design - Master Implementation
  Brief.md*) was added to `main` during the Phase 0 review. The architecture was reconciled
  against the whole document: the twelve assumptions were resolved or narrowed to genuinely open
  questions, and every contradiction found was corrected
  ([ARCHITECTURE § 14](ARCHITECTURE.md#14-master-design-reconciliation)). Remaining open points
  are ordinary design and tuning questions, tracked there, not a missing-source risk.

## R-17 · Stories feel like transactions
- **Failure modes.** Correct systems produce dry logs. Players never say "Remember Dead Red?".
- **Mitigation.** Narrative is part of each phase's acceptance criteria (A7 in Phase 1). There
  is a narrative formatter with variety. Legends freeze good text. Letters are written as story
  beats. The Contractors tab shows deeds, not stats.
- **Proven by.** Playtest reviews in Phases 1–4. This is a content risk, not a code risk, which
  is why it is tracked here.

## R-18 · Claim accounting inaccurate
- **Failure modes.** History says "abandoned" when the player left by gravship with the loot.
- **Mitigation.** `PostCaravanFormed` + a fallback sample every 2,500 ticks + coarse narrative
  phrasing. Upgrade path: a Network SitePartDef worker `Notify_SiteMapAboutToBeRemoved`, or
  contingency patch C-3.
- **Proven by.** Phase 1 playtests with caravan, pod and shuttle departures.

## R-19 · RimWorld 1.6.x updates changing internals
- **Failure modes.** A method signature or behaviour we rely on changes (GC reasons, quest
  loading, site parts).
- **Mitigation.** Mostly public or virtual APIs. A reflective verification tool (as in
  Grandmaster21's `verify-real.sh`) for any reflective targets. The integration doc cites file
  and line for each assumption, so re-checking after an update is mechanical.
- **Proven by.** A per-release re-verification checklist (run the spikes' automated parts
  again).

## R-20 · Economic exploits
- **Failure modes.** Cancel-and-refund loops to fish for good leads. Reload before paying.
  Invalidation abuse by toggling mods. And the master § 78 list: procuring an item for less than
  it sells for; accepting an NPC procurement contract and buying the goods from the issuer's own
  settlement; transport-pod loops; stack-value and quality exploits; modded items with broken
  market values; zero-cost recipe products with enormous value; quest-only artifacts; faction
  goods duplication. An infinite wealth generator (master § 77).
- **Mitigation.** Cancellation refunds decay with elapsed time. The seed is per request, so a new
  request is a new roll, but it costs a new fee. Invalidation refunds only for defs missing at
  load or resolution, and a re-added mod's references resolve again. Determinism prevents
  reload fishing on existing requests. **Deposits are not refunded on in-world failure**, so
  failure is never a free reroll (ADR-027). Procurement prices have a floor above a market
  purchase and scale steeply with rarity; market value is one input, passed through sanity caps
  and fallback valuation (category medians, recipe input value) for broken or absurd values;
  quality is priced explicitly; unique items get severe premiums ([SIMULATION § 5.3](SIMULATION.md#53-pricing)).
  NPC procurement rewards (Phase 4) account for the issuer's own stock and the player's buy/sell
  spread. Intel sites need danger, travel, cost and uncertainty (master § 77).
- **Proven by.** Phase 1–2 balance passes; the Phase 2 "simulate N contracts" harness reports
  price-to-market ratios; Phase 4 arbitrage tests against issuer settlements.

## R-21 · Vanilla site composition forces extermination
- **Failure modes.** The vanilla `ItemStash` + threat-part site, or one of its comps, keeps the
  player from leaving with part of the loot while defenders live; recovered items are lost or
  duplicated on departure or re-entry; history records a defeat instead of a partial recovery.
  The design's objective is acquisition, not extermination (master § 17).
- **Mitigation.** No Network state waits for defenders to die; `Claimed` covers partial recovery
  ([STATE_MACHINES § 2.2](STATE_MACHINES.md#22-opportunity-truth)). If the vanilla composition
  fails, switch to another vanilla composition or a minimal Network site part (ADR-015).
- **Proven by.** S19 and acceptance criterion A10 in Phase 1.

## R-22 · Source-context misattribution
- **Failure modes.** Every modded item is "guarded" by a faction from its own mod; a friendly
  faction is turned into an enemy to fit an archetype; the Network appears to require a content
  mod; hardcoded per-mod rules creep in.
- **Mitigation.** Source package is one weighted signal; seeded draws; stance is never forced;
  "no credible source" is valid; no per-item or per-mod code (ADR-026; [ARCHITECTURE § 6.14.1](ARCHITECTURE.md#6141-opportunity-source-and-context-resolution)).
- **Proven by.** Phase 1 A11 and headless resolver tests; playtests with and without Beyond Our
  Reach.

## R-23 · Global cast settings lost, corrupted or leaking into saves
- **Failure modes.** A settings migration wipes the player's custom contractors or their custom
  Legendary Fixer. One malformed template breaks the whole cast. A global edit renames or
  resurrects an actor in an ongoing colony. Runtime outcomes leak into `ModSettings`, so one
  colony's history appears in another.
- **Mitigation.** Stable template IDs; `NetworkSettingsVersion` with ordered migrations that
  preserve custom entries; per-template quarantine instead of wiping; regeneration replaces only
  generated entries; each world snapshots the cast and is authoritative afterwards; runtime code
  never writes settings (ADR-030; [SAVE_AND_MIGRATION § 11](SAVE_AND_MIGRATION.md#11-global-cast-settings-networksettingsversion)).
- **Proven by.** Phase 1 A15 and settings-migration fixture tests (a malformed template and
  custom entries that must survive).

## R-24 · Hidden spatial continuity costs, misleads or leaks (Phase 2.5)
- **Failure modes.** World pathing is slow on large or heavily modded worlds; a route cannot be
  found and a contractor teleports or an operation stalls; a changed world (a removed layer, new
  impassable terrain) leaves invalid anchors; hidden positions leak into the UI and turn the mod into
  a tracking map; ambient movement starts producing events, history or an invisible economy. Found
  and fixed in the correction pass: an overdue journey arriving after load without a route; a short
  approximate distance hiding a long real route (hidden super-speed); a Disaster with survivors never
  coming home; a recovered group left at the incident; a dead Solo walking home; sparse land never
  found by seeded probes.
- **Mitigation.** No per-tick work: catch-up only on the existing daily upkeep and at checkpoints;
  routes only when a journey starts or a cache is rebuilt, never for stationary contractors;
  reachability checked before pathing; soft failure (last valid anchor kept, `Blocked`) and the
  operation timeline stays authoritative; load reconciliation re-anchors or drops invalid tiles;
  read models carry no location field and the Field Log speaks in reports; ambient movement publishes
  nothing; the remaining route is proven before arrival; real route steps, never approximate
  distance, decide what fits; arrivals are never sooner than walking takes; only a Troubled outcome
  keeps a group out; recovery reconciles; ended contractors are frozen; a guaranteed last-resort
  search (ADR-041 to ADR-045; [SPATIAL](SPATIAL.md)).
- **Proven by.** Headless `Spatial.*`, `Migration.*`, `FieldLog.*` and `Charter.*` tests (each
  correction's regression fails with its defect re-introduced); the soaks' daily spatial invariants
  (no teleport, no contractor faster than its own pace, no ended contractor moving, no recovered group
  out of place, no invalid anchor, no Field Log leak or duplicate, nothing stuck); spike S20 in game
  (not run yet).

## R-25 · Abstract charter transport becomes a teleport, a hidden economy or a crutch (Phase 2.5)
- **Failure modes.** Charter "fixes" an invalid or cross-layer tile; a one-way crossing leaves a
  group unable to come back; a charter quietly costs the player silver or spawns a craft; ambient
  contractors hop between islands in the background; charter replaces walking everywhere; a reload
  changes the provider or the landing; a destroyed provider strands a leg forever or causes a jump; a
  delay makes a group walk home and ignore the pickup it booked; a plan keeps claiming a charter the
  live journey no longer uses; the Field Log says "reached the area" for a group still travelling or
  Blocked (all three found and fixed in the final pass).
- **Mitigation.** Same-layer, valid, passable destinations only; ground always tried first, per
  candidate; operation travel only; one committed two-way plan (hub + landing, reused for the pickup);
  only the two crossing ends are saved and the crossing is a single step, so a rebuilt route is the
  journey that was left; provider loss reconciles from current truth (another provider, on foot, or
  `Blocked`); a used charter is the way back until it is genuinely lost; `charterUsed` / `charterLost`
  keep history and current truth apart; "reached the area" follows spatial arrival; a source scan
  forbids spawning and money calls in spatial code; the provider fact is derived from the game's
  `TechLevel`, never a named faction (ADR-045, [SPATIAL § 6.1](SPATIAL.md)).
- **Proven by.** `Charter.A`–`N`, `Charter.CommittedRoundTripStillUsesPickupAfterDelay`,
  `Charter.ReplanToGroundDoesNotLeaveStaleLiveCharterState`, `Charter.ReturnProviderLossCanDegradeSafely`,
  `FieldLog.BlockedSpatialDoesNotClaimArrived`, `FieldLog.LateSpatialArrivalLogsWhenActuallyReached`,
  `Spatial.CharterBridgesDisconnectedGeography`, the soaks' plan/leg and arrival invariants, the charter soak world
  and `Soak.ArchipelagoCharterStress` (charters under every invariant, provider settlements coming and
  going); spike S20 in game (not run yet).

## R-26 · Contractor careers run away, double-credit, contradict themselves or bloat the save (Phase 2.75)
- **Failure modes.** Reputation **runs away** (safe hauls or sheer contract count make everyone Famous);
  **everyone converges to Legendary** or tier 5; **wealth inflation** (the rich get richer with nothing to
  spend on, or everyone is broke); contractor money is **double-credited** (a retry, a reload, a ledger
  rescan, an award that credits twice); a **replacement contractor is mis-credited** (the carried-over deposit
  paid to the contractor that was lost lands in the replacement's funds, or vice versa); a technical
  invalidation leaves a **windfall**; an **equipment upgrade runs away** (during a job, every day, without
  reputation or reserve); a derived **Tag contradicts** the state it is read from, or quietly becomes a second
  stat system that stacks with the resolver; the career result is applied **twice** (a recovery, a retry, a
  reload) or **to an old operation** (invented credit); the **save grows** because history is copied onto the
  contractor.
- **Mitigation.** Reputation gain uses the operation's own frozen danger and tapers above a danger-dependent
  ceiling, so easy work stops counting (twenty thousand trivial triumphs make a local name and no more); no
  reputation is ever added by count alone and none is ever lost. Equipment advances at most one tier per thirty
  days, only with reputation, `cost + reserve`, health and no live commitment, from the existing upkeep; the
  ladder is capped at 5. All funds movement goes through one saturating path; the contractor's share is written
  on the ledger record in the same step as the movement, a refund takes back exactly the proportion that was
  refunded (a technical invalidation takes back everything the current contractor still holds, even after an
  insurance payout reduced the player's refund), an insurance payout and a replacement transfer move nothing, and a
  load never rescans the ledger. The career result is a persisted flag on the operation that is set only after the
  planned result's durable commit (a failed commit, or a missing contractor or simulation, leaves it false and
  retryable), applied from the lifecycle's end only, and never for an operation from a pre-2.75 save. Tags and `CareerNeed` are computed on demand and a
  source scan keeps the resolver, pricing, willingness and upkeep from ever reading them. `CareerRecord` is a
  fixed set of counters (about 1.6 % of the save); History keeps the detail.
- **Observed, not asserted (soak, 100 and 300 contractors, 20 in-game years).** Nothing runs away: Legendary
  stays at the starting handful (3 of 125, 8 of 375), tier 5 at the starting few, and the fame distribution is
  flat from the first quarter of the run to the last. The opposite is the finding: because Phase 2's market is
  player-driven, the median contractor finishes **2 jobs in 20 years** (a few finish hundreds), so most careers
  barely move, and **wealth is concentrated and mostly negative** (funds −107 k / −5.9 k / +715 k, min / median /
  max; 92 of 125 contractors need `Capital`) because upkeep is flat per head with no floor and the only income is
  the player's jobs. That is a Phase 2 economy trait that the career spine now makes visible, not a regression;
  it is also what Phase 4 (NPC-issued work) and Phase 4B (alternative compensation, which consumes
  `CareerNeed`) are for. Treat a future change that makes everyone Legendary, or that gives funds a floor, as a
  tuning decision with the soak report in hand.
- **Proven by.** `Career.*` and `Migration.V3ToV4_*` tests (matrix A–G, each exactly-once path, the farming
  loop, the money cases); the soaks' career invariants, all zero: duplicate outcomes, stuck results, legacy
  operations awarded, funds drift (funds = the sum of every tallied flow), ledger attribution (bounded; equals
  credits less clawbacks; a voided contract holds nothing), fame/score mapping, negative score, overflow, tier
  bounds, advancement while committed, tier moved other than by advancement, Tag contradictions, Augmented.

## R-27 · The in-game runtime test framework damages a live colony, leaks overrides, hangs, lies, or becomes a second architecture (Phase 2.9)
- **Failure modes.** (1) The runner **mutates the live save**: a test spends silver, adds a contract or
  history entry, moves a contractor, changes a career, or leaves a test actor that the next autosave persists.
  (2) A **global dev override leaks**: a test sets `ProcurementDevOverrides` / `IntelDevOverrides` /
  `ServiceToggles`, and the owner's colony then gets forced outcomes or free fees; or a test "resets" an override
  the owner had deliberately set. (3) A **false PASS** from testing copied logic instead of the production
  services. (4) A **hanging runtime test** that blocks or starves the game thread, or waits forever. (5) A
  **persisted dev scheduler job**: the runner's own bookkeeping is a job in the saved scheduler and survives a
  reload or a mod removal. (6) **Destructive physical cleanup** of a future physical suite deletes the player's
  items or leaves cargo and world objects. (7) The framework grows into a **second gameplay architecture**
  (its own services, rules and state that the game starts to depend on). (8) The tests **diverge from the
  actual production code** (the sandbox ports drift from the real adapters; a suite is green while the game is
  not).
- **Mitigation.** (1) Sandboxes share no writable state with the live Network; the live Network is read only
  through read-only views; a `LiveFingerprint` of the Network's durable truth (every persisted field of every
  store, by content, so a change to an existing relation, contract term, checkpoint, history record or Field
  Log entry is seen even when no count moves) plus a colony/world sentinel (beacon silver, cargo, world
  objects, letters) is compared before and after every slice and reported as RT-INFRA-001; **a capture that
  throws on a running Network FAILS it (fail closed)**; a runtime test **never starts, reconciles or repairs
  the live Network** (`EnsureStarted`, `StartNow`, `RunStartup`, `NetValidator` are forbidden in runtime-test
  code; a never-started Network gives SKIP, with the advice to allow one tick); nothing the runner holds is
  `Scribe`d; a source scan forbids spawning, spending, launching, letters and live-scheduler use. (2) Every step captures the current values of all 18 static overrides and toggles,
  presents the neutral state a scenario expects, and restores the **previous** values after the step even when
  it threw, so the game never sees one; a step that forgets to clear one fails its test (RT-INFRA-002). (3)
  Sandbox scenarios call the *production* services over sandbox ports; they contain no copy of the rules; the
  expected values are derived from generated terms; eight mutation checks against the production code and the
  runner each made a named test fail. (4) The runner is pumped in 8 ms slices from the world component's
  update, a `Wait` yields at once, every test has a finite real-time timeout and wait count, a sandbox's clock
  only moves through bounded `AdvanceUntil`, and a timeout reports step, sandbox tick, pending jobs and
  entities. (5) The runner has no job at all; RT-INFRA-003 checks that no runtime-test job kind is in the live
  scheduler. (6) No destructive or physical suite exists (ADR-047 point 5); one needs its own disposable
  environment, design and ADR; the safe suite only *plans* a drop-pod delivery. (7) ADR-047 freezes the scope:
  developer infrastructure, no player UI, no background monitoring, no Harmony, no save change; nothing in the
  game's own code depends on it except one static null check per frame. (8) The suites run the production
  classes (a change to them changes the test's subject); a real catalog item is priced through production
  services (RT-PROC-011); the live scan inspects the real adapters; the sandbox ports mirror the port
  interfaces and a port interface change fails the build.
- **Residual risk (stated, not hidden).** The in-game suites `RT-SMOKE-*` / `RT-LIVE-*`, the game host and the
  Dev actions were compile-checked where this phase was built and have since **run in the owner's game in two
  environments with zero runtime FAILs** ([RUNTIME_TESTING § 15](RUNTIME_TESTING.md#15-owner-observed-runtime-evidence));
  the in-progress Cancel path was not exercised by hand. The fingerprint covers the Network's persisted fields and the
  sentinel's selected colony/world state, and nothing else (it is not a byte-compare of the save, and pawn state,
  terrain and buildings are not covered). A false alarm is possible if a runtime-only cache field that a
  read-only call fills in is not named `cached*`; the walker skips dictionaries, sets and `cached*` fields, and
  `FingerprintIgnoresReadOnlyAccess` checks the known read paths. The fingerprint costs about 5 ms per capture
  on a synthetic 365-actor world (two per frame while a run is active, nothing otherwise). The sandbox is a model of the game, not the game:
  real-game-only behaviour (a real pod landing, a real map) stays a manual check (§ 13 of
  [RUNTIME_TESTING](RUNTIME_TESTING.md)).
- **Proven by.** `Runner.*` (exception containment, stable order and IDs, stop / continue, cancel restoring
  overrides, clean timeout, runtime-only preserved failure, discarded sandboxes, live-scan non-mutation, exact
  report counts, stable export format, exact override restore, no control job in the scheduler, the safe suite
  leaving a synthetic live world's fingerprint, silver, scheduler and careers identical, a mutated fake live
  world detected, idle cost one null check); the three sandbox suites through the real runner; and the
  source scan. The owner's in-game *Full safe regression* runs (a fresh Quicktest colony; the real modded
  colony twice) are the in-game evidence: 0 FAILs, live colony unchanged by manual check.

## R-28 · An abstract writer keeps simulating a physical person (Phase 3)
- **Failure modes.** "Abstract Halvard is healthy, available and taking jobs while physical Halvard is dead in a ditch":
  an upkeep job heals him, procurement selects him, spatial relocates him, succession counts him, an operation
  checkpoint resolves his fate a second time.
- **Mitigation.** One central question, `CanSimulateAbstractly(person)`, in front of every writer in the inventory
  ([PHYSICAL_LIFECYCLE § 2.3](PHYSICAL_LIFECYCLE.md#23-the-writer-inventory-every-abstract-site-that-must-honour-authority));
  authority changes only in `Materialize` and `Reconcile`; a validator finding for a custody/episode mismatch;
  a static test that enumerates the writers. *(Correction)* The gate also stays **closed while any episode membership
  remains**, including a Closed episode whose release has not completed, so a half-released person is never advanced by two
  layers (P3-INV-029).
- **Proven by.** Phase 3.0: `RT-PHYS-011` (the gate is complete), `RT-PHYS-002/006/016`, `RT-PHYS-029` (the gate across a
  release interruption), mutation checks (a writer bypassing the gate must fail a named test).

## R-29 · Exactly-once reconciliation fails (Phase 3)
- **Failure modes.** A death signal, a poll, a map removal, a load pass and a dev action each apply the consequence;
  or a throw leaves half of it applied; or the flag is set before the mutation.
- **Mitigation.** *observe → decide → plan → validate → atomic durable commit → flag last → release → follow-up → publish*;
  set-once member outcomes; `consequencesApplied` as the last statement of a **snapshot-guarded** commit; the Closed gate;
  an explicit durable marker for every post-commit stage; and **publication from a durable outbox under a persisted
  per-event cursor**, so the existing bus (which has no dedupe key and assigns a new sequence number on every `Publish`)
  is never asked to accept an event twice and a consumer is never replayed
  ([§ 15](PHYSICAL_LIFECYCLE.md#15-reconciliation-algorithm)).
  *Amended:* the first revision called the commit "primitive assignments that cannot fail" (see R-37), and the second
  assumed "keyed" events could be republished, which the real bus does not support.
- **Proven by.** Phase 3.0: `RT-PHYS-003/013/014/026/028`; mutation checks (flag first; reconcile inline in a handler; a
  retry that re-submits an accepted event).

## R-30 · The registry reservation is unworkable or too costly (Phase 3)
- See R-15 and [PHYSICAL_LIFECYCLE § 7.4](PHYSICAL_LIFECYCLE.md#74-the-registry-reservation-retained-pawns-only). Cost
  ∝ the reserved list length × the non-mothballed world pawns, per tick; bounded by the small reserved set (stored
  named people, soft cap ≈ 150) and measured in the Phase 3 soak.
- **Proven by.** Spike **S9r** (3.1): no redress in 200 forced generations, `Suspended`, GC-safe across several passes,
  clean save/load, removal ≤ 3 errors, cost within budget; or the fallback ladder is decided and recorded.

## R-31 · Observation gaps lose a person (Phase 3)
- **Failure modes.** No downed signal; no signal when a pawn joins a caravan; no resurrection signal; map removal
  passes non-colonist pawns to the world without `LeftMap`; `SignalManager` drops signals past 3,000 per frame.
- **Mitigation.** Signals are only wake-ups; bounded polls (Open-episode members every 250 ticks, held people every
  2,500); `Returned` only on a positive observation; the load pass; Quarantine for the unclassifiable
  ([§ 14](PHYSICAL_LIFECYCLE.md#14-event-detection), [§ 9](PHYSICAL_LIFECYCLE.md#9-custody-model)).
- **Proven by.** Spike **S21** and the physical tier (`RT-PHYX-*`); `RT-PHYS-008/010`.

## R-32 · The physical test tier damages a real colony (Phase 3)
- **Failure modes.** A destructive suite is pressed by accident in the owner's real, heavily modded colony: it spawns
  pawns, creates world objects and factions, and mutates the Network's records in a save the owner plays.
- **Mitigation.** A separate menu category; a typed **session-only arm** (runtime-only, cleared on load and after each run,
  never persisted); a **dedicated generated test map by default**; a stronger second gate for any home-colony scenario;
  **no inference** of whether a save is disposable (a low-wealth save may be a real early colony); every created entity
  tagged; a blast-radius proof (untagged state unchanged); cleanup only of tagged entities; a separate source folder with
  its own scan; Full Safe Regression never reaches it
  ([PHYSICAL_LIFECYCLE § 21](PHYSICAL_LIFECYCLE.md#21-runtime-qa-strategy), ADR-049).
- **Proven by.** Spike **S22** (the arm flow and the test-map lifecycle); headless `Runner.*` tests for the guard.

## R-33 · Pawn creation on a heavily modded list (Phase 3)
- **Failure modes.** A modded race or xenotype cannot be generated or named; generation is slow; relation generation
  creates relatives as new world pawns; a redressed existing pawn is returned instead of a new one.
- **Mitigation.** `ForceGenerateNewPawn = true`, `CanGeneratePawnRelations = false`, capability-based kind selection
  with a fallback chain, a contained abort that reverts custody, bounded group size; role verification adds a bounded
  retry budget (R-35).
- **Proven by.** Spike **S23** across the owner's mod list.

## R-34 · An unprepared removal strands reserved, suspended pawns (Phase 3)
- **Failure modes.** If the registry is built only of vanilla classes, removing the mod without *Prepare for removal*
  leaves the pawns reserved, suspended and un-aging forever.
- **Mitigation.** The default registry part is **Network-owned**, so vanilla drops it on removal and the pawns become
  ordinary; *Prepare for removal* clears the registry either way ([§ 20](PHYSICAL_LIFECYCLE.md#20-prepare-for-removal)).
- **Proven by.** Spike S9r with a mod-removal run.

## R-35 · A first projection contradicts established truth (Phase 3)
- **Failure modes.** A "crack marksman" materializes with Shooting 1, Melee 19, the Brawler trait or no capacity for
  violence; a legendary medic cannot doctor. Or the opposite: the role constraints are so tight, or the modded kind list so
  poor, that generation fails, loops or spikes a frame. A request validator is trusted as a guarantee although vanilla
  **drops validators from the 100th try**.
- **Mitigation.** Request fields and validators as an optimization; an **authoritative post-generation verification** while
  the candidate is still unbound and unspawned; the smallest correction (raise a role-defining skill's **base level** only,
  respecting aptitudes, then re-verify; **never passion**, trait, backstory, gene or incapability); an incapability is
  identity and the candidate is rejected; the role and an organization's composition derive from **immutable origin facts**
  by a frozen pure function, never from the moment of first observation (P3-INV-030); a bounded attempt count then
  an abort that persists nothing; roles map to *existing* kinds only (`Pawn.kindDef` is saved by def name)
  ([§ 6.8](PHYSICAL_LIFECYCLE.md#68-role-constrained-creation-validate-then-the-smallest-correction), ADR-050).
- **Proven by.** Spike **S25**; `RT-PHYS-020/021` (pure verdict and correction), `RT-PHYS-030` (time-independent identity),
  `RT-PHYX-011` on real pawns.

## R-36 · Identity versus retention (Phase 3)
- **Failure modes.** Progressive concretization retains every placed seat of small organizations, so the retained-pawn count
  grows past the soft cap; or the cap tempts the Network to release a person the player has met and later put a *stranger* in
  that seat; or a persistent roster of company soldiers appears by accident.
- **Mitigation.** Concretization is **bounded by the existing named-people caps** (1 leader + ≤ 2 lieutenants + ≤ 6 known
  members); rank-and-file of large organizations stay ephemeral and are promoted only by **strong story evidence** (a
  material outcome, being named by the Network, a narrowed battle-log signal), **never by presence alone**, so a company does
  not slowly turn into a persistent roster; the cap is a *performance* policy that releases only never-encountered people and
  those with no living seat, and is **exceeded rather than break identity**
  ([§ 4.5](PHYSICAL_LIFECYCLE.md#45-progressive-concretization)).
- **Proven by.** `RT-PHYS-023`, `RT-PHYX-014`, the Phase 3 soak (retained-pawn growth); spike **S27** (encounter evidence,
  including the reliability of the narrowed log tests).

## R-37 · The reconciliation commit crosses services and is half-applied (Phase 3)
- **Failure modes.** The existing casualty, succession and actor-ending paths interleave durable mutation with inline
  `ctx.bus.Publish` (whose consumers run foreign code), scheduler cancellation and fault-swallowing facades (the spatial
  facade returns a default on any fault). A commit that simply calls them can leave a character killed but the headcount
  unchanged, a successor created but not installed, an actor ended with the anchor unwritten, or a consequence applied again
  on retry.
- **Mitigation.** A pure, validated plan; a **snapshot of exactly the touched set** restored on any throw; the flag as the
  last statement; **no publication, scheduler, vanilla or fault-swallowing call inside the commit**; idempotent post-commit
  stages, each with an **explicit durable marker written last and never inferred from side effects** (a removed tag, a
  changed status), and a finish-pending pass that reads only those markers; a durable publication outbox with a per-event
  cursor; a split (not a call) of the existing paths with a parity test
  ([§ 15.2, 15.6](PHYSICAL_LIFECYCLE.md#156-failure-semantics-of-the-commit-service-by-service), ADR-048). The linked
  operation's follow-up (`OnPhysicalResolved`) must be re-entrant because the legacy `TroubledDeadline` flips status mid-way.
- **Proven by.** Phase 3.0: `RT-PHYS-026` (a fault-injection sweep over every step, using the existing `LiveFingerprint`),
  `RT-PHYS-027` (purity and parity), `RT-PHYS-028` (interruption between stages), `RT-PHYS-029` (release interruption and the
  authority gate), `RT-PHYS-014` (publication interruption); mutation checks.

## R-38 · Truthful aging has side effects (Phase 3)
- **Failure modes.** A stored contractor catches up many years at once: chronic age-related conditions appear, the person is
  unfit to materialize, the abstract record still calls them Active, or the catch-up API misbehaves on a non-ticking pawn
  (letters, life-stage consistency, an exception). The opposite failure, capping the interval, would age rarely met people
  more slowly than frequently met ones.
- **Mitigation.** A frozen contract (chronological age derived from the clock; biological age fully, uncapped, current before
  any observation) with the mechanism left to spike S12 (candidate: vanilla's own `AgeTickMothballed`, which crosses every
  birthday); an unfit person is not placed and nothing is mutated; the abstract consequence is an owner decision (O-12)
  ([§ 6.4](PHYSICAL_LIFECYCLE.md#64-truthful-aging-of-a-retained-pawn), ADR-050).
- **Proven by.** Spike **S12** (extended); `RT-PHYS-025`, `RT-PHYX-006/012`.

## R-39 · Team cohesion is infeasible, over-trusted, or becomes sanitizing (Phase 3)
- **Failure modes.** The opinion of an *unspawned candidate* is not meaningful; friction traits and ideologies are mod-
  dependent; the screen over-rejects and spikes generation; or, worse, the Network starts "fixing" real pawns' relationships
  after binding, which would erase organic history.
- **Mitigation.** A derived band; screening only at first generation; prevention by construction (relations off, the
  organization's existing ideology); the Network never writes relations, opinions, thoughts, memories or traits of a bound
  pawn; a best-effort fallback the owner decides ([§ 6.9](PHYSICAL_LIFECYCLE.md#69-team-cohesion)).
- **Proven by.** Spike **S26**; `RT-PHYS-024` (initial-only; source scan), `RT-PHYX-013`.

## R-40 · Reputation, fame and capability stay conflated (Phase 3 design; a later phase)
- **Failure modes.** Today one numeric score is both the professional record and the "fame" band, and the equipment rung is
  gated on the band. Physical projection could treat fame as skill; a low-profile elite cannot buy better kit; a famous but
  mediocre principal with a competent bodyguard, and an unknown professional new to a region, cannot be represented.
- **Mitigation.** Phase 3 never reads fame, reputation or visibility in projection (P3-INV-019, `RT-PHYS-022`); wording
  corrections in CAREERS and DATA_MODEL mark *implemented truth* versus *future design*; the separation (a visibility value,
  a professional-reputation reading, regional scope, per-seat capability grade) is deferred to a focused phase before
  compensation and the contract board ([§ 6.10](PHYSICAL_LIFECYCLE.md#610-professional-reputation-fame-and-capability), O-17).
- **Proven by.** `RT-PHYS-022` now; the later phase's own tests.

## R-41 · Handoff exploits (Phase 3.3, design direction)
- **Failure modes.** Contractual cargo is duplicated (items in the contractor's hands and delivered by the handoff);
  ownership is ambiguous between contractor and player; a "form caravan / job complete" path takes goods that were never
  paid for; a failure after the charge leaves the player charged and empty-handed; a robbery is blocked by an invented
  ownership lock.
- **Mitigation.** Cargo stays under vanilla possession of the contractor party until an explicit, **idempotent staged
  protocol** with exactly-once *semantics* (not one atomic commit: payment and Thing movement are external side effects):
  validate, reserve under a persisted transaction id, charge, transfer with positive transfer evidence, finalize, and a
  compensating path; the order is like today's delivery (plan, charge, hand over; failure after the charge takes the
  existing refund path); a terminal handoff state is required before normal completion; **no ownership locks and no invulnerability** (physical reality wins)
  ([§ 27](PHYSICAL_LIFECYCLE.md#27-phase-33-procurement-fulfillment-and-physical-handoff-design-direction), ADR-051).
- **Proven by.** Spikes **S28 to S30**; handoff scenarios `RT-PHYX-030+` (pay, decline, rob, abandon). Not before 3.3 is approved.

## R-42 · A retained pawn becomes temporarily `Free` during a vanilla exit (Phase 3.1)
- **Failure modes.** A normal exit is performed by **vanilla**: `Pawn.ExitMap` despawns the pawn and passes it to
  `WorldPawns` itself, and map removal does the same for every pawn with no `LeftMap` signal for a contractor. Until the
  Network's registry reservation takes effect the contractor is an ordinary `Free` world pawn. `Free` is what vanilla
  **redresses** into a raid, visitor or prisoner (and *mutates*), what its world-pawn GC may **discard**, and what quest
  generation may select; and the episode's temporary faction is removed on a later tick, nulling the faction of a still-`Free`
  pawn. A related defect, found while auditing this: the first design passed a `Returned` pawn to the world **again**, which
  vanilla rejects as "already here" ([PHYSICAL_LIFECYCLE § 7.5](PHYSICAL_LIFECYCLE.md#75-who-may-call-passtoworld-an-observed-world-pawn-is-never-passed-again), P3-INV-031).
- **Mitigation.** **No guessed fix: the mandatory runtime spike S31 chose the mechanism, and it passed in the owner's runtime with M1
  (ADR-053).** The smallest safe mechanism, in the order of preference S31 tried: reserve the retained named pawn **while it is still spawned** (static reading suggests every reservation
  consumer is gated on `WorldPawns.Contains`, which is a reason to test it and not a proof); else reserve synchronously at
  a vanilla callback (the `LeftMap` signal inside `ExitMap`; `Notify_SiteMapAboutToBeRemoved` for a map removal); else the
  narrowest documented Harmony contingency (**C-4**, only if both are proven insufficient; **not needed**, never adopted). 3.0 was never
  affected (it creates no pawn) ([§ 7.6](PHYSICAL_LIFECYCLE.md#76-the-vanilla-exit-window-resolved-by-m1-spike-s31-owner-validated)).
- **Proven by.** Spike **S31** (cases A to G): **PASSED in the owner's runtime; M1 accepted** (ADR-053). Then the physical-tier
  regressions `RT-PHYX-015` (normal exit) and `RT-PHYX-016` (map removal) and P3-INV-032 on the 3.1 build: implemented; the owner's first run
  (build `29f31dd`) showed M1 holding while spawned and across a vanilla exit, and exposed the **load-time gap of R-46** (corrected); the corrected build awaits its reduced retest.
- **Phase 3.1 status (implemented; owner physical validation in progress, not a PASS).** Phase 3.1 is built on the accepted
  **M1** (reserve while spawned): the registry's membership is derived from the binding and the retained custody, the binding precedes
  the spawn, placement is refused unless the registry already covers the pawn, and RELEASE only **proves** the reservation (it never
  creates it); no Network `PassToWorld` for a pawn vanilla passed (source-scanned). After M1 an **actual `Free` on a retained named
  person is itself the failure** (P3-INV-034): it is observed as `ReservationBroken`, quarantined, never returned and never repaired by the
  lifecycle, and `RT-PHYX-002`, `005`, `015` and `016` fail on it. `RT-PHYX-015` and `016` are implemented with per-frame and
  `LeftMap`-instant checks.

## R-43 · The physical test tier runs where it should not (Phase 3.1)
- **Failure modes.** An owner presses a physical scenario in a save that matters; a run's cleanup removes something it did not
  create; a failed run's evidence is deleted.
- **Mitigation.** A separate Dev Mode category whose name says "disposable environment only"; a typed phrase arms **one** action for
  the current game object only (never saved, cleared on load and quit); the guard checks facts only (Dev Mode, armed, Network running,
  adapter available, no other run, no incomplete episode) and never guesses whether a save matters; every scenario runs on the suite's
  own test map; the only discard is of a pawn proven to be the run's own (tagged, unbound, never spawned, not a world pawn); bound
  pawns are never destroyed; cleanup refuses while an incomplete episode has a member on the map; nothing is cleaned up on a failure.
- **Proven by.** `Phys31.Tier*` and the `Phys31.Scan_*` source scans (headless); the owner's run.

## R-44 · Role-constrained creation fails on a modded kind list (Phase 3.1)
- **Failure modes.** No loaded kind yields a candidate that satisfies a role, so a person cannot be materialized; or a contradicting
  pawn slips through.
- **Mitigation.** Kinds come only from the encounter faction's generic member pool (capability and content, never a mod name; no global scan, no fallback to one: R-47); four seeded attempts; the
  authoritative verdict runs on the real candidate; failure is a contained abort (the member stays unplaced, the episode closes
  `NeverPlaced` through the commit, nothing is bound). A contradicting pawn is never returned.
- **Proven by.** `RT-PHYS-020` (pure verdict and correction); `RT-PHYX-011` on the owner's mod list (counts aborts by role).

## R-45 · Encounter-faction residue (Phase 3.1)
- **Failure modes.** Each episode leaves a vanilla faction leader world pawn behind, or a temporary faction is never removed.
- **Mitigation.** The faction is vanilla's own hidden temporary refugee pattern; a normal exit queues its removal through vanilla and
  RELEASE hands it back too (covering map removal). Its leader is an ordinary vanilla world pawn the world-pawn GC may collect, exactly
  as for vanilla's refugee quests. The sentinel reports both as explained deltas.
- **Proven by.** `RT-PHYX-008` (the faction is removed after the episode); the sentinel notes of every run.

## R-46 · The retained registry does not survive a load (Phase 3.1, observed)
- **Failure modes.** `World.FinalizeInit` (which builds the registry) runs before `Scribe.loader.FinalizeLoading()` resolves cross-references, so an index built from `PawnRef.pawn` is empty; the first tick runs `WorldPawns` before any world component, so every
  Stored or Deployed person is an ordinary `Free` world pawn that vanilla may redress or discard; later saves warn about references to discarded things.
- **Mitigation.** Two load stages: the durable thing-id index at `FinalizeInit` (a narrow bridge: the persisted `PawnRef` stays authoritative, a resolved pointer must agree) and the validated pointer index at the world component's `PostLoadInit`; the registry quest restored
  from durable state before the first tick; a bounded integrity audit of every living bound person (unresolved, discarded, mismatching) that reports and never repairs; nothing is generated, spawned, cleared or healed at load
  ([PHYSICAL_LIFECYCLE § 16.3](PHYSICAL_LIFECYCLE.md#163-the-registry-across-load-corrected-by-the-phase-31-runtime-qa-pass), P3-INV-037).
- **Proven by.** `Phys31Qa.Fix1_*` and `Phys31Qa.Fix7_*` (headless, over real `Pawn` objects; five mutations that reintroduce the defect are caught); the owner's `RT-PHYX-010` A and B, each with a reload and `010V`, on a fresh save.

## R-47 · A first projection borrows a special-purpose kind (Phase 3.1, observed)
- **Failure modes.** A global capability ranking admits boss, royal, ancient and cultist kinds as ordinary contractors; later gameplay (resurrection, titles, ideology) is then genuinely wrong for the person.
- **Mitigation.** The encounter faction's own generic member pool only, admitted by what the kind carries (P3-INV-038); no named list; a faction with no generic member is not chosen and the projection aborts before anything is bound; a pawn that already exists is never sanitized.
- **Proven by.** `Phys31Qa.Fix2_*`; `RT-PHYX-011` reports the allowed provenance on the owner's mod list.

## R-48 · A person's role is unknown at first materialization (Phase 3.1, observed)
- **Failure modes.** The derivation returned `Unset` for an individual with no `ContractorProfile` (a Fixer), so the first projection was unconstrained and `RT-PHYX-001` failed.
- **Mitigation.** The derivation is total for an embodied individual; the compatibility pass stores it once at bootstrap and load; `Plan` stays the safety net; never overwritten, never reconstructed for a bound person (P3-INV-039).
- **Proven by.** `Phys31Qa.Fix3_*`; `RT-PHYX-001`.
