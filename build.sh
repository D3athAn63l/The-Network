#!/usr/bin/env bash
# Build The Network against a local RimWorld 1.6 install.
#
#   ./build.sh <RimWorld Managed folder>
#   RIMWORLD_MANAGED=<folder> ./build.sh
#
# The folder is RimWorld's own "RimWorldWin64_Data/Managed" (or the macOS/Linux equivalent). It must
# contain Assembly-CSharp.dll and the UnityEngine*.dll files. Nothing from it is copied into this
# repository. The output is 1.6/Assemblies/TheNetwork.dll.
#
# Requires the .NET SDK (dotnet). On machines without Windows reference assemblies, the
# Microsoft.NETFramework.ReferenceAssemblies NuGet package supplies them.
set -euo pipefail
cd "$(dirname "$0")"
MANAGED="${1:-${RIMWORLD_MANAGED:-}}"
[ -n "$MANAGED" ] || { echo "usage: ./build.sh <RimWorld Managed folder> (or set RIMWORLD_MANAGED)" >&2; exit 1; }
[ -f "$MANAGED/Assembly-CSharp.dll" ] || { echo "error: no Assembly-CSharp.dll in $MANAGED" >&2; exit 1; }

EXTRA=()
if [ ! -f "$MANAGED/netstandard.dll" ]; then
  # Unity 2022 assemblies reference netstandard 2.1. RimWorld ships it next to Assembly-CSharp;
  # when building from a trimmed set of DLLs, fall back to Mono's facade if one is installed.
  for f in /usr/lib/mono/4.8-api/Facades/netstandard.dll /usr/lib/mono/4.7.2-api/Facades/netstandard.dll; do
    if [ -f "$f" ]; then EXTRA+=("-p:NetStandardDll=$f"); break; fi
  done
fi

dotnet build Source/TheNetwork/TheNetwork.csproj -c Release -nologo -v:minimal \
  -p:RimWorldManaged="$MANAGED" "${EXTRA[@]}"
echo "Built 1.6/Assemblies/TheNetwork.dll"
