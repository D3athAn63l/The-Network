# S27 — Progressive concretization evidence audit

> **Historical readiness/source record; verdict remains PARTIAL.** The accepted policy now has a Phase 3.2B production implementation and headless tests ([as built](../PHYSICAL_LIFECYCLE.md#appendix-m-phase-32b-as-built-groups-and-progressive-concretization), [validation](../PHASE32B_VALIDATION.md)). The audit below preserves its original source/experiment evidence; it does not establish owner runtime PASS. The later [current Create/Reset and 026–032 owner runtime acceptance](../PHASE32B_VALIDATION.md#pr-13-final-owner-runtime-acceptance), including 030/032 loads, does not close S27's broader combat endpoint, direct-relation and mod-timing probes. Independent final audit PASSED.

**Verdict: PARTIAL — source audit and isolated headless experiment.** Source-qualified bounded evidence is feasible; actual game, save/load and mod compatibility remain future owner runtime gates. This report does not implement promotion, groups, runtime scenarios or Harmony in the mod.

The owner accepted the conservative S1–S4 evidence matrix and supporting-only, optional PlayLog policy below. Promotion remains part of whole-Episode terminal reconciliation: an eligible anonymous Pawn stays temporarily Episode-owned/reserved while other members remain Pending, then the same Pawn is promoted through PLAN → VALIDATE → atomic COMMIT before RELEASE. No early per-member identity/custody commit is authorized for 3.2B.

Audit base: The Network `main` merge `a472766ff26a8af975d2a15c18d690cc13f8aa13`. Actual RimWorld 1.6 `Assembly-CSharp.dll` identity `1.6.9676.17735`, SHA-256 `5cf1b5be399d5b1c9c56ca72c9d35b4ecf307feacf5859d04ac5a1aa5926356a`. Selected loose reference source lacks these classes; findings below are ILSpy 9.1 decompilation of that supplied binary. No 1.4/1.5 inference. Decompiled files are outside both repositories at `/workspace/.onboarding/phase32b-s27/`.

## Public API and source evidence

| Member | Actual signature / visibility | Source evidence |
| --- | --- | --- |
| Battle history | `public List<Battle> BattleLog.Battles` | `Verse.BattleLog.cs:14` |
| Battle entries | `public List<LogEntry> Battle.Entries` | `Verse.Battle.cs:43` |
| Entry concerns | `public abstract IEnumerable<Thing> LogEntry.GetConcerns()`; elements are Things, not necessarily Pawns | `Verse.LogEntry.cs:114` |
| Entry time | public `Tick`, `Timestamp`, both backed by absolute `ticksAbs` | `Verse.LogEntry.cs:39,43` |
| Play history | `public List<LogEntry> PlayLog.AllEntries` | `Verse.PlayLog.cs:12` |
| Interaction semantics | `PlayLogEntry_Interaction.intDef`, `initiator`, `recipient` are **protected**. Public `initiatorFaction` and `initiatorIdeo` do not reveal meaningful interaction kind | `Verse.PlayLogEntry_Interaction.cs:11–21` |
| Direct relations | `public List<DirectPawnRelation> Pawn_RelationsTracker.DirectRelations` | `RimWorld.Pawn_RelationsTracker.cs:63` |
| Broad related-pawn enumeration | public `RelatedPawns` invokes `PotentiallyRelatedPawns`, traversing direct, inverse and virtual relationship graphs | `RimWorld.Pawn_RelationsTracker.cs:194–280` |
| Former colonist/animal | `public static bool PawnUtility.EverBeenColonistOrTameAnimal(Pawn)` reads `pawn.records.GetAsInt(RecordDefOf.TimeAsColonistOrColonyAnimal) > 0` | `RimWorld.PawnUtility.cs:1197–1200` |
| Vanilla GC critical reason | `WorldPawnGC.GetCriticalPawnReason(Pawn)` is **private**, contrary to older lifecycle API prose | `RimWorld.Planet.WorldPawnGC.cs:174` |
| Public GC diagnostic | `public Dictionary<Pawn,string> AccumulatePawnGCDataImmediate()` performs full world-pawn accumulation and relationship/memory expansion | `RimWorld.Planet.WorldPawnGC.cs:79–121`; unsuitable for the promotion collector |
| Current player hosting | `public Faction Pawn.HostFaction => guest?.HostFaction` | prior independently decompiled `Verse.Pawn.cs:578` |

Use the direct public record/relationship/ownership APIs. Do not use reflection, translated strings, GC reason strings, a full GC diagnostic pass or broad `AnyEntryConcerns` to decide promotion.

## A same-entry query is expressible, but needs a semantic allowlist

Checking a candidate plus a player-side Pawn in **one entry** is different from checking `Battle.Concerns` or two entries in the same battle. A merged battle can contain several factions; battle-level co-membership is a false positive.

Even generic same-entry co-occurrence needs narrowing:

- `BattleLogEntry_MeleeCombat.GetConcerns()` yields nonnull initiator then recipient Pawns (`:107–117`). They are the combat endpoints.
- `BattleLogEntry_RangedFire.GetConcerns()` yields nonnull firing Pawn then target Pawn (`:88–98`). A missed shot still represents directed combat contact.
- `BattleLogEntry_ExplosionImpact.GetConcerns()` yields nonnull initiator then recipient Pawns (`:93–103`). A non-Pawn explosion initiator supplies no attacking Pawn; require two Pawn endpoints.
- `BattleLogEntry_RangedImpact.GetConcerns()` yields nonnull **initiator**, **actual recipient**, then **original target** (`:112–126`). Three different Pawns can co-occur although the candidate and player never fought one another. Worse, a turret/non-Pawn initiator may leave exactly two distinct Pawn concerns: candidate actual recipient plus player original target. Therefore **two distinct concerns alone is not sufficient for RangedImpact**.
- `BattleLogEntry_Event`, `AbilityUsed` and `ItemUsed` can involve beneficial or neutral actions. `DamageTaken` and `StateTransition` do not expose enough public causal semantics for a generic strong-evidence classification. Omit them initially.

**Owner-approved exact-type allowlist** (not `is` or an unknown mod subclass):

1. MeleeCombat, RangedFire, ExplosionImpact: exactly two nonnull, distinct Pawn concerns, exactly the candidate and another Pawn satisfying the current player-side predicate.
2. Optional RangedImpact safe case: the ordered concerns sequence contains **exactly three nonnull Pawn items**, the first differs from the second, and `ReferenceEquals(items[1],items[2])`. For this exact vanilla type, that proves a Pawn initiator and an actual recipient equal to the original target. These two distinct endpoints must be candidate plus player-side. All other RangedImpact patterns remain insufficient.
3. Exclude all unknown entry types and subclassed entry types until independently audited.

Read at most four yielded concerns, rejecting a fourth, null/non-Pawn or unsupported shape. Do not build an unbounded list from `GetConcerns()`.

The rule establishes combat contact with the player's side; it does **not** establish hostility, injury severity or a hostile historical faction relationship. Friendly fire and social-fight punches are meaningful contact under this accepted policy. Current faction checks cannot reconstruct faction/host state when an old entry was created. Any future requirement for *hostile combat only* needs a separate policy/API decision; current log public members do not supply that history.

A guarded current player-side predicate is: `other != candidate && playerFaction != null && (other.Faction == playerFaction || other.HostFaction == playerFaction)`, with missing/invalid prerequisites yielding no evidence. Compare actual faction instances; do not use labels or Network organization IDs. A player-hosted prisoner/lodger is included as requested. Do not treat every ally as player-side. The isolated experiment below uses explicit Pawn identity for its player argument and does not prove the real game's ownership transitions.

## Episode scope and strict scan budget

Only collect during reconciliation, for at most eight Episode members. Evaluate material custody/ownership first; a cap or missing log must never suppress tracking of a durably held created Pawn.

Owner-approved initial bounds:

- first **32** entries of `BattleLog.Battles`;
- first **128** entries of each selected battle;
- at most **4,096 log entries per candidate**, at most **32,768 entry checks for eight candidates**;
- at most four concern yields per relevant exact-type entry;
- first **150** PlayLog entries for supporting diagnostics, only if the optional collector is included;
- first **128** candidate direct-relation records for the initial narrow S4 relationship rule.

These are policy constants, not persisted rosters or watchers. Iterate by index without copying complete logs. A single shared scan matching a small candidate set can later reduce work, but is unnecessary to establish the bound. Do not scan all world Pawns, run `AccumulatePawnGCDataImmediate`, use `RelatedPawns.Take(128)` (its graph traversal before each yield is not bounded), or scan every tick.

`BattleLog.Add` inserts a new battle at index zero. `Battle.Add` inserts its entry at zero; `Battle.Absorb` sorts by ascending `LogEntry.Age` (`Battle.cs:93,109`). Entries normally appear newest first. The battle list is not reordered when an older battle receives a newer entry. Thus the first 32 battles are a **fixed list window**, not guaranteed globally newest combat entries. Mods may reorder public lists. Inspect every selected entry's timestamp; do not exit early merely because one entry is old.

Entry `Timestamp` is **absolute ticks**. Episode ticks are game-relative. Use a validated absolute cutoff `episode.createdTick + Find.TickManager.gameStartAbsTick`, with wide arithmetic and range checks. `TickManager.TicksAbs` is `ticksGameInt + gameStartAbsTick`; both values are saved (`Verse.TickManager.cs:53–64,284–288`). Never compare an absolute log timestamp directly to a game-relative Episode tick. Skip future timestamps as corrupt/unusable. If the tick origin is missing/invalid, no S3 log promotion; positive material custody still promotes.

An older active battle outside the first 32, an older entry beyond 128, pruned entries, malformed references or unsupported mod types can all produce false negatives. This is deliberate: **remain anonymous**, retain no synthetic “probably encountered” identity, and report an aggregate truncated/unsupported-evidence diagnostic. This conservative rule must not discard the physical Pawn or lose a held person.

The limits bound our work under normal enumerator behavior. A mod can patch an exact vanilla `GetConcerns()` implementation to execute arbitrary expensive code before yielding; no bounded query can guarantee a CPU deadline against arbitrary injected code. Unknown entries are skipped without invoking their concerns enumerator. Owner mod-stack runtime timing remains a gate.

## Pruning, references and save/load limits

`PlayLog.Add` reduces its list to **150** on each add (`PlayLog.cs:26–38`). Public list mutation/mods can exceed this until the next vanilla add, so enforce our own limit. Even ordinary chatter can rapidly prune a relevant conversation.

`BattleLog.BattleHistoryLength = 20` is **not a hard practical limit**. Reduction requires more than 20 nonabsorbed battles **and** the selected trailing battle's `LastEntryTimestamp + Max(420000,5000) < Find.TickManager.TicksGame` (`BattleLog.cs:51–63`). Recent battles can exceed 20; an individual battle's entries have no cap. The source's `LastEntryTimestamp` is taken from `entries[Count-1]` (normally the oldest after front insertion), and the reduction compares that absolute entry timestamp to game-relative ticks. We do not fix or rely on vanilla pruning/expiry. Our independent window handles very large logs.

Vanilla deep-saves battles/entries. Entry classes save endpoint Pawns with `Scribe_References.Look(..., saveDestroyedThings:true)` (Melee `:238–239`, RangedFire `:196–198`, Explosion `:206–208`, RangedImpact `:308–312`). These are **reference records**, not owners that deep-save an otherwise missing Pawn. `Battle.ExposeData` rebuilds its concerns set from loaded entries in PostLoadInit (`:141–157`). No promotion should rely on an unresolved endpoint, stale instance retained from before load, or an entry absent after pruning. Evaluate after normal cross-reference/PostLoadInit completion from the currently loaded same Pawn references.

Both logs have `Notify_PawnDiscarded`; they remove affected entries and warn unless silent removal is requested. `WorldPawnGC` keeps log/tale/relationship/memory-referenced Pawns even when they do not qualify for Network promotion. **“Ephemeral to The Network” does not mean “force-discard this Pawn now.”** Normal group cleanup must strip Episode ownership and hand eligible off-map anonymous Pawns to vanilla's `PassToWorld(..., Decide)` / normal lifecycle. Do not delete unrelated logs, relations or memories to satisfy a retention target. A continuing vanilla Pawn can survive without a KnownCharacter; that is not a hidden Network roster. Existing production release uses `Decide` and guarded state checks (`RimWorldPhysicalWorldPort.cs:342–347`); production never imports the PR #11 fixture-specific Discard workaround.

No actual in-game or save/load test was run in this spike. The binary's persistence methods support the proposed design; they do not prove that the owner’s loaded save/mod stack preserves every reference.

## Owner-approved strong-evidence matrix

| Evidence | Promotion decision | Exact qualification / exclusions |
| --- | --- | --- |
| S1 material custody/ownership | **Strong; mandatory** | Positive validated observation that a created anonymous Pawn is arrested/captured/enslaved/recruited, kidnapped, or otherwise durably held by vanilla. Reserve the same Pawn while other Episode members remain Pending; promote it in the atomic whole-Episode terminal commit before release, even at normal named capacity. Include durable third-party custody to satisfy trackability invariant. A spawned Pawn, any caravan membership, or lack of an owner lookup by itself is insufficient. |
| S1 rescue | Future strong signal | Only a verified actual rescued person/outcome; no rescue implementation or site in 3.2B. S11 remains FAIL. |
| S2 explicit Network identification | **Strong** | Authoritative planned event/letter deliberately identifies this exact Episode Pawn/slot as an individual. The promotion intention must enter DECIDE/PLAN before atomic commit; subsequent PUBLISH cannot discover it after release. Mere vanilla personal name, translated text matching, a generic faction letter or being mentioned incidentally is insufficient. |
| S3 player-side combat | **Strong within window** | Exact-type allowlist, endpoint shapes above, same entry, current candidate plus player-side identity, Episode absolute time scope, bounded query. Generic `AnyEntryConcerns`, whole-battle membership, third-party combat, turret false pair and unsupported events do not promote. |
| S4 former player colony stake | **Strong** | Public `PawnUtility.EverBeenColonistOrTameAnimal` true for this humanlike candidate, from positive recorded time. Current recruitment supplies S1 eligibility without waiting for the record to increment; promotion still commits with the terminal whole-Episode batch. |
| S4 direct relationship | **Strong within window** | First 128 actual `candidate.relations.DirectRelations` records; nonnull valid relation and nonnull other Pawn currently player-faction/player-hosted. No guessed opinion threshold or arbitrary acquaintance. Direct-only qualification intentionally misses inverse-only/implied/virtual family relationships; broad graph expansion is deferred pending a separate bounded policy. Never force-discard Pawns retained by vanilla for such missed relations. |
| Other vanilla GC reasons | **Insufficient** | `Generating`, `Spawned`, `CorpseExists`, `FactionLeader`, `ForceKept`, `ReservedByQuest`, `ForSale`, `TransportPod`, raw `InPlayLog`/`InBattleLog`, `InActiveTale`, random retention and Network's own reservation are not individually strong evidence. Kidnapping/holding belongs to validated S1, not a GC string probe. |
| Narrowed PlayLog interaction | **Supporting only** | Candidate plus player-side in a relevant saved entry within bounded/time window, but does not independently promote and does not turn another weak flag into strong evidence. |
| Simple company/team ordinary presence | **Insufficient** | No named record from map placement, generic vanilla name, broad “ever seen” or chatter. Small/role-defining seat presence follows the independent size/seat policy. |

## PlayLog decision

The owner accepted **supporting only** and permits omission entirely from the first production collector unless a concrete diagnostic consumer needs it. `PlayLogEntry_Interaction` exposes identities through concerns but not its `InteractionDef`; `InteractionWithMany` is not proof of a consequential individual interaction and can include many recipients. `InteractionSinglePawn` cannot provide a two-Pawn pair. Public `LogEntry.def` is a log rendering definition, not a substitute for protected interaction semantics; translated game strings and `ToString` are not stable classification APIs. No reflection or Harmony merely to recover `intDef`.

If collected, at most 150 entry inspections/candidate and four yielded concerns; an incomplete many-recipient entry is supporting evidence missing, not a promotion fallback. Do not sum two weak observations into S1–S4.

## Isolated headless experiment

Reviewed isolated source (not shipped/registered in production or the ordinary test runner):

- [ScanSpike.cs](exploratory/S27/ScanSpike.cs)
- [ScanSpike.csproj](exploratory/S27/ScanSpike.csproj)
- [Portable reproduction steps](exploratory/S27/README.md), using external build/output directories.

Original experiment files and logs in this prepared cloud workspace:

- `/workspace/.onboarding/phase32b-s27/ScanSpike.cs`
- `/workspace/.onboarding/phase32b-s27/ScanSpike.csproj`
- `/workspace/.onboarding/phase32b-s27/build.log`
- `/workspace/.onboarding/phase32b-s27/benchmark.log`

The standalone net472 executable references the **real** game binary. Reflection/`FormatterServices.GetUninitializedObject` creates controlled endpoint shells without generation/maps; reflection sets private fixture fields only. Actual `GetConcerns()` and timestamp properties execute unmodified. Test-only Harmony suppresses Unity presentation dependencies (`ContentFinder<Texture2D>.Get`, Verse Log output); it is not imported by TheNetwork.dll. The player argument is an explicit Pawn identity in this microbenchmark, not an actual Faction/host observation. No game generation, pawn opinions, custody, combat simulation or save/load is faked as runtime proof.

**15 assertions passed**: candidate/player melee; third-faction exclusion; candidate/player ranged fire; explosion; self-only rejection; generic Event rejection; turret RangedImpact false pair rejection; three-party impact rejection; valid duplicate actual==original impact; self-hit with original player rejection; ambiguous two-raw-concern impact rejection; old timestamp exclusion; all-negative large history; out-of-window evidence remains anonymous; in-window evidence promotes exactly one.

History: **64 battles × 10,000 entries = 640,000 entries**, eight anonymous candidate shells, every entry an actual known RangedFire type. Worst case examines **32 × 128 × 8 = 32,768 entries** regardless of total history. After 20 warm-up scans, **100** all-negative scans on this cloud Mono process: median **2.599 ms**, p95 **2.831 ms**, maximum **3.039 ms**. Fixture creation and Harmony startup are excluded from scan timing. Build: **0 warnings / 0 errors**. This proves the bound and the source-qualified query shape on synthetic data; it does not estimate real combat rates or establish frame-time safety with mods.

The reviewed copy was rebuilt using the portable external-output steps with warnings treated as
errors: **0 warnings / 0 errors, 15 assertions passed**. Its repeat timings were median **2.624 ms**,
p95 **3.063 ms**, max **3.337 ms**. Variation reinforces that these are machine-local scan measurements.

Use the linked reproduction steps with `RIMWORLD_MANAGED`/`HARMONY_DLL` or MSBuild
`RimWorldManaged`/`HarmonyDll` values and a local net472 targeting pack. Keep output outside
`1.6/Assemblies`; do not import this source into `Source/TheNetwork` or register it as a gameplay scenario.

## Required future runtime gates and new risks

- One candidate versus actual player colonist; actual captured/player-hosted Pawn; third-party raider/animal; missed shot; friendly fire/social fight policy; non-Pawn turret with candidate actual recipient and player original target; three-way ranged impact; beneficial ability/item use; unsupported mod entry.
- Save/load before reconciliation: same endpoint Pawn resolution, converted absolute cutoff, pruned and unresolved entries do not promote; capture still promotes when logs are absent or full.
- Real mod-stack bounded reconciliation timings; large public lists and older active battle outside 32 demonstrate conservative miss behavior. No per-tick collector.
- Ephemeral company presence/chatter can leave vanilla-owned logged/related Pawns. Cleanup must neither retain them in a Network roster nor force-discard/unreference them. Save/load stays free of dangling references.
- Direct-only S4 is knowingly incomplete. A requirement to exhaustively find every implied/virtual player relation would require a new bounded relationship policy and may be an owner decision; do not silently use a world/family graph scan.
- Do not declare owner/runtime S27 PASS from this experiment. S27 has useful source/headless evidence and a precise implementation rule; future production runtime acceptance remains pending.
