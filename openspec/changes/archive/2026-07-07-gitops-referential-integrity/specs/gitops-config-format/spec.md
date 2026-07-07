## ADDED Requirements

### Requirement: Config-format documentation covers every supported kind

The GitOps config-format documentation (`docs/gitops.md`) SHALL document every
kind the loader and validator support. For each kind it SHALL state the schema
fields, at least one example document, and the validation rules, including the
referential-integrity rules (which fields reference which other kind by id and
that a reference to an absent id is rejected). This SHALL include
EvidenceCollector (references a control by id and, optionally, a vendor by id)
and AttestationTemplate (references a control by id), which were previously
undocumented, so the documented surface matches the shipped
`GitOpsSchema` kind set.

#### Scenario: EvidenceCollector documented with its referential-integrity rules

- **WHEN** a reader consults `docs/gitops.md`
- **THEN** it describes the EvidenceCollector kind, its `control` (required) and
  `vendor` (optional) references, and states that a `control` or `vendor` naming
  an id that no document defines is rejected as a validation error

#### Scenario: AttestationTemplate documented with its referential-integrity rule

- **WHEN** a reader consults `docs/gitops.md`
- **THEN** it describes the AttestationTemplate kind, its required `control`
  reference, and states that a `control` naming an id that no document defines is
  rejected as a validation error

#### Scenario: Supported-kind list and noun table are complete

- **WHEN** a reader consults the supported-kinds list and the noun mapping table
  in `docs/gitops.md`
- **THEN** both include EvidenceCollector and AttestationTemplate alongside the
  existing kinds, so no shipped kind is omitted from the catalogue
