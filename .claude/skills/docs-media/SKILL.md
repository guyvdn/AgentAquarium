---
name: docs-media
description: Regenerate Agent Aquarium's README screenshot, animated GIF and square promotion video after visual or provider changes, using fictional mixed Claude/Codex sessions and the real tank renderer.
---

# Docs media

Use the current app modules and fictional data. Show both providers, parent/subagent schools,
labelled Claude and Codex chests, and the diver at a waiting session. Keep labels and the Schools
panel readable. **No hovered fish or tooltip** in README media: the user found it distracting.

The user also rejected fish freezing between updates. Visible fish keep swimming; current caps
are 24 fps after updates and 12 fps when quiet. Captures must reflect that behaviour. Only hidden
and reduced-motion views should be still. See [the performance measurements](../../../docs/performance.md).

## README screenshot and GIF

Prerequisites: .NET SDK, Node, `npm ci`, Microsoft Edge and ffmpeg. Run from the repository root.

```powershell
dotnet build src/AgentAquarium -o artifacts/agent-aquarium-media/bin -p:UseAppHost=false
node .claude/skills/docs-media/capture.mjs http://localhost:5205 artifacts/agent-aquarium-media/docs artifacts/agent-aquarium-media/bin/AgentAquarium.dll
```

The optional DLL argument starts a fresh, owned demo server on an unused spare port, with both
real sources disabled, and stops it after capture. Without that argument, the URL must already
serve the mixed-provider demo. Never use 5180 or stop the user's live aquarium.

Prefer a fresh demo: the random scene can otherwise have too many subagents, no blocked session,
or empty chests. Startup includes a Codex question held for one minute. The helper waits for
7-16 rows, someone needing input, and both provider limits below 60% used. It then sets a
capture-only starting layout clear of the panel; subsequent movement is the real simulation.

Outputs:

- `screenshot.png`: 1600 x 900, about 330 KB.
- `demo.gif`: 960 x 540, 12 fps, nine seconds. The helper rejects a GIF above 5 MB.
- `frames/`: temporary PNGs, not for committing.

Inspect the still and animated GIF before copying them to `docs/`. Check that the diver does not
hide the waiting label, both chests are legible, the fish move, and no tooltip appears. The GIF's
jump at the loop boundary is accepted. Its swatch glow should breathe at the real 2.4-second
period, not blink rapidly; measure several frames if needed. The 2026-10-26 take measured 2.42 s.

The GIF uses a 128-colour palette, Bayer dithering and a 960-pixel width to stay under the size
budget. About one minute is typical for the fast capture, excluding build time.

## Promotion video

The reproducible sources live in `promo/`. Preserve the approved square 1080 x 1080 format,
30 fps, 21.5-second duration, glass captions and fictional sessions. The sequence shows Claude
and Codex, a school forming, the diver, two usage chests and the repository link. The diver shot
uses 2x simulation speed, as in the approved original, so it can arrive within the short shot.

```powershell
# Leave this local server running during rendering:
node .claude/skills/docs-media/promo/server.mjs src/AgentAquarium/wwwroot .claude/skills/docs-media/promo 5203
# In a second terminal:
node .claude/skills/docs-media/promo/render.mjs artifacts/agent-aquarium-media/promo-frames
python .claude/skills/docs-media/promo/music.py artifacts/agent-aquarium-media/music.wav
node .claude/skills/docs-media/promo/encode.mjs artifacts/agent-aquarium-media/promo-frames artifacts/agent-aquarium-media/music.wav artifacts/agent-aquarium-media/brag.mp4
```

Music synthesis needs numpy. To keep an already approved soundtrack exactly, pass its MP4 to
`encode.mjs` instead of a WAV: it copies the AAC stream verbatim. A WAV is loudness-normalised
and AAC-encoded. The encoder also writes `brag.jpg`; the renderer places that title card in the
video's first frame, matching the approved upload thumbnail.

For still checks, `render.mjs` accepts a comma-separated frame list after the output directory,
then an optional base URL. For example: `render.mjs artifacts/promo-check 170,260,380,480,610`.
Review the encoded video too, not just source stills. Confirm readable captions, intact URLs,
two labelled chests, normal glow speed and no personal sessions or paths.

Keep the storyboard and share copy in `promo/brag-plan.md` and `promo/share-copy.txt` current.
The latest local export is `%USERPROFILE%/Videos/agent-aquarium-brag-codex/`: `brag.mp4`,
`brag.jpg`, `brag-plan.md` and `share-copy.txt`. Frame folders and MP4 exports stay outside Git;
the source scripts and README PNG/GIF are versioned. Do not overwrite the original approved
export when making a new version.

## Shared capture clock

Load `virtual-clock.js` before either boot script. It virtualises `performance.now`, `Date.now`,
**setTimeout/clearTimeout and requestAnimationFrame/cancelAnimationFrame**. Virtualising only
rAF stalls the capped renderer, because it waits on timers between frame requests. Freezing
transfers pending callbacks rather than dropping them; `npm run test:js` covers that behaviour.

README capture paces real demo SSE events against virtual time so a slow screenshot does not
pile up tool effects. The promo uses a seeded scripted feed. Both drive CSS animations from
the same virtual clock; real-time CSS was the cause of the earlier fast-blinking GIF. Keep small
clock ticks so scheduled render timers and frame callbacks advance together. Production physics
subdivides elapsed time to preserve swim speed at the quiet cadence.

Don't capture while another agent edits `wwwroot/`: the static assets are served from the source
tree even when the .NET output is elsewhere. Capture-only module access is for layout/camera
control; do not add that exposure to the production app.

## Legacy Git Bash capture

`capture.sh` remains available on Windows with Git Bash, PowerShell, agent-browser and ffmpeg.
It is slower (typically 5-15 minutes), builds to a temporary directory and refuses occupied
ports or port 5180. Run `capture.sh <output-directory>`; `PORT` defaults to 5198.

Only this legacy entry point supports `HOVER=1`; leave it off for README media. Its helper
`pick.js` matches full Schools labels against drawn labels, so keep demo titles short enough
not to be truncated. The Bash scene check uses the latest single `/api/usage` report below
45% used, whereas the fast helper checks both sources through `/api/usages`.

Preserve `capture.sh`'s executable bit (`git add --chmod=+x`). Its `MSYS_NO_PATHCONV=1` avoids
Git Bash rewriting JavaScript arguments as Windows paths. Use literal, verified absolute paths
for cleanup, and stop only the capture's owned server. Browser `.js` and Node `.mjs` helpers are
covered by ESLint; check their formatting explicitly because the root Prettier scope is limited
to frontend/tooling files.
