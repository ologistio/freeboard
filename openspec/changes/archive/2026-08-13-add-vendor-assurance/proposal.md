## Why

The vendor register at `/compliance/vendors` renders an Assurance column with nothing
behind it: every vendor reads "None on file", because no field holds the fact. Which
certifications a vendor holds, and when each one lapses, is the second thing a
third-party risk register exists to record, after how much damage the vendor can do.
Without it a lapse is invisible until someone asks for the certificate, and the work
queued behind it - skipping a review a certification already answers, setting a review
cadence, deriving a score - has nothing to read.

This change is MIT. It extends the declared asset model, the gitops loader and
validator, the MySQL schema, the read API, the web page, the nav rail, and the CLI.
None of that is a paid, enterprise-gated feature, so no part of it belongs in
`src/Freeboard.Enterprise`.

## What Changes

- A `Vendor` asset gains an optional `assurances` list, declared-only. Each entry names
  a `standard` (a `Standard` id), an `expires` date, and an optional `warn_days`
  override. No other asset type may carry the field.
- The `standard` is a hard reference: a dangling one is an `Error`, matching a
  `Scope` target. Recording a vendor's SOC 2 therefore means declaring SOC 2 as a
  `Standard` document, which is what later lets a certification answer a requirement
  without a mapping layer.
- Authoring gains six errors. Five are `ConfigValidator` rules: an `assurances` list on an
  asset that is not a `Vendor`, an unknown or dangling `standard`, a missing or unparseable
  `expires`, a `warn_days` that is not a whole number of zero or more, and two entries naming the same standard on one
  vendor. The sixth, an unknown field on an entry, is a `ConfigLoader` diagnostic, because
  it has to read the authored YAML node. A vendor with no `assurances` produces no
  diagnostic. An
  `expires` already in the past produces no diagnostic either: the validator is
  clock-free, so its output cannot depend on when it ran.
- `Freeboard.Core` gains an assurance status - `Valid`, `Expiring`, `Expired` - derived
  by a pure function of `(expires, warnDays, today)`. The status is never stored: a
  stored one goes stale the moment the clock passes it. A `warn_days` of zero gives no
  advance notice at all: the entry reads `Valid` up to and including its expiry date and
  `Expired` after it.
- A new `vendor_assurances` table holds the rows, keyed on `(vendor_id, standard_id)`,
  added by a forward-only migration. A `sync` replaces the whole set, so an entry removed
  from config leaves no row behind.
- The warning window is `AssuranceOptions.WarnWindowDays`, bound from configuration and
  defaulting to 90 days. An entry may override it with `warn_days`.
- The register renders one provenance stamp per assurance: the standard's title and the
  expiry, untinted when valid, amber when expiring, red when expired. A vendor with none
  keeps the plain "None on file".
- The summary notice above the tabs turns amber and names the count when any readable
  vendor holds an expiring or expired assurance.
- The Vendors nav item badges that same owner-narrowed count. It is the first actionable
  count source in the app; every nav item badges nothing today.
- `GET /api/v1/freeboard/vendors` returns each assurance's `standard`, `expires`, and
  derived `status`.
- `freeboard vendor list` prints each assurance under its vendor.
- `docs/gitops.md`, `examples/gitops`, `examples/fixture-corp`, and
  `src/Freeboard.Web/Content/Docs/resources/vendors.md` document the field.

No breaking change. `assurances` is optional, so every existing config keeps loading,
and the new table starts empty.

## Capabilities

### New Capabilities

None. Every behavior added here belongs to an existing capability.

### Modified Capabilities

- `gitops-config-format`: asset authoring gains the nested `assurances` list, and asset
  validation gains the five validator rules above, with the sixth error - an unknown field
  on an entry - reported by the loader.
- `asset-model`: a new `vendor_assurances` table, its whole-set replacement on sync, and
  its place in the foreign-key-safe prune order that the declared-asset removal follows.
- `gitops-cli`: the `sync` command's hard-remove order, which enumerates the constraints it
  holds itself to and now names the assurance replace among them.
- `vendor-register`: the register page, the vendors API row, the CLI listing, the amber
  notice, and the nav badge all carry assurances, in one requirement so the parity rule
  has a single home.
- `web-design-system`: the provenance stamp gains an optional typed tone, and its `source`
  slot is stated to name where the value came from - a collecting integration for an
  automated value, the standard a certification is against for an assurance.
- `web-ux-conventions`: P2, one clause. A value that is a reference to a named record held
  outside the system, shown with that record's own date, names that record rather than being
  stamped MANUAL. Content a person supplies stays MANUAL.
- `compliance-web-read`: the vendors endpoint's field list. Its ratified text still says
  the endpoint returns "their `id` and `title` and no other field", which the shipped
  tier and data-class fields already contradict. This change states the real field set
  and repairs that drift.
- `compliance-persistence`: the importer's fixed order, which the capability enumerates
  clause by clause rather than delegating. The enumeration is stated to be a partial order -
  it never listed the integration-connection and collector placements that other capabilities
  own - and the assurance replace is added to it as one constraint: after the domain upserts,
  before the first absent-row delete. The same capability gains the
  vendor assurance input snapshot, beside the Statement of Applicability ones: the assets
  and the assurances are read in one repeatable-read transaction, because the owner edges
  that decide narrowing and the rows being narrowed cannot come from two views of the
  database. The web app takes that snapshot once per request and serves the request's asset
  reads from it, so `/compliance/vendors` goes from two asset reads to one.

## Impact

Code:

- `src/Freeboard.Core/Assets/` - the assurance status kind and its pure evaluation.
- `src/Freeboard.Core/GitOps/ConfigModel.cs` - the nested `Assurance` record and the
  list on `Asset`.
- `src/Freeboard.Core/GitOps/ConfigLoader.cs` - the `assurances` schema key and a nested
  unknown-field check over each entry, following the collector `config` precedent.
- `src/Freeboard.Core/GitOps/ConfigValidator.cs` - the five validator rules.
- `src/Freeboard.Persistence/Migrations/` - one migration creating `vendor_assurances`.
- `src/Freeboard.Persistence/GitOps/ImportPlan.cs` and `MySqlGitOpsImporter.cs` - the
  row plan and the whole-set replace, placed before the prune block.
- `src/Freeboard.Persistence/ComplianceReadModels.cs`, `IComplianceStore.cs`, and
  `MySqlComplianceStore.cs` - the assurance read model and the snapshot that reads it
  with the assets in one repeatable-read transaction.
- `src/Freeboard/Authz/AuthzRequestCache.cs` - the request-scoped snapshot memo, which
  becomes the request's one asset read.
- `src/Freeboard/Web/OrgSelection.cs`, `src/Freeboard/Pages/Compliance/Collectors.cshtml.cs`
  and `src/Freeboard/Pages/Compliance/IntegrationConnections.cshtml.cs` - the three other
  consumers of the request's asset list, switched onto that memo so they stop taking a
  second read and narrow by the same owner edges.
- `src/Freeboard/Compliance/` - `AssuranceOptions` and the vendors endpoint projection.
- `src/Freeboard/Pages/Compliance/Vendors.cshtml` and its page model - the cell, the
  notice, and the count.
- `src/Freeboard/Navigation/ShellNavResolver.cs` - the first badge count.
- `src/Freeboard/TagHelpers/StampTagHelper.cs`, `MarkEnums.cs`, and
  `assets/css/app.css` - the stamp tone.
- `src/Freeboard.CLI/` - the API row and the printed lines.

Docs and examples: `docs/gitops.md` (the field, an example, the validation rules, the
schema table list, and the `Freeboard:Assurance:WarnWindowDays` config key),
`examples/gitops/vendors.yaml`, `examples/fixture-corp`, all three tabbed blocks of
`src/Freeboard.Web/Content/Docs/resources/vendors.md`, P2 in
`src/Freeboard/stories/UxRules.mdx`, and a Storybook story for the toned stamp.

Dependencies: none added.

Operational: the migration creates one empty table with two `ON DELETE RESTRICT`
foreign keys and a `CHECK` keeping `warn_days` non-negative. It carries no data migration
and no backfill. Deploy order is the established one: `freeboard system migrate`, then the
app.

## Non-goals

- Anything that reads an assurance. It does not skip a review, set a cadence, gate a
  scope, or feed a score. That payoff is what this unlocks, not part of it.
- The vendor review model. Status and Next review stay empty, and the register's
  "Not evaluated" status stays correct: a certificate on file is not a Freeboard verdict.
- An authored status. `expires` plus the clock is the only source of the state, so no
  two facts can contradict each other.
- Evidence of the certification. No report upload, no auditor, no scope statement, no
  link to the certificate itself. Only the standard and the date.
- Assurances on a Company, Department, or Machine. The field is Vendor-only, as `tier`
  and `data_classes` are.
- Filtering, grouping, or sorting by assurance state. Row order stays vendor id.
- A user-editable warning window. Both the default and the per-entry override are
  configuration and git until the non-GitOps write path lands.
- A `Standard` catalog, or any relaxation of the rule that a referenced standard must be
  declared. Declaring ISO 27001 to record a vendor's certificate is the accepted cost.
