## 1. Flip the standard-level default to opt-out

Commit: `feat(compliance)!: resolve standards in-scope by default (opt-out)`

- [x] 1.1 In `src/Freeboard/Compliance/StatementOfApplicability.cs`, rename
  `SoaResolution.Undetermined` to `SoaResolution.Default` and update its doc comment to
  say it marks a node with no Scope on the path, resolving the default `In`.
- [x] 1.2 In `SoaResolutionNames.ToWireValue`, map `SoaResolution.Default` to `"default"`
  and remove the capital-`U` `"Undetermined"` special case (all three values are now
  lowercase: `explicit`, `inherited`, `default`).
- [x] 1.3 In `ResolveNode`, change the no-scope-on-path return from
  `(null, SoaResolution.Undetermined)` to `(nameof(ScopeDisposition.In), SoaResolution.Default)`
  so the disposition is never null and the node is in scope by default.
- [x] 1.4 Encode the never-null invariant in the types: change `SoaNode.Disposition`
  from `string?` to non-nullable `string`, and change `ResolveNode`'s return tuple from
  `(string? Disposition, SoaResolution Resolution)` to `(string Disposition, SoaResolution Resolution)`.
  Nullable is enabled, so leaving the tuple element nullable would make task 2.1's direct
  return emit CS8603; the resolver no longer produces a null disposition.
- [x] 1.5 Update the `SoaNode` and `StatementOfApplicability` type/class doc comments to
  state the standard disposition is always `In` or `Out` (never null) and that `default`
  means in-scope with no authored Scope. Remove the "Undetermined is distinct from Out"
  wording.
- [x] 1.6 Confirm the requirement-layer gate is unchanged (it runs when disposition
  equals `In`, which now includes `default`-`In` nodes) and no standard-`Undetermined`
  branch or null-disposition return remains in the file.

## 2. Simplify the read surfaces

Commit: `refactor(web): drop the null-disposition path from the SoA read surfaces`

- [x] 2.1 In `src/Freeboard/Pages/Compliance/StatementOfApplicability.cshtml.cs`,
  simplify `DispositionLabel` to return `node.Disposition` directly (the `?? "Undetermined"`
  fallback is dead now the disposition is non-null); update its doc comment to "In or Out".
- [x] 2.2 In `src/Freeboard/Pages/Compliance/StatementOfApplicability.cshtml`, remove the
  unreachable third disposition branch (the `badge-warn` `@disposition` else) so only the
  `In` and `Out` badges remain.
- [x] 2.3 Verify the SoA JSON endpoint in `src/Freeboard/Compliance/ComplianceEndpoints.cs`
  needs no code change (it emits `disposition` and `resolution` via `ToWireValue`, which
  now yields `default`); do not alter its shape.

## 3. Update tests

Commit: `test(web): cover opt-out default scope resolution`

- [x] 3.1 In `tests/Freeboard.Web.Tests/StatementOfApplicabilityTests.cs`, rework the
  three tests that assert `Undetermined`, giving each its post-change outcome. After this,
  no test asserts an `Undetermined` resolution or an empty requirements list on a
  defaulted-in node:
  - `NoAncestorDispositionIsUndeterminedNotOut`: rework (rename to match) so a no-Scope
    node resolves `In` marked `SoaResolution.Default` with a non-null disposition.
  - `ScopeForAnotherStandardDoesNotLeak`: keep the no-leak intent - a `Scope` for a
    different standard must not make THIS standard `explicit`/`inherited`; assert this
    standard resolves `In` marked `default`.
  - `EmptyRequirementListOnUndeterminedNode`: it seeds a `RequirementScope Out` with no
    standard `Scope`. Post-change the node resolves `In`/`default` and reports that
    requirement as an `Out` deviation, so its `Assert.Empty(Requirements)` inverts.
    Reframe (rename) it to assert the node resolves `In`/`default` and the
    `RequirementScope Out` deviation IS reported. Drop the empty-list assertion. This
    subsumes the standalone default-`In`-reports-a-deviation case, so do not add a
    separate test for it.
- [x] 3.2 Add a test for descendant-overrides-opted-out-ancestor: a parent `Scope Out`,
  a child `Scope In` resolving `In` `explicit`, and a no-scope sibling resolving `Out`
  `inherited`.
- [x] 3.3 Keep the `Scope Out` opt-out, inheritance, and requirement child-override
  scenarios green; ensure no test still asserts an `Undetermined` resolution or an empty
  requirements list on a defaulted-in node.
- [x] 3.4 In `tests/Freeboard.Web.Tests/EvidenceIngestEndpointTests.cs`, add a test that a
  defaulted-in (org, requirement) pair - an organisation with no `Scope` for the standard -
  is accepted by ingest (201), confirming the flip widens the ingest in-scope set.
- [x] 3.5 Confirm the existing `OrganisationNotInScopeIs422` test still covers ingest
  rejection when the standard carries an explicit `Scope Out` on the node; keep it green.
- [x] 3.6 Cover the read surfaces for the new `default` wire value. In
  `tests/Freeboard.Web.Tests/ComplianceEndpointTests.cs`, add a test that the SoA JSON
  endpoint emits `resolution == "default"` and `disposition == "In"` for a standard/org
  with no `Scope`. In `tests/Freeboard.Web.Tests/StatementOfApplicabilityPageTests.cs`,
  assert a defaulted-in node renders as in-scope with the `default`/`In` badge.

## 4. Update documentation

Commit: `docs(gitops): describe opt-out scope resolution`

- [x] 4.1 In `docs/gitops.md`, update the Scope resolution prose (the "Dispositions are
  sparse ... A node with no such ancestor is undetermined" paragraph) to state that a
  node with no Scope on its path defaults to `In` (in scope), that `Out` opts a standard
  out, and that a descendant `In` overrides an opted-out ancestor.
- [x] 4.2 In `docs/gitops.md`, update the RequirementScope resolution prose to drop the
  `Undetermined` case (a standard now resolves `In` or `Out`).
- [x] 4.3 In `docs/gitops.md`, update the SoA read-API output description (the
  `GET /api/v1/freeboard/statement-of-applicability/{standardId}` bullet): the resolution
  value set becomes `explicit`/`inherited`/`default`; the disposition is always `In` or
  `Out`; an `Out` node always carries an empty `requirements` list (requirement scopes are
  not applied under an out-of-scope standard); an in-scope node (`explicit`, `inherited`,
  or `default`) carries its per-requirement deviations, which is an empty list when it has
  none.
- [x] 4.4 Add a short migration note in `docs/gitops.md`: a standard left unscoped is now
  in scope; a deployment that wants it out MUST author an explicit `Scope Out`.

## 5. Verification

Commit: folded into the relevant commit above; no separate commit.

- [x] 5.1 `dotnet build` succeeds.
- [x] 5.2 `dotnet test tests/Freeboard.Web.Tests` passes (SoA unit tests reflect the new
  default).
- [x] 5.3 `npx markdownlint-cli2 "docs/gitops.md"` passes.
- [x] 5.4 `openspec validate "make-requirements-opt-out" --strict` passes.
