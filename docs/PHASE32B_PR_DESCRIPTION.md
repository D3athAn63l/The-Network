NPC organizations can materialize bounded role-correct groups, reserve anonymous Episode Pawns through the existing M1 registry and promote strong/eligible identity evidence atomically to the same Pawn. Composition v1, durable roles, conservative accounting, same-Pawn custody and existing encounter faction/shared Lord behavior remain the accepted Phase 3.2B architecture.

This corrects the owner's first `RT-PHYX-027` run and updates the existing PR #13:

- Verify first-created candidates against their actual creation RoleSpec. Verify retained people by exact CharacterId/Pawn/binding/opRole and reservation, without reapplying a later capability floor or altering skills/history.
- Include anonymous committed tiers in `VeteranShare`, preserving tier ratio and ExperienceBand during matching-tier checkout/return. Preserve the genuinely-empty 0.5 fallback and accepted anonymous→named representation behavior; add no named-tier schema.
- Add a **240 ordinary game tick** dwell only to 026 and the first 027 rematerialization. Use the existing step/wait runner and check live owned map/Pawn/role/faction/Lord/Episode/registry identity; normal AI/jobs/social behavior continues. The final five-person repeat stays fast. Headless wait/failure tests do not prove real game ticks or Lord/social behavior.
- Preserve the previous custom Verse.Name display-only fallback and Detached anonymous COMPLETE binding cleanup.

Validation and artifact:

- Production source: `bd59bd0` (`bd59bd0a57704597feefead598721151a6e42758`).
- Focused regressions: **98 tests / 4,699 checks / 0 failures**; **16 new tests / 253 checks** (VeteranShare **6 / 122 / 0**, retained-role/dwell **10 / 131 / 0**).
- Two fresh full-suite runs: run 1 **602 tests / 43,431 checks / 0 failures, exit 0**; run 2 **602 tests / 43,431 checks / 0 failures, exit 0**.
- Production/test compiler: **0 warnings / 0 errors** in production and tests in each run, warnings treated as errors. Repository source gates: **All nine source gates PASS** in each final run.
- Shipped exact validated second-run `1.6/Assemblies/TheNetwork.dll`, without later rebuild: build stamp `built 2026-10-06T11:49Z, source commit bd59bd0`; informational version `0.1.0+bd59bd0a57704597feefead598721151a6e42758`; SHA-256 `758be09235ef6e879e5a02eca1d395df703aafa9d3da94fe26e8e402f749d81b`; **1,248,768** bytes.
- Save format stays **5**, no migration/new field, no production Harmony.

An earlier full invocation aborted in Mono's native runtime with SIGSEGV (exit 134), without a preceding assertion failure. The same executable passed the isolated existing soak and a fresh full pass without Source/test/environment changes. The aborted invocation is excluded from pass totals; observed context and logs are disclosed in the validation report, with no claimed root cause.

Owner evidence: previous **026 PASS**. Previous **027 continuity passed**, including same remembered Pawns, only previously abstract new projections, zero replacements on the full repeat, conserved humans and no observed reservation gap/duplication; the **scenario FAILed four invalid retained-Pawn current-band role assertions**. This is not an identity-continuity failure. On the corrected DLL, rerun **026 → 027 in a fresh disposable fixture before 028**. Corrected 027 and the new dwell have no owner PASS yet.

S21/S26/S27 stay **PARTIAL**, R-50 **OPEN**, O-20 **LOCKED**, S11 **FAIL / rescue STOPPED**. Full 3.2C, 3.3 and Phase 4 remain unimplemented. TestSite P0 remains explicitly synthetic; real visibility, further game loads/combat/custody and practical 150/300 save/load/TPS evidence remain pending. See [current validation](PHASE32B_VALIDATION.md#pr-13-runtime-correction-current-owner-review-record) and [runtime instructions](RUNTIME_TESTING.md#19-phase-32b-groups-and-progressive-concretization).

**PHASE 3.2B IMPLEMENTED / HEADLESS VALIDATED — OWNER RUNTIME VALIDATION PENDING**

**PR #13 RUNTIME CORRECTION COMPLETE — OWNER 026 → 027 RERUN REQUIRED**

**DO NOT MERGE.**
