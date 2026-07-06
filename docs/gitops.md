# GitOps config management

Freeboard manages compliance state as declarative YAML in git, FleetDM-style. The
git files are the source of truth: standards, the requirements each standard
publishes, the controls that satisfy those requirements, the organisations being
assessed, and the scopes that map an organisation to a standard. A CLI validates
and previews the config, and the web app can run read-only so changes flow through
git rather than the UI.

It ships the config format, `validate`, and `apply --dry-run`, plus a web
read-only mode. A MySQL persistence layer now backs the compliance domain:
`gitops sync` imports a validated config into the store, `system migrate` applies
the schema, and the web app serves the persisted domain read-only. Real
reconciling apply, soft-delete on removal, and drift detection are not built yet.

## Fleet noun mapping

Freeboard borrows Fleet's structure but renames the nouns for compliance:

| Fleet    | Freeboard          | Meaning                                                   |
| -------- | ------------------ | --------------------------------------------------------- |
| (n/a)    | organisations      | the tree of entities being assessed (company/department)  |
| labels   | scopes             | maps an organisation to a standard with a disposition     |
| (n/a)    | requirement-scopes | maps an organisation to a requirement with a disposition  |
| policies | checks             | executable conformance checks (deferred, not built)       |
| (n/a)    | controls           | an implemented control mapped to one or more requirements |
| (n/a)    | requirements       | a standard's published normative statements               |
| (n/a)    | standards          | a compliance standard in scope                            |

This increment ships `standards`, `requirements`, `controls`, `organisations`,
`scopes`, and `requirement-scopes`. `checks` are named for the trajectory but are
not built.

## Format

A config directory holds one or more `.yaml` files. Each file is a stream of one
or more documents separated by `---`. Every document declares:

- `apiVersion` - must be exactly `freeboard.dev/v1alpha1`.
- `kind` - one of `Standard`, `Requirement`, `Control`, `Organisation`, `Scope`,
  or `RequirementScope`.

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

### Organisation

An organisation is a node in a tree. `type` is `Company` or `Department`.
`parent` is another `Organisation` id (omit it for a root). The document
discriminator `kind` names `Organisation`; the Company/Department distinction is
authored under `type` so the two do not collide. It persists and reads back as
the organisation's `kind`.

```yaml
apiVersion: freeboard.dev/v1alpha1
kind: Organisation
id: ologist-products
title: Ologist Products Ltd
type: Company
---
apiVersion: freeboard.dev/v1alpha1
kind: Organisation
id: ologist-products-eng
title: Engineering
type: Department
parent: ologist-products
```

### Scope

A scope maps one `Organisation` to one `Standard` with a `disposition` (`In` or
`Out`). At most one scope may exist per `(organisation, standard)` pair.

```yaml
apiVersion: freeboard.dev/v1alpha1
kind: Scope
id: scope-products-ce
title: Ologist Products - Cyber Essentials
organisation: ologist-products
standard: std-cyber-essentials
disposition: In
```

Dispositions are sparse: a node with no scope for a standard inherits its nearest
ancestor's disposition. A node with no such ancestor is undetermined. The
Statement of Applicability (below) resolves this per node.

### RequirementScope

A requirement-scope maps one `Organisation` to one `Requirement` with a
`disposition` (`In` or `Out`). At most one requirement-scope may exist per
`(organisation, requirement)` pair. There is no `standard` field: the requirement
fixes the standard.

```yaml
apiVersion: freeboard.dev/v1alpha1
kind: RequirementScope
id: rs-products-firewalls-01-out
title: Exclude firewall-on-every-device company-wide
organisation: ologist-products
requirement: req-ce-plus-firewalls-01
disposition: Out
```

Requirement-scopes sit under the standard-level scope. For a node and a
requirement (owned by standard S): if the node's disposition for S resolves `Out`
or `Undetermined`, the requirement follows the standard and requirement-scopes are
not consulted; only where S resolves `In` does the requirement layer apply. Within
an `In` standard, requirement-scopes inherit by the same nearest-ancestor rule as
scopes, and a child re-includes (`In`) a requirement an ancestor excluded (`Out`).
A requirement-level `In` cannot re-include a requirement whose standard is `Out`.

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
- an `Organisation.parent` names an `Organisation` id that does not exist;
- the organisations form a cycle through `parent`;
- an `Organisation`'s kind is not `Company` or `Department`;
- a `Scope.organisation` or `Scope.standard` names an id that does not exist;
- a `Scope.disposition` is not `In` or `Out`;
- two scopes name the same `(organisation, standard)` pair;
- a `RequirementScope.organisation` or `RequirementScope.requirement` names an id
  that does not exist;
- a `RequirementScope.disposition` is not `In` or `Out`;
- two requirement-scopes name the same `(organisation, requirement)` pair;
- `apiVersion` is not exactly `freeboard.dev/v1alpha1`.

A missing or unknown `kind`, and malformed YAML, are reported as diagnostics by
the loader rather than throwing.

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

The compliance domain (standards, requirements, controls, organisations, scopes) is persisted
in MySQL. The data is the general compliance store; GitOps `sync` is one writer
into it.

### Schema

Six domain tables (`standards`, `requirements`, `controls`, `organisations`,
`scopes`, `requirement_scopes`), each keyed on `id` with `api_version`, `title`,
`created_at`, and `updated_at`. `standards` also carries nullable `version`,
`authority`, `publisher`, and `source_url` metadata columns. `requirements` has a
`standard_id` foreign key (`ON DELETE RESTRICT`), a `theme`, a `statement`,
nullable `guidance`, and `citation_label`/`citation_url`. `organisations` has a
nullable self-referential `parent_id` foreign key and a `kind` column. `scopes`
has `organisation_id` and `standard_id` foreign keys, a `disposition` column, and
a unique key on `(organisation_id, standard_id)`. `requirement_scopes` has
`organisation_id` and `requirement_id` foreign keys (both `ON DELETE RESTRICT`, no
`standard_id`: the standard is derived from the requirement), a `disposition`
column, and a unique key on `(organisation_id, requirement_id)`. One relation table
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
- `GET /api/v1/freeboard/scopes` - persisted scopes (`id`, `title`,
  `organisation`, `standard`, `disposition`).
- `GET /api/v1/freeboard/requirement-scopes` - persisted requirement-scopes
  (`id`, `title`, `organisation`, `requirement`, `disposition`).
- `GET /api/v1/freeboard/statement-of-applicability/{standardId}` - the SoA
  projection for a standard: every organisation node with its resolved
  `disposition` and whether that value is `Explicit`, `Inherited`, or
  `Undetermined`, plus a `requirements` list of the per-requirement deviations for
  nodes whose standard resolves `In` (each with its `requirement`, resolved
  `disposition`, and `Explicit`/`Inherited` resolution; a requirement not listed
  follows the node's standard disposition). Nodes resolving `Out`/`Undetermined`
  carry an empty `requirements` list.
- `GET /api/v1/freeboard/compliance/status` - a `persisted` object of per-kind
  counts.

Resources are ordered by `id`; relation arrays are ordered by id. When the store
is unreachable, the read endpoints return HTTP 503 with an RFC 7807 problem body,
and `/api/v1/freeboard/compliance/status` returns HTTP 200 with
`{ "persisted": { "standards": null, "controls": null, "requirements": null, "organisations": null, "scopes": null, "requirementScopes": null } }`
(`null` marks the count as unknown, not zero). `GET /api/v1/freeboard/gitops/status`
is unchanged and does not depend on the store.

### App-managed writes

When the instance is NOT in GitOps read-only mode, organisations, scope
dispositions, and requirement-scope dispositions can be written through the API,
enforcing the same invariants as import:

- `PUT /api/v1/freeboard/organisations/{id}` - create or update an organisation.
- `DELETE /api/v1/freeboard/organisations/{id}` - delete an organisation (fails if
  it still has children, scopes, or requirement-scopes).
- `PUT /api/v1/freeboard/scopes/{id}` - set a scope disposition for an
  `(organisation, standard)` pair.
- `DELETE /api/v1/freeboard/scopes/{id}` - delete a scope disposition.
- `PUT /api/v1/freeboard/requirement-scopes/{id}` - set a requirement-scope
  disposition for an `(organisation, requirement)` pair.
- `DELETE /api/v1/freeboard/requirement-scopes/{id}` - delete a requirement-scope
  disposition.

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
