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
| R-16 | Master design brief unavailable during Phase 0 | Medium | High | before Phase 1 |
| R-17 | Stories feel like transactions (narrative under-investment) | High | Medium | Phases 1–4 playtests |
| R-18 | Claim accounting inaccurate (non-caravan departures) | Low | Medium | Phase 1 |
| R-19 | RimWorld 1.6.x updates changing internals we rely on | Medium | Medium | every release |
| R-20 | Economic exploits (refund loops, cancel abuse) | Medium | Medium | Phases 1–2 |

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
- **Proven by.** Phase 2 soak (60 orgs × 20 years). Phase 3 soak (150 stored pawns + 5
  deployments), with an A/B TPS comparison.

## R-15 · Registry-quest custody unworkable at runtime
- **Failure modes.** Suspension causes oddities: pawns frozen in bad states, or vanilla code
  that assumes reserved pawns belong to real quests. Quest mods break it.
- **Mitigation.** Spike S9 before Phase 3 content. A documented fallback (`KeepForever` plus
  factionless storage) and a contingency patch C-1 with its analysis done in advance
  ([RIMWORLD_INTEGRATION § 3.3](RIMWORLD_INTEGRATION.md#33-contingency-patches-analysed-not-adopted)).
- **Proven by.** S9.

## R-16 · Master design brief unavailable during Phase 0
- **Failure modes.** An architectural assumption conflicts with the canonical design.
- **Mitigation.** Assumptions are listed explicitly
  ([ARCHITECTURE § 14](ARCHITECTURE.md#14-assumptions-pending-master-brief-review)). The
  architecture keeps content and tuning replaceable. Review against the brief is required
  before Phase 1.
- **Proven by.** The pre-Phase-1 review.

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
  Invalidation abuse by toggling mods.
- **Mitigation.** Cancellation refunds decay with elapsed time. The seed is per request, so a new
  request is a new roll, but it costs a new fee. Invalidation refunds only for defs missing at
  load or resolution, and a re-added mod's references resolve again. Determinism prevents
  reload fishing on existing requests.
- **Proven by.** Phase 1–2 balance passes.
