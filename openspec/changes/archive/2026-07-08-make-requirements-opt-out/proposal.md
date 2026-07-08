## Why

Standard scoping is opt-in today: a standard with no `Scope` on the path from an
organisation node to the tree root resolves `Undetermined`, which the Statement of
Applicability treats as not-in-scope. A deployment must author an explicit `Scope In`
to bring any standard into scope. This is the wrong default for the product: it forces
users to in-scope everything by hand and makes it easy to leave a standard silently
out of scope. The desired model is opt-out: standards are in scope by default, and a
user explicitly opts a standard out. The only case where a user manually opts back in
is to override an opted-out ancestor at an individual organisation.

## What Changes

- **BREAKING** (behavioural): the standard-level default flips. With no `Scope` on the
  path from a node to the root, the node's disposition for a standard resolves `In`
  (was `Undetermined`, treated as out of scope). Every standard that a deployment left
  unscoped becomes in scope.
- Replace the `Undetermined` resolution provenance with `Default`: a node whose
  standard disposition comes from neither its own `Scope` (`explicit`) nor an
  ancestor's (`inherited`) resolves `In` marked `default`. The resolved standard
  disposition is therefore always `In` or `Out`, never null.
- Preserve opt-out: a user authors a `Scope Out` to remove a standard, and a descendant
  authors a `Scope In` to override an opted-out ancestor. This is unchanged
  nearest-ancestor behaviour; only the empty-path default changes.
- The requirement layer is already opt-out (an absent `RequirementScope` under an `In`
  standard follows the standard, i.e. `In`); it is unchanged except that its former
  standard-`Undetermined` branch is removed, because a standard no longer resolves
  `Undetermined`.
- Update the Statement of Applicability read view and JSON endpoint: the disposition
  badge is always `In` or `Out`; the resolution wire value set becomes `explicit`,
  `inherited`, `default` (was `explicit`, `inherited`, `Undetermined`).
- Update `docs/gitops.md` scope and requirement-scope resolution prose to describe the
  opt-out default. Operator-facing meaning change, stated explicitly: omitting a `Scope`
  for a standard now means the standard is IN scope by default (previously it meant out
  of scope); to keep a standard out, author an explicit `Scope Out`.

No database schema change, no GitOps config schema change: the `scopes` and
`requirement_scopes` tables and the `Scope`/`RequirementScope` YAML kinds are
byte-identical. Only the resolution semantics computed in the web app change.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `statement-of-applicability`: the standard-level nearest-ancestor rule defaults to
  `In` (was `Undetermined`); the resolution provenance enum replaces `Undetermined`
  with `Default`; the requirement-level rule drops its standard-`Undetermined` branch;
  the read-only projection reports a non-null disposition for every node and the new
  resolution wire values.

## Impact

- Resolver: `src/Freeboard/Compliance/StatementOfApplicability.cs` (enum, wire names,
  the empty-path branch, doc comments).
- Read surfaces: `src/Freeboard/Compliance/ComplianceEndpoints.cs` (SoA JSON endpoint),
  `src/Freeboard/Pages/Compliance/StatementOfApplicability.cshtml.cs` and
  `.cshtml` (disposition label and badge branch).
- Evidence ingest: `src/Freeboard/Evidence/EvidenceIngestEndpoints.cs` reuses the SoA
  scope resolver for its in-scope check. No code change: an (org, requirement) pair that
  previously resolved `Undetermined` is now accepted because the node resolves `In` by
  default; an explicit `Scope Out` still causes ingest rejection. Covered by tests only.
- Tests: `tests/Freeboard.Web.Tests/StatementOfApplicabilityTests.cs`.
- Docs: `docs/gitops.md` scope and requirement-scope resolution prose.
- Persistence: none. No migration, no store or query change.
- GitOps config schema: none. No `apiVersion` bump.

MIT, not EE. This is core compliance scoping logic. It lives in the MIT web project
`Freeboard` (which references Core and Persistence for the read models) and touches no
`Freeboard.Enterprise` code. `Freeboard.Agent` and `Freeboard.CLI` are not involved.

## Non-goals

- Not moving the resolver out of the web project into `Freeboard.Core`. It stays where
  it is; relocating it is a separate refactor with no payoff here.
- Not adding a per-standard or per-deployment configurable default. The default is
  fixed to `In`.
- Not auto-authoring `Scope Out` rows to preserve any deployment's prior
  implicit-out set. The flip to in-scope-by-default is the intended outcome.
- Not changing the requirement layer's opt-out behaviour, the `Scope`/`RequirementScope`
  schema, the persistence schema, authorization/accessible-set narrowing, or the
  vendor-scope model.
