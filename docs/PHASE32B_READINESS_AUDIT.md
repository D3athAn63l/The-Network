# Phase 3.2B — Groups and progressive concretization readiness audit

**2026-10-06 · design/source audit only · owner decisions ACCEPTED in § 12 (ADR-057).**

The owner reviewed audit HEAD **`7d5e4ebc9308a12d4a1416c08e1a3db1c14b8bac`** and accepted
the recommended path. The current verdict is **READY FOR 3.2B IMPLEMENTATION**. The accepted
design remains future work: **Phase 3.2B is NOT IMPLEMENTED**, and S26/S27 remain PARTIAL.

Audited `main`: **`a472766ff26a8af975d2a15c18d690cc13f8aa13`**. Its subject is
`Merge pull request #11 from D3athAn63l/claude/new-session-nhng3f`; its second parent is
`7ba3f19b0d3c8d8da8604cd7102ff6d37144cb33`, the final acceptance documentation commit.
[PR #11](https://github.com/D3athAn63l/The-Network/pull/11) is **merged**. Phase 3.2A is
**MERGED / HEADLESS VALIDATED / OWNER RUNTIME VALIDATED**: final owner acceptance on
`e768fef` was 022 **41/0/0** → clean SAVE/LOAD → 025 **11/0/0**, total **52/0/0**;
**1 bound / 1 healthy / 1-of-1 retained / 0 integrity findings**. This audit does not rerun
or extend that owner evidence.

The attached readiness brief authorizes an audit, expressly **not production implementation**.
The shipped DLL, production source, ordinary tests, runtime scenarios and save fields are unchanged.
Save format remains **5**, with no production Harmony reference. Proposed behavior below is not
as-built behavior. The reviewed Phase 3 identity/authority model remains intact. Owner acceptance
freezes implementation direction, not evidence that groups or future runtime scenarios already work.

## 1. Evidence and corrected status

Game evidence comes from the reference repository's 1.6 archive, extracted outside both repos:
`Assembly-CSharp 1.6.9676.17735`, SHA-256
`5cf1b5be399d5b1c9c56ca72c9d35b4ecf307feacf5859d04ac5a1aa5926356a`.
The reference repository was read at `9fcca42215c247135067694cd97c1ed7a4d86a7b` and is unchanged.
Decompiled members and headless experiments establish source behavior, not gameplay or mod-list
compatibility. This environment has no RimWorld Unity player.

Current merge-ready/open/unmerged banners were corrected in README, IMPLEMENTATION_PHASES,
PHYSICAL_LIFECYCLE, DECISIONS, RISKS, RUNTIME_TESTING, PR11_CORRECTION_VALIDATION and the
spike index/S21 record. PHYSICAL_LIFECYCLE's current “headless only” summary was also stale.
Historical `76b3ae1`, `db0f795` and pre-acceptance correction records retain their earlier statuses.
The PR11 acceptance source, artifact hash and owner results were preserved.

Two companion audits provide the game-source detail:

- [S26: first-generation cohesion](spikes/S26-team-cohesion.md).
- [S27: bounded promotion evidence](spikes/S27-progressive-concretization-evidence.md).

## 2. Seat and composition model

An **organization seat** is one unit of role capacity, not an anonymous person record. Its identity
is initially only `{organization, OperationalRole}` plus a count. An Episode selects a particular
unit into a durable slot with its source tier and role. Once a living person is concretized, the
pin is `{organization, CharacterId, opRole}` and its existing Pawn binding. For repeated identical
roles, the person's CharacterId distinguishes the pins; an ordinal is presentation, not identity.
An unavailable living pin still consumes its seat. It is never overwritten by an anonymous stand-in.

The current structures already provide `OrganizationProfile.tiers/committed/knownMembers`,
`KnownCharacter.org/opRole/firstEncounterTick/pawn`, `EpisodeMember.character/slot/tier/pawn`
and the CharacterStore. `EpisodeMember.seatRole` already exists and is persisted, but anonymous
projection does not yet use it; the real adapter refuses anonymous creation. There is **no
organization composition implementation**. There is no second person registry to introduce.

### 2.1 Owner-accepted Composition v1 direction for the coding prompt

Use the owner-accepted pure **Composition v1** from `{actor.seed, org.capacity,
ContractorProfile.specialties}`. `capacity` is the retained origin-form proxy: 3/7/14/32 for
Duo/Crew/Team/Company in `ContractorService.BuildRoster`. Unknown/nonstandard origin capacities
refuse with a diagnostic rather than invent a form class. The original ContractorForm is not
uniformly stored: world-generated newcomers discard their template. Current doctrine, skill,
equipment, fame, morale, headcount and current leadership rank are excluded from the origin recipe.

The accepted v1 recipe is explicit and becomes compatibility-sensitive when implemented:

1. Reserve one Leader weight entry. Solos use the existing Solo role derivation instead.
2. Take the distinct non-Leader/non-Unset roles from the existing
   `RoleDerivation.Candidates(original specialties)` mapping and add Rifleman as ordinary security.
   Unknown specialties retain the existing Specialist fallback.
3. If more than seven non-Leader roles result, choose seven by an ordinal, stable hash ordering of
   `{seed, capacity, "composition.v1", role name}`; resolve hash ties by the enum value. Store no roster.
4. Give Rifleman, Technician and Logistician weight 2; other selected roles weight 1. These are
   role-template weights, never capability bonuses. At most eight distinct entries result.
5. Derive aggregate quotas at living membership L: Leader 1, and largest-remainder allocation of
   L−1 over non-Leader weights, breaking equal remainders by stable enum order. Subtract pins
   **per role**: residual capacity is `max(0, quota(role) − pins(role))`. Pins exceeding a quota
   remain intact; apportion the actual residual anonymous count over those residual capacities,
   using largest remainder with each role capped by that residual capacity. This reduces other
   anonymous capacity rather than clipping any pin. A pinned Medic that fills its quota leaves
   zero anonymous Medic vacancy. Validate that totals equal L and no count is negative.

For five living members and specialties `escort, medical`, this yields Leader 1, Rifleman 2,
Medic 1, Heavy 1. For eight and `salvage, medical, logistics`, it yields Leader 1, Rifleman 2,
Technician 2, Medic 1, Logistician 2. “Security” and “Logistics” are narrative aliases for existing
Rifleman and Logistician; no new role enum is needed.

The origin weights are deterministic. Their **live apportionment** changes with actual headcount;
it does not describe fixed anonymous identities. Pins override proportional quotas, including when
casualties would otherwise apportion fewer seats of that role. First subtract pins, then distribute
the residual; never subtract a named member twice. Captured members keep their pin and cannot be
replaced. Recruitment permanently removes old NPC availability under O-20, while preserving org
provenance and personal history; it must be an explicit seat departure, not an inferred faction change.

Origin-era characters get roles once from the full origin cohort, ordered by CharacterId, including
historical/dead records when deriving old saves. Do not filter the cohort by current status or rank.
For this cohort, derive full-origin quotas at capacity and enumerate slots Leader first, then other
roles in enum order; assign successive CharacterIds to successive slots. Refuse malformed origins
with more origin people than capacity. Use the same assignment at instantiation and old-save lazy
first use. Legacy later-created unbound people lacking a role have no recoverable historic slot;
give them a fixed hash pick from that immutable slot list using CharacterId and a separate v1 salt,
then persist it once. Newly promoted physical people take their actual slot role in the plan.
Persisted non-Unset roles always win.
The role assignment must be idempotent and refuse conflicts, rather than rewrite a bound person.
**Owner-required implementation tests:** old/new-save fixtures must exercise initial role assignment,
leader/lieutenant standing, successor selection and reload. A persistent Medic who succeeds as
organizational leader retains the same Medic operational role and Pawn. No rank change may silently
rewrite physical role identity; explicit future role evolution is a separate design.
Organizational `CharacterRole` and operational `opRole` remain different axes: a succession does
not silently change a Medic Pawn into an operational Leader. A mission requiring an unavailable
qualified Leader shrinks/refuses; leadership/role evolution is a later explicit design.

### 2.2 Mission selection and accounting inputs

A mission carries an explicit required/optional role-count vector totaling **at most eight**.
Choose available pins of the requested role first, ordered by CharacterId; then allocate abstract
matching-role capacity and healthy source tiers in stable order. Subtract other active Episode
slots' recorded roles before allocation, and conservatively reduce the remaining anonymous role
quotas for wounded/abstractly committed units to the actually available healthy anonymous count;
do not infer particular anonymous identities or roles from an abstract operation. A pinned but wounded, held, deployed
or otherwise committed person is unavailable, and their reserved capacity is not an abstract vacancy.
If a required role cannot be filled, refuse; optional shortages shrink the group and report the
missing role. A wrong-role existing Pawn never fills a vacancy for convenience.

For the first slice, permit **one active group-materialization Episode per organization**, including
Planned/Open/Closed-with-RELEASE-pending ownership; separate already-named custody Episodes do not
create new anonymous seats. This prevents concurrent placements spending the same residual roles,
headcount or discretionary concretization budget. If later permitting concurrent detachments,
their latched P0 slots must reserve both role and discretionary cap capacity, not only anonymous stock.

Use **actual living current organization membership at placement**, not ContractorForm or the
current `ContractorService.Headcount` shortcut. That shortcut adds `knownMembers.Count`, including
dead/defected/history records, and cannot implement the new thresholds truthfully. The new pure
count is anonymous healthy + wounded + committed, plus distinct living current members, with
pending slots counted once through their commitment. Held current members count and pin capacity;
dead/lost/retired/former-member/defected records do not. Missing current-affiliation people count
conservatively until positive permanent loss; captivity/missing alone never vacates their pin.
Malformed/unresolvable membership or inconsistent counts refuse placement.
Existing named people remain named at every size, including when a crew grows.

Abstract casualties alter only residual role apportionment. They do not identify which unobserved
role died. A per-role vacancy ledger (O-9), role-aware abstract casualty simulation and organization
role evolution are deferred. Future UI must not claim a particular anonymous Medic survived an
abstract loss when the abstract layer never knew that.

### 2.3 Persistence and migration

**Composition itself needs no persisted field or migration** if v1 remains a pure immutable recipe.
This is the owner-accepted simplification of the earlier persisted-template design; no schema is
implemented in this audit. Reserve future explicit composition-version storage for a later v2/evolution feature; do not
change v1 outputs under the same identifier. Pins use existing KnownCharacters and roles, not a seat registry.

The complete 3.2B slice nevertheless needs new **Episode-local truth**, at least a successful
player-visible placement tick/eligibility latch (plus the sampled size/cap
decision if it cannot be derived after departure). P0 cannot be reconstructed by looking at a map
after a Pawn already left. Record eligibility at successful placement; apply persistent person
creation in the reconciliation commit. Do not call “first placement concretizes” an immediate
character-store mutation outside that transaction.

These fields can default absent to **unknown/ineligible**, with old anonymous Episodes quarantined
or left unsupported rather than invented into promotable histories. `SAVE_AND_MIGRATION § 4.3`
allows additive fields with correct defaults without a bump. If coding requires backfilling old
records or changing commitment meaning, a versioned migration is required. Therefore **composition
does not force a bump; the final whole-slice schema must still be checked**. This audit adds no field.

## 3. O-2 size and cap decisions — ACCEPTED

| Actual living membership at placement | Presence policy |
|---|---|
| Solo | Always the existing named person, bound once. |
| 2–6 | Every successfully player-visible placed seat is eligible to concretize. |
| 7–12 | Role-defining seats eligible; ordinary Rifleman requires S1–S4. |
| 13+ | Anonymous members require S1–S4; ordinary presence produces zero people. |

Use **6 and 12 as named policy constants**, not user-facing taxonomy or gameplay tuning controls
in this slice. Role-defining means the derived template's Leader, Marksman, Heavy, Breacher, Medic,
Scout, Engineer, Technician, Logistician, Negotiator and Specialist seats; already
named Riflemen are always reused. Respect a normal **six living seat-concretization** budget.
If a new role-defining seat cannot safely receive a pin within that discretionary budget, shrink
or refuse its placement; do not repeatedly show a remembered specialist as different humans.
Changing size later does not undo an existing pin. Eligibility is latched from the placement,
not recomputed from a smaller post-casualty headcount.

**Important correction:** `OrganizationProfile.MaxKnownMembers == 6` is the current **total**, with
leader and lieutenants included (`ContractorComponents.cs:539–546`, BuildRoster:431–443).
The old design's “1 leader + 2 lieutenants + 6 members” does not match the code.

**Owner accepted B: the cap limits discretionary living seat creation/concretization, never durable
identity storage**. Every accepted strong-evidence promotion may exceed it; held promotions must exceed it
when necessary. Keep such people in the existing CharacterStore and org membership list, preserving
the same Pawn. A provenance-only record outside knownMembers would be missed by current strength,
checkout and availability loops. Audit those consumers to count current eligible membership,
not the list's length, and never truncate overflow on load. Historical records do not consume
living discretionary seat capacity. Strong-evidence promotions are identity obligations, not an
excuse to discard a memorable person. There is no hard global CharacterStore cap.

## 4. S26 findings and accepted cohesion policy

The [S26 record](spikes/S26-team-cohesion.md) is **PARTIAL — SOURCE AUDIT**. Important core social
thoughts support despawned Pawns, and same-faction Pawns can know one another without spawning.
`OpinionOf` can therefore reveal real initial friction, but its result excludes map-dependent
and some modded context. Compatibility also affects insult selection independently of opinion.
Reading mutual opinion populates transient social-thought caches; rejection can leave references
in a suspended teammate's cache. A pre-spawn zero is not a cohesion guarantee.

`CanGeneratePawnRelations = false` disables vanilla generated relations, not later social history.
Eligible `FixedIdeo` chooses ideology during generation, not a license to rewrite existing members.
ProhibitedTraits is not authoritative when kinds force traits. Returned candidates must be verified.

The owner accepted outcome **C: BEST-EFFORT construction only** for initial 3.2B: force new first-generation
candidates, relations off, shared existing ideology where valid, verify applicability and returned
ideology within the existing bounded generation process. Do not add a mutual-opinion rejection
floor, trait blacklist, fabricated relations or candidate-to-retained-teammate screening yet.
Any optional future opinion/trait screen needs a disposable owner runtime probe of rejection rate
and cache residue. Hostility frequency is **unmeasured**, not zero. Physical history owns every
later relation, opinion, thought, memory, trait, ideology change and social fight.

## 5. S27 findings and final promotion matrix

The [S27 record](spikes/S27-progressive-concretization-evidence.md) is **PARTIAL — SOURCE AUDIT /
ISOLATED HEADLESS EXPERIMENT**. Public narrowed BattleLog access is sufficient, but generic
same-entry co-occurrence is too broad: ranged impact can include shooter, actual victim and
original target; a turret can concern two Pawns who never fought each other. Event/ability/item
entries can be benign. Use source-audited exact entry types and endpoint qualification.

| Signal | Required positive evidence | Result / limits |
|---|---|---|
| P0 | Successful player-visible placement latch, size 2–6 or permitted 7–12 role-defining seat | Seat-policy concretization only. Never promotes company rank-and-file. |
| S1 | Validated material custody/outcome: player arrest/capture, enslavement, recruitment, kidnapping/other durable holder; actual rescue outcome if separately supported | Always create/retain a record for the same Pawn, even above cap. Ordinary caravan/transport membership or spawn state alone is insufficient. Rescue implementation remains stopped. |
| S2 | A deliberate individual subject in planned Network publication, identifying this exact Pawn/person | Promote atomically before publishing the name. Aggregate casualty text, pawn-generated name, generic vanilla letters do not qualify. |
| S3 | Allowed combat entry during the Episode, with candidate and player-side Pawn as confirmed combat endpoints | Promote if found within the budget. Misses/throws/truncation remain anonymous unless another strong signal exists. |
| S4a | A non-null direct relation from candidate to a currently player-faction/player-hosted Pawn, found within the direct-relation budget | Promote; inverse/implied relationships not cheaply proved are conservative misses. |
| S4b | `PawnUtility.EverBeenColonistOrTameAnimal(candidate)` | Promote. Public record derives from positive colonist/colony-animal time; a newly recruited Pawn still qualifies through S1 even before this record advances. |
| Supporting | Narrowed candidate + player-side PlayLog interaction | Diagnostic support only. Never promotes alone and never relaxes another strong signal's conditions. |
| Rejected | Mere company presence, `AnyEntryConcerns`, raw GC reason, arbitrary tales/logs/quest reservation, third-party combat, internal chatter | Remain anonymous. Do not force-discard vanilla-referenced Pawns. |

Player-side means currently `Faction == Faction.OfPlayer` or `HostFaction == Faction.OfPlayer`,
evaluated at reconciliation on the exact referenced Pawn. This is a conservative current-side
proxy, not proof of historical allegiance or hostility. No all-world-pawn scan or permanent watcher.

### 5.1 Combat qualification and bounded cost

Allow exact vanilla `BattleLogEntry_MeleeCombat`, `BattleLogEntry_RangedFire`,
`BattleLogEntry_ExplosionImpact`: GetConcerns must contain exactly two non-null distinct Pawn
endpoints, candidate and player-side. For exact `BattleLogEntry_RangedImpact`, require the three
ordered Pawn concerns to be `{initiator, actual recipient, original target}` with recipient and
original target **the same Pawn**, and initiator distinct. Reject turret/null initiator, three-party
impact and unknown/custom entry types. Do not infer evidence from a broad entry type or text.

Scan no more than **32 battles × 128 entries per battle**, maximum **4,096 entries per candidate**,
at most **8 candidates**: **32,768 bounded entry checks** per reconciliation evidence pass. No
per-tick scan. Cap the direct-relation read to **128 direct relation entries per candidate** and
skip indirect graph traversal. Do not build an unbounded filtered log copy before imposing the cap.
Failures collecting a material custody observation remain fail-closed; failure of optional logs
simply yields no log evidence.

Filter using `LogEntry.Timestamp >= episodeStartGameTick + TickManager.gameStartAbsTick` in a
wide integer: logs use **absolute** ticks, Episodes game ticks. Reject invalid/unknown start
timestamps. Battles and entries are normally newest-first, but updated older battles need not move
to the front. Do not early-break on timestamp or assume chronology for modded history.

Vanilla's 20-battle retention rule is conditional, not a hard bound, and one Battle's Entries can
grow. Logs are also pruned and Pawn references may become unresolved on load. The bound can miss
older entries, a busy battle's entries beyond 128, or active battles outside the first 32. A miss
means **remain anonymous**, never “promote just in case.” Save/load and mod-list checks remain
required runtime acceptance; no persistent observer/log history is added to compensate.

The isolated actual-assembly probe made **15 assertions** using real GetConcerns implementations,
including the turret/three-party false positives and bounded misses. With synthetic **640,000
entries**, eight candidates and 100 scans, measured median **2.599 ms**, p95 **2.831 ms**, max
**3.039 ms** in this cloud Mono environment. These are microbenchmark observations, not game TPS,
Pawn-generation results or a guarantee on a heavy mod list. See S27 for reproducible source/steps.

### 5.2 PlayLog and vanilla critical reasons

**PlayLog remains supporting only; initial 3.2B may omit its scan entirely.** The interaction Def
is protected and no stable public severity classifier proves consequential interaction. Broad
GetConcerns matches cannot distinguish chatter. `WorldPawnGC.GetCriticalPawnReason` is private;
public immediate GC data accumulation scans the world, so neither is a production evidence API.
Use only the public narrow S4 predicates above. Vanilla may keep an anonymous Pawn because of logs,
tales, indirect relationships or other stakes without the Network promoting it. Release it safely
to vanilla; do not erase those references or assume “anonymous” means disposable.

## 6. Atomic promotion and the pending-group gap

The existing terminal transaction is reusable **with extensions**, not already a promotion API.
`PhysicalLifecycleService.ReconcileCore` waits when any member is Pending (:719);
`ReconciliationPlanner.Validate` requires all members terminal (:574–586); and
`ReconciliationApplier.Commit` writes whole-Episode `consequencesApplied` last (:58).
Anonymous held outcomes currently quarantine as UnsupportedCustody. Existing `CommitOpKind.Promotion`
supports abstract succession, not physical slot promotion. Calling that applier on one captured member while
the other members remain spawned would falsely finish the entire Episode.

### 6.1 Accepted terminal-batch prerequisite

Use owner-approved **terminal-batch promotion** for 3.2B, with a bounded **active-Episode reservation** covering
anonymous slot Pawns until that batch commits and releases. Extend the existing runtime reservation
mechanism from already-durable Episode PawnRefs; do not create anonymous KnownCharacters just for
reservation, a persistent company roster, a second person registry, or a per-Pawn watcher. Derive
the reservation index before the first post-load world tick using the existing two-stage binding
pattern. It must cover spawned/held/exiting slot Pawns across the vanilla exit window.

This prerequisite is necessary even for a conservative implementation: current M1 indexes named
KnownCharacters only, so a still-anonymous Pawn can become Free/redressed/discarded while peers
remain Pending. Distinguish an Episode reservation from permanent named M1 retention in the
observer; an owned anonymous return must not turn into WorldOther/Pending forever because the
reservation quest now holds it. Neither KnownCharacter creation nor permanent retention happens
merely because the temporary reservation exists.

An anonymous held Pawn remains under its durable Episode slot until all peers are terminal. Then
the same atomic commit installs its KnownCharacter, binding, role, provenance, custody and headcount
effects **before RELEASE**. Rebuild/index held people immediately after commit; the custody watch
may act only after RELEASE clears the episode link, as the existing watch already requires
(`CustodyWatchRun`:1211). “Immediate custody tracking” thus means after successful terminal commit
and release, not a promise of promotion on the arrest frame while the rest of the group is active.
Future scenario D must make its ordinary peers return before testing this completion.

**No early per-member identity/custody commit is authorized for 3.2B.** Do not promote on the arrest
frame while peers remain Pending or set the whole-Episode completion flag from one member's outcome.
A future requirement for independent early promotion needs a separate architecture decision;
it is outside this accepted path. The full mixed-fate resolver still belongs to 3.2C.

### 6.2 Terminal promotion plan and touched set

OBSERVE → DECIDE → PLAN → VALIDATE → atomic COMMIT → RELEASE → FOLLOW-UP → PUBLISH stays intact.
Evidence collection may call the physical adapter **before** the pure plan/commit. The plan previews
CharacterIds and every new record, copies the slot's exact PawnRef identity and role, assigns org
and episode link, samples its real name, writes firstEncounterTick from proven evidence, transfers
one committed anonymous unit, applies the observed fate/holder, and prepares publication data.
Select RELEASE actions from the planned **named** result, not the slot's old anonymous classification.
No Pawn regeneration, rename, trait/ideo change or second binding is allowed.

| Durable target | Required snapshot / rollback |
|---|---|
| Episode + nested members/PawnRefs | Slot character/role/evidence, observed outcome, progress flags and durable publication outbox. |
| Existing affected KnownCharacters | Custody/status/episode links and succession effects; include every touched existing person. |
| Actor header and components | knownMembers/leader/lieutenants, tiers/committed/recovery, morale/strength cache, commitments/spatial and any ending/succession effects. |
| New CharacterStore membership and index | Append each new person; on throw truncate to prior count and restore index, including multiple promotions. No whole-store snapshot. |
| ID allocator | Preview without advancing in planning; allocate only in guarded commit; restore on any fault. |
| Linked operation, if present | Existing physical resolution/follow-up/outbox markers; no contract mutation unless declared and captured. |
| Publication data | Episode outbox captured with Episode; standalone parity outbox captured explicitly. Cursor and completion markers remain durable. |

`DurableSnapshot` follows Network-owned nested objects and remembers engine objects by reference;
it cannot roll back vanilla mutation. Therefore the atomic block contains Network assignments only.
The current target's single `promoted` result must become a bounded collection for multiple people.
New records and their nested state must be discarded/restored on rollback; no dangling plan binding
may survive a retry. Validate unique new IDs, one Pawn per living person, same slot Pawn pointer/id,
exact role, capacity exception, custody observation, and nonnegative conservative counts before commit.
Validate the existing **16-publication** outbox limit too. Do not add one promotion event plus one
held event per person and overflow it for eight members; promotion can be represented by the
existing individually identified custody/event data or bounded aggregate publication. Naming data
must still reference the new ID exactly. Never deny a held record merely to fit an event outbox.
Write whole-Episode `consequencesApplied` last only for an all-terminal plan.

Episode membership index rebuild, registry/held-index refresh, tags, normalization, vanilla calls, watch scheduling and publication
are post-commit idempotent stages. Do not let an observer or event bus see a half-created character.
The active slot reservation bridges a crash/load or interrupted RELEASE until named M1 coverage
is verified, then relinquishes temporary ownership exactly once. Fail-closed/quarantined Episodes
keep their reservation and diagnostic; they do not destroy held people to regain budget.

## 7. Headcount conservation contract

Count a human once, as an anonymous aggregate unit **or** a named record. Let `A` be healthy
anonymous stock, `W` wounded anonymous stock, `E` checked-out anonymous units, and `N` distinct
living current named organization members. Organization living membership is `A + W + E + N`;
faction/custody affects availability, not a second identity count. Missing/lost membership needs
explicit outcome semantics rather than an assumption of death. Retain departed named people in
history/provenance while excluding them from current NPC membership.

| Transition for one human | Atomic delta |
|---|---|
| Anonymous checkout | A −1, E +1. |
| Named checkout | No stock delta; its availability is closed by the Episode link. |
| Anonymous normal return | E −1, A +1 (or W +1 on positively observed wound). |
| Anonymous death/permanent departure without a new record | E −1; one death/departure in outcome accounting. |
| Anonymous promotion returning to org | E −1, N +1; do **not** also add A/W or count another casualty. |
| Anonymous held promotion, still org member | E −1, N +1; captured/held state closes availability and keeps seat pin. |
| Anonymous recruitment/permanent departure promotion | E −1; create historical/current player-side person; N does not increase for the old NPC org. One departure. |
| Named death/departure | N −1 once; do not subtract anonymous tiers/committed again. |
| Never placed | Restore only actually checked-out anonymous stock; named checkout has no stock delta. No promotion from unproven placement. |

For a returned promoted wounded member, wound truth belongs to the KnownCharacter/Pawn, not an
anonymous recovery bucket. A dead promoted/named person may retain history/binding, but is not a
living seat. Aggregate morale/events count observed persons once regardless of promotion, never
once as anonymous and again as named. Retries, save/load and interrupted RELEASE must preserve
the same deltas. These identities are the future conservation tests, not code added here.

Headcount conservation does not imply unchanged abstract strength: `ContractorService.Strength`
uses anonymous tier weights 1/2/3.5 and ordinary named weight 3 (leader 4). The owner accepted retaining that
existing named-person abstraction in this slice, explicitly measuring the resulting capability
change rather than promising neutral promotion. Preserving exact source-tier strength would require
another provenance policy/schema decision; it is not established by the current person model.

## 8. Retention opportunity model

The owner accepted **150 as a soft performance target / warning region**, never an identity ceiling.
Identity continuity wins; do not replace a living encountered person, regenerate a lookalike, discard
a held person or return an encountered seat to abstraction to reduce retained count. A simple
deterministic model uses the actual default generator's form weights (`CastGenerator:97`): 40
Solo/Specialist, 10 Duo, 20 Crew, 15 Team, 15 Company. Representative living sizes are 1, 2,
ten 4-person + ten 5-person crews, five each of 8/10/12-person teams, and 20-person companies.
Eligible persistent seats: all small members, five per team (three originally named + two distinctive),
four already-known per company. This is an **assumption model**, not sampled live worlds or gameplay tuning.

Visits use actor `(visitIndex × 37) mod 100`. Non-company visits rotate up to three eligible seats;
companies rotate one already-known representative and expose two ordinary anonymous members.
The model retains every encountered eligible person; no deaths, retirements, departures or new
recruits reduce/increase the cast. Hypothetical strong signals occur every 200 anonymous exposures
(rare case) or 50 (stress case); these rates are assumptions, not measured event frequencies.
Promotion consumes that company's existing anonymous living stock; no extra human is invented.

| Total visits | Distinct actors seen | Presence-only retained | Every 200 exposures | Every 50 exposures |
|---|---|---|---|---|
| 25 | 25 | 44 | 44 | 44 |
| 100 | 100 | 180 | 180 | 180 |
| 300 | 100 | 270 | 270 | 271 |
| 1,000 | 100 | 285 | 286 | 291 |
| 5,000 | 100 | 285 | 292 | 315 |
| 10,000 | 100 | 285 | 300 | 343 |

Presence-only saturation is `40 + 20 + 90 + 75 + 60 = 285`; **company anonymous presence creates
zero additional records even after 10,000 visits**. In the rare case, 3,000 anonymous exposures
produce 15 promotions; stress yields 58 with uneven company stock exhaustion. The arithmetic is
reproducible from the preceding rules. These counts measure retained identity obligations, including
held Pawns; they do not estimate exact save bytes or suspended-versus-spawned CPU cost.

For a lightly encountered cast, 150 remains a useful warning target. Broad repeated engagement
can exceed it without a giant anonymous roster. It is not valid to claim “3.2B honors a hard 150
budget.” Keep bounded Episode size, no company presence promotion, no background Pawn generation,
and one budgeted diagnostic when exceeding the soft target. Never clone/replace an encountered
living person to reduce it. In-game TPS/save-size soak at 150/300+ retained Pawns is acceptance
work for the implementation, not evidence supplied by this model. The accepted future runtime plan
requires performance/save-size/save-load observation around **150 and 300 retained Pawns**. Solve
unacceptable measured costs in a later lifecycle/performance pass; do not preemptively break identity.
Infinite strong-evidence stories,
turnover or cast growth can grow identity history; no finite hard cap is compatible with the held
identity invariant without a separate owner-approved lifecycle policy.

## 9. Minimum future runtime and headless acceptance matrix

These scenarios are **proposed only**, with tentative 026+ IDs subject to the next PR's inventory.
They use the session-armed disposable physical tier. Its placement evidence must be explicitly
injected by the test fixture; merely being a dev map does not qualify as player-visible P0.
Also run a separately approved home/player-occupied-map verification of the actual visibility rule.

| Scenario | Required proof |
|---|---|
| A — five-person first visit | `escort, medical` immutable fixture with ONLY the leader initially known → Leader/Medic/Rifleman. Preserve leader, concretize exactly two proven visible seats, bind once, no unrelated people/headcount changes. Do not let a generated lieutenant already occupy the tested Rifleman pin. |
| B — second visit | Required Medic/Rifleman select same CharacterIds and Pawn references/ThingIDs; truthful ordinary stored aging, unchanged roles, no substitutions for held/unavailable pins. |
| C — company presence | Ordinary detachment visits repeatedly; zero new KnownCharacters, role-correct ephemeral slots, commitments restore exactly once. Temporary reservation released safely; no forced destruction of vanilla-referenced people. |
| D — one arrested company member | Promote exactly one same Pawn despite full discretionary cap; ordinary peers return before terminal batch commit. Binding/role/provenance/custody/headcount atomic; watch indexes it after commit and acts after RELEASE. Verify safe waiting/reservation while peers were Pending. |
| E — SAVE/LOAD | Checkpoint an active group including an anonymous held slot; after terminal promotion checkpoint before RELEASE and after RELEASE. Load preserves bindings, pins, reservations before first world tick, counts and publication cursor; same named people on a later visit. |
| S27 evidence negatives/positives | Real melee/ranged endpoints vs third parties, turret and three-party impact, chatter, internal logs, old timestamps, pruning/bounds, unresolved references; no conservative miss promotes. Measure within budget on owner mod list. |
| S26 construction | Relations generation off; applicable FixedIdeo verified for newly generated eligible members; existing Pawns' traits/relations/memories/ideology preserved across visits. No claim of guaranteed friendly opinions. |
| Organization role identity | Old/new-save fixtures prove deterministic initialization and migration, then leader/lieutenant/successor changes plus reload preserve each person's operational role and Pawn. A Medic successor remains a Medic. |
| Retention performance | Observe runtime cost, save size and clean save/load at approximately 150 and 300 retained Pawns; preserve every encountered living/held identity regardless of the warning target. |

Headless coding acceptance needs deterministic composition/role assignment across old/new actors
and load, boundary sizes 6/7/12/13, unavailable pins, source-tier/role constraints, cap-full held
overflow, conservation, multiple promotions and ID allocation rollback at **every commit step**,
retry after all post-commit interruptions and whole-Episode completion ordering. Existing authority,
save5 compatibility, no-Harmony, S11 and prior custody regressions remain mandatory checks.
Do not turn a source audit or this microbenchmark into an owner runtime PASS.

## 10. Exact slice boundaries and remaining risks

**3.2B:** bounded group materialization; immutable composition/role assignment; matching-role
mission selection; anonymous Episode slots; player-visible eligibility latch; same-Pawn seat
concretization and strong-evidence promotion; cap overflow for obligated identities; the minimal
terminal-batch promotion + temporary reservation prerequisite; conservation and rollback foundation;
save/load support and the scenarios above. Use existing Lord/Duty behavior. No player-facing group
content, new organization taxonomy or tactical AI is necessary for this developer-triggered slice.

**3.2C:** full mixed reconciliation of one group with return, death, capture, recruitment, ephemeral
return and combat promotion together; its casualty/morale/succession/operation matrix and partial
group extraction policy. D's one-held-plus-ordinary-return path and batch-safe reservation are the
small unavoidable 3.2B prerequisite; they do not authorize the entire matrix. Do not claim other
mixed combinations supported until their phase proves them. Unknown combinations fail closed with
all Pawn references/reservations preserved. Independent early identity/custody promotion while peers
remain active is **excluded from 3.2B** and needs a separate future architecture decision.

**S11 remains FAIL; rescue STOPPED.** No rescue site/world object/custom GenStep/retained site holder
or Harmony. Composition could help a future proven rescue alternative but does not prove a holder.
**3.3 and Phase 4 remain unimplemented.** O-20 stays locked: recruited person permanently exits old
NPC availability and later participates only as a real player Pawn through PlayerProxy +
ContractorProfile + ColonyReader, never a former-contractor abstract simulator.

**R-50 remains OPEN.** Ordinary new groups and ordinary Stored-pawn repeat visits can proceed using
the existing validated aging machinery. The new Episode reservation must not assume that reservation
alone establishes an exact aging suspension tick. Long-held release/rematerialization, rescue and
uncertain suspension intervals remain gated/fail-closed; do not replay unproven held biological time.
A first held promotion can persist custody without immediately rematerializing that person.
S21 stays PARTIAL for caravan/transport/other-faction-prisoner runtime observations. Those paths
need owner acceptance before a 3.2B PR claims wider runtime coverage.

Newly exposed risks, to track in the implementation:

- Six-total cap conflicts with older design prose; provenance-only overflow would silently miss membership consumers.
- Terminal-only reconciliation cannot safely do early per-member promotion; anonymous slots need exit/load reservation coverage until commit.
- Placement visibility/size evidence disappears after departure unless latched in durable Episode truth.
- Same-entry BattleLog matching can promote unrelated turret victims/original targets; absolute/game tick mismatch can misclassify old combat.
- Conditional log pruning and bounded windows produce deliberate false negatives; optional evidence must fail conservative.
- Mutual opinion reads create transient caches and do not model all friction; returned generation constraints need verification.
- A fully encountered default-sized cast can retain roughly 285+ people, requiring owner runtime performance evidence beyond the soft 150 target.
- New temporary reservations change WorldFree classification and load ordering; failing to distinguish them could strand Episodes indefinitely.

## 11. Changes and validation of the original audit (7d5e4eb)

Changed existing documentation: README.md; docs/DECISIONS.md; IMPLEMENTATION_PHASES.md;
PHYSICAL_LIFECYCLE.md; PR11_CORRECTION_VALIDATION.md; RISKS.md; RUNTIME_TESTING.md;
spikes/README.md; spikes/S21-observation-completeness.md. New documents: this report, S26 and S27.
The [S27 exploratory probe](spikes/exploratory/S27/README.md) is a standalone audit executable
with no reference to TheNetwork and no inclusion in the mod/test builds. Its test-only Harmony
stubs replace native presentation entry points; it does not ship in production.

Validation: the merged-base and changed-file scope were inspected, historical correction text was
preserved byte-for-byte from its historical section onward, **668 local Markdown links** checked,
diff whitespace checked, and the accepted DLL hash compared to merged main. The S27 probe was
rebuilt from its reviewed repository path to an external directory: **0 warnings / 0 errors,
15 assertions passed**; repeat median **2.624 ms**, p95 **3.063 ms**, max **3.337 ms**. The existing
compiled documentation gate passed without rebuilding (**1 test / 69 checks / 0 failures**).
That old gate reads preserved implementation milestones; the current merged banners were checked
separately. The isolated S27 checks/microbenchmark are the only new game-assembly experiment;
the external retention arithmetic model is described in § 8.
No production build, full headless suite, runtime scenario or in-game acceptance was run for these
documentation changes. Existing PR11 full-suite/owner results above remain historical evidence.

## 12. Owner decisions — ACCEPTED (ADR-057)

The owner accepted the following on 2026-10-06, reviewing audit HEAD `7d5e4eb`:

1. **Cap semantics ACCEPTED:** six living discretionary named/concretized seats total, including
   leader/lieutenants, with all accepted strong promotions retained in the existing CharacterStore/
   organization membership model even above six. Held identity always overrides the cap. No second
   registry or provenance-only shadow membership.
2. **Timing/prerequisite ACCEPTED:** whole-Episode terminal-batch promotion plus temporary anonymous
   active-Episode reservation derived from durable PawnRefs, safe before the first post-load world
   tick. No dummy person for reservation and no independent early commit. Named M1 takes over where
   applicable; custody watch proceeds after RELEASE under the existing ownership rule.
3. **Retention policy ACCEPTED:** approximately 150 is a soft target/warning region. Identity takes
   priority; roughly 285+ retained people from repeated default-cast engagement is architecturally
   acceptable. Implementation acceptance observes performance/save-load around 150 and 300.

The owner also accepted the 6/12 actual-living-membership thresholds as initial policy constants,
pure immutable Composition v1 direction, BEST-EFFORT construction-only cohesion, conservative S1–S4
promotion rules, supporting-only/optional PlayLog, and the 3.2B/3.2C boundary. Composition v1 must
freeze its implemented algorithm; a future semantic change requires explicit v2/migration/design
approval. Operational role initialization/migration and succession/reload safety receive the
additional old/new-save tests in § 9. These decisions are recorded in
[ADR-057](DECISIONS.md#adr-057--phase-32b-readiness-owner-decisions).

**No technical blocker remains for this accepted path.** Its group generation, reservation,
promotion and conservation behavior still must be implemented and validated. S26 remains
**PARTIAL — source audit**; S27 remains **PARTIAL — source audit / isolated headless experiment**.
Future group runtime scenarios have not passed. S11 remains FAIL / rescue STOPPED, R-50 OPEN and
S21 PARTIAL where runtime coverage is incomplete; Phase 3.2B, 3.2C, 3.3 and Phase 4 remain
**NOT IMPLEMENTED**. Save format is still 5 and production has no Harmony.

Historical audit verdict at `7d5e4eb`: “READY WITH OWNER DECISIONS REQUIRED.” This owner-decision
follow-up supersedes that verdict without changing the audit's source/headless evidence.

# READY FOR 3.2B IMPLEMENTATION
