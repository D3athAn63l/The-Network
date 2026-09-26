# Compatibility: Item Catalog, External Defs, DLC and Other Mods

> The Network targets heavily modded games. Nothing may assume that a Def, faction, DLC or mod
> exists, still exists, or will exist later. Related: [DATA_MODEL § 2](DATA_MODEL.md#2-external-references),
> [SAVE_AND_MIGRATION § 8](SAVE_AND_MIGRATION.md#8-external-def-repair), [RISKS](RISKS.md).

## Contents

1. [Principles](#1-principles)
2. [Item catalog](#2-item-catalog)
3. [External Def safety](#3-external-def-safety)
4. [DLC and optional systems](#4-dlc-and-optional-systems)
5. [Interaction with other mods](#5-interaction-with-other-mods)
6. [Compatibility adapter architecture](#6-compatibility-adapter-architecture)

---

## 1. Principles

1. **No hard dependencies.** The Network is standalone. It does not depend on or assume
   Grandmaster21, RegenNanites or Beyond Our Reach, and it does not require Harmony before a
   patch is adopted.
2. **Heuristics over lists.** Eligibility and classification are derived from Def properties.
   Hand-maintained lists of mod items are used only for explicit overrides.
3. **Strings at rest, objects at runtime.** Persisted external references are strings or IDs,
   resolved silently ([DATA_MODEL § 2](DATA_MODEL.md#2-external-references)).
4. **Fail small.** A missing reference cancels the smallest affected unit (one Intel request,
   one contract, one lease) with an explanation, and never the subsystem or the save.
5. **Optional features register themselves.** DLC and mod integrations add capabilities through
   adapters when they are present. The core never branches on them in scattered places.
6. **Source mod is contextual evidence, not ownership** (master § 14, § 57). An item's source
   package can make an active faction from the same package a more plausible holder, trader or
   guard. It never makes that faction an enemy, never makes it the owner automatically, and is
   never a dependency: the Network runs, and searches for "Weirdium", without knowing what any
   mod is (master § 56). See [ARCHITECTURE § 6.14.1](ARCHITECTURE.md#6141-opportunity-source-and-context-resolution).

---

## 2. Item catalog

### 2.1 Build timing and lifetime

- The catalog is built **once per game session**, lazily, on the first call that needs it
  (opening the Network UI, submitting Intel, or validation). The build runs on the main thread
  at a safe moment (never inside a Scribe pass).
- Defs are immutable after startup, so the catalog is valid for the whole session. **Changing
  the active mod list requires a restart in RimWorld**, which rebuilds the catalog. No other
  invalidation exists or is needed.
- **The catalog is not serialized.** It is a cache of Def properties. Saving it would only
  create staleness bugs and save bloat.
- Expected cost: one pass over `DefDatabase<ThingDef>.AllDefsListForReading` (typically
  5,000–25,000 defs in heavy mod lists) plus one pass over recipes to index products. That is on
  the order of 10–60 ms, once. It is timed and logged at debug level. If a spike shows it is too
  slow, it can be split across frames with `LongEventHandler`.

### 2.2 Entry

```
CatalogEntry (runtime)
  def: ThingDef                     defName: string
  label, description snippet
  source: { packageId, modName, isCore, isOfficialDlc }
  categoryPath: ThingCategoryDef[]  (primary top-level for grouping)
  market: { baseValue: float (with default stuff for stuff-made), stackLimit: int, valuePerStack }
  techLevel
  tradeability: Tradeability        tradeTags: string[]
  craftable: bool                   recipes: RecipeDef[] (index, capped)
  usedByRecipes: bool               (appears as an ingredient; metadata only, never "need")
  equipment: { isWeapon, isApparel, hasQuality, madeFromStuff, isBiocodable? }
  flags: { isStuff, isDrug, isMedicine, isFood, isBodyPart/implant, isMineable/deep, isRawResource }
  rarity: RaritySignals             (see 2.4)
  verdict: Eligible | Unusual | Ineligible
  reasons: CatalogReason[]          (codes explaining the verdict)
  override: Auto | Allowed | Blocked   (from settings)
  effective: bool                   (verdict + override + runtime failures → can be requested)
```

### 2.3 Eligibility heuristics (conservative)

The Network runs in heavily modded games, where many valid items break vanilla conventions. So
the catalog separates two very different things, and the player's `Allowed` override is what
rescues valid modded content that the heuristics get wrong (master § 6):

- **A. Technical impossibility (Ineligible, not overridable).** The def cannot exist as a
  standalone Thing in someone's possession, so no opportunity, stash or delivery could ever
  contain it. Overriding would only produce a broken activity.
- **B. Suspicious, unusual or heuristic (Unusual, overridable).** The def looks odd by vanilla
  conventions but may be perfectly valid. Hidden by default; the "Show unusual items" toggle
  reveals it and `Allowed` makes it an ordinary eligible item.

**A. Ineligible: technically non-materializable** (every rule states why):

| Reason code | Rule | Why it cannot be a possessed item |
|---|---|---|
| `NotAThingCategory` | `category` is `Pawn`, `Plant`, `Projectile`, `Filth`, `Gas`, `Attachment`, `Mote`, `Ethereal`, `PsychicEmitter` or `None` | pawns are generated from PawnKinds, not ThingDefs; plants, filth, gas, motes, attachments (fire), projectiles and ethereal controllers are map effects or internal objects that exist only in place or in flight. Blueprints (`Ethereal`) are covered here. |
| `NonMinifiableBuilding` | `category == Building` and not `Minifiable` | an installed structure cannot be carried, stashed or delivered. Frames (`thingClass Frame`) are covered here. Minifiable buildings are **B** (`Building`). |
| `NoThingClass` | `thingClass == null`, abstract, or not a `Thing` subclass | `ThingMaker` cannot instantiate it |
| `Corpse` | `IsCorpse` | a corpse is made from a dead pawn and cannot be generated on its own (master § 6: "corpses unless intentionally supported later") |
| `Unfinished` | `isUnfinishedThing` | a work-in-progress object bound to a bill and a worker; not a standalone item |
| `MinifiedWrapper` | `thingClass` is `MinifiedThing` (the placeholder) | the wrapper needs an inner building; the building's own def is what gets requested |
| `DestroyOnDrop` | `destroyOnDrop` | destroyed when placed or dropped, so it cannot sit in a stash or arrive by pod (vanilla's `PlayerAcquirable` is false for it) |
| `EngineInternal` | a small, documented list of engine placeholders that pass the rules above, matched by `thingClass` type, not by mod (for example the active drop pod: `category Item`, `thingClass ActiveTransporter`; skyfallers are already `Ethereal`) | internal carriers of other things, never items in their own right |

**B. Unusual: heuristics, overridable by `Allowed`:**

| Reason code | Rule |
|---|---|
| `NotHaulable` | neither `alwaysHaulable` nor `designateHaulable` (a convention many mods skip) |
| `NoLabel` | empty label (the UI falls back to the defName) |
| `NoGraphic` | no `graphicData` (static check only; the catalog **never** instantiates Things, because `ThingMaker.MakeThing` allocates thing IDs and runs `PostMake` side effects) |
| `UnusualThingClass` | a `thingClass` outside the usual item classes |
| `NotTradeable` | `tradeability == None` **and** no `tradeTags` |
| `NoMarketValue` | `BaseMarketValue <= 0` |
| `QuestOrStoryItem` | tagged only in quest-reward ThingSetMakers, or other signals of "special" (no recipes, not tradeable, unique comps). Artifacts and progression keys stay requestable if valid, at severe prices and low odds (master § 80). |
| `Building` | a minifiable building's def |
| `Chunk` | stone chunks and slag (valid items, silly to request) |
| `Implausible` | extreme value or mass outliers relative to the category |

**Eligible**: everything else.

**Runtime safety net** (`FailedToGenerate`). A heuristic can be wrong in both directions, so the
final authority is what actually happens. If creating a def's Things for an opportunity or a
delivery ever throws or yields an invalid Thing, the def is marked **unusable for the rest of the
session** (whatever its override), the affected activity is **invalidated safely** (Intel
invalidated, contract voided, opportunity invalidated) with a **full refund** because this is a
technical failure, and a diagnostic reason is logged once and shown in the catalog and the letter
("could not be produced in this game: <reason>"). The def is evaluated again next session.

Heuristics are **reason codes, not a single boolean**. That makes classification explainable in
the UI ("hidden because it is not tradeable and has no recipe") and debuggable (§ 2.8).

**The catalog never infers what the player needs.** It does not rank by colony demand, does not
suggest items, and does not read the player's stockpiles or research (master § 53). Sorting is
by name, category, source mod or value, at the player's choice.

### 2.4 Rarity signals

These are used by Intel duration, lead probability and procurement pricing. They are **never
shown as a single "rarity" number**, which would be false precision.

| Signal | Derivation |
|---|---|
| `tradeReach` | vanilla `TraderKindDef` stock generators that could stock the def (indexed once), or `tradeTags` overlap with trader tag sets |
| `craftable` | at least one `RecipeDef` produces it |
| `valueBand` | log-scaled `BaseMarketValue` band |
| `mineable` | appears as `building.mineableThing` or has `deepCommonality > 0` |
| `generatorPresence` | appears in `ThingSetMakerDef` filters (loot or reward) |
| `techLevel` | vs. the world's faction tech levels (higher means rarer) |
| `modded` | not Core or official DLC. A tiny weight only, so modded items are not treated as rare merely for being modded. |
| `unique` | artifact-like or single-use progression signals (quest-reward-only, no recipe, no trade). Raises procurement price and difficulty and lowers lead probability (master § 80). |

### 2.5 Per-item overrides

- Values are `Auto` (use the verdict), `Allowed` (Unusual becomes Eligible; an Ineligible
  technical exclusion stays Ineligible, and the UI says why) and `Blocked` (never requestable,
  hidden).
- Stored in **`ModSettings`**, as a `Dictionary<string defName, OverrideValue>`. They apply to
  all saves: the player's taste in what the Network should offer is a user preference.
- **Unknown defNames are retained** (a mod may be removed temporarily).
- Future option (not Phase 1): per-save overrides layered on top, for challenge runs.

### 2.6 Removed defs

The catalog cannot contain removed defs. Persistent references to them resolve as missing and
follow [§ 3](#3-external-def-safety). Settings overrides for removed defs stay dormant.

### 2.7 Source-mod metadata

`def.modContentPack` gives `PackageId`, `Name`, `IsCoreMod` and `IsOfficialMod`. The UI can
filter by source mod. History snapshots store `packageId` so that removed content stays
attributable ("Tenebrite (Beyond Our Reach, not loaded)").

The same metadata is **contextual evidence for opportunity generation**. The catalog keeps a
session index `packageId → FactionDef[]` (from each `FactionDef.modContentPack`), which the
source resolver joins against the live faction list when an opportunity is generated. A faction
from the item's own package that is present, undefeated and fitting gains relevance; one that is
absent, defeated or unfitting is simply not a candidate, and generation falls back through the
hierarchy ([ARCHITECTURE § 6.14.1](ARCHITECTURE.md#6141-opportunity-source-and-context-resolution)).
Source-package overlap also counts toward an Intel source's quality for that topic (master § 13).

### 2.8 Debugging classification

- Dev action **"Network: catalog report"** writes a CSV to the save folder's `TheNetwork/`
  subfolder: `defName, label, packageId, verdict, reasons, override, baseValue, tradeability,
  craftable, rarity signals`.
- Dev action **"Network: explain item…"** picks a def and prints its verdict and the reason for
  each rule.
- Dev action **"Network: rebuild catalog"** forces a rebuild (for testing heuristic changes in
  development builds).

---

## 3. External Def safety

The example that must work: a save references `BOR_Tenebrite` and Beyond Our Reach is later
removed.

| Missing thing | Detected at | Behaviour | Player sees |
|---|---|---|---|
| **ThingDef** in an Intel topic | load validation / resolve | Intel → `Invalidated`, full refund | letter: "Your contact closed the inquiry: the item no longer exists in this world." |
| **ThingDef** in a procurement objective | load validation / checkpoint | Contract → `Voided(DefMissing)`, deposit refunded, operation aborted with forces returned unharmed | letter |
| **ThingDef** in an opportunity payload (site not yet visited) | load validation | Opportunity → `Invalidated`. The vanilla site stays and times out normally. Vanilla may log errors for the Things of the missing def inside the stash; the Network adds none. | letter; the site label is unchanged |
| **ThingDef** in a lease | materialization / validation | lease → `WrittenOff` | contractor narrative only |
| **ThingDef** in history, knowledge or legends | never "missing": snapshots and strings | reads "3× Tenebrite (Beyond Our Reach)" | unchanged history |
| **PawnKindDef** for a character or template | materialization | fallback chain: template kinds, then origin faction def kinds, then vanilla defaults (for example a mercenary kind), then any humanlike kind | nothing |
| **FactionDef** (the faction instance is removed by vanilla on load) | load validation | proxy actor → `Destroyed(FactionVanished)`. Contracts it issued → Voided. The org's origin link is cleared (the org continues). | letter only if the player had active dealings |
| **Faction instance** removed at runtime (temporary factions) | registry `Notify_FactionRemoved` / validation | clear the `encounterFaction` reference; recreate on the next deployment | nothing |
| **Transport system** removed (for example a shuttle mod) | delivery planning | delivery method falls back: shuttle, then drop pods, then walk-in, then Hold | the delivery ETA may change, with a letter |
| **Optional DLC** missing (for example Odyssey removed) | load validation (`TileRef` layer check) and capability registry | orbit opportunities → `Invalidated`. Contracts needing space logistics → `Voided` with a refund. Knowledge `layer:Orbit` stays as data. | letter |
| **Compat adapter** disabled (its mod was removed) | adapter registration at startup | archetypes and capabilities it registered are absent. Entities using them are validated as missing (by archetype key) and Invalidated. | letter |
| **Quest or site Def** (a Network def removed in a future version) | load | Network defs are ours, so a migration remaps or retires them before removal. The registry quest root is handled in [SAVE_AND_MIGRATION § 10](SAVE_AND_MIGRATION.md#10-removing-the-network-from-a-save) | — |
| **Vanilla SitePartDef** used by a live site | vanilla load | vanilla drops the part ("Some site parts were null"). Our validation sees a site without its stash part, so the opportunity is `Invalidated`. | letter |

Rules:

- Invalidation happens **once**, is recorded, and emits `Reference.Invalidated` plus the
  subsystem's own event.
- **History, summaries, relationships, legends and unrelated entities are never touched** by an
  invalidation.
- **Money is always settled** in the player's favour when the invalidation is not the player's
  fault.
- Every class of miss is logged **once per session** in aggregate (for example "[TheNetwork] 3
  references to missing ThingDef 'BOR_Tenebrite' were invalidated (2 intel, 1 opportunity)").

---

## 4. DLC and optional systems

| DLC | Gate | Adds (when active) | Without it |
|---|---|---|---|
| **Royalty** | `ModsConfig.RoyaltyActive` | shuttle delivery method (`Ship_Shuttle` TransportShipDef); empire faction proxies with titles as flavour | drop pods and walk-in |
| **Ideology** | `ModsConfig.IdeologyActive` | optional `HistoryEvent` adapter (precepts react to Network deeds); ideo-aware doctrine flavour | none |
| **Biotech** | `ModsConfig.BiotechActive` | xenotype snapshot on characters; mech-related threat topics | fields left null |
| **Anomaly** | `ModsConfig.AnomalyActive` | tag-hygiene checks for duplication; entity threats as topics | none |
| **Odyssey** | `ModsConfig.OdysseyActive` | orbit layer tiles for opportunities; archetypes such as orbital salvage, asteroid sites and mech platforms (reusing Odyssey SitePartDefs); a gravship logistics capability for contractors; gravship landing on Network sites | none. The Network works fully on the surface. |

**Mechanics:**

- DLC types are in `Assembly-CSharp`. Code touching them is inside `Compat.<Dlc>` classes and is
  called only after the gate check. Static `DefOf` fields for DLC defs use `[MayRequire<Dlc>]`,
  or are looked up with `GetNamedSilentFail`.
- **Odyssey specifics**: `TileRef` stores `layerDef`. Layer checks happen before any tile use.
  `TileFinder` is called with `canBeSpace` / `layer` only by the Odyssey module. Contractors
  have a `logistics.space` capability, added only when Odyssey is active and gained through
  sponsorship or content.
- **Removing a DLC mid-save** is handled like a removed mod (§ 3).

---

## 5. Interaction with other mods

| Mod category | Risk | Mitigation |
|---|---|---|
| **Quest frameworks and quest-heavy mods** | iterate or modify all quests; might act on our registry quest | the registry quest is hidden, has no expiry, and its parts do nothing on generic callbacks; the root def is flagged non-generating. Spike S9 includes a smoke test with popular quest mods where possible. |
| **World-pawn cleaners and GC tweakers** | discard pawns vanilla would keep | `Notify_PawnDiscarded` makes the character `Lost` (graceful). Documented in the FAQ. |
| **Map-generation and site mods** that alter or destroy vanilla sites | our site vanishes, or its parts change | comp `PostDestroy` and reconciliation mark the opportunity `Vanished` or `Destroyed`. The stash part is validated at engagement. |
| **Faction mods** (many factions, removal tools) | proxy explosion, missing factions | proxies are lazy (only factions that take part). A missing faction becomes Destroyed. |
| **Performance mods** (tick throttling, world-pawn tick skipping) | job timing jitter | the scheduler is due-tick based and tolerant of skipped ticks. Nothing depends on per-tick cadence. |
| **Save editors and cleaners** | removed our component or quest | the load pipeline handles a missing registry quest (recreate) and missing data (fresh bootstrap). |
| **Combat overhauls** (for example Combat Extended) | pawn gear generation for deployments | vanilla generation APIs are used, so the overhaul applies its own rules. Leases of CE-incompatible defs fall back. |
| **Other "contractor", "mercenary" or "bounty" mods** | conceptual overlap | no shared state. Coexist. Future compat adapters may map their factions to proxies. |
| **Grandmaster21 / RegenNanites** | none | no interaction assumed. They were references only. |

---

## 6. Compatibility adapter architecture

```
interface ICompatModule {
  string Key;                     // "odyssey", "royalty", "mod.<packageId>"
  bool ShouldActivate();          // ModsConfig / ModLister.GetActiveModWithIdentifier(packageId) != null
  void Register(CompatRegistry r);// archetypes, delivery methods, logistics capabilities, topic keys, name packs
}
```

- Modules are discovered by reflection over the Network assembly at startup, a small known set.
  Each activates only if its gate passes, and registration happens inside try/catch. A throwing
  module is disabled with one error and the rest continue.
- **Mod-specific adapters** go in the same assembly behind a packageId check, or in a separate
  optional patch mod. They **only register content**: archetype variants, text, topic aliases,
  and **association hints** for the source resolver (for example "these items are usually traded
  by that faction"). They never change core behaviour, and the base system works without them
  (master § 58). No adapter is needed for the ordinary case: a Tenebrite lead that names Tenebral
  forces comes from the generic same-package evidence when that faction is present and fits, not
  from a Beyond Our Reach adapter.
- The **registry keys** (archetype keys, delivery method keys, capability keys) are the strings
  that persisted entities reference. A missing registration at load is exactly the "disabled
  compat adapter" case in § 3.
