# No process references in code

Source code, comments, and commit messages describe the software, not the
process that produced it. Do not reference the internal workings of any
planning, review, or agent workflow.

## Prohibited in comments, code, and commits

- OpenSpec artifacts: change names, `tasks.md` items, task numbers ("group 5",
  "task 8.2"), capability names, change/proposal ids, `openspec/changes/...` paths.
- Plan-implement-review-loop internals: finding ids (`F-12`, `I-22`, `D-F-7`),
  round numbers, session-file notes, reviewer/dispute/gate terminology.
- Any other private working note: ticket-tracker scratch ids, agent names, or
  "as flagged in review" style back-references.

These are ephemeral. The change directory is archived and the loop session is
deleted, so the cross-reference becomes dangling noise that a future reader (who
never saw those notes) cannot resolve - exactly as we would not expect a human
to cite their own scratch notes in a code comment.

## Do this instead

Explain the thing on its own terms. If a comment only made sense because it
pointed at a finding id, rewrite it to state the actual reason.

```csharp
// Bad
// I-22: reject a session whose stored credential epoch is stale.

// Good
// Reject a session whose stored credential epoch is stale: a password change
// bumps the user's epoch, so older sessions no longer match and are logged out.
```

Permanent, public references are fine: RFCs, CVEs, library docs, a standard's
section number, or a stable issue URL the project intends to keep.

## Carve-out: ratified requirement IDs

Requirement IDs from an established, ratified spec under `openspec/specs/` are
permitted in code, comments, and tests. These are stable, versioned clauses of an
adopted contract - the project's own "standard's section number" - not ephemeral
working notes, and they stay resolvable because the spec is kept, not archived and
deleted like a change proposal. `code-review.md` directs reviewers to cite them by
ID (for example "this breaks L4"), so referencing them in the code they govern is
consistent, not a process leak.

Concretely, the `web-ux-conventions` rule IDs (the `N`/`O`/`L`/`S`/`T`/`P`/`X`/
`E`/`F`/`A`/`W` requirements, such as `S2`, `S3`, `T6`, `L2`, `A6`) may appear as a
terse citation next to the code that enforces the invariant they name. Cite the
rule to point at the durable requirement; do not restate the ephemeral process that
implemented it. Still prefer a plain-language reason where the code is not
self-evidently tied to the rule - the ID is a pointer, not a substitute for the
"why".
