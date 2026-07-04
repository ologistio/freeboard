## Context

The compliance model in `Freeboard.Core/GitOps/ConfigModel.cs` has five kinds:
`Standard` (id, title, version, authority, publisher, source_url), `Requirement`
(belongs to one standard: theme, statement, guidance, citation), `Control`
(`maps_to` requirement ids), `Organisation` (recursive tree, Company/Department,
parent pointer), and `Scope` (binds one organisation to one standard with a
disposition `In`/`Out`, unique per `(organisation, standard)`).

The Statement of Applicability (`src/Freeboard/Compliance/StatementOfApplicability.cs`,
MIT web code) resolves each organisation node's disposition for a standard by
nearest-ancestor inheritance: `Resolve(organisations, scopes, standardId)` returns
`SoaNode(Id, Title, Kind, Parent, Disposition, Resolution)` where `Resolution` is
`Explicit`, `Inherited`, or `Undetermined`. `ResolveNode` returns the node's own
scope if present, else the nearest ancestor's, else `Undetermined`.

`Freeboard.Persistence` (MIT, Dapper + MySqlConnector) persists the domain: a read
store (`IComplianceStore`), a write store (`IComplianceWriteStore`, PUT/DELETE
upserts used by the web write endpoints under the admin policy), a GitOps importer
(`IGitOpsImporter`, one FK-safe transaction, prune-then-upsert for scopes), and
forward-only checksum-tracked migrations (`001..008`, next is `009`). Migration
`007` created the `scopes` table with `UNIQUE KEY (organisation_id, standard_id)`
and FKs to `organisations` and `standards` both `ON DELETE RESTRICT`; migration
`008` added the `requirements` table with a `standard_id` FK `ON DELETE RESTRICT`.

This change adds requirement-level scoping: an organisation node can mark a specific
`Requirement` `In` or `Out`, layered under the standard-level `Scope` and resolved
with the same nearest-ancestor rule.

## Goals / Non-Goals

**Goals:**

- A `RequirementScope` kind that binds one `Organisation` to one `Requirement` with
  a disposition `In`/`Out`, unique per `(organisation, requirement)`, authored like
  `Scope` and consistent with existing patterns (id identity, binary collation,
  FK-safe import, deterministic ordered reads).
- Requirement-level nearest-ancestor resolution keyed by `(organisation,
  requirement)`, layered under the standard result.
- Persistence, importer, read-store, read/write web surface, CLI summary, and SoA
  projection support for the new kind.
- Example and test fixtures showing a company-wide exclusion and a department
  re-include.

**Non-Goals:**

- Re-including a requirement whose standard resolves `Out` or `Undetermined` (D3).
- A `standard` field on `RequirementScope` (D2).
- Control-coverage/gap analysis, a bespoke exclusions HTML editor, or any change to
  `Scope`, `Requirement`, or `Control.maps_to`.
- Any EE placement. This is MIT core compliance logic.

## Plan synthesis and provenance

This design merges two independent plans (A and B) and one binding mediator
decision. It records the origin of each idea so a reviewer can trace it.

Shared by both plans (converged independently):

- A distinct first-class requirement-level resource rather than overloading the
  standard-level `Scope` (Plan A "RequirementScope kind"; Plan B "RequirementScope
  resource"). Kept - see D1.
- Standard-level `Scope` stays the gate. Requirement resolution is nested under the
  standard: standard `In` -> resolve requirement layer; standard `Out` -> every
  requirement `Out`; standard `Undetermined` -> every requirement `Undetermined`
  (Plan A D3; Plan B "standard-level scope is the gate"). Kept - see D3.
- Nearest-ancestor inheritance for requirement-scopes over the org tree (both). Kept.
- A new forward-only migration (`009`), never editing `008` (Plan A D7; Plan B H-3).
  Kept.
- Prune requirement-scopes before deleting organisations/requirements in the
  importer (Plan A D8; Plan B H-4 importer half). Kept - see D8.
- Do not extend `Scope` with a nullable `requirement` column, to avoid the MySQL
  NULL-in-unique-key hole (Plan A D1 rationale; Plan B H-1). Kept - see D1.

Divergences and resolutions:

1. Disposition semantics. Plan A proposed symmetric `In`/`Out` with child
   re-include. Plan B proposed `Out`-only, no re-include. RESOLVED by the binding
   mediator decision in favour of symmetric `In`/`Out` with child override, matching
   the existing standard-level `Scope` model. Plan A's approach is kept unchanged;
   Plan B's `Out`-only is dropped. See D4, and the resolution and validation
   scenarios in the statement-of-applicability and organisation-model specs.
2. Whether the config kind carries an explicit `standard` field. Plan A omits it
   (config key `(organisation, requirement)`, standard derived from the
   requirement). Plan B includes it (key `(organisation, standard, requirement)`,
   validated to match `Requirement.standard`). RESOLVED in favour of Plan A: no
   `standard` field on the authored kind, and - decided here explicitly - no derived
   `standard_id` column in storage either. See D2 (config shape and storage shape)
   and D7. Plan B's M-1 "validate standard matches requirement" check is therefore
   moot: with no authored `standard` there is nothing to cross-check.
3. SoA projection shape. Plan A projects only per-node deviations (compact,
   exclusions-oriented). Plan B leaned toward a dense node x requirement matrix but
   flagged its payload cost (M-2). RESOLVED in favour of Plan A's compact shape; Plan
   B's own M-2 size concern is the deciding reason. See D5.

Constraints contributed by Plan B and folded in:

- H-2: a requirement-scope must never turn a standard `Undetermined` into `Out`.
  Preserved by D3 (requirement-scopes are consulted only under a standard that
  resolves `In`) and pinned by the "Requirement is undetermined when the standard is
  undetermined" scenario.
- H-4 (write path half): deleting an organisation still referenced by a
  requirement-scope must fail cleanly, not crash on the RESTRICT foreign key. The
  existing `DeleteOrganisationAsync` already pre-checks child organisations and
  scopes; the requirement-scope check joins it. See D10. Requirements are not
  deletable through the write path (no requirement write endpoint), so the
  requirement half of H-4 applies only to the importer (D8).
- The full verification matrix (Plan B) is reflected in tasks sections 2, 4, 7, 8,
  and 10.

## Decisions

### D1. A distinct `RequirementScope` kind, not an extension of `Scope`

Add a new kind `RequirementScope` rather than adding an optional `requirement`
field to the existing `Scope`.

Rationale:

- Uniqueness. `Scope` enforces one disposition per `(organisation, standard)` via a
  SQL `UNIQUE KEY (organisation_id, standard_id)`. Overloading `Scope` with a
  nullable `requirement_id` would move the key to `(organisation_id, standard_id,
  requirement_id)`, and SQL unique keys treat NULL as distinct - two
  standard-level rows (NULL requirement) for the same `(organisation, standard)`
  would both be allowed, silently defeating the "at most one standard-level scope"
  invariant. A separate table keeps each invariant enforceable with a clean NOT
  NULL composite unique key.
- Two-layer resolution. Standard-level and requirement-level scopes resolve at
  different granularities and the requirement layer sits UNDER the standard layer
  (D3). Two kinds keep the layering explicit rather than a self-referential
  nullable-column projection that a reader must mentally split.
- Precedent. The last change chose a distinct `Requirement` kind over overloading
  `Control` for the same reasons (clear identity, simple FK, simple validation).
  `RequirementScope` follows that pattern.

Alternative considered: extend `Scope` with an optional `requirement`. Rejected for
the NULL-uniqueness hole and the resolution-layering muddle. The small cost of a new
kind (one SchemaKeys entry, one validation phase, one table) is paid once and keeps
both invariants and both resolution layers clean.

Naming: it is a scope at requirement granularity, so `RequirementScope` is the
honest name and mirrors `Scope`. "Exclusion" is the dominant authoring action
(`disposition: Out`), but the kind is symmetric (D4), so it is named for what it is,
not only its common use.

### D2. Fields: organisation + requirement + disposition; no `standard` field

`RequirementScope` fields mirror `Scope` with `requirement` replacing `standard`:

| YAML key       | Property      | Required | Notes                                     |
| -------------- | ------------- | -------- | ----------------------------------------- |
| `apiVersion`   | ApiVersion    | yes      | camelCase, `freeboard.io/v1alpha1`        |
| `kind`         | Kind          | yes      | discriminator `RequirementScope`          |
| `id`           | Id            | yes      | immutable identity                        |
| `title`        | Title         | yes      | short display label                       |
| `organisation` | Organisation  | yes      | `Organisation` id                         |
| `requirement`  | Requirement   | yes      | `Requirement` id                          |
| `disposition`  | Disposition   | yes      | `In` or `Out` (D4)                        |

There is deliberately NO `standard` field. A `Requirement` already names its owning
`Standard` (`Requirement.standard`), so the standard is derivable and restating it
would introduce a class of inconsistency (a `RequirementScope` naming a standard the
requirement does not belong to). The storage uniqueness key is therefore
`(organisation, requirement)`, which is equivalent to the `(organisation, standard,
requirement)` the task describes, because a requirement determines its standard.
When the SoA projects a given `standardId`, it selects requirement-scopes whose
requirement belongs to that standard (an in-memory filter over
`Requirement.standard`, mirroring how `Resolve` already filters scopes by
`standardId`).

Config shape vs storage shape. This decision is about two things that could differ:
the authored config kind and the persisted table. The authored kind carries no
`standard` (above). The persisted `requirement_scopes` table ALSO carries no derived
`standard_id` column: the row holds `organisation_id`, `requirement_id`, and
`disposition`, and the `requirement_id` FK to `requirements` is the only referential
tie. Per-standard SoA queries do not need a `standard_id` column because the SoA
endpoint already loads all requirements and filters them to the requested `standardId`
in memory, then filters requirement-scopes to that requirement set - exactly how
`Resolve` already filters standard-level scopes by `standardId`. A denormalised
`standard_id` would have to be kept equal to the requirement's own `standard_id` on
every importer and write-path path, adding a drift invariant and a redundant FK for a
filter the read already does in memory at negligible cost. Referential integrity to
standards is transitive and automatic: a requirement-scope RESTRICTs on its
requirement, and a requirement RESTRICTs on its standard, so a standard cannot be
deleted while any requirement-scope indirectly depends on it.

Alternative considered (Plan B): carry an explicit `standard` on the kind (key
`(organisation, standard, requirement)`) and/or a `standard_id` column, for symmetry
with `Scope` and per-standard query convenience. Rejected: on the kind it is redundant
(the requirement fixes the standard) and adds a cross-consistency invariant to
validate; in storage it adds a denormalisation drift invariant and a redundant FK. The
`(organisation, requirement)` key is equivalent to `(organisation, standard,
requirement)` because a requirement determines its standard.

### D3. Resolution: requirement layer sits under the standard layer

For an organisation node N and a requirement R (owned by standard S), the effective
disposition is computed in two steps:

1. `standardResolution = Resolve(N, S)` - the existing nearest-ancestor scope
   resolution (`In`/`Out`/`Undetermined`).
2. If `standardResolution` is `Undetermined` -> R is `Undetermined` (nothing to
   exclude from an unscoped standard).
   If `standardResolution` is `Out` -> R is `Out` (the whole standard, and thus every
   requirement, is out; requirement-level scopes are NOT consulted).
   If `standardResolution` is `In` -> consult the requirement layer: the
   nearest-ancestor `RequirementScope` for `(N, R)` gives `explicit`/`inherited`
   `In` or `Out`; if none exists in N's ancestry, R follows the standard and is `In`.

The requirement-layer walk is identical to `ResolveNode` but keyed by
`(organisation, requirement)`: N's own `RequirementScope` for R wins (`explicit`);
else the first ancestor with one (`inherited`); else none. It only APPLIES when the
standard resolves `In` at N.

This directly answers "how a requirement resolves when its standard resolves Out or
Undetermined": the standard dominates. A requirement-level `In` cannot re-include a
requirement whose standard is `Out` - that would assert a requirement is in scope
while its standard is not, which is incoherent. Child override still works within an
`In` standard: a company sets R `Out` company-wide, a department sets R `In`, and at
the department the nearest requirement-scope is its own explicit `In`, overriding the
inherited company `Out`.

Alternative considered: let a requirement-level `In` re-include a requirement even
when the standard is `Out`. Rejected as incoherent (a requirement cannot be in scope
under an out-of-scope standard) and because it would make the two layers independent
rather than nested, complicating every consumer.

### D4. Disposition is symmetric `In`/`Out`, not `Out`-only

`RequirementScope.disposition` reuses the existing `ScopeDisposition { In, Out }`
enum and its `TryParseDisposition` parser.

Rationale: symmetry is what enables child override (D3). A department re-includes a
requirement its parent excluded company-wide by asserting `In`, resolved by the same
nearest-ancestor rule. `Out`-only could not express re-inclusion and would still need
inheritance (to know whether an ancestor excluded), so it saves no machinery while
losing a capability. The dominant authoring action is `Out` (exclude), but `In` is
the override primitive. Reusing `Scope`'s enum, resolution walk, and validation keeps
the code surface minimal.

Alternative considered: an `Out`-only exclusion list. Rejected: asymmetric with
`Scope`, cannot express re-inclusion, and no simpler to resolve.

### D5. SoA projection: extend the existing per-standard read

The Statement of Applicability is genuinely a per-requirement applicability
statement, so per-requirement resolution belongs on the existing per-standard
projection, not a second read.

`Resolve` gains two inputs and its node DTO gains a requirement list:

```csharp
public static IReadOnlyList<SoaNode> Resolve(
    IReadOnlyList<OrganisationRow> organisations,
    IReadOnlyList<ScopeRow> scopes,
    IReadOnlyList<RequirementRow> requirements,
    IReadOnlyList<RequirementScopeRow> requirementScopes,
    string standardId)

public sealed record SoaRequirementResolution(
    string Requirement, string Disposition, SoaResolution Resolution);

// SoaNode gains: IReadOnlyList<SoaRequirementResolution> Requirements
```

For each node whose standard resolves `In`, the projection lists only the
requirements (of that standard) whose `(node, requirement)` nearest-ancestor walk
finds a `RequirementScope` - i.e. the deviations, each tagged `explicit`/`inherited`
and `In`/`Out`. Requirements with no requirement-scope in the node's ancestry are NOT
listed; a consumer reads them as following the node's standard disposition (`In`).
Nodes whose standard resolves `Out` or `Undetermined` carry an empty list
(requirement scopes are not applied there, D3).

This keeps the payload proportional to the number of exclusions rather than
nodes x requirements (35 CE+ requirements x every org node), and matches the
"exclusions" framing. The default-following rule (an unlisted requirement follows the
node's standard disposition) is stated in the spec so the projection stays
unambiguous and self-contained. `Resolve` stays pure (no I/O) and unit-testable; the
endpoint supplies the four collections from the store.

The SoA has two consumers: the JSON endpoint
(`GET /statement-of-applicability/{standardId}`) and the Razor page model
(`Pages/Compliance/StatementOfApplicability.cshtml.cs`). Both read their four inputs -
organisations, scopes, requirements, and requirement-scopes - in a single
repeatable-read snapshot via a new `IComplianceStore` method
`GetStatementOfApplicabilityInputsAsync` (D9), then filter requirements to
`standardId` in memory and pass all four collections to `Resolve`. Reading them in
one transaction, rather than separate `Get...Async` calls, keeps the four
collections from straddling a concurrent `gitops sync` commit (e.g. pairing old
organisations with new requirement-scopes) - the same reason `GetControlsAsync` already
reads controls and their `control_requirements` join under a single repeatable-read
transaction.

Today only the endpoint calls the pure `Resolve`; the page model
(`StatementOfApplicabilityModel.OnGetAsync`) does its own separate
`GetOrganisationsAsync` + `GetScopesAsync` reads and then calls `Resolve`. Both
consumers move to `GetStatementOfApplicabilityInputsAsync` so they share one snapshot
method and one repeatable-read read, rather than the page keeping a second,
separately-read projection path that could pair inputs from different importer commits.
The page still reads `GetStandardsAsync` separately for its standard selector (the
combined read is scoped to the projection inputs, not the selector list). The
endpoint/page auth and read-only behaviour are unchanged (same route, GET-only, served
in read-only mode; a store outage still maps to 503 for the endpoint and to the
in-page unreachable notice for the page).

The compact deviations-only shape is chosen over a dense per-node x per-requirement
matrix. A matrix grows as organisations x requirements (35 CE+ requirements times
every org node) and Plan B flagged that growth (its M-2 payload-size concern); the
compact shape makes that concern the deciding factor. Node output is deterministically
ordered by `id` and each node's per-requirement list by requirement `id`, so the
projection is stable for consumers and diffs.

Alternative considered: a dense per-requirement matrix per node (Plan B). Rejected on
the M-2 payload-size ground above; the compact shape carries the same information (an
unlisted requirement follows the node's standard disposition).

Alternative considered: a separate `GET /requirement-exclusions/{standardId}` read.
Rejected: it duplicates the organisation-tree walk and splits one SoA into two reads.

### D6. Raw resource read and status count

Mirroring how every kind is exposed, add `GET /api/v1/freeboard/requirement-scopes`
returning the raw persisted rows `{ id, title, organisation, requirement,
disposition }` ordered by id, and add a `requirementScopes` count to the
`GET /compliance/status` `persisted` object. The count is APPENDED after the existing
keys (`standards, controls, requirements, organisations, scopes, requirementScopes`)
so no existing key order changes, and it degrades to `null` on an unreachable store
like the others. This raw read is additive and follows the established resource-read
pattern (auth-required, GET-only, 503 on outage).

### D7. Persistence: `requirement_scopes` table, migration `009`

`009_requirement_scopes.sql` (forward-only, binary collation on identifiers), the
next ordinal after `008`. Purely additive - no existing table altered:

- `CREATE TABLE IF NOT EXISTS requirement_scopes (id VARCHAR(190) utf8mb4_bin PK, api_version
  VARCHAR(64) NOT NULL, title VARCHAR(512) NOT NULL, organisation_id VARCHAR(190)
  utf8mb4_bin NOT NULL, requirement_id VARCHAR(190) utf8mb4_bin NOT NULL, disposition
  <same definition as scopes.disposition> NOT NULL, created_at DATETIME(6) NOT NULL,
  updated_at DATETIME(6) NOT NULL, UNIQUE KEY uq_requirement_scopes_organisation_requirement
  (organisation_id, requirement_id), KEY ix_requirement_scopes_requirement_id
  (requirement_id), CONSTRAINT fk_requirement_scopes_organisation FOREIGN KEY
  (organisation_id) REFERENCES organisations (id) ON DELETE RESTRICT, CONSTRAINT
  fk_requirement_scopes_requirement FOREIGN KEY (requirement_id) REFERENCES
  requirements (id) ON DELETE RESTRICT) ENGINE=InnoDB;`

`ON DELETE RESTRICT` on both FKs matches the `scopes` table (which RESTRICTs on both
`organisations` and `standards`): the importer prunes referencing rows before
deleting an organisation or requirement rather than relying on cascade. `id`,
`organisation_id`, and `requirement_id` use `utf8mb4_bin` for exact-byte identity,
consistent with Core. The unique key is `(organisation_id, requirement_id)` (D2). The
disposition column copies the `scopes.disposition` column definition exactly so both
tables store the enum identically.

The table deliberately has no `standard_id` column (D2): the standard is derived from
the requirement, per-standard SoA filtering is done in memory over `Requirement.standard`,
and standard referential integrity is transitive through the requirement FK. There is
therefore no `standards` FK on this table and no `standard_id` prune step.

### D8. Importer order and plan

`ImportPlan` gains `RequirementScopeRowPlan(Id, ApiVersion, Title, Organisation,
Requirement, Disposition)`, a `RequirementScopes` list flattened from
`config.RequirementScopes`, and `RequirementScopeIds`. The importer mirrors the
`scopes` prune-then-upsert exactly, placed immediately after the existing scope step:

1. Upsert standards, requirements, controls, organisations (unchanged).
2. Prune absent scopes, then upsert scopes (unchanged).
3. Prune absent requirement-scopes, then upsert requirement-scopes. Prune-before-
   upsert preserves rename safety for a same-`(organisation, requirement)` row (the
   unique key is freed before the new id is inserted), exactly as for scopes.
   Requirement-scopes reference organisations and requirements, both already upserted.
4. Replace `control_requirements` join rows (unchanged).
5. Delete absent domain rows FK-safe (unchanged): because requirement-scopes RESTRICT
   on organisations and requirements, and step 3 has already pruned absent
   requirement-scopes, the later absent-organisation and absent-requirement deletes
   do not hit the RESTRICT FK.

`ImportPlan` stays pure (no DB), so the ordering and flattening remain unit-testable
without MySQL. `UpsertRequirementScopesAsync` copies `UpsertScopesAsync` (INSERT ...
ON DUPLICATE KEY UPDATE), and `DeleteAbsentAsync` is reused for the prune.

### D9. Read store, read models, counts

- New read model `RequirementScopeRow(Id, Title, Organisation, Requirement,
  Disposition)`.
- `ComplianceCounts` gains `RequirementScopes` in positional order `(Standards,
  Controls, Requirements, Organisations, Scopes, RequirementScopes)`. Every
  `new ComplianceCounts(...)` call site (store, integration test, web fake) is
  updated for the widened record.
- `IComplianceStore` gains `GetRequirementScopesAsync`; the counts query adds
  `(SELECT COUNT(*) FROM requirement_scopes)`. `GetRequirementScopesAsync` selects
  `id, title, organisation_id, requirement_id, disposition` ordered by `id`, mirroring
  `GetScopesAsync`.
- `IComplianceStore` also gains `GetStatementOfApplicabilityInputsAsync`, returning the
  four SoA inputs together in one repeatable-read transaction: a
  `SoaInputs(IReadOnlyList<OrganisationRow> Organisations, IReadOnlyList<ScopeRow> Scopes,
  IReadOnlyList<RequirementRow> Requirements, IReadOnlyList<RequirementScopeRow>
  RequirementScopes)` read model. The MySQL implementation mirrors `GetControlsAsync`:
  open one connection, `BeginTransactionAsync(IsolationLevel.RepeatableRead)`, run the
  four ordered-by-`id` selects on that transaction, then commit. This gives both SoA
  consumers - the JSON endpoint and the Razor page model - one consistent snapshot so
  their inputs cannot straddle a concurrent importer commit. The raw resource reads
  (`/organisations`, `/scopes`, `/requirement-scopes`) keep their individual
  single-select methods; both SoA consumers use the combined read (D5).
- Reads stay deterministically ordered by `id` (ordinal/binary).

### D10. App-managed writes

`IComplianceWriteStore` gains `UpsertRequirementScopeDispositionAsync(id, title,
organisation, requirement, disposition)` and `DeleteRequirementScopeAsync(id)`,
copying the scope write methods. The MySQL implementation validates id/title
non-blank, parses the disposition via `ConfigValidator.TryParseDisposition`, checks
the organisation and requirement exist, and enforces one row per `(organisation,
requirement)` (select the existing id for the pair; fail if a different id holds it),
then upserts in a per-write transaction. `ComplianceWriteEndpoints` gains
`PUT /requirement-scopes/{id}` and `DELETE /requirement-scopes/{id}` with a
`RequirementScopeInput(Id, Title, Organisation, Requirement, Disposition)` record,
under the same admin policy as the scope writes, so the GitOps read-only middleware
409s them in read-only mode and a `23000` unique-key violation surfaces as 409. This
reuses the existing write plumbing (`WriteResult`, `RunAsync`, error mapping) with one
adjustment: the shared `Conflict()` problem detail is scope-specific today ("A scope
already maps this organisation and standard."), so a requirement-scope 409 would report
scope wording. Generalise that detail so it is not scope-specific - either a
kind-neutral message (e.g. "The write conflicts with an existing record.") or one keyed
to the resource being written - so a requirement-scope conflict does not report a
scope-only message. `RunAsync`'s catch block runs for every write route, so it must not
assume the resource is a scope. A plain pre-check duplicate still returns 422 via
`WriteResult.Fail` (D1/D2 wording); only the concurrent-race path and read-only mode
reach 409 (matching the existing scope write behaviour).

`DeleteOrganisationAsync` gains a requirement-scope pre-check (H-4 write path).
Requirement-scopes RESTRICT on `organisations`, so deleting an organisation still
referenced by a requirement-scope would otherwise fail on the foreign key. The method
already pre-checks child organisations and referencing scopes and returns a clean
`WriteResult.Fail`; it adds a third check, `SELECT COUNT(*) FROM requirement_scopes
WHERE organisation_id = @Id`, failing with "Cannot delete an organisation that still
has requirement-scopes." rather than surfacing a raw FK error. Requirements are not
deletable through the write path (no requirement write endpoint), so no analogous
requirement-delete guard is needed here; the importer covers requirement removal (D8).

### D11. Loader and validator

- `ConfigModel.cs`: add `RequirementScope` record and `GitOpsSchema.KindRequirementScope
  = "RequirementScope"`; add `RequirementScopes` to `GitOpsConfig`.
- `ConfigLoader.cs`: add a `RequirementScope` `SchemaKeys` entry (apiVersion, kind,
  id, title, organisation, requirement, disposition); add the `apiVersion` alias
  override for `RequirementScope`; route `KindRequirementScope` in the switch; add
  `RequirementScope` to the unknown-kind error enumeration.
- `ConfigValidator.cs`: add `ValidateRequirementScopes(config, organisationIds,
  requirementIds, diagnostics)` after `ValidateScopes` (it needs both the
  organisation id set from `ValidateOrganisations` and the requirement id set from
  `ValidateRequirements`). Rules mirror `ValidateScopes`: apiVersion check; required
  id/title/organisation/requirement/disposition; organisation resolves; requirement
  resolves; disposition in `In`/`Out`; duplicate id rejected; duplicate
  `(organisation, requirement)` pair rejected. The loader/validator still never throw
  or print.

### D12. CLI output

`GitOpsCommands` summary/sync counts gain `{RequirementScopes.Count}
requirement-scope(s)`, and `PrintPlannedState` gains a `RequirementScopes (n):`
section printing `  - {Id}: {Title} -> {Organisation} / {Requirement} =
{Disposition}`, mirroring the `Scopes` section. Output text only; no new command and
no spec-level behaviour change. The CLI stays EE-free and cross-platform (Core +
Persistence only).

### D13. Fixtures

`examples/gitops/scopes.yaml` gains a standard-level scope binding `ologist-products`
to `std-cyber-essentials-plus` with disposition `In`, so the CE+ requirement-scopes
have an `In` standard to sit under. `examples/gitops/requirement-scopes.yaml` adds two
documents: a company-wide exclusion (`ologist-products` marks a CE+ requirement `Out`)
and a department re-include (`ologist-products-eng` marks the same requirement `In`),
demonstrating both the exclusion and the child-override path (D3). `README.md` and
`docs/gitops.md` document the `RequirementScope` kind and the new file. Each test tier
gets a small, layer-appropriate fixture in its established style (Core inline YAML,
Persistence domain builders, Web read-model rows); no shared cross-tier fixture module
is added (it would cross the reference graph for negligible gain).

## Risks / Trade-offs

- [Two-layer resolution confusion] Requirement resolution depends on both the standard
  resolution and a second nearest-ancestor walk. -> The spec pins the precedence
  (standard `Out`/`Undetermined` dominates; requirement scopes apply only under an
  `In` standard) with scenarios for each branch, and `Resolve` stays a pure function
  covered by unit tests for company-wide exclude, department re-include, exclude under
  an `Out` standard (ignored), and exclude under an `Undetermined` standard (ignored).
- [Projection payload size] A naive per-node x per-requirement matrix would be large.
  -> The projection lists only deviations (requirements with an explicit/inherited
  requirement-scope) and only for `In` nodes; unlisted requirements follow the node's
  standard disposition. Payload is proportional to the number of exclusions.
- [NULL-uniqueness hole if `Scope` were overloaded] -> Avoided by a distinct table
  with a NOT NULL `(organisation_id, requirement_id)` unique key (D1/D7).
- [FK deletion ordering] Requirement-scopes RESTRICT on organisations and
  requirements, so a bad prune order would fail import when an organisation or
  requirement is removed. -> Prune requirement-scopes before deleting absent
  organisations and requirements (D8), covered by an integration test that removes an
  organisation and a requirement that had a requirement-scope in the prior state.
- [Rename safety] A requirement-scope whose `id` is renamed while keeping its
  `(organisation, requirement)` pair must not collide on the unique key. ->
  Prune-then-upsert (mirrors scopes), covered by a rename regression test.
- [Wire additivity] Adding `requirementScopes` to `persisted` and a `requirements`
  list to each SoA node changes response shapes. -> Additive only, pre-1.0; existing
  consumers ignore unknown fields. No key is removed or renamed.
- [Standard-level scope missing for CE+ in the example] The existing example scopes an
  org to `std-cyber-essentials`, not CE+. -> The fixture adds a CE+ standard-level
  `In` scope so the requirement-scopes resolve under an `In` standard and the SoA
  demonstrates exclusions.

## Migration Plan

1. Ship code and `009_requirement_scopes.sql`.
2. Operators run `freeboard system migrate` (web never auto-migrates). `009` creates
   the `requirement_scopes` table; no existing table is touched.
3. `freeboard gitops sync` imports requirement-scopes (from the CE+ fixture or any
   config) into the new table. Configs with no `RequirementScope` documents import
   exactly as before (the new importer step is a no-op prune of an empty set).
4. Rollback: pre-1.0, roll back by deploying the prior build and, if needed, dropping
   the `requirement_scopes` table. The table is additive, so a prior build that never
   references it keeps working; no data contract is owed.

## Open Questions

1. Example exclusion choice: the fixture excludes one CE+ requirement company-wide and
   re-includes it for the engineering department. Confirm the chosen requirement id
   and org ids match the intended example story once `organisations.yaml` ids are
   final.
