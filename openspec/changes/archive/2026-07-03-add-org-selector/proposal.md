## Why

The web UI shows compliance data across the whole organisation tree at once. As
the tree grows, a user assessing one company or department has no way to narrow
every view to that part of the tree. There is no shared, persistent notion of a
"current organisation" that the app's org-scoped views can honour.

This change adds an organisation selector to the app menu: a tree of the
organisations a user may access plus an "All Organisations" entry that applies no
filter. Selecting an organisation scopes every org-scoped view to that
organisation and its descendants; the selection persists as the user navigates.

## What Changes

- Add an organisation selector to the app shell menu (the left sidebar), rendered
  as the existing organisation tree (Company / Department nodes with parents),
  plus an "All Organisations" entry at the top.
- Persist the current selection across navigation and page reloads via a cookie,
  resolved and re-validated server-side on every request.
- Scope org-scoped views to the selected organisation and its descendant subtree.
  The Statement of Applicability page is the one org-scoped view today; it renders
  only the in-scope subtree while its disposition inheritance still resolves over
  the full tree so inherited values from ancestors above the selection are
  preserved.
- "All Organisations" is the default (no cookie) and applies no subtree filter; it
  is bounded by the set of organisations the user may access.
- Enforce scoping server-side: the in-scope node set is computed in the page model
  and only in-scope rows are rendered, so the filter is not a client-side
  convenience. Resolving the selection re-validates the selected id against the
  accessible set and drops any id the user may not access (fail closed).
- Add an accessibility seam (`IOrgAccess`) that returns the organisation ids a user
  may access. Its v1 implementation returns all organisations, matching today's
  authorization model where any authenticated user may read the whole compliance
  domain. The seam is the single point a future per-organisation access model
  narrows, without touching call sites.

This change is MIT (default). It is core product UX and view scoping, not a paid,
enterprise-gated feature, so no code lands in `src/Freeboard.Enterprise`. All new
code lives in the web app (`src/Freeboard`), which already references
`Freeboard.Core` and `Freeboard.Persistence`. No new project references, no new
runtime dependency, no schema migration (the organisation tree already exists).

## Capabilities

### New Capabilities

- `org-scope-selection`: the menu organisation selector, the persisted current
  selection, the "All Organisations" default, server-side resolution and
  fail-closed validation of the selection, the subtree scoping applied to
  org-scoped views, and the accessibility seam that bounds what a user may select.

### Modified Capabilities

- `statement-of-applicability`: the read-only projection page scopes its rendered
  node list to the active organisation selection (selected node plus descendants),
  while the underlying projection and the JSON endpoint still compute over the full
  tree so inherited dispositions from ancestors above the selection stay correct.

## Impact

- Affected code (web app, `src/Freeboard`): `Pages/Shared/_Layout.cshtml` (menu),
  a new organisation-selector view component, a new pure subtree helper, a new
  request-scoped selection resolver plus its cookie, a small GET selection
  endpoint, DI registration in `Program.cs`, and the Statement of Applicability
  page model and view.
- No change to `Freeboard.Core`, `Freeboard.Persistence`, `Freeboard.Enterprise`,
  `Freeboard.Agent`, or `Freeboard.CLI`.
- No change to the JSON compliance read endpoints in v1; they remain full-domain
  machine reads behind the same authentication.
- Tests: new unit tests for the subtree helper and selection resolver, new page
  and endpoint tests for scoping and fail-closed behaviour, and a browser E2E test
  for select, filter, "All", and persistence across navigation.
