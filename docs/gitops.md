# GitOps config management

Freeboard manages compliance state as declarative YAML in git, FleetDM-style. The
git files are the source of truth: standards, the requirements each standard
publishes, the controls that satisfy those requirements, the assets being
assessed, and the scopes that map a subject asset to a standard, requirement, or
control. A CLI validates
and previews the config, and the web app can run read-only so changes flow through
git rather than the UI.

It ships the config format, `validate`, and `apply --dry-run`, plus a web
read-only mode. A MySQL persistence layer now backs the compliance domain:
`gitops sync` imports a validated config into the store, `system migrate` applies
the schema, and the web app serves the persisted domain read-only. Real
reconciling apply, soft-delete on removal, and drift detection are not built yet.

## Fleet noun mapping

Freeboard borrows Fleet's structure but renames the nouns for compliance:

| Fleet    | Freeboard               | Meaning                                                                     |
| -------- | ----------------------- | --------------------------------------------------------------------------- |
| (n/a)    | assets                  | the estate being assessed: companies, departments, vendors, and machines    |
| labels   | scopes                  | maps one subject asset to a standard, requirement or control                |
| policies | checks                  | conformance checks; authored per integration collector, execution deferred  |
| (n/a)    | controls                | an implemented control mapped to one or more requirements                   |
| (n/a)    | requirements            | a standard's published normative statements                                 |
| (n/a)    | standards               | a compliance standard in scope                                              |
| (n/a)    | evidence-collectors     | attaches a data source to a control                                         |
| (n/a)    | attestation-templates   | a form or quiz attached to a control                                        |
| (n/a)    | integration-connections | a provider connection driving discovery and integration collectors          |

This increment ships `standards`, `requirements`, `controls`, `assets`, `scopes`,
`evidence-collectors`, `attestation-templates`, and `integration-connections`.
`checks` are authored per integration collector (the tracked-check list) but their
execution runner is not yet built.

## Format

A config directory holds one or more `.yaml` files. Each file is a stream of one
or more documents separated by `---`. Every document declares:

- `apiVersion` - must be exactly `freeboard.dev/v1alpha1`.
- `kind` - one of `Standard`, `Requirement`, `Control`, `Asset`, `Scope`,
  `EvidenceCollector`, `AttestationTemplate`, or `Integration`.

`apiVersion` and `kind` stay camelCase (Kubernetes-style). All other fields are
snake_case (so `maps_to`, not `mapsTo`). Unknown fields are rejected so typos
surface instead of being silently dropped.

Every resource has:

- `id` - a stable, immutable identity. References and duplicate detection key off
  `id`, never the title.
- `title` - human-facing display text that may change without changing identity.

### Standard

`version` and `authority` are required and non-empty. `publisher` and `source_url`
are optional; omit them (or leave them blank) when they do not apply. `source_url`,
when present, must be an absolute `http`/`https` URL.

```yaml
apiVersion: freeboard.dev/v1alpha1
kind: Standard
id: std-cyber-essentials-plus
title: Cyber Essentials Plus
version: "3.3"
authority: National Cyber Security Centre
publisher: IASME Consortium
source_url: https://www.ncsc.gov.uk/files/cyber-essentials-requirements-for-it-infrastructure-v3-3.pdf
```

### Requirement

A requirement is a published normative statement belonging to exactly one
`Standard` (named by the singular `standard` field). `theme` is a free-form label
grouping a standard's requirements. `title` is a short display label; `statement`
is the full normative text. `guidance` is optional. The citation is two fields:
`citation_label` (a human label) and `citation_url` (an absolute `http`/`https`
link to the published source).

```yaml
apiVersion: freeboard.dev/v1alpha1
kind: Requirement
id: req-ce-plus-user-access-control-04
title: Multi-factor authentication
standard: std-cyber-essentials-plus
theme: User Access Control
statement: Implement multi-factor authentication where available, and always for authentication to cloud services.
citation_label: Cyber Essentials - Requirements for IT Infrastructure v3.3 - User Access Control
citation_url: https://www.ncsc.gov.uk/files/cyber-essentials-requirements-for-it-infrastructure-v3-3.pdf
```

### Control

`maps_to` is a non-empty list of `Requirement` ids: the specific requirements the
control satisfies. A control's standard is derived from those requirements, so
`maps_to` no longer names `Standard` ids.

```yaml
apiVersion: freeboard.dev/v1alpha1
kind: Control
id: ctrl-mfa
title: Multi-factor authentication enforced
maps_to:
  - req-ce-plus-user-access-control-04
```

### Asset

An asset is a single kind spanning the whole estate. The document discriminator
`kind` is always `Asset`; the specific type is authored under `type`, one of
`Company`, `Department`, `Vendor`, or `Machine`. `source` says how the asset
entered the store: config authors only `source: declared`. `Machine` assets are
normally `discovered` (written by ingest), but any declared document - whatever its
`type` - may not carry the discovered-only fields (`identity_kind`,
`identity_value`, `state`, `first_seen`, `last_seen`); the loader rejects them.

Two mutually-exclusive edges anchor an asset:

- `parent` - containment. A `Company`, `Department`, or `Machine` may name a
  `parent` that is another `Company`/`Department` asset (omit it for a root
  company). `parent` drives read-access and scope inheritance.
- `owner` - accountability. Only a `Vendor` names an `owner`, which must be a
  `Company`/`Department` asset. `owner` drives vendor read-access.

An asset sets at most one of `parent` or `owner`. A missing or dangling
`parent`/`owner` is a non-blocking warning, not an error: the asset is simply
visible to no caller until the edge resolves, and one uncoordinated writer cannot
wedge a `sync`. A `parent` cycle among declared assets is likewise a warning. A
declared `Vendor` with no `owner` and a `Machine` with no `parent` each warn (they
would be visible to nobody); a parent-less `Company`/`Department` root does not.

```yaml
apiVersion: freeboard.dev/v1alpha1
kind: Asset
id: ologist-products
title: Ologist Products Ltd
type: Company
source: declared
---
apiVersion: freeboard.dev/v1alpha1
kind: Asset
id: ologist-products-eng
title: Engineering
type: Department
source: declared
parent: ologist-products
```

### Scope

A scope maps one `subject` (any asset id) to exactly one target - a `Standard`, a
`Requirement`, or a `Control` - with a `disposition` (`In` or `Out`). The `subject`
is a typed asset id: a Company/Department (org tree), a Vendor, or a Machine (or any
other parent-anchored asset). Exactly one of `standard`, `requirement`, or `control`
must be set (never two, never none). `justification` is required when `disposition`
is `Out` (it explains the exception) and optional when `In`. At most one scope may
exist per `(subject, standard)`, `(subject, requirement)`, and `(subject, control)`
pair.

One cross-field rule: a `Vendor` subject cannot target a `standard` (a vendor has no
standard-level disposition); it targets a `requirement` or a `control`. Org subjects
may target any of the three.

```yaml
apiVersion: freeboard.dev/v1alpha1
kind: Scope
id: scope-products-ce
title: Ologist Products - Cyber Essentials
subject: ologist-products
standard: std-cyber-essentials
disposition: In
---
apiVersion: freeboard.dev/v1alpha1
kind: Scope
id: rs-products-firewalls-01-out
title: Exclude firewall-on-every-device company-wide
subject: ologist-products
requirement: req-ce-plus-firewalls-01
disposition: Out
justification: >-
  Company-issued laptops run a managed host firewall centrally; the per-device
  perimeter-firewall requirement is excepted pending the endpoint rollout.
---
apiVersion: freeboard.dev/v1alpha1
kind: Scope
id: vs-okta-firewall-out
title: Okta host-firewall control not applicable
subject: vendor-okta
control: ctrl-firewall
disposition: Out
justification: SaaS identity provider - no host firewall under our control.
```

**Standard-target scopes** are sparse and scoping is opt-out: a node with no scope
for a standard inherits its nearest ancestor's disposition, and a node with no scope
on its path to the root defaults to `In` (in scope). An explicit `Out` opts a
standard out, and a descendant `In` overrides an opted-out ancestor. The Statement
of Applicability (below) resolves this per node. A standard left unscoped is in
scope; a deployment that wants a standard to stay out MUST author an explicit `Out`
at the appropriate organisation. A root-level `In` is a redundant no-op (it resolves
`In` marked `explicit` instead of `default`) and may be deleted.

**Requirement-target scopes** layer under the standard-level scope. For a node and a
requirement (owned by standard S): if the node's disposition for S resolves `Out`,
the requirement follows the standard and requirement-target scopes are not consulted;
only where S resolves `In` does the requirement layer apply. Within an `In` standard,
requirement-target scopes inherit by the same nearest-ancestor rule as standard
scopes, and a child re-includes (`In`) a requirement an ancestor excluded (`Out`). A
requirement-level `In` cannot re-include a requirement whose standard is `Out`.

**Vendor-subject scopes** are flat: they do not inherit down the org tree. They
record whether one `Requirement` or one `Control` applies to a vendor, with a
required `justification` on `Out`. The vendor register always surfaces the
justification, so an exception is never silent.

**Control-target scopes** are stored and read back, but the Statement of
Applicability resolves only standard-level and requirement-level dispositions, so a
control-target scope contributes no resolved node disposition yet (control-level
resolution is deferred). Its dangling-subject warning (below) still applies.

The `subject` is a scalar reference with NO foreign key: it may name an asset a later
sync removes, a retired discovered machine, or a not-yet-discovered asset. A `subject`
that resolves to no live asset is a NON-BLOCKING warning at sync and a generic
page-level notice on the Statement of Applicability, never an error; the scope still
loads and persists. The three target references (`standard`/`requirement`/`control`)
are the opposite: each keeps a real foreign key, so a dangling target is a hard error
and a standard/requirement/control cannot be deleted while a scope targets it.

### Vendor asset

A vendor is an `Asset` with `type: Vendor`. It names a piece of software or
platform in use. `owner` (a Company/Department asset) makes the vendor visible to
that org's readers; a vendor with no owner is visible to no caller (a non-blocking
warning). See [Asset](#asset) above.

```yaml
apiVersion: freeboard.dev/v1alpha1
kind: Asset
id: vendor-okta
title: Okta
type: Vendor
source: declared
owner: ologist-products
```

### EvidenceCollector

An evidence-collector attaches a data source to one `Control` (the attach point
named by the required `control` field) and, optionally, to one `Vendor` (the
optional `vendor` field). `type` is one of `integration`, `script`,
`manual-attestation`, `training-attestation`, or `agent`. `frequency` is the
collection cadence, one of `continuous`, `daily`, `weekly`, `monthly`,
`quarterly`, or `annual`. `threshold` is optional; when present it is an integer
percent from 0 to 100. `config` is an optional free-form map of type-specific
settings; it holds no secret material.

A collector of `type: integration` additionally requires a `connection` (the id of
an `Integration`) and a non-empty `checks` list. Each `checks` item is a
tracked check with a `source_key` (the provider-native id, e.g. a Fleet policy id),
a `name` (the Freeboard check name), and a `severity` of `Hard` or `Soft`. The
authored `checks` list is the exhaustive tracked set: a provider result whose id is
not authored here is not a tracked check and changes nothing. `name` and `source_key`
are each unique within the collector. A collector of any other type must not declare
`connection` or `checks`.

A control that has at least one attached evidence-collector must declare an
`evaluation` roll-up rule (`all`, `any`, or `manual`) saying how its collectors
combine into a status.

```yaml
apiVersion: freeboard.dev/v1alpha1
kind: Control
id: ctrl-mfa
title: Multi-factor authentication enforced
maps_to:
  - req-ce-plus-user-access-control-04
evaluation: all
---
apiVersion: freeboard.dev/v1alpha1
kind: EvidenceCollector
id: ec-okta-mfa
title: Okta MFA enrolment
control: ctrl-mfa
vendor: vendor-okta
type: integration
frequency: daily
threshold: 95
connection: conn-fleet-prod
config:
  policy: default
checks:
  - source_key: "42"
    name: mfa-enforced
    severity: Hard
```

### AttestationTemplate

An attestation-template is a form or quiz attached to one `Control` (the attach
point named by the required `control` field). A template references only its
attach-point control; its standard is reached through the control's mapped
requirements, so there is no `requirement` or `standard` field. `type` is `manual`
or `training`. `body` is optional markdown stored verbatim.

A `manual` template collects `fields`: an ordered list of form fields, each with
an `id`, a `label`, and a `type` (`boolean`, `single-choice`, or `short-text`). A
`single-choice` field carries two or more `options`; other field types carry none.
A `manual` template must not declare `pass_mark` or `quiz`.

A `training` template requires a `pass_mark` (an integer percent from 0 to 100)
and a non-empty `quiz`: an ordered list of items, each with an `id`, a `prompt`,
two or more `options`, and an `answer` that is one of those options. The answer is
persisted for grading but redacted from every read surface.

```yaml
apiVersion: freeboard.dev/v1alpha1
kind: AttestationTemplate
id: attest-mfa-review
title: MFA enforcement review
control: ctrl-mfa
type: manual
fields:
  - id: enforced
    label: Is MFA enforced for all cloud services?
    type: boolean
---
apiVersion: freeboard.dev/v1alpha1
kind: AttestationTemplate
id: attest-phishing
title: Phishing awareness
control: ctrl-mfa
type: training
pass_mark: 90
quiz:
  - id: q1
    prompt: What should you do with an unexpected attachment?
    options:
      - Open it immediately
      - Report it and do not open it
    answer: Report it and do not open it
```

### Integration

An `Integration` is a provider connection - one base URL and a discovery
cadence that drive machine discovery and back many integration collectors. `provider`
is a closed token selecting the runner/adapter; its only value is `fleet`. `provider`
is not unique: one provider backs many connections, and identity is `id`. `base_url`
is a required absolute `http`/`https` URL. `discovery_cadence` is required and drawn
from the same vocabulary as a collector's `frequency` (`continuous`, `daily`,
`weekly`, `monthly`, `quarterly`, `annual`). `vendor` is an optional `Vendor` id.

The API token is resolved out-of-band by connection id from configuration at
`Freeboard:Integrations:<id>:ApiToken` (supplied by environment variables or
user-secrets). It is never a field here, never authored in git, never persisted, and
never logged. Because the id becomes a configuration-key segment, a connection `id`
must not contain `:` or `__`, and two connection ids must not collide
case-insensitively.

```yaml
apiVersion: freeboard.dev/v1alpha1
kind: Integration
id: conn-fleet-prod
title: Fleet Production
provider: fleet
base_url: https://fleet.example.com
discovery_cadence: daily
vendor: vendor-okta
```

## Validation

Validation collects every error in one pass (not just the first). It fails when:

- a required field is missing or empty;
- a document has an unknown field;
- an `id` is duplicated within its kind;
- a `Standard` omits `version` or `authority` (both are required and non-empty);
- a `Standard.source_url` is present but not an absolute `http`/`https` URL;
- a `Requirement.standard` names a `Standard` id that does not exist;
- a `Requirement.citation_url` is not an absolute `http`/`https` URL;
- a `Control.maps_to` entry names a `Requirement` id that does not exist;
- a `Control.maps_to` lists the same `Requirement` id more than once;
- an `Asset.type` is not one of `Company`, `Department`, `Vendor`, or `Machine`;
- an `Asset.source` is not `declared`, or is `discovered` (which cannot be authored);
- an `Asset` sets both `parent` and `owner`, or sets an edge to the wrong carrier
  type (a `Vendor` with `parent`, or a non-`Vendor` with `owner`);
- an `Asset.parent` or `Asset.owner` names an asset that is not a `Company`/`Department`;
- a declared `Asset` carries a discovered-only field (`identity_kind`,
  `identity_value`, `state`, `first_seen`, `last_seen`);
- a `Scope` does not name exactly one of `standard`, `requirement`, or `control`
  (none set, or more than one);
- a `Scope.standard`, `Scope.requirement`, or `Scope.control` names an id that does
  not exist;
- a `Scope.disposition` is not `In` or `Out`;
- a `Scope` has `disposition: Out` but no `justification`;
- a `Scope.subject` resolves to a `Vendor` asset and the target is a `standard` (a
  vendor has no standard-level disposition);
- two scopes name the same `(subject, standard)`, `(subject, requirement)`, or
  `(subject, control)` pair;
- an `EvidenceCollector.control` names a `Control` id that does not exist;
- an `EvidenceCollector.vendor` is present but names a `Vendor` id that does not
  exist;
- an `EvidenceCollector.type` is not one of `integration`, `script`,
  `manual-attestation`, `training-attestation`, `agent`;
- an `EvidenceCollector.frequency` is not one of `continuous`, `daily`, `weekly`,
  `monthly`, `quarterly`, `annual`;
- an `EvidenceCollector.threshold` is present but not an integer percent from 0 to
  100;
- a `Control` has at least one attached evidence-collector but omits `evaluation`
  (which must be `all`, `any`, or `manual`);
- an `EvidenceCollector` of `type: integration` omits `connection`, names an
  `Integration` id that does not exist, or omits a non-empty `checks`;
- an `EvidenceCollector` of any other type declares `connection` or `checks`;
- an `EvidenceCollector` `checks` item omits `source_key`/`name`/`severity`, has a
  `severity` other than `Hard` or `Soft`, or repeats a `name` or `source_key` within
  the collector;
- an `Integration` omits a required field (`provider`, `base_url`,
  `discovery_cadence`), has a `provider` other than `fleet`, a `base_url` that is not
  an absolute `http`/`https` URL, or a `discovery_cadence` outside the frequency set;
- an `Integration.vendor` is present but names a `Vendor` id that does not
  exist;
- an `Integration.id` contains `:` or `__`, or two connection ids collide
  case-insensitively (the id resolves an out-of-band configuration token key);
- an `AttestationTemplate.control` names a `Control` id that does not exist;
- an `AttestationTemplate.type` is not `manual` or `training`;
- an `AttestationTemplate.pass_mark` is present but not an integer percent from 0
  to 100;
- an `AttestationTemplate` of type `training` omits `pass_mark` or a non-empty
  `quiz`, or one of type `manual` declares `pass_mark` or `quiz`;
- an `AttestationTemplate` field is malformed - the validator rejects, among other
  cases, a missing `id`/`label`/`type`, a duplicate field `id`, an unknown field
  type, a `single-choice` field with fewer than two options or with duplicate
  options, and a non-`single-choice` field that declares options;
- an `AttestationTemplate` quiz item is malformed - the validator rejects, among
  other cases, a missing `id`/`prompt`/`answer`, a duplicate quiz `id`, fewer than
  two options, duplicate options, and an `answer` that is not one of its options;
- `apiVersion` is not exactly `freeboard.dev/v1alpha1`.

A missing or unknown `kind`, and malformed YAML, are reported as diagnostics by
the loader rather than throwing.

Some conditions are non-blocking warnings, not errors: they are printed to stderr
but `validate`, `apply --dry-run`, and `sync` still succeed (exit 0). These are a
dangling or missing `Asset.parent`/`owner`, a `parent` cycle among declared
assets, a declared `Vendor` with no `owner`, a `Machine` with no `parent`, and a
`Scope.subject` that resolves to no live asset. The rationale is that one
uncoordinated writer (for example a discovered machine naming a declared parent a
later `sync` removes) must not be able to wedge the whole config; the asset is
simply invisible to readers until the edge resolves, and a scope with a dangling
subject is hidden but not fatal.

The dangling-subject warning is computed two ways. `validate` and `apply --dry-run`
have no database, so they warn whenever a `subject` names no asset authored in the
config (the authored-set check). `sync` has the store, so it warns from the
DB-accurate asset set: a subject that resolves to no `assets` row, or only to a
retired discovered asset, is unresolved. So a healthy discovered machine subject
draws no false sync warning, while a retired or truly-absent subject warns at sync
with the database as ground truth.

## Commands

```sh
# Validate. Exit 0 when valid, 1 on validation or input error (incl. missing path).
freeboard gitops validate <dir>

# Dry-run. Print the state that would be applied. Exit 0 when valid, 1 on error.
freeboard gitops apply <dir> --dry-run
```

`--dry-run` is required for `apply` in this version. Running `apply` without
`--dry-run` exits `2` and prints that real apply lands in a later increment.
`validate` and `apply --dry-run` make no network calls and write no state. `sync`
(below) is the explicit write path that loads config into the store.

## Persistence

The compliance domain (standards, requirements, controls, assets, scopes) is
persisted in MySQL. The data is the general compliance store; GitOps
`sync` is one writer into it.

### Schema

Eight domain tables (`standards`, `requirements`, `controls`, `assets`,
`scopes`, `evidence_collectors`, `attestation_templates`,
`integration_connections`), each keyed on `id`
with `api_version`, `title`, `created_at`, and `updated_at`. `standards` also
carries nullable `version`, `authority`, `publisher`, and `source_url` metadata
columns. `requirements` has a `standard_id` foreign key (`ON DELETE RESTRICT`), a
`theme`, a `statement`, nullable `guidance`, and `citation_label`/`citation_url`.
`assets` is the one unified estate table: companies, departments, vendors, and
machines share the id space, discriminated by a `type` column
(`Company`/`Department`/`Vendor`/`Machine`) and a `source` column
(`declared`/`discovered`). It carries two scalar, FK-free edges - `parent`
(containment) and `owner` (accountability), mutually exclusive by `CHECK` - so a
`parent`/`owner` may dangle; declared rows carry `api_version`/`title`, discovered
machine rows carry the identity/`state`/seen columns. Organisations are the
`Company`/`Department` subset. `scopes` is the one unified scope table: a scalar
`subject_id` with NO foreign key (so a subject may dangle, matching an asset's
`parent`/`owner`); nullable `standard_id`, `requirement_id`, and `control_id`
foreign keys (each `ON DELETE RESTRICT` to `standards`/`requirements`/`controls`); a
`disposition` column; and a nullable `justification`. A `CHECK` constraint enforces
that exactly one of the three target columns is set, and three unique keys on
`(subject_id, standard_id)`, `(subject_id, requirement_id)`, and
`(subject_id, control_id)` bound each target pair (MySQL treats each `NULL` as
distinct, so a key constrains only the rows whose own target column is non-null).
`evidence_collectors` has a required `control_id` foreign key and a nullable
`vendor_id` foreign key (both `ON DELETE RESTRICT`), the `type`/`frequency` token
columns, a nullable `threshold`, and a native JSON `config` column.
`attestation_templates` has a required `control_id` foreign key (`ON DELETE
RESTRICT`), a `type` column, a nullable `body`, a nullable `pass_mark`, and native
JSON `fields`/`quiz` columns. `integration_connections` has a `provider`, a
`base_url`, a `discovery_cadence`, and a nullable `vendor_id` foreign key to the
`Vendor` assets; an evidence collector references it through a nullable
`connection_id` foreign key. The `evidence_collectors` and `attestation_templates`
`RESTRICT` foreign keys are why `gitops sync` prunes an absent collector or
template before deleting the control or vendor it referenced.
One relation table
(`control_requirements` for `Control.maps_to`) with a composite primary key and
`ON DELETE CASCADE` foreign keys. One migration-tracking table
(`schema_migrations`) bootstrapped by the migration runner.

Every `id` and foreign-key column uses the binary collation `utf8mb4_bin`, so the
database's identity rules match Core's case-sensitive, exact-byte `id` semantics
(`ctrl-a` and `CTRL-A` are distinct ids).

### Migrate first, then sync

Migrations are forward-only and applied explicitly. The web app never
auto-migrates and never auto-syncs.

```sh
# Apply pending schema migrations.
freeboard system migrate --connection-string "<conn>"

# Import a validated config into the store.
freeboard gitops sync <dir> --connection-string "<conn>"
```

Migration 011 (and any later trigger-using migration) runs `CREATE TRIGGER`. On a
binary-logging MySQL 8.x server the migration database user must either connect to a
server started with `log_bin_trust_function_creators=1` or hold a privilege sufficient
to create triggers under binary logging; otherwise `system migrate` fails with error
1419 (`ER_BINLOG_CREATE_ROUTINE_NEED_SUPER`).

`gitops sync` loads and validates the config via the same path as `validate`, then
checks migration state. If the schema is not current and `--migrate` is not
supplied, it exits `3` and writes nothing (not even the tracking table). Pass
`--migrate` to apply pending migrations first, then import:

```sh
freeboard gitops sync <dir> --connection-string "<conn>" --migrate
```

Exit codes for the persistence-backed commands: `0` success; `1` validation or
input error (`gitops sync`, writes nothing); `3` operational failure (missing
connection string, database unreachable, schema not current without `--migrate`,
migration checksum mismatch, an applied migration missing from the embedded
migrations, or a migration that fails during execution).

### sync vs apply --dry-run

`apply --dry-run` validates and prints the planned state; it writes nothing and
makes no network call. `gitops sync` connects to the database and writes the
persisted set. `sync` is the explicit increment-2 loading mechanism and will be
subsumed by real reconciling `apply` later.

### Hard removal warning

`gitops sync` replaces the persisted set: it upserts every resource in the config
by `id` and HARD-REMOVES any persisted resource whose `id` is absent from the
config. Narrowing the config deletes rows. There is no soft-delete yet. Review the
config before syncing.

### Web read endpoints

The web app serves the persisted domain read-only (GET only, not blocked by
read-only mode). All routes live under the `/api/v1/freeboard/` prefix:

- `GET /api/v1/freeboard/standards` - persisted standards (`id`, `title`,
  `version`, `authority`, `publisher`, `source_url`; the last four are null when
  unset).
- `GET /api/v1/freeboard/requirements` - persisted requirements (`id`, `title`,
  `standard`, `theme`, `statement`, `guidance`, and a composed
  `citation: { label, url }`).
- `GET /api/v1/freeboard/controls` - persisted controls (`id`, `title`, `maps_to`;
  `maps_to` carries `Requirement` ids).
- `GET /api/v1/freeboard/organisations` - persisted organisations (`id`, `title`,
  `kind`, resolved `parent`, null for a root).
- `GET /api/v1/freeboard/scopes` - persisted scopes (`id`, `title`, `subject`,
  `standard`, `requirement`, `control`, `disposition`, `justification`; exactly one
  of `standard`/`requirement`/`control` is set, the others null; `justification` is
  null when unset). Rows are narrowed by subject readability: an org-tree subject in
  the caller's accessible-organisation set, a vendor subject whose `owner` is in that
  set, and a machine (or other parent-anchored) subject whose parent-org ancestry
  reaches that set. A subject that is missing, dangling, or resolves to no live asset
  hides the scope (fail-closed), so a hidden subject's `Out` justification never
  leaks.
- `GET /api/v1/freeboard/vendors` - persisted vendors (`id`, `title`). Narrowed by
  owner access: a vendor is returned only when its `owner` (a Company/Department
  asset) is in the caller's accessible-organisation set; a vendor with a null or
  dangling owner is hidden from everyone (fail-closed).
- `GET /api/v1/freeboard/integration-connections` - persisted integration
  connections (`id`, `provider`, `base_url`, `discovery_cadence`, `vendor`, and a
  read-time `token_resolvable` health flag). The API token is never returned. These
  are not narrowed by organisation access - any authenticated user reads every row.
- `GET /api/v1/freeboard/statement-of-applicability/{standardId}` - the SoA
  projection for a standard: every organisation node with its resolved
  `disposition` (always `In` or `Out`) and whether that value is `explicit`,
  `inherited`, or `default` (`default` means in scope with no authored scope on the
  path), plus a `requirements` list of the per-requirement deviations for nodes
  whose standard resolves `In` (each with its `requirement`, resolved
  `disposition`, and `explicit`/`inherited` resolution; a requirement not listed
  follows the node's standard disposition). A node resolving `Out` always carries an
  empty `requirements` list (requirement-target scopes are not applied under an
  out-of-scope standard); an in-scope node (`explicit`, `inherited`, or `default`)
  carries its per-requirement deviations, which is an empty list when it has none.
  The `/compliance/statement-of-applicability` page additionally shows a generic,
  non-blocking notice ("rule targets a resource that does not currently exist") when
  any scope of any target kind has a subject that resolves to no live asset; the
  notice names neither the scope nor the subject id.
- `GET /api/v1/freeboard/compliance/status` - a `persisted` object of per-kind
  counts, carrying one `scopes` count for the unified scope table.

Resources are ordered by `id`; relation arrays are ordered by id. When the store
is unreachable, the read endpoints return HTTP 503 with an RFC 7807 problem body,
and `/api/v1/freeboard/compliance/status` returns HTTP 200 with
`{ "persisted": { "standards": null, "controls": null, "requirements": null, "organisations": null, "scopes": null, "vendors": null, "evidenceCollectors": null, "attestationTemplates": null } }`
(`null` marks the count as unknown, not zero). Both the healthy and unreachable-store
shapes carry the same keys, including a single `scopes` count and no
`requirementScopes` or `vendorScopes` keys. `GET /api/v1/freeboard/gitops/status` is unchanged and does not depend on the
store.

### App-managed writes

When the instance is NOT in GitOps read-only mode, organisations, standard-target
scope dispositions, and requirement-target scope dispositions can be written through
the API, enforcing the same invariants as import. Both scope routes write the one
unified `scopes` table, each confined to its own target column:

- `PUT /api/v1/freeboard/organisations/{id}` - create or update an organisation.
- `DELETE /api/v1/freeboard/organisations/{id}` - delete an organisation (fails if
  it still has children or scopes).
- `PUT /api/v1/freeboard/scopes/{id}` - set a standard-target scope disposition for
  a `(subject, standard)` pair. The body may carry a `justification`, required when
  the disposition is `Out`.
- `DELETE /api/v1/freeboard/scopes/{id}` - delete a standard-target scope.
- `PUT /api/v1/freeboard/requirement-scopes/{id}` - set a requirement-target scope
  disposition for a `(subject, requirement)` pair. The body may carry a
  `justification`, required when the disposition is `Out`.
- `DELETE /api/v1/freeboard/requirement-scopes/{id}` - delete a requirement-target
  scope.

Each route addresses only rows of its own target kind: the `/scopes` route reaches
standard-target rows and the `/requirement-scopes` route requirement-target rows. An
id that names an existing row of a different target kind is NOT-FOUND (404), never a
silent retarget; a brand-new id creates a row of the route's own target kind.
Control-target scopes are gitops-write-only and reachable by neither app route.
Vendor-subject scopes also stay gitops-write-only.

An invalid write returns an RFC 7807 problem body and changes nothing. In GitOps
read-only mode these endpoints are rejected with HTTP 409 by the read-only
middleware, exactly as other mutating routes are.

### Connection string

The connection string is a secret. Supply it via an environment variable, .NET
user-secrets, or a config provider only - never in the GitOps YAML (its schema has
no secret fields) and never in committed config.

- Web app: `ConnectionStrings:Freeboard` (standard .NET config).
- CLI: a per-subcommand `--connection-string` option, or the `FREEBOARD_DB`
  environment variable. An explicit `--connection-string` overrides `FREEBOARD_DB`.

`FREEBOARD_DB` is the runtime connection string for `gitops sync` and
`system migrate`. `FREEBOARD_TEST_DB` is a separate variable read only by the
integration test suite to discover a MySQL to run against; the integration tests
skip cleanly when it is absent. Do not confuse the two.

### Local MySQL

The test-infrastructure project `tests/Freeboard.TestInfrastructure` is the single
home for test infrastructure and local-dev tooling: the shared MySQL test fixture,
the `docker-compose.yml`, and the mysql-init grant script. Its `docker-compose.yml`
stands up MySQL 8 for development and the integration tests:

```sh
docker compose -f tests/Freeboard.TestInfrastructure/docker-compose.yml up -d
export FREEBOARD_DB="Server=127.0.0.1;Port=3306;Database=freeboard;User ID=freeboard;Password=freeboard;"
export FREEBOARD_TEST_DB="$FREEBOARD_DB"
```

The integration tests create a throwaway `fb_test_*` database per test. The
compose stack grants the local `freeboard` user the rights to create and drop
those databases (see `tests/Freeboard.TestInfrastructure/docker/mysql-init/`); this
is a local dev/test convenience only and is not how a runtime user should be
provisioned.

## Read-only (GitOps) mode

The web app reads two config keys:

- `Freeboard:GitOps:ReadOnly` (bool, default `false`) - when `true`, the app is
  read-only.
- `Freeboard:GitOps:RepositoryUrl` (string, optional) - the git repo URL surfaced
  to callers; omitted when empty.

When read-only is on, mutating HTTP requests (POST, PUT, PATCH, DELETE) are
rejected with `409 Conflict` and an RFC 7807 `application/problem+json` body that
states the instance is GitOps-managed and changes must be made in git. When the
repository URL is set, it is included in the body. GET, HEAD, and OPTIONS pass
through. Enforcement is server-side, not merely disabled UI controls.

`GET /api/gitops/status` reports whether GitOps mode is on, and includes the
repository URL when set, so a client can show a read-only banner.

## Secrets are never in git

The schema has no field that holds a secret (token, key, or password), by design.
Credentials needed by future integrations will be referenced by a named
credential resolved out-of-band, never inlined in git-tracked config. Never put
secret material in these files.
