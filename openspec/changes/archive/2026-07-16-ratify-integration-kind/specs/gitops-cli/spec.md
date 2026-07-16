## MODIFIED Requirements

### Requirement: gitops commands enforce referential integrity for every config kind

The `gitops validate` and `gitops sync` commands SHALL apply the loader and
validator's referential-integrity checks to every config kind, including Vendor,
VendorScope, Integration, EvidenceCollector, and AttestationTemplate. A
config in which any kind references an id that no document defines SHALL be
rejected: `gitops validate` SHALL print a diagnostic that names the offending
kind, resource, and missing id, and SHALL exit non-zero, and `gitops sync` SHALL
NOT import such a config.

At the command surface the coverage is a representative dangling reference for
EACH new kind: a VendorScope naming an unknown vendor, an EvidenceCollector
naming an unknown control, an EvidenceCollector of `type: integration` naming an
unknown connection, and an AttestationTemplate naming an unknown control. The
exhaustive per-edge matrix (for example a VendorScope naming an unknown
requirement or control, an EvidenceCollector naming an unknown vendor, or an
Integration naming an unknown vendor) is owned by the `Freeboard.Core`
ConfigValidator unit tests, so the CLI layer proves command wiring only rather
than re-running every edge.

#### Scenario: Validate rejects a dangling vendor-scope vendor reference

- **WHEN** the user runs `gitops validate` on a directory whose VendorScope names
  a `vendor` id that no Vendor document defines
- **THEN** the command prints a diagnostic naming the VendorScope and the unknown
  vendor id and exits non-zero

#### Scenario: Validate rejects a dangling evidence-collector control reference

- **WHEN** the user runs `gitops validate` on a directory whose EvidenceCollector
  names a `control` id that no Control document defines
- **THEN** the command prints a diagnostic naming the EvidenceCollector and the
  unknown control id and exits non-zero

#### Scenario: Validate rejects a dangling evidence-collector connection reference

- **WHEN** the user runs `gitops validate` on a directory whose EvidenceCollector
  of `type: integration` names a `connection` id that no Integration
  document defines
- **THEN** the command prints a diagnostic naming the EvidenceCollector and the
  unknown connection id and exits non-zero

#### Scenario: Validate rejects a dangling attestation-template control reference

- **WHEN** the user runs `gitops validate` on a directory whose AttestationTemplate
  names a `control` id that no Control document defines
- **THEN** the command prints a diagnostic naming the AttestationTemplate and the
  unknown control id and exits non-zero

#### Scenario: Sync does not import a config with a dangling reference

- **WHEN** the user runs `gitops sync` on a directory that fails referential
  integrity for any of the new kinds
- **THEN** the command reports the validation error, exits non-zero, and writes
  no rows for that config

### Requirement: gitops sync round-trips and hard-removes the new config kinds

The `gitops sync` command SHALL persist Vendor, VendorScope, Integration,
EvidenceCollector, and AttestationTemplate resources from a valid config and, on a
later sync that drops a resource, SHALL hard-remove the persisted row whose id is
absent from the new config, in foreign-key-safe order, preserving the existing
hard-remove semantics. A resource kept across syncs SHALL remain persisted. The
foreign-key-safe order SHALL prune an absent EvidenceCollector before an absent
Integration it referenced, and an absent Integration before an
absent Vendor it referenced, so no `RESTRICT` foreign key blocks a removal.

#### Scenario: New kinds round-trip through sync

- **WHEN** the user runs `gitops sync` on a valid config containing vendors,
  vendor-scopes, integrations, and evidence-collectors (some of
  `type: integration` naming a connection and declaring checks), and
  attestation-templates against a migrated store
- **THEN** each resource is persisted and readable with its fields and references
  intact, including an integration's provider, base URL, and cadence and
  an integration collector's connection and checks

#### Scenario: Dropping a resource hard-removes it on the next sync

- **WHEN** a first `gitops sync` persists the new kinds and a second `gitops sync`
  runs on a config that omits one vendor-scope (keeping its vendor and its
  requirement or control target), one integration-collector (keeping its connection),
  and one integration whose only referencing collector was the omitted one
  (keeping its vendor)
- **THEN** the omitted rows are removed from the store, the retained rows remain,
  and the import succeeds without a foreign-key violation

The dropped VendorScope exercises the whole-set-replace removal path
(`MySqlGitOpsImporter` ReplaceVendorScopes), which is distinct from the
DeleteAbsent path used for evidence-collectors, integrations, and
attestation-templates. Dropping the integration-collector before its
integration exercises the foreign-key-safe delete ordering: the collector
row must clear before the connection row it referenced can be hard-removed.
