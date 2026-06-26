# Example GitOps config

Sample declarative compliance config for Freeboard. The ids here are
placeholders for illustration and are not a claim of conformance to any real
standard.

## Layout

| File             | Kind       | Purpose                                  |
| ---------------- | ---------- | ---------------------------------------- |
| `standards.yaml` | `Standard` | Compliance standards in scope            |
| `controls.yaml`  | `Control`  | Requirements, each `maps_to` standard(s) |
| `scopes.yaml`    | `Scope`    | Asset groups, each lists its `controls`  |

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
