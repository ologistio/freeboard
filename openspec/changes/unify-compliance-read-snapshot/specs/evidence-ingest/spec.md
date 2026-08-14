## ADDED Requirements

### Requirement: The admission checks read their inputs from one snapshot

The ingest admission checks SHALL read from ONE store snapshot, naming the assets, the scopes,
the requirements, the controls, and the collectors. Ingest admits a run only after three checks
against the persisted domain: the `collector_id` names a registered collector that carries a
vendor, the `requirement_id` is one the collector's control maps to, and the
`(organisation_id, requirement_id)` pair resolves `In` in the Statement of Applicability for the
requirement's owning standard.

Reading them separately lets a GitOps sync commit between the checks, so a run can be admitted
against a control mapping and a scope resolution that were never in force together - for
example a requirement that has just resolved `Out` for that organisation. The credential
identifies a collector rather than a user, so this is an admission decision rather than an
accessible-set narrowing and it discloses nothing. It is held to the same rule because it is one
composed decision over the same domain, and an exception here would leave the rule as "every
decision but this one".

A check that fails SHALL keep its existing outcome and its existing message. This requirement
governs where the inputs are read, not what they decide.

#### Scenario: The three checks cannot straddle a sync

- **WHEN** a collector posts evidence while a GitOps sync commits a change to the collector's
  control mapping and to the scope that resolves its requirement
- **THEN** all three admission checks are evaluated against one side of that commit, so the run
  is admitted or rejected against one state of the domain rather than a mixture

#### Scenario: The admission reads once

- **WHEN** a well-formed ingest request is admitted
- **THEN** the endpoint took one store snapshot for the collector, the control mapping, and the
  Statement of Applicability inputs, rather than one read per check
