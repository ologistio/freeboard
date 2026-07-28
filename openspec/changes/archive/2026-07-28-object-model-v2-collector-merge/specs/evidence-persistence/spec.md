## MODIFIED Requirements

### Requirement: Migration adds collector identity and cadence to evidence runs

A forward-only migration applied by `freeboard system migrate` SHALL add to the existing
`evidence_runs` table a nullable `collector_id VARCHAR(190)` column with `utf8mb4_bin`
collation (recording the id of the collector that produced the run) and a nullable
`frequency VARCHAR(16)` column (recording that collector's cadence token). It SHALL NOT
add a new index; the existing `(organisation_id, requirement_id, collected_at)` index
already serves the batch read via its leftmost prefix and per-collector grouping is done
in the read store, so a further index would only add write cost to the append-hot table.
There SHALL be NO foreign key from `collector_id` to the collector table, before or after
the collector merge, so a recorded run survives deletion or gitops churn of the collector,
consistent with the scalar `organisation_id`/`requirement_id` columns; the collector-merge
migration SHALL NOT add one.

The migration SHALL be purely additive: a single `ALTER TABLE` adding the two nullable
columns, matching the repo idiom (existing migrations use bare `ADD COLUMN`). It SHALL NOT
backfill any row, SHALL NOT run any `UPDATE` against `evidence_runs`, and SHALL NOT drop
or re-create the append-only `trg_evidence_runs_no_update` BEFORE UPDATE trigger, so the
`evidence_runs` append-only integrity guarantee is preserved through the migration.
Pre-migration runs (and any run appended without a cadence) SHALL therefore read back null
for both new columns; they are never retroactively flagged stale. This is a deliberate,
accepted forward-only limitation: staleness covers only evidence collected after this
migration ships, and a collector that had already stopped keeps its last verdict because
it never reports again to record a cadence.

Because the migration runner runs the migration SQL and only then records the version (the
two steps are not transactionally atomic) and plain `ADD COLUMN` is not idempotent, the
migration is NOT atomically replay-safe: a crash after the `ALTER` commits but before the
version is recorded makes a re-run fail on the duplicate column. The migration header SHALL
document the operational recovery: drop the partially-added columns and re-run, or record
the migration version by hand. This code SHALL live in the MIT `Freeboard.Persistence`
project and SHALL NOT add any reference to `Freeboard.Enterprise` or any new dependency.

#### Scenario: Migration adds the nullable identity and cadence columns

- **WHEN** the migration runs against a database with the evidence migration applied
- **THEN** the `evidence_runs` table gains nullable `collector_id` and `frequency`
  columns

#### Scenario: A pre-migration run keeps null identity and cadence

- **WHEN** the migration runs and a pre-migration collector run already exists
- **THEN** the run's new `collector_id` and `frequency` columns are both null (no
  backfill), its `result`, `collected_at`, `collector_ref`, and checks are unchanged, and
  it is never evaluated as stale

#### Scenario: The append-only guard is untouched by the migration

- **WHEN** the migration runs
- **THEN** the `trg_evidence_runs_no_update` BEFORE UPDATE trigger is never dropped, so a
  stray UPDATE against `evidence_runs` remains rejected throughout and after the migration

#### Scenario: The collector merge adds no foreign key to evidence runs

- **WHEN** the collector-merge migration completes
- **THEN** `evidence_runs.collector_id` still carries no foreign key, so a run whose
  collector was since deleted is retained and its collector delete is never blocked
