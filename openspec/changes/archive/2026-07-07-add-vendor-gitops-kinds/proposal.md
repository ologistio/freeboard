## Why

The evidence model needs first-class vendors (Crowdstrike, FleetDM, Google
Workspace, the accountant) so collectors, evidence, and scope can map to the
software and platforms in use, and so control exceptions can be justified per
vendor. Today the GitOps config has no vendor concept: a requirement that a given
tool cannot meet (an accounting package that supports MFA but not SSO) has nowhere
to record the exception or its rationale. This is Phase 0 of the CE+ readiness V1
milestone and is on the critical path: it unblocks issue #47 (Control rules) and
the downstream evidence and scoring work.

## What Changes

- Add a static GitOps kind `Vendor` with `id` and `title`, parsed, validated, and
  synced through the existing config pipeline exactly as `Requirement` and
  `Organisation` are.
- Add a static GitOps kind `VendorScope` that binds one `Vendor` to exactly one
  target (a `Requirement` id or a `Control` id) with a `disposition` (`In` or
  `Out`) and a `justification`. `disposition` reuses the existing
  `ScopeDisposition` (`In`/`Out`) enum.
- Add one net-new validation rule: a `VendorScope` with `disposition: Out` MUST
  carry a non-empty `justification` (the exception rationale, e.g. "accounting
  package supports MFA but not SSO"; "accountant has no logins - N/A"). Every
  other vendor rule mirrors an existing scope rule (required fields, reference
  resolution, one-per-pair uniqueness, case-sensitive disposition parse).
- Persist both kinds in new per-kind MySQL tables (`vendors`, `vendor_scopes`) via
  a new forward-only migration, wired into the GitOps importer and read store.
- Expose a read-only vendor register on both surfaces in the same PR (parity
  rule): a web SSR page under `/compliance/vendors` and a CLI `vendor list`
  command that reads through the HTTP API. The register always shows every
  `Out` VendorScope with its `justification` - an exception is never silent.

## Capabilities

### New Capabilities

- `vendor-register`: a read-only register of vendors and their per-vendor
  requirement/control exceptions, exposed as a web SSR page and a CLI command.
  Vendors are a new domain with no existing read-surface owner, so the register
  view is a distinct user capability with a distinct owner. Authorship (the config
  kinds) and storage extend existing layer capabilities rather than duplicating
  them, following the convention set by the `RequirementScope` change.

### Modified Capabilities

- `gitops-config-format`: adds the `Vendor` and `VendorScope` kinds to the schema,
  the kind enumeration, loader routing, and validation rules (including the
  justification-required-when-`Out` rule and the exactly-one-target rule).
- `compliance-persistence`: adds `vendors` and `vendor_scopes` tables via a new
  migration (including a `CHECK` backstop for the exactly-one-target invariant),
  extends the read store, importer order, read models, and counts.
- `compliance-web-read`: adds the `vendors` and `vendor-scopes` read endpoints and
  their counts in the compliance status summary.

## Acceptance criteria mapping

Mapping #46's acceptance language to what this change actually delivers, stated
honestly where a literal reading is not buildable on the current codebase:

- "Add `Vendor` and `VendorScope` GitOps kinds" -> Delivered: both kinds parse,
  validate, persist, and sync through the existing pipeline (D1-D5).
- "VendorScope binds a vendor to a requirement or control with a disposition and
  justification" -> Delivered: exactly-one-of `requirement`/`control`,
  `In`/`Out` disposition, `justification` required when `Out` (D2, D3).
- "Resolves under scope inheritance like `RequirementScope`" -> Delivered as flat
  per-`(vendor, target)` resolution. #46's field list has no `organisation`, so
  there is no org tree to inherit along; V1 ships flat and does NOT wire
  `StatementOfApplicability.Resolve` (D4). Adding an org dimension is an additive
  future change (Open Question Q1). This is a deliberate, documented scope
  reduction, not silent omission.
- "Excepted items leave the score denominator and always show their justification"
  -> Partially delivered, honestly bounded. There is no score or denominator in the
  codebase today (only per-kind counts via `ComplianceCounts`), so "leaves the
  denominator" cannot be implemented literally here. This change delivers the two
  buildable halves: (1) every `Out` exception is recorded with its `justification`
  and (2) the read surfaces always render that justification (never silent). The
  recorded exception is exactly what a downstream scoring change (#56/#58) needs to
  exclude an item from a denominator. No dead/placeholder scoring code is added now
  (Open Question Q2).
- "Read surface for the vendor register" -> Delivered on both web SSR and CLI in one
  PR per the parity rule, always showing each `Out` exception with its justification
  (D6).

## Impact

- MIT (default). All code lands in `Freeboard.Core` (parse/validate),
  `Freeboard.Persistence` (tables/importer/read store), `Freeboard` (API endpoints
  and SSR page), and `Freeboard.CLI` (read command via HTTP API). Nothing here is
  an enterprise-gated feature, so nothing goes in `Freeboard.Enterprise`. The
  reference graph is respected: the CLI reads through the API and never references
  `Freeboard.Enterprise`.
- New MySQL migration `011_vendors.sql` (additive, forward-only; no existing table
  altered).
- New CLI command group `vendor`; new API routes `GET /api/v1/freeboard/vendors`
  and `GET /api/v1/freeboard/vendor-scopes`; new page `/compliance/vendors`.

## Non-goals

- No scoring or coverage denominator. No compliance score exists in the codebase
  today (only raw per-kind counts). "Excepted items leave the score denominator
  and always show their justification" is delivered here as: the read surfaces
  always render each `Out` exception with its justification (never silent), and the
  data model records the exception so a future scoring change can exclude it from a
  denominator. Building the denominator is downstream (issues #56/#58).
- No org-tree inheritance for VendorScope. As specified in #46 a `VendorScope`
  carries no `organisation` field, so it does not participate in the
  nearest-ancestor resolution that `RequirementScope` uses; it is a flat per-
  `(vendor, target)` statement. Adding an organisation dimension is a possible
  future extension (see design Open Questions).
- No app-managed (UI/API) create/update/delete of vendors. Authoring is GitOps
  only in V1, matching how requirement scopes were first shipped.
- No `Control` model changes. `Control` already exists as a kind; #47 enriches
  control semantics. VendorScope targets an existing `Control` id as-is.
- No optional Vendor metadata beyond `id` and `title` in V1 (additive later).
