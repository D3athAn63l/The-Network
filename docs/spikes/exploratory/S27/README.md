# S27 isolated combat-evidence probe

**Audit/test only.** This standalone executable is not included in TheNetwork.dll, the ordinary
test runner, or any runtime scenario. It does not implement production promotion. It references
the owner's RimWorld assemblies and test-only Harmony; no proprietary binary is committed here.

The [S27 record](../../S27-progressive-concretization-evidence.md) explains the evidence policy,
15 assertions, measured results and gameplay limits. The probe uses controlled uninitialized Pawn
and log-entry shells, reflection to supply private fixture fields, and actual vanilla GetConcerns
implementations. Its explicit player Pawn argument does not test faction/host observations.
Harmony suppresses Unity-native texture lookup/log presentation only, outside the production mod.

Build with a .NET SDK, Mono and local .NET Framework 4.7.2 reference assemblies. Set
`RIMWORLD_MANAGED` to the game Managed folder and `HARMONY_DLL` to the test Harmony assembly.
`FrameworkPathOverride` should point to Mono's `4.7.2-api` reference directory. The prepared cloud
environment supplies those through `/workspace/.onboarding/env.sh`.

From the repository root, keep all outputs and reference copies outside the repository:

```bash
# Prepared cloud environment only; other machines set the three variables described above.
source /workspace/.onboarding/env.sh
unset DirectoryBuildTargetsPath
probe_out=$(mktemp -d /tmp/network-s27.XXXXXX)
dotnet build docs/spikes/exploratory/S27/ScanSpike.csproj --nologo -v minimal \
  -p:TreatWarningsAsErrors=true \
  -p:BaseIntermediateOutputPath="$probe_out/obj/" \
  -p:MSBuildProjectExtensionsPath="$probe_out/obj/" \
  -p:AppendTargetFrameworkToOutputPath=false \
  -p:OutputPath="$probe_out/bin/"
python3 - "$RIMWORLD_MANAGED" "$HARMONY_DLL" "$probe_out/bin" <<'PY'
import pathlib, sys
managed, harmony, output = map(pathlib.Path, sys.argv[1:])
# Prefer Mono's own framework. Do not override it with the game's System/Mono assemblies.
for dll in managed.glob('*.dll'):
    if dll.name.startswith(('System', 'Mono.')) or dll.name in ('mscorlib.dll', 'netstandard.dll'):
        continue
    destination = output / dll.name
    if not destination.exists():
        destination.symlink_to(dll.resolve())
(output / '0Harmony.dll').symlink_to(harmony.resolve())
PY
mono "$probe_out/bin/ScanSpike.exe"
```

Expected: 15 PASS lines and one synthetic timing line. Timings vary by machine; build/startup and
fixture construction are excluded. This is not a full game, Pawn-generation, custody or save/load
experiment. Run the future owner scenarios before calling production S27 runtime validated.
