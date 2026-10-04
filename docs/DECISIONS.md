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
- **Phase 3 design review.** Confirmed and refined by [ADR-048](#adr-048--one-authority-at-a-time-physical-presence-is-an-episode-reconciled-exactly-once)
  and [PHYSICAL_LIFECYCLE](PHYSICAL_LIFECYCLE.md): the tiers stand; a *named* person is bound when first materialized;
  `Actor ≠ Person ≠ Pawn`.

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
- **Phase 3 design review.** Re-audited against the 1.6.9676 assemblies ([PHYSICAL_LIFECYCLE § 7.4](PHYSICAL_LIFECYCLE.md#74-the-registry-reservation-retained-pawns-only),
  [Appendix E](PHYSICAL_LIFECYCLE.md#appendix-e-what-the-audit-changed-from-the-phase-0-design)). The decision stands and is
  sharper: reservation is **required** for a retained pawn that would be `Free` (redress chance up to 0.8 per generation);
  reserve **before** passing to the world (`Notify_PassedToWorld` rewrites a `Free` pawn's faction); the reserved set is
  only the stored named people; its cost grows with the list length. **Open (S9r):** a registry of vanilla classes only
  (`QuestPart_ReservePawns`) vs the Network-owned part (the default, because it self-heals on removal).

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
  reconciliation. Contingency patches are pre-analysed and not adopted. *(Policy wording, Phase 3.0.)* No Harmony patch is currently adopted. Phases 1–3 target zero Harmony. A patch may be adopted only after a runtime spike proves vanilla extension points insufficient **and** an ADR explicitly adopts the patch.
  The title states the target, not a guarantee: spike S31 could in principle have forced contingency C-4. *(Phase 3.1: S31 passed in
  the owner's runtime with M1, [ADR-053](#adr-053--retained-pawn-exit-reservation-uses-m1); C-4 is not needed and no patch is adopted.)*
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
  * A refund takes back the proportional part of the contractor's pay on that contract, **following the
    funding actually refunded and who was paid it** (a typed `RefundScope`, recorded as
    `MoneyRecord.fromOwnFunding`); an insurance payout, a carried-over replacement deposit and the Fixer's fee
    never touch the contractor's funds, and carried-in funding can neither dilute nor enlarge a replacement's
    clawback; a technical invalidation takes back everything the current contractor still holds from that contract (a typed
    full reversal, `MoneyRecord.fullReversal`), so an earlier insurance payout that reduced the player's refund
    can never shield contractor pay from it (no windfall).
  * Exactly-once is a persisted flag on the operation meaning the durable career mutation really committed:
    the result is planned as a pure delta, committed as one small durable step, flagged only after that
    commit, and only then announced; a commit that cannot complete (including a missing contractor or simulation) changes
    nothing, leaves the flag false and is retried by the next validation.
  * A written-off Troubled group is a Failure in the career record whatever band the resolver rolled; the
    committed outcome keeps its band.
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
- **Phase 3 amendment note (terminology only; no behaviour change).** Rule 1 keeps one numeric score, but that score is
  built only from completed work (`ReputationGain`), so in substance it is a **professional-record score** that the code,
  the UI and the docs call *fame*, and rule 3's advancement gate reads the derived `FameBand`
  (`CareerPolicy.RequiredFame`). Rule 4 stays true of the *bands* (Fame and Experience are independent), but one number
  cannot express a low-profile elite, a famous-but-mediocre principal with a competent bodyguard, or an unknown
  professional new to a region. The intended separation of professional reputation, fame/visibility and capability is
  recorded in [PHYSICAL_LIFECYCLE § 6.10](PHYSICAL_LIFECYCLE.md#610-professional-reputation-fame-and-capability) for a later
  focused phase; **nothing is implemented and ADR-046's rules stand.** Phase 3 only forbids physical projection from
  reading fame ([ADR-050](#adr-050--a-first-projection-never-contradicts-established-network-truth)).

### ADR-047 · Runtime regression tests are isolated from live gameplay state
- **Context.** The headless suite proves the logic but not the integration in a running game, and the owner
  had been the only runtime test. Phase 3 will add multi-step physical behaviour that cannot be re-verified
  by hand after every change, so an in-game test runner is built now, while The Network is still abstract.
  Its defining constraint is that it must be **safe to press in a real colony**: a test that spends silver,
  spawns cargo or leaves objects behind is itself a defect generator.
- **Decision.** Runtime regression testing ([RUNTIME_TESTING](RUNTIME_TESTING.md)) is frozen as eight rules:
  1. **Runtime tests complement the headless tests; they do not replace them.** The headless suite stays
     the required, independent proof of the logic; the production assembly never references the test
     project; the runner is itself tested headlessly.
  2. **The default runtime suite never mutates live colony or Network truth, and never starts, reconciles or
     repairs the live Network.** No silver, item, contract, operation, history, relationship, career, contractor
     position, site, cargo, letter or world object in the live game; and no `EnsureStarted`, `StartNow`,
     `RunStartup` or `NetValidator` from runtime-test code (the game starts the Network on its first tick, and a
     "safe" tool does not initialise or repair what it is meant to observe). A Network the game has not started
     is a gameplay state: the tests that need it SKIP with the advice to allow one normal tick. Non-mutation is
     *checked*, not asserted: a fingerprint of the Network's durable truth (every persisted field of every store,
     by content) plus selected safety-critical colony/world state (payment silver, cargo, world objects, letters)
     is compared before and after every slice (RT-INFRA-001); it is not a proof that all of RimWorld is untouched.
     **The sentinel fails closed**: a capture that throws on a running Network FAILS RT-INFRA-001; it may SKIP only
     when there is no live Network or the game has not started it, and then it says nothing was verified. A source
     scan forbids spawning, spending, live-scheduler use, letters and the start-up paths.
  3. **Mutable scenarios execute in an isolated sandbox.** A sandbox is a private in-memory Network built
     from the *production* services over *sandbox ports* (own ids, clock, scheduler, bus, journal, stores,
     payment, comms, catalog, world graph, delivery recorder), shares nothing writable with the live
     Network, and is discarded after the test.
  4. **Real RimWorld integration is inspected read-only by the safe suite.** The real catalog, comms gate,
     payment environment, world graph and drop-pod *plan* are read, never driven; the live Network is
     checked by a read-only invariant scan, never by the repairing validator. Production warnings and errors raised while a test
     runs are captured, not lost: an otherwise passing test becomes WARN and the lines are kept in the report.
  5. **Physical or destructive integration requires a future explicit, disposable environment.** Spending
     real silver, spawning cargo, launching a pod, creating world objects or editing the live Network is not
     part of any default suite and needs its own design and its own ADR. No automated save reload, quit or
     restart is built.
  6. **Test-runner control state is never persisted.** The runner, its session, results, preserved failures
     and sandboxes are runtime-only: not `Scribe`d, no `devtest.*` job in the persisted scheduler (RT-INFRA-003),
     no save-version change (still 4). Every static dev override a test can influence is snapshotted and
     restored to its **previous** value after every step (RT-INFRA-002), never blindly reset.
  7. **Runtime tests use no Harmony.** The mod remains Harmony-free (ADR-017); the headless runner's own
     Harmony stub is a test-host tool and is never in the mod.
  8. **No normal-game background monitoring.** Nothing runs unless a Dev Mode action starts it. Idle cost is
     one static null check per frame, with no allocation, scan or job; a run is time-sliced (default 8 ms
     per frame), never blocks the game thread, and has finite real-time and wait limits.
- **Design details that matter.**
  * Stable test IDs (`RT-SMOKE-*`, `RT-LIVE-*`, `RT-PROC-*`, `RT-CAR-*`, `RT-SPAT-*`, plus `RT-INFRA-*` appended
    to every run) are permanent and never reused; the outcome vocabulary is PASS / FAIL / WARN / SKIP.
  * Tests exercise the **real production services** (not copies of their logic), so a green run says something
    about the code that ships; expected numbers are derived from generated state, never copied constants.
  * Determinism comes from `NetRng` seeded from the test ID and a fixed Phase 2.9 seed.
  * A failed sandbox is kept in memory (one at a time) for inspection and is never saved.
  * The fingerprint walks persisted fields by reflection, with per-type accessors compiled once (identical hashes
    from a reflective fallback), so new persisted fields are covered automatically; runtime-only caches are
    named `cached*` and are not hashed. Capture results are explicit (`Available`, `NetworkUnavailable`,
    `NetworkNotStarted`, `Failed`), never "a fingerprint or null".
  * The runner has no start-up exemption: if availability changes inside a slice, RT-INFRA-001 FAILS.
- **Rejected.** Running scenarios against the live Network and "cleaning up" afterwards (cleanup is where
  player items get deleted); a persisted control job in the scheduler (a stuck job would survive a reload);
  Harmony patches to observe the game (ADR-017); an always-on background self-check (idle cost, noise); a
  second copy of the domain logic written for the tests (a false PASS about code that does not ship);
  automated save-reload and physical-delivery suites (not safe in a real colony; deferred to a future
  explicit design); a player-facing test tab (developer infrastructure only). Also rejected after review: calling `EnsureStarted` to make the Network testable
  (it runs the game's own load reconciliation and repairs); a fingerprint of counts and a few hashes (it missed
  mutations of existing durable state); treating a failed fingerprint capture as a skip (a silent loss of the
  principal safety proof).
- **Consequences.** The owner can press *Full safe regression* in any colony and keep playing it. A
  regression in the integration or in a multi-step invariant is caught in seconds instead of by a playtest.
  Phase 3 adds its own stepped scenarios and a sandbox delivery port to a runner that is already proven.
  The framework can drift from production (see [RISKS R-27](RISKS.md)) and is itself a surface that needs
  its own headless tests; both are mitigated by calling production services and by the `Runner.*` suite.
  Honest limit (updated after the owner's runs): the in-game suites (`RT-SMOKE-*`, `RT-LIVE-*`), the game host,
  the colony sentinel and the Dev actions were compile-checked where they were built and have since been
  **run by the owner in a fresh Dev Quicktest colony and in the real, heavily modded, ongoing colony, with zero
  runtime FAILs and the live colony (130 contractors, 4,114 silver) unchanged by manual check**
  ([RUNTIME_TESTING § 15](RUNTIME_TESTING.md#15-owner-observed-runtime-evidence)). That is evidence for this
  framework in two environments, not a proof of every mod combination or of every RimWorld state; the sentinel
  remains a bounded tripwire, and the in-progress Cancel path has headless coverage only.
  Phase 3's physical scenarios are the case this ADR deferred (point 5): they get their own separate,
  explicit, disposable-environment tier and ADR ([PHYSICAL_LIFECYCLE § 21](PHYSICAL_LIFECYCLE.md#21-runtime-qa-strategy)).

### ADR-048 · One authority at a time; physical presence is an Episode, reconciled exactly once
- **Status.** Proposed by the Phase 3 design review ([PHYSICAL_LIFECYCLE](PHYSICAL_LIFECYCLE.md)); implemented in
  subphases 3.0 to 3.2. Refines ADR-013 and ADR-014; the Phase 0 `Deployment` concept is superseded by the Episode.
  **Amended** by the Phase 3 amendment pass: rules 3, 6 and 10 are revised (see [PHYSICAL_LIFECYCLE Appendix F](PHYSICAL_LIFECYCLE.md#appendix-f-amendment-log)).
  **Corrected** by the Phase 3 correction pass: rule 1 (the gate also waits for release) and rule 6 (explicit stage markers
  and a durable publication outbox) are tightened, no rule changes direction (see [Appendix G](PHYSICAL_LIFECYCLE.md#appendix-g-correction-log)).
  A **micro-correction** adds to rule 6: a pawn vanilla has already passed to the world is never passed again, and the
  retained-pawn exit window is an open spike that gates 3.1 ([Appendix G.2](PHYSICAL_LIFECYCLE.md#g2-micro-correction-on-top-of-3f1cbee)).
- **Context.** Phase 3 crosses from abstract records into real RimWorld pawn state. The failure to avoid is a hidden
  second authority: a person simulated abstractly while physical, a death overwritten by stale abstract health, a
  clone, a consequence applied twice, a prisoner abstracted because it is "not spawned". The Phase 3 design audit of the
  1.6.9676 assemblies showed several Phase 0 assumptions were wrong (death does send `Killed`; `PassToWorld` rewrites a
  `Free` pawn's faction; a reference to a dead pawn saves `null`; `GeneratePawn` can return someone else's world pawn).
- **Decision.** Twelve rules.
  1. **One authority per person:** Abstract, Physical or Vanilla-held. One gate, `CanSimulateAbstractly(person)`, fronts
     every abstract writer of person state; authority changes only in `Materialize` and `Reconcile`. *(Correction)* The gate
     is true only with **no episode membership at all**: a person whose episode is Closed but whose **release has not
     completed** is still not abstractly simulatable, so a half-released person is never advanced by two layers.
  2. **`Actor ≠ Person ≠ Pawn`.** A crew is never one pawn; an anonymous member is an episode slot; `thingIDNumber` is a
     binding attribute, never identity.
  3. **A named person has one pawn for life,** bound at first materialization and never regenerated or rerolled; a
     lost pawn makes the person `Lost`. **Rank-and-file of a large organization are ephemeral:** a new pawn per episode
     (`ForceGenerateNewPawn`), released to vanilla, headcount deltas only. **Small recurring organizations concretize
     progressively** ([ADR-050](#adr-050--a-first-projection-never-contradicts-established-network-truth)): a seat the player has
     met becomes a named, bound person, bounded by the existing named-people caps.
  4. **One new durable concept, the Physical Episode,** in the reserved `deployments` slot. It owns *presence facts only*;
     consequences go through the existing casualty, career, spatial and event services in the existing vocabulary.
  5. **Provenance is the persisted binding** (pointer saved with `saveDestroyedThings: true`, plus `thingIDNumber`),
     written **before** the pawn is spawned. Quest tags are a signal-routing aid only. A registry reservation protects a
     retained pawn that would be `Free` (S9r). No Hediff, no ThingComp, no name or faction matching.
  6. **Reconciliation is observe → decide → plan → validate → atomic durable commit → flag last → release → follow-up →
     publish,** idempotent, from *observed* state; signals are wake-ups that only enqueue; `Returned` requires a positive
     observation, never absence. **The commit is all-or-nothing for the Network's durable state:** a pure, validated plan;
     a snapshot of exactly the touched set; a restore on any throw; `consequencesApplied` as the last statement; and **no
     publication, scheduler, vanilla or fault-swallowing call inside it**, because the existing casualty, succession and
     actor-ending paths interleave exactly those. *(Correction)* Each post-commit stage is idempotent and has its **own
     explicit durable marker, written last and never inferred from side-effect state** (a removed routing tag is clean-up,
     not completion evidence; an operation status that a legacy path flips mid-way is not a marker). **Publication progress
     is durable per event:** the commit stores an ordered outbox and `publishCursor`; an event the existing bus has accepted
     is never submitted again, no dedupe key is assumed (the bus has none), a throwing consumer is never redispatched, and
     publication failure never replays a consequence. Atomicity is *demonstrated* by a fault-injection sweep (RT-PHYS-026),
     not argued. *(Micro-correction)* **`PassToWorld` is never called for a pawn already in `WorldPawns`.** Vanilla's
     `Pawn.ExitMap`, map removal and site destruction pass the pawn themselves, so a `Returned` member is already a world
     pawn and RELEASE only normalizes it, proves its reservation and strips routing; the Network calls `PassToWorld` only for
     a bound pawn that is positively unspawned, not in `WorldPawns` and held by nobody
     ([§ 7.5](PHYSICAL_LIFECYCLE.md#75-who-may-call-passtoworld-an-observed-world-pawn-is-never-passed-again)). The interval between vanilla's pass and the Network's reservation was **not solved** at that point: spike **S31**
     was to choose the mechanism (reserve while spawned, a synchronous vanilla callback, or a narrow patch, in that order), 3.1
     was blocked until it had been run and owner-reviewed, and 3.0 was not. *(Resolved: the owner ran S31 and it passed with
     M1, the first option; [ADR-053](#adr-053--retained-pawn-exit-reservation-uses-m1), [§ 7.6](PHYSICAL_LIFECYCLE.md#76-the-vanilla-exit-window-resolved-by-m1-spike-s31-owner-validated).)*
  7. **Death is monotonic;** resurrection is observed, never initiated, and never makes a person `Active`.
  8. **Held people are a persisted custody state** observed by a bounded custody watch; an unsupported custody fails safe
     into `Quarantined`, never faked.
  9. **Faction:** one temporary hidden faction per *episode* (vanilla removes it with the episode); the Network
     relationship and the faction relation are bridged only at seeding and at reconcile.
  10. **Gear is real and never mirrored** into the abstract tier; Phase 4 gets two separate explicit-record seams, a
      *Lease* and a *Notable Asset* (ADR-050).
  11. **No Harmony** for the recommended slice; the three genuine hook gaps (downed, caravan join, resurrection) are
      bounded polls.
  12. **Event-driven:** no job, scan or allocation when nobody is physical or held.
- **Rejected.** Extending `Operation` or `KnownCharacter` to own presence (a visit has no operation; operations are
  compacted; a character cannot own a group event). A permanent faction per contractor, a shared hidden faction, the
  origin faction, null faction, guest status. A second pawn-regeneration path ("reproject from a snapshot") for named people
  (mod-list dependent; a twin if the original lives on). `KeepForever` alone (the pawn stays `Free`: redress, hijack by
  `PrisonerWillingToJoin`). A Hediff or ThingComp provenance marker (Defs, removal errors, every race). A persisted
  roster of anonymous individuals. Mirroring Hediffs, inventories or gear into abstract state. *(Amendment)* Treating the
  commit as "one block of primitive assignments that cannot fail" by calling the existing casualty, succession and
  actor-ending paths directly (they publish, schedule and swallow faults inline), and publishing inside the transaction.
  *(Micro-correction)* Calling `PassToWorld` for a pawn vanilla already passed; assuming the Network can reserve a retained
  pawn "in time" after a vanilla exit without proof; choosing a Harmony patch for the exit window before the vanilla
  callbacks are shown insufficient.
  *(Correction)* Inferring a stage's completion from its side effects (a removed tag, a changed status); republishing "keyed"
  events after a retry (the bus has no idempotency key, so a second `Publish` is a new event that every consumer would
  process again); letting a Closed-but-unreleased person become abstractly simulatable.
- **Consequences.** One save-format bump (no data migration; it makes older builds warn instead of silently dropping
  episode data). A registry whose cost must be measured. Four subphases (3.3 is design direction only) with owner gates.
  The writer inventory is part of the design and a test. The existing casualty / succession / ending paths are *split*
  into a pure state half and an effects half, proved identical to the abstract path by a parity test (RT-PHYS-027).

### ADR-049 · Physical runtime QA is a separate, explicit, disposable-environment tier
- **Status.** Proposed by the Phase 3 design review; implemented in 3.1. Completes ADR-047's deferred point 5.
- **Context.** ADR-047 made Full Safe Regression safe on a real colony by forbidding spawning, spending, creating and
  sending. Phase 3's behaviour *is* spawning and creating (real pawns, maps, factions). The two requirements cannot share
  a suite. The owner validated the safe tier in a fresh Quicktest colony and the real modded colony with zero FAILs; it
  must not be weakened.
- **Decision.** Two tiers. **Tier S (safe):** the abstract half of the lifecycle (`RT-PHYS-*`) runs over a scriptable fake
  `PhysicalWorldPort` and never references the real physical adapter or a pawn-creating API (source-scan enforced).
  **Tier P (physical):** a separate Dev menu category with a scary label; a **session-only typed arm** (runtime-only,
  cleared on every load and after each run, never persisted); a **dedicated generated test map by default** (the suite
  creates and owns it; no existing pawn, map or colonist is touched); a **stronger second gate** (a separate typed
  confirmation naming the home map) for the few scenarios that fundamentally need a home colony; every created entity tagged
  `TheNetwork.Test.<runId>`; a blast-radius proof (untagged state unchanged); cleanup of tagged entities only; a preserved
  failed scenario; its own source folder and scan; and a pawn-level sentinel. Nothing about either tier is saved.
  **(Amended.)** The guard makes *no inference* about whether a save matters.
- **Rejected.** Letting Full Safe Regression spawn on a "test" map in the live save (one bug from the owner's colony).
  Auto-cleanup of anything untagged. A persisted "this save is a test" flag (none is needed). **A heuristic guard**
  ("low colonist count and low wealth, so probably a test colony"): a legitimate early-game colony looks exactly like that,
  and the Network must not try to infer whether the player's save matters. Automated save/reload (not safe; the save
  matrix is an owner checklist).
- **Consequences.** Two places to maintain; the physical tier needs the owner's explicit, per-session choice to run. There
  is no disposability detection to get wrong; S22 builds the arm flow and the test-map lifecycle and proves the arm clears.

### ADR-050 · A first projection never contradicts established Network truth
- **Status.** Proposed by the Phase 3 amendment pass ([PHYSICAL_LIFECYCLE § 4.5, § 6.4–6.10, § 11.2](PHYSICAL_LIFECYCLE.md));
  persisted shapes declared in 3.0, behaviour implemented in 3.1 (a Solo) and 3.2 (groups). Refines ADR-013 and ADR-048.
  **Corrected** by the Phase 3 correction pass: rules 2, 3, 4 and 9 (see [Appendix G](PHYSICAL_LIFECYCLE.md#appendix-g-correction-log)).
- **Context.** Materializing a contractor creates a real RimWorld pawn from vanilla's random generator. Left alone it can
  contradict what the Network already established: a crack marksman with Shooting 1 or a Brawler trait, a legendary
  medic who cannot doctor, a "professional veteran team" whose members hate each other on arrival, a five-person crew
  whose medic is a different human every visit, a contractor who does not age while stored, a famous heir treated as a
  combat expert. The audit of the 1.6.9676 assemblies showed vanilla gives levers but **no guarantee**: validators are
  dropped after 100 tries, a kind is saved by def name (so no runtime kinds), an incapability is a consequence of the
  pawn's identity, biological age freezes while a pawn is suspended, and a pawn's equipment is real.
- **Decision.** Nine rules.
  1. **An Operational Role is a durable semantic function,** not a class, perk or bonus, distinct from the organizational
     `CharacterRole`. It constrains **only what is necessary**; RimWorld's randomness fills everything the Network never
     established and may never contradict what it did.
  2. **A role is verified before a pawn is bound.** Request fields and validators are an optimization; an authoritative
     post-generation check decides. The only correction is the smallest: *raise* a role-defining skill's base level
     (respecting aptitudes) and re-verify. *(Correction)* **Passion is never a role constraint, preference or correction.**
     An incapability, a backstory, a trait, a gene, a passion or any other identity is **never** corrected: the candidate
     is rejected, and after a bounded number of attempts the placement aborts. Roles map to **existing** kinds.
  3. **An organization has a role composition** (a small template) and each mission picks a **mission composition**; a
     wrong-role pawn never fills a need. No roster is stored. *(Correction)* The composition is a **pure, versioned function
     of immutable origin facts** (seed, form class, specialties, `CharacterId`): it may be *stored* lazily to save space, but
     storing it later never changes what it is, and it never depends on when the player first observed the organization.
  4. **Progressive concretization.** Rank-and-file of a large organization stay ephemeral; the placed seats of a small
     recurring organization crystallize into named, bound people, **bounded by the existing named-people caps**, so the
     Network preserves stories without a giant roster. *(Correction)* Promotion beyond the seat policy needs **strong
     story evidence** (a material outcome such as capture, recruitment or rescue; being named by the Network; a narrowed
     battle-log signal): **being present on a player-visible map is not enough** to turn a company rifleman into a persistent
     person. A performance cap never breaks the identity of a person the player met.
  5. **Team cohesion is a derived, never-stored band that constrains first generation only** (prevention by construction,
     a screen against teammates). After binding the Network never writes a pawn's relations, opinions, thoughts, memories
     or traits: a real fight is real history.
  6. **Chronological age is derived from the clock; biological age is brought fully current before any observation,**
     never capped per materialization, through a mechanism chosen by spike S12.
  7. **Capability is independent of fame, reputation and visibility.** Projection never reads them. (Implemented truth:
     one score is both the professional record and the "fame" band; the separation is a later focused phase.)
  8. **Equipment stays abstract unless an exact item matters.** A *Lease* (ownership stays external) and a *Notable Asset*
     (ownership transfers) are separate future seams; generic equipment is never an inventory; a never-materialized
     recipient's notable asset is the one case where the Network creates an item.
  9. **Established truth is immutable and time-independent.** A role or composition *derives* from immutable origin facts
     by a frozen pure function; lazy persistence only decides *when* it is written, never *what* it is. Experience, new
     cast, a changed setting or a later observation can change a person's *competence*, never their initial role or an
     organization's initial composition.
- **Rejected.** Runtime-created `PawnKindDef`s (not savable); roles as classes, perks or stat bonuses; persisting an exact
  skill sheet for a never-materialized person; trusting a validator as a guarantee; "fixing" an incapability or swapping a
  backstory; capping elapsed aging; setting `AgeBiologicalTicks` alone (right digits, no birthday consequences); a persisted
  roster of company soldiers; releasing an encountered member of a living organization to satisfy a cap; injecting positive
  memories or relations to make a team like each other (mind control); using fame as a proxy for skill; forcing every exact
  item through the lease slot. *(Correction)* Correcting passion to satisfy a role; promoting a company member because the
  player saw them; deriving identity from state that changes after creation or from the moment of first observation.
- **Consequences.** A bounded retry cost per first creation (measured by S25); a progressive growth of retained pawns that
  the soak must watch; truthful aging may produce chronic conditions or an unfit person (the abstract consequence, O-12,
  is a content decision); persisted shapes `opRole`, `firstEncounterTick` and `agedThroughTick` are declared in 3.0 and the
  composition in 3.2. *(Correction)* The seat-assignment function and an optional explicit `origin` snapshot are the one open
  item (O-18); the derivation must be a frozen, versioned function so that old saves, new saves and a later settings change
  agree.

### ADR-051 · Procurement fulfillment may be a physical handoff (Phase 3.3, design direction)
- **Status.** **Direction only**, proposed by the Phase 3 amendment pass ([PHYSICAL_LIFECYCLE § 27](PHYSICAL_LIFECYCLE.md));
  nothing is decided beyond the direction, nothing is implemented, and it needs the owner's approval after 3.2.
- **Context.** Phase 2 delivers by vanilla drop pods (owner-observed: 10,000 Plasteel). The first design review pushed
  in-person delivery out of 3.0 to 3.2 and suggested "later or Phase 4". It is in fact the first natural consumer of the
  physical lifecycle and deserves its own subphase and visible seams.
- **Decision (direction).** (1) Three delivery **modes**, orbital (existing), colony handoff and rendezvous, as a contract
  term constrained by **capability, logistics and technology, never fame**. (2) **Personnel mobility is not freight
  capability**: a Solo can procure 10,000 Plasteel through abstract contracted freight that stays abstract until handoff;
  no persistent vehicles. (3) An optional **per-contract orbital charter** ("Additional Funds for Orbital Delivery"): no
  surcharge for a contractor with native capability, a temporary capability for this contract otherwise, a higher contract
  total, never a permanent upgrade. (4) The **physical handoff is an explicit, idempotent staged protocol** (a right-click on
  a representative; no Harmony needed, S28), **not one atomic transaction**: payment and the movement of real Things are
  external side effects owned by RimWorld and cannot share the Network's commit. It has exactly-once *semantics* through
  stages keyed by a persisted transaction id (validate, reserve, charge, transfer with positive transfer evidence, finalize,
  compensate), each resumable and none repeated; **cargo stays under the contractor's possession until then**; a terminal
  handoff state is required before normal completion. (5) **Physical reality wins:** robbery, violence
  and abandonment are legal outcomes the Network records; no ownership locks, no invulnerability. (6) Current
  Fixer-mediated brokerage and deposit are unchanged; unusual **payment timing is a future Direct Contract term**. (7) A
  **rival-interception seam** with the rule "spatial overlap creates opportunity, not automatic encounter" (spatial
  opportunity, plausible knowledge, motive, availability, capability): compatible seams only.
- **Rejected.** Ownership locks or invulnerable contractors; magical "enter map, reform caravan, take the goods"; persistent
  trucks, ships or freight simulation; a charter that permanently upgrades a contractor; changing Fixer terms or current
  Procurement; fame-gated delivery; omniscient or detection-radius rivals; building any of it in 3.0 to 3.2.
- **Consequences.** A named fourth subphase with its own spikes (S28 to S30) and open questions (O-15); the lifecycle's
  `Delivery` purpose and the handoff states are designed against the same reconciliation machinery.

### ADR-052 · Phase 3.0 implementation choices for the lifecycle foundation
- **Status.** **Deviation / clarification, for owner review.** Recorded by the Phase 3.0 implementation. None changes an approved
  principle of ADR-048 (one authority per person, the Episode owns presence only, exactly-once reconciliation with explicit
  stage markers); each settles a point [PHYSICAL_LIFECYCLE](PHYSICAL_LIFECYCLE.md) left open or makes it concrete. Listed with its
  code locations in [PHYSICAL_LIFECYCLE Appendix H](PHYSICAL_LIFECYCLE.md#appendix-h-phase-30-as-built).
- **Context.** Phase 3.0 builds the lifecycle brain over a port, with no RimWorld pawn. A few decisions had to be made to write it.
- **Decision.**
  1. **Held custody is quarantined until 3.2.** DECIDE maps `Dead` ⇒ `Killed`, `Gone` ⇒ `Lost`, `WorldFree` with exit evidence ⇒
     `Returned`; `HeldByPlayer`, `JoinedPlayer`, `Kidnapped`, `HeldByOther` and `InCaravan` ⇒ `Quarantined(UnsupportedCustody)`
     (§ 17); anything else stays `Pending`. No 3.0 observation yields `Missing`.
  2. **Operation-linked episodes are named-only**, hand the operation over (`OpStatus.Physical`, its deadline suspended), and
     record `Found` / `WrittenOff`. An episode that placed nobody or was detached records `None`, and FOLLOW-UP returns the
     operation to `Troubled` and its own deadline: nothing physical happened, so nothing is decided for it.
  3. **The unavailable reason** reuses `Availability.Unavailable` ("they cannot be reached"); no refusal text says "busy".
  4. **`MemberOutcome.Detached`** (value 10) is the removal-settle outcome of a member that was not terminal.
  5. **A named member created but never placed** keeps its write-once binding and becomes `Stored`.
  6. **`Lost`** sets status and custody `Lost`, is counted as missing in the casualty event, and ends a Solo with reason `Lost`.
  7. **Anonymous members** are moved to the organization's checked-out (`committed`) headcount while out.
  8. **The commit's snapshot** is an in-place reflection snapshot of exactly the touched Network objects plus a truncation of the
     characters store (for a promoted record); the vocabulary of the commit is a closed enum of durable assignments.
  9. **Parity** with the abstract casualty path is exact unless a successor is promoted, where only the new person's id number
     differs (the abstract path publishes, and so draws history-record ids, before it creates the person; the commit after).
  10. **Narrow guards** (unreachable in 3.0 production): a `Physical` operation is never aborted, finished by a contract's terminal
      path, or "repaired" by the procurement validator; its episode's FOLLOW-UP resolves it.
  11. **A refused `PassToWorld` precondition blocks RELEASE** (PR #8 review). Only `Allowed` (the pass) and `AlreadyInWorldPawns`
      (an observed no-op, never a second pass) complete the action; `Spawned`, `Held`, `Dead`, `Unknown` or any future non-success
      value throws `PhysicalPreconditionException` into the ordinary stage failure, so the cursor stays, COMPLETE cannot run, the
      link and the closed gate stay, and the watch retries.
  12. **Anonymous headcount is checked per tier in aggregate** (PR #8 review): duplicate rows for one tier are summed against
      `FateRules.PeekHealthy` before the plan is accepted, and a negative or missing row is refused.
  13. **A positive return resolves `Missing` / `Captured`** (PR #8 review): healthy ⇒ `Active` through `FateRules.ReturnedFree`,
      injured ⇒ `Wounded` through the shared `Fate.Wounded` rule; VALIDATE keeps `Dead` monotonic and never converts `Lost`.
  14. **A stuck post-commit stage stays pending, not quarantined.** Bounded retries quarantine only before the commit; a
      `Closed(Reconciled)` episode with a failing RELEASE, FOLLOW-UP or PUBLISH keeps that marker false, its people linked and
      blocked, is retried (slowly past the bound) and reported by the validator after 30 days, and is never completed by inference.
  15. **Succession in a plan reads projected truth** (PR #8 review): an episode member is judged by the status the complete plan
      will write (`ReconciliationPlanner.EligibleAfterPlan` over `ProjectedStatus` and the shared `FateRules.MayLead`), so a
      Missing/Captured person the plan positively returns may lead (a wounded return as the living `Wounded` person the abstract
      rule already admits); `NeverPlaced` resolves nothing; `Killed`/`Lost`/`Detached` never lead; ranking is unchanged.
- **Rejected.** Inventing a capture, a rescue or a "came home" for a held or unobservable person; writing an operation off because
  a materialization failed; a new availability value that would change refusal texts; inferring any stage's completion from
  operation status, tags or custody; skipping a release action whose precondition failed; re-labelling a committed episode
  `Quarantined`; deciding a succession from a status the same plan resolves, or by temporarily applying the plan.
- **Consequences.** 3.2 replaces rule 1 with held-person support and adds anonymous members to linked episodes. Rule 9 is the
  only observable difference from the abstract path, and it is in id numbering alone.

### ADR-053 · Retained pawn exit reservation uses M1
- **Status.** **Accepted — owner runtime validated.** The number was reserved by the Phase 3 micro-correction for the S31 result. The
  Phase 3.1 implementation branch left it unwritten (it described S31 as awaiting confirmation); that wording was out of date: the
  owner had already run the dedicated S31 spike before 3.1 was implemented and accepted M1. This record corrects the state. It
  validates the **mechanism** only (see "What this does not claim").
- **Context.** Vanilla passes an exiting pawn to `WorldPawns` itself (`Pawn.ExitMap`, `MapDeiniter`, site destruction), before any Network
  code can react, and a map removal sends no `LeftMap` for a contractor. A `Free` world pawn is vanilla's redress, garbage-collection
  and quest-selection candidate, and a pass of a `Free` pawn can rewrite its faction (P3-INV-032 forbids exposing a retained named
  pawn to any of that). [PHYSICAL_LIFECYCLE § 7.6](PHYSICAL_LIFECYCLE.md#76-the-vanilla-exit-window-resolved-by-m1-spike-s31-owner-validated) listed three
  mechanisms in order of preference, **M1** reserve while spawned, **M2** a synchronous vanilla callback, **M3** a narrow Harmony
  contingency (C-4), and made spike S31 a mandatory, owner-reviewed gate on Phase 3.1.
- **Decision.** **M1.** A living bound named person whose custody is `Deployed` or `Stored` is reserved by the Network registry
  **even while spawned**. The binding and the `Deployed` custody exist before the spawn, so the reservation predicate already holds
  before vanilla's normal exit or map-removal path calls `PassToWorld`; the Network does not wait for `LeftMap` to establish
  retention. **RELEASE merely proves the reservation; it does not create it** (no stage may repair a missing registry quest: an
  unproven reservation keeps RELEASE pending). This closes P3-INV-032.
- **Owner-runtime S31 findings (recorded as proven, and no more than proven).** The owner's S31 spike passed the required cases: a
  normal vanilla exit; a retained pawn covered by the reservation while spawned; **no Free-world-pawn window observed**; **no faction
  rewrite**; **no Network double `PassToWorld`**; same-pawn rematerialization; the injured-return path; several retained named pawns;
  populated world-pawn / redress pressure; save and load; map removal with no `LeftMap`. The spike's harness and report are the owner's;
  no spike code is in this repository.
- **What this does not claim.** S31 / M1 is validated. **Phase 3.1 as a complete controlled physical episode is not**: the physical tier
  `RT-PHYX-001…012`, the production regressions `RT-PHYX-015` and `RT-PHYX-016` on the 3.1 build, the 3.1 save/load matrix, role generation on
  real pawns and truthful aging on real disposable pawns are all still owner runs.
- **Rejected.** **M2** (a synchronous reserve-only route on `LeftMap`, plus a pre-removal `SitePartWorker` hook): unnecessary after M1
  passed, and it would not cover a map type without the hook. **M3 / C-4** (a Harmony prefix on `PassToWorld`): not needed, so ADR-017's
  zero-Harmony stance stands. Waiting for `LeftMap` to establish retention: there is no `LeftMap` on a map removal.
- **Consequences.** Registry membership is derived and covers the pawn from its binding (ADR-054 item 2); (the registry's load order is corrected by [ADR-055](#adr-055--phase-31-runtime-qa-correction-the-load-order-generic-first-kinds-role-compatibility)) the reserved set now includes
  deployed people (still bounded by the named-people cap) and the per-tick cost is re-measured by the physical tier (`RT-PHYX-007`); a
  retained named person observed as an actual `Free` world pawn is the reservation **failing**, never a return (P3-INV-034, the
  Phase 3.1 correction pass); `EnsureRetained` is a pure proof; § 6.3, § 6.4, § 7.4 and RELEASE's wording now say "prove", not
  "establish". The spikes README, the phase plan and the risk register carry the corrected status.

### ADR-054 · Phase 3.1 implementation choices for the controlled physical episode
- **Status.** **Deviation / clarification, for owner review. Phase 3.1 IMPLEMENTED — OWNER PHYSICAL VALIDATION IN PROGRESS (not a PASS; the first owner run's findings are corrected by [ADR-055](#adr-055--phase-31-runtime-qa-correction-the-load-order-generic-first-kinds-role-compatibility)).** Recorded by the Phase 3.1 implementation; none changes an approved principle of ADR-048 to ADR-052. Each
  settles a point [PHYSICAL_LIFECYCLE](PHYSICAL_LIFECYCLE.md) left open; code locations are in
  [PHYSICAL_LIFECYCLE Appendix I](PHYSICAL_LIFECYCLE.md#appendix-i-phase-31-as-built) and the correction pass in
  [Appendix J](PHYSICAL_LIFECYCLE.md#appendix-j-phase-31-post-review-correction-pass-pr-10). The implementation is built on **M1, accepted and
  owner-runtime validated by [ADR-053](#adr-053--retained-pawn-exit-reservation-uses-m1)**; the 3.1 physical suite itself has not been run by the owner.
- **Context.** 3.1 connects the Phase 3.0 lifecycle to RimWorld for one Solo, on the physical test tier's own map, with no Harmony
  and no new durable field.
- **Decision.**
  1. **The real port** (`Integration/Physical/RimWorldPhysicalWorldPort`) is the only production `IPhysicalWorldPort`, selected
     by the live runtime alone; headless code, the sandbox and the soak keep the fail-closed port or the fake. The port gains
     `EnsureEncounterFaction`, `ReleaseEncounterFaction` and a faction argument on `Place`: the lifecycle decides *when*, the port
     acts.
  2. **M1 as built** ([ADR-053](#adr-053--retained-pawn-exit-reservation-uses-m1)). Membership is derived (the bound pawn by reference, a living person, custody `Deployed` or `Stored`); the
     binding precedes the spawn, so the reservation already covers a spawned pawn and is in force when vanilla's exit or map
     removal passes it; placement is refused unless the registry covers the pawn; RELEASE proves the reservation and never
     establishes or recreates it. One hidden accepted quest with a fieldless Network-owned part answers vanilla's reservation question;
     the registry is built in two load stages (the durable thing-id index at `FinalizeInit`, the validated pointer index at `PostLoadInit`; *corrected by ADR-055: it was first built from pointers in `FinalizeInit`, which runs before cross-references resolve, and was empty after every load*).
  3. **One temporary encounter faction per episode**, the vanilla refugee pattern (hidden, neutral, named after the actor, goodwill
     seeded once from the Network standing, never hostile in 3.1), handed back to vanilla's own removal by RELEASE.
  4. **Vanilla AI only**: `LordJob_VisitColony` with a fixed stay and no gift.
  5. **Role derivation v1** from the actor's seed and original specialties only, stored at `Instantiate` (or by the load-time compatibility pass, ADR-055, or lazily, identically,
     at the first `Plan`); first creation is bounded (four seeded attempts over the encounter faction's generic member pool, ADR-055; *an earlier global capability ranking leaked special-purpose kinds*), verified, corrected
     at most by raising one role skill's base level, and aborted rather than contradicted.
  6. **Truthful aging** through `AgeTickMothballed` over the full interval, chunked per game year. *(Correction pass: the step is **not
     atomic**: it advances the whole step before its birthday effects run, so a step that throws has an unprovable progress. The
     bookmark advances by the steps that returned only, that interval is never replayed or falsified, and the episode is quarantined
     — `AgeTruthUncertain` — with the person blocked; an earlier claim that a failing catch-up records exactly what was applied is
     withdrawn.)* `BirthAbsTicks` is never written; the exit tick is the synchronous `LeftMap` when seen, otherwise the commit tick
     (bounded under-age).
  7. **Store-time normalization** heals non-permanent vanilla injuries only and logs everything else it leaves.
  8. **The physical test tier** labels every item with its scenario id first (the owner's handoff rule, replacing the `⚠ PHYSICAL`
     prefix; the category carries the warning); its guard reads facts only; 007 forces no real GC pass and redresses nothing
     unrelated; 012 proves the aging mechanism on disposable pawns without skipping the clock (006 proves it on the real person).
  9. **A failed placement never strands a bound person** *(correction pass)*. After binding, a pawn's physical state is authoritative:
     a placement that did not report success is classified from positive observation (spawned or dead ⇒ `Present`; discarded or gone ⇒
     `Lost`; alive, unspawned, undiscarded and held by nobody ⇒ `NeverPlaced`; held or unobservable ⇒ quarantine), never assumed
     `NeverPlaced`. A binding that no longer resolves is `Lost`. `PHYSICAL_LIFECYCLE` § 7.3 rule 7, P3-INV-033.
  10. **An actual `Free` on a retained named person is a bug** *(correction pass)*: `ObservedKind.ReservationBroken`, quarantined, never
     `Returned`, never repaired by the lifecycle; `RT-PHYX-002/005/015/016` fail on it. P3-INV-034.
  11. **First gender and age follow the person** *(correction pass)*: a pure function of the world seed and the `CharacterId`
     (`PersonIdentity` v1), passed through `PawnGenerationRequest.FixedGender`, `FixedBiologicalAge` and `FixedChronologicalAge`, never
     persisted, never applied to a pawn afterwards, never episode-seeded. P3-INV-035.
  12. **Truthful aging after a throw is uncertain** *(correction pass)*: see item 6. P3-INV-036.
- **Rejected.** A Harmony patch on `ExitMap`, `PassToWorld`, `MapDeiniter`, faction removal or `PawnGenerator`; a synchronous
  callback (M2) or C-4; a Network `PassToWorld` for a pawn vanilla already passed; a persisted pawn list in the quest part;
  regenerating, rerolling or "fixing" a person beyond the one skill raise; reading fame for capability; a time skip or a falsified
  `agedThroughTick` to test aging; a heuristic that decides a save is disposable.
- **Consequences.** The live game can now create a contractor pawn, but only through the session-armed physical tier. Save
  format stays 5. Owner validation of `RT-PHYX-001…012`, `015`, `016` in RimWorld is the remaining gate (S31 / M1 itself is already
  validated, ADR-053); 3.2 (custody, rescue, groups, group extraction) is not started. Two pre-existing headless tests that encoded the
  placement defect of item 9 were updated, keeping their purpose (Appendix J.2).

### ADR-055 · Phase 3.1 runtime-QA correction: the load order, generic first kinds, role compatibility
- **Status.** **Deviation / clarification, for owner review. Phase 3.1 owner physical validation in progress — NOT a PASS.** Recorded by the correction pass after the owner's first physical run (build `29f31dd`, a
  disposable save). It corrects ADR-054 item 2's and [PHYSICAL_LIFECYCLE § 16.3](PHYSICAL_LIFECYCLE.md#163-the-registry-across-load-corrected-by-the-phase-31-runtime-qa-pass)'s earlier claim that the registry is rebuilt
  "after cross-references, before the first tick"; it changes no principle of ADR-048 to ADR-053 (S31 / M1 timing stays accepted). Details and evidence: [Appendix K](PHYSICAL_LIFECYCLE.md#appendix-k-phase-31-runtime-qa-correction-pass-pr-10).
- **Context.** (1) After a save and load the load pass said "15 bound pawn(s), 0 retained", the read-only verifier "14 not" reserved, the pawn was `Free` at its exit and RELEASE refused; later vanilla discarded retained pawns. The audit of the 1.6 assembly found
  `World.FinalizeInit` (our `FinalizeInit`, where the registry was built) runs before `Scribe.loader.FinalizeLoading()` resolves references, so the pointer-built index was empty, and `World.WorldTick` runs `WorldPawns` before any world component, so
  the first-tick start-up gate is too late for anything exposure-critical. (2) Ordinary Solos were generated as a highthrall, an ancient soldier and Empire royals and champions: the kind chain ranked every loaded humanlike kind. (3) A person could reach first
  materialization with the operational role `Unset`. (4) Three harness defects (a skip counter that must not move, a late intermediate assertion, three near-identical menu labels).
- **Decision.**
  1. **The registry has two load stages.** Stage 1, at `FinalizeInit`: a durable `thingIdNumber` index from persisted values only (never a pointer); until the pointer index exists a pawn is covered iff its own thing id equals the persisted one of a living
     `Deployed`/`Stored` binding and no resolved pointer contradicts it (`BindingRules.BridgeCovers`). Stage 2, at the world component's `PostLoadInit` (the narrowest supported point: after every cross-reference, before any tick): the validated pointer index
     (reference equality, an agreeing persisted thing id) becomes the authority and the bridge closes; the registry quest is ensured from the **durable** retained count; a binding that is unresolved, discarded or disagrees is **reported and never regenerated,
     cleared, healed or reserved on a guess** (P3-INV-037). Only an `Ongoing` quest counts as live. Provenance is not weakened: the persisted `PawnRef` stays authoritative and the thing id is a load-safety bridge for an existing binding, not an identity system.
  2. **A first projection draws its kind only from the encounter faction's generic member pool** (basic member kind first, then its Combat and Peaceful group kinds), admitted by what a kind carries (no boss, leader, royal title, mutant, trader, fixed backstory,
     built-in conditions or abilities, forced traits or xenotype, other faction): structural, never a list of def names; the encounter faction def must offer one; none ⇒ a clean abort before binding, no global fallback (P3-INV-038). Role strength is the role's job.
     Resurrection and every later real change are untouched.
  3. **The role of an NPC Solo contractor is stored early, and a Fixer is not a Phase 3.1 candidate:** `ContractorService.EnsureSoloRoles` stores the role once at bootstrap and load for an NPC Solo contractor (`IsNpcSoloContractor` = `IsSolo` and `IsNpcContractor`),
     never overwriting a stored role and never reconstructing one for a person who ever had a pawn; `RoleDerivation.ForSolo` is contractor-only (anything else is `Unset`, no Fixer mapping); the physical tier's picker selects only NPC Solo contractors; `Plan` refuses an embodied person
     whose role is `Unset` and not derivable (`RoleUnderivable`) instead of inventing one; a role an earlier build stored on a Fixer is left alone. *(The first correction made the derivation total for a Fixer, which the follow-up review reversed: Appendix K.8; P3-INV-039.)*
  4. **Harness only:** a Returned release has no pass action, so `RT-PHYX-002` and `015` assert 0 passed, 0 skipped, 0 refused; `RT-PHYX-009` captures its intermediate facts when the quarantine is observed and asserts the post-custody facts separately;
     `RT-PHYX-010`'s menu labels are `010A SAVE — visitor spawned`, `010B SAVE — post-map`, `010V VERIFY — loaded save` with the family id unchanged. Production RELEASE and P3-INV-031 are unchanged.
- **Rejected.** Weakening provenance to "the same thing id is the same person"; building the pointer index in the first-tick gate (too late); a pawn list persisted in the quest part; a later repair of the registry; giving a Fixer a contractor-style role, or cleaning one an earlier build stored; a named PawnKind blacklist or whitelist; a global kind scan
  or a fallback to one; disabling resurrection; generating a replacement for a discarded pawn; making the corrupted QA save look clean; persisting a test-control object or a "disposable save" flag; Harmony.
- **Consequences.** Save format stays 5; no Harmony; no new durable field. The owner retests on a **fresh** disposable save: `RT-PHYX-001`, `002`, `004`, `009`, `010A` + reload + `010V`, `010B` + reload + `010V`, `011`, `015`. Three existing headless tests that
  encoded the defects were updated and say so (Appendix K.7). 3.2 is not started.

