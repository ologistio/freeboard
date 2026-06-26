# GitOps config management

Freeboard manages compliance state as declarative YAML in git, FleetDM-style. The
git files are the source of truth: standards, the controls under them, and the
scopes those controls apply to. A CLI validates and previews the config, and the
web app can run read-only so changes flow through git rather than the UI.

It ships the config format, `validate`, and `apply --dry-run`, plus a web
read-only mode. A MySQL persistence layer now backs the compliance domain:
`gitops sync` imports a validated config into the store, `system migrate` applies
the schema, and the web app serves the persisted domain read-only. Real
reconciling apply, soft-delete on removal, and drift detection are not built yet.

## Fleet noun mapping

Freeboard borrows Fleet's structure but renames the nouns for compliance:

| Fleet    | Freeboard | Meaning                                              |
| -------- | --------- | ---------------------------------------------------- |
| labels   | scopes    | org units or asset groups that controls apply to     |
| policies | checks    | executable conformance checks (deferred, not built)  |
| (n/a)    | controls  | a requirement under a standard                       |
| (n/a)    | standards | a compliance standard in scope                       |

This increment ships `standards`, `controls`, and `scopes`. `checks` are named
for the trajectory but are not built.

## Format

A config directory holds one or more `.yaml` files. Each file is a stream of one
or more documents separated by `---`. Every document declares:

- `apiVersion` - must be exactly `freeboard.io/v1alpha1`.
- `kind` - one of `Standard`, `Control`, or `Scope`.

`apiVersion` and `kind` stay camelCase (Kubernetes-style). All other fields are
snake_case (so `maps_to`, not `mapsTo`). Unknown fields are rejected so typos
surface instead of being silently dropped.

Every resource has:

- `id` - a stable, immutable identity. References and duplicate detection key off
  `id`, never the title.
- `title` - human-facing display text that may change without changing identity.

### Standard

```yaml
apiVersion: freeboard.io/v1alpha1
kind: Standard
id: std-cyber-essentials
title: Cyber Essentials
```

### Control

`maps_to` is a list of `Standard` ids.

```yaml
apiVersion: freeboard.io/v1alpha1
kind: Control
id: ctrl-mfa
title: Multi-factor authentication enforced
maps_to:
  - std-cyber-essentials
  - std-soc2
```

### Scope

`controls` is a list of `Control` ids.

```yaml
apiVersion: freeboard.io/v1alpha1
kind: Scope
id: scope-corp-laptops
title: Corporate laptops
controls:
  - ctrl-mfa
```

## Validation

Validation collects every error in one pass (not just the first). It fails when:

- a required field is missing or empty;
- a document has an unknown field;
- an `id` is duplicated within its kind;
- a `Control.maps_to` entry names a `Standard` id that does not exist;
- a `Scope.controls` entry names a `Control` id that does not exist;
- `apiVersion` is not exactly `freeboard.io/v1alpha1`.

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

The compliance domain (standards, controls, scopes) is persisted in MySQL. The
data is the general compliance store; GitOps `sync` is one writer into it.

### Schema

Six tables. Three domain tables (`standards`, `controls`, `scopes`), each keyed on
`id` with `api_version`, `title`, `created_at`, and `updated_at`. Two relation
tables (`control_standards` for `Control.maps_to`, `scope_controls` for
`Scope.controls`) with composite primary keys and `ON DELETE CASCADE` foreign
keys. One migration-tracking table (`schema_migrations`) bootstrapped by the
migration runner.

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
config. Narrowing the config deletes rows (foreign keys cascade to the relation
tables). There is no soft-delete yet. Review the config before syncing.

### Web read endpoints

The web app serves the persisted domain read-only (GET only, not blocked by
read-only mode):

- `GET /api/standards` - persisted standards (`id`, `title`).
- `GET /api/controls` - persisted controls (`id`, `title`, `maps_to`).
- `GET /api/scopes` - persisted scopes (`id`, `title`, `controls`).
- `GET /api/compliance/status` - a `persisted` object of per-kind counts.

Resources are ordered by `id`; relation arrays are ordered by id. When the store
is unreachable, the read endpoints return HTTP 503 with an RFC 7807 problem body,
and `/api/compliance/status` returns HTTP 200 with
`{ "persisted": { "standards": null, "controls": null, "scopes": null } }`
(`null` marks the count as unknown, not zero). `GET /api/gitops/status` is
unchanged and does not depend on the store.

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
