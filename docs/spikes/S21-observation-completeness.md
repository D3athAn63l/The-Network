# S21 — Observation completeness (held custody)

**Verdict: PARTIAL — HEADLESS PASS; owner baseline 194 PASS, 0 FAIL, 0 INCONCLUSIVE; final RT-PHYX-022/save/load/025 acceptance PASS (52 PASS, 0 FAIL, 0 INCONCLUSIVE); caravan/transport/other-faction-prisoner observations remain headless-only**

**Phase 3.2A status: MERGED / HEADLESS VALIDATED / OWNER RUNTIME VALIDATED. PR #11 — MERGED.** The owner ran `RT-PHYX-020…025` and save/load in a fresh Dev Quicktest at
source `76b3ae1`. All scenarios reported PASS; post-load registry state was 5 bound / 5 healthy, 4/4 retained covered, 0 integrity findings.
The save/load nevertheless reported a removed Network encounter faction through `slaveFaction` in synthetic 022. Other scenario passes
remain evidence. The owner then reran `db0f795`: **022: 40 PASS / 0 FAIL / 0 INCONCLUSIVE**, save/load,
**025: 11 PASS / 0 FAIL / 0 INCONCLUSIVE**, with **1 bound / 1 healthy, 1/1 retained covered, 0 integrity findings**.
The same contractor remained PlayerSlave / Captured, `slaveFaction = null`, and no removed Network faction reference survived.
That accepted production enslavement; the remaining warden `LookTargets /targets/1` reference and state -1 discard warning were confined to
the fixture. **Historical status:** the corrected RT-PHYX-022 fixture acceptance was recorded as **PENDING** before the owner's final run;
the following completed acceptance supersedes that earlier pending state.

On accepted source **`e768fef`**, the owner completed **022 → SAVE → LOAD → 025**: **022 41/0/0, clean save/load, 025 11/0/0**;
focused total **52 PASS / 0 FAIL / 0 INCONCLUSIVE**, **1 bound / 1 healthy / 1-of-1 durable retained covered / 0 integrity findings**.
The same Pawn stayed PlayerSlave / Captured / OutOfCustody(PlayerSlave), reserved and unavailable to old-NPC simulation; `slaveFaction`
was null in the actual save. Exactly the test-created message was cleaned, the warden was safely discarded, and the encounter faction was
removed with repeated RELEASE harmless. There were no slaveFaction/faction-reference, warden LookTargets or invalid-discard errors.
The correction issues are **FIXED**; no corrected acceptance rerun is pending
([final acceptance and source audit](../PR11_CORRECTION_VALIDATION.md#final-owner-runtime-acceptance-pass)).
Caravan, transport and another faction's prisoner still have headless proof only, so this spike remains PARTIAL.

## 1. The question

[PHYSICAL_LIFECYCLE § 25](../PHYSICAL_LIFECYCLE.md#25-open-questions-and-spikes), S21: do the bounded polls and the signals catch every custody
transition (downed, caravan join, held transitions, map removal, kidnapped then recruited), even when signals are dropped? The Phase 3.2A
prompt's pass criterion: **no named Network person is silently returned home while still held, lost because they left a map, simulated
abstractly while vanilla owns them, regenerated, duplicated, or transitioned twice.** Signals may be missed. The bounded custody watch must
still converge on the truth.

## 2. Build and environment

| Item | Value |
|---|---|
| Base | `main` at `24a4881` (Phase 3.1 merged, owner runtime validated) |
| Game assemblies | `Assembly-CSharp 1.6.9676.17735` (owner-provided reference set; vanilla behaviour read from the decompiled source) |
| Headless | Mono; `Tests/run-tests.sh`; the `Custody.*` and `Rescue.*` tests in `Phase32aCustodyTests`, and `RT-PHYS-031…033` (the safe tier over the scriptable fake port) |
| Runtime | Historical baseline `76b3ae1`: **194 PASS, 0 FAIL, 0 INCONCLUSIVE**, with the subsequently fixed slaveFaction issue; final `e768fef` acceptance: **022 41/0/0, clean save/load, 025 11/0/0, total 52/0/0**, healthy 1-of-1 retained coverage |

## 3. How each custody state is distinguished (positive vanilla state only)

Every state below is read from public vanilla state. None is inferred from a pawn being **absent** (`PawnObserver.Observe`, classified in
the order of [PHYSICAL_LIFECYCLE § 9.3](../PHYSICAL_LIFECYCLE.md#93-classification)). Stop condition R.1 (a custody state that cannot be
told apart) was **not** hit.

| Case | Vanilla state read | Observation (holder) | What the Network does | Headless proof | Physical scenario |
|---|---|---|---|---|---|
| Arrest (player prisoner) | `IsPrisonerOfColony` | `HeldByPlayer` (`PlayerPrisoner`) | the mission episode closes once: `HeldByPlayer`, status `Captured`, custody `OutOfCustody`, `KnownCharacter.CapturedByPlayer` | `Custody.CaptureCommitsTheEpisodeOnce`, `Custody.EpisodeCompletesWhileThePersonStaysHeld`; RT-PHYS-031 | RT-PHYX-020 |
| Still a prisoner (time passes) | as above | as above | nothing: the watch observes, changes nothing, never advances the person abstractly | `Custody.HeldPersonIsNeverAdvancedAbstractly`, `Custody.WorkIsBoundedByTheHeldPeople` | RT-PHYX-020, 025 |
| Release / escape | the pawn left the map: a world pawn, `ReservedByQuest` by the Network's registry, no permanent faction | `WorldFree` + exit | a **Custody episode** commits `Returned` once; RELEASE; then `Stored`; `KnownCharacter.Freed` | `Custody.ReturnIsExactlyOnceAndWaitsForRelease`, `Custody.SamePawnSurvivesCaptureReturnAndRematerialization`; RT-PHYS-032 | RT-PHYX-020 |
| Released prisoner still walking out | `Spawned`, not a prisoner | `Spawned` (`Unaffiliated`) | still held: "not a prisoner any more" is not "home" | `Custody.Rules_ClassificationMatrix` | RT-PHYX-020 |
| Map removal | `MapDeiniter.PassPawnsToWorld` (1.6, `MapDeiniter.cs:142–180`): a player-hosted prisoner gets `SetGuestStatus(null)` and is passed to the world (`LeftMap` sent); a colonist on a hostile map is **kidnapped** | `WorldFree` (released) or `Kidnapped` | the same two rows as release or kidnap: no separate path | `Custody.Rules_ClassificationMatrix`, `Custody.ReturnIsExactlyOnceAndWaitsForRelease` | RT-PHYX-020 (falls back to removing the test map if the released pawn has not left) |
| Recruitment by the player | `Faction == OfPlayer`, not held | `JoinedPlayer` (`PlayerColonist`) | `Defected`, **never `Stored`**, never abstract; the pawn is left as the player's colonist (no faction change back, no removal, no clone). O-20 locks exit from old NPC availability; future Player Contractor work reads the real colony through `PlayerProxy` (not implemented) | `Custody.RecruitmentIsDefectedAndNeverStored`; RT-PHYS-033 | RT-PHYX-021 |
| Enslavement (Ideology) | `IsSlaveOfColony` | `HeldByPlayer` (`PlayerSlave`) | a change of holder only (no episode, no event) | `Custody.Rules_ClassificationMatrix` | RT-PHYX-022 (needs Ideology; without it the run reports a GAP, never a pass) |
| Kidnapped | in a faction's `kidnapped` list | `Kidnapped` | `Kidnapped`, `Captured`, `Contractor.Captured` (§ 15.3; never with the contract) | `Custody.KidnappedThenRecruitedByTheCaptor`; RT-PHYS-008 | RT-PHYX-023 |
| Kidnapped, then recruited by the captor | `SetFaction(captor)` and `RemoveKidnappedPawn` (vanilla's MTB ≈ 30 days); a world pawn with a permanent non-player faction | `WorldFree` + other allegiance | held by `OtherFaction`, still `Captured`; **never a free return** | `Custody.KidnappedThenRecruitedByTheCaptor`; RT-PHYS-033 | RT-PHYX-023 (forces the captor's recruitment) |
| Held by another faction | `HostFaction` not the player | `HeldByOther` (`OtherFaction`) | captive of another faction | `Custody.Rules_ClassificationMatrix`; `Rescue.OnPhysicalResolvedFaultMatrix_SoloHeldWrittenOff` | — (headless only) |
| Caravan | `IsCaravanMember`; the caravan's faction | `InCaravan` (`PlayerCaravan`, `OtherFaction`, or `Unknown` when the caravan has no faction) | held, not captive | `Custody.CaravanAndTransportHolders` | — (headless only) |
| Transport pod / shuttle | `IsTravelingInTransportPodWorldObject` | `InTransport` (`Transport`) | a mission episode **waits** (§ 12.3); a held person's holder becomes `Transport` | `Custody.CaravanAndTransportHolders` | — (headless only) |
| Death while held | `pawn.Dead` | `Dead` | a Custody episode commits `Killed` once; death is monotonic; custody is `Released`, `heldBy = None`, `heldSinceTick = -1`, and the person leaves the held index | `Custody.DeathWhileHeldIsMonotonic`, `Custody.OrgLeaderCapturedThenDiesWhileHeld`; RT-PHYS-033 | RT-PHYX-024 |
| Unknown / unrecognized holder | nothing positive | `Unknown` | **fail closed**: stays held, holder `Unknown`; never home | `Custody.UnknownOwnershipFailsClosed` | — |
| A binding that no longer resolves | null / discarded | `Gone` | `Lost`, never regenerated, live holder metadata cleared | `Custody.UnresolvedBindingIsLostNeverRegenerated` | — |
| An anonymous member held | any held kind, unnamed slot | — | `Quarantined(UnsupportedCustody)` (anonymous promotion is 3.2B) | RT-PHYS-012 (re-targeted) | — |

## 4. Signals: they only wake

The pawn's tag signals (`LeftMap`, `Killed`, the custody events routed through `SiteCallbacks.RoutePawnSignal` and
`RetainedPawnRegistry.OnPawnEvent`) **only** move the custody watch forward (`WakeHeld`); they never decide. The watch observes each held
person every 2,500 ticks regardless, so a dropped signal costs at most one period (`Custody.SignalsOnlyWakeAndDroppedSignalsConverge`),
and a duplicated one changes nothing (each physical scenario checks it). Every transition with consequences goes through a **Custody
episode**, the exactly-once reconciliation pipeline 3.0 built, so a second wake-up finds the person already reconciled.

## 5. Pass criteria, headlessly

| Criterion | Headless evidence |
|---|---|
| never returned home while still held | `Custody.UnknownOwnershipFailsClosed`, `Custody.RecruitmentIsDefectedAndNeverStored`, `Custody.KidnappedThenRecruitedByTheCaptor`, the rules matrix (`Spawned`, `WorldOther`, `ReservationBroken`, caravan, transport never return) |
| never lost because they left a map | the classification never reads "not spawned"; release is a positive `WorldFree` |
| never simulated abstractly while vanilla owns them | `Custody.HeldPersonIsNeverAdvancedAbstractly` (the authority gate refuses every abstract writer); RT-PHYS-031 |
| never regenerated | `Custody.SamePawnSurvivesCaptureReturnAndRematerialization`, `Custody.UnresolvedBindingIsLostNeverRegenerated` |
| never duplicated | the same `Pawn` object throughout; no Network `PassToWorld` (the port's counter stays 0) |
| never transitioned twice | `Custody.CaptureCommitsTheEpisodeOnce`, `Custody.ReturnIsExactlyOnceAndWaitsForRelease`, `Custody.CustodyCommitFaultRestoresAndRetriesOnce`, duplicate wake-ups |
| survives save/load | `Custody.SaveLoadWithAHeldPerson` (real Verse Scribe) |

## 6. Harmony

**Not needed.** Every case above is observed through public vanilla state, and the watch closes the gaps that have no signal. The
design's Harmony contingency ([PHYSICAL_LIFECYCLE § 25](../PHYSICAL_LIFECYCLE.md#25-open-questions-and-spikes), "if S21 fails: document the
one method, specify a postfix, do not implement") is not triggered.

## 7. Completed owner evidence and remaining observation limits

The owner evidence now directly covers player prisoner/release, recruitment, enslavement, kidnapping/captor recruitment, death while held,
and save/load retention of held states represented in these runs. The final sequence and all fixture/save acceptance criteria passed;
earlier 020, 021, 023 and 024 baseline passes remain accepted. The reproduction steps and final results are in
[RUNTIME_TESTING § 18](../RUNTIME_TESTING.md#18-phase-32a-held-custody).

Caravan, transport and other-faction-prisoner observation still have **headless proof only**. Captor recruitment proves that specific
OtherFaction allegiance case, not every other-faction-prisoner transition. This limits S21 to **PARTIAL** after PR #11 merged.
R-50 truthful aging remains OPEN, O-20 direction remains locked, S11 remains FAIL / rescue STOPPED, and 3.2B, 3.2C, 3.3 and Phase 4 are
not implemented. The acceptance update changes documentation only; validated source/artifact provenance remains `e768fef`.
