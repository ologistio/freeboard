## Why

The web UI design direction (the "audit ledger" system) lives in Storybook under
`src/Freeboard/stories/`, where `UxRules.mdx` states 65 numbered, review-checkable rules
and `UxPhilosophy.mdx` gives their rationale. Storybook is reference-only and
self-contained: it can drift from the app or be deleted outright (the prior
`adopt-webawesome-ui-baseline` change directory was lost exactly this way). Ratifying the
rules as an OpenSpec capability makes them durable, versioned, and diffable, and gives the
UI migration a spec to build and test against instead of a mutable prototype page.

This is the P0 contract for the UI migration in `tmp/ui-migration-critical-path.md`: every
later phase implements it, and the conformance phase tests against it.

## What Changes

- Introduce a `web-ux-conventions` capability that encodes each rule from `UxRules.mdx` as
  a distinct requirement, keyed by its stable ID (N1-N9, O1-O6, L1-L7, S1-S6, T1-T7,
  P1-P5, X1-X4, E1-E6, F1-F4, A1-A6, W1-W5) so code review and tests can cite it directly
  ("this breaks L4").
- Each rule is a normative requirement regardless of the current implementation state; no
  requirement carries an enforceability tag. Which rules the P7 conformance suite tests today
  is decided by the suite from the surfaces that exist, and coverage grows as surfaces land -
  the rules themselves stand as the standing contract either way.
- The rules' rationale is captured in this change's `design.md`, sourced from
  `UxPhilosophy.mdx`, so the requirements are applied with judgment, not by rote.
- No application code changes. This change ratifies the design contract only. The pixels
  are delivered by the later migration phases, each of which cites these requirement IDs.

## Capabilities

### New Capabilities

- `web-ux-conventions`: the numbered UX ruleset for the Freeboard web UI - navigation
  depth and grouping, the single object model and its detail anatomy, list/table
  behaviour, the status-and-color vocabulary, the task model, provenance and freshness,
  notifications, external surfaces, forms and errors, accessibility, and language. Each
  rule is a distinct requirement carrying its stable ID; the P7 conformance suite decides
  what to test today from the surfaces that exist.

### Modified Capabilities

None. The accessibility rules (A1-A6) overlap the existing `web-accessibility` capability;
this change does not modify it. `web-accessibility` remains the enforcement home for the
axe-core audit; `web-ux-conventions` states the A-rules as design requirements and points to
`web-accessibility` for the checks axe covers - A1 (contrast) and A6's AA-in-both-themes part.
The other A-rules (A2 keyboard path, A3 reduced motion, A5 focus trap and restore, A6 semantic
stability and personal setting) are not measurable by axe, and A4's Freeboard-specific hit-target thresholds (32px
in dense tables, 44px elsewhere) are not what axe's generic target-size rule enforces, so
`web-ux-conventions` owns those conformance checks. Either way the contract is stated once,
not duplicated.

## Non-goals

- Building any of the not-yet-built surfaces (task queue, notifications, trust center,
  assistant). Their rules are recorded, not implemented.
- Restyling pages, porting tokens or components, or changing the app shell. That is the
  work of migration phases P1-P7 and is out of scope here.
- Re-deriving or editing the rules themselves. This change transcribes the ratified ruleset
  faithfully; it does not rewrite the UX direction.

## Impact

- New: `openspec/specs/web-ux-conventions/spec.md` (via this change's `specs/` delta on
  archive). No source, dependency, or build changes.
- Reference target for the migration: `tmp/ui-migration-critical-path.md` phases P1-P7 and
  the P7 conformance checks cite these requirement IDs.
- MIT (default). No EE carve-out: this is a design contract for the MIT web app
  (`src/Freeboard`) and touches nothing in `src/Freeboard.Enterprise`.
