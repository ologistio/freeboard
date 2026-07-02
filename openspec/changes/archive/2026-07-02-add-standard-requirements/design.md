## Context

The GitOps compliance model in `Freeboard.Core/GitOps/ConfigModel.cs` has four
kinds: `Standard` (id + title), `Control` (id + title + `maps_to` Standard ids),
`Organisation`, and `Scope`. The loader routes by `kind` and rejects unknown
top-level fields; the validator reports every error as data and never throws or
prints. `Freeboard.Persistence` (MIT, Dapper + MySqlConnector, hand-written SQL)
persists the domain: a general read store (`IComplianceStore`), a GitOps importer
(`IGitOpsImporter`, one FK-safe transaction, upsert-by-id, prune-absent), and
forward-only checksum-tracked migrations (`001..007`, next is `008`). The web app
serves read-only endpoints under `/api/v1/freeboard/` and a Statement of
Applicability projection.

This change makes a `Standard` a real, structured object and introduces a
distinct `Requirement` kind that carries a standard's published normative
statements. It then authors a real CE+ fixture. The mediator decisions (full
structured Standard, a distinct Requirement kind, the full CE+ requirement set
across all five themes) are binding.

## Goals / Non-Goals

**Goals:**

- A `Requirement` kind, distinct from `Control`, that belongs to one `Standard`
  and carries `theme`, `statement`, `guidance` (optional), and an external
  citation (`citation_label` + `citation_url`).
- `Standard` metadata: required `version` and `authority`; optional `publisher`
  and `source_url`.
- Full persistence, importer, read-store, and web read support for the new kind
  and metadata, consistent with the existing patterns (id identity, binary
  collation, FK-safe import, deterministic ordered reads).
- A real, reusable CE+ fixture (example YAML plus test fixtures) covering all
  five CE+ technical control themes.
- Repoint `Control.maps_to` from `Standard` ids to `Requirement` ids, replacing
  the `control_standards` join with a `control_requirements` join.

**Non-Goals:**

- Any change to Statement of Applicability semantics. SoA reads organisations and
  scopes, not controls, so the `Control.maps_to` repoint does not affect it.
- A rendered HTML requirements screen.
- App-managed writes for requirements (GitOps-authored only).
- Any EE placement. This is MIT core compliance logic.

## Plan A vs Plan B synthesis

Two independent plans fed this change. This section records provenance and how
each divergence was resolved on the merits. The resolved decisions are then
detailed in D1-D11.

**Shared by both plans (kept):** distinct `Requirement` kind owned by exactly one
`Standard`; free-form `theme`; requirement ids globally unique within the kind;
GitOps-only authorship; additive `requirements` table and importer step; new web
read endpoint and status count.

**Divergences and resolutions:**

1. `Control.maps_to` target - **repointed -> Requirement ids (Plan B)**. Plan A
   kept `maps_to` at `Standard` ids; Plan B repointed it at `Requirement` ids. The
   mediator resolved this to Plan B: a control maps to the specific requirements it
   satisfies, which is the precise coverage relationship the `Requirement` kind now
   makes expressible. This is a pre-1.0 break: it drops `control_standards`, adds a
   `control_requirements` join, and remaps the example controls. It does NOT rework
   the Statement of Applicability - SoA projects the organisation tree over scopes
   and never reads controls (`StatementOfApplicability.cs`), so it is unaffected.
   The blast radius is validation, migration `008`, the importer join, the read
   model, the web `GET /controls` output, and the example controls. See D2.
2. `theme` model - **free string, no per-standard vocabulary (Plan A)**. Both
   plans use a free string. Plan B added an optional `Standard.themes` list plus a
   `standard_themes` table and a membership check. Resolved to Plan A: a new
   table, importer step, prune ordering, and web field to constrain a free string
   for one standard is disproportionate; theme integrity is guaranteed by
   authoring the fixture correctly and by tests. Recorded as open question 3.
3. Requirement id scheme - **synthesised**. Plan A: `req-<standard>-<theme>-<n>`
   (readable slug, no version, unpadded). Plan B: `req-ceplus-v3-3-fw-01`
   (version-embedded, abbreviated theme, zero-padded). Resolved: readable theme
   slug and no embedded version (Plan A - version lives on `Standard.version`;
   embedding it in an immutable id would force id churn on a version bump), with a
   2-digit zero-padded ordinal (Plan B - so ids sort correctly by binary id order
   when a theme exceeds nine requirements, which User Access Control does). Final:
   `req-ce-plus-<theme-slug>-NN`. See D3.
4. Standard metadata required vs optional - **version + authority required
   (Plan B); publisher + source_url optional**. Plan A made all three optional.
   Resolved to Plan B: the binding decision lists `version` and `authority` as the
   metadata a Standard gains and marks only the source URL optional; optional-
   everything leaves `Standard` almost as thin as before. Plan B's data-fabrication
   risk (defaulting legacy DB rows to fake authority) is avoided by keeping the DB
   columns nullable and upgrading the example placeholders with honest real
   metadata. See D4.
5. `publisher` as a field - **added (Plan B)**. For CE+ the scheme owner (NCSC)
   and the delivery/certification body (IASME) are genuinely distinct real-world
   roles, and the binding decision wrote "authority/publisher". `publisher` is
   optional and low-cost. Plan A folded both into one `authority` string; Plan B's
   split is more honest. See D4.
6. Citation shape - **synthesised: two flat columns `citation_label` +
   `citation_url` (Plan B semantics, Plan A mechanism)**. Plan A used a single
   `citation` string; Plan B a nested `external_citation:{label,url}` object.
   Resolved: the label+url split (Plan B) is the right shape - it web-renders as a
   link and lets the validator check the URL - but the loader has no nested-object
   precedent (`ReportUnknownFields` only checks top-level keys), so a nested object
   would need bespoke recursive unknown-field detection. Two flat keys keep the
   existing flat model intact and still give label+url and URL validation. The web
   read composes them into a nested `citation: { label, url }` for a clean API.
   See D2/D9.
7. CE+ facts and count - **v3.3, Danzell, authority NCSC, publisher IASME, 35
   requirements (source-traced against the published v3.3 requirements PDF, not
   from either plan)**. Plan A's v3.2 is superseded (v3.3 went live 2026-04-27;
   today is 2026-07-02). Plan A's 28 and Plan B's ~46 were both authored guesses;
   the fixture instead uses the faithful, non-padded decomposition of the published
   v3.3 document, traced control-by-control from its "Requirements by technical
   control theme" section: 7 + 8 + 5 + 11 + 4 = 35. See D11.

## Decisions

### D1. Theme is a free-form string, not an enum

`Requirement.theme` is a required, non-empty free-text label bound from the YAML
key `theme`. It is NOT a fixed enum, and there is no per-standard theme vocabulary.

Rationale: `Freeboard.Core` is standard-agnostic. A fixed five-value CE enum would
bake one standard's taxonomy into the shared model; other standards (SOC 2, ISO
27001, NIS2) have entirely different theme sets. A free string keeps the model
general and lets each standard's fixture supply its own themes. The five CE+
themes are simply the five distinct string values the CE+ fixture uses
(`Firewalls`, `Secure Configuration`, `Security Update Management`,
`User Access Control`, `Malware Protection`).

Alternatives considered and rejected: (a) an `OrganisationKind`-style closed enum
parsed in the validator - not extensible, forces a Core code change per standard;
(b) an optional `Standard.themes` list constraining `Requirement.theme`
membership - a new persistence table and importer step to constrain a free string
for one standard, disproportionate now (open question 3). Validation is limited to
"theme is present and non-empty"; grouping/normalisation of theme values is a
display concern, not a Core rule.

### D2. Requirement schema and its relationship to Control

New record `Requirement` in `ConfigModel.cs`:

| YAML key         | Property      | Required | Notes                                       |
| ---------------- | ------------- | -------- | ------------------------------------------- |
| `apiVersion`     | ApiVersion    | yes      | camelCase, `freeboard.io/v1alpha1`          |
| `kind`           | Kind          | yes      | discriminator `Requirement`                 |
| `id`             | Id            | yes      | immutable identity                          |
| `title`          | Title         | yes      | short display label                         |
| `standard`       | Standard      | yes      | owning `Standard` id (single)               |
| `theme`          | Theme         | yes      | free-form label (D1)                        |
| `statement`      | Statement     | yes      | the normative requirement text              |
| `guidance`       | Guidance      | no       | optional helper text                        |
| `citation_label` | CitationLabel | yes      | human label for the published source        |
| `citation_url`   | CitationUrl   | yes      | absolute http/https link to the source      |

The external citation is two flat keys, not a nested object, so the loader's
existing top-level `ReportUnknownFields` and flat `SchemaKeys` model stays intact
(the loader has no nested-object precedent; adding one would need bespoke
recursive unknown-field detection). The two columns still give a labelled,
URL-validated citation, and the web read composes them into `citation:{label,url}`.

Keeping both `title` and `statement` mirrors every other kind having a `title`
(so the existing id/title identity story and the generic upsert stay uniform)
while `statement` holds the full normative text. `title` is a short label
("Firewall on every in-scope device"); `statement` is the requirement sentence.

`Control.maps_to` is repointed from `Standard` ids to `Requirement` ids. A control
now maps to the specific requirements it satisfies, which is the precise coverage
relationship the distinct `Requirement` kind makes expressible; a control's
standard is derivable from `Requirement.standard`. `maps_to` stays a non-empty list
of ids; validation resolves each entry against `Requirement` ids (a dangling entry
is an error), rejects duplicate ids within one control, and keeps the existing
unknown-field rules. Persistence replaces the `control_standards` join with a
`control_requirements` join (D6/D7); the read model's `ControlRow.MapsTo` and the
web `GET /controls` `maps_to` now carry `Requirement` ids (D8/D9).

Because `Control.maps_to` now resolves against `Requirement` ids, the validator
must know the requirement id set before it validates controls. `Validate` runs a
fixed phase order: `ValidateStandards -> ValidateRequirements -> ValidateControls
-> ValidateOrganisations -> ValidateScopes`. `ValidateStandards` produces the
standard id set; `ValidateRequirements` consumes that set (to resolve each
`Requirement.standard`) and produces the requirement id set; `ValidateControls`
consumes the requirement id set to resolve each `maps_to` entry (a dangling
requirement id is an error). This mirrors how the existing validator already
threads the standard id set from `ValidateStandards` into control validation - the
same seam, now carrying requirement ids instead.

This repoint does NOT touch the Statement of Applicability:
`StatementOfApplicability.Resolve` projects the organisation tree over scopes and
reads no controls, so SoA is unaffected. The doc comment on `Control` is reworded
from "a requirement under one or more standards" (which now conflicts with the
distinct `Requirement` kind) to "an implemented control mapped to one or more
requirements".

### D3. Cardinality: one Standard per Requirement; global id identity

A `Requirement` names exactly one owning `Standard` via the singular `standard`
field (not a list). Rationale: a published requirement is text belonging to one
standard; which controls satisfy it is a separate mapping concern (`Control.maps_to`,
now targeting requirements), not requirement ownership. Single ownership gives a
simple `requirements.standard_id` foreign key and a simple validation rule, and
mirrors `Scope.standard`.

Requirement ids are global identity strings, deduplicated within the Requirement
kind exactly like every existing kind (the validator's per-kind duplicate check).
The persistence primary key is the global id, so ids must be unique across all
requirements regardless of standard.

Requirement numbering maps to ids as `req-<standard-slug>-<theme-slug>-NN`, for
example `req-ce-plus-firewalls-01`. `NN` is a 2-digit zero-padded per-theme
sequence so ids sort correctly under binary/ordinal id order even when a theme has
more than nine requirements (User Access Control has eleven; zero-padding keeps the
ordering stable). The scheme deliberately does NOT embed the
scheme version: `version` lives on `Standard.version`, and an immutable id must not
churn when the scheme is revised. Identity is the whole string.

### D4. Standard metadata: version/authority required, publisher/source_url optional

`Standard` gains four metadata fields:

- `version` (required, free string) - the scheme version, e.g. `"3.3"`.
- `authority` (required, free string) - the body that owns the scheme, e.g. the
  National Cyber Security Centre.
- `publisher` (optional, free string) - the delivery/certification body, e.g. the
  IASME Consortium. Distinct from `authority` because for CE+ these are two
  different real-world organisations.
- `source_url` (optional, absolute http/https URL) - the official source.

Validation (required vs optional differ):

- `version` and `authority` are required and must be non-empty; a Standard that
  omits either (or sets it to a whitespace-only value) fails validation with a
  diagnostic naming the field. These keep the non-empty rule.
- `publisher` is optional and treated as blank==absent: an omitted or
  whitespace-only value is not an error and carries no non-empty rule.
- `source_url` is optional and treated as blank==absent: the well-formed absolute
  `http`/`https` URI check runs only when it is present and non-empty; an omitted or
  whitespace-only value is not an error.

Optional fields (`Standard.publisher`, `Standard.source_url`,
`Requirement.guidance`) are NOT normalized by the validator. The validator only
returns diagnostics and never mutates the config (see the "never throw or print"
requirement), so the empty-or-whitespace-to-null normalization happens in the
read/persist mapping layer: `ImportPlan` flattening for the write path and the
row-to-model mapping for the read path, exactly as `Organisation.parent` is mapped
(`string.IsNullOrEmpty(parent) ? null : parent`). An absent value is stored and
read as NULL. The URI-format check on `source_url` still runs in the validator, but
only when the field is present and non-empty.

Rationale: the point of this change is to make `Standard` a real, described object;
requiring `version` and `authority` enforces that. `publisher` and `source_url`
stay optional because not every standard models a separate delivery body or a
canonical URL. This does break pre-1.0 thin `Standard` YAML that omits
`version`/`authority` - acceptable with a clear diagnostic. It does NOT require
fabricating data: the DB columns are nullable (D6), so legacy rows read null until
re-synced, and the example placeholders are upgraded with honest real metadata
(D11), not defaults.

### D5. Backward compatibility (pre-1.0 contract)

- The existing `std-cyber-essentials` and `std-soc2` placeholders are kept but
  upgraded with honest `version`/`authority` metadata (now required). CE+ is a
  distinct standard (`std-cyber-essentials-plus`), added alongside. The example
  controls are remapped: their `maps_to` now names `Requirement` ids (see D11).
- Migration `008` adds nullable metadata columns on `standards` and the new
  `requirements` table (additive, no existing rows rewritten) and swaps
  `control_standards` for `control_requirements` (the join table is replaced, not
  migrated; pre-1.0, no join rows are carried over). Pre-migration `standards` rows
  read back with null metadata; a later `gitops sync` from a config that now
  supplies `version`/`authority` populates them and rebuilds the join.
- Wire contract: the `standards` read gains nullable `version`/`authority`/
  `publisher`/`source_url` fields; the `controls` read `maps_to` now carries
  `Requirement` ids (shape unchanged, ids retargeted); the `compliance/status`
  `persisted` object gains a `requirements` count. The metadata and count are
  additive; the `maps_to` retarget is a value change, not a shape change. Pre-1.0,
  no deprecation window is owed.
- Config contract: thin `Standard` YAML that omits `version`/`authority` now fails
  validation, and a `Control` whose `maps_to` names `Standard` ids now fails
  validation (it must name `Requirement` ids). Pre-1.0 (`v1alpha1`), a breaking
  validation tightening is acceptable; the diagnostic tells the author what to fix.

### D6. Persistence shape (migration 008)

`008_standard_requirements.sql` (forward-only, binary collation on identifiers).
The DDL below is schematic shorthand for the shape; the migration file uses the
repo's standard `CHARACTER SET utf8mb4 COLLATE utf8mb4_bin` form on identifier
columns and spells out every column type, matching `001..007`:

- `ALTER TABLE standards ADD COLUMN version VARCHAR(64) NULL, ADD COLUMN authority
  VARCHAR(512) NULL, ADD COLUMN publisher VARCHAR(512) NULL, ADD COLUMN source_url
  VARCHAR(2048) NULL;` (additive, nullable; existing rows get null).
- `CREATE TABLE requirements (id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin PK, api_version, title,
  standard_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, theme VARCHAR(190) NOT NULL,
  statement TEXT NOT NULL, guidance TEXT NULL, citation_label VARCHAR(512) NOT
  NULL, citation_url VARCHAR(2048) NOT NULL, created_at DATETIME(6), updated_at
  DATETIME(6), KEY ix_requirements_standard_id (standard_id), CONSTRAINT
  fk_requirements_standard FOREIGN KEY (standard_id) REFERENCES standards (id) ON
  DELETE RESTRICT) ENGINE=InnoDB;`

`ON DELETE RESTRICT` matches the scopes/organisations pattern: the importer prunes
referencing rows before deleting a standard rather than relying on cascade.
`id` and `standard_id` use `utf8mb4_bin` so requirement identity is exact-byte,
consistent with Core's ordinal id semantics.

`008` also repoints the control join. It `DROP TABLE control_standards` and
`CREATE TABLE control_requirements (control_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
requirement_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, PRIMARY KEY (control_id,
requirement_id), KEY ix_control_requirements_requirement_id (requirement_id),
CONSTRAINT fk_control_requirements_control FOREIGN KEY (control_id) REFERENCES
controls (id) ON DELETE CASCADE, CONSTRAINT fk_control_requirements_requirement
FOREIGN KEY (requirement_id) REFERENCES requirements (id) ON DELETE CASCADE)
ENGINE=InnoDB;`. `control_requirements` must be created after `requirements`. The
`ON DELETE CASCADE` on both FKs mirrors the dropped `control_standards` join, so
the importer's whole-set delete-and-insert of join rows and its domain-row prune
behave as before. Dropping `control_standards` is a pre-1.0, forward-only break; no
existing join rows are migrated (the join now targets a different table).

### D7. Importer order and plan

`ImportPlan` gains a `Requirements` list (`RequirementRowPlan` with id,
api_version, title, standard, theme, statement, guidance, citation_label,
citation_url). Because `standards` now carries metadata, standards can no longer
share the generic `DomainRow` upsert with controls: add a `StandardRowPlan` (id,
api_version, title, version, authority, publisher, source_url) and a dedicated
`UpsertStandardsAsync`; controls keep the generic `DomainRow` upsert. This
flattening is where the optional fields are normalized: `publisher`, `source_url`,
and `RequirementRowPlan.guidance` map an omitted-or-whitespace-only value to null
(the same `string.IsNullOrEmpty(...) ? null : ...` mapping already used for
`Organisation.parent`), so an absent optional field persists as NULL. `ImportPlan`
drops `ControlStandardRow` and gains `ControlRequirementRow (ControlId,
RequirementId)`, flattened from `Control.maps_to` (still `Distinct()` to guard the
composite-PK join against a duplicate id within one control).

Importer transaction order (extending the existing FK-safe sequence):

1. Upsert standards (with metadata).
2. Upsert requirements (reference standards; after standards, before any standard
   deletion).
3. Upsert controls, then organisations (parent-before-child).
4. Prune absent scopes, then upsert scopes (prune-then-upsert preserves rename
   safety for a same-`(organisation, standard)` scope; see the persistence spec).
5. Replace `control_requirements` join rows (delete all, then insert the new set).
   Both controls and requirements exist by now.
6. Delete absent rows FK-safe: scopes (done via step 4 prune), requirements before
   standards, organisations child-before-parent, controls, standards.

Requirements are upserted after standards and deleted before standards so removing
a standard that still had requirements in the prior state does not hit the
`RESTRICT` FK. `ImportPlan` is pure (no DB), so the ordering and flattening stay
unit-testable without MySQL.

### D8. Read store, read models, counts

- New read model `RequirementRow(Id, Title, Standard, Theme, Statement, Guidance,
  CitationLabel, CitationUrl)`. `Guidance` is nullable.
- `StandardRow` gains nullable `Version`, `Authority`, `Publisher`, `SourceUrl`.
- `ControlRow.MapsTo` now holds `Requirement` ids (unchanged type
  `IReadOnlyList<string>`); `GetControlsAsync` joins `control_requirements` instead
  of `control_standards`, ordered by `control_id, requirement_id`.
- `ComplianceCounts` gains `Requirements` in positional order
  `(Standards, Controls, Requirements, Organisations, Scopes)`, matching the web
  `persisted` JSON key order. Every `new ComplianceCounts(...)` call site (store,
  integration test, web fake) is updated for the widened record.
- `IComplianceStore` gains `GetRequirementsAsync`; `GetStandardsAsync` selects the
  new columns; the counts query adds `(SELECT COUNT(*) FROM requirements)`.
- Reads stay deterministically ordered by `id` (ordinal/binary), matching the
  existing store.

### D9. Web read surface

- `GET /api/v1/freeboard/requirements`: returns requirements as
  `{ id, title, standard, theme, statement, guidance, citation: { label, url } }`,
  ordered by id. The `citation` object is composed in the endpoint projection from
  the `citation_label`/`citation_url` columns. On an unreachable store it returns
  RFC 7807 / HTTP 503, like the other reads.
- `GET /api/v1/freeboard/standards`: response objects gain `version`, `authority`,
  `publisher`, `source_url` (null when unset).
- `GET /api/v1/freeboard/controls`: the `maps_to` array now carries `Requirement`
  ids resolved from `control_requirements` (shape unchanged; ids retargeted).
- `GET /api/v1/freeboard/compliance/status`: the `persisted` object gains
  `requirements` (integer when reachable, `null` on outage, consistent with the
  existing degrade-to-null behaviour).
- Requirements read requires an authenticated user, GET-only, unaffected by
  read-only mode - identical to the existing reads.
- `FakeComplianceStore` (web test double) gains settable requirements, standard
  metadata, and the requirements count so `dotnet test` stays green without MySQL.

### D10. CLI

`GitOpsCommands` summary/dry-run/sync output gains a requirements count and a
"Requirements" section in the planned-state print. This is output text only; no
new command and no spec-level behaviour change. The CLI stays EE-free and
cross-platform (it already references only Core and Persistence).

### D11. CE+ fixture content

The fixture adds one Standard and the full CE+ requirement set. CE+ shares the
technical control requirements of Cyber Essentials (the independently audited
tier); the statements below are the five themes' controls, source-traced
control-by-control from the "Requirements by technical control theme" section of
the publicly published NCSC/IASME "Cyber Essentials: Requirements for IT
Infrastructure v3.3" (April 2026; the verified self-assessment adopts the name
Danzell at this version), faithfully paraphrased and not copied verbatim (the
source is UK Crown Copyright).

Standard:

```yaml
apiVersion: freeboard.io/v1alpha1
kind: Standard
id: std-cyber-essentials-plus
title: Cyber Essentials Plus
version: "3.3"
authority: National Cyber Security Centre
publisher: IASME Consortium
source_url: https://www.ncsc.gov.uk/files/cyber-essentials-requirements-for-it-infrastructure-v3-3.pdf
```

`source_url` is the canonical requirements document. Every requirement's
`citation_url` is that same document
(`https://www.ncsc.gov.uk/files/cyber-essentials-requirements-for-it-infrastructure-v3-3.pdf`)
so the Standard source and every citation are consistent; each `citation_label` is
the theme within that document, e.g.
`Cyber Essentials: Requirements for IT Infrastructure v3.3 - Firewalls`.

Requirement set (id -> statement; `title` is a short label; `guidance` optional):

Firewalls (7):

1. `req-ce-plus-firewalls-01` - Protect every in-scope device with a correctly
   configured firewall, or a network device that provides firewall functionality.
2. `req-ce-plus-firewalls-02` - Change default administrative passwords on
   firewalls to strong, unique passwords, or disable remote administrative access.
3. `req-ce-plus-firewalls-03` - Block access to the firewall administrative
   interface from the internet unless there is a documented business need; where
   access is allowed, protect it with multi-factor authentication or an IP allow
   list combined with managed password authentication.
4. `req-ce-plus-firewalls-04` - Block unauthenticated inbound connections by
   default.
5. `req-ce-plus-firewalls-05` - Ensure each inbound firewall rule is approved and
   documented by an authorised person and records the business need.
6. `req-ce-plus-firewalls-06` - Remove or disable firewall rules once they are no
   longer needed.
7. `req-ce-plus-firewalls-07` - Use a software firewall on devices used on
   untrusted networks such as public wifi.

Secure Configuration (8):

1. `req-ce-plus-secure-configuration-01` - Remove or disable unnecessary user
   accounts, including guest accounts and administrative accounts that will not be
   used.
2. `req-ce-plus-secure-configuration-02` - Change default or easily guessed
   account passwords before a device or service is used.
3. `req-ce-plus-secure-configuration-03` - Remove or disable unnecessary software,
   including applications, system utilities, and network services.
4. `req-ce-plus-secure-configuration-04` - Disable auto-run features that execute
   files without user authorisation.
5. `req-ce-plus-secure-configuration-05` - Authenticate users before granting them
   access to organisational data or services.
6. `req-ce-plus-secure-configuration-06` - Require a device-unlocking credential
   (biometric, password, or PIN) before a physically present user can reach a
   device's services.
7. `req-ce-plus-secure-configuration-07` - Protect device-unlocking authentication
   against brute force by throttling attempts or locking after no more than ten
   unsuccessful attempts.
8. `req-ce-plus-secure-configuration-08` - Enforce device-unlocking credential
   quality: at least six characters for unlock-only credentials, or the full
   user-access password requirements when the credential is also used for
   authentication.

Security Update Management (5):

1. `req-ce-plus-security-update-management-01` - Ensure all in-scope software is
   licensed and supported by the vendor.
2. `req-ce-plus-security-update-management-02` - Remove unsupported software from
   in-scope devices, or move it out of scope by isolating it from all internet
   traffic.
3. `req-ce-plus-security-update-management-03` - Enable automatic updates where the
   vendor provides them.
4. `req-ce-plus-security-update-management-04` - Apply updates for vendor-declared
   critical or high-risk vulnerabilities, or vulnerabilities with a CVSS v3 base
   score of 7 or above, within 14 days of release.
5. `req-ce-plus-security-update-management-05` - Apply updates within 14 days of
   release when the vendor gives no vulnerability severity detail.

User Access Control (11):

1. `req-ce-plus-user-access-control-01` - Operate a documented process to create
   and approve user accounts.
2. `req-ce-plus-user-access-control-02` - Authenticate users with unique
   credentials before granting access to applications or devices.
3. `req-ce-plus-user-access-control-03` - Remove or disable user accounts when they
   are no longer required, such as when a user leaves or after a defined period of
   inactivity.
4. `req-ce-plus-user-access-control-04` - Implement multi-factor authentication
   where available, and always for authentication to cloud services.
5. `req-ce-plus-user-access-control-05` - Use separate accounts for administrative
   activities only, keeping email, web browsing, and other standard-user
   activities off administrative accounts.
6. `req-ce-plus-user-access-control-06` - Remove or disable special access
   privileges when they are no longer required, such as on a role change.
7. `req-ce-plus-user-access-control-07` - Protect passwords against brute-force
   guessing using at least one of multi-factor authentication, attempt throttling,
   or lockout after no more than ten unsuccessful attempts.
8. `req-ce-plus-user-access-control-08` - Manage password quality using at least
   one of multi-factor authentication, a minimum length of twelve characters, or a
   minimum length of eight characters with automatic blocking of common passwords
   through a deny list.
9. `req-ce-plus-user-access-control-09` - Support users in choosing unique
   passwords by educating them against common passwords, promoting longer
   multi-word passwords, providing secure password storage, and not enforcing
   regular expiry or complexity rules.
10. `req-ce-plus-user-access-control-10` - When multi-factor authentication uses a
    password as one factor, require that password to be at least eight characters
    with no maximum length limit.
11. `req-ce-plus-user-access-control-11` - Operate an established process to change
    passwords promptly whenever a password or account is known or suspected to be
    compromised.

Malware Protection (4):

1. `req-ce-plus-malware-protection-01` - Ensure an active malware protection
   mechanism on every in-scope device, using at least one of anti-malware software
   or application allow-listing.
2. `req-ce-plus-malware-protection-02` - Where anti-malware software is used, keep
   it updated in line with vendor recommendations and configure it to prevent
   malware and malicious code from running.
3. `req-ce-plus-malware-protection-03` - Where anti-malware software is used,
   configure it to block connections to known malicious websites.
4. `req-ce-plus-malware-protection-04` - Where application allow-listing is used,
   actively approve applications before deployment, maintain a current approved
   list, and prevent execution of unsigned or invalidly signed applications.

Total: 35 requirements (Firewalls 7 + Secure Configuration 8 + Security Update
Management 5 + User Access Control 11 + Malware Protection 4).

Example controls remap (`examples/gitops/controls.yaml`). Because `maps_to` now
names `Requirement` ids and `std-soc2` has no requirements, the existing
`ctrl-mfa -> std-soc2` mapping cannot become a requirement mapping. Rather than
invent SOC 2 requirements (out of scope; only CE+ is authored), the example
controls map to specific CE+ requirements and the SOC 2 link is dropped:

- `ctrl-mfa.maps_to` -> `[req-ce-plus-user-access-control-04]` (the MFA
  requirement). The former `std-soc2` link is removed.
- `ctrl-patching.maps_to` -> `[req-ce-plus-security-update-management-04,
  req-ce-plus-security-update-management-05]` (the 14-day update requirements).

`std-soc2` remains a defined `Standard` (now with `version`/`authority` metadata);
it simply has no requirements and no control mapping until SOC 2 requirements are
authored in a later change.

## Risks / Trade-offs

- [Stale wording / conformance risk] The CE+ statements paraphrase a live public
  scheme that is revised roughly yearly (v3.3 went live 2026-04-27, when the
  verified self-assessment took the name Danzell). ->
  Store the scheme `version` on the Standard and cite the source per requirement,
  so a future bump is a fixture edit, not a schema change. The fixture is labelled
  an example, not a certification.
- [Copyright] The requirements document is UK Crown Copyright. -> Statements are
  Freeboard-authored paraphrases of the discrete controls, not copied text, and
  each cites the official source. No verbatim scheme text is stored.
- [Breaking config tightening] Making `version`/`authority` required rejects
  pre-1.0 thin `Standard` YAML. -> Pre-1.0 (`v1alpha1`); the validator diagnostic
  names the missing field; example placeholders are upgraded with honest metadata;
  DB columns stay nullable so pre-migration rows survive.
- [Wire additivity] Adding `requirements` to the `persisted` status object and
  metadata to the standards read changes response shapes. -> Additive only, pre-1.0;
  existing consumers ignore unknown fields. No key is removed or renamed.
- [FK deletion ordering] Requirements reference standards with `RESTRICT`, so a
  bad prune order would fail import when a standard is removed. -> Prune
  requirements before standards in the importer; cover with a persistence
  integration test that removes a standard that had requirements.
- [Control repoint blast radius] Repointing `Control.maps_to` from `Standard` to
  `Requirement` ids touches validation, migration `008`, the importer join, the
  read model, the web `GET /controls` output, and the example controls. -> It does
  NOT touch SoA (SoA reads scopes and organisations, not controls). Cover with
  config tests (control resolves to a requirement; dangling control->requirement
  rejected) and an importer round-trip test over `control_requirements`.
- [Two title-like fields] `title` plus `statement` on Requirement risks
  confusion. -> Document the split (title = short label, statement = normative
  text) in the XML doc; the fixture demonstrates it.
- [Migration correctness] `008` adds nullable `standards` columns and the
  `requirements` table (additive, no data rewrite) and drops `control_standards`
  for `control_requirements` (not additive: pre-1.0, no join rows are migrated; the
  join is rebuilt on the next `gitops sync`). Forward-only and checksum-tracked like
  `001..007`. Covered by the fresh-database migration test asserting the new tables,
  columns, collations, and FKs, and the absence of `control_standards`.

## Migration Plan

1. Ship code and `008_standard_requirements.sql`.
2. Operators run `freeboard system migrate` (web never auto-migrates). `008` adds
   the nullable `standards` columns and the `requirements` table, and swaps
   `control_standards` for `control_requirements`; existing standard/control rows
   are untouched (only the join table is replaced).
3. `freeboard gitops sync` imports the CE+ fixture (or any config with
   requirements) into the new table and rebuilds the `control_requirements` join.
   Configs whose standards omit `version`/`authority` now fail validation and must
   add those fields; configs whose controls still `maps_to` `Standard` ids now fail
   validation and must retarget to `Requirement` ids.
4. Rollback: pre-1.0, roll back by deploying the prior build and, if needed,
   dropping the `requirements` table and the four `standards` columns and restoring
   `control_standards`. No data contract is owed; the prior build's join is rebuilt
   on its next sync. The `standards` columns and `requirements` table are additive,
   so a prior build ignoring them keeps working, but the join-table swap means a
   code-only rollback also needs `control_standards` back.

## Open Questions

Decided by the mediator and folded into the design (no longer open): the
`Control.maps_to` repoint to `Requirement` ids (D2); `citation_url` required (D2);
the `requirements` count in `compliance/status` (D9); and the free-string `theme`
with no per-standard vocabulary (D1). A future multi-standard change may still add
an optional declared theme vocabulary for integrity, judged over-engineering for
one standard now.

1. CE+ requirement granularity: the fixture decomposes the v3.3 document into 35
   discrete requirements (7/8/5/11/4), traced control-by-control from the published
   "Requirements by technical control theme" section. This is a faithful, non-padded
   reading; another reviewer might merge or split a few (for example the
   secure-configuration device-unlocking trio, or the two security-update 14-day
   conditions). Confirm the granularity is acceptable.
