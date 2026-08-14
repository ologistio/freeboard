## Why

Most compliance read paths compose their authorization decision from two or more independent
store calls, each on its own connection and its own snapshot. A GitOps sync commits atomically
between them, so the inputs to one decision can straddle that commit. The accessible-asset set
is the shared cross-read input, so a stale asset list decides what a fresh row list may
disclose. A caller who may read `org-a` but not `org-b` can therefore still receive a vendor's
`Out` scope and its justification for one request after a sync moves that vendor to `org-b`.

The vendor register already draws its assurance rows and its asset list from one repeatable-read
snapshot, and the accessible-asset-set memo is already keyed by the asset list, so several
snapshots can coexist in one request with each decision narrowing by its own rows. That leaves
eight surfaces outside the guarantee. Three API endpoints straddle a commit: `GET /scopes`,
`GET /collectors`, and `GET /integration-connections`. So do the vendor register's scope read,
the collector register page, the integration-connection page, the scope-write stored-owner
lookup, and the ingest admission check.

They stay outside for one reason. The store exposes ten separate read methods, and a caller
composes a decision by calling several of them. Nothing in the API says which reads belong to
one decision, and nothing a reader or a test can inspect records which reads a decision drew
on, so every new read surface can reintroduce the defect unnoticed.

## What Changes

- **BREAKING (internal API)**: `IComplianceStore` loses its ten read methods and gains one,
  `GetSnapshotAsync(ComplianceReadSet sets, CancellationToken)`. A caller names the sets its
  decision needs and receives them from one repeatable-read snapshot. `GetCountsAsync` stays.
  `SoaInputs`, `SoaDrilldownInputs`, and `VendorAssuranceInputs` collapse into one
  `ComplianceSnapshot` record. One argument at the call site names the inputs of one decision,
  and the returned snapshot carries the set it was read with, so a test can assert what a
  surface drew on. This does not make the composed form impossible - the gate path still reads
  an assets-only snapshot, so a caller can still take two - and the design says so plainly.
- The snapshot is per decision, not per request. `AuthzRequestCache` memoizes the snapshots a
  request takes and serves a request whose sets a taken snapshot already covers, so surfaces
  that render together still share one read where their sets allow it.
- Several snapshots per request stay honest because the accessible-asset memo is already keyed
  by the principal AND the asset list it was resolved from. This change builds on that keying
  rather than introducing it, and the authorization gate path keeps reading the `assets` table
  alone: `AuthzRequestCache.GetAssetsAsync` names `ComplianceReadSet.Assets` and no payload set.
- One gate cannot keep that rule and takes a delta rather than an unstated exception. A scope
  write is authorized against the organisation owning the STORED row, which is only knowable by
  reading that row, so its snapshot names the assets and the scopes together. The delta narrows
  the ratified sentence to gates whose organisation is not derived from a stored row, and bounds
  the exception to the table holding the row the gate reads.
- `GET /organisations`, `GET /scopes`, `GET /collectors`, `GET /integration-connections`,
  `GET /statement-of-applicability/{standardId}`, the vendor register page, the collector
  register page, the integration-connection page, the two Statement of Applicability pages, the
  scope-write stored-owner lookup on all four of its call sites, and the evidence ingest
  admission check each draw their decision from one snapshot.
- A concurrency test races a read against a sync that reparents an asset across the caller's
  accessible boundary and proves the response comes wholly from one side of the commit. The
  guarantee ratified is that no decision MIXES two states of the domain, not that a read always
  reflects the latest committed one: a snapshot opened before a sync may answer from the
  pre-sync state after it commits, and that answer is correct.
- `freeboard vendor list` keeps its two HTTP reads. Two requests are two decisions and no
  server-side snapshot can join them, but each response is narrowed against its own snapshot and
  the CLI drops a scope whose vendor the vendor response did not carry, so the composition can
  lag by one sync and cannot disclose. That property is ratified and pinned by a test rather
  than left as an accident of the join.

## Capabilities

### New Capabilities

None. This change re-expresses reads the compliance capabilities already own.

### Modified Capabilities

- `compliance-persistence`: the read abstraction becomes one shape-parameterized snapshot
  method; the two Statement of Applicability snapshot requirements and the vendor assurance
  snapshot requirement are restated against it.
- `authz-enforcement`: one authorization decision SHALL draw its rows and its narrowing input
  from one snapshot; the shared-asset-read rule is narrowed to gates whose organisation is not
  derived from a stored row.
- `compliance-web-read`: every narrowed read endpoint takes its rows and its asset list from
  one snapshot.
- `compliance-write`: the stored-owner authorization lookup takes the scope row, the asset it
  resolves, and the ancestry chain it gates on from one snapshot, on the two DELETE selectors
  and the two PUT handlers alike.
- `vendor-register`: the register's snapshot gains the unified scopes, a snapshot is reused when
  a wider one already covers a later request, and the CLI listing's two-request composition is
  stated with the join property that keeps it safe.
- `evidence-ingest`: the admission checks draw the collector, the control, and the Statement
  of Applicability inputs from one snapshot.

## Impact

MIT. No code lands in `src/Freeboard.Enterprise`, and nothing outside it gains an EE
reference. The runtime change is confined to `src/Freeboard.Persistence` and `src/Freeboard`;
`src/Freeboard.CLI` gains a comment and a test, not a behaviour change.

- `src/Freeboard.Persistence`: `IComplianceStore`, `MySqlComplianceStore`,
  `ComplianceReadModels`.
- `src/Freeboard`: `Authz/AuthzRequestCache.cs`,
  `Compliance/ComplianceEndpoints.cs`, `Compliance/ComplianceWriteEndpoints.cs`,
  `Evidence/EvidenceIngestEndpoints.cs`, `Evidence/CollectorCredentialEndpoints.cs`,
  `Scheduler/CollectorSchedulerService.cs`, `Navigation/ShellNavResolver.cs`,
  `Web/OrgSelection.cs`, five Razor page models, and `Program.cs`.
- `src/Freeboard.CLI`: `VendorCommands.cs` gains a comment stating why its two reads stay two.
- Tests: all four hand-written `IComplianceStore` doubles (`FakeComplianceStore` and its two
  counting subclasses, plus the standalone counting double in `OrgSelectionTests`), their fault
  flags, about 120 call sites of the deleted methods across ten test files, and new coverage for
  the snapshot cache, the concurrent-sync race, and the CLI join.
- No database migration. No configuration key. No new dependency.

The collector register, integration connection, and Statement of Applicability capabilities
take NO delta. Each has surfaces this change touches, and none has a requirement this change
contradicts: they govern which field is narrowed and delegate the accessible-set definition to
the authorization enforcement capability, which is where the wording actually moves.

## Non-goals

- Pushing the accessible-set derivation into SQL. It duplicates the ancestry rules in a second
  language and is rejected in the design.
- Holding one transaction open across a whole request, or any cross-request read cache.
- Changing any endpoint's JSON, any page's markup, or any narrowing rule. What a caller may
  read is unchanged; only which read answers the question changes.
- Pulling an unnarrowed catalog read inside a narrowing snapshot - the standards beside the
  register and the two Statement of Applicability surfaces, or the controls beside the collector
  register. The design states why they stay outside.
- Changing how the accessible asset set is memoized, or which table the shared asset read draws
  on. Both are already in the shape this change needs and neither is touched.
- Any change to the GitOps writer, the importer transaction, or the migration runner.
