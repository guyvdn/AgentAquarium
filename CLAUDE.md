# Agent Aquarium

See `README.md` for what the app does, how to run it, and how it is built.

## Shipping changes

- **No pull requests.** Commit and push straight to `main`.
- **A change to what the tank or HUD shows waits until the user has confirmed it in the running
  app** before it is committed. Start the demo as the `docs-media` skill does, on a spare port such
  as 5198, never 5180, and built to a temp folder (`dotnet build -o`) so it does not lock `bin/`.
- **Nothing is pushed while a question to the user is still open.**
- **Before every push to `main`, have a fresh subagent review the diff** for bugs, races and
  convention drift, and fix what it finds first. Say what the review found, even when it found
  nothing.
- Before committing: `dotnet build && dotnet test`, `npm run lint`, and `npm run format` (or check
  with `npx prettier --check .` and `dotnet format --verify-no-changes`).
- A running aquarium locks `bin/Debug/net10.0/AgentAquarium.exe`, and `dotnet build` then fails
  with MSB3026/MSB3027. Stop it (`Get-Process AgentAquarium | Stop-Process`), build and test, then
  start it again.
