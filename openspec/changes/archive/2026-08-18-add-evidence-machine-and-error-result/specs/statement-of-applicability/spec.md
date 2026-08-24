## MODIFIED Requirements

### Requirement: Statement of Applicability surfaces per-collector evidence status

The `/compliance/statement-of-applicability` view page SHALL show, for each collector
check under a control, that collector's derived evidence status for the organisation node
it appears under. The status SHALL be read from the evidence read store
(`IEvidenceStore`) as a per-collector status keyed by `(organisation, requirement,
collector)`, and the page SHALL batch-read the statuses for all visible organisation
nodes in one call rather than issuing a read per node. The evidence read MAY use a
separate snapshot from the drill-down projection read: the status is advisory display and
is not part of the config-tree consistency guarantee.

The page SHALL render `Errored`, `Stale`, and `Unknown` each distinctly from the other two:
`Errored` (the collector's latest collection attempt failed, so the requirement was not
observed) SHALL be shown as a "collection failed" state, `Stale` (the collector's latest
evidence is older than its cadence window plus grace) SHALL be shown as a "collection
stopped" state, and `Unknown` SHALL be shown as a separate "not collected" state. A
collector check that has no status from the store (an expected collector that never
produced evidence for that organisation and requirement) SHALL be derived as `Unknown` by
the page. `HardFailure`, `SoftFailure`, and `Passing` SHALL each render distinctly from
`Errored`, `Stale`, and `Unknown`. `Errored` SHALL NOT render as a passing state and SHALL
NOT render red: red is reserved for an observed policy failure, and an error means the
answer is not known. Only checks tagged as a collector SHALL carry a status; checks
tagged as an attestation (a collector of `type: manual` or `type: training`) carry no
evidence status.

The words the row badge writes for these states are evidence-status labels, not product
status terms. The UX status rule permits that closed label set on this one surface and
nowhere else, so the row SHALL draw its text from that set, and the control anatomy SHALL
keep speaking the product vocabulary.

The `Errored` rule SHALL hold for the row badge AND for the control anatomy the page renders
in its drawer and at the control's direct link, so the two surfaces cannot report an errored
collector differently. The two surfaces map a persistence status through separate code, and
they already render `Stale` differently from each other. This requirement does not reconcile
that older difference. It fixes only that `Errored` reaches both surfaces, because a status
added to one surface alone falls through the other's default and reads as "not collected".

The status surfacing SHALL NOT change the drill-down projection or its inputs: the
existing resolution, scoping, authorization-boundary, read-only, store-unreachable, and
JSON-endpoint behaviours of the page SHALL continue to hold, and the JSON endpoint SHALL
remain free of live evidence status.

#### Scenario: An errored collector renders as collection failed

- **WHEN** a collector check's latest collection for an in-scope organisation and
  requirement failed and was recorded as an errored run
- **THEN** the page shows that collector check with a "collection failed" status, distinct
  from "collection stopped", from "not collected", and from a passing status

#### Scenario: An errored collector renders the same on the page and in the drawer

- **WHEN** a collector check is `Errored` and the viewer opens the control in the drawer or
  at its direct link
- **THEN** the control anatomy shows the same errored state that the page row shows, and it
  is not shown as passing

#### Scenario: A stale collector renders as collection stopped

- **WHEN** a collector check's latest evidence for an in-scope organisation and
  requirement is older than its cadence window plus grace
- **THEN** the page shows that collector check with a "collection stopped" status,
  distinct from a "not collected" status

#### Scenario: A never-collected collector renders as unknown

- **WHEN** a collector check is configured on a control for an in-scope requirement but
  has produced no evidence for the organisation
- **THEN** the page shows that collector check with an `Unknown` "not collected" status,
  distinct from "collection stopped" and from "collection failed"

#### Scenario: A fresh passing collector renders as passing

- **WHEN** a collector check's latest evidence for an in-scope organisation and
  requirement is within its cadence window plus grace and has no failing check
- **THEN** the page shows that collector check with a `Passing` status

#### Scenario: An attestation-tagged check carries no evidence status

- **WHEN** a collector of `type: manual` or `type: training` is projected as a check
- **THEN** the page renders no evidence status badge for it - matching how a check from an
  `AttestationTemplate` behaved pre-merge, and CHANGING how a check from a
  `manual-attestation` or `training-attestation` `EvidenceCollector` behaved, since that
  check was tagged as a collector and did carry a status

#### Scenario: Visible organisation statuses are batch-read

- **WHEN** the page renders collector checks across several in-scope organisation nodes
- **THEN** the per-collector statuses for those organisations are read from the evidence
  store in a single batch call rather than one read per organisation
