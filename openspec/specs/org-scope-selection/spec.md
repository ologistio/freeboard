# org-scope-selection Specification

## Purpose
TBD - created by archiving change add-org-selector. Update Purpose after archive.
## Requirements
### Requirement: Menu shows an organisation selector as a tree

The web app SHALL render an organisation selector in the authenticated app shell
menu. The selector SHALL present the organisations the user may access as a tree
that reflects each organisation's `parent`: a root organisation appears at the top
level and a child appears nested under its parent. Each node SHALL show its title.
Above the tree the selector SHALL present an "All Organisations" entry. The
selector SHALL indicate which entry is the current selection. When the user has no
accessible organisations the selector SHALL render only the "All Organisations"
entry.

The selector renders on every page of the authenticated app shell and reads the
organisation data from a lazy store that may be unreachable. When that data cannot
be loaded, the selector SHALL degrade to rendering only the "All Organisations"
entry and SHALL NOT throw into the shared layout, so a store outage never turns an
unrelated page into a server error.

#### Scenario: Tree reflects the organisation hierarchy

- **WHEN** an authenticated user opens the menu and the accessible organisations
  are a company with a department child
- **THEN** the selector shows an "All Organisations" entry, the company at the top
  level, and the department nested under the company

#### Scenario: Current selection is indicated

- **WHEN** an organisation is the current selection
- **THEN** the selector marks that organisation as selected and does not mark "All
  Organisations" as selected

#### Scenario: No accessible organisations

- **WHEN** an authenticated user has no accessible organisations
- **THEN** the selector renders only the "All Organisations" entry

#### Scenario: Organisation data unavailable degrades to All Organisations

- **WHEN** the organisation store is unreachable while a shared-layout page renders
- **THEN** the selector shows only the "All Organisations" entry with no tree and
  the page still renders normally rather than returning a server error

### Requirement: Current organisation selection persists across navigation

The web app SHALL persist the user's current organisation selection so that it
survives navigation between views and a page reload without re-selecting. The
selection SHALL be carried in a cookie set when the user chooses an entry and read
on each subsequent request. Choosing an organisation SHALL set the selection to
that organisation; choosing "All Organisations" SHALL clear the selection. The
selection endpoint SHALL require an authenticated user; an unauthenticated request
SHALL NOT set the selection cookie and SHALL be redirected to sign in, like the
other authenticated reads. The endpoint SHALL be GET-only so it is served in GitOps
read-only mode, and after recording the selection it SHALL redirect back to the
page the user was on. The return target SHALL preserve the originating page's query
string, not only its path, so state carried in the query (for example the active
standard on the Statement of Applicability page) survives a selection. The return
target SHALL be validated as a local path, falling back to a safe app page when it
is not local.

#### Scenario: Selecting an organisation persists it

- **WHEN** a user selects an organisation and then navigates to another view
- **THEN** the other view sees that organisation as the current selection without
  the user re-selecting it

#### Scenario: Selection survives a reload

- **WHEN** a user has selected an organisation and reloads the page
- **THEN** the selection is still that organisation

#### Scenario: All Organisations clears the selection

- **WHEN** a user selects "All Organisations"
- **THEN** the current selection is cleared and subsequent views apply no subtree
  filter

#### Scenario: Selection is served in read-only mode

- **WHEN** GitOps read-only mode is on and a user chooses a selector entry
- **THEN** the selection is recorded and the user is redirected back, not rejected
  with the read-only 409 response

#### Scenario: Return target must be local

- **WHEN** the selection endpoint is given a non-local return target
- **THEN** it redirects to a safe default local path rather than the supplied
  target

#### Scenario: Return target preserves the query string

- **WHEN** a user selects an organisation, and separately chooses "All
  Organisations", from a page whose state is carried in its query string (for
  example `?standard=`)
- **THEN** each redirect returns to that page with its query string intact, so the
  page state is not reset by the selection

#### Scenario: Anonymous request cannot set the selection

- **WHEN** an unauthenticated request calls the selection endpoint
- **THEN** no selection cookie is set and the request is redirected to sign in

### Requirement: Selection is resolved and validated server-side

The web app SHALL resolve the current organisation selection server-side on each
request from the cookie and the set of organisations the user may access. The
resolved selection SHALL be the selected organisation only when its id is present
in the accessible set; otherwise the resolved selection SHALL be "All
Organisations". No organisation the user may not access SHALL become the resolved
selection, whatever the cookie contains.

#### Scenario: Absent cookie resolves to All Organisations

- **WHEN** no selection cookie is present
- **THEN** the resolved selection is "All Organisations"

#### Scenario: Accessible organisation resolves to itself

- **WHEN** the cookie names an organisation in the accessible set
- **THEN** the resolved selection is that organisation

#### Scenario: Inaccessible or unknown organisation is dropped

- **WHEN** the cookie names an organisation id that is not in the accessible set
  (unknown, deleted, or not permitted)
- **THEN** the resolved selection is "All Organisations" and no data for that id is
  shown through the accessible-set bound

### Requirement: Org-scoped views filter to the selected subtree

An org-scoped view SHALL restrict the organisation rows it renders to the in-scope
set for the resolved selection. When an organisation is selected the in-scope set
SHALL be that organisation plus all of its descendants (its subtree). When the
resolved selection is "All Organisations" the in-scope set SHALL be all accessible
organisations. The restriction SHALL be applied server-side so that rows outside
the in-scope set are absent from the rendered response, not merely hidden by
client code. Computing descendants SHALL terminate even if the stored parent links
contain a cycle.

#### Scenario: Selected organisation scopes to its subtree

- **WHEN** an organisation with descendants is the resolved selection and an
  org-scoped view is rendered
- **THEN** the view renders that organisation and its descendants and omits
  organisations outside that subtree

#### Scenario: All Organisations applies no subtree filter

- **WHEN** the resolved selection is "All Organisations" and an org-scoped view is
  rendered
- **THEN** the view renders every accessible organisation

#### Scenario: Out-of-scope rows are absent server-side

- **WHEN** an organisation is selected and an org-scoped view is rendered
- **THEN** the rendered response contains no rows for organisations outside the
  in-scope subtree

#### Scenario: Cyclic parent links do not hang scoping

- **WHEN** the stored organisations contain a parent cycle and a subtree is
  computed for a selection
- **THEN** the computation terminates and returns a finite in-scope set

### Requirement: Accessible organisation set bounds selection and scoping

The web app SHALL determine the set of organisation ids a user may access through a
single seam and use that set both to bound the selector tree and to bound every
org-scoped view. Selecting an organisation outside the accessible set SHALL fail
closed: it SHALL NOT become the resolved selection and its data SHALL NOT be
rendered. The accessible set SHALL be derived from authorization: it is the union
of organisation subtrees on which the user holds a read-granting role, and all
persisted organisations for a super-admin. Under the Observe rollout mode reads are
NOT narrowed: the accessible set SHALL be all persisted organisations for every
caller regardless of grants, so read behaviour is unchanged while decisions are
observed. Under the Compat rollout mode a user with no assignments SHALL retain
access to all persisted organisations through an audited fallback; under Enforce a
user with no read-granting role SHALL have an empty accessible set. Because the seam
is async (it loads the user's grants), it
SHALL resolve the accessible set once per request, memoized alongside the
authorization fact load.

#### Scenario: Accessible set bounds the selector and views

- **WHEN** the selector tree and an org-scoped view are rendered
- **THEN** both include only organisations in the accessible set

#### Scenario: All Organisations is bounded by the accessible set

- **WHEN** the resolved selection is "All Organisations" and the accessible set is
  a strict subset of the persisted organisations
- **THEN** the selector tree and every org-scoped view render only the accessible
  organisations, and no organisation outside the accessible set appears even though
  no subtree filter is applied

#### Scenario: Selecting an inaccessible organisation fails closed

- **WHEN** a user submits a selection for an organisation not in the accessible set
- **THEN** the selection does not take effect and no data for that organisation is
  rendered

#### Scenario: Super-admin accesses every organisation

- **WHEN** a super-admin renders the selector or an org-scoped view
- **THEN** the accessible set is all persisted organisations

#### Scenario: Observe does not narrow reads

- **WHEN** the rollout mode is Observe and a caller holds a grant on only part of the
  organisation tree
- **THEN** the accessible set is all persisted organisations, so the caller sees the
  full tree and read behaviour is unchanged

### Requirement: Selector markup preserves the accessibility baseline

The selector markup SHALL NOT regress the accessibility audit of the shared
authenticated layout. The selector renders on the account, MFA, and admin pages the
`web-accessibility` capability audits at zero axe-core violations across every
supported standard, and it SHALL pass that same automated audit at WCAG A, AA, and
AAA on those layout-carrying pages. Each selector entry SHALL have discernible link text, the
tree SHALL use correct list semantics, text and selection-marker colours SHALL meet
WCAG AAA contrast, and the selector SHALL NOT add a navigation landmark that
duplicates the existing sidebar navigation landmark. Each expand/collapse toggle
control SHALL have a discernible accessible name and SHALL expose its open or closed
state through `aria-expanded`.

#### Scenario: Layout with the selector passes the accessibility audit

- **WHEN** a layout-carrying page (account, MFA, or admin) is rendered with a
  multi-node organisation tree and a current selection on a top-level node, with at
  least one branch expanded so the nested tree and the selection marker are VISIBLE
  (not merely present in collapsed markup that axe-core skips) alongside the
  expand/collapse toggle controls, and it is audited with axe-core against every
  supported standard including WCAG AAA
- **THEN** the audit reports zero violations, and the previously audited account,
  MFA, and admin pages continue to pass

#### Scenario: Expand/collapse toggles are labelled and expose their state

- **WHEN** the selector renders a branch that can be expanded or collapsed
- **THEN** the toggle control has a discernible accessible name and exposes an
  `aria-expanded` state reflecting whether the branch is open

