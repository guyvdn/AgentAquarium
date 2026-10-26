# Idle rendering measurements

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
2. Wait at least three seconds after the last session change, with the pointer still.
3. Open Edge's browser task manager with Shift+Esc and identify the aquarium renderer by its process ID. Let the first sample populate. Record renderer CPU separately from GPU-process CPU.
4. Generate a session/tool event: animation should resume at no more than 24 fps, then stop after three quiet seconds.
5. Switch tabs and return: hidden time should not be replayed. Enable reduced motion: status and hover must still work without animation.

Scheduler regressions are covered by `npm run test:js`. The temporary measurement fixture and raw samples were kept outside the repository, under `%TEMP%/aquarium-perf`.
