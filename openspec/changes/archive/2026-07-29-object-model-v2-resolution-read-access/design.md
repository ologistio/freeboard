## Context

### What is already built

The asset spine (#125) and the scope generalization (#127) are merged and archived.
Confirmed against the code, not the RFC:

- `assets` (migration `019_asset_unification.sql`) is one table for every asset:
  `id`, `type`, `source`, `title`, the two nullable scalar edges `parent` and
  `owner`, and the discovered-only columns. `MySqlComplianceStore` already reads
  `owner` (`SELECT id, title, owner FROM assets WHERE type = 'Vendor'`) into
  `VendorRow.Owner`, and `parent` into `OrganisationRow.Parent`. **The vendor `owner`
  edge EXISTS; this change does not add it and needs no migration.**
- `scopes` (migration `020_scope_generalization.sql`) is one table with a scalar
  no-FK `subject_id` and one of three target columns.
- `MySqlComplianceStore.ScopeSelect` already `LEFT JOIN`s `assets` to carry
  `SubjectType`/`SubjectSource`/`SubjectState`/`SubjectParent`/`SubjectOwner` on
  `ScopeRow`, purely to feed `ComplianceEndpoints.SubjectReadable`.
- `ComplianceEndpoints.SubjectReadable` already switches on `SubjectType` with three
  branches (org via the accessible set, vendor via `owner`, everything else via
  `parent` ancestry). `/vendors` and `Pages/Compliance/Vendors.cshtml.cs` each apply
  their own `Owner is not null && accessible.Contains(Owner)` test.
- `ComplianceWriteEndpoints` reads both deleted shapes on its AUTHORIZATION path:
  `OrganisationPutSelector` and `UpsertOrganisationAsync` call `GetOrganisationsAsync`,
  and the four scope write handlers narrow their stored-owner lookup with
  `IsOrgSubject`, which reads `ScopeRow.SubjectType`.

### What is still on the old shape

- `StatementOfApplicability.Resolve` and `ResolveDrilldown` take
  `IReadOnlyList<OrganisationRow>`, sourced from
  `assets WHERE type IN ('Company','Department')`. A `Machine` is never a node, so a
  machine-subject scope resolves nothing and a machine inherits nothing.
- `SoaResolution` is `Explicit | Inherited | Default`, wire `explicit | inherited |
  default`.
- `IOrgAccess.AccessibleOrgIdsAsync(user, IReadOnlyList<OrganisationRow>)` returns
  organisation ids. `AuthzOrgAccess.ReadSubtreeUnion` walks `OrganisationRow.Parent`
  from the caller's `compliance.read` grant roots.
- `OrgAncestry.InclusiveAncestors` walks `OrganisationRow` and is shared by
  `Authorizer.ResolveAncestryAsync`, the resolver, and `SubjectReadable`.
- `EvidenceIngestEndpoints.IsOrganisationInScope` calls the SAME
  `StatementOfApplicability.Resolve(soa.Organisations, ...)` to decide whether a posted
  `organisation_id` is in scope for a requirement. The ingest gate therefore moves with
  the resolver whether or not the plan mentions it.
- `StatementOfApplicability.BuildControlCatalogue` renders a check's vendor as the
  vendor's TITLE and falls back to the raw vendor id when no vendor matches, and the
  drill-down page renders whatever that produces. The fallback fires on exactly the
  vendor a caller should not see.
- `/collectors` and `/integration-connections` are deliberately un-narrowed and each
  projects a raw `vendor` id. The `/vendors` comment in `ComplianceEndpoints` already
  records the consequence in as many words: those two endpoints "still expose a hidden
  vendor's id". `docs/gitops.md` and the two capability specs document the same.
- `docs/gitops.md` describes standard-scope provenance as `explicit` and the SoA
  projection as "every organisation node".

### Constraints

- MIT only. Read models and stores in `Freeboard.Persistence`, resolver/seams/
  endpoints/pages in `Freeboard` (web). No `Freeboard.Enterprise` reference from
  either, per the one-way EE rule. `Freeboard.Core`, `Freeboard.Agent`, and
  `Freeboard.CLI` are not touched, so the cross-platform community components are
  unaffected.
- Pre-production cutover: no wire-compatibility layer, no dual-read, no converter.
- `dotnet test` must stay green with no MySQL; MySQL-backed assertions go in
  `tests/Freeboard.Persistence.Tests` behind `FREEBOARD_TEST_DB`.

## Goals / Non-Goals

**Goals:**

- One resolution rule, over `parent` edges on the unified asset tree, that resolves a
  disposition and a provenance for every organisation and for everything that hangs
  under one.
- One read-access rule, derived from the caller's grant-rooted organisation subtree
  union and closed over `parent` and `owner`, replacing three hand-rolled per-surface
  rules.
- One read model (`AssetNode`) and one store read (`GetAssetsAsync`) behind both
  rules, so the resolver and the access closure cannot see different trees.
- A precedence walk and a provenance enum shaped so #123 inserts the `group` rank
  without reshaping either.

**Non-Goals:**

- Everything in the proposal's Non-goals section: the #123 group axis, machine rows
  on the SoA page, node-set pagination, control-level resolution, asset-rooted grants, a
  CLI SoA command, machine-level evidence ingest, row-level narrowing of `/collectors`
  and `/integration-connections`, `Person`, discovery ingest, collector retargeting.
- Any schema change. This is read-side only.

## Decisions

### D1: One `AssetNode` read model replaces `OrganisationRow` and `VendorRow`

`Freeboard.Persistence` gains:

```csharp
public sealed record AssetNode(
    string Id, string Title, string Type, string Source, string? State,
    string? Parent, string? Owner);
```

`IComplianceStore.GetOrganisationsAsync` and `GetVendorsAsync` are replaced by one
`GetAssetsAsync` selecting every `assets` row. `OrganisationRow` and `VendorRow` are
deleted. `/organisations` and `/vendors` project from the one read, filtering by
`Type` at the endpoint, and keep their existing response shapes.

Rationale: the alternative - keep `OrganisationRow` and ADD `AssetNode` - leaves two
read models over one table and two parent walks, which is precisely the duplication
this change exists to remove (`code-as-liability.md`: "do not leave two systems doing
the same job"). The cost is a wide but mechanical rename across ~20 call sites and
their tests; the payoff is that the resolver, the access closure, the authorizer's
ancestry walk, the selector, and the write endpoints all read one list.

`State` is nullable because it is a discovered-only column; a declared asset reads
`null`, which the live-subject predicate treats as live.

`AssetNode` also carries the organisation predicate, `IsOrganisation`
(`Type is "Company" or "Department"`), and it is the only expression of that rule ON
`AssetNode`. About a dozen sites need that test after this change - the resolver's node set,
the drill-down's node filter, the org-only projections and selectors, the write-path
authorization guards, and the ingest acceptance gate - and two hand-written copies exist
already (`ComplianceWriteEndpoints.IsOrgSubject` and the `/scopes` arm of `SubjectReadable`,
both deleted here). Twelve copies of one rule is exactly the duplication
`.claude/rules/code-as-liability.md` names, and a rule that decides authorization outcomes is
the worst kind to have twelve copies of. It lives on the read model in `Freeboard.Persistence`
because every C# caller is in `Freeboard` (web) or `Freeboard.Persistence`, both of which
already see it under the reference graph.

The same two type names are spelled out in three other places, and each stays because it is
over a different row than the one the store read back, so none of them can call the predicate.
The `type IN ('Company', 'Department')` predicates in `MySqlComplianceWriteStore` and
`MySqlAuthzAdministrationStore` are SQL in a different layer (D15).
`MySqlComplianceWriteStore` also tests `locked.SubjectType` on the scope row it holds under a
write lock, which is a projection of that lock query rather than a read-model node. And
`GitOps/ImportPlan.OrganisationIds` filters `AssetRowPlan` - the parsed config row on its way
IN - to build the keep set for the org-role-assignment prune.

### D2: Resolution's node set is every organisation, plus whatever hangs under one

`StatementOfApplicability.Resolve(IReadOnlyList<AssetNode> assets, ...)` takes as its
node set every `Company` and `Department` asset, plus every other asset whose
cycle-guarded inclusive `parent` chain reaches one of them.

An organisation is therefore ALWAYS a node, whatever its `parent` says. A `Company`
whose `parent` names an id no asset defines, and a `Department` whose `parent` chain
runs into a cycle, both still resolve and still appear in the projection. Only a
NON-organisation asset needs a root: a `Machine` whose chain reaches no organisation is
not a node, so it is absent rather than defaulted `In` - fail-closed, and consistent
with the asset-model rule that an unrooted asset is visible to no caller.

That first arm is a type test in the resolver, and it is deliberate. Today's
organisation-only resolver reads `assets WHERE type IN ('Company','Department')` and
never consults `parent` to decide membership, so a dangling-parent organisation is a
node today. Making membership depend on reaching a rootless organisation would silently
drop such an organisation from the Statement of Applicability, from the evidence-ingest
gate, and from the default `In` that the opt-out scoping model guarantees every
organisation. GitOps validation is unchanged and stays permissive on exactly these
states: `ConfigValidator` reports a dangling `parent` edge and a `parent` cycle as
non-blocking warnings, so both are reachable in a validly synced deployment and the
resolver must not read either as a deletion.

Vendors still fall out with no vendor-specific branch: a vendor is not
organisation-typed, and it carries `owner`, never `parent` (the asset-model spec makes
the two mutually exclusive), so it satisfies neither arm.

Alternatives rejected:

- *Enumerate every asset and let vendors resolve `In`/`default`.* A vendor cannot
  carry a standard-target scope (the generalization forbids it), so an `In` `default`
  vendor node would assert an applicability the model does not define, and would leak
  vendor ids onto a standard-level projection.
- *Require every node, organisations included, to terminate at a rootless
  organisation.* Purer - membership by graph shape alone, with no type test - but it
  changes what a dangling-parent or cycle-parented organisation resolves to, from "a
  node carrying the default `In`" to "absent from the projection". Dropping an
  organisation from the SoA is a larger behaviour change than a type test is a blemish,
  and the two states it would drop are ones the validator explicitly tolerates.

### D3: Precedence and provenance, written for the #123 insertion

`ResolveNode` keeps its shape: walk the inclusive ancestry `[node, parent, ..., root]`
and stop at the first entry with a scope for the target.

- The first entry is the node itself: provenance `Asset`.
- Any later entry: provenance `Inherited`.
- No entry on the path: disposition `In`, provenance `Default`.

`SoaResolution.Explicit` is renamed to `SoaResolution.Asset`, wire value `asset`. The
same enum carries the requirement layer's provenance, so both layers rename together.
The requirement layer keeps its two-step rule verbatim: resolve the standard first,
and consult requirement-target scopes only where the standard resolves `In`.

#123 inserts its `Group` rank between the asset-leaf check and the ancestry walk and
adds a `Group` enum value; nothing above or below moves. That is why the walk stays a
single ordered probe rather than being folded into a dictionary lookup.

Alternative rejected: keeping the wire value `explicit` and adding `asset` as a
synonym. Two names for one provenance is a compatibility layer with no named
consumer, and this is a pre-production cutover.

### D4: Read-access is the grant-rooted organisation union CLOSED over the asset tree

Grants stay on organisations. The seam does two steps:

1. Compute the read-subtree union over the organisation-typed nodes exactly as
   `AuthzOrgAccess.ReadSubtreeUnion` does today (roots = the caller's
   `compliance.read` org grants; descend `parent`). Rollout-mode behaviour -
   `Observe` never narrows, `Compat` gives a zero-grant caller the audited full
   fallback, `Enforce` is strict, super-admin sees everything - is unchanged.
   The mode governs THIS step only.
2. Close that union over the asset tree, per asset:
   - has `parent`: accessible when its cycle-guarded inclusive `parent` chain
     intersects the union;
   - else has `owner`: accessible when `owner` is in the union;
   - else: accessible when its own id is in the union (an organisation root).
   A discovered asset in state `Retired` is excluded outright: it is not a live
   authorization anchor, which is the rule `SubjectReadable` applies today.

Step two is NOT mode-relaxed. Under `Observe` the union is every organisation, so every
parent-anchored asset and every owner-anchored vendor is accessible - but an asset whose
edge is missing or dangling, and a retired discovered asset, are still excluded. That is
what the code does today: `/vendors` fail-closes an ownerless vendor in every mode, and
`Observe` widens the ORG set rather than switching the edge rules off. Keeping the
relaxation confined to step one is what stops `Observe` from becoming an "everything is
readable" mode with different fail-closed semantics from the mode it is meant to preview.

The closure is a pure static (`AssetReadAccess.AccessibleAssetIds(assets, orgUnion)`)
in `Freeboard/Compliance`, next to `AssetAncestry`, so the authz-backed seam and the
`AllAssetAccess` test double share it.

The branches key off which EDGE an asset carries, not its type; `parent` and `owner`
are mutually exclusive by the asset-model spec, so the branches are disjoint. Unlike the
resolver's node set (D2), the closure needs no type test at all: readability follows the
edges, and there is no type whose visibility has to be preserved against them.

That is deliberately the opposite of what the write gate does at the same edge, and the
asymmetry is the decision rather than an oversight. Step two follows a `parent` edge whatever
the linked asset's type, so an organisation whose `parent` names a machine is readable to a
caller whose grant sits only in the subtree beyond that machine; the write gate CUTS the chain
at exactly that link (D15) and refuses. The two warrant different bars: a read exposes a title,
while a grant match confers WRITE. Widening a read here also widens nothing structural - the
organisation is already in one tree with that subtree, and its own scopes were already visible
to anyone holding the parent chain. Keeping the closure edge-only, with no type test, is also
what makes vendors fall out structurally rather than through a vendor branch, so adding a type
test to close the asymmetry would cost the property D2 and D4 are both built on. No diagnostic
is emitted for the crossing and the closure is not bounded: `ConfigValidator` already reports an
unknown `parent` as a non-blocking warning, and a second signal for the same authored mistake
would be a rule that fires on a state the sync already surfaces.

Alternative rejected: keeping `IOrgAccess` for selectors and existing role grants and
layering a separate asset-visibility helper on top of it. That reads as the smaller
change but is not, for a mechanical reason: `AccessibleOrgIdsAsync` takes the node list
as a PARAMETER (`AccessibleOrgIdsAsync(user, IReadOnlyList<OrganisationRow>, ct)`), and
D1 deletes `GetOrganisationsAsync` and `OrganisationRow`, so all seven call sites -
`/organisations`, `/scopes`, `/vendors`, the SoA endpoint, `OrgSelection`, and the
`StatementOfApplicability` and `ControlDetail` page models - must change their argument
either way. Keeping the old seam would therefore mean keeping a second read model over
one table purely to feed it, which is the duplication this change exists to remove
(`code-as-liability.md`: do not leave two systems doing the same job). The rename costs
those same seven call sites plus the DI registration and the test double, and leaves
one concept.

Renaming the seam does NOT re-key authorization ASSIGNMENTS onto assets. Grants stay on
organisations, `authz_organisation_role_assignments` is untouched, and the organisation
union survives as a named intermediate INSIDE the seam (step one above). The org-only
consumers (the selector, `/organisations`, the selection resolver) intersect the asset
set with the organisation-typed nodes at the point of use, which is one
`.Where(a => a.Type is "Company" or "Department")` each; that intersection returns
exactly the step-one union, because the union is already parent-subtree-closed.

### D5: Readability becomes one set-membership test, and the scope join goes away

With the accessible asset set in hand:

- `/scopes`: `accessible.Contains(scope.Subject)`. `ComplianceEndpoints.SubjectReadable`
  and its `SubjectType` switch are deleted.
- `/vendors` and the vendor register page: `accessible.Contains(vendor.Id)`. Both
  hand-rolled owner filters are deleted.
- The SoA JSON endpoint: `accessible.Contains(node.Id)`.

`ScopeRow` therefore loses `SubjectType`, `SubjectSource`, `SubjectState`,
`SubjectParent`, and `SubjectOwner`, and `ScopeSelect` loses its `LEFT JOIN assets`.
The `/scopes` response shape is unchanged - those fields were never serialized.

Trade-off: `/scopes` today reads scopes (joined) and organisations in two separate
connections, so it already has no cross-read snapshot; after the change it reads
scopes and assets in two separate connections. No snapshot guarantee is lost. A
scope whose subject asset is committed between the two reads is simply not yet
readable - fail-closed, the same outcome as an unresolved subject.

`MySqlComplianceWriteStore`'s own locked `a.type AS SubjectType` read is a WRITE-path
lookup with its own transaction and is deliberately left alone.

Three write-path lookups read the deleted shapes and must be re-narrowed BY TYPE rather
than simply retyped, because each is an authorization input rather than presentation.
They are a subset of the write-path work, and they do not displace D15: filtering these lookups
to organisation-typed assets settles what the id NAMES, while D15 settles what its ancestry may
anchor. Both apply to the same ids.

- `ComplianceWriteEndpoints.OrganisationPutSelector` decides update-vs-create from
  whether `id` is in the organisation list, and `UpsertOrganisationAsync` reads the
  stored row's `Parent` to drive the reparent authorization. Fed an unfiltered asset
  list, `PUT /organisations/{machineId}` would flip from "create" to "update": the
  selector would authorize `org.write` on the machine id, whose ancestry now resolves
  through its `parent`, so an owner of the parent organisation would pass a check that
  today needs `system.admin` (creating a root authorizes an id on which no grant
  exists). Both call sites therefore filter `Type is "Company" or "Department"` before
  the existence test and the parent read.
- The four scope write handlers narrow their stored-owner lookup with
  `IsOrgSubject(scope)`, which reads `ScopeRow.SubjectType` - a field this change
  deletes. It is replaced by the same lookup the read surfaces now use: resolve the
  scope's `Subject` against the asset list and keep the row only when that asset is
  `Company`- or `Department`-typed. A subject that resolves to no asset yields no
  stored owner, exactly as an unresolved `SubjectType` does today, so the handler still
  takes the new/absent-row path and the store's global-id branch still answers `404`.

### D6: The SoA input snapshots carry `Assets` and drop two inputs

`SoaInputs` becomes `(Assets, Scopes, Requirements)` and `SoaDrilldownInputs` becomes
`(Assets, Scopes, Requirements, Controls, Collectors)`. Removed:

- `Organisations` - derived from `Assets` by type where a surface needs orgs only.
- `Vendors` - the drill-down's vendor-title lookup reads the `Vendor`-typed assets,
  narrowed to the caller's accessible set (D9).
- `ResolvableAssetIds` - the live-subject predicate ("an asset row exists and is not a
  retired discovered asset") is computed from `Assets`, deleting the
  `ResolvableAssetIdsSelect` query from both snapshot reads.

The SQL is stated precisely, because the current query is the thing that excludes
machines. Today both snapshots run
`SELECT id, title, type AS Kind, parent FROM assets WHERE type IN ('Company','Department')`
plus a separate `SELECT id FROM assets WHERE NOT (source = 'discovered' AND state =
'Retired')`. Both are replaced by ONE unfiltered flat read:

```sql
SELECT id, title, type, source, state, parent, owner FROM assets ORDER BY id;
```

No `WHERE` clause, no recursive CTE, no per-node read. Retirement is filtered at the
CONSUMING layer, not in SQL, and at exactly two places:

1. the read-access closure (D4) drops a discovered asset in state `Retired` from the
   accessible set, so it anchors no read;
2. the live-subject predicate for the dangling-subject warning treats it as not live.

The read is unfiltered because ONE snapshot has to serve three different consumers with
three different notions of relevance: the resolution tree (which needs a retired
machine's ancestors and its `parent` edge), the drill-down's vendor titles (vendors,
which no retirement filter should touch), and the live-subject predicate (which needs
the retired rows in order to classify them). A `WHERE` clause that satisfies any one of
them breaks the other two, and re-adding the excluded rows would mean a second query -
which is exactly the extra read this decision deletes. Retirement is a CONSUMER concern
here, not a storage one.

Retirement is not a distinction the dangling-subject warning draws. A scope naming a
retired discovered asset and a scope naming an id no asset defines produce the same
generic, id-less page notice ("rule targets a resource that does not currently exist"),
which is today's behaviour and stays. The unfiltered read is justified by the one-read
consolidation above, not by any finer warning.

One SQL detail is preserved rather than inherited. The predicate being deleted,
`WHERE NOT (source = 'discovered' AND state = 'Retired')`, also excludes a discovered
row whose `state` is NULL, because the `AND` yields NULL and `NOT NULL` is not true; the
in-memory predicate would include it. Verified: no such row is producible.
`assets.state` is nullable only because DECLARED rows share the table (the GitOps
importer's asset insert names no `state` column, so a declared asset reads NULL), while
every discovered row is written by `MySqlAssetWriteStore`, which sets `state` on insert
and only ever moves it between `Seen` and `Retired`; migration `019` copied the legacy
discovered rows from a `NOT NULL` column. The two predicates are therefore equivalent on
every reachable row, and the in-memory predicate is stated as written - a discovered
asset in state `Retired` - with no null branch to carry.

This removes two queries from the drill-down snapshot and one from the flat snapshot
while keeping the repeatable-read guarantee, because the remaining inputs still read
inside the one transaction.

`HasDanglingSubject(scopes, assets)` keeps its contract: true when any scope of any
target kind names a subject that is not a live asset. The page notice stays generic
and id-less.

### D7: The page keeps an organisation-only node set; the endpoint does not

`ResolveDrilldown` filters its node list to organisation-typed assets before building
the requirement/control/check hierarchy. The page's selector scoping
(`OrgScope.InScopeIds`), its active-scope label, and its batched per-collector
evidence status are all keyed on an organisation id, so admitting machine rows would
be a UX and a data-shape change, not a resolution change.

The flat `Resolve` - which backs `GET /statement-of-applicability/{standardId}` - does
NOT filter, so machine nodes are observable there. This is the surface the acceptance
criterion "resolution produces correct dispositions over a mixed declared/discovered
asset tree" is asserted against.

The endpoint returns readable `Machine` nodes in THIS change rather than deferring the
node-set widening. Machine-level resolution with no observable surface is a rule nobody
can check: the acceptance criterion "resolution produces correct dispositions over a
mixed declared/discovered asset tree" would be assertable only through unit tests
against an internal method. The cost is that the response is now bounded by the readable
device count rather than the organisation count (R2). That risk is accepted and recorded,
not mitigated here: no pagination, no node cap, and no `type` query parameter is added,
because bounding the endpoint is a design with its own choices (page tokens versus
offsets, ordering stability, whether the page shares the bound) and none of them is
resolution or read-access.

The asymmetry is deliberate and documented in the spec delta so a reader does not
read the page as the whole contract.

### D8: `OrgAncestry` becomes `AssetAncestry`

Same cycle-guarded walk, typed over `AssetNode`. Its three consumers -
`Authorizer.ResolveAncestryAsync`, the SoA resolver, and the read-access closure -
keep sharing one walk. `AuthzRequestCache` caches `IReadOnlyList<AssetNode>` from
`GetAssetsAsync` in place of the organisation list, so the per-request memoization is
unchanged.

That WIDENS what `Authorizer.ResolveAncestryAsync` resolves, and the widening is an
authorization change rather than a latent gain. The method resolves the ancestry of
whatever id a selector puts in `AuthzResource.OrganisationId`. A `Machine` id resolves
today to the one-element chain `[machineId]`, because the organisation map has no entry
for it and the walk stops; after the change it resolves to the machine's whole `parent`
chain up to its company. `OrgRbacPolicy` permits when ANY grant organisation appears in
that chain. Eight live constructions put an organisation id in that field - the four
organisation and scope write selectors, the two stored-owner scope-delete selectors, the
cross-org-move and reparent helper, and the role-assignment API selector and page guard -
so a caller holding `org.write` or `compliance.scope.write` on an organisation beyond a
non-organisation link would pass gates that today only a super-admin passes. D15 is the
guard.

`OrgScope` keeps its name and role (the SELECTION subtree for the page) and takes
`AssetNode`; it is about an organisation selection, not about access.

### D9: An unreadable vendor id is withheld from every read surface that carries one

The leak is real and is already documented in the code. The comment above `/vendors`
says the narrowing "covers /vendors only; /collectors and /integration-connections
still expose a hidden vendor's id", and both endpoints project `vendor = r.Vendor`
unconditionally. The acceptance criterion "a caller cannot read a vendor outside its
owner's subtree" is therefore not met by narrowing `/vendors` alone: a caller with no
grant reaching `vendor-okta` still learns the id from a collector row.

Decision: close the vendor-id leak IN THIS CHANGE, at the FIELD, not at the row.
`/collectors`, `/integration-connections`, the collector register page, and the
integration-connections page emit `vendor` only when that vendor id is in the caller's
accessible asset set, and emit `null` otherwise. The rows themselves stay global.

Rationale. Narrowing the ROWS - dropping a collector whose vendor the caller cannot
read - is a different and much larger decision than the one this issue makes. Collectors
and integration connections have NO organisation dimension; that is why they are global
today, and it is what the `collector-register`, `integration-connection`, and
`authz-enforcement` specs all say. Dropping rows would have to answer what happens to a
collector with no vendor at all (`CollectorRow.Vendor` is nullable), what a zero-grant
operator sees on the register page under strict enforcement, and whether the scheduler's
view of its own collectors changes. None of that is resolution or read-access over the
asset tree. Nulling one field is four `.Contains` calls and answers the criterion the
issue actually states.

What still leaks, and why that is accepted: a collector's `title` and `config` and a
connection's `provider` and `base_url` may still IMPLY which vendor a deployment uses -
a connection to `https://acme.okta.com` with `provider: okta` names Okta without
carrying the `vendor-okta` id. Those fields describe the INTEGRATION, not the vendor
asset, and suppressing them means giving collectors and connections an organisation
dimension they do not have. That is a confidentiality model for reference data, worth a
separate issue; this change closes the vendor-ASSET id, which is the object the `owner`
edge governs.

`CollectorRow.Vendor` and `IntegrationConnectionRow.Vendor` are already nullable and the
wire contract already emits them as explicit `null` when unset, so nulling adds no shape
change and no new null-handling on any consumer. The CLI's `ApiCollector.Vendor` and
`ApiIntegrationConnection.Vendor` are already `string?`.

There is a THIRD surface carrying a vendor id, and it is the likeliest of the three to
expose one. `BuildControlCatalogue` renders a check's vendor as the vendor's TITLE and
falls back to the raw vendor id when no vendor asset matches, and the drill-down page
renders whatever that produces. The fallback fires on exactly the vendor a caller must
not see: once the vendor-title lookup is narrowed to the caller's accessible assets, a
vendor with a missing or dangling `owner` - the fail-closed case the whole `owner` rule
exists for - resolves to no title and would print its id.

The drill-down therefore applies the same accessible-asset test. `ResolveDrilldown` takes
the caller's accessible asset set in place of the `vendors` parameter it drops, and builds
its vendor-title map from the `Vendor`-typed assets in that set; a vendor outside it
contributes no entry, so the check's vendor is null. The raw-id fallback is deleted, not
guarded - there is no path on which the drill-down renders a vendor id. That keeps one rule across all three
surfaces and makes the claim "a caller cannot learn the id of a vendor outside its
owner's subtree from any read surface" true rather than aspirational. A check whose
vendor is unreadable renders exactly as a check with no vendor, so the page cannot be
used to distinguish the two.

### D10: Evidence ingest moves with the resolver and stays organisation-only

`EvidenceIngestEndpoints.IsOrganisationInScope` resolves the posted
`(organisation_id, requirement_id)` through `StatementOfApplicability.Resolve`. D1 and
D2 change that method's signature, so ingest MOVES whether or not it is planned for -
the only question is what it does once moved.

Decision: ingest resolves over the asset tree like every other consumer, and then
requires the matched node to be a `Company` or `Department`. A posted `organisation_id`
naming a `Machine` is rejected exactly as it is today.

Rationale. Letting ingest accept a machine id looks like the natural consequence of
unification, but it is an unspecced widening of a public, externally-called API.
`evidence_runs.organisation_id`, the collector-scoped idempotency key, and every
downstream evidence roll-up are keyed on an organisation; admitting a machine there
would need all of them to move, which is a capability change, not this one
(`code-as-liability.md`: preserve public behaviour unless the task requires changing
it). Machine-level evidence is a legitimate follow-up and this change does not block it.

The ACCEPTANCE SET is unchanged, and D2 is what makes that literally true. Every
`Company` and `Department` asset is a node under the new rule exactly as it is under the
current `type IN ('Company','Department')` read - including one whose `parent` dangles or
sits in a cycle - so no organisation that ingest accepts today becomes absent tomorrow.

The DISPOSITIONS are resolved by the same cycle-guarded walk over the same `parent`
column, but not over the same node map, and there is one case where that shows. A
`Machine` hanging under a department cannot change that department's resolution: the walk
runs upward, and nothing reaches a node from below. The case that does differ is an
organisation whose `parent` names an id that is not in the GitOps config but IS a live
row in the database - a discovered `Machine`'s ULID is the reachable instance, and
`ConfigValidator` reports it only as a non-blocking "unknown parent" warning, so a validly
synced deployment can hold it. Today that chain stops at the organisation, because the
organisation-only map has no entry for the machine, and the organisation resolves the
default `In`. After the change the chain continues through the machine to the machine's
own parent organisation, so the organisation can inherit that organisation's scopes. Both
readings are defensible - the edge does name a live asset - and the new one is the one
the unified tree implies. It is recorded here rather than mitigated: it changes a
disposition only for a deployment that has authored an organisation `parent` pointing at
a discovered asset, which the validator already flags, and the fix is to author the edge
correctly. The compiler forces the move; the type filter preserves the
acceptance set; the node rule preserves the acceptance SET's membership. An `evidence-ingest` spec delta records the
clarification, because the existing wording ("`organisation_id` MUST resolve In-scope
for that requirement through the Statement of Applicability") would otherwise read, once
the SoA resolves over assets, as admitting machines.

### D11: No Statement-of-Applicability command is added to the CLI

The issue requires "Web + CLI read models updated in the same PR (parity)". Verified
against the code: the CLI registers `gitops`, `system`, `user`, `vendor`, `collector`,
and `connections`, and `IFreeboardApiClient` reads `/vendors`, `/scopes`, `/collectors`,
and `/integration-connections`. There is no Statement-of-Applicability client, DTO, or
command.

Decision: read "parity" as feature parity over the reads the CLI HAS, not as a new
command surface. The CLI's affected reads are `collector list` and `connections list`,
whose `vendor` column now reads `-` for a vendor the caller cannot see (D9) - both
renderers already print `-` for a null vendor
(`CollectorCommands.Print`, `ConnectionCommands.Print`) - and `vendor list` and the scope
reads, which narrow server-side with no client change. No CLI source change is required;
the parity claim is verified by test, not implemented. The existing CLI tests do not
assert that rendering, so the tests are added even though the source is not touched.

Rationale. Adding a `soa` command means a DTO tree for a nested node/requirement
projection, a parser, a command class, a table renderer, and their tests - a whole
public surface with no named consumer, for an acceptance criterion that is already
observable on the JSON endpoint. That is exactly the liability
`.claude/rules/code-as-liability.md` forbids ("generic helpers with only one caller",
"speculative abstractions"), and the CLI reaches the API over HTTP, so nothing about the
resolution change is invisible to it. If an operator later needs an SoA listing, it is a
one-issue addition on an unchanged endpoint.

The CLI is also constrained by the reference graph: it must not reference
`Freeboard.Enterprise` and must build on Windows, Linux, and macOS. Not touching it
keeps that trivially true.

### D12: One flat asset read, ancestry memoized per node

Asset count is unbounded where organisation count was not: a fleet has one `Machine`
row per device, so the node set the resolver walks can be orders of magnitude larger
than the organisation list it replaces. The resolution must not re-walk the tree once per
descendant, and must not read the store per node.

- The store issues ONE flat query per snapshot (D6). No recursive CTE, no per-node read,
  no N+1.
- The inclusive ancestry chain is memoized per node id for the life of ONE pass, so each
  `parent` edge on a cycle-free chain is WALKED at most once per pass rather than once per
  descendant; a chain that terminates on a cycle caches nothing and is re-walked.
- The memo bounds the walking, not the reading. The read-access closure still scans the
  chain it gets back for every parented asset, so the worst case stays nodes x depth
  whatever the memo saves. That residual is accepted, not removed: depth is the tree's
  height (an organisation hierarchy plus a device), not its size, and the cheaper shape -
  a per-node reachability memo the closure could stop at - would mean the closure no
  longer consumes the shared chain, giving up `AssetAncestry`'s one-build-cannot-diverge
  property for a constant factor.
- Each pass builds one id-to-node map up front and threads one cache through it. The
  read-access closure is one such pass;
  the resolver's node-set derivation and its per-node precedence probe are another, and
  they share the map and the cache with each other. They do NOT share with the closure:
  `IAssetAccess.AccessibleAssetIdsAsync` and `StatementOfApplicability.Resolve` are
  separate calls with separate snapshots, and threading a cache across them would mean
  putting it on the seam's contract for a saving of one walk per request. Two passes over
  the same list is the accepted cost of keeping the seam's signature to
  `(principal, assets, ct)`.

The memo is a CALLER-SUPPLIED cache threaded through the pass, not a field on a static
class. `OrgAncestry` is a `public static class` with one pure static method today;
memoizing inside it would put the cache in a process-wide static, which is neither
thread-safe under concurrent requests nor invalidated when a sync changes an asset's
`parent` - it would serve a stale ancestry indefinitely. `AssetAncestry` keeps its pure
static `InclusiveAncestors` and gains an overload taking the id-to-node map plus a
`Dictionary<string, IReadOnlyList<string>>` cache that the caller creates at the start of
a resolution or closure pass and discards at the end. The cache's lifetime is therefore
the same as the asset snapshot it was computed from, and it cannot outlive it.

Acceptance point: NO static mutable state. A test asserts that two passes over different
asset lists, run against the same helper, produce ancestries consistent with their own
input - which a shared static memo would fail.

This is the same discipline the resolver already has (in-memory over a list, not SQL
recursion); the mechanism is spelled out because the input size changes, not the
technique.

### D13: `docs/gitops.md` is updated with the code

Verified stale in four places. The scope section describes a redundant root-level `In`
as resolving "`In` marked `explicit` instead of `default`", and the API section names
`explicit` three more times: once for the standard layer's resolution value, once for
the requirement layer's, and once in the sentence distinguishing an in-scope node
("`explicit`, `inherited`, or `default`") from an out-of-scope one. All four are wrong
after this change, on the provenance value, and the API section is also wrong on the
node set. The same section's description of `/collectors` and
`/integration-connections` as "not narrowed by organisation access - any authenticated
user reads every row" stays true under D9 and gains the vendor-field caveat.

Two more `docs/gitops.md` paragraphs, and the established `vendor-register` and
`web-object-drawer` specs, call the narrowing set the "accessible-organisation set".
Behaviour at all of them is unchanged, so none takes a spec delta; the name is corrected so
the one narrowing rule does not read as several.

`comment-etiquette.md` applies to the code side of the same sweep. Six comments assert a
condition this change removes and must be rewritten, not left standing: the `/vendors`,
`/collectors`, and `/integration-connections` comments in `ComplianceEndpoints` that record
the vendor-id leak; the `CollectorsModel` and `IntegrationConnectionsModel` class comments
that say the page "does NOT narrow by accessible organisation" (true of the rows, false of
the `vendor` field after D9); and the `VendorsModel` class comment, which states the owner
rule D5 replaces.

### D14: Two read surfaces are deliberately left as they are

`GET /compliance/status` keeps its GLOBAL counts, vendors and collectors included, and
gains no narrowing. A count is not readability: it discloses how many vendors exist, not
which, and it names no id, no title, and no `owner`. Narrowing it would mean deriving
per-caller counts from the accessible asset set - a second, differently-shaped
authorization rule over a summary endpoint - to withhold a single integer. The
established `compliance-web-read` requirement already defines this endpoint as a
persisted-count summary; nothing in it changes here. If a deployment ever treats the
existence-count of vendors as confidential, that is a decision about the status endpoint,
not about the `owner` edge.

`GET /vendors` keeps its `{ id, title }` shape and does NOT gain an `owner` field, even
though the field is now sitting in the read model it is served from. No surface asks for
it: the register page renders title and scopes, the CLI prints title and scopes, and
narrowing is done server-side, so a client has no use for the edge. Adding it would be a
new wire field with no named consumer, and the value it carries is precisely the
authorization anchor - which is worth having a reason to publish, not a default.

### D15: An organisation gate anchors only on organisation ancestry

`Authorizer.ResolveAncestryAsync` resolves the inclusive ancestry of whatever id a caller
puts in `AuthzResource.OrganisationId`, and `OrgRbacPolicy` permits when any of the
caller's grant organisations appears in that chain. Once that walk runs over the asset
list (D8), a `Machine` id resolves through its `parent` to its department and company, so
an organisation gate handed a machine id would be satisfied by a grant on the machine's
parent organisation. Today the same call resolves to `[machineId]`, matches no grant, and
denies everyone but a super-admin. That is a real widening of who may write, not a
theoretical one: eight live constructions put an organisation id in that field, taking it from a
route value, a request body, a form post, or a stored row.

Decision: every organisation gate anchors on the ORGANISATION-BOUNDED chain. The gate builds
the cycle-guarded inclusive `parent` chain over the assets and CUTS it immediately after the
first entry that names a non-organisation asset; that entry is kept, and the walk goes no
further. An entry naming no asset at all is kept and is not a cut point, because the walk
already stops there for want of a `parent`.

Cutting, rather than pinning `[id]` whenever the id is not an organisation, is what makes the
guard behaviour-preserving. Today's chain comes from `OrgAncestry.InclusiveAncestors` over an
organisation-only map, and that walk stops at the first id the map does not hold - which is
every id that names a non-organisation asset AND every id that names no asset. Pinning only
when the START id is not an organisation would leave one reachable class widened: an
ORGANISATION whose `parent` names a live `Machine`. Today that chain is `[dept, machineId]`
and stops; over the asset list it would run on to the machine's own department and company,
and because the start id IS an organisation the pin would never fire, so a grant on that far
company would authorize a write no non-super-admin can perform today. D10 already records
that this state is reachable in a validly synced deployment, since `ConfigValidator` reports
an unknown `parent` as a non-blocking warning.

The cut reproduces the pre-change chain for every id class: an id naming no asset (`[id]`,
so a create-root `PUT /organisations/{newId}` still authorizes the bare new id and still
needs `system.admin`); a machine, a retired discovered machine, or a vendor (`[id]`); an
organisation with a dangling `parent` (`[org, gone]`, the dangling entry kept); an
organisation in a `parent` cycle (the visited-set guard ends it identically); an organisation
parented onto a machine (`[org, machineId]`); and an ordinary all-organisation chain, which
has no cut point and is unchanged. Because the cut and the plain walk agree wherever no cut
applies, the gate pins the chain unconditionally rather than deciding per id whether to pin.

That is why the guard is stated as one rule at one helper rather than as a list of per-route
rejections - there is nothing to decide per route.

The rule is therefore stated over the CONSTRUCTION, not over the class of id a construction
happens to receive: every `AuthzResource` carrying a non-null organisation id is built through
the guarded helper. Confining an id to an organisation ASSET before it is used - as the scope
handlers' stored-owner lookup does (D5) - does not make it a safe anchor, because it is exactly
an organisation whose own `parent` names a machine that the cut exists for. Narrowing the rule to
"ids that might not be organisations" would reinstate the premise this decision refutes and leave
the stored-owner selectors, the cross-org-move helper, and the `PUT /organisations/{id}` update
arm outside the guard. Constructions carrying a NULL organisation id (the `system` resources) are
not gates on an organisation and are untouched.

A refusal under this rule is an authorization outcome, not a store outcome, but the STATUS is
the route's to choose and this rule does not change it. Every gate but the role-assignment API
answers `403`.
The three `/organisations/{orgId}/role-assignments` routes keep their `404`: their selector
runs its own mode-aware `org.read` check first and returns a null resource on a deny, which
the endpoint filter answers as existence non-disclosure. That is today's outcome, it is what
the established `authz-enforcement` requirement mandates for an org-scoped resource the
principal cannot see, and a `403` on a machine id would disclose that the id exists. Because
that read check is mode-relaxed, the outcome is mode-dependent: `Enforce` (and `Compat` for a
grant-holding caller) gives `404`, while under `Observe` the would-be deny is not enforced,
the resource is selected, and the route's force-enforced assignment gate gives `403`. Both are
unchanged by this change.

Three things this decision deliberately does not do.

*It does not teach `Authorizer` or the engine about resource types.* `AuthzResource.Type`
exists for the audit trail; the engine is generic over it and `AuthzResource` is a
`Freeboard.Core` type with no view of the compliance asset model. Adding a
resource-type opt-in - "only these types may anchor an organisation ancestry" - would
make a generic decision engine depend on one capability's type vocabulary, and every
future resource kind would have to register with it. The caller already knows what kind of
resource its route acts on, already reads the store, and is where the 403-vs-404 choice is
made today (`RoleAssignmentEndpoints.OrgVisibilitySelector`).

*It does not deny by returning a null resource.* `AuthzEndpointFilter` maps a null
selector result to `404` (existence non-disclosure) and a denied decision to `403`. The
guard never returns null: cutting the ancestry keeps the denial inside `IAuthorizer`, so it
is audited through the same `authz.decision.denied` path as every other deny. Where a route
already decides visibility for itself - the role-assignment selector's `org.read` check -
that decision stands ahead of the guard and keeps producing the `404` it produces today.

*It does not move the decision into persistence.* `MySqlComplianceWriteStore` already
confines its organisation reads, deletes, and scope-owner lookups with
`type IN ('Company', 'Department')`, and `MySqlAuthzAdministrationStore` confines its
role-assignment target the same way - which is what makes this decision's premise, that no
grant exists on a machine id, true rather than assumed. Those predicates STAY - a caller
reaching the store by another path still has to meet them. But they are DEFENCE IN DEPTH from
here on, not the thing holding the line. Relying on them would turn an authorization failure
into whatever the store happens to return: a `404` from the global-id branch, or a no-op `204`
from a `DELETE` that matched no row. Both hide the real answer, both are reached only
after the caller has been let through the gate, and neither is auditable as a denial.

The helper goes on `AuthzRequestCache`, which is already the request-scoped holder of the
memoized asset list and is already resolved by both the selectors (from
`RequestServices`) and the page handlers (by injection). A separate service would be a new
file and a new DI registration for one method over data that class already holds.

## File changes

`src/Freeboard.Persistence`:

- `ComplianceReadModels.cs`: add `AssetNode` and its `IsOrganisation` predicate; delete
  `OrganisationRow` and `VendorRow`; trim `ScopeRow` to its eight public fields; reshape
  `SoaInputs` and `SoaDrilldownInputs`.
- `IComplianceStore.cs`: replace `GetOrganisationsAsync` and `GetVendorsAsync` with
  `GetAssetsAsync`; update the SoA input doc comments.
- `MySqlComplianceStore.cs`: one `AssetSelect`; drop the scope `LEFT JOIN` and the
  `ResolvableAssetIdsSelect`; rewrite both snapshot reads.

`src/Freeboard`:

- `Compliance/OrgAncestry.cs` -> `Compliance/AssetAncestry.cs`.
- `Compliance/AssetReadAccess.cs` (new): the pure accessible-asset closure. Justified
  as a new file because it is the one rule three surfaces and the seam share; putting
  it on the seam would hide it from the test double.
- `Web/OrgAccess.cs` -> `Web/AssetAccess.cs`: `IAssetAccess` and `AllAssetAccess`.
- `Authz/AuthzOrgAccess.cs` -> `Authz/AuthzAssetAccess.cs`: unchanged mode logic, org
  union kept private, closure applied on the way out.
- `Authz/AuthzRequestCache.cs`: cache assets; add the organisation-resource builder that
  anchors the resource on the organisation-bounded ancestry chain (D15).
- `Authz/RoleAssignmentEndpoints.cs` and `Pages/Admin/RoleAssignments.cshtml.cs`: route
  their organisation id through that builder. `RoleAssignmentsModel` takes
  `AuthzRequestCache` as a new constructor dependency to reach it.
- `Compliance/StatementOfApplicability.cs`: asset-typed resolver; `SoaResolution.Asset`;
  node-set derivation; drill-down org filter; vendor titles from the caller's accessible
  `Vendor`-typed assets, with the raw-id fallback in `BuildControlCatalogue` removed.
- `Compliance/ComplianceEndpoints.cs`: delete `SubjectReadable`; one membership test
  on `/scopes`, `/vendors`, `/organisations`, and the SoA endpoint; null an unreadable
  `vendor` on `/collectors` and `/integration-connections`; rewrite the three comments
  that record the vendor-id leak.
- `Evidence/EvidenceIngestEndpoints.cs`: `IsOrganisationInScope` resolves over the
  asset list and requires the matched node to be organisation-typed.
- `Pages/Compliance/Collectors.cshtml.cs` and
  `Pages/Compliance/IntegrationConnections.cshtml.cs`: withhold an unreadable `vendor`
  from the rendered row. Neither has an access seam or an asset read today
  (`CollectorsModel(IComplianceStore)` and
  `IntegrationConnectionsModel(IComplianceStore, IIntegrationTokenResolver)`), so each
  takes `IAssetAccess` as a new constructor dependency and adds a `GetAssetsAsync` read
  to resolve the accessible set. Five tests assert these exact parameter lists by
  reflection and change with them. Both class doc comments currently state that the page
  "does NOT narrow by accessible organisation" and are rewritten to the rule that
  replaces it: rows global, `vendor` narrowed.
- `Compliance/ComplianceWriteEndpoints.cs`: retype to `AssetNode`, filter the two
  organisation lookups with `IsOrganisation`, replace `IsOrgSubject`, and build every
  organisation-carrying `AuthzResource` through the D15 guard - the two put/delete selectors, the
  two body selectors, the two stored-owner scope-delete selectors, and `AuthorizeOrgAsync`.
  `AuthorizeOrgAsync` is a static taking only `IAuthorizer`, so it gains an `AuthzRequestCache`
  parameter, as do `AuthorizeParentAsync` and the three static minimal-API handlers that reach it -
  `UpsertOrganisationAsync`, `UpsertScopeAsync`, and `UpsertRequirementScopeAsync` - the last three
  as a handler parameter the minimal-API container resolves.
- `Compliance/OrgScope.cs`: retype, and intersect the null-selection ("All Organisations")
  branch with the organisation-typed assets as the selected-subtree branch already does -
  otherwise the accessible ASSET set would flow through it as in-scope machines and vendors.
- `Web/OrgSelection.cs`,
  `Pages/Shared/Components/OrgSelector/OrgSelectorViewComponent.cs`,
  `Pages/Compliance/StatementOfApplicability.cshtml(.cs)`,
  `Pages/Compliance/ControlDetail.cshtml.cs`,
  `Program.cs`: retype to `AssetNode`/`IAssetAccess`. `Web/OrgSelectEndpoints.cs` is NOT in
  this set: it names no organisation type, no access seam, and no asset - it sets or clears
  the selection cookie and redirects.
- `Pages/Compliance/Vendors.cshtml.cs`: retype, delete the hand-rolled owner filter, and
  rewrite the class doc comment, which states the owner rule this change replaces.

`docs/gitops.md`: the four stale `explicit` passages, the SoA node-set passage (D13), the
`/collectors` and `/integration-connections` vendor-field caveat, and the two readability
paragraphs that call the narrowing set the "accessible-organisation set".

`openspec/specs/vendor-register/spec.md` and `openspec/specs/web-object-drawer/spec.md`:
the same "accessible-organisation set" phrasing. Behaviour at both is unchanged - a vendor
is still admitted exactly when its `owner` is in the caller's organisation union, and the
drawer's control lookup still spans the caller's whole readable tree - so neither takes a
spec delta; only the name of the one set is corrected, so the wording does not read as a
second narrowing rule.

`tests`: `Freeboard.Web.Tests` doubles and fixtures; `Freeboard.Persistence.Tests`
integration coverage; `Freeboard.WebE2E` fixtures that build organisations, including
`StatementOfApplicabilityE2ETests` which asserts the literal string `explicit` on the
rendered row.

`src/Freeboard.CLI`: no change (D11). `src/Freeboard.Core`, `src/Freeboard.Agent`, and
`src/Freeboard.Enterprise`: no change.

## Verification strategy

- Unit (`Freeboard.Web.Tests`, no MySQL): resolution over a mixed tree
  (Company -> Department -> declared Machine and discovered Machine) asserting
  `asset`/`inherited`/`default`, leaf override, vendor absence from the node set,
  unrooted- and dangling-parent-machine absence, dangling-parent and cycle-parented
  ORGANISATIONS still present as nodes resolving the default `In`, and requirement-layer
  suppression under an `Out` standard. Read-access closure: parent-chain hit and miss,
  owner hit and miss, retired discovered exclusion, cycle termination, and the three
  rollout modes - including that `Observe` still excludes an ownerless vendor and a
  retired discovered asset. Ancestry memoization: no static mutable state across passes.
  Write-path authorization: `PUT /organisations/{existing-machine-id}` is still a create and
  is still refused for a non-super-admin, `403` on a null `parent` and `409` from the store's
  id collision on a `parent` the caller may write; each organisation-id construction (D15),
  given a `Machine` id and a caller who holds that route's permission on the machine's parent
  organisation, refuses without reaching the store - `403` everywhere but the role-assignment
  API, whose `404` under `Enforce` and `403` under `Observe` are both pinned - never a `400`
  or a no-op `204`; an ORGANISATION whose `parent` names a machine is refused for a caller
  granted only on the company beyond that machine, asserted on a stored-owner scope delete and
  on a plain organisation update as well as on a body-supplied id, since both of those anchor
  on an id that IS an organisation; and a super-admin is unaffected. Endpoint tests for the
  renamed wire value
  and the narrowed `/scopes`, `/vendors`, and SoA node lists. Vendor-field nulling on
  `/collectors` and `/integration-connections` and on the two register pages: a
  readable vendor keeps its id, an unreadable one reads `null`, and the row is still
  returned in both cases. Evidence ingest: an in-scope organisation id is still
  accepted, an out-of-scope one is still rejected 422, and a `Machine` id is rejected.
- Integration (`Freeboard.Persistence.Tests`, `FREEBOARD_TEST_DB`, skips cleanly):
  `GetAssetsAsync` shape over a seeded mixed tree; the two SoA snapshot reads; the
  scope read without its join. These are the tests the acceptance criterion
  "integration tests cover resolution and read-access over the unified tree" names.
- Integration coverage explicitly includes a MIXED `Company` -> `Department` ->
  declared and discovered `Machine` tree, so the store read that used to be
  `type IN ('Company','Department')` is proved to carry machines, and a vendor whose
  `owner` narrows it out of one caller's set and into another's.
- E2E (`Freeboard.WebE2E`, `FREEBOARD_TEST_E2E`): the existing SoA and authz-isolation
  fixtures are retyped; `StatementOfApplicabilityE2ETests` asserts `asset` where it
  asserts `explicit` today; assert the page still renders organisation rows only and a
  caller outside a vendor's owner subtree sees neither the vendor nor its scopes.
- CLI (`Freeboard.CLI.Tests`, no source change): assert that `collector list` and
  `connections list` render `-` in the vendor column when the endpoint sends `null`.
  The existing cases do not assert this - the collector case uses a non-null vendor, and
  the connection case has a null-vendor row but never asserts the rendered column - so
  the assertions are added.
- `dotnet build src/Freeboard` once the read-model unification and everything retargeted onto it
  are in place (tasks.md groups 1 to 7 are one compilable unit, so no earlier source build is
  green). That gate is the SOURCE tree only: `Freeboard.slnx` includes the test projects, and they
  keep naming the deleted types until group 8 retypes them, so a whole-solution `dotnet build` is
  red until then and is gated with `dotnet test` at the end of group 8. Then
  `npx markdownlint-cli2` for any touched Markdown outside the OpenSpec carve-out.

## Risks / Trade-offs

- **R1 Wide mechanical blast radius.** Replacing `OrganisationRow` touches ~20 source
  files and ~15 test files; a missed call site is a compile error, but a missed TEST
  fixture can silently assert the old shape. -> Mitigation: the rename is
  compiler-enforced (the type is deleted, not aliased), and the read-access tests are
  written against the new closure rather than adapted from the old per-surface tests,
  so a stale expectation does not survive.
- **R2 Unbounded SoA JSON node set.** The endpoint may now return one node per
  readable machine; a large fleet makes the response large. -> Mitigation: the node
  set is still narrowed by the accessible asset set and by the standard, and the page
  (the surface a human loads) keeps its organisation-only node set. Bounding the
  endpoint is called out as a non-goal and left to a follow-up. This is an ACCEPTED
  risk, not a mitigated one: no cap, no page token, and no node budget ships here, so a
  deployment with a large fleet gets a large response from this endpoint and the
  follow-up that bounds it is the fix.
- **R3 Read-access widens where it used to be absent.** A machine-subject scope that
  today is visible only through `SubjectReadable`'s parent branch now also appears as
  a resolved node on the SoA endpoint. That is the intent, but it is more surface. ->
  Mitigation: every new row passes the same closure a scope row passes, so nothing is
  reachable that was not already reachable through `/scopes`; the E2E isolation test
  asserts the negative case.

  The closure also widens in a second, subtler way, and this one is a deliberate ASYMMETRY with
  the write gate rather than a mitigated risk. Step two follows the `parent` edge with no type
  test, so an organisation whose `parent` chain runs through a machine into another subtree
  becomes readable - on `/organisations`, and as a `/scopes` subject - to a caller granted only
  beyond that machine, while the write gate cuts the chain at that link and refuses (D15). The
  bars differ because the exposures differ: a read exposes a title, a grant match confers write.
  The state is unreachable today only because `AuthzOrgAccess.ReadSubtreeUnion` builds its
  child index from organisation rows alone. -> Accepted, not mitigated: no diagnostic and no
  bound is added, since `ConfigValidator` already warns on the authored edge and the edge-only
  closure is what keeps vendors out without a vendor branch.
- **R4 Losing the scope `LEFT JOIN` changes the consistency window.** -> Mitigation:
  as D5 notes, `/scopes` already reads its two inputs in separate connections, so no
  guarantee is lost, and the failure mode is fail-closed omission.
- **R5 Renaming the `explicit` wire value breaks any external consumer.** ->
  Mitigation: pre-production, the CLI has no SoA reader, and the change is marked
  BREAKING for the release notes.
- **R6 Cycles in `parent`.** A cycle in the asset tree could hang the node-set
  derivation or the closure. -> Mitigation: both reuse `AssetAncestry`'s visited-set
  guard, and `OrgScope` already carries a cycle test; a resolver cycle test is added.
- **R7 Asset-count growth makes resolution the hot path.** The node set is now one row
  per device, not one per department. -> Mitigation: D12 - one flat query per snapshot, and
  one id-to-node map plus one ancestry memo per pass, shared by the node-set derivation and
  the per-node precedence probe. No SQL recursion and no per-node read is introduced, and the
  memo bounds the walking to each edge on a cycle-free chain once per pass; the per-asset chain
  scan still carries the nodes times depth term, accepted because depth is the tree's height,
  not its size. The read-access
  closure is a second pass over the same snapshot; D12 records why it is not merged into the
  first.
- **R8 The vendor-field nulling is partial.** D9 closes the vendor id on the two global
  reads and on the SoA drill-down, but leaves `provider`, `base_url`, `title`, and
  `config`, which can imply the vendor. -> Mitigation: stated as an accepted residual in D9 with the reason (those
  fields describe the integration, and suppressing them needs an organisation dimension
  collectors do not have). Reviewers should confirm they agree with that line, because
  it is the one place this change knowingly stops short of "no vendor information
  leaks".
- **R9 Migrated vendors have a null `owner` until the next sync.** Migration
  `019_asset_unification.sql` copies legacy `vendors` rows into `assets` WITHOUT an
  `owner` ("owner is re-authored from config on the next sync"). Once vendor
  readability follows `owner` and is fail-closed, every such vendor is invisible to
  every caller. -> Mitigation: the Migration Plan below carries the cutover step.

- **R10 The unified asset read widens every organisation-scoped WRITE gate.** This is the
  largest correctness risk in the change and it has two distinct halves.

  The first is the two write-path lookups that read the organisation list directly.
  `OrganisationPutSelector`'s existence test and `UpsertOrganisationAsync`'s stored-parent
  read both decide an authorization outcome from what that list contains; an unfiltered
  list would turn `PUT /organisations/{machineId}` into an update and lower the bar from
  `system.admin` to `org.write` on the machine's parent.

  The second is broader and does not depend on those two call sites at all. Retargeting
  `AuthzRequestCache` at the asset list (D8) changes what `Authorizer.ResolveAncestryAsync`
  resolves for EVERY organisation-scoped route, because the ancestry walk now follows a
  non-organisation id's `parent` chain into the organisation tree. Eight constructions pass an
  organisation id: the `PUT /organisations/{id}` selector, the `DELETE /organisations/{id}`
  selector, the two scope-write body selectors, the two stored-owner scope-delete selectors, the
  `AuthorizeOrgAsync` helper behind the cross-org-move and reparent checks, the role-assignment
  API's `OrgVisibilitySelector`, and the role-assignment page's handler guard. Without a guard, a
  caller with `org.write` or `compliance.scope.write` on an organisation beyond a non-organisation
  link passes gates that today refuse everyone but a super-admin.
  Nothing in the read-side work would surface this: the routes keep compiling, the reads
  keep returning the same rows, and only an authorization test that names such an id fails.

  A third, narrower half hides inside the second: the widening does not need the SUPPLIED id
  to be a machine. An organisation whose `parent` names one carries the walk through it into a
  second organisation subtree, so a guard that fired only on a non-organisation supplied id
  would miss it - and so would a site list derived from "which ids might not be organisations",
  which is why the guard covers every construction carrying an organisation id rather than a
  chosen subset of them. That is also why D15 cuts the chain at the first non-organisation entry
  rather than pinning `[id]`.

  -> Mitigation: D5 requires the two lookups to filter with `AssetNode.IsOrganisation`
  before use, and D15 requires every organisation gate to anchor on the organisation-bounded
  chain before it reaches org RBAC, deciding the refusal at the authorization layer rather
  than leaning on the write store's SQL type predicates. Regression tests cover every
  construction with a caller who holds the route's permission on the organisation beyond the
  link, asserting the status the route gives today - `403` everywhere but the role-assignment
  API, which gives `404` under `Enforce` - and never a `400` or a no-op `204`, plus the
  organisation-parented-onto-a-machine case the cut exists for, on a stored-owner scope delete
  and on a plain organisation update.

## Migration Plan

No data migration and no schema change: `assets.parent` and `assets.owner` already
exist. Deployment is a normal app rollout, and rollback is a redeploy of the previous
build, because no schema or data state changes.

One CUTOVER step is required in a deployment that ran migration `019` against legacy
data. `019` copies each legacy vendor into `assets` with a null `owner`, to be
re-authored from config on the next GitOps sync. Read-access is fail-closed on that
edge, so between the migration and the next sync every migrated vendor is invisible to
every caller, including a super-admin, and its vendor-subject scopes are omitted from
`/scopes`. The runbook step is therefore: after `019`, author an `owner` on every vendor
in the GitOps config and run `freeboard gitops sync` BEFORE the app build carrying this
change goes live. `ConfigValidator` already warns on an ownerless vendor, so the sync
surfaces the gap rather than silently completing.

This is a pre-production concern only - there is no deployment carrying legacy vendor
data that must survive - but it is written down because "the vendors all disappeared" is
otherwise indistinguishable from a bug in the new closure.
