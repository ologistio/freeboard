## ADDED Requirements

### Requirement: gitops commands enforce referential integrity for every config kind

The `gitops validate` and `gitops sync` commands SHALL apply the loader and
validator's referential-integrity checks to every config kind, including Vendor,
VendorScope, EvidenceCollector, and AttestationTemplate. A config in which any
kind references an id that no document defines SHALL be rejected: `gitops
validate` SHALL print a diagnostic that names the offending kind, resource, and
missing id, and SHALL exit non-zero, and `gitops sync` SHALL NOT import such a
config.

At the command surface the coverage is a representative dangling reference for
EACH new kind: a VendorScope naming an unknown vendor, an EvidenceCollector
naming an unknown control, and an AttestationTemplate naming an unknown control.
The exhaustive per-edge matrix (for example a VendorScope naming an unknown
requirement or control, or an EvidenceCollector naming an unknown vendor) is
owned by the `Freeboard.Core` ConfigValidator unit tests, so the CLI layer proves
command wiring only rather than re-running every edge.

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

The `gitops sync` command SHALL persist Vendor, VendorScope, EvidenceCollector,
and AttestationTemplate resources from a valid config and, on a later sync that
drops a resource, SHALL hard-remove the persisted row whose id is absent from the
new config, in foreign-key-safe order, preserving the existing hard-remove
semantics. A resource kept across syncs SHALL remain persisted.

#### Scenario: New kinds round-trip through sync

- **WHEN** the user runs `gitops sync` on a valid config containing vendors,
  vendor-scopes, evidence-collectors, and attestation-templates against a
  migrated store
- **THEN** each resource is persisted and readable with its fields and references
  intact

#### Scenario: Dropping a resource hard-removes it on the next sync

- **WHEN** a first `gitops sync` persists the new kinds and a second `gitops sync`
  runs on a config that omits one vendor-scope (keeping its vendor and its
  requirement or control target), one evidence-collector (keeping its control and
  vendor), and one attestation-template (keeping its control)
- **THEN** the omitted rows are removed from the store, the retained rows remain,
  and the import succeeds without a foreign-key violation

The dropped VendorScope exercises the whole-set-replace removal path
(`MySqlGitOpsImporter` ReplaceVendorScopes), which is distinct from the
DeleteAbsent path used for evidence-collectors and attestation-templates, so it is
worth covering at the command surface. A bare Vendor's hard-remove uses the same
DeleteAbsent path and is covered by the Persistence
`MySqlIntegrationTests.ResyncRemovesVendorThatHadVendorScope` resync-removal test,
so the CLI case delegates it rather than adding an unreferenced vendor to the
fixture.
