## Why

Today a `Standard` is only an id and a title, and the only normative content in
the config model is the `Control` kind (an implemented control that `maps_to`
one or more standards). There is no way to record what a standard actually
requires - its published requirement statements, themes, guidance, and source
citations. Without that, Freeboard cannot show a real standard's requirement set,
cannot map a control to the specific requirements it satisfies, and ships only
placeholder standards. This change gives a `Standard` real structure and adds a first real,
freely bundleable standard: Cyber Essentials Plus (CE+), whose scheme
requirements are publicly published by the NCSC and IASME.

## What Changes

- Add a new config kind `Requirement`, distinct from `Control`. A `Requirement`
  belongs to exactly one `Standard` and carries a `theme`, a normative
  `statement`, optional `guidance`, and an external citation split into a
  `citation_label` and a `citation_url` (the published source and a link to it).
- Enrich the `Standard` kind with metadata: required `version` and `authority`
  (the scheme owner), plus optional `publisher` (delivery/certification body) and
  `source_url` (official source). `version` and `authority` are required so a
  `Standard` is a real, described object; `publisher` and `source_url` are
  optional.
- Extend loader, validator, persistence (new `requirements` table plus new
  `standards` metadata columns, migration `008`), the GitOps importer, the read
  store, and the web read surface (`GET /api/v1/freeboard/requirements`, standard
  metadata on the standards read, a `requirements` count in `compliance/status`)
  to carry the new kind and metadata.
- Author a real CE+ fixture: a `std-cyber-essentials-plus` Standard plus the full
  CE+ requirement set across all five technical control themes (Firewalls, Secure
  Configuration, Security Update Management, User Access Control, Malware
  Protection) - 35 requirements, faithfully paraphrased from the publicly
  published NCSC/IASME "Requirements for IT Infrastructure v3.3" (not copied
  verbatim). It ships as example GitOps YAML and as reusable test fixtures.
- Upgrade the existing placeholder standards (`std-cyber-essentials`, `std-soc2`)
  with honest real `version`/`authority` metadata, because those fields are now
  required. No metadata is fabricated.
- Repoint `Control.maps_to` from `Standard` ids to `Requirement` ids: a control
  now maps to the specific requirements it satisfies, not to a whole standard (see
  design D2). This replaces the `control_standards` join with a
  `control_requirements` join and updates validation, the importer, the read model,
  the web `GET /controls` output, and the example controls. The stale doc comment
  that calls a Control "a requirement under one or more standards" is reworded now
  that a distinct `Requirement` kind exists.

This is pre-1.0 (`freeboard.io/v1alpha1`). Migration `008` is partly additive and
partly a join-table replacement: the new nullable `standards` metadata columns and
the new `requirements` and `control_requirements` tables are additive, but the
repoint also drops `control_standards` (its rows are not migrated; the join is
rebuilt on the next `gitops sync`). See design D5/D6 and the Migration Plan.
Making `version`/`authority` required is a config-validation change: pre-existing
thin `Standard` YAML that omits them now fails validation with a clear
diagnostic, and pre-migration DB rows read back with null metadata until they are
re-synced from a config that supplies the fields.

## Capabilities

### New Capabilities

None. This change follows the repo's layer-oriented capability convention
(`gitops-config-format`, `compliance-persistence`, `compliance-web-read`) exactly
as the Organisation and Scope kinds did: the new kind extends the same layers
rather than introducing a feature-oriented capability. Adding a
`standard-requirements` capability would duplicate those layers and add spec
surface with no distinct owner, against code-as-liability.

### Modified Capabilities

- `gitops-config-format`: adds the `Requirement` kind and the `Standard` metadata
  (required `version`/`authority`, optional `publisher`/`source_url`) to the
  schema, loader routing, and validation rules, and repoints `Control.maps_to` to
  resolve against `Requirement` ids.
- `compliance-persistence`: adds a `requirements` table (with a `standard_id`
  foreign key) and `standards` metadata columns via migration `008`, drops the
  `control_standards` join and adds a `control_requirements` join, and extends the
  importer order, read store, read models, and counts.
- `compliance-web-read`: adds the requirements read endpoint, standard metadata
  on the standards read, `requirement` ids in the controls read `maps_to`, and a
  `requirements` count in the status summary.

## Impact

- Code: `Freeboard.Core/GitOps` (`ConfigModel`, `ConfigLoader`,
  `ConfigValidator`); `Freeboard.Persistence` (`ComplianceReadModels`,
  `MySqlComplianceStore`, `IComplianceStore`, `GitOps/ImportPlan`,
  `GitOps/MySqlGitOpsImporter`, `Migrations/008_*.sql`); `Freeboard`
  (`Compliance/ComplianceEndpoints`); `Freeboard.CLI` (`GitOpsCommands` summary
  output); web test double (`FakeComplianceStore`).
- Data: new `requirements` table; new nullable `standards.version`,
  `standards.authority`, `standards.publisher`, `standards.source_url` columns;
  `control_standards` dropped and replaced by a `control_requirements` join.
  Applied via `freeboard system migrate` (the web app never auto-migrates).
- Licensing: MIT. This is core compliance business logic and lives only in
  `Freeboard.Core`, `Freeboard.Persistence`, and the web app. Nothing goes in
  `Freeboard.Enterprise`; `Freeboard.Agent` and `Freeboard.CLI` stay EE-free.
- Fixtures: `examples/gitops/` gains the CE+ standard and requirements and
  upgrades the placeholder standards with metadata; test fixtures under `tests/`
  gain the same set for loader/validator, persistence integration, and web double
  tests.

## Non-goals

- No change to the Statement of Applicability. SoA stays a per-standard
  organisation-tree projection over scopes; it reads organisations and scopes, not
  controls, so repointing `Control.maps_to` does not touch it.
- No new HTML standard-detail/requirements screen. This change adds only the JSON
  read surface and counts; a rendered requirements view is deferred.
- No change to app-managed writes (`compliance-write`). Requirements are authored
  through GitOps only, like the other kinds.
- No conformance claim. The CE+ fixture is a faithful, plainly worded rendering
  of the publicly published CE scheme requirements, not a certification.
