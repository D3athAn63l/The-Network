# Runtime spikes (Phase 1, Phase 2 and Phase 2.5)

One file per spike ([RIMWORLD_INTEGRATION § 5](../RIMWORLD_INTEGRATION.md#5-runtime-spikes)). Each records the
question, the build, the environment, the steps, the result, the logs, the verdict and the consequence.

## Verdicts used

| Verdict | Meaning |
|---|---|
| **PASS** | Run in RimWorld 1.6 and met every pass criterion. |
| **PARTIAL** | Some of the question was answered by real evidence: either not a RimWorld runtime run (for example the real Verse `Scribe` executed headlessly), or a runtime run that covered only some of the cases. What remains is listed. |
| **NOT RUN — OWNER RUNTIME VALIDATION REQUIRED** | Needs the RimWorld player (maps, sites, caravans, UI, letters). It was **not** run. The owner steps below are reproducible. |
| **FAIL** | Run and did not meet the criteria. |

**No spike in this folder is marked PASS.** The environment that built Phases 1 and 2 cannot launch RimWorld, and
compiling, static inspection or headless tests are not runtime passes.

**What the owner has run since, and what it is not.** The owner has since run real-game observations that are
recorded elsewhere and are deliberately **not** counted as spike passes here: the Phase 2 / 2.75 legacy
procurement run (payment hold → recovery → full drop-pod delivery) and the **Phase 2.9 runtime-test validation**
in a fresh Dev Quicktest colony and the real modded colony (0 runtime FAILs;
[RUNTIME_TESTING § 15](../RUNTIME_TESTING.md#15-owner-observed-runtime-evidence)). Neither is the formal S4, S13
edge-case or **S20** checklist, so those verdicts below are unchanged. The Phase 2.9 runtime pass does not
complete S20: its spatial scenarios run in a sandbox over a synthetic world graph.

## Environment of this pass

| Item | Value |
|---|---|
| Machine | Linux container (`Linux 6.18`), no Unity player, no GPU, no RimWorld install |
| Build | .NET SDK 8.0.131, target `net472`, `LangVersion 7.3`; `./build.sh <Managed>` → `1.6/Assemblies/TheNetwork.dll`, 0 errors, 0 C# warnings (one MSB3277 netstandard 2.0/2.1 notice caused by the trimmed reference set; a real `Managed` folder ships `netstandard.dll`) |
| Game assemblies | Owner-provided `Rimworld DLLs (update).zip` from the `zRim_Source_XMLs` repository: `Assembly-CSharp 1.6.9676.17735`, Unity modules. Used as external references only; nothing proprietary is committed. |
| Headless tests | Mono 6.8.0.105; `Tests/run-tests.sh <Managed> <0Harmony.dll>`; Phase 1: 71 tests, 4,588 checks, 0 failures (after the pre-runtime fix pass). Phase 2 (after the final correction pass): 144 tests, 12,026 checks, 0 failures. Phase 2.5 (after its correction passes): 213 tests, 15,169 checks, 0 failures. `0Harmony 2.4.1` is used **by the test runner only** to stub Unity-only `Log`/`DeepProfiler` calls and to let `GenTypes` see TheNetwork.dll as a loaded mod would. TheNetwork.dll references no Harmony (a test checks its referenced assemblies). |

## Owner setup for every spike

1. Build (`./build.sh "<RimWorld>/RimWorldWin64_Data/Managed"`) or use the committed `1.6/Assemblies/TheNetwork.dll`.
   Check the first `[TheNetwork]` line in `Player.log`: it prints the build stamp (build time and source commit).
2. Enable **Development mode** (Options → Development mode). Dev actions are under the debug menu category
   **"The Network"** ([DEBUGGING § 3](../DEBUGGING.md#3-dev-actions)).
3. Optional for speed: "The Network → Toggle fee waiver" (fees are still frozen in the request's terms, just not
   charged) and "Toggle comms-gate override". **Do not use them for S4**, which tests the real payment and gate.
4. Phase 2 actions are under **"The Network (Phase 2)"**: close a bidding window now, run the next operation
   checkpoint now, force an outcome band, deliver now, force delivery failures, and so on ([DEBUGGING § 3](../DEBUGGING.md#3-dev-actions)).
5. "The Network → Run the next search round now…" resolves a search round immediately, with the same seeded
   outcome as waiting. "Force lead on next round", "Force divergence class…" and "Force source kind for next
   round…" steer the next round.
6. After each spike, attach `Player.log` (or the relevant `[TheNetwork]` lines) to the spike file and change its
   verdict.

| Spike | Verdict in this pass |
|---|---|
| [S1](S1-programmatic-vanilla-site.md) programmatic vanilla Site | NOT RUN — OWNER RUNTIME VALIDATION REQUIRED |
| [S2](S2-injected-site-comp.md) injected comp: callbacks, persistence, removal | NOT RUN — OWNER RUNTIME VALIDATION REQUIRED |
| [S3](S3-signal-bridge-registration.md) signal registration across new game/load/reload | NOT RUN — OWNER RUNTIME VALIDATION REQUIRED |
| [S4](S4-payment-and-comms-gate.md) payment, refund, Comms Console gate | NOT RUN — OWNER RUNTIME VALIDATION REQUIRED |
| [S5](S5-tolerant-list-loading.md) tolerant per-element list loading | PARTIAL (real Verse Scribe, headless) |
| [S6](S6-network-removal.md) removing and re-adding The Network | NOT RUN — OWNER RUNTIME VALIDATION REQUIRED |
| [S7](S7-external-mod-removal.md) removing an item mod mid-search / mid-site | NOT RUN — OWNER RUNTIME VALIDATION REQUIRED |
| [S8](S8-determinism.md) reload determinism (incl. Odyssey) | PARTIAL (Network logic headless; vanilla tile finder and Odyssey not run) |
| [S18](S18-performance.md) performance and save size | PARTIAL (harness and real Scribe run headlessly; in-game run pending) |
| [S19](S19-loot-without-extermination.md) loot without extermination | NOT RUN — OWNER RUNTIME VALIDATION REQUIRED |
| [S13](S13-drop-pod-delivery.md) drop-pod delivery (Phase 2) | PARTIAL — normal home delivery PASSED in the owner's runtime test; edge cases NOT RUN |
| [S20](S20-abstract-spatial-routing.md) abstract spatial routing, charter transport and Last Known Location placement (Phase 2.5) | NOT RUN — OWNER RUNTIME VALIDATION REQUIRED |

## Phase 3 spikes (planned; no records yet)

The Phase 3 design review revised the Phase 0 spike list and added four; the **amendment pass** extended S12 and S22 and added
S25 to S30. They are **defined, not run**, and have no record files yet; each gets one when its subphase starts. The questions, narrowest experiments and pass criteria are in
[PHYSICAL_LIFECYCLE § 25](../PHYSICAL_LIFECYCLE.md#25-open-questions-and-spikes): **S9r** registry reservation (revises S9),
S10 temporary faction, S11 site-part pawn holder, **S12** store-time normalization **and truthful aging catch-up**, S14 visit
Lord, S17 tag hygiene, **S21** observation completeness, **S22** the physical-tier guard (a session-only arm and a dedicated test
map; no disposability inference), **S23** first-creation pins and modded races, **S24** the save/load matrix, **S25**
role-constrained creation, **S26** team cohesion, **S27** encounter evidence, and, for the design-direction-only Phase 3.3,
**S28** a right-click command without Harmony, **S29** physical cargo at a handoff, **S30** a rendezvous site. A final
**micro-correction** added **S31**, *retained pawn exit reservation / the Free-world-pawn window*: the smallest safe 1.6
mechanism that keeps a retained named pawn from being redressed, discarded or reused between a **vanilla** map exit and
`Stored` authority (reserve while spawned, else a synchronous vanilla callback, else a narrow Harmony contingency, in that
order; [PHYSICAL_LIFECYCLE § 7.6](../PHYSICAL_LIFECYCLE.md#76-the-vanilla-exit-window-an-open-mandatory-spike-s31)). **S31 is
mandatory and blocks Phase 3.1** (3.0 does not need it); it is **NOT RUN**.
