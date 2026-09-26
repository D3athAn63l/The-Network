# Simulation: Scheduler, Resolver, Organizations, Willingness, Determinism

> How the off-map world moves without costing TPS, and how its random outcomes stay stable
> across save and load. Related: [PERFORMANCE](PERFORMANCE.md), [STATE_MACHINES](STATE_MACHINES.md),
> [DECISIONS ADR-012/022](DECISIONS.md).

## Contents

1. [Scheduler](#1-scheduler)
2. [Background cadence](#2-background-cadence)
3. [Abstract resolver](#3-abstract-resolver)
4. [Organizations: upkeep, morale, doctrine](#4-organizations-upkeep-morale-doctrine)
5. [Willingness, refusal and bidding](#5-willingness-refusal-and-bidding)
6. [Determinism and RNG](#6-determinism-and-rng)
7. [Population management](#7-population-management)

---

## 1. Scheduler

### 1.1 Shape

```
ScheduledJob { seq: long, dueTick: int, kind: string, target: int, arg: int, createdTick: int }
SchedulerState (persisted): jobs as a flat list
Runtime: binary min-heap ordered by (dueTick, seq); handler table kind → Action<ScheduledJob>
```

### 1.2 Tick entry

```
WorldComponentTick():
  if (!started) EnsureStarted()
  if (Clock.Now < heap.PeekDue) return                 // the ONLY per-tick cost when idle
  budgetJobs = 16; budgetMs = 1.5
  while heap.PeekDue <= Clock.Now and budgetJobs-- > 0 and elapsed < budgetMs:
      job = heap.Pop()
      try handler[job.kind](job)  catch → retry/backoff/quarantine (ARCHITECTURE § 12)
```

- Jobs still due when the budget runs out are simply left in the heap and run next tick. Even
  a catch-up burst after a long pause spreads itself out.
- **Unknown job kinds** (removed in a newer version) are dropped with one info log line. A
  migration should have converted them first.
- **Catch-up policy.** If the mod was absent for a while and then re-added, or the save was
  edited, many jobs can be overdue at once. They run in `(dueTick, seq)` order under the
  budget. Upkeep jobs coalesce: an org whose upkeep is more than two periods overdue runs
  once, scaled by the elapsed time, instead of N times.

### 1.3 Staggering

Periodic per-entity jobs get a **seeded phase offset**:

```
firstDue = now + (Hash(entity.seed, kindSalt) mod period)
nextDue  = lastDue + period ± jitter(seed, runIndex) (jitter ≤ 10% of period)
```

Forty organizations with daily upkeep therefore run about 40 times per 60,000 ticks, spread
across the day, never all at once.

### 1.4 Rules

- **One job per (kind, target) for periodic kinds.** `Schedule` replaces any existing one, so
  duplicates cannot pile up.
- **The entity stores its own next due tick** (for example `ContractorProfile.nextUpkeepTick`
  or `Operation.checkpoints[].dueTick`). The load validator rebuilds any missing job from the
  entity and deletes jobs whose target is gone. The job list is therefore an index; the source
  of truth lives on the entities.
- The time base is `Find.TickManager.TicksGame`, read through the kernel `Clock`.

---

## 2. Background cadence

| Work | Trigger | Frequency | Phase |
|---|---|---|---|
| Intel resolution | job at `dueTick` | once per request | 1 |
| Site claim fallback sample | job, only while the Network site map exists | every 2,500 ticks | 1 |
| Opportunity/lead closing and archiving | job | once each | 1 |
| History retention sweep | job | every 15 days, budgeted | 1 |
| Terminal entity compaction | job | every 15 days, budgeted | 1 |
| Journal prune | on append | — | 1 |
| Reference validation | load pipeline and checkpoints | on load, and per checkpoint | 1 |
| Organization upkeep (recovery, morale drift, funds, recruitment, retirement pressure) | job per org, staggered | every 60,000 ticks (1 day) | 2 |
| Operation checkpoints | job at each checkpoint | per operation, 3–6 per contract | 2 |
| Bidding window collection | jobs | at open, one mid-window pass, at close | 2 |
| Population manager (spawn or retire orgs toward target counts) | job | every 7 days | 2 |
| Deployment watchdog | job, only while a deployment is Active | every 2,500 ticks | 3 |
| Registry reservation audit | job | every 60,000 ticks | 3 |
| Consequence follow-up generation | job scheduled by the rule | once each | 3 |
| Gossip propagation | job scheduled per newsworthy event | once each (at most 2 hops) | 5 |
| Belief decay | lazy on read | — | 5 |
| Relation decay | lazy on read and write | — | 2 |

Nothing in this table runs every tick. The highest routine frequency is the 2,500-tick sample,
and it exists only while a Network site or deployment is physically active.

---

## 3. Abstract resolver

### 3.1 Goals

Cheap, deterministic, partial, and capable of casualties, capture, retreat and delay. Influenced
by history and knowledge. **Explicitly not a combat simulation.** Outputs are coarse bands with
bounded detail, which avoids false precision.

### 3.2 Inputs, frozen at the start of engagement

```
ResolverInputs (persisted on Operation.frozenInputs when the Engaged phase begins)
  forcePower:        from committed tiers × equipment tier × condition × leases bonus
  threatPower:       opportunity threat points (or abstract source difficulty for market procurement)
  preparedness:      Knowledge.Proficiency(contractor, topics[archetype, threat, region, item]) (0..1)
  intelQuality:      lead reliability if the operation targets a known lead (else 0.5)
  logistics:         f(distance in tiles, layer (orbit needs capability), season/biome hazard tag)
  moraleFactor:      from OrgMorale (Confident 1.1 … Desperate 0.75; Reckless raises variance)
  doctrine:          caution, cruelty, professionalism (affect retreat thresholds and variance)
  sponsorship:       flat bonus from sponsor leases/profile
  opposition:        opposing faction tech level tag, "mechanoid"/"insectoid"/"human"
```

These inputs are **frozen**. Reloading, or world changes after engagement begins, cannot change
the outcome. Changes made *before* engagement (for example the player re-equipping a sponsored
contractor) legitimately change it.

### 3.3 Algorithm

```
ratio      = forcePower / max(1, threatPower)
edge       = log2(ratio) + 0.8·(preparedness − 0.5) + 0.4·(intelQuality − 0.5)
             + 0.3·(moraleFactor − 1) − logisticsPenalty + sponsorshipBonus
variance   = base 1.0 × (Reckless ? 1.4 : 1) × (1.2 − 0.4·professionalism)
roll       = edge + variance · NormalLike(rng)          // private PRNG, § 6
band       = Bands.Lookup(roll)  // Triumph ≥ 1.6 · Success ≥ 0.6 · CostlySuccess ≥ 0 · Partial ≥ −0.6
                                  // Failure ≥ −1.4 · Disaster < −1.4   (data table, tunable)
retreat    = doctrine.caution high and band ≤ Partial → convert part of casualties into "wounded"
             and shift band up one step for survival (less cargo, fewer deaths)
```

**Per-band output tables** (data, tunable) give *ranges* per tier:

| Band | Secured payload | KIA (of committed) | Wounded | Capture/Missing chance | Delay |
|---|---|---|---|---|---|
| Triumph | 100% (+ extra loot chance) | 0–5% | 0–10% | 0 | −10%…0 |
| Success | 90–100% | 0–10% | 5–20% | 0 | 0…+10% |
| CostlySuccess | 70–100% | 10–25% | 15–35% | small | +10…+30% |
| Partial | 20–70% | 10–30% | 20–40% | low | +20…+50% |
| Failure | 0–20% | 20–50% | 20–40% | medium | +30…+80% |
| Disaster | 0% | 40–90% | rest | high (a captor faction is chosen) | n/a |

Draws within each range use the operation's `NetRng` stream. Counts are rounded per tier. No
fractional people.

### 3.4 Known characters in operations

Each participating Known Character gets a **fate draw** weighted by role and band. Leaders are
protected by the crew (a lower death weight) unless the band is Disaster. Specialists face
normal odds. Fates are `Unharmed | Wounded | Killed | Captured | Missing`. A captured or missing
character becomes a candidate for a rescue follow-up through consequence rules.

### 3.5 Outputs and consequences

The outcome is committed to `Operation.outcome`, then published: `Operation.Resolved` and, as
applicable, `Contractor.Casualties`, `KnownCharacter.Killed`, `Leader.Killed`,
`Contractor.Captured`, `Cargo.Lost`, `Cargo.Stolen`. Knowledge gains per topic depend on the
band. Even failures teach something, and Disasters teach the most, *if anyone survives*.

### 3.6 Market procurement (no specific site)

Many procurement contracts target "the market", not an opportunity. `threatPower` then comes
from item rarity signals (see [COMPATIBILITY § 2.4](COMPATIBILITY.md#24-rarity-signals)) and
from the contractor's reach, not from combat. The same band machinery applies. Disasters here
mean being robbed, cheated or ambushed.

---

## 4. Organizations: upkeep, morale, doctrine

### 4.1 Upkeep job (daily, staggered)

1. Heal wounded: recovery buckets whose `dueTick` has passed move from wounded to healthy.
2. Drift morale toward baseline. The baseline is set by doctrine and by the `recent` summary.
3. Funds: add the abstract income from completed contracts. Pay upkeep. Equipment condition
   recovers if funds allow.
4. Recruitment: if below capacity and funds allow, recruit (Recruits tier). Promote from Recruit
   to Regular to Veteran based on survived operations (counters).
5. Update retirement pressure (from career age, leader age, losses, prosperity).
6. Invalidate cached strength.

The job is O(1) per org and small. It never touches pawns.

### 4.2 Organizational morale and trauma

The state is `cohesion`, `confidence` and `fatigue`, each 0..1. These are **group** properties,
not pawn mood.

| Input | Effect |
|---|---|
| Casualties (by % of roster, with extra weight for leaders and known characters) | confidence −, cohesion ± (a shared loss can bind the group, or break it if the doctrine is low-loyalty) |
| Successes and triumphs | confidence + |
| Consecutive operations without rest | fatigue + |
| Payment defaults or betrayal by an employer | cohesion −, and trust toward the employer drops sharply |
| Rescue by the player | cohesion +, confidence + |

**Descriptor**, persisted with hysteresis:

| Descriptor | Condition (illustrative) | Behavioural effect |
|---|---|---|
| Confident | confidence > 0.7 | accepts higher risk; slight resolver bonus |
| Steady | default | — |
| Cautious | confidence 0.3–0.5, caution-doctrine orgs | refuses high-risk work; retreats early |
| Shaken | recent shock, confidence < 0.3 | refuses most combat work; higher prices |
| Reckless | low confidence *and* high ambition/greed, or desperate funds | accepts suicidal work; higher variance |
| Exhausted | fatigue > 0.75 | refuses all work until rested |
| Desperate | funds critically low | cheap, risky bids; fraud chance (low-loyalty doctrine) |

A sharp shift publishes `Organization.MoraleShifted`. Long-term trauma is represented by the
`recent` summary (casualty history), which lowers the baseline that confidence recovers toward.

### 4.3 Doctrine drift

Doctrine is seeded at creation from a template plus noise. It drifts **slowly**: at most ±0.02
per notable event and bounded by the template ±0.3. The drift follows what the org experiences.
Repeated betrayals reduce loyalty. Surviving by retreating increases caution. This is how
"known for accepting suicidal contracts" emerges from behaviour, not from a label.

### 4.4 Recruitment and experience

Tiers are Recruit → Regular → Veteran. Promotion is driven by survived operations
(`opsSurvived` per tier cohort, aggregated), so an org's quality rises with its history.
**Contractor learning** is separate: topic knowledge lives in `KnowledgeBook` and grows from
operations touching those topics.

### 4.5 Leadership and succession

- The leader is always a Known Character (a record; it becomes a pawn only when physically
  needed).
- **When the leader is killed or captured:** succession runs immediately inside the event
  consumer. It picks the best living Known Character (by role, lieutenant first, then
  notability), otherwise promotes a Veteran from headcount into a new Known Character. The
  org's identity does not change. It publishes `Leader.Succeeded`, gives a morale shock, and
  may shift doctrine a little toward the successor's traits.
- **No successor possible** (roster empty) → `Dissolved`.
- Phase 6 adds contested succession, which can cause fragmentation.

### 4.6 Retirement transformation, fragmentation, mergers

| Process | Trigger | Result |
|---|---|---|
| Retirement | retirement pressure > threshold and the roster is stable | org → `Retired`. The leader or veterans may become `Individual` actors with `IntelSourceProfile`, `IntroducerProfile`, `TraderProfile` or a recruiter role. `Knowledge.Transfer(org → individual, 0.6)`. Relations are inherited at 0.5. A Legend if eligible. |
| Fragmentation | contested succession, very low cohesion with a large roster, or a doctrine split | 1–2 splinter actors (new IDs, `splitFrom`) take a share of the roster, a share of the leases, knowledge (0.7) and selected rivalries. The parent continues smaller, or dissolves. |
| Merger / absorption | two weak orgs with a strong positive edge, or a dominant org absorbing a failing one | the survivor takes the roster, leases, knowledge (max-merge) and relations (weighted). The absorbed actor becomes `Absorbed`, with `absorbedInto`. |
| Settlement founding (later) | long-retired prosperous org | optional content through a world-object adapter; out of scope for core |

Reputation is inherited only as **legacy text** ("successors of the Red Hand"). It is never
copied as counters. Successors earn their own summaries.

---

## 5. Willingness, refusal and bidding

### 5.1 Willingness model

`Willingness.Evaluate(contractor, contract) → Decision { accept: bool, priceFactor, depositFactor,
etaFactor, riskTolerance, conditions[], reasonKeys[] }`

Inputs, all cheap reads:

| Factor | Source |
|---|---|
| Danger vs appetite | estimated risk (resolver pre-estimate, **no RNG**) against doctrine caution, morale and `op.suicidal.*` history |
| Losses | the `recent` casualty summary and the current wounded share |
| Relationship | edge contractor→issuer: standing, trust, betrayals, defaults; open obligations |
| Doctrine | the kind's alignment with doctrine (cruelty for assassination, discretion for black work) |
| Morale | the descriptor (Exhausted refuses everything, Shaken refuses combat work) |
| Workload | `commitments.Count` against the roster's available headcount |
| Relevant history | knowledge of the topics; past failures on the same kind or region |
| Political | the issuer's or target's faction relations with the contractor's origin, and sanctions (Phase 5) |
| Payment | the offered budget against the contractor's price floor (greed, funds, reputation premium) |

Each factor produces a contribution and possibly a **veto** with a reason key
(`TooDangerous`, `Overcommitted`, `BadBlood`, `Exhausted`, `AgainstDoctrine`, `PoliticalRisk`,
`PayTooLow`, `YouOweUs`). The decision is deterministic given the inputs. Where a tie-break
coin is needed, it comes from `NetRng(contract.seed, contractor.id, "bid")`.

### 5.2 Bidding

- When a contract is posted, `Bidding.OpenWindow` selects **eligible contractors**: active,
  with the capability, not quarantined, and (Phase 4) visible to the issuer through contacts
  or listing. The candidates are capped at 12, chosen by a relevance score (knowledge of the
  topics, region, relationship).
- Evaluations are spread over the window: some at open, some mid-window, the rest at close.
  This makes offers trickle in. It also spreads cost.
- Each accept becomes an `Offer` with price, deposit, ETA, insurance, risk tolerance and
  conditions. Each refusal becomes a `Refusal` with reason keys.
- The issuer (the player through the UI, or NPC logic) accepts one offer. The rest become
  Superseded.
- **Phase 2 scope:** a single eligible contractor, or the best-scored one, bids. The data path
  is identical to full bidding, so enabling competition in Phase 4 is a content and UI change.

### 5.3 Pricing

```
price = marketValue(def, count) × rarityPremium × riskPremium(estimatedRisk, doctrine)
        × relationshipFactor(edge) × reputationPremium(contractor fame) × moraleFactor
        × (1 − favorDiscount if an obligation is cashed)
deposit = price × depositShare(doctrine.greed, trust in issuer)
eta     = baseTravel(distance, logistics) × (1 + workload) × etaFactor
```

Pricing is purely derived. The final numbers are persisted on the Offer. They are never
recomputed after the offer is made.

---

## 6. Determinism and RNG

### 6.1 Principles

1. **No outcome depends on when the save was loaded, or how many times.**
2. **No outcome depends on UI inspection.** Read models never draw randomness.
3. **Random results are committed** (persisted) at a defined decision point. After that point
   they are data.
4. **Before the decision point, results are reproducible**: same seed plus same inputs gives the
   same result. Reloading and waiting does not reroll.

### 6.2 Seed hierarchy

```
networkSeed  = fixed at bootstrap = Hash(StableStringHash(world.info.seedString), bootstrapTick, 0x4E4554)
entity.seed  = Hash(networkSeed, entity.id, kindSalt)                  // persisted on the entity
stream       = NetRng(entity.seed, decisionKey, entity.rerollNonce)    // e.g. "intel.lead", "op.band"
```

`NetRng` is a small, private, **self-contained PRNG** (SplitMix64-style) implemented in The
Network. It is **not** `Verse.Rand`. Reasons:

- vanilla `Rand` state is global, not persisted, and consumed by everything, so any vanilla call
  in between shifts the sequence;
- a private generator gives identical sequences regardless of mod list, DLCs and unrelated game
  activity;
- it can be used in headless tests without the game.

### 6.3 When vanilla generation is needed

Some results must come from vanilla generators: `TileFinder`, `PawnGenerator`, `ThingMaker`
with quality, and site part parameters. These calls are wrapped:

```
Rand.PushState(Hash(entity.seed, decisionKey)); try { vanillaCall } finally { Rand.PopState(); }
```

Their **results are committed immediately**: tile stored, pawn stored (bound), Things created
and stored in the site part. The next load reads the committed results. It does not re-run the
generator, so mod-list differences at load time cannot change past outcomes.

Caveat, spike S8: `TileFinder.TryFindNewSiteTile` itself mixes in `TicksGame` for Odyssey
landmark selection. The result is still stable because resolution happens at a fixed due tick,
and it is committed at once.

### 6.4 Commit points

| Question from the brief | Answer |
|---|---|
| When is a random outcome committed? | At the decision point listed below. Up to then it is reproducible from the persisted seed. |
| Is a seed stored? | Yes, on every stateful entity (`seed`, `rerollNonce`). |
| Does loading before completion reroll? | No. Same seed and same (frozen) inputs give the same result. |
| When is opportunity cargo fixed? | At `Opportunity.Generate` (def, count, stuff). The Things are created at `Materialize`. |
| When are contractor casualties fixed? | At the Engaged → resolve checkpoint, as `Operation.outcome`. Physical encounters: at reconciliation, from actual pawn state. |
| When is Intel truth fixed? | At `intel.resolve`: lead/no-lead, archetype, payload, location, threat, reliability. |
| Intel search duration? | At submit. |
| Offer terms? | When each offer is created (persisted on the Offer). |
| Organization creation (name, doctrine, roster)? | At creation. |
| Consequence rule firing? | When the triggering event is dispatched (seed from `event.seq`). The follow-up content is generated when its job runs, from its own seed. |
| Can developers reroll deliberately? | Yes. The dev action "Reroll entity" increments `rerollNonce` and returns the entity to its pre-commit state where that is safe (Intel: Searching; Operation: before Engaged; Opportunity: only while Latent). It is never exposed to players. |

### 6.5 Save-scumming stance

Reloading cannot reroll background outcomes. Reloading can still let the player **change their
own actions** (a different offer, different timing, better equipment for a sponsored crew),
which legitimately changes inputs. That is intended. The world is deterministic, and the
player's choices are not.

---

## 7. Population management

- **Targets**: the active NPC contractor org count, from settings (default 12–40, scaled by world
  size and faction count), and a broker/intel-source count (Phase 5).
- **The weekly job** creates organizations from templates when below target (tied to origin
  factions when they exist, independent otherwise). It lets the natural lifecycle (retirement,
  dissolution, wipe-outs, mergers) bring the count down, and never deletes an actor
  artificially.
- **Templates** (a Def, later) give the name grammar, a doctrine range, a size range, specialties,
  an equipment tier range, preferred pawn kinds (with fallback chains), and an origin faction
  def filter.
- Bootstrap creates an initial population on the first tick of a new game, or when the mod is
  added to an existing save (Phase 2+).
