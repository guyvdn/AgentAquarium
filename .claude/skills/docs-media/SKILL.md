---
name: docs-media
description: How to regenerate the README media, docs/screenshot.png and docs/demo.gif, from the demo source. Use when the screenshot or demo GIF is stale after a visual change (new creature, HUD element, state colour), when asked to update, retake or re-record them, or when capturing the tank for docs in any other way.
---

# Docs media

`docs/screenshot.png` (1600x900 PNG, ~300 KB) and `docs/demo.gif` (960x540, 12 fps, 9 s, ~4 MB)
are both shot from the demo source. Retake them whenever the tank gains something a reader should
see. As of 2026-10-26 that means: sessions labelled with their titles and subagents with their
tasks, the diver with its badge, both labelled usage chests, amber "needs you", subagent schools, the slow glow on
the Schools dots of the sessions the diver comes for, and coins floating away from their provider's chest from token
spend (the demo emits `SpendObserved`). No fish is hovered: the user found the tooltip distracting
in the README media (2026-10-22). `HOVER=1` brings it back, with its highlighted Schools row.

## Run it

The current pipeline uses Node, `playwright-core` (installed by `npm install`), Microsoft Edge, ffmpeg and the .NET SDK. It never uses the live port 5180. Both real sources are disabled in capture demos.

```powershell
dotnet build src/AgentAquarium -o artifacts/agent-aquarium-media/bin -p:UseAppHost=false
node .claude/skills/docs-media/capture.mjs http://localhost:5205 artifacts/agent-aquarium-media/docs artifacts/agent-aquarium-media/bin/AgentAquarium.dll
```

The optional DLL argument starts a fresh demo on an unused spare port and stops that owned process after capture. Without it, the URL must already serve the demo. The helper waits for a readable scene with subagents, a waiting session and gold in both chests, then uses capture-only module access to spread roots and children clear of the Schools panel. Their subsequent swimming uses the real simulation. No tooltips are shown.

It writes `screenshot.png`, `demo.gif` and temporary frame PNGs to the output folder. Inspect both deliverables before copying them to `docs/`. Keep the GIF below 5 MB. A typical run takes about one minute; an unsuitable already-running random demo can time out, so use the fresh-DLL argument.

The older Git Bash `capture.sh` entry point remains supported. It builds in a temp folder and runs agent-browser; its slower screenshot loop can take 5-15 minutes. It now disables Codex as well as Claude and loads the shared virtual clock before `tank-boot.js`. Commit that script with its executable bit.

### Promotion video

The previously external promotion sources now live in `promo/` beside this skill. They preserve the approved 21.5-second square format and generated soundtrack while showing Claude and Codex together, subagent schools, the diver, two chests and the repository link. Fictional data only.

```powershell
# Serve the current app with the promo assets (leave running during capture):
node .claude/skills/docs-media/promo/server.mjs src/AgentAquarium/wwwroot .claude/skills/docs-media/promo 5203
# In a second terminal:
node .claude/skills/docs-media/promo/render.mjs artifacts/agent-aquarium-media/promo-frames
python .claude/skills/docs-media/promo/music.py artifacts/agent-aquarium-media/music.wav
ffmpeg -y -framerate 30 -i artifacts/agent-aquarium-media/promo-frames/f%04d.jpg -i artifacts/agent-aquarium-media/music.wav -vf scale=1080:1080:flags=lanczos -c:v libx264 -crf 18 -pix_fmt yuv420p -c:a aac -b:a 192k -movflags +faststart -shortest artifacts/agent-aquarium-media/brag.mp4
```

Music synthesis needs numpy. Use `promo/encode.mjs <frames-dir> <original.mp4> <output.mp4>` to preserve the approved AAC soundtrack verbatim, or pass a WAV to normalise and encode a new audio track. `render.mjs` takes an optional comma-separated list of frames for still checks; the following optional argument overrides the base URL. The title-card frame is copied to the upload's first frame, matching the original video. Use that same frame as `brag.jpg`.

## Capture clock

`virtual-clock.js` is shared by the README and promo workflows. It virtualises **setTimeout/clearTimeout and requestAnimationFrame/cancelAnimationFrame**, along with `performance.now` and `Date.now`. The capped animation scheduler uses timers between frame requests, so virtualising only rAF would stall the capture. Freezing transfers already-pending callbacks rather than losing them. Regression tests run with `npm run test:js`.

## How it works, and why

- **It builds into a temp output dir** (`dotnet build -o <work>/bin`). An app already running from
  the worktree's `bin/` locks `AgentAquarium.exe`, and then `dotnet run` fails with MSB3026.
- **Demo launch.** This is the same as the `demo` profile, on a spare port:
  `ASPNETCORE_ENVIRONMENT=Development Sources__ClaudeCode__Enabled=false Sources__Codex__Enabled=false Sources__Demo__Enabled=true ./AgentAquarium.exe --urls http://localhost:5198 --Sources:Demo:Sessions=4`.
  Never use 5180: the user's live app may be on it. Four sessions give enough schools without
  filling the list. Don't change committed config for a shot.
- **Getting everything in frame.** The demo is random, so `capture.sh` polls every ~3.5 s until
  all of these hold at once:
  - the list says "needs you", so the diver is out and the fish is amber;
  - the list has 7-16 rows: schools with subagents, but still readable;
  - with `HOVER=1`, there is a working root to hover;
  - the fast helper checks `/api/usages` for both provider windows below 60% used, so both chests have gold. The legacy Bash helper checks the latest single report below 45%.

  The demo's 5-hour window lasts only 3 minutes, so the chest drains and refills about every 3
  minutes. "needs you" comes and goes in under a minute. A failed `pick.js` eval counts as a bad
  poll and does not stop the run.

- **Holding the pointer on a moving fish** (`HOVER=1` only). `main.js` keeps the fish in module scope, which a page
  script can't reach. So `tank-boot.js` wraps `CanvasRenderingContext2D.prototype.fillText` and
  records where each root's `tokens · status` line was drawn, since `drawLabel` draws it right after
  the name. That line is at `fish.y - 0.55 x length + 4`. Every frame the script dispatches a
  `mousemove` 18 px below it, at `fish.y - 0.55 x length + 22`. The hit radius is
  `max(12, 0.55 x length)`, so the point lands on the body for any root bigger than 20 px. The
  tooltip and `markHoveredRow` follow on their own.
- **Frame stepping.** `agent-browser record` (CDP screencast) only manages about 2 fps on a 1600x900
  canvas, and its WebM plays that back sped up. Each `agent-browser screenshot` takes about 1.9 s,
  so the page clock is paused instead. After `__freeze()`, `performance.now`, `Date.now` and
  `requestAnimationFrame` are virtual, and `__step(2)` advances them two 1/30 s ticks per
  screenshot.
  - The ticks are 1/30 s so the capped render scheduler sees enough timer/frame ticks. The production simulation subdivides elapsed time to preserve swim speed.
  - `Date.now` has to be virtual too. Otherwise the chests' reset times passes in real time
    mid-capture and it greys out as "reset · awaiting update".
  - CSS animations have to follow it as well. They run on the real clock, so the Schools swatch
    pulse played about 28x too fast in the GIF (2026-10-22) and looked like frantic blinking. Each
    `__step` pauses every animation and sets its `currentTime` to the virtual time since it was
    first seen; `main.js` recreates the pulses on each panel rebuild with a clock-derived delay, so
    that keeps their phase.
- **Pacing server updates.** If the clock is paused and the SSE stream isn't, the demo's real-time
  tool calls pile up against a clock running about 28x slower, and the tank fills with bubbles.
  `tank-boot.js` therefore replaces `EventSource` with a wrapper. While frozen, the wrapper queues
  each event with its real arrival time and delivers it when virtual time catches up.
- **Patching before `main.js` runs.** agent-browser has no init-script hook. So the script opens any
  same-origin file (`/styles.css`), installs the patches, then `document.write()`s the fetched
  `index.html`. The global object survives `document.open()`, so the module loads under the
  patches. A 404 URL does not work: Chrome shows its own error page, on another origin.
- **The still** is the first settled frame after freezing (`__step(6)`), at full 1600x900.

## GIF size trade-offs (measured on 135 frames, 9 s)

| Width | fps | Colours | Size                   |
| ----- | --- | ------- | ---------------------- |
| 1120  | 15  | 192     | 7.0 MB                 |
| 960   | 15  | 192     | 5.6 MB                 |
| 1120  | 10  | 128     | 4.6 MB                 |
| 1024  | 10  | 160     | 4.2 MB                 |
| 960   | 12  | 128     | 3.9 MB, the one in use |

The assembly is `ffmpeg … fps=12,scale=960:-1:flags=lanczos,split … palettegen=max_colors=128:stats_mode=diff …
paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle -loop 0`, as in `capture.sh`. Keep the GIF
under about 5 MB; it is downloaded with the README.

## Traps

- **Demo labels must fit above the fish.** `drawLabel` cuts a label wider than 220 px with "…",
  and `tank-boot.js` keys `__labels` by the text drawn, while `pick.js` reads the full name from the
  Schools list. A demo title that gets cut never matches, so its fish is never hovered. Keep the
  titles in `DemoSource.Sessions` under about 28 characters.
- **The loop is not seamless**: it jumps from the last frame back to the first. That was accepted.
  A cross-fade or ping-pong would cost size.
- **Diver over a label.** The diver hovers at the waiting fish and can cover its label. Retake if it
  hides the "needs you" text completely.
- **Don't run it while other agents are editing `wwwroot/`.** The temp build serves the source
  `wwwroot` through the static web assets manifest, so half-made edits end up in the shot.
- **Removing temp folders:** use literal absolute paths. A `cd …; rm -rf dir/*` with a relative
  glob is refused by Claude Code's safety check.
- **If the server dies mid-capture** (for example a Bash background task hitting its timeout),
  the events already queued still play, then the tank stops changing.
- **Git Bash path conversion.** Git Bash rewrites path-like arguments for native programs, and a
  JS argument starting with `//` counts. So `capture.sh` runs agent-browser under
  `MSYS_NO_PATHCONV=1`, but not `taskkill`, whose `//PID` depends on the conversion.
- **ESLint covers these scripts** through the `.claude/skills/**/*.js` block in
  `eslint.config.js`, with browser globals and `sourceType: 'script'`.
