## Why

Two of the evidence source types the just-merged `EvidenceCollector` kind names -
`manual-attestation` (Case 3) and `training-attestation` (Case 4) - render a form
that a human fills in to attest a control. Nothing today describes that form. The
model needs a first-class `AttestationTemplate` that declares, as static GitOps
config, the fields a manual attestation captures and the quiz a training
attestation must pass. There is no visual builder in V1: templates are authored as
YAML and rendered downstream. This is the next Phase 0 item on the CE+ readiness V1
critical path; it builds on the merged EvidenceCollector kind (#48 follows #49-style
collector work) and unblocks the downstream attestation runtime (form render and
response capture), which is out of scope here.

## What Changes

- Add a static GitOps kind `AttestationTemplate` that attaches an attestation form
  to a `Control`: `id`, `title`, `control` (a `Control` id), a `type` (one of
  `manual` or `training`), an optional markdown `body`, an optional list of `fields`
  (each with an `id`, a `label`, and a `type` that is exactly one of `boolean`,
  `single-choice`, or `short-text`, plus `options` for a `single-choice` field), an
  optional `quiz` (a list of items, each with an `id`, a `prompt`, `options`, and the
  correct `answer`), and an optional `pass_mark` (an integer percent).
- Parse, validate, and sync through the existing GitOps pipeline with referential
  integrity: `template -> control` resolves (mirroring `collector -> control`); a
  field `type` outside the three allowed tokens is rejected with a clear diagnostic;
  `type: training` requires a `pass_mark` and at least one `quiz` item, while
  `type: manual` must not declare a quiz or pass mark.
- Persist `attestation_templates` in a new table via one forward-only migration,
  wired into the importer and the read store. The `fields` and `quiz` lists are
  stored as JSON, mirroring how the collector `config` map is stored; nothing renders
  or scores a template in this change.
- Expose a read-only attestation-template register on both surfaces in the same PR
  (parity rule): a web SSR page under `/compliance/attestation-templates` and a CLI
  `attestation-template list` command that reads through the HTTP API. The register is
  authenticated but broad (any logged-in user, like the vendor and collector
  registers), so it MUST NOT leak a training quiz's correct answer: the answer is
  persisted but redacted from every read surface, and the markdown `body` is rendered as
  encoded text (never raw HTML) to avoid stored XSS from git-authored content. See
  design.md "Plan synthesis".

## Capabilities

### New Capabilities

- `attestation-template-register`: a read-only register of the persisted attestation
  templates - each template's control, type, body, fields, and (for training) pass
  mark and quiz - exposed as a web SSR page and a CLI command. Attestation templates
  are a new domain with no existing read-surface owner, so the register is a distinct
  capability with a distinct owner, following the convention set by `vendor-register`
  and `evidence-collector-register`. Authorship (the config kind) and storage extend
  existing layer capabilities rather than duplicating them.

### Modified Capabilities

- `gitops-config-format`: adds the `AttestationTemplate` kind to the schema, the kind
  enumeration, loader routing, and validation rules (the closed `type` enum, the
  closed field-`type` enum, the `pass_mark` range, the `template -> control`
  reference resolution, the `single-choice`-needs-options rule, the quiz-item shape,
  and the training-vs-manual conditional rules).
- `compliance-persistence`: adds the `attestation_templates` table via a new
  migration, extends the importer order to keep the new RESTRICT foreign key safe,
  and extends the read store, read models, and counts.
- `compliance-web-read`: adds the `attestation-templates` read endpoint and adds the
  `attestationTemplates` count to the compliance status summary and its unreachable
  degradation.

## Impact

- MIT (default). Config parse/validate lands in `Freeboard.Core`; the table,
  importer, and read store in `Freeboard.Persistence`; the API endpoint and the SSR
  page in `Freeboard`; the read command in `Freeboard.CLI` (reading through the HTTP
  API, never `Freeboard.Enterprise`, never the database directly). Nothing here is an
  enterprise-gated feature, so nothing goes in `Freeboard.Enterprise`. The reference
  graph and the one-way EE rule are respected.
- New MySQL migration `013_attestation_templates.sql`: creates `attestation_templates`.
  Additive and forward-only; it alters no existing table, so old configs keep syncing.
- New CLI command group `attestation-template`; new API route
  `GET /api/v1/freeboard/attestation-templates`; new page
  `/compliance/attestation-templates`; `attestationTemplates` added to
  `GET /api/v1/freeboard/compliance/status`.

## Non-goals

- No attestation runtime. This change is the static config kind plus its read
  surfaces only. Rendering a template to an interactive form, capturing a user's
  responses, storing those responses, grading a quiz against `answer`/`pass_mark`, or
  computing an attestation pass/fail is downstream and adds no code here.
- No coupling to `EvidenceCollector`. A template attaches to a `Control`, exactly as
  the task specifies; wiring a `manual-attestation`/`training-attestation` collector
  to a specific template is a later change. This change adds no collector-to-template
  reference and no cross-validation between the two kinds.
- No deep per-type field semantics beyond structure. `boolean`, `single-choice`, and
  `short-text` are validated for shape (a `single-choice` field carries options;
  the others do not); nothing renders or evaluates a field value here.
- No app-managed (UI/API) create/update/delete of templates, and no visual builder.
  Authoring is GitOps only in V1, matching how every other kind first shipped.
