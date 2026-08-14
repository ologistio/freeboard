## ADDED Requirements

### Requirement: Compliance reads are served by one shape-parameterized snapshot

`IComplianceStore` SHALL serve every domain read through ONE method that takes the set of
lists the caller needs and returns them from ONE snapshot:
`GetSnapshotAsync(ComplianceReadSet sets, CancellationToken)`. `ComplianceReadSet` SHALL be a
flags enum naming the lists the store serves - the unified assets, the standards, the
requirements, the controls (with their resolved `maps_to`), the unified scopes, the collectors,
the integration connections, and the vendor assurances. The per-kind counts SHALL stay a
separate method, because they answer one question from one statement and take part in no
narrowing.

There SHALL NOT be a second read method returning domain LISTS beside this one, and no per-list
wrapper over it.
Ten methods say nothing about which reads belong to one decision. One method with a set argument
states a decision's inputs at the call site, and the returned snapshot records them, so a test
can assert what a surface drew on.

This requirement does NOT make the composed form impossible. A caller may still take a snapshot
of the assets and a second snapshot of another list and pair them, because the authorization
gate path needs an assets-only snapshot and so that shape stays available. Which sets each
surface names is held by the structural tests the consuming capabilities require, not by this
API shape.

A snapshot that needs more than one SQL statement SHALL read every list inside one transaction
at `RepeatableRead` isolation, so it cannot straddle a concurrent importer commit. A snapshot
that needs exactly one statement SHALL run without a transaction, because a single statement is
already atomic. The controls list SHALL count as two statements, since it joins the
`control_requirements` rows.

Reading a list from a snapshot that did not request it SHALL throw a dedicated exception type,
and that type SHALL NOT be one the read paths treat as a store failure. An unrequested list
SHALL NOT read as an empty list: an empty list is a plausible answer, so returning one would
report a programming error as an ordinary render, and reusing the store-unreachable path would
report it to an operator as a database outage.

The snapshot SHALL carry the `ComplianceReadSet` it was read with, so a caller and a test can
assert which lists a decision drew on.

#### Scenario: One decision names its inputs and gets one snapshot

- **WHEN** a caller needs the unified assets and the unified scopes for one narrowing decision
- **THEN** it makes ONE call naming both sets, receives both lists read inside one
  `RepeatableRead` transaction, and receives a snapshot reporting that it was read with exactly
  those two sets

#### Scenario: A single-set snapshot needs no transaction

- **WHEN** a caller reads a snapshot of the unified assets alone
- **THEN** the store answers with one statement and no transaction, because one statement
  cannot straddle a commit

#### Scenario: An unrequested list fails loudly

- **WHEN** a caller reads a snapshot of the assets alone and then reads its scopes
- **THEN** the read throws a dedicated exception rather than returning an empty list, and the
  web read paths do NOT report it as "compliance store unreachable"

#### Scenario: The snapshot reports its own shape

- **WHEN** a caller inspects a returned snapshot
- **THEN** it carries the set of lists it was read with, so a test can assert that a surface
  asked for exactly the lists its decision narrows on

## MODIFIED Requirements

### Requirement: General read store and GitOps importer abstractions

The system SHALL expose separate abstractions for reading the store and for
importing config into it. `IComplianceStore` (the general read abstraction, in the
`Freeboard.Persistence` namespace) SHALL serve, through the one shape-parameterized snapshot
read, the persisted standards (with their `version`, `authority`, optional `publisher`, and
optional `source_url` metadata), controls (with their resolved `maps_to`
`Requirement` ids, read from the `control_requirements` join), requirements (with
their resolved owning `standard`, `theme`, `statement`, `guidance`,
`citation_label`, and `citation_url`), ONE unified ASSET set, and
scopes (each with its resolved `subject`, exactly one target of
`standard`/`requirement`/`control`, `disposition`, and `justification`), and SHALL expose
per-kind counts that include requirements and one unified scope count as a separate method.

The unified asset read SHALL return every row of the `assets` table - `Company`,
`Department`, `Machine`, and `Vendor`, declared and discovered - each with its `id`, `title`,
`type`, `source`, `state` (null on a declared asset), and the two scalar edges `parent` and
`owner`. It SHALL REPLACE the separate organisation read and vendor read: there SHALL NOT be a
per-type read returning organisations or vendors, because a second read model over the
same table would let the resolver and the read-access closure see different trees. Consumers
that present one type - the `/organisations` and `/vendors` endpoints, the organisation
selector, and the drill-down's vendor-title lookup - SHALL filter the one set by `type` at the
point of use.

The unified scope read SHALL NOT join the `assets` table and SHALL NOT carry any subject
metadata (`type`, `source`, `state`, `parent`, `owner`) on the scope row. Subject readability
and the live-subject predicate are both resolved by the caller against the unified asset set,
so carrying denormalized subject columns on the scope row would be a second copy of the same
facts.

`IGitOpsImporter`
(in the `Freeboard.Persistence.GitOps` namespace - GitOps is one writer into the
general store) SHALL provide a method that replaces the persisted set from an
already-validated `GitOpsConfig`. `IGitOpsImporter.ImportAsync` SHALL document that
its caller guarantees the config has been validated; the importer SHALL NOT re-run
Core validation. `ImportAsync` SHALL return an import result carrying any scope subjects
that do not resolve against the persisted `assets` table (the DB-accurate subject-resolution
predicate) as non-blocking warnings, since Core, having no database, cannot evaluate a
discovered or retired subject; the importer SHALL compute that set within the import
transaction before commit (rolling the import back on a check failure), not after commit.
The web app's dependency-injection registration SHALL register
`IComplianceStore` for reads, so the web app's service provider does not resolve
`IGitOpsImporter` or `IMigrationRunner`. The MySQL implementations SHALL satisfy
these abstractions. Consumers SHALL depend on the abstractions, not the concrete
implementations.

#### Scenario: Read returns persisted domain

- **WHEN** a caller reads a snapshot naming every set after an import
- **THEN** it receives the persisted standards (with metadata), controls,
  requirements, the one unified asset set, and unified scopes with their `id`, `title`,
  resolved `subject`, one target, `disposition`, and `justification`

#### Scenario: The asset read returns every type with both edges

- **WHEN** a caller reads the unified asset set after an import and an ingest
- **THEN** it receives one row per asset - declared `Company`, `Department`, and `Vendor`
  and discovered `Machine` alike - each carrying `id`, `title`, `type`, `source`, `state`
  (null on a declared asset), `parent`, and `owner`, ordered by `id`

#### Scenario: There is no separate organisation or vendor read

- **WHEN** the `IComplianceStore` surface is inspected
- **THEN** it exposes no read returning only organisations and no read returning only
  vendors; both are served by filtering the one unified asset set by `type`

#### Scenario: Scope read carries no subject metadata

- **WHEN** a caller reads the unified scopes
- **THEN** each row carries only its `id`, `title`, `subject`, one target, `disposition`,
  and `justification`, with no joined subject `type`, `source`, `state`, `parent`, or
  `owner`, because the caller resolves readability against the unified asset set instead

#### Scenario: Counts include requirements and one scope count

- **WHEN** a caller reads the per-kind counts after an import
- **THEN** the counts include the number of persisted requirements and one unified scope
  count alongside standards, controls, and organisations

#### Scenario: Import replaces the persisted set

- **WHEN** a caller imports an already-validated `GitOpsConfig` via `IGitOpsImporter`
- **THEN** the store reflects exactly that config: resources present by `id` are
  upserted, and resources whose `id` is no longer present are removed

#### Scenario: Web registration resolves only the reader

- **WHEN** the web app's service provider is built from its read-path DI registration
- **THEN** it resolves `IComplianceStore` and does NOT resolve `IGitOpsImporter`
  or `IMigrationRunner`

### Requirement: Statement of Applicability input snapshots read one asset set

The Statement of Applicability inputs SHALL be read as snapshots of the one shape-parameterized
read - a flat shape for the JSON endpoint and the ingest admission check, and a drill-down shape
for the view page - each of which SHALL read the ONE unified asset set exactly once and SHALL
NOT read a separate organisation list, a separate vendor list, or a separate
resolvable-asset-id list.

The flat shape SHALL name the assets, the unified scopes, and the requirements. The
drill-down shape SHALL name those three plus the controls (with resolved `maps_to`) and the
unified collectors. In both shapes the resolution tree, the live-subject predicate for the
dangling-subject warning (an asset row exists and is not a discovered asset in the `Retired`
state), and - in the drill-down - the vendor titles SHALL all be derived from that one asset
list, so the projection cannot resolve over one view of the assets while warning against
another. Every list in a snapshot SHALL be read inside one transaction at repeatable-read
isolation, so the snapshot cannot straddle a concurrent importer commit.

#### Scenario: Both shapes read the assets once

- **WHEN** a caller reads either Statement of Applicability input shape
- **THEN** the result carries one unified asset list and no separate organisation list,
  vendor list, or resolvable-asset-id set, and every list in the snapshot was read in one
  repeatable-read transaction

#### Scenario: The warning predicate and the tree come from one read

- **WHEN** a snapshot is read while an importer concurrently removes an asset that a scope
  names as its subject
- **THEN** the resolution and the dangling-subject warning agree, because both are computed
  from the same asset list in the same snapshot

### Requirement: Vendor assurance inputs are read as one snapshot

The vendor assurance inputs SHALL be read as one repeatable-read snapshot naming the unified
assets and the vendor assurances together. Every consumer that narrows the assurances SHALL name
both sets in ONE snapshot rather than reading them separately.

Both lists SHALL be read inside one transaction at repeatable-read isolation, so the
snapshot cannot straddle a concurrent importer commit. The pairing is not an optimisation:
every consumer narrows the assurances by the `owner` edges carried on the asset rows, so
two separate reads can pair the pre-import owner edges with post-import assurance rows and
produce a combination that never existed in the database. The asset list SHALL be the same
unified set every other consumer reads, not a vendor-only or otherwise filtered one, so the
narrowing decision resolves over the same tree everywhere.

A surface that narrows the assurances by a further payload - the register, which also renders
each excluded scope's justification - SHALL name that payload in the SAME snapshot rather than
reading it separately. The standards SHALL stay a separate read. The criterion is NOT that a
separate read takes no part in the response: it is that the read takes no part in deciding what
the caller may see. The standards are a shared reference label, so an unresolvable standard
title already renders as the standard id and a title read from the far side of a commit costs a
label rather than a narrowing decision. The unified scopes ARE narrowed by the same vendor
visibility, so they SHALL travel in the snapshot of any decision that narrows them.

The assurance list SHALL be the whole set, ordered by vendor id then standard id, which the
caller groups by vendor, matching how the register reads scopes.

#### Scenario: The snapshot reads the assets and the assurances once, together

- **WHEN** a caller reads the vendor assurance inputs
- **THEN** it receives one unified asset list and the whole assurance set, both read in one
  repeatable-read transaction, and the snapshot reports that it was read with both sets

#### Scenario: Narrowing and the rows it narrows agree

- **WHEN** the snapshot is read while an importer concurrently commits a sync that changes
  both a vendor's `owner` and its assurance rows
- **THEN** the owner edges and the assurance rows in the result are from one side of that
  commit, so no surface derived from the snapshot can show a vendor's assurances against
  an owner edge that no longer decides its readability

#### Scenario: A narrowed scope read travels in the same snapshot

- **WHEN** the register reads the assets, the assurances, and the unified scopes for one
  render, while an importer concurrently reparents a vendor out of the caller's reach
- **THEN** all three lists are from one side of that commit, so the register cannot render an
  excluded scope's justification for a vendor the post-commit owner edge hides
