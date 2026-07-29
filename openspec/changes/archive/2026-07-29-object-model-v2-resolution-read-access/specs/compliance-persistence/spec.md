## MODIFIED Requirements

### Requirement: General read store and GitOps importer abstractions

The system SHALL expose separate abstractions for reading the store and for
importing config into it. `IComplianceStore` (the general read abstraction, in the
`Freeboard.Persistence` namespace) SHALL provide read methods returning the
persisted standards (with their `version`, `authority`, optional `publisher`, and
optional `source_url` metadata), controls (with their resolved `maps_to`
`Requirement` ids, read from the `control_requirements` join), requirements (with
their resolved owning `standard`, `theme`, `statement`, `guidance`,
`citation_label`, and `citation_url`), ONE unified ASSET set, and
scopes (each with its resolved `subject`, exactly one target of
`standard`/`requirement`/`control`, `disposition`, and `justification`) and per-kind
counts that include requirements and one unified scope count.

The unified asset read SHALL return every row of the `assets` table - `Company`,
`Department`, `Machine`, and `Vendor`, declared and discovered - each with its `id`, `title`,
`type`, `source`, `state` (null on a declared asset), and the two scalar edges `parent` and
`owner`. It SHALL REPLACE the separate organisation read and vendor read: there SHALL NOT be a
per-type read method returning organisations or vendors, because a second read model over the
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

- **WHEN** a caller invokes the `IComplianceStore` read methods after an import
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
- **THEN** it exposes no method returning only organisations and no method returning only
  vendors; both are served by filtering the one unified asset read by `type`

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

## ADDED Requirements

### Requirement: Statement of Applicability input snapshots read one asset set

The store SHALL expose the Statement of Applicability inputs as two repeatable-read
snapshots - a flat snapshot for the JSON endpoint and a drill-down snapshot for the view page -
each of which SHALL read the ONE unified asset set exactly once and SHALL NOT read a separate
organisation list, a separate vendor list, or a separate resolvable-asset-id list.

The flat snapshot SHALL carry the assets, the unified scopes, and the requirements. The
drill-down snapshot SHALL carry those three plus the controls (with resolved `maps_to`) and the
unified collectors. In both snapshots the resolution tree, the live-subject predicate for the
dangling-subject warning (an asset row exists and is not a discovered asset in the `Retired`
state), and - in the drill-down - the vendor titles SHALL all be derived from that one asset
list, so the projection cannot resolve over one view of the assets while warning against
another. Every list in a snapshot SHALL be read inside one transaction at repeatable-read
isolation, so the snapshot cannot straddle a concurrent importer commit.

#### Scenario: Both snapshots read the assets once

- **WHEN** a caller reads either Statement of Applicability input snapshot
- **THEN** the result carries one unified asset list and no separate organisation list,
  vendor list, or resolvable-asset-id set, and every list in the snapshot was read in one
  repeatable-read transaction

#### Scenario: The warning predicate and the tree come from one read

- **WHEN** a snapshot is read while an importer concurrently removes an asset that a scope
  names as its subject
- **THEN** the resolution and the dangling-subject warning agree, because both are computed
  from the same asset list in the same snapshot
