## 1. Core: schema, validation, and the derived status

Commit: `feat(core): add vendor assurances to the asset schema`

- [x] 1.1 Add `AssuranceStatus` (`Valid`, `Expiring`, `Expired`) to
  `src/Freeboard.Core/Assets/`, next to `VendorTier`, with the pure evaluation beside
  it taking `(DateOnly expires, int warnDays, DateOnly today)`. Follow
  `CollectorFrequency.IsStale` for the clock-free shape: the caller supplies the date.
  Encode the boundary exactly - `expires < today` is `Expired`, otherwise
  `warnDays > 0 && expires.DayNumber - today.DayNumber <= warnDays` is `Expiring`,
  otherwise `Valid`. Compare day numbers, NOT `today.AddDays(warnDays)`: `warnDays` arrives
  as an unbounded `int` (the deployment default binds unvalidated), and `AddDays` throws
  once the sum leaves the supported date range, which would fault the page, the API and the
  rail. A `DayNumber` difference cannot overflow. Do NOT clamp `warnDays`: config validation
  rejects a negative authored value, and the guard already makes a negative behave exactly
  as zero, so a clamp would only hide a caller bug. The `warnDays > 0` guard is
  load-bearing: it is what makes `warn_days: 0` warn only once the certificate has expired.
  Say both things in the doc comment.
- [x] 1.2 Add an `Assurance` record (`Standard`, `Expires`, `WarnDays`, all raw
  authored text) to `src/Freeboard.Core/GitOps/ConfigModel.cs` and an
  `Assurances` list on the `Asset` record, bound from the YAML key `assurances`. Keep
  `Expires` and `WarnDays` as strings so the validator owns the parse errors, matching
  how `Tier` stays raw text.
- [x] 1.3 Add `assurances` to the `Asset` entry in `SchemaKeys` in
  `src/Freeboard.Core/GitOps/ConfigLoader.cs`, and handle both degenerate inputs in the
  `KindAsset` branch alongside the `DataClasses` normalization: an explicit-null
  `assurances:` normalizes to an empty list, and a null sequence item is KEPT as an empty
  `Assurance` so the validator reports its missing fields. That is the `checks` treatment,
  not the `fields`/`quiz` one - dropping a null entry would swallow the diagnostic.
- [x] 1.4 Add a nested unknown-field check for each `assurances` entry in
  `ConfigLoader`, following `ReportUnknownConfigKeys`: diff the authored mapping's keys
  against `standard`, `expires`, `warn_days`, so a key with an empty value is rejected
  too. Call it from the same place the collector `config` check is called. Without this
  the key vanishes silently: the deserializer runs with `IgnoreUnmatchedProperties()`, and
  `ReportUnknownFields` reads the document's top-level keys only.
- [x] 1.5 Pass the standard id set into `ValidateAssets` in
  `src/Freeboard.Core/GitOps/ConfigValidator.cs` (standards are already validated
  first) and add the five validator rules next to `ValidateVendorRiskProfile`: `assurances`
  on a non-Vendor asset, a missing/blank/dangling `standard`, a missing or unparseable
  `expires` (`YYYY-MM-DD`, invariant, exact), a `warn_days` that is not a whole number
  of zero or more, and two entries naming the same standard. Each message names the
  asset and the offending value, in the surrounding style. The sixth error an author can
  hit - an unknown field on an entry - is the `ConfigLoader` diagnostic from 1.4, not a
  rule here.
- [x] 1.6 Confirm no diagnostic is produced for an omitted or empty `assurances`, and
  none for an `expires` already in the past. The validator reads no clock.
- [x] 1.7 Extend `tests/Freeboard.Core.Tests/AssetValidationTests.cs`: a load case for
  a vendor carrying two assurances (one with `warn_days`), one `Error` case each for the
  five validator rules from 1.5 and one for the loader's unknown-field diagnostic from 1.4,
  and no diagnostic for an omitted list, an empty list, or a past
  expiry. Add loader cases for the two degenerate inputs from 1.3: `assurances:` with no
  value loads as no assurances, and a null list item is reported rather than dropped.
- [x] 1.8 Add the status tests covering the three states and, by name, both boundaries:
  `ExpiryOnTodayIsExpiringWithinAWindowAndValidWithNone` (`expires == today`, which is
  `Expiring` when `warnDays > 0` and `Valid` when it is zero) and
  `LastDayOfTheWindowIsExpiring` (`expires` exactly `warnDays` days after `today` is
  `Expiring`, while one day beyond it is `Valid`). Pin the expired boundary in both windows, not only the
  zero one: `YesterdaysExpiryIsExpiredInsideANonZeroWindow` (`expires == today - 1` with a
  window of 30 is `Expired`, not `Expiring`) alongside a `warnDays` of zero across the
  expiry (`Valid` on the date, `Expired` the day after). Also cover a per-entry override
  that puts two assurances sharing an expiry in different states. All driven by a supplied
  `today`. Add `AnEnormousWindowIsExpiringRatherThanThrowing`: `warnDays` of `int.MaxValue`
  against a future expiry returns `Expiring`, which fails if the rule ever goes back to
  `today.AddDays(warnDays)`.

## 2. Persistence: table, sync, and read

Commit: `feat(persistence): persist vendor assurances`

- [x] 2.1 Add migration `src/Freeboard.Persistence/Migrations/023_vendor_assurances.sql`
  creating `vendor_assurances` (`vendor_id`, `standard_id`, `expires`, `warn_days`,
  `created_at`, `updated_at`), `PRIMARY KEY (vendor_id, standard_id)`,
  `ON DELETE RESTRICT` foreign keys to `assets (id)` and `standards (id)`, the covering
  key each foreign key needs, and `CHECK (warn_days IS NULL OR warn_days >= 0)` as a
  backstop to the config validation. Nullability follows the schema's convention that a
  required value is `NOT NULL`: `expires DATE NOT NULL` and both `DATETIME(6)` timestamps
  `NOT NULL`, with `warn_days INT NULL` the only nullable column, because it is the only
  optional one.
  The table and column names follow the schema's conventions: a child table is named for
  its parent (`vendor_scopes`, `collector_credentials`) and a foreign-key column ends in
  `_id`. Follow the DDL conventions of `020_scope_generalization.sql`:
  `VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin` id columns,
  `DATETIME(6)` timestamps with no default, `ENGINE=InnoDB DEFAULT CHARSET=utf8mb4`,
  and a header comment covering intent, the additive/forward-only property, and why
  there is no index on `expires`. `023` is the next free ordinal - do not reuse one.
- [x] 2.2 Add `VendorAssuranceRowPlan(VendorId, StandardId, Expires, WarnDays)` and a
  `VendorAssurances` list to `src/Freeboard.Persistence/GitOps/ImportPlan.cs`, projected
  from every asset's entries. `warn_days` is optional, so parse it with the tolerant
  `TryParse` idiom `ParseThreshold` already uses. `expires` is required and the column is
  `NOT NULL`, so parse it strictly and throw an `InvalidOperationException` naming the
  vendor and the value when it does not parse: validation has already rejected an
  unparseable date, so reaching this line means the caller skipped validation, and silently
  dropping the row would hide that as a vendor that quietly lost a certification. The type
  matters - `ImportPlan.From` runs inside `MySqlGitOpsImporter.ImportAsync`, and
  `GitOpsCommands` catches only `MigrationException`, `DbException`, and
  `InvalidOperationException`, so a `FormatException` or `ArgumentException` would escape as
  an unhandled stack trace instead of exit 3. The CLI then prints it under "Database
  operation failed", which misdescribes a config parse error; accept that rather than adding
  a message path, and make the exception message name the vendor and the value so the line
  still tells the operator what to fix. Extend `ImportPlanTests` for the projection,
  including an asset with no entries contributing no rows and an unparseable `expires`
  throwing `InvalidOperationException` rather than vanishing.
- [x] 2.3 Add `ReplaceVendorAssurancesAsync` to
  `src/Freeboard.Persistence/GitOps/MySqlGitOpsImporter.cs`, copying
  `ReplaceScopesAsync`: `DELETE FROM vendor_assurances;` then one batched insert. Call it
  immediately after `ReplaceControlRequirementsAsync` (step 3) and before the first delete
  of the prune block (step 4, the org role assignment prune). One placement covers BOTH
  `RESTRICT` keys because the prune block already deletes the declared assets before the
  standards: the replace must precede the declared-asset prune, which itself precedes the
  standard delete. Extend the class-level ordering comment to say exactly that, in those
  terms - not that any other position breaks one of the two keys, which is not true.
- [x] 2.4 Add `VendorAssuranceRow(VendorId, StandardId, Expires, WarnDays)` and a
  `VendorAssuranceInputs(Assets, Assurances)` snapshot record to
  `src/Freeboard.Persistence/ComplianceReadModels.cs`, a
  `GetVendorAssuranceInputsAsync` to `IComplianceStore`, and its implementation in
  `MySqlComplianceStore` following `GetStatementOfApplicabilityInputsAsync`: one
  `IsolationLevel.RepeatableRead` transaction reading the assets through the existing
  `ReadAssetsAsync` helper and the assurances through a
  `private const string VendorAssuranceSelect` ordered by `vendor_id, standard_id`. The
  two lists MUST come from one transaction: every caller narrows the assurances by the
  `owner` edges on the assets, so two autocommit reads can pair the pre-sync owner edges
  with post-sync assurance rows. Return the whole assurance set for the caller to group,
  matching how the register reads scopes. Add no standalone assurance-only read.
- [x] 2.5 Extend the `FREEBOARD_TEST_DB`-gated integration tests in
  `tests/Freeboard.Persistence.Tests/`: the migration's shape (composite key, both foreign
  keys, the `CHECK`); a sync writes both entries with and without an override; a second
  sync that drops one entry deletes only that row; a sync that empties a vendor's list
  removes all of its rows; a sync that drops the vendor removes its rows and does not
  violate a foreign key; and a sync that drops a referenced standard together with its
  entry succeeds; and that `GetVendorAssuranceInputsAsync` returns the persisted assets and
  the persisted assurances together. The removal cases are the ones that fail silently
  without a test.

## 3. Design system: the stamp gains a tone

Commit: `feat(web): give the provenance stamp a tone`

- [x] 3.1 Add `StampTone` (`Neutral`, `Warn`, `Fail`) to
  `src/Freeboard/TagHelpers/MarkEnums.cs`, each member named for the colour it emits:
  `Neutral` is the bare `.fb-stamp` (muted ink), NOT the violet `gen` variant. Doc comment:
  why it is not `MarkTone` (a stamp has no pass tone and no brand tone, so a green or brand
  stamp stays unrepresentable) and that `manual` and `gen` are provenance variants rather
  than tones.
- [x] 3.2 Add a nullable `Tone` property to
  `src/Freeboard/TagHelpers/StampTagHelper.cs` and map it to the variant class, replacing
  the variant rather than appending. No tone is the default and keeps today's provenance
  rendering: `fb-stamp manual` when `Manual` is set, `fb-stamp gen` otherwise. `Neutral`
  emits the bare `fb-stamp`, `Warn` and `Fail` the two new classes. Nullable, not a
  defaulted member, so no existing caller shifts and no member has to stand for "untoned".
  Update the doc comment to say the source slot names an integration for an automated value
  and, for a certification, the standard that certification is against.
- [x] 3.3 Give `.fb-stamp.warn` and `.fb-stamp.fail` rules in the components layer of
  `src/Freeboard/assets/css/app.css`, beside `.fb-stamp.manual`. `.fb-stamp.warn` is
  byte-identical to `.fb-stamp.manual` (`--color-warn-ink` with a
  `color-mix(in srgb, var(--color-warn) 40%, transparent)` border - the border mixes
  `--color-warn`, not `--color-warn-ink`), so give the two classes ONE shared rule with a
  one-line comment saying the sameness is deliberate rather than declaring the same body
  twice. `.fb-stamp.fail` is its own rule on `--color-fail-ink` with a `--color-fail`
  border. No literal colour, so `ComponentLayerGuardTests` stays green.
- [x] 3.4 Extend `tests/Freeboard.Web.Tests/MarkTagHelperTests.cs` in both directions: each
  tone maps to exactly one emitted class, and each variant class the stamp can emit
  (`manual`, `gen`, `warn`, `fail`) is reached by exactly one of the tone or the provenance
  flag - so a class the CSS defines cannot become unreachable and a tone cannot silently
  share a class. An untoned stamp still renders `fb-stamp gen`, an untoned manual stamp
  still renders `fb-stamp manual`, and the existing throw cases still hold.
- [x] 3.5 Add a `Components / Stamps` story under `src/Freeboard/stories/`, following
  `Badges.stories.js`, showing the provenance variants and the three tones.
- [x] 3.6 Update P2 in `src/Freeboard/stories/UxRules.mdx` to match the ratified wording:
  a value that references a named record held outside the system, shown with that record's
  own date, names that record instead of MANUAL, while content a person supplies stays
  MANUAL. The file is the visual reference for the rules, so leaving it stating the old
  rule makes the two disagree.

## 4. Web read surface

Commit: `feat(web): show vendor assurances on the register`

- [x] 4.1 Make the snapshot the request's one asset read, in
  `src/Freeboard/Authz/AuthzRequestCache.cs`: memoize `GetVendorAssuranceInputsAsync` there
  beside the existing asset memo, and serve `GetAssetsAsync` from the memoized snapshot's
  asset list. Then switch the three other consumers of the request's asset list from
  `IComplianceStore.GetAssetsAsync` to `AuthzRequestCache.GetAssetsAsync`, so none of them
  takes a second asset read: `OrgSelectionResolver` in `src/Freeboard/Web/OrgSelection.cs`,
  `src/Freeboard/Pages/Compliance/Collectors.cshtml.cs`, and
  `src/Freeboard/Pages/Compliance/IntegrationConnections.cshtml.cs`. The two page models
  already narrow a vendor id by the accessible set, so leaving them on their own read would
  make the rail's count on those pages narrow by the page's owner edges rather than the
  snapshot's - the same defect this task exists to remove. Each is one swapped call inside
  the try/catch it already sits in. `OrgSelectionResolver` reads nothing else from the
  store, so it takes the cache in place of `IComplianceStore`; the two page models keep the
  store for their own rows and take the cache as well. This is what
  makes the narrowing honest: the accessible set is
  memoized per principal per request and resolved from the FIRST asset list that reaches
  `IAssetAccess`, so unless every consumer reads one snapshot, the rail ends up narrowing
  its assurance rows with the page's owner edges. Say that in the class comment, and say
  that the two Statement of Applicability pages keep their own snapshot read - one shared
  read, `GetStatementOfApplicabilityDrilldownInputsAsync` - and so still seed the accessible
  set from their own asset list on their own pages.
- [x] 4.2 Add `AssuranceOptions` (`Freeboard:Assurance`, `WarnWindowDays` defaulting to
  90) under `src/Freeboard/Compliance/` and bind it in `Program.cs` with the plain
  `Configure<T>` every other options type here uses, matching `GitOpsOptions`. Do NOT add a
  validating bind: `AddOptions<>().Validate().ValidateOnStart()` appears nowhere in `src`,
  and a negative window needs no rejection because the `warnDays > 0` guard degrades it to
  no advance warning, which is the safe direction. Record in the doc comment why 90 days,
  and that a negative or zero configured window means no advance warning at all.
- [x] 4.3 Project each vendor's assurances onto the `/vendors` row in
  `src/Freeboard/Compliance/ComplianceEndpoints.cs` with `standard`, `expires`, and the
  status derived through `AssuranceOptions`, keeping the existing owner-narrowed
  `Where` unchanged. Read the assets and the assurances from the request's snapshot on
  `AuthzRequestCache` rather than from `IComplianceStore.GetAssetsAsync` plus a second read,
  so the owner edges that narrow the response and the rows being narrowed come from one
  snapshot. Take today from the registered `TimeProvider` (`timeProvider.GetUtcNow()`),
  never from `DateTime.UtcNow`, so a test can pin the date.
- [x] 4.4 Carry the assurances onto `VendorRow` in
  `src/Freeboard/Pages/Compliance/Vendors.cshtml.cs`: replace the page's
  `GetAssetsAsync` call with the request's snapshot from `AuthzRequestCache` and group the
  assurances by vendor, taking today from the injected `TimeProvider` as 4.3 does, with the
  standard's title resolved from `GetStandardsAsync` the way `OwnerTitle` is resolved today.
  The scopes read and the standards read stay separate reads. Delete the page's private
  `IsStoreFailure` copy and call `ComplianceEndpoints.IsStoreFailure` instead. Do the same
  in `Collectors.cshtml.cs` and `IntegrationConnections.cshtml.cs`, which 4.1 already
  opens - two lines each, so folding them in costs nothing. There are five private copies
  of the predicate beside the shared one; this leaves two, in `ControlDetail.cshtml.cs` and
  `StatementOfApplicability.cshtml.cs`, which this change does not open. Add the expiry wording helper as
  a private static, mirroring `DueTagHelper.Describe` with the certificate's verb (T6):
  `Expired` and `Expiring` are relative within a week and absolute beyond it, and `Valid`
  is ALWAYS absolute (`expires Mar 27`). Valid is not relative because with `warn_days: 0`
  an assurance expiring tomorrow is `Valid`, and a relative form there would read
  word-for-word like an `Expiring` one, leaving colour as the only difference (S2).
- [x] 4.5 Render the Assurance column in
  `src/Freeboard/Pages/Compliance/Vendors.cshtml` as one `<fb-stamp>` per assurance,
  the standard title in `source`, the expiry wording in `age`, toned neutral, warn, or
  fail. Keep "None on file" for a vendor with none. Do not change row order or add a
  control.
- [x] 4.6 Make the summary notice above the tabs take `notice-warning` and name the
  count when any readable vendor holds a lapsing assurance, keeping the neutral
  `notice` and its current wording when the count is zero.
- [x] 4.7 Extend `tests/Freeboard.Web.Tests/VendorsPageTests.cs`: the three toned
  stamps, "None on file" for a vendor with none, the amber notice and its count, and
  the neutral notice when nothing lapses, all against a fixed `TimeProvider` and fixed
  options. Add three named cases the general ones do not force:
  `ExpiredOnlyVendorTurnsTheNoticeAmber` (a readable vendor whose only assurance is
  `Expired`, so an expiring-only fixture cannot satisfy the case),
  `HiddenVendorsLapseIsNotCounted` (a lapsing vendor outside the accessible set leaves the
  notice neutral and out of the count), and `AnEnormousConfiguredWindowRendersRatherThanThrowing`
  (`WarnWindowDays` of `int.MaxValue`, and a vendor whose entry overrides it with the same,
  both render). Add `GetVendorAssuranceInputsAsync` to BOTH
  `IComplianceStore` doubles - `tests/Freeboard.Web.Tests/FakeComplianceStore.cs` and the
  `CountingComplianceStore` inside `tests/Freeboard.Web.Tests/OrgSelectionTests.cs` - or
  the test project stops compiling, and update `CountingComplianceStore` to count the
  snapshot read now that `OrgSelectionResolver` no longer calls `GetAssetsAsync`. Extend
  `ComplianceEndpointTests` for the API row, its derived status, and the absence of a hidden
  vendor's assurances from the response.
- [x] 4.8 Make `FakeComplianceStore.GetVendorAssuranceInputsAsync` throw under BOTH
  `Unreachable` and `AssetsUnreachable`, the way the two Statement of Applicability reads
  already do, and add it to the `AssetsUnreachable` doc comment, which names the
  asset-surfacing reads one by one. That comment is already stale: three reads throw under
  the flag and it names two, omitting `GetStatementOfApplicabilityDrilldownInputsAsync`. Name
  that one too, so the list matches the code. Without this the flag stops meaning what it says: after
  4.1 the cache reads the snapshot rather than `GetAssetsAsync`, so a fake that ignores the
  flag would leave `StatementOfApplicabilityPageTests.InputsLoadFailingAfterStandardsStillRendersNotice`
  passing while exercising no outage.
- [x] 4.9 Update the `Resolver` helper in `tests/Freeboard.Web.Tests/OrgSelectionTests.cs`,
  which constructs `new OrgSelectionResolver(accessor, store, access)`: it now builds an
  `AuthzRequestCache` over `FakeAuthzStore` and the fake compliance store, and passes that
  in place of the store. Mechanical, but every test in the file goes through it.
- [x] 4.10 Add a write-path test to
  `tests/Freeboard.Web.Tests/ComplianceWriteEndpointTests.cs` pinning what a gated write
  does when the snapshot read fails: `PUT /api/organisations/{id}` against an unreachable
  store answers 403, because the permission filter catches a throwing selector as a deny.
  This is the recorded failure behaviour for a deploy that reaches an unmigrated schema, so
  it needs a test rather than a paragraph.

## 5. Nav badge

Commit: `feat(web): badge the vendors nav item with lapsing assurances`

- [x] 5.1 Give `src/Freeboard/Navigation/ShellNavResolver.cs` `AuthzRequestCache`, the
  asset access seam, `TimeProvider`, and `IOptions<AssuranceOptions>`, and set the Vendors
  item's `Count` to the number of readable vendors holding an expiring or expired
  assurance, null when that number is zero, read from the request's memoized snapshot. The
  clock and the options are not optional extras: `Expired` needs today and `Expiring` needs
  both today and the effective window, resolved as 4.3 resolves them
  (`timeProvider.GetUtcNow()`, and `entry.WarnDays ?? options.WarnWindowDays`), so the
  badge and the page agree. Take the cache rather than `IComplianceStore`: the
  snapshot lives there (4.1), and reading the store directly would take a second one and
  reopen the straddle. Memoize the resolved count on the instance: the
  resolver is registered scoped and `ResolveAsync` is called up to three times per render
  (`ShellRailViewComponent`, `ShellPaletteViewComponent`, and
  `ShellTopbarViewComponent.GroupHrefAsync`, all reached from `_Layout.cshtml`), so
  without a memo an ordinary page counts three times. Memoize the failed
  result too, so an outage costs one attempt per request. Replace the "no actionable-count
  source exists yet" comment with what the count is, why a vendor with no assurance is
  never counted, and why the count reads the request's snapshot rather than taking its own.
- [x] 5.2 Compute the count inside a store-failure catch so an unreachable store yields
  no badge rather than failing every page in the app. Use the existing
  `ComplianceEndpoints.IsStoreFailure` predicate (`OrgSelection.IsStoreFailure` already
  delegates to it) rather than writing a third copy of it.
- [x] 5.3 Update `tests/Freeboard.Web.Tests/ShellNavCatalogTests.cs`: replace the
  badges-nothing test with one that badges the owner-narrowed count (two vendors lapsing,
  one accessible, badge reads `1`), one where the readable vendor's only assurance is
  `Expired` and the badge still reads `1` (an expiring-only fixture cannot satisfy it),
  one that leaves the item unbadged when nothing lapses, one that leaves it unbadged when
  the store throws, one that resolves the nav three times against a THROWING store and
  asserts the store was read once (so the memoized failure is covered, not only the
  memoized success), one that resolves the nav three times against a working store and
  asserts the same, and confirm every other item still badges nothing. All against a fixed
  `TimeProvider` and fixed options, as 4.7 does: a fixture dated against the wall clock
  changes state as the wall clock moves, and the `Expired`-only case and the
  nothing-lapses case are the two that flip silently. Rewrite the class
  doc comment too: it says the resolver "never badges (no count source yet, N6)", which
  this change makes false.
- [x] 5.4 Add a test that pins the shared snapshot: read the snapshot through
  `AuthzRequestCache` from one component, then resolve the nav in the same scope, and assert
  the store was read once and that the count was narrowed by an accessible set resolved from
  that snapshot's asset rows, again against a fixed `TimeProvider` and fixed options. This
  is the regression that catches a later change reverting
  either the rail or the register to its own read.
- [x] 5.5 Extend
  `tests/Freeboard.WebE2E/AuthzIsolationE2ETests.CallerOutsideAVendorOwnerSubtreeSeesNeitherTheVendorNorItsScopes`:
  give both vendors an expiring assurance, keep the existing whole-document negative
  assertions, and add that neither the hidden vendor's id nor its assurance text appears
  anywhere in the document while the rail badge counts the visible vendor only. This is
  the end-to-end pin on the owner-narrowing guarantee for the new count.

## 6. CLI parity

Commit: `feat(cli): print vendor assurances in vendor list`

- [x] 6.1 Add an `ApiAssurance(Standard, Expires, Status)` record and an `Assurances`
  list to `ApiVendor` in `src/Freeboard.CLI/IFreeboardApiClient.cs`, and read them in
  `ReadVendor` in `src/Freeboard.CLI/HttpFreeboardApiClient.cs`, tolerating an absent
  property.
- [x] 6.2 Print each assurance as an indented line under its vendor in
  `src/Freeboard.CLI/VendorCommands.cs`, above the scope lines, carrying the standard,
  the expiry, and the status. Leave the vendor line, the scope lines, and the exit
  codes untouched.
- [x] 6.3 Extend `tests/Freeboard.CLI.Tests/VendorCommandTests.cs`: the assurance lines
  print with their status, and a vendor with none prints no assurance line.

## 7. Documentation and examples

Commit: `docs(gitops): document vendor assurances`

- [x] 7.1 Add an assurances subsection to the `Asset` section of `docs/gitops.md`: the
  fields, an example document, the six errors an author can hit (the five validator rules
  and the loader's unknown-field diagnostic), that a referenced standard
  must be declared, that an already-passed expiry is not a validation error, and how
  the warning window and `warn_days` decide the rendered state - including that
  `warn_days: 0` gives no advance notice at all. Update the separate `Vendor asset`
  section too: it lists the vendor's optional fields and carries its own
  `tier`/`data_classes` example, so leaving it alone makes it read as the complete list.
- [x] 7.2 Update the Persistence `Schema` section of `docs/gitops.md` for
  `vendor_assurances`, its composite key, its two `RESTRICT` foreign keys, and its
  `warn_days` check. It is NOT a domain table: that sentence defines the seven as each
  keyed on `id` with `api_version`, `title`, `created_at`, and `updated_at`, and
  `vendor_assurances` has none of the first three. Leave the count at seven. Correct the
  relation-table sentence instead, which says "One relation table (`control_requirements`)"
  with `ON DELETE CASCADE` foreign keys: there are now two, and the new one is `RESTRICT`,
  so the CASCADE claim has to stop covering both.
- [x] 7.3 Update the `/vendors` bullet under `Web read endpoints` in `docs/gitops.md`
  for the assurance fields and the derived status.
- [x] 7.4 Document `Freeboard:Assurance:WarnWindowDays` in `docs/gitops.md`. The only
  section listing the web app's config keys is `## Read-only (GitOps) mode`, which opens
  "The web app reads two config keys" - a heading that does not cover this key, and a
  sentence the addition would falsify. So either give the key its own config section, or
  widen that heading and rewrite its lead sentence to cover all three keys. Document: its type, its default of 90, that it is the window an entry
  with no `warn_days` uses, that a per-entry `warn_days` overrides it, and that a value of
  zero - or a negative one, which the bind does not reject and the status rule treats as
  zero - means no advance notice at all. It is the only operator-tunable knob this change
  adds.
- [x] 7.5 Give vendors in `examples/gitops/vendors.yaml` real assurances (`std-soc2` is
  already declared; declare any other standard the examples reference), keeping at
  least one vendor with none. Do the same for `examples/fixture-corp`, adding the
  standards its vendors' certifications name. Update either example README layout table
  whose files or kinds change.
- [x] 7.6 Update every tab of
  `src/Freeboard.Web/Content/Docs/resources/vendors.md` - it has three tabbed blocks (add a
  vendor, record an exception, read the register), each carrying UI, GitOps, and API, and
  all three need the change - including its field reference
  and validation-rules sections, so the described columns and rules match what the page
  now renders.

## 8. Verification

- [x] 8.1 `dotnet build`.
- [x] 8.2 `dotnet test` with no external services: Core, CLI, and web tests pass and the
  gated suites skip cleanly.
- [x] 8.3 Bring up the test MySQL, export `FREEBOARD_TEST_DB`, and run
  `dotnet test tests/Freeboard.Persistence.Tests` so the migration, the removal and
  ordering cases, and the snapshot read actually execute.
- [x] 8.4 Run `freeboard gitops validate` against `examples/gitops` and
  `examples/fixture-corp`, confirming exit `0`.
- [x] 8.5 `npx markdownlint-cli2 "**/*.md"` over the changed markdown.
- [x] 8.6 Open `/compliance/vendors` against a synced example config and confirm the
  three stamp states, the amber notice, and the nav badge, in both themes.
- [x] 8.7 Run the gated browser suite for 5.5: build `tests/Freeboard.WebE2E`, install
  Chromium, export `FREEBOARD_TEST_E2E=1`, and run `dotnet test tests/Freeboard.WebE2E`.
