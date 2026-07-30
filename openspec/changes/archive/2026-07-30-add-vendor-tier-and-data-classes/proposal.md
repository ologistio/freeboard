## Why

The vendor register at `/compliance/vendors` renders a Tier column and a Data column
with nothing behind them. Both read "Not tracked" for every vendor, because no field
holds either fact. Tier (how much damage this vendor can do) and data classification
(what regulated data it holds) are the two facts a third-party risk register exists to
record. Without them the register is a list of names, and the work queued behind it -
review cadence, filter chips, grouping - has nothing to key on.

This change is MIT. It extends the declared asset model, the gitops loader and
validator, the MySQL asset row, the read API, the web page, and the CLI. None of that
is a paid, enterprise-gated feature, so no part of it belongs in
`src/Freeboard.Enterprise`.

## What Changes

- `kind: Asset` gains two declared-only fields, valid on `type: Vendor` only:
  - `tier`: one of `Critical`, `High`, `Medium`, `Low`.
  - `data_classes`: a set of tokens from `pii`, `phi`, `special-category`,
    `payment-card`, `credentials`.
- Validation gains five rules. Errors: an unknown tier token, an unknown data class
  token, either field on a non-Vendor asset, and a duplicate entry in `data_classes`.
  A Vendor carrying no `tier` is a non-blocking `Warning`, matching a declared Vendor
  with no `owner`. An absent or empty `data_classes` produces no diagnostic.
- `Freeboard.Core` gains a `VendorTier` enum and one closed set of data class tokens.
  Data classes stay `string` end to end.
- The `assets` table gains `tier VARCHAR(16) NULL` and `data_classes JSON NULL` via a
  forward-only migration. The declared-asset upsert writes both, so removing a key from
  YAML clears the column.
- `GET /api/v1/freeboard/vendors` returns both fields on each vendor row.
- The register's Tier and Data columns render neutral tags. An absent value keeps
  rendering "Not tracked".
- `freeboard vendor list` prints both on the existing vendor line, `-` when absent.
- `docs/gitops.md`, `examples/gitops/vendors.yaml`, and
  `src/Freeboard.Web/Content/Docs/resources/vendors.md` document the fields.

No breaking change. Both fields are optional, so every existing config keeps loading.

## Capabilities

### New Capabilities

None. Every behavior added here belongs to an existing capability.

### Modified Capabilities

- `gitops-config-format`: asset authoring gains `tier` and `data_classes`, and asset
  validation gains the five rules above.
- `asset-model`: the `assets` table gains the two columns, and declared-asset sync
  clears a column when its key is removed from config.
- `vendor-register`: the register page, the vendors API row, and the CLI output all
  carry tier and data classes, in one requirement so the parity rule has a single home.

No `compliance-web-read` delta. That capability specifies the read surface's general
behavior (owner narrowing, fail-closed reads), not the field set of one endpoint.

## Impact

Code:

- `src/Freeboard.Core/Assets/` - new `VendorTier` enum and the data class token set.
- `src/Freeboard.Core/GitOps/ConfigModel.cs` - two fields on the `Asset` record.
- `src/Freeboard.Core/GitOps/ConfigValidator.cs` - the five validation rules.
- `src/Freeboard.Persistence/Migrations/` - one new migration adding both columns.
- `src/Freeboard.Persistence/GitOps/MySqlGitOpsImporter.cs` - `UpsertAssetsAsync`
  extends its explicit column list and its `VALUES(...)` assignments.
- `src/Freeboard/Compliance/ComplianceEndpoints.cs` - the vendor projection.
- `src/Freeboard/Pages/Compliance/Vendors.cshtml` and its page model - the two columns.
- `src/Freeboard.CLI/VendorCommands.cs` and the API row it deserializes.

Docs: `docs/gitops.md`, `examples/gitops/vendors.yaml`, and both tabs of
`src/Freeboard.Web/Content/Docs/resources/vendors.md`.

Dependencies: none added.

Operational: the migration is forward-only and additive (two nullable columns). It
holds no data migration, so a re-run against an already-migrated database is the only
failure mode, which the existing schema-version runner already prevents.

## Non-goals

- Filtering, grouping, or sorting by tier or data class. The register keeps sorting by
  vendor id. Chips and grouping are separate work, and both need the per-person view
  persistence that L2 demands.
- Anything reading the fields. Tier does not drive review cadence, status, or scoring.
  Weighted scoring stays out of V1.
- An admin-editable data class taxonomy. Core validates against a closed set. The
  enterprise editor that reopens it moves that check to sync time when it lands, so no
  taxonomy table ships now.
- Tier or data classes on Machine, Company, or Department assets. The field names stay
  generic so a later change can widen them without a rename, but authoring either on a
  non-Vendor asset is an error today.
- A tri-state "assessed as holding no data" distinct from "not assessed".
  `data_classes: []` means what omitting the key means. That fact has an owner and a
  date, so it belongs to the vendor review record, not to this field.
- An access axis (the prototype's `production` and `internal` labels). Those classify
  what systems a vendor touches, not what data it holds, and blast radius is what
  `tier` already says.
- A `--json` output mode for the CLI. Wanted, but its own change across every read
  command.
