# R-50 — held-pawn biological aging bookmark audit

**Verdict: OPEN after source audit. Production aging is unchanged.** No Harmony, background aging, extra polling, new persisted field or save-format change is introduced. The existing `PawnRef.agedThroughTick` can represent the desired bookmark; the missing information is an exact, trustworthy suspension boundary. It is not a reason to add another timestamp.

## Evidence and scope

Audited the owner's `Assembly-CSharp.dll`, assembly version **1.6.9676.17735**, SHA-256 `5cf1b5be399d5b1c9c56ca72c9d35b4ecf307feacf5859d04ac5a1aa5926356a`, decompiled with ILSpyCmd **9.1.0.7988**. References below name the decompiled type and method; line ranges describe that decompilation, not an upstream source checkout. Proprietary source and assemblies are not committed.

| Vanilla source evidence | Result |
|---|---|
| `Verse.TickList.Tick` (63–100), `Verse.Pawn.TickInterval` (1618–1726) | Map ticking reaches biological aging only while the pawn is not suspended. Mutants that disable aging are also excluded. A prisoner/slave label alone does not determine whether the pawn ages. |
| `RimWorld.Planet.WorldPawns.WorldPawnsTick` (73–109), `DoMothballProcessing` (435–460), `RemovePawn` (236–258) | Alive world pawns receive `DoTick`. Mothballed pawns receive batches every 15,000 ticks and a remainder on removal. Both age paths still respect `Pawn.Suspended`. Removal does not repair a suspended interval. |
| `WorldPawns.GetSituation` (267–315), `Verse.Pawn.Suspended` (1112–1126) | Quest reservation makes a world pawn `ReservedByQuest` and therefore suspended. Faction leader, starting pawn left behind, kidnapped and caravan situations precede reservation. Transport and borrowed situations follow it. |
| `Verse.Thing.Suspended` (552–565) | Holder suspension is independent evidence: a non-spawned pawn in a suspending holder can also be suspended. |
| `RimWorld.KidnappedPawnsTracker.KidnappedPawnsTrackerTick` (87–117) | Vanilla tries recruitment every 15,051 ticks. It changes faction and removes the pawn from the kidnapped list, with no exact suspension notification to the Network. The pawn can remain in the same `WorldPawns` container throughout. |
| `Verse.Pawn.SetFaction` (2694–2788) | Faction change performs many vanilla side effects and resets age-reversal demand. That demand is not a last-aging bookmark. It does not notify the Network when the later kidnapped-list removal changes suspension. |
| `RimWorld.Planet.World.WorldTick` (213–220) | World-pawn ticking precedes faction ticking and world components. A later Network observation cannot substitute for the intervening vanilla boundary. |
| `Verse.Pawn_AgeTracker.TickBiologicalAge`, `AgeTickInterval`, `AgeTickMothballed` (434–497), `ExposeData` (403–415) | Biological age, fractional tick progress and growth/reversal state are persisted; there is no last-successful-biological-aging game tick. Rates can change with age, genes and other vanilla conditions. Mothball aging advances age before birthday effects and is non-atomic. |

## Which custody states age

These are vanilla routing conclusions, not new owner runtime results. "Normally" assumes a living pawn, a non-suspending holder and no independent aging exclusion.

| State | Biological aging owner / behavior |
|---|---|
| Spawned contractor, prisoner or slave | Vanilla map ticking normally ages the pawn; the Network does no background age work. |
| Kidnapped world pawn | `Kidnapped` wins before reservation, so vanilla active/mothball aging normally continues. |
| Caravan pawn | `CaravanMember` wins before reservation, so vanilla normally ages the pawn. |
| Traveling transporter pawn | `TravellingTransporters.AddTransporter` (168–192) passes its pawns to `WorldPawns`; `PawnUtility.IsTravelingInTransportPodWorldObject` (92–99) recognizes their holder. An ordinary non-reserved transit pawn follows vanilla aging, but an M1-reserved contractor can become `ReservedByQuest` because that check precedes transit. A holder can independently suspend contents. Transit is not proof of current biological age. |
| Ordinary faction world pawn | Vanilla active/mothball processing normally ages a non-reserved pawn. Permanent faction membership does not itself override quest suspension. |
| `ReservedByQuest` world pawn, including a captor's recruit / off-map captive | `Pawn.Suspended` is true; both normal and mothball aging skip it. |
| Any other suspended pawn | Normal/mothball aging skips it. Chronological age remains derived from birth time; this does not prove biological age is current. |

## M1 and the available timestamps

`RetainedPawnRegistry.RetainedCustody` covers living, bound `Deployed`, `Stored` and `OutOfCustody` people. Binding and the registry quest exist before spawn. `QuestPart_NetworkRetainedPawns.QuestPartReserves` reads this derived predicate; it neither observes nor changes age. `WorldPawns.GetSituation` reads the reservation when needed; a later change in a higher-priority situation can expose it without changing the Network's custody record.

| Available timestamp | What it proves / limit |
|---|---|
| `PawnRef.agedThroughTick` | Creation or a completed Network catch-up can establish biological age through this game tick. The existing field is sufficient for this contract. |
| `Pawn.becameWorldPawnTickAbs` (`Notify_PassedToWorld`, 1883; saved at 4572) | The absolute tick of entry to `WorldPawns`, not a later change from kidnapped/caravan to quest-suspended. `RemovePawn` clears it. It does not identify the last successful age step. |
| `RimWorldPhysicalWorldPort.leftMap` / `NoteLeftMap` / `ExitTick` | Synchronous map-exit game tick, session-only. Useful for the existing normal exit path; absent after reload or an exit without that signal, and stale for a later off-map suspension. |
| `KnownCharacter.heldSinceTick` | Start of observed continuous vanilla holding, preserved across holder changes. It is neither an aging boundary nor a suspension timestamp. |
| Observation / custody-watch tick | When the Network noticed current custody. It does not prove what biological time vanilla applied. |

The steady-state custody watch detects observable holder changes within **2,500 ticks**, assuming the scheduled job runs normally; signals may wake it earlier. Scheduler delay and quarantined/retry paths can increase that latency. This is a bound on observation latency, **not** a guaranteed bound on biological-age uncertainty. A pawn may stay suspended for years with the same holder, or change situation between observations. After reload, the session-only exit evidence is gone. The general uncertainty can span the entire interval since the last trustworthy age bookmark; no finite 2,500-tick maximum can honestly be claimed for it.

## Existing Network age writes and calls

| Call / write | Current behavior and audit result |
|---|---|
| `PhysicalLifecycleService.Bind`: first projection and anonymous creation | Initializes `boundTick` and `agedThroughTick` to creation time. |
| `Bind`: reuse of a stored named pawn | Requests the full positive `now - agedThroughTick` through `IPhysicalWorldPort.CatchUpAge`; sets the bookmark to now after success. No background aging job exists. |
| `RimWorldPhysicalWorldPort.CatchUpAge` → `PawnAging.CatchUp` | Calls real `AgeTickMothballed`, uncapped in chunks; never writes `BirthAbsTicks`. |
| Failed mothball chunk | Existing `AgingUncertainException` reports completed chunks and uncertain evidence. The lifecycle advances only completed chunks and quarantines `AgeTruthUncertain`; it does not replay the uncertain step. Unchanged. |
| `ReconciliationPlanner.PlanEpisode` → `CharacterStored` | Uses a valid observed exit tick, otherwise the commit tick. `ReconciliationApplier` advances character/member bookmarks monotonically to that value. This fallback is not evidence that age was applied; it remains the known R-50 gap, including held returns and missing-exit cases. |

**Required definition:** `agedThroughTick` is the latest game tick through which the pawn's biological age is **known to have actually been brought current**, not the current tick of observation. Code comments and the design now state that contract and explicitly identify the existing fallback's limitation. This audit does not relabel that fallback as truthful or close R-50.

## Why the production change stops here

Captured → kidnapped and normally aged → captor recruits/removes from kidnapped list → M1 suspension → long wait → same pawn returns → rematerialization is the counterexample. The saved world-entry tick predates suspension, and the retained bookmark may predate vanilla's normally applied captivity interval. Replaying everything since either timestamp double-ages that interval. Stamping the later observation/return tick instead skips the suspended interval. Biological age and a current rate cannot reconstruct earlier rates, exclusions and birthday side effects.

There is no proven exact boundary across the required paths using the existing lifecycle callbacks and durable evidence. Polling every pawn, a new per-pawn scheduler, per-tick monitoring, mutation inside the pure reservation query, Harmony, or a custom biological age simulator would violate this pass. No such workaround is added. No R-50 closure tests or physical aging scenario are claimed.

**Narrowest future alternative:** separately prove an exact, one-time vanilla/lifecycle suspension handoff that also establishes the last completed biological-aging boundary. Then reuse `agedThroughTick` at that boundary and the existing catch-up immediately before reuse. Network-controlled handoffs alone cannot cover every vanilla captor transition. If no such general callback/evidence exists, bring that specific integration constraint back for an owner design decision; another persisted field by itself cannot recover an unobserved boundary. Save format remains **5**.
