## MODIFIED Requirements

### Requirement: Asset authoring, type, source, and edges

The system SHALL support an `Asset` kind that unifies the previous `Organisation`
and `Vendor` kinds and the discovered machine model into one resource. An `Asset`
has an immutable `id`, a mutable `title`, a required `type` (exactly one of
`Company`, `Department`, `Machine`, or `Vendor`), a required `source` (exactly one
of `declared` or `discovered`), and at most one of two mutually exclusive scalar
edges: `parent` (a `Company` or `Department` asset id) or `owner` (a `Company` or
`Department` asset id). A `Company`, `Department`, or `Machine` asset MAY carry a
`parent`; a `Vendor` asset MAY carry an `owner`. A declared asset MAY carry the
authored fields only; the discovered-only fields (`identity_kind`,
`identity_value`, `state`, `first_seen`, `last_seen`) are written by ingest, never
authored in config.

A `Vendor` asset MAY additionally carry two optional risk-profile fields, and no
other asset type may carry either: a `tier`, which is exactly one of `Critical`,
`High`, `Medium`, or `Low`, and a `data_classes`, which is a set of tokens drawn
from the closed set `pii`, `phi`, `special-category`, `payment-card`, and
`credentials`. `tier` states how much damage the vendor can do; `data_classes`
states which regulated data it holds. The `data_classes` order is not meaningful
and carries no ranking: the tokens are facts, not severities. The set is defined
by regulatory regime rather than by data type, so `phi` means HIPAA-regulated and
`special-category` means UK/EU GDPR Article 9. Health data falls under both, and
authoring both tokens on one vendor is correct rather than a duplicate. An absent
`data_classes` and an empty `data_classes` mean the same thing: the system SHALL
NOT distinguish "not assessed" from "assessed as holding nothing". The field names
are deliberately not vendor-prefixed so a later change MAY widen them to another
asset type without renaming an authored key.

A `Vendor` asset MAY also carry an optional `assurances` list recording the
certifications it holds, and no other asset type may carry it. Each entry has a
required `standard` (a `Standard` id), a required `expires` (a calendar date in
`YYYY-MM-DD` form), and an optional `warn_days` (a whole number of days, zero or
more) that overrides the deployment's warning window for that entry alone. An entry
carries no id: the pair `(asset, standard)` identifies it, so one vendor SHALL NOT
carry two entries naming the same standard. An entry carries no status either: the
state of a certification is derived from `expires` and the clock, so an authored
status could contradict the date it sits beside.

The `standard` reference SHALL keep referential integrity, matching the `Scope`
target references rather than the dangling-tolerated scalar edges: a certification
names a document in the same config, not a thing another writer owns. Recording a
vendor's certification therefore requires declaring that standard as a `Standard`
document, even when the organisation pursues none of its requirements. A `Standard`
with no `Requirement` documents is already valid, so that cost is one short document.

An unknown field inside an `assurances` entry SHALL be reported rather than ignored. The
loader ignores unmatched properties when it binds a document, and its unknown-field check
reads the document's top-level keys only, so an entry's keys SHALL be checked explicitly
against `standard`, `expires`, and `warn_days` on the authored entry itself. Checking the
authored entry rather than the bound object is what makes a key with an empty value
reportable, since an absent key and a key with no value bind identically. This follows the
one existing nested check, which diffs a collector's `config` mapping the same way.

Two degenerate inputs SHALL load without throwing, so a malformed document produces a
diagnostic rather than an unhandled failure: an `assurances` key with no value SHALL
normalize to an empty list, matching how an empty `data_classes` is treated, and a null
entry in the list SHALL be kept as an empty entry so validation reports its missing
`standard` and `expires`. Dropping a null entry instead would silently swallow the
authoring mistake.

A declared config MAY author `source: declared` only. `source: discovered` is
reserved for ingest and SHALL be rejected when authored in config. A declared
asset uses an authored slug id; a discovered asset uses a ULID id; both share one
id space. `parent` and `owner` are scalar references validated at write with no
foreign key: a reference that does not resolve is tolerated (see Asset validation).
The `Scope.subject` and `Collector.vendor` references SHALL name the matching
asset: `Scope.subject` names any asset (a Company/Department, a Machine, or a Vendor; a Vendor
subject may not target a standard), and `Collector.vendor` names a `Vendor` asset. The
`Scope.subject` reference is scalar with no foreign key and is dangling-tolerated, while
the `Scope` target references (`standard`/`requirement`/`control`) keep referential
integrity.

#### Scenario: Company asset with a department child loads

- **WHEN** a `kind: Asset` document of `type: Company` with `source: declared` and a
  `kind: Asset` of `type: Department` with `source: declared` and a `parent` naming
  the Company both appear
- **THEN** both load into the typed model, the Company is a root asset, and the
  Department is its child

#### Scenario: Vendor asset with an owner loads

- **WHEN** a `kind: Asset` of `type: Vendor` with `source: declared` and an `owner`
  naming a `Company` asset appears
- **THEN** it loads as a declared vendor asset owned by that Company

#### Scenario: Vendor asset with a tier and data classes loads

- **WHEN** a `kind: Asset` of `type: Vendor` carries `tier: Critical` and
  `data_classes: [pii, payment-card]`
- **THEN** it loads with that tier and both data class tokens, in no meaningful
  order

#### Scenario: Vendor asset with neither risk-profile field loads

- **WHEN** a `kind: Asset` of `type: Vendor` carries neither `tier` nor
  `data_classes`
- **THEN** it loads with no tier and no data classes, and validation still passes
  (see the tierless-vendor warning in Asset validation)

#### Scenario: Empty data classes matches an omitted key

- **WHEN** one `Vendor` asset authors `data_classes: []` and another omits the key
- **THEN** both load with no data classes, indistinguishable from each other

#### Scenario: Vendor asset with assurances loads

- **WHEN** a `kind: Asset` of `type: Vendor` carries an `assurances` list whose
  entries name a declared `standard` and an `expires` date, one of them also naming
  a `warn_days`
- **THEN** it loads with one assurance per entry, each carrying its standard id, its
  expiry date, and its own `warn_days` where authored and none where not

#### Scenario: Vendor asset with no assurances loads

- **WHEN** a `kind: Asset` of `type: Vendor` omits `assurances`, or authors an empty
  list
- **THEN** it loads with no assurances and validation still passes, because holding
  no certification is a legitimate state

#### Scenario: Declared source is the only authorable source

- **WHEN** a `kind: Asset` document authors `source: declared`
- **THEN** it loads, whereas a document authoring `source: discovered` is rejected
  (see Asset validation)

#### Scenario: Unknown field on an Asset is rejected

- **WHEN** a `kind: Asset` document carries a field not defined for the kind
- **THEN** the loader reports the document and the unknown field

#### Scenario: Unknown field on an assurance entry is rejected

- **WHEN** an `assurances` entry carries a field other than `standard`, `expires`,
  or `warn_days`
- **THEN** the loader reports the document and the unknown field, evaluated on the
  authored entry rather than on the bound object so a key with an empty value is
  rejected too

#### Scenario: An assurances key with no value loads as no assurances

- **WHEN** a `kind: Asset` of `type: Vendor` authors `assurances:` with no value
- **THEN** it loads with no assurances, indistinguishable from omitting the key, and the
  loader does not throw

#### Scenario: A null assurance entry is reported, not dropped

- **WHEN** an `assurances` list carries a null entry
- **THEN** the entry is kept as an empty one and validation reports its missing `standard`
  and `expires`, rather than the entry disappearing or the loader throwing

### Requirement: Asset validation

The system SHALL validate assets and report every error as a structured
diagnostic. Validation SHALL fail (an `Error` diagnostic) when any of the
following hold: an `Asset` is missing or blank on `id` or `title`; an `Asset.type`
is not one of `Company`, `Department`, `Machine`, or `Vendor`; an `Asset.source`
is not `declared` or `discovered`; a declared config authors `source: discovered`; a
declared config authors any discovered-only field (`identity_kind`, `identity_value`,
`state`, `first_seen`, `last_seen`), which is ingest-written and never authored;
an `Asset` carries both `parent` and `owner`; an `Asset.parent` names an asset that
is not a `Company` or `Department`; an `Asset.owner` names an asset that is not a
`Company` or `Department`; a `parent` is carried by an asset that is not a
`Company`, `Department`, or `Machine`; an `owner` is carried by an asset that is not
a `Vendor`; an `Asset.tier` is present and is not one of `Critical`, `High`,
`Medium`, or `Low`; an `Asset.data_classes` contains a token outside the closed set
`pii`, `phi`, `special-category`, `payment-card`, `credentials`; a `tier` or a
`data_classes` is carried by an asset that is not a `Vendor`; an
`Asset.data_classes` lists the same token more than once; an unknown field is
present; or an `Asset` id is duplicated. Authoring a
discovered-only field is a distinct error from authoring `source: discovered`: the
first names the offending field, the second names the source.

Validation SHALL likewise fail (an `Error` diagnostic) when any of the following
hold on an `assurances` entry: an `assurances` list is carried by an asset that is
not a `Vendor`; an entry's `standard` is missing, blank, or names an id that no
`Standard` document defines; an entry's `expires` is missing, blank, or is not a
calendar date in `YYYY-MM-DD` form; an entry's `warn_days` is present and is not a
whole number of zero or more; or two entries on one asset name the same `standard`.
Each diagnostic SHALL name the asset and the offending entry's standard or value.

A dangling assurance `standard` is an `Error` rather than the non-blocking warning a
dangling `parent` or `owner` draws, matching the `Scope` target references: the
scalar edges point at things another writer may own, while a certification names a
document in the same config, so an unresolved one is an authoring mistake. A
duplicate `standard` on one asset is an `Error` rather than a silent collapse,
matching the duplicate `data_classes` token rule.

An `expires` that has already passed SHALL NOT produce a diagnostic of any severity.
Validation is pure and clock-free, so a time-dependent check would make the result
of `validate` depend on when it ran and would make two runs over one unchanged config
disagree. An expired certification is a real state that the read surfaces render.

A duplicate `data_classes` token is an `Error` rather than a silent de-duplication,
matching duplicate check names and duplicate requirement ids elsewhere in this
format: silently collapsing a duplicate hides an authoring mistake. The tier and
data class vocabularies are validated against closed sets held in
`Freeboard.Core`, with no database read, so the check runs offline in
`freeboard gitops validate`.

A `parent` or `owner` that names an id absent from the resolved asset set (a
dangling reference) SHALL NOT be an error: it SHALL be reported as a NON-BLOCKING
`Warning` diagnostic that does not fail `validate`, `apply`, or `sync`. A `parent`
cycle among declared assets SHALL likewise be tolerated (a warning, not an error),
because resolution walks are cycle-guarded. A missing required edge - a declared
`Vendor` with no `owner`, or a `Machine` with no `parent` - SHALL also be a
NON-BLOCKING `Warning`, not an error, because such an asset is invisible under the
fail-closed read model; a `Company` or `Department` with no `parent` is a legitimate
root and SHALL NOT warn.

A declared `Vendor` with no `tier` SHALL likewise be a NON-BLOCKING `Warning`, not
an error: `owner` is the edge that decides whether the vendor is visible at all and
it only warns, so a field that colors a tag SHALL NOT fail a sync. An absent or
empty `data_classes` SHALL produce NO diagnostic of any severity, because a vendor
holding none of the organisation's regulated data is a real and common state. An
absent or empty `assurances` SHALL likewise produce NO diagnostic of any severity,
for the same reason: holding no certification is a real and common state, and a
warning that fires on the normal case teaches authors to ignore sync output.

#### Scenario: Unknown type rejected

- **WHEN** an `Asset` declares a `type` other than `Company`, `Department`,
  `Machine`, or `Vendor`
- **THEN** validation fails, naming the asset and the bad type

#### Scenario: Authoring a discovered asset in config is rejected

- **WHEN** a config document declares `kind: Asset` with `source: discovered`
- **THEN** validation fails, naming the asset, because ingest is the only writer of
  discovered assets

#### Scenario: Authoring a discovered-only field in config is rejected

- **WHEN** a `kind: Asset` document authors a discovered-only field (`identity_kind`,
  `identity_value`, `state`, `first_seen`, or `last_seen`)
- **THEN** validation fails with an `Error` naming the asset and the discovered-only
  field, distinct from the `source: discovered` rejection, because those fields are
  written only by ingest

#### Scenario: Parent and owner are mutually exclusive

- **WHEN** an `Asset` declares both `parent` and `owner`
- **THEN** validation fails, naming the asset

#### Scenario: Parent target must be Company or Department

- **WHEN** an `Asset.parent` names an asset that is not a `Company` or `Department`
- **THEN** validation fails, naming the asset and the invalid parent target

#### Scenario: Vendor owner target must be Company or Department

- **WHEN** a `Vendor` asset's `owner` names an asset that is not a `Company` or
  `Department`
- **THEN** validation fails, naming the vendor and the invalid owner target

#### Scenario: Unknown tier token rejected

- **WHEN** an `Asset.tier` is present and is not one of `Critical`, `High`,
  `Medium`, or `Low`
- **THEN** validation fails, naming the asset and the bad tier token

#### Scenario: Unknown data class token rejected

- **WHEN** an `Asset.data_classes` contains a token outside `pii`, `phi`,
  `special-category`, `payment-card`, and `credentials`
- **THEN** validation fails, naming the asset and the bad token

#### Scenario: Duplicate data class token rejected

- **WHEN** an `Asset.data_classes` lists the same token twice
- **THEN** validation fails, naming the asset and the duplicated token, rather than
  silently de-duplicating it

#### Scenario: Tier or data classes on a non-Vendor asset rejected

- **WHEN** a `Company`, `Department`, or `Machine` asset carries a `tier` or a
  `data_classes`
- **THEN** validation fails, naming the asset and the field, because both fields are
  valid on a `Vendor` only

#### Scenario: Assurances on a non-Vendor asset rejected

- **WHEN** a `Company`, `Department`, or `Machine` asset carries an `assurances` list
- **THEN** validation fails, naming the asset and the field, because `assurances` is
  valid on a `Vendor` only

#### Scenario: Dangling assurance standard rejected

- **WHEN** an `assurances` entry names a `standard` id that no `Standard` document
  defines
- **THEN** validation fails, naming the asset and the unknown standard id, rather
  than warning as a dangling `parent` or `owner` does

#### Scenario: Missing or unparseable expiry rejected

- **WHEN** an `assurances` entry omits `expires`, leaves it blank, or authors a value
  that is not a `YYYY-MM-DD` calendar date
- **THEN** validation fails, naming the asset and the entry, because an assurance
  with no usable expiry cannot be warned on

#### Scenario: Negative warn_days rejected

- **WHEN** an `assurances` entry authors a `warn_days` below zero, or a value that is
  not a whole number
- **THEN** validation fails, naming the asset and the value, while `warn_days: 0` is
  accepted and means no advance notice

#### Scenario: Duplicate assurance standard rejected

- **WHEN** one `Vendor` asset carries two `assurances` entries naming the same
  `standard`
- **THEN** validation fails, naming the asset and the duplicated standard, because
  the pair of asset and standard identifies the entry

#### Scenario: Expiry already in the past produces no diagnostic

- **WHEN** an `assurances` entry authors an `expires` date that has already passed
- **THEN** no diagnostic of any severity is produced for it, and two runs of
  `validate` over the unchanged config agree, because validation reads no clock

#### Scenario: Tierless vendor is a non-blocking warning

- **WHEN** a declared `Vendor` carries no `tier`
- **THEN** a non-blocking `Warning` diagnostic names the vendor and validation does
  not fail on it

#### Scenario: Absent or empty data classes produce no diagnostic

- **WHEN** a declared `Vendor` omits `data_classes`, or authors `data_classes: []`
- **THEN** no diagnostic of any severity is produced for that field

#### Scenario: Absent or empty assurances produce no diagnostic

- **WHEN** a declared `Vendor` omits `assurances`, or authors an empty list
- **THEN** no diagnostic of any severity is produced for that field

#### Scenario: Dangling parent or owner is a non-blocking warning

- **WHEN** an `Asset.parent` or `Asset.owner` names an id that no asset defines
- **THEN** a non-blocking `Warning` diagnostic names the dangling reference and
  validation does not fail on it

#### Scenario: Missing required edge is a non-blocking warning

- **WHEN** a declared `Vendor` carries no `owner`, or a `Machine` carries no
  `parent`
- **THEN** a non-blocking `Warning` diagnostic names the asset as unreachable and
  validation does not fail on it, while a parent-less `Company` or `Department`
  produces no diagnostic
