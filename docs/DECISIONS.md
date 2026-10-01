# Architectural Decision Records

> A lightweight ADR log. Each record gives the decision, the alternatives we rejected, and the
> consequences. Status values: **Accepted** (binding), **Conditional** (binding unless the named
> spike fails), **Superseded**, and **Deviation** (a technically necessary departure from the
> master design; binding only once the owner has reviewed it). Add new records at the end. Never
> renumber.
>
> Authority: the master design document defines product and gameplay intent; the architecture
> documents define technical implementation. Where they conflict, the design takes precedence
> unless a reviewed ADR explicitly records a necessary deviation.

| ADR | Title | Status |
|---|---|---|
| 001 | Single WorldComponent as the persistence root | Accepted |
| 002 | Lazy bootstrap on the first tick | Accepted |
| 003 | Own integer ID space with typed wrappers | Accepted |
| 004 | Typed external references, resolved silently | Accepted |
| 005 | Actors by composition (capability components) | Accepted |
| 006 | Contractor organizations are not RimWorld factions | Accepted |
| 007 | Contract = core + composable parts; Operation separate; branching by new contracts | Accepted |
| 008 | Intel separated into Interest, Perception and Truth | Accepted |
| 009 | Synchronous event bus + bounded journal + persisted jobs | Accepted |
| 010 | Tiered history retention + incremental summaries | Accepted |
| 011 | Facts vs awareness from day one | Accepted |
| 012 | Determinism via per-entity seeds, private PRNG and commit points | Accepted |
| 013 | Character identity tiers and custody invariants | Accepted |
| 014 | Off-map pawn custody via a hidden registry quest | Conditional (S9) |
| 015 | Sites = vanilla Site + vanilla parts + injected WorldObjectComp | Conditional (S1, S2, S19) |
| 016 | Item catalog is a session cache; overrides in ModSettings | Accepted |
| 017 | Zero Harmony for Phases 1–3 | Accepted |
| 018 | Optional DLC via runtime gating inside one assembly | Accepted |
| 019 | Single integer save version + ordered migrations + quarantine | Accepted |
| 020 | UI through read models and commands | Accepted |
| 021 | Sparse directed relationship edges with lazy decay | Accepted |
| 022 | Persisted due-tick scheduler with budget and stagger | Accepted |
| 023 | Temporary per-organization encounter factions | Conditional (S10) |
| 024 | Terminal entities are compacted; actors become tombstones | Accepted |
| 025 | Network lifecycles are not vanilla quests | Accepted (owner review complete) |
| 026 | Source mod is contextual evidence, not ownership | Accepted |
| 027 | The deposit is committed cost, normally lost on failure | Accepted |
| 028 | Contractor capability is separate from NPC contractor simulation | Accepted |
| 029 | Fixers are first-class actors; quotes are assembled from actor contributions | Accepted |
| 030 | Global cast in ModSettings, snapshotted per world, with its own settings version | Accepted |
| 031 | One Intel request, many leads (search rounds) | Accepted |
| 032 | Network access requires a usable Comms Console (Phase 1) | Accepted |

---

### ADR-001 · Single WorldComponent as the persistence root
- **Decision.** All Network state lives under one `NetworkWorldComponent`, split into stores in
  a fixed order.
- **Rejected.** A *GameComponent*: world data belongs with the world, a second component doubles
  the removal errors, and there is no ordering benefit. *Several WorldComponents per subsystem*:
  more removal errors, cross-component ordering problems, no shared versioning. *Data on vanilla
  objects* (comps on pawns and sites): scattered, hard to version, and lost with the object.
- **Consequences.** One version number and one load pipeline. Removal costs one error. The root
  must stay thin and delegate to stores.

### ADR-002 · Lazy bootstrap on the first tick
- **Decision.** `FinalizeInit` rebuilds runtime state only. Bootstrap and first-load
  reconciliation run on the first `WorldComponentTick`.
- **Context.** For new worlds, `World.FinalizeInit(false)` runs during world generation
  (`WorldGenerator.cs:67`), before the colony exists.
- **Rejected.** Bootstrapping in `FinalizeInit` (world-gen ordering hazards); a GameComponent's
  `StartedNewGame` (see ADR-001).
- **Consequences.** UI read models must tolerate "not yet started" for the fraction of a second
  before the first tick.

### ADR-003 · Own integer ID space with typed wrappers
- **Decision.** One persisted `nextId` counter. Typed readonly struct wrappers per entity kind.
  IDs are never reused.
- **Rejected.** `UniqueIDsManager` (closed; it would need patching), GUIDs (bloat, unreadable),
  object references (fragile), per-kind counters (no cross-kind uniqueness).
- **Consequences.** Merges and successors always get new IDs, linked by lineage. `EntityRef`
  can reference any kind.

### ADR-004 · Typed external references, resolved silently
- **Decision.** `DefRef` (defName string), `FactionRef` (loadID), `WorldObjectRef` (ID),
  `TileRef` (PlanetTile + layer def), and `PawnRef` (the only `Scribe_References` use). Each has
  a snapshot and a defined missing-target policy.
- **Context.** `Scribe_Defs` logs one error per missing def reference
  (`ScribeExtractor.cs:56`). Missing factions are removed on load (`FactionManager.cs:85`).
- **Rejected.** `Scribe_Defs` and `Scribe_References` everywhere (error cascades when mods are
  removed).
- **Consequences.** Resolution caches and validators are required. History stays readable after
  removals.

### ADR-005 · Actors by composition (capability components)
- **Decision.** `NetworkActor` has a kind, a status, bindings and a list of capability
  components (Contractor, IntelSource, Issuer, Sponsor, Introducer, …). Services decide
  "can do X now".
- **Rejected.** An inheritance hierarchy (Contractor : Actor, Broker : Actor): a retired
  contractor becoming a broker would need identity migration. A god object with nullable
  fields: unbounded growth.
- **Consequences.** Retirement transformation, the player becoming a contractor and successors
  are all "add or remove a component". Persisted component type names are frozen.

### ADR-006 · Contractor organizations are not RimWorld factions
- **Decision.** Orgs exist only in Network data. RimWorld factions take part through lazily
  created `FactionProxy` actors. Physical presence uses encounter factions (ADR-023).
- **Rejected.** A permanent faction per org (the faction count grows, permanent factions cannot
  be removed, world generation and other mods assume a fixed faction set, and every faction
  carries relations with every other). A single shared hidden faction (no per-org hostility).
- **Consequences.** Org membership is never inferred from `pawn.Faction`. Goodwill is mirrored
  into relation edges at reconciliation.

### ADR-007 · Contract = core + composable parts; Operation separate; branching by new contracts
- **Decision.** A shared contract core (identity, kind, status, parties, lineage) plus parts
  (Terms, Confidentiality, Objectives, Offers, Progress, Outcome). Execution lives in
  `Operation`. Terminal contracts are immutable. Continuation and inheritance create linked new
  contracts.
- **Rejected.** A class per contract flavour (duplicated lifecycles). A single class with
  flavour-specific nullable fields (a god object). Reopening terminal contracts (lost audit
  trail, confusing history).
- **Consequences.** Bidding, inheritance, retries and black contracts need no schema change.
  Kind rules live in `NetworkContractKindDef`.

### ADR-008 · Intel separated into Interest, Perception and Truth
- **Decision.** `IntelRequest` (the player's interest), `Lead` (the reported perception, with
  reliability), `Opportunity` (world truth).
- **Rejected.** A single "IntelQuest" object that holds the loot (it would assume a guaranteed
  loot quest, and would prevent misinformation, competitors and no-lead outcomes).
- **Consequences.** Rumors and wrong leads are data, not special cases. Opportunities can come
  from non-Intel sources.

### ADR-009 · Synchronous event bus + bounded journal + persisted jobs
- **Decision.** Typed events dispatched synchronously in a fixed consumer order, with breadth-first
  cascades. The journal is bounded and never replayed. Deferred reactions are scheduler jobs.
- **Rejected.** A persistent outbox with replay (complex, needs idempotency keys everywhere).
  Direct service-to-service calls (coupling; six systems rediscovering the same fact). A
  generic enterprise message bus (over-engineering).
- **Consequences.** No double application after load, by construction
  ([EVENTS_AND_HISTORY § 1.4](EVENTS_AND_HISTORY.md#14-no-double-application-after-save-and-load)).
  Consumers must be fast and must not generate content inline.

### ADR-010 · Tiered history retention + incremental summaries
- **Decision.** Minor events become counters only. Notable, Major and Legendary events become
  records with tiered retention and caps. Summaries are updated O(1) per event, and behaviour
  reads summaries only.
- **Rejected.** An unbounded event log. Periodic re-aggregation from the log (history rescans).
- **Consequences.** Save size stays bounded. Summaries must be migrated with care (they can be
  rebuilt from records, but only approximately).

### ADR-011 · Facts vs awareness from day one
- **Decision.** Every history record carries `Awareness` (scope, knowers, perpetrator and
  principal knowledge). Beliefs come later as a separate store.
- **Rejected.** "Everything is public" (it would need a schema rewrite to add secrecy,
  witnesses and rumors later).
- **Consequences.** Phases 1–4 set simple defaults. Phase 5+ features plug in without
  migrating records.

### ADR-012 · Determinism via per-entity seeds, private PRNG and commit points
- **Decision.** `entity.seed = Hash(networkSeed, id, salt)`. A private `NetRng` for Network
  decisions. Vanilla generators run only under `Rand.PushState`, and their results are
  committed immediately. Outcomes are persisted at defined commit points.
- **Rejected.** `Verse.Rand` directly (global, not persisted, disturbed by unrelated calls).
  Re-deriving outcomes on load (breaks when mods change). Rerolling on every evaluation
  (save-scum friendly and UI-sensitive).
- **Consequences.** Reloading cannot reroll background outcomes. A dev reroll exists
  (`rerollNonce`).

### ADR-013 · Character identity tiers and custody invariants
- **Decision.** Anonymous headcount, then a Known Character record, then a bound pawn. One pawn
  per character, ever. The Network never destroys or discards pawns. Reconciliation from actual
  pawn state is the source of truth.
- **Rejected.** Persisting all members as pawns (TPS and save cost). Regenerating characters
  from a seed on demand (mod-list-dependent, breaks identity). Tracking by signals alone
  (missed signals would corrupt state).
- **Consequences.** A promotion policy and caps are needed. Released generic pawns become
  vanilla's concern.

### ADR-014 · Off-map pawn custody via a hidden registry quest (Conditional: S9)
- **Decision.** One hidden, never-ending Network quest with a Network `QuestScriptDef` root. Its
  part reserves every pawn in Network custody.
- **Context.** `KeepForever` pawns remain `WorldPawnSituation.Free` and can be redressed into
  random raids (`PawnGenerator.GetValidCandidatesToRedress`). Quest reservation gives GC
  protection, redress exclusion, suspension (needs, health and aging skipped; mothballed once normalized), and kill and discard notifications. A
  null quest root is dropped on load (`QuestManager.cs:181`), so the root def is mandatory.
- **Rejected.** `KeepForever` alone (redress, ticking cost). Holding pawns in our own
  `ThingOwner` (outside vanilla pawn systems, and vanishes with the mod). Harmony patches on GC
  and redress (hot-path adjacent, unnecessary).
- **Fallback.** `KeepForever` plus factionless storage; then contingency patch C-1
  ([RIMWORLD_INTEGRATION § 3.3](RIMWORLD_INTEGRATION.md#33-contingency-patches-analysed-not-adopted)).
- **Consequences.** Stored pawns are frozen; catch-up happens at materialization (S12). Mod
  removal adds about 2 errors unless the save is prepared for removal.

### ADR-015 · Sites = vanilla Site + vanilla parts + injected WorldObjectComp (Conditional: S1, S2, S19)
- **Decision.** Use `WorldObjectDefOf.Site` and vanilla `SitePartDef`s (`ItemStash` + a threat
  part). Network-made Things go in `SitePart.things`. Callbacks come through
  `WorldObjectComp_NetworkSite`, which an XML patch adds to the vanilla `Site` def.
- **Context.** Comp data is stored inline and silently ignored when the comp is no longer in the
  def (`WorldObject.ExposeData` → `InitializeComps`). A missing `SitePartDef` nulls parts and
  can break sites.
- **Rejected.** A custom `WorldObjectDef` or `Site` subclass (removal errors, a broken world
  object). A custom `SitePartDef` for the loot (removal errors, parts nulled). Vanilla quests
  owning the site (lifecycle ownership).
- **Consequences.** Claim accounting is approximate (PostCaravanFormed + sampling). An exact
  pre-removal hook would need a Network SitePartDef (preferred) or patch C-3.
- **Also conditional on S19.** The objective of a site is acquisition, not extermination
  (master § 17). If the vanilla stash-plus-threat composition turns out to force the player to
  kill every defender before useful loot can leave, Phase 1 switches to another vanilla
  composition or a minimal Network site part; the rest of this decision stands.

### ADR-016 · Item catalog is a session cache; overrides in ModSettings
- **Decision.** Build lazily once per session from DefDatabase. Do not serialize it. Per-item
  overrides are stored by defName in ModSettings and kept even for unknown defs. Only technical
  impossibility is non-overridable; every heuristic yields to `Allowed`, and runtime generation
  failure is the final safety net ([COMPATIBILITY § 2.3](COMPATIBILITY.md#23-eligibility-heuristics-conservative)).
- **Rejected.** Serializing the catalog (stale data, bloat). Rebuilding per query (repeated
  scans). Per-save overrides as the default (the user's taste is global).
- **Consequences.** A mod-list change needs a restart (as RimWorld already requires). Classification is
  explainable through reason codes.

### ADR-017 · Zero Harmony for Phases 1–3
- **Decision.** No patches. Observation through signals, quest parts, world-object comps and
  reconciliation. Contingency patches are pre-analysed and not adopted.
- **Rejected.** Convenience patches on `Pawn.Kill`, GC, redress, site removal and goodwill.
- **Consequences.** The mod does not even require Harmony until a spike forces an adoption.

### ADR-018 · Optional DLC via runtime gating inside one assembly
- **Decision.** DLC code lives in `Compat.<Dlc>` modules, activated by `ModsConfig` gates, using
  types in `Assembly-CSharp`. DLC defs are resolved with `MayRequire` or `GetNamedSilentFail`.
- **Rejected.** Separate assemblies per DLC (load-order and packaging complexity for no gain,
  since the types always exist).
- **Consequences.** Every DLC code path must sit behind a gate. `TileRef` records the layer def
  for Odyssey safety.

### ADR-019 · Single integer save version + ordered migrations + quarantine
- **Decision.** `NetworkSaveVersion` (int). Legacy field reads during load, semantic migrations
  after load, per-entity quarantine, per-subsystem degradation.
- **Rejected.** Vanilla `BackCompatibility` (private chain). Semantic versioning of data (no
  benefit). Wiping data that fails to migrate.
- **Consequences.** Fixture-based migration tests are mandatory. Persisted type names are frozen.

### ADR-020 · UI through read models and commands
- **Decision.** Immutable view objects rebuilt on `StateVersion` change. All mutation goes
  through `NetworkCommands`, with `CanX` reason codes.
- **Rejected.** Windows that mutate stores directly. A backend shaped around specific windows.
- **Consequences.** The UI can be rewritten freely. The same commands serve dev actions and tests.

### ADR-021 · Sparse directed relationship edges with lazy decay
- **Decision.** A dictionary keyed by the directed actor pair. Neutral defaults are not stored.
  Decay is applied on read and write.
- **Rejected.** Dense matrices (N² growth). Periodic decay sweeps (wasted work). Symmetric edges
  (attitudes are asymmetric).
- **Consequences.** Every edge read goes through `Relations.Get`, which applies decay.

### ADR-022 · Persisted due-tick scheduler with budget and stagger
- **Decision.** A min-heap of `(dueTick, seq)` jobs with an O(1) idle check, a per-tick
  job/time budget and seeded phase offsets for periodic work. Entities hold their own due ticks,
  and jobs are an index rebuilt by validation.
- **Rejected.** Per-tick loops over entities. `IsHashIntervalTick` polling per entity (still
  O(N) per tick). Vanilla incidents for timing (storyteller-coupled).
- **Consequences.** Job kinds are persisted strings (versioned, and unknown ones are dropped).

### ADR-023 · Temporary per-organization encounter factions (Conditional: S10)
- **Decision.** When physical, an org's pawns belong to a temporary faction created for that org
  and reused while it exists. Vanilla removes it when unused.
- **Rejected.** See ADR-006.
- **Consequences.** A faction may briefly appear in the factions list (hidden if S10 allows).
  Goodwill is mirrored into relation edges so it survives recreation.

### ADR-024 · Terminal entities are compacted; actors become tombstones
- **Decision.** Terminal contracts, operations, intel requests and deployments are deleted after
  1 in-game year (history keeps the story). Ended actors and dead characters become minimal
  tombstones and are never deleted.
- **Rejected.** Keeping everything (unbounded saves). Deleting actors (dangling history
  references).
- **Consequences.** Every reference to a compacted entity resolves to "archived" or a
  tombstone, and the UI handles both.

### ADR-025 · Network lifecycles are not vanilla quests
- **Design.** Master § 87: "Where practical, use Quest, QuestPart, QuestScriptDef, WorldObject,
  Site, SitePart, Faction, Incident, Letter, Caravan/transport systems … Mission generator should
  provide appropriate Slate/context values and let vanilla systems handle downstream gameplay
  where possible."
- **Decision.** Use every system on that list **except** Quest/QuestGen/Slate as the owner of
  Network lifecycles. Opportunities are vanilla Sites with vanilla parts; letters, factions,
  caravans and transport are vanilla. Quests are used only as the hidden registry that anchors
  pawn custody (ADR-014). Incidents are deferred (the Network is not a storyteller, master § 55).
- **Status.** **Accepted.** Recorded first as a deviation for review; the owner's review is
  complete (see Owner review below).
- **Why this is technically necessary.** A quest would *own* the lifecycle: vanilla quest state,
  expiry, accept/decline and cleanup would decide when a site or its pawns disappear; the Network
  lifecycles (bidding, Troubled, inheritance, lineage, partial claims) have no vanilla quest
  equivalent; other mods iterate and modify quests; and a Network quest root or part missing
  after removal is a vanilla load error on every such quest
  ([RIMWORLD_INTEGRATION § 2.3](RIMWORLD_INTEGRATION.md#23-quest-questpart-questscriptdef-slate-questgen--limited-reuse-registry-quest-phase-3)).
- **What is kept from the design's intent.** Vanilla map generation, combat, AI, factions and
  transport handle all downstream gameplay. An optional, read-only "Quests tab mirror" can
  present an opportunity as an informational quest later without owning it.
- **Owner review (concluded).** Master § 87 says "where practical", and quest ownership is not a
  practical lifecycle match for bidding, partial recovery, inheritance, Troubled states,
  continuation, contractor history or mod-removal robustness. Vanilla downstream gameplay is
  still reused extensively: Site, SitePart, Faction, WorldObject, map generation, pawn combat and
  AI, caravans, transport and letters. This is therefore **not** a violation of the master
  design, and no further quest-ownership investigation is required before Phase 1.
- **Consequences.** Binding. The registry quest for custody (ADR-014) is unaffected and stays
  conditional on S9.

### ADR-026 · Source mod is contextual evidence, not ownership
- **Decision.** Opportunity generation resolves plausible world context through a source
  resolver inside `OpportunityService`. An item's source package is one signal among several
  (with faction stance, defeated and hidden state, tech level, type, trade capability,
  geography, archetype, world state, source knowledge and optional adapter hints). The master
  § 15 hierarchy is the default prior, the draw is seeded, and "no credible source" is a valid
  result ([ARCHITECTURE § 6.14.1](ARCHITECTURE.md#6141-opportunity-source-and-context-resolution)).
- **Rejected.** Per-item or per-mod rules ("if `BOR_Tenebrite` then the Tenebral faction").
  Treating a same-package faction as an automatic enemy or owner. Requiring any content mod.
- **Consequences.** Tenebrite and "Weirdium" follow the same code. Compatibility adapters can
  only add hints. A friendly same-package faction is a candidate owner or trader, never a
  forced enemy.

### ADR-027 · The deposit is committed cost, normally lost on failure
- **Decision.** A procurement deposit (share and schedule from the brokering Fixer's deposit
  policy; master § 21 suggests half) pays for preparation, logistics, transport, scouting,
  equipment, supplies, labour and accepted risk. It is normally lost on in-world failure,
  catastrophic loss included, and on fraud unless later gameplay recovers it. Cancellation after
  commitment forfeits part or all of it by the terms. Technical invalidation refunds it in full.
  Insurance, when offered by the Fixer, recovers part of it and is never risk-free. **A
  contractor that disappears before work starts has no global rule:** the Fixer's replacement
  policy and the terms decide among a successor, a replacement, a full or partial refund,
  credit, an insurance claim, renegotiation or forfeit
  ([STATE_MACHINES § 4.2](STATE_MACHINES.md#42-money-rules)).
- **Rejected.** Refunding the deposit when the contractor is wiped out or dissolved ("nobody is
  left to keep it"): that made catastrophic failure free and contradicted master § 21 and § 23.
- **Consequences.** Failure hurts financially and still creates content through consequences
  (last known location, rescue).

### ADR-028 · Contractor capability is separate from NPC contractor simulation
- **Decision.** Three components: `ContractorProfile` (can act as a contractor; on an
  Organization, an Individual Solo or the PlayerProxy), `ContractorSimulation` (abstract off-map
  state of an NPC contractor, Solo or organization) and `OrganizationProfile` (roster,
  leadership, wounded buckets, recruitment, succession; NPC organizations only)
  ([DATA_MODEL § 6](DATA_MODEL.md#6-contractor-actors-capability-npc-simulation-organization)).
  The player's execution state is read from the real colony through the Integration layer.
- **Rejected.** One overloaded `ContractorProfile` holding both the capability and the
  organization simulation: it would give the player a fake roster, wounds, equipment tier and
  morale (two truths about the colony) and force a headcount model on Solos.
- **Consequences.** Made now, while no runtime persistence exists, so the persisted actor model
  never needs restructuring. Operational capability and public fame are separate axes, and
  mobility (`MobilityProfile`, capability tags, never DLC ownership flags) is a third.
  **Providing work and issuing work are independent**: `ContractorProfile` never grants issuing,
  `IssuerProfile` does, and contractor organizations may hold both. A Fixer brokers between an
  issuer and contractors and never replaces the issuer.

### ADR-029 · Fixers are first-class actors; quotes are assembled from actor contributions
- **Decision.** Fixers and brokers are persistent actors (normally `Individual`s) with a
  `FixerProfile` of bands and replaceable policy keys (fee, brokerage, deposit, insurance, quote,
  replacement, reach), usually alongside an `IntelSourceProfile`. Not every Intel source is a
  Fixer. Procurement quotes keep each component with the actor that contributed it (contractor
  bid, Fixer fee and terms) and show the client one price
  ([DATA_MODEL § 4.3, § 9](DATA_MODEL.md#43-fixers-and-brokers-fixerprofile)).
- **Rejected.** Fixers as flavour text; a single `marketValue × multiplier` price; frozen
  formulas; raw stat sheets shown to the player.
- **Consequences.** Intel fees, speed and reliability, procurement terms, deposits and insurance
  vary by Fixer. Fixers have history, relationships, reputation and knowledge like any actor.

### ADR-030 · Global cast in ModSettings, snapshotted per world, with its own settings version
- **Decision.** The recurring cast (contractor and Fixer templates with stable GUID template
  IDs and `Generated` / `Custom` provenance) lives in `ModSettings`. Each world snapshots it at
  bootstrap and instantiates actors from its own snapshot with world-local ids. Runtime outcomes
  are never written back. Regeneration replaces only generated entries. The settings data has its
  own `NetworkSettingsVersion` and migrations
  ([DATA_MODEL § 18](DATA_MODEL.md#18-global-network-cast-modsettings-cross-save),
  [SAVE_AND_MIGRATION § 11](SAVE_AND_MIGRATION.md#11-global-cast-settings-networksettingsversion)).
- **Rejected.** Cross-save runtime history (one colony's events leaking into another); a cast
  generated per world only (no recurring characters); names as identity; reusing
  `NetworkSaveVersion` for settings.
- **Consequences.** The same Dead Red can live different lives in different saves. Global edits
  never rename, replace or resurrect actors in an existing save.

### ADR-031 · One Intel request, many leads (search rounds)
- **Decision.** An `IntelRequest` runs in rounds and can deliver zero, one or many leads over its
  lifetime. After a lead the search may continue, wait for the player, or end, as the source's
  continuation policy says; pursuing a lead never stops the search
  ([STATE_MACHINES § 1](STATE_MACHINES.md#1-intel-request)).
- **Rejected.** One lead per request, with "keep waiting" meaning a new request (it contradicts
  master § 11); a global continuation fee.
- **Consequences.** Each round has its own seed stream and commit point; fees are recorded per
  round.

### ADR-032 · Network access requires a usable Comms Console (Phase 1)
- **Decision.** Every outgoing Network command requires a spawned, usable vanilla
  `Building_CommsConsole` (`CanUseCommsNow`) on a player home map, checked in `Commands.CanX`
  through `CommsAccessAdapter`. Reading the Network needs none. Nothing in progress is suspended,
  cancelled or destroyed when the console is lost.
- **Rejected (for now).** Designing alternative communications (portable radios, broker visits,
  orbital links, other mods). They may come later as additional access providers.
- **Consequences.** A colony without a powered console cannot start or answer Network business
  until it has one again.

### ADR-033 · Procurement quotes are frozen per offer; losing bids are dropped at close
- **Decision.** Each offer stores the contractor's own components and the Fixer's components
  (fee, coordination, market access, contingency, market-floor top-up) with their contributors,
  the final price, deposit and balance, the insurance offer, the replacement and refund policy
  keys, and the validity. It is never recomputed. When the contract closes, the offers that were
  not accepted are dropped; the accepted quote stays with the contract until compaction.
- **Rejected.** Recomputing a quote when the UI opens (it would draw randomness and drift);
  keeping every losing bid forever (save growth for no story value).
- **Consequences.** History and the economy can see who charged what. Save growth stays bounded
  (about 2.5 MB after 18 in-game years of heavy procurement in the soak).

### ADR-034 · Money moves only when the goods can land
- **Decision.** Delivery plans a drop spot first, with no side effect. The balance is charged only
  when a plan exists; then the pods launch. With no home map or no drop spot, the contract goes to
  Hold (daily retries) and fails after the kind's limit (15 days), refunding any paid balance.
- **Rejected.** Charging at the return checkpoint and refunding on failure (it charges players who
  have no home at all); dropping pods on a random cell (roof punching, lost goods).
- **Consequences.** No contract can be stuck, and no player pays for goods that never arrive.

### ADR-035 · Phase-2-safe payment default
- **Decision.** If the client cannot pay the balance, the contractor applies its doctrine and the
  relationship. It either holds the goods (AwaitingPayment, pay later, 7-day grace) or hands over
  what the deposit covered (partial handover, also the grace default).
  `Payment.Defaulted` hurts the relationship.
- **Rejected (for now).** The debt option: it needs Obligations (Phase 5). Hostile collection is
  Phase 5+.
- **Consequences.** A later phase adds the debt branch without changing the states.

### ADR-036 · Consequence Engine v0 fires at dispatch and builds content in a job
- **Decision.** One rule (Last Known Location). It fires when `Contract.Failed(CatastrophicLoss)`,
  `Contractor.Missing` or `Contractor.Stranded` is dispatched, seeded by the event's sequence
  number, with guards: one per contract, at most six active follow-ups, one per day, lineage
  depth ≤ 4. The pending rule is persisted in the `consequences` store; a job generates the site
  with the Phase 1 machinery from its own seed.
- **Rejected.** Building the site inside the event consumer (world mutation inside dispatch, and
  no persisted commit point); contractor pawns, survivors, captives or bodies (Phase 3).
- **Consequences.** A reload between firing and generation gives the same site. The deposit is not
  refunded because a follow-up exists.

### ADR-037 · The client learns an operation's result when the contractor reports it
- **Decision.** The resolver runs once, at the resolve checkpoint, and commits the outcome.
  The client sees it only at the return checkpoint, through the contract (delivery, partial
  result, failure) or earlier through a report the contractor sends (delay, Troubled, "worse
  than expected"). `Operation.Resolved` is Minor and not shown in history.
- **Rejected.** A Notable history record at resolution (it duplicates the contract outcome and
  reveals it early).
- **Consequences.** A deviation from the catalog's default importance, recorded in
  [EVENTS_AND_HISTORY § 2](EVENTS_AND_HISTORY.md#2-event-catalog).

### ADR-038 · Contract money is a typed ledger; a replacement is an internal transfer
- **Decision.** Every contract money record has a direction and a purpose. Only `PlayerPaid` and
  `PlayerRefunded` move real silver. A replacement (a Fixer handing a lost job to a new contractor)
  moves the parent's whole remaining position to the child as a `TransferOut` on the parent and a
  matching `TransferIn` on the child: same amount, same purpose (deposit, premium, insurance
  premium, …), each naming the other contract. Policies read `Funding(purpose)` = charged + carried in
  − carried out; a technical invalidation refunds `NetFunding()` = all funding − already returned.
  No accounting reads a note string. The new contractor is not paid again for carried money.
- **Invariant.** Across a lineage, external charges − external refunds = the player's real net
  silver, and transfers sum to zero (in total and per purpose). Tests A–H and the soak check it.
- **Rejected.** Counting transfers as payments (a parent would stay refundable for money it gave
  away, and a void would refund twice); one `Transferred` direction on both sides (the parent's
  record cannot be told from the child's).
- **Consequences.** A replaced parent holds nothing refundable; the child recognises the carried
  deposit everywhere (success balance, cancel, insurance, pro-rating, handover, void).

### ADR-039 · One live job per named person; capacity is rechecked at acceptance
- **Decision.** Which named people (KnownCharacters) are out is derived from the contractor's live
  operation commitments, never stored: checkout skips anyone on another live operation, so one
  person is in at most one abstract operation. `JobCapacity` counts the whole able roster (people
  out on jobs included), and acceptance refuses a quote whose bidder has filled its capacity since
  quoting (`BidderNowCommitted`: nothing charged, no operation, the offer stays open until it
  expires). A quote from a contractor already working says "alongside other work"; nothing is
  queued.
- **Rejected.** A global assignment system or pawn references (Phase 3); a job queue.
- **Consequences.** The soak counts zero commitments above capacity and zero people on two live
  jobs; about one acceptance in five in the soak now falls back to another quote.

### ADR-040 · A Last Known Location holds at most what was secured; only survivors report
- **Decision.** The goods left at a Last Known Location are bounded by the committed
  `outcome.secured` (between half and all of it; none when nothing was secured) and are the exact
  committed payload (def, stuff, quality). Unrelated extra loot is still allowed. Knowledge gained
  from an operation is committed only if someone came back: the killed, captured and missing carry
  nothing home.
- **Rejected.** Inventing goods at the site when none were secured; a Disaster that teaches a
  contractor with nobody left to report.
- **Consequences.** The site never contradicts the operation's result.

### ADR-041 · Abstract spatial continuity (implemented by Phase 2.5)
- **Status.** Recorded as a deferred design during Phase 2; **implemented by Phase 2.5**
  ([SPATIAL](SPATIAL.md)) with its philosophy unchanged. Concrete choices: ADR-042 to ADR-045.
- **Decision.**
  1. Abstract spatial truth is invisible to the player: no map marker, no route, no omniscience.
  2. `MobilityProfile` stays a CAPABILITY (range, speed, lift, transport), never a location.
  3. A future `SpatialState` is WORLD TRUTH: an approximate current `TileRef`, an optional
     destination `TileRef`, a coarse travel state or purpose, and the last update and journey timing
     it needs.
  4. Awareness is separate: spatial truth never reveals a contractor's position to the player by
     itself.
  5. Movement is coarse and lazy (scheduled or computed from timestamps when asked): no per-tick
     updates, no persistent WorldObjects, no visible routes or icons, no NPC caravan simulation.
  6. Spatial answers WHERE; operations answer WHAT.
  7. Last Known Locations, rescues and visits may later read it.
  8. Timing: a dedicated PR after Phase 2 is merged and validated at runtime, before spatially
     aware Phase 3 physicalization matters.
- **Rejected (still).** Tile occupancy indexes, intersection checks and travel corridors, relay
  behaviour, ambient visits, tracking, map icons, world objects, caravans, pawns, cross-layer travel.
- **Consequences.** Phase 2 shipped without spatial data. Phase 2.5 added it as save format 3, with a
  migration that invents no past and leaves running Phase 2 operations untouched.

### ADR-042 · Spatial movement is lazy catch-up over a rebuildable route
- **Decision.** Persist only world truth that cannot be reconstructed: one anchor tile, an optional
  committed destination, the journey's timing (start, last update, arrival) and the next ambient
  decision. Position is caught up proportionally to elapsed time toward the committed arrival, only
  when something needs it: the contractor's existing staggered daily upkeep, operation checkpoints,
  consequences, load. The route is a runtime cache rebuilt from the saved anchor to the saved
  destination; the exact path is softer truth than a committed outcome (a changed world may give a
  different path, never a teleport or a reroll). Failure is soft: the last valid anchor is kept and
  the journey is dropped (`Blocked`); contracts are never touched. World access goes through the
  `ISpatialWorld` port (RimWorld adapter; a synthetic grid for tests and the soak).
- **Amended by the Phase 2.5 correction pass.** (1) The remaining route is proven before any progress,
  the final arrival included; a destination that is still a valid tile is no evidence it can be
  reached, so passing `arrivalTick` alone never places a contractor there. (2) Approximate distance
  only discovers candidates; a destination is committed only when its real route, in steps, fits the
  time and range it must fit. (3) A committed arrival is never sooner than walking the route takes;
  a longer rebuilt route makes a journey later, never faster. (4) An ended contractor is frozen where
  it ended. (5) The last-resort anchor search is guaranteed: seeded probes, then a deterministic scan
  of the whole surface.
- **Rejected.** A per-tick mover or a new job per contractor; persisting routes, corridors or
  presence sets; WorldObjects or caravans as carriers; teleporting across impossible geography to
  keep coordinates tidy; arriving because the clock says so.
- **Consequences.** Idle contractors cost nothing extra; the 18-year soak adds about 127 KB to the save
  and zero teleports, pace violations or invalid states. Spatial may lag behind a squeezed operation
  timeline; it never catches up by moving faster.

### ADR-043 · Spatial conforms to the operation timeline; the incident feeds consequences
- **Decision.** A new operation commits a hidden plan at start: origin = the contractor's real anchor,
  a work region scaled to the travel time the committed ETA leaves, the return point, and later the
  incident. Departure is the Prep checkpoint, arrival the Arrive checkpoint, the return leg ends at
  the (possibly delayed) Return checkpoint; the resolver alone decides what happened. Trouble or
  disaster fixes the incident tile before any consequence reads it, and the Last Known Location is
  placed near it (bounded local search, else the Phase 2 placement). Quotes and ETAs are not changed
  by spatial in this phase. Operations loaded from a Phase 2 save keep their lifecycle and get no
  plan. An organization's concurrent job is a detachment: the main body's anchor does not move for it.
- **Amended by the Phase 2.5 correction pass.** Only a Troubled outcome suspends the return; an
  incident or a Disaster band by itself does not (a Disaster with survivors comes home, its incident
  still recorded). When Phase 2's Troubled deadline declares the group found and returned, spatial
  is reconciled to `returnTo` (a lifecycle reconciliation, not a journey); a write-off keeps the
  incident as the last truth. Arrive and Return are never snapped; the work region must fit the
  tighter of the outbound and planned return windows in real route steps. Only an active contractor
  is moved for an operation.
- **Rejected.** Spatial movement as the authority over contract state; a separate spatial delay;
  rewriting active Phase 2 operations on upgrade; spatial distance changing prices now (later tuning).
- **Consequences.** The recovery site appears where the contractor was working, without revealing
  its anchor: hidden truth → consequence → player-visible clue.

### ADR-044 · The Field Log is contract-scoped and temporary
- **Decision.** The Field Log belongs to a player-issued contract, from the accepted quote until the
  contract closes, and is then cleared (History and letters keep the durable record). It holds a
  bounded list of translation keys plus snapshotted words, written in the same step as the reported
  state change; reporting language only (no tile, route or hidden number), results only once the
  contractor reports them (ADR-037). It is shown as a compact section of the contract card, never as
  a tab or letters.
- **Rejected.** A permanent journal on the contractor (Phase 4 contractors will work for several
  issuers); logging daily movement or ambient travel; copying the log into History.
- **Consequences.** A tracking page for the player's own job, not a notification stream, with no
  save growth after the contract ends.

### ADR-045 · Abstract charter transport bridges disconnected same-layer geography
- **Context.** A contractor may need to reach an island or another same-planet region with no
  ground route, or only an impractically long detour. Declaring it unreachable is not always
  believable in a high-tech setting; teleporting there is never acceptable.
- **Decision.** An operation leg that cannot be walked in the time it has may cross by an
  **abstract, reusable, two-way charter** from a high-tech provider ([SPATIAL § 6.1](SPATIAL.md)):
  1. **Ground first.** Walking is always tried first; charter only for a candidate (or a leg) out of
     reach on foot.
  2. **Same layer only.** Never cross-layer, orbital, gravship or planet-to-planet; never for an
     invalid, impassable or cross-layer destination.
  3. **High-tech provider.** The port exposes one generic fact, `SettlementFacts.canProvideCharterTransport`;
     the RimWorld adapter derives it from the faction's real `TechLevel` (Spacer or better). No
     faction, DLC or defName is named.
  4. **Two-way.** One committed hub and one landing: set down near the work region, picked up again
     there, set down at the hub. Never a one-way pod. **A valid committed round trip is the return
     mode** even when a later delay makes walking possible: the extra time does not cancel the
     booking. Only the loss of the charter (its provider or pickup gone, and no replacement) may
     degrade the return to foot.
  5. **Committed truth.** The plan's hub and landing, and a leg's two crossing ends, are saved;
     nothing about a craft is. Save/load never turns ground into charter, changes the provider or
     rerolls the landing; a world change reconciles from current truth (another provider, on foot, or
     `Blocked`), never by teleport. **History and the live leg are never confused:** the live leg is
     `SpatialState.bridgeFrom/bridgeTo`; the plan records `charterUsed` (the outbound crossing
     happened, never cleared) and `charterLost` (a used charter can no longer carry the return; hub
     and landing kept as history), and `Charter` means only "a committed round trip in force". A
     charter never used is dropped (hub and landing cleared, with a fallback reason).
  6. **No surprise fee.** The charter is part of the contractor's quoted operational costs: no
     invoice, fee, deposit, insurance, transport contract or vendor, and no hidden transport economy.
  7. **No physical vehicle.** No shuttle, pawn, WorldObject, caravan, map icon, fuel or manifest.
  8. **Operation travel only.** Ambient relocation stays on foot.
  9. **Resolver unchanged.** The crossing fits the existing checkpoints; no second scheduler, no
     pickup deadline, no missed-pickup rolls, no new outcome bands, no casualties from transport.
- **Rejected.** Declaring every island unreachable (implausible); teleporting across water (no
  cause); a physical shuttle or a transport economy (Phase 3+ and beyond scope); a one-way transport
  pod (who brings them back?); charter for ambient movement (hidden background traffic);
  hard-coding the Empire or Royalty.
- **Consequences.** Disconnected geography has a cause the simulation knows, and the player sees at
  most one Field Log line. The committed hub, landing and pickup make later extraction-window stories
  possible (a crew withdrawing toward pickup, cargo abandoned, people who do not make it back); they
  are deliberately **deferred** until the resolver can consume a pickup window. In a Core-only game the
  settlement factions (tribes, outlanders, pirates) are expected to be below Spacer (S20 confirms), so
  no charter exists and spatial degrades softly as before.

### ADR-046 · Contractor careers extend existing simulation truth
- **Context.** Phase 2 contractors already carry public reputation (`PublicReputation`), wealth
  (`ContractorSimulation.funds`), equipment (`EquipmentProfile`), operational know-how (`skill`), a career
  stage and a roster. What was missing is the *spine* that makes work change them: a way for finished
  jobs to move reputation, for contractor money to be exact, and for wealth and reputation to buy kit.
  Building a parallel "career system" (Rep2, Wealth2, Skill2, EquipmentPower2) would have created two
  truths to keep in step.
- **Decision.** Careers are an extension of the existing state, frozen as ten rules
  ([CAREERS](CAREERS.md)):
  1. **Reputation extends `PublicReputation`.** A numeric `score` sits beneath the `FameBand`; the band
     is **derived** from the score through one `CareerPolicy`. No second reputation exists.
  2. **Wealth is `ContractorSimulation.funds`.** One saturating money path; contractor-owned money is
     attributed on the contract's own ledger records at the commit point; nothing is rescanned on load.
  3. **Equipment progression uses `EquipmentProfile`.** Its range and meaning are unchanged; advancement
     only raises `tier` under stated conditions.
  4. **Fame and Experience are independent.** Neither is derived from the other, ever.
  5. **Detailed job history stays in History.** `CareerRecord` is a fixed handful of cumulative
     counters, never one row per job.
  6. **Derived Tags are not a second stat system.** They are descriptors computed on demand from
     existing state, never stored, never read by the resolver, pricing, willingness or upkeep.
  7. **`CareerNeed` is derived and future-facing.** A pure read with no effect in Phase 2.75.
  8. **Advancement and results use the existing hooks.** Equipment advances from the existing staggered
     daily upkeep; the career result is applied once at the existing operation lifecycle's end
     (`Finish`, a post-outcome `Abort`, a written-off Troubled group); money rides the existing
     committed ledger transitions. No new scheduler job, no per-tick work.
  9. **No physical inventory, pawns, vehicles or augmentations in 2.75.** `Augmented` is a reserved Tag
     key that is never emitted.
  10. **Old saves preserve the visible state and invent no detailed past.** The score starts at the band
      floor; `legacyResolved` carries the old job count; operations from before are never
      career-eligible.
- **Design details that matter.**
  * Reputation gain is `round(difficultyValue × outcomeMultiplier × taper)`, with the operation's own
    **frozen** danger; a work-ceiling taper (never below Local) is the anti-farming rule, so contract
    count alone cannot make a Legendary name. No reputation is ever lost.
  * A refund takes back the proportional part of the contractor's pay on that contract; an insurance
    payout, a carried-over replacement deposit and the Fixer's fee never touch the contractor's funds; a
    technical invalidation leaves the contractor nothing (no windfall).
  * Exactly-once is a persisted flag on the operation, set before the result is applied.
- **Rejected.** A separate career score, wealth score or equipment-power value (two truths); Tags that
  grant bonuses (a hidden second stat system that would double the resolver's own effects); a row per
  finished job on the contractor (save growth; History already holds detail); reputation that can fall
  (deferred: nothing in Phase 2.75 justifies it); a new advancement scheduler (the daily upkeep exists);
  reconstructing a past from pruned history (invented data); a player-facing Career tab (the existing Fame
  descriptor is enough).
- **Consequences.** Contractors now change through play, but only as fast as real work arrives: Phase 2's
  market is player-driven, so most contractors do few jobs and move slowly (see RISKS). The derived `CareerNeed`
  and Tags are the hooks that Phase 4B (alternative compensation), hiring, sponsorship, rivalries and
  legends will consume, with no further change to what is saved. The save grows by about 1.6 %.
