#!/usr/bin/env bash
# Builds the mod and the in-game test harness, deploys both into an r2modman profile,
# launches Lethal Company through Steam (the same way r2modman does), waits for the
# harness to finish its scenarios, then collects the report and logs.
#
# Usage: tests/run-ingame-tests.sh [--profile NAME] [--multiplayer] [--timeout SECONDS] [--no-build]
#
#   --profile      r2modman profile to run in (default: YoutubeOnTV-Test). Any other
#                  profile is restored afterwards: the dev build is removed and the
#                  YoutubeOnTV copies it temporarily disabled are re-enabled.
#   --multiplayer  Host in the profile and join it from a second game instance, started
#                  directly through Proton with its own profile (YoutubeOnTV-TestClient,
#                  created from the test profile when missing) so it has its own cache.
#   --timeout      Watchdog in seconds before the game is killed (default: 900).
#   --no-build     Deploy whatever is already in the bin folders.
#   --audible      Leave the game's sound on. By default the game's audio stream is muted
#                  in PipeWire/PulseAudio; the harness measures sound inside Unity, so the
#                  audio checks are unaffected.
#
# Exit code: 0 when every scenario passed, 1 on any failure or timeout.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROFILE_NAME="YoutubeOnTV-Test"
CLIENT_PROFILE_NAME="YoutubeOnTV-TestClient"
TIMEOUT=900
BUILD=1
MULTIPLAYER=0
MUTE=1

while [ $# -gt 0 ]; do
  case "$1" in
    --profile) PROFILE_NAME="$2"; shift 2 ;;
    --multiplayer) MULTIPLAYER=1; shift ;;
    --timeout) TIMEOUT="$2"; shift 2 ;;
    --no-build) BUILD=0; shift ;;
    --audible) MUTE=0; shift ;;
    -h|--help) sed -n '2,22p' "$0"; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

APP_ID=1966720
STEAM_ROOT="${STEAM_ROOT:-$HOME/.local/share/Steam}"
R2_ROOT="${R2_ROOT:-$HOME/.config/r2modmanPlus-local/LethalCompany}"
PROFILE="$(realpath "$R2_ROOT/profiles/$PROFILE_NAME")"
CLIENT_PROFILE="$R2_ROOT/profiles/$CLIENT_PROFILE_NAME"
GAME_DIR="$STEAM_ROOT/steamapps/common/Lethal Company"
PROTON="${PROTON:-$STEAM_ROOT/steamapps/common/Proton 11.0/proton}"
# Proton needs Steam's runtime container: outside it, its GStreamer cannot load the H.264
# decoder and every video "plays" as a 4-second placeholder.
STEAM_RUNTIME="${STEAM_RUNTIME:-$STEAM_ROOT/steamapps/common/SteamLinuxRuntime_sniper/_v2-entry-point}"
LOCALLOW="$STEAM_ROOT/steamapps/compatdata/$APP_ID/pfx/drive_c/users/steamuser/AppData/LocalLow/ZeekerssRBLX/Lethal Company"
TEST_SAVE="$LOCALLOW/LCSaveFileYoutubeOnTVTest"
GAME_PATTERN="Lethal Company[.]exe" # the brackets keep pgrep from matching shells that mention it
RESULTS_NAME="youtubeontv-test-results.json"

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

die() { echo "ERROR: $*" >&2; exit 1; }

[ -d "$PROFILE/BepInEx/core" ] || die "profile '$PROFILE_NAME' not found at $PROFILE"
pgrep -f "$GAME_PATTERN" >/dev/null && die "Lethal Company is already running; close it first"
pgrep -x steam >/dev/null || die "Steam is not running"
if [ "$MULTIPLAYER" -eq 1 ]; then
  [ -x "$PROTON" ] || die "Proton not found at $PROTON (set PROTON=/path/to/proton)"
  [ -x "$STEAM_RUNTIME" ] || die "Steam runtime not found at $STEAM_RUNTIME (set STEAM_RUNTIME)"
fi

# ---- Build ----
if [ "$BUILD" -eq 1 ]; then
  echo "[1/5] Building mod and harness..."
  # Compile against the YoutubeDLSharp the profile actually loads, so API drift is a build error.
  YTDL_DIR="$PROFILE/BepInEx/core/Lordfirespeed-YoutubeDLSharp"
  [ -f "$YTDL_DIR/YoutubeDLSharp.dll" ] || YTDL_DIR="$ROOT/libs"
  [ -f "$ROOT/libs/Assembly-CSharp.dll" ] || "$ROOT/fetch-libs.sh"
  dotnet build "$ROOT/tests/YoutubeOnTV.TestHarness/YoutubeOnTV.TestHarness.csproj" -c Release \
    -p:YoutubeDLSharpDir="$YTDL_DIR" -nologo -clp:ErrorsOnly
fi

MOD_BIN="$ROOT/YoutubeOnTV/bin/Release"
HARNESS_BIN="$ROOT/tests/YoutubeOnTV.TestHarness/bin/Release"

# ---- Deploy ----
echo "[2/5] Deploying..."
DISABLED=()
REMOVE_DEV_DIRS=()
SNAPSHOT=""

restore_profiles() {
  for f in "${DISABLED[@]}"; do
    [ -e "$f.disabled-by-tests" ] && mv "$f.disabled-by-tests" "$f"
  done
  DISABLED=()
  for d in "${REMOVE_DEV_DIRS[@]}"; do
    rm -rf "$d"
  done
  REMOVE_DEV_DIRS=()
}

restore_saves() {
  local restored=0
  while IFS= read -r -d '' f; do
    rel="${f#$SNAPSHOT/}"
    case "$rel" in Player*.log) continue ;; esac
    if ! cmp -s "$f" "$LOCALLOW/$rel"; then
      cp -a "$f" "$LOCALLOW/$rel"
      restored=$((restored + 1))
    fi
  done < <(find "$SNAPSHOT" -type f -print0)
  rm -f "$TEST_SAVE" "$TEST_SAVE".*
  rm -rf "$SNAPSHOT"
  SNAPSHOT=""
  echo "  restored $restored save/settings file(s) the game changed"
}

kill_games() {
  if pgrep -f "$GAME_PATTERN" >/dev/null; then
    pkill -f "$GAME_PATTERN" || true
    sleep 5
  fi
}

# Keeps muting the game's audio streams while it runs (streams come and go with scenes).
MUTER_PID=""
start_muter() {
  command -v pactl >/dev/null || { echo "  pactl not found, the game will be audible"; return; }
  (
    while true; do
      pactl -f json list sink-inputs 2>/dev/null | python3 -c '
import json, sys
try:
    inputs = json.load(sys.stdin)
except Exception:
    sys.exit()
for i in inputs:
    p = i.get("properties", {})
    text = " ".join(str(p.get(k, "")) for k in ("application.name", "application.process.binary", "media.name")).lower()
    if not i.get("mute") and ("lethal company" in text or "wine" in text):
        print(i["index"])
' | while read -r idx; do pactl set-sink-input-mute "$idx" 1; done
      sleep 1
    done
  ) &
  MUTER_PID=$!
}
stop_muter() {
  if [ -n "$MUTER_PID" ]; then
    kill "$MUTER_PID" 2>/dev/null || true
    MUTER_PID=""
  fi
}

cleanup() {
  kill_games
  stop_muter
  [ -n "$SNAPSHOT" ] && [ -d "$SNAPSHOT" ] && restore_saves
  restore_profiles
}
trap cleanup EXIT

# deploy <profile dir> <keep dev dir afterwards: 1/0>
deploy() {
  local profile="$1" keep="$2"
  local plugins="$profile/BepInEx/plugins"
  local dev="$plugins/YoutubeOnTV-Dev"

  # BepInEx loads only the newest copy of a plugin GUID, and a dev build usually shares the
  # installed version number, so any installed copy has to step aside for the run.
  while IFS= read -r -d '' dll; do
    mv "$dll" "$dll.disabled-by-tests"
    DISABLED+=("$dll")
    echo "  disabled for this run: ${dll#$plugins/}"
  done < <(find "$plugins" -name YoutubeOnTV.dll -not -path "$dev/*" -print0)

  mkdir -p "$dev"
  [ "$keep" -eq 1 ] || REMOVE_DEV_DIRS+=("$dev")
  cp "$MOD_BIN/YoutubeOnTV.dll" "$MOD_BIN/fallback.mp4" "$HARNESS_BIN/YoutubeOnTV.TestHarness.dll" \
     "$ROOT/tests/assets/control.mp4" "$dev/"
  # Start every run with an empty video cache so the download path is really exercised.
  # yt-dlp.exe and ffmpeg.exe stay, as they would on a player's machine after first launch.
  rm -rf "$dev/cache"
  rm -f "$profile/BepInEx/$RESULTS_NAME"
  echo "  $(basename "$profile"): YoutubeOnTV.dll $(sha1sum "$dev/YoutubeOnTV.dll" | cut -c1-12)"
}

KEEP_HOST_DEV=0
if [ "$PROFILE_NAME" = "YoutubeOnTV-Test" ]; then KEEP_HOST_DEV=1; fi
deploy "$PROFILE" "$KEEP_HOST_DEV"

REPORTS=("$PROFILE/BepInEx/$RESULTS_NAME")
if [ "$MULTIPLAYER" -eq 1 ]; then
  if [ ! -d "$CLIENT_PROFILE/BepInEx/core" ]; then
    echo "  creating client profile $CLIENT_PROFILE_NAME from $PROFILE_NAME"
    mkdir -p "$CLIENT_PROFILE"
    (cd "$PROFILE" && tar --exclude='./BepInEx/plugins/YoutubeOnTV-Dev/cache' --exclude='./BepInEx/*.log' -cf - .) \
      | (cd "$CLIENT_PROFILE" && tar -xf -)
  fi
  CLIENT_PROFILE="$(realpath "$CLIENT_PROFILE")"
  deploy "$CLIENT_PROFILE" 1
  REPORTS+=("$CLIENT_PROFILE/BepInEx/$RESULTS_NAME")
fi

rm -f "$TEST_SAVE"

# The game rewrites its settings file (and a few others) while running. Snapshot everything
# but the logs so a test run can never change the player's settings or saves.
SNAPSHOT="$(mktemp -d)"
cp -a "$LOCALLOW/." "$SNAPSHOT/"

# ---- Launch ----
echo "[3/5] Launching Lethal Company..."
if [ "$MUTE" -eq 1 ]; then start_muter; fi
HOST_ARG="--youtubeontv-autotest"
if [ "$MULTIPLAYER" -eq 1 ]; then HOST_ARG="--youtubeontv-autotest-mp-host"; fi
"$STEAM_ROOT/steam.sh" -applaunch "$APP_ID" \
  --doorstop-enabled true \
  --doorstop-target-assembly "Z:$PROFILE/BepInEx/core/BepInEx.Preloader.dll" \
  "$HOST_ARG" >/dev/null 2>&1 &

for _ in $(seq 1 120); do
  pgrep -f "$GAME_PATTERN" >/dev/null && break
  sleep 1
done
pgrep -f "$GAME_PATTERN" >/dev/null || die "the game did not start within 120s"

# Steam will not start a second copy, so the client runs through Proton in Steam's runtime
# container, in the same prefix. It is started once the host has a video playing.
CLIENT_STARTED=0
start_client() {
  echo "  starting the client instance through Proton in the Steam runtime"
  STEAM_COMPAT_DATA_PATH="$STEAM_ROOT/steamapps/compatdata/$APP_ID" \
  STEAM_COMPAT_CLIENT_INSTALL_PATH="$STEAM_ROOT" \
  SteamAppId="$APP_ID" SteamGameId="$APP_ID" \
  WINEDLLOVERRIDES="winhttp=n,b" \
    "$STEAM_RUNTIME" --verb=waitforexitandrun -- "$PROTON" waitforexitandrun "$GAME_DIR/Lethal Company.exe" \
      --doorstop-enabled true \
      --doorstop-target-assembly "Z:$CLIENT_PROFILE/BepInEx/core/BepInEx.Preloader.dll" \
      --youtubeontv-autotest-mp-client >"$SCRATCH_CLIENT_LOG" 2>&1 &
  CLIENT_STARTED=1
}
SCRATCH_CLIENT_LOG="$(mktemp)"

# ---- Wait ----
echo "[4/5] Running scenarios (watchdog ${TIMEOUT}s)..."
STATUS="timeout"
START=$(date +%s)
declare -A SEEN
while [ $(( $(date +%s) - START )) -lt "$TIMEOUT" ]; do
  all_complete=1
  for report in "${REPORTS[@]}"; do
    who="$(basename "$(dirname "$(dirname "$report")")")"
    if [ -f "$report" ]; then
      SEEN[$who]=$(python3 - "$report" "${SEEN[$who]:-0}" "$who" <<'PY'
import json, sys
try:
    results = json.load(open(sys.argv[1]))["results"]
except Exception:
    print(sys.argv[2]); sys.exit()
for r in results[int(sys.argv[2]):]:
    print(f"  [{sys.argv[3]}] {'PASS' if r['passed'] else 'FAIL'}  {r['name']}", file=sys.stderr)
print(len(results))
PY
)
      grep -q '"complete": true' "$report" 2>/dev/null || all_complete=0
    else
      all_complete=0
    fi
  done

  if [ "$MULTIPLAYER" -eq 1 ] && [ "$CLIENT_STARTED" -eq 0 ] && grep -q '"mp_host_video_starts"' "${REPORTS[0]}" 2>/dev/null; then
    start_client
  fi

  if [ "$all_complete" -eq 1 ]; then
    STATUS="complete"
    break
  fi
  if ! pgrep -f "$GAME_PATTERN" >/dev/null; then
    STATUS="exited"
    break
  fi
  sleep 2
done

sleep 3
kill_games

# ---- Collect ----
SUFFIX=""
if [ "$MULTIPLAYER" -eq 1 ]; then SUFFIX="-multiplayer"; fi
OUT="$ROOT/tests/results/$(date +%Y%m%d-%H%M%S)-$PROFILE_NAME$SUFFIX"
mkdir -p "$OUT"
cp "$LOCALLOW/Player.log" "$OUT/" 2>/dev/null || true
for report in "${REPORTS[@]}"; do
  bep="$(dirname "$report")"
  who="$(basename "$(dirname "$bep")")"
  cp "$bep/LogOutput.log" "$OUT/LogOutput-$who.log" 2>/dev/null || true
  cp "$report" "$OUT/results-$who.json" 2>/dev/null || true
done
if [ "$MULTIPLAYER" -eq 1 ]; then cp "$SCRATCH_CLIENT_LOG" "$OUT/proton-client.log"; fi
rm -f "$SCRATCH_CLIENT_LOG"
restore_saves

echo "[5/5] Report ($STATUS) -> ${OUT#$ROOT/}"
python3 - "$STATUS" "$OUT"/results-*.json <<'PY'
import json, os, sys
status, files = sys.argv[1], sys.argv[2:]
if not files or not os.path.exists(files[0]):
    print("  No results were written. Check the logs in the report folder.")
    sys.exit(1)
failed = 0
for path in files:
    report = json.load(open(path))
    print(f"\n  {os.path.basename(path)}")
    for r in report["results"]:
        mark = "PASS" if r["passed"] else "FAIL"
        print(f"  {mark}  {r['name']:<40} {r['detail'][:150]}")
    print(f"  {report['passed']} passed, {report['failed']} failed{'' if report['complete'] else ' (incomplete)'}")
    failed += report["failed"] + (0 if report["complete"] else 1)
print(f"\n  run {status}")
sys.exit(0 if status == "complete" and failed == 0 else 1)
PY
