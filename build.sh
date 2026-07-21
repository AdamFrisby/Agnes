#!/usr/bin/env bash
#
# Build distributable Agnes artifacts for the common platforms into builds/ (gitignored).
#
#   ./build.sh                      # everything: windows, linux, mac (arm64+x64), android, web
#   ./build.sh linux windows        # only those desktop targets
#   ./build.sh android web          # only the mobile / web heads
#   ./build.sh --client-only linux  # skip the host daemon (build just the desktop app)
#
# Output layout (builds/ is git-ignored):
#   builds/windows/Agnes.exe        + builds/windows/host/Agnes.Host.exe
#   builds/linux/Agnes              + builds/linux/host/Agnes.Host
#   builds/mac/arm64/Agnes          + builds/mac/arm64/host/Agnes.Host
#   builds/mac/x64/Agnes            + builds/mac/x64/host/Agnes.Host
#   builds/android/*.apk
#   builds/web/                     (static WebAssembly site — serve wwwroot/)
#
# The desktop client and host are self-contained, single-file native executables (no .NET install
# needed on the target); they are NOT trimmed, because Avalonia and the host rely on reflection.
# Android and web are only built when their workloads are installed (dotnet workload install …).
#
set -euo pipefail

cd "$(dirname "$0")"
ROOT="$(pwd)"
OUT="$ROOT/builds"

DESKTOP_PROJ="src/Agnes.App.Desktop/Agnes.App.Desktop.csproj"
HOST_PROJ="src/Agnes.Host/Agnes.Host.csproj"
UNO_PROJ="src/Agnes.App/Agnes.App/Agnes.App.csproj"
CONFIG="Release"
BUILD_HOST=1

# ---- parse args ----
targets=()
for a in "$@"; do
  case "$a" in
    --client-only|--no-host) BUILD_HOST=0 ;;
    windows|linux|mac|android|web|all) targets+=("$a") ;;
    -h|--help) sed -n '2,20p' "$0"; exit 0 ;;
    *) echo "unknown target '$a' (expected: windows linux mac android web all)" >&2; exit 2 ;;
  esac
done
if [ ${#targets[@]} -eq 0 ] || printf '%s\n' "${targets[@]}" | grep -qx all; then
  targets=(windows linux mac android web)
fi
want() { printf '%s\n' "${targets[@]}" | grep -qx "$1"; }

# Self-contained, single-file, no-trim native publish flags.
common_flags=(-c "$CONFIG" --self-contained true
  -p:PublishSingleFile=true
  -p:IncludeNativeLibrariesForSelfExtract=true
  -p:DebugType=none -p:DebugSymbols=false
  --nologo)

exe_suffix() { if [ "$1" = "win-x64" ]; then echo ".exe"; else echo ""; fi; }

publish_desktop() { # rid outdir
  local rid="$1" dir="$2" sfx
  echo "==> desktop client · $rid → ${dir#$ROOT/}"
  rm -rf "$dir"; mkdir -p "$dir"
  dotnet publish "$DESKTOP_PROJ" -r "$rid" "${common_flags[@]}" -o "$dir" >/dev/null
  sfx="$(exe_suffix "$rid")"
  if [ -f "$dir/Agnes.App.Desktop$sfx" ]; then
    mv -f "$dir/Agnes.App.Desktop$sfx" "$dir/Agnes$sfx"   # friendlier app-host name
  fi
  # Native debug symbols (e.g. Skia/HarfBuzz .pdb) ship with the NuGet native assets but aren't needed
  # at runtime and bloat the bundle — drop them.
  find "$dir" -name '*.pdb' -delete 2>/dev/null || true
}

publish_host() { # rid outdir
  [ "$BUILD_HOST" -eq 1 ] || return 0
  local rid="$1" dir="$2/host"
  echo "==> host daemon   · $rid → ${dir#$ROOT/}"
  rm -rf "$dir"; mkdir -p "$dir"
  dotnet publish "$HOST_PROJ" -r "$rid" "${common_flags[@]}" -o "$dir" >/dev/null
  find "$dir" -name '*.pdb' -delete 2>/dev/null || true
}

desktop_target() { publish_desktop "$1" "$2"; publish_host "$1" "$2"; }

# ---- desktop OSes ----
if want windows; then desktop_target win-x64   "$OUT/windows"; fi
if want linux;   then desktop_target linux-x64 "$OUT/linux";   fi
if want mac; then
  desktop_target osx-arm64 "$OUT/mac/arm64"
  desktop_target osx-x64   "$OUT/mac/x64"
fi

# ---- android apk ----
if want android; then
  if dotnet workload list 2>/dev/null | grep -qw android; then
    echo "==> android apk"
    rm -rf "$OUT/android"; mkdir -p "$OUT/android"
    dotnet publish "$UNO_PROJ" -f net10.0-android -c "$CONFIG" --nologo -o "$OUT/android/_stage" >/dev/null
    # Collect whatever apk the Android build produced (signed + unsigned), from the publish output and
    # the project's android bin — whichever the SDK wrote them to.
    find "$OUT/android/_stage" -name '*.apk' -exec cp -f {} "$OUT/android/" \; 2>/dev/null
    find src/Agnes.App -path '*net10.0-android*' -name '*.apk' -exec cp -f {} "$OUT/android/" \; 2>/dev/null
    rm -rf "$OUT/android/_stage"
    ls "$OUT/android"/*.apk >/dev/null 2>&1 || echo "   (no .apk produced — check the android SDK / signing keystore)"
  else
    echo "!! skipping android — the 'android' workload isn't installed (dotnet workload install android)"
  fi
fi

# ---- web (WebAssembly) ----
if want web; then
  if dotnet workload list 2>/dev/null | grep -qw wasm-tools; then
    echo "==> web (WebAssembly)"
    rm -rf "$OUT/web"; mkdir -p "$OUT/web"
    dotnet publish "$UNO_PROJ" -f net10.0-browserwasm -c "$CONFIG" --nologo -o "$OUT/web" >/dev/null
  else
    echo "!! skipping web — the 'wasm-tools' workload isn't installed (dotnet workload install wasm-tools)"
  fi
fi

echo
echo "Done. Artifacts under ${OUT#$ROOT/}/:"
find "$OUT" -type f \( -name 'Agnes' -o -name 'Agnes.exe' \
  -o -name 'Agnes.Host' -o -name 'Agnes.Host.exe' -o -name '*.apk' \) 2>/dev/null \
  | sed "s|$ROOT/||" | sort
if want web && [ -d "$OUT/web" ]; then echo "  builds/web/  (static site — serve the published folder)"; fi
