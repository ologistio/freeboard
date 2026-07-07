# Fixture Corp - a worked Cyber Essentials Plus example

A realistic, company-shaped GitOps config for **Fixture Corp Ltd**, a mid-size
software company pursuing Cyber Essentials Plus (v3.3) through Freeboard. It shows
how a real adopter structures their config: a shared standard catalog, a logical
organisation tree, the controls they run, and a vendor register with per-vendor
exceptions. Placeholder ids; not a claim about any real entity or a certification
claim.

## Layout

| Path                                   | Kind                       | Purpose                                                     |
| -------------------------------------- | -------------------------- | ----------------------------------------------------------- |
| `standards/cyber-essentials-plus.yaml` | `Standard` + `Requirement` | Symlink to the shared CE+ catalog                           |
| `organisations.yaml`                   | `Organisation`             | The company tree: one Company, four departments, one nested |
| `scopes.yaml`                          | `Scope`                    | Company-wide In scope for CE+                               |
| `requirement-scopes.yaml`              | `RequirementScope`         | A department exclusion overridden by a child department     |
| `controls.yaml`                        | `Control`                  | The controls Fixture Corp operates, each `maps_to` CE+ reqs |
| `vendors/*.yaml`                       | `Vendor` + `VendorScope`   | One file per vendor: the vendor and its scopes together     |

The config is loaded as a whole (every `.yaml` under this directory, recursively),
so the split into files and folders is organisational only - references resolve
across all of them.

## Shared standard catalog

`standards/cyber-essentials-plus.yaml` is a symlink to
[`../../shared/cyber-essentials-plus.yaml`](../../shared/cyber-essentials-plus.yaml):
the standard-authored CE+ `Standard` plus its full 35-requirement v3.3 technical
control set. A company adopts the standard by referencing this catalog, not by
copying it; the generic [`../gitops`](../gitops/README.md) example symlinks the
same file.

## Company structure

Fixture Corp is one `Company` with four `Department` children (Engineering, Sales
and Marketing, Finance and Operations, IT and Security). Engineering has a nested
`Platform Team`. A single company-wide `Scope` puts the whole tree In for CE+ by
inheritance.

`requirement-scopes.yaml` shows a two-level exception: Engineering excludes the
14-day patch requirement for a legacy build server, and the Platform team
(nested under Engineering) re-includes it - demonstrating nearest-ancestor
inheritance with child override.

## Vendors and exceptions

Each file under `vendors/` holds one `Vendor` and the `VendorScope`s that bind it
to the controls or requirements it stands behind:

- **CrowdStrike Falcon**, **Fleet**, **Google Workspace** - the real named
  integrations, all `In` (they participate normally in a control or requirement).
  CrowdStrike backs the EDR control, Fleet the device-management control and patch
  reporting, Google Workspace the MFA control and account system of record.
- **Northwind Cloud**, **LedgerLeaf Accounting**, **Quill Accountancy**,
  **KeyVault** - invented (marked `(example)`) to round out a plausible stack.

Two vendors carry `Out` exceptions, each with a required `justification` that the
vendor register always surfaces (never silent):

- **LedgerLeaf** (Finance's accounting SaaS) is `Out` of centralised user access
  control - it supports MFA but not SSO, so accounts are managed manually with a
  quarterly review as the compensating control.
- **Quill Accountancy** (external firm) is `Out` of the MFA control - it has no
  logins to Fixture Corp's systems (N/A); documents move over an encrypted portal.

A `VendorScope` targets exactly one of a `Requirement` id or a `Control` id, and
is flat: it carries no `organisation` and does not inherit down the org tree.

## Commands

```sh
# Validate the config (exit 0 when valid, 1 on errors).
freeboard gitops validate examples/fixture-corp

# Print the state that would be applied (dry-run only in this version).
freeboard gitops apply examples/fixture-corp --dry-run
```

See [docs/gitops.md](../../docs/gitops.md) for the full format and rules.
