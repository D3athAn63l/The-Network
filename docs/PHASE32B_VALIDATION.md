# Phase 3.2B implementation and validation

**PHASE 3.2B MERGED / HEADLESS VALIDATED / OWNER RUNTIME VALIDATED / INDEPENDENT FINAL AUDIT PASSED.**

This report describes the developer-triggered group slice delivered on `codex/phase32b-groups-concretization`, based on merged PR #12 `main` **`e251c61efcbb7773f36e31e4862373e22930173e`**. The owner accepted Composition v1, the promotion policy and the 3.2B boundary in [ADR-057](DECISIONS.md#adr-057--phase-32b-readiness-owner-decisions). The earlier [readiness audit](PHASE32B_READINESS_AUDIT.md) remains a historical design/source record.

Phase 3.2A remains **MERGED / HEADLESS VALIDATED / OWNER RUNTIME VALIDATED**: PR #11's final `e768fef` result remains 022 **41/0/0** → clean SAVE/LOAD → 025 **11/0/0**, total **52/0/0**. S26 and S27 remain **PARTIAL**; S21 remains **PARTIAL** for the previously unrun observation paths. S11 remains **FAIL / rescue STOPPED**, R-50 remains **OPEN**, O-20 remains **LOCKED**. Transient capture→escape-before-terminal and full 3.2C remain deferred; 3.3 and Phase 4 remain unimplemented.

## PR #13 final owner runtime acceptance

**2026-10-07 · owner-reported in-game evidence · documentation/PR metadata update only.** The owner's final runtime validation record covers the dedicated QA lab and the frozen wildlife-reset build below. **Phase 3.2B implemented runtime slice: owner runtime validation complete for the current Create/Reset, 026–032 scenario set, including 030 and 032 save/load. Owner runtime gate complete; independent final audit PASSED.** PR #13 is **CLOSED / MERGED**, accepted as [8986997afb9809bd66309684e7b01d3e58570292](https://github.com/D3athAn63l/The-Network/commit/8986997afb9809bd66309684e7b01d3e58570292). Its final pre-merge HEAD was `4b1af08fe80873a3f8c140a7aaada384b3c09de0`.

### Independent final audit and merge signoff

This post-merge record reflects the owner's completed independent final audit **after Test 6**. Its verdict was **APPROVED / MERGE READY**; no technical blocker remained for the accepted implemented slice. The owner subsequently merged PR #13, and GitHub confirms it is **CLOSED / MERGED**.

| Final audit item | Accepted evidence |
|---|---|
| Final pre-merge PR #13 HEAD | `4b1af08fe80873a3f8c140a7aaada384b3c09de0` |
| Final publication | `4d6ca01` → `4b1af08` was documentation-only; no source/test/DLL change after the frozen validated build. |
| Pre-merge technical readiness | GitHub reported mergeable; the independent audit found no branch-protection or required-status-check blocker and no technical blocker for this slice. |
| Validated source / DLL | Source `98ddc71f77920592b81845eb23c63f291f18843e`; DLL SHA-256 `610cb8feadef228e746bb43254ee78a87ff8071d69f2d4d756b91632894ca580`; exact stamp/version/bytes below unchanged. |
| Headless / strict compiler / source gates | Two complete **633 tests / 45,174 checks / 0 failures**, exit 0 each; **0 warnings / 0 errors**; all **nine source gates PASS**. Prior evidence, not new executions in this cleanup. |
| Save format / production Harmony | **5 / none**, unchanged. |
| Owner runtime acceptance | Dedicated Create/Reset/disposable non-humanlike cleanup and current **026–032**, including terminal 030A, active Pending 030B and ~150/~300 retained 032 real save/load; final 030V/032V correctly **WITHOUT ARMING**. |
| PR #13 merge commit | `8986997afb9809bd66309684e7b01d3e58570292` — Phase 3.2B accepted and merged. |

The documentation-only owner acceptance commit and final GitHub merge do **not** replace validated source or DLL provenance. This cleanup performs no source/test/DLL change, compilation, rebuild or runtime rerun. S11 **FAIL / rescue STOPPED**, R-50 **OPEN**, O-20 **LOCKED**, S21/S26/S27 **PARTIAL** and all broader/deferred scope remain unchanged; S26/S27 cover evidence beyond this accepted runtime suite.

### Frozen source and artifact

| Value | Validated build, unchanged by this documentation update |
|---|---|
| Validated source commit | `98ddc71f77920592b81845eb23c63f291f18843e` |
| Artifact/docs HEAD before the owner runtime acceptance record | `4d6ca01dfaf0e0d491072307065063e484618fcf` |
| DLL | `1.6/Assemblies/TheNetwork.dll` |
| Build stamp | `built 2026-10-07T10:38Z, source commit 98ddc71` |
| Informational version | `0.1.0+98ddc71f77920592b81845eb23c63f291f18843e` |
| SHA-256 | `610cb8feadef228e746bb43254ee78a87ff8071d69f2d4d756b91632894ca580` |
| Bytes | **1,291,776** |
| Save format / production Harmony | **5 / none** |

The owner acceptance documentation commit `4b1af08fe80873a3f8c140a7aaada384b3c09de0` and merge `8986997afb9809bd66309684e7b01d3e58570292` do not replace the validated source SHA or artifact provenance. This post-merge cleanup is documentation/PR metadata only. No production source, QA source, tests or DLL change; no compilation or rebuild. The existing two complete fresh-process headless passes remain **633 tests / 45,174 checks / 0 failures**, exit 0 each, with zero strict compiler warnings/errors and all nine source gates PASS. These are prior validated-build results, not new runs for this update. Detailed source/audit/build evidence is preserved in the historical wildlife-reset delivery below.

Pre-merge documentation verification (`4d6ca01` → `4b1af08`): `git diff --check` PASS; **732 local Markdown links checked / zero new link or anchor errors**. Two pre-existing repository-root links remain unchanged inside the preserved historical PR handoff. All prior validation delivery bodies, custody/removal audit bodies, readiness/spike audit bodies and Phase 3.1/3.2A acceptance appendices are preserved byte-for-byte. The diff from `4d6ca01` contains **13 Markdown documentation files only**; source/tests and DLL bytes, hash, stamp and informational version remain unchanged. No current-status contradiction or new runtime blocker was found.

### Final owner runtime matrix

Counts below are **PASS / FAIL / GAP** as reported by the owner. Checkpoint and verifier counts are separate reports and are not combined into a fabricated suite total. Each save/load case used an actual RimWorld SAVE → main menu → LOAD; read-only 030V and 032V ran **WITHOUT ARMING**.

| Scenario / checkpoint | Owner result | Proven scope |
|---|---|---|
| QA Lab Create | **PASS** | Explicit 60×60 dedicated TestSite creation. |
| QA Lab Reset / non-humanlike disposal | **PASS** | All 3,600 cells Concrete; 505 generated Things and 4 disposable non-humanlike Pawns removed; 0 protected Pawns removed; deterministic lab validated. Wildlife no longer blocked initialization. |
| 026 final-lab sanity | **54 / 0 / 0** | Ordinary small-crew visitor path on the final lab. |
| 027 | **128 / 0 / 0** | Same-Pawn crew continuity and role persistence. |
| 028 | **75 / 0 / 0** | Large-company ordinary presence without discretionary promotion. |
| 029 corrected custody/promote | **46 / 0 / 0** | Sustained real prisoner custody, then terminal same-Pawn promotion. |
| 030A SAVE checkpoint | **54 / 0 / 0** | Released/concretized group checkpoint. |
| 030A SAVE/LOAD → 030V | **42 / 0 / 0** | Durable **terminal** save/load truth; Episode already complete before verification. |
| 030B Pending-arrest SAVE checkpoint | **36 / 0 / 0** | Active anonymous-arrest checkpoint with Pending peers. |
| 030B SAVE/LOAD → 030V | **38 / 0 / 0** | Actual loaded active Pending state; 3 named + 3 anonymous subjects, **6/6** covered; continued into truthful same-Pawn promotion. |
| Later terminal 030V | **29 / 0 / 0** | Additional terminal confirmation after 030B continuation; **not another independent save/load case**. |
| 031 | **71 / 0 / 0** | Existing simple organizational succession scenario only. |
| 032A ~150 retained build/run | **1,936 / 0 / 0** | Retained identities around 150 in the owner's running game. |
| 032A SAVE/LOAD → 032V | **10 / 0 / 0** | 150 bound/healthy named subjects; **150/150** retained coverage; 0 anonymous Episode subjects; 0 physical findings; arm cleared. |
| 032B ~300 retained build/run | **1,936 / 0 / 0** | Retained identities around 300 in the owner's running game. |
| 032B SAVE/LOAD → 032V | **10 / 0 / 0** | 300 bound/healthy named subjects; **300/300** retained coverage; 0 anonymous Episode subjects; 0 physical findings; arm cleared. |

There is **no pending formal 032V rerun**. Humanlike/Network-bound reset protection remains unchanged; the successful live reset reported zero protected Pawns removed. Existing headless protected-refusal coverage remains part of the frozen validation, rather than being reclassified as a new adversarial live reset test.

### Custody, load and retention interpretation

**029:** the exact anonymous Rifleman was the same owned Pawn relocated once into the validated real prison, arrested once through vanilla `CapturedBy(Faction.OfPlayer)` and assigned a legitimate real prisoner bed. Actual prisoner custody survived both normal WatchPeriod intervals, approximately **500+ ordinary game ticks**, while peers remained Pending. No KnownCharacter was created early; the temporary Episode reservation remained continuous. Terminal batch promoted the exact same Pawn and ThingID, preserving mission role, organization/provenance and truthful custody. There was no duplicate human representation, reservation gap, binding integrity finding or Episode integrity finding.

**030A:** the loaded Episode was already complete before 030V began. The verifier confirmed arm clearing, durable Pawn ThingIDs, Character identities, roles, live bindings, Returned/Stored terminal state and retained coverage, with no phantom abstract or WorldPawn duplicates and zero binding/Episode findings. This proves **durable terminal save/load truth**, not an instantaneous active/pre-commit load frame or a committed-before-RELEASE checkpoint.

**030B:** the real loaded save still contained an incomplete Pending Episode: **3 named bound + 3 anonymous Episode-bound = 6/6 durable physical subjects covered**, zero binding/Episode findings. While incomplete, anonymous PawnRefs resolved to the same Pawns, temporary reservations persisted, anonymous peers had no dummy KnownCharacters and the captured identity bridge survived load. Ordinary production/vanilla continuation promoted the exact captured Pawn to retained named identity, preserving ThingID/live Pawn/role/organization/provenance, vanilla prisoner truth and custody/authority. Ordinary anonymous peers returned to abstract stock exactly once, without phantom Pawn copies or duplicate identity. The later **29/0/0** terminal verifier confirms that same continuation. This closes the current **active Pending load gate represented by 030B**.

**031:** **71/0/0** accepts the implemented simple leader-death/Medic-return succession path. It does not establish the full mixed-fate, morale, operation or long-held-aging matrix, nor supply an additional independent 031 save/load result.

**032:** fresh Test 6 supplied both actual retention loads and fully passing unarmed 032V reports. These timings are **owner-environment observations**, not universal benchmarks, Unity TPS measurements, full save sizes or load elapsed times:

| Owner probe | Observed time |
|---|---|
| 150 retained, after LOAD: 15,000 reservation lookups | approximately **0.542 ms** |
| 300 retained, before SAVE: registry rebuild | approximately **0.118 ms** |
| 300 retained, before SAVE: 30,000 reservation lookups | approximately **1.216 ms** |
| 300 retained, after LOAD: 30,000 reservation lookups | approximately **1.030 ms** |

### Chronology, superseded attempts and incidental observations

The earlier wilderness-edge **029 escape/FAIL** and later automatic-provisioning/debris refusal **before Plan/materialization** remain historical below. The final dedicated-lab **029 46/0/0** supersedes their outstanding custody/setup gates without rewriting those failures. Earlier 026–028 passes remain historical; the final-lab runs above are the current accepted results.

Historical **Test 4** read-only verifier attempts were **030V 37/1/0** and **032V 9/1/0**. In each, the sole failure was the physical-arm-cleared assertion because the owner manually pressed ARM after LOAD and before the read-only verifier. These are **owner workflow/protocol errors**, not persistence, lifecycle, registry, binding, save-corruption or production defects. Fresh **Test 5** supersedes the 030V failure; fresh **Test 6** supersedes the 032V failure. The historical attempts remain recorded here.

During Test 6 heavy-retention loads, general Kernel validation reported approximately **25 validation findings (25 repaired)** alongside Spatial contractor anchoring. Afterwards, retained coverage was complete (**150/150** or **300/300**), physical integrity findings were zero and 032V passed. The runtime record does not establish the exact category or root cause of those general Kernel repairs; adjacent Spatial messages do not establish attribution. No source correction or physical identity/registry defect is inferred.

Test 4 also contained one isolated **`InvalidOperationException: Stack empty`**, with frames involving `ThingOwnerUtility.GetAllThingsRecursively`, Gravship utility, `Map.IsPlayerHome`, `ForbidUtility` and path-grid/Unity job execution. No The Network frame appeared; subsequent Network validation remained healthy. Record this as an **isolated vanilla/Odyssey-side anomaly observed; not attributable to PR #13 on current evidence and not reproduced in the fresh validation runs**. No root cause is claimed and it is not a current Phase 3.2B blocker on this evidence.

### Remaining scope and independent audit

The implemented runtime slice now has owner evidence for dedicated disposable setup, bounded role-correct group materialization, same-Pawn visit continuity, anonymous sustained custody and terminal promotion, active Pending and terminal save/load, simple succession and retained coverage around 150/300. **S11 FAIL / rescue STOPPED; R-50 OPEN; O-20 LOCKED; S21/S26/S27 PARTIAL** remain. Transient capture→escape-before-terminal and full 3.2C are deferred; 3.3 and Phase 4 remain unimplemented. Genuine separately gated player-visible-map eligibility, cap-full/above-six runtime demonstration, committed-before-RELEASE saves, broader S26/S27 probes and full game-save/TPS/load measurements are not closed by this suite. No future physical procurement/handoff or player-contractor phase is claimed complete.

The independent final audit completed after owner Test 6 and found no technical blocker for the accepted Phase 3.2B implemented slice. Its verdict was **APPROVED / MERGE READY**; the owner subsequently merged PR #13. No current Phase 3.2B merge blocker remains. Broader/deferred evidence and implementation scope above remain unchanged.

**PHASE 3.2B MERGED / HEADLESS VALIDATED / OWNER RUNTIME VALIDATED / INDEPENDENT FINAL AUDIT PASSED.**

> **Historical deliveries follow.** Every delivery body below is preserved from pre-update HEAD `4d6ca01`. Former “current owner review” headings, pending/rerun instructions, API-denial observations and superseded artifact hashes describe their delivery dates. Current runtime acceptance is the section above; the wildlife-reset source/DLL remain the frozen validated build.

## PR #13 QA lab wildlife reset: current owner review record

The previous Reset refused initialization of a freshly generated disposable lab whenever ordinary wildlife existed. Source correction **`98ddc71f77920592b81845eb23c63f291f18843e`** permits only non-humanlike, non-Network-owned Pawns to be removed during the explicit destructive armed Reset. Humanlike or Network-owned/bound Pawns, including corpse/holder contents, still refuse before any map write. This continues existing PR #13 on `codex/phase32b-groups-concretization` from remote HEAD `c2c6e494d57866d54fa7b89d22f241214b3e26fb`; no new PR or merge.

### Owner policy, audit and implemented boundary

Incidental animals, insects and mechs are unnecessary for these tests. After the initial source-audit stop for an off-map mechanitor, the owner explicitly authorized deleting them with ordinary vanilla linked-entity cleanup in this disposable save. The [actual supplied-assembly audit](PHASE32B_CUSTODY_QA_AUDIT.md#disposable-non-humanlike-pawn-removal-audit) records why standalone Destroy can retain Pawns, why direct Discard is insufficient, and why detached explicit Discard mode avoids world retention. No off-map Pawn is directly selected or removed.

The read-only complete preflight retains exact unique TestSite/non-home/factionless/size/arm/run/physical-obligation guards and recursive corpse/holder traversal. `PawnBlocksReset` applies Humanlike OR Network-owned independently. `NetworkOwnsPawn` reads existing registry indexes, all KnownCharacter bindings and incomplete Episode bindings by actual pointer or durable ThingID; unresolved/inert ownership refuses. Approved Pawns are rechecked immediately before detachment, then passed through vanilla explicit Discard mode; success requires Discarded and absent from WorldPawns. Empty corpse/holder wrappers remain ordinary disposable content. Success reports removed disposable Pawns and zero protected Pawns; protected refusal reports counts. Existing non-Pawn callback/quest/assignment/explosion guards remain.

Only `QaLab.InitializeOrReset` gains this narrow disposal operation. 026–032 remain prepared-lab consumers and cannot remove wildlife. Create semantics, deterministic geometry, ordinary/prison rooms, 029/030B one-arrest/17-fact guard, terminal batch, M1, production observer/lifecycle/reservations and save format are unchanged. An unexpected callback failure can leave a partially reset map and stops for inspection; no transactional rollback is claimed.

### Validation and exact shipped artifact

| Check | Result |
|---|---|
| Final source commit | `98ddc71f77920592b81845eb23c63f291f18843e` |
| Git/API source proof | Published source tree `b7b3b3db0e63c9bc4f821a77bb36cc35bc0438b5` exactly equals the previously reviewed source/test tree; local HEAD is the byte-identical published Git commit. |
| Focused QA lab / Pawn policy | **17 tests / 809 checks / 0 failures** |
| Focused physical wiring/safety | **88 tests / 9,863 checks / 0 failures** (`Phys31`) |
| Focused Phase 3.2B | **112 tests / 5,371 checks / 0 failures** (`Phys32b`) |
| Focused 029/030B guards | **9 tests / 107 checks / 0 failures** (`CaptureGuard`); pending capture across wakes/real Scribe reload **1 / 27 / 0** |
| Full fresh-process pass 1 | **633 tests / 45,174 checks / 0 failures**, exit 0 |
| Full fresh-process pass 2 | **633 tests / 45,174 checks / 0 failures**, exit 0 |
| Strict production/test compilers | **0 warnings / 0 errors**, warnings as errors |
| Source gates / whitespace | **All nine shell gates PASS**; focused narrow Pawn-destruction gates PASS; `git diff --check` PASS |
| DLL path | `1.6/Assemblies/TheNetwork.dll`; same bytes as the test runner copy, no rebuild after provenance capture |
| Build stamp | `built 2026-10-07T10:38Z, source commit 98ddc71` |
| Informational version | `0.1.0+98ddc71f77920592b81845eb23c63f291f18843e` |
| SHA-256 | `610cb8feadef228e746bb43254ee78a87ff8071d69f2d4d756b91632894ca580` |
| Bytes | **1,291,776** |
| Save / production Harmony | **5**, no persistence/migration or production Harmony |
| Publication | Source `98ddc71` published on the existing PR #13 branch; this reviewed artifact/docs delivery is owner-authorized. PR remains draft/open/unmerged. |
| Owner runtime | **PENDING**; setup/disposal and corrected 029 have no owner PASS |

Focused filters overlap and are not summed. Full runs use fresh Mono 6.8 processes with the previously successful explicit environment AND command options: `MONO_ENV_OPTIONS=--optimize=-inline mono --optimize=-inline TheNetwork.Tests.exe`. Both published-source executions and their completion/exit evidence are recorded in `wildlife-published-full-results.json`. The same executable and DLL are used without a later rebuild. Only an exit-0 invocation with a complete test summary is accepted. Headless proof covers policy, actual RaceProperties/registry/bindings, real corpse census/detachment, source/write boundaries and fake lifecycle/real Scribe continuity; it does not execute Unity map reset, the full vanilla discard callbacks, real rooms/AI ticks or sustained live custody.

An earlier invocation using only the command-line inlining option exited 0 at the existing procurement soak, after `Compaction.TerminalContractsArchived`, without a completion summary or an assertion failure. It is **excluded** from pass totals; no root cause is claimed. Its log is `wildlife-final-full-1-incomplete.log`. The earlier local-source runs are recorded in `wildlife-final-full-1.log` and `wildlife-final-full-2.log`; fresh published-source runs are `wildlife-published-full-1.log` and `wildlife-published-full-2.log`. Both compilers use SDK 8.0.130 Roslyn, C# 7.3, optimized deterministic net472 and warnings as errors against the supplied game references; the repository build scripts are unchanged. Reflection confirms the actual build stamp, informational version, .NET 4.7.2 target and absence of Harmony assembly references. No test dependency is shipped.

Changed-document validation checked **84 local links** with **zero new link/anchor errors**. Two existing historical PR-description links already used repository-root `docs/` paths inside this docs file; they remain unchanged in the preserved historical record. Earlier validation from the dedicated-lab heading onward and all of PHYSICAL_LIFECYCLE are byte-for-byte unchanged from `c2c6e49`.

The source correction changes exactly two developer QA source files and three test files; production lifecycle files are byte-for-byte unchanged from `c2c6e49`. Seven new Pawn-policy tests complement the existing reset tests and narrow destruction-boundary gates. The source gates authorize only the approved explicit Reset disposal loop, not arbitrary QA Pawn destruction.

### Owner workflow and remaining evidence

Fresh disposable save → ARM → **PHYX — Create 60×60 Test Map** → ARM → **PHYX — Initialize / Reset QA Lab [DESTRUCTIVE]** → ARM → **RT-PHYX-029**. Generated animals/mechs no longer block initialization unless protected by Network ownership. If 029 passes, continue **030B SAVE → LOAD → 030V**; one 026 sanity run remains recommended before any merge. Previously accepted 026–028 PASS, old 029 escape/FAIL, failed automatic provisioning and all earlier validation/provenance remain historical below.

R-50 stays **OPEN**, O-20 **LOCKED**, S11 **FAIL / rescue STOPPED**, S21/S26/S27 **PARTIAL**. Transient capture→escape and full 3.2C remain deferred; 3.3 and Phase 4 remain unimplemented. **DO NOT MERGE.**

> The following dedicated-lab record is the **historical delivery** of source `6162748` / artifact HEAD `c2c6e49`. Its former ANY-Pawn refusal and no-linked-Pawn-mutation statements are superseded by the owner-authorized policy above. Its prior tests, artifact and owner findings remain intact.

## PR #13 dedicated QA lab: current owner review record

This dedicated-lab update continues [PR #13](https://github.com/D3athAn63l/The-Network/pull/13) on `codex/phase32b-groups-concretization`,
from pre-task HEAD **`e0d4a3a85bcfa1bc4bc2ec3a845a59df8afab71d`**. Final source/tests commit: **`6162748424d4bdb125e878812ed9a7765bc1b7cb`**.
The prior source `5548f0b`, its two **616 / 44,269 / 0** accepted runs and DLL `b929ad26…` remain historical below.

### Owner finding and resulting behavior

The owner found `Filth_RubbleRock`, `Filth_MachineBits`, `Filth_Ash`, plants, mechanoid slag chunks and factionless
`AncientShipBeacon` / `AncientMechDropBeacon` inside the generated Encounter-map footprint. The old automatic
`TestSite.Ensure` → `TestCompound.Ensure` correctly refused unexpected Things; 026/028/029 aborted **before PlanGroup/materialization**.
This identifies QA provisioning ownership, not a production lifecycle failure. Setup now belongs to two explicit Dev actions;
scenarios validate/use the prepared infrastructure and do not automatically create, clear, build or repair it.

Previous owner **026/027/028 PASS**, including 240-tick dwell, remains historical evidence. The earlier edge-arrest
029 escape/FAIL and rejected automatic setup are preserved; no PASS is claimed for the new lab or corrected 029.

### Explicit provisioning and actual lab

| Exact Dev Mode label | Effect |
|---|---|
| `PHYX — Create 60×60 Test Map [armed]` | One arm; create only the narrow WorldObject/map when absent and clear new-map fog. No fixtures/Pawns/Episodes or normalization. Reuse existing map without reset/resize; report WorldObject/map IDs, actual size and read-only lab validation. |
| `PHYX — Initialize / Reset QA Lab [DESTRUCTIVE] [armed]` | A separate arm; the sole full-map mutating setup path after complete safety preflight. |

Dev Mode and a current one-action PHYX arm are required. The read-only preflight requires the exact unique current TestSite, exactly plain `MapParent`, null parent faction, `IsPlayerHome == false`, unchanged narrow def (no home eligibility/comps/incident targets), approved 60×60 or 100×100 size, available enabled room/path services and expected vanilla layout definitions. No physical runner may be active; the Network must be running with resolved physical ownership. Associated incomplete Episodes block by target map, fallback tile/layer or a member’s `MapHeld`, including CLOSED but not fully released/followed-up/published Episodes. Retained bound physical obligations block by actual `MapHeld` or a deployed Episode link. Fully complete historical references alone do not block and are never erased. A deduplicated census combines recursive map/holder contents with `allowUnreal: true`, every cell’s Thing grid and `MapPawns.AllPawns`: **ANY Pawn**, including held/unspawned and corpse-contained Pawns, refuses reset. Unknown/unresolved Thing ownership, non-destroyable or quest-tagged Things, occupied/assigned beds or `CompAssignableToPawn` structures (including off-map owners), and `CompExplosive.Props.explodeOnDestroyed` and the callback classes `CompHasPawnSources`, `CompTreeConnection` and `CompObelisk_Abductor` also refuse before any map write: their Vanish callbacks can alter WorldPawns retention, off-map Pawns or another pocket map.

Only the explicit armed destructive Dev action calls `QaLab.InitializeOrReset(TestSite.Map, ...)`. After complete preflight and spending the one-action arm, remove all roofs first; destroy the snapshotted spawned **non-Pawn exact-map Things** through vanilla `Destroy(DestroyMode.Vanish)`; require no leftover/new Things before terrain or building writes; normalize every map cell to Concrete; spawn the deterministic ordinary vanilla structures and roof only the rooms; update real room/district state; then validate actual results. There is no debris DefName whitelist. Failed hard guards change nothing. Unexpected vanilla callbacks, leftover Things or setup exceptions **STOP for inspection** and can leave a partially normalized map; there is no automatic cleanup, callback-object deletion or transactional reset claim. Repeating Reset on a proven empty eligible lab may rebuild it deterministically, with a new arm and the same guards.

All **3,600 cells** of a new **60×60** map use vanilla **Concrete**; an approved legacy **100×100** map uses **10,000 cells** without resizing. Offsets are `(x, z)` from `map.Center`. The central **13×13 camp** spans **−6..+6** on both axes: one wooden North-facing `Table2x2c` at **(0, 4)** and four wooden stools at **(−1, 4), (2, 4), (0, 3), (0, 6)**. The west ordinary room has a **9×9** perimeter **x −23..−15 / z −4..+4**, a **7×7** interior, east wooden door **(−15, 0)** and one North-facing normal wooden bed **(−20, −1)**. The east prison retains its **9×9** perimeter **x +15..+23 / z −4..+4**, **7×7** interior, west wooden door **(+15, 0)** and two North-facing prisoner beds **(+18, −1), (+21, −1)**. The plan contains **62 granite walls, 2 wooden doors, 3 wooden beds, 1 wooden table and 4 wooden stools: 72 structures**. Constructed roof covers only the two 81-cell room footprints (**162 cells**); the remaining field is unroofed. The camp retains at least eight free staging cells and a route to the map edge.

`TestSite.GetPrepared` → read-only `GetExisting` → `QaLab.Validate`. Validation and its transitive helpers perform no construction, terrain/roof/faction writes, bed designation/assignment or repair. They inspect the exact map/scope/size, expected Things/footprints/materials/factions, every Concrete/roof cell, free field and camp capacity, ordinary normal-bed room, recognized prison beds sharing a proper non-edge prison room, closed player-owned prison door, and actual `NoPassClosedDoors` camp-to-edge / prison-no-edge reachability. `RoomCanBePrisonCell` supplies the proper/non-huge condition. Actual captive bed eligibility, `PawnCanOpen == false` and ByPawn no-edge reachability are checked in the unchanged capture helper. Prepared validation permits runtime subjects and benign extra Things that leave required layout/standability intact; all armed starts require it before fixture creation, Solo/group placements revalidate, and read-only 030/032 starts require it. Missing/invalid labs report the explicit Create or Reset instruction.

029/030B retain the same exact anonymous Episode/member/Pawn binding, one pre-capture owned relocation, one real `CapturedBy`, valid real prisoner-bed claim without eviction, immutable 17-fact fail-closed guard, **two WatchPeriod intervals (at least 500 ordinary game ticks)** while peers remain Pending, zero early Character/commit, terminal-batch same-Pawn promotion and named retention. The prior capture-helper source tail is unchanged apart from its `TestCompound` → `QaLab` API name; loaded 030V remains read-only. The lab setup itself proves no live custody survival. Transient capture→escape-before-batch stays unlatched and deferred to separate temporal-design review, likely 3.2C.

The [actual 1.6 custody/reset API audit](PHASE32B_CUSTODY_QA_AUDIT.md) separates supplied assembly predicates from pinned
XML definitions. The map remains factionless/non-home/no-comp/no-incident-target; ownership of individual lab buildings
by Player does not convert the parent to a colony. No colonist, warden, food/production/power system or persistent lab marker is added.

### Validation and exact shipped artifact

| Check | Current result |
|---|---|
| Source commit | `6162748424d4bdb125e878812ed9a7765bc1b7cb` |
| Focused | **QaLab 10 tests / 753 checks / 0 failures**; physical runtime wiring/safety **Phys31 88 / 9,847 / 0**; **Phys32b 112 / 5,359 / 0**; capture guards **9 / 107 / 0**; 030B pending capture with real Scribe reload **1 / 27 / 0**. These overlapping filters are not added together. |
| Fresh full pass 1 | **626 tests / 45,090 checks / 0 failures, exit 0** |
| Fresh full pass 2 | **626 tests / 45,090 checks / 0 failures, exit 0** |
| Compiler | **0 warnings / 0 errors**, strict production/tests, warnings as errors; final full-run evidence below. |
| Source gates | All **9 unchanged shell gates PASS**; existing narrow Pawn-safety source test updated for the explicit reset boundary. |
| Links/whitespace/history | **774** changed-document local links checked, **0** errors; `git diff --check` PASS; all **444,780 bytes** before Appendix M and **81,181 bytes** of prior validation bodies preserved byte-for-byte. All 37 current report rows present; production core unchanged and Source/tests frozen. |
| DLL | `1.6/Assemblies/TheNetwork.dll`, exact validated final run 2 output; Copied byte-for-byte from final full-run-2 output, also equal to production build output; no later rebuild. |
| Build stamp | `built 2026-10-07T08:49Z, source commit 6162748` |
| Informational version | `0.1.0+6162748424d4bdb125e878812ed9a7765bc1b7cb` |
| SHA-256 | `c408b863123b08f24c2f7ebdea21af09db0a1df23fdb884e4f23e1698ccccb67` |
| Bytes | **1,289,728** |
| Save / Harmony | **5**, no lab marker/registry/version/migration; no production Harmony. |
| Remote PR body | The remote body was stale at the owner’s last external audit. The latest authorized REST PATCH returned **Forbidden**; current remote content is unconfirmed and requires manual owner update. The complete local description and GitHub-ready body are available; this is not a code blocker |

Headless tests prove policy, deterministic geometry, source/API wiring, preflight/write ordering, transitive read-only infrastructure validation, no-Pawn destruction boundary and fake-world/real-Scribe custody continuity. They do not execute Unity generation/reset, map/holder callbacks, actual room/path/AI ticks or real 500-tick custody; **owner Create → Reset → 029 and 030 save/load remain required**.

Final source `6162748` → focused validation → full run 1 → fresh full run 2 → exact tested DLL → artifact/docs commit. All final invocations exited 0; both production and test builds had 0 warnings/errors with warnings as errors, and all 9 shell gates passed. No source/tests changed after final validation. Earlier local candidate validation was superseded when the callback audit added three narrow refusal guards; no candidate artifact was published.

Before publication, independent read-only review identified actual vanilla Vanish callbacks that affect WorldPawns retention sources, off-map dryads or a linked pocket map. The final reset preflight refuses `CompHasPawnSources`, `CompTreeConnection` and `CompObelisk_Abductor`; the narrow destruction-safety source checks assert these refusals before writes. No WorldPawns, off-map Pawn or other-map mutation is authorized.

The source/tests commit has **10 changed entries, 555 insertions / 158 deletions**, including `TestCompound.cs` → `QaLab.cs`, new `QaLabRules.cs`, `PhysicalTestDevActions.cs`, `PhysicalTestRunner.cs`, `PhysicalTestWorld.cs`, `PhysicalGroupScenarios.cs`, `Phase31Tests.cs`, `Phase32bCompoundCustodyTests.cs`, new `QaLabTests.cs` and `TestMain.cs`. The separate artifact/docs delivery updates `1.6/Assemblies/TheNetwork.dll`, `README.md`, `RUNTIME_TESTING.md`, `PHASE32B_VALIDATION.md`, `PHYSICAL_LIFECYCLE.md`, `PHASE32B_CUSTODY_QA_AUDIT.md` and `PHASE32B_PR_DESCRIPTION.md` (documentation under `docs/`). Core lifecycle, planning, projection, evidence, observer, reservation/M1, reconciliation/rollback, conservation, CareerService and ContractorService source remain unchanged.

### Requested 37-item dedicated-lab report

| Item | Evidence / result |
|---|---|
| 1. Source SHA | `6162748424d4bdb125e878812ed9a7765bc1b7cb` |
| 2. Final PR HEAD | Read after artifact/docs commit in task delivery / [existing PR #13 commits](https://github.com/D3athAn63l/The-Network/pull/13/commits); no self-hash invented. |
| 3. Files changed | The source/tests commit has **10 changed entries, 555 insertions / 158 deletions**, including `TestCompound.cs` → `QaLab.cs`, new `QaLabRules.cs`, `PhysicalTestDevActions.cs`, `PhysicalTestRunner.cs`, `PhysicalTestWorld.cs`, `PhysicalGroupScenarios.cs`, `Phase31Tests.cs`, `Phase32bCompoundCustodyTests.cs`, new `QaLabTests.cs` and `TestMain.cs`. The separate artifact/docs delivery updates `1.6/Assemblies/TheNetwork.dll`, `README.md`, `RUNTIME_TESTING.md`, `PHASE32B_VALIDATION.md`, `PHYSICAL_LIFECYCLE.md`, `PHASE32B_CUSTODY_QA_AUDIT.md` and `PHASE32B_PR_DESCRIPTION.md` (documentation under `docs/`). Core lifecycle, planning, projection, evidence, observer, reservation/M1, reconciliation/rollback, conservation, CareerService and ContractorService source remain unchanged. |
| 4. Dev action labels | `PHYX — Create 60×60 Test Map [armed]`; `PHYX — Initialize / Reset QA Lab [DESTRUCTIVE] [armed]`. |
| 5. Create semantics | Raw 60×60 WorldObject/map plus new-map fog only; no lab/fixtures/subjects. Existing actual IDs/size/validation reported, no duplicate/reset/resize. |
| 6. Reset semantics | Only the explicit armed destructive Dev action calls `QaLab.InitializeOrReset(TestSite.Map, ...)`. After complete preflight and spending the one-action arm, remove all roofs first; destroy the snapshotted spawned **non-Pawn exact-map Things** through vanilla `Destroy(DestroyMode.Vanish)`; require no leftover/new Things before terrain or building writes; normalize every map cell to Concrete; spawn the deterministic ordinary vanilla structures and roof only the rooms; update real room/district state; then validate actual results. There is no debris DefName whitelist. Failed hard guards change nothing. Unexpected vanilla callbacks, leftover Things or setup exceptions **STOP for inspection** and can leave a partially normalized map; there is no automatic cleanup, callback-object deletion or transactional reset claim. Repeating Reset on a proven empty eligible lab may rebuild it deterministically, with a new arm and the same guards. |
| 7. Complete hard guards | Dev Mode and a current one-action PHYX arm are required. The read-only preflight requires the exact unique current TestSite, exactly plain `MapParent`, null parent faction, `IsPlayerHome == false`, unchanged narrow def (no home eligibility/comps/incident targets), approved 60×60 or 100×100 size, available enabled room/path services and expected vanilla layout definitions. No physical runner may be active; the Network must be running with resolved physical ownership. Associated incomplete Episodes block by target map, fallback tile/layer or a member’s `MapHeld`, including CLOSED but not fully released/followed-up/published Episodes. Retained bound physical obligations block by actual `MapHeld` or a deployed Episode link. Fully complete historical references alone do not block and are never erased. A deduplicated census combines recursive map/holder contents with `allowUnreal: true`, every cell’s Thing grid and `MapPawns.AllPawns`: **ANY Pawn**, including held/unspawned and corpse-contained Pawns, refuses reset. Unknown/unresolved Thing ownership, non-destroyable or quest-tagged Things, occupied/assigned beds or `CompAssignableToPawn` structures (including off-map owners), and `CompExplosive.Props.explodeOnDestroyed` and the callback classes `CompHasPawnSources`, `CompTreeConnection` and `CompObelisk_Abductor` also refuse before any map write: their Vanish callbacks can alter WorldPawns retention, off-map Pawns or another pocket map. |
| 8. ANY Pawn refusal | Recursive holders (allowUnreal), full cell grid and AllPawns census; held/unspawned/corpse-contained Pawns block. No classification, movement, Pawn Destroy/Discard/Kill/PassToWorld or cleanup. |
| 9. Active ownership refusal | Target-map/member MapHeld/fallback tile-layer incomplete Episodes and retained map/deployed links block; unresolved ownership refuses. Complete historical references are preserved without blocking alone. |
| 10. Full-map clearing | Roofs first, one snapshotted exact-map non-Pawn Vanish loop, no debris whitelist; callback leftovers STOP before terrain/buildings. |
| 11. Floor | Vanilla Concrete on all 3,600 / legacy 10,000 cells. |
| 12. Dimensions/layout | All **3,600 cells** of a new **60×60** map use vanilla **Concrete**; an approved legacy **100×100** map uses **10,000 cells** without resizing. Offsets are `(x, z)` from `map.Center`. The central **13×13 camp** spans **−6..+6** on both axes: one wooden North-facing `Table2x2c` at **(0, 4)** and four wooden stools at **(−1, 4), (2, 4), (0, 3), (0, 6)**. The west ordinary room has a **9×9** perimeter **x −23..−15 / z −4..+4**, a **7×7** interior, east wooden door **(−15, 0)** and one North-facing normal wooden bed **(−20, −1)**. The east prison retains its **9×9** perimeter **x +15..+23 / z −4..+4**, **7×7** interior, west wooden door **(+15, 0)** and two North-facing prisoner beds **(+18, −1), (+21, −1)**. The plan contains **62 granite walls, 2 wooden doors, 3 wooden beds, 1 wooden table and 4 wooden stools: 72 structures**. Constructed roof covers only the two 81-cell room footprints (**162 cells**); the remaining field is unroofed. The camp retains at least eight free staging cells and a route to the map edge. |
| 13. Camp | 13×13; Table2x2c (0,4), four stools (−1,4)/(2,4)/(0,3)/(0,6); ≥8 free staging cells and edge path. |
| 14. Ordinary room | West 9×9 / 7×7 interior, east door (−15,0), one normal wooden North bed (−20,−1), separate proper non-prison room. |
| 15. Prison | East 9×9 / 7×7 interior, west door (+15,0), prisoner beds (+18,−1)/(+21,−1), constructed roof; two beds permit 029 then 030B without eviction. |
| 16. Vanilla prison facts | Proper non-edge/non-huge recognized IsPrisonCell with actual ForPrisoners beds; closed player-owned door, actual room/path facts; captive PawnCanOpen/ByPawn edge denial and IsValidBedFor checked at arrest/claim. |
| 17. Non-home/factionless | Exact plain MapParent, null faction, narrow no-home/no-comp/no-incident-target def and IsPlayerHome false; individual Player building ownership only. |
| 18. Scenario/setup separation | No scenario Create/Reset/Ensure/debris/terrain/roof/building provisioning; deliberate existing test-subject actions remain. |
| 19. Prepared path | `TestSite.GetPrepared` → read-only `GetExisting` → `QaLab.Validate`. Validation and its transitive helpers perform no construction, terrain/roof/faction writes, bed designation/assignment or repair. They inspect the exact map/scope/size, expected Things/footprints/materials/factions, every Concrete/roof cell, free field and camp capacity, ordinary normal-bed room, recognized prison beds sharing a proper non-edge prison room, closed player-owned prison door, and actual `NoPassClosedDoors` camp-to-edge / prison-no-edge reachability. `RoomCanBePrisonCell` supplies the proper/non-huge condition. Actual captive bed eligibility, `PawnCanOpen == false` and ByPawn no-edge reachability are checked in the unchanged capture helper. Prepared validation permits runtime subjects and benign extra Things that leave required layout/standability intact; all armed starts require it before fixture creation, Solo/group placements revalidate, and read-only 030/032 starts require it. Missing/invalid labs report the explicit Create or Reset instruction. |
| 20. 026–032 fail clearly | All armed starts validate before fixture creation; placements revalidate; read-only 030/032 validate. Missing/invalid lab supplies explicit Create/Reset instructions. |
| 21. Custody architecture | 029/030B retain the same exact anonymous Episode/member/Pawn binding, one pre-capture owned relocation, one real `CapturedBy`, valid real prisoner-bed claim without eviction, immutable 17-fact fail-closed guard, **two WatchPeriod intervals (at least 500 ordinary game ticks)** while peers remain Pending, zero early Character/commit, terminal-batch same-Pawn promotion and named retention. The prior capture-helper source tail is unchanged apart from its `TestCompound` → `QaLab` API name; loaded 030V remains read-only. The lab setup itself proves no live custody survival. Transient capture→escape-before-batch stays unlatched and deferred to separate temporal-design review, likely 3.2C. |
| 22. Focused | **QaLab 10 tests / 753 checks / 0 failures**; physical runtime wiring/safety **Phys31 88 / 9,847 / 0**; **Phys32b 112 / 5,359 / 0**; capture guards **9 / 107 / 0**; 030B pending capture with real Scribe reload **1 / 27 / 0**. These overlapping filters are not added together. |
| 23. Full pass 1 | **626 tests / 45,090 checks / 0 failures, exit 0** |
| 24. Full pass 2 | **626 tests / 45,090 checks / 0 failures, exit 0** |
| 25. Compiler | Strict production/tests **0 warnings / 0 errors**, warnings as errors. |
| 26. Gates | All **9 unchanged shell gates PASS**; no broad QaLab exemption in the existing destructive-Pawn safety test. |
| 27. Save | **5**, no new persisted QA state. |
| 28. Harmony | No production Harmony or AI patch. |
| 29. Build stamp | `built 2026-10-07T08:49Z, source commit 6162748` |
| 30. Informational version | `0.1.0+6162748424d4bdb125e878812ed9a7765bc1b7cb` |
| 31. SHA-256 | `c408b863123b08f24c2f7ebdea21af09db0a1df23fdb884e4f23e1698ccccb67` |
| 32. DLL bytes | **1,289,728** |
| 33. PR body | The remote body was stale at the owner’s last external audit. The latest authorized REST PATCH returned **Forbidden**; current remote content is unconfirmed and requires manual owner update. The complete local description and GitHub-ready body are available; this is not a code blocker |
| 34. Owner runtime | Historical 026–028 PASS; owner debris caused pre-Plan refusal of automatic setup. New explicit lab/029 has no owner PASS. |
| 35. Corrected 029 | **OWNER CREATE → RESET → RT-PHYX-029 REQUIRED**; 026–028 not prerequisites; 030 follows 029 PASS. |
| 36. Open/deferred status | S11 **FAIL / rescue STOPPED**, R-50 **OPEN**, O-20 **LOCKED**, S21/S26/S27 **PARTIAL**; transient capture→escape review and full 3.2C/3.3/Phase 4 remain deferred/unimplemented. |
| 37. Final verdict | **PR #13 DEDICATED QA LAB COMPLETE — OWNER CREATE → RESET → RT-PHYX-029 REQUIRED** |

Use a **fresh disposable save**: ARM → **Create 60×60 Test Map**; inspect the raw map if desired; ARM again → **Initialize / Reset QA Lab [DESTRUCTIVE]**; ARM → **RT-PHYX-029**. 026–028 are not prerequisites for reaching 029. If 029 passes, continue to 030 save/load. Before final merge, one 026 sanity run on the final lab is recommended because the physical environment changed; repeat the full 026–028 sequence only if that check or later evidence reveals a shared visitor problem. **DO NOT MERGE.**

PR-body copy/review artifact: [PHASE32B_PR_DESCRIPTION](PHASE32B_PR_DESCRIPTION.md); GitHub-ready root-relative body:
`/workspace/.onboarding/pr13-lab-pr-body.md`. Latest authorized REST PATCH failed: `Patch https://api.github.com/repos/D3athAn63l/The-Network/pulls/13: Forbidden`; log `/workspace/.onboarding/pr13-lab-pr-body-update.log`. This API denial was not an automatic approval-review or sandbox rejection. The failed request establishes no successful body update; the current local body is ready for manual application to existing PR #13.

<a id="pr-13-custody-qa-correction-current-owner-review-record"></a>

## Prior PR #13 custody QA correction (historical)

> Historical 34-item delivery from source `5548f0b` and pre-task HEAD `e0d4a3a`. Automatic bounded TestCompound provisioning is superseded by the explicit QA lab above. Its source, validation, artifact and owner-pending claims describe that delivery; the full body is preserved.



This continues existing [PR #13](https://github.com/D3athAn63l/The-Network/pull/13) on `codex/phase32b-groups-concretization`, from **`be3088cefd496e453c9e60d67a87fea9444faac2`**. The previous validated source `bd59bd0`, two **602 / 43,431 / 0** passes and DLL hash `758be092…` now form the historical runtime-correction record below. No new PR or merge.

### Accepted owner result and 029 failure

The owner reports **026 PASS / 027 PASS / 028 PASS / 029 FAIL** on the previous corrected DLL, including the accepted **240 ordinary-game-tick dwell** in 026 and the first 027. These passes remain accepted; the new compound/custody fixture has no owner PASS yet.

029 created three anonymous Riflemen and used real `CapturedBy(Faction.OfPlayer)`. Immediately afterward the same Pawn was a colony prisoner with its Episode binding/temporary reservation, Pending peers, no early Character and conserved humans. About three ticks later vanilla announced escape. At terminal batch around 500 ticks it was WorldFree; all three members truthfully Returned, no captured person was promoted and nine final assertions followed that single lost-custody prerequisite. This is a custody-fixture failure, not evidence that atomic promotion broke.

### Actual vanilla source audit and narrow correction

The [custody QA audit](PHASE32B_CUSTODY_QA_AUDIT.md) identifies the concrete supplied 1.6 assembly and separately pinned Humanlike.xml evidence. `JobGiver_PrisonerEscape` can issue an exit job for a secure player-hosted prisoner in an edge-connected district or an open-door/doorless route to an edge. `CapturedBy` changes actual guest/custody/Lord state but does not create/transport to a prison. Repository placement supplies a more specific cause: `RimWorldPhysicalWorldPort.Place` selects an edge cell and spawns each group Pawn there; the QA runner proceeds from placement to arrest without a game-tick wait. The old captive therefore remained in the edge district at arrest, directly satisfying the audited `district.TouchesMapEdge` predicate. This repository timing evidence is separate from the supplied DLL predicate; the cloud audit did not inspect the owner's saved district independently.

A real proper non-huge room with actual ForPrisoners beds and a **closed player-owned door** blocks that immediate ordinary open-route escape. Factionless doors can be human-openable and are insufficient. Audited non-home wandering/food paths respect ordinary reachability/closed doors. No fundamental home-map, player parent, fake colonist or warden requirement was found; the existing factionless, no-comp/no-incident-target TestSite remains non-home. Ordinary random prison breaks remain possible and are reported truthfully, never suppressed.

### TestSite / compound as built

Fresh TestSites are **60×60**. Coordinates are offsets from map center: open **13×13 soil courtyard** at x/z **−6..+6**; east **9×9 granite wall ring** x **+15..+23**, z **−4..+4**, enclosing a **7×7** prison interior. One west-wall **wooden door** is at **(+15, 0)**; two real **wooden North-facing 1×2 beds** are anchored **(+18, −1)** and **(+21, −1)**. Constructed roof covers the prison. The cell is outside the visitor chill radius 12. Existing valid 100×100 TestSites are reused, never resized beneath Pawns; logs report actual size.

`TestCompound.Ensure` accepts only the exact current dedicated TestSite with its narrow def, plain factionless parent and false IsPlayerHome. It validates the full bounds/Pawn/foreign-Thing footprint before bounded natural clearing, soil/roof and real structure spawning. A valid existing compound returns from read-only Validate before any write, preserving walls/doors/beds and their owners. Real prisoner designation is followed by actual region/district/room updates; require ProperRoom, !IsHuge, RoomCanBePrisonCell, IsPrisonCell, no edge-touching and the closed player-owned door. Actual NoPassClosedDoors edge reachability must hold for the courtyard and fail for the cell. Bed footprints allow vanilla PassThroughOnly; free cells must be Standable. Door autoorientation is respected; bed North orientation remains checked. No occupied bed owner is evicted.

Shared `GroupCaptureRun.ArrestAnonymous` calls `TestCompound.TryPreparePrisoner(e, member, pawn, out bed, out report)` to verify the exact owned anonymous slot and cell/available bed. It performs ONE owned `Position` + `Notify_Teleported` relocation before ONE real `CapturedBy(Faction.OfPlayer)`, then `TryClaimPrisonerBed` requires actual prisoner/IsValidBedFor and uses vanilla ClaimBedIfNonMedical without evicting any other bed owner. Read-only GuardCapture checks follow immediately; there are no relocation/repair/recapture calls after arrest.

The immutable 17-fact guard checks before returning Wait, on each relevant frame and after the existing reconciliation call: exact active-owned Episode/member/PawnRef/Pawn/ThingID/role/map, live spawned captive, actual IsPrisonerOfColony AND observed HeldByPlayer/PlayerPrisoner, continuous temporary reservation, anonymous member, unchanged CharacterStore/commit count and conserved headcount, Open/uncommitted Episode and an ordinary Pending peer. The first invalid fact irreversibly latches one failure with Pawn/binding/observation/role/map/reservation/Episode/tick/elapsed diagnostics. Peer-exit, wait and final steps abort instead of cascading. After deliberate ordinary peer exit only that peer condition is relaxed; after the atomic batch, physical custody checks permit the expected named handoff through RELEASE. 030B checks again before its SAVE pause. Loaded 030V uses a read-only exact saved company-slot-0 Rifleman custody/binding guard while incomplete; it requires no current armed run or artificial Pending peer and permits natural peer exits. Nothing repairs, relocates or recaptures after arrest.

029 retains its existing **two WatchPeriod intervals = at least 500 game ticks** custody window; no generic 240-tick dwell is copied to it. Terminal assertions still require exactly one same-Pawn Captured/OutOfCustody/PlayerPrisoner identity, actual name/organization/opRole, real colony-prisoner status, physical authority, named M1 handoff, correct Character routing/removed Episode tag, custody watch, conserved humans, zero gaps/findings. 030B uses the same arrest/pending fixture and still pauses for owner SAVE→LOAD→read-only 030V.

**Transient S1 custody before terminal batch is not currently latched as durable promotion evidence; 029 intentionally proves sustained custody through terminal batch. Capture→escape-before-batch requires separate design review, likely Phase 3.2C.** Core identity/evidence/promotion/reconciliation/observer/watch/rollback/reservation policies are unchanged. No fake held state, timer manipulation, recapture loop, prisoner-AI freeze/patch, Harmony, home conversion, colony mutation, named tier/schema or extra persistent compound registry is added.

### Current validation and artifact

| Check | Current result |
|---|---|
| Final source | `5548f0b5471614929a76a853dd53b82d8e0119a6` |
| Focused compound/custody/030B | **112 tests / 5,363 checks / 0 failures** (`Phys32b`); same-executable filters: Compound **5 / 552 / 0**, CaptureGuard **9 / 107 / 0**, 030B real Scribe reload **1 / 27 / 0**, Pawn-safety scan **1 / 65 / 0** |
| Full fresh-process pass 1 | **616 tests / 44,269 checks / 0 failures, exit 0** |
| Full fresh-process pass 2 | **616 tests / 44,269 checks / 0 failures, exit 0** |
| Compiler | **0 warnings / 0 errors** in strict production and test builds for each accepted full run; warnings as errors |
| Source gates | **PASS**, all 9 source gates in each accepted full run |
| Markdown/whitespace/history | **773 changed-document local links / 0 errors; `git diff --check` PASS**. Appendices H–L (**72,517 bytes**) and all prior validation bodies (**58,282 bytes**) are raw-byte-equal to pre-correction `be3088ce`; Source/tests are unchanged after `5548f0b` |
| DLL path | `1.6/Assemblies/TheNetwork.dll`; exact final validated output, no later rebuild. |
| Build stamp | `built 2026-10-06T13:02Z, source commit 5548f0b` |
| Informational version | `0.1.0+5548f0b5471614929a76a853dd53b82d8e0119a6` |
| SHA-256 | `b929ad26f82d500bc8e74924a0143e6bb1e756e961c323c07ebe22ed7257b502` |
| Bytes | **1,279,488** |
| Save / Harmony | **5**, no new persisted compound state/migration; no production Harmony. |
| Remote PR description | Current remote PR-description content is **UNVERIFIED**: the current GET returned Forbidden, and the latest authorized REST PATCH with the complete body also returned Forbidden. The owner may have manually updated the body; neither failed request establishes its current content. The final local body is ready to copy |
| Owner corrected custody | **PENDING** corrected 029, then 030B SAVE/LOAD→030V. |

The 14 new geometry/custody registrations passed in the final amended-source focused filters above. These prove deterministic geometry, refusal/reuse/API wiring, immutable pending-custody policy/failure latch and the fake-lifecycle escape-before-batch boundary; they do not execute Unity room/path/escape AI or establish actual 500-tick custody.

- `Phys32b.Compound_DeterministicGeometryFitsSmallMap`
- `Phys32b.Compound_VisitorCellsStayOutsidePrison`
- `Phys32b.Compound_RealDoorClosesWallBoundaryAndBedsStayInside`
- `Phys32b.Compound_ConstructionRefusesOrdinaryColonyMaps`
- `Phys32b.Compound_EnsureReusesValidStructuresAndVerifiesVanillaPrison`
- `Phys32b.CaptureGuard_EveryPendingPrerequisiteMustHold`
- `Phys32b.CaptureGuard_PeerExitRelaxesOnlyPeerPresence`
- `Phys32b.CaptureGuard_ActualAndObservedCustodyMustAgree`
- `Phys32b.CaptureGuard_NamedTerminalHandoffKeepsPhysicalCustodyRequired`
- `Phys32b.CaptureGuard_FailureCannotBeErasedByLaterRecovery`
- `Phys32b.CaptureGuard_FactsAndRulesNeverMutateOrRepair`
- `Phys32b.CaptureGuard_EscapeBeforePeersResolveFailsWithoutEarlyIdentity`
- `Phys32b.CaptureGuard_RuntimeChecksBeforeWaitAndAfterReconcile`
- `Phys32b.CaptureGuard_029And030BShareOneLegitimateArrest`

Preliminary source-text matcher failure: one new test treated “before CapturedBy” in a diagnostic as a call; requiring an invocation parenthesis fixed that matcher, without a production behavior change.

The superseded candidate source `550d1cd` passed focused 112 / 5,363 / 0, but its full run was rejected at **616 tests / 44,264 checks / 1 failure**: the existing `Phys31.Scan_NoDestroyOrDiscardOfBoundPawns` blanket Destroy scan also rejected explicitly authorized natural-obstacle clearing. The existing scan was narrowed only for TestCompound’s one preflighted unowned/untagged plant/natural-rock/chunk set, retaining refusal of every Pawn, foreign Thing, footprint crossing and other/home map. No production source behavior changed for this test correction. That full run is not a pass or shipped-artifact evidence; its log is `/workspace/.onboarding/pr13-custody-final1.log`.

Final amended-source focused evidence above passed at `/workspace/.onboarding/pr13-custody-focused-final.log`; an additional completed full invocation against `5548f0b` passed **616 / 44,269 / 0, exit 0** (`/workspace/.onboarding/pr13-custody-final-full1.log`). The next invocation aborted in Mono’s native runtime with **SIGSEGV / exit 134** during existing `Soak.Career_TwentyGameYearsHundredContractors` → `ReportCareerSize` → `PersistenceTests.SaveState` → `NetworkState.ExposeStores` → `DeliverObjective.ExposeData` → `Verse.Scribe_Values.Look<bool>(ref balancePaid)`; no completed totals or PASS are claimed (`/workspace/.onboarding/pr13-custody-final-full2.log`). That code/test is unchanged by this task; the observed context does not establish a root cause. The same aborted-run executable passed isolated career soak **1 test / 41 checks / 0 failures, exit 0** (`/workspace/.onboarding/pr13-custody-crash-isolation.log`). No assertion failed before the native abort; the implicated core/soak/test code is unchanged and the physical tests had not yet run. This points outside the custody QA path without identifying the exact native root cause.

Both accepted fresh complete runs passed **616 tests / 44,269 checks / 0 failures, exit 0**, without a Source/test change, in `/workspace/.onboarding/pr13-custody-accepted1.log` and `pr13-custody-accepted2.log`. Each strictly built production/tests with **0 warnings / 0 errors**, warnings as errors, and all **9 source gates PASS**. The shipped DLL is the exact accepted-run-2 tested output `/workspace/.onboarding/pr13-custody-accepted2.W3exJG/TheNetwork.dll`, compared byte-for-byte with the external mod copy and tracked artifact: **PASS**. No later rebuild occurred. These measured totals are not inferred from old counts or the additional first completed invocation.

**15 changed files** against pre-correction `be3088ce`: source/tests commit `5548f0b` contains eight files (949 insertions / 21 deletions): four shipped QA source files (`PhysicalGroupScenarios.cs`, `PhysicalGroupTestRules.cs`, `PhysicalTestWorld.cs`, new `TestCompound.cs`) and four test files (`Phase31Tests.cs`, new `Phase32bCompoundCustodyTests.cs`, `Phase32bRuntimeQaTests.cs`, `TestMain.cs`). The separate artifact/docs delivery contains `1.6/Assemblies/TheNetwork.dll`, `README.md`, `docs/PHASE32B_VALIDATION.md`, `docs/PHYSICAL_LIFECYCLE.md`, `docs/RUNTIME_TESTING.md`, `docs/PHASE32B_PR_DESCRIPTION.md`, and new `docs/PHASE32B_CUSTODY_QA_AUDIT.md`. Core lifecycle/observer/adapter source is unchanged.

The latest authorized REST PATCH of existing PR #13 using the complete body failed: `Patch https://api.github.com/repos/D3athAn63l/The-Network/pulls/13: Forbidden` (`/workspace/.onboarding/pr13-custody-pr-update.log`). The earlier current GET also failed (`/workspace/.onboarding/pr13-custody-pr-read.log`). The remote body remains **UNVERIFIED**; possible owner updates are not disproved. Prior 403 records below describe historical attempts only. The [current local description](PHASE32B_PR_DESCRIPTION.md) and GitHub-ready `/workspace/.onboarding/pr13-custody-pr-body.md` provide complete review/copy text. This API denial was not an automatic approval-review rejection.

### Requested 34-item custody QA report

| Item | Evidence / result |
|---|---|
| 1. Immediate escape reason | Audited player-prisoner edge/open-route escape predicate; no enclosed holding area. Repository edge-cell placement and same-frame arrest directly satisfy `district.TouchesMapEdge`; owner escape followed around three ticks later. Saved owner district was not independently inspected. |
| 2. Audited methods | Exact CapturedBy/guest/escape/room/bed/door/Map/MapParent/think-tree/wander/food/prison-break methods and evidence provenance in the audit above. |
| 3. Home required? | **No fundamental home/colonist/warden requirement found** in audited paths; preserve existing non-home parent semantics. |
| 4. Bed/room required? | Real proper non-huge prison room with ForPrisoners bed and closed player-owned door; verify actual room/bed predicates, not a Network flag. |
| 5. TestSite size | New **60×60**; reuse valid old 100 maps without resizing; report actual generated/loaded size. |
| 6. Compound geometry | Fresh TestSites are **60×60**. Coordinates are offsets from map center: open **13×13 soil courtyard** at x/z **−6..+6**; east **9×9 granite wall ring** x **+15..+23**, z **−4..+4**, enclosing a **7×7** prison interior. One west-wall **wooden door** is at **(+15, 0)**; two real **wooden North-facing 1×2 beds** are anchored **(+18, −1)** and **(+21, −1)**. Constructed roof covers the prison. The cell is outside the visitor chill radius 12. Existing valid 100×100 TestSites are reused, never resized beneath Pawns; logs report actual size. |
| 7. Vanilla Things | Real vanilla granite walls, wooden door, two wooden North-facing prisoner beds, Soil terrain and Constructed roof; no optional colony/furniture/food/warden infrastructure. |
| 8. Prison validation | `TestCompound.Ensure` accepts only the exact current dedicated TestSite with its narrow def, plain factionless parent and false IsPlayerHome. It validates the full bounds/Pawn/foreign-Thing footprint before bounded natural clearing, soil/roof and real structure spawning. A valid existing compound returns from read-only Validate before any write, preserving walls/doors/beds and their owners. Real prisoner designation is followed by actual region/district/room updates; require ProperRoom, !IsHuge, RoomCanBePrisonCell, IsPrisonCell, no edge-touching and the closed player-owned door. Actual NoPassClosedDoors edge reachability must hold for the courtyard and fail for the cell. Bed footprints allow vanilla PassThroughOnly; free cells must be Standable. Door autoorientation is respected; bed North orientation remains checked. No occupied bed owner is evicted. |
| 9. Idempotence | Preserve valid existing structures and occupied beds; no duplicates or unjustified replacement. Focused evidence: **112 tests / 5,363 checks / 0 failures** (`Phys32b`); same-executable filters: Compound **5 / 552 / 0**, CaptureGuard **9 / 107 / 0**, 030B real Scribe reload **1 / 27 / 0**, Pawn-safety scan **1 / 65 / 0** |
| 10. Target refusal | Exact current non-home TestSite only; ordinary/player-home/other maps and invalid defs refuse before construction. No colony/sentinel weakening. |
| 11. 029 setup | Shared `GroupCaptureRun.ArrestAnonymous` calls `TestCompound.TryPreparePrisoner(e, member, pawn, out bed, out report)` to verify the exact owned anonymous slot and cell/available bed. It performs ONE owned `Position` + `Notify_Teleported` relocation before ONE real `CapturedBy(Faction.OfPlayer)`, then `TryClaimPrisonerBed` requires actual prisoner/IsValidBedFor and uses vanilla ClaimBedIfNonMedical without evicting any other bed owner. Read-only GuardCapture checks follow immediately; there are no relocation/repair/recapture calls after arrest. |
| 12. Pending checks | The immutable 17-fact guard checks before returning Wait, on each relevant frame and after the existing reconciliation call: exact active-owned Episode/member/PawnRef/Pawn/ThingID/role/map, live spawned captive, actual IsPrisonerOfColony AND observed HeldByPlayer/PlayerPrisoner, continuous temporary reservation, anonymous member, unchanged CharacterStore/commit count and conserved headcount, Open/uncommitted Episode and an ordinary Pending peer. The first invalid fact irreversibly latches one failure with Pawn/binding/observation/role/map/reservation/Episode/tick/elapsed diagnostics. Peer-exit, wait and final steps abort instead of cascading. After deliberate ordinary peer exit only that peer condition is relaxed; after the atomic batch, physical custody checks permit the expected named handoff through RELEASE. 030B checks again before its SAVE pause. Loaded 030V uses a read-only exact saved company-slot-0 Rifleman custody/binding guard while incomplete; it requires no current armed run or artificial Pending peer and permits natural peer exits. Nothing repairs, relocates or recaptures after arrest. |
| 13. Sustained custody proof | Headless checks prove policy/correct checkpoints and held-fact lifecycle behavior only; actual vanilla custody across 500 ticks remains owner 029 evidence. |
| 14. No early person | Still-anonymous bound member, unchanged CharacterStore/commit count and humans through Pending; no dummy identity. |
| 15. Atomic architecture | Terminal batch/production collector/observer/reconciliation/rollback/watch/reservation source unchanged. |
| 16. Shared 030B | Same GroupCaptureRun / TestCompound helpers; second available bed preserves earlier 029 captive; owner SAVE→LOAD→030V remains required. |
| 17. Transient capture | No durable early S1 latch; capture→escape-before-batch deferred for separate review, likely 3.2C. |
| 18. Focused totals | **112 tests / 5,363 checks / 0 failures** (`Phys32b`); same-executable filters: Compound **5 / 552 / 0**, CaptureGuard **9 / 107 / 0**, 030B real Scribe reload **1 / 27 / 0**, Pawn-safety scan **1 / 65 / 0** |
| 19. Full pass 1 | **616 tests / 44,269 checks / 0 failures, exit 0** |
| 20. Full pass 2 | **616 tests / 44,269 checks / 0 failures, exit 0** |
| 21. Compiler | **0 warnings / 0 errors** in strict production and test builds for each accepted full run; warnings as errors |
| 22. Source gates | **PASS**, all 9 source gates in each accepted full run |
| 23. Save format | **5**; vanilla Things/guest/bed state use ordinary save behavior, no Network compound state/migration. |
| 24. Harmony | None added; no escape patch/global suppression. |
| 25. Source SHA | `5548f0b5471614929a76a853dd53b82d8e0119a6` |
| 26. PR HEAD | Final artifact/docs HEAD is read after commit in task delivery/[PR13 commits](https://github.com/D3athAn63l/The-Network/pull/13/commits); no self-hash invented. |
| 27. Build stamp | `built 2026-10-06T13:02Z, source commit 5548f0b` |
| 28. Informational version | `0.1.0+5548f0b5471614929a76a853dd53b82d8e0119a6` |
| 29. DLL SHA-256 | `b929ad26f82d500bc8e74924a0143e6bb1e756e961c323c07ebe22ed7257b502` |
| 30. DLL bytes | **1,279,488** |
| 31. Owner status | **026 PASS / 027 PASS / 028 PASS; prior 029 FAIL** due unstable wilderness custody; corrected 029 **RERUN REQUIRED**, no new custody PASS. |
| 32. Need 026 rerun? | Preserve accepted **026–028 PASS**. Run corrected **029 first on a fresh TestSite/disposable fixture**, then continue to **030 if PASS**. No mandatory 026 rerun: the prison lies outside the unchanged radius-12 visitor chill area, the central courtyard retains its edge route, and adapter/Lord/chill behavior is unchanged. A real shared visitor-behavior issue would require a targeted sanity rerun; headless checks do not prove the 60-square map in Unity. |
| 33. Open/deferred status | S11 **FAIL / rescue STOPPED**, R-50 **OPEN**, O-20 **LOCKED**, S21/S26/S27 **PARTIAL**; full 3.2C/3.3 / Phase 4 unimplemented. |
| 34. Final verdict | **PR #13 CUSTODY QA FIX COMPLETE — OWNER RT-PHYX-029 RERUN REQUIRED** |

**DO NOT MERGE.** Preserve accepted **026–028 PASS**. Run corrected **029 first on a fresh TestSite/disposable fixture**, then continue to **030 if PASS**. No mandatory 026 rerun: the prison lies outside the unchanged radius-12 visitor chill area, the central courtyard retains its edge route, and adapter/Lord/chill behavior is unchanged. A real shared visitor-behavior issue would require a targeted sanity rerun; headless checks do not prove the 60-square map in Unity.

## Prior PR #13 rematerialization/dwell correction (historical)

> Historical 30-item delivery from `bd59bd0`/`be3088c`, before the owner accepted 026–028 and reported 029 escape. Its required 026 → 027 rerun was subsequently satisfied. Prior failed PR-description writes were recorded then; they do not establish the current remote body after possible owner updates.

This updates the existing [PR #13](https://github.com/D3athAn63l/The-Network/pull/13) on `codex/phase32b-groups-concretization`, from pre-correction HEAD **`f204edf5488ea2c4d181cf53ad7e9b0f287fe7fb`**. The prior Detached/custom-name correction's source `809e835`, two **586 / 43,106 / 0** runs and DLL hash `8ef464…` are historical below. The original `5b403fe`/581-test delivery also remains historical. Neither artifact validates the current source changes.

### Owner result and required next run

- **RT-PHYX-026 — owner runtime PASS**, from the previous DLL run.
- **RT-PHYX-027 — continuity behavior passed, but runtime scenario FAIL due to invalid retained-Pawn current-capability assertion; correction pending rerun.** Four assertions failed. The previous concretized Pawns were reused, Medic/Rifleman continuity held, only two previously abstract seats generated Pawns, and the full five-person repeat reused all Pawns with zero replacement projections. Human count stayed conserved, Episodes completed and no reservation gap or unrelated identity duplication was observed.
- On the corrected DLL, use a **fresh disposable fixture and rerun 026 → 027 before proceeding to 028**. The changed assertions, capability calculation and new dwell have no corrected owner PASS yet. No result is invented for 028+.

### Retained physical truth and anonymous tier capability

The old group runtime assertion verified every placed Pawn against `RoleRules.SpecFor(member.seatRole, ContractorService.Experience(a))`. This correctly tests a first-created candidate, but a stricter current band cannot revoke an already-real person's durable `opRole`. The corrected runtime captures preexisting exact bindings and first-creation RoleSpecs before Materialize. An initially unbound known leader still requires first creation. Retained checks establish expected CharacterId, exact Pawn/durable binding, unchanged operational role, zero replacement generation and healthy reservation without reapplying skill floors or sanitizing history. First creation still uses and verifies the captured creation RoleSpec; it is not weakened. Retained skill/passions and concrete history are compared immediately across Materialize; ordinary later dwell ticks may naturally add skill XP, memories or social history.

The production `VeteranShare` ratio previously counted anonymous healthy+wounded tiers and omitted `org.committed`. Checking out the fixture's remaining Regulars therefore made its generic population appear empty, triggering the existing **0.5** fallback and a transient Green→Seasoned band jump at fixture skill **0.3**. The correction includes committed tier counts in the generic denominator and Veteran numerator. Healthy ↔ committed ↔ same-tier return preserves ratio and ExperienceBand. A genuine casualty/tier-population change may still change capability, and the **0.5 genuinely-empty fallback is preserved**. No KnownCharacter tier provenance, persistence field or migration is added. Anonymous→named promotion may still alter abstract capability/strength under the accepted policy.

### Bounded real-map dwell and its evidence limit

`GroupQaRules.MaterializationDwellTicks = 240` defines one stable game-tick interval. `DwellGroup` captures the exact Episode, map, encounter faction and shared Visit Lord. Per-frame protection and read-only `ObserveDwell` feed immutable `GroupDwellFacts` to `EvaluateDwell(startTick, nowTick, facts)`, which returns Wait until 240 ticks, Complete only with every invariant intact, or Invalid for null/negative/regressing time or any broken fact even at the completion boundary. Only 026 and the first 027 use this existing runner step/wait path; it does not manually advance TickManager, sleep/freeze AI or teleport Pawns.

026 dwells after immediate placement checks and before ExitPeers. The first 027 rematerialization dwells before exit, preserving all remembered identities and validating new candidates only at first creation; its final full-five-person no-regeneration repeat stays fast. The scope excludes 028+; 029 and 030B already exercise sustained custody/Pending state.

The checkpoint reads owned live Pawns, same expected map/spawn/faction/shared Visit Lord with the exact owned member count, active unreleased Episode, correct slot/role/binding, real registry reservation, no unexpected Free state or reservation gap, no HostFaction/prisoner/slave transition, no unrelated adoption and zero new KnownCharacters/projections merely from elapsed ticks. Named registry coverage must resolve the exact CharacterId, Deployed custody and same Episode under physical authority; anonymous coverage must be temporary with no invented KnownCharacter. 027 additionally preserves remembered CharacterIds/Pawns/roles without role re-projection. Normal movement/jobs/social behavior is allowed. No busy tick loop, sleep, freeze, teleport or Pawn repair is used. Failure diagnostics retain CharacterId/anonymous slot, ThingID, role, spawn/map/faction/HostFaction/prisoner/slave state, reservation, Episode/release state and elapsed ticks; failed fixtures are preserved.

Headless regressions cover wait/deadline state and failure on invalid ownership/reservation. They do **not** prove real vanilla map ticks, Lord/job/social behavior or corrected owner runtime PASS. Those remain the owner's rerun evidence.

### Current validation and exact artifact

| Check | Result |
|---|---|
| Production source SHA | `bd59bd0a57704597feefead598721151a6e42758` |
| Focused regressions | **98 tests / 4,699 checks / 0 failures**; **16 new tests / 253 checks** (VeteranShare **6 / 122 / 0**, retained-role/dwell **10 / 131 / 0**) |
| Full fresh-process run 1 | **602 tests / 43,431 checks / 0 failures, exit 0** |
| Full fresh-process run 2 | **602 tests / 43,431 checks / 0 failures, exit 0** |
| Production/test compiler | **0 warnings / 0 errors** in production and tests in each run, warnings treated as errors |
| Repository gates | **All nine source gates PASS** in each final run |
| Markdown/whitespace/history | **764 changed-document local links / 0 errors; `git diff --check` PASS**. Appendices H–L are raw-byte-identical to pre-correction `f204edf`; Source/tests are unchanged after `bd59bd0`. |
| Shipped artifact | `1.6/Assemblies/TheNetwork.dll`, exact tested final run 2 output, byte-for-byte comparison **PASS**; no later rebuild |
| Embedded build stamp | `built 2026-10-06T11:49Z, source commit bd59bd0` |
| Informational version | `0.1.0+bd59bd0a57704597feefead598721151a6e42758` |
| DLL SHA-256 | `758be09235ef6e879e5a02eca1d395df703aafa9d3da94fe26e8e402f749d81b` |
| DLL bytes | **1,248,768** |
| Save format / Harmony | **5**, no migration/new field; no production Harmony. Final gates confirm. |
| Existing PR description | **NOT UPDATED — GitHub API HTTP 403.** `gh pr edit 13` returned `Post https://api.github.com/graphql: Forbidden`; REST PATCH with the same exact body returned `Patch https://api.github.com/repos/D3athAn63l/The-Network/pulls/13: Forbidden`. Final concrete copy body is available. |
| Owner corrected runtime | **PENDING** fresh 026 → 027, including actual live dwell. |

Focused log: `/workspace/.onboarding/pr13-runtime-focused.log`. The strict focused run passed **98 tests / 4,699 checks / 0 failures**, including **16 new tests / 253 checks**, with **0 production/test warnings/errors**, warnings treated as errors and all **nine source gates PASS**. Final committed-source full passes/artifact provenance are separate evidence below.

`Phase32bVeteranShareTests` contributes **6 / 122 / 0**: `Phys32b.VeteranShare_MixedWoundedPartialAndFullCheckoutReturn`, `Phys32b.VeteranShare_AllRegularCommittedKeepsGreen`, `Phys32b.VeteranShare_AllMixedCommittedUsesActualTierRatio`, `Phys32b.VeteranShare_ServiceCheckoutReturnKeepsCapability`, `Phys32b.VeteranShare_ActualVeteranLossChangesCapability`, and `Phys32b.VeteranShare_GenuinelyEmptyKeepsExistingFallback`. They exercise available wounded stock plus committed TierCount.healthy values, real matching-tier checkout/return, the skill-0.3 all-Regular Green invariant and genuine Veteran loss changing ratio/band.

`Phase32bRematerializationDwellTests` contributes **10 / 131 / 0**: `Phys32b.Dwell_RetainedClassificationNeedsExistingExactBinding`, `Phys32b.Dwell_StricterCurrentBandCannotRevokeRetainedRole`, `Phys32b.Dwell_FirstCreationStillUsesOriginalRoleSpec`, `Phys32b.Dwell_RetainedContinuityRejectsEachMissingFact`, `Phys32b.Dwell_LifecycleReloadAndStricterBandReuseSameKnownPawn`, `Phys32b.Dwell_OnlyCompletesAfter240OrdinaryGameTicks`, `Phys32b.Dwell_BrokenInvariantAbortsEvenAtCompletionBoundary`, `Phys32b.Dwell_FactsAndEvaluationAreReadOnly`, `Phys32b.Dwell_RuntimeCreationAndRetainedChecksStaySeparate`, and `Phys32b.Dwell_Only026AndFirst027WaitBeforeVanillaExit`. The lifecycle test uses the existing fake-world physical token/binding and real Scribe save/load; pure fact/time and source-wiring checks catch current-band revalidation or missing dwell. These are not actual vanilla map/Lord/social tick results.

Both authoritative fresh full runs against unchanged committed source `bd59bd0` passed **602 tests / 43,431 checks / 0 failures, exit 0**. Production/test compilation in each run had **0 warnings / 0 errors** with warnings treated as errors; all **nine unchanged repository source gates PASS**. The shipped DLL is the exact tested final run 2 output, compared byte-for-byte and not rebuilt afterward. These measured full totals are not inferred from the old suite plus focused checks; existing soaks can produce different dynamic check totals. Logs: `/workspace/.onboarding/pr13-runtime-final1.log` and `/workspace/.onboarding/pr13-runtime-final2.log`.

An earlier full invocation aborted in Mono's native runtime with **SIGSEGV / exit 134** while ordinary bool Scribe serialization was occurring in the existing 300-contractor soak. No assertion failed before the abort. An isolated rerun with the **same executable** passed that soak (**1 test / 40 checks / 0 failures**); fresh complete runs then passed with **no Source/test/environment change**. The observed context does not establish a root cause. The aborted invocation is **not** counted as a pass or substituted for either completed fresh full run. Retained logs: `/workspace/.onboarding/pr13-runtime-aborted-full.log` and `/workspace/.onboarding/pr13-runtime-crash-repro.log`.

### Requested 30-item runtime correction report

| Item | Evidence / result |
|---|---|
| 1. Production source SHA | `bd59bd0a57704597feefead598721151a6e42758` |
| 2. Final PR HEAD | Read after artifact/docs delivery in the task's final response and [PR #13 commits](https://github.com/D3athAn63l/The-Network/pull/13/commits); source/artifact provenance above is explicit. No new PR or merge. |
| 3. Changed files | **12 files** against `f204edf5488ea2c4d181cf53ad7e9b0f287fe7fb`: source/tests `Source/TheNetwork/Diagnostics/RuntimePhysicalTests/PhysicalGroupScenarios.cs`, `Source/TheNetwork/Diagnostics/RuntimePhysicalTests/PhysicalGroupTestRules.cs`, `Source/TheNetwork/Domain/Contractors/ContractorService.cs`, `Tests/TheNetwork.Tests/TestMain.cs`, `Tests/TheNetwork.Tests/Phase32bRematerializationDwellTests.cs`, `Tests/TheNetwork.Tests/Phase32bVeteranShareTests.cs`; artifact/docs `1.6/Assemblies/TheNetwork.dll`, `README.md`, `docs/PHASE32B_VALIDATION.md`, `docs/PHYSICAL_LIFECYCLE.md`, `docs/RUNTIME_TESTING.md`, `docs/PHASE32B_PR_DESCRIPTION.md`. |
| 4. 027 false negative | Four invalid retained-Pawn current-band RoleRules assertions, compounded by omitted committed tiers temporarily triggering VeteranShare's empty fallback. Identity/rematerialization itself worked. |
| 5. Retained role validation | Expected CharacterId, same Pawn/durable binding/opRole and reservation; no current skill-floor re-admission, replacement, re-projection or history sanitation. |
| 6. First-generation validity | New candidates retain their actual creation RoleSpec verification; retained identities do not bypass first creation. |
| 7. VeteranShare defect | Healthy+wounded-only population omitted committed anonymous tiers, falsely using 0.5 after checkout. |
| 8. VeteranShare correction | Include committed count in generic population and Veteran numerator; empty anonymous population still falls back to 0.5. |
| 9. Checkout/return invariance | **98 tests / 4,699 checks / 0 failures**; **16 new tests / 253 checks** (VeteranShare **6 / 122 / 0**, retained-role/dwell **10 / 131 / 0**); exact ratio/band assertions and true population-change negatives are in the focused evidence above. |
| 10. No named-tier schema | No KnownCharacter tier field/provenance; accepted promotion representation semantics unchanged. |
| 11. Dwell interval | **240 ordinary game ticks**, using existing step/wait runner; real ticks remain owner evidence. |
| 12. 026 dwell | Before ExitPeers: exact live bound owned TestSite Pawns/map/faction/Lord, Episode/role/registry, no Free gap or new identities. |
| 13. 027 dwell | Same first-rematerialization checkpoint plus remembered CharacterId/Pawn/opRole and zero replacement; new-seat first-generation checks preserved; final repeat stays fast. |
| 14. Focused tests | **98 tests / 4,699 checks / 0 failures**; **16 new tests / 253 checks** (VeteranShare **6 / 122 / 0**, retained-role/dwell **10 / 131 / 0**) |
| 15. Full suite run 1 | **602 tests / 43,431 checks / 0 failures, exit 0** |
| 16. Full suite run 2 | **602 tests / 43,431 checks / 0 failures, exit 0** |
| 17. Warnings/errors | **0 warnings / 0 errors** in production and tests in each run, warnings treated as errors |
| 18. Source gates | **All nine source gates PASS** in each final run |
| 19. Save format | **5**, no bump/migration/new persisted fields. |
| 20. Harmony | No production dependency/reference added. |
| 21. DLL build stamp | `built 2026-10-06T11:49Z, source commit bd59bd0` |
| 22. Informational version | `0.1.0+bd59bd0a57704597feefead598721151a6e42758` |
| 23. DLL SHA-256 | `758be09235ef6e879e5a02eca1d395df703aafa9d3da94fe26e8e402f749d81b` |
| 24. DLL bytes | **1,248,768** |
| 25. PR description | **NOT UPDATED — API HTTP 403** on both GraphQL and REST attempts. The final body is retained in [PHASE32B_PR_DESCRIPTION](PHASE32B_PR_DESCRIPTION.md) for repository review; the exact GitHub-ready body with PR-root-relative links is `/workspace/.onboarding/phase32b-pr-body.md`. No new PR or merge. |
| 26. Owner runtime | Previous 026 **PASS**; previous 027 continuity passed but scenario **FAIL** on four invalid assertions; corrected fresh **026 → 027 rerun REQUIRED before 028**, no dwell PASS claimed. |
| 27. R-50 | **OPEN**; O-20 stays **LOCKED** and S21/S26/S27 stay **PARTIAL**. |
| 28. S11 | **FAIL / rescue STOPPED**. |
| 29. Deferred phases | Full **3.2C / 3.3 / Phase 4 NOT IMPLEMENTED**. |
| 30. Final status | **PR #13 RUNTIME CORRECTION COMPLETE — OWNER 026 → 027 RERUN REQUIRED** |

**DO NOT MERGE.** Prior custom-name/Detached fixes, Composition v1, seats/6–12/P0/strong evidence, terminal atomic promotion, reservation, conservation, cohesion, encounter factions and Lord architecture remain unchanged.

PR-description update logs: `/workspace/.onboarding/pr13-runtime-pr-description-update.log` and `/workspace/.onboarding/pr13-runtime-pr-description-rest-update.log`. Both requests failed through the GitHub API; this was not an automatic approval-review rejection. The description remains stale until the final copy body is applied to existing PR #13.

## Prior PR #13 surgical correction (historical)

> Historical headless record from the previous surgical delivery, before the owner 026/027 run. Its source, counts, artifact and pending-owner statement refer to that delivery.

The owner opened [PR #13](https://github.com/D3athAn63l/The-Network/pull/13) for the delivered branch. Its pre-correction HEAD is **`83328c57d7c11d6746e4b88ffda47fa659f568e2`**. This correction updates that existing PR; no new PR is requested and nothing is to be merged. The original source `5b403fe`, two **581 / 42,946 / 0** passes and DLL hash `c8acac…` below remain historical evidence. They do not validate the changed production source.

### The two corrections

**Completed anonymous Detached history.** The existing authoritative `PhysicalLifecycleService.Complete` clearing rule is extended to include `MemberOutcome.Detached`, alongside Returned, Killed, Lost and NeverPlaced. It applies only to anonymous operational-role slots after successful RELEASE. `AnonymousBack` has already restored the aggregate human; COMPLETE forgets the slot's PawnRef after routing-only release. The real vanilla Pawn is neither destroyed nor discarded. Named historical bindings and continuing named identity/custody remain intact. No second cleanup mechanism or anonymous roster is added.

The focused lifecycle regression proves exactly-once healthy/committed accounting, zero invented KnownCharacters, successful routing-only RELEASE, preservation of the actual Pawn, no early binding clear, completed `member.pawn == null`, idempotent retry and no temporary ownership reconstructed by registry rebuild/Resume. It uses a real Verse Pawn/PawnRef and actual M1 callbacks, early-ID rebuild, pointer resolution and Resume; world observation and strip/faction-release fault injection use the existing fake port. The actual Pawn's Destroyed/Discarded/dead flags and Name stay unchanged, and only the Episode routing tag is stripped. This is headless lifecycle evidence, not an owner map/world-tick result.

`EpisodeChecks.Report` also reports stale released anonymous operational-role PawnRefs for those five forgettable outcomes. Its narrow finding excludes named bindings, legacy nonoperational slots, pending RELEASE and unlisted continuing outcomes. The diagnostic never clears a reference or changes Pawn, stock or identity state; malformed saves are reported without repair.

**Actual custom name display.** `RimWorldPhysicalWorldPort.ExistingPawnName` retains exact `NameTriple` first/nick/last/display fields. Every other `Verse.Name` subtype with a nonblank actual `ToStringFull`, including NameSingle and a modded subtype, yields a display-only `NameSnapshot` with null first/nick/last. There is no string splitting, translated-text parsing, replacement generation or mutation of `Pawn.Name`. Null/blank display still supplies no name evidence. The focused adapter-to-commit regression proves that positive mandatory S1 custody promotes the same Pawn with the exact custom display, while evidence collection and PLAN remain free of CharacterStore/allocator mutations. Existing null/mismatched/unresolved/foreign-binding guards remain.

The existing adapter regression's old `UnknownName`-returns-null assertion is replaced with exact display/null-structure assertions because this brief explicitly changes that subtype behavior. NameTriple, NameSingle, null name, thing-ID mismatch, unresolved binding and foreign Episode/member checks are retained; no intentional ownership invariant is weakened.

### Focused regression evidence

| Checkpoint | Result |
|---|---|
| `Phys32b.Name_CustomDisplaySupportsMandatorySamePawnPromotion` and `Phys32b.Name_CustomBlankProvidesNoNameEvidence` | **2 tests / 44 checks / 0 failures**; strict **0 warnings / 0 errors**, all **nine source gates PASS**. |
| Existing `Phys32b.Promotion_ActualAdapterReadsExactExistingPawnName` | **1 test / 21 checks / 0 failures** with the requested custom-display assertion. |
| `Phys32b.Detached_ReferenceForgottenOnlyAfterReleaseComplete`, `Phys32b.Detached_NamedAndLegacyNonOperationalReferencesRemain`, `Phys32b.Detached_ValidatorReportsStaleHistoryWithoutRepair` | **3 tests / 115 checks / 0 failures**. |
| Combined affected `Phys32b` coverage | **82 tests / 4,446 checks / 0 failures**; strict **0 warnings / 0 errors**, all **nine source gates PASS**. |

The five new regressions account for **159 checks**. Checkpoint logs: `/workspace/.onboarding/pr13-names-focused.log`, `/workspace/.onboarding/pr13-existing-name-focused.log`, `/workspace/.onboarding/pr13-detached-focused.log` and `/workspace/.onboarding/pr13-both-focused.log`. These focused results precede the final source commit; the final committed-source full-suite and artifact evidence follows.

### Current source, validation and artifact

The final corrected production source and tests are committed as **`809e83556981a82309786c326b2d5eedba63749d`**. Both fresh complete-suite runs passed against that unchanged source. The exact validated **final run 2** DLL is shipped, with byte-for-byte equality to its tested output; it was not rebuilt after provenance was recorded. Save format remains **5**; no new field, migration, production Harmony, runtime menu or unrelated architecture change is part of this cleanup.

| Surgical check | Current result |
|---|---|
| Production source commit | `809e83556981a82309786c326b2d5eedba63749d`. |
| New focused regressions | Five new tests **5 / 159 / 0**; combined affected coverage **82 / 4,446 / 0** above. |
| Full headless suite, run 1 | **586 tests / 43,106 checks / 0 failures, exit 0**, fresh process against `809e835`. |
| Full headless suite, run 2 | **586 tests / 43,106 checks / 0 failures, exit 0**, second fresh process against `809e835`. |
| Production/test compiler | Each run: **0 warnings / 0 errors**, warnings treated as errors. |
| Source gates | Each run: **all nine gates PASS**. |
| Markdown/whitespace | **760 changed-document local links / 0 errors; `git diff --check` PASS**. Appendices H–L remain byte-identical to pre-correction HEAD. |
| Corrected shipped DLL | `1.6/Assemblies/TheNetwork.dll`, exact tested final run 2 output, byte-for-byte comparison PASS. |
| Embedded build stamp | `built 2026-10-06T10:59Z, source commit 809e835`. |
| Informational version | `0.1.0+809e83556981a82309786c326b2d5eedba63749d`. |
| SHA-256 | `8ef464be121f0fabb26eb98a811a16071c6e57d3f9f2949a4df1560362b82ff2`. |
| DLL bytes | **1,235,968**. |
| Owner RimWorld runtime | **PENDING**, unchanged; no owner in-game PASS claimed. |

Final logs: `/workspace/.onboarding/pr13-final1.log` and `/workspace/.onboarding/pr13-final2.log`. Each script ran in a fresh process against committed source `809e835`, including strict production/test compilation and all nine unchanged repository source gates. All five new regressions passed in both complete suites. No test-invariant conflict or new test failure was encountered during this correction. Compilation uses the same RimWorld 1.6 reference and .NET Framework 4.7.2 as the original delivery.

### Requested 27-item surgical report

| Item | Correction evidence / result |
|---|---|
| 1. Final PR HEAD | The final artifact/docs HEAD is recorded in this task's delivery response and the [existing PR #13 commit list](https://github.com/D3athAn63l/The-Network/pull/13/commits), on `codex/phase32b-groups-concretization`. This report is part of that delivery; its own commit hash is read after commit. Pre-correction HEAD: `83328c57d7c11d6746e4b88ffda47fa659f568e2`. |
| 2. DLL production source | `809e83556981a82309786c326b2d5eedba63749d`. Original `5b403fe` evidence below is historical. |
| 3. Exact changed files | **12 files** against pre-correction HEAD. Source/tests: `Source/TheNetwork/Domain/Physical/PhysicalLifecycleService.cs`, `Source/TheNetwork/Domain/Physical/EpisodeChecks.cs`, `Source/TheNetwork/Integration/Physical/RimWorldPhysicalWorldPort.cs`, `Tests/TheNetwork.Tests/Phase32bDetachedCleanupTests.cs`, `Tests/TheNetwork.Tests/Phase32bCustomPawnNameTests.cs`, `Tests/TheNetwork.Tests/Phase32bPromotionTests.cs`, `Tests/TheNetwork.Tests/TestMain.cs`. Artifact/docs: `1.6/Assemblies/TheNetwork.dll`, `README.md`, `docs/PHASE32B_VALIDATION.md`, `docs/PHYSICAL_LIFECYCLE.md`, `docs/RUNTIME_TESTING.md`. |
| 4. Detached correction | Include Detached in the existing COMPLETE anonymous operational-role binding clear, after successful RELEASE. |
| 5. Actual Pawn untouched | Real Pawn remains not Destroyed/Discarded/dead, same Name and external PawnRef; only Episode routing is stripped. No normalize/pass/named-retain action. Focused Detached **3 / 115 / 0**. |
| 6. Exactly-once stock | One checkout takes healthy **4→3**, committed **0→1**; `AnonymousBack` restores **4/0** once. Failure/retry/repeated completion adds no second restoration, identity, commit or successful routing strip. |
| 7. Completed history | Strip/faction-release faults retain the PawnRef and temporary protection. Successful COMPLETE sets `member.pawn = null`; this completed anonymous operational Detached slot has no PawnRef. Named/legacy historical bindings remain preserved. |
| 8. No ownership resurrection | Actual temporary index becomes empty, RebuildEarly returns zero, ResolvePointers/Resume do not reserve the former Pawn. |
| 9. Custom Verse.Name | Any nonblank non-NameTriple display becomes display-only actual name facts; NameTriple remains structured. |
| 10. No name fabrication | Preserve exact `ToStringFull`, null first/nick/last; no parsing, renaming or Pawn/Name/PawnRef replacement. |
| 11. Focused regressions | New custom-name **2 / 44 / 0**, Detached COMPLETE/registry/exclusion/report-only **3 / 115 / 0**; retained adapter guard **1 / 21 / 0**; combined affected **82 / 4,446 / 0**. Exact IDs above. |
| 12. Full suite run 1 | **586 tests / 43,106 checks / 0 failures, exit 0**, fresh process against `809e835`. |
| 13. Full suite run 2 | **586 tests / 43,106 checks / 0 failures, exit 0**, same source in a fresh process. |
| 14. Compiler | Each run: production/test **0 warnings / 0 errors**, warnings treated as errors. |
| 15. Nine source gates | **All nine PASS** in both runs. |
| 16. Save format | **5**, no bump, new field or migration. |
| 17. Production Harmony | **None**, confirmed by unchanged source gate in both final runs. |
| 18. DLL build stamp | `built 2026-10-06T10:59Z, source commit 809e835`. |
| 19. Informational version | `0.1.0+809e83556981a82309786c326b2d5eedba63749d`. |
| 20. DLL SHA-256 | `8ef464be121f0fabb26eb98a811a16071c6e57d3f9f2949a4df1560362b82ff2`. |
| 21. DLL bytes | **1,235,968**. |
| 22. R-50 | **OPEN**, unchanged. |
| 23. O-20 | **LOCKED**, unchanged. |
| 24. S11 | **FAIL / rescue STOPPED**, unchanged. |
| 25. Later phases | Full **3.2C / 3.3 / Phase 4 NOT IMPLEMENTED**. |
| 26. Owner runtime | **PENDING**. S21/S26/S27 remain PARTIAL; original PR #11 owner acceptance is preserved. |
| 27. Final verdict | **PR #13 SURGICAL CORRECTION COMPLETE — READY FOR REVIEW / OWNER RUNTIME QA.** |

**PR #13 SURGICAL CORRECTION COMPLETE — READY FOR REVIEW / OWNER RUNTIME QA.**

**DO NOT MERGE.** The overall Phase 3.2B owner-runtime status remains pending; none of these results claims owner in-game acceptance.

## Original delivery and validation evidence (historical)

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

### Original shipped DLL provenance (historical)

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

Implemented menu families: **026** small first visit; **027** five-person continuity (three reused + two concretized, then full same-Pawn repeat); **028** company zero presence promotion; **029** one anonymous arrest/atomic promotion; **030A SAVE** released/concretized group, **030B SAVE** anonymous arrest while peers Pending, **030V VERIFY** read-only loaded newest unambiguous 030/031 checkpoint; **031** owned Medic organizational succession then SAVE/LOAD→030V; **032A/032B BUILD** approximately 150/300 retained and **032V OBSERVE** loaded coverage/lookups. Exact labels/actions are in runtime § 19. The original strict/headless result is historical. Owner 026 previously passed and 027 continuity worked with four invalid assertion failures; the current correction requires a fresh 026 → 027 rerun before 028. Genuine home P0 has a separate typed adapter scope API but no added home-map action/UI.

Scenarios use existing session-only arm, dedicated tagged test map, sentinel and preserve-on-failure behavior. They are excluded from Quick smoke/Full safe regression. Existing IDs are not renumbered; save/load is owner-assisted, not automated. See [RUNTIME_TESTING § 19](RUNTIME_TESTING.md#19-phase-32b-groups-and-progressive-concretization) for exact checkpoints and commands.

The original delivery's final run 2 headless retention observations used actual PawnRef/registry and real Scribe fixture round-trips. The measurements below retain that original evidence, rather than claiming new owner-runtime observations from this surgical cleanup. No count above 150 is a failure criterion; every required identity remains intact.

| Headless population | Early registry rebuild | Pointer resolution | 100,000 reservation lookups |
|---|---:|---:|---:|
| 150 named | 0.009 ms | 0.054 ms | 6.901 ms |
| 300 named + 8 temporary Episode members | 0.017 ms | 0.169 ms | 7.347 ms |

| Reference XML fixture | Bytes | Scribe save | Load + cross-references | Early thing-ID bridge | Resolved pointer index |
|---|---:|---:|---:|---:|---:|
| 150 named | 91,871 | 3.078 ms | 5.451 ms | 0.015 ms | 0.061 ms |
| 300 named + 8 temporary | 186,516 | 4.451 ms | 10.219 ms | 0.026 ms | 0.172 ms |

The XML fixture delta is **94,645 bytes**. These are observed process-local costs in the original delivery's final run, including headless save/load reference resolution and coverage checks; they are **not a complete RimWorld save, Unity TPS, real game load elapsed or a mod-stack frame-time guarantee**. Runtime 032 exposes the separately pending owner registry/lookup observations; full save bytes/TPS/load timing remain manual owner evidence.

Above-six strong identity is proven by headless planner/commit/lifecycle tests. The current 029/030B runtime company begins with one known leader; a separate cap-full/overflow owner fixture remains pending rather than being falsely attributed to that case.

The owner still needs actual first/second crew visits and same Pawn continuity, large-company zero presence promotion, one anonymous capture while peers remain Pending, clean active/pre-RELEASE/post-RELEASE SAVE/LOAD, request-time construction behavior/opinion observations, real player-visible-map eligibility, source-qualified combat/relations on the active mod list, and practical runtime/save-size/save-load observations near **150 and 300 retained Pawns**. Synthetic headless registry timings and pointer fixtures do not establish Unity TPS, actual save bytes, crew hostility rates or owner gameplay PASS. No count above 150 fails merely for exceeding the soft region; no identities are removed to improve measurements.

### Exact deferred 3.2C scope

3.2B covers bounded group return, P0/strong identity promotion and the minimal one-held-plus-ordinary-return terminal batch. It does not intentionally build/validate the full A-returned/B-wounded/C-dead/D-captured/E-recruited/F-missing/G-combat-promoted/H-ephemeral matrix, its casualty/morale/succession/operation combinations or partial group extraction. Unknown combinations fail closed with references/reservations preserved. Independent early per-member identity/custody commit is also outside this slice. Ordinary Stored repeat visits do not resolve R-50 long-held suspension boundaries.

## Original 37-item delivery checklist (historical)

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


## Original manual PR handoff after GitHub API HTTP 403 (historical)

This records the original delivery attempt before the owner opened [PR #13](https://github.com/D3athAn63l/The-Network/pull/13). The no-PR result and complete body below describe that earlier handoff; the current correction continues the existing PR.

The concrete PR creation attempt failed with `Post https://api.github.com/graphql: Forbidden`. This was a GitHub API failure, not an automatic approval-review rejection. **No PR was created and nothing was merged.** Native Git successfully pushed `codex/phase32b-groups-concretization`, including artifact commit `0186390abae9c5d12cb258c88fa9562b4e7c6215`, and configured its upstream. A final documentation-only delivery commit records this handoff; the shipped DLL source remains `5b403fe`.

- Title: `Phase 3.2B: groups and progressive concretization`
- Head: `codex/phase32b-groups-concretization`
- Base: `main`
- Open manually: [GitHub compare / new PR](https://github.com/D3athAn63l/The-Network/compare/main...codex/phase32b-groups-concretization?expand=1).
- Exact body also available in the cloud workspace: `/workspace/.onboarding/phase32b-pr-body.md`.

The following is the original complete handoff body, retained as history. Its source/counts/DLL identify the original delivery; use the current surgical record above for corrected provenance. Do not merge.

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
