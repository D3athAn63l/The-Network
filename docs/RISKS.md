# Technical Risk Register

> The highest technical risks, how each is mitigated, and the phase in which it must be proven
> **experimentally** (by a spike or a soak test), not just argued. Severity and likelihood are
> judged after mitigation is designed but before it is proven.
> Scale: **Severity** Critical / High / Medium / Low · **Likelihood** High / Medium / Low.

| ID | Risk | Sev. | Lik. | Proven in |
|---|---|---|---|---|
| R-01 | Abstract ↔ physical pawn identity (duplication, resurrection, lost identity) | Critical | Medium | Phase 3 (S9, S11, S17) |
| R-02 | Save/load during physical contractor encounters | High | Medium | Phase 3 (S9, S11, S14) |
| R-03 | Vanilla quest and site cleanup semantics | High | Medium | Phase 1 (S1, S2), Phase 3 (S9) |
| R-04 | Third-party mods destroying or modifying generated sites | Medium | Medium | Phase 1 (S2), ongoing |
| R-05 | External Def removal | High | High | Phase 1 (S7) |
| R-06 | Optional DLC APIs (Odyssey, Royalty) | Medium | Medium | Phase 7 (S15, S16) |
| R-07 | Duplicate rewards or cargo | High | Low | Phase 1 (A2), Phase 2 (S13) |
| R-08 | Deterministic background outcomes | High | Medium | Phase 1 (S8), Phase 2 soak |
| R-09 | Unbounded history and save growth | High | Medium | Phase 1 (S18), Phase 2 and 6 soaks |
| R-10 | Faction relation changes during active contracts | Medium | High | Phase 2 |
| R-11 | Contractor pawns recruited or captured by the player | High | High | Phase 3 |
| R-12 | World-object deletion | Medium | Medium | Phase 1 (S1, S2) |
| R-13 | Cross-mod quest interactions | Medium | Medium | Phase 3 (S9) |
| R-14 | Performance of large, long-running contractor populations | High | Low | Phase 2 soak, Phase 3 soak |
| R-15 | Registry-quest custody unworkable at runtime | High | Medium | Phase 3 (S9) |
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

---

## R-01 · Abstract ↔ physical pawn identity
- **Failure modes.** The same character instantiated twice. A dead character reappears. A
  character's pawn is discarded by vanilla GC. A pawn is reused by vanilla in a random raid.
  Another mod copies a pawn along with our tags.
- **Mitigation.** Invariants I-1 to I-10 ([ABSTRACT_PHYSICAL_LIFECYCLE § 3](ABSTRACT_PHYSICAL_LIFECYCLE.md#3-invariants)):
  bind-once, a reverse map, a deployment exclusivity check, a dead-is-final guard, handlers that
  act only on bound objects, reconciliation from actual pawn state, and registry reservation
  (no GC, no redress). A validator checks one-to-one maps on every load.
- **Proven by.** S9 (reservation effects), S11 (holder), S17 (copies); Phase 3 soak with forced
  scenarios (dev actions).

## R-02 · Save/load during physical encounters
- **Failure modes.** Deployment state and pawn locations disagree after load. Signals are lost
  (receivers are not persisted). Reservations are lost.
- **Mitigation.** All state is persisted. Pawns are saved by vanilla wherever they are. The
  first-tick reconciliation re-derives fates. The registry reserved set is rebuilt from our
  stores. Signals are re-registered. The watchdog backstops missed signals.
- **Proven by.** Phase 3 test matrix: save/load at each deployment state × each anchor type.

## R-03 · Vanilla quest and site cleanup
- **Failure modes.** Vanilla removes a site or map at an unexpected time. Timeout destroys the
  site while the player is en route. Our registry quest is cleaned up or dropped.
- **Mitigation.** No Network state is owned by vanilla quests. Sites use vanilla semantics, which
  players already understand, and our comp mirrors each lifecycle callback. The registry quest
  has a non-null root, is `Ongoing` and never ends. On load, a missing registry quest is
  recreated.
- **Proven by.** S1, S2 (sites) and S9 (registry).

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
- **Proven by.** S9 with a sample of popular quest-related mods.

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
- **Mitigation.** Spike S9 before Phase 3 content. A documented fallback (`KeepForever` plus
  factionless storage) and a contingency patch C-1 with its analysis done in advance
  ([RIMWORLD_INTEGRATION § 3.3](RIMWORLD_INTEGRATION.md#33-contingency-patches-analysed-not-adopted)).
- **Proven by.** S9.

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
  changes the provider or the landing; a destroyed provider strands a leg forever or causes a jump.
- **Mitigation.** Same-layer, valid, passable destinations only; ground always tried first, per
  candidate; operation travel only; one committed two-way plan (hub + landing, reused for the pickup);
  only the two crossing ends are saved and the crossing is a single step, so a rebuilt route is the
  journey that was left; provider loss reconciles from current truth (another provider, on foot, or
  `Blocked`); a source scan forbids spawning and money calls in spatial code; the provider fact is
  derived from the game's `TechLevel`, never a named faction (ADR-045, [SPATIAL § 6.1](SPATIAL.md)).
- **Proven by.** `Charter.A`–`N`, `Spatial.CharterBridgesDisconnectedGeography`, the charter soak world
  and `Soak.ArchipelagoCharterStress` (charters under every invariant, provider settlements coming and
  going); spike S20 in game (not run yet).
