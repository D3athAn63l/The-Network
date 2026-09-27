# Events and History

> How The Network notices things, remembers them at bounded cost, and lets its own history
> change what happens next. Related: [DATA_MODEL § 13](DATA_MODEL.md#13-history-summaries-legends),
> [SIMULATION](SIMULATION.md), [DECISIONS ADR-009/010/011](DECISIONS.md).

## Contents

1. [Network events](#1-network-events)
2. [Event catalog](#2-event-catalog)
3. [From events to history records](#3-from-events-to-history-records)
4. [Retention, pruning and aggregation](#4-retention-pruning-and-aggregation)
5. [Summaries: the simulation reads its own history](#5-summaries-the-simulation-reads-its-own-history)
6. [Reputation through history](#6-reputation-through-history)
7. [Facts, awareness, rumors and witnesses](#7-facts-awareness-rumors-and-witnesses)
8. [Gossip](#8-gossip)
9. [Legends](#9-legends)
10. [Chain reactions (consequence rules)](#10-chain-reactions-consequence-rules)
11. [Save-size budget](#11-save-size-budget)

---

## 1. Network events

### 1.1 What an event is

A `NetworkEvent` is an immutable statement that **something meaningful happened in the
Network**. It is published exactly once, by the subsystem that caused it or observed it.

```
NetworkEvent header: seq(long) · tick(int) · typeKey(string) · schema(byte)
                    · importance(Ephemeral|Minor|Notable|Major|Legendary)
                    · subjects(EntityRef[]) · place(TileRef?) · flags
payload: typed fields per event class (e.g. ContractFailedEvent { contract, contractor, causeKey, … })
```

It is **not** used for UI refresh, cache invalidation or per-tick notifications. Those use dirty
flags and the `StateVersion` counter.

### 1.2 Publishing and dispatch

```
Events.Publish(evt):
  evt.seq = ids.nextEventSeq++ ; evt.tick = Clock.Now
  journal.Append(evt)                               // bounded
  if dispatching: fifo.Enqueue(evt); return         // re-entrancy: breadth-first
  dispatching = true
  Dispatch(evt); while fifo.TryDequeue(out e): Dispatch(e)   // cascade cap: 64 events per root publish
  dispatching = false

Dispatch(e): for handler in table[e.typeKey] (fixed order):
               try handler(e) catch → record failed consumer, flag e.hadErrors, CONTINUE (never re-dispatch)
```

**Consumer order is fixed and global.** It is defined once at startup. Later consumers can rely
on state that earlier ones have already updated.

| Order | Consumer | Role |
|---|---|---|
| 1 | Domain owners (Contracts, Opportunities, Custody, Intel) | state already transitioned by the publisher; secondary bookkeeping |
| 2 | **History** | write the record (if Notable or higher), update summaries, mark reputation dirty |
| 3 | Organizations | morale, roster, succession triggers |
| 4 | Relationships and Obligations | edge deltas, favors and debts |
| 5 | Knowledge | topic experience |
| 6 | Reputation | epithet re-evaluation (cheap; summaries only) |
| 7 | Consequence Engine | schedule follow-ups (jobs), never inline generation |
| 8 | Gossip (Phase 5) | schedule propagation jobs |
| 9 | Presentation | letters and messages (last, so text reflects the final state) |

### 1.3 Which events persist

| Class | Persisted? | Examples |
|---|---|---|
| **Ephemeral** | Not published as NetworkEvents at all | cache invalidation, UI refresh, "upkeep ran" |
| **Minor** | In the bounded journal only; feeds summary counters | `IntelRequested`, `OperationCheckpoint`, `OfferReceived`, `ActorLearnedTopic` |
| **Notable** | Journal **and** a history record (tiered retention) | `IntelLeadDelivered`, `OpportunityClaimed`, `ContractCompleted`, `ContractFailed`, `KnownCharacterPromoted` |
| **Major** | Journal and a history record kept for the life of the participants | `ContractorRescued`, `LeaderKilled`, `PlayerBetrayedContractor`, `CargoStolen`, `OrganizationFragmented` |
| **Legendary** | History record plus immediate Legend evaluation | `ImpossibleRecovery`, `Massacre`, `OrganizationWipedOut` |

Importance is a **default per event type**. The publisher may raise it using magnitude, for
example a contract worth ten times the median.

### 1.4 No double application after save and load

This follows from how dispatch works, not from bookkeeping:

1. **Dispatch is synchronous and completes inside one call on the main thread.** RimWorld saves
   happen between our calls, never during them. A save therefore always sees either *all* of
   an event's consumer effects or *none* of them.
2. **Events are never re-dispatched on load.** The journal is an audit trail, not a replay log.
3. **Deferred reactions are scheduler jobs.** A job runs once and is removed in the same step
   that applies its effect. Each job carries the data it needs, not a pointer to a journal
   entry, which may since have been pruned.
4. **Effects on the outside world happen inside the same synchronous step**: silver moved,
   letters sent, sites created. Money movements are recorded in the contract ledger in that same
   step ([DATA_MODEL § 16](DATA_MODEL.md#16-money)).
5. **Observed vanilla facts are idempotent by state check.** Signals and reconciliation
   compare against persisted state before publishing. For example a `DeploymentEntry.fate` that
   is already `Killed` is never killed again, so a signal plus a reconciliation pass cannot
   double-count.

**When a consumer throws.** Exceptions inside a consumer do not roll back earlier consumers, and
the event is **never re-dispatched**: earlier consumers may already have changed relations,
favors, history, money or scheduled follow-ups, and running them again would apply those effects
twice. Instead:

1. The failure is recorded once in `diagnostics.failedConsumers` (event seq, type key, consumer,
   message, tick), the event is flagged `hadErrors`, and one error is logged.
2. The entity the consumer was updating is marked dirty, `Degraded` or `Quarantined`, as the
   subsystem's policy says.
3. The owning subsystem's **reconciliation pass** repairs its own state from persisted facts
   (summaries from records, roster arithmetic, custody from pawn state) at the next validation.
4. An individual consumer may be retried **only if it has an explicit idempotency guard**, such
   as a persisted "last applied event seq" on the state it changes. Consumers without a guard
   are never retried.

This is deliberately different from scheduler jobs, which may be retried with backoff when their
kind is idempotent or state-guarded ([ARCHITECTURE § 12](ARCHITECTURE.md#12-threading-exceptions-and-failure-containment)).
It is simpler and safer than attempting transactional rollback over RimWorld state.

### 1.5 Deterministic handling

- Consumer order is fixed. Cascades are breadth-first in FIFO order.
- Consumers never draw from vanilla `Rand`. Any randomness a consumer needs is derived from
  `(networkSeed, event.seq, consumerSalt)` with `NetRng`
  ([SIMULATION § 6](SIMULATION.md#6-determinism-and-rng)). The same event therefore always
  produces the same reaction, whenever and however many times the save was reloaded before it
  happened.

### 1.6 Event versioning

- `typeKey` strings are **permanent** (for example `"Contract.Failed"`). Renaming a C# class
  keeps its key.
- `schema` increments when payload fields change meaning. The journal is bounded and never
  replayed, so old schemas only matter to diagnostics and UI, which handle them defensively
  (missing field means "unknown").
- An event class that no longer exists at load time is dropped from the journal with one info
  log line. This is harmless.
- **History records** have their own `schema` and are migrated like any other persistent entity
  ([SAVE_AND_MIGRATION § 4](SAVE_AND_MIGRATION.md#4-migrations)).

### 1.7 Journal pruning

Entries are kept while `count ≤ 256` **or** `age ≤ 15 days`, and never more than 1024. Pruning
runs on append (amortized O(1): trimming from the front of a ring buffer). `droppedCount` is
persisted for diagnostics.

---

## 2. Event catalog

"Ph." is the phase that introduces the event. Imp. is the default importance: Mi = Minor,
N = Notable, Ma = Major, L = Legendary.

| Event | Ph. | Imp. | Key subjects | Main consumers |
|---|---|---|---|---|
| `Network.Bootstrapped` / `Network.Loaded` | 1 | Mi | — | diagnostics |
| `Cast.Imported` | 1 | Mi | — (counts, settings version) | diagnostics |
| `Intel.Requested` | 1 | Mi | requester, source | summaries |
| `Intel.LeadDelivered` | 1 | N | request, lead, opportunity, source, round | history, knowledge(source), relations (source), letters |
| `Intel.NoLead` | 1 | Mi | request, round | summaries |
| `Intel.SearchContinued` | 1 | Mi | request, round | summaries |
| `Intel.Concluded` | 1 | Mi (N if the search ended with no lead at all) | request, lead count | history, knowledge(source), summaries, letters |
| `Intel.Cancelled` / `Intel.Invalidated` | 1 | Mi | request | letters |
| `Opportunity.Generated` / `.Materialized` | 1 | Mi | opportunity | — |
| `Opportunity.Engaged` | 1 | Mi | opportunity, player | summaries |
| `Opportunity.Claimed` | 1 | N (Ma if high value) | opportunity, claimer, `recoveredBand` (partial recovery is a claim) | history, knowledge, summaries, letters |
| `Opportunity.Expired` / `.Abandoned` / `.Destroyed` / `.Invalidated` | 1 | Mi | opportunity | summaries |
| `Opportunity.ExpiringSoon` | 1 | Mi | opportunity | letters (the optional expiry warning) |
| `Opportunity.LostToCompetitor` | 4 | N | opportunity, competitor | relations (rivalry), consequences |
| `Reference.Invalidated` | 1 | Mi | owning entity | owning subsystem |
| `Contract.Posted` | 2 | Mi | contract, issuer | bidding |
| `Contract.OfferReceived` / `Contract.Refused` | 2 | Mi | contract, bidder, broker | summaries (refusal counters), UI |
| `Contract.Quoted` | 2 | Mi | contract, bidder, broker | UI (the client-facing quote) |
| `Contract.Awarded` | 2 | Mi | contract, contractor | relations (familiarity) |
| `Contract.Delayed` / `.RenegotiationRequested` | 2 | Mi | contract | relations (trust −), letters |
| `Contract.Completed` / `.PartiallyCompleted` | 2 | N | contract, contractor, issuer | history, relations, morale, knowledge, reputation |
| `Contract.Failed` | 2 | N / Ma | contract, contractor, causeKey | history, relations, morale, consequences |
| `Contract.Cancelled` / `.Voided` / `.Expired` | 2 | Mi | contract | relations (small), letters |
| `Contract.Continued` | 3 | N | parent, child, inheritor | history (causal link) |
| `Payment.Received` / `Payment.Defaulted` | 2 | Mi / N | payer, payee | relations, obligations |
| `Operation.Started` / `.Checkpoint` | 2 | Mi | operation | — |
| `Operation.Resolved` | 2 | N | operation, contractor, band | orgs, knowledge, history |
| `Contractor.Casualties` | 2 | N / Ma | org, counts | morale, roster, history |
| `Contractor.Captured` / `.Missing` / `.Stranded` | 2 | Ma | org, characters, captor | consequences (rescue), relations |
| `Contractor.Rescued` | 3 | Ma | rescuer, rescued org, characters | history, relations, obligations, morale, gossip |
| `KnownCharacter.Promoted` | 2 | N | character, org | history |
| `KnownCharacter.Killed` | 2 | N (Ma if leader) | character, killer? | history, morale, succession, legends |
| `Leader.Killed` / `Leader.Succeeded` | 2 | Ma / N | org, old, new | morale, history |
| `KnownCharacter.CapturedByPlayer` / `.Defected` / `.Lost` | 3 | Ma / Ma / N | character | relations, history |
| `Deployment.Reconciled` | 3 | Mi | deployment | roster (return), leases |
| `Cargo.Lost` / `Cargo.Stolen` | 2 | N / Ma | contract, thief? | consequences (hunt), relations |
| `Player.BetrayedContractor` | 3 | Ma | player, org | relations, gossip, reputation, sanctions |
| `Employer.RefusedPayment` | 2 | Ma | issuer, contractor | relations, gossip, sanctions |
| `JointOperation.Completed` | 7 | N | actors | relations (+) |
| `Organization.MoraleShifted` | 2 | Mi | org, descriptor | history (Notable only on extreme shifts) |
| `Organization.Retired` / `.Dissolved` / `.WipedOut` | 6 | Ma / Ma / L | org | legends, knowledge transfer |
| `Organization.Fragmented` / `.Merged` / `.Absorbed` | 6 | Ma | orgs | lineage, knowledge, relations inheritance |
| `Obligation.Created` / `.Settled` / `.Defaulted` | 5 | Mi / Mi / N | debtor, creditor | relations |
| `Contact.Introduced` | 5 | Mi | player, introduced, introducer | contacts |
| `Belief.Spread` / `Rumor.Fabricated` | 5 | Mi | holder, subject | beliefs |
| `Exposure.Changed` (evidence, witnesses) | 7 | N / Ma | record, perpetrator, principal | consequences (faction goodwill), relations |
| `Legend.Promoted` | 6 | L | subject | letters, UI |
| `Epithet.Gained` / `.Lost` | 4 | Mi | actor, epithet | letters (optional) |

The table is a catalogue, not a commitment to implement everything early. Adding an event type
is cheap: a class, a typeKey and consumer registrations.

---

## 3. From events to history records

The **History consumer** turns events into records using a per-type rule:

```
importance = typeDefault
           ↑ raise one tier if: value ≥ 5× median contract value, or casualties ≥ 50% of committed,
                                or a leader/known character died, or first-of-its-kind for the actor
           ↑ Legendary if: survived a Disaster band with success, or wipe-out, or ≥ 3 Major records
                           in one lineage
if importance ≥ Notable → create HistoryRecord (participants, place, subject, magnitudes, awareness)
always → update summaries (actor, character, pair) with the event's deed deltas
```

**Important-event criteria** (what earns a record rather than a counter):

1. It changed an actor's or character's **status** (death, capture, rescue, defection,
   retirement, succession).
2. It changed a **relationship qualitatively** (betrayal, rescue, refusal to pay, joint
   victory).
3. It was **extreme in magnitude** for its kind (value, casualties, danger, speed).
4. It was a **first** for that actor ("first contract for the player", "first mechanoid site").
5. It starts or ends a **lineage** (a chain reaction root, or its conclusion).

Everything else is aggregated into counters and never stored individually.

---

## 4. Retention, pruning and aggregation

Retention is tiered and uses reference counting. There is **no unbounded log**.

| Tier | Kept while | Hard caps | On expiry |
|---|---|---|---|
| Notable | any *principal* participant still lists it among its 25 most recent Notable records, **or** age < 1 in-game year | 25 per actor (per principal participant); global 3,000 | dropped; counters already hold its contribution |
| Major | any participant is active (not Tombstone), **or** age < 5 years | 60 per actor; global 1,500 | compacted: its deed contributes to the participants' Legend candidacy, then dropped unless promoted |
| Legendary | forever | global 400 (beyond that, the oldest move to legend-only form) | frozen into a Legend deed |

- **Principal participants** are roles such as issuer, contractor, rescuer, victim or leader.
  Bystander roles do not hold records alive.
- **Retention sweep**: one scheduler job per in-game quarter (15 days), budgeted to 500 records
  per run and continued in the next run if needed. The sweep walks the ledger in id order; it
  never runs per tick.
- **Salient pointers** (`RelationEdge.salient`, `ActorRecordSummary.extremes`) count as retention
  references. A pointer to a pruned record resolves to "forgotten" and is cleared lazily.
- **Actors that end** (retired, dissolved, absorbed) are compacted to **tombstones** after 2
  years without references: `{ id, name, kind, founded, ended, fateKey, legendId? }`, about
  60 bytes. Tombstones are kept forever so every historical `ActorId` still resolves to a name.
- **Known Characters** that are dead, not legendary and unreferenced for 2 years are compacted
  the same way: `{ id, name, org, diedTick, causeKey }`.

---

## 5. Summaries: the simulation reads its own history

The rule: **no system other than UI and legend promotion reads the ledger.** Behaviour reads:

| Summary | Updated by | Read by |
|---|---|---|
| `ActorRecordSummary.lifetime` (counters) | History consumer, O(1) per event | reputation inference, legend scoring, UI |
| `ActorRecordSummary.recent` (exponential decay, half-life about 1 year) | History consumer, decay applied lazily on read and write | willingness, pricing, bidding, morale baselines |
| `ActorRecordSummary.extremes` | History consumer | narrative, legends |
| `CharacterRecordSummary` | History consumer | promotion, succession, legends |
| `RelationEdge.counters` and `salient` | Relations consumer | willingness, favors, gossip targeting |
| `KnowledgeBook` | Knowledge consumer | resolver preparedness, Intel reliability |
| `OrgMorale` | Organizations consumer | resolver, willingness, retirement |

**Deed keys** are a controlled vocabulary of strings, so summaries survive code changes:

`contract.done.<kind>`, `contract.failed.<kind>`, `contract.fast`, `contract.late`,
`client.abandoned`, `employer.betrayed`, `employer.betrayedBy`, `rescue.performed`,
`rescue.received`, `recovery.impossible`, `op.suicidal.accepted`, `op.suicidal.survived`,
`discretion.kept`, `discretion.exposed`, `casualties.taken`, `casualties.inflicted`,
`payment.defaulted`, `payment.onTime`, `cargo.lost`, `cargo.stolen`.

New keys are appended freely. Unknown keys in old saves are kept as data.

---

## 6. Reputation through history

Reputation is **derived from what actually happened**. It is not a stat chosen at creation.

1. **Epithet definitions** (later an XML Def, `NetworkEpithetDef`, keyed by string) have
   conditions over summary counters and ratios. For example, "Reliable" requires
   `contract.done ≥ 8` and a failure ratio below 0.1, measured in `recent`.
2. **Hysteresis.** An epithet is gained at score ≥ 0.7 and lost at ≤ 0.5, and evaluation happens
   only when the summary is dirty. The current set is persisted in `PublicReputation`, so it has
   inertia. Reputation lags reality the way real reputations do.
3. **Public vs perceived.** Phases 2–4 use *public* reputation, computed only from records with
   `Awareness.Public`. Phase 5 adds *perceived* reputation, `Reputation.As(observer, subject)`.
   It blends public reputation with the observer's beliefs (rumors), weighted by confidence,
   and with **direct experience** (the relation edge counters), which gets the highest weight.
   **Personal experience outranks hearsay** by construction: direct experience has weight 1.0,
   a witnessed belief 0.6, and a rumor at most 0.3 × confidence.
4. **Player-facing identity** is the epithets and the salient deeds ("known for finishing jobs
   unusually quickly: 11 early deliveries"), plus a **fame tier** kept in `PublicReputation`
   with the same hysteresis: Unknown, Local, Established, Famous, Legendary for contractor groups
   (master § 66), and Unknown, Local, Established, Respected, Renowned, Legendary for the player's
   registered group (master § 39). The master's reputation dimensions (reliability, prestige,
   speed, combat, integrity, discretion) are internal inputs. Raw numbers stay internal.
   **Fame is not capability**: an actor's fame tier and epithets are separate from its
   operational experience tier (which comes from its simulation or, for the player, the real
   colony). A global-cast actor starts with the template's fame, then its own record takes over.
   Fixers have fame and epithets too ("absurd contacts", "cheap but unreliable").
5. **Consumers**: willingness and refusal, pricing and risk premium, bidding priority,
   sanctions and blacklists (Phase 5), the likelihood of introductions, and legend scoring.

---

## 7. Facts, awareness, rumors and witnesses

The Network does **not** assume that every action is known to everyone. The architecture
separates three layers:

| Layer | Holds | Store |
|---|---|---|
| **Fact** | what happened, including the hidden truth (true principal, perpetrator) | `HistoryRecord` |
| **Awareness** | who knows *that it happened* and how certainly | `HistoryRecord.awareness` |
| **Belief** | what a specific actor believes, possibly wrongly | `BeliefStore` (Phase 5) |

```
Awareness
  scope: Public | Regional(regionKey) | Involved | Secret
  knowers: ActorId[]            // explicit extras (≤ 8); Involved = participants implied
  perpetratorKnown: Unknown | Suspected | Confirmed
  principalKnown:  Unknown | Suspected | Confirmed
```

- Phases 1–4 set `scope = Public` for normal work and `Involved` for Private or Confidential
  contracts.
- **Consequences that depend on knowledge**, such as faction goodwill penalties for an
  assassination, are applied only when awareness reaches a threshold (`Exposure.Changed`).
  Doing the action is not enough.
- **Evidence and witnesses (Phase 7).** Physical encounters report potential witnesses: pawns of
  other factions on the map who survived, captured contractors who can be interrogated, and
  items left behind. They raise `perpetratorKnown` or `principalKnown` through
  `Exposure.Changed` events. There is no crime simulation, just a small set of evidence rules
  evaluated at encounter reconciliation.
- **Rumors (Phase 5)** are Beliefs that may be true, partially true, exaggerated, obsolete,
  false or maliciously fabricated (`distortion`). A fabricated rumor has `truth = null`. An
  obsolete rumor points at a fact that newer facts supersede. The UI presents beliefs as
  hearsay with a source attribution ("word from the Ashen Coil").

---

## 8. Gossip

Gossip is **event-driven and lightweight**. No messenger pawns.

1. When a Major or Legendary event is processed, Gossip computes `newsworthiness` from the
   importance, the fame of the participants (reputation and legend scores) and how shocking it
   was (betrayal and massacre weigh heavily).
2. If it passes the threshold and the record's scope allows spreading, one **propagation job**
   is scheduled after a delay that depends on the region (same region about 2 days, elsewhere
   about 7 days).
3. When the job runs it picks a bounded audience (at most 12 actors): allies and rivals of the
   participants, actors in the same region, and brokers (who amplify). It then:
   - widens `awareness` (Public becomes effective for that audience), or creates Beliefs for
     non-public facts, with distortion rolled per recipient from the seeded RNG and the source's
     discretion;
   - applies *second-hand* relation deltas at 0.25 of first-hand weight.
4. Brokers with high reach may schedule **one** further hop. The hop depth is capped at 2.

Cost is O(audience) per newsworthy event, a few times per in-game month at most.

---

## 9. Legends

Legends are the lightweight long-term memory of exceptional people and organizations, kept after
their operational existence ends. They **do not retain Pawns**.

- **Candidacy score**, computed at end of life (death, retirement, dissolution) or when crossing
  a Legendary record: `Σ deed weights (Major = 3, Legendary = 10) + reputation fame + player
  involvement bonus`.
- **Promotion** above the threshold creates a `Legend`: frozen name, epithets, lifespan, fate,
  at most 7 deed snapshots and at most 6 associates. Deed text is frozen as translation keys plus
  argument *strings*, so it still reads correctly after mods are removed.
- The legend subject's Known Character pawn (if any) is **released** to vanilla. Vanilla GC
  decides its fate, and the Legend no longer needs it.
- Legends feed: the History tab ("Remember Dead Red?"), name reuse avoidance, the reputation of
  successors ("Dead Red's successors still trade on the name"), and occasional rumor and
  gossip seeds.
- Budget: at most 400 Legends. Beyond that, the lowest-scored non-player-involved legend is
  demoted to a one-line tombstone.

---

## 10. Chain reactions (consequence rules)

Chain reactions come from **consequence rules** reacting to events. There are no campaign
scripts.

```
ConsequenceRule (C# predicate + XML-tunable weights, keyed by event type)
  on:          event typeKey
  when:        predicate over event + summaries (cheap, no ledger scans)
  chance:      seeded (networkSeed, event.seq, ruleKey)
  produces:    Opportunity (archetype, payload, location near event place) and/or Contract draft
  lineage:     parent = event's contract/opportunity; depth = parent.depth + 1
  guards:      depth ≤ 4 · rule cooldown per actor · global follow-up budget (e.g. ≤ 6 active)
```

Example chain (each arrow is one rule firing on the previous outcome):

```
Recovery contract resolved (band Partial, evidence of raider group)  → rule "raider.trace"
  → Opportunity: raider camp (escort contract offered by the victim faction proxy)
  → Escort completed, leader escaped (committed outcome flag)       → rule "leader.escaped"
  → Opportunity: hunt / investigation lead on the named raider leader (Known Character promoted)
```

Follow-ups are **scheduled jobs** that run a little later (hours to days). They never run inline
during dispatch. That keeps dispatch cheap, and it makes chains feel like the world reacting.

---

## 11. Save-size budget

Targets for a 10-year save with the default cast of about 100 contractor identities (Solos to
companies) and a small set of Fixers:

| Item | Count (cap) | Approx. bytes each (XML) | Total |
|---|---|---|---|
| Cast snapshot (template copies until instantiated, then links) | ~120 | 300 / 40 | ~10–40 KB |
| Actors (active + tombstones) | ~120 + 400 | 1,200 / 60 | ~170 KB |
| Known Characters (records; every Solo and Fixer embodies one) | 400 | 400 | ~160 KB |
| History records | ≤ 4,900 (3,000 + 1,500 + 400) | 350 (measured in Phase 1: ~500 before indentation, ~600 in the file; see [S18](spikes/S18-performance.md)) | ≤ 1.7 MB (typical 300–600 KB); ≈ 2.5–3 MB at full caps with the measured size |
| Legends | ≤ 400 | 1,200 | ≤ 480 KB (typical 50 KB) |
| Relation edges | ~1,500 | 250 | ~375 KB |
| Contracts, operations (terminal ones compacted after 1 year into history) | ~200 live | 1,000 | ~200 KB |
| Journal | ≤ 1,024 | 200 | ≤ 200 KB |
| **Typical total** | | | **~1.2–1.8 MB** (hard ceiling ~3.5 MB) |

**Terminal contracts, operations, intel requests and deployments are compacted after 1 in-game
year.** Their history record (if any) keeps the story, and the entity itself is deleted.
References to deleted terminal entities resolve to "archived". This is the second pruning lever
next to history retention.

A dev action prints current counts against these caps
([DEBUGGING § 3](DEBUGGING.md#3-dev-actions)).
