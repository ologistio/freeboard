# Example GitOps config

Sample declarative compliance config for Freeboard. The ids here are
placeholders for illustration and are not a claim of conformance to any real
standard.

## Layout

| File                 | Kind           | Purpose                                                 |
| -------------------- | -------------- | ------------------------------------------------------- |
| `standards.yaml`     | `Standard`     | Compliance standards in scope, with version/authority   |
| `requirements.yaml`  | `Requirement`  | A standard's published statements, each in one `theme`  |
| `controls.yaml`      | `Control`      | Implemented controls, each `maps_to` requirement(s)     |
| `organisations.yaml` | `Organisation` | The organisation tree, each with a `type` and parent    |
| `scopes.yaml`        | `Scope`        | Maps an organisation to a standard with a disposition   |

`requirements.yaml` holds the full Cyber Essentials Plus v3.3 technical control
set (35 requirements across five themes) as a worked example. A `Control.maps_to`
now names `Requirement` ids (not `Standard` ids); a control's standard is derived
from the requirements it satisfies.

Kinds may be mixed in any file; the split above is a convention, not a rule.
Every document declares `apiVersion: freeboard.io/v1alpha1`. Every resource has a
stable `id` (its identity) and a `title` (display text that may change).

## Commands

```sh
# Validate the config (exit 0 when valid, 1 on errors).
freeboard gitops validate examples/gitops

# Print the state that would be applied (dry-run only in this version).
freeboard gitops apply examples/gitops --dry-run
```

See [docs/gitops.md](../../docs/gitops.md) for the full format and rules.
