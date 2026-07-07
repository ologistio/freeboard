## Why

Controls today say which requirements they satisfy (`maps_to`), but nothing says
how a control is evidenced or how its collected checks decide a pass/fail status.
The evidence model needs a first-class `EvidenceCollector` that attaches a data
source (an integration, a script, a manual or training attestation, or the device
agent) to a control, plus a rule on the control for how the collectors' checks
roll up into a status. This is the next Phase 0 item on the CE+ readiness V1
critical path; it builds on the just-merged Vendor/VendorScope kinds (#46) and
unblocks the downstream evidence store (#49), evidence ingest (#51), and scoring.

## What Changes

- Add a static GitOps kind `EvidenceCollector` that attaches a collector to a
  `Control`: `id`, `title`, `control` (a `Control` id), an optional `vendor` (a
  `Vendor` id), a `type` (one of `integration`, `script`, `manual-attestation`,
  `training-attestation`, `agent`), a `frequency` (a collection cadence used later
  for staleness), an optional `threshold` (the pass bar for the collector's
  checks), and an optional free-form `config` map for type-specific settings.
- Extend the existing `Control` kind with an optional `evaluation` rule (`all`,
  `any`, or `manual`) that says how the control's attached collectors roll up into
  a status. `evaluation` is required when a control has at least one attached
  collector, mirroring the conditional-required precedent set by VendorScope's
  `justification`. A control's "attached collectors" are the collectors that name
  it; the edge lives on the collector, so `Control` gains only the rule field, not
  a duplicate collector list.
- Parse, validate, and sync both through the existing GitOps pipeline with
  referential integrity: `collector -> control` and `collector -> vendor` resolve;
  `collector -> control -> requirement` holds transitively because a valid control
  already carries a non-empty `maps_to`. An unknown `type` (or `frequency`, or
  `evaluation`) is rejected with a clear diagnostic.
- Persist `evidence_collectors` in a new table and add the `evaluation` column to
  `controls`, via one forward-only migration, wired into the importer and read
  store. `frequency`, `threshold`, and the type-specific `config` are stored and
  exposed for later scoring/staleness use; nothing runs a collector in this change.
- Expose a read-only evidence-collector register on both surfaces in the same PR
  (parity rule): a web SSR page under `/compliance/evidence-collectors` and a CLI
  `collector list` command that reads through the HTTP API. Both are control-centric:
  each control shows its `evaluation` rule and its attached collectors.

## Capabilities

### New Capabilities

- `evidence-collector-register`: a read-only register of controls, their evaluation
  rule, and their attached evidence collectors (type, vendor, frequency, threshold,
  config), exposed as a web SSR page and a CLI command. Collectors are a new domain
  with no existing read-surface owner, so the register view is a distinct capability
  with a distinct owner, following the convention set by `vendor-register`.
  Authorship (the config kind) and storage extend existing layer capabilities
  rather than duplicating them.

### Modified Capabilities

- `gitops-config-format`: adds the `EvidenceCollector` kind and the `Control.evaluation`
  field to the schema, the kind enumeration, loader routing, and validation rules
  (the closed `type`/`frequency`/`evaluation` enums, the `threshold` range, the
  `collector -> control`/`collector -> vendor` reference resolution, and the
  evaluation-required-when-a-control-has-collectors rule).
- `compliance-persistence`: adds the `evidence_collectors` table and the
  `controls.evaluation` column via a new migration, extends the importer order to
  keep the new RESTRICT foreign keys safe, and extends the read store, read models,
  and counts.
- `compliance-web-read`: adds the `evidence-collectors` read endpoint, adds
  `evaluation` to the `controls` payload, and adds the `evidenceCollectors` count to
  the compliance status summary.

## Impact

- MIT (default). Config parse/validate lands in `Freeboard.Core`; tables, importer,
  and read store in `Freeboard.Persistence`; API endpoints and the SSR page in
  `Freeboard`; the read command in `Freeboard.CLI` (reading through the HTTP API,
  never `Freeboard.Enterprise`, never the database directly). Nothing here is an
  enterprise-gated feature, so nothing goes in `Freeboard.Enterprise`. The reference
  graph and the one-way EE rule are respected.
- New MySQL migration `012_evidence_collectors.sql`: creates `evidence_collectors`
  and adds a nullable `evaluation` column to `controls`. Additive and forward-only;
  the `controls` change is a nullable column add, so old configs keep syncing.
- New CLI command group `collector`; new API route
  `GET /api/v1/freeboard/evidence-collectors`; new page
  `/compliance/evidence-collectors`; `evaluation` added to
  `GET /api/v1/freeboard/controls`.

## Non-goals

- No runtime Evidence store and no evidence ingest. This change is the static config
  kind plus the Control evaluation rule only. Producing Evidence from a collector is
  issue #49 (store) and #51 (ingest); nothing in this change executes a collector.
- No scoring or staleness engine. `frequency` and `threshold` are parsed, validated,
  persisted, and exposed on the read surfaces so a later scoring/staleness change can
  consume them. This change adds no scoring code and no dead placeholder for it.
- No runtime evaluator. `Control.evaluation` is the static declaration of how nested
  checks map to a status. This change parses, validates, and exposes the rule; it
  does not compute a control status. The roll-up semantics (`all`/`any`/`manual` and
  the per-collector `threshold`) are written down as specification for the downstream
  scoring work but are not executed here; see design D8.
- No deep validation of the type-specific `config` map. Its per-type required keys
  are meaningful only to the collector runtime (#51), so V1 stores and echoes the
  map verbatim. The "config holds no secret material" rule is a documented schema
  expectation asserted by convention and test (design D6), NOT runtime secret scanning;
  this config-only change adds no secret-scanning code and does not inspect config
  contents. A collector's credentials are referenced by a named credential resolved
  out-of-band, never inlined in git.
- No app-managed (UI/API) create/update/delete of collectors. Authoring is GitOps
  only in V1, matching how every other kind first shipped.
