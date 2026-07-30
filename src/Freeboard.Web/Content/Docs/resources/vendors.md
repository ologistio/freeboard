# Vendors

A vendor is an external party you depend on. Freeboard records the vendor, the
organisation accountable for it, and every requirement or control the vendor is excluded
from. This page covers the three ways you work with a vendor: the web app, the GitOps
config, and the HTTP API.

## What a vendor is

A vendor is an `Asset` with `type: Vendor`. Assets share one id space, so a vendor, a
company, a department, and a discovered machine cannot collide on an id.

An asset carries at most one edge. A vendor uses `owner`, which names the `Company` or
`Department` accountable for it. A vendor cannot set `parent`, and only a vendor sets
`owner`.

The `owner` edge also decides who sees the vendor. A caller reads a vendor when the
caller can read the vendor's owner. A vendor with no owner, or with an owner that names
no asset, is visible to nobody. Freeboard fails closed here on purpose.

Exclusions are separate documents. A `Scope` whose `subject` is the vendor records the
disposition of that vendor against one requirement or one control. A vendor has no
standard-level disposition, so a vendor subject cannot target a standard. A scope with
`disposition: Out` must carry a `justification`, and Freeboard never shows an `Out`
without it.

> Git is the only write path for a vendor. The web app and the API both read. The tabs
> below show the same three tasks from each side.

## Add a vendor

::::tabs Interaction model

:::tab UI

The web app does not create vendors. It runs in GitOps read-only mode, so the register
at `/compliance/vendors` is a read surface and the write endpoints answer `409`. Use the
app to confirm what Git produced.

1. Sync the config from Git (see the GitOps tab).
2. Open the app and select **Risk > Vendors**.
3. Find the vendor by title. The id appears under it.

If the vendor is missing, its `owner` is the first thing to check. An owner you cannot
read hides the vendor from you, and an owner that names no asset hides it from everyone.

:::

:::tab GitOps

1. Add a document to your config directory. Freeboard reads `*.yaml` from the directory
   and every directory below it, so file layout is yours to choose.

   ```yaml
   apiVersion: freeboard.dev/v1alpha1
   kind: Asset
   id: vendor-ledgerleaf
   title: LedgerLeaf Accounting
   type: Vendor
   source: declared
   owner: fixture-corp
   ```

2. Validate the directory. Nothing is written and no network call is made.

   ```sh
   $ freeboard gitops validate ./config
   OK: 1 standard(s), 12 requirement(s), 9 control(s), 4 asset(s) (1 company,
   2 department, 0 machine, 1 vendor), 3 scope(s), 5 collector(s), 1 integration(s).
   ```

3. Write it to the store.

   ```sh
   $ freeboard gitops sync ./config
   Synced: 1 standard(s), 12 requirement(s), 9 control(s), 4 asset(s) (1 company,
   2 department, 0 machine, 1 vendor), 3 scope(s), 5 collector(s), 1 integration(s).
   ```

`sync` needs a MySQL connection string. Pass `--connection-string` or set `FREEBOARD_DB`.
`sync` refuses to write to a schema that is out of date. Run `freeboard system migrate`
first, or pass `--migrate`.

:::

:::tab API

The API has no create endpoint for a vendor. `PUT /api/v1/freeboard/scopes/{id}` accepts
an organisation subject only, and a vendor row is GitOps-write-only. Author the vendor in
Git, then read it back here.

```sh
$ curl https://freeboard.example.com/api/v1/freeboard/vendors \
    -H "Authorization: Bearer $FREEBOARD_SESSION_TOKEN"
[
  { "id": "vendor-ledgerleaf", "title": "LedgerLeaf Accounting" }
]
```

The response carries `id` and `title` and no other field. The rows are narrowed by the
same owner rule the web app uses, so this list is what the caller may read, not what the
store holds.

:::

::::

## Record an exception

The example below excludes one accounting package from the central user-access
requirement. The package supports MFA but not SSO, so a quarterly access review stands in
its place.

::::tabs Interaction model

:::tab UI

The register renders each vendor with a row per exception: the target, the disposition,
and the justification. An `Out` always shows its reason, so no exclusion is silent.

| Target | Disposition | Justification |
| --- | --- | --- |
| `req-ce-plus-user-access-control-01` (requirement) | Out | LedgerLeaf supports MFA but not SSO, so Finance provisions and removes accounts by hand. A quarterly access review is the compensating control. |

A vendor you cannot read has its exceptions hidden with it. The justification text never
reaches a caller who is not entitled to the vendor row.

:::

:::tab GitOps

Add a `Scope` document beside the vendor. Set `subject` to the vendor id and name exactly
one target, either `requirement` or `control`.

```yaml
apiVersion: freeboard.dev/v1alpha1
kind: Scope
id: vs-ledgerleaf-user-access-out
title: LedgerLeaf excluded from centralised user access control
subject: vendor-ledgerleaf
requirement: req-ce-plus-user-access-control-01
disposition: Out
justification: >-
  LedgerLeaf supports MFA but not SSO, so Finance provisions and removes accounts
  by hand. A quarterly access review is the compensating control.
```

Check the plan before you sync. `apply --dry-run` prints the state the config describes
and writes nothing.

```sh
$ freeboard gitops apply ./config --dry-run
Planned config state (dry-run, nothing written):
...
Assets (4):
  - vendor-ledgerleaf: LedgerLeaf Accounting [Vendor] owner=fixture-corp
Scopes (3):
  - vs-ledgerleaf-user-access-out: LedgerLeaf excluded from centralised user access
    control -> vendor-ledgerleaf / requirement req-ce-plus-user-access-control-01 = Out
```

:::

:::tab API

Read the exceptions from the unified scope endpoint and keep the rows whose `subject` is
the vendor.

```sh
$ curl https://freeboard.example.com/api/v1/freeboard/scopes \
    -H "Authorization: Bearer $FREEBOARD_SESSION_TOKEN"
[
  {
    "id": "vs-ledgerleaf-user-access-out",
    "title": "LedgerLeaf excluded from centralised user access control",
    "subject": "vendor-ledgerleaf",
    "standard": null,
    "requirement": "req-ce-plus-user-access-control-01",
    "control": null,
    "disposition": "Out",
    "justification": "LedgerLeaf supports MFA but not SSO, so Finance provisions and removes accounts by hand. A quarterly access review is the compensating control."
  }
]
```

There is one scope endpoint for every subject kind. A row is returned only when the
caller may read its subject, so a hidden vendor leaks neither its id nor its reason.

:::

::::

## Read the register

::::tabs Interaction model

:::tab UI

Open **Risk > Vendors**, or go to `/compliance/vendors` directly. The page needs a signed
in user. An anonymous request redirects to `/login`. If the store is unreachable the page
says so in place, rather than failing the request.

:::

:::tab GitOps

Git holds the authored state, not the stored state. To list what the config describes,
run the dry run and read the `Assets` and `Scopes` sections.

```sh
freeboard gitops apply ./config --dry-run
```

Two things never appear in this output, because the config never holds them: a discovered
machine, and any secret. A collector token is resolved out of band by connection id.

:::

:::tab API

`freeboard vendor list` prints the register through the HTTP API. The command makes no
database connection.

```sh
$ export FREEBOARD_API_URL=https://freeboard.example.com
$ export FREEBOARD_ADMIN_TOKEN=...
$ freeboard vendor list
vendor-ledgerleaf  LedgerLeaf Accounting
    Out  requirement req-ce-plus-user-access-control-01 - LedgerLeaf supports MFA but
    not SSO, so Finance provisions and removes accounts by hand. A quarterly access
    review is the compensating control.
```

The command exits `0` on success, `1` on a validation response, and `3` on an operational
failure. An unauthorized, forbidden, unreachable, or failing API is an operational
failure.

:::

::::

## Field reference

An `Asset` of `type: Vendor`:

| Field | Required | Notes |
| --- | --- | --- |
| `apiVersion` | yes | `freeboard.dev/v1alpha1`. |
| `kind` | yes | `Asset`. |
| `id` | yes | Identity. Unique across every asset. |
| `title` | yes | Display text. |
| `type` | yes | `Vendor`. |
| `source` | yes | `declared`. Config authors declared assets only. |
| `owner` | no | A `Company` or `Department` id. Absent means visible to nobody. |

A `Scope` whose subject is a vendor:

| Field | Required | Notes |
| --- | --- | --- |
| `apiVersion` | yes | `freeboard.dev/v1alpha1`. |
| `kind` | yes | `Scope`. |
| `id` | yes | Identity. |
| `title` | yes | Display text. |
| `subject` | yes | The vendor id. |
| `requirement` | one of | A requirement id. Set this or `control`, never both. |
| `control` | one of | A control id. Set this or `requirement`, never both. |
| `disposition` | yes | `In` or `Out`. |
| `justification` | on `Out` | The reason for the exclusion. Optional on `In`. |

## Validation rules

`freeboard gitops validate` stops the following as errors:

- A vendor that sets `parent`. A vendor uses `owner`.
- A non-vendor asset that sets `owner`. Only a vendor has an owner.
- An asset that sets both `parent` and `owner`.
- An `owner` that names an asset which is not a `Company` or `Department`.
- A vendor-subject scope that targets a standard.
- A scope that names no target, or more than one.
- A scope that names a requirement id or a control id the config does not define.
- A scope with `disposition: Out` and no `justification`.
- A duplicate asset id or scope id.
- A second scope for a pair the config already maps, such as one vendor and one
  requirement.

The following are warnings. They do not stop a sync, and the operator sees them on
standard error:

- A vendor with no `owner`. The vendor is visible to nobody.
- An `owner` that names an id no asset defines.
- A scope `subject` that resolves to no live asset.
