# Idle rendering measurements

## Current behaviour: continuous swimming

The user chose continuous visible movement after trying the static-idle version. The current loop caps rendering at 24 fps after session events and 12 fps when quiet, stops when hidden, and uses still frames only for reduced motion. Small simulation steps preserve swim speed at the lower render cadence. The static-idle numbers below are historical, not the current default.

Retested on 2026-10-26 in the same visible Edge fixture: six idle sessions, two chests, 1280 x 800 CSS pixels, DPR 1. A 10-second CPU sample measured the renderer at **4.90% of one core**, compared with the original 39.34% (about 87.5% lower). GPU-process CPU was **38.10% of one core**, versus the original 137.24% (about 72% lower). On this 16-thread machine those current values are approximately 0.31% and 2.38% of total machine CPU, respectively. They are process CPU measurements, not hardware GPU utilization.

A 100-second instrumentation interval recorded about 8.9 rendered frames per second under the 12 fps cap, zero Schools-list replacements, and `document.hidden === false`. Rendering remains continuous; browser frame pacing and timer alignment mean a cap is not a promised frame rate. Raw samples are under `%TEMP%/aquarium-perf/continuous-cpu.json`.

## Earlier static-idle experiment

Measured 2026-10-26 on the user's 16-thread Windows machine, in a separate visible Microsoft Edge window. The scene held six unchanged idle sessions and two usage chests at 1280 x 800 CSS pixels, device pixel ratio 1. Both versions used the same local fixture, browser, viewport and renderer PID 6676. The aquarium stayed visible (`document.hidden === false`).

Before changing code, inspection found unconditional simulation and full-canvas drawing on every `requestAnimationFrame`, including ambient light, weeds, motes, bubbles and fish. The Schools DOM was rebuilt for every 500 ms snapshot. CSS waiting indicators also animated indefinitely.

Edge's browser task manager (Shift+Esc, read through Windows accessibility) showed renderer CPU at **40.6% of one core before**, and **0.1% after** the scene settled.

An independent 10-second sample of Edge's `SystemInfo.getProcessInfo` CPU counters measured:

- Renderer: 39.34% to 0.09% of one core.
- GPU process: 137.24% to 0.76% of one core, equivalent to approximately 8.58% to 0.048% of the 16-thread machine.

These GPU figures are **GPU-process CPU time**, matching the original report's process measurement; they are not hardware GPU-engine utilization. Other applications and the normal Edge profile were outside the isolated browser process sample.

Canvas instrumentation measured 58.9 full draws per second and about two Schools-list replacements per second before. After settling, a 38.9-second interval had zero canvas draws, animation callbacks and panel replacements. The page still receives snapshots, so CPU is low rather than literally zero. Clock deadlines or user input can cause an occasional still-frame redraw.

## Recheck

1. Keep the aquarium visible at the same size. Do not minimize it or fully cover it: Edge occlusion throttling invalidates a visible-page comparison.
2. Wait at least three seconds after the last session change, with the pointer still. Confirm that the fish keep swimming at the quiet cadence.
3. Open Edge's browser task manager with Shift+Esc and identify the aquarium renderer by its process ID. Let the first sample populate. Record renderer CPU separately from GPU-process CPU.
4. Generate a session/tool event: animation should use no more than 24 fps, then return to the lower, continuous cadence after three quiet seconds.
5. Switch tabs and return: hidden time should not be replayed. Enable reduced motion: status and hover must still work without animation.

Scheduler regressions are covered by `npm run test:js`. The temporary measurement fixture and raw samples were kept outside the repository, under `%TEMP%/aquarium-perf`.
