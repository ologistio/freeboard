## ADDED Requirements

### Requirement: Statement of Applicability surfaces per-collector evidence status

The `/compliance/statement-of-applicability` view page SHALL show, for each collector
check under a control, that collector's derived evidence status for the organisation node
it appears under. The status SHALL be read from the evidence read store
(`IEvidenceStore`) as a per-collector status keyed by `(organisation, requirement,
collector)`, and the page SHALL batch-read the statuses for all visible organisation
nodes in one call rather than issuing a read per node. The evidence read MAY use a
separate snapshot from the drill-down projection read: the status is advisory display and
is not part of the config-tree consistency guarantee.

The page SHALL render `Stale` distinctly from `Unknown`: `Stale` (the collector's latest
evidence is older than its cadence window plus grace) SHALL be shown as a "collection
stopped" state, and `Unknown` SHALL be shown as a separate "not collected" state. A
collector check that has no status from the store (an expected collector that never
produced evidence for that organisation and requirement) SHALL be derived as `Unknown` by
the page. `HardFailure`, `SoftFailure`, and `Passing` SHALL each render distinctly from
`Stale` and `Unknown`. Only collector checks SHALL carry a status; attestation checks
carry no evidence status.

The status surfacing SHALL NOT change the drill-down projection or its inputs: the
existing resolution, scoping, authorization-boundary, read-only, store-unreachable, and
JSON-endpoint behaviours of the page SHALL continue to hold, and the JSON endpoint SHALL
remain free of live evidence status.

#### Scenario: A stale collector renders as collection stopped

- **WHEN** a collector check's latest evidence for an in-scope organisation and
  requirement is older than its cadence window plus grace
- **THEN** the page shows that collector check with a "collection stopped" status,
  distinct from a "not collected" status

#### Scenario: A never-collected collector renders as unknown

- **WHEN** a collector check is configured on a control for an in-scope requirement but
  has produced no evidence for the organisation
- **THEN** the page shows that collector check with an `Unknown` "not collected" status,
  distinct from "collection stopped"

#### Scenario: A fresh passing collector renders as passing

- **WHEN** a collector check's latest evidence for an in-scope organisation and
  requirement is within its cadence window plus grace and has no failing check
- **THEN** the page shows that collector check with a `Passing` status

#### Scenario: Visible organisation statuses are batch-read

- **WHEN** the page renders collector checks across several in-scope organisation nodes
- **THEN** the per-collector statuses for those organisations are read from the evidence
  store in a single batch call rather than one read per organisation
