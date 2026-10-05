# PR #11 correction validation

Existing [PR #11](https://github.com/D3athAn63l/The-Network/pull/11), branch `claude/new-session-nhng3f`. This final correction is limited to **RT-PHYX-022 fixture cleanup**. Production custody, encounter-faction qualification/removal, recruitment, retention and physical lifecycle logic are unchanged. Save format remains **5**, with no production Harmony reference or new persisted Network state. R-50 remains OPEN, O-20 direction remains locked, S11 remains FAIL / rescue STOPPED; their implementation and decision/audit files are untouched.

## Final runtime-test fixture cleanup

### Accepted owner evidence and remaining rerun

The owner reran corrected source **`db0f795`** with the real vanilla enslavement action: **RT-PHYX-022: 40 PASS, 0 FAIL, 0 INCONCLUSIVE**, then **SAVE → LOAD → RT-PHYX-025: 11 PASS, 0 FAIL, 0 INCONCLUSIVE**. Post-load state: **1 bound, 1 healthy binding, 1/1 durable retained covered, 0 integrity findings**. The same contractor Pawn remained PlayerSlave / Captured / OutOfCustody(PlayerSlave), unavailable to the old Solo, with healthy binding coverage. Its saved `slaveFaction` was null; there was no dangling Network slave faction or unresolved removed encounter faction. This is owner-supplied production evidence, not a new run by this agent. The `d.hidden` correction is accepted and untouched.

The remaining noise was confined to the disposable warden: `Tried to discard Ed whose state is -1`, followed on load by `Could not resolve reference to object with loadID Thing_Human55842`, under `Verse.LookTargets`, `/targets/1`. **The new fixture-cleanup owner rerun is PENDING.** The earlier 194-PASS baseline and earlier correction validations are preserved below as historical records.

### Exact root causes and vanilla audit

Evidence: reference checkout **`9fcca42215c247135067694cd97c1ed7a4d86a7b`**, supplied **Assembly-CSharp 1.6.9676.17735**, SHA-256 **`5cf1b5be399d5b1c9c56ca72c9d35b4ecf307feacf5859d04ac5a1aa5926356a`**, decompiled with **ILSpy 9.1.0.7988**. These are binary method findings; the reference snapshot's exact game-build/source alignment is still unrecorded. No reference-repository content changed.

| Vanilla type/member | Audited behavior and consequence |
|---|---|
| `RimWorld.GenGuest.TryEnslavePrisoner(Pawn warden, Pawn prisoner)` | Calls `Messages.Message("MessagePrisonerEnslaved".Translate(prisoner, warden), new LookTargets(prisoner, warden), MessageTypeDefOf.NeutralEvent)`. Target 0 is the contractor; target 1 is this run's disposable warden. The real API remains the scenario's action. |
| `Verse.Messages.Message` | Its omitted `historical` argument defaults to true. The **same Message object** goes into `Find.Archive` and the static live-message list. Natural expiry removes the live view only, not the archived message. There is no public API to remove one live message. `Messages.Clear` clears every live message and is unsuitable. |
| `RimWorld.Archive.ExposeData`, `Verse.Message.ExposeData`, `Verse.LookTargets.ExposeData`, `Verse.Scribe_TargetInfo.Look(GlobalTargetInfo, ...)` | The archive deep-saves its messages; each message deep-saves LookTargets; targets are serialized as GlobalTargetInfo references and cross-resolved on load. A target reference does **not** deep-save the pawn. The old fixture was neither map-owned nor world-owned after cleanup, so its saved load ID could not resolve. This is exactly the `/targets/1` error, independent of contractor binding and `slaveFaction`. |
| `RimWorld.Archive.Remove(IArchivable)` | Public, narrow removal of one object, including its pin. It leaves unrelated entries/pins alone. Suitable for deleting only the message created by this test. |
| `Verse.Thing.DeSpawn`, `Verse.Thing.Discard`, `Verse.Pawn.Discard` | DeSpawn leaves `mapIndexOrState = -1`. Thing.Discard accepts only destroyed state **-2**, then writes discarded state **-3**. Calling Pawn.Discard directly after despawn therefore emits the reported state -1 warning; `silentlyRemoveReferences: true` does not fix that precondition. |
| `RimWorld.Planet.WorldPawns.PassToWorld(..., PawnDiscardDecideMode.Discard)`, private `DiscardPawn`, `Verse.Pawn.Destroy` | Explicit Discard invokes vanilla's guarded disposal: mark the pawn as being discarded, Destroy if necessary, then Discard if necessary, unwind the guard in finally. Pawn.Destroy sees the guard and does not re-add the pawn to WorldPawns. This establishes -2 before -3 without permanently retaining the fixture. |
| `WorldPawns.ExposeData`, `GetSituation`, `WorldPawnGC.GetCriticalPawnReason` | World-owned pawns are deep-saved; unspawned player faction membership alone does not provide save ownership. Keeping a warden through `KeepForever` pins it; a former humanlike colonist can also become GC-critical. Neither guarantees automatic post-load cleanup. A local unspawned pawn referenced only by LookTargets is not save-safe. |

### Chosen solution and rejected alternatives

Before the real enslavement call, 022 snapshots archived **object identities**. In its finally block, `EnslavementFixtureCleanup.TryRemoveMessage` validates the entire archive delta before mutation. It accepts at most one newly archived NeutralEvent message whose ordered targets are exactly **this contractor Pawn and this fresh warden Pawn**. It does not match translated text. Pre-existing, wrong-type, differently targeted or ambiguous warden references cause a FAIL and preserve the warden on the test map, rather than discarding a save target.

For the identified message only, it calls vanilla `archive.Remove(owned)`, then assigns an empty `LookTargets` to that same object. The detached live UI view can expire normally, with no warden reference. No unrelated message or target is cleared. If vanilla rejects enslavement or throws before creating the message, zero removals are allowed; a successful enslavement must account for exactly one removal.

After successful cleanup, the warden is despawned from the suite's test map, passes the existing `TestFixtures.DisposeRefusal` guard (this run's tag, unbound, unspawned, not a world pawn), and goes through **vanilla explicit Discard mode**. The scenario verifies `Discarded` and absence from WorldPawns. The shared `TestFixtures.TryDispose` implementation remains untouched. The contractor and every prior production assertion remain intact.

Keeping a synthetic warden save-owned until after load was rejected: it either leaves a world/colonist fixture in the save, relies on GC that may retain it, or needs new durable cleanup state. Merely waiting for live-message expiry leaves the archived references. Clearing all messages would alter unrelated player state. A real colonist, synthetic `SetGuestStatus` action, reflection access to private live messages, Harmony patches and new Network fields are unnecessary and unused. The narrow public archive API plus one owned message's public targets provides the cleanup boundary.

### Regression coverage

Seven new tests in `EnslavementFixtureCleanupTests` prove:

- Real `Archive.Remove` removes only the identified new message and its pin; existing and newly added unrelated messages/pins/targets remain. The same live object loses its warden targets, and cleanup is idempotent.
- Pre-existing, reversed, extra-target, wrong-type and duplicate warden references refuse cleanup **before any mutation**. Missing prerequisites fail closed; early-rejected enslavement needs no message removal.
- **Real Archive/Message/LookTargets Scribe save/load**, with a negative control reproducing exactly `Thing_Human55842` at `/targets/1`. The cleaned archive round-trips without vanilla warnings/errors and retains the unrelated archived message.
- Real vanilla IL confirms the historical message/archive behavior and Destroy-before-Discard ordering. Real Thing.Discard reproduces the -1 warning and accepts -2 without a warning.
- Real `WorldPawns.PassToWorld(..., Discard)` executes Destroy then Discard with its guard and never retains the pawn. Only the map-dependent virtual pawn calls are test probes; this is not a claim of full in-game pawn destruction.
- Source gates preserve the real enslavement API, test-owned warden, exact snapshot/cleanup/disposal ordering, ownership guard, fail-closed preservation, no broad message clear, no direct despawn/discard, and no retention or new persisted state.

The existing real-API source gate is updated to require this safe cleanup. The Phase 3.1 PassToWorld scope gate keeps the production adapter as the sole production caller and permits exactly one additional **ownership-guarded, Discard-only** call for 022's warden. It continues to reject any other caller. No prior gameplay assertion is weakened.

### Final build and headless validation

Corrected source commit: **`e768fefc3ab613aaa8d08f834f0edabf4ad0e735`**. The subsequent artifact/validation commit changes only this record and the shipped DLL; all runtime and test source is identical to that commit. Final HEAD is reported in the delivery message.

| Check | Result |
|---|---|
| Complete suite, run 1 | **481 tests, 35,084 checks, 0 failures**, exit 0 |
| Complete suite, run 2, new Mono process over the same shipped DLL bytes | **481 tests, 35,084 checks, 0 failures**, exit 0 |
| Production and test compilation | **0 warnings, 0 errors**, `TreatWarningsAsErrors=true` confirmed for both projects |
| All nine repository source-scan gates | **PASS** |
| New fixture regressions | **7 tests, 49 checks, 0 failures**; included in both final full runs |
| Save format / production Harmony | **5 unchanged / no reference** |
| New owner fixture-cleanup rerun | **PENDING** |

Both full runs are unfiltered and include the existing long procurement/career simulations. Run 1 uses `Tests/run-tests.sh` from `/workspace/The-Network`, with a newly created empty `TEST_OUT`. Run 2 launches `THENETWORK_REPO=/workspace/The-Network mono TheNetwork.Tests.exe` without a filter in that same test-output directory. The rebuilt mod DLL is copied to `1.6/Assemblies/TheNetwork.dll`; byte comparison proves the test copy and shipped artifact are identical for both runs. No stale runner output, skipped/disabled tests or synthetic passing replacements are used.

| DLL provenance | Value |
|---|---|
| Embedded stamp | `built 2026-10-05T14:09Z, source commit e768fef` |
| Informational version | `0.1.0+e768fefc3ab613aaa8d08f834f0edabf4ad0e735` |
| SHA-256 | `502a4def6dddadf2686bffad9a2d61f6545941f7628742bffdeb92e5e6f0fc28` |
| Bytes | **1,138,176** |
| Framework | **.NET Framework 4.7.2** |

Build environment: **.NET SDK 8.0.425 / MSBuild 17.11.48 / Mono 6.12.0.199**, supplied game and Harmony 2.4.1.0 references, C# 7.3 Release. The repository's normal build scripts and projects are unchanged. External MSBuild targets keep working output outside the checkout and route the runner to that fresh DLL, with warnings treated as errors; the final validated bytes are explicitly copied into the shipped artifact. The supplied Steamworks assembly is available; no replacement dependency or Mono JIT workaround is needed. Metadata confirms net472 and no Harmony reference in the mod. No test dependency is shipped.

### Exact owner acceptance rerun

Use the new shipped DLL in a **fresh disposable save with Ideology active**:

**RT-PHYX-022 → SAVE → LOAD → RT-PHYX-025**.

022 must PASS: same contractor Pawn, PlayerSlave / Captured, `SlaveFaction == null`, old Solo unavailable with strength zero, encounter faction removed, and exactly the test-created message cleaned before the owned warden is discarded. Save/load must have no Network deep-save error, removed Network faction, disposable-warden `Thing_Human...` reference, `LookTargets /targets/1` error or invalid-state discard warning. 025 must PASS with the same binding, correct custody, healthy retained coverage and **0 integrity findings**. Preserve Player.log across the sequence. The new owner rerun is **PENDING** until actually performed; the accepted `db0f795` production results above remain evidence.

### Files changed and scope

- `Source/TheNetwork/Diagnostics/RuntimePhysicalTests/PhysicalCustodyScenarios.cs`: narrow 022 archive snapshot/finally cleanup and vanilla fixture disposal.
- `Source/TheNetwork/Diagnostics/RuntimePhysicalTests/EnslavementFixtureCleanup.cs`: test-message identity/target cleanup helper; no durable state.
- `Tests/TheNetwork.Tests/EnslavementFixtureCleanupTests.cs`: seven new regression tests.
- `Tests/TheNetwork.Tests/EnslavementCorrectionTests.cs`: registration and corrected disposal source gate.
- `Tests/TheNetwork.Tests/Phase31Tests.cs`: exact owned-warden Discard-mode exception to the PassToWorld scope gate.
- `docs/RUNTIME_TESTING.md`, `docs/spikes/S21-observation-completeness.md`, this record: preserve owner evidence and specify the fixture-only rerun.
- `1.6/Assemblies/TheNetwork.dll`: rebuilt from the final committed source in the subsequent artifact commit.

**No production custody/encounter/recruitment/retention logic changed. `EncounterFactions.Qualifies(d.hidden)` and `EncounterFactions.Release` are untouched. R-50/O-20/S11 are untouched. No new PR or merge.**

---

## Historical enslavement correction record (source db0f795)

The following record is preserved verbatim for provenance. Its pending production-rerun statements describe that earlier point in time; the accepted owner results and the current fixture-only rerun are recorded above.

Existing [PR #11](https://github.com/D3athAn63l/The-Network/pull/11), branch `claude/new-session-nhng3f`. Phase 3.2A remains **IMPLEMENTED / HEADLESS VALIDATED** with owner baseline passes preserved and the corrected enslavement/save/load rerun **PENDING**. Save format stays **5**. R-50 remains **OPEN**, O-20 direction stays locked, S11 stays **FAIL / rescue STOPPED**. Their production behavior and audit/decision files are untouched by this correction.

## Runtime enslavement correction

### Owner evidence (preserved)

The owner tested source **`76b3ae1`** in a fresh Dev Quicktest. `RT-PHYX-020`, `021`, `022`, `023`, `024`, save/load and `025` all reported PASS: **194 PASS, 0 FAIL, 0 INCONCLUSIVE**. Post-load state: **5 bound, 5 healthy bindings, 4/4 retained covered, 0 binding integrity findings**. These are the owner's reported results, not new runtime runs by this correction.

| Scenario | Accepted baseline evidence |
|---|---|
| 020 | Arrest, held commit, release and same-pawn return PASS. |
| 021 | Real vanilla recruitment, same Pawn became player faction / Defected, old Solo unavailable with zero abstract strength. |
| 022 | Holder transition to PlayerSlave / Captured passed; its synthetic action failed to model vanilla faction clearing, exposed by the later save/load warning. Corrected action rerun PENDING. |
| 023 | Real kidnapping and captor recruitment, same pawn became ReservedByQuest after leaving the kidnapped tracker; independently supports the already documented R-50 boundary edge without closing R-50. |
| 024 | Held death, Released / None / -1, held-watch exclusion, same dead pawn binding PASS. |
| 025 | Bindings and held state survived save/load; the separate slaveFaction warning prevents calling this a completely clean save/load. |

Save reported a `slaveFaction` reference to **Faction_19** that was not deep-saved; load could not resolve Faction_19 in `Pawn_GuestTracker`. The owner identifies it as the 022 contractor's temporary Network encounter faction. No provenance ties the later unrelated `otherPawn` warning to a Network mutation, so it is not attributed or investigated here.

### Exact root cause and production risk

Old 022 called `p.guest.SetGuestStatus(Faction.OfPlayer, GuestStatus.Slave)` directly. The tracker takes the old faction into a local, calls `Pawn.SetFaction(player)`, then writes that local into `slaveFactionInt`. The nested faction change resets guest state and notifies `FactionManager.Notify_PawnLeftFaction(oldFaction)` **before** the outer call installs the slave cache. Vanilla can therefore queue the temporary faction while the pawn already has player faction and no slave cache. A later manager tick removes queued factions without another eligibility check. Removal clears main pawn factions, not the cached guest-tracker slave faction. Scribe then writes a removed faction reference.

The observed default-refugee failure is a **test-path error**, but the audit also proves a **real production fallback risk**: real `GenGuest.TryEnslavePrisoner` skips its pre-clear when a fallback's **def** is not hidden, even if Network made the **instance** hidden. Such a fallback enters the same caching/removal sequence. The spawned/transit removal guard is also insufficient for an ordinary world pawn because those pawns are not scanned. Hidden defs are therefore required for every new eligible encounter shell.

### Vanilla source audit

Primary method evidence is the supplied **Assembly-CSharp 1.6.9676.17735**, SHA-256 **`5cf1b5be399d5b1c9c56ca72c9d35b4ecf307feacf5859d04ac5a1aa5926356a`**, decompiled with ILSpy 9.1.0.7988. The public API is **`bool GenGuest.TryEnslavePrisoner(Pawn warden, Pawn prisoner)`**.

| Source | Evidence and implication |
|---|---|
| `GenGuest.TryEnslavePrisoner` | Keeps already-slave/creepjoiner cases; only `prisoner.Faction.def.hidden` triggers `SetFactionDirect(null)` before slave status; then messages, history and apparel unlocking. The real API must be called. |
| `Pawn_GuestTracker.SetGuestStatus`, `ExposeData`, `Notify_PawnRecruited` | Slave status caches the previous faction after changing pawn faction; ExposeData saves it as `slaveFaction`; recruitment explicitly clears it. |
| `Pawn.SetFaction` | Nested guest reset precedes old-faction removal notification; this can queue removal before the outer slave cache is assigned. |
| `Faction.Hidden`, `FactionGenerator.NewGeneratedFactionWithRelations` | Instance Hidden is an override over the def. The generator sets the instance override, not `FactionDef.hidden`; hiding the instance cannot satisfy the enslavement check. |
| Refugee/beggar quest roots | Both create hidden temporary factions with neutral relations and register them with FactionManager. Refugees use `FactionDefOf.OutlanderRefugee`; beggars use `FactionDefOf.Beggars`. |
| `RecruitUtility.Recruit` | Unlocks apparel, clears guest status, changes to the recruiting faction and clears the slave cache. No recruitment change is needed. |
| `GenGuest.PrisonerRelease`, `SlaveRelease`, `GuestRelease`; `Pawn.HomeFaction` | Prisoner release can restore SlaveFaction; release uses HomeFaction to decide whether to stay or exit. A stale slave faction can therefore affect lifecycle behavior as well as serialization. |
| `GuestUtility.GetExtraFactionsFromGuestStatus`, `QuestUtility.GetExtraFaction`, `QuestPart_ExtraFaction.Notify_FactionRemoved` | SlaveFaction participates in extra home allegiance; quest-owned extra home/mini factions are separately handled by quest parts on removal. Network adds no such extra-faction part; a pawn's HomeFaction is derived, not an independent cleanup cache. |
| `FactionManager.FactionCanBeRemoved`, `TryQueuePawnFactionForRemoval` | Checks living spawned/transit pawns' main, extra home, extra mini, slave and leader references, world-object faction and quest reservation. Ordinary world pawns are not checked. Queueing also considers extra/slave refs when a pawn leaves a map or dies. These checks do not repair references or recheck an already queued shell. |
| `FactionManager.QueueForRemoval`, `FactionManagerTick`, `Remove` | Queue deduplicates; tick removes without a new eligibility check; removal nulls only matching main Pawn.Faction and not guest slave caches, then notifies vanilla managers. |
| Network `EncounterFactions.Release` | Existing temporary-only vanilla notification remains unchanged; duplicates queue once and removed refs resolve null. No custom slave cleanup is added. |

The owner DLL bundle has no game XML. The vanilla-data mirror at **GAarsin/Rimworld_Data commit `673f1fc1792faf998cb40418bf5e01592e4a7966`** confirms the 1.6-era OutlanderRefugee and Beggars defs declare **`hidden = true`**:
[Royalty Factions_Misc.xml](https://github.com/GAarsin/Rimworld_Data/blob/673f1fc1792faf998cb40418bf5e01592e4a7966/Royalty/Defs/FactionDefs/Factions_Misc.xml) (blob `45c51521dce3ca2f1ed55432040bbcf230fac5f3`),
[Ideology Factions_Misc.xml](https://github.com/GAarsin/Rimworld_Data/blob/673f1fc1792faf998cb40418bf5e01592e4a7966/Ideology/Defs/FactionDefs/Factions_Misc.xml) (blob `236fcb10e140c0fc4521d8b2a038152468ece370`). This is mirrored vanilla XML, not the owner's exact patched def database. The corrected runtime scenario also verifies the loaded encounter def's hidden property. Vanilla OutlanderRefugee remains preferred; an unsafe patched preferred def is rejected under the same capability rule as any fallback.

### Narrow changes

- `EncounterFactions.Qualifies` adds **`d.hidden`** to the existing predicate; ordinal fallback, generic pool, preferred vanilla choice and temporary creation stay unchanged. No candidate means the existing contained abort, never an unsafe or permanent faction. Existing saved faction instances are not migrated or edited.
- RT-PHYX-022 invokes **`GenGuest.TryEnslavePrisoner(warden, p)`** with a fresh tagged disposable player warden spawned on the suite's existing test map. In finally it despawns/disposes only that unbound fixture through the existing ownership guard. No home colony pawn is used. Vanilla history records the event ticks, not a persisted reference to that disposable doer (`HistoryEventsManager.RecordEvent`).
- It observes PlayerSlave / Captured, same binding, holder-only bookkeeping, blocked abstraction and unavailable old Solo; checks SlaveFaction null; retries the completed episode's existing RELEASE and waits for vanilla removal, then verifies no cached shell reference and a harmless repeated RELEASE. The retry is an explicit test cleanup/proof: the first RELEASE was blocked by the prisoner, and vanilla's direct hidden-faction pre-clear does not notify the old faction. No production cleanup schedule changes.
- No new Harmony patch/reference, persisted field, save schema, custody/recruitment rule, R-50 behavior, O-20 design or rescue work is added.

### Regression coverage and limits

Eight added tests in `EnslavementCorrectionTests` cover the real API source gate and own-warden guard, original vanilla IL clearing/caching/queue ordering, **real Pawn_GuestTracker Scribe save/load with the exact dangling-faction failure as a negative control**, same-Pawn holder-only production reconciliation and NPC checkout exclusion (Solo/Crew), hidden capability, preferred/safe ordinal fallback, fail-closed creation, and idempotent **real vanilla removal queue** with permanent factions untouched. The S21 documentation gate is updated to preserve the owner results and corrected rerun gap.

The Scribe test inputs are the audited null/cache outcomes; it does not execute map-dependent enslavement headlessly. The queue test simulates the completed removal boundary after testing vanilla's queue. Full TryEnslavePrisoner, actual map removal and physical Pawn pointer resolution across a game save/load remain **owner rerun PENDING**. No headless result is presented as an owner runtime pass.

### Corrected build and final validation

| Final check | Result |
|---|---|
| Complete headless suite, run 1 against final committed source and shipped DLL | **474 tests, 34,979 checks, 0 failures** |
| Complete headless suite, run 2 against the same committed source and shipped DLL | **474 tests, 34,979 checks, 0 failures** |
| Production and test compilation | **0 warnings, 0 errors**, warnings treated as errors |
| All nine repository source-scan gates | **PASS** |
| Owner corrected 022 / save / load / 025 | **PENDING** |

The shipped DLL was built from published corrected source **`db0f795882ceb2ec388e940930e510d46d66899c`**. The subsequent artifact/validation commit changes only this record and the shipped DLL; production and test source are identical to that source commit. Both final suite runs occur after that commit and load a byte-identical copy of its committed DLL. All tests run without a filter. The earlier 466-test validation below remains historical evidence for source 76b3ae1, not the new totals.

| DLL provenance | Value |
|---|---|
| Source stamp | `built 2026-10-05T11:45Z, source commit db0f795` |
| Informational version | `0.1.0+db0f795882ceb2ec388e940930e510d46d66899c` |
| SHA-256 | `cc3d70fd5b4cf4fba38adb755f79ed7b712b3f99783a773a7e7ec9db7bdd8fb8` |
| Bytes | **1,136,128** |
| Target / save format / production Harmony | **net472 / 5 unchanged / no reference** |

Build environment: SDK 8.0.130 Roslyn, C# 7.3, optimized deterministic net472, supplied game references. The SDK compiler is invoked directly with project sources/generated attributes and the existing stamp rule, as in the earlier validation; repository build scripts are unchanged. The headless environment supplies the missing unmodified Steamworks.NET 2024.8.0 dependency (commit `a2fc889ab2672981ec3e6225d551d86ce6923121`). Both complete runs use Mono 6.8 with **`MONO_ENV_OPTIONS=--optimize=-inline`**, preserving the earlier workaround for its native JIT crash in the existing procurement soak. No test is skipped or weakened; no test-only dependency is shipped.

### Required owner rerun

On a **fresh disposable save**, Ideology active, corrected DLL source stamp: **RT-PHYX-022 → SAVE → LOAD → RT-PHYX-025**. Both scenarios must PASS, the same bound pawn stays PlayerSlave / Captured, custody and registry coverage remain correct, and no Network red error or removed Network slaveFaction reference may occur. Preserve the log from before save through after 025. Existing 020/021/023/024 baseline passes stand: the only production change narrows unsafe fallback defs, while vanilla OutlanderRefugee remains eligible and shared custody behavior is unchanged. A broader 020–025 rerun is not required for this correction.

R-50 remains OPEN, O-20 direction locked, S11 FAIL / rescue STOPPED. PR #11 remains open and unmerged; no new PR or merge is performed.

---

## Earlier correction: source 76b3ae1 (historical record)

This is a bounded correction on `claude/new-session-nhng3f`, for the existing [PR #11](https://github.com/D3athAn63l/The-Network/pull/11). Phase 3.2A remains **IMPLEMENTED / HEADLESS VALIDATED**, with owner physical runtime evidence pending. Save format remains **5**. S11 remains **FAIL** and rescue-site implementation remains **STOPPED**. No later phase is implemented.

## Custody correction

`ReconciliationApplier.LeaveHeldCustody` writes the next custody state and clears `heldBy = None`, `heldSinceTick = -1` within the existing durable snapshot/rollback commit:

| Commit operation | Result |
|---|---|
| Physical `CharacterKilled` | Existing monotonic death plus custody `Released`; live holder cleared. The abstract-only operation keeps its existing custody behavior. |
| `CharacterLost` | Existing Lost fate plus custody `Lost`; live holder cleared. |
| `CharacterStored` | Custody `Stored`; existing cleanup consolidated in the helper. Aging behavior is unchanged. |
| `CharacterReverted` | Existing bound/unbound distinction (`Stored` / `Unmaterialized`); live holder cleared. |

The assignment audit found no legitimate non-`OutOfCustody` state requiring live holder metadata. Planning starts from abstractly authorized custody. Genuine `CharacterHeld`, `CharacterDetached`, holder-only updates and snapshot restoration still describe actual held custody; they are not cleared. Continuous holder changes retain their original `heldSinceTick`.

`EpisodeChecks` now reports both stale holder and stale timestamp in **every** non-held state, including timestamp-only corruption. Validation remains report-only. Existing defaults and Scribe defaults are `None` / `-1`; no migration or historical capture reconstruction is added. Old already-stale saves are diagnosed, not silently repaired.

RT-PHYX-024 explicitly checks Dead / Released, non-held custody, `None` / `-1`, absence from the held index/watch, the same dead pawn binding and released reservation. RT-PHYX-021 also checks old Solo availability is `Unavailable` with zero abstract strength.

## R-50 audit: deliberately OPEN

The [full source audit](spikes/R50-held-aging-bookmark-audit.md) identifies the supplied RimWorld **1.6.9676.17735** assembly, its SHA-256, decompiled vanilla methods, every relevant custody state, available timestamps and all Network aging calls/writes.

Vanilla normally ages spawned prisoners/slaves, kidnapped pawns and caravan members. Quest reservation makes a qualifying world pawn suspended, and both vanilla age paths skip suspended pawns. Kidnapped and caravan situations precede reservation; transport follows it, so a transit label alone does not prove applied biological time. When a captor recruits and removes a pawn from the kidnapped list, reservation can become effective without a Network suspension callback.

`PawnRef.agedThroughTick` is now defined precisely as **the latest game tick through which this pawn's biological age is known to have actually been brought current**, rather than its observation tick. The existing field can represent that invariant. Creation and successful existing catch-up establish evidence; the existing Stored fallback can still stamp exit/commit time without a trustworthy boundary. That is explicitly documented as the unresolved R-50 gap.

Available evidence is limited: `becameWorldPawnTickAbs` records world entry; session-only `LeftMap` records a map exit; `heldSinceTick` records observed holding; AgeTracker has no last-applied game-tick timestamp. A 2,500-tick watch period limits normal observation latency, **not** biological uncertainty. The missing age interval can span the entire time since the last trustworthy bookmark, including years. Advancing to the observation time skips suspended years; blindly replaying from an older bookmark double-ages vanilla's already-applied interval.

**Production aging is unchanged.** Existing `PawnAging.CatchUp` / `AgeTickMothballed`, successful bookmark advancement, completed-chunk accounting and uncertain-failure quarantine remain. There is no new persisted field, scheduler, background age job, Harmony or `BirthAbsTicks` write. No R-50 closure tests or new runtime aging scenario are claimed. The narrow future alternative is a separately proven exact, one-time vanilla/lifecycle suspension and completed-aging boundary, then reuse of the existing bookmark and catch-up. Another field cannot recover an unobserved boundary.

## O-20 and rescue direction

O-20 is **direction locked**: the recruited person remains the same real player Pawn / KnownCharacter, retaining identity, relationships, provenance and history. Old NPC membership becomes previous affiliation and the person permanently exits that organization's available NPC roster/simulation. The existing Phase 3 bridge remains `Defected` + `OutOfCustody(PlayerColonist)`. No organization transfer or final status/affiliation schema is added.

Future Phase 4 participation uses `PlayerProxy` with `ContractorProfile`, retaining the real player faction and reading execution truth from the actual colony through `ColonyReader`. There is no abstract player roster or former-contractor simulator.

The gate/checkout audit found no current recruitment deployment leak: `AuthorityGate` excludes held custody; `KnownCharacter.IsAvailable` excludes Defected; Solo availability/strength and organization named-person strength, availability and checkout honor those gates. Physical planning also refuses held people. A new Solo/Crew regression uses a real Pawn with a player-faction fixture, production reconciliation and actual contractor checkout: the original NPC organization cannot select the person, physical planning refuses, identity/previous organization remain, and the same Pawn remains player faction. Actual vanilla recruitment still requires RT-PHYX-021 in RimWorld.

Rescue is documented as a separate outcome: the player frees the **same** Pawn; positive evidence of genuine freedom and departure returns the person to Network custody and the original organization with holder metadata cleared. An injured contractor can return Wounded and resume the career after recovery. Freedom does not recruit them, rewrite their permanent organization/history or use `WillJoinColonyIfRescued`.

The preferred future S11 alternative must prove captor → liberation → temporary free contractor faction/guest/Lord behavior → departure → storage, using the existing temporary encounter-faction shell. No permanent organization factions, site generation, staging, trigger or letter are implemented. **S11 remains FAIL; rescue remains STOPPED.**

## Regression tests and final validation

All original Phase 3.2A tests are preserved. Existing death/loss tests now check holder cleanup and held-watch exclusion. Six new tests cover:

| Test | Evidence |
|---|---|
| `Custody.Correction_PlayerRecruitCannotRedeployForOriginalNpc` | Real Pawn/player-faction fixture; Solo and Crew checkout exclusion; denied physical planning; same identity, organization and Pawn. |
| `Custody.Correction_TerminalMetadataSurvivesSaveLoad` | Real Scribe round-trip after death, loss and free return; holder metadata and held index/watch stay clear. |
| `Custody.Correction_RevertedClearsLiveHolder` | Bound and unbound NeverPlaced reverts retain the intended custody state and clear stale holder fields. |
| `Custody.Correction_HolderChangesKeepContinuousSinceTick` | Prisoner → slave → colonist preserves the continuous holding start, including save/load. |
| `Custody.Correction_AllNonHeldStatesReportStaleMetadata` | Every non-held enum state; holder plus timestamp and timestamp alone; report-only validation; existing defaults. |
| `Custody.Correction_TerminalRollbackRestoresLiveHolder` | Death/loss fault-after-each-commit-step sweeps; snapshot restores genuine live holder; retry clears once and emits one terminal event. |

| Final check | Result |
|---|---|
| Entire headless suite, run 1 against the final committed source and shipped DLL | **466 tests, 34,910 checks, 0 failures** |
| Entire headless suite, run 2 against the same committed source and shipped DLL | **466 tests, 34,910 checks, 0 failures** |
| Production and test C# compilation | **0 warnings, 0 errors**, warnings treated as errors |
| Repository source-scan gates | PASS |
| Save format / production Harmony reference | **5 unchanged / none** |
| Owner physical runtime | **PENDING** |

Build environment: SDK **8.0.130** Roslyn, C# **7.3**, optimized deterministic net472, Mono reference assemblies and the supplied game references. This container's MSBuild CLI had a process-metadata/PID namespace failure, so the final build invoked SDK Roslyn directly with the project sources, generated SDK assembly attributes and the same project build-stamp rule. No repository build scripts were changed.

The game DLL bundle lacked `com.rlabrecque.steamworks.net.dll`, required by vanilla ParseHelper during Scribe. The test environment supplies the unmodified open-source Steamworks.NET **2024.8.0** source, commit `a2fc889ab2672981ec3e6225d551d86ce6923121`, compiled with its Unity assembly name. It is not shipped. Mono **6.8** produced a native SIGSEGV in the existing procurement soak under its default JIT; the full runs use `MONO_ENV_OPTIONS=--optimize=-inline`. No test is skipped or weakened, and production code has no Harmony dependency. The existing test harness retains its test-only Harmony reference.

## Shipped DLL provenance

Corrected source commit: **`76b3ae1d80741a34cc5e76dbec30e4b1b07a8e0b`**. The subsequent artifact/record commit changes no production or test source. Its final HEAD is recorded in PR #11 and the delivery message.

`1.6/Assemblies/TheNetwork.dll` was rebuilt after committing that corrected source. Embedded build stamp: **`built 2026-10-05T00:55Z, source commit 76b3ae1`**. Assembly informational version: `0.1.0+76b3ae1d80741a34cc5e76dbec30e4b1b07a8e0b`.

DLL SHA-256: **`2bd8fe88c04657c3dadb81a52cdedf6c6f4c4f770cafec85c5cdfb246596a720`**. Both final suite runs load a byte-identical copy of this shipped DLL. Metadata inspection confirms net472 and no Harmony assembly reference.

## Files changed in this correction

| File | Purpose |
|---|---|
| `Source/TheNetwork/Domain/Physical/ReconciliationApplier.cs` | Atomic live-holder cleanup helper and four commit paths. |
| `Source/TheNetwork/Domain/Physical/EpisodeChecks.cs` | Diagnose both fields in every non-held state. |
| `Source/TheNetwork/Domain/Actors/NetworkActor.cs` | Clarify the live continuous-held timestamp. |
| `Source/TheNetwork/Domain/Physical/EpisodeModel.cs` | Precise age-bookmark contract and explicit R-50 limitation. |
| `Source/TheNetwork/Domain/Physical/CustodyRules.cs` | O-20 comments; rules unchanged. |
| `Source/TheNetwork/Domain/Physical/ReconciliationPlanner.cs` | O-20 comment; rules unchanged. |
| `Source/TheNetwork/Diagnostics/RuntimePhysicalTests/PhysicalCustodyScenarios.cs` | Strengthen RT-PHYX-021/024. |
| `Tests/TheNetwork.Tests/Phase32aCustodyTests.cs` | Six regressions and existing death/loss assertions. |
| `README.md` | Current scope, design locks and test totals. |
| `docs/DECISIONS.md` | ADR-056 custody invariant, O-20, R-50 and rescue semantics. |
| `docs/IMPLEMENTATION_PHASES.md` | Bounded current-phase status. |
| `docs/PHYSICAL_LIFECYCLE.md` | Bookmark contract, live holder invariant, O-20, rescue and evidence. |
| `docs/RISKS.md` | R-50 source-audited open; remaining R-51 lifecycle concerns. |
| `docs/RUNTIME_TESTING.md` | Exact recruitment/death runtime expectations and post-load checklist. |
| `docs/spikes/README.md` | Index the R-50 audit. |
| `docs/spikes/S11-rescue-site-holder.md` | Documentation-only rescue/recruitment distinction and future physical spike. |
| `docs/spikes/S21-observation-completeness.md` | Updated custody/recruitment expectations; runtime still pending. |
| `docs/spikes/R50-held-aging-bookmark-audit.md` | New source audit, custody matrix, timestamps, uncertainty and stop verdict. |
| `docs/PR11_CORRECTION_VALIDATION.md` | This review and validation record. |
| `1.6/Assemblies/TheNetwork.dll` | Rebuilt artifact from the committed corrected source. |

## Remaining owner work and limits

Run **RT-PHYX-020, 021, 022, 023, 024, save/load, then 025**, on a fresh disposable save with source stamp `76b3ae1`. RT-PHYX-022 requires Ideology. INCONCLUSIVE/GAP are not PASS. S21 remains PARTIAL until physical evidence exists.

R-50's exact aging boundary still needs a separately proven integration path and owner design review if no permitted evidence exists. R-51's accumulating held people/actor shells, actor retirement, public reactions and final condition/affiliation schema remain later work. O-20's direction is decided; Phase 4 implementation remains future. The future rescue alternative still needs its faction/guest/Lord proof. Already-stale legacy metadata is reported without inventing history.

The existing PR remains draft, open and **unmerged**. No new PR is created and no merge is performed.
