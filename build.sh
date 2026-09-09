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
MOBILE_PROJ="src/Agnes.App.Mobile/Agnes.App.Mobile.csproj"
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

# macOS needs a real .app bundle, not a bare executable. Two things depend on it: Launch Services only
# reads CFBundleURLTypes from a bundle on disk, so `agnes://` links are unclickable without one (a process
# cannot register a scheme for itself the way it can on Linux and Windows); and a bare Mach-O binary gets no
# Dock icon or app name. The bundle wraps the executable that was just published — same binary, moved.
bundle_macos() { # dir arch
  local dir="$1" arch="$2" app="$1/Agnes.app"
  echo "==> mac bundle    · $arch → ${app#$ROOT/}"
  rm -rf "$app"
  mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
  cp "$ROOT/packaging/macos/Agnes.icns" "$app/Contents/Resources/Agnes.icns"

  # Everything the publish produced belongs inside the bundle; the app host must sit in Contents/MacOS.
  find "$dir" -maxdepth 1 -mindepth 1 ! -name 'Agnes.app' ! -name 'host' -exec mv -f {} "$app/Contents/MacOS/" \;

  cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Agnes</string>
  <key>CFBundleDisplayName</key><string>Agnes</string>
  <key>CFBundleIdentifier</key><string>com.multitudal.agnes</string>
  <key>CFBundleExecutable</key><string>Agnes</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleIconFile</key><string>Agnes.icns</string>
  <key>CFBundleShortVersionString</key><string>0.1.0</string>
  <key>CFBundleVersion</key><string>0.1.0</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <!-- What makes agnes:// links clickable on macOS. Launch Services reads this from the bundle; the app
       receives the URL as an Apple Event, which Avalonia surfaces as a protocol activation. -->
  <key>CFBundleURLTypes</key>
  <array>
    <dict>
      <key>CFBundleURLName</key><string>Agnes pairing link</string>
      <key>CFBundleTypeRole</key><string>Viewer</string>
      <key>CFBundleURLSchemes</key><array><string>agnes</string></array>
    </dict>
  </array>
</dict>
</plist>
PLIST
}

desktop_target() { publish_desktop "$1" "$2"; publish_host "$1" "$2"; }

# ---- desktop OSes ----
if want windows; then desktop_target win-x64   "$OUT/windows"; fi
if want linux;   then desktop_target linux-x64 "$OUT/linux";   fi
if want mac; then
  desktop_target osx-arm64 "$OUT/mac/arm64"; bundle_macos "$OUT/mac/arm64" arm64
  desktop_target osx-x64   "$OUT/mac/x64";   bundle_macos "$OUT/mac/x64"   x64
fi

# ---- android apk ----
# The Android client is its own Avalonia head (src/Agnes.App.Mobile), not a target of the Uno app: it
# is a ground-up phone UI rather than the desktop layout reflowed, so it shares Agnes.Ui.Core and
# nothing else. Needs the android workload plus a JDK and the Android SDK.
if want android; then
  if dotnet workload list 2>/dev/null | grep -qw android; then
    echo "==> android apk (Avalonia)"
    rm -rf "$OUT/android"; mkdir -p "$OUT/android"
    dotnet publish "$MOBILE_PROJ" -f net10.0-android -c "$CONFIG" --nologo -o "$OUT/android/_stage" >/dev/null
    # Prefer the signed APK — the SDK emits both, and only the signed one will install. Look in the
    # publish output first, then in the project's own bin, since which one the Android SDK writes to
    # varies with how the build was invoked.
    apk="$(find "$OUT/android/_stage" "$(dirname "$MOBILE_PROJ")/bin" -name '*-Signed.apk' 2>/dev/null | head -1)"
    [ -n "$apk" ] || apk="$(find "$OUT/android/_stage" "$(dirname "$MOBILE_PROJ")/bin" -name '*.apk' 2>/dev/null | head -1)"
    [ -n "$apk" ] && cp -f "$apk" "$OUT/android/Agnes.apk"
    rm -rf "$OUT/android/_stage"
    [ -f "$OUT/android/Agnes.apk" ] || echo "   (no .apk produced — check the android SDK / signing keystore)"
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
