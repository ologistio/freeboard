Groups 1 to 7 are ONE compilable unit landing in one PR. Group 1 deletes `OrganisationRow`,
`VendorRow`, `GetOrganisationsAsync`, and `GetVendorsAsync`, and about twenty source files
that name them (or `IOrgAccess`, or the snapshot properties this change reshapes) are
retargeted across groups 2 to 7, so no group in that span compiles on its own. The separate
commit subjects below are kept because each carries its own type and `BREAKING CHANGE:`
footer into the release notes; they are not separately buildable checkpoints. The SOURCE build
gate for the whole unit is task 7.3, which builds `src/Freeboard` and therefore
`Freeboard.Core`, `Freeboard.Persistence`, and `Freeboard.Enterprise` transitively. It is not a
whole-solution gate: `Freeboard.slnx` includes the test projects, and about twenty-five of their
files name the deleted types and are retyped in group 8, so a root `dotnet build` stays red
until then. The whole-solution gate is 8.9.

## 1. Unify the persistence read model on one asset set

Commit: `refactor(persistence)!: read one asset set in place of organisations and vendors`,
with a `BREAKING CHANGE:` footer describing the removal of `GetOrganisationsAsync` and
`GetVendorsAsync`, the deletion of `OrganisationRow` and `VendorRow`, and the five
subject-narrowing fields dropped from `ScopeRow`.

- [x] 1.1 Add `AssetNode(Id, Title, Type, Source, State, Parent, Owner)` to
  `src/Freeboard.Persistence/ComplianceReadModels.cs` and delete `OrganisationRow` and
  `VendorRow`.
- [x] 1.1a Give `AssetNode` one `IsOrganisation` predicate (`Type is "Company" or
  "Department"`) and make it the only expression of that rule ON `AssetNode`. About a dozen
  sites need the test - the resolver's node set (3.2), the drill-down's node filter (3.4), the
  org-only projections and selectors (4.2, 4.4), the write-path authorization guards
  (group 5), and the ingest acceptance gate (7.2) - and `.claude/rules/code-as-liability.md`
  forbids a dozen copies of one rule. It lives on `AssetNode` in `Freeboard.Persistence`
  because that is where the read model lives and every C# caller is in `Freeboard`
  (web) or `Freeboard.Persistence`, both of which already see it under the reference
  graph. Delete the two existing hand-written literals it subsumes:
  `ComplianceWriteEndpoints.IsOrgSubject` (replaced in 5.3, and it reads the deleted
  `ScopeRow.SubjectType`) and the `/scopes` arm of `ComplianceEndpoints.SubjectReadable`
  (deleted with the whole method in 4.1). The predicates that spell out the same two type
  names elsewhere stay, because none of them is over an `AssetNode` and so none can call it:
  the `type IN ('Company', 'Department')` predicates in `MySqlComplianceWriteStore` and
  `MySqlAuthzAdministrationStore` are SQL in another layer (see D15 and 5.5);
  `MySqlComplianceWriteStore`'s `locked.SubjectType is not ("Company" or "Department")` is
  over the write-path locked scope row; and `GitOps/ImportPlan.OrganisationIds` is over
  `AssetRowPlan`, the parsed CONFIG row the importer is about to write rather than the row
  the store read back.
- [x] 1.2 Replace `IComplianceStore.GetOrganisationsAsync` and `GetVendorsAsync` with one
  `GetAssetsAsync`; update the interface doc comment.
- [x] 1.3 In `MySqlComplianceStore`, add one shared `AssetSelect`
  (`id, title, type, source, state, parent, owner FROM assets ORDER BY id`) and implement
  `GetAssetsAsync` from it. It replaces the two `type IN ('Company','Department')` snapshot
  queries and carries NO `WHERE` clause: one snapshot read has to serve the resolution tree,
  the drill-down's vendor titles, and the live-subject predicate, and no single `WHERE`
  satisfies all three. Retirement is applied by the consumers.
- [x] 1.3a Confirm the deleted `ResolvableAssetIdsSelect` predicate is preserved exactly. Its
  SQL, `WHERE NOT (source = 'discovered' AND state = 'Retired')`, also excludes a discovered
  row whose `state` is NULL; the in-memory predicate would include it. Verify no such row is
  producible - `MySqlAssetWriteStore` sets `state` on every discovered insert and only moves it
  between `Seen` and `Retired`, the GitOps importer's asset insert names no `state` column so
  only DECLARED rows read NULL, and migration `019` copied the legacy discovered rows from a
  `NOT NULL` column - and then write the in-memory predicate as "a discovered asset in state
  `Retired`", with no null branch. If a null-state discovered row turns out to be producible,
  preserve the exclusion instead.
- [x] 1.4 Trim `ScopeRow` to its eight public fields and drop the `LEFT JOIN assets` from
  `ScopeSelect`.
- [x] 1.5 Reshape `SoaInputs` to `(Assets, Scopes, Requirements)` and `SoaDrilldownInputs` to
  `(Assets, Scopes, Requirements, Controls, Collectors)`; delete the
  `ResolvableAssetIdsSelect` query and both snapshot reads' organisation and vendor reads,
  replacing them with the one `AssetSelect` inside the same repeatable-read transaction.
- [x] 1.6 Retarget any in-repo consumer the compiler flags. `Freeboard.CLI` names none of the
  deleted types (verified: no `OrganisationRow`, `VendorRow`, `GetOrganisationsAsync`,
  `GetVendorsAsync`, or `IComplianceStore` reference under `src/Freeboard.CLI`), so confirm
  `Freeboard.Core`, `Freeboard.Agent`, and `Freeboard.CLI` still build with no source change.

## 2. One shared ancestry walk and one read-access closure

Commit: `feat(authz)!: resolve read-access over the asset tree via parent and owner`,
with a `BREAKING CHANGE:` footer describing the `IOrgAccess` -> `IAssetAccess` seam rename
and the accessible set changing from organisation ids to asset ids.

- [x] 2.1 Rename `src/Freeboard/Compliance/OrgAncestry.cs` to `AssetAncestry.cs`, typed over
  `AssetNode`, keeping the visited-set cycle guard. Memoize the resolved inclusive chain per
  node id for the life of ONE pass via a CALLER-SUPPLIED cache: keep `InclusiveAncestors` a
  pure static and add an overload taking the id-to-node map plus a
  `Dictionary<string, IReadOnlyList<string>>` that the caller creates at the start of a pass
  and discards at the end. NO static mutable state - a static memo on this class would be
  process-wide, unsafe under concurrent requests, and would serve stale ancestry after a sync
  changed an asset's `parent`.
- [x] 2.2 Add `src/Freeboard/Compliance/AssetReadAccess.cs`: a pure
  `AccessibleAssetIds(IReadOnlyList<AssetNode> assets, IReadOnlySet<string> organisationUnion)`
  implementing the three edge branches (parent chain, owner, own id) and excluding discovered
  assets in state `Retired`. Build one id-to-node map up front and reuse the memoized
  ancestry from 2.1, so no `parent` edge on a cycle-free chain is walked twice in a pass as
  the asset count grows (a chain terminating on a cycle caches nothing and is re-walked). The
  closure still scans the returned chain per asset, so the bound is nodes x depth, not linear -
  see D12.
- [x] 2.3 Rename `src/Freeboard/Web/OrgAccess.cs` to `Web/AssetAccess.cs`: `IAssetAccess` with
  `AccessibleAssetIdsAsync(ClaimsPrincipal, IReadOnlyList<AssetNode>, CancellationToken)` and
  the `AllAssetAccess` test double, which returns the closure over the supplied assets with
  every organisation in the union.
- [x] 2.4 Rename `AuthzOrgAccess` to `AuthzAssetAccess`: keep the rollout-mode branches and the
  `ReadSubtreeUnion` grant walk (now over the organisation-typed assets), and apply
  `AssetReadAccess.AccessibleAssetIds` to the union before returning. Keep the Observe
  would-narrow log and the Compat fallback audit unchanged. Retype the `all` set to every
  ORGANISATION-TYPED asset id, not every asset id: it is the step-one union, and all three
  paths that return it - super-admin, `Observe`, and the `Compat` zero-grant fallback - must
  hand it to the closure rather than returning it as the accessible set. Retyped naively it
  would return every asset id and bypass step two, readmitting the ownerless vendor and the
  retired discovered asset that D4 and the `Observe` spec scenario put outside the set in
  every mode. The would-narrow log compares two organisation counts, so it stays as written.
- [x] 2.5 Point `AuthzRequestCache` at `GetAssetsAsync` and retype its memoized list; update
  `Authorizer.ResolveAncestryAsync` to walk `AssetAncestry`. This WIDENS what an
  organisation gate resolves, in two ways: a `Machine` id resolves to `[machineId]` today and
  to the machine's whole `parent` chain afterwards, and an ORGANISATION whose `parent` names a
  machine stops at that machine today and runs on through it into a second organisation
  subtree afterwards. Group 5 must land with it.
- [x] 2.6 Add `AuthzRequestCache.OrganisationResourceAsync(type, id, orgId, ct)`: it builds
  the `AuthzResource` with `OrgAncestryInclusive` pinned to the ORGANISATION-BOUNDED chain -
  the cycle-guarded inclusive `parent` chain over the assets, CUT immediately after its first
  entry that names a non-organisation asset. The offending entry is kept and the walk goes no
  further, so a `Machine` id yields `[machineId]` and a `Department` whose `parent` names a
  machine yields `[dept, machineId]`; an entry naming NO asset is kept and is not a cut point,
  because the walk already stops there for want of a `parent`. That reproduces the pre-change
  chain for every id class, since today's walk reads an organisation-only map and stops at the
  first id absent from it - an id naming no asset, a machine, a vendor, a dangling `parent`, a
  `parent` cycle, and an organisation parented onto a machine all resolve to the same chain
  before and after. Pin unconditionally rather than leaving the field unset: the cut chain and
  what `ResolveAncestryAsync` would compute agree wherever no cut applies, so one rule covers
  every case, including a create-root `PUT` on an id naming no asset, which still authorizes
  the bare new id alone. It goes on the existing scoped cache rather than in a new service
  because that is where the memoized asset list already lives; update the class doc comment to
  say it also builds the organisation resource. See D15.
- [x] 2.7 Update the DI registration in `Program.cs`.

## 3. Resolve the Statement of Applicability over the asset tree

Commit: `feat(compliance)!: resolve applicability over the asset parent tree`, with a
`BREAKING CHANGE:` footer describing the `explicit` -> `asset` resolution-source wire rename
on the Statement-of-Applicability JSON endpoint and page, and the node set widening to
`Machine` assets.

- [x] 3.1 Rename `SoaResolution.Explicit` to `SoaResolution.Asset` and its wire value
  `explicit` to `asset` in `SoaResolutionNames`. Carry the rename through the rest of
  `StatementOfApplicability.cs` in the same edit: the doc comments that name the provenance
  "explicit" (on the node record, the drill-down record, the projection summary, and both
  precedence walks) and the `explicitByOrg` identifier the standard layer threads from its
  scope index into `ResolveNode`. Left alone they would describe a value the code no longer
  emits, which `comment-etiquette.md` forbids; the identifier names the same map, so it reads
  as the asset-leaf index it now is.
- [x] 3.2 Change `StatementOfApplicability.Resolve` to take `IReadOnlyList<AssetNode>` and
  derive its node set as every asset for which `AssetNode.IsOrganisation` holds PLUS every
  other asset whose cycle-guarded inclusive `parent` chain reaches one. An organisation is a node
  unconditionally - a dangling `parent` or a `parent` cycle must not drop it, since both are
  non-blocking GitOps warnings and both resolve today. Only a non-organisation asset needs a
  root; vendors fall out because they are neither organisation-typed nor parent-carrying.
- [x] 3.3 Keep the ordered precedence probe (asset leaf, then nearest ancestor, then default
  `In`) as a single walk so the #123 group rank inserts between the first two.
- [x] 3.4 Change `ResolveDrilldown` to take `IReadOnlyList<AssetNode>` and the caller's
  accessible asset set in place of the `vendors` parameter it drops, and filter its node list
  with `AssetNode.IsOrganisation`. Build `vendorTitleById` from the `Vendor`-typed assets THAT
  ARE IN THAT SET, never from the vendor assets at large: a vendor outside it contributes no
  entry, so the check's vendor is null and renders as an unset one. DELETE the raw-id fallback
  in `BuildControlCatalogue` (`vendorTitleById.TryGetValue(...) ? title : collector.Vendor`)
  rather than guarding it - there must be no path on which a vendor id is rendered. The
  fallback fires on exactly the vendor that must stay hidden: a vendor with a null or dangling
  `owner` resolves to no title. The page's existing `@if (check.Vendor is not null)` block
  already renders an unset vendor as nothing, which is what an unreadable one must match, so
  the `.cshtml` needs no change.
- [x] 3.5 Change `HasDanglingSubject` to take the asset list and apply the live-subject
  predicate (row exists and is not a discovered `Retired` asset) itself.

## 4. Narrow every read surface by one membership test

Commit: `feat(web)!: narrow compliance reads by the accessible asset set`, with a
`BREAKING CHANGE:` footer describing that global vendor readability is dropped - a vendor
whose `owner` is missing, dangling, or outside the caller's set is now invisible on
`/vendors`, `/scopes`, and the vendor register.

- [x] 4.1 In `ComplianceEndpoints`, delete `SubjectReadable` and narrow `/scopes` by
  `accessible.Contains(row.Subject)`.
- [x] 4.2 Serve `/organisations` and `/vendors` from `GetAssetsAsync` filtered by `type`,
  each narrowed by the same accessible-asset-set test; delete the `/vendors` owner filter.
  Select the organisation rows with `AssetNode.IsOrganisation`. Also apply `IsOrganisation` to
  `/organisations`' parent-nulling test, which today only asks whether the parent is accessible:
  once `accessible` is the accessible ASSET set, an organisation whose `parent` names a readable
  `Machine` would emit a machine id in a field that always holds an organisation id or null. That
  is no confidentiality regression - the machine is readable to that caller - but `parent` must
  keep naming a row of the same listing, the same organisation-only rule 4.4 applies to every
  surface presenting organisations. Keep both response shapes byte-identical.
- [x] 4.3 Narrow the Statement of Applicability JSON endpoint's nodes by the accessible asset
  set and emit the renamed `resolution` values.
- [x] 4.4 Retype `Pages/Compliance/StatementOfApplicability.cshtml.cs`,
  `Pages/Compliance/Vendors.cshtml.cs`, `Pages/Compliance/ControlDetail.cshtml.cs`,
  `Compliance/OrgScope.cs`, `Web/OrgSelection.cs`, and
  `Pages/Shared/Components/OrgSelector/OrgSelectorViewComponent.cs` onto
  `AssetNode`/`IAssetAccess`, intersecting with `AssetNode.IsOrganisation` where a surface
  presents organisations only. `Web/OrgSelectEndpoints.cs` needs no change: it names no
  organisation type, no access seam, and no asset - it sets or clears the selection cookie and
  redirects. `Compliance/ComplianceWriteEndpoints.cs` is retyped in group 5
  instead, because every one of its reads is an authorization input.
- [x] 4.4a Keep `OrgScope` organisation-only on BOTH branches once its node list and its
  accessible set are asset-typed. The selected-subtree branch descends `parent`, so filter the
  node list with `AssetNode.IsOrganisation` to stop the walk descending into a machine; the
  null-selection ("All Organisations") branch returns the supplied accessible set VERBATIM, so
  intersect it with the same organisation-typed ids or it puts machine and vendor ids in scope.
  Correct the class doc comment, which states that null branch as returning the accessible set
  whole. The production caller passes an already organisation-filtered set, so this is latent
  rather than a live leak - which is why the rule belongs in the code rather than in that
  caller.
- [x] 4.5 Delete the vendor register page's own owner filter, replacing it with the accessible
  asset set, and rewrite the `VendorsModel` doc comment, which states the rule this task
  replaces ("a vendor is shown only when its owner is in the caller's accessible-org set").
  State the rule now in force: a vendor is shown when it is in the caller's accessible asset
  set, which admits it exactly when its `owner` resolves into the caller's organisation union.
- [x] 4.6 Update any `.cshtml` that binds a renamed property or the `explicit` label.

## 5. Keep every organisation gate on organisation ancestry

Commit: `fix(authz): anchor an organisation gate on organisation ancestry`

No `!` and no `BREAKING CHANGE:` footer: the guard reproduces the ancestry the pre-change code
computed, so every write that succeeds today still succeeds and every one that is refused is
still refused with the same status. It must not take a MAJOR bump for holding a boundary still.

This is its own group and its own commit because it is write-path AUTHORIZATION, not the read
narrowing of group 4: it decides who may write, its regression tests assert status codes rather
than row sets, and folding it under group 4's breaking `feat(web)` subject would file an
authorization guard under a read-narrowing headline in the release notes.

The widening it answers comes from 2.5: `Authorizer.ResolveAncestryAsync` resolves the ancestry
of whatever id a selector puts in `AuthzResource.OrganisationId`, and once that walk runs over
the asset list it no longer stops where the organisation-only map stopped. A `Machine` id
resolves through its `parent` to its department and company, and an organisation whose `parent`
names a machine resolves on through that machine into a second organisation subtree.
`OrgRbacPolicy` permits when any grant organisation appears in that chain, so without this
group a caller holding `org.write` or `compliance.scope.write` on an organisation beyond the
non-organisation link passes a gate that today refuses it. See D15.

- [x] 5.1 Build every `AuthzResource` that carries an organisation id through
  `AuthzRequestCache.OrganisationResourceAsync` (2.6), so a grant beyond a non-organisation link
  is refused at the authorization layer and never reaches the store to be answered there as a
  `404`, a `400`, or a no-op `204`. State the rule over the CONSTRUCTION, not over the class of id
  a construction happens to receive: an id is not a safe anchor merely because it names an
  organisation, since that organisation's own `parent` may name a live machine and carry the walk
  on through it into a second subtree (D15). The sites are therefore exactly the `new AuthzResource`
  expressions whose organisation argument is non-null; a `system` resource passes `null` and needs
  nothing. Applying the guard uniformly costs nothing, because the cut chain and the plain walk
  agree wherever no cut applies - on an ordinary all-organisation chain the guard returns exactly
  what `ResolveAncestryAsync` would compute. All but the role-assignment API answer a refusal with
  `403`; its three routes keep the `404` their visibility selector already gives them, for the
  reason recorded under that bullet:
  - `ComplianceWriteEndpoints.OrganisationPutSelector` - ONE construction serving both arms, the
    update arm (`orgForAuth` is the route `id`) and the create arm (`orgForAuth` is the caller's
    `input.Parent`, or the route `id` when creating a root). The update arm is the same exposure as
    the create arm: an existing organisation parented onto a machine anchors a grant beyond it;
  - `ComplianceWriteEndpoints.RouteOrgSelector("organisation")` on
    `DELETE /organisations/{id}`;
  - `ComplianceWriteEndpoints.BodyOrgSelector<ScopeInput>("scope", i => i.Subject)` on
    `PUT /scopes/{id}`;
  - `ComplianceWriteEndpoints.BodyOrgSelector<RequirementScopeInput>` on
    `PUT /requirement-scopes/{id}`;
  - `ComplianceWriteEndpoints.StoredScopeOrgSelector` on `DELETE /scopes/{id}` and
    `StoredRequirementScopeOrgSelector` on `DELETE /requirement-scopes/{id}`. Their organisation is
    the stored row's `Subject`, which 5.3 confines to an organisation ASSET - which does NOT make it
    safe, for the reason above;
  - `ComplianceWriteEndpoints.AuthorizeOrgAsync`, the one helper behind the cross-org-move checks in
    `UpsertScopeAsync` and `UpsertRequirementScopeAsync` and behind `AuthorizeParentAsync`'s
    non-null-parent arm, so guarding inside it covers all three, including both of
    `UpsertOrganisationAsync`'s `AuthorizeParentAsync` calls - the caller's `input.Parent` and the
    stored `existing.Parent`. A stored `parent` naming a discovered machine's id is only a
    non-blocking GitOps warning, so it is reachable in a validly synced deployment.
    `AuthorizeParentAsync`'s null-parent arm builds a `system` resource and is untouched.
    `AuthorizeOrgAsync` is a static taking only `IAuthorizer`, so it gains an `AuthzRequestCache`
    parameter, as do `AuthorizeParentAsync` and the three static minimal-API handlers that reach
    it - `UpsertOrganisationAsync`, `UpsertScopeAsync`, and `UpsertRequirementScopeAsync` - the
    last three as a handler parameter the minimal-API container resolves from the scoped
    registration;
  - `RoleAssignmentEndpoints.OrgVisibilitySelector`, which serves all three
    `/organisations/{orgId}/role-assignments` routes. These three KEEP their `404`: the
    selector runs its own mode-aware `org.read` check and returns null on a deny, which the
    endpoint filter answers as existence non-disclosure. That is both today's outcome and what
    the established `authz-enforcement` requirement mandates for an org-scoped resource the
    principal cannot see, and a `403` on a machine id would disclose that the id exists. The
    outcome is mode-dependent because that read check is mode-relaxed: under `Enforce` (and
    for a grant-holding caller under `Compat`) the route answers `404`, while under `Observe`
    the would-be deny is not enforced, so the resource is selected and the route's
    force-enforced `authz.assignment.write` gate answers `403`. Both are unchanged by this
    group;
  - `Pages/Admin/RoleAssignments.cshtml.cs`'s `GuardAsync`, which reads `orgId` from the route
    and the form posts. `RoleAssignmentsModel` takes `AuthzRequestCache` as a constructor
    dependency to reach it; `AuthzPageGuard` already returns a bare `403` on a deny.
- [x] 5.2 In `ComplianceWriteEndpoints`, filter the two organisation lookups with
  `AssetNode.IsOrganisation` BEFORE they are used: `OrganisationPutSelector`'s existence test
  and `UpsertOrganisationAsync`'s `existing`/`existing.Parent` read. Both decide an
  authorization outcome, not a presentation one. Unfiltered,
  `PUT /organisations/{machineId}` flips from "create" to "update", so the selector authorizes
  `org.write` on the machine id instead of requiring `system.admin` on the create-root path.
- [x] 5.3 Replace `IsOrgSubject(ScopeRow)`, which reads the deleted `ScopeRow.SubjectType`, at
  its four call sites (the standard- and requirement-scope upsert handlers and their two
  delete selectors). Resolve the scope's `Subject` against the asset list and keep the row only
  when `AssetNode.IsOrganisation` holds for it. A subject that resolves to no asset yields no
  stored owner, exactly as an unresolved `SubjectType` does today, so the handler still takes
  the new/absent-row path and the store's global-id branch still answers `404`.
- [x] 5.4 Retype the rest of `Compliance/ComplianceWriteEndpoints.cs` onto `AssetNode`, and
  update the two selector comments that describe the stored-owner narrowing so they name the
  asset-list lookup rather than `SubjectType`.
- [x] 5.5 Record in `MySqlComplianceWriteStore`'s existing comments that its
  `type IN ('Company', 'Department')` predicates are DEFENCE IN DEPTH behind the authorization
  guard, not the thing holding the line: an unauthorized caller is now refused before the
  store is reached. Do the same for `MySqlAuthzAdministrationStore.OrganisationExistsAsync`,
  which applies the same predicate on the role-assignment write path and is what makes the
  premise the guard relies on - that no grant exists on a machine id - true. Do not remove
  either - a caller arriving by another path still has to meet them - and do not restate the
  guard's rule in SQL.

## 6. Stop the vendor id leaking through the two global reference reads

Commit: `fix(web)!: withhold an unreadable vendor id from collector and connection reads`,
with a `BREAKING CHANGE:` footer describing that `/collectors` and
`/integration-connections` now send `null` for a `vendor` outside the caller's accessible
asset set.

- [x] 6.1 In `ComplianceEndpoints`, emit `vendor` on `/collectors` and
  `/integration-connections` only when the id is in the caller's accessible asset set, and
  `null` otherwise. Keep both row sets global and both response shapes unchanged; both
  fields already declare themselves nullable. Neither endpoint can do this today: both take only
  `IComplianceStore` (and the token resolver), with no access seam and no principal. Each therefore
  gains `IAssetAccess` and a `ClaimsPrincipal` as handler parameters and a `GetAssetsAsync` read
  inside the existing try/catch, so a store outage still answers the 503 rather than throwing.
- [x] 6.2 Apply the same field rule in `Pages/Compliance/Collectors.cshtml.cs` and
  `Pages/Compliance/IntegrationConnections.cshtml.cs`, so an unreadable vendor renders exactly
  as an unset one. Neither page model can do this today: `CollectorsModel(IComplianceStore)`
  and `IntegrationConnectionsModel(IComplianceStore, IIntegrationTokenResolver)` have no
  access seam and read no assets. Add `IAssetAccess` as a constructor dependency to each and a
  `GetAssetsAsync` read inside the existing try/catch, so a store outage still renders the
  in-page notice rather than a 500. Keep the row sets global.
- [x] 6.3 Rewrite the two page-model doc comments this group falsifies. Both
  `CollectorsModel` and `IntegrationConnectionsModel` say the page "does NOT narrow by
  accessible organisation"; after 6.2 the row set is still global but the `vendor` field is
  narrowed. State that split, so neither comment asserts a condition the change removes.
- [x] 6.4 Rewrite the three `ComplianceEndpoints` comments that record the leak (the
  `/vendors` comment's "still expose a hidden vendor's id" sentence and the `/collectors`
  and `/integration-connections` "NOT narrowed" comments) to state the rule now in force:
  rows global, vendor id narrowed. Do not leave a comment asserting a condition the change
  fixes.

## 7. Move the evidence-ingest scope gate with the resolver

Commit: `refactor(evidence): resolve the ingest scope gate over the asset tree`

No `!` and no `BREAKING CHANGE:` footer: this group changes no observable ingest
behaviour. Every id ingest accepts today is still accepted and every id it rejects is
still rejected, so `refactor` is the accurate type and the release must not take a MINOR
bump for it. The type gate 7.2 adds is what PRESERVES the acceptance set once the
resolver widens; it is not a new capability.

- [x] 7.1 Point `EvidenceIngestEndpoints.IsOrganisationInScope` at the asset-typed resolver
  (`inputs.Assets`), which the reshaped `SoaInputs` forces.
- [x] 7.2 Keep the acceptance set organisation-only: after resolving, require
  `AssetNode.IsOrganisation` to hold for the matched node, so a posted `organisation_id`
  naming a `Machine` is still rejected `422`. Behaviour for organisation ids is unchanged, and
  the node set from 3.2 is what makes that exact: every organisation is a node under the new
  rule just as it is under today's `type IN ('Company','Department')` read, INCLUDING one whose
  `parent` dangles or sits in a cycle, so no id ingest accepts today becomes rejected.
- [x] 7.3 Verify: `dotnet build src/Freeboard`, which builds `Freeboard.Core`,
  `Freeboard.Persistence`, and `Freeboard.Enterprise` transitively. This is the first point at
  which the SOURCE tree compiles: it gates groups 1 to 7 together, which is what the preamble's
  one-unit rule means in practice. It is not a whole-solution gate - the test projects still name
  the deleted types until group 8, so a root `dotnet build` is red here by design and is gated at
  8.9.

## 8. Tests

Commit: `test: cover asset-tree resolution and read-access`

- [x] 8.1 Retype the web test doubles and fixtures (`FakeComplianceStore`, `AuthWebFactory`,
  and every test building `OrganisationRow`, `VendorRow`, or a narrowing-field `ScopeRow`)
  onto `AssetNode`.
- [x] 8.1a Update the five reflection assertions that pin a page model's exact constructor
  parameter list: `CollectorsPageTests.ConstructorTakesOnlyComplianceStore`,
  `IntegrationConnectionsTests.PageConstructorTakesStoreAndTokenResolver`,
  `VendorsPageTests.ConstructorTakesComplianceStoreAndOrgAccess`,
  `ControlDetailPageTests.ConstructorTakesComplianceStoreOrgAccessAndEvidenceStore`, and
  `StatementOfApplicabilityPageTests.ConstructorTakesComplianceStoreOrgAccessAndEvidenceStore`.
  The first two gain `IAssetAccess` (6.2); the last three swap `IOrgAccess` for `IAssetAccess`.
  Rename each to match what it now asserts. `RoleAssignmentsModel` gains
  `AuthzRequestCache` (5.1); if a test pins its parameter list too, update that with them.
- [x] 8.2 Resolution unit tests: `asset`/`inherited`/`default` over a mixed
  Company -> Department -> declared Machine + discovered Machine tree; machine leaf override;
  vendor absent from the node set; unrooted and dangling-parent MACHINE absent; requirement
  layer suppressed under an `Out` standard and overridable at a machine under an `In`
  standard.
- [x] 8.2a Node-set tests for the organisation cases the resolver must never drop: an
  organisation whose `parent` names an id no asset defines is still a node and resolves the
  default `In`; two organisations naming each other as `parent` both resolve and the node set
  is finite. `ScopeReadabilityTests` already fixtures this state (`org-mid` parented to the
  undefined `gone-org`), so reuse that shape rather than inventing a second one.
- [x] 8.2b Drill-down vendor tests: a readable vendor renders its title; an unreadable one
  renders exactly as a check with no vendor; no case renders a raw vendor id, including when
  the vendor asset is absent entirely.
- [x] 8.3 Read-access unit tests for `AssetReadAccess`: parent-chain hit and miss, owner hit
  and miss, missing/dangling edge, retired discovered exclusion, cycle termination, and the
  three rollout modes through `AuthzAssetAccess` - including that `Observe`, which widens the
  organisation union to everything, still excludes an ownerless vendor and a retired
  discovered asset, because the mode relaxes step one only.
- [x] 8.3a Ancestry memoization test: two passes over DIFFERENT asset lists through the same
  helper each return ancestries consistent with their own input, so no static mutable state
  survives a pass.
- [x] 8.4 Endpoint tests: `/scopes` and `/vendors` narrowing by the one test; `/organisations`
  and `/vendors` shapes unchanged; SoA JSON emits `asset` and includes readable machine nodes
  and excludes unreadable ones; the page renders organisation rows only.
- [x] 8.4a Organisation-only selection tests, feeding an accessible asset set that deliberately
  contains a `Machine` and a `Vendor` (the production caller filters them out already, so a
  positive-path fixture would pass with the intersection removed): neither id appears as a
  selector entry, a selection cookie naming either resolves to "All Organisations", and
  `OrgScope` excludes both on BOTH branches - the null "All Organisations" branch, which
  otherwise returns the supplied set verbatim, and the selected-subtree branch, whose `parent`
  walk must not descend into a machine to reach whatever hangs below it.
- [x] 8.5 Vendor-field tests: `/collectors` and `/integration-connections` return every row
  for a caller who cannot read a named vendor, with that row's `vendor` `null` and the
  readable case still carrying the id; the collector register and integration-connections
  pages render the unreadable vendor exactly as an unset one.
- [x] 8.6 Evidence-ingest tests: an in-scope organisation id is still accepted; an
  out-of-scope one is still rejected `422`; a `Machine` id that resolves in scope is
  rejected `422`; an organisation whose `parent` dangles, and one in a `parent` cycle, are
  both still accepted when in scope (they must not fall out of the node set); the ingest
  verdict for a node matches what the SoA read surfaces report for that same node.
- [x] 8.6a Write-path authorization regression tests. Every case uses a caller who is NOT a
  super-admin but DOES hold the route's permission on the machine's parent organisation - the
  caller the widened ancestry walk would otherwise let through - and asserts the status named,
  never a `400` or a no-op `204`:
  - `403` on `DELETE /organisations/{machineId}`;
  - `403` on `PUT /scopes/{id}` with `subject` naming a `Machine`, and the same for
    `PUT /requirement-scopes/{id}`;
  - `403` on `PUT /organisations/{newId}` creating with `parent` naming a `Machine`, and on
    reparenting an existing organisation onto a `Machine`;
  - `403` on the role-assignment page's `GET` and its grant and revoke posts with a machine
    `orgId`;
  - `404` on `GET`, `PUT`, and `DELETE` on `/organisations/{machineId}/role-assignments` under
    `Enforce`, because the route's visibility selector withholds the resource before the
    assignment gate runs. Assert the `Observe` variant too, where the mode-relaxed read check
    permits and the force-enforced assignment gate answers `403`, so the mode dependence is
    pinned rather than discovered.

  Plus the cases that must keep their CURRENT outcome. `PUT /organisations/{machineId}` is
  still a CREATE, and the status depends on the body: with `parent: null` it takes the
  create-root path and is refused `403` for want of `system.admin`; with `parent` naming an
  organisation the caller holds `org.write` on, the selector permits and the request reaches
  the store, which finds no organisation row to lock and then collides on the existing
  `assets.id`, answering `409`. Both are today's outcomes. And a scope write whose stored row
  has a `Vendor` or `Machine` subject still resolves to `404` rather than authorizing against
  that row.
- [x] 8.6b A super-admin is unaffected: the guard removes an ancestry match, so `SystemPolicy`
  still permits and the request reaches the store, which no-ops or 404s on the non-organisation
  id exactly as it does today.
- [x] 8.6c Ancestry-cut regression tests for the id class the guard would otherwise widen: an
  ORGANISATION whose `parent` names a live `Machine` that itself hangs under another
  department and company. A caller holding the route's permission on that far company - and nothing
  at or above the organisation itself - is refused `403`, because the chain is cut after the
  machine. Cover the constructions that take a stored or route-supplied organisation id, not only
  the ones that take it from a body: `DELETE /scopes/{id}` where the stored row's `Subject` is that
  organisation, and a plain `PUT /organisations/{existingOrgId}` update on it. Both build their
  resource from an id that IS an organisation, which is why the guard cannot key on the id class.
  Assert the cut chain directly as well, for every id class it has to
  reproduce: an id naming no asset, a machine, a retired discovered machine, a vendor, an
  organisation with a dangling `parent`, an organisation in a `parent` cycle, an organisation
  parented onto a machine, and an ordinary all-organisation chain.
- [x] 8.7 MySQL integration tests (`tests/Freeboard.Persistence.Tests`, gated on
  `FREEBOARD_TEST_DB`, skipping cleanly when unset): `GetAssetsAsync` over a seeded MIXED
  `Company` -> `Department` -> declared and discovered `Machine` tree (proving the read no
  longer stops at `Company`/`Department`) carrying a vendor with its `owner`; both SoA snapshot
  reads; the scope read without its join. Persistence can assert rows and edges only; the
  CALLER-specific narrowing (a vendor visible to one caller and not another) belongs in the
  web/authz tests of 8.3 and 8.4, not here.
- [x] 8.7a Update the existing `Freeboard.Persistence.Tests` call sites of the deleted store
  methods and snapshot properties: `MySqlIntegrationTests.cs` (lines 560, 648, 821, 903, 952,
  1206, 1307, 1327, 1407, 1433), `AssetUnificationIntegrationTests.cs` (283, 295),
  `ScopeGeneralizationIntegrationTests.cs` (344), and
  `IntegrationConnectionIntegrationTests.cs` (216). Most are mechanical
  `GetOrganisationsAsync`/`GetVendorsAsync` -> `GetAssetsAsync` retypes with a `Type` filter,
  but three are SEMANTIC: `MySqlIntegrationTests.cs:1307` and `:1434` assert
  `inputs.Organisations`, and `:1327` asserts `inputs.Vendors`, on snapshot properties this
  change deletes. Rewrite those three against `inputs.Assets` and assert the widened content
  (machines present, both edges carried) rather than translating the old expectation.
- [x] 8.8 Retype the `Freeboard.WebE2E` fixtures; update
  `StatementOfApplicabilityE2ETests` to assert `asset` where it asserts `explicit` today;
  assert a caller outside a vendor's owner subtree sees neither the vendor nor its scopes.
- [x] 8.9 Verify: `dotnet build` at the repo root - the whole-solution gate, and the first point
  the test projects compile, since `Freeboard.slnx` includes them and group 8 is what retypes them.
  Then `dotnet test`; then with the compose stack up and `FREEBOARD_TEST_DB` set, `dotnet test`
  again so the integration tier runs.

## 9. Documentation and final verification

Commit: `docs: record the asset-tree resolution and read-access rules`

- [x] 9.1 Update `docs/gitops.md`: the scope section's "resolves `In` marked `explicit`
  instead of `default`" and the API section's "every organisation node ... whether that
  value is `explicit`, `inherited`, or `default`" (both the standard and requirement
  layers). State the node set as the asset forest and the provenance as
  `asset|inherited|default`.
- [x] 9.2 In the same file, add the vendor-field caveat to the `/collectors` and
  `/integration-connections` entries: the rows stay global, the `vendor` id does not.
- [x] 9.3 Sweep every remaining in-repo doc, comment, and established spec that describes
  resolution as organisation-only, names the provenance value `explicit`, or calls the
  narrowing set the "accessible-organisation set". Behaviour at each of these is unchanged, so
  none needs a spec delta, but leaving the wording makes the one narrowing rule read as
  several. The known sites:
  - `openspec/specs/vendor-register/spec.md` (a vendor is still admitted exactly when its
    `owner` is in the caller's organisation union);
  - `openspec/specs/web-object-drawer/spec.md`, which governs `ControlDetail.cshtml.cs` -
    retyped by 4.4 - and says "accessible organisation set" three times, including the
    not-found rule;
  - `docs/gitops.md`'s two vendor/scope readability paragraphs, which name the
    "accessible-organisation set" for both the scope subject and the vendor register.
- [x] 9.4 Confirm CLI parity with no CLI source change: `vendor list` and the scope reads
  narrow server-side, and `collector list` and `connections list` render `-` in the vendor
  column when the endpoint sends `null` - both renderers already do this
  (`CollectorCommands.Print`, `ConnectionCommands.Print`) and both DTO fields are already
  `string?`. Add the missing assertions in `tests/Freeboard.CLI.Tests`: the collector list
  case uses a non-null vendor and never covers the null path, and the connection list case has
  a null-vendor row but never asserts the rendered column. Record the parity result in the PR
  description.
- [x] 9.5 Record the cutover step as a deployment runbook in proposal.md for any deployment that ran migration
  `019` against legacy data: author a vendor `owner` in config and run
  `freeboard gitops sync` before this build goes live, or every migrated vendor is
  invisible.
- [x] 9.6 Verify: `dotnet build`, `dotnet test`, `npx markdownlint-cli2 "**/*.md"`, and
  `openspec validate "object-model-v2-resolution-read-access" --strict`.
