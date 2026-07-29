## Why

Object model v2 unified every subject into one `Asset` tree (#125) and every
disposition into one `Scope` table (#127), but the two rules that CONSUME that tree
still run over the old organisation-only shape. Statement-of-Applicability
resolution walks a `Company`/`Department`-filtered list, so a scope whose subject is
a `Machine` is stored, validated, and readable but contributes NO disposition and
inherits nothing from the department it hangs under. Read-access is the mirror
image: the accessible set is a set of ORGANISATION ids, and every non-organisation
surface re-derives readability for itself - `/scopes` switches on a
`SubjectType` string carried by a `LEFT JOIN`, `/vendors` and the vendor register
page each test the `owner` edge by hand. One tree, three hand-rolled walks over it.

This change moves both rules onto the one `Asset` tree: resolution walks `parent`
edges, read-access is the closure of the caller's grant-rooted organisation set over
`parent` and `owner`. That is the last piece the v2 cutover needs before the group
axis (#123) can attach a third precedence rank to a resolver that already resolves
per asset.

This is a pre-production HARD CUTOVER: there is no data contract to preserve, so the
read models change shape in place with no compatibility layer.

## What Changes

### Resolution

- **BREAKING** Statement-of-Applicability resolution runs over the unified asset
  tree instead of the organisation list. Its node set is every `Company` and
  `Department` asset, plus every other asset whose `parent` chain reaches one:
  `Machine` assets, declared and discovered alike, become nodes. A `Machine` inherits
  its department's disposition and MAY carry its own leaf scope. No organisation is
  ever dropped, whatever its `parent` says - a dangling `parent` or a `parent` cycle
  is a non-blocking GitOps warning, so both states occur in a validly synced
  deployment and both keep resolving as they do today. Only a non-organisation asset
  needs a root; one that reaches none is absent rather than defaulted `In`.
- Vendors are excluded from inheritance without a vendor-specific branch: a vendor is
  not organisation-typed, and it carries `owner`, never `parent`, so it satisfies
  neither arm of the node rule. The flatness the old `VendorScope` encoded now falls
  out of the graph.
- Precedence is unchanged in substance and re-stated over assets: (1) a scope on the
  asset itself, (2) the nearest ancestor up the `parent` chain, (3) the default `In`.
- **BREAKING** The resolution-source enum becomes `asset | inherited | default`. The
  wire value `explicit` is renamed to `asset` on the Statement-of-Applicability JSON
  endpoint and the page, for the standard layer and the requirement layer alike. The
  enum grows a `group` value with #123.
- Requirement-level scoping still applies only where the requirement's standard
  resolves `In` at that node; an `Out` standard is followed and requirement-target
  scopes are not consulted under it. Unchanged, now evaluated per asset.

### Read-access

- **BREAKING** The single accessibility seam returns an ACCESSIBLE ASSET set, not an
  accessible organisation set. `IOrgAccess`/`AuthzOrgAccess`/`AllOrgAccess` become
  `IAssetAccess`/`AuthzAssetAccess`/`AllAssetAccess`. Grants stay rooted on
  organisations (`authz_organisation_role_assignments` is untouched); the seam
  computes today's read-subtree union over organisations and then closes it over the
  asset tree: an asset with a `parent` is accessible when its inclusive `parent` chain
  intersects that union, an asset with an `owner` is accessible when its `owner` is in
  it, and an edgeless asset is accessible only when it is itself in it. Retired
  discovered assets are excluded from the set, so they are no caller's authorization
  anchor.
- The three hand-rolled readability rules collapse into one set-membership test.
  `ComplianceEndpoints.SubjectReadable`'s per-type switch, the `/vendors` owner
  filter, and the vendor register page's owner filter are all replaced by
  "is the subject/vendor id in the accessible asset set".
- Global vendor readability stays dropped; a vendor whose `owner` is missing,
  dangling, or outside the caller's set remains invisible, fail-closed.
- **BREAKING** The Statement-of-Applicability drill-down shows a collector's vendor by
  TITLE only when that vendor is in the caller's accessible asset set, and shows nothing
  otherwise. Today it falls back to the raw vendor id when no vendor asset matches -
  which is precisely the hidden-vendor case, since a vendor with a null or dangling
  `owner` is exactly the one the caller cannot resolve. The page never renders a raw
  vendor id.
- **BREAKING** The vendor id stops leaking through the two GLOBAL reference reads.
  `/collectors` and `/integration-connections`, and the collector register and
  integration-connections pages, emit a `vendor` only when that vendor is in the
  caller's accessible asset set and emit `null` otherwise. Their ROWS stay global -
  collectors and connections have no organisation dimension - so this narrows one
  field, not the endpoint. Without it the acceptance criterion "a caller cannot read a
  vendor outside its owner's subtree" is not met, because a hidden vendor's id is still
  readable from a collector row today (the code comment above `/vendors` says so).
- The Statement-of-Applicability JSON endpoint narrows its nodes by the accessible
  ASSET set, so a readable `Machine` node is returned and an unreadable one is not.

### Write-path authorization

- The authorizer's shared ancestry walk moves onto the same asset tree, so an id it could
  not resolve before - a `Machine`, say - now resolves through its `parent` chain into the
  organisation tree. Every organisation-scoped route and page handler therefore anchors on a
  chain that STOPS at the first non-organisation asset, which is exactly where the
  organisation-only walk stops today. That covers both ways in: a supplied `Machine` id, and
  an organisation whose own `parent` names one. Without it, a caller holding `org.write` or
  `compliance.scope.write` on the organisation beyond that machine would pass gates that
  today refuse everyone but a super-admin. The guard reinstates exactly the pre-change
  outcome, so no write that succeeds today starts failing and none that fails starts
  succeeding, and the refusal is decided at the authorization layer with the status the route
  gives today rather than becoming a silent no-op or a store-decided `404`.

### Evidence ingest

- Evidence ingest's in-scope gate resolves through the SAME moved resolver, so ingest
  and the read surfaces cannot disagree about a node's disposition. Its ACCEPTANCE set
  is unchanged: a posted `organisation_id` must still resolve to a `Company` or
  `Department` node that is `In` for the requirement, and a `Machine` id is still
  rejected. Machine-level evidence would have to move `evidence_runs.organisation_id`,
  the idempotency key, and the evidence roll-ups with it, and is out of scope.

### Reads

- **BREAKING** `IComplianceStore.GetOrganisationsAsync` and `GetVendorsAsync` are
  replaced by one `GetAssetsAsync` returning every asset row with its `type`,
  `source`, `state`, `parent`, and `owner`. `OrganisationRow` and `VendorRow` are
  replaced by one `AssetNode`. `/organisations` and `/vendors` keep their response
  shapes and project from the one read.
- **BREAKING** `ScopeRow` loses its five server-side subject-narrowing fields
  (`SubjectType`, `SubjectSource`, `SubjectState`, `SubjectParent`, `SubjectOwner`)
  and the scope read loses its `LEFT JOIN assets`: readability is now one lookup into
  the accessible asset set. The `/scopes` response shape is unchanged (it never
  exposed those fields).
- `SoaInputs` and `SoaDrilldownInputs` carry `Assets` in place of `Organisations`,
  `Vendors`, and the separate `ResolvableAssetIds` query; the live-subject predicate
  and the vendor title lookup both derive from the one asset list, so the
  dangling-subject warning keeps its meaning with one less query in the snapshot.
- The shared inclusive-ancestry helper `OrgAncestry` becomes `AssetAncestry` over
  `AssetNode`, so the authorizer, the resolver, and the read-access closure keep
  walking the same cycle-guarded parent chain and cannot diverge.
- Web and CLI read models ship together, and the CLI needs no SOURCE change. It has no
  Statement-of-Applicability reader to update, and the four payloads it binds keep their
  shapes: `/vendors` and `/scopes` narrow server-side, and `collector list` and
  `connections list` already print `-` for a null vendor, which is what the endpoint now
  sends for one the caller cannot read (`ApiCollector.Vendor` and
  `ApiIntegrationConnection.Vendor` are already nullable). Parity is verified by test,
  not implemented as a new command; the CLI tests gain the missing assertions on that
  rendered column.

## Capabilities

### New Capabilities

None. The change reworks existing capabilities; it introduces no new spec folder.

### Modified Capabilities

- `statement-of-applicability`: resolution moves from the organisation list to the
  asset tree - every organisation plus whatever hangs under one - so `Machine` nodes
  resolve and inherit and vendors are excluded; the resolution-source enum's `explicit` value becomes
  `asset`; the JSON endpoint's node set and its authorization narrowing move to the
  accessible ASSET set; the dangling-subject scan derives its live-subject predicate
  from the asset list rather than a separate read; and the drill-down shows a
  collector's vendor title only when the caller may read that vendor, never a raw
  vendor id.
- `authz-enforcement`: the compliance-read narrowing seam resolves an accessible
  ASSET set - the grant-rooted organisation read-subtree union closed over the asset
  `parent` chain and the vendor `owner` edge - instead of an accessible organisation
  set, and the narrowed-read enumeration is re-stated in terms of it. No permission,
  grant root, or rollout mode changes. The capability also gains the invariant that an
  organisation gate anchors only on organisation ancestry: the authorizer's ancestry walk
  now runs over the whole asset tree, so a route handed a `Machine` id - or an organisation
  parented onto one - would otherwise be satisfied by a grant on the organisation beyond it.
  Anchoring on a chain that stops at the first non-organisation asset keeps the write gates
  exactly where they are today.
- `compliance-web-read`: `/scopes`, `/vendors`, and `/organisations` narrow by one
  accessible-asset-set membership test instead of three per-surface rules; `/vendors`
  is served from the unified asset read. Response shapes are unchanged.
- `compliance-persistence`: `IComplianceStore` exposes one `GetAssetsAsync` in place
  of `GetOrganisationsAsync` and `GetVendorsAsync`; the unified scope read drops its
  `LEFT JOIN assets` and the five subject-narrowing fields; the Statement of
  Applicability input snapshots carry the asset set in place of organisations,
  vendors, and the separate resolvable-asset-id read.
- `org-scope-selection`: the accessible set the selector and org-scoped views are
  bounded by is now an asset set; the selector tree and the selection resolver
  continue to present and validate organisation nodes only, by intersecting that set
  with the organisation-typed assets.
- `integration-connection`: the connections page and its JSON endpoint emit a `vendor`
  only when the caller may read that vendor, and `null` otherwise. The row set, the
  other fields, and the token-resolvable flag are unchanged.
- `collector-register`: the collector register page shows a collector's `vendor` only
  when the caller may read that vendor. The page still lists every control and every
  collector for every authenticated caller.
- `evidence-ingest`: the in-scope gate is re-stated over the asset tree while keeping
  its organisation-only acceptance, so a `Machine` id is still not an ingest subject.

## Impact

- MIT work only, no Enterprise carve-out: the change touches the domain read models
  and stores in `Freeboard.Persistence` and the resolver, seams, endpoints, ingest gate,
  and pages in `Freeboard` (web). `Freeboard.Core`, `Freeboard.Enterprise`,
  `Freeboard.Agent`, and `Freeboard.CLI` are untouched, so the reference graph and the
  one-way EE rule hold unchanged. No new package dependency.
- No schema change and no migration: `assets.parent` and `assets.owner` already exist
  (migration `019_asset_unification.sql`), so this change is read-side only. One
  CUTOVER step applies where `019` ran against legacy data: it copies vendors with a
  null `owner` to be re-authored on the next sync, and once vendor readability follows
  `owner` fail-closed, those vendors are invisible until that sync runs. See design.md,
  Migration Plan.
- API: `GET /api/v1/freeboard/statement-of-applicability/{standardId}` changes its
  `resolution` wire value `explicit` to `asset` and returns readable `Machine` nodes.
  `/organisations`, `/scopes`, and `/vendors` keep their shapes. `/organisations` and `/scopes`
  may return MORE rows than before, because the accessible set closes over `parent` with no type
  test: a caller reaches whatever hangs under an organisation it may read, and an organisation
  whose own `parent` chain runs through a non-organisation asset into another subtree becomes
  readable from that subtree. The write gate cuts the chain at that link and does not widen with
  it - a read exposes a title, a grant match confers write (design.md, D4 and D15). `/vendors`
  keeps its row set: a vendor is admitted exactly when its `owner` is in the caller's organisation
  union, which is what its hand-rolled filter tests today. `/collectors` and
  `/integration-connections` keep their shapes and row sets and may now send `null` for
  a `vendor` the caller cannot read - a field both already declare nullable.
- Docs: `docs/gitops.md` names the resolution source `explicit` in four places and
  describes the SoA projection as "every organisation node"; all are updated with the
  code.
- Tests: the web test doubles (`FakeComplianceStore`, `AllOrgAccess`) and every test
  that builds `OrganisationRow`, `VendorRow`, or a narrowing-field `ScopeRow` move to
  `AssetNode`. MySQL integration tests cover resolution and read-access over a mixed
  declared/discovered tree.

## Deployment runbook

One operator step is required, and only where migration `019_asset_unification.sql` ran
against legacy data. That migration copies each legacy vendor into `assets` with a null
`owner`. Read-access is fail-closed on that edge, so once vendor readability follows
`owner`, a vendor with no `owner` is invisible to every caller, super-admins included, and
its vendor-subject scopes are omitted from `/scopes`.

Before this build goes live on such a deployment:

1. Author an `owner` on every vendor asset in the GitOps config. `owner` names the
   organisation asset the vendor belongs to.
2. Run `freeboard gitops sync`.

`ConfigValidator` already warns on an ownerless vendor, so step 1 surfaces the gap rather
than the sync silently completing with vendors still dark. A deployment that has never run
`019` against legacy vendor rows needs neither step.

## Non-goals

- The group axis (#123). No `Group` asset type, no group membership, no `group`
  precedence rank, and no `group` resolution-source value are added here. The enum
  and the precedence walk are written so #123 inserts a rank between the asset leaf
  and the nearest ancestor without reshaping either.
- Rendering `Machine` rows on the `/compliance/statement-of-applicability` page. The
  page's four-level drill-down, its organisation selector scoping, and its
  per-collector evidence status are all keyed on an organisation node; the page keeps
  its organisation-only node set and the machine-level resolution is exposed on the
  JSON endpoint only. Extending the page is later work.
- Pagination or any other bound on the Statement-of-Applicability JSON node set. The
  endpoint may now return one node per readable machine; bounding a large fleet is a
  separate change (see design.md, R2).
- Control-level disposition resolution for organisation subjects, still deferred from
  the scope generalization: an org subject may target a control and the row is stored
  and read back, but it contributes no node disposition.
- Moving authorization GRANTS onto assets. Roles are still assigned on organisations;
  only the derived read-access closure spans the asset tree.
- Adding a Statement-of-Applicability read command to the CLI. The CLI has no such
  reader today, and adding one means a nested DTO tree, a parser, a command, a renderer,
  and their tests for an acceptance criterion already observable on the JSON endpoint.
  The parity rule is satisfied by the CLI reads this change does affect.
- Narrowing the ROWS of `/collectors` and `/integration-connections`. They have no
  organisation dimension, so row-level narrowing is a confidentiality model for
  reference data - it would have to answer what a zero-grant operator and the scheduler
  see - and is a separate change. Only the vendor id is closed here; a collector's
  `title` or `config` and a connection's `provider` or `base_url` may still imply a
  vendor, which is an accepted residual (design.md, D9 and R8).
- Accepting a `Machine` as an evidence-ingest `organisation_id`. Ingest moves onto the
  same resolver but keeps its organisation-only acceptance set.
- Narrowing `GET /compliance/status`. Its counts stay global, vendors and collectors
  included. A count discloses how many vendors exist, not which; it carries no id, no
  title, and no `owner`, so it is not vendor readability. Deriving per-caller counts
  would be a second authorization rule over a summary endpoint to withhold an integer.
- Adding an `owner` field to `GET /vendors`. It keeps its `{ id, title }` shape. The
  field now sits in the read model it is served from, but no surface asks for it and
  narrowing is done server-side, so publishing the authorization anchor needs a reason
  rather than a default.
- `Person` assets, discovery-ingest changes, and collector retargeting.
