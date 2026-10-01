# Contractor careers

**Phase 2.75 · status: implemented (foundation only) · [ADR-046](DECISIONS.md#adr-046--contractor-careers-extend-existing-simulation-truth)**

A contractor's career is the durable spine that connects what it does to what it becomes:

```
work creates history → history changes reputation, experience and wealth
→ wealth + reputation enable advancement → advancement changes capability
→ capability produces semantic Tags (descriptors, never bonuses)
```

Phase 2.75 builds that spine on state that **already exists**. It adds a numeric reputation beneath the
fame band, a small cumulative career record, a real contractor-money path, equipment advancement, a
derived `CareerNeed` and derived Tags. It adds no second reputation, wealth, skill or equipment system.

## 1. What is reused, what is new

| Concept | Where it lives | New in 2.75 |
|---|---|---|
| Public reputation | `NetworkActor.reputation` (`PublicReputation`) | a numeric `score`; `FameBand` is **derived** from it |
| Operational know-how | `ContractorSimulation.skill` (+ roster for organizations) | nothing (it already grows with each job) |
| Wealth | `ContractorSimulation.funds` | one saturating money path; contractor-owned money attributed on contract ledgers |
| Kit | `ContractorSimulation.equipment` (`EquipmentProfile`: tier 1–5, condition, specialties) | an advancement rule, nothing about the profile's range or meaning |
| Cumulative history | History (`HistoryRecord`, summaries) | `CareerRecord`: a fixed handful of counters, never one row per job |
| Capability | `mobility`, `equipment`, `Experience(actor)` | nothing |
| Need / Tags | — | derived on demand, **never stored** |

All the numbers live in one place, `CareerPolicy`. Nothing else in the mod carries a career literal.

## 2. Fame is not experience

`FameBand` (Unknown, Local, Established, Famous, Legendary) is **public standing**. `ExperienceBand`
(Green … Legendary) is **operational capability**. They are independent in both directions: a green
Solo can be Legendary by name (a famous heir, a notorious accident) and a Legendary hand can be
Unknown. Nothing derives one from the other: `CareerService` never reads or writes the experience
band, and `skill` never reads reputation.

## 3. Numeric reputation

`PublicReputation { fame, score }`. The score is the truth; the band is derived through `CareerPolicy`:

| Band | Score |
|---|---|
| Unknown | 0 – 99 |
| Local | 100 – 299 |
| Established | 300 – 799 |
| Famous | 800 – 1,999 |
| Legendary | 2,000 + (capped at 1,000,000) |

`fame` is a read-only property of `PublicReputation`; it can change only by changing the score
(`SetScore`) or by starting an actor at a band's floor (`SetBand`, used by a template's
`startingFame`, by Fixers and by the migration). The two can never disagree, and a load-time
validation repairs any mismatch without ever lowering fame.

### 3.1 What a finished job earns

Reputation comes from meaningful successful work, once, when the operation reaches the end of its
lifecycle:

```
difficultyValue = 4 + 36 × clamp01(danger)^1.5            // 4 for trivial work, 40 for the hardest
outcomeMultiplier  Triumph 1.25 · Success 1.00 · CostlySuccess 0.90
                   Partial 0.45 → 0.90 by secured/requested (499 of 500 ≈ 0.90; 20 of 500 ≈ 0.47)
                   Failure 0 · Disaster 0 · nothing secured 0
gain = round(difficultyValue × outcomeMultiplier × taper)  // never negative
```

* **danger** is the operation's own frozen figure: `Resolver.Danger(Resolver.Edge(frozenInputs))`,
  the very inputs the resolver used at engagement, or the danger committed when it started. It is
  never recomputed from the world. It is *relative to the contractor* (a strong crew on a trivial
  item faces danger near 0), which is exactly what makes easy work cheap fame.
* **No market-value term.** The danger already reflects how hard the goods were to get.
* **No negative reputation in Phase 2.75.** A failure or a disaster simply earns nothing.

### 3.2 The anti-farming rule

Contract count alone must not create a Legendary name (no 10,000 safe hauls). Work of a given danger
only builds fame **up to a ceiling**, then the gain tapers to nothing:

```
ceiling(danger) = max(100, round(2400 × danger²))
taper = 1 while score ≤ ceiling, falling linearly to 0 over max(50, ceiling/2) points above it
```

| Work of danger | Builds a name up to about | Successful jobs to get there |
|---|---|---|
| trivial (≈ 0 – 0.2) | Local (≈ 146) | ≈ 25–30 to reach Local |
| 0.3 | Established (≈ 320) | 10 to Local, 38 to Established |
| 0.5 | Famous (≈ 890) | 6 / 18 / 55 to Local / Established / Famous |
| 0.7 | Famous (≈ 1,750) | — |
| 0.8 | Legendary (≈ 2,300) | 4 / 10 / 27 / 75 to Local … Legendary |
| 0.95 | ≈ 3,240 | 55 to Legendary |

A test runs twenty thousand triumphs at each danger: trivial work stops at a local name, ordinary work
never reaches Famous, and only the most dangerous work reaches Legendary, in dozens of jobs.

## 4. The career record

`ContractorSimulation.career` (`CareerRecord`) is a **summary**, never a log. Detailed job history stays
in History.

| Field | Meaning |
|---|---|
| `legacyResolved` | jobs resolved before the record existed (migration: the old `opsCompleted`) |
| `triumphs`, `successes`, `partials`, `failures`, `disasters` | outcomes counted at the end of each eligible operation (a costly success counts as a success) |
| `highestDanger` | the most dangerous work at least partly done, in thousandths |
| `careerEarnings` | silver paid and kept (credits less clawbacks) |
| `casualtiesTaken`, `peopleLost`, `captured`, `missing` | people hurt / killed / captured / reported missing on counted jobs |
| `reputationEarned` | score earned from finished work (not grants) |
| `lastOutcomeTick` | when the last outcome was recorded |
| `lastAdvancementTick`, `advancementCount` | the equipment cooldown start and number of advances |

Every counter saturates at 10⁹. Fame, funds, skill and equipment are **not** copied here.

### 4.1 Exactly-once, at the real end of the operation

`Operation.careerEligible` and `Operation.careerOutcomeApplied` are persisted. The result is applied
by `CareerService.CommitOutcome` from exactly three places in `OperationService`:

* `Finish`: the end of an operation with a committed outcome (delivery, a failed delivery, a refused
  partial result, the contract closing);
* `Abort` **after** an outcome was committed (the committed result stands);
* a Troubled group **written off** at its deadline.

It is never applied at the first resolution while the group is still Troubled; a recovered group is
applied once, when its work later finishes. An operation aborted **before** an outcome (a cancelled
contract, a contractor lost before the work) gives no career result.

**`careerOutcomeApplied` means the durable career mutation really committed.** The result is *planned* first,
as a pure delta that reads the operation, the actor, its record and its score and changes none of them (anything
that can fail fails here, with nothing touched); the small durable commit (the record's fields and the score)
follows, with a snapshot restored if it somehow fails; only then is the flag set, saved with the operation; and the
fame event is published *after* the commit in its own guard, so a failing consumer can never undo, retry or
duplicate the result. A repeat, a save/load, a delivery retry, a dev advance, a replacement contractor or a repeated
terminal call cannot apply it twice. The commit is fail-soft for the lifecycle (a problem there never breaks the
operation) **and** retryable: a commit that could not complete leaves the flag false and the career state exactly as
it was, and the next load-time validation applies it (once) when the fault is repaired. A contractor that no longer
exists has nothing to receive a career: that is marked applied, since nothing can ever be.

**A written-off Troubled group is a Failure.** Whatever the resolver originally rolled (a Success, a Partial, even a
Disaster), a group that is ultimately written off counts in the career record as one **Failure** with nothing secured
and earns nothing. The committed `OperationOutcome.band` is historical truth and keeps its original band: only the
career classification differs, so a `Disaster` outcome and a `Failure` career result coexist. A retry derives
"written off" from the operation itself (Troubled, and never came home).

An operation loaded from a pre-2.75 save has `careerEligible = false` and **never** receives invented
credit, even if it finishes later.

## 5. Contractor wealth is `ContractorSimulation.funds`

There is no second wealth score. The audit (see the PR) found that Phase 2 already credited the
contractor's share of what the client paid into `funds` (at award, for a renegotiation extra and for
the balance), but never took anything back and could overflow. Phase 2.75 keeps those semantics and
makes them exact:

* **One saturating path.** Every change to `funds` (credit, clawback, upkeep, repair, recruitment,
  advancement, dev grants) goes through `CareerService.MoveFunds`, bounded to ±10⁹, never wrapping.
  Each movement is tallied by flow, so a soak can prove the books balance exactly.
* **Mirrored at the commit point.** The amount the contractor owns is written on the contract's own
  ledger record (`MoneyRecord.contractorSilver`) in the same step as the movement. Nothing is ever
  rescanned from the ledger on load (the funds already hold it).
* **What is, and is not, the contractor's money.**

| Movement | Contractor funds |
|---|---|
| Deposit, premium contribution, renegotiation extra, balance | credited its share of the quote (the Fixer keeps its fee) |
| Insurance premium | not the contractor's, never credited |
| Refund (cancel, walked away, lost before the work, undeliverable, **technical invalidation**) | taken back **in proportion to the funding actually refunded and to whom it was paid** (below): the contractor keeps exactly the part of its pay that was not refunded; a void refunds everything, so it keeps nothing of what it was paid on that contract (no windfall) |
| Insurance payout | the insurer's money: nothing is taken from the contractor |
| Replacement `TransferOut` / `TransferIn` | no contractor money moves; the carried deposit was paid to the contractor that was lost, so it belongs, economically, to the **old** contractor; the replacement earns only what is paid to it afterwards, and the old contractor's pay is never changed from the replacement's contract |
| Retry of an undelivered refund | flips the pending flag only: nothing taken again |
| Intel fees | unchanged (contractors-as-sources are not paid into funds; out of scope) |

**Refund provenance.** A refund asks "of the funding being refunded now, how much is the contractor-owned silver
attributed to *this* contractor?", never "how much has this contract ever carried?". Every `Refund` call site passes
a typed `RefundScope` (which purposes it draws from: deposit, premium, renegotiation, balance, everything, or none
for an insurance payout), never a note key. `Contract.OwnDrawnBy(scope)` is the part of the refund drawn from funding
the player paid **on this contract** (carried-in funding is neither in the numerator nor in the denominator), it is
written on the refund's ledger record (`MoneyRecord.fromOwnFunding`, additive, default 0) next to the clawback
(`contractorSilver`), and the clawback is `held × fromOwn / OwnBearingRemaining`. So a replacement's inherited deposit
can neither dilute nor enlarge its later clawback: a refund of a balance paid to the replacement takes back exactly
the replacement's share of that balance; a refund of the inherited deposit or premium takes back nothing; a full void
takes back all that the replacement was paid on that contract and none of what the lost contractor was. The scope
never changes how much silver the player receives, a pending refund's retry only flips its pending flag, and a load
never rescans the ledger. Pre-2.75 ledger records carry no attribution and still claw nothing.

Funds may go negative (as in Phase 2: upkeep has no floor); there is no insolvency model yet.

## 6. Equipment advancement

`EquipmentProfile` keeps its meaning (tier 1–5, condition, specialties). Advancement raises `tier` by
one rung when **all** hold, checked from the existing staggered daily upkeep (no new scheduler job):

* the actor is Active and not quarantined;
* it has **no live commitment** (an operation reads the current equipment);
* it is not already at tier 5;
* thirty in-game days have passed since the last advance (`lastAdvancementTick`);
* its fame meets the rung: 1→2 **Local**, 2→3 **Established**, 3→4 **Famous**, 4→5 **Legendary**;
* `funds ≥ cost + reserve`, with cost 500 / 2,000 / 8,000 / 30,000;
* it is not recovering (`CareerNeed.Recovery`: seriously wounded, kit wrecked, or heavy recent losses).

The **operating reserve** is 45 days of the contractor's real upkeep (`DailyUpkeep` is the same rate the
daily upkeep pays: 3 per person for an organization, 2 for a Solo), floor 150, so it scales with size
(a Solo ≈ 150, a 24-person company ≈ 3,200). The purchase spends the cost and nothing else; condition
is untouched. It is deterministic (no randomness). The result is a normal `Contractor.Advanced` event.

## 7. CareerNeed (derived)

`CareerService.CurrentNeed(actor)` is a pure, deterministic read, never stored, with no effect in
Phase 2.75 (Phase 4B will consume it). In this order: **Recovery** (serious wound state, kit in ruins or
heavy recent losses) · **Capital** (funds below the operating reserve) · **Equipment** (tier below what
its reputation or experience supports) · **Expansion** (an organization well below capacity with the drive
to grow) · **Mobility** (an ambitious, established contractor with short range) · **Mastery** (experience
lags reputation) · **Prestige** (a famous, ambitious contractor with no practical deficit) · **None**.
Doctrine (ambition, professionalism) softens the ambitious needs.

## 8. Derived Tags

`CareerService.Tags(actor)` returns stable string keys in a fixed order, computed on demand from state
that already exists, **never persisted**:

| Tag | When |
|---|---|
| `WellEquipped` | equipment tier ≥ 4 |
| `Wealthy` | funds ≥ 5 × the operating reserve (so it scales with size) |
| `EliteCombat` | experience Elite or Legendary **and** credible kit (tier ≥ 3, condition ≥ 0.4) |
| `BattleTested` | at least 8 jobs resolved (before and after the record began) |
| `LongRange` | mobility range band ≥ High |
| `RapidTransport` / `HeavyLift` | the mobility mode |
| `SpacerCapable` | a durable `Orbital` mobility mode (never from fame) |
| `LegendaryReputation` | fame is Legendary |
| `Augmented` | **reserved, never emitted**: there is no durable augmentation state (Phase 3 / compensation) |

**Tags are descriptors, not bonus multipliers.** `EliteCombat` means "existing experience and equipment
qualify", never "+25 % success". The resolver, pricing, willingness and upkeep never read a Tag (a
source scan in the test run holds that), so a Tag can never apply an advantage the underlying skill or
equipment already applies.

## 9. Events and history

Only meaningful transitions publish: `Contractor.FameChanged` (a band was crossed; remembered in
History from Established up) and `Contractor.Advanced` (equipment advanced; remembered from tier 4). No
letters, no spam: +17 hidden reputation publishes nothing.

## 10. Save and migration (4)

`NetworkSaveVersion` 3 → 4 (`V3ToV4ContractorCareers`, chained after V1→V2→V3):

* each actor's score = the **floor** of its existing band (nobody is raised or lowered);
* `career.legacyResolved = opsCompleted`; every other counter 0; nothing is reconstructed from pruned
  history; no wins, losses, income or upgrades are invented;
* every operation in the save is `careerEligible = false`; a running one keeps its lifecycle;
* ledger records carry no contractor attribution (money already credited stays where it is; a refund
  of an old contract takes back nothing, exactly as in Phase 2);
* funds, equipment, skill, career stage, mobility, spatial state, contracts, operations, Field Logs and
  all Phase 2.5 state are untouched.

## 11. Doctrine and spatial wording

`Doctrine.loyalty` is a general behavioural tendency (stickiness to partners and employers), **not** a
player-specific loyalty system; the Phase 5 relationship Loyalty is actor-to-actor in Relations.
`SpatialStatus.Idle` stays the technical state; in the fiction an available contractor is maintaining
contacts, looking for work, relocating occasionally and preparing for commissions. There are no hidden NPC
contracts or fake jobs (NPC-issued work is Phase 4).

## 12. Dev tools

Under "The Network (Phase 2.75)" (development mode only): *Inspect contractor career…*, *Grant
reputation to contractor…*, *Add test funds to contractor…*, *Run career advancement now…*,
*Dump career distribution…*. No normal UI changes: the existing Fame descriptor follows the band.

## 13. Future consumers (not built)

Pawn generation, NPC competition, hiring and loyalty, alternative compensation (Phase 4B), sponsorship,
rivalries, equipment generation, augmentations and legends will read the record, the reputation, the
funds, the equipment, `CareerNeed` and the Tags. None is implemented here.
