## Context

The vendor register is the first page on the `fb-*` design system. Its Directory table
has eight columns. After the tier and data-class work, five carry real data: the vendor
(its title and id in one column), tier, data classes, the owner organisation's title,
and the exception count. Assurance renders
the hardcoded literal `<span class="fb-tdsub">None on file</span>` at
`src/Freeboard/Pages/Compliance/Vendors.cshtml:142`, because nothing feeds it.

The object model is the post-v2 one: `Company`, `Department`, `Machine`, and `Vendor`
are rows in one `assets` table, and a declared asset is authored as `kind: Asset` in
gitops YAML. Git is the only write path. A vendor reaches the page through
`ConfigLoader` -> `ConfigValidator` -> `MySqlGitOpsImporter` -> the asset row ->
`MySqlComplianceStore` -> the page model, and reaches the CLI through the same store via
`GET /api/v1/freeboard/vendors`. This change follows that path exactly.

Constraints that shape the design:

- `Freeboard.Core` is MIT and dependency-free. `ConfigValidator` is pure, offline, and
  clock-free: `freeboard gitops validate` runs it with no database.
- Read-model parity is an acceptance rule. A read model ships to web and CLI together.
- Owner narrowing is not a SQL predicate. `MySqlComplianceStore.GetAssetsAsync` runs one
  unfiltered `SELECT ... FROM assets ORDER BY id`, and the caller's accessible set is
  computed in the web layer by `IAssetAccess.AccessibleAssetIdsAsync(user, assets, ct)`.
  Every narrowed vendor read filters the loaded list in C#. That seam's contract requires
  the UNFILTERED asset list, and its answer is memoized: `AuthzRequestCache` holds one
  accessible set per principal per request and serves every later call from the FIRST
  caller's result, which `AuthzAssetAccess` resolved from the asset list that first caller
  passed. A caller therefore cannot narrow before it calls, and it cannot assume the set it
  gets back was derived from the asset list it just supplied.
- The web app already resolves the clock through `TimeProvider`, registered as a singleton
  in `Program.cs` and injected into the collector scheduler and the evidence store. Any new
  "today" comes from there, not from a direct `DateTime.UtcNow`.
- The importer runs the whole sync in one transaction and hard-removes rows absent from
  config in a foreign-key-safe order.

## Goals / Non-Goals

**Goals:**

- Author which certifications a vendor holds and when each expires, in gitops YAML.
- Validate the reference and the date offline, with the mistakes an author actually
  makes reported as errors.
- Persist the entries in their own table, and remove a row when its entry leaves config.
- Derive `Valid` / `Expiring` / `Expired` from the clock, never from a stored column.
- Warn before a certification lapses in three places: the cell, the page notice, and the
  nav rail.
- Render the state on the register, return it from the vendors API, and print it from
  the CLI, in one change.

**Non-Goals:**

- Any consumer of an assurance: review skipping, cadence, scoping, scoring.
- The vendor review model. Status and Next review stay empty.
- Evidence of the certification: no report, auditor, scope statement, or link.
- An authored status, or a second status vocabulary before the review model defines the
  first one.
- Filtering, grouping, or sorting by assurance state.
- Assurances on any asset type other than `Vendor`.

## Decisions

### Nested on the vendor, not a top-level kind

A certification is a property of the vendor, not a thing that points at one. A
`kind: Assurance` document would need its own id, its own `vendor` reference, its own
duplicate-id rule, and its own place in the sync order, to carry three fields with no
lifecycle of their own. It goes on the `Asset` document, next to `tier`.

Alternative considered: a JSON column on `assets`, matching `data_classes`. Rejected,
and the tier work already said why: an assurance is a record with an expiry, and an
expiry buried in JSON is neither joinable to `standards` nor readable by an operator
tool. The two-part key is also the uniqueness rule, which JSON cannot express.

### Identity is `(vendor_id, standard_id)`, with no id column

The row is not addressable: no API route, no scope target, nothing references it. A
synthetic id would be a value that appears nowhere in config and that a reader could
mistake for an authored one. The composite primary key carries the duplicate rule for
free, matching the duplicate `data_classes` check.

### `standard` is a hard reference to a declared `Standard`

This is the point of the feature. "A SOC 2 certified vendor already answers most of what
we would ask an organisation seeking SOC 2" becomes a join on a standard id with no
mapping layer. A closed token set in Core would name standards in a second place and
still need that mapping built later.

A dangling `standard` is an `Error`, matching `Scope.standard`, `Scope.requirement`, and
`Scope.control`, which keep referential integrity while the dangling-tolerated scalar
edges (`parent`, `owner`, `Scope.subject`) only warn. The distinction the format already
draws is between an edge to a thing another writer owns (tolerated) and a reference to a
document in the same config (hard). An assurance's standard is the second kind.

The cost is real and accepted: recording a vendor's ISO 27001 means declaring ISO 27001
as a `Standard` document even when the organisation pursues none of its requirements. A
`Standard` with no `Requirement` documents is already valid, so the cost is one short
document with a `version` and an `authority`.

### Status is derived, never stored

`AssuranceStatus` is an enum in `src/Freeboard.Core/Assets/`, next to `VendorTier`, with
a pure evaluation beside it. It takes the current date as a parameter rather than
introducing a clock abstraction into Core, which is the shape
`CollectorFrequency.IsStale(collectedAtUtc, frequency, nowUtc)` already uses, and it is
unit-testable with no database and no clock.

The predicate, stated exactly because a boundary bug here is silent:

- `expires < today` is `Expired`. A certificate is valid through its expiry date.
- otherwise `warnDays > 0 && expires.DayNumber - today.DayNumber <= warnDays` is
  `Expiring`.
- otherwise `Valid`.

The window is compared as a difference of day numbers rather than as
`expires <= today.AddDays(warnDays)`. The two forms agree on every value the second one can
evaluate, but `DateOnly.AddDays` throws once `today + warnDays` leaves the supported date
range, and `warnDays` reaches Core as an unbounded `int`: the per-entry override is only
validated as a whole number of zero or more, and the deployment default is bound with no
validation at all. Subtracting two `DayNumber` values cannot overflow, so a `warn_days` of
`int.MaxValue` reads `Expiring` instead of throwing out of the page, the API and the rail.
That is smaller than capping the value, and it removes the failure rather than choosing a
ceiling nobody can justify.

The `warnDays > 0` guard is what makes `warn_days: 0` mean "warn only once expired". Drop
it and a zero window still warns for one day, on the expiry date itself, which is a day
early and contradicts the authored meaning of the value. The two boundaries the guard
decides are `expires == today` (`Valid` when the window is zero, `Expiring` when it is
not) and a window whose last day is exactly `expires` (the last `Expiring` day).

`today` comes from the injected `TimeProvider` as a UTC date, so a state flips at UTC
midnight and a test can pin the date without waiting for one. `warnDays` is resolved by
the caller as `entry.WarnDays ?? options.WarnWindowDays`, so Core needs no configuration.

Nothing clamps that value. Config validation rejects a negative `warn_days` offline, so no
authored entry reaches Core with one, and the `warnDays > 0` guard makes a negative value
behave exactly as zero. A clamp would be a third guard whose only effect is to hide a
caller bug. The deployment default is a different matter and is stated plainly rather than
guarded: `AssuranceOptions` binds with the plain `Configure<T>` every other options type in
this app uses, which validates nothing, so a negative `Freeboard:Assurance:WarnWindowDays`
binds silently and degrades to no advance warning at all - the same behaviour as zero. That
is the safe direction (an operator loses notice rather than gaining a false one), it is
documented with the key, and adding `AddOptions<>().Validate().ValidateOnStart()` here would
introduce a validation pattern the app does not use anywhere for one setting whose worst
case is silence. The `CHECK` on the column is the one remaining backstop, and it exists for
a writer that does not exist yet rather than for this path.

A stored status column was rejected: it goes stale the moment the clock passes it, so a
vendor would read `Valid` until somebody happened to run a sync. An authored
`status: Certified` was rejected too: two facts that can contradict each other, and a
second status vocabulary before the review model defines the first one.

### The warning window is an option with a per-entry override

`AssuranceOptions.WarnWindowDays` binds from the `Freeboard:Assurance` configuration
section and defaults to 90, matching the `IOptions` pattern of `GitOpsOptions`,
`SchedulerOptions`, and `WebAuthOptions`. Ninety days because an annual certification
needs about a quarter's notice to book a renewal audit and get a report issued, so a
shorter window warns after the outcome is already decided. An entry overrides it with
`warn_days`, which is what a monthly-renewed or a slow-to-audit scheme needs. Both
halves become user-editable settings when the non-GitOps write path lands.

The option lives in the web app because the web app is the only process that derives the
status. The CLI reads the derived status off the API row, so the window lives in one
process and the two surfaces cannot disagree.

### Validation: six errors, no warning, no clock

Six errors, because each is an authoring mistake with no sensible interpretation. Five are
`ConfigValidator` rules: an unknown or dangling `standard`; a missing or unparseable
`expires`; a `warn_days` below zero; two entries naming the same standard on one vendor;
and `assurances` on a non-Vendor asset. The sixth, an unknown field on an entry, is a
`ConfigLoader` diagnostic rather than a validator rule, because it has to read the authored
YAML node - see the nested unknown-field paragraph below.

`expires` is required. An assurance with no expiry cannot be warned on, which is the
whole feature, so there is nothing to default it to.

An `expires` already in the past is not a diagnostic. The validator is pure and
clock-free by rule, so a time-dependent check would make `validate` output depend on
when it ran, and two runs of the same config would disagree. Expired is a real state the
page renders in red.

A vendor with no `assurances` is silent, matching an absent `data_classes`. Holding no
certification is legitimate and common - the example config's accountancy firm and
managed VPN appliance plainly have none - and a warning that fires on the normal case
teaches people to ignore sync output.

The nested unknown-field check follows the one precedent for a nested authored block:
`ConfigLoader.ReportUnknownConfigKeys` diffs the collector's `config` mapping against a
registered key set on the authored YAML node rather than on the bound object, because an
absent key and a key with an empty value bind identically. The assurance check does the
same over each sequence entry.

Two degenerate inputs need explicit handling, because `IgnoreUnmatchedProperties()` makes
both silent otherwise. An explicit-null `assurances:` normalizes to an empty list. A null
sequence item is KEPT as an empty entry, so the validator reports its missing `standard`
and `expires` rather than the loader throwing or the entry vanishing.

### Storage: one child table, replaced whole on each sync

```text
vendor_assurances(vendor_id, standard_id, expires DATE, warn_days INT NULL, created_at, updated_at)
PRIMARY KEY (vendor_id, standard_id)
FOREIGN KEY (vendor_id)   REFERENCES assets (id)    ON DELETE RESTRICT
FOREIGN KEY (standard_id) REFERENCES standards (id) ON DELETE RESTRICT
CHECK (warn_days IS NULL OR warn_days >= 0)
```

The name follows the schema's own conventions rather than the shorter one. Every child
table is named for its parent (`vendor_scopes`, `control_requirements`, `control_standards`,
`scope_controls`, `requirement_scopes`, `collector_credentials`), and every foreign-key
column in the schema ends in `_id` (`vendor_id`, `standard_id`, `control_id`,
`requirement_id`). A bare `assurances` table with a bare `standard` column would be the
only reference column in the database without the suffix, and would read as a scalar
authored token rather than a foreign key. The row is vendor-specific, so the prefix is
accurate as well as conventional.

Both foreign keys are `RESTRICT`, matching every other importer-pruned reference, so the
sync must clear a vendor's assurance rows before deleting the vendor and before deleting
a standard.

The `CHECK` on `warn_days` is a backstop, not the primary guard: config validation rejects
a negative value offline, and this change ships no other writer. It costs one line and it
keeps the column's domain true if a later write path forgets the rule, which is the same
reason the schema constrains rather than trusts elsewhere.

Importing a nested list into a child table needs no new machinery.
`MySqlGitOpsImporter` already replaces two whole child sets each sync -
`ReplaceScopesAsync` runs `DELETE FROM scopes;` then inserts, and
`ReplaceControlRequirementsAsync` does the same for `control_requirements` - and both run
before the prune block for exactly this foreign-key reason. `ReplaceAssurancesAsync` is
one more call in that sequence, in the same shape. Whole-set replacement is also strictly
safer than a per-vendor replace: it cannot leave rows behind for a vendor that left config
entirely, and it subsumes the absent-row prune.

The position in the transaction is not a detail. The importer's numbered sequence is:
upsert the domain rows (standards, requirements, controls, assets, connections,
collectors); replace the whole scope set; replace the control-to-requirement rows; prune
org role assignments; then hard-remove absent collectors, absent connections, the
source-guarded declared assets, controls, requirements, and standards.
`ReplaceAssurancesAsync` goes immediately after `ReplaceControlRequirementsAsync` and
before the first delete of the prune block. Both `RESTRICT` keys are then covered by one
placement, because the prune block already deletes the declared assets before the
standards: the replace must precede the declared-asset prune, which itself precedes the
standard delete, so a single position before the block satisfies the vendor key and the
standard key together. Stating it as one constraint rather than two is what keeps a later
reader from moving the call down into the block and finding that it still passes the
standard test.

A per-parent replace (`DELETE FROM vendor_assurances WHERE vendor_id = ...` per vendor)
was considered because `MySqlAuthzAdministrationStore` uses that shape for role
permissions. Rejected: it is one statement per vendor instead of one, and it needs a
second pass to catch rows whose vendor is gone. The whole-set replace is smaller and
covers both.

### No index on `expires`

The count that drives the notice and the badge is computed in application code (see the
next decision), so nothing queries `expires` in SQL. This follows the tier migration's
own reasoning: an index nothing queries does not pay for itself. If a filter later needs
SQL, adding the index is a one-line migration.

### The count is computed in C# from the narrowed read

A dedicated indexed query for the badge count - a store method taking the caller's visible
vendor ids, the date, and the default window, and answering with one SQL `COUNT` over an
index on `expires` - was weighed against a plain C# aggregate and lost on two counts.

It does not save the work it appears to save. The visible vendor ids are themselves the
output of `IAssetAccess.AccessibleAssetIdsAsync`, whose contract requires the unfiltered
asset list and memoizes its first answer for the rest of the request. Any caller of an
id-taking count must therefore load every asset first. The SQL count avoids only the
assurance read, and pays for it with a second round trip and a widening `IN` list.

It duplicates the boundary. The status rule is a Core function with a `warnDays > 0`
guard and two exact edges; a SQL count has to restate that predicate in a second language,
including the per-row `warn_days` override falling back to the deployment default. Two
implementations of one silent boundary is the failure the Core evaluator exists to
prevent, and the divergence would show up as a badge that disagrees with the page it
points at.

So the count is: read the assets and the assurances in one snapshot (see the next
decision), narrow the assets by the caller's accessible set, and count the vendors
holding at least one `Expiring` or `Expired` assurance.

**The owner-narrowing guarantee.** The badge, the notice, the page cell, and the API row
are all derived from one narrowed set: the vendors whose id is in
`IAssetAccess.AccessibleAssetIdsAsync`'s answer for this caller. No surface applies a
second narrowing rule, and no surface reads a vendor the page would not show. A vendor
outside that set contributes nothing to the count and nothing to the document - its row is
absent rather than redacted, so neither its id nor its assurance text can appear.

Four tests pin it, one per surface:

- `ShellNavCatalogTests` - two vendors hold a lapsing assurance, one is accessible, the
  badge reads `1`.
- `VendorsPageTests` - a lapsing vendor outside the accessible set leaves the notice
  neutral and out of its count, so the notice's narrowing is pinned without the browser.
- `ComplianceEndpointTests` - a hidden vendor's row and its assurances are absent from
  `GET /api/v1/freeboard/vendors`.
- `AuthzIsolationE2ETests.CallerOutsideAVendorOwnerSubtreeSeesNeitherTheVendorNorItsScopes` -
  extended so the hidden vendor holds an expiring assurance, asserting over the whole
  document (not the visible text) that neither the vendor id nor its assurance text
  appears, and that the rail badge counts the visible vendor only.

Vendors with no assurance are never counted. A badge that is permanently non-zero stops
being read, which is what N6 exists to prevent.

### One snapshot per request, and it is the request's asset read

The narrowing decision and the rows it narrows must come from one view of the database.
`GetAssetsAsync` is an autocommit read, so a second autocommit read of the assurances can
land on the far side of a `sync` commit and pair the old asset list - and therefore the
old `owner` edges that decide what the caller may read - with new assurance rows. That
combination never existed, and it can surface through the badge, the API, or the page,
which is exactly what the owner-narrowing guarantee above claims cannot happen.

So the store exposes the pair as one snapshot, `GetVendorAssuranceInputsAsync`, carrying
the unified asset list and the whole assurance set read inside one repeatable-read
transaction. That is the shape the store already uses where straddling matters: the two
Statement of Applicability input snapshots exist for the same reason and are specified in
the same terms. There is no standalone `GetVendorAssurancesAsync`: a lone assurance read
has no honest caller, since every caller has to narrow the rows it gets back.

One transaction is necessary but not sufficient, because the accessible set is memoized.
`AuthzRequestCache.AccessibleAssetIdsAsync` keeps one set per principal per request and
returns the first caller's answer, which `AuthzAssetAccess` resolved from the asset list
that first caller passed. So a component that takes its own snapshot does not necessarily
narrow by its own asset rows - it narrows by whichever asset list reached the seam first.
Two components each taking a snapshot would be the same straddle moved one level up: on
`/compliance/vendors` the page handler runs before the layout, so the page would seed the
memo, and the rail would then narrow ITS assurance rows with the PAGE's owner edges.

The snapshot is therefore taken once per request, not once per component, and it is the
request's asset read. `AuthzRequestCache` memoizes it and serves `GetAssetsAsync` from it.
The cache already owns the request's asset view and the accessible set derived from that
view; it now owns the read both come from, which is the smallest place to put it. The
register page, the vendors endpoint and `ShellNavResolver` take the snapshot from the
cache. `OrgSelectionResolver`, the collector register and the integration-connection
register take their assets from the cache instead of calling the store themselves. Every
one of them then narrows by an accessible set resolved from the same asset rows the
snapshot carries, whichever of them ran first.

The rejected alternative is a separate request-scoped snapshot holder, with
`AuthzRequestCache.GetAssetsAsync` serving its assets from that holder. It keeps a
compliance-feature table out of a type named for authorization, which is the whole of its
appeal. It loses because it does not change what it appears to change. The cache's assets
still come from the snapshot either way, so every authorization gate still reads
`vendor_assurances` and the blast radius recorded under Risks is identical. Only the type
holding the read moves. The one shape that would shrink that blast radius is the cache
keeping an asset read of its own, which is the two-view straddle this decision exists to
close. The real choice is one memo on an existing type against two request-scoped types
with the same failure surface, and the smaller one wins.

The read count moves in two directions, not one. `/compliance/vendors` takes two asset
reads today - the page's and `OrgSelectionResolver`'s - and takes one snapshot after this
change, which returns the assurances alongside the assets. Every authenticated page THAT
DOES NOT READ ASSETS ITSELF pays more rather than less. Today it issues one autocommit
`SELECT` for the layout selector. After this change it issues the snapshot, which is a
repeatable-read transaction of begin, two selects, and commit. So what such a page newly
carries is a second query as well as the assurance rows, and `vendor_assurances` holds one
row per certification on file.

The pages that do read assets themselves are roughly cost-neutral instead, because they
already pay two asset reads: their own and the layout selector's. That is the collector
register, the integration-connection register, and both Statement of Applicability pages.
The first two collapse to the one snapshot under 4.1. The Statement of Applicability pages
keep their own snapshot read, so they stay at two.

Two reads stay outside the snapshot, for two different reasons, and the criterion is NOT
that they take no part in narrowing.

- Standard titles are a shared reference label. An unresolvable title already renders as
  the standard id, so a title read from the far side of a commit costs a label rather than
  a narrowing decision.
- The unified scopes ARE narrowed by the same vendor visibility, and the register renders
  every `Out` justification behind it, so the register's scope read straddles exactly as a
  separate assurance read would. That straddle is not created here and is not closed here:
  it exists in the shipped page, and widening the snapshot to carry the scopes would put a
  full scope read on every authenticated request to close a hole this change did not open.
  It is recorded under Risks as open.

One residual case stays open and is recorded there too. The two Statement of Applicability
pages take their own snapshot - one shared read,
`GetStatementOfApplicabilityDrilldownInputsAsync` - and a page that takes one seeds the
accessible memo from its own asset list, so on those pages the rail's count is narrowed by
that page's assets rather than by the assurance snapshot's. Closing it would mean one snapshot shape for every read in a
request, which is a change to the read model rather than to this feature.

### The nav badge takes a real dependency, is resolved once, and fails soft

`ShellNavResolver` is a concrete request-scoped class taking `IAuthzFactProvider` and
`IEnterpriseEntitlements`. It sets `Count: null` for every item, with a class comment
saying no actionable-count source exists yet. This change makes it take `AuthzRequestCache`,
`IAssetAccess`, `TimeProvider`, and `IOptions<AssuranceOptions>` as well and compute the
Vendors count. The clock and the options are part of the count, not incidental: `Expired`
needs today and `Expiring` needs today and the effective window, resolved exactly as the
register resolves them so the badge and the page cannot disagree. It takes the cache rather than
`IComplianceStore` because the cache is where the request's one snapshot lives; reading the
store directly would take a second one.

Three consequences the shape does not make obvious:

- `ResolveAsync` is not rail-only. The rail, the command palette, and the topbar's
  breadcrumb resolution all call it, and the layout renders all three, so a naive
  implementation would take the snapshot up to three times per page. The count is
  therefore memoized on the resolver, which is already registered scoped: it is computed
  at most once per request and every later call reads the memo. Memoizing the count is not
  a cache that can lag - a request-scoped memo is resolved during the render it is shown
  in.
- The rail draws on every authenticated page, so the snapshot is read on every
  authenticated page. It adds no round trip, because `AuthzRequestCache` serves
  `GetAssetsAsync` from it, but it does cost more than the read it replaces on a page that
  reads no assets of its own. Such a page issued one autocommit `SELECT` and now issues a
  repeatable-read transaction of two selects, so what it newly carries is that second query
  as well as the assurance rows, one
  per certification on file. If that cost shows up, the fix is to move the count off the
  render path, not to un-pair the two reads or to denormalize a counter that can go stale.
- A store outage currently breaks only the pages that read the store, each of which
  catches it. Letting it escape the nav resolver would fail every page in the app. The
  count is therefore computed inside the same store-failure catch the register uses, and
  a failure yields `Count: null`: no badge, which is exactly what the shell requirement
  already says an item with no count source renders. The memo holds the failed result too,
  so an outage costs one attempt per request rather than three.

A dedicated `INavCountSource` seam was considered and rejected as a single-caller
abstraction. The direct dependency is smaller and the second count source, when the
review model lands, can extract a seam then with two callers to shape it.

### Rendering: one toned stamp per assurance

One `<fb-stamp>` per assurance, the standard's title in the label slot and the expiry
wording in the age slot, following the prototype's `SOC 2 / EXP 03-27`. One stamp per
certification rather than a roll-up: the Data column already renders one mark per value,
no roll-up rule needs defining, and hiding certifications behind a count is the
fabricated-completeness failure O2 exists to prevent.

Tones are neutral for `Valid` - the mark's untinted grey, not a provenance colour - warn
for `Expiring`, and fail for `Expired`. Green is not used. A certification is a fact on file rather than a Freeboard verdict, the Status
column two cells to the left still correctly reads "Not evaluated", and spending the pass
colour on a self-declared certificate nobody has verified misreads it. Amber and red are
earned because a lapsed date is a fact, not a judgement, and S3 reserves red for exactly
this kind of overdue.

The word differs per state, so colour is never the only channel (S2). The two states that
ask for action follow T6's near/far rule; `Valid` does not:

- `Expired`: `expired 3 days ago` within a week, `expired Mar 3` beyond it.
- `Expiring`: `expires tomorrow` / `expires in 5 days` within a week,
  `expires Mar 26` beyond it.
- `Valid`: `expires Mar 27`, always absolute.

`Valid` is absolute at every distance because the relative form is what marks a date the
reader is being asked to act on, and because the near/far rule applied to all three states
would collide on the boundary this design makes a point of. With `warn_days: 0` an
assurance expiring tomorrow is `Valid`, and a relative `Valid` would render it
`expires tomorrow` - word-for-word what an `Expiring` one renders, leaving amber as the
only difference, which is the S2 failure the differing words exist to prevent. Absolute
`Valid` keeps the word channel true at exactly that boundary.

That mirrors `DueTagHelper.Describe`, which is the T6 implementation, with the
certificate's own verb in place of "Overdue since". It is a private static on the page
model with one caller. If a second caller appears, the two merge into `fb-due` with a
verb; duplicating a nine-line rule is cheaper today than parameterizing a shared one for
a single use.

A vendor with no assurance keeps the plain "None on file" muted text. The prototype
stamps it (`stamp manual`), but a stamp is a provenance claim and there is no provenance
for a value that does not exist.

### The mark is the existing stamp, toned, not a second mark

A dedicated assurance mark reusing the stamp's styling was considered, on the grounds that
`fb-stamp` is specced as the provenance helper (P1/P2) and toning it widens a ratified
mark. It was rejected.

A second helper emitting the same border, the same mono type, and the same label/age
structure is a twin doing one job, which the design system forbids in as many words: its
ratified text requires that "each visual job resolves to one class, with no `fb-*` twin of
a redefined bare name doing the same job", and it explicitly tolerates a single-caller mark
staying a plain class until a second caller appears. The assurance mark has exactly one
caller. Building a second helper for it adds a class, a tag helper, a test file, and a
story, to render what the existing mark already renders.

The widening is real, so this change ratifies it rather than smuggling it: the
`web-design-system` delta restates the requirement to say the stamp takes its own tone
enum, and that its source slot names an integration for an automated value and a scheme
for a certification. The doc comment says the same. A widened rule that the spec states is
a decision; a widened rule the code takes silently is drift.

The amber-versus-`MANUAL` collision noted under Risks does not discriminate between the two
options. A separate mark reusing stamp styling with warn ink collides identically, so the
collision is a property of the palette, not of which helper renders it.

### The stamp's tone is its own three-member enum, and it is optional

`StampTagHelper` has never had a tone. It emits `fb-stamp manual` or `fb-stamp gen`,
picking the label from `Manual ? "MANUAL" : Source`. Neither of those two is a tone:
`manual` is amber ink because a hand-entered value is a weaker provenance, and `gen` is
brand-violet ink because Freeboard generated the value. Both name where the value came
from. The genuinely untinted rendering is the bare `.fb-stamp`, which is muted ink on the
strong line colour.

The tone is a new `StampTone` enum - `Neutral`, `Warn`, `Fail` - in `MarkEnums.cs` beside
`MarkTone` and `StatusKind`. Each member names the colour it emits: `Neutral` emits the
bare `.fb-stamp`, `Warn` and `Fail` emit `.fb-stamp.warn` and `.fb-stamp.fail`. A tone
SELECTS the rendering rather than adding to it, so a toned stamp never carries a
provenance variant's colour underneath its tone.

The property is nullable, and no tone is the default. An untoned stamp keeps the
provenance rendering it has today - `manual` when the flag is set, `gen` otherwise - so
no existing caller changes and the two existing class assertions in `MarkTagHelperTests`
hold. The alternative, a non-nullable enum whose default member means "untoned", would
have to name a member for something that is not a colour; naming a member `Neutral` while
it emitted the violet `gen` class would state the opposite of what it renders.

That leaves four emitted variants - `manual`, `gen`, `warn`, `fail` - and three tones,
selected by two different things: the provenance flag picks between `manual` and `gen`,
the tone picks between the bare mark, `warn`, and `fail`. Each emitted variant is reachable
by exactly one of the two, which is the invariant the design system delta states and the
tag-helper test pins in both directions.

`MarkTone` was rejected. It has five members, and a stamp has no use for `Ok` or `Brand`
- `Ok` is the green this design deliberately does not spend, and `Brand` is the provenance
the `gen` variant already carries, not a tone. Reusing it would make an S3-violating stamp
representable and would break the design system's one-tone-one-rendering invariant for
this mark. A three-member enum keeps that invariant exact.

`Valid` therefore renders the bare mark: grey ink, no tint. That is the honest rendering
for a fact on file that Freeboard has not verified and does not colour.

`Warn` reuses `--color-warn-ink` with a `color-mix(in srgb, var(--color-warn) 40%,
transparent)` border, which is byte-for-byte what `.fb-stamp.manual` already declares.
Rather than write the same body twice, `.fb-stamp.manual` and `.fb-stamp.warn` share one
rule with a comment saying the sameness is deliberate: the palette holds one amber, and
both a hand-entered value and a lapsing certificate are the thing that amber means here.
`.fb-stamp.fail` is its own rule on `--color-fail-ink` with a `--color-fail` border.

### The `source` slot names the standard, and P2 gets a one-clause delta

P1 requires an automated value to name its source and age. The stamp's doc comment says
`source` is "the collecting source for an automated value". An assurance names neither
Freeboard nor a collector, so the slot widens from "the collecting integration" to "what
the value came from: a collecting integration for an automated value, the certification's
standard for an assurance".

The model stores two fields, `standard` and `expires`, so those two are what the stamp can
say. It names the standard the certificate is against and the date that certificate
expires. It does not name an issuer, an auditor, a report, or a link to the certificate,
because none of those exists here - a standard is what the certification is measured
against, not who says so. Non-goals lists the evidence of a certification as out of scope,
and the stamp is bounded by the same line.

P1 is not the rule that bites. An assurance is hand-authored in gitops YAML, so the rule
that governs it is P2: "Manual values SHALL be stamped MANUAL and dated. Manual is a
provenance, not the absence of one." Read literally, P2's scenario covers this value and
asks for `MANUAL`, which this change does not render.

Naming the standard serves P2 better than `MANUAL` does. The value the register shows is
not something the author composed: it is a reference to a record held outside Freeboard
(the certificate) plus that record's own date. `MANUAL` would name the act of transcribing
it and drop the reference, which is the only part a reader came for. The mark still carries
a date, so the "and dated" half of P2 is met exactly.

That reading has to be bounded, or it swallows the case P2 exists for. A hand-uploaded
evidence document is also transcribed from somewhere outside the system, and it stays
`MANUAL`: the person supplied the content, so the person is the origin. The line the delta
draws is between content a person supplies and a reference to a named external record shown
with that record's own date. The first is `MANUAL`; only the second names the record.

That is a reading P2's text does not yet admit, so this change states it rather than taking
it silently - the same treatment it gives the widened stamp. The `web-ux-conventions` delta
is that one clause and nothing else. It says nothing about `tier` or `data_classes`, which
carry no stamp today, still carry none after this change, and are governed by no rule this
change touches.

### Parity: the endpoint derives, the CLI prints

`GET /api/v1/freeboard/vendors` returns each assurance's `standard`, `expires`, and the
derived `status`. The endpoint deriving the status is what makes `freeboard vendor list`
free: the CLI prints the state without knowing the window exists. Returning the raw date
alone would put the window in two places, which read-model parity does not allow.

The CLI prints each assurance as an indented line under its vendor, above the scope
lines, carrying the standard id, the expiry, and the status. That is the established
idiom on this command for a per-vendor child list, where absence is silence. The `-`
placeholder stays what it is: the marker for an absent scalar field on the vendor line.

## Choices that a first reading of the feature would not predict

Eight points where the obvious shape is not the one taken, each for a reason found in the
code:

1. **The badge count is a C# aggregate, not an indexed SQL count, and there is no index on
   `expires`.** Owner narrowing lives in `IAssetAccess` over a loaded list, so an
   owner-narrowed SQL count would reimplement the accessible-set rule in a
   security-relevant second place. Nothing then queries `expires`, so an index on it would
   have no query to pay for it.
2. **`compliance-web-read` gets a delta.** Its ratified text specifies this endpoint's
   field set - the vendors endpoint returns "their `id` and `title` and no other field" -
   which the shipped tier and data-class fields already contradict. The delta states the
   real field set and repairs that drift.
3. **`ContrastGuardTests` needs no new case.** It enumerates token names, not component
   classes, and already asserts `warn-ink` and `fail-ink` on `panel`, `field`, and
   `panel-dim` in both themes. The new stamp tones reuse those exact pairs, so they are
   already covered. The tone is pinned instead by a tag-helper test asserting the
   tone-to-class map, which is the thing that can actually regress.
4. **The stamp tone is an optional three-member `StampTone`, not `MarkTone`.** See the
   decision above: a five-member tone would make green and brand stamps representable, and
   an optional tone is what leaves every existing caller's provenance rendering alone.
5. **The CLI prints assurances as sub-lines, not as a `-` placeholder field.** A
   per-vendor child list already has an idiom on this command (the scope lines), and a
   `-` on the vendor line cannot carry three values per entry.
6. **The importer needs no new machinery.** Whole-set child replacement already exists
   twice in `MySqlGitOpsImporter`, in the right place in the transaction. The new call
   copies it.
7. **The table is `vendor_assurances` and the reference column is `standard_id`, not the
   shorter `assurances(vendor_id, standard, ...)`.** Every child table in the schema is
   named for its parent and every foreign-key column ends in `_id`, so the shorter names
   would be the only exceptions in the database.
8. **A zero warning window gives no warning at all until the certificate has expired.**
   Read as a plain inclusive window, a zero one warns on the expiry date itself, a day
   early, which is not what "no advance notice" means. The evaluator therefore guards the
   `Expiring` branch with `warnDays > 0`.

Three further deltas - `gitops-cli`, `compliance-persistence`, and `web-ux-conventions` -
are also added; see Resolved questions and the source-slot decision above. Eight in all.

## Risks / Trade-offs

- **A sync leaves stale assurance rows after an entry is removed from YAML.** Silent, and
  no unit test sees it. -> A `FREEBOARD_TEST_DB`-gated integration test syncs a vendor
  with two assurances, syncs again with one removed and the vendor's whole list removed,
  and asserts the rows are gone. The removal case is the half that fails silently.
- **A vendor or a standard that an assurance references cannot be deleted.** Both foreign
  keys are `RESTRICT`, so a misplaced replace call turns a normal config edit into a
  failed sync. -> The replace runs before every delete in the same transaction, and the
  integration test drops a vendor that holds assurances and asserts the sync succeeds.
- **The rail now reads assurances on every authenticated page.** The layout resolves the nav
  three times per render (rail, palette, breadcrumbs). -> The snapshot is memoized on
  `AuthzRequestCache` and the count on the request-scoped resolver, so a page takes one
  snapshot rather than three, and the count runs inside the store-failure catch so an
  outage degrades to no badge. The snapshot replaces the request's asset read rather than
  adding one, so `/compliance/vendors` goes from two asset reads to one. An authenticated
  page that reads no assets of its own trades one autocommit `SELECT` for a two-statement
  repeatable-read transaction, so what it newly carries is a second query as well as the
  assurance rows, one per certification on file. A page that already reads assets itself -
  the collector register, the integration-connection register, and both Statement of
  Applicability pages - already pays two reads and is roughly cost-neutral.
- **Every compliance write and every organisation-anchored gate now reads
  `vendor_assurances`.** `AuthzRequestCache.GetAssetsAsync` is the asset source for the
  ancestry `Authorizer` resolves on every organisation gate, for the three compliance write
  handlers and their selectors, for the role-assignment page guard, and for the
  role-assignment endpoint selector. After this change all of them take their assets from
  the snapshot, so an absent or unreadable `vendor_assurances` fails them, not only the
  register. -> Accepted, and fail-closed. The response is not the 503 the read pages give,
  and it is not uniform. A selector throw is caught as a deny by the permission filter
  (`AuthzEndpointExtensions`), so every gated write answers 403 - `PUT
  /api/organisations/{id}` included. The guarded pages answer 403 as well: `Authorizer`
  catches an ancestry-resolution failure and fails closed to a deny, and `AuthzPageGuard`
  returns 403 for a deny. The 500 path is narrower than "outside every catch" and is one
  call site: a page that builds its resource before the guard runs. `RoleAssignments`
  resolves the organisation resource through the cache first, so a throw there escapes the
  handler. Every one of these refuses the
  write, which is the safe direction. A write-path test pins the 403 so the behaviour
  cannot drift unnoticed, and the Migration Plan states the deploy-order consequence.
- **The register's scope read still straddles a concurrent sync.** The page narrows scopes
  by the same vendor visibility and renders every `Out` justification, so a scope read on
  the far side of a `sync` commit can pair post-sync justification text with pre-sync owner
  edges. -> Open and out of scope. It exists in the shipped page and this change neither
  widens nor closes it; carrying the scopes in the snapshot would put a full scope read on
  every authenticated request to close a hole this change did not open.
- **A page that takes its own snapshot still seeds the accessible set from its own asset
  list.** The two Statement of Applicability pages do, through one shared read, so on those
  pages the rail's count is narrowed by the page's assets rather than by the assurance
  snapshot's. -> Open. Closing it
  needs one snapshot shape for every read in a request, which is a read-model change rather
  than a feature change. The exposure is narrow: a caller must render a Statement of
  Applicability page in the moment a `sync` moves a vendor out of their reach, between the
  page's read and the rail's.
- **An amber stamp is visually identical to a `MANUAL` stamp.** Both use `--color-warn-ink`
  with a 40% `--color-warn` border. -> The word differs (`MANUAL` against
  `SOC 2 EXPIRES MAR 26`), the register renders no manual stamps, and giving the expiring
  state its own colour would mean spending a sixth semantic token to solve a collision no
  page has. The two classes share one CSS rule rather than declaring the same body twice.
- **`warn_days` gives two vendors different notice for the same standard.** That is the
  point, but it also means the badge count cannot be explained by one number. -> The cell
  states each entry's own wording, so the count is always the sum of what the page shows.
- **The declared-standard requirement will annoy an operator who tracks a certification
  for a standard the organisation does not pursue.** -> Accepted, and documented: a bare
  `Standard` document is four lines. The alternative is a token set that has to be mapped
  onto standard ids later anyway.
- **A new `IComplianceStore` method breaks every hand-written double of the interface, and
  the new dependencies touch every construction of `ShellNavResolver` and
  `OrgSelectionResolver` in the tests.** -> Mechanical, but there are two doubles, not one:
  `FakeComplianceStore` and the `CountingComplianceStore` inside `OrgSelectionTests`. Both
  must implement the new read or the test project stops compiling, and
  `CountingComplianceStore` now counts the snapshot rather than `GetAssetsAsync`.
- **The badge is the first count in the app, so an owner-narrowing mistake in it would
  leak the existence of a vendor whose row the caller cannot see.** -> The count is derived
  from the same accessible set the page uses, with no second rule, and the browser test
  named under the count decision asserts over the whole document that a hidden vendor's id
  and assurance text are absent while the badge counts the visible vendor only.

## Migration Plan

One forward-only migration, `023_vendor_assurances.sql`, creates the `vendor_assurances`
table. It carries no data migration and no backfill: the table starts empty and every
vendor keeps rendering "None on file" until a config declares an entry.

The migration is additive - a new table, no change to `assets` - so an older app against
the migrated schema simply never selects from it. It is therefore safe to apply ahead of
the deploy. Deploy order is the established one: `freeboard system migrate`, then the app.

The reverse order is what this change makes expensive, and the order is now a requirement
rather than a convention. The snapshot is the request's asset read, so a new app against an
unmigrated schema does not merely show the vendor register's unreachable notice. Every
organisation-anchored authorization gate and every compliance write reads the same
snapshot, so all of them fail while the table is absent: a gated write answers 403, a
guarded page answers 403, and the one page that builds its resource before the guard runs
(`RoleAssignments`) faults with a 500. Reads still degrade to their in-page notice. Applying the
migration fixes it with no restart, since nothing is cached across requests.

Rollback: forward-only by repo convention. A new empty table needs no down path.
Restore-and-replay stays the recovery route for a failed apply, as for every other
migration here.

Ordering note for the implementation: `023` is the next free ordinal. Ordinal `011` is
used by two files, and `MigrationCatalog` orders by ordinal alone, so do not repeat that.

## Resolved questions

**The `gitops-cli` hard-remove requirement gets a delta.** Its text does not merely
delegate to "foreign-key-safe order": it enumerates the concrete constraints - the whole-set
scope replace precedes the absent standard, requirement, and control deletes; an absent
collector is pruned before an absent integration; an absent integration before an absent
vendor asset. That enumeration reads as the list of orderings the `sync` contract holds
itself to, and a reader implementing `sync` from that capability alone would not learn that
a vendor's assurance rows must be cleared first. The assurance replace is one more
constraint of exactly the kind the clause names, so it is stated there as well as in
`asset-model`. The two deltas say different things about it on purpose: `asset-model`
specifies the table and its replacement, `gitops-cli` specifies the command's removal
order.

**`compliance-persistence` gets a delta too, for the same reason and for the read.** Its
import-order requirement does not delegate to "foreign-key-safe order" either: it
enumerates the fixed sequence clause by clause, down to which replace precedes which
delete and why. Inserting the assurance replace into that sequence without a delta would
leave a ratified enumeration that no longer matches the importer. The same capability owns
`IComplianceStore`, and it is where the Statement of Applicability input snapshots are
specified, so the vendor assurance snapshot belongs beside them rather than in
`asset-model`, which owns the table rather than the read.

**The badge counts expired assurances as well as expiring ones.** A lapsed certificate is
at least as actionable as one about to lapse, and N6 admits both as work waiting on the
viewer; a badge that dropped to zero the moment a certificate actually expired would read
as "resolved" at the moment the problem became real. The two states also share one owner
and one remedy, so splitting them across two surfaces would ask the reader to look in two
places for one job. If a lapsed certificate should instead read as a failing vendor once
the review model exists, the count moves there rather than being split now.
