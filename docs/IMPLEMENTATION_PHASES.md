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
| **1** | Foundation + Intel | catalog → persistence → scheduling → Intel state → source/context resolution → opportunity → vanilla site → physical loot, taken in part or in full → cleanup → history | S1, S2, S5, S6, S8, S19 |
| **2** | Contractor organizations (abstract) + Procurement | persistent orgs, Open and Direct contracts with the offer path, deterministic resolver, deposits that are at risk, drop-pod delivery, willingness/refusal, relationships core, history changing behaviour, failures that leave a last known location | S13 |
| **3** | Abstract ↔ physical lifecycle | Known Characters as pawns, custody, deployments, encounter factions, in-person delivery, rescue follow-ups, contract inheritance | **S9**, S10, S11, S12, S14 |
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
6. **Reconciled with the master design** (this revision; [ARCHITECTURE § 14](ARCHITECTURE.md#14-master-design-reconciliation)).
   The phase split is unchanged. Phase 1 gains the player's choice of contact, a simplified
   source resolver, a subset of the Intel divergence classes, extra cargo and a proof of partial
   recovery (S19). Phase 2 gains one consequence rule, a "last known location" recovery
   opportunity, because the design says failure must create content (master § 3.4, § 24).

## 3. Rules for every phase

- **Only build what the phase uses.** No speculative managers, no Defs for systems not yet
  built, no fake interfaces. Store *slots* in the root layout are the one exception: they are
  added as empty nodes from Phase 1 so the layout never shifts.
- **Every new persisted field has a default and a documented migration story.**
- **Every phase ends with**: save/load tests at each new state; the mod-removal test (S6
  extended); the external-mod-removal test (S7 extended); a performance report; updated docs.
- **Update this documentation in the same PR** as any architectural deviation.
- **Spikes first**: a phase's listed spikes are run and documented before its content is built.

---

## 4. Phase 1: Foundation + Intel

### 4.1 Target playable flow (Phase 0 brief, aligned with master § 9 and § 17)

1. Start a game. 2. Open The Network. 3. Choose a contact (the Exchange or a faction) and search
the eligible item catalog. 4. Request Intel about an item, **with no quantity**. 5. Pay the Intel
fee. 6. The search persists over time ("several days"). 7. The search resolves. 8. One plausible
opportunity is generated from the item and the world context, or no credible lead. 9. A
physical world location is added. 10. The player travels there. 11. The desired item physically
exists there, in the amount the opportunity decided (which may differ from the report). 12. The
player can acquire some or all of it, without having to kill every defender. 13. The site
resolves and cleans up. 14. Meaningful history is recorded. 15. Save and load work throughout.

### 4.2 Smallest slice that proves the chain

| Link | Phase 1 implementation | Explicitly not in Phase 1 |
|---|---|---|
| **Catalog** | full catalog (verdicts, reasons, overrides in ModSettings, source mod, market value); UI search, filter and sort; dev reports | rarity-driven pricing beyond the fee |
| **Persistence** | `NetworkWorldComponent` root with every store slot (most empty); `saveVersion = 1`; tolerant list loading; typed IDs; `DefRef`, `FactionRef`, `WorldObjectRef`, `TileRef` | `PawnRef` (unused until Phase 3) |
| **Scheduling** | scheduler with budget and staggering; job kinds `intel.resolve`, `opp.sample`, `opp.close`, `history.sweep`, `compact.sweep` | periodic org upkeep |
| **Events** | event bus + journal; the Phase 1 events from the [catalog](EVENTS_AND_HISTORY.md#2-event-catalog) | consequence engine, gossip |
| **Actors** | `PlayerProxy`; one `Institution` "the Exchange" (the generic information network) with `IntelSourceProfile`; lazy `FactionProxy` actors, both for the site's holder or defenders (so history can name them) and as **Intel contacts** (non-hostile factions, with a derived `IntelSourceProfile`) | contractors; named brokers |
| **Intel state** | the full Intel machine ([STATE_MACHINES § 1](STATE_MACHINES.md#1-intel-request)), including cancel and invalidate with refunds; **topic only, no quantity anywhere** | intel topics other than items; quality qualifiers (master § 79) |
| **Opportunity generation** | a **simplified source resolver** that keeps the full rule set ([ARCHITECTURE § 6.14.1](ARCHITECTURE.md#6141-opportunity-source-and-context-resolution)): candidates from the item's package, live factions (stance, defeated, hidden, tech) and world; the master § 15 hierarchy as the prior; a non-hostile same-package faction is never made a guard; "no credible source" is a real result. Archetype **`GuardedCache`** (vanilla `ItemStash` + one threat part chosen by the resolved context and threat points from `Outpost` / `BanditCamp` / `AmbushHidden` / `Manhunters` / `SleepingMechanoids`, or unguarded for an abandoned cache) **and** outcome **`NoCredibleLead`**. Quantity chosen by the generator. **Divergence subset:** Accurate, Partial, Outdated, Bad, Jackpot, Complication, Trap (the cache variants can express these); a confidence descriptor on the lead. A little archetype-appropriate **extra cargo**. | other archetypes (trader or owner holds it, convoys, salvage, orbital); Misinformation (needs source motives, Phase 5); Contested (competitors, Phase 4) |
| **World object / site** | `SiteAdapter` with a vanilla `Site`, `TimeoutComp` (the lead's operational window), and `WorldObjectComp_NetworkSite` injected by XML patch into the vanilla `Site` def; quest tag `TheNetwork.Opp.<id>` | custom SitePartDefs, custom WorldObjectDefs (unless S19 requires a minimal site part) |
| **Physical loot** | Things created at materialization into `SitePart.things` (seeded quality and stuff where applicable); placed by vanilla `GenStep_ItemStash`. Taking part of it and leaving with defenders alive is a valid, recorded outcome (S19). | — |
| **Cleanup** | comp callbacks + fallback sampling + reconciliation for vanished sites; closing and archiving | — |
| **History** | ledger (Notable+ records with participants, place, subject, awareness Public/Involved); player summary counters; retention sweep; History tab list with narrative lines | epithets, legends, gossip |
| **Payment** | `PaymentAdapter` with beacon silver (`LaunchSilver`) and drop-pod refunds (Spike S4 picks the final UX) | debts, obligations |
| **UI** | `MainButtonDef` → tabs **Intel** (contact choice, catalog search with the "show unusual items" toggle and per-item overrides, request dialog with fee and no quantity field, active requests with elapsed time and a vague estimate, leads with reported cargo, threat, distance, window and confidence, and look-at-site) and **History**. The Procurement, Contracts and Contractors tabs are **not shown** until their phases. | — |
| **Letters** | vanilla letters: resolved, no lead, invalidated, expired soon (optional), claimed | — |
| **Diagnostics** | logging policy; validators (IDs, orphans, external refs, scheduler agreement, caps); dev actions from the Phase 1 rows of [DEBUGGING § 3](DEBUGGING.md#3-dev-actions); timing | — |
| **Removal** | "Prepare save for removal" | — |

**Defs created in Phase 1 (and only these):** `TheNetwork_MainButton` (`MainButtonDef`), one
XML patch adding `WorldObjectCompProperties_NetworkSite` to the vanilla `Site` def, and keyed
translation strings. **No** QuestScriptDef, SitePartDef, WorldObjectDef, FactionDef or IncidentDef,
unless S19 shows that partial recovery needs one minimal Network SitePartDef (ADR-015).

### 4.3 Phase 1 acceptance criteria

| # | Criterion |
|---|---|
| A1 | All 15 flow steps work in a vanilla + DLC game **and** with Beyond Our Reach (Tenebrite) loaded. |
| A2 | Save and load at each Intel state (Submitted, Searching, Resolved, Closed) and each Opportunity state (Materialized, Engaged, Claimed/Abandoned/Expired) gives identical outcomes (S8). |
| A3 | Removing BOR mid-search and mid-site gives Invalidated with a refund and letters; zero Network-originated errors (S7). |
| A4 | Removing The Network, prepared or not, gives exactly 1 error in Phase 1 (the missing WorldComponent class; the site comp contributes none), and the game stays playable (S6). |
| A5 | Adding The Network to an existing save bootstraps cleanly. |
| A6 | Idle per-tick cost is unmeasurable; the S18 harness report is attached. |
| A7 | History shows a coherent narrative line for each resolved Intel and each claimed or abandoned opportunity; records survive the retention sweep as specified. |
| A8 | No Harmony. No Network code runs per tick beyond the idle check (verified by timing). |
| A9 | Headless tests: NetRng, scheduler ordering and budget, the Intel transition table, retention policy, tolerant loader (fixture with a corrupted element), **catalog verdicts** (every hard exclusion, every heuristic, overrides), **source resolver** (cases in A11), and a check that no Intel type, command or formula has a quantity input. |
| A10 | **Loot without extermination (S19).** On a guarded Network opportunity the player enters, recovers **only part** of the target payload, and leaves by caravan and by pods while some defenders are still alive. The caravan or pods can leave; recovered items stay recovered; the opportunity resolves as Claimed with a partial `recoveredBand`; history records a partial recovery, not a required victory; no loot is duplicated (including re-entry or a second departure); the site cleans up as vanilla does. Leaving empty-handed resolves as Abandoned. |
| A11 | **Source resolution.** With an active, undefeated, fitting faction from the item's own package, that faction is a preferred candidate; with it absent, defeated or unfitting, generation falls back down the hierarchy or finds no credible lead; a non-hostile same-package faction is never turned into a hostile guard; vanilla items and other mods work with no Beyond Our Reach installed; no code path names a specific item, faction or mod. |
| A12 | **Catalog safety.** `Allowed` makes an Unusual def requestable; an Ineligible technical exclusion cannot be allowed and the UI says why; a def whose Things fail to generate at runtime is marked unusable for the session, and the affected Intel is invalidated with a full refund, a letter and one diagnostic line. |

### 4.4 Phase 1 spikes

S1, S2, S3, S4, S5, S6, S7, S8, S18, **S19** ([RIMWORLD_INTEGRATION § 5](RIMWORLD_INTEGRATION.md#5-runtime-spikes)).
If S19 shows that the vanilla `ItemStash` + threat-part composition forces extermination or
breaks partial recovery, Phase 0 is **not** redesigned: the result is recorded, and Phase 1
switches to a different vanilla composition or a minimal Network site part (the upgrade path
ADR-015 already names).

---

## 5. Phase 2: Contractors (abstract) + Procurement

**Scope**

- `ContractorProfile`, organization templates (a Def), a population manager, daily upkeep,
  morale v1, doctrine, roster tiers, wounded recovery, recruitment, **minimal succession**.
- Known Characters as **records only** (leaders, lieutenants, notable fates). There are no pawns
  yet.
- Contracts: the full core, parts and lineage fields. Kind `Procurement` via
  `NetworkContractKindDef`, with an exact item and quantity. **Open** and **Direct** contracts
  (master § 22); Premium contracts with silver contributions as a resolver input. The offers
  path is used, with 1–3 bidders; an Open contract may bring a new organization into the world.
  Refusals carry reasons. The money rules of [STATE_MACHINES § 4.2](STATE_MACHINES.md#42-money-rules):
  a default 50/50 split, the deposit normally lost on failure, optional insurance, full refunds
  only on technical invalidation. The client's choices on partial results and renegotiation.
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

---

## 6. Phase 3: Abstract ↔ physical lifecycle

**Spikes first:** S9 (registry quest), S10 (encounter factions), S11 (site-part pawn holder),
S12 (catch-up), S14 (walk-in Lord), S17 (tag hygiene).

**Scope**

- `CustodyService`, registry quest (or the documented fallback), `PawnRef`, binding invariants,
  deployments, reconciliation, collapse, leases.
- `EncounterFactionAdapter` (temporary per-org factions).
- Scenarios: **in-person delivery** (walk-in contractors hand over goods) and **rescue**
  (captured, stranded or missing contractors become an Opportunity with pawns in the site holder).
  Last known locations gain survivors, captives, bodies and left-behind gear.
- **Sponsorship with equipment** (master § 30–31): gear the player gives or lends becomes
  tracked leases that reappear physically with the org's pawns.
- Consequence Engine v1: rules for rescue, recovery and continuation. **Contract inheritance and
  continuation** (repost, successor inheritance).
- Promotion of generic pawns to Known Characters from encounters.
- Events: `Contractor.Rescued`, `KnownCharacter.CapturedByPlayer` / `.Defected` / `.Lost`,
  `Player.BetrayedContractor`.
- Deployment Monitor dev window.

---

## 7. Phase 4: Player as contractor, contract board, bidding

- The player registers as a contractor (a setting enables it). A `ContractorProfile` with the
  chosen name and public profile is added to the `PlayerProxy`, reputation starts at Unknown,
  and the player keeps their faction (master § 35).
- Faction sponsorship of the player: loaned gear with return expected (master § 41).
- NPC issuers (faction proxies, orgs, institutions) post contracts to a **board**. The player
  bids or accepts. Completion is physical (deliver items, reach a site, escort).
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
- Named brokers and Intel contacts (Individuals with `IntelSourceProfile`). **Geographic Intel
  quality** by region knowledge.

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
- **Odyssey** module: orbital salvage, asteroid sites, gravship logistics, space contractor
  operations (S15).
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
retirement transformation · legends · joint operations · subcontracting · quality-qualified
Intel and Procurement (master § 79) · Odyssey and orbital content ·
smuggling · NPC world-map caravans (possibly never) · a vanilla-quest mirror · custom map
generation · Harmony (possibly never).
