#!/usr/bin/env bash
# Publishes the smoke program natively (NativeAOT) with warnings as errors, then runs it.
# Needs the native toolchain (clang, or set CPP_COMPILER=gcc) and the ILCompiler package from NuGet.
#   tests/OneYaml.AotSmoke/run.sh            NativeAOT publish
#   tests/OneYaml.AotSmoke/run.sh trimmed    trimmed, self-contained publish (no native toolchain needed)
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
out="${ONEYAML_ARTIFACTS:-${TMPDIR:-/tmp}/oneyaml-aot}/aot-smoke"
rm -rf "$out"
mode=aot
if [ "${1:-}" = trimmed ]; then mode=trimmed; fi
extra=()
if [ -n "${CPP_COMPILER:-}" ]; then extra+=("-p:CppCompilerAndLinker=$CPP_COMPILER"); fi
dotnet publish "$here" -c Release -p:SmokeMode="$mode" -p:TreatWarningsAsErrors=true "${extra[@]}" -o "$out"
"$out/OneYaml.AotSmoke"
