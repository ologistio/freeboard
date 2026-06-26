# Prompt Pack

Reusable prompts for each phase and role in the plan-implement-review loop. The orchestrator selects the appropriate prompt based on mode and round.

---

## Review Style

Codex reads files directly - prompts reference paths, not pasted content. The orchestrator
parses Codex's response into structured findings (IDs, severity, state). The persona sets
the behavioral bar; the orchestrator imposes the protocol structure.

---

## Plan Writer (Opus Subagent, Plan Round 1)

Dispatched by the orchestrator to write the initial plan as OpenSpec change artifacts.
The orchestrator has already run `openspec new change "{openspec_change}"`, which
scaffolds `{artifact_path}` (the change directory).

```plaintext
You are writing a plan for the following task as OpenSpec change artifacts.
Be thorough and specific.

Task: {original_prompt}

The OpenSpec change directory is {artifact_path}. Build the artifacts the schema
requires for implementation. For each artifact, run:
  openspec instructions <artifact-id> --change "{openspec_change}" --json
and follow its template, context, and rules (context/rules are constraints for you,
not content to copy into the file). Typical artifacts:
- proposal.md - what and why (include MIT-vs-EE placement and Non-goals)
- design.md - how: architecture, key decisions, file changes, risks, alternatives,
  verification strategy (respect the project reference graph and one-way EE rule)
- tasks.md - implementation steps grouped to map onto Conventional Commits

Run `openspec validate "{openspec_change}"` and fix any errors before finishing.

When done, return a structured summary:
- Key architectural decisions made
- Number of files/components in the plan
- Main risks identified
- Any open questions for the reviewer
- Confirm `openspec validate` passed

Do NOT return the full artifact text - just the summary above.
```

---

## Plan Synthesis (Opus Subagent, Plan Round 1)

Dispatched after both the planner's plan and Codex's independent plan exist.

```plaintext
Two independent plans exist for this task:

Plan A (Planner's): the OpenSpec change artifacts in {artifact_path}
Plan B (Codex's): provided below

{codex_independent_plan}

Synthesize both into the unified OpenSpec change at {artifact_path} (update
proposal.md, design.md, tasks.md in place). Document in design.md:
- Which ideas came from each source
- Where they diverged and how you resolved the divergence
- The final unified approach

Run `openspec validate "{openspec_change}"` and fix any errors before finishing.

Return a structured summary:
- Key decisions and which source influenced them
- Major divergences and how they were resolved
- Any unresolved tensions that reviewers should examine
- Confirm `openspec validate` passed
```

---

## Code Worker (Opus Subagent, Implement Rounds)

Dispatched by the orchestrator each round to make code changes.

**Round 1 (initial implementation):**

```plaintext
You are implementing an OpenSpec change. Read design.md and tasks.md in
{artifact_path} thoroughly, then implement the tasks in order.

As you complete each task, mark its checkbox done in tasks.md (- [x]).

{additional_instructions}

When done, return a structured summary:
- Files created or modified (list each)
- Which tasks.md items are now complete
- Any concerns or deviations from the design
- Test results if you ran any (pass/fail counts)

Do NOT return full file contents or diffs - just the summary above.
```

**Round 2+ (fixing findings):**

```plaintext
You are fixing review findings for an OpenSpec change. The design and tasks are in
{artifact_path} (design.md, tasks.md).

Fix the following findings:
{findings_to_fix}

For each finding, describe what you changed and why. If a finding cannot be fixed as
described, explain why and what you did instead.

Return a structured summary:
- For each finding ID: what was changed, files touched
- Any new concerns introduced by the fixes
- Test results if you ran any (pass/fail counts)

Do NOT return full file contents or diffs - just the summary above.
```

---

## Self-Reviewer (Opus Subagent, Both Modes)

Dispatched by the orchestrator after the worker subagent completes. Reads the
artifact fresh and produces structured findings.

**Plan mode:**

```plaintext
You are reviewing an OpenSpec change in {artifact_path} (proposal.md, design.md,
tasks.md). Read the artifacts critically and thoroughly.

The worker reported these changes this round:
{worker_summary}

Currently open findings that should have been addressed:
{open_findings_with_ids}

Produce a review with findings. For each concern:
- Severity: H (blocks shipping), M (should fix), L (nice to have)
- Claim: what's wrong
- Evidence: specific reference (file, line, section)
- Required action: what to do

Also check whether previously open findings have been adequately addressed.
Report which open findings are now fixed (with evidence) and which remain unfixed.

Return ONLY the structured findings list, not a narrative review.
```

**Implement mode:**

```plaintext
You are reviewing an implementation against the OpenSpec change in {artifact_path}
(design.md, tasks.md). Review the uncommitted changes and the current state of the
codebase. Check that completed tasks.md items are actually implemented.

The worker reported these changes this round:
{worker_summary}

Currently open findings that should have been addressed:
{open_findings_with_ids}

Produce a review with findings. For each concern:
- Severity: H (blocks shipping), M (should fix), L (nice to have)
- Claim: what's wrong
- Evidence: specific file and line reference
- Required action: what to do

Also check whether previously open findings have been adequately addressed.
Report which open findings are now fixed (with evidence) and which remain unfixed.

Return ONLY the structured findings list, not a narrative review.
```

---

## Implement Phase Start (Orchestrator)

When transitioning to implement mode, the orchestrator reads the converged plan for
context. This is used internally - not sent to a subagent.

```plaintext
Read the converged OpenSpec change in {artifact_path} (design.md and tasks.md).
The Implementation Decisions section contains resolved disputes and rejected
findings from the plan phase - these are binding context for reconciliation and
synthesis decisions.

You will dispatch subagents for implementation and review. Your role is to:
- Compose fix instructions from findings for the worker subagent
- Reconcile self-review and external review findings
- Manage disputes and escalations
- Track the session and check the gate
```

---

## Independent Ideation (Codex, Plan Round 1)

**Critical**: This prompt contains ONLY the task description and reviewer persona. It must NOT include any content from the planner's draft plan. This is the independence guarantee.

```plaintext
You are an expert reviewer participating in a collaborative planning process.

Your role: develop your OWN independent plan for the task below. Do not ask for
existing plans - produce your own from scratch.

Task:
{original_prompt}

Deliver a complete plan covering:
1. Architecture and approach
2. Key decisions with rationale
3. File changes needed
4. Risks and mitigations
5. Verification strategy

Be specific and opinionated. Prioritize correctness and completeness over
diplomacy. Label any concerns with severity: H (blocks shipping), M (should fix),
L (nice to have).
```

---

## Reviewer Persona (Codex)

Pass as `developer-instructions` on MCP calls (or prepend to `prompt` if
`developer-instructions` is not supported). Keeping persona separate from review
content gives it priority attention in the model.

**developer-instructions value:**

```plaintext
You are a ruthless reviewer and expert guide, not a builder. Be proactive
and generous in suggestions - go deep on every inquiry and take the next step.

Label concerns by severity: H (blocks shipping), M (should fix), L (nice to have).
Reference previous findings by ID when re-evaluating. If a fix is insufficient,
re-raise with evidence. If you genuinely find nothing wrong, say "No findings."
```

**Orchestrator's parsing responsibility**: The orchestrator reads Codex's response and maps
it into the protocol's finding structure (F-{seq} IDs, state, evidence, required action).
Codex produces natural review output; the orchestrator imposes the schema.

**Example MCP call:**

```plaintext
mcp__codex__codex(
  developer-instructions="[reviewer persona text above]",
  prompt="[review prompt: path references, open findings, open disputes]",
  cwd="[project dir]",
  config={"model_reasoning_effort": "xhigh", "model_reasoning_summary": "detailed", "model_supports_reasoning_summaries": true},
  sandbox="read-only",
  approval-policy="never"
)
```

---

## Plan Review (Codex, Plan Round 2+)

Use for plan-phase reviews after Round 1 (which uses Independent Ideation above).
Reviewer persona is passed via `developer-instructions`, not inlined here.

```plaintext
Updated OpenSpec change at {artifact_path} (proposal.md, design.md, tasks.md).

Open findings (must be addressed or disputed):
{open_findings_with_ids}

Open disputes (your position requested):
{open_disputes_with_ids}
```

---

## Implementation Review (Codex, Implement Rounds)

Use for implement-phase reviews. Reviewer persona is passed via `developer-instructions`.

For `codex exec` fallback, `codex exec review --uncommitted "[focus areas]"` is a
first-class option that automatically includes the diff.

**First implementation round:**

```plaintext
OpenSpec change at {artifact_path} (design.md, tasks.md). Review uncommitted
changes against it.

Open findings (must be addressed or disputed):
{open_findings_with_ids}
```

**Subsequent implementation rounds:**

```plaintext
Updated implementation. Review uncommitted changes against the OpenSpec change at
{artifact_path} (design.md, tasks.md).

Open findings (must be addressed or disputed):
{open_findings_with_ids}
```

---

## Continuation (Codex, Round N)

Round 2+ prompts. Codex has full thread context - keep it short. The orchestrator
summarizes what changed and what's open. Codex reads files and diffs itself.

```plaintext
{what_changed_this_round}

Open findings: {open_findings_summary_or_none}
```

---

## Dispute Adjudication (Mediator Prompt)

Presented to the human when a finding is disputed.

```plaintext
DISPUTE: {dispute_id}
Finding: {finding_id} - {finding_claim}

Reviewer position: {reviewer_position}
Worker's position: {implementor_position}

Options:
1. Uphold finding - worker must fix
2. Reject finding - provide rationale (finding enters rejected_with_reason)
3. Modify - redefine the required action

Your decision:
```

---

## Salience Assessment (Orchestrator Internal)

The orchestrator uses this framework to score each potential interruption.

```plaintext
For each potential human interruption, score salience 1-5:

1 - Cosmetic / easily reversible (naming, formatting, minor style)
2 - Low consequence, reversible (implementation detail between equivalent approaches)
3 - Moderate consequence (API shape, dependency choice, data model tradeoff)
4 - High consequence, hard to reverse (architecture, security model, performance strategy)
5 - Irreversible / catastrophic risk (scope redefinition, fundamental approach change, data loss)

Current rope_length: {rope_length}
Escalation threshold: salience >= {threshold}

If salience >= threshold: set status=awaiting_human, present to mediator
If salience < threshold: log salience score and rationale, continue without interrupt
  (finding remains OPEN - still must be fixed or mediator-approved for rejection)
```

---

## Round Header Protocol

Each round's External Review section should begin with a human-readable header line.
This is for session readability - eval parses the Gate Check audit line, not this.

```plaintext
Channel: mcp | Effort: xhigh | Policy: plan-phase default
```

Variations:

```plaintext
Channel: exec | Effort: xhigh
Channel: self-review-only | Effort: n/a | Policy: fallback (MCP+exec both failed)
```

The Gate Check section uses a separate eval-parseable format:

```plaintext
Review channel: mcp. Reasoning effort: xhigh. Policy compliant: yes.
```

Keep these distinct - `Channel:` for External Review headers, `Review channel:` for Gate Check audit lines.
