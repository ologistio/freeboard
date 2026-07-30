## 1. Core vocabulary, config model, and validation

Commit: `feat(core): add vendor tier and data classes to the asset schema`

- [x] 1.1 Add `VendorTier` (`Critical`, `High`, `Medium`, `Low`) to
  `src/Freeboard.Core/Assets/`, next to `AssetKind`, with a doc comment saying what
  the tier means and that it is a permanent closed set.
- [x] 1.2 Add the data class token set as one `static readonly` ordinal set in the
  same folder: `pii`, `phi`, `special-category`, `payment-card`, `credentials`.
  Keep it `string`, not an enum, and record in the doc comment that an
  admin-editable taxonomy is planned so the type must not close.
- [x] 1.3 Add `Tier` (string, raw authored text) and `DataClasses`
  (`IReadOnlyList<string>`, empty when absent) to the `Asset` record in
  `src/Freeboard.Core/GitOps/ConfigModel.cs`, with the YAML keys `tier` and
  `data_classes`. Confirm the loader's unknown-field check now accepts both.
- [x] 1.4 Add the four error rules to `ValidateAssets` in
  `src/Freeboard.Core/GitOps/ConfigValidator.cs`: unknown tier token, unknown data
  class token, either field on a non-Vendor asset, duplicate token in
  `data_classes`. Each diagnostic names the asset and the offending value, matching
  the surrounding message style.
- [x] 1.5 Add the tierless-vendor `Warning` alongside the existing missing-`owner`
  warning, and confirm an absent or empty `data_classes` emits nothing.
- [x] 1.6 Extend the Core tests: one load case for a vendor carrying both fields,
  one `Error` case each for the four rules, exactly one `Warning` for a tierless
  vendor with validation still passing, and no diagnostic for an omitted or empty
  `data_classes`.

## 2. Persistence: columns, migration, and sync

Commit: `feat(persistence): persist vendor tier and data classes on the asset row`

- [x] 2.1 Add migration `022_vendor_tier_data_classes.sql` adding
  `tier VARCHAR(16) NULL` and `data_classes JSON NULL` to `assets`. No index, no
  foreign key, no backfill.
- [x] 2.2 Add both fields to `AssetRowPlan` in
  `src/Freeboard.Persistence/GitOps/ImportPlan.cs` and to whatever maps a Core
  `Asset` into it.
- [x] 2.3 Extend `UpsertAssetsAsync` in
  `src/Freeboard.Persistence/GitOps/MySqlGitOpsImporter.cs`: add both columns to the
  explicit column list, to the parameter set, and to the `ON DUPLICATE KEY UPDATE`
  assignments, so removing a key from YAML nulls the column exactly as `owner` does.
  Serialize `data_classes` as a JSON array, writing null for an absent or empty list.
- [x] 2.4 Add both columns to `AssetSelect` and to `AssetNode` in
  `src/Freeboard.Persistence`, deserializing the JSON array back to a list (empty
  when null).
- [x] 2.5 Add `FREEBOARD_TEST_DB`-gated integration tests: a sync writes both
  columns, and a second sync with both keys removed nulls both. The clear-on-removal
  case is the one that fails silently without a test.

## 3. Web read surface

Commit: `feat(web): show vendor tier and data classes on the register`

- [x] 3.1 Project both fields onto the `/vendors` row in
  `src/Freeboard/Compliance/ComplianceEndpoints.cs`, keeping the existing
  owner-narrowed `Where` unchanged.
- [x] 3.2 Carry both onto `VendorRow` in
  `src/Freeboard/Pages/Compliance/Vendors.cshtml.cs`.
- [x] 3.3 Render the Tier and Data columns in
  `src/Freeboard/Pages/Compliance/Vendors.cshtml` as `<fb-tag>` with the default
  neutral tone. Keep "Not tracked" for an absent tier and for an empty class list.
  Do not change the row order or add a filter control.
- [x] 3.4 Extend `tests/Freeboard.Web.Tests/VendorsPageTests.cs`: rendered tags for a
  vendor carrying both, and "Not tracked" in both columns for one carrying neither.

## 4. CLI parity

Commit: `feat(cli): print vendor tier and data classes in vendor list`

- [x] 4.1 Add both fields to `ApiVendor` in
  `src/Freeboard.CLI/IFreeboardApiClient.cs` and read them in `ReadVendor` in
  `src/Freeboard.CLI/HttpFreeboardApiClient.cs`, tolerating an absent property.
- [x] 4.2 Print both on the existing vendor line in
  `src/Freeboard.CLI/VendorCommands.cs`, using the established `?? "-"` placeholder.
  Leave the scope lines and the exit codes untouched.
- [x] 4.3 Extend the CLI tests: `vendor list` prints both, and prints `-` for each
  absent value.

## 5. Documentation and examples

Commit: `docs(gitops): document vendor tier and data classes`

- [x] 5.1 Add both fields to the `Asset` section of `docs/gitops.md`: the schema
  fields, an example document, and the validation rules including the four errors and
  the tierless warning.
- [x] 5.2 Define each data class token by its regime in the same section, and show
  health data authored as both `phi` and `special-category`.
- [x] 5.3 Give the vendors in `examples/gitops/vendors.yaml` and
  `examples/fixture-corp/vendors/*.yaml` real tiers and data classes, keeping at
  least one vendor with no data classes so the empty case stays exercised. Confirm
  `freeboard gitops validate` still exits `0` over both roots.
- [x] 5.4 Update both tabs of
  `src/Freeboard.Web/Content/Docs/resources/vendors.md` so the described columns
  match what the page now renders.

## 6. Verification

- [x] 6.1 `dotnet build`.
- [x] 6.2 `dotnet test` with no external services: Core, CLI, and web tests pass and
  the gated suites skip cleanly.
- [x] 6.3 Bring up the test MySQL, export `FREEBOARD_TEST_DB`, and run
  `dotnet test tests/Freeboard.Persistence.Tests` so the migration and the
  clear-on-removal test actually execute.
- [x] 6.4 `npx markdownlint-cli2 "**/*.md"` over the changed markdown.
- [x] 6.5 Run `freeboard gitops validate` against `examples/gitops` and
  `examples/fixture-corp`, confirming exit `0`.
