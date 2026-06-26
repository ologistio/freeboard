## 1. Config model and validator in Core

Commit: `feat(core): add gitops compliance config model and validator`

- [x] 1.1 Add the YamlDotNet PackageReference to `src/Freeboard.Core/Freeboard.Core.csproj` (pinned version)
- [x] 1.2 Add config model records under `src/Freeboard.Core/GitOps/` (Standard, Control, Scope, and a Config aggregate). Each resource has an immutable `id` (identity) and a mutable `title` (display). `Control` has `maps_to` (a list of Standard ids). `Scope` has `controls` (a list of Control ids). Use snake_case property binding for domain/property fields so `maps_to`/`controls` bind; the schema keys `apiVersion` and `kind` stay camelCase (Kubernetes-style) and are not snake_case-bound
- [x] 1.3 Add `ConfigLoader` that reads all `.yaml` files in a directory in deterministic order (files sorted by normalized relative path with `StringComparer.Ordinal`, then in-file document order), parses each multi-document file by reading `apiVersion`/`kind` from a mapping node first, then deserializes each document into the record type chosen by `kind`. Missing/unknown `kind` is a loader diagnostic (loader owns kind-routing; validator does not re-check `kind`). The loader never throws and never prints
- [x] 1.4 Add a structured diagnostic type and a result type that carries the loaded config and the list of diagnostics (errors as data)
- [x] 1.5 Reject unknown fields: parse each document to a mapping node and diff its keys against the known schema keys for its `kind`; emit an unknown-field diagnostic naming the document and key. Do not rely on YamlDotNet (it drops unmatched keys by default)
- [x] 1.6 Wrap all YamlDotNet parse calls so thrown parse exceptions (`YamlException`/`SemanticErrorException`, with file and line/column where available) are converted to diagnostics and never rethrown (keeps the never-throw contract)
- [x] 1.7 Add `ConfigValidator` enforcing: required fields present; unknown fields rejected; `id` unique per kind (identity is `id`, never `title`); every `Control.maps_to` entry resolves to a known Standard id; each `Scope.controls` entry resolves to a known Control id; `apiVersion` equals `freeboard.io/v1alpha1`; collect all errors. The validator checks the `apiVersion` value only; it does not re-check `kind` (the loader reports unknown/missing `kind`). An unknown `apiVersion` still fails

## 2. Tests for loader and validator

Commit: `test(core): cover gitops config loading and validation`

- [x] 2.1 Add a test project (e.g. `tests/Freeboard.Core.Tests`) referencing `Freeboard.Core`; add it to `Freeboard.slnx`
- [x] 2.2 Test: valid config loads into the model with correct counts; `id` and `title` are distinct fields
- [x] 2.3 Test: multi-document file parses all documents
- [x] 2.4 Test: missing required field, unknown field, duplicate id, unknown `apiVersion`, missing/unknown `kind`, malformed YAML, and dangling `maps_to`/`controls` references each return a named diagnostic (malformed YAML returns a diagnostic, not an exception)
- [x] 2.5 Test: changing `title` does not change identity (matching is by `id`); all errors reported (not just the first); loading order matches a known multi-file fixture (files by normalized relative path ordinal, then in-file order), not just run-to-run equality

## 3. CLI gitops commands

Commit: `feat(cli): add gitops validate and apply --dry-run`

- [x] 3.1 Add a `gitops` command group in `src/Freeboard.CLI/Program.cs` with `validate <path>` and `apply <path> --dry-run`
- [x] 3.2 Set `<AssemblyName>freeboard</AssemblyName>` in `Freeboard.CLI.csproj` so the built binary matches the documented `freeboard gitops ...` invocation
- [x] 3.3 `validate`: load+validate via Core, print success summary to stdout or each error to stderr; exit `0` on pass, `1` on validation/input error (incl. missing path); no network calls
- [x] 3.4 `apply --dry-run`: load+validate, then print planned standards/controls/scopes to stdout; exit `0` on valid, `1` on validation/input error; reject `apply` without `--dry-run` with exit `2` and a stderr message that real apply lands in a later increment
- [x] 3.5 Confirm `Freeboard.CLI` references only `Freeboard.Core` (no `Freeboard.Enterprise`)

## 4. CLI tests

Commit: `test(cli): cover gitops validate and apply exit codes`

- [x] 4.1 Add a CLI test project (e.g. `tests/Freeboard.CLI.Tests`); add it to `Freeboard.slnx`
- [x] 4.2 Test: `validate` on a valid fixture exits `0` and prints counts to stdout; on an invalid fixture exits `1` and prints errors to stderr; on a missing path exits `1`
- [x] 4.3 Test: `apply --dry-run` exits `0` and prints planned state to stdout; `apply` without `--dry-run` exits `2` with the deferral message on stderr
- [x] 4.4 Test: structural/architecture test asserting the gitops load/validate code path references no HTTP/socket APIs (no `System.Net.Http` or `System.Net.Sockets` usage), rather than detecting a live connection

## 5. Web read-only (GitOps) mode

Commit: `feat(web): add gitops read-only mode middleware and status`

- [x] 5.1 Wire `Freeboard:GitOps:ReadOnly` (bool, default false) and `Freeboard:GitOps:RepositoryUrl` (string, optional, default empty) in `src/Freeboard/Program.cs`
- [x] 5.2 Add middleware that, when the flag is true, rejects POST/PUT/PATCH/DELETE with `409 Conflict`, `Content-Type: application/problem+json`, and an RFC 7807 body with at least `type`, `title`, `status` (`409`), `detail`; include the repo URL when `RepositoryUrl` is set; leave GET/HEAD/OPTIONS unaffected. Enforcement is server-side
- [x] 5.3 Add a `GET /api/gitops/status` endpoint that reports whether GitOps mode is on and includes the repo URL when `RepositoryUrl` is set
- [x] 5.4 Make the web app `Program` testable for `WebApplicationFactory`: expose it as `public partial class Program` (or add `InternalsVisibleTo` for the web test project)

## 6. Web tests

Commit: `test(web): cover gitops read-only middleware and status`

- [x] 6.1 Add a web test project (e.g. `tests/Freeboard.Web.Tests`) referencing `Microsoft.AspNetCore.Mvc.Testing` for `WebApplicationFactory`; add it to `Freeboard.slnx`
- [x] 6.2 Test: with read-only on, a mutating verb returns 409 with `Content-Type: application/problem+json` and a body containing `type`, `title`, `status` (`409`), `detail` (and the repo URL when `RepositoryUrl` is set); a GET is served normally
- [x] 6.3 Test: with read-only off (default), the middleware does not intercept a mutating request to a test-only POST endpoint (registered via the factory); assert the response is the downstream response and not the 409 GitOps problem-details response
- [x] 6.4 Test: `GET /api/gitops/status` reports the mode in both states, and includes the repo URL when `RepositoryUrl` is set

## 7. Architecture tests for the EE one-way rule

Commit: `test(arch): assert core, cli, agent do not reference enterprise`

- [x] 7.1 Add an architecture test project (e.g. `tests/Freeboard.Architecture.Tests`); add it to `Freeboard.slnx`
- [x] 7.2 Test: `Freeboard.Core`, `Freeboard.CLI`, and `Freeboard.Agent` have no reference (project or assembly) to `Freeboard.Enterprise`

## 8. Example config and docs

Commit: `docs(gitops): add example config layout and gitops guide`

- [x] 8.1 Add `examples/gitops/` with sample `standards.yaml`, `controls.yaml`, `scopes.yaml` using `apiVersion: freeboard.io/v1alpha1` and placeholder ids (not a conformance claim), with `id`/`title`, `maps_to` as a list (one entry is fine), and `controls` references
- [x] 8.2 Add a short `examples/gitops/README.md` describing the file layout and the `freeboard gitops` commands
- [x] 8.3 Add `docs/gitops.md` covering the format, the Fleet noun mapping, the commands, read-only mode, and the secrets-never-in-git rule
- [x] 8.4 Add a GitOps section to the root `README.md` linking to `docs/gitops.md`
- [x] 8.5 Ensure all new Markdown passes markdownlint

## 9. Verification

- [x] 9.1 `dotnet build` the solution; no warnings introduced, reference graph unchanged
- [x] 9.2 `dotnet test` passes (Core, CLI, web, architecture)
- [x] 9.3 Run `freeboard gitops validate examples/gitops` (exit 0) and a known-bad fixture (exit 1); run `apply examples/gitops --dry-run` (exit 0) and `apply examples/gitops` (exit 2)
- [x] 9.4 Start the web app with `Freeboard:GitOps:ReadOnly=true` and a `Freeboard:GitOps:RepositoryUrl`; confirm a POST returns 409 with `application/problem+json` (including the repo URL) and `GET /api/gitops/status` reports GitOps mode on with the repo URL; confirm default (flag unset) does not intercept POST. Covered by the web integration tests (`Freeboard.Web.Tests`) using `WebApplicationFactory` rather than a live server run
- [x] 9.5 `npx markdownlint-cli2 "**/*.md"` passes for new docs
