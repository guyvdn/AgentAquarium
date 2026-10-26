# Agent Aquarium

See `README.md` for what the app does, how to run it, and how it is built.

## Behaviour to preserve

- Fish keep swimming while the aquarium is visible. Quiet scenes use a lower frame-rate cap,
  not a freeze. The current caps are 24 fps after updates and 12 fps when quiet. Pause animation
  only when hidden or when reduced motion is requested. This is the user's explicit preference.
- Keep Claude and Codex usage separate, and read local caches/logs rather than calling usage APIs.
- Follow [.claude/skills/docs-media/SKILL.md](.claude/skills/docs-media/SKILL.md) when refreshing
  screenshots, GIFs or the promotion video. The README media has no hovered fish or tooltip.
- [docs/performance.md](docs/performance.md) records the current continuous-motion measurements.
  Its earlier near-zero static-idle results are historical, not the current behaviour.

## Shipping changes

- **No pull requests.** Commit and push straight to `main`.
- **A change to what the tank or HUD shows waits until the user has confirmed it in the running
  app** before it is committed. Start the demo as the `docs-media` skill does, on a spare port such
  as 5198, never 5180, and built to a temp folder (`dotnet build -o`) so it does not lock `bin/`.
- **Nothing is pushed while a question to the user is still open.**
- **Before every push to `main`, have a fresh subagent review the diff** for bugs, races and
  convention drift, and fix what it finds first. Say what the review found, even when it found
  nothing.
- Before committing: `dotnet build && dotnet test`, `npm run lint`, `npm run test:js`, and `npm run format` (or check
  with `npx prettier --check .` and `dotnet format --verify-no-changes`).
- A running aquarium can lock its build output. Prefer a separate output folder (as the capture
  workflow does), or `-p:UseAppHost=false` for checks that do not need the executable. If stopping a
  process is necessary, identify the one using this checkout's output; do not stop every aquarium
  process or interrupt the user's live instance on port 5180.
