# Architectural Decision Records

> A lightweight ADR log. Each record gives the decision, the alternatives we rejected, and the
> consequences. Status values: **Accepted** (binding), **Conditional** (binding unless the named
> spike fails), **Superseded**. Add new records at the end. Never renumber.

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
| 015 | Sites = vanilla Site + vanilla parts + injected WorldObjectComp | Conditional (S1, S2) |
| 016 | Item catalog is a session cache; overrides in ModSettings | Accepted |
| 017 | Zero Harmony for Phases 1–3 | Accepted |
| 018 | Optional DLC via runtime gating inside one assembly | Accepted |
| 019 | Single integer save version + ordered migrations + quarantine | Accepted |
| 020 | UI through read models and commands | Accepted |
| 021 | Sparse directed relationship edges with lazy decay | Accepted |
| 022 | Persisted due-tick scheduler with budget and stagger | Accepted |
| 023 | Temporary per-organization encounter factions | Conditional (S10) |
| 024 | Terminal entities are compacted; actors become tombstones | Accepted |

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

### ADR-015 · Sites = vanilla Site + vanilla parts + injected WorldObjectComp (Conditional: S1, S2)
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

### ADR-016 · Item catalog is a session cache; overrides in ModSettings
- **Decision.** Build lazily once per session from DefDatabase. Do not serialize it. Per-item
  overrides are stored by defName in ModSettings and kept even for unknown defs.
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
