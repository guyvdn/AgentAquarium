#!/usr/bin/env bash
# Captures the README media from the demo source: a 1600x900 still and a 9 s looping GIF.
# Windows only: Git Bash, with powershell.exe and taskkill for process control.
# Usage, from the repo root:  .claude/skills/docs-media/capture.sh [out-dir]
# Writes screenshot.png, demo.gif, build.log and server.log to out-dir (default $TEMP/aq-media);
# copying the media into docs/ is up to you. The build also refreshes the repo's (ignored) obj/.
# Env: PORT (default 5198, never 5180), FRAMES (135 = 9 s at 15 fps), SCENE_POLLS (150, ~3.5 s each).
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/../../.." && pwd)"
PORT="${PORT:-5198}"
FRAMES="${FRAMES:-135}"
SCENE_POLLS="${SCENE_POLLS:-150}"
URL="http://localhost:$PORT"
# Git Bash rewrites arguments that look like paths ("//..." in JS, too) for native programs.
AB=(env MSYS_NO_PATHCONV=1 agent-browser --session "aq-media-$$")

[ "$PORT" = 5180 ] && { echo "5180 is the live app's port; pick another" >&2; exit 1; }

# PowerShell, not netstat: from Git Bash, netstat -ano did not list the listener (2026-10-15).
ps_out() { powershell.exe -NoProfile -Command "$1" | tr -d '\r'; }
listening() { ps_out "(Get-NetTCPConnection -LocalPort $PORT -State Listen -ErrorAction SilentlyContinue | Measure-Object).Count"; }
if [ "$(listening)" != 0 ]; then
  echo "Something already listens on $PORT; the shots would come from it. Pick another PORT." >&2
  exit 1
fi

mkdir -p "${1:-${TEMP:-/tmp}/aq-media}"
# Mixed form (C:/...): agent-browser runs without path conversion, so it needs a Windows path.
OUT="$(cygpath -m "$(cd "${1:-${TEMP:-/tmp}/aq-media}" && pwd)")"
# Build output and frames live in their own temp dir, never under OUT, so cleanup can't hit anything else.
WORK="$(cygpath -m "$(mktemp -d "${TEMP:-/tmp}/aq-media-work.XXXXXX")")"
BIN="$WORK/bin"
WIN_BIN="$(cygpath -w "$BIN")"

# Kill by executable path, so only the server this script started is ever touched. $! is an MSYS pid.
stop_server() {
  local pid
  for pid in $(ps_out "Get-CimInstance Win32_Process -Filter \"Name='AgentAquarium.exe'\" | Where-Object { \$_.ExecutablePath -like '$WIN_BIN\*' } | ForEach-Object ProcessId"); do
    taskkill //PID "$pid" //F >/dev/null 2>&1 || true
  done
  # The subshell holds bin/ as its cwd; give it 10 s to exit, then kill it.
  if [ -n "${SERVER_JOB:-}" ]; then
    for _ in $(seq 1 20); do kill -0 "$SERVER_JOB" 2>/dev/null || break; sleep 0.5; done
    kill -9 "$SERVER_JOB" 2>/dev/null || true
    wait "$SERVER_JOB" 2>/dev/null || true
  fi
}
cleanup() {
  "${AB[@]}" close >/dev/null 2>&1 || true
  stop_server
  # taskkill returns before the exe releases its image lock; one retry, and never fail the run over it.
  rm -rf -- "$WORK" 2>/dev/null || { sleep 2; rm -rf -- "$WORK" || true; }
}
trap cleanup EXIT

# Build into a temp dir: an app already running from the worktree's bin/ locks AgentAquarium.exe.
if ! dotnet build "$REPO/src/AgentAquarium" -o "$BIN" -nologo >"$OUT/build.log" 2>&1; then
  tail -n 30 "$OUT/build.log" >&2
  echo "Build failed; full log in $OUT/build.log" >&2
  exit 1
fi

( cd "$BIN" && ASPNETCORE_ENVIRONMENT=Development Sources__ClaudeCode__Enabled=false Sources__Demo__Enabled=true \
    ./AgentAquarium.exe --urls "$URL" --Sources:Demo:Sessions=4 >"$OUT/server.log" 2>&1 ) &
SERVER_JOB=$!
up=false
for _ in $(seq 1 60); do curl -sf --max-time 5 -o /dev/null "$URL/" && { up=true; break; }; sleep 1; done
if [ "$up" != true ]; then
  tail -n 30 "$OUT/server.log" >&2
  echo "Server did not come up on $URL within 60 s" >&2
  exit 1
fi

"${AB[@]}" set viewport 1600 900 >/dev/null
"${AB[@]}" open "$URL/styles.css" >/dev/null # any same-origin page; tank-boot.js replaces it
"${AB[@]}" eval "$(cat "$HERE/tank-boot.js")" >/dev/null
"${AB[@]}" wait 20000 >/dev/null # let the fish spread and the first subagents spawn

# Wait for a scene worth shooting: someone needs you (diver out, amber), 7-16 rows (schools with
# subagents but a readable list), a working root to hover, and the chest at least half full.
for i in $(seq 1 "$SCENE_POLLS"); do
  s=$("${AB[@]}" eval "$(cat "$HERE/pick.js")" 2>/dev/null | tr -d '"') || s=""
  [[ "$s" =~ ^[0-9]+\|(true|false)\| ]] || s="0|false|null" # a failed eval is just a bad poll
  used=$(curl -s --max-time 5 "$URL/api/usage" | grep -o '"fiveHour":{"usedPercent":[0-9.]*' | grep -o '[0-9.]*$' || echo 100)
  IFS='|' read -r rows needs follow <<<"$s"
  echo "poll $i: rows=$rows needsYou=$needs follow=$follow used=$used%"
  if [ "$needs" = true ] && [ "$follow" != null ] && [ "$rows" -ge 7 ] && [ "$rows" -le 16 ] && [ "${used%.*}" -lt 45 ]; then
    break
  fi
  "${AB[@]}" wait 2000 >/dev/null
done

"${AB[@]}" eval "window.__freeze()" >/dev/null
"${AB[@]}" eval "window.__step(6)" >/dev/null # let the tooltip and row highlight settle
"${AB[@]}" screenshot "$OUT/screenshot.png" >/dev/null

FR="$WORK/frames"
mkdir -p "$FR"
for n in $(seq 1 "$FRAMES"); do
  i=$(printf %03d "$n")
  "${AB[@]}" eval "window.__step(2)" >/dev/null # 2 x 1/30 s = one 15 fps frame
  "${AB[@]}" screenshot "$FR/f$i.png" >/dev/null
done

# 960 wide at 12 fps keeps 9 s under 5 MB; see SKILL.md for the measured alternatives.
ffmpeg -v error -y -framerate 15 -i "$FR/f%03d.png" \
  -vf "fps=12,scale=960:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=128:stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle" \
  -loop 0 "$OUT/demo.gif"

ls -l "$OUT/screenshot.png" "$OUT/demo.gif"
