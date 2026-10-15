# Agent Aquarium

A live aquarium of your AI coding agents. Each session is a school of fish: the parent leads, its subagents swim with it.

![Four demo sessions: the red school shows a parent with its subagents and their own, duller subagents](docs/screenshot.png)

| Signal | Fish |
|---|---|
| Context size (tokens) | Body length (sqrt scale) |
| Activity (working, tool calls/min) | Swim speed, tail beat, bubbles |
| Turn finished, your move | Slow drift near the sand |
| Blocked mid-turn on you (permission prompt, question, plan approval) | Same, with a pulsing amber glow |
| Generation | Hue = session; each generation is less saturated and darker. Parents also glow, wear two pale bands and a tall dorsal fin |
| Tool error | Red flash |
| Compaction | Squeeze + burst of bubbles; the fish shrinks |
| Subagent finished | Swims off and fades |

The treasure chest on the sand is your plan's **5-hour limit**: the gold is what is left, the lid opens as far as there is gold to show, and spending lets coins float away. It glows amber below 30% and pulses red below 10%; when the window resets it refills with a burst. Grey means the numbers are old (over 30 minutes, or the window has reset since). Hover it for the weekly limit too.

The diver is **you**. It swims to whichever agent has waited on you longest, agents blocked mid-turn before ones that merely finished, and climbs back up the boat's ladder when nobody waits. The badge over its head counts who is waiting, amber when one of them is blocked; hover it for the list with how long each has waited. "Blocked" comes from Claude Code's live-session registry (`status: "waiting"`, interactive CLI only) and, for SDK hosts such as T3 Code, from an `AskUserQuestion` or `ExitPlanMode` still unanswered in the transcript. A permission prompt in an SDK host leaves no trace in either: install the plugin below to see those too. Without it, such a session shows as working until it stalls.

## Run

```bash
dotnet run --project src/AgentAquarium --launch-profile live   # your real Claude Code sessions
dotnet run --project src/AgentAquarium --launch-profile demo   # synthetic agents, for working on the visuals
```

Open http://localhost:5180.

To give it a window of its own, install it as an app: in Edge, **⋯ › Apps › Install this site as an app**; in Chrome, **⋮ › Cast, save and share › Install page as app**. The window only has something to show while the server runs.

### Plugin: permission prompts in SDK hosts

In Claude Code, run once:

```
/plugin install agent-aquarium --marketplace guyvdn/AgentAquarium
```

It adds a `PermissionRequest` hook (`plugin/hooks/hooks.json`) that posts each permission prompt to `http://localhost:5180/api/hooks/permission-request`. The aquarium matches it to the tool call still open in the transcript, and the session shows as blocked until that call's result comes in. No hook fires when a prompt is approved, so an approved long-running command stays amber until it finishes. CLI sessions ignore the hook: the live-session registry already reports their prompts, approvals included. The hook never answers the prompt, and when the aquarium is not running the post fails quietly. Running on another port? Change the URL in the hook. Needs Claude Code 2.1.275 or later.

### Settings

Settings are in `src/AgentAquarium/appsettings.json`:

- `Sources:ClaudeCode:ProjectRoots`: transcript folders. The default is `~/.claude/projects`. Add another profile's `projects` folder to watch it too.
- `Sources:ClaudeCode:LookBack`: fallback only. A session is shown while its process runs, according to Claude Code's live-session registry (`<config>/sessions/<pid>.json`, checked against the process start time because pids get reused). If that registry is missing (older Claude Code), sessions whose transcript changed within this window are shown instead.
- `Sources:ClaudeCode:UsageFile`: where the chest's numbers come from, Claude Code's own cache of its `/usage` call (`cachedUsageUtilization`). Default `~/.claude.json`; a profile with its own `CLAUDE_CONFIG_DIR` keeps it at `<dir>/.claude.json`. Empty string, or `Sources:ClaudeCode:Enabled=false`, turns the chest off: it only appears once a 5-hour window has been reported. One file is read even when `ProjectRoots` lists several profiles. `UsagePollInterval` (default 10 s) is how often its timestamp is checked; it is only parsed when it changed. The aquarium never calls the usage API itself: that endpoint is rate limited hard and shares its budget with Claude Code, so the numbers are only as fresh as Claude Code's last fetch (roughly every 20 minutes while it runs). Undocumented, checked against Claude Code 2.1.300.
- `Tank:StallAfter` / `ForgetAfter` / `DoneLinger`: when silent agents turn idle, leave, and when finished subagents leave.

## Development

```bash
dotnet build && dotnet test          # Meziantou + .NET analyzers (latest-all), warnings are errors
npm install && npm run lint          # ESLint, Stylelint, html-validate for wwwroot
npm run format                       # Prettier; dotnet format for C#
```

## How it works

```mermaid
flowchart LR
  T[Claude Code transcripts<br/>~/.claude/projects/**.jsonl] --> CC[ClaudeCodeSource<br/>polls + tails]
  D[DemoSource] --> S
  CC --> S[IAgentSink<br/>AgentRegistry]
  U[~/.claude.json<br/>cachedUsageUtilization] --> CU[ClaudeUsageSource] --> UB[IUsageSink<br/>UsageBoard]
  S -->|SSE /api/agents/stream, 2 Hz| B[Browser canvas<br/>boids + procedural fish]
  UB -->|SSE event: usage, on change| B
```

- **`Domain/AgentModel.cs`** is the seam. Sources only speak `AgentDescriptor` (identity, parent, depth) and `AgentObservation` (context, tool call, error, compaction, status). Nothing downstream knows about Claude Code.
- **`Sources/ClaudeCode`** reads `<session>.jsonl` for the main agent and `<session>/subagents/agent-<id>.jsonl` plus `.meta.json` for subagents. A subagent's parent is the agent whose transcript contains the spawning `tool_use` id, so nesting deeper than one level works without special cases. Context = `input + cache_read + cache_creation + output` tokens of the latest assistant message. The transcript format is undocumented (checked against Claude Code 2.1.300), so the parser ignores whatever it does not recognise.
- **`Tank/AgentRegistry`** holds state, derives activity and staleness, and prunes.
- **`wwwroot/js`** is plain ES modules on Canvas 2D, with no build step. `fish.js` handles steering and drawing, `scenery.js` the water, `chest.js` the treasure chest, `diver.js` the diver, `colors.js` the session hue × generation tone.

### Adding a source

Implement a `BackgroundService` that calls `IAgentSink.Upsert` / `Observe` / `Remove`, and register it in `Program.cs`. `DemoSource` is the minimal example. Use `Source` on the descriptor to say where an agent came from. For a push-based source (hooks, OpenTelemetry, another tool's webhook), add an endpoint that maps the payload onto the same sink calls.

## License

[MIT](LICENSE)
