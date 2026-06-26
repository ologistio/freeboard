## Context

Freeboard is an early-stage .NET 10 solution. The projects are scaffold only:
`Freeboard.Core` holds a single constants class, `Freeboard.CLI` is a one-line
ConsoleAppFramework sample, and the `Freeboard` web app is a single
`MapGet("/")`. There is no data model, no persistence, and no UI yet. This is
the first functional feature, so the design must fit the bare current state, not
an imagined mature system.

The goal is to start FleetDM-style GitOps: declarative config in git, a CLI that
validates and applies it, and a UI that can defer to git as the source of truth.
FleetDM does this with YAML files, a `fleetctl gitops` apply command wired into
CI, and a "GitOps mode" that puts the UI in read-only and rejects config changes
via the API. We map that onto compliance: the git-managed files describe
compliance standards, controls, and scopes rather than MDM device queries.

### Fleet noun mapping

We borrow Fleet's structure but rename its nouns to compliance terms:

| Fleet    | Freeboard | Meaning                                             |
| -------- | --------- | --------------------------------------------------- |
| labels   | scopes    | org units or asset groups that controls apply to    |
| policies | checks    | executable conformance checks (DEFERRED, not built) |
| (n/a)    | controls  | a requirement under a standard                      |
| (n/a)    | standards | a compliance standard in scope                      |

This increment ships `standards`, `controls`, and `scopes` only. `checks` are
named here for the trajectory but are not built (see Non-Goals); they need an
execution engine that does not exist.

Constraints from the repo: MIT default with a single EE carve-out in
`Freeboard.Enterprise`; one-way dependency (Core/Agent/CLI must never reference
Enterprise); Agent and CLI must stay cross-platform; Conventional Commits; ASCII
punctuation; markdownlint; code-as-liability.

## Plan synthesis

This change merges two independently produced plans (Plan A, the Planner's
OpenSpec artifacts; Plan B, Codex's independent plan) under a mediator decision.

What each plan contributed:

- Shared foundation (both A and B): YAML config via YamlDotNet in
  `Freeboard.Core` (MIT); versioned `apiVersion`/`kind` documents; a CLI command
  group; server-side read-only enforcement; the EE one-way rule preserved;
  architecture tests.
- From Plan A (kept as the spine): the OpenSpec change structure, the three
  capabilities (`gitops-config-format`, `gitops-cli`, `gitops-readonly-ui`), the
  validator-returns-data design, `apply --dry-run`-only, and the
  `Freeboard:GitOps:ReadOnly` middleware that already short-circuits mutating
  verbs with 409 before any handler runs.
- From Plan B (banked correctness, folded into A):
  - H4 stable IDs as identity: every resource has an immutable `id`; display
    text is a mutable `title`; matching and cross-references use `id`, never the
    display name. (Plan A originally used `name` for both roles.)
  - Fleet noun mapping made explicit (table above).
  - Cross-reference naming: a `Control` maps to its standards via `maps_to`, a
    list of Standard ids (Plan A had a singular `standard`); a `Scope` lists
    `controls` by id.
  - H1 read-only enforced server-side, not just disabled buttons - confirmed
    Plan A already does this; we add the RFC 7807 problem-details body and the
    `GET /api/gitops/status` path.
  - M2 secrets must never live in git: enforced structurally (schema has no
    secret fields) and documented as a rule; no scanner built.
  - M1 prefer soft-delete/disable over hard delete for GitOps-owned resources:
    recorded as a forward principle for when real apply lands.
  - EE architecture tests covering Core, CLI, and Agent.

Scope divergence and how the mediator resolved it:

- Plan B pushed for a full vertical slice in increment 1: a persisted GitOps
  state store, a bearer-token authenticated apply endpoint that writes server
  state, and a write guard.
- The mediator chose "minimal first increment + bank correctness." The full
  vertical slice is DEFERRED. Increment 1 ships the Core model + validator, the
  CLI `validate` and dry-run-only `apply`, and the web read-only flag +
  middleware + status endpoint. The correctness points above are banked now
  because they are cheap to get right early and expensive to retrofit (stable
  ids especially). Persisted store, authenticated/real apply, soft-delete,
  `lib/` includes, path globs, JSON Schema, export, CI wiring, drift, and
  `checks`/evidence are all named as trajectory only.

Final unified approach: Plan A's artifacts are the canonical structure; Plan B's
correctness items are merged into the schema, specs, and tasks; the scope is held
to the mediator's minimal increment.

## Goals / Non-Goals

**Goals:**

- A declarative YAML format for compliance state (standards, controls, scopes)
  with stable `id` identity and mutable `title` display text.
- A parse-and-validate library in `Freeboard.Core`, usable by any component,
  that returns all diagnostics as data and never throws or prints.
- `freeboard gitops validate` and `freeboard gitops apply --dry-run` in the CLI,
  with no network calls.
- A web read-only mode flag that rejects mutating requests with 409 + RFC 7807
  and is discoverable via `GET /api/gitops/status`.
- Architecture tests pinning the EE one-way rule.
- The smallest coherent increment that proves the trajectory.

**Non-Goals:**

- Persistence, authenticated/real apply, soft-delete execution, bidirectional
  sync, drift detection, CI wiring, RBAC, UI page rendering, `lib/` includes,
  path globs, JSON Schema generation, and executable `checks`/evidence. See
  proposal Non-goals. `apply` does not write any state.

## Decisions

### Config model and validator live in Freeboard.Core (MIT)

The schema is the contract every component shares: the CLI loads it, the web app
will later read it, and tests assert on it. Putting it in `Freeboard.Core` keeps
it MIT, EE-free, and reusable without duplication. GitOps is community plumbing,
not a paid feature, so none of it goes in `Freeboard.Enterprise`. A future EE
standards pack may consume this model one-way (`Enterprise -> Core`).
Alternative considered: define the model in `Freeboard.CLI`. Rejected because the
web app would then need to reference the CLI or duplicate the model.

### Stable immutable `id` is identity; `title` is mutable display

Every resource (Standard, Control, Scope) has an `id` that is its permanent
identity and a `title` that is human-facing and may change. All cross-references
and duplicate detection key off `id`; nothing matches on `title`. This is banked
now because renaming a display string must never re-create or orphan a resource
once persistence exists, and retrofitting identity later is a breaking migration.
Alternative considered (Plan A's original): a single `name` field used for both
display and matching. Rejected: it conflates identity with presentation.

### YAML via YamlDotNet (one new dependency in Core)

YAML matches the FleetDM model and is the expected format for hand-edited,
git-reviewed config. .NET has no first-party YAML parser. YamlDotNet is the
de-facto standard, MIT-licensed, cross-platform, and dependency-light.
Liability justification: writing a YAML parser by hand is far more code and risk
than taking one well-maintained MIT dependency. Alternative considered: JSON
with System.Text.Json (no new dep). Rejected because JSON is poor for
hand-edited, multi-document, comment-friendly config and diverges from the
FleetDM model the user asked to copy.

### Kubernetes-style apiVersion/kind documents

Each YAML document carries `apiVersion` and `kind` (`Standard`, `Control`,
`Scope`). This is a familiar, extensible GitOps shape: new kinds (e.g. `Check`)
can be added later without restructuring existing files, and validation keys off
`kind`. Unknown fields are rejected so typos surface instead of being silently
dropped. Alternative considered: a single monolithic config object per file.
Rejected because it scales poorly and is harder to split across files for review.

The only valid `apiVersion` for this increment is `freeboard.io/v1alpha1`. The
loader/validator accept exactly this string; any other value (including a missing
`apiVersion`) is a validation error. The example configs and the Core tests use
this literal value, and a validator test asserts that an unknown `apiVersion`
still fails.

### Loader: multi-document dispatch by kind

The loader handles each `.yaml` file as a YAML stream of one or more documents
(split by `---`). For each document it first parses to a mapping node and reads
`apiVersion` and `kind` as plain strings, then deserializes the document into the
record type selected by `kind` (`Standard`, `Control`, `Scope`). A document whose
`kind` is missing or not one of the known kinds is reported by the loader as a
diagnostic, and that document is not deserialized further; the validator does not
re-check `kind`, so kind-routing is owned only by the loader (no double-report).
`apiVersion` value correctness is owned by the validator (it has the full list of
known versions); the loader only needs `kind` to choose a type. The loader keeps
the never-throw contract: a document it cannot route still produces a diagnostic,
not an exception.

### Unknown-field rejection and snake_case binding

YamlDotNet ignores YAML keys that do not match a target property by default, so
unknown fields would be silently dropped. To make typos surface, the loader
parses each document to a mapping node and diffs its keys against the known
schema keys for that `kind`; any key not in the schema is emitted as an
unknown-field diagnostic naming the document and the offending key. Property
binding uses a snake_case naming convention for domain/property fields only, so
YAML `maps_to` binds to the `MapsTo` record property (and likewise `controls`,
etc.). The schema keys `apiVersion` and `kind` are exceptions kept in camelCase
to match the Kubernetes-style convention; snake_case binding does not apply to
them, so `apiVersion` is the valid key (not `api_version`). This
key-diff approach is chosen over a strict deserializer because it lets the loader
collect every unknown key as data while keeping the never-throw contract, rather
than aborting on the first `YamlException`.

### Loader never throws: parse errors become diagnostics

All YamlDotNet parse calls (stream load and per-document deserialize) are wrapped
so that a thrown parse exception (`YamlException`, `SemanticErrorException`, or a
subtype) is caught and converted into a structured diagnostic that includes the
file path and, where the exception exposes it, the line/column of the fault. The
loader never rethrows parse exceptions; a malformed file yields a result with
diagnostics. A Core test feeds malformed YAML and asserts a diagnostic is
returned rather than an exception thrown.

### Validator returns all errors as data, CLI decides exit code

`Freeboard.Core` exposes a load+validate function returning a result that holds
the typed model and a list of structured diagnostics. It never throws for
validation problems and never writes output. The CLI prints and sets the exit
code. This keeps Core pure and testable and lets the web app reuse the same
validation later. Validation enforces: required fields present; `id` immutable
and unique within its kind; unknown fields rejected; every `Control.maps_to`
entry resolves to a known `Standard` id (`maps_to` is a list of Standard ids);
each `Scope.controls` entry resolves to a known `Control` id; `apiVersion` equals
`freeboard.io/v1alpha1`. The validator does not re-check `kind`; kind-routing
diagnostics (missing/unknown `kind`) are owned solely by the loader. The
aggregate load+validate result still fails on an unknown or missing `kind`, but
that diagnostic is emitted by the loader only. All errors are
collected, not just the first. Alternative considered: throw on first error. Rejected because users want
all problems in one run, and throwing couples Core to presentation.

### Read-only mode is HTTP middleware gated by config, with RFC 7807 body

The web app reads `Freeboard:GitOps:ReadOnly`. When true, middleware short-
circuits POST/PUT/PATCH/DELETE with `409 Conflict` and an RFC 7807 problem-
details body before any handler runs. Enforcement is server-side; disabling UI
buttons is not sufficient and is not the mechanism. `GET /api/gitops/status`
reports the mode so a future banner can read it. This is the smallest mechanism
that enforces read-only without any UI pages existing yet. Alternative
considered: per-endpoint checks. Rejected as more code and easy to forget on new
endpoints; one middleware covers all mutations by default.

A second config key, `Freeboard:GitOps:RepositoryUrl` (string, optional, default
empty), holds the git repo URL. When set, the 409 problem-details body includes
it (pointing the caller at where to make changes), and `GET /api/gitops/status`
returns it too. When empty, the URL is omitted from both.

The 409 response sets `Content-Type: application/problem+json` and a body with at
least the RFC 7807 fields `type`, `title`, `status`, and `detail` (`status` is
`409`; `detail` states the instance is GitOps-managed and changes must be made in
git). When `RepositoryUrl` is set, the repo URL is included (the `detail` text
references it, and it is also surfaced as a member of the body).

409 Conflict is chosen over 403 or 405. The request is well-formed and the method
is supported in general; the rejection is a state conflict - git is the source of
truth, so the server's current state does not permit the mutation. That is a
conflict, not an authorization failure (403) or an unsupported-method condition
(405).

### apply --dry-run only, no store

There is no backing store, so `apply` cannot persist. Rather than build a store
now (large liability, out of the minimal-increment steer), `apply` requires
`--dry-run` and prints the planned state. Invoking `apply` without `--dry-run`
exits non-zero with a clear message that real apply lands in a later increment.
The command exists so the trajectory and UX are established; persistence,
authentication, and write guards land in a later change. Alternative: omit
`apply`. Rejected because naming the command now sets the contract and makes the
next increment a fill-in rather than a redesign.

Exit codes are pinned: `0` = success/valid (validate passed, or dry-run printed
planned state); `1` = validation or input error, including a missing or
nonexistent path; `2` = unsupported real apply (running `apply` without
`--dry-run`). Errors go to stderr; success summaries and planned state go to
stdout.

### CLI binary name is `freeboard`

The docs and specs invoke `freeboard gitops ...`, so the built CLI binary must be
named `freeboard`. `Freeboard.CLI.csproj` sets `<AssemblyName>freeboard</AssemblyName>`
so the produced executable matches the documented invocation.

### Secrets never live in git (documented, not scanned)

The schema deliberately has no fields that hold secrets (tokens, keys,
passwords). When integrations later need credentials, config will reference a
named credential resolved out-of-band, never inline secret material. This
increment enforces the rule structurally (no secret-bearing fields) and
documents it; it does not build a secret scanner. Alternative considered: build
a scanner now. Rejected as premature liability with nothing yet to scan for.

### Deterministic load order

The loader sorts the discovered `.yaml` files by their normalized relative path
(relative to the config directory, forward-slash separators) using
`StringComparer.Ordinal`, then loads documents within each file in their in-file
order. This gives a stable, platform-independent ordering of the config model and
the diagnostic list. The order test (task 2.5) asserts the loaded order against a
known multi-file fixture, not merely that two runs agree with each other.

### No-network verification is structural, not runtime

The "validate makes no network call" guarantee is verified structurally, not by
trying to observe a live connection. A test asserts that the gitops CLI code path
references no HTTP or socket APIs - no use of `System.Net.Http` or
`System.Net.Sockets` types on the load/validate path. This is deterministic and
does not depend on network state. Alternative considered: detect a live outbound
connection at runtime. Rejected as flaky and unable to prove the negative.

### EE one-way rule is pinned by architecture tests

Tests assert that `Freeboard.Core`, `Freeboard.CLI`, and `Freeboard.Agent` carry
no reference to `Freeboard.Enterprise`. This converts a written rule into a build
check so an accidental reference fails CI. Alternative considered: rely on review
only. Rejected: the rule is licensing-critical and cheap to automate.

## File changes

New, all small:

- `src/Freeboard.Core/GitOps/` config model records (Standard, Control, Scope,
  Config) using `id`/`title` (with `Control.maps_to` a list of Standard ids), a
  loader (`ConfigLoader`), a validator (`ConfigValidator`), and a
  result/diagnostic type. YamlDotNet PackageReference added to
  `Freeboard.Core.csproj`.
- `src/Freeboard.CLI/Program.cs` gains a `gitops` command group with `validate`
  and `apply` subcommands calling Core. No network calls.
  `Freeboard.CLI.csproj` sets `<AssemblyName>freeboard</AssemblyName>`.
- `src/Freeboard/` gains a read-only middleware, the config flag wiring in
  `Program.cs` (`Freeboard:GitOps:ReadOnly` and
  `Freeboard:GitOps:RepositoryUrl`), the RFC 7807 problem-details body
  (`application/problem+json`, including the repo URL when set), and a
  `GET /api/gitops/status` endpoint (returning the mode and the repo URL when
  set). The web app `Program` is made testable for `WebApplicationFactory` by
  exposing it as a `public partial class Program` (or `InternalsVisibleTo` the
  web test project).
- `examples/gitops/` sample `standards.yaml`, `controls.yaml`, `scopes.yaml`
  using placeholder ids (not a conformance claim), plus a short README.
- `docs/gitops.md` describing the format, the noun mapping, the commands, and the
  secrets-not-in-git rule; a README section linking to it.
- Test projects under `tests/`: Core (parse/validate/diagnostics/stable-id,
  malformed YAML returns a diagnostic, deterministic order against a fixture), CLI
  (exit codes, dry-run output, no-network structural check), web (middleware 409
  on mutating verb when read-only, status endpoint, mutation not intercepted when
  off), and an architecture test (EE rule for Core/CLI/Agent). The web test
  project references the `Microsoft.AspNetCore.Mvc.Testing` package for
  `WebApplicationFactory`. Justified: validation correctness and the licensing
  rule are the core value and must be covered; these are the first test projects,
  added once here.

## Risks / Trade-offs

- New dependency (YamlDotNet) increases supply-chain surface. -> Mitigation:
  single, widely-used MIT library, pinned version, added only to Core.
- `apply --dry-run`-only may confuse users expecting real apply. -> Mitigation:
  the command prints clearly that only dry-run is supported and that real apply
  lands in a later increment.
- Read-only middleware could block legitimate non-config mutations once real
  endpoints exist. -> Mitigation: in this increment there are no such endpoints;
  the scope of what counts as a "config mutation" is revisited when endpoints are
  added. Documented as an open question.
- Schema may not yet match real compliance standards (Cyber Essentials Plus,
  SOC 2, ISO 27001, ISO 20000). -> Mitigation: the format is deliberately
  minimal and extensible via `kind`; the example uses placeholder ids, not a
  claim of conformance.

## Migration Plan

Greenfield feature; nothing to migrate. Rollback is removing the new files,
the YamlDotNet reference, and the read-only flag wiring. The flag defaults to
false, so deploying the web change is inert until an operator opts in.

## Open Questions

- "ISO20001" in the request is ambiguous. Treated as ISO/IEC 20000 (IT service
  management); it may be a typo for ISO 27001. The schema is standard-agnostic,
  so this does not block the change. Example config uses placeholder ids and is
  not a conformance claim; the reviewer should confirm the intended standard
  before any real standards data is added.
- Confirm MIT placement: this design puts all GitOps code in Core/CLI/web as
  MIT. Reviewer to confirm no part should be EE-gated now.
- Future: which mutations the read-only middleware should and should not block
  once real endpoints exist (e.g. login, audit log writes).
- Future: where applied compliance state is persisted, how the authenticated
  apply endpoint is secured, whether deletes are soft-delete/disable, and the
  export-to-YAML direction for bidirectional sync.
