## MODIFIED Requirements

### Requirement: Statement of Applicability projection carries the requirement-control-check structure per in-scope node

For a chosen standard, a projection that backs the view page SHALL attach to each
organisation node whose standard resolves `In` the full list of that standard's
requirements (not only the node's requirement-level deviations), each with its
resolved disposition (`In` or `Out`) and provenance (`explicit`, `inherited`, or
`default`), including requirements excluded (`Out`) at the node. Each requirement that
resolves `In` SHALL carry the controls whose mapping (`maps_to`) includes that
requirement; each control SHALL carry the checks configured on it and its optional
evaluation roll-up as metadata; and each check SHALL be a collector attached to that
control, tagged by kind. The check's kind SHALL be DERIVED from the collector's `type`
rather than read from two separate input sets: a collector whose `type` is `manual` or
`training` SHALL be tagged as an attestation, and a collector of any other type SHALL be
tagged as a collector. The wire values of the tag are unchanged. An excluded (`Out`)
requirement SHALL be a leaf: it carries no controls. A node whose standard resolves
`Out` SHALL carry no requirements at all. Requirements SHALL be ordered by requirement
`id`, controls by control `id`, and checks by kind then `id`.

A check SHALL expose configuration and metadata only. Attestation quiz answers SHALL
NOT be surfaced. Vendors SHALL NOT affect applicability; a collector's optional
vendor is metadata only, and this projection SHALL NOT read live evidence
(`evidence_checks`) or vendor-subject scopes.

A check tagged as an attestation SHALL carry NO collection cadence, even though every
collector now has a required `frequency`. The cadence SHALL be derived in the same step as
the tag - a `manual` or `training` collector projects a null cadence and a collector of any
other type projects its own `frequency` - so the two cannot drift apart. This preserves the
rendering of a check that was ALREADY tagged as an attestation, which had no cadence to
show. It does NOT preserve the rendering of a check whose tag this derivation moves: a
collector previously typed `manual-attestation` or `training-attestation` was tagged as a
collector and showed both a cadence and an evidence status, and as a `manual` or `training`
collector it shows neither. It keeps the
projection coherent with the rule that an attestation-tagged check carries no evidence
status: the page interprets a cadence against a check's status (`Stale` means older than
the cadence window plus grace), so a cadence beside a check with no status would assert a
collection promise the page cannot back. A collector's optional `vendor` is NOT nulled this
way and SHALL be carried for a check of either tag, because it plays no part in that
interpretation.

This projection SHALL be added alongside the existing flat resolver, which SHALL be
left unchanged. The controls and the unified collector set that
populate the structure SHALL be read in the same repeatable-read snapshot as the
organisations, the unified scopes, and requirements (one unified `scopes` read and one
unified collector read, not separate evidence-collector and attestation-template inputs),
so the rendered tree cannot straddle a concurrent importer commit.

#### Scenario: In-scope node lists every requirement tagged In or Out

- **WHEN** the projection is computed for a standard at a node that resolves the
  standard `In` and excludes one requirement `Out`
- **THEN** the node lists every requirement of the standard with its resolved
  disposition (`In` or `Out`) and provenance, ordered by requirement `id`, including
  the excluded one tagged `Out`

#### Scenario: Excluded requirement is a leaf with no controls

- **WHEN** a requirement resolves `Out` at an in-scope node and a control maps to it
- **THEN** the requirement appears tagged `Out` and carries no controls, so it renders
  as a leaf with no control children

#### Scenario: Requirement carries its mapped controls

- **WHEN** a control's `maps_to` includes a requirement of the standard that resolves
  `In`
- **THEN** that control appears under that requirement in the projection, carrying
  its evaluation roll-up as metadata

#### Scenario: Control carries its configured checks of both kinds

- **WHEN** a collector of `type: integration` and a collector of `type: training` each name
  a control as their attach-point and that control is mapped to an in-scope requirement
- **THEN** both appear as checks under that control in the projection, the integration
  collector tagged as a collector and the training collector tagged as an attestation,
  with the tag derived from each collector's `type`

#### Scenario: An attestation-tagged check carries no cadence

- **WHEN** a `manual` or `training` collector with a `frequency` and an `integration`
  collector with a `frequency` are each projected as a check
- **THEN** the integration check carries its cadence and the attestation-tagged check
  carries none - matching how a check from an `AttestationTemplate` rendered pre-merge,
  and CHANGING how a check from a `manual-attestation` or `training-attestation`
  `EvidenceCollector` rendered, since that check was tagged as a collector and did show a
  cadence

#### Scenario: Attestation check hides quiz answers

- **WHEN** a `training` collector with quiz items in its `config` is projected
- **THEN** the check carries the collector's configuration and metadata but no quiz
  answer

#### Scenario: Requirement without controls and control without checks render empty child levels

- **WHEN** an in-scope requirement has no control mapped to it, or a control has no
  check configured on it
- **THEN** the projection carries that requirement or control with an empty child
  level rather than omitting it

#### Scenario: Structure inputs share the projection snapshot

- **WHEN** the page reads the inputs needed to build the drill-down
- **THEN** the controls and the unified collector set are read
  together with the organisations, the unified scopes, and requirements in
  one repeatable-read snapshot

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
`Stale` and `Unknown`. Only checks tagged as a collector SHALL carry a status; checks
tagged as an attestation (a collector of `type: manual` or `type: training`) carry no
evidence status.

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
