## Context

Freeboard's GitOps config is declarative YAML (Kubernetes-style `apiVersion` +
`kind`) loaded into a typed model in `Freeboard.Core`, validated to a diagnostic
list, and synced into per-kind MySQL tables by an importer in
`Freeboard.Persistence`. Existing kinds are `Standard`, `Requirement`, `Control`,
`Organisation`, `Scope`, and `RequirementScope`. The pipeline is pure and testable:
the loader and validator never throw and never write output; the importer replaces
the whole persisted set in one transaction.

Two existing kinds are the templates for this change:

- `Requirement` / `Organisation`: a plain identity+metadata record (`id`, `title`,
  plus fields). `Vendor` mirrors this shape.
- `RequirementScope`: binds one `Organisation` to one `Requirement` with a
  `disposition` (`In`/`Out`), unique per `(organisation, requirement)`. `VendorScope`
  mirrors its parse/validate/persist machinery, but binds a `Vendor` to a target
  and adds a `justification`.

Key files studied:

- `src/Freeboard.Core/GitOps/ConfigModel.cs` - `GitOpsSchema` kind constants,
  the `sealed record` per kind, `ScopeDisposition { In, Out }`, aggregate
  `GitOpsConfig`.
- `src/Freeboard.Core/GitOps/ConfigLoader.cs` - `SchemaKeys` allowed-key sets,
  `apiVersion` attribute overrides, the `switch (kind)` dispatch, the unknown-kind
  diagnostic message.
- `src/Freeboard.Core/GitOps/ConfigValidator.cs` - phase-ordered `Validate`,
  `ValidateRequirementScopes`, `CheckRequired`, `TryParseDisposition`.
- `src/Freeboard.Persistence/GitOps/{ImportPlan.cs,MySqlGitOpsImporter.cs,IGitOpsImporter.cs}`.
- `src/Freeboard.Persistence/{IComplianceStore.cs,MySqlComplianceStore.cs,ComplianceReadModels.cs}`.
- `src/Freeboard.Persistence/Migrations/009_requirement_scopes.sql` (DDL template);
  highest existing migration is `010_authorization.sql`.
- `src/Freeboard/Compliance/ComplianceEndpoints.cs` (Minimal API read endpoints),
  `src/Freeboard/Pages/Compliance/StatementOfApplicability.cshtml(.cs)` (SSR page
  injecting `IComplianceStore` in-process), `src/Freeboard/Program.cs` wiring.
- `src/Freeboard.CLI/{UserCommands.cs,IFreeboardApiClient.cs,HttpFreeboardApiClient.cs,ApiClientFactory.cs,Program.cs}`
  (HTTP-backed read command pattern).

## Goals / Non-Goals

**Goals:**

- `Vendor` and `VendorScope` parse, validate, and sync through the existing
  pipeline with no new machinery beyond one migration and the per-kind wiring.
- `VendorScope` with `disposition: Out` fails validation unless `justification` is
  non-empty.
- A read-only vendor register on web SSR and CLI, in one PR, that always shows each
  `Out` exception with its justification.

**Non-Goals:**

- Scoring/denominator (none exists yet; forward hook only).
- Org-tree inheritance for VendorScope (no `organisation` field in #46).
- App-managed vendor CRUD (GitOps-only authoring in V1).
- Vendor metadata beyond `id`/`title`; `Control` model changes.

## Decisions

### D1: Vendor and VendorScope are static GitOps kinds, not DB-authored

Match `RequirementScope`. Both are YAML kinds parsed in `Freeboard.Core`, validated
to diagnostics, and synced by the importer. No app write path in V1. This reuses
the whole existing pipeline and keeps authoring in git (the source of truth).
Alternative (DB-first authoring via API/UI) is rejected: it duplicates the write
store and diverges from every other kind.

Files: `ConfigModel.cs` (add `KindVendor`, `KindVendorScope` to `GitOpsSchema`; add
`Vendor` and `VendorScope` records; add `List<Vendor> Vendors` and
`List<VendorScope> VendorScopes` to `GitOpsConfig`), `ConfigLoader.cs` (two
`SchemaKeys` entries; two `apiVersion` attribute overrides; two `switch` cases;
extend the unknown-kind message list), `ConfigValidator.cs` (add `ValidateVendors`
and `ValidateVendorScopes`, called in phase order after controls and requirements
so their id sets are available).

### D2: VendorScope targets exactly one of `requirement` or `control`

The issue says the target is `requirement|control`. `Control` already exists as a
kind (with `maps_to` requirements); #47 only enriches its semantics, so a
`VendorScope` can name an existing `Control` id today. Model both as optional
string fields (`Requirement`, `Control`) on the `VendorScope` record. Validation
requires **exactly one** to be non-empty and requires the named id to resolve in
the corresponding id set (requirements or controls). This matches the literal
`requirement|control` and is forward-compatible: when #47 lands, control-targeted
vendor scopes already work.

Rejected alternatives:

- A single `target` string plus a `target_kind` discriminator: loses referential
  integrity (cannot FK one column to two tables) and departs from the typed-FK
  precedent set by every other kind.
- Requirement-only now, control later: rejected because `Control` already exists,
  so supporting it now costs one extra nullable column and one validator branch and
  avoids a follow-up migration.

### D3: `justification` required only when `disposition: Out`

This is the one net-new validation rule; nothing in the codebase does conditional
required-ness today (`CheckRequired` is unconditional). Implement it directly in
`ValidateVendorScopes`: after the disposition parses, if it is `Out` and
`justification` is null/whitespace, emit a diagnostic naming the vendor-scope. An
`In` vendor-scope MAY carry a justification (harmless) but does not require one.
`justification` is stored on both `In` and `Out` rows (nullable), so the read
surface can always render whatever rationale was authored.

Rejected alternative: require `justification` on every VendorScope. Rejected
because an `In` (applies normally) scope needs no rationale, and over-requiring
invites noise placeholder text.

### D4: VendorScope is flat - no org-tree inheritance

As specified in #46, `VendorScope` has no `organisation` field, and `Vendor` has no
parent, so there is no tree to inherit along. "Resolves under scope inheritance
like `RequirementScope`" is interpreted as: reuse the `ScopeDisposition` enum, the
same case-sensitive `TryParseDisposition`, the same one-per-pair uniqueness
(`(vendor, target)` mirrors `(organisation, requirement)`), and the same
never-silent surfacing of `Out` items in the read projection. It is NOT wired into
`StatementOfApplicability.Resolve` (that projection is org-tree keyed and vendor-
free). See Open Questions Q1 for the alternative that adds an organisation
dimension.

### D5: Persistence via one new migration `011_vendors.sql`

Storage is per-kind (no generic document table), so two new tables are required.
Mirror `009_requirement_scopes.sql` exactly (utf8mb4_bin ids, `created_at`/
`updated_at` DATETIME(6), InnoDB, `CREATE TABLE IF NOT EXISTS`, `ON DELETE
RESTRICT` FKs):

- `vendors(id PK, api_version, title, created_at, updated_at)`.
- `vendor_scopes(id PK, api_version, title, vendor_id FK->vendors RESTRICT,
  requirement_id NULL FK->requirements RESTRICT, control_id NULL FK->controls
  RESTRICT, disposition VARCHAR(16), justification TEXT NULL, created_at,
  updated_at)`, with `UNIQUE (vendor_id, requirement_id)` and
  `UNIQUE (vendor_id, control_id)` (MySQL treats NULLs as distinct, so each unique
  key constrains only its own target kind), and secondary indexes on the FK
  columns. The exactly-one-target invariant is enforced primarily in the Core
  validator (D2) - that is where the user gets an actionable diagnostic - and
  additionally by a table `CHECK` constraint as defence-in-depth:
  `CHECK ((requirement_id IS NULL) <> (control_id IS NULL))`. The MySQL baseline is
  8.4 (see the test compose file, `mysql:8.4`), and MySQL enforces `CHECK`
  constraints since 8.0.16, so a raw row with both target columns set or both null
  is rejected by the engine even if the importer is bypassed. The validator stays
  the primary, human-facing gate; the CHECK is a backstop, not the error a normal
  author ever sees.

Migrations are auto-discovered embedded resources ordered by ordinal
(`MigrationCatalog`), so adding the file needs no code registration.

Importer (`MySqlGitOpsImporter`): upsert `vendors` alongside the other independent
upserts (after standards/requirements/controls, before the scope replaces);
full-replace `vendor_scopes` in the delete-all+insert phase like
`requirement_scopes`; prune absent `vendors` in the reverse-FK delete phase after
`vendor_scopes` are gone. Vendors reuse the existing `DomainRow(Id, ApiVersion,
Title)` (as `controls` do - no vendor-specific row type); add `VendorScopeRowPlan`
to `ImportPlan`, with blank `justification` normalized to null via the existing
`NullIfBlank`.

### D6: Read surfaces mirror the compliance stack; parity via SSR page + CLI-over-API

- Read store: add `GetVendorsAsync` and `GetVendorScopesAsync` to `IComplianceStore`
  / `MySqlComplianceStore`; add `VendorRow(Id, Title)` and
  `VendorScopeRow(Id, Title, Vendor, Requirement, Control, Disposition,
  Justification)` to `ComplianceReadModels`; extend `ComplianceCounts` with
  `Vendors` and `VendorScopes`.
- API (`ComplianceEndpoints`): add `GET /api/v1/freeboard/vendors` and
  `GET /api/v1/freeboard/vendor-scopes` (GET-only, `RequireAuthorization`, 503 on
  unreachable store); include the two new counts in `/compliance/status`. JSON
  naming follows the existing endpoints exactly (see D7): the resource payload keys
  (`id`, `title`, `vendor`, `requirement`, `control`, `disposition`,
  `justification`) are all single-word, so no snake_case/camelCase question arises;
  the `/compliance/status` `persisted` object is camelCase for multi-word keys, so
  the new count key is `vendorScopes` (matching the existing `requirementScopes`),
  not `vendor_scopes`.
- Web SSR (`vendor-register`): new page `/compliance/vendors`
  (`Pages/Compliance/Vendors.cshtml(.cs)`) injecting `IComplianceStore` in-process
  like the SoA page. Lists each vendor and, under it, its scopes with target,
  disposition, and - for every `Out` - the justification text. GET-only, any
  authenticated user, unaffected by read-only mode. Add a nav link. No org filtering:
  unlike `/organisations`, `/scopes`, and `/requirement-scopes` (which narrow rows to
  the caller's accessible orgs via `IOrgAccess`), the vendor endpoints and page
  intentionally expose all vendors and vendor-scopes - including exception
  justifications - to any authenticated user, including one with zero org access.
  Vendors are org-independent reference data (D4 flat model, no `organisation`
  dimension), so there is no per-org confidentiality boundary to enforce. This ties
  to Open Question Q1: if an organisation dimension is later added to vendor-scopes,
  revisit this access decision and add narrowing.
- CLI (`vendor-register`): new `VendorCommands` group registered in `Program.cs`,
  modelled on `UserCommands.List` (HTTP, not DB-direct). `freeboard vendor list`
  calls a new `IFreeboardApiClient.ListVendorsAsync` -> `GET /vendors` and a
  companion read of `/vendor-scopes`, printing each vendor with its exceptions and
  the justification for every `Out` (never silent). Exit codes follow the CLI
  convention (0 ok, 1 validation, 3 operational). The `Run` (client construction +
  URL/token resolution + 0/1/3 exit contract) and `Translate` (ApiResult -> exit
  code) helpers are currently `private static` inside `UserCommands`, so
  `VendorCommands` cannot call them. Extract both into a small `internal static`
  helper in `Freeboard.CLI` (e.g. `ApiCommandRunner`) and have both `UserCommands`
  and `VendorCommands` call it. This is low-liability: the helpers are already
  generic (they depend only on `ApiClientFactory` and `ApiResult`/`ApiOutcome`, no
  user-specific state), so extracting them removes a would-be duplicate rather than
  adding an abstraction, and keeps the 0/1/3 exit-code mapping defined in exactly one
  place.

The CLI reading over HTTP (not DB-direct) keeps parity with `UserCommands` and
avoids giving the CLI a second persistence path.

### D7: JSON field naming matches the two existing conventions already in the code

The existing API uses two naming styles, and the new surfaces MUST match each in its
own place (verified against `src/Freeboard/Compliance/ComplianceEndpoints.cs`):

- The resource read endpoints serialise multi-word keys in snake_case:
  `/standards` emits `source_url`, `/controls` emits `maps_to`. Every field on the
  new `vendors` and `vendor-scopes` payloads (`id`, `title`, `vendor`,
  `requirement`, `control`, `disposition`, `justification`) is a single word, so
  snake_case and camelCase are byte-identical for them - there is no real choice to
  make, and no multi-word key to get wrong.
- The `/compliance/status` `persisted` object serialises multi-word count keys in
  camelCase: the existing key is `requirementScopes`. The new count keys therefore
  MUST be `vendors` and `vendorScopes` (camelCase), NOT `vendor_scopes`. Using
  snake_case here would make the status object internally inconsistent.

The CLI wire records read fields by explicit property name (see
`HttpFreeboardApiClient` reading `global_role`, `temporary_password`), so the CLI
just reads the same single-word keys the API emits; no DTO naming divergence exists.

This decision resolves the one naming ambiguity between the two source plans: the
draft spec/tasks text in one place said `vendor_scopes` for the status count, which
would not match the code. The unified plan uses `vendorScopes`.

## Risks / Trade-offs

- [Interpretation risk: "resolves under scope inheritance like RequirementScope"
  with no organisation field] -> Documented decision D4 and Open Question Q1. The
  flat model is the faithful reading of the #46 field list; an org dimension is an
  additive future change (new column + validator branch + resolution wiring), not a
  rework.
- [Scoring hook without a denominator] -> No score exists, so "leaves the
  denominator" cannot be implemented literally. Mitigation: the data model records
  the exception and the read surfaces always show the justification, so a later
  scoring change has everything it needs. The requirement is not silently dropped -
  it is delivered as the never-silent read plus a documented forward hook.
- [Two-nullable-target columns allow a malformed row with both or neither target if
  the importer is bypassed] -> Closed on both sides: the Core validator rejects
  both/neither before import (the human-facing gate), and a DB `CHECK
  ((requirement_id IS NULL) <> (control_id IS NULL))` rejects a raw malformed row at
  the engine (MySQL 8.4 enforces CHECK). The FKs keep per-column referential
  integrity. Defence-in-depth, not a single trust boundary.
- [Read-surface size: full vendor stack (store, API, page, CLI) is a lot of code]
  -> Each layer is a thin mirror of an existing one; the parity rule is an explicit
  acceptance criterion. No new abstraction is introduced.
- [Duplicate `(vendor, target)` across the two unique keys] -> Enforced both in the
  validator (`seenPairs` over `(vendor, requirement)` and `(vendor, control)`) and
  by the two DB unique keys.

## Migration Plan

- Add `011_vendors.sql`. Additive and forward-only; no existing table is altered,
  so rollback is dropping the two new tables (no data migration).
- Applied by the existing operator path: `freeboard system migrate` or
  `gitops sync --migrate`. Web app does not migrate at startup.
- Deploy order: apply migration, then `gitops sync` a config that includes the new
  kinds. Old configs without vendors continue to sync unchanged (empty vendor set).

## Open Questions

- Q1 (for reviewer): Is the flat, org-independent VendorScope (D4) the intended
  model, or should a `VendorScope` optionally carry an `organisation` and resolve by
  nearest-ancestor like `RequirementScope`, and/or should the SoA projection fold in
  vendor exceptions? The #46 field list has no organisation, so V1 ships flat; the
  org dimension is additive if wanted.
- Q2 (for reviewer): The scoring/denominator interaction cannot be implemented
  (no denominator exists yet, #56/#58 downstream). Is delivering the never-silent
  read plus the recorded exception (so downstream scoring can exclude it) the right
  V1 boundary, or should this change also introduce a first scoring primitive?
- Q3: Should `Vendor` carry optional metadata now (e.g. `category`, `url`,
  `contact`) or stay `id`/`title` until a consumer needs it? V1 keeps it minimal
  (additive later).
- Q4: CLI shape - one `vendor list` that prints vendors with nested exceptions, or
  separate `vendor list` and `vendor exceptions` commands? Proposal assumes the
  former with justifications inline.

## Plan synthesis: sources, divergences, resolutions

This change unifies two independent plans for issue #46, both grounded in the same
real files. They agreed on the architecture; this section records where each idea
came from, where they differed, and how the difference was resolved.

### Where the plans agreed (shared basis)

- Use `RequirementScope` as the implementation template, not a new GitOps
  subsystem. Same pipeline: `ConfigModel`/`ConfigLoader`/`ConfigValidator` in
  `Freeboard.Core/GitOps` -> `ImportPlan`/`MySqlGitOpsImporter` in
  `Freeboard.Persistence/GitOps` -> `IComplianceStore` reads in
  `ComplianceReadModels.cs` -> API/SSR/CLI read surfaces. (Both.)
- `Vendor` and `VendorScope` are static GitOps kinds persisted in per-kind tables,
  not app-authored write-store entities. No `Freeboard.Enterprise` involvement.
  (Both; D1.)
- `VendorScope` targets exactly one of `requirement` or `control`; `Control`
  already exists as a kind with `maps_to`, so control targets work today without
  waiting on #47. (Both; D2.)
- `Out` requires a non-empty `justification`; `In` may omit it; validation lives in
  the validator after disposition parsing. (Both; D3.)
- Read surfaces on web SSR and CLI (over HTTP, not DB-direct) in one PR, always
  rendering each `Out` exception with its justification. (Both; D6.)
- Full-replace `vendor_scopes`, upsert `vendors`, prune absent vendors after their
  scopes are gone; blank justification normalised to NULL. (Both; D5.)

### Divergences and resolutions

1. JSON field naming for the status counts. One plan's draft text said
   `vendor_scopes`; the other (Codex M2) pointed out the existing status counts
   serialise camelCase (`requirementScopes`). Verified against
   `ComplianceEndpoints.cs`: the `persisted` object is camelCase for multi-word
   keys, while resource endpoints use snake_case for multi-word keys
   (`source_url`, `maps_to`). Resolution (D7): use `vendorScopes` for the status
   count; the resource payload fields are all single-word so the style is moot
   there. Kept internally consistent with the code.

2. DB CHECK for exactly-one-target. The first plan enforced the invariant only in
   the Core validator and explicitly declined a DB CHECK. Codex M1 proposed adding a
   CHECK if the MySQL baseline supports it. Verified: baseline is MySQL 8.4, which
   enforces CHECK (since 8.0.16). Resolution (D5): keep the validator as the
   primary, user-friendly rejection AND add
   `CHECK ((requirement_id IS NULL) <> (control_id IS NULL))` as defence-in-depth.

3. Flat scope (no org inheritance). Both plans agree: #46's field list has no
   `organisation`, so `VendorScope` is a flat per-`(vendor, target)` statement with
   no nearest-ancestor resolution and no `StatementOfApplicability.Resolve` wiring
   (D4). Codex H1 and the first plan reached the same reading independently. Left as
   Open Question Q1 for the reviewer; V1 ships flat.

4. Scoring/denominator boundary. Both plans agree no score or denominator exists in
   the code today (only per-kind counts via `ComplianceCounts`). Codex H2 and the
   first plan both conclude the acceptance phrase "excepted items leave the score
   denominator" cannot be implemented literally. Resolution: V1 delivers the
   never-silent read (every `Out` shown with its justification) plus the recorded
   exception row, so a downstream scoring change can exclude it. No dead scoring
   code is added. Made explicit in the proposal's acceptance-criteria mapping and
   Open Question Q2.

### Final unified approach

The synthesized plan is the first plan's structure (kinds, tables, importer, read
stack, parity rule) with two Codex-driven hardening changes folded in: the
`vendorScopes` camelCase status count (D7) and the DB CHECK backstop (D5). No file
path or existing-kind claim in this design is speculative; each was read in the
current repo on the working branch.
