## Context

Scoping is resolved in one pure function, `StatementOfApplicability.Resolve`, in the
MIT web project (`src/Freeboard/Compliance/StatementOfApplicability.cs`). It projects
the persisted organisation tree, `scopes`, `requirements`, and `requirement_scopes`
into a per-node disposition for a chosen standard. Nothing is stored; the projection is
recomputed on each read of the SoA JSON endpoint
(`GET /api/v1/freeboard/statement-of-applicability/{standardId}`) and the
`/compliance/statement-of-applicability` page.

Current resolution has two layers:

- Standard layer (`ResolveNode`): walk the inclusive ancestry `[node, parent, ..., root]`
  to the first node with a `Scope` for the standard. The node's own `Scope` is
  `explicit`, an ancestor's is `inherited`. If no node on the path has a `Scope`, the
  result is `(null, Undetermined)`. `Undetermined` is treated as not-in-scope and is
  distinct from `Out`.
- Requirement layer (`ResolveRequirements`): consulted only when the standard resolves
  `In`. It applies the same nearest-ancestor walk over `RequirementScope` rows. An
  absent `RequirementScope` follows the standard (`In`).

So the standard axis is opt-in (nothing is in scope until a `Scope In` is authored) and
the requirement axis under an in-scope standard is already opt-out (a requirement is in
until a `RequirementScope Out` excludes it). GitHub issue ologistio/freeboard#90 wants
both axes to be opt-out: in scope by default, explicit opt-out, and manual opt-in only
to override an opted-out ancestor.

The `SoaResolution` enum (`Explicit`, `Inherited`, `Undetermined`) is consumed only in
this capability: the resolver, the SoA JSON endpoint, the page model
(`DispositionLabel`), the Razor view, and the SoA unit tests. Grep confirms no other
`Undetermined` usage in the compliance domain (the identically named authorization
concept is separate and untouched).

## Goals / Non-Goals

**Goals:**

- Flip the standard-level empty-path default from not-in-scope to `In`.
- Keep the resolved standard disposition honest about provenance (own scope vs
  inherited vs defaulted) without a null disposition.
- Preserve, unchanged, the descendant-overrides-opted-out-ancestor capability the issue
  singles out.
- Keep the change small: one resolver branch, its enum/wire names, the two read
  surfaces, the tests, and the docs. No persistence or schema work.

**Non-Goals:**

- Moving the resolver into `Freeboard.Core`.
- A configurable default.
- Auto-generating `Scope Out` rows to preserve any deployment's prior implicit-out set.
- Touching the requirement-layer opt-out behaviour, the config or DB schema,
  authorization narrowing, or the vendor model.

## Decisions

### D1: Standard-level default resolves `In`, marked `default`

`ResolveNode`'s no-scope-on-path branch changes from `(null, Undetermined)` to
`(In, Default)`. The resolved disposition string is `In` (the `ScopeDisposition.In`
name), so the disposition is never null. Because the resolver never returns null now,
the never-null invariant moves into the types: `SoaNode.Disposition` becomes
non-nullable `string` and `ResolveNode`'s return tuple element becomes non-nullable
`string`. Nullable is enabled, so keeping it `string?` would force each reader to
special-case a null that can no longer occur. Explicit and inherited walks are unchanged, so
an authored `Scope Out` still resolves `Out` (opt-out) and a descendant `Scope In` under
an `Out` ancestor still wins (override). The issue's required capabilities fall directly
out of the existing nearest-ancestor walk once the terminal default flips.

Alternative considered: keep the branch returning a null disposition but reinterpret
null as in-scope downstream. Rejected: it pushes the opt-out meaning into every reader
and keeps a null that each consumer must special-case. A concrete `In` at the resolver
boundary is simpler and removes the null.

### D2: Replace `SoaResolution.Undetermined` with `SoaResolution.Default`

Provenance is still worth surfacing: a node in scope because someone authored a `Scope`
reads differently from a node in scope only because nothing said otherwise. Rename the
third enum member `Undetermined` to `Default`, wire value `"default"` (lowercase, like
`explicit`/`inherited`; the old capital-`U` `"Undetermined"` special case goes away).
`Default` means: no `Scope` on the path, so the node takes the system default
disposition `In`.

Alternatives considered:

- Remove the third member entirely and mark defaulted nodes `Inherited`. Rejected:
  `inherited` would claim a value was inherited from an ancestor's scope when there is
  none on the path. That is a false provenance and would confuse the read view.
- Retain `Undetermined` for some narrow case. Rejected: once the standard default is
  `In`, no node resolves `Undetermined`, and the requirement layer's
  standard-`Undetermined` branch becomes dead. There is no surviving case, so the member
  is removed rather than kept as a state with no producer.

### D3: Requirement layer keeps opt-out, drops its dead `Undetermined` branch

The requirement layer already defaults a requirement to `In` under an `In` standard, so
it is already opt-out and needs no behavioural change. Its former branch "if the standard
resolves `Undetermined`, the requirement resolves `Undetermined`" is removed because no
standard resolves `Undetermined` any more. The gate that runs the requirement walk only
when the standard resolves `In` is unchanged; it now fires for `default`-`In` nodes too
(they carry disposition `In`), which is correct: a defaulted-in standard can still carry
requirement-level opt-outs. The "standard `Out` means requirement `Out`, scopes not
consulted" rule stays.

### D4: No persistence or config-schema change

Resolution is computed, not stored. The `scopes`/`requirement_scopes` tables and the
`Scope`/`RequirementScope` YAML kinds are unchanged, so there is no DB migration and no
`apiVersion` bump. This keeps the blast radius inside the web project and its tests plus
the docs prose. Existing persisted rows keep their exact meaning under the new rules
(see Migration Plan); only absent rows are reinterpreted.

### D5: Read surfaces simplify

- SoA JSON endpoint: `disposition` is now always `In` or `Out`; `resolution` emits
  `explicit`/`inherited`/`default`. No shape change beyond the value set. The endpoint's
  documented output in `docs/gitops.md` (the SoA read-API bullet, separate from the Scope
  and RequirementScope resolution prose) updates to the same value set: `disposition` is
  always `In`/`Out`, `resolution` is `explicit`/`inherited`/`default`, and an `Out` node
  always carries an empty `requirements` list (requirement scopes are not applied under an
  out-of-scope standard) while an in-scope node carries its per-requirement deviations,
  which is an empty list when it has none.
- Page model `DispositionLabel`: the `?? "Undetermined"` fallback is dead once
  disposition is non-null; simplify to return the disposition directly.
- Razor view: the third badge branch (`badge-warn` for a non-`In`/`Out` label) becomes
  unreachable for the standard disposition; remove it rather than leave dead markup, per
  code-as-liability.

### D6: Ship the flip and document it; no migration, flag, or auto-authoring

The flip is delivered resolver-only: change the empty-path default, update the read
surfaces and docs, and ship. There is no schema migration, no GitOps `apiVersion` bump,
and no auto-authored `Scope Out` rows to preserve any deployment's prior implicit-out
set. A deployment that wants a standard to stay out authors an explicit `Scope Out` at
the relevant root, in git, like any other scoping decision.

Rationale: this is the lowest-liability delivery. The alternatives - gating the new
default behind a release-long transition or a config flag, or shipping a one-time tool
that lists every currently-`Undetermined` standard per root so operators can pre-author
`Scope Out` rows - each add code and state for a one-time upgrade concern. The flip to
in-scope-by-default is the intended product outcome, so persisting the old implicit-out
set works against the goal. Absence-of-scope carried no author intent to keep a standard
out; it was only the old default. Reinterpreting it as in-scope is safe, and the release
notes plus `docs/gitops.md` tell operators how to reassert an opt-out. The change stays
inside the web project, its tests, and the docs.

## Impact

- Evidence ingest: the ingest endpoint's in-scope check
  (`src/Freeboard/Evidence/EvidenceIngestEndpoints.cs`, `IsOrganisationInScope`) reuses
  `StatementOfApplicability.Resolve` and accepts a run only when the node's standard
  disposition is `In` and the requirement is not an `Out` deviation. Under the new
  default it needs no code change: a node with no `Scope` now resolves `In` (was a null
  disposition, which the check treated as not-in-scope), so an (org, requirement) pair
  that previously resolved `Undetermined` is now accepted. This is intended - the flip is
  meant to widen the in-scope set. An explicit `Scope Out` on the standard still resolves
  `Out`, so ingest still rejects it. Both cases are covered by tests (see tasks 3.4-3.5).
- No-change files under option D6:
  `src/Freeboard.Core/GitOps/ConfigModel.cs` keeps `ApiVersion = freeboard.dev/v1alpha1`;
  the config schema is byte-identical, so no bump. `src/Freeboard.Persistence/GitOps/`
  `MySqlGitOpsImporter.cs` keeps pruning `scopes` rows absent from config; that is
  harmless because absence of a `Scope` is now the in-scope default, so a pruned-absent
  scope leaves the node in scope, which is the intended result. Neither file is edited.

## Provenance

The resolver mechanics here - flip only the empty-path default, keep the nearest-ancestor
walk, and drop the now-dead `Undetermined` state - were reached by two independent
planning passes that arrived at the same design. The remaining question, whether to ship
the flip directly or stage it behind a migration or flag, was settled in favour of
ship-and-document (decision D6) as the lowest-liability path that matches the intended
product default.

## Risks / Trade-offs

- [Silent in-scope expansion for existing deployments] Every standard a deployment left
  unscoped flips from out-of-scope to in-scope on upgrade, pulling that standard's
  requirements into every organisation's applicability. This is the intended product
  change, but it is behaviourally breaking. -> Mitigation: document the flip in the
  proposal, `docs/gitops.md`, and the release notes; state that a deployment which wants
  a standard to stay out MUST author an explicit `Scope Out` at the relevant root (see
  decision D6).
- [Redundant root `Scope In` rows] Root-level `Scope In` rows authored under the old
  opt-in model are now no-ops (the default is already `In`); they resolve `In` marked
  `explicit` instead of `default`. Harmless and correct; operators MAY delete them. No
  automated pruning. -> Mitigation: note in docs; no code.
- [External consumers keyed on the `"Undetermined"` wire value] Any client that branches
  on the SoA JSON `resolution == "Undetermined"` breaks. Consumers are internal (the
  page and tests). -> Mitigation: update both; call the wire-value change out in the
  proposal.
- [Requirement walk now runs for more nodes] Requirement-level resolution runs for every
  in-scope node, which is now most of the tree rather than only explicitly-in nodes.
  Same per-node ancestry walk already built once per node; cost is negligible. -> No
  action.

## Migration Plan

- No schema migration. The resolver change ships in the web app; on deploy, every SoA
  read recomputes under the new default.
- Existing data keeps its meaning: `Scope Out` stays an opt-out; an intermediate
  `Scope In` under an `Out` ancestor stays an override; a root `Scope In` becomes a
  redundant no-op. Only the absence of any `Scope` on a path is reinterpreted (was out,
  now in).
- Deployments that relied on implicit-out for a standard must add an explicit
  `Scope Out` for that standard at the appropriate organisation before or with the
  upgrade to preserve the prior out-of-scope result. This is operator config in git; it
  is not something the change can infer safely (see decision D6).
- Rollback: revert the resolver change. No data has changed, so rollback is clean.
