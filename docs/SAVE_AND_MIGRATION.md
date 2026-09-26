# Save Format, Versioning and Migration

> Network data is versioned from the **first** release and migrated forward for as long as
> saves exist. Related: [DATA_MODEL](DATA_MODEL.md), [COMPATIBILITY § 3](COMPATIBILITY.md#3-external-def-safety),
> [DECISIONS ADR-019](DECISIONS.md).

## Contents

1. [Principles](#1-principles)
2. [Save layout](#2-save-layout)
3. [Persisted type names](#3-persisted-type-names)
4. [Migrations](#4-migrations)
5. [Load pipeline](#5-load-pipeline)
6. [Unsupported versions](#6-unsupported-versions)
7. [Failed migration and quarantine](#7-failed-migration-and-quarantine)
8. [External Def repair](#8-external-def-repair)
9. [Adding The Network to an existing save](#9-adding-the-network-to-an-existing-save)
10. [Removing The Network from a save](#10-removing-the-network-from-a-save)

---

## 1. Principles

1. **One integer version for all Network data**: `NetworkSaveVersion`. It is bumped whenever
   the *meaning or shape* of persisted data changes in a way that needs code to fix. Pure
   additions with safe defaults do **not** require a bump.
2. **Forward-only migrations**, run in ascending order, each idempotent.
3. **Nothing is silently discarded.** Data that cannot be migrated is quarantined with a reason
   and kept for diagnostics until a dev action clears it.
4. **One subsystem's failure never destroys another's data.** Subsystems load and migrate
   independently.
5. **Vanilla-level errors are minimized, never multiplied.** No `Scribe_Defs` on external defs,
   and no custom classes in vanilla-owned containers (letters, archive), except the documented
   ones: the WorldComponent itself, the registry quest part and def, and the comp data (which is
   ignored when the comp is missing).

## 2. Save layout

```xml
<li Class="TheNetwork.NetworkWorldComponent">
  <saveVersion>1</saveVersion>
  <createdWithModVersion>0.1.0</createdWithModVersion>
  <lastSavedWithModVersion>0.1.0</lastSavedWithModVersion>
  <networkSeed>-1873344123</networkSeed>
  <bootstrapped>True</bootstrapped>
  <ids><nextId>1204</nextId><nextEventSeq>5531</nextEventSeq><nextJobSeq>9002</nextJobSeq></ids>
  <actors>…</actors>
  <characters>…</characters>
  <knowledge>…</knowledge>
  <relations>…</relations>
  <obligations>…</obligations>
  <contacts>…</contacts>
  <intel>…</intel>
  <opportunities>…</opportunities>
  <contracts>…</contracts>
  <operations>…</operations>
  <deployments>…</deployments>
  <leases>…</leases>
  <history>…</history>
  <summaries>…</summaries>
  <legends>…</legends>
  <beliefs>…</beliefs>
  <consequences>…</consequences>
  <scheduler>…</scheduler>
  <journal>…</journal>
  <diagnostics>…</diagnostics>
</li>
```

- The root node sits inside vanilla's `<world><components>` list, after factions, world pawns
  and world objects (see [RIMWORLD_INTEGRATION § 2.1](RIMWORLD_INTEGRATION.md#21-worldcomponent--reuse-persistence-root)).
- **Store order is fixed.** New stores are appended *before* `scheduler`, `journal` and
  `diagnostics`, which always come last. Order matters only for readability and for
  deterministic diffs. Cross-store references are IDs, so load order does not change meaning.
- Every store writes **empty** nodes when it has nothing to save, so the layout never varies
  with content.
- Site comp data lives inside each vanilla `Site` node (`<opportunityId>`). Registry quest data
  (Phase 3) lives inside the vanilla quest node and holds **no state**: the reserved-pawn set is
  rebuilt from `CharacterStore` and `DeploymentStore` at load.

## 3. Persisted type names

Scribe writes `Class="Full.Type.Name"` for polymorphic deep saves. These names are **frozen**
once shipped:

| Category | Types (planned) |
|---|---|
| Root | `TheNetwork.NetworkWorldComponent` |
| Actor components | `TheNetwork.Persist.ContractorProfile`, `IntelSourceProfile`, `IssuerProfile`, `SponsorProfile`, `SponsoredProfile`, `IntroducerProfile`, `TraderProfile`, `RivalryProfile` |
| Objectives | `TheNetwork.Persist.AcquireObjective`, `DeliverObjective`, `ReachOpportunityObjective`, `RescueObjective`, … |
| Payloads | `TheNetwork.Persist.ItemPayload`, `CharacterPayload`, `ActorPayload` |
| Events (journal) | `TheNetwork.Persist.Events.*` (unknown classes are dropped quietly) |
| Site comp (vanilla-owned node) | `TheNetwork.WorldObjectComp_NetworkSite`, `WorldObjectCompProperties_NetworkSite` |
| Registry quest part (Phase 3) | `TheNetwork.QuestPart_NetworkRegistry` |

Rules:

- All polymorphic persisted types live in the namespace **`TheNetwork.Persist`**, apart from the
  vanilla-facing classes that must be referenced from XML. Code refactors elsewhere cannot
  rename them by accident.
- **Renaming or removing** a persisted type needs a shim: keep an `[Obsolete]` subclass with
  the old name that loads the data and is converted by the next migration. Vanilla's type
  back-compatibility hook is private
  ([RIMWORLD_INTEGRATION § 2.15](RIMWORLD_INTEGRATION.md#215-backcompatibility--not-usable-for-mods)),
  so shims are the only safe approach.
- Where the set of variants is closed and small, **prefer a `kindKey` string plus flat fields**
  over a class per variant. Scheduler jobs work this way, and so do many objectives if their
  fields fit.

## 4. Migrations

### 4.1 Mechanism

```
interface INetworkMigration { int From; int To; string Name; void Apply(NetworkState s, MigrationContext ctx); }
MigrationRegistry = ordered list, contiguous From→To (1→2, 2→3, …)

After load (in FinalizeInit(fromLoad: true)):
  v = state.saveVersion
  if v == 0 and not bootstrapped → fresh (mod added to an existing save): no migrations; bootstrap on the first tick
  if v > Current → § 6
  for m in registry where m.From >= v: 
      try m.Apply(state, ctx); state.saveVersion = m.To; ctx.Log(m)
      catch → § 7 (stop the chain for the affected subsystem; continue the others)
  state.saveVersion = Current  (written at the next save)
```

### 4.2 Two kinds of migration work

| Kind | Where | Example |
|---|---|---|
| **Structural read** (the field was renamed or moved) | in the entity's `ExposeData` during `LoadingVars`, guarded by `if (Scribe.mode == LoadingVars && loadingVersion < N)`. The legacy field is read into a temporary, and the migration moves it | `standing` renamed to `affinity`: read `standing` when `loadingVersion < 3` |
| **Semantic transform** (data meaning changed) | `INetworkMigration.Apply`, after all loading, with the full state available | split `morale` into `cohesion` + `confidence`; rebuild summaries from records; map old contract kind keys |

`loadingVersion` is read **first** in `NetworkWorldComponent.ExposeData` and exposed to all
stores through a static `NetScribe.LoadingVersion` during load.

### 4.3 Newly added fields

These need no migration and no version bump if the default is correct: `Scribe_Values.Look(ref
x, "x", default)`. If the correct value must be *derived* (for example a new summary counter
backfilled from records), bump the version and derive it in a migration.

### 4.4 Removed fields

Stop reading them. Scribe ignores unknown nodes silently. If their data must be carried into
new fields, read them as legacy (§ 4.2) for one version range. Remove the legacy read code only
when the minimum supported version (§ 6) moves past it.

### 4.5 Ordering and determinism

- Migrations run **after** all stores have loaded and cross-refs have resolved, and **before**
  any runtime cache rebuild and before the first tick. They must not publish events, send
  letters or touch the scheduler except through `ctx` helpers, which queue work for the first
  tick.
- Migrations that need randomness derive it from `(networkSeed, entity.id, migrationName)`.

### 4.6 Testing

Every migration ships with a **fixture save fragment**: an XML of the `NetworkWorldComponent`
node at the old version, plus a headless test that loads it through the migration and asserts
invariants ([DEBUGGING § 6](DEBUGGING.md#6-headless-tests)). Fixture files live in
`Tests/Fixtures/vN/`.

## 5. Load pipeline

| Step | Scribe mode / hook | Network work |
|---|---|---|
| 1 | `LoadingVars` | read `saveVersion` first; read stores through tolerant list loading (per-element try/catch, S5); read legacy fields where versioned |
| 2 | `ResolvingCrossRefs` | vanilla resolves `PawnRef` pointers (warnings for any that are unresolvable) |
| 3 | `PostLoadInit` | defaults for null collections; nothing else |
| 4 | `FinalizeInit(fromLoad: true)` | run migrations; rebuild runtime caches (ID maps, heaps, reverse maps); **no events, no world mutation** |
| 5 | first `WorldComponentTick` → `EnsureStarted` | validators: reference resolution (`ReferenceInvalidated` events), custody audit and re-reservation, scheduler/entity agreement, orphan detection; re-register `SignalBridge`; process queued migration follow-ups; then normal scheduling |

Step 5 is where the world may change (refunds, cancellations, letters). It happens inside the
game's tick, after the game is fully loaded. It runs once per load and is budgeted and logged.

## 6. Unsupported versions

| Situation | Behaviour |
|---|---|
| `saveVersion` **>** current (the save came from a newer Network) | Load best-effort (Scribe ignores unknown nodes). Set `diagnostics.downgradedFrom = v`. Show **one** warning letter and a log message: "This save was made with a newer version of The Network; some Network data may be lost if you save." The simulation continues. Nothing is blocked, because the game cannot be stopped from saving and blocking would only make things worse. |
| `saveVersion` **<** `MinimumSupportedVersion` | Keep the actors, characters, history, summaries and legends that load. Mark the other subsystems `Degraded` (they are Voided with refunds on the first tick). Show one letter. This should be rare. The minimum supported version moves only across major releases, and each such move is documented in the changelog. |
| `saveVersion == 0` but data present (corrupt) | Treat as version 1 and validate. Quarantine what fails. |

## 7. Failed migration and quarantine

- **Per-entity failures** (an exception while migrating or validating one entity): the entity is
  marked `Quarantined(reasonKey, migrationName, exceptionSummary)`. It keeps its state, is
  skipped by all simulation, and is listed by the dev action "Network: quarantine report".
  Dependants (for example contracts of a quarantined actor) are validated. If they cannot
  function, they are Voided with a refund.
- **Per-subsystem failures** (a migration throws at store level): the chain stops for that
  subsystem. It is marked `Degraded` (commands return `SubsystemDegraded`), and the other
  subsystems continue. The next mod version can ship a repair migration keyed on
  `diagnostics.failedMigrations`.
- **Diagnostics recorded** in `DiagnosticsState`: `failedMigrations[] { name, from, to, tick,
  message }`, `quarantine[] { entityRef, reason, tick }`, and `oneTimeWarnings` (a set of keys,
  so each warning is logged once per save rather than every load).
- **Logging**: one `[TheNetwork]` error per failed migration with its name and entity count.
  Details are logged only with verbose logging enabled ([DEBUGGING § 2](DEBUGGING.md#2-logging-policy)).

## 8. External Def repair

External defs are stored as defName strings, so repair is a **string remap**:

- **Built-in remap table** (`NetworkDefRemaps`, code or XML): `(defType, oldName) → newName`,
  for known renames in popular mods and in The Network's own defs.
- **Optional player-facing repair** (a later phase, dev mode): a dialog listing unresolved
  DefRefs, grouped by name with counts. Each group can be remapped to an existing def or
  confirmed as removed.
- **Unresolved after remap**: the owning subsystem applies its missing-target policy
  ([DATA_MODEL § 2](DATA_MODEL.md#2-external-references)). The snapshot labels keep history
  readable.
- **Re-adding a mod**: every DefRef is a string, and resolution caches are per session. If a
  removed mod comes back, its references resolve again. Entities already Invalidated stay
  Invalidated (terminal), but knowledge topics, history subjects and catalog overrides resume.

## 9. Adding The Network to an existing save

1. `World.FillComponents` constructs the component, with `saveVersion = 0` and
   `bootstrapped = false`.
2. `FinalizeInit(true)` detects a fresh state and skips migrations.
3. The first tick bootstraps: `networkSeed`, the pseudo-actors (PlayerProxy, the Exchange),
   faction proxies (lazy), and from Phase 2 the initial contractor population.
4. The XML patch has already added the site comp to the vanilla `Site` def. Existing vanilla
   sites get an inert comp with no data. Harmless.

## 10. Removing The Network from a save

**Supported path: "Prepare save for removal"** (a Mod Settings button, following the
Grandmaster21 uninstall pattern):

1. End and remove the registry quest (Phase 3+), so vanilla sees no null-root quest on the next
   load.
2. Release every reserved pawn: stored pawns become ordinary world pawns (`Decide`).
3. Strip `TheNetwork.*` quest tags from pawns, things and world objects.
4. Convert active Network sites into plain vanilla sites: unbind the comp, which is then inert
   and ignored after removal. Timeouts stay.
5. Refund any escrowed deposits for active contracts through drop pods.
6. Show a summary. The player then saves, disables the mod and reloads.

**Expected errors after the prepared removal:** 1 (the missing WorldComponent class). Without
preparation: at most about 3 (the component class, plus the registry quest root and part in
Phase 3+). In both cases the game remains consistent. This is verified by Spike S6.

**What stays in the world:** vanilla sites (time out normally), pawns (ordinary world pawns,
subject to vanilla GC), delivered items (ordinary items), letters and archive entries (vanilla
classes only, so no errors). The `MainButtonDef` disappears.
