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

A vendor also carries two optional risk-profile fields, and no other asset type may carry
either. `tier` says how much damage the vendor can do: `Critical`, `High`, `Medium`, or
`Low`. `data_classes` says which regulated data the vendor holds, as a set of tokens:
`pii`, `phi`, `special-category`, `payment-card`, or `credentials`. The tokens name
regulatory regimes and overlap on purpose. Health data is `phi` under HIPAA and
`special-category` under UK/EU GDPR Article 9, so author both for it.

Both fields are optional. A vendor with no `tier` produces a warning, not an error. An
absent `data_classes` and an empty one mean the same thing, and neither produces a
diagnostic: a vendor that holds none of your regulated data is a real state.

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
   tier: High
   data_classes: [pii]
   assurances:
     - standard: std-soc2
       expires: 2027-03-31
   ```

   `assurances` is optional. Each entry names a `Standard` the config declares, so
   recording a vendor's SOC 2 means declaring SOC 2 as a `Standard` even when you pursue
   none of its requirements. A vendor with no certification simply omits the field.

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
  {
    "id": "vendor-ledgerleaf",
    "title": "LedgerLeaf Accounting",
    "tier": "High",
    "data_classes": ["pii"],
    "assurances": [
      { "standard": "std-soc2", "expires": "2026-06-30", "status": "Expired" }
    ]
  }
]
```

The response carries `id`, `title`, `tier`, `data_classes`, and `assurances` and no other
field. A vendor with no tier reads `null`, and one with no data classes or no assurances
reads `[]`. Each assurance carries its `standard`, its `expires` date, and a `status` of
`Valid`, `Expiring`, or `Expired` that the server derives from the current date and the
window that applies to the entry. The rows are narrowed by the same owner rule the web app
uses, so this list is what the caller may read, not what the store holds.

:::

::::

## Record an exception

The example below excludes one accounting package from the central user-access
requirement. The package supports MFA but not SSO, so a quarterly access review stands in
its place.

::::tabs Interaction model

:::tab UI

The **Scope rules** tab renders one row per rule: the vendor, the target, the kind, the
disposition, and the justification. An `Out` always shows its reason, so no exclusion is
silent. The **Directory** tab counts each vendor's exceptions in its own column.

| Vendor | Target | Kind | Disposition | Justification |
| --- | --- | --- | --- | --- |
| LedgerLeaf Accounting | `req-ce-plus-user-access-control-01` | requirement | Out | LedgerLeaf supports MFA but not SSO, so Finance provisions and removes accounts by hand. A quarterly access review is the compensating control. |

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

The register carries five tabs. **Directory** lists the vendors you may read. **Scope
rules** lists their exceptions. **Discovery**, **Reviews**, and **Procurement** are stages
Freeboard does not collect yet, so each one says what would appear there. Columns with no
data behind them read "Not tracked" rather than a guessed value.

The Directory shows each vendor's tier and data classes as plain tags. The tags carry no
color rank: a tier is a static attribute, and a data class names a regime rather than a
severity. The rows stay ordered by vendor id.

The Assurance column shows one stamp per certification on file, naming the standard and
the expiry. A stamp turns amber as the expiry approaches and red once it has passed. It
never turns green: a certificate is a fact the vendor supplied, not a Freeboard verdict,
so the Status column still reads "Not evaluated". A vendor with no certification reads
"None on file".

When any vendor you can read holds a lapsing or lapsed certification, the notice above the
tabs turns amber and names the count, and the Vendors item in the left rail carries the
same count. Both count only the vendors you can read.

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
vendor-ledgerleaf  LedgerLeaf Accounting  High  pii
    std-soc2  2026-06-30  Expired
    Out  requirement req-ce-plus-user-access-control-01 - LedgerLeaf supports MFA but
    not SSO, so Finance provisions and removes accounts by hand. A quarterly access
    review is the compensating control.
```

Each vendor line carries the id, the title, the tier, and the data classes. An absent
tier or data class list prints `-`. Each certification prints on its own indented line
below the vendor, with the standard, the expiry, and the state. A vendor with none prints
no such line.

The API returns the state alongside the date, so the command does not need to know the
warning window. The window lives in the web app, which is why the register and the command
cannot disagree about one certification.

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
| `tier` | no | `Critical`, `High`, `Medium`, or `Low`. Absent produces a warning. |
| `data_classes` | no | A set of `pii`, `phi`, `special-category`, `payment-card`, `credentials`. Absent and empty are the same. |
| `assurances` | no | The certifications the vendor holds. Absent means none, which is a normal state. |

Each entry under `assurances`:

| Field | Required | Notes |
| --- | --- | --- |
| `standard` | yes | The id of a `Standard` the config declares. A standard it does not declare is an error. |
| `expires` | yes | The date the certification lapses, as `yyyy-MM-dd`. |
| `warn_days` | no | Days of advance notice for this entry. Overrides the deployment default of 90. `0` warns only once expired. |

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
- A `tier` outside `Critical`, `High`, `Medium`, and `Low`.
- A `data_classes` token outside the five the vocabulary defines.
- A `tier` or a `data_classes` on an asset that is not a vendor.
- The same `data_classes` token listed twice. A repeat is an authoring mistake, so
  Freeboard reports it rather than removing it.
- An `assurances` list on an asset that is not a vendor.
- An assurance `standard` that names a standard the config does not declare.
- An assurance with no `expires`, or an `expires` that is not a `yyyy-MM-dd` date. An
  expiry already in the past is not an error: `validate` does not read the clock, so its
  result cannot depend on when it ran.
- An assurance `warn_days` that is not a whole number of zero or more.
- Two assurances on one vendor naming the same standard.
- An unknown field on an assurance entry.
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
- A vendor with no `tier`. The register shows the vendor as untracked.
- An `owner` that names an id no asset defines.
- A scope `subject` that resolves to no live asset.
