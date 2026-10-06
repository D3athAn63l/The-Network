# Phase 3.2B implementation and validation

**PHASE 3.2B IMPLEMENTED / HEADLESS VALIDATED — OWNER RUNTIME VALIDATION PENDING.**

This report describes the developer-triggered group slice delivered on `codex/phase32b-groups-concretization`, based on merged PR #12 `main` **`e251c61efcbb7773f36e31e4862373e22930173e`**. The owner accepted Composition v1, the promotion policy and the 3.2B boundary in [ADR-057](DECISIONS.md#adr-057--phase-32b-readiness-owner-decisions). The earlier [readiness audit](PHASE32B_READINESS_AUDIT.md) remains a historical design/source record.

No new owner in-game result is claimed. Phase 3.2A remains **MERGED / HEADLESS VALIDATED / OWNER RUNTIME VALIDATED**: PR #11's final `e768fef` result remains 022 **41/0/0** → clean SAVE/LOAD → 025 **11/0/0**, total **52/0/0**. S26 and S27 remain **PARTIAL**; S21 remains **PARTIAL** for the previously unrun observation paths. S11 remains **FAIL / rescue STOPPED**, R-50 remains **OPEN**, O-20 remains locked, and full 3.2C, 3.3 and Phase 4 remain unimplemented.

## Delivery and validation evidence

| Stage | Commit | Scope |
|---|---|---|
| 1 | `90e2f6679858ee2004cd5af7b91b486e024f9cdd` | Composition v1, stable operational roles, live seat policy and mission selection. |
| 2 | `7601e9d911c4b32bd200e717f8b996aa2ad6f330` | Same M1 registry extended with temporary durable Episode ownership and load reconstruction. |
| 3 | `99f678a572215fd8d71fd211c8af93907b7de27c` | Organization group planning/materialization, placement evidence and construction-only cohesion. |
| 4 | `3b80ca326d136be2d52ffc16fba308fd87d64100` | Source-qualified evidence and atomic same-Pawn promotion with exact conservation. |
| 5 | `5b403fe5fcc2f8e1d8e8e23d8f5c2f52264f0d8d` | Runtime scenarios, owner checklists, final documentation and validation. |
| 6 | `0186390abae9c5d12cb258c88fa9562b4e7c6215` | Ship the exact final run 2 DLL and acceptance evidence. |

| Final check | Result |
|---|---|
| Full headless suite, run 1 | **581 tests / 42,946 checks / 0 failures, exit 0** |
| Full headless suite, run 2, fresh process | **581 tests / 42,946 checks / 0 failures, exit 0** |
| Production and test compiler | **0 warnings / 0 errors**; warnings treated as errors. |
| Repository source gates | **All nine gates PASS** |
| Markdown links / whitespace | **760 local Markdown links checked / 0 errors; `git diff --check` PASS** |
| Save format | **5**, unchanged. |
| Production Harmony reference | **None**. |
| Owner RimWorld runtime | **PENDING**; this environment has no Unity player. |

The committed Stage 4 focused checkpoint passed **204 tests / 17,817 checks / 0 failures**, strict **0 compiler warnings / 0 errors**, all **nine source gates PASS**. The two final complete suite runs above supersede this focused checkpoint; their results were measured independently.

An intermediate Stage 5 full run passed **581 tests / 42,946 checks / 0 failures**, **0 compiler warnings/errors**, all **nine source gates PASS**. The final read-only custody-verifier hardening followed that run; two final fresh full passes against committed source `5b403fe` then each passed with the same **581 / 42,946 / 0** totals. This intermediate run is not substituted for the final artifact runs.

Final logs in the prepared cloud workspace: `/workspace/.onboarding/phase32b-final1.log` and `/workspace/.onboarding/phase32b-final2.log`. Each full script ran in a fresh process against committed source `5b403fe`, with strict production/test builds and all source gates. The shipped DLL is the tested **final run 2** output; separately rebuilt runs may have different time stamps. During development, a stale visibility assertion and a source-gate fixture/birth-factory assertion failed, were diagnosed and corrected before the final two passes; no flaky result was hidden.

Intermediate Stage 2 evidence was **507 tests, 38,193 checks, 0 failures**, strict production/test compilation. It is a checkpoint rather than the final whole-slice count above. Full runs retain prior procurement/career soaks, authority, custody, reservation, runtime-runner and source-gate coverage.

### Shipped DLL provenance

| Value | Delivered artifact |
|---|---|
| Path | `1.6/Assemblies/TheNetwork.dll` |
| Source commit | `5b403fe5fcc2f8e1d8e8e23d8f5c2f52264f0d8d` |
| Embedded build stamp | `built 2026-10-06T10:10Z, source commit 5b403fe` |
| Informational version | `0.1.0+5b403fe5fcc2f8e1d8e8e23d8f5c2f52264f0d8d` |
| SHA-256 | `c8acac8534822dfa3e55521019c1379f2acd3fd3084266ad63739cfad7270adc` |
| Bytes | **1,235,456** |
| Framework | .NET Framework 4.7.2 |
| Game reference | `Assembly-CSharp 1.6.9676.17735`, SHA-256 `5cf1b5be399d5b1c9c56ca72c9d35b4ecf307feacf5859d04ac5a1aa5926356a`. |

DLL and test outputs stay outside the mod checkout; standard ignored project intermediates are generated inside the checkout and are neither shipped nor tracked. The final run 2 DLL was copied to `1.6/Assemblies/TheNetwork.dll` and compared byte-for-byte with the tested output. Compiler/project/test dependencies are not shipped.

## Composition, operational identity and mission selection

`OrganizationCompositionV1` derives only role weights from immutable actor seed, origin capacity and original `ContractorProfile.specialties`. Supported origin capacities are **3/7/14/32**; unsupported origins refuse with a diagnostic. One Leader entry is reserved, existing `RoleDerivation.Candidates` supplies distinct non-Leader roles, and Rifleman supplies ordinary security. More than seven non-Leader roles are reduced by the stable `composition.v1` hash order with enum ties. Rifleman, Technician and Logistician carry weight two; other roles carry weight one. There are at most eight distinct recipe entries. The recipe is compatibility-sensitive under **Version 1** and is never persisted as a roster/template.

Live quotas preserve one Leader and use deterministic largest-remainder allocation over the rest. Living named pins consume matching seats, including held/unavailable pins; dead/departed pins release current capacity while their records remain historical. Existing pins exceeding proportional quotas remain intact. Remaining anonymous capacity is a count and never identifies an unobserved person. Abstract casualties remain tier-based; O-9's role-aware vacancy refinement is deferred.

Origin-era people receive write-once roles from the immutable full-origin cohort in CharacterId order, including dead/history records. The origin slots list starts with Leader, then enum-ordered quotas. Later unbound legacy people with an Unset role use a separate stable CharacterId salt. Non-Unset roles are never overwritten. A bound person with unknown role refuses rather than being repaired from current rank. Organizational `CharacterRole` remains separate: a Medic succeeding as Leader stays operationally a Medic through reload.

`OrganizationSeatPolicy` counts actual living current membership as anonymous healthy + wounded + committed plus distinct current living pins. Historical dead/lost/retired/defected records are excluded; captives retain identity and seat capacity. Narrow store-aware `CurrentNamedCount` inputs to Headcount/JobCapacity/OperatingReserve exclude departed history from current service scalar counts without changing their formulas; one-argument compatibility helpers remain. It selects role-matching available pins first, then matching anonymous roles and healthy source tiers in stable order. Required shortages refuse and optional shortages shrink with diagnostics; a wrong-role retained Pawn is never substituted. Missions are bounded to **eight members**; placement, observation and Closed-release entry points reject malformed overbound Episodes before engine member walks while preserving durable ownership/committed state. One unreleased group Episode per organization prevents double-spending residual seats/capacity. Operation-linked group handoff is deferred by the explicit group planning guard.

## Temporary reservation and placement truth

The existing hidden M1 quest/registry indexes two distinguishable categories: normal named retention and temporary active-Episode ownership. Temporary source of truth is **`PhysicalEpisode.members[].pawn`** while RELEASE is incomplete, including quarantine and interrupted release. Positive death removes the need for living-Pawn protection; corpse ownership remains vanilla. There is no dummy KnownCharacter, second quest/container, permanent anonymous registry or per-tick world scan.

`RebuildEarly` indexes durable thing IDs during world FinalizeInit, before cross-reference pointers are available. `ResolvePointers` validates resolved PawnRef pointers in PostLoadInit and installs reference-identity indexes before the first vanilla world tick. Unresolved/discarded/mismatching references are findings, never silently rebound. Named retention takes precedence when categories overlap. Queries revalidate durable eligibility, so rollback/release cannot leave a stale claim. A promoted slot remains protected through interrupted release until named coverage is established; successful completion removes temporary ownership and clears the derived temporary cache through `IGroupPhysicalWorldPort.EpisodeReleased`; no anonymous Pawn roster survives completion. Prepare-for-removal makes both categories inert and never deletes a vanilla-owned Pawn.

The only added persisted EpisodeMember facts are:

| Field | Absent/default | Meaning |
|---|---|---|
| `playerVisibleTick` | **−1** | First proven successful player-visible placement tick. |
| `p0Eligible` | **false** | Placement-time discretionary eligibility, frozen before later size/casualty changes. |

Both use additive Scribe defaults under save format **5**. An old anonymous Episode with absent fields gains no invented P0/history. A failed placement/generated-but-unspawned Pawn does not latch evidence. Production P0 requires successful placement on a home/player-occupied map and excludes the diagnostic test map. Runtime fixtures may explicitly inject visibility evidence to exercise this policy; that does not prove the real visibility rule or modify a colony. CharacterStore is never mutated at placement.

### Presence and strong identity policy

| Living organization size at placement | Anonymous presence eligibility |
|---|---|
| 2–6 | Every role may use P0 within the six discretionary living-seat target. |
| 7–12 | Role-defining non-Rifleman seats may use P0; ordinary Riflemen need strong evidence. |
| 13+ | Presence alone promotes nobody; anonymous members need strong evidence. |

Solos retain the existing always-named path. Existing known people remain known across growth/shrinkage. The six discretionary target includes leader/lieutenants and is not a CharacterStore ceiling. Accepted strong identity obligations may exceed six through the same membership model; no held identity is denied or truncated. Approximately **150 retained Pawns is a soft performance region**, and identity wins above it.

## Evidence, atomic promotion and conservation

The collector runs only at terminal reconciliation, for at most eight candidates. Positive material custody is evaluated before optional logs/caps. Unknown or malformed optional evidence does not itself authorize promotion; valid S1 and S4 remain independent of missing logs or unusable combat windows.

| Signal | Implemented qualification |
|---|---|
| **S1** | Positive material capture/arrest/enslavement/recruitment/kidnapping/supported durable third-party custody is mandatory same-Pawn identity; logs and discretionary capacity are unnecessary. |
| **S2** | Exact authoritative planned individual-subject predicate/seam. No existing production producer was invented; labels, translations, incidental letters and names are insufficient. |
| **S3** | Exact vanilla `BattleLogEntry_MeleeCombat`, `RangedFire`, `ExplosionImpact` endpoint types; guarded `RangedImpact` only for ordered {initiator, actual, original} with actual==original. Candidate plus current player-faction/player-hosted Pawn must be the actual two endpoints. Unknown subclasses, third-party contact, turret false pairs, unrelated battle membership and unsupported entry types are rejected. |
| **S4** | Public `PawnUtility.EverBeenColonistOrTameAnimal`, or a bounded valid direct relation to a current player-faction/player-hosted Pawn. No graph traversal, private GC reason probe or world-Pawn search. |
| **PlayLog** | Omitted from production; supporting-only policy remains. No reflection/Harmony or inferred interaction semantics. |

S3 checks at most **32 battles × 128 entries = 4,096 entries/candidate**, **32,768 for eight**. Concerns shapes read at most four yields, rejecting extras/non-Pawn/null shapes. S4 reads at most **128 direct relation records**. The Episode's game tick is converted to a validated absolute-time window using `gameStartAbsTick`; old/future/invalid windows provide no **S3 combat evidence**; positive S1/S4 facts still qualify independently. Bounded windows can miss pruned/out-of-window evidence deliberately. Missing evidence does not authorize force-discarding a Pawn vanilla still retains.

Promotion enters DECIDE/PLAN before atomic COMMIT, never after RELEASE or during PUBLISH. The same Pawn supplies its existing name and unchanged PawnRef; the plan supplies a new CharacterId, organization provenance, slot `opRole`, member link, current status and custody. `firstEncounterTick` takes only a proven `playerVisibleTick <= now`; strong evidence without dated placement leaves it **−1**, while still creating the required same-Pawn identity. No encounter date is inferred from a capture or log query. ID previews do not advance the allocator. All new records, membership, committed stock, episode nested state, touched existing people, actor components and publication state are in the guarded touched set. A commit fault restores the snapshot, truncates CharacterStore additions and restores its index, restores the ID allocator and removes the promotion result; `consequencesApplied` is written last. The Episode reservation remains intact for safe retry. Vanilla mutations, scheduler actions, reservation handoff and publication stay in idempotent post-commit stages.

The conservation contract counts one human as aggregate stock **or** a named record:

| One-person transition | Count change |
|---|---|
| Anonymous checkout | Healthy stock −1; committed anonymous +1. |
| Ordinary anonymous return | Committed −1; healthy/recovery stock +1 once. |
| Promoted return/current-member capture | Committed −1; current named membership +1; no anonymous restoration. |
| Promoted recruitment/permanent departure | Committed −1; historical/player-side KnownCharacter created; old NPC current membership does not increase. |
| Anonymous death/permanent departure | Committed −1 once; no second subtraction. |
| Named death/departure | Current named membership −1 once; anonymous stock untouched. |
| Never placed | Restore only checked-out anonymous units; no unproven promotion. |

Tests check the explicit components and rollback/retry, rather than relying only on final abstract strength. Named strength still follows the existing named-person weights; promotion is not promised to be strength-neutral. Defected people remain excluded from old NPC availability under O-20 and are never cloned/reabstracted.

## Faction, Lord and construction-only cohesion

One group uses the existing per-Episode temporary encounter faction, shared by its members, and vanilla Visit/Lord behavior. Network organizations stay persistent domain actors; they do not receive permanent RimWorld factions. The Solo path remains supported. No tactical AI, new UI window, rescue holder, procurement handoff or player-as-contractor implementation is added.

First projections force new Pawns and skip vanilla initial blood/non-blood relation generation. Eligible adult candidates use request-time FixedIdeo selected from established organization members or the first valid new batch candidate, then verify the returned Pawn has no initial direct relations and retains the exact requested ideology. FixedIdeo eligibility requires active Ideology and a kind that permits ideology. Existing bound members keep actual ideology and all physical/social history. A rejected inconsistent candidate is refused safely. No opinion floors, friendships, memories, trait deletion, relation edits or post-bind social sanitization are implemented. S26's generated-crew hostility frequency and mod thought safety remain unmeasured until the owner runs the disposable probe.

## Runtime scenarios and pending owner acceptance

Implemented menu families: **026** small first visit; **027** five-person continuity (three reused + two concretized, then full same-Pawn repeat); **028** company zero presence promotion; **029** one anonymous arrest/atomic promotion; **030A SAVE** released/concretized group, **030B SAVE** anonymous arrest while peers Pending, **030V VERIFY** read-only loaded newest unambiguous 030/031 checkpoint; **031** owned Medic organizational succession then SAVE/LOAD→030V; **032A/032B BUILD** approximately 150/300 retained and **032V OBSERVE** loaded coverage/lookups. Exact labels/actions are in runtime § 19. Strict compilation and full headless validation passed; owner execution remains pending, and no new runtime PASS is claimed. Genuine home P0 has a separate typed adapter scope API but no added home-map action/UI.

Scenarios use existing session-only arm, dedicated tagged test map, sentinel and preserve-on-failure behavior. They are excluded from Quick smoke/Full safe regression. Existing IDs are not renumbered; save/load is owner-assisted, not automated. See [RUNTIME_TESTING § 19](RUNTIME_TESTING.md#19-phase-32b-groups-and-progressive-concretization) for exact checkpoints and commands.

The final run 2 headless retention observations used actual PawnRef/registry and real Scribe fixture round-trips. No count above 150 is a failure criterion; every required identity remains intact.

| Headless population | Early registry rebuild | Pointer resolution | 100,000 reservation lookups |
|---|---:|---:|---:|
| 150 named | 0.009 ms | 0.054 ms | 6.901 ms |
| 300 named + 8 temporary Episode members | 0.017 ms | 0.169 ms | 7.347 ms |

| Reference XML fixture | Bytes | Scribe save | Load + cross-references | Early thing-ID bridge | Resolved pointer index |
|---|---:|---:|---:|---:|---:|
| 150 named | 91,871 | 3.078 ms | 5.451 ms | 0.015 ms | 0.061 ms |
| 300 named + 8 temporary | 186,516 | 4.451 ms | 10.219 ms | 0.026 ms | 0.172 ms |

The XML fixture delta is **94,645 bytes**. These are observed process-local costs in the final run, including headless save/load reference resolution and coverage checks; they are **not a complete RimWorld save, Unity TPS, real game load elapsed or a mod-stack frame-time guarantee**. Runtime 032 exposes the separately pending owner registry/lookup observations; full save bytes/TPS/load timing remain manual owner evidence.

Above-six strong identity is proven by headless planner/commit/lifecycle tests. The current 029/030B runtime company begins with one known leader; a separate cap-full/overflow owner fixture remains pending rather than being falsely attributed to that case.

The owner still needs actual first/second crew visits and same Pawn continuity, large-company zero presence promotion, one anonymous capture while peers remain Pending, clean active/pre-RELEASE/post-RELEASE SAVE/LOAD, request-time construction behavior/opinion observations, real player-visible-map eligibility, source-qualified combat/relations on the active mod list, and practical runtime/save-size/save-load observations near **150 and 300 retained Pawns**. Synthetic headless registry timings and pointer fixtures do not establish Unity TPS, actual save bytes, crew hostility rates or owner gameplay PASS. No count above 150 fails merely for exceeding the soft region; no identities are removed to improve measurements.

### Exact deferred 3.2C scope

3.2B covers bounded group return, P0/strong identity promotion and the minimal one-held-plus-ordinary-return terminal batch. It does not intentionally build/validate the full A-returned/B-wounded/C-dead/D-captured/E-recruited/F-missing/G-combat-promoted/H-ephemeral matrix, its casualty/morale/succession/operation combinations or partial group extraction. Unknown combinations fail closed with references/reservations preserved. Independent early per-member identity/custody commit is also outside this slice. Ordinary Stored repeat visits do not resolve R-50 long-held suspension boundaries.

## Requested delivery checklist

| Item | Delivered result / reference |
|---|---|
| 1. Branch | `codex/phase32b-groups-concretization`. |
| 2. Base main | `e251c61efcbb7773f36e31e4862373e22930173e`. |
| 3. Commits | Stage table above; the final delivery commit is reported with the branch/PR handoff and is discoverable with `git log` (its own artifact-commit hash is available from `git log` after delivery). |
| 4. Files | `1.6/Assemblies/TheNetwork.dll`, `README.md`, `Source/TheNetwork/Core/NetworkWorldComponent.cs`, `Source/TheNetwork/Diagnostics/RuntimePhysicalTests/PhysicalGroupScenarios.cs`, `Source/TheNetwork/Diagnostics/RuntimePhysicalTests/PhysicalGroupTestRules.cs`, `Source/TheNetwork/Diagnostics/RuntimePhysicalTests/PhysicalTestDevActions.cs`, `Source/TheNetwork/Diagnostics/RuntimePhysicalTests/PhysicalTestModel.cs`, `Source/TheNetwork/Diagnostics/RuntimePhysicalTests/PhysicalTestRunner.cs`, `Source/TheNetwork/Diagnostics/RuntimePhysicalTests/PhysicalTestWorld.cs`, `Source/TheNetwork/Diagnostics/RuntimeTests/FakePhysicalWorldPort.cs`, `Source/TheNetwork/Domain/Contractors/CareerService.cs`, `Source/TheNetwork/Domain/Contractors/ContractorService.cs`, `Source/TheNetwork/Domain/Contractors/FateRules.cs`, `Source/TheNetwork/Domain/Contractors/UpkeepService.cs`, `Source/TheNetwork/Domain/Physical/ConcretizationEvidence.cs`, `Source/TheNetwork/Domain/Physical/EpisodeChecks.cs`, `Source/TheNetwork/Domain/Physical/EpisodeModel.cs`, `Source/TheNetwork/Domain/Physical/ObservationRules.cs`, `Source/TheNetwork/Domain/Physical/OrganizationComposition.cs`, `Source/TheNetwork/Domain/Physical/PhysicalLifecycleService.cs`, `Source/TheNetwork/Domain/Physical/PhysicalWorldPort.cs`, `Source/TheNetwork/Domain/Physical/ReconciliationApplier.cs`, `Source/TheNetwork/Domain/Physical/ReconciliationPlan.cs`, `Source/TheNetwork/Domain/Physical/ReconciliationPlanner.cs`, `Source/TheNetwork/Integration/Physical/ConcretizationEvidenceCollector.cs`, `Source/TheNetwork/Integration/Physical/PawnProjection.cs`, `Source/TheNetwork/Integration/Physical/RetainedPawnRegistry.cs`, `Source/TheNetwork/Integration/Physical/RimWorldPhysicalWorldPort.cs`, `Tests/TheNetwork.Tests/Fakes.cs`, `Tests/TheNetwork.Tests/Phase31Tests.cs`, `Tests/TheNetwork.Tests/Phase32bBoundedEpisodeTests.cs`, `Tests/TheNetwork.Tests/Phase32bCompositionTests.cs`, `Tests/TheNetwork.Tests/Phase32bEvidenceTests.cs`, `Tests/TheNetwork.Tests/Phase32bGroupPromotionTests.cs`, `Tests/TheNetwork.Tests/Phase32bGroupTests.cs`, `Tests/TheNetwork.Tests/Phase32bPromotionTests.cs`, `Tests/TheNetwork.Tests/Phase32bReservationTests.cs`, `Tests/TheNetwork.Tests/Phase32bRetentionScaleTests.cs`, `Tests/TheNetwork.Tests/Phase32bRuntimeQaTests.cs`, `Tests/TheNetwork.Tests/TestMain.cs`, `docs/DATA_MODEL.md`, `docs/DECISIONS.md`, `docs/IMPLEMENTATION_PHASES.md`, `docs/PHASE32B_READINESS_AUDIT.md`, `docs/PHASE32B_VALIDATION.md`, `docs/PHYSICAL_LIFECYCLE.md`, `docs/RISKS.md`, `docs/RUNTIME_TESTING.md`, `docs/spikes/README.md`, `docs/spikes/S26-team-cohesion.md`, `docs/spikes/S27-progressive-concretization-evidence.md`. |
| 5. Architecture | Composition → role-correct mission → Episode-owned Pawns → terminal-batch same-Pawn identities. |
| 6. Composition v1 | Immutable seed/capacity/specialties, ≤8 role weights, frozen v1 recipe. |
| 7. Role initialization | Full immutable origin cohort; write once; stable legacy fallback; no rank/succession/reload drift. |
| 8. Mission composition | Named matches first; matching healthy anonymous seats; ≤8; required refuse/optional shrink. |
| 9. Anonymous reservation | Durable Episode PawnRefs through incomplete RELEASE; same M1 registry, no dummy people. |
| 10. Load reservation | Early thing-id bridge then validated pointer indexes before first world tick. |
| 11. Added fields | `EpisodeMember.playerVisibleTick = -1`, `p0Eligible = false`. |
| 12. Save format | **5**, additive safe defaults, no composition schema/migration. |
| 13. P0 | Successful actual visible placement latch; test-map fixture injection is explicitly diagnostic. |
| 14. Size policy | 2–6 seats; 7–12 role-defining; 13+ strong only; existing pins persist. |
| 15. S1–S4 | Evidence table and conservative bounds above. |
| 16. PlayLog | No production scan; optional/supporting only. |
| 17. Six-seat overflow | Strong/held identity uses normal membership above six; no truncation/shadow store. |
| 18. Atomic rollback | Touched-set snapshot, store truncation/index, allocator and nested slot rollback; flag last. |
| 19. Conservation | Explicit aggregate/committed/named deltas and fault/retry tests above. |
| 20. Faction/Lord | One temporary shell per Episode, vanilla Visit behavior. |
| 21. Cohesion | Force-new, initial relations off, eligible request-time FixedIdeo + verification; no social rewriting. |
| 22. Runtime scenarios | 026–032 plus 030A/B/V and 032A/B/V exact menus in runtime § 19; owner run pending. |
| 23. Headless totals | **581 tests / 42,946 checks / 0 failures, exit 0**. |
| 24. Compiler totals | **0 warnings / 0 errors**. |
| 25. Source gates | **All nine gates PASS**. |
| 26. Repeat suite | **581 tests / 42,946 checks / 0 failures, exit 0**. |
| 27. DLL source | `5b403fe5fcc2f8e1d8e8e23d8f5c2f52264f0d8d`. |
| 28. DLL SHA-256 | `c8acac8534822dfa3e55521019c1379f2acd3fd3084266ad63739cfad7270adc`. |
| 29. 150/300 observations | Measured headless costs above; actual Unity/save-size observations pending. |
| 30. S26 | **PARTIAL**; no owner-runtime PASS. |
| 31. S27 | **PARTIAL**; production tests do not substitute for owner-runtime evidence. |
| 32. S11 | **FAIL / rescue STOPPED**. |
| 33. R-50 | **OPEN**, no aging fix. |
| 34. 3.2C | Full mixed-fate matrix/partial group extraction deferred as detailed above. |
| 35. Owner runtime | Explicit pending matrix above and runtime § 19. |
| 36. PR | **Branch pushed; no PR created:** `gh pr create` returned `Post https://api.github.com/graphql: Forbidden` (HTTP 403). Manual handoff below: title `Phase 3.2B: groups and progressive concretization`, head `codex/phase32b-groups-concretization`, base `main`. Native Git push succeeded, including artifact commit `0186390`; nothing was merged. Do not merge. |
| 37. Final status | **PHASE 3.2B IMPLEMENTED / HEADLESS VALIDATED — OWNER RUNTIME VALIDATION PENDING**. |


## Manual PR handoff after GitHub API HTTP 403

The concrete PR creation attempt failed with `Post https://api.github.com/graphql: Forbidden`. This was a GitHub API failure, not an automatic approval-review rejection. **No PR was created and nothing was merged.** Native Git successfully pushed `codex/phase32b-groups-concretization`, including artifact commit `0186390abae9c5d12cb258c88fa9562b4e7c6215`, and configured its upstream. A final documentation-only delivery commit records this handoff; the shipped DLL source remains `5b403fe`.

- Title: `Phase 3.2B: groups and progressive concretization`
- Head: `codex/phase32b-groups-concretization`
- Base: `main`
- Open manually: [GitHub compare / new PR](https://github.com/D3athAn63l/The-Network/compare/main...codex/phase32b-groups-concretization?expand=1).
- Exact body also available in the cloud workspace: `/workspace/.onboarding/phase32b-pr-body.md`.

Replace the PR description with the complete Markdown body below, preserving the pending owner-runtime scope. Do not merge.

```markdown
NPC organizations can now materialize bounded, role-correct groups and turn encountered anonymous members into persistent identities when the whole Episode reaches its atomic terminal commit. A captured anonymous member remains protected as the same Pawn while peers are still Pending; promotion then records that Pawn's actual name, operational role and organization without restoring a second abstract human.

This implements the accepted Phase 3.2B developer-triggered slice from merged PR #12, based on `main` at `e251c61efcbb7773f36e31e4862373e22930173e`.

- Derive immutable Composition v1 from origin facts, initialize operational roles once, and select matching named pins before healthy anonymous seats. Missions contain at most eight members.
- Extend the existing M1 registry with temporary ownership derived from unreleased Episode PawnRefs. Rebuild the durable thing-ID bridge before cross-reference resolution, then validate exact loaded Pawn bindings before the first world tick.
- Latch proven placement eligibility: all roles at 2–6 living members, non-Rifleman roles at 7–12, strong evidence only at 13+. The normal six-person target includes existing leaders and lieutenants; mandatory identity obligations can exceed it.
- Qualify bounded S1–S4 evidence and perform same-Pawn promotion inside the existing rollback-protected terminal commit. No production PlayLog scan or individual-identification producer is invented.
- Use one temporary encounter faction and a shared vanilla Visit Lord. Initial projections force new Pawns, disable initial relation generation and use eligible request-time FixedIdeo; existing physical and social history remains authoritative.
- Add armed disposable runtime scenarios `RT-PHYX-026–032`, including first/repeat visits, pending anonymous arrest, read-only save/load verification, Medic succession and real 150/300 retained-Pawn observation builders.

Validation:

- Two fresh final full-suite runs: **581 tests / 42,946 checks / 0 failures each**.
- Production and test builds: **0 warnings / 0 errors**, with warnings treated as errors; all **nine source gates pass**.
- Build uses the authoritative RimWorld 1.6 `Assembly-CSharp 1.6.9676.17735`. Save format remains **5**, with only additive `playerVisibleTick = -1` and `p0Eligible = false` defaults. No production Harmony reference.
- Shipped `1.6/Assemblies/TheNetwork.dll` matches the second tested binary: source `5b403fe5fcc2f8e1d8e8e23d8f5c2f52264f0d8d`, stamp `built 2026-10-06T10:10Z, source commit 5b403fe`, SHA-256 `c8acac8534822dfa3e55521019c1379f2acd3fd3084266ad63739cfad7270adc`, **1,235,456 bytes**.
- Real Scribe reference fixtures cover 150 named and 300 named + eight temporary bindings. XML sizes are **91,871 / 186,516 bytes**, a **94,645-byte fixture delta**. These measure NetworkState/reference-probe fixtures, not complete RimWorld save size or Unity TPS. Exact costs and delivery details are in [the validation report](docs/PHASE32B_VALIDATION.md).

Owner runtime acceptance remains pending. TestSite P0 is explicitly synthetic; genuine home/player-occupied-map visibility, active/pre-RELEASE/post-RELEASE game loads, above-six capture, construction/opinion behavior and real save/load/TPS measurements still need owner evidence. The current menus expose active and completed checkpoints; they do not freeze between COMMIT and RELEASE. See [runtime instructions](docs/RUNTIME_TESTING.md#19-phase-32b-groups-and-progressive-concretization).

S26/S27 and S21 remain **PARTIAL**, S11 remains **FAIL / rescue STOPPED**, R-50 remains **OPEN**, and O-20 stays locked. The full mixed-fate resolver and partial extraction remain Phase 3.2C; 3.3 and Phase 4 remain unimplemented. PR #11's accepted 3.2A runtime evidence is preserved.

**PHASE 3.2B IMPLEMENTED / HEADLESS VALIDATED — OWNER RUNTIME VALIDATION PENDING**
```
