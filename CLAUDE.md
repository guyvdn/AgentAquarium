# Agent Aquarium

See `README.md` for what the app does, how to run it, and how it is built.

## Shipping changes

- **No pull requests.** Commit and push straight to `main`.
- **Before every push to `main`, have a fresh subagent review the diff** for bugs, races and
  convention drift, and fix what it finds first. Say what the review found, even when it found
  nothing.
- Before committing: `dotnet build && dotnet test`, `npm run lint`, and `npm run format` (or check
  with `npx prettier --check .` and `dotnet format --verify-no-changes`).
- A running aquarium locks `bin/Debug/net10.0/AgentAquarium.exe`, and `dotnet build` then fails
  with MSB3026/MSB3027. Stop it (`Get-Process AgentAquarium | Stop-Process`), build and test, then
  start it again.
