## MODIFIED Requirements

### Requirement: The shell applies the information-architecture route moves as a clean break

Landing the shell SHALL apply the information-architecture re-home that moves
configuration and administration pages under `/settings`, and the nav map SHALL
point at each destination's new route. The moves are: the collector register
`/compliance/evidence-collectors` and `/compliance/attestation-templates` to one
`/settings/collectors` page (the two legacy registers merge with the collector kind, so
the shell carries ONE `Collectors` nav entry rather than separate evidence-collector and
attestation-template entries); users `/admin/users` to `/settings/users` (and the one-time
credential display `/admin/usercredential` to `/settings/usercredential`); custom roles
`/admin/custom-roles` to `/settings/custom-roles` (and the role editor
`/admin/custom-roles/designer/{slug?}` to
`/settings/custom-roles/designer/{slug?}`); and role assignments
`/admin/role-assignments` to `/settings/role-assignments`. No module reporting page
exists today, so no page moves under `/reports`.

The move SHALL change only each page's route URL through its `@page` directive; the
page files SHALL stay in their current Razor Pages folders (`Pages/Compliance`,
`Pages/Admin`), so the existing folder authorization conventions
(`AuthorizeFolder("/Compliance")`, `AuthorizeFolder("/Admin")`) still gate them and
in-page enforcement is unchanged. No redirect from an old path SHALL be added: the
prior URLs cease to exist (a deliberate clean break, acceptable in pre-release
software). This applies equally to the two retired register URLs
`/settings/evidence-collectors` and `/settings/attestation-templates`, which cease to
exist with no redirect to `/settings/collectors`.
The path-asserting web and end-to-end tests SHALL be updated to the new
paths, and the preserved test markers (`temp-password`, `soa-nodes`,
`data-node-id`, `btn-primary`, `badge`, `badge-danger`, `badge-success`) SHALL
remain intact - the asserted path changes, the markers do not.

Moving these pages under `/settings` satisfies N4 and N5 at the route and
information-architecture level: module configuration and register pages no longer
hang off a feature's own nav entry, and no module report page exists. Full N4
consolidation into a single Settings page with per-module sections is page-body work
staged to a later phase; this change moves the routes and groups them, it does not
merge the page bodies into one Settings page.

#### Scenario: Moved pages answer at their new routes with no redirect

- **WHEN** the shell change is applied
- **THEN** each moved page answers at its new `/settings` route, the nav map links to
  the new route, and the old path returns no page and no redirect

#### Scenario: One collector nav entry replaces the two register entries

- **WHEN** the nav map is built
- **THEN** it carries a single `Collectors` destination at `/settings/collectors` under
  its group, and carries no evidence-collector or attestation-template destination, so no
  page appears twice and the palette index derived from the same map lists one entry

#### Scenario: Path-asserting tests and markers stay green

- **WHEN** the web and end-to-end test suites run after the change
- **THEN** they pass with their path assertions updated to the new `/settings`
  routes, and the preserved markers are still present in the rendered markup
