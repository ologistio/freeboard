# Example GitOps config

Sample declarative compliance config for Freeboard. The ids here are
placeholders for illustration and are not a claim of conformance to any real
standard.

## Layout

| File                      | Kind               | Purpose                                                  |
| ------------------------- | ------------------ | -------------------------------------------------------- |
| `standards.yaml`          | `Standard`         | Compliance standards in scope, with version/authority    |
| `requirements.yaml`       | `Requirement`      | A standard's published statements, each in one `theme`   |
| `controls.yaml`           | `Control`          | Implemented controls, each `maps_to` requirement(s)      |
| `organisations.yaml`      | `Organisation`     | The organisation tree, each with a `type` and parent     |
| `scopes.yaml`             | `Scope`            | Maps an organisation to a standard with a disposition    |
| `requirement-scopes.yaml` | `RequirementScope` | Maps an organisation to a requirement with a disposition |
| `vendors.yaml`            | `Vendor`           | Software, platforms, and external parties in use         |
| `vendor-scopes.yaml`      | `VendorScope`      | Per-vendor requirement/control exceptions with rationale |

`requirements.yaml` holds the full Cyber Essentials Plus v3.3 technical control
set (35 requirements across five themes) as a worked example. A `Control.maps_to`
now names `Requirement` ids (not `Standard` ids); a control's standard is derived
from the requirements it satisfies.

`requirement-scopes.yaml` shows requirement-level scoping: Ologist Products
excludes one CE+ requirement company-wide, and its Engineering department
re-includes it. A `RequirementScope` carries no `standard` (the requirement fixes
it) and resolves only under a standard that is `In`.

`vendors.yaml` and `vendor-scopes.yaml` show per-vendor exceptions. A `Vendor` is
a plain id + title for a tool or party in use (CrowdStrike, Fleet, and Google
Workspace are real named integrations; the rest are invented for illustration). A
`VendorScope` binds one vendor to exactly one target - a `Requirement` id or a
`Control` id - with a disposition. An `Out` scope is an exception and must carry a
non-empty `justification` (e.g. "supports MFA but not SSO"; "external firm, no
logins - N/A"); the register always surfaces it, so an exception is never silent.
A `VendorScope` is flat: it carries no `organisation` and does not inherit down
the org tree.

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
