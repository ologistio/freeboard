## 1. Author the capability spec

- [x] 1.1 Create `specs/web-ux-conventions/spec.md` with one `### Requirement:` per rule,
      each title carrying the stable rule ID, transcribed faithfully from
      `src/Freeboard/stories/UxRules.mdx`.
- [x] 1.2 Give every requirement at least one `#### Scenario:` in WHEN/THEN form that a
      reviewer or test could check.
- [x] 1.3 State every requirement unconditionally, with no enforceability tag: a rule is a
      rule regardless of the current implementation state.
- [x] 1.4 Add the `Purpose and rule citation` requirement stating that IDs are frozen, that
      each rule is cited by its stable ID, and that the conformance suite tests each rule
      wherever a surface exists to exercise it.
- [x] 1.5 State each A-rule's automated-check owner precisely: `web-accessibility` (axe) owns
      A1 (contrast) and A6's AA-in-both-themes part; `web-ux-conventions` owns A2, A3, A5, A6's
      semantic-stability and personal-setting parts, and A4's Freeboard-specific 32/44px hit-target thresholds, which
      axe's generic target-size rule does not enforce. One owner per rule, not duplicated.

## 2. Validate coverage and format

- [x] 2.1 Confirm every rule ID from `UxRules.mdx` (N1-N9, O1-O6, L1-L7, S1-S6, T1-T7,
      P1-P5, X1-X4, E1-E6, F1-F4, A1-A6, W1-W5) maps to exactly one requirement, with no
      duplicates and no omissions.
- [x] 2.2 Run `openspec validate ratify-web-ux-rules --strict` and resolve any errors
      (every requirement has a scenario, headers are exactly `####`, delta parses).
- [x] 2.3 Check the spec against the ASCII-punctuation rule: no em/en dashes, Unicode
      arrows, ellipses, or curly quotes in the authored files.

## 3. Wire the contract into the migration

- [x] 3.1 Confirm `tmp/ui-migration-critical-path.md` P0 points at this change and P7 names
      these requirement IDs as its conformance source of truth.
- [x] 3.2 Confirm no application code, dependency, or build change is bundled in - this
      change is documentation only.
- [x] 3.3 Confirm the control-authoring decision in `design.md` (tag helpers for marks, view
      components for the shell, Alpine for behaviour) is reflected in `tmp/ui-migration-critical-path.md`
      P2 and P3, so the migration builds repeatable controls one way.

## 4. Ratify

- [x] 4.1 Review with the design owner; adjust any rule transcription that misreads the
      ratified intent.
- [x] 4.2 On approval, archive the change so `web-ux-conventions` folds into
      `openspec/specs/`, then set its Purpose from the archived spec.
