## Context

The vendor register is the first page on the new `fb-*` design system. Its Directory
table has eight columns, and only three carry real data: vendor title, vendor id, and
owner organisation title. Tier and Data render the explicit empty "Not tracked",
because no field holds either fact.

The object model is the post-v2 one: `Company`, `Department`, `Machine`, and `Vendor`
are all rows in one `assets` table, and a declared asset is authored as `kind: Asset`
in gitops YAML. Git is the only write path. A vendor reaches the page through
`ConfigLoader` -> `ConfigValidator` -> `MySqlGitOpsImporter` -> the MySQL asset row ->
the compliance store -> the page model, and reaches the CLI through the same store via
`GET /api/v1/freeboard/vendors`.

Constraints that shape the design:

- Core is MIT and dependency-free. `ConfigValidator` is pure and offline: the CLI runs
  `freeboard config validate` with no database, so validation cannot consult stored
  state.
- Read-model parity is an acceptance rule. Every read model ships to web and CLI in the
  same change.
- An admin-editable data class taxonomy is a planned enterprise feature. Core cannot
  reference `Freeboard.Enterprise`, so nothing here may assume that editor exists.

## Goals / Non-Goals

**Goals:**

- Author a vendor's tier and the classes of data it holds in gitops YAML.
- Validate both closed vocabularies at load, offline, with the failure modes an author
  actually makes reported as errors.
- Persist both on the asset row, and clear them when the author removes the key.
- Render both on the register, return both from the vendors API, and print both from
  the CLI, in one change.

**Non-Goals:**

- Filtering, grouping, or sorting by either field. Sort stays vendor id.
- Any behavior that reads either field: review cadence, status, scoring.
- A taxonomy table or an admin-editable class set.
- Either field on a non-Vendor asset.
- Distinguishing "assessed, holds nothing" from "not assessed".

## Decisions

### Two fields on `Asset`, not a new kind

`tier` and `data_classes` are attributes of a vendor, not relationships. A `VendorProfile`
kind would need its own id, its own reference to the vendor, its own validation phase,
and its own table, to hold two scalars that have no lifecycle of their own. They go on
the `Asset` document, next to `owner`.

The names stay generic. `vendor_tier` and `vendor_data_classes` would close the door on
widening the fields to machines later, and widening a restriction is backward
compatible while a rename is not. Validation, not the name, is what confines them to
Vendor today.

### Casing follows the shipped examples

A closed domain set is PascalCase in this config format (`severity: Hard`,
`disposition: In`), so `tier: Critical`. Data class tokens are lowercase slugs
(`payment-card`), because they become admin-editable ids later and an id authored by an
admin should not carry a casing convention the UI then has to preserve. The UI renders a
display label either way.

### `VendorTier` is an enum; a data class is a string

`VendorTier` is a permanent closed set of four values, in `Freeboard.Core/Assets/` next
to `AssetKind`. Nothing will add a fifth without a code change anyway.

Data classes stay `string` end to end, validated against one `static readonly` set in
Core. An enum cannot hold a value it was not compiled with, and the enterprise editor
that lets an admin extend the set is planned, so an enum would force every layer -
loader, validator, importer, API row, page model, CLI - to change type on the day that
ships. Nothing switches on a data class today, so the exhaustiveness an enum buys costs
nothing to give up. Anything that switches on one later is EE code, which cannot
reference a Core enum in the first place.

Alternative considered: a taxonomy table now, with a foreign key from the vendor's
classes. Rejected as speculative. Core's validator is pure and DB-free, so it could not
consult the table, and moving the check to sync time is exactly the question the
enterprise feature has to answer. Shipping the table now answers it under guesswork.

### Storage: two columns on `assets`, one JSON

`tier VARCHAR(16) NULL` and `data_classes JSON NULL`.

`assets` is already wide and sparse with type-specific nullable columns: `hostname`,
`identity_kind`, `identity_value`, `state`, `first_seen_at`, `last_seen_at`, and
`retired_at` are Machine-only. Two Vendor-only columns match that shape.

The repo's precedent for an authored list is a JSON column: a collector's `checks`, an
attestation template's `fields` and `quiz`. A child table was considered for the future
taxonomy foreign key and rejected: the register filters in C# over the whole asset set,
not in SQL, so an index nothing queries does not pay for the join, the second store, and
the second prune path.

A generic `attributes JSON` bag was rejected too. `tier` would stop being visible to any
operator tool or ad-hoc query, and the facets actually queued behind this one - reviews,
assurance - are records with owners and expiries that want their own tables, so the bag
would collect nothing else.

### Removing a key clears the column

`UpsertAssetsAsync` in `MySqlGitOpsImporter` lists its columns explicitly and assigns
`owner = VALUES(owner)` on duplicate key, so dropping `owner` from YAML nulls the
column. Both new columns extend that statement the same way. This is the one silent
failure mode in the change: an omitted column in the `ON DUPLICATE KEY UPDATE` list
leaves a stale value that no test would notice, so a gated integration test pins the
clear-on-removal path specifically, not just the write path.

### Validation: four errors, one warning, one silence

Errors, because each is an authoring mistake with no sensible interpretation:

1. An unknown `tier` token.
2. An unknown `data_classes` token.
3. Either field on an asset that is not a Vendor.
4. A duplicate entry in `data_classes`. Silent deduping hides a mistake, and this
   matches how duplicate check names and duplicate requirement ids already behave.

A Vendor with no `tier` is a non-blocking `Warning`. A declared Vendor with no `owner`
only warns, and `owner` is the edge that decides whether anyone can see the vendor at
all. A field that colors a tag must not fail a sync when the edge that gates visibility
does not.

An absent or empty `data_classes` produces no diagnostic. A vendor that holds none of
your data is a real state, and the shipped example config already has one.

`data_classes: []` means exactly what omitting the key means. A tri-state (unassessed /
assessed-as-none / populated) would thread a nullable through the YAML bind, the column,
the API row, the page model, and the CLI to buy two empty-cell strings a reader cannot
tell apart, and it assumes authors reliably distinguish "not checked" from "checked,
none", which they will not. "Assessed and holds nothing" is a fact with an owner and a
date, so it belongs to the vendor review record, not to this field.

### Vocabulary is defined by regime, not by data type

`pii`, `phi`, `special-category`, `payment-card`, `credentials`.

`phi` means HIPAA-regulated. `special-category` means UK/EU GDPR Article 9. Health data
is both, and setting both is correct rather than a mistake. The docs must say this
explicitly or authors will guess, and a validator cannot catch a wrong guess here. `phi`
is carried now because HIPAA is a target standard later.

The prototype's `production` and `internal` labels are dropped. They classify what
systems a vendor touches, not what data it holds, and blast radius is what `tier`
already says. Two fields answering one question is the duplication `code-as-liability.md`
says to consolidate. If the access axis earns its place it arrives as its own field with
its own consumer.

### Rendering: every tag is neutral

All four tier tags and all five data class tags are `MarkTone.Neutral`.

S3 reserves red for failing and overdue. Spending it on a static attribute leaves the
genuinely failing vendors arriving with the review model no color to use. Amber was
considered for `Critical` and dropped for the same reason. S2 already requires the word
to carry the meaning, and it does.

Data classes are facts, not severities. `pii` is not worse than `payment-card`, so
toning them would imply a ranking that does not exist.

An absent value keeps rendering "Not tracked" (O2, S6). Sort stays vendor id: tier is
editable config, so a tier sort would reshuffle the register whenever someone re-tiers a
vendor, and L1 wants the default order driven by failing and due-soon rows, which do not
exist yet.

### Parity: the vendors endpoint grows, the CLI prints inline

`GET /api/v1/freeboard/vendors` returns `{id, title}` today and there is no generic
`/assets` endpoint, so adding both fields is additive to a vendor-specific projection -
which is the intended shape, the API mirroring the view.

The CLI prints both on the existing vendor line, `-` when absent, matching the `?? "-"`
idiom already in `VendorCommands`. A `--json` mode is wanted but is its own change
across every read command, and it should emit the CLI's joined view rather than a
passthrough of the API row.

## Risks / Trade-offs

- **An omitted column in `ON DUPLICATE KEY UPDATE` leaves a stale tier after the author
  removes it.** Silent, and no unit test sees it. -> A `FREEBOARD_TEST_DB`-gated
  integration test syncs a vendor with both fields, syncs again with both keys removed,
  and asserts both columns are null.
- **The Core class set and `docs/gitops.md` drift.** No test guards it, because the docs
  are moving into `Freeboard.Web` soon and a guard written now would move with them. ->
  Accepted for this change. The set has five members and one home in Core.
- **The regime-based vocabulary is easy to author wrongly.** Health data belongs in both
  `phi` and `special-category`, and a validator cannot detect a missing one. -> The docs
  state the overlap explicitly with a worked example. Nothing reads the field yet, so a
  wrong classification has no downstream effect until the review model lands.
- **`data_classes` as JSON is not queryable by index.** -> Deliberate. The register
  filters in C# over the full asset set. If a filter ever needs SQL, MySQL supports a
  generated column with an index over the JSON without a schema rewrite.
- **Widening the fields to Machine later needs a validation change and a docs change.**
  -> The names are already generic, so it is a restriction to relax, not a rename. That
  is the cheap direction.

## Migration Plan

One forward-only migration adds two nullable columns to `assets`. It carries no data
migration, no backfill, and no foreign key. Every existing row keeps a null tier and a
null class list, which the page already renders as "Not tracked".

Deploy order is the established one: `freeboard system migrate`, then the app. An older
app against the migrated schema ignores two columns it does not select, so the migration
is safe to apply ahead of the deploy.

Rollback: the migration is forward-only by repo convention. Two additive nullable
columns need no down path - an older build ignores them. Restore-and-replay stays the
recovery route for a failed apply, as for every other migration here.

## Open Questions

None. The vocabulary, casing, storage shape, validation severities, and rendering tone
were resolved before this proposal. The next decisions - review cadence keyed on tier,
grouping by tier, the admin-editable taxonomy - belong to the changes that introduce
their consumers.
