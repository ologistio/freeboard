# GitOps config management

Freeboard manages compliance state as declarative YAML in git, FleetDM-style. The
git files are the source of truth: standards, the controls under them, and the
scopes those controls apply to. A CLI validates and previews the config, and the
web app can run read-only so changes flow through git rather than the UI.

This is the first increment. It ships the config format, `validate`, and
`apply --dry-run`, plus a web read-only mode. Real apply, persistence, and drift
detection are not built yet.

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

`--dry-run` is required in this version. Running `apply` without `--dry-run`
exits `2` and prints that real apply lands in a later increment, because there is
no backing store yet. The commands make no network calls.

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
