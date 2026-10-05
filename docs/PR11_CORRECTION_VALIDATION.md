# PR #11 custody, aging and affiliation correction

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
