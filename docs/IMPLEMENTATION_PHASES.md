# Implementation Phases

> "One-shot architecture, phased implementation." Each phase is a **vertical slice** that is
> playable, saveable and removable, and each builds on persistent structures that do not need
> rewriting. Related: [ARCHITECTURE § 10](ARCHITECTURE.md#10-what-is-stable-now-and-what-stays-replaceable),
> [RIMWORLD_INTEGRATION § 5](RIMWORLD_INTEGRATION.md#5-runtime-spikes), [RISKS](RISKS.md).

## Contents

1. [Phase plan at a glance](#1-phase-plan-at-a-glance)
2. [Changes to the proposed sequence, and why](#2-changes-to-the-proposed-sequence-and-why)
3. [Rules for every phase](#3-rules-for-every-phase)
4. [Phase 1: Foundation + Intel](#4-phase-1-foundation--intel)
5. [Phase 2: Contractors (abstract) + Procurement](#5-phase-2-contractors-abstract--procurement)
   - [Phase 2.5: Abstract spatial continuity + player-contract Field Log](#5a-phase-25-abstract-spatial-continuity--player-contract-field-log)
   - [Phase 2.75: Contractor career foundation](#5b-phase-275-contractor-career-foundation)
   - [Phase 2.9: Runtime test infrastructure](#5c-phase-29-runtime-test-infrastructure)
6. [Phase 3: Abstract ↔ physical lifecycle](#6-phase-3-abstract--physical-lifecycle)
7. [Phase 4: Player as contractor, contract board, bidding](#7-phase-4-player-as-contractor-contract-board-bidding)
8. [Phase 5: Social layer](#8-phase-5-social-layer)
9. [Phase 6: Organizational lifecycle and legends](#9-phase-6-organizational-lifecycle-and-legends)
10. [Phase 7+: Expansions](#10-phase-7-expansions)
11. [Deliberately deferred](#11-deliberately-deferred)

---

## 1. Phase plan at a glance

| Phase | Theme | Proves | Highest-risk spike(s) |
|---|---|---|---|
| **0** | Architecture (this branch) | the design | — |
| **1** | Foundation + Intel + Fixer foundation | global cast in ModSettings → world snapshot → Fixers → Comms Console gate → catalog → persistence → scheduling → multi-lead Intel → source/context resolution → opportunity → vanilla site → physical loot, taken in part or in full → cleanup → history | S1, S2, S5, S6, S8, S19 |
| **2** | Contractors (abstract) + Procurement | about 100 contractor identities (Solos to companies) from the world's cast, capability split from NPC simulation, Open and Direct contracts with the offer path, Fixer-mediated quotes, deposits and insurance, deterministic resolver, drop-pod delivery, willingness/refusal, relationships core, history changing behaviour, failures that leave a last known location | S13 |
| **2.5** | Abstract spatial continuity + player-contract Field Log | every contractor has hidden approximate geography (one anchor, coarse lazy journeys, no icons, no caravans, no per-tick work); operations start from the real anchor and work in a hidden region on their committed timeline; Last Known Locations appear where the contractor actually was; the player's running job has a short Field Log | S20 |
| **2.75** | Contractor career foundation | work changes a contractor: numeric reputation beneath the fame band, a cumulative career record, exact contractor money in the existing funds, equipment advancement, derived `CareerNeed` and Tags; no second reputation, wealth or stat system | — (headless; the soak reports the distributions) |
| **2.9** | Runtime test infrastructure | an in-game regression runner that exercises the real production services in an isolated sandbox and reads the live game strictly read-only, so it is safe to press in a real colony; stable test IDs, time-sliced, never saved, no Harmony, no player UI; Phase 3 adds its own scenarios to it | — (headless `Runner.*`; **owner runtime validation PASSED** in a fresh Dev Quicktest colony and the real modded colony) |
| **3** | Abstract ↔ physical lifecycle (**design reviewed; not implemented**; three subphases 3.0 / 3.1 / 3.2, [§ 6](#6-phase-3-abstract--physical-lifecycle)) | one authority per person; Known Characters keep one pawn for life; physical Episodes reconciled exactly once; custody and rescue; a separate physical test tier. Leases, sponsorship, in-person delivery and contract inheritance are **re-scoped for owner decision** | **S9r**, S10, S11, S12, S14, S21–S24 |
| **4** | Player as contractor + NPC contract board + full bidding | player registration (same faction), NPC-issued contracts, competing offers, competitors at opportunities, public reputation epithets | — |
| **5** | Social layer | rumors and beliefs, gossip, favors and debts, introductions, perceived reputation, sanctions and blacklists, morale v2 | — |
| **6** | Organizational lifecycle + legends | contested succession, retirement transformation, fragmentation, mergers, legends | — |
| **7+** | Expansions | black and confidential contracts, evidence and witnesses, joint operations, Odyssey orbital work, smuggling, compat packs, optional vanilla presentation adapters | S15, S16 |

## 2. Changes to the proposed sequence, and why

The Phase 0 brief proposed: 2 = contractors + procurement + abstract→physical; 3 = player registration
+ NPC board + physical delivery; 4 = relationships, rumors, …; 5 = leadership, succession,
retirement, legends.

**Changes:**

1. **The abstract ↔ physical lifecycle is split out of Phase 2 into its own Phase 3.** This is
   the highest-risk system. It depends on the registry-quest custody mechanism (Spike S9), which
   cannot be proven statically. Putting it in the same phase as the first contractors would
   leave Phase 2 blocked on its riskiest unknown. As its own phase, Phase 2 still ships a
   complete loop (procurement resolved abstractly, delivered by drop pods), and Phase 3 can fail
   over to the documented fallback without disturbing contracts.
2. **A relationships *core* (standing, trust, counters, lazy decay) moves into Phase 2.**
   Refusal and willingness need it, and "history must alter future behaviour" should be true
   from the first contractors onward. Rumors, gossip, favors and introductions stay in Phase 5.
3. **Minimal succession (leader dies, next in line takes over) moves into Phase 2.** Contractors
   can die from Phase 2 onward, so organizations must survive leader death from then on.
   Contested succession and fragmentation stay in Phase 6.
4. **Physical delivery by contractors *in person* goes to Phase 3** with the lifecycle. Phase 4
   (the proposed Phase 3) focuses on the player-as-contractor and the NPC board, which reuse the
   Phase 3 machinery.
5. **Public reputation epithets arrive in Phase 4**, when there is enough history and enough
   actors to make them meaningful. Summaries and counters exist from Phase 1.
6. **Reconciled with the master design** ([ARCHITECTURE § 14](ARCHITECTURE.md#14-master-design-reconciliation)).
   The phase split is unchanged. Phase 1 gains the player's choice of contact, a simplified
   source resolver, a subset of the Intel divergence classes, extra cargo and a proof of partial
   recovery (S19). Phase 2 gains one consequence rule, a "last known location" recovery
   opportunity, because the design says failure must create content (master § 3.4, § 24).
7. **Final owner decisions** ([ARCHITECTURE § 14.3](ARCHITECTURE.md#143-final-owner-decisions-foundation-pass)).
   The phase split is still unchanged. Phase 1 adds the global cast (settings schema, generation
   when missing, world snapshot), Fixers as Intel sources, the Comms Console gate and multi-lead
   searches. Phase 2 activates the contractor side of the cast and Fixer-mediated procurement.

## 3. Rules for every phase

- **Only build what the phase uses.** No speculative managers, no Defs for systems not yet
  built, no fake interfaces. Store *slots* in the root layout are the one exception: they are
  added as empty nodes from Phase 1 so the layout never shifts.
- **Every new persisted field has a default and a documented migration story.**
- **Every phase ends with**: save/load tests at each new state; the mod-removal test (S6
  extended); the external-mod-removal test (S7 extended); a performance report; updated docs.
- **Update this documentation in the same PR** as any architectural deviation.
- **From Phase 3 on, new multi-step behaviour ships with runtime scenarios** (stepped cases and sandbox ports
  on the Phase 2.9 runner, [RUNTIME_TESTING § 14](RUNTIME_TESTING.md)) alongside its headless tests.
- **Spikes first**: a phase's listed spikes are run and documented before its content is built.

---

## 4. Phase 1: Foundation + Intel

### 4.1 Target playable flow (Phase 0 brief, aligned with master § 9 and § 17)

1. Start a game (the world snapshots the global cast). 2. With a usable Comms Console, open The
Network. 3. Choose a contact (a Fixer, a faction or the Exchange) and search the eligible item
catalog. 4. Request Intel about an item, **with no quantity**. 5. Pay the fee that contact
charges. 6. The search persists over time ("several days", depending on the contact). 7. A round
resolves. 8. One plausible opportunity is generated from the item and the world context, or
nothing credible is found. 9. A physical world location is added. 10. The player travels there,
and may keep the same search going for another lead meanwhile. 11. The desired item physically
exists there, in the amount the opportunity decided (which may differ from the report). 12. The
player can acquire some or all of it, without having to kill every defender. 13. The site
resolves and cleans up. 14. Meaningful history is recorded. 15. Save and load work throughout.

### 4.2 Smallest slice that proves the chain

| Link | Phase 1 implementation | Explicitly not in Phase 1 |
|---|---|---|
| **Catalog** | full catalog (verdicts, reasons, overrides in ModSettings, source mod, market value); UI search, filter and sort; dev reports | rarity-driven pricing beyond the fee |
| **Global cast** | `NetworkSettings` schema with `NetworkSettingsVersion` and tolerant template loading (a malformed entry is quarantined, the rest kept); generation of the roster when none exists (about 100 contractor templates and a small set of Fixer templates, compositional names from Phase 1 name pools); "regenerate generated entries" preserving custom ones; stable template IDs | a full cast editor UI (a minimal path is enough); importing new templates into an existing world |
| **World snapshot** | first-tick import of enabled templates into `WorldCastSnapshot`; **Fixers instantiated** as actors (world-local ids, template provenance); contractor entries kept as snapshot data for Phase 2; nothing written back to settings | contractor actors |
| **Persistence** | `NetworkWorldComponent` root with every store slot (most empty); `saveVersion = 1`; tolerant list loading; typed IDs; `DefRef`, `FactionRef`, `WorldObjectRef`, `TileRef`; Known Character **records** for Fixers (no pawns) | `PawnRef` (unused until Phase 3) |
| **Scheduling** | scheduler with budget and staggering; job kinds `intel.round`, `opp.sample`, `opp.close`, `history.sweep`, `compact.sweep` | periodic contractor upkeep |
| **Events** | event bus + journal; the Phase 1 events from the [catalog](EVENTS_AND_HISTORY.md#2-event-catalog) | consequence engine, gossip |
| **Actors** | `PlayerProxy`; **Fixers** (`Individual` + `FixerProfile` + `IntelSourceProfile`, each embodying a Known Character record) from the snapshot; one `Institution` "the Exchange" (the generic information network) with `IntelSourceProfile`; lazy `FactionProxy` actors, both for the site's holder or defenders (so history can name them) and as **Intel contacts** (non-hostile factions, with a derived `IntelSourceProfile`) | contractors; the Fixer's brokerage role (Phase 2) |
| **Comms gate** | `CommsAccessAdapter`: every outgoing Network command requires a usable vanilla Comms Console; refusal with a reason and a disabled button; reading the Network needs no console; nothing in progress is affected when the console is lost | other communication methods |
| **Intel state** | the full Intel machine ([STATE_MACHINES § 1](STATE_MACHINES.md#1-intel-request)): **one request, many leads**, search rounds, continue or end after a lead, cancel and invalidate with refunds; **fee, round duration, reliability and continuation policy from the chosen contact**, frozen at submission; **topic only, no quantity anywhere** | intel topics other than items; quality intent (master § 79) |
| **Opportunity generation** | a **simplified source resolver** that keeps the full rule set ([ARCHITECTURE § 6.14.1](ARCHITECTURE.md#6141-opportunity-source-and-context-resolution)): candidates from the item's package, live factions (stance, defeated, hidden, tech) and world; the master § 15 hierarchy as the prior; a non-hostile same-package faction is never made a guard; "no credible source" is a real result. Archetype **`GuardedCache`** (vanilla `ItemStash` + one threat part chosen by the resolved context and threat points from `Outpost` / `BanditCamp` / `AmbushHidden` / `Manhunters` / `SleepingMechanoids`, or unguarded for an abandoned cache) **and** outcome **`NoCredibleLead`**. Quantity chosen by the generator. **Divergence subset:** Accurate, Partial, Outdated, Bad, Jackpot, Complication, Trap (the cache variants can express these); a confidence descriptor on the lead. A little archetype-appropriate **extra cargo**. | other archetypes (trader or owner holds it, convoys, salvage, orbital); Misinformation (needs source motives, Phase 5); Contested (competitors, Phase 4) |
| **World object / site** | `SiteAdapter` with a vanilla `Site`, `TimeoutComp` (the lead's operational window), and `WorldObjectComp_NetworkSite` injected by XML patch into the vanilla `Site` def; quest tag `TheNetwork.Opp.<id>` | custom SitePartDefs, custom WorldObjectDefs (unless S19 requires a minimal site part) |
| **Physical loot** | Things created at materialization into `SitePart.things` (seeded quality and stuff where applicable); placed by vanilla `GenStep_ItemStash`. Taking part of it and leaving with defenders alive is a valid, recorded outcome (S19). | — |
| **Cleanup** | comp callbacks + fallback sampling + reconciliation for vanished sites; closing and archiving | — |
| **History** | ledger (Notable+ records with participants, place, subject, awareness Public/Involved); player summary counters; retention sweep; History tab list with narrative lines | epithets, legends, gossip |
| **Payment** | `PaymentAdapter` with beacon silver (`LaunchSilver`) and drop-pod refunds (Spike S4 picks the final UX) | debts, obligations |
| **UI** | `MainButtonDef` → tabs **Intel** (contact choice with Fixer descriptors, catalog search with the "show unusual items" toggle and per-item overrides, request dialog with fee and no quantity field, active requests with elapsed time and a vague estimate, continue / end search, leads with reported cargo, threat, distance, window and confidence, and look-at-site) and **History**; a minimal Mod Settings section for the global cast (regenerate generated entries, enable or disable, rename). The Procurement, Contracts and Contractors tabs are **not shown** until their phases. | — |
| **Letters** | vanilla letters: lead delivered, search concluded with nothing, invalidated, expired soon (optional), claimed | — |
| **Diagnostics** | logging policy; validators (IDs, orphans, external refs, scheduler agreement, caps); dev actions from the Phase 1 rows of [DEBUGGING § 3](DEBUGGING.md#3-dev-actions); timing | — |
| **Removal** | "Prepare save for removal" | — |

**Defs created in Phase 1 (and only these):** `TheNetwork_MainButton` (`MainButtonDef`), one
XML patch adding `WorldObjectCompProperties_NetworkSite` to the vanilla `Site` def, the name pools
for organization and Fixer names (vanilla `RulePackDef` grammar or a small Network def), and keyed
translation strings. **No** QuestScriptDef, SitePartDef, WorldObjectDef, FactionDef or IncidentDef,
unless S19 shows that partial recovery needs one minimal Network SitePartDef (ADR-015).

### 4.3 Phase 1 acceptance criteria

| # | Criterion |
|---|---|
| A1 | All 15 flow steps work in a vanilla + DLC game **and** with Beyond Our Reach (Tenebrite) loaded. |
| A2 | Save and load at each persisted Intel state (Submitted, Searching, **between search rounds**, AwaitingDecision, Concluded, Cancelled, Invalidated, Closed) and each Opportunity state (Materialized, Engaged, Claimed/Abandoned/Expired) gives identical outcomes: a continued search resumes deterministically with the same next round, and nothing is rerolled (S8). |
| A3 | Removing BOR mid-search and mid-site gives Invalidated with a refund and letters; zero Network-originated errors (S7). |
| A4 | Removing The Network, prepared or not, gives exactly 1 error in Phase 1 (the missing WorldComponent class; the site comp contributes none), and the game stays playable (S6). |
| A5 | Adding The Network to an existing save bootstraps cleanly. |
| A6 | Idle per-tick cost is unmeasurable; the S18 harness report is attached. |
| A7 | History shows a coherent narrative line for each resolved Intel and each claimed or abandoned opportunity; records survive the retention sweep as specified. |
| A8 | No Harmony. No Network code runs per tick beyond the idle check (verified by timing). |
| A9 | Headless tests: NetRng, scheduler ordering and budget, the Intel transition table (including rounds and continuation), settings migrations and template quarantine (fixture settings files), retention policy, tolerant loader (fixture with a corrupted element), **catalog verdicts** (every hard exclusion, every heuristic, overrides), **source resolver** (cases in A11), and a check that no Intel type, command or formula has a quantity input. |
| A10 | **Loot without extermination (S19).** On a guarded Network opportunity the player enters, recovers **only part** of the target payload, and leaves by caravan and by pods while some defenders are still alive. The caravan or pods can leave; recovered items stay recovered; the opportunity resolves as Claimed with a partial `recoveredBand`; history records a partial recovery, not a required victory; no loot is duplicated (including re-entry or a second departure); the site cleans up as vanilla does. Leaving empty-handed resolves as Abandoned. |
| A11 | **Source resolution.** With an active, undefeated, fitting faction from the item's own package, that faction is a preferred candidate; with it absent, defeated or unfitting, generation falls back down the hierarchy or finds no credible lead; a non-hostile same-package faction is never turned into a hostile guard; vanilla items and other mods work with no Beyond Our Reach installed; no code path names a specific item, faction or mod. |
| A12 | **Catalog safety.** `Allowed` makes an Unusual def requestable; an Ineligible technical exclusion cannot be allowed and the UI says why; a def whose Things fail to generate at runtime is marked unusable for the session, and the affected Intel is invalidated with a full refund, a letter and one diagnostic line. |
| A13 | **Comms Console gate.** With no usable console (none built, unpowered, or an electricity-disabling condition) every outgoing Network command is refused with its reason and charges nothing; reading still works; an active search keeps running and its leads still arrive; once a console is usable again the same commands work, with no request lost. |
| A14 | **Multi-lead Intel.** One request delivers a lead, the player keeps the same search going (under the contact's continuation policy), pursues Lead A, and later receives Lead B from the same request; ending the search keeps both leads; save and load at every Intel state and between rounds gives identical rounds. |
| A15 | **Global cast.** A fresh settings file generates a cast (about 100 contractor and some Fixer templates) with stable IDs; a new world snapshots it and gets world-local actors; runtime events never change `ModSettings`; renaming or regenerating the global cast leaves an existing save's actors unchanged; "regenerate" keeps custom entries; a malformed template is quarantined and the rest load. |

### 4.4 Phase 1 spikes

S1, S2, S3, S4, S5, S6, S7, S8, S18, **S19** ([RIMWORLD_INTEGRATION § 5](RIMWORLD_INTEGRATION.md#5-runtime-spikes)).
If S19 shows that the vanilla `ItemStash` + threat-part composition forces extermination or
breaks partial recovery, Phase 0 is **not** redesigned: the result is recorded, and Phase 1
switches to a different vanilla composition or a minimal Network site part (the upgrade path
ADR-015 already names).

---

## 5. Phase 2: Contractors (abstract) + Procurement

**Scope**

- **The contractor side of the cast**: about 100 configurable contractor identities
  instantiated from the world's snapshot (Solos, duos, crews, teams, companies, specialists),
  with `ContractorProfile` (capability), `ContractorSimulation` (NPC state), for
  organizations `OrganizationProfile`, and `IssuerProfile` where the template's `canIssueWork`
  is set (the component only; issuing behaviour is later scope). A population manager for
  world-generated newcomers, daily upkeep, morale v1, doctrine, roster tiers, wounded recovery, recruitment, **minimal
  succession**. Fame and operational experience tracked separately; Legendary is never
  protection.
- Known Characters as **records only** (leaders, lieutenants, notable fates). There are no pawns
  yet.
- Contracts: the full core, parts and lineage fields. Kind `Procurement` via
  `NetworkContractKindDef`, with an exact item and quantity. **Open** and **Direct** contracts
  (master § 22); Premium contracts with silver contributions as a resolver input. The offers
  path is used, with 1–3 bidders; an Open contract may bring a new organization into the world.
  **Fixer-mediated procurement**: the Fixer brokers the contract, reaches the contractors, and
  wraps each bid into one client-facing quote (contractor components + Fixer fee, markup,
  deposit terms, insurance offer, validity), and mediates a contractor who disappears before
  work starts.
  Refusals carry reasons. The money rules of [STATE_MACHINES § 4.2](STATE_MACHINES.md#42-money-rules):
  a default 50/50 split, the deposit normally lost on failure, optional insurance, full refunds
  only on technical invalidation; deposit and insurance terms from the Fixer's policies. The
  client's choices on partial results and renegotiation.
- **Consequence Engine v0**: one rule. A failed, missing or catastrophically lost procurement
  can leave a **last known location** (master § 24): a recovery opportunity built with the
  Phase 1 site machinery, holding the cargo the contractor had secured (or nothing) and the
  threat that stopped them, with a letter in the master § 81 style. It has no contractor pawns
  yet; survivors, captives and bodies arrive in Phase 3.
- Operations plus the **abstract resolver** (bands, casualties, captures as records, delays),
  frozen inputs, committed outcomes.
- **Delivery by drop pods** to a player home map, with fallbacks (S13).
- **Relationships core**: edges, counters, salient records, lazy decay. Willingness and pricing
  read them.
- Knowledge v1: topic experience from operations; preparedness in the resolver.
- UI: the **Procurement** and **Contractors** tabs (actor cards built from summaries and
  records, with the descriptor vocabulary of master § 28–29, § 43 and § 73, not raw stats), and
  a **Contracts** tab (the player's contracts). Known contractors become selectable Intel
  contacts.
- Dev: create contractor, force outcome band, simulate N contracts, fast-forward.

**Proves**: persistent orgs at decade scale (soak test); history changing behaviour
(willingness, pricing); branch-ready contracts (lineage fields populated; Troubled states
without physical follow-ups yet, which resolve abstractly by deadline).

### 5.1 Phase 2 as implemented

Everything in the scope above exists in code with headless tests. Runtime spike S13 (drop pods) is
**NOT RUN**: owner validation is required ([spikes/S13](spikes/S13-drop-pod-delivery.md)).

| Area | Where |
|---|---|
| Contractor actors from the world snapshot; composition; upkeep; population; succession; morale | `Domain/Contractors/*` |
| Relationships core, knowledge v1 | `Domain/Relations/Relations.cs`, `Domain/Knowledge/Knowledge.cs` |
| Contract core, procurement kind, lineage, money ledger | `Domain/Contracts/ContractModel.cs`, `ContractKinds.cs`, `1.6/Defs/ContractKindDefs` |
| Valuation, Fixer policies, contractor pricing | `Domain/Contracts/ProcurementPolicies.cs` |
| Willingness | `Domain/Contracts/Willingness.cs` |
| Bidding, quotes, award, cancel, void | `Domain/Contracts/ProcurementService.cs` |
| Outcomes, decisions, delivery, payment, failure, replacement | `Domain/Contracts/ProcurementOutcomes.cs` |
| Operations, frozen inputs, resolver | `Domain/Operations/*` |
| Consequence Engine v0 | `Domain/Consequences/ConsequenceEngine.cs` |
| Drop-pod delivery | `Integration/DeliveryAdapter.cs` |
| UI, letters, history lines | `UI/MainTabWindow_Network.Contracts.cs`, `ContractReadModels.cs`, `ContractLetters.cs`, `ContractNarrative.cs` |
| Dev actions, soak harness | `Diagnostics/NetworkDevActions.Phase2.cs`, `Diagnostics/SoakHarness.cs` |

Tuning and deviations worth knowing (the formulas are replaceable policy; see
[SIMULATION § 5](SIMULATION.md#5-willingness-refusal-and-bidding)):

- **Deposit share.** "About half" is only a starting point. It comes from the Fixer's brokerage
  style (Lean 0.6, Standard 0.5, Premium 0.4), adjusted for danger and the contractor's trust in
  the client, and clamped to 0.3–0.75.
- **Market threat.** For market procurement the threat is `0.6 + 1.2·log10(1 + goods/300) +
  30·difficulty²`. Difficulty is built only from generic rarity signals: unique, untradeable,
  neither craftable nor mineable, tech level, unit value and volume.
- **"Worse than expected".** When engagement shows a danger well above the quoted one, a
  professional contractor asks for more silver or a smaller scope. The client's answer re-freezes
  the inputs before the resolver runs: a client choice made before resolution legitimately
  changes them. The grace default accepts the reduced scope.
- **Partial results.** Any band can bring back part of the cargo; a Failure can bring back up to
  20 %. Whenever something short of the full quantity comes back, the client decides. The grace
  default accepts the partial result.
- **Payment default.** The Phase-2-safe subset only: the contractor holds the goods
  (AwaitingPayment, 7-day grace) or hands over what the deposit covered. The debt option needs
  Obligations (Phase 5) and is deferred.
- **Delivery.** The balance is charged only once a drop plan exists. Money never moves for goods
  that cannot land.
- **Retention.** When a contract closes, its losing bids are dropped (the accepted quote stays).
  Terminal contracts, with their accepted offer and operation, are archived a year after closing.
- **Events.** `Operation.Resolved` is published as Minor. Five catalog additions are listed in
  [EVENTS_AND_HISTORY § 2](EVENTS_AND_HISTORY.md#2-event-catalog).

Headless evidence (after the final correction pass): 144 tests, 12,026 checks, 0 failures. They
include every persistent contract state round-tripping through the real Scribe, replacement money
across a lineage (A → B → C, void, cancel, insured failure, pro-rating, handover, save/load), named
character exclusivity, job capacity at acceptance, Last Known Location cargo, ID and upkeep-job
repair, the Decline comms gate and survivor-only knowledge. A soak of 1,080 simulated days (18
in-game years, 2,160 contracts, about 100 contractors) leaves nothing stuck, holds the population and
keeps relations and knowledge bounded. Every simulated day it also checks that no contractor runs
more jobs than its capacity, no named person is on two live operations, no money record is empty,
negative or duplicated, ledger charges minus refunds equal the silver that actually moved, transfers
sum to zero, and no Last Known Location holds more than was secured: all zero. The Network save node
is about 2.5 MB, with about 1.2 ms of work per simulated day.

---

## 5A. Phase 2.5: Abstract spatial continuity + player-contract Field Log

Inserted after Phase 2 was merged and runtime-tested by the owner, and before Phase 3, which needs
contractors to be *somewhere* before they can be materialized. Phase 3 keeps its number and scope.
Normative spec: [SPATIAL](SPATIAL.md); decisions ADR-041 (implemented), ADR-042, ADR-043, ADR-044,
ADR-045 (abstract charter transport, added by the correction pass).

**Scope (as implemented)**

- `SpatialState` on `ContractorSimulation` (world truth: anchor, destination, journey timing);
  `MobilityProfile` stays capability. Deterministic initialization near the origin faction's
  settlements, never at the player's colony.
- Movement is lazy catch-up from committed timing on the existing daily upkeep and at operation
  checkpoints; routes are a runtime cache over the `ISpatialWorld` port (RimWorld world grid and
  pathing; a synthetic grid headlessly). Occasional ambient relocation, silent (no events, history,
  letters, relations, money or hidden contracts).
- New operations commit a hidden plan (origin = real anchor, work region scaled to the committed
  ETA, return point, incident). Checkpoints stay the timeline; the resolver still decides the
  outcome; delays move the return leg; aborts stop where they are.
- The Last Known Location rule places its site near the recorded incident (fallback: Phase 2
  placement); cargo truth unchanged.
- The Field Log: short reports for the player's own running contract, shown on its card, cleared
  when it closes.
- Save format 3; Phase 2 contractors are anchored at load, running Phase 2 operations are untouched.
- Dev actions, soak counters and invariants, spike S20.

**Correction pass (on the same PR)**

- A journey is never completed without proving its remaining route (no arrival by the clock alone),
  never faster than the contractor can walk (real route steps, not approximate distance, decide what
  fits; a longer rebuilt route makes it later), and an ended contractor never moves again.
- Returns follow the Phase 2 lifecycle: only Troubled keeps a group out (a Disaster with survivors
  comes home, its incident kept); Phase 2's "found" reconciles the group home; a write-off keeps the
  incident truth.
- A guaranteed last-resort anchor search; anchors around settlements, never on them; a Solo is
  "captured" in its Field Log.
- **Abstract charter transport** (ADR-045): an operation leg that cannot be walked in time may cross
  same-layer geography by a reusable two-way charter from a high-tech provider (derived from the
  game's `TechLevel`); ground first, never ambient, never cross-layer, no craft, no money, one Field
  Log beat.
- Final pass: a used charter is the way back (a delay does not cancel the booking); the plan keeps
  history (`charterUsed`, `charterLost`) apart from the live leg; "reached the area" only when true.

**Not in Phase 2.5:** pawns, custody, deployments, in-person delivery, rescues (Phase 3); visits,
relay stopovers, encounters, intersections, corridors, tracking (later phases); cross-layer or
orbital travel; physical shuttles, extraction windows, missed-pickup consequences (deferred story
hooks); spatial pricing (later tuning).

Headless evidence: 213 tests, 15,169 checks, 0 failures (69 new in Phase 2.5: spatial, migration,
the Field Log matrix, the correction regressions, the charter matrix A–N and the final pass's
round-trip, plan-truth and arrival-narration regressions). The soaks add daily
spatial invariants: every active contractor has a valid anchor; nobody teleports or walks faster than
its own pace; ended contractors never move; recovered groups are where Phase 2 says; ambient movement
never charters; Field Logs exist only on the player's live contracts and never repeat a line. Zero
violations in the 18-year soak (a charter world) and the archipelago stress soak; the spatial data
adds about 127 KB to the 18-year save. S20 (runtime) is **not run**.

---

## 5B. Phase 2.75: Contractor career foundation

Inserted after Phase 2.5 was merged and before Phase 3: the durable progression spine that later phases
(pawn generation, NPC competition, hiring, alternative compensation, sponsorship, rivalries, equipment
generation, augmentations, legends) will read. Normative spec: [CAREERS](CAREERS.md); decision
[ADR-046](DECISIONS.md) (careers extend existing simulation truth).

**Scope (as implemented)**

- `PublicReputation.score` beneath the `FameBand` (derived; thresholds 100 / 300 / 800 / 2,000 in one
  `CareerPolicy`); reputation from finished work by the operation's frozen danger and outcome, with an
  anti-farming ceiling; no reputation loss.
- `CareerRecord` on `ContractorSimulation` (fixed counters; History keeps the detail);
  `Operation.careerEligible` / `careerOutcomeApplied` and an exactly-once result at the lifecycle's end.
- Contractor money in the existing `funds`: one saturating path; the contractor's share mirrored on the
  ledger record at the commit point; proportional clawback on refunds; nothing for the Fixer's fee, an
  insurance payout or a replacement transfer; no windfall on a technical invalidation.
- Equipment advancement from the existing daily upkeep (reputation, `cost + reserve`, health, cooldown, no
  commitment, tier cap).
- `CareerNeed` and Tags, derived, never stored, never read by the resolver.
- `Contractor.FameChanged` / `Contractor.Advanced` history events; dev actions; save format 4 with the
  V3→V4 migration (score = band floor, `legacyResolved = opsCompleted`, old operations ineligible).

**Not in Phase 2.75:** pawns, custody, physical inventories, vehicles or ships, augmentations,
loadouts, exact weapon ownership, retirement, mergers, NPC-issued contracts, player contractor mode,
direct hiring, alternative compensation, player Loyalty, rivalries. Mobility advancement is future work.

Headless evidence: 283 tests, 17,609 checks, 0 failures (70 new: the reputation, record, money,
advancement, Tag, need and migration matrix, the exactly-once paths, the farming loop, and three career
soaks). The soaks add career invariants, all zero over 20 in-game years at 100 and 300 contractors:
duplicate outcomes, stuck results, legacy operations awarded, funds drift, ledger attribution, refund
provenance, technical-void windfalls, fame/score mapping, bounds, advancement rules and Tag contradictions. Career data is about 1.6 % of the save.

---

## 5C. Phase 2.9: Runtime test infrastructure

Inserted after Phase 2.75 and before Phase 3, while The Network is still almost entirely abstract: a small
in-game regression runner so that Phase 3's physical contractor lifecycle can add its own multi-step runtime
scenarios to a framework that is already proven. **Developer infrastructure only**: no gameplay change, no
tuning, no new player UI, no Harmony, no save-format change (still 4). Normative guide:
[RUNTIME_TESTING](RUNTIME_TESTING.md); decision [ADR-047](DECISIONS.md) (runtime regression tests are isolated
from live gameplay state); risk [R-27](RISKS.md).

**Scope (as implemented)**

- A time-sliced, never-blocking runner pumped from `NetworkWorldComponent.WorldComponentUpdate` (one static
  null check per frame when idle), with stable test IDs, PASS / FAIL / WARN / SKIP, expected / actual /
  exception / elapsed / entity ids, a tiny assertion API (`True`, `Equal`, `NotNull`, `Zero`, `Eventually`,
  ...), immediate and stepped (multi-step, waiting) tests, per-test exception containment, finite timeouts,
  clean cancel, and options `stopOnFirstFailure` / `verbose` / `preserveFailedSandbox`.
- An isolated **sandbox**: a private Network built from the production services over sandbox ports
  (comms, payment, catalog, world facts, sites, a recording delivery) and a synthetic world graph, sharing
  nothing writable with the live Network; a failed one can be kept in memory and inspected.
- A read-only **live scan** of the real catalog, comms gate, payment environment, world graph and drop-pod
  plan, and a read-only invariant scan of the live Network (never the repairing validator).
- **Safety proof built in**: the Network's durable truth (every persisted field, by content) plus a colony/world
  sentinel compared before and after every slice, failing closed if the capture itself fails (RT-INFRA-001),
  exact snapshot / restore of all 18 static dev overrides and service toggles (RT-INFRA-002), no control job
  in the persisted scheduler (RT-INFRA-003), every sandbox accounted for (RT-INFRA-004). A runtime test never
  starts, reconciles or repairs the live Network (a never-started Network gives SKIP), and production warnings
  raised during a test surface as WARN.
- Suites: `RT-SMOKE-001..008`, `RT-LIVE-001..006`, `RT-PROC-001..011`, `RT-CAR-001..014`, `RT-SPAT-001..008`.
- Eight Dev Mode actions under **The Network**: Runtime tests: Quick smoke, Full safe regression, Live
  integration scan, Status, Cancel current run, Last report, Export last report, Inspect preserved failure.
- The owner's real-game observation (legacy procurement → continuation → payment hold → recovery → full
  drop-pod delivery) is recorded as an **owner-observed runtime pass**, not as a formal S20 result; the sandbox
  keeps its *invariant* as RT-PROC-007.

**Not in Phase 2.9:** any physical, destructive or save-reload suite; real delivery in a test; automated
quit / restart; a normal-game background monitor; a player-facing tab; Phase 3 or Phase 4 behaviour; Harmony.

**Status: merged (PR #6) and owner-runtime-validated.**

Headless evidence at merge: 315 tests, 20,331 checks, 0 failures, 0 `warning CS` (32 new `Runner.*` tests: the
runner, the plans, the sandbox suites through the real runner against a synthetic live world, the fingerprint,
the idle cost). The existing 283 tests and every soak are unchanged and green. RimWorld could not be launched where
this phase was built; the in-game half was therefore compile-checked at merge and then run by the owner.

**Owner runtime validation: PASS** ([RUNTIME_TESTING § 15](RUNTIME_TESTING.md#15-owner-observed-runtime-evidence)),
in two environments, with **0 runtime FAILs**:

- *Fresh Dev Quicktest colony* (simple base, stockpiles, silver, power, Comms Console, trade beacon over the
  payment stockpile): Quick smoke 12 PASS (≈ 84 ms); Full safe regression 49 PASS, 2 WARN (RT-PROC-001 ≈ 6.65 ms
  and RT-SPAT-008 ≈ 6.97 ms slow-step telemetry; ≈ 160 ms); Live integration scan 10 PASS (≈ 3 ms).
- *The real, ongoing, heavily modded colony* (130 contractors, 4,114 silver, both unchanged by manual check after
  each run): Quick smoke 12 PASS (≈ 133 ms); Full safe regression 50 PASS, 1 WARN (RT-SPAT-008,
  `job:consequence.followup` ≈ 5.01 ms; ≈ 222 ms) then 51 PASS, 0 WARN (≈ 96 ms); Live integration scan 10 PASS
  (≈ 9 ms). No missing or extra silver, test cargo, test contracts, changed careers or reputation, fake history,
  fake gameplay Letters, moved contractors or leftover test world objects were observed.

This does **not** claim every mod interaction is proven, that every RimWorld state is fingerprinted, that the
in-progress Cancel path was exercised by hand (the run had already finished; it has headless coverage), or that
the formal Phase 2.5 **S20** checklist was completed: S20 remains *NOT RUN — owner runtime validation required*.
The earlier owner-observed legacy-procurement pass (payment hold → recovery → full drop-pod delivery, no
observed errors) stays recorded as Phase 2 / 2.75 runtime evidence.

---

## 6. Phase 3: Abstract ↔ physical lifecycle

> **Status: design reviewed, not implemented.** Normative design: [PHYSICAL_LIFECYCLE](PHYSICAL_LIFECYCLE.md)
> (audited against the 1.6.9676 assemblies; supersedes the Phase 0 scope below where they differ). Decisions:
> [ADR-048](DECISIONS.md) and [ADR-049](DECISIONS.md). Risks: [R-28 to R-34](RISKS.md). Save format stays **4** until
> implementation; Phase 3 will need one bump (the number is not chosen here).

**The target is not "spawn some contractor pawns."** It is: a persistent Network actor can temporarily become
physically present, actual physical consequences become authoritative, and that reality reconciles back into The
Network **exactly once**, with one authority per person at a time.

**Three subphases, each ending in something the owner can review**

| Subphase | Content | Spikes needed |
|---|---|---|
| **3.0 Authority and episode foundation** (no RimWorld pawn) | the Episode store (reserved `deployments` slot), `PawnRef`, the new `KnownCharacter` fields, the authority gate and every abstract writer behind it, reconciliation (plan → commit → flag → publish), a `PhysicalWorldPort` with a scriptable fake, validator, compaction, prepare-for-removal settle, `RT-PHYS-001…019` in the **safe** runtime tier, one save-format bump | none |
| **3.1 The controlled physical episode** (the vertical slice) | the real adapter: create-once-and-bind a named pawn, spawn, tags, signal routes, the visit Lord, the per-episode temporary faction, the registry reservation, store-time normalization and catch-up, the **physical test tier** (separate, disposable-save only), a read-only Episode Monitor. One Solo contractor, dev-triggered: exits, is wounded, is killed, or its map is removed; then re-materializes as the **same pawn** | S9r, S10, S12, S14, S17, S21, S22, S23, S24 |
| **3.2 Custody and rescue** | held people (arrest, recruit, enslave, kidnap, caravan, pod) and the custody watch; anonymous members and groups; promotion of generic pawns; the **rescue** episode for a Troubled operation (site holder, `OpStatus.Physical`); Last Known Locations with survivors and captives. First player-visible content | S11, S21 |

**Recommended re-scope (needs owner confirmation).** The Phase 0 scope listed below is larger than the physical
lifecycle needs and partly belongs to Phase 4:

| Original Phase 3 item | Recommendation |
|---|---|
| `CustodyService`, registry quest, `PawnRef`, binding invariants, deployments, reconciliation, collapse | **In** (3.0 to 3.2), as the Episode design |
| `EncounterFactionAdapter` | **In** (3.1), one temporary faction per *episode* |
| Rescue scenario; Last Known Locations with survivors | **In** (3.2) |
| In-person delivery (walk-in hand-over) | **Out** of 3.0 to 3.2; the owner-validated drop-pod delivery stays; revisit after 3.2 |
| Sponsorship with equipment and tracked leases | **Out** (Phase 4 compensation); the `leases` slot and a gear seam are reserved |
| Contract inheritance and continuation | **Out** (a contract-lifecycle feature) |
| Consequence Engine v1 | **Reduced** to the rescue rule (3.2) |
| Promotion of generic pawns | **In**, minimally (3.2) |
| Events `Contractor.Rescued`, `KnownCharacter.CapturedByPlayer/.Defected/.Lost`, `Player.BetrayedContractor` | **In** as 3.2 needs them |
| Deployment Monitor dev window | **In**, read-only, as an Episode Monitor (3.1) |
| Runtime scenarios for the lifecycle | **In**, as two tiers: abstract half in the safe suites over a fake port; physical half in a separate disposable-environment tier ([ADR-049](DECISIONS.md)) |
| Ambient contractor visits | **Deferred**, unchanged |

**Not in Phase 3:** Phase 4 compensation, the contract board, the player as contractor, physical contractor caravans for
background movement, a persisted roster of anonymous individuals, a Hediff/inventory mirror, Harmony.

---

## 7. Phase 4: Player as contractor, contract board, bidding

- The player registers as a contractor (a setting enables it). A `ContractorProfile` with the
  chosen name and public profile is added to the `PlayerProxy`, reputation starts at Unknown,
  and the player keeps their faction (master § 35). **No abstract roster or simulation**: the
  player's execution state is read from the real colony (`ColonyReader`).
- Faction sponsorship of the player: loaned gear with return expected (master § 41).
- NPC issuers (faction proxies, institutions, and contractor organizations that hold an
  `IssuerProfile`) post contracts to a **board**, usually through a Fixer. The player bids or
  accepts. Completion is physical (deliver items, reach a site, escort).
- **Full bidding** among NPC contractors on player contracts. Competitors can race the player to
  opportunities (`Opportunity.LostToCompetitor`).
- **Public reputation epithets and fame tiers** v1 (hysteresis, persisted; master § 39, § 66).
- Contested leads (another group has the same information, master § 12) and non-exclusive
  contracts (master § 42).
- The ContactBook v1 (listings; the player sees orgs they have dealt with).

## 8. Phase 5: Social layer

- `BeliefStore`: rumors with distortion; personal experience outranks hearsay.
- Gossip propagation jobs.
- **Favors and debts**: `ObligationLedger` + calling in favors in bidding and pricing.
- **Introductions**: `IntroducerProfile`; the ContactBook grows organically.
- Perceived reputation (`Reputation.As`), **sanctions and blacklists** (legitimate vs criminal
  observers).
- Morale v2 (trauma baselines; Reckless and Desperate behaviours; fraud chance).
- Deeper Fixer play: Fixers introducing contractors, favors and debts owed to Fixers, Fixer
  rivalries and misinformation from low-trust sources. **Geographic Intel quality** by region
  knowledge. (Fixers themselves exist from Phase 1.)

## 9. Phase 6: Organizational lifecycle and legends

- Contested succession, **fragmentation**, **mergers and absorption**, dissolution.
- **Retirement transformation** (a leader becomes a broker, trader or recruiter; knowledge
  transfer).
- **Legends** v1 (promotion, frozen deeds, History tab "Legends", successor legacy text).
- Tombstone compaction at scale; long-soak tests (30+ in-game years).

## 10. Phase 7+: Expansions

- **Black and confidential contracts** (assassination, theft, sabotage, kidnapping,
  intelligence theft, secret recovery). Confidentiality exposure.
- **Evidence and witnesses**: exposure rules at encounter reconciliation; knowledge-gated
  political consequences.
- **Joint operations** (player + NPC crews, NPC + NPC).
- **Subcontracting** (master § 50): the player hires a known group to help with a contract they
  accepted, as a linked child contract.
- **Odyssey** module: orbital salvage, asteroid sites, space contractor operations (S15).
- **Optional DLC-aware transport presentation** of contractor mobility: Royalty shuttles,
  Odyssey passenger transport, possibly a Gravship-style presence for exceptional actors. These
  are directions, not commitments.
- **Smuggling** and trader integration.
- **Royalty shuttle delivery** (S16).
- Optional presentation adapters: a Quests-tab mirror, vanilla Tales for art, Ideology
  HistoryEvents.
- Mod-specific compat packs (for example a Beyond Our Reach flavour pack).

## 11. Deliberately deferred

These are **designed for** (data slots, event types, lineage fields), but **not implemented**
before their phase:

rumors and beliefs · gossip · favors and debts · introductions · perceived reputation ·
sanctions and blacklists · black contracts · evidence and witnesses · fragmentation and mergers ·
retirement transformation · legends · joint operations · subcontracting · **ambient contractor
visits** (see below) · DLC-aware transport presentation of contractor mobility · Intel quality intent
(optional broad preference) and strict quality-qualified Procurement (master § 79) · a full global
cast editor and importing new templates into existing worlds · communications other than the
Comms Console · Odyssey and orbital content ·
smuggling · NPC world-map caravans (possibly never) · a vanilla-quest mirror · custom map
generation · Harmony (possibly never).

**Ambient contractor visits (deferred direction, not designed).** Eventually a contractor may
appear at the player's colony for its own reasons: passing through, returning from a contract,
rest and recovery, resupply, trading salvage, a stopover, waiting for another job, an emergency,
visiting a client, or hauling treasure. The player might meet an unknown group (ContactBook
`MetInField`), trade, see its equipment and condition, hear rumors, or discover Fixers through it.
**Not every appearance exists to give the player a quest**; some actors are just living their own
lives. Visits will reuse the abstract → physical → abstract deployment machinery with new
deployment purposes (for example `PassingThrough`, `Stopover`, `TradeVisit`); there is no separate
physical lifecycle. Direction only: Phase 3 makes visits technically possible, Phases 4–5 could
add contact discovery, trading, social interactions, rumors and spontaneous contracts, and the
DLC-aware transport presentation belongs to Phase 7+. Out of scope throughout: ship and Gravship
implementation, shuttles as persistent objects, interiors, fuel, contractor-owned maps or mobile
bases, world-map fleets, transport inventories, visiting AI, visitor trading or quest code,
DLC-specific transport code, and any orbital economy or market hub.
