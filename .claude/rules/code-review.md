# Code review rules

## UX reviews

Web UI changes (`src/Freeboard`) follow the Freeboard UX ruleset. Do not restate the rules here; read them from the source of truth and cite a rule by its ID in review comments (for example "this breaks L4"):

- Rules (numbered N/O/L/S/T/P/X/E/F/A/W): `src/Freeboard/stories/UxRules.mdx`
- Why each rule exists: `src/Freeboard/stories/UxPhilosophy.mdx`

Apply a rule only where the surface it governs exists in the change; several rules describe features not yet built and do not apply until they are. The accessibility rules are also checked by the automated axe-core audit.

## Specification review

Code changes must not contradict the capability specs. Do not restate the specs here; read them from the source of truth and cite the capability and requirement in review comments (for example "this violates specs/foo, Requirement: bar"):

- Established specs: `openspec/specs/<capability>/spec.md`

Review the implementation against the requirements and scenarios of any spec whose capability the change touches. If the change implements an approved proposal, the baseline is the established specs as modified by that proposal's deltas; otherwise it is the established specs alone. Divergence from that baseline is a blocking finding. Intentional behaviour changes are fine only when the spec delta travels with the code; an undocumented improvement is still a violation.

Specs govern observable behaviour, not implementation detail. A spec applies only where the change touches the capability it describes; unspecced areas are out of scope for this section.

## Project rules

Every change must comply with the repo's rules in `.claude/rules/`. A review is a conformance check against them, not optional citation. Do not restate the rules here; read the rule files that bear on what the change touches and cite one by its filename in review comments (for example "this breaks code-as-liability.md" or "see ascii-punctuation.md"):

- Rule files: `.claude/rules/*.md`

Verify the change against every rule that applies to what it touches. An applicable rule the change fails to meet is a blocking finding. Do not read silence as compliance; confirm each applicable rule is met before clearing it.
