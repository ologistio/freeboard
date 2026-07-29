## ADDED Requirements

### Requirement: An organisation gate anchors only on organisation ancestry

An authorization check whose resource carries an organisation id SHALL anchor that id on an
inclusive ancestry chain that STOPS at the first entry naming an asset that is not a `Company`
or `Department`. That entry SHALL be kept and the chain SHALL go no further, so no grant held
beyond it can match. An entry naming no asset at all SHALL be kept and SHALL NOT stop the
chain, because the chain already ends there for want of a `parent`; an id that names no asset
therefore anchors only itself, and creating an organisation at the root still requires
`system.admin`.

The chain SHALL stop at a non-organisation entry wherever it appears, not only at the supplied
id. Ancestry is INCLUSIVE and the asset tree is one tree, so a `Machine` hanging under a
department resolves through that department to its company, and an ORGANISATION whose `parent`
names a machine resolves on through that machine into a second organisation subtree. Both are
grant matches the organisation-only ancestry never produced, and both are closed by stopping
at the same point that walk stopped.

This rule holds wherever an organisation-scoped resource is BUILT, whatever the id's provenance
and whatever it names: the organisation upsert on both its create and its update arm, the
organisation delete, the scope and requirement-scope writes and deletes including their
stored-owner lookups, the cross-organisation-move and parent-side reparent checks, the
role-assignment API, and the role-assignment page. Confining an id to an organisation ASSET
before it is used SHALL NOT exempt it, because an organisation's own `parent` may name a
non-organisation asset and carry the chain on through it.

A request refused by this rule SHALL be refused as an audited authorization decision, and
SHALL NOT be allowed to reach the persistence layer and be answered there as a `404`, a
validation `400`, or a no-op success. The write store's own type predicates remain as defence
in depth for a caller that arrives by some other path; they are not the enforcement point. The
refused request SHALL carry the status its route gives today: `403` for a resource the caller
can see, and, where a route resolves visibility for itself before this rule applies, the `404`
that existing visibility rule produces.

#### Scenario: A machine id on an organisation route is refused

- **WHEN** a caller who is not a super-admin, but who holds the route's permission on a
  `Machine`'s parent organisation, supplies that machine's id as the organisation of a
  compliance write or an organisation delete or reparent
- **THEN** the request is denied with `403` and audited as a denial, and the write store is
  never reached

#### Scenario: A role-assignment route keeps existence non-disclosure

- **WHEN** that same caller supplies the machine's id as the `orgId` of a role-assignment
  route whose selector already gates on whether the caller can read that organisation
- **THEN** the read check denies under `Enforce`, so the route answers `404` and discloses
  nothing about the id, and the assignment is neither listed nor mutated

#### Scenario: An organisation parented onto a machine anchors no grant beyond it

- **WHEN** an organisation's `parent` names a `Machine` that itself hangs under another
  organisation subtree, and a caller holds the route's permission only in that further
  subtree
- **THEN** the chain stops at the machine, so no grant in the further subtree matches and the
  request is denied

#### Scenario: An unknown id still resolves as a create

- **WHEN** a caller upserts an organisation at an id that names no asset
- **THEN** the id anchors only itself, so the call requires `system.admin` exactly as it does
  for any other root creation

#### Scenario: A super-admin is unaffected

- **WHEN** a super-admin supplies a non-organisation asset id on an organisation-scoped route
- **THEN** the super-admin permission permits the call, and the route's own type handling
  decides the outcome from there

## MODIFIED Requirements

### Requirement: Compliance reads narrow to the caller's authorized subtree

The org-scoped compliance reads SHALL be narrowed through a single accessibility seam that
resolves the caller's ACCESSIBLE ASSET set over the unified asset tree. The seam SHALL derive
that set in two steps, and its default implementation SHALL resolve the first step by the
rollout mode (see the rollout-mode requirement) instead of narrowing unconditionally.

Step one is the ORGANISATION read-subtree union, unchanged from the organisation-only model:
under `Observe` reads SHALL NOT be narrowed and the union SHALL be all persisted organisations
for every caller regardless of grants; under `Compat` the union SHALL be the union of
organisation subtrees on which the caller holds a read-granting role (all organisations for a
super-admin), and a caller with no grants SHALL reach the audited full read fallback; under
`Enforce` the union SHALL be that same subtree union (all organisations for a super-admin) and
a caller with no read-granting role SHALL have an empty union. Authorization GRANTS remain
rooted on organisations; no grant is assigned on a non-organisation asset.

Step two SHALL close that union over the asset tree, deciding each asset by the edge it
carries rather than by its type, because `parent` and `owner` are mutually exclusive:

- an asset that carries a `parent` SHALL be accessible when its cycle-guarded inclusive
  `parent` chain intersects the organisation union;
- an asset that carries an `owner` (a `Vendor`) SHALL be accessible when its `owner` is in the
  organisation union;
- an asset that carries neither SHALL be accessible only when its own id is in the
  organisation union.

A discovered asset in the `Retired` state SHALL NOT be in the accessible set: a retired asset
is not a live authorization anchor.

A missing or dangling edge SHALL resolve to no hit rather than to a wildcard, so read-access
is fail-closed. The exclusion is decided by the whole chain, not by the edge alone: the
`parent` chain is INCLUSIVE and its first entry is the asset itself, so an ORGANISATION whose
own id is in the union SHALL remain accessible even when its `parent` names an id no asset
defines or sits in a `parent` cycle. What a dangling or missing edge excludes is an asset that
has no other way in - a `Machine` whose chain reaches no organisation in the union, and a
`Vendor`, which has a single `owner` edge and no inclusive chain to fall back on, so an
ownerless or dangling-owner vendor SHALL NOT be accessible to any caller. Global vendor
readability - every authenticated user seeing every vendor regardless of grants - SHALL NOT
apply.

The rollout mode SHALL govern STEP ONE only. The `Observe` relaxation and the `Compat`
zero-grant fallback widen the ORGANISATION union; they SHALL NOT disable, weaken, or bypass any
step-two edge rule. Under every mode, an asset whose inclusive chain reaches no organisation in
the union, and a discovered asset in the `Retired` state, SHALL be outside the accessible set
even when the union is every organisation. A mode changes which organisations anchor a read,
never whether an edge has to resolve.

The narrowed reads SHALL be `GET /organisations` (filtered by id against the accessible asset
set), `GET /vendors` (filtered by id against the same set, which admits a vendor exactly when
its `owner` is in the organisation union), the unified `GET /scopes` (filtered by whether the
scope's `subject` id is in the accessible asset set, which is one membership test covering the
organisation, vendor-`owner`, and machine-`parent`-ancestry cases that were previously three
per-surface rules), and the Statement of Applicability JSON endpoint and page (nodes resolved
over the full tree then filtered to the accessible asset set so inherited dispositions
survive). The non-tenant catalog reads `GET /standards`, `GET /controls`, and
`GET /requirements` SHALL remain authenticated-only and unnarrowed.

`GET /collectors` and `GET /integration-connections` SHALL likewise keep their full ROW
sets for every authenticated caller - they carry no organisation dimension - but SHALL
NOT emit a `vendor` id outside the caller's accessible asset set: the `vendor` field on
each row SHALL be emitted only when that vendor id is in the set, and as `null`
otherwise. The same field rule SHALL apply to the collector register page, the
integration-connections page, and the Statement of Applicability drill-down, which SHALL show
a check's vendor title only when that vendor is in the accessible asset set and SHALL NOT fall
back to the raw vendor id. This is what makes global vendor readability actually
dropped: narrowing `/vendors` alone leaves a hidden vendor's id readable from a collector
or connection row. The rule bounds the vendor ASSET id only; the other fields of those
rows describe the collector or the connection rather than the vendor asset, and are not
narrowed by it.

#### Scenario: Org-scoped list is narrowed

- **WHEN** a caller whose accessible set is a strict subset of the asset tree reads
  `/organisations`, `/vendors`, or the unified `/scopes`
- **THEN** the response contains only rows whose id, or whose subject id, is in the
  accessible asset set

#### Scenario: A machine is accessible through its parent chain

- **WHEN** a caller holds a read grant on a company and a `Machine`'s `parent` chain reaches
  that company through a department
- **THEN** the machine is in the caller's accessible asset set, and a machine whose chain
  reaches no organisation in the union is not

#### Scenario: A vendor is accessible only through its owner

- **WHEN** a caller's organisation union contains a vendor's `owner`
- **THEN** the vendor is in the caller's accessible asset set; and a vendor whose `owner` is
  missing, dangling, or outside the union is not, for any caller including one with grants
  elsewhere in the tree

#### Scenario: A dangling parent does not hide an organisation from its own grant

- **WHEN** a caller's organisation union contains a `Department` whose `parent` names an id
  no asset defines, and another whose `parent` chain sits in a cycle
- **THEN** both are in the caller's accessible asset set, because the inclusive chain starts
  at the asset itself, while a `Machine` whose chain reaches no organisation in the union is
  not

#### Scenario: A retired discovered asset is never accessible

- **WHEN** a discovered `Machine` in the `Retired` state has a `parent` chain reaching the
  caller's organisation union
- **THEN** it is not in the accessible asset set, so neither it nor a scope naming it as
  subject is returned

#### Scenario: Observe widens the union but keeps the edge rules fail-closed

- **WHEN** the rollout mode is `Observe` and a caller with no grants reads an org-scoped
  compliance endpoint while a vendor has no `owner` and a discovered `Machine` is `Retired`
- **THEN** the caller's organisation union is every persisted organisation, so every asset
  whose `parent` chain or `owner` reaches an organisation in that union is accessible, but the
  ownerless vendor, the retired discovered machine, and any asset whose chain reaches no
  organisation in the union are still outside the accessible set

#### Scenario: Super-admin sees the whole readable domain

- **WHEN** a super-admin reads any org-scoped compliance endpoint
- **THEN** the response contains the whole READABLE persisted set - every asset anchored in
  the super-admin's whole-tree organisation union, including live `Machine` and owner-anchored
  `Vendor` assets, and every scope whose subject is one of them - while a scope whose subject
  is unresolved (no asset row, or a retired discovered asset) or unsupported (a deferred group
  subject) is still OMITTED even for the super-admin, consistent with the
  `compliance-web-read` fail-closed rule; such a scope surfaces only through the generic
  dangling-subject warning, never as a `/scopes` row

#### Scenario: Catalog reads stay global

- **WHEN** an authenticated caller reads `/standards`, `/controls`, or
  `/requirements`
- **THEN** the full catalog is returned regardless of organisation grants

#### Scenario: A hidden vendor's id does not leak through the global reference reads

- **WHEN** an authenticated caller with no grant reaching a vendor's `owner` reads
  `/collectors` or `/integration-connections` and a row names that vendor
- **THEN** every row is still returned and the row's `vendor` reads `null`, so the caller
  cannot learn the id of a vendor outside its owner's subtree from any read surface
