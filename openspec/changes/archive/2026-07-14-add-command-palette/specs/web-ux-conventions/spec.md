## MODIFIED Requirements

### Requirement: N7 One command palette

There SHALL be exactly one command palette (Ctrl-K) and it SHALL be the only global search or
ask entry point in the chrome. There SHALL NOT be a second search box elsewhere. The built
behavior this phase is a single search surface that jumps to pages (a Page result navigates to a
nav destination the viewer can reach) and runs in-app commands. Asking the assistant is the
aspirational part of this rule: it is not built while no assistant backend exists, so the palette
SHALL NOT show an assistant result, placeholder, or coming-soon row until an assistant exists.
When an assistant exists, asking it becomes a live capability of this same single surface,
without a second search box. Renumbering the rule ID is forbidden, so the ID stays N7 even though
the assistant clause is staged.

#### Scenario: Single search surface

- **WHEN** the app chrome is rendered
- **THEN** the command palette is the only global search or ask entry point

#### Scenario: Assistant is aspirational until it exists

- **WHEN** the palette renders and no assistant backend exists
- **THEN** the palette jumps to pages and runs commands, shows no assistant result of any kind,
  and asking the assistant re-enters this same single surface once an assistant is built
