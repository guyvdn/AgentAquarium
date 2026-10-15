---
name: docs-media
description: How to regenerate the README media, docs/screenshot.png and docs/demo.gif, from the demo source. Use when the screenshot or demo GIF is stale after a visual change (new creature, HUD element, state colour), when asked to update, retake or re-record them, or when capturing the tank for docs in any other way.
---

# Docs media

`docs/screenshot.png` (1600x900 PNG, ~300 KB) and `docs/demo.gif` (960x540, 12 fps, 9 s, ~4 MB)
are both shot from the demo source. Retake them whenever the tank gains something a reader should
see. As of 2026-10-15 that means: the diver with its badge, the chest, amber "needs you", subagent
schools, a hovered fish's tooltip and its highlighted Schools row, and coins trickling into the
chest from token spend (the demo emits `SpendObserved`).

## Run it

**Windows only:** Git Bash, with `powershell.exe` and `taskkill` for process control. It also needs
`agent-browser`, `ffmpeg` and the .NET SDK.

```bash
.claude/skills/docs-media/capture.sh "$TEMP/aq-media"   # from the repo root; PORT defaults to 5198
cp "$TEMP/aq-media/screenshot.png" "$TEMP/aq-media/demo.gif" docs/
```

- **How long:** about 5 minutes of capture (4m45s on 2026-10-15, about 1.9 s per frame), plus the
  wait for a scene. That wait was 0-4 minutes in practice; the worst case is the full
  `SCENE_POLLS` (150 polls of about 3.5 s, roughly 9 minutes), after which it shoots anyway. Run it
  in the background with a `timeout` of 20 minutes or more.
- **What it writes:** `screenshot.png`, `demo.gif`, `build.log` and `server.log` in the out-dir. The
  build also refreshes the repo's git-ignored `obj/`. The build output and frames go in a separate
  `mktemp -d` dir, which is deleted on exit, along with closing the browser and killing the server
  it started.
- **What it refuses:** port 5180, and any port something already listens on. Otherwise the shots
  would come from that app.
- **Check before copying:** open both files before copying them into `docs/`, because the scene is
  random.
- **Commit `capture.sh` with its exec bit:** `git add --chmod=+x .claude/skills/docs-media/capture.sh`.

| File           | What it does                                                                                  |
| -------------- | --------------------------------------------------------------------------------------------- |
| `capture.sh`   | Builds, runs the demo, waits for a good scene, shoots the still and the frames, makes the GIF |
| `tank-boot.js` | Boots the tank with a pausable clock, paced server updates and pointer-follow                 |
| `pick.js`      | Reads the Schools list, picks the fish to hover, reports whether the scene is ready           |

## How it works, and why

- **It builds into a temp output dir** (`dotnet build -o <work>/bin`). An app already running from
  the worktree's `bin/` locks `AgentAquarium.exe`, and then `dotnet run` fails with MSB3026.
- **Demo launch.** This is the same as the `demo` profile, on a spare port:
  `ASPNETCORE_ENVIRONMENT=Development Sources__ClaudeCode__Enabled=false Sources__Demo__Enabled=true ./AgentAquarium.exe --urls http://localhost:5198 --Sources:Demo:Sessions=4`.
  Never use 5180: the user's live app may be on it. Four sessions give enough schools without
  filling the list. Don't change committed config for a shot.
- **Getting everything in frame.** The demo is random, so `capture.sh` polls every ~3.5 s until
  all of these hold at once:
  - the list says "needs you", so the diver is out and the fish is amber;
  - the list has 7-16 rows: schools with subagents, but still readable;
  - there is a working root to hover;
  - `/api/usage` reports `fiveHour.usedPercent` < 45, so the chest has gold.

  The demo's 5-hour window lasts only 3 minutes, so the chest drains and refills about every 3
  minutes. "needs you" comes and goes in under a minute. A failed `pick.js` eval counts as a bad
  poll and does not stop the run.

- **Holding the pointer on a moving fish.** `main.js` keeps the fish in module scope, which a page
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
  - The ticks are 1/30 s because `main.js` caps a frame at 50 ms (`MAX_DT`). A single 1/15 s step
    would be clipped and slow the motion.
  - `Date.now` has to be virtual too. Otherwise the chest's reset time passes in real time
    mid-capture and it greys out as "reset · awaiting update".
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
