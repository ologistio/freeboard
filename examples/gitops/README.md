# Example GitOps config

A small, generic sample config that exercises every kind. The ids here are
placeholders for illustration and are not a claim of conformance to any real
standard. For a worked, company-shaped example see
[`../fixture-corp`](../fixture-corp/README.md).

## Layout

| File                         | Kind                       | Purpose                                                        |
| ---------------------------- | -------------------------- | -------------------------------------------------------------- |
| `cyber-essentials-plus.yaml` | `Standard` + `Requirement` | Symlink to the shared CE+ catalog (standard + 35 requirements) |
| `standards.yaml`             | `Standard`                 | The other standards declared locally (CE, SOC 2)               |
| `controls.yaml`              | `Control`                  | Implemented controls, each `maps_to` requirement(s)            |
| `organisations.yaml`         | `Asset`                    | The organisation tree, each with a `type` and parent           |
| `scopes.yaml`                | `Scope`                    | Maps a subject asset to a standard, requirement, or control    |
| `vendors.yaml`               | `Asset` (Vendor)           | Software, platforms, and external parties in use               |

`cyber-essentials-plus.yaml` is a symlink to
[`../shared/cyber-essentials-plus.yaml`](../shared/cyber-essentials-plus.yaml),
the standard-authored CE+ catalog (the `Standard` plus its full 35-requirement
v3.3 technical control set). Both example directories symlink the same file
rather than copying it. A `Control.maps_to` names `Requirement` ids (not
`Standard` ids); a control's standard is derived from the requirements it
satisfies.

`scopes.yaml` holds every scope. A `Scope` maps one `subject` (any asset id) to
exactly one target - a `Standard`, a `Requirement`, or a `Control` - with a
disposition. It shows all three layers together:

- Standard-target scopes on the org tree, inherited down it by nearest ancestor.
- Requirement-level scoping: Ologist Products excludes one CE+ requirement
  company-wide, and its Engineering department re-includes it. A requirement-target
  scope carries no `standard` (the requirement fixes it) and resolves only under a
  standard that is `In`.
- Vendor-subject exceptions: a vendor `subject` (declared in `vendors.yaml`) targets
  exactly one `Requirement` or `Control` (a vendor cannot target a standard). An
  `Out` scope is an exception and must carry a non-empty `justification` (e.g.
  "supports MFA but not SSO"; "external firm, no logins - N/A"); the register always
  surfaces it, so an exception is never silent. A vendor-subject scope is flat: it
  does not inherit down the org tree.

`vendors.yaml` declares each vendor as an `Asset` with `type: Vendor` (a plain id +
title for a tool or party in use). CrowdStrike, Fleet, and Google Workspace are real
named integrations; the rest are invented for illustration.

Kinds may be mixed in any file; the split above is a convention, not a rule.
Every document declares `apiVersion: freeboard.dev/v1alpha1`. Every resource has a
stable `id` (its identity) and a `title` (display text that may change).

## Commands

```sh
# Validate the config (exit 0 when valid, 1 on errors).
freeboard gitops validate examples/gitops

# Print the state that would be applied (dry-run only in this version).
freeboard gitops apply examples/gitops --dry-run
```

See [docs/gitops.md](../../docs/gitops.md) for the full format and rules.
