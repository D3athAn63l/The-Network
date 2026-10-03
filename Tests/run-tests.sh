#!/usr/bin/env bash
# Builds The Network, then builds and runs the headless tests under Mono.
#
#   Tests/run-tests.sh <RimWorld Managed folder> <path to 0Harmony.dll>
#   RIMWORLD_MANAGED=... HARMONY_DLL=... Tests/run-tests.sh
#
# 0Harmony.dll is needed by the TEST RUNNER only (it stubs Unity-only logging so Verse's Scribe can run
# outside the game). The mod never references Harmony; a test checks that.
set -euo pipefail
cd "$(dirname "$0")/.."
MANAGED="${1:-${RIMWORLD_MANAGED:-}}"
HARMONY="${2:-${HARMONY_DLL:-}}"
[ -n "$MANAGED" ] && [ -f "$MANAGED/Assembly-CSharp.dll" ] || { echo "usage: Tests/run-tests.sh <RimWorld Managed folder> <0Harmony.dll>" >&2; exit 1; }
[ -n "$HARMONY" ] && [ -f "$HARMONY" ] || { echo "error: 0Harmony.dll not found (needed by the test runner only)" >&2; exit 1; }

./build.sh "$MANAGED"

echo "### Source scan: no code path names a specific item, faction or mod (A11)"
if grep -rniE "tenebri|beyond ?our ?reach|\bBOR_|grandmaster|regennanite" Source/ ; then
  echo "FAIL: Source names a specific mod or item" >&2; exit 1
fi
if grep -rn "HarmonyLib\|0Harmony" Source/ ; then
  echo "FAIL: Source references Harmony" >&2; exit 1
fi
echo "ok"

echo "### Source scan: spatial continuity creates no pawn, caravan or world object (SPATIAL § 10)"
if grep -rnE "PawnGenerator|CaravanMaker|WorldObjectMaker|MakeWorldObject|GeneratePawn|WorldObjects\.Add|SpawnSetup" Source/TheNetwork/Domain/Spatial Source/TheNetwork/Integration/SpatialWorldAdapter.cs ; then
  echo "FAIL: spatial code creates world things" >&2; exit 1
fi
echo "ok"

echo "### Source scan: abstract charter spawns no craft and moves no money (ADR-045)"
if grep -rnE "ThingMaker|GenSpawn|SkyfallerMaker|TransportShuttle|CompShuttle|DropPodUtility|ctx\.payment|MoneyRecord|ledger" Source/TheNetwork/Domain/Spatial Source/TheNetwork/Integration/SpatialWorldAdapter.cs ; then
  echo "FAIL: spatial code spawns things or touches money" >&2; exit 1
fi
echo "ok"

echo "### Source scan: derived Tags are read models, never a second stat system (ADR-046)"
if grep -rnE "CareerTags|\.Tags\(|HasTag\(" Source/TheNetwork/Domain/Operations Source/TheNetwork/Domain/Contracts Source/TheNetwork/Domain/Opportunities Source/TheNetwork/Domain/Consequences Source/TheNetwork/Domain/Contractors/ContractorService.cs Source/TheNetwork/Domain/Contractors/UpkeepService.cs Source/TheNetwork/Domain/Contractors/MoraleModel.cs ; then
  echo "FAIL: a Tag is read where state decides outcomes (resolver, pricing, willingness, upkeep)" >&2; exit 1
fi
echo "ok"

echo "### Source scan: careers add no pawn, inventory, vehicle, augmentation or Harmony (ADR-046)"
if grep -rnE "PawnGenerator|Hediff|Bionic|Implant|ThingMaker|Vehicle|Inventory|HarmonyLib" Source/TheNetwork/Domain/CareerPolicy.cs Source/TheNetwork/Domain/Contractors/CareerService.cs Source/TheNetwork/Domain/Contractors/CareerDistribution.cs ; then
  echo "FAIL: career code touches physical things" >&2; exit 1
fi
echo "ok"

echo "### Source scan: runtime tests never spawn, spend, launch or touch the live scheduler (ADR-047)"
# The scratch worlds reach the colony through nothing: no Thing creation, no spawn, no trade/launch, no drop pod, no silver movement,
# no live scheduler, no letters, no start-up of the live Network. Only the read-only Live suite and the colony sentinel may name RimWorld
# world APIs (and only to READ them).
if grep -rnE "ThingMaker|GenSpawn|GenPlace|SkyfallerMaker|DropPodUtility|TradeUtility\.LaunchSilver|\.TryCharge|\.TryRefund|NetworkRuntime\.Current\.Scheduler|rt\.Scheduler\.(Schedule|Cancel|RegisterKind|Clear)|ctx\.scheduler\.Schedule\(\"devtest|\"devtest\." Source/TheNetwork/Diagnostics/RuntimeTests ; then
  echo "FAIL: runtime-test code reaches a gameplay effect or the live scheduler" >&2; exit 1
fi
if grep -rnE "HarmonyLib|0Harmony|Scribe_|IExposable|ExposeData" Source/TheNetwork/Diagnostics/RuntimeTests Source/TheNetwork/Diagnostics/NetworkDevActions.RuntimeTests.cs ; then
  echo "FAIL: runtime-test code uses Harmony or is Scribed (it must be runtime only)" >&2; exit 1
fi
if grep -n "TheNetwork.Tests" Source/TheNetwork/TheNetwork.csproj ; then
  echo "FAIL: the production project references the test project" >&2; exit 1
fi
# Reading the letter stack (the colony sentinel counts letters) is fine; sending, removing or building one is not.
if grep -rnE "ReceiveLetter|RemoveLetter|LetterMaker|ChoiceLetter|StandardLetter|ContractLetterConsumer|LetterConsumer|Find\.LetterStack\.[A-Za-z]*(Receive|Remove)" Source/TheNetwork/Diagnostics/RuntimeTests Source/TheNetwork/Diagnostics/NetworkDevActions.RuntimeTests.cs | grep -vE ':[0-9]+:[[:space:]]*(//|\*|/\*)' ; then
  echo "FAIL: runtime-test code sends a gameplay letter" >&2; exit 1
fi
# A runtime test never starts, repairs or reconciles the LIVE Network (the game does that on its first tick, Network tab or command).
# Comment lines are ignored; production code outside RuntimeTests is not scanned.
if grep -rnE "\.EnsureStarted\(|\.StartNow\(|\bRunStartup\(|\b(rt|runtime|Runtime|Current)\.Active\b|\.Session\.Ensure\(|NetValidator\." Source/TheNetwork/Diagnostics/RuntimeTests Source/TheNetwork/Diagnostics/NetworkDevActions.RuntimeTests.cs | grep -vE ':[0-9]+:[[:space:]]*(//|\*|/\*)' ; then
  echo "FAIL: runtime-test code starts, reconciles or repairs the live Network (EnsureStarted / StartNow / RunStartup / .Active / NetValidator)" >&2; exit 1
fi
echo "ok"

echo "### Source scan: the Phase 3.0 lifecycle creates no pawn, Lord, faction, quest, map or thing, and uses no Harmony (PHYSICAL_LIFECYCLE § 23)"
# Scoped to the new physical-lifecycle code (older phases' vanilla usage is not affected). The fake port may NAME an action
# (PassToWorld) for test semantics; it can never reach the real API, which this scan forbids by its vanilla type names.
PHYS="Source/TheNetwork/Domain/Physical Source/TheNetwork/Domain/Contractors/FateRules.cs Source/TheNetwork/Diagnostics/RuntimeTests/FakePhysicalWorldPort.cs Source/TheNetwork/Diagnostics/RuntimeTests/Suites/PhysicalRuntimeSuite.cs"
if grep -rnE "\b(PawnGenerator|GeneratePawn|GenSpawn|WorldPawns|LordMaker|MakeNewLord|FactionGenerator|NewGeneratedFaction|HarmonyLib|0Harmony|ThingMaker|QuestGen|BirthAbsTicks)\b|Find\." $PHYS | grep -vE ':[0-9]+:[[:space:]]*(//|\*|/\*)' ; then
  echo "FAIL: physical-lifecycle code reaches a real physical API" >&2; exit 1
fi
echo "ok"

echo "### Source scan: the reconciliation commit (Applier) and the shared fate rules are pure (RT-PHYS-027)"
if grep -nE "ctx\.bus|\.Publish\(|[Ss]cheduler|NetLog|Verse\.|Rand\.|System\.Random|UnityEngine|physicalPort|Port\." Source/TheNetwork/Domain/Physical/ReconciliationApplier.cs Source/TheNetwork/Domain/Contractors/FateRules.cs | grep -vE ':[0-9]+:[[:space:]]*(//|\*|/\*)' ; then
  echo "FAIL: the atomic commit references a bus, scheduler, port, log, vanilla API or random source" >&2; exit 1
fi
echo "ok"

echo "### Source scan: production holds only the fail-closed physical port; the fake exists in the safe test code only"
if grep -rln "FakePhysicalWorldPort" Source/TheNetwork --include=*.cs | grep -v "^Source/TheNetwork/Diagnostics/RuntimeTests/" ; then
  echo "FAIL: production code names the fake physical port" >&2; exit 1
fi
grep -q "physicalPort = new Domain.Physical.UnavailablePhysicalWorldPort()" Source/TheNetwork/Core/NetworkRuntime.cs || { echo "FAIL: the live runtime does not hold the fail-closed physical port" >&2; exit 1; }
echo "ok"

echo "### Source scan: spike S31 is dev-only, armed, runtime-only and isolated (PHYSICAL_LIFECYCLE § 7.6, § 21.2)"
S31="Source/TheNetwork/Diagnostics/Spikes/S31"
# Real creation APIs exist in the spike folder and nowhere else in the mod.
if grep -rnE "\b(PawnGenerator|GeneratePawn|GenSpawn|LordMaker|MakeNewLord|FactionGenerator|NewGeneratedFaction|WorldObjectMaker|GetOrGenerateMap|DeinitAndRemoveMap|HediffMaker)\b|QuestManager\.Add\b" Source/TheNetwork --include=*.cs | grep -v "^$S31/" | grep -vE ':[0-9]+:[[:space:]]*(//|\*|/\*)' ; then
  echo "FAIL: a real creation API is used outside the S31 spike folder" >&2; exit 1
fi
# The harness saves nothing of its own, never calls PassToWorld, never forces a GC pass, and implements no physical port.
if grep -rnE "Scribe_|IExposable|ExposeData|GameComponent|WorldComponent|MapComponent|PassToWorld[[:space:]]*\(|(RunGC|PawnGCPass|WorldPawnGCTick)[[:space:]]*\(|IPhysicalWorldPort|PhysicalLifecycleService|EpisodeRequest|physicalPort" $S31 | grep -vE ':[0-9]+:[[:space:]]*(//|\*|/\*)' ; then
  echo "FAIL: the S31 harness persists state, passes a pawn itself, forces a GC pass or touches the physical port" >&2; exit 1
fi
# The safe runtime suites never reach the spike.
if grep -rnE "Spikes|S31Spike|S31Run" Source/TheNetwork/Diagnostics/RuntimeTests | grep -vE ':[0-9]+:[[:space:]]*(//|\*|/\*)' ; then
  echo "FAIL: a safe runtime suite references the S31 spike" >&2; exit 1
fi
echo "ok"

REPO="$(pwd)"
OUT="${TEST_OUT:-$(mktemp -d)}"
EXTRA=()
if [ ! -f "$MANAGED/netstandard.dll" ]; then
  for f in /usr/lib/mono/4.8-api/Facades/netstandard.dll /usr/lib/mono/4.7.2-api/Facades/netstandard.dll; do
    if [ -f "$f" ]; then EXTRA+=("-p:NetStandardDll=$f"); break; fi
  done
fi
dotnet build Tests/TheNetwork.Tests/TheNetwork.Tests.csproj -c Release -nologo -v:quiet \
  -p:RimWorldManaged="$MANAGED" -p:HarmonyDll="$HARMONY" -p:TestOut="$OUT/" "${EXTRA[@]}" | grep -vE "MSB3277|^\s*$" || true
[ -f "$OUT/TheNetwork.Tests.exe" ] || { echo "test build failed" >&2; exit 1; }
cp "$MANAGED"/*.dll "$OUT/" 2>/dev/null || true
cp -r Tests/Fixtures "$OUT/"
echo "### Running tests"
(cd "$OUT" && THENETWORK_REPO="$REPO" mono TheNetwork.Tests.exe "${TEST_FILTER:-}")
