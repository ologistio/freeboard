## Why

Freeboard is a code-driven compliance system. Compliance evidence is only
trustworthy when its source of truth is auditable and reviewable. Today there is
no declarative, version-controlled description of an organisation's compliance
state, and no way to make the (future) web UI defer to git. This change starts
the FleetDM-style GitOps model: compliance configuration lives in git as plain
YAML, a CLI validates and applies it, and the UI can be told it is no longer the
source of truth. We start with the smallest useful slice so the trajectory is
proven before more is built.

## What Changes

- Define a declarative YAML config format that describes compliance state:
  standards in scope, the controls under each standard, and the scopes
  (org units or asset groups) those controls apply to. Each resource has a
  stable, immutable `id` used as identity, plus a mutable `title` for display.
- Add a parse-and-validate library in `Freeboard.Core` that loads one or more
  YAML files into a typed config model and reports structured errors as data
  (the loader and validator never throw on bad input and never print).
- Add a CLI command group `freeboard gitops` with `validate` and
  `apply --dry-run` subcommands. In this first increment, `apply` is dry-run
  only: it validates and reports the resulting config; it does not persist to a
  backing store (there is no store yet) and makes no network calls.
- Add a read-only mode signal for the web UI: a configuration flag
  (`Freeboard:GitOps:ReadOnly`) that, when set, makes the web app advertise
  GitOps mode and reject mutating HTTP requests with `409 Conflict` and an
  RFC 7807 problem-details body (`application/problem+json`), pointing the caller
  at the git repo. An optional `Freeboard:GitOps:RepositoryUrl` config key
  supplies that URL. Enforcement is server-side at the HTTP layer, not just
  disabled buttons.
- Add a `GET /api/gitops/status` endpoint that reports whether GitOps mode is on
  so a future banner can read it.
- Add architecture tests asserting the EE one-way rule: `Freeboard.Core`,
  `Freeboard.CLI`, and `Freeboard.Agent` do not reference `Freeboard.Enterprise`.
- Add an example config directory, `docs/gitops.md`, and a README section so
  users can see the intended shape.

No breaking changes; all behaviour is new. There is no public API to break yet.

## Capabilities

### New Capabilities

- `gitops-config-format`: the declarative YAML schema and typed config model for
  compliance standards, controls, and scopes (stable `id` identity, mutable
  `title`), plus its validation rules.
- `gitops-cli`: the `freeboard gitops validate` and `freeboard gitops apply
  --dry-run` commands that load, validate, and report the config from a
  directory of YAML files, with no network calls.
- `gitops-readonly-ui`: the web UI read-only (GitOps) mode that advertises
  GitOps as the source of truth, rejects mutating requests with 409 + RFC 7807,
  and exposes a status endpoint.

### Modified Capabilities

None. There are no existing specs in `openspec/specs/`.

## Impact

- Code:
  - `Freeboard.Core`: new config model, YAML loader, and validator (MIT).
  - `Freeboard.CLI`: new `gitops` command group (MIT).
  - `Freeboard` (web app): new read-only mode middleware, status endpoint, and
    config flag (MIT).
  - Test projects: Core/CLI/web tests plus an architecture test enforcing the EE
    one-way rule.
- Dependencies: one new dependency, a YAML parser (YamlDotNet) added to
  `Freeboard.Core`. Justified in design.md.
- Reference graph: unchanged. `Freeboard.CLI` references only `Freeboard.Core`.
  No new reference to `Freeboard.Enterprise` from any community component, and an
  architecture test pins this.
- Licensing: MIT. See design.md for the EE-vs-MIT decision and rationale.
- Docs: example config under `examples/gitops/`, `docs/gitops.md`, and a README
  section.

## Licensing

This change is MIT (the repo default), placed in `Freeboard.Core`,
`Freeboard.CLI`, and the `Freeboard` web app. GitOps is core platform plumbing
that the community edition needs to be useful, and the Agent and CLI must remain
EE-free and cross-platform. No part of this change is a paid, enterprise-gated
feature, so nothing belongs in `Freeboard.Enterprise`. Trajectory: a future EE
standards pack or drift-remediation feature may consume the Core config model,
but only one-way (`Freeboard.Enterprise` -> `Freeboard.Core`); EE code never
flows back into Core/CLI/Agent.

## Non-goals

This increment is deliberately minimal. The following are named here and in
design.md as trajectory only, and are NOT implemented in this change:

- No backing store or persistence. `apply` is dry-run only; it does not write
  compliance state anywhere yet.
- No authenticated (bearer-token) apply endpoint and no real, server-writing
  apply. These land with the persisted store in a later increment.
- No soft-delete/disable execution for GitOps-owned resources. (When real apply
  lands, deletes should prefer soft-delete/disable over hard delete; recorded as
  a forward principle, not built now.)
- No executable `Check` or evidence kinds. Those need an execution engine that
  does not exist yet; the schema stays to `Standard`, `Control`, `Scope`.
- No `lib/` includes and no `path`/`paths` globs. Deferred until the validator is
  proven, because directory globbing introduces path-traversal risk.
- No JSON Schema generation from the model.
- No bidirectional sync or export of live state back to YAML.
- No drift detection or reconciliation loop.
- No GitHub Actions or CI wiring in this change.
- No RBAC for the read-only mode.
- No web UI page rendering; read-only mode only gates mutating requests at the
  HTTP layer and exposes a status endpoint. Building compliance UI pages is out
  of scope.
- No agent involvement. The Agent does not change (it is covered only by the
  EE-rule architecture test).
- No secret scanner. Secrets-not-in-git is enforced structurally: the schema has
  no fields that hold secrets, and the rule is documented. A scanner is not
  built this increment.
