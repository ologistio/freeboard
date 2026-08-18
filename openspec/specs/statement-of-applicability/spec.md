# statement-of-applicability Specification

## Purpose
TBD - created by archiving change redefine-scope-org-standard. Update Purpose after archive.
## Requirements
### Requirement: Scope disposition resolves by nearest-ancestor inheritance

The system SHALL resolve an ASSET node's disposition for a standard over the unified asset
tree, walking `parent` edges only, as follows: if a standard-level scope exists for that node
(a `Scope` whose `subject` is that asset and whose target is that standard), its disposition is
the resolved value and is `asset`; otherwise, if an ancestor up the `parent` chain has such a
scope for that standard, the resolved value is the nearest such ancestor's disposition and is
`inherited`; if no asset on the `parent` chain has a scope for that standard, the resolved value
is `In` and is `default`. Standards are in scope by default: a node with no scope on the path
from it to the root resolves `In`. A user opts a standard `Out` by authoring a `Scope` targeting
that standard with disposition `Out`; a descendant MAY override an opted-out ancestor by
authoring its own standard-level `Scope` `In`. The resolved standard disposition SHALL always be
`In` or `Out` and SHALL NOT be null; `default` is a provenance marker (no scope on the path, so
the node takes the default `In`) and is distinct from `asset` and `inherited`.

The resolution-source values SHALL be exactly `asset`, `inherited`, and `default`. The value
`asset` REPLACES the former `explicit`: the node's own scope is now a scope on the asset itself,
so the provenance names the level it came from and leaves room for the further levels the group
axis adds later. No other provenance value SHALL be emitted.

The node set SHALL be every `Company` and `Department` asset, PLUS every other asset whose
cycle-guarded inclusive `parent` chain reaches one of them. `Company`, `Department`, and
`Machine` assets are therefore nodes, declared and discovered alike, and a `Machine`'s `parent`
MAY cross the declared/discovered boundary.

An organisation asset SHALL be a node unconditionally: a `Company` or `Department` whose `parent`
names an id no asset defines, or whose `parent` chain runs into a cycle, SHALL still resolve and
SHALL still appear in the projection, taking the default `In` when no scope reaches it. A
dangling `parent` edge and a `parent` cycle are non-blocking GitOps validation warnings, not
errors, so both states occur in a validly synced deployment and neither SHALL remove an
organisation from the Statement of Applicability.

A NON-organisation asset SHALL be a node only when its `parent` chain reaches an organisation. An
unrooted or dangling-parent `Machine` SHALL NOT be a node, so it is absent from the projection
rather than resolving to the default `In`. A `Vendor` asset SHALL NOT be a node and SHALL NOT
participate in inheritance: it is not organisation-typed, and it carries `owner`, never `parent`
(the two are mutually exclusive), so it satisfies neither arm of the rule.

#### Scenario: A scope on the asset itself wins

- **WHEN** a node has a scope of disposition `Out` targeting a standard
- **THEN** the node resolves to `Out`, marked `asset`, regardless of its ancestors

#### Scenario: Child inherits nearest ancestor

- **WHEN** a company has disposition `Out` for a standard and its department has no
  scope for that standard
- **THEN** the department resolves to `Out`, marked `inherited`

#### Scenario: No ancestor disposition defaults to In

- **WHEN** neither a node nor any ancestor has a scope for a standard
- **THEN** the node resolves to `In`, marked `default`, and the resolved disposition
  is not null

#### Scenario: Descendant overrides an opted-out ancestor

- **WHEN** a company has disposition `Out` for a standard and a department under it has
  its own scope `In` for that standard
- **THEN** the department resolves to `In`, marked `asset`, overriding the ancestor's
  `Out`, while a sibling department with no scope resolves `Out`, marked `inherited`

#### Scenario: A machine inherits its department's disposition

- **WHEN** a department has disposition `Out` for a standard and a discovered `Machine`
  whose `parent` is that department has no scope for that standard
- **THEN** the machine resolves to `Out`, marked `inherited`, and a declared `Machine`
  under the same department resolves the same way

#### Scenario: A machine's own scope overrides its department

- **WHEN** a department has disposition `Out` for a standard and a `Machine` under it
  has its own scope `In` for that standard
- **THEN** the machine resolves to `In`, marked `asset`

#### Scenario: A vendor is not a node and inherits nothing

- **WHEN** the projection is resolved for a standard while a `Vendor` asset exists whose
  `owner` is an in-scope organisation with a standard-level `Out`
- **THEN** the vendor does not appear as a node and takes no disposition from that
  organisation, because it carries no `parent` edge to inherit along

#### Scenario: An unrooted machine is absent rather than defaulted In

- **WHEN** a `Machine` has no `parent`, or its `parent` names an id no asset defines
- **THEN** the machine is not a node in the projection and does not resolve to the
  default `In`

#### Scenario: An organisation with a dangling parent is still a node

- **WHEN** a `Department` whose `parent` names an id no asset defines is projected for a
  standard no scope on it or reachable from it targets
- **THEN** the department IS a node and resolves `In`, marked `default`, rather than being
  omitted, because an organisation never depends on reaching a root

#### Scenario: An organisation in a parent cycle is still a node

- **WHEN** two `Department` assets name each other as `parent` and the projection is resolved
- **THEN** the resolution terminates, both departments appear as nodes with a resolved
  disposition, and the node set is finite

### Requirement: Statement of Applicability requires an authenticated user

The Statement of Applicability SHALL require an authenticated user, both on the
`GET /api/v1/freeboard/statement-of-applicability/{standardId}` endpoint and on the
`/compliance/statement-of-applicability` read-only view page. Authentication (any
logged-in user) is sufficient; neither SHALL require the admin role. An anonymous
request to the endpoint SHALL return HTTP 401; an anonymous browser GET to the page
SHALL be redirected to `/login` rather than rendering the view. Authentication is
orthogonal to the GitOps read-only gate: both the endpoint and the page SHALL still
be served to an authenticated user when the instance is in read-only mode, and both
remain GET-only.

#### Scenario: Anonymous request to the endpoint is rejected

- **WHEN** an anonymous client requests
  `GET /api/v1/freeboard/statement-of-applicability/{standardId}`
- **THEN** the endpoint returns HTTP 401

#### Scenario: Anonymous request to the page redirects to login

- **WHEN** an anonymous browser requests `/compliance/statement-of-applicability`
- **THEN** the response redirects to `/login` rather than rendering the view

#### Scenario: Authenticated user is served in read-only mode

- **WHEN** GitOps read-only mode is on and an authenticated user requests the
  Statement of Applicability endpoint or view page
- **THEN** the request is served normally and is not rejected with the 409
  read-only response

### Requirement: Statement of Applicability is a read-only projection

The web app SHALL serve a Statement of Applicability for a standard as a read-only
projection over the unified asset tree, computed from the persisted assets, scopes, and
requirements and stored nowhere. Its scope inputs SHALL come from the unified `scopes`
table: a standard-level disposition is a scope targeting the standard, and a
requirement-level disposition is a scope targeting one of the standard's requirements. For
the given standard the projection SHALL include every node of the asset forest (see the
nearest-ancestor requirement) with its resolved standard disposition (always `In` or `Out`,
never null) and whether that value is `asset`, `inherited`, or `default`. Each node whose
standard resolves `In` SHALL additionally report its per-requirement exclusions: the
requirements of that standard whose `(subject, requirement)` nearest-ancestor resolution finds
a requirement-targeting scope, each with its resolved disposition (`In` or `Out`) and whether
that value is `asset` or `inherited`. A requirement of the standard that is not listed for a
node follows that node's standard disposition (`In`). A node whose standard resolves `Out`
SHALL report no per-requirement exclusions, because requirement scopes are not applied under
an out-of-scope standard. The endpoint SHALL be GET-only and SHALL NOT be blocked by GitOps
read-only mode. Node output SHALL be deterministically ordered by `id`, and each node's
per-requirement list SHALL be ordered by requirement `id`. A scope whose `subject` does not
resolve to any asset SHALL NOT fail the projection and SHALL surface a non-blocking warning on
the view page (see the dangling-subject requirement).

The projection SHALL always be computed over the full asset tree so that nearest-ancestor
inheritance is correct, then filtered to the caller's ACCESSIBLE ASSET set so a node whose
disposition is inherited from an ancestor above the accessible subtree keeps that inherited
value. The accessible asset set is defined by the authorization enforcement capability: the
caller's grant-rooted organisation read-subtree union, closed over the asset `parent` chain and
the vendor `owner` edge. The `/compliance/statement-of-applicability` view page SHALL render
only ORGANISATION nodes (`Company` and `Department`), in scope for the active organisation
selection and bounded by the accessible set: when an organisation is selected, the selected
node and its organisation descendants intersected with the accessible set; when the selection
is "All Organisations", every accessible organisation node. `Machine` nodes SHALL be resolved
but SHALL NOT be rendered as rows on the page; they are exposed on the JSON endpoint only. The
scoping SHALL be applied server-side so out-of-scope nodes are absent from the rendered page.
The page SHALL name the active organisation scope above the projection: the selected
organisation's title when one is selected, or "All Organisations" when none is. The page SHALL
derive its resolved selection from its own reads - its own asset list, its accessible set, and
the selection cookie it reads itself - and SHALL NOT take the resolved selection from the
shared request-scoped selection resolver, so a transient failure that degrades only the
resolver's own read cannot drop the page's scope to "All Organisations". The JSON endpoint
`GET /api/v1/freeboard/statement-of-applicability/{standardId}` SHALL likewise resolve over the
full tree and then return only the nodes in the caller's accessible asset set - INCLUDING
readable `Machine` nodes - so the endpoint and the page apply the same authorization boundary
while differing in which node types they present.

Authentication precedes this read and shares the same backing store as the compliance store. So
the HTTP 503 unreachable-store response describes the case where the request is authenticated
and only the compliance store is unavailable to it. A full database outage that also fails
authentication surfaces first as an authentication failure (HTTP 401 for the endpoint, a
`/login` redirect for the page) - the request never reaches the projection - not as this 503
response.

#### Scenario: Projection reflects the tree and dispositions

- **WHEN** an authenticated user requests the Statement of Applicability for a
  standard with a company marked `Out` and a department left unstated
- **THEN** the response lists the company as `Out` `asset` and the department as
  `Out` `inherited`, ordered by `id`

#### Scenario: Unscoped node defaults to In

- **WHEN** an authenticated user requests the Statement of Applicability for a standard
  for which no asset has a scope
- **THEN** every node resolves `In`, marked `default`, with a non-null disposition

#### Scenario: Projection reports per-requirement exclusions on in-scope nodes

- **WHEN** an authenticated user requests the Statement of Applicability for a
  standard where a company resolves `In` and marks one requirement `Out` company-wide
- **THEN** the company node lists that requirement with disposition `Out` marked
  `asset`, and requirements it does not exclude are absent from the list (they
  follow the node's `In` standard disposition), the list ordered by requirement `id`

#### Scenario: Out-of-scope node reports no per-requirement exclusions

- **WHEN** a node resolves `Out` for the standard
- **THEN** the node reports no per-requirement exclusions, because requirement scopes
  are not applied under an out-of-scope standard

#### Scenario: JSON endpoint returns only accessible nodes with inheritance preserved

- **WHEN** a caller whose accessible asset set is a strict subset of the tree
  requests the Statement of Applicability JSON endpoint for a standard
- **THEN** the response contains only the nodes in the accessible asset set, each keeping
  a disposition inherited from an ancestor above the accessible subtree

#### Scenario: JSON endpoint returns readable machine nodes

- **WHEN** a caller whose accessible asset set reaches a department requests the JSON
  endpoint for a standard, and a `Machine` hangs under that department
- **THEN** the response includes that machine as a node with its resolved disposition and
  resolution source, and omits a machine under a department outside the accessible set

#### Scenario: The page renders organisation nodes only

- **WHEN** an authenticated user views `/compliance/statement-of-applicability` for a
  standard while readable `Machine` nodes exist under the selected organisation
- **THEN** the page renders the organisation nodes and renders no machine row, while the
  JSON endpoint for the same standard and caller does include those machine nodes

#### Scenario: Projection is served in read-only mode

- **WHEN** GitOps read-only mode is on and an authenticated user requests the
  Statement of Applicability
- **THEN** the request is served normally and is not rejected with the 409

### Requirement: Requirement disposition resolves by nearest-ancestor inheritance under the standard

The system SHALL resolve an ASSET node's disposition for a specific `Requirement` (owned by a
standard) in two layers, walking `parent` edges only. First it SHALL resolve the node's
disposition for the requirement's standard by the standard-level nearest-ancestor rule, which
defaults `In`. Then:

- If the standard resolves `Out` at the node, the requirement resolves `Out`, and
  requirement-targeting scopes SHALL NOT be consulted: the whole standard, and thus every
  requirement, is out of scope.
- If the standard resolves `In` at the node - whether `asset`, `inherited`, or
  `default` - the system SHALL consult the requirement layer by nearest-ancestor
  inheritance keyed by `(subject, requirement)`: if a scope targeting that requirement
  exists for that node, its disposition is the resolved value and is `asset`;
  otherwise the resolved value is the nearest ancestor's requirement-targeting scope
  disposition for that requirement and is `inherited`; if no ancestor has a scope for
  that requirement, the requirement follows the standard and resolves `In`.

A requirement-level `In` SHALL NOT re-include a requirement whose standard resolves
`Out` at the node; the standard-level result dominates. Within an `In` standard, a child
node's own requirement-targeting scope SHALL override an ancestor's inherited one, so
a department MAY re-include (`In`) a requirement its parent excluded (`Out`) company-wide, and a
`Machine` MAY do the same against its department.

#### Scenario: Company-wide exclusion is inherited by departments

- **WHEN** a company resolves `In` for a standard and marks a requirement `Out`
  company-wide, and a department under it has no scope targeting that requirement
- **THEN** the department resolves that requirement `Out`, marked `inherited`, while
  requirements it does not exclude follow the standard and resolve `In`

#### Scenario: Department re-includes a company-excluded requirement

- **WHEN** a company marks a requirement `Out` company-wide and a department under it
  marks the same requirement `In`
- **THEN** the department resolves that requirement `In`, marked `asset`,
  overriding the inherited company `Out`

#### Scenario: Machine re-includes a department-excluded requirement

- **WHEN** a department marks a requirement `Out` and a `Machine` whose `parent` is that
  department marks the same requirement `In`
- **THEN** the machine resolves that requirement `In`, marked `asset`

#### Scenario: Requirement scopes ignored when the standard is out

- **WHEN** a node resolves `Out` for a standard and a scope marks one of that standard's
  requirements `In` at or above the node
- **THEN** the requirement resolves `Out` (following the standard) and the `In`
  requirement scope is not applied

#### Scenario: Requirement scope of another standard is excluded from the projection

- **WHEN** the Statement of Applicability is resolved for one standard while a scope
  targets a requirement of a different standard
- **THEN** that scope does not appear in the requested standard's projection, because
  requirement-targeting scopes are filtered to the requested standard by their
  requirement's owning standard (`Requirement.standard`)

### Requirement: Statement of Applicability view supports hierarchical drill-down

The `/compliance/statement-of-applicability` view page SHALL present the projection
as a hierarchical, progressively disclosed tree with four levels in the order
Organisation -> Requirement -> Control -> Check: an organisation node, its in-scope
requirements, the controls mapped to each requirement, and the checks configured on
each control. Each level SHALL be collapsible and expandable independently of the
others, and SHALL default to collapsed on first load; the page SHALL NOT
auto-expand the selected organisation.

The disclosure SHALL be rendered server-side: the HTML returned by the initial GET
SHALL contain all four levels for the rendered nodes, and client script SHALL only
toggle the visibility of nested rows, never fetch additional data. Toggling a level
SHALL NOT issue a new request to the server. Selecting a different standard SHALL
re-render the page with all levels collapsed, so no stale expansion state carries
across a standard change.

The disclosure SHALL be keyboard operable and accessible: each expand/collapse
control SHALL be a native button element carrying an `aria-expanded` state that
reflects whether its section is open, and a text label identifying the row it
controls. Nested content SHALL remain in the DOM while collapsed (hidden, not
removed) for server-side rendering, tests, and the no-JavaScript reveal; because it
is hidden with `display:none`, it is exposed to assistive technology only when its
section is expanded. With JavaScript disabled the nested levels SHALL be reachable
rather than permanently hidden.

The JSON endpoint `GET /api/v1/freeboard/statement-of-applicability/{standardId}`
SHALL be unchanged by this drill-down; the hierarchy is a page-only presentation.
All existing scoping, authorization-boundary, read-only, and store-unreachable
behaviours of the page SHALL continue to hold.

#### Scenario: Initial load shows organisation rows collapsed

- **WHEN** an authenticated user first views the Statement of Applicability for a
  chosen standard
- **THEN** the page shows the organisation nodes with every disclosure collapsed,
  including the selected organisation

#### Scenario: Organisation row expands to its requirements

- **WHEN** the user expands an in-scope organisation node
- **THEN** the page reveals that organisation's requirements, each tagged with its
  resolved disposition (`In` or `Out`) and provenance, without issuing a new server
  request

#### Scenario: Requirement row expands to its controls

- **WHEN** the user expands an in-scope requirement that has controls mapped to it
- **THEN** the page reveals the controls mapped to that requirement

#### Scenario: Control row expands to its checks

- **WHEN** the user expands a control that has checks configured on it
- **THEN** the page reveals the checks configured on that control, each tagged as a
  collector or an attestation

#### Scenario: Disclosure is server-rendered and present without JavaScript

- **WHEN** the initial page GET response is inspected before any client script runs
- **THEN** the HTML already contains the requirement, control, and check rows for
  the rendered nodes, and they are hidden rather than absent

#### Scenario: Toggle exposes accessible expanded state

- **WHEN** the user activates an expand/collapse control with the keyboard
- **THEN** the control is a focusable button whose `aria-expanded` value reflects
  the open or closed state of the section it controls

#### Scenario: Standard change clears stale expansion

- **WHEN** the user has expanded some nodes and then selects a different standard
- **THEN** the page re-renders with all disclosures collapsed

#### Scenario: Out-of-scope organisation node has no requirement children

- **WHEN** an organisation node resolves `Out` for the standard
- **THEN** the node exposes no in-scope requirement children to drill into, because
  no requirement of an out-of-scope standard is in scope

### Requirement: Statement of Applicability projection carries the requirement-control-check structure per in-scope node

For a chosen standard, a projection that backs the view page SHALL attach to each
ORGANISATION node whose standard resolves `In` the full list of that standard's
requirements (not only the node's requirement-level deviations), each with its
resolved disposition (`In` or `Out`) and provenance (`asset`, `inherited`, or
`default`), including requirements excluded (`Out`) at the node. The drill-down node set
SHALL be the `Company` and `Department` nodes of the resolved projection; `Machine` nodes
SHALL be excluded from it, because the drill-down's requirement/control/check hierarchy, its
organisation-selection scoping, and its per-collector evidence status are all keyed on an
organisation node. Each requirement that resolves `In` SHALL carry the controls whose mapping
(`maps_to`) includes that requirement; each control SHALL carry the checks configured on it and
its optional evaluation roll-up as metadata; and each check SHALL be a collector attached to
that control, tagged by kind. The check's kind SHALL be DERIVED from the collector's `type`
rather than read from two separate input sets: a collector whose `type` is `manual` or
`training` SHALL be tagged as an attestation, and a collector of any other type SHALL be tagged
as a collector. The wire values of the tag are unchanged. An excluded (`Out`) requirement SHALL
be a leaf: it carries no controls. A node whose standard resolves `Out` SHALL carry no
requirements at all. Requirements SHALL be ordered by requirement `id`, controls by control
`id`, and checks by kind then `id`.

A check SHALL expose configuration and metadata only. Attestation quiz answers SHALL
NOT be surfaced. Vendors SHALL NOT affect applicability; a collector's optional
vendor is metadata only, and this projection SHALL NOT read live evidence
(`evidence_checks`) or vendor-subject scopes. A collector's vendor SHALL be shown by
title, resolved from the `Vendor`-typed assets in the same asset input the projection
resolves over, and SHALL be narrowed by the SAME accessible-asset-set test the other read
surfaces apply: the title SHALL be shown when that vendor id is in the caller's accessible
asset set, and NOTHING SHALL be shown otherwise. The projection SHALL NOT fall back to the
raw vendor id, on this or any other path. A check whose vendor is unreadable SHALL render
exactly as a check with no vendor, so the drill-down cannot be used to tell the two apart
and a hidden vendor's id cannot be read from it.

A check tagged as an attestation SHALL carry NO collection cadence, even though every
collector now has a required `frequency`. The cadence SHALL be derived in the same step as
the tag - a `manual` or `training` collector projects a null cadence and a collector of any
other type projects its own `frequency` - so the two cannot drift apart. This preserves the
rendering of a check that was ALREADY tagged as an attestation, which had no cadence to
show. It does NOT preserve the rendering of a check whose tag this derivation moves: a
collector previously typed `manual-attestation` or `training-attestation` was tagged as a
collector and showed both a cadence and an evidence status, and as a `manual` or `training`
collector it shows neither. It keeps the
projection coherent with the rule that an attestation-tagged check carries no evidence
status: the page interprets a cadence against a check's status (`Stale` means older than
the cadence window plus grace), so a cadence beside a check with no status would assert a
collection promise the page cannot back. The cadence derivation SHALL NOT touch a check's
`vendor`, which plays no part in that interpretation: a check of either tag SHALL carry
whatever vendor the readability rule above admits - the title when the vendor is in the
caller's accessible asset set, nothing when it is not.

This projection SHALL be added alongside the existing flat resolver, which SHALL be
left unchanged. The controls and the unified collector set that
populate the structure SHALL be read in the same repeatable-read snapshot as the
ASSETS, the unified scopes, and requirements (one unified `scopes` read, one unified collector
read, and one unified ASSET read that serves the tree, the vendor titles, and the
live-subject predicate alike, not separate organisation, vendor, and resolvable-asset reads),
so the rendered tree cannot straddle a concurrent importer commit.

#### Scenario: In-scope node lists every requirement tagged In or Out

- **WHEN** the projection is computed for a standard at a node that resolves the
  standard `In` and excludes one requirement `Out`
- **THEN** the node lists every requirement of the standard with its resolved
  disposition (`In` or `Out`) and provenance, ordered by requirement `id`, including
  the excluded one tagged `Out`

#### Scenario: An unreadable vendor shows no title and no id

- **WHEN** the drill-down is computed for a caller with no grant reaching a vendor's `owner`
  and a check's collector names that vendor
- **THEN** the check carries no vendor at all - neither the vendor's title nor its id - and
  renders exactly as a check whose collector has no vendor

#### Scenario: The drill-down carries organisation nodes only

- **WHEN** the drill-down projection is computed for a standard while `Machine` nodes
  resolve under an in-scope department
- **THEN** the drill-down carries the `Company` and `Department` nodes and no `Machine`
  node, so the page renders no machine row

#### Scenario: Excluded requirement is a leaf with no controls

- **WHEN** a requirement resolves `Out` at an in-scope node and a control maps to it
- **THEN** the requirement appears tagged `Out` and carries no controls, so it renders
  as a leaf with no control children

#### Scenario: Requirement carries its mapped controls

- **WHEN** a control's `maps_to` includes a requirement of the standard that resolves
  `In`
- **THEN** that control appears under that requirement in the projection, carrying
  its evaluation roll-up as metadata

#### Scenario: Control carries its configured checks of both kinds

- **WHEN** a collector of `type: integration` and a collector of `type: training` each name
  a control as their attach-point and that control is mapped to an in-scope requirement
- **THEN** both appear as checks under that control in the projection, the integration
  collector tagged as a collector and the training collector tagged as an attestation,
  with the tag derived from each collector's `type`

#### Scenario: An attestation-tagged check carries no cadence

- **WHEN** a `manual` or `training` collector with a `frequency` and an `integration`
  collector with a `frequency` are each projected as a check
- **THEN** the integration check carries its cadence and the attestation-tagged check
  carries none - matching how a check from an `AttestationTemplate` rendered pre-merge,
  and CHANGING how a check from a `manual-attestation` or `training-attestation`
  `EvidenceCollector` rendered, since that check was tagged as a collector and did show a
  cadence

#### Scenario: Attestation check hides quiz answers

- **WHEN** a `training` collector with quiz items in its `config` is projected
- **THEN** the check carries the collector's configuration and metadata but no quiz
  answer

#### Scenario: Requirement without controls and control without checks render empty child levels

- **WHEN** an in-scope requirement has no control mapped to it, or a control has no
  check configured on it
- **THEN** the projection carries that requirement or control with an empty child
  level rather than omitting it

#### Scenario: Structure inputs share the projection snapshot

- **WHEN** the page reads the inputs needed to build the drill-down
- **THEN** the controls and the unified collector set are read together with the one
  unified asset set, the unified scopes, and requirements in one repeatable-read snapshot,
  and the vendor titles and the live-subject predicate are derived from that same asset set
  rather than from separate reads

### Requirement: Statement of Applicability surfaces per-collector evidence status

The `/compliance/statement-of-applicability` view page SHALL show, for each collector
check under a control, that collector's derived evidence status for the organisation node
it appears under. The status SHALL be read from the evidence read store
(`IEvidenceStore`) as a per-collector status keyed by `(organisation, requirement,
collector)`, and the page SHALL batch-read the statuses for all visible organisation
nodes in one call rather than issuing a read per node. The evidence read MAY use a
separate snapshot from the drill-down projection read: the status is advisory display and
is not part of the config-tree consistency guarantee.

The page SHALL render `Errored`, `Stale`, and `Unknown` each distinctly from the other two:
`Errored` (the collector's latest collection attempt failed, so the requirement was not
observed) SHALL be shown as a "collection failed" state, `Stale` (the collector's latest
evidence is older than its cadence window plus grace) SHALL be shown as a "collection
stopped" state, and `Unknown` SHALL be shown as a separate "not collected" state. A
collector check that has no status from the store (an expected collector that never
produced evidence for that organisation and requirement) SHALL be derived as `Unknown` by
the page. `HardFailure`, `SoftFailure`, and `Passing` SHALL each render distinctly from
`Errored`, `Stale`, and `Unknown`. `Errored` SHALL NOT render as a passing state and SHALL
NOT render red: red is reserved for an observed policy failure, and an error means the
answer is not known. Only checks tagged as a collector SHALL carry a status; checks
tagged as an attestation (a collector of `type: manual` or `type: training`) carry no
evidence status.

The words the row badge writes for these states are evidence-status labels, not product
status terms. The UX status rule permits that closed label set on this one surface and
nowhere else, so the row SHALL draw its text from that set, and the control anatomy SHALL
keep speaking the product vocabulary.

The `Errored` rule SHALL hold for the row badge AND for the control anatomy the page renders
in its drawer and at the control's direct link, so the two surfaces cannot report an errored
collector differently. The two surfaces map a persistence status through separate code, and
they already render `Stale` differently from each other. This requirement does not reconcile
that older difference. It fixes only that `Errored` reaches both surfaces, because a status
added to one surface alone falls through the other's default and reads as "not collected".

The status surfacing SHALL NOT change the drill-down projection or its inputs: the
existing resolution, scoping, authorization-boundary, read-only, store-unreachable, and
JSON-endpoint behaviours of the page SHALL continue to hold, and the JSON endpoint SHALL
remain free of live evidence status.

#### Scenario: An errored collector renders as collection failed

- **WHEN** a collector check's latest collection for an in-scope organisation and
  requirement failed and was recorded as an errored run
- **THEN** the page shows that collector check with a "collection failed" status, distinct
  from "collection stopped", from "not collected", and from a passing status

#### Scenario: An errored collector renders the same on the page and in the drawer

- **WHEN** a collector check is `Errored` and the viewer opens the control in the drawer or
  at its direct link
- **THEN** the control anatomy shows the same errored state that the page row shows, and it
  is not shown as passing

#### Scenario: A stale collector renders as collection stopped

- **WHEN** a collector check's latest evidence for an in-scope organisation and
  requirement is older than its cadence window plus grace
- **THEN** the page shows that collector check with a "collection stopped" status,
  distinct from a "not collected" status

#### Scenario: A never-collected collector renders as unknown

- **WHEN** a collector check is configured on a control for an in-scope requirement but
  has produced no evidence for the organisation
- **THEN** the page shows that collector check with an `Unknown` "not collected" status,
  distinct from "collection stopped" and from "collection failed"

#### Scenario: A fresh passing collector renders as passing

- **WHEN** a collector check's latest evidence for an in-scope organisation and
  requirement is within its cadence window plus grace and has no failing check
- **THEN** the page shows that collector check with a `Passing` status

#### Scenario: An attestation-tagged check carries no evidence status

- **WHEN** a collector of `type: manual` or `type: training` is projected as a check
- **THEN** the page renders no evidence status badge for it - matching how a check from an
  `AttestationTemplate` behaved pre-merge, and CHANGING how a check from a
  `manual-attestation` or `training-attestation` `EvidenceCollector` behaved, since that
  check was tagged as a collector and did carry a status

#### Scenario: Visible organisation statuses are batch-read

- **WHEN** the page renders collector checks across several in-scope organisation nodes
- **THEN** the per-collector statuses for those organisations are read from the evidence
  store in a single batch call rather than one read per organisation

### Requirement: Dangling scope subject surfaces a non-blocking warning at resolution

The Statement of Applicability resolution SHALL treat a scope whose `subject` does not
resolve to a live asset - no asset row has that id, or the row is a discovered asset in the
`Retired` state - as a NON-BLOCKING condition: it SHALL surface a warning ("rule
targets a resource that does not currently exist", covering a retired asset and a
not-yet-discovered one) on the `/compliance/statement-of-applicability` view page and
SHALL NOT fail the projection. The live-subject predicate SHALL be evaluated against the ONE
unified asset set the projection already reads, not a separate id read, so the warning and the
resolution cannot disagree about which subjects exist. The unresolved-subject warning scan SHALL
cover EVERY unified scope regardless of its target kind - standard-target, requirement-target,
AND control-target - so no target kind is exempt from the warning. Only the disposition
RESOLUTION stays limited to standard-level and requirement-level scopes on the asset forest: a
control-target scope contributes no node disposition (control-level resolution is a non-goal),
but a control-target scope whose subject is unresolved STILL surfaces the same generic warning
on the SoA page. The page notice SHALL be generic: it SHALL NOT disclose the
scope id or the unresolved subject id to an ordinary caller, because the unresolved subject
has no authorization anchor to check readability against; any detailed-id surface would be
system-admin-only and is out of scope for this change. A scope with a dangling subject does
not contribute a node disposition (no node matches its subject) and is otherwise ignored
by the resolution. The JSON endpoint SHALL keep its shape; the warning is a page-level
notice. This warning concerns only a subject that resolves to no live asset at all; a scope
whose subject DOES resolve - a `Vendor` asset, or any other asset outside the `parent`-rooted
forest - is simply not consulted by the standard-level and requirement-level resolution and is
not a dangling-subject warning, because its subject exists.

#### Scenario: A scope with a vanished subject warns without failing

- **WHEN** the Statement of Applicability is resolved for a standard while a scope targets
  that standard (or one of its requirements) with a `subject` id that no asset defines
- **THEN** the projection is served, the affected node dispositions resolve as if that
  scope were absent, and the page shows a generic non-blocking warning ("rule targets a
  resource that does not currently exist") that does NOT name the scope id or the
  unresolved subject id to an ordinary caller (any detailed-id surface would be
  system-admin-only and is out of scope here)

#### Scenario: Control-target scope with a vanished subject also warns on the page

- **WHEN** the Statement of Applicability page is rendered while a control-target
  scope has a `subject` id that no asset defines
- **THEN** the same generic non-blocking warning ("rule targets a resource that does not
  currently exist") is shown on the page, even though a control-target scope contributes no
  node disposition, because the warning scan is not exempted by target kind

#### Scenario: A retired discovered subject warns without failing

- **WHEN** a scope's `subject` names a discovered `Machine` whose `state` is `Retired`
- **THEN** the same generic non-blocking warning is shown, no node for that machine is
  returned to any caller (a retired discovered asset is in no caller's accessible asset set,
  so the accessible-set filter drops it), and the projection is served

#### Scenario: Dangling subject does not block a sync or the projection

- **WHEN** an asset that was a scope subject is removed so the scope's subject dangles
- **THEN** the sync succeeds (the subject has no foreign key), the Statement of
  Applicability still resolves, and the dangling subject surfaces only as a non-blocking
  warning at sync (CLI) and at resolution (page)

