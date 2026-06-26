---
name: plan-implement-review-loop
description: >-
  Automated plan-implement loop with expert review. An orchestrator dispatches
  subagents for planning, implementation, and self-review, and uses Codex for
  external review each round; the human steers via a single rope-length knob
  (0-5) that controls interruption frequency. The subagent architecture keeps
  the orchestrator's context lean enough to finish in one session. Use for any
  planning, development, or implementation task that benefits from structured,
  iterative expert review.
when_to_use:
  # Direct invocation
  - /plan-implement-review-loop
  - plan-implement-review loop
  # Planning
  - plan this
  - let's plan
  - make a plan
  - plan with expert
  - plan and build
  - plan then implement
  # Plan-only
  - plan only
  - just plan
  - design review
  - RFC review
  # Development / implementation
  - build this
  - implement this
  - implement with expert
  - build with expert review
  # Implement-only
  - just implement
  - just build
  - skip planning
  # Codex / expert review
  - codex review
  - get codex to review
  - review with codex
  - expert review loop
  # Automation emphasis
  - automated review loop
  - autonomous review loop
---

# plan-implement-review-loop

## Preflight

Do not enter the round loop until all preflight checks pass.

### Step 1: Stop hook check

Read `~/.claude/settings.json` and look for a `Stop` hook entry pointing to this
skill's `scripts/stop-hook.sh`.

If the hook is NOT installed, tell the user:

> The plan-implement-review loop works best with the stop hook installed - it keeps the loop
> running automatically so you don't have to type "continue" each round. The hook
> is dormant when no loop session is active (it checks for a session file and
> exits immediately if none exists).
>
> Want me to add it to your settings?

If the user agrees, add this entry to `~/.claude/settings.json` under `hooks.Stop`
(create the key path if it doesn't exist):

```json
{
  "matcher": "",
  "hooks": [{
    "type": "command",
    "command": "SKILL_SCRIPTS_DIR/stop-hook.sh",
    "timeout": 10000
  }]
}
```

Replace `SKILL_SCRIPTS_DIR` with the absolute path to this skill's `scripts/`
directory (resolve from the skill installation location).

If the user declines the hook, proceed in Manual tier (the user will type
"continue" between rounds). Note the tier in the session's first round summary.

If the hook IS already installed, proceed without mentioning it.

### Step 2: Codex reviewer channel check

Probe whether the Codex MCP tools are callable (search available tools for
`mcp__codex__codex`, or attempt a lightweight call). Don't inspect how it's
configured - it could be project `.mcp.json`, user-wide MCP settings, or
another harness entirely.

- If `mcp__codex__codex` is available -> record `reviewer_backend: mcp` and `review_channel_status: mcp_ready` in session, proceed.
- If unavailable -> check `which codex` for CLI fallback.
  - If codex CLI exists -> offer to configure MCP:
    > Codex MCP isn't available in this session. I can add it for you:
    >
    > 1. **User-level** - available in all projects
    > 2. **Project-level** - scoped to this repo
    > 3. **Skip** - use `codex exec` CLI fallback (slower, session-based persistence)
    >
    > Which do you prefer?

    For options 1 or 2, run the appropriate command, then stop - do not enter
    the round loop. Tell the user to restart Claude Code and re-invoke the skill.
    Preflight will re-run and find MCP available.

    ```bash
    # User-level
    claude mcp add --scope user --transport stdio codex -- codex mcp-server

    # Project-level
    claude mcp add --scope project --transport stdio codex -- codex mcp-server
    ```

    If the user chooses "skip" (option 3), record `reviewer_backend: exec` and
    `review_channel_status: exec_opt_in`, proceed with the downgrade logged.
  - If no codex CLI at all -> hard stop:
    > The plan-implement-review loop requires Codex as reviewer. Install: `npm i -g @openai/codex`
    > Then either restart (I'll offer to configure MCP) or ensure the CLI is in your PATH.

### Step 3: Reasoning policy initialization

Confirm rope length and inform the user of the reasoning policy (no action
needed from them):

> Reasoning policy: xhigh for all rounds, with detailed reasoning summaries.

## Protocol

Open `@references/guide.md` and follow it. Do not proceed without it.

Automated plan-implement loop with subagent workers and Codex as reviewer. The
orchestrator dispatches subagents for planning/implementation and self-review, Codex for
external review. Use when you want:

- Plans stress-tested through parallel ideation then iterative convergence
- Implementation reviewed each round with zero-finding close gate
- Adjustable autonomy via rope-length (0 = approve everything, 5 = full auto)
- Walk-away execution with all decisions tracked in a session file
- Context-efficient execution that completes in a single context window

The guide contains:

- Core protocol: orchestrator + three subagent types (planner/implementor worker, self-reviewer, Codex external reviewer)
- Round mechanics: implement, self-review, external review, reconciliation, synthesis, gate check
- Subagent dispatch patterns and prompt templates
- Plan context loading rules
- Rope-length semantics and salience scoring
- Finding and dispute tracking with stable IDs
- Close gate derivation and anti-gaming constraints
- Phase transition (plan -> implement) with decisions ledger
- Parallel ideation protocol (Round 1 independence via subagents)
- Session file format and continuation block structure
- Stop hook integration for loop enforcement
- Prompt pack reference (`@references/prompts.md`)
- Session template (`@references/session-template.md`)
- Eval checks and failure modes
