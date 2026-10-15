---
name: plugin-hooks
description: How the Agent Aquarium Claude Code plugin and its PermissionRequest hook work, and how to change, test and ship them. Use when editing anything under plugin/, the /api/hooks/permission-request endpoint, PermissionRequests or MarkPermissionWait/PickToolCall in ClaudeCodeSource, or when a permission prompt in an SDK host such as T3 Code does not make the agent show as Waiting.
---

# Plugin hooks

The plugin exists for SDK hosts such as T3 Code. A permission prompt there leaves no trace in the
transcript or the live-session registry, so without the hook the agent shows as Working until it
stalls. The user-facing description is in `README.md` under *Plugin: permission prompts in SDK hosts*.

## How the permission hook works

- **The hook.** `plugin/hooks/hooks.json` is an HTTP hook that POSTs to `http://localhost:5180/api/hooks/permission-request`, with a 2 s timeout. It returns an empty 204, which means "no decision".
- **Hand-off.** The endpoint in `Program.cs` passes each request to `PermissionRequests`, a bounded channel. `ClaudeCodeSource.MarkPermissionWait` drains it on every poll.
- **Matching.** The hook payload has `session_id`, `tool_name`, `tool_input` and, for subagents, `agent_id`. It has **no `tool_use_id`**, so `PickToolCall` matches the request to an open call in the asking agent's transcript:
  - same tool name, made at or before the request;
  - then deep-equal arguments, otherwise the only remaining candidate, otherwise no match.
- **Retry and expiry.** A request with no match is retried for 10 s, then dropped.
- **No `agent_id`** means the main agent asked, so only the main transcript is searched.
- **A matched call becomes an entry in `Waits`.** It clears like any other wait: when the call's result is read, or when the turn ends or a new prompt starts (`TranscriptLine.IsPrompt`).
- **CLI sessions are skipped.** The live-session registry already reports their prompts, approvals included.

All of this was verified by experiment:

- The `tool_use` line is already in the transcript when the hook fires.
- Main-agent and subagent prompts each showed Waiting end to end.
- The hook's `agent_id` equals the `<id>` in `subagents/agent-<id>.jsonl`.
- A real T3 Code thread in supervised mode showed Waiting on a `Write` prompt (2026-10-15).

## Known limitations (accepted, documented in README)

- **No hook fires when a prompt is approved.** In SDK hosts such as T3 Code, an approved long-running call stays amber until its result arrives. The docs list no approval event; check again when Claude Code updates.
- **The port is fixed at 5180 in the plugin.** An HTTP hook URL can't take `userConfig` values.
- **Sessions started before the plugin was installed don't run the hook.**
- **In auto mode nothing prompts**, so there is nothing to see: a call is allowed or declined without asking.

## Traps

- **Bump `version` when you change the plugin.** `plugin/.claude-plugin/plugin.json` pins `"version"`, and users stay on the pinned version until it changes. Then run `claude plugin update agent-aquarium@agent-aquarium`.
- **`/plugin` does not exist in T3 Code** (an SDK host). Use the CLI there: `claude plugin install agent-aquarium --marketplace guyvdn/AgentAquarium --scope user`.
- **Paths in a probe hook's JSON, from Git Bash:** `$TEMP` contains backslashes, which break JSON. Use `cygpath -m`.
- **Before pushing, validate the plugin** with `claude plugin validate .` and `claude plugin validate ./plugin`.

## Test a hook end to end

1. Run a test instance on port 5181.
2. Make a probe plugin whose hook POSTs to 5181, and add a second command hook, `sleep 10`, to hold the prompt open.
3. Run `claude -p "...use Write..." --plugin-dir <probe> --permission-mode default --model haiku --session-id <uuid>`.
4. Poll `/api/agents` on 5181 while the prompt is held.

`--include-hook-events` with `--output-format stream-json --verbose` shows whether a hook ran and what status it got.
