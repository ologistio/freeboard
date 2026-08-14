## Context

A compliance read narrows its rows by the caller's ACCESSIBLE ASSET set. That set is not a SQL
predicate. `MySqlComplianceStore` reads the whole `assets` table, and
`IAssetAccess.AccessibleAssetIdsAsync(user, assets, ct)` closes the caller's organisation union
over the `parent` and `owner` edges on those rows. Every narrowed surface then filters its rows
in C# against the resulting id set.

So one decision has two inputs: the rows, and the asset list that decides which of them the
caller may see. Today most surfaces read those two things with two separate calls, each on its
own connection and its own autocommit snapshot. `MySqlGitOpsImporter` runs a whole sync in one
transaction, so a commit can land between the two calls and pair pre-sync owner edges with
post-sync rows. The result is a combination that never existed in the database.

Three reads already avoid this by pairing the assets with their payload in one repeatable-read
transaction: `GetStatementOfApplicabilityInputsAsync`,
`GetStatementOfApplicabilityDrilldownInputsAsync`, and `GetVendorAssuranceInputsAsync`. The last
of those is held on `AuthzRequestCache` and serves the three assurance surfaces. The cache's
shared asset read is separate and reads the `assets` table alone, so no organisation gate reads a
payload table.

The accessible set is memoized per principal PER ASSET LIST, so two components each taking their
own snapshot each narrow with their own owner edges. That property is the premise every decision
below rests on. Without it, one snapshot would have to serve a whole request, and the union of
every surface's payload would land on every organisation gate.

What is left open, verified against the code rather than taken on trust:

| Surface | Rows | Asset list | Straddles |
| --- | --- | --- | --- |
| `GET /organisations` | assets | own autocommit read | no (one read) |
| `GET /scopes` | `GetScopesAsync` | own autocommit read | yes |
| `GET /collectors` | `GetCollectorsAsync` | own autocommit read | yes |
| `GET /integration-connections` | `GetIntegrationConnectionsAsync` | own autocommit read | yes |
| `GET /vendors` | assurance snapshot | same snapshot | no |
| `GET /statement-of-applicability/{id}` | flat snapshot | same snapshot | no |
| Vendor register page | assurance snapshot, plus `GetScopesAsync` | assurance snapshot | yes, on the scopes |
| Collector register page | `GetCollectorsAsync` | cache asset read | yes |
| Integration-connection page | `GetIntegrationConnectionsAsync` | cache asset read | yes |
| Statement of Applicability page | drill-down snapshot | same snapshot | no |
| Control detail page | drill-down snapshot | same snapshot | no |
| Nav rail badge | assurance snapshot | same snapshot | no |
| Organisation selector | cache asset read | same read | no |
| Scope write stored-owner lookup (two DELETE selectors, two PUT handlers) | `GetScopesAsync` | cache asset read | yes |
| Evidence ingest admission | three autocommit reads | none (machine credential) | yes, no disclosure |

The scope-write stored-owner lookup is the same defect on a write path. `StoredOrgSubjectAsync`
reads the stored scope row with `GetScopesAsync`, then resolves that row's `subject` against
`cache.GetAssetsAsync` to confirm it is an organisation asset, and that subject is the
organisation the write is authorized against. Four call sites use it, not one: the two DELETE
authorization selectors, and the two PUT handlers, which authorize the stored owner in-handler
when the body moves a row between organisations.

Constraints that shape the design:

- `IAssetAccess.AccessibleAssetIdsAsync` requires the UNFILTERED asset list. A caller cannot
  narrow before it calls.
- Resolving the set is a full pass over the asset tree with a per-node ancestry walk, and a
  page render asks for it at least twice. Memoization has to survive.
- `AuthzRequestCache` is registered SCOPED and is the request's one fact and asset cache. The
  authorizer, the write selectors, and the page guards all take their assets from it.
- Whatever the shared asset read pulls becomes an input to every organisation gate, so it reads
  the `assets` table alone. A payload table on that path fails every gated write closed when the
  schema does not have it, which is a rule the capability already ratifies. The one gate that
  cannot keep this - the scope write, whose organisation is only knowable from the stored row -
  is handled by a delta rather than by breaking the rule silently.
- `IsStoreFailure` catches `DbException`, `InvalidOperationException`, and `TimeoutException`
  and turns them into a 503 or an in-page notice. A programming error must not be catchable
  by it.
- MIT. All of this lives in `Freeboard.Persistence` and `Freeboard`, neither of which may
  reference `Freeboard.Enterprise`.

## Goals / Non-Goals

**Goals:**

- Every authorization decision on a compliance read draws its rows and its asset list from one
  repeatable-read snapshot.
- The same rule covers the write-path selector that authorizes a scope write.
- Make each decision's inputs visible and testable rather than fixed case by case: one argument
  states them, and the snapshot records them.
- Do not force one surface's payload onto every other surface's decision. A page that needs
  scopes must not make an organisation gate read scopes.
- Keep the authorization gate path on the `assets` table alone, except where a gate's
  organisation is only knowable from a stored row.
- Keep the per-request read count no worse than today on every path.

**Non-Goals:**

- Deriving the accessible set in SQL.
- Holding one transaction open for the lifetime of a request.
- Any change to what a caller may read, to endpoint JSON, or to page markup.
- Serializable isolation or any read lock. Repeatable read is what the store already uses and
  it is what the defect needs.
- A cross-request cache of any kind.

## Decisions

### One read method whose shape the caller names

`IComplianceStore` drops its ten read methods and gains one:

```csharp
Task<ComplianceSnapshot> GetSnapshotAsync(
    ComplianceReadSet sets, CancellationToken cancellationToken = default);
```

`ComplianceReadSet` is a `[Flags]` enum over the eight lists the store serves: `Assets`,
`Standards`, `Requirements`, `Controls`, `Scopes`, `Collectors`, `IntegrationConnections`,
`VendorAssurances`. `ComplianceSnapshot` carries the requested lists and the `ComplianceReadSet`
it was read with. `GetCountsAsync` is untouched: it is one statement answering one question and
takes part in no narrowing.

The whole snapshot is read inside one `RepeatableRead` transaction whenever it needs more than
one statement, which is the rule the three existing snapshot reads already follow. A snapshot
needing exactly one statement runs without a transaction, because a single statement is already
atomic and the gate path takes that shape on every request. `Controls` counts as two statements
(the controls and the `control_requirements` join), matching `GetControlsAsync` today.

**What this shape does and does not deliver.** It does not make the straddle impossible, and the
change does not claim it does. The gate path needs an assets-only read, so
`GetSnapshotAsync(ComplianceReadSet.Assets)` is always constructible, and a caller can therefore
still take one snapshot of the assets and another of the scopes and pair them. What the shape
delivers is narrower and still worth having:

- A decision's inputs are named in ONE argument at the call site, so the reads that belong
  together are visible as one expression rather than inferred from adjacent lines.
- The returned snapshot carries the `ComplianceReadSet` it was read with, so a test can assert
  what a surface drew on. That is what turns "this surface pairs two reads" from a review
  observation into a failing test.
- There is one read method rather than ten, so a new set is added in one place and every surface
  states its shape the same way.

Enforcement is therefore the structural test per surface, not the type system. That is stated
here rather than left implied, because a reader who took "one decision, one snapshot" as an
API-level guarantee would stop looking for the tests that actually hold the line.

A store-side rule was considered that would reject a set naming a payload without `Assets`,
which would close the two composed forms the worked examples name. It is not adopted. The
`Collectors` and `IntegrationConnections` sets have legitimate unnarrowed readers - the
scheduler, the credential endpoint, and the startup token warning - so the rule would have to
exempt exactly the two sets whose narrowing is a FIELD rather than a ROW. That exemption leaves
the `/collectors` straddle constructible, which is a straddle the compliance-web-read delta
forbids by its own scenario, so the rule would buy an exception list rather than the property.
Requiring `Assets` on every payload instead would put an asset read on the scheduler's loop,
which is the cost the resolved question below rejects for the same reason.

**Alternative considered: named typed projections per surface**, such as an assets read, a
compliance-read-inputs read, a vendor-register-inputs read, and a drill-down-inputs read, with
the raw single-set reads kept but documented as unsuitable for a narrowed decision. It is the
compile-time-safe shape: a caller cannot read a list its projection does not carry, because the
record does not have the property.

It is NOT rejected for failing to close the composed form. Neither shape closes it: both must
keep a public assets read for the gate path, so in both the two-call pairing stays one line of
obvious code. That ground is symmetric and decides nothing. Three others are not.

First, the projections are named after web surfaces. A vendor-register-inputs method and a
drill-down-inputs method put the web layer's page inventory into the persistence layer's public
API, so every new surface needs a new store method, a new record, and a new transaction body -
and a surface whose needs sit between two projections either over-reads or gets a fifth method.
The store should be named for the capability it serves, not for the pages that consume it.

Second, the surfaces need eight distinct shapes, not four (assets alone; assets and assurances;
assets, assurances and scopes; assets and scopes; assets and collectors; assets and connections;
the flat Statement of Applicability triple; the drill-down quintuple), plus the catalog reads.
Four projections cover them only by over-reading, which reintroduces the cost the change exists
to remove: a decision paying for a table it does not narrow on.

Third, a projection record does not report the shape it was read with in a form a test can
assert against uniformly. Each projection would need its own assertion, so the one structural
test that pins every surface's sets becomes a per-projection test that pins a name.

Rejected, with its compile-time safety recorded as a real and unrecovered loss. The mitigation
is in the next decision and in the structural tests.

One objection raised against a shape-parameterized read is worth answering directly, because it
would be decisive if it applied: that a snapshot object handed to endpoint and page code is a
transaction scope leaking out of the store, easy to misuse across Razor handlers and layout
rendering. It does not apply here. `ComplianceSnapshot` is a materialized immutable record of
lists that have already been read. The transaction opens and commits inside
`MySqlComplianceStore.GetSnapshotAsync` and no connection, transaction, or open reader crosses
that boundary. Holding a snapshot for the length of a render costs memory, not a held lock or a
leaked connection.

**Alternative considered: push the narrowing into SQL**, joining the accessible-organisation
derivation into each query so every read is one statement. It removes the window rather than
synchronizing it, which is attractive. It loses because the ancestry rules are not simple: an
inclusive cycle-guarded `parent` chain, a single `owner` edge with no fallback, a retired
discovered asset excluded, and three rollout modes that widen the organisation union. That
logic exists once in C# and is covered by the authorization tests. A second copy in SQL would
be a second place for a fail-open bug, and it would still not cover the pages, which resolve
over the full tree before filtering. Rejected, as the issue anticipated.

### An unrequested set throws, and the throw is not a store failure

`ComplianceSnapshot` exposes each list through a property backed by a nullable field. Reading a
set the caller did not request throws `ComplianceReadSetNotRequestedException`, a new exception
type in `Freeboard.Persistence`.

Returning an empty list instead would be the smaller type, and it would be wrong. An empty list
is a plausible answer - a fresh database has no collectors - so the mistake would surface as a
page that renders nothing rather than as a failure, which is the "code that hides failure"
pattern the repo bans outright.

The exception type is new rather than `InvalidOperationException` because `IsStoreFailure`
catches `InvalidOperationException` and reports it as "compliance store unreachable". A
programming error must not be reported to an operator as a database outage. The new type is not
in that catch list, so it surfaces as a 500 and appears in the log with the offending set.

### Snapshots are memoized per request and reused when a wider one already covers them

`AuthzRequestCache` keeps the snapshots the request has taken. `GetSnapshotAsync(sets)` returns
the first taken snapshot whose sets are a superset of `sets`, and otherwise reads a new one and
keeps it.

Reuse is always sound: a wider snapshot is one transaction containing every list the narrower
request asked for. It is what keeps the read count from rising. On `/compliance/vendors` the
page handler asks for `Assets | VendorAssurances | Scopes` and the rail then asks for
`Assets | VendorAssurances`, which the page's snapshot already covers, so the page and the rail
share one read and one accessible set - exactly the guarantee the vendor register capability
already ratifies.

Reuse depends on order, and that dependence is a performance property, not a correctness one.
When the narrower request comes first the request takes two snapshots, and each decision is
still internally consistent. This is stated plainly because a reader will otherwise assume the
sharing is guaranteed. The vendor register capability already ratifies the ordered form of this
- the rail is served the page's snapshot when the page reads first - so superset reuse keeps
that scenario true rather than weakening it.

On the register the required order is a FACT, not a hope. Razor Pages runs the page handler to
completion and only then executes the view, and the navigation rail is a view component in the
layout, so `OnGetAsync` has taken the page's snapshot before the rail asks for anything. The
reuse test asserts it, so a later refactor that moved the rail's read ahead of the handler would
fail rather than silently cost a round trip.

**The objection that reuse is what caused the defect, and why it does not hold.** The defect was
not snapshot sharing. It was ACCESSIBLE SET sharing across DIFFERENT asset lists: a memo keyed on
the principal alone handed surface B a set resolved from surface A's asset rows, while surface B
narrowed rows it had read from its own snapshot. Superset reuse is the opposite arrangement. The
reused snapshot is one transaction, so the reusing decision gets the rows AND the asset list from
that same transaction, and the memo, keyed by the asset list, serves it a set resolved from
exactly those rows. Nothing is paired across a boundary.

Two limits keep that argument true, and both are rules rather than incidental behaviour. Reuse
SHALL serve a single taken snapshot whose sets cover the request. Two snapshots SHALL NOT be
merged into a synthetic wider one, because the merged lists would come from two transactions and
would be exactly the straddle. Reuse SHALL NOT cross a request, because the cache is scoped and
a cross-request snapshot would serve one caller's decision from another's state.

**Alternative considered: declare a request's read shape up front**, through an endpoint filter
or a page convention, so the cache takes exactly one union snapshot per request. It would make
sharing order-independent. It needs a declaration on every endpoint and page, it goes stale
silently when a handler starts reading something new, and it puts the union of every surface's
payload back on the gate path. Rejected.

**The memo key is the premise, not a decision here.** The accessible set is memoized per
principal PER ASSET LIST, keyed by reference identity of the list. That is what lets several
snapshots coexist in one request with each decision narrowing by its own rows. The cache hands
the same `ComplianceSnapshot` instance, and therefore the same `Assets` list instance, to every
caller it serves, so identity stays exact under snapshot reuse and no hashing of the list is
needed. A caller that allocates a fresh list per call gets a fresh resolution: correct, and
slower. After this change no such caller remains, because the cache is the only source of asset
lists.

**What several snapshots deliberately give up.** Two decisions rendered in one response may come
from two snapshots taken microseconds apart. A nav badge counted from one snapshot can disagree
with a table rendered from another. That is a stale count, self-correcting on the next request,
and it is a different thing from the defect: each decision is internally consistent, and no
decision narrows with another's owner edges. The acceptance is stated per decision, and this
meets it.

**Alternative considered: one snapshot per request**, widened to the union of every surface's
needs. It fails the goal directly: the union includes the scopes, the collectors, the
connections, and the assurances, so every organisation gate and every compliance write would read
all of them. A five-table read on `PUT /api/organisations/{id}` is not defensible for a decision
that needs the `assets` table alone, and a table missing from the schema would fail every gated
write closed. Rejected.

### The gate path keeps reading the assets alone

`AuthzRequestCache.GetAssetsAsync` asks for `ComplianceReadSet.Assets` and no payload set, which
keeps every route-anchored gate, every body-anchored write selector, and both role-assignment
guards on the `assets` table. Under the reuse rule it still returns a wider snapshot's assets
when the request already took one, so a page render does not pay twice. The stored-owner scope
lookup is the one path that does not reach its assets this way, for the reason set out below.

This is a repoint onto the new read method rather than a behaviour change. The rule it keeps is
ratified in the authorization enforcement capability, and the structural tests below assert the
sets each surface names, so a later surface cannot quietly widen the gate path by adding one more
table to the shared read.

### A gate resolves its ancestry from the snapshot its selector read

`AuthzRequestCache` builds the pinned organisation ancestry chain that an organisation gate
authorizes on. Where a caller has already read a snapshot to find the
organisation, the ancestry must come from that same snapshot, or the decision straddles between
the row and the chain.

`StoredOrgSubjectAsync` is that caller. It reads the scope row of this id and target kind, takes
its `subject`, and confirms that subject resolves to an ORGANISATION asset - the owning
organisation is the subject itself, and no `owner` edge takes part. Four call sites use it: the
two DELETE selectors (`StoredScopeOrgSelector` and `StoredRequirementScopeOrgSelector`, through
`StoredScopeSelectorAsync`) and the two PUT handlers (`UpsertScopeAsync` and
`UpsertRequirementScopeAsync`), which authorize the stored owner in-handler when a body moves a
row between organisations.

So `StoredOrgSubjectAsync` returns a small record carrying BOTH the organisation it found and
the `ComplianceSnapshot` it found it in, and all four call sites pass that snapshot on:
the DELETE selectors to `OrganisationResource`, the PUT handlers to `AuthorizeOrgAsync`,
which forwards it. Returning the organisation alone is what leaves the PUT handlers gating on
the request's pinned assets-only read - a different snapshot from the one the row came from, and
the straddle again.

The cache gains `OrganisationResource`, taking the `ComplianceSnapshot` the caller already holds.
It is synchronous, because a caller holding a snapshot needs no read. `OrganisationResourceAsync`
stays as it is, taking the assets-only snapshot, so the call sites that hold no snapshot are
unchanged. Relying on reuse ordering here instead would
make a correctness property depend on call order, and the point of keying the memo per asset
list is that no correctness property depends on which surface reads first.

**What this costs the gate path, and why it is accepted.** The DELETE selectors run BEFORE
anything else in the request, so on `DELETE /scopes/{id}` and `DELETE /requirement-scopes/{id}`
the request's first asset read is a gate that also reads the `scopes` table. The authorization
enforcement capability ratifies that a request whose first asset read is a gate reads the
`assets` table alone, so that sentence is narrowed by a delta rather than left contradicted.

The narrowing gives up nothing operationally. Those routes already read the scope row in the
selector today, on a separate connection, so the `scopes` table is already on their
authorization path - the change makes it one read instead of two. And the rule the ratified
sentence protects is that a MISSING payload table must not close a gated write: a scope delete
cannot complete without the `scopes` table in any case, so a schema without it fails that write
either way. What moves is where it fails, from the store call to the gate.

The delta bounds the exception to the table holding the row the gate reads, and to gates whose
organisation is derived from a stored row. Route-anchored gates, body-anchored gates, and both
role-assignment guards keep reading the assets alone, which is where the rule was earning its
keep. Restructuring so the gate stays assets-only is not available: the gate cannot know which
organisation to authorize against until it has read the row.

### The unnarrowed catalog reads stay outside every narrowing snapshot

Four surfaces read a catalog list next to a narrowed read: the vendor register (the standards,
for assurance titles), the Statement of Applicability page and endpoint and the control detail
page (the standards, for an existence check), and the collector register page (the CONTROLS, as
the unnarrowed rows it groups its collectors under).

They stay separate reads, for the reason the vendor register already establishes and two more:

- A standard title is a shared reference label. An unresolvable title renders as the standard
  id, so a title read from the far side of a commit costs a label, not a narrowing decision.
- The existence check decides not-found, not visibility. A standard that appears mid-read yields
  404; one that disappears mid-read yields a projection over a standard with no requirements.
  Neither discloses anything the caller could not read.
- The collector register's control list is not narrowed at all. The page's own capability
  ratifies that collectors are org-independent reference data, so the ROW set is shown whole and
  only the `vendor` FIELD is narrowed. Only the collectors and the assets decide visibility
  there, and those two travel in the page's snapshot. A control read from the far side of a
  commit changes which headings the page groups under, not who may see a row.

The criterion is NOT that a separate read takes no part in the response. It is that it takes no
part in deciding what the caller may see. That distinction is carried into the spec text so a
later reader does not generalize this exception into a rule.

### The evidence ingest admission check joins the rule

`EvidenceIngestEndpoints` composes its admission from three autocommit reads: the collector, the
control's `maps_to`, and the flat Statement of Applicability inputs. A sync between them can
admit a run against a requirement that has just resolved `Out`.

It is not an accessible-set narrowing - the caller is a collector credential, not a user - and
it discloses nothing. It is brought in anyway because it is one call site, because the store
methods it uses are being removed regardless, and because leaving one composed decision behind
would make the rule "every decision but that one". It asks for
`Assets | Scopes | Requirements | Controls | Collectors`, which is one read where there were
three.

That count is the ADMITTED path. A reject costs more than it did: an unknown `collector_id` used
to be refused after reading the collectors alone, and the whole five-set snapshot - six statements
in one transaction - is now read before the first check runs. It is accepted because the composed
decision needs all five sets, so keeping the cheap early reject would mean reading the collectors
first and the rest afterwards, which is the straddle again on the highest-volume endpoint in the
system.

### Reads that stay outside the rule, and why

Stated plainly, as the acceptance bar requires:

- `GET /standards`, `GET /requirements`, `GET /controls`, and `GET /compliance/status` are
  unnarrowed catalog and count reads. Each answers from one snapshot of one set because there
  is nothing to pair it with.
- `CollectorSchedulerService` and `CollectorCredentialEndpoints` read the collectors alone. They
  make no accessible-set decision, so a one-set snapshot is the whole decision.
- The startup token-resolvability warning in `Program.cs` reads the collectors and the
  connections to log a warning. It has no principal and no decision. It takes one snapshot of
  both sets because that is now the only way to read them, not because it needs the guarantee.
- The evidence status read (`IEvidenceStore.GetCollectorEvidenceStatusesAsync`) stays outside
  the projection snapshot, as the Statement of Applicability capability already ratifies: the
  status is advisory display, and a status from the far side of a commit changes a badge, not a
  visibility decision.
- The unnarrowed catalog reads above: the standards beside the register and the two Statement of
  Applicability surfaces, and the controls beside the collector register.

### The guarantee is "no mixed decision", not "always the latest state"

A snapshot the store opens before an importer commits keeps answering from the pre-commit state
after that commit lands. That is what repeatable read means, and it is correct: the pre-commit
state is a state the database really held, the caller's answer is internally consistent, and the
next request reads the new state.

So the acceptance is stated as the absence of an IMPOSSIBLE combination, not as post-commit
linearization. Claiming the stronger property would be false - no isolation level short of
blocking every reader behind every writer delivers it - and a requirement the code cannot meet
is worse than a weaker one it can. The spec deltas are worded this way throughout, and the
concurrency test asserts "wholly one side", never "the post-commit side".

### The CLI's two requests stay two requests

`freeboard vendor list` reads `GET /vendors` and then `GET /scopes`, and joins them in the
client. Those are two HTTP requests, so they are two server-side decisions on two snapshots, and
no store-level change can make them one. This is named here rather than left implied, because a
reader who takes "one decision, one snapshot" as a whole-system property would otherwise expect
it to cover this path.

It is left as two requests, and no combined endpoint is added. The composition is already safe,
for a reason that is a property of the join rather than of timing:

- Each response is narrowed on the server against its own snapshot. Neither can carry a vendor,
  or a vendor-subject scope, the caller may not read.
- The client joins scopes onto vendors by vendor id, and a scope whose vendor is absent from the
  vendor response is dropped. A printed justification has therefore passed BOTH narrowings.

Both directions of a mid-command sync are therefore harmless. A vendor that becomes unreadable
between the calls prints with no scope lines. A vendor that becomes readable is absent from the
vendor response, so its scopes are dropped. Neither prints a hidden vendor id or a hidden
justification. What the caller can see is a listing that lags by one sync, which is staleness.

Adding a combined endpoint to remove that staleness would add public API surface, a second read
model over the vendor register, and a second thing to keep in step with the page - for no
security gain. It is not worth its cost.

The orphan-drop behaviour is what makes this argument work, and today it is an unremarked
consequence of a dictionary lookup. A refactor that changed the join to print every returned
scope would turn staleness into disclosure. So the property is ratified in the vendor register
capability and pinned by a test, which is the cheap way to keep an accidental safety property
from being refactored away.

### Capabilities whose surfaces change but whose requirements do not

The collector register and the integration connection capabilities each ratify that a row's
`vendor` field is shown only when that vendor is in the caller's ACCESSIBLE ASSET set, and that
the rule narrows the FIELD and never the ROW. Both delegate the definition of that set to the
authorization enforcement capability, and neither says anything about which store read supplies
the asset list the set is resolved from. This change alters only that - where the asset list
comes from - so both requirements stay true word for word, and neither capability takes a delta.
Writing one would restate an unchanged requirement, which makes the spec harder to read and
invites a later reader to think the behaviour moved.

The Statement of Applicability capability is in the same position. Its projection requirement
already demands that the controls and collectors be read in the same repeatable-read snapshot as
the assets, the scopes, and the requirements, and already permits the evidence status to use a
separate one. Both hold after this change. The persistence-side restatement of the two input
shapes belongs to the persistence capability, which does take a delta, because that is where the
read method they name is defined.

The rule applied here: a capability takes a delta when a ratified requirement's TEXT would
otherwise be contradicted, not when a surface it describes is touched.

Applying the same rule the other way found one delta this change owes and might have missed. The
authorization enforcement capability's shared-asset-read requirement says in terms that a request
whose first asset read is a gate reads the `assets` table alone. The scope DELETE selector is
exactly that gate and its snapshot names the scopes, so that sentence is contradicted and takes a
MODIFIED block. A carve-out written only into the compliance-write delta would have left the two
capabilities disagreeing, with the narrower one silently winning.

### Proving it

Two layers, because neither is enough alone.

1. **A structural test per surface.** A counting `IComplianceStore` double asserts that each
   read path calls `GetSnapshotAsync` once with the sets its decision needs, and that its
   accessible set was resolved from that snapshot's asset list. This is what stops a future
   surface from quietly reintroducing a second read, and it runs with no database. Since the API
   shape does not make the composed form impossible, this layer is the enforcement, not a
   check on it. Nineteen surfaces are covered: six narrowed read endpoints, five narrowing
   pages, the nav rail, the organisation selector, the two DELETE selectors, the two PUT
   handlers, the ingest admission, and the gate path. The gate path's set is pinned the same
   way, so a payload table cannot return to it unnoticed, and the scope write's
   `Assets | Scopes` is pinned as an exact set so the exception cannot grow.
2. **A concurrency test**, gated on `FREEBOARD_TEST_DB`, as the acceptance requires. A
   decorating `IDbConnectionFactory` in `Freeboard.TestInfrastructure` returns a connection
   whose Nth command execution first runs a full importer sync on a separate connection. The
   sync reparents a vendor from an organisation the caller may read to one it may not. The test
   then asserts the response is from one side of that commit: either the vendor and its `Out`
   justification are both present, or both absent, and never the justification without the
   readability. It runs against `/scopes` and against the vendor register page, which are the
   two surfaces the worked example names.

The decorator is real test infrastructure and is the only new type the tests need. It is
justified because the guarantee is a concurrency property and a single-threaded test cannot
observe it: without an interleaving hook the test would only be asserting that MySQL implements
repeatable read.

## Risks / Trade-offs

- **Replacing ten store methods with one touches about fifteen source files, all four
  hand-written store doubles, and about 120 call sites across ten test files in one commit.** A
  large mechanical diff hides a real mistake, and the test side is the bulk of it: roughly 53
  sites in `MySqlIntegrationTests.cs` alone, plus the two `FakeComplianceStore` fault flags,
  which name methods that will not exist and have to become a `ComplianceReadSet` fault mask.
  -> The replacement is mechanical and compiler-checked: every removed method has exactly one
  snapshot equivalent, and the build fails on any call site missed. The set argument at each
  call site is reviewed against the table in the Context section, and the structural tests pin
  each surface's sets so a wrong set is a failing test rather than a silent over-read. The fault
  flags are the one part that is not mechanical, so they are a named task rather than a
  find-and-replace.
- **An unrequested set is a runtime failure, not a compile-time one.** A caller can ask for
  `Assets` and read `Scopes`. -> It throws immediately and uncatchably by `IsStoreFailure`, so
  the first render of that surface fails loudly in development. A compile-time answer would need
  a type per shape, which is the eight-record alternative already rejected. Accepted, and the
  structural tests cover each shipped surface.
- **Snapshot reuse is order-dependent, so a refactor that moves a read earlier can silently add
  a database round trip.** -> Performance only: correctness holds whichever order runs, because
  each snapshot carries its own accessible set. The structural tests assert the snapshot count
  per surface, so a regression on the paths that matter is visible.
- **Two decisions in one response can come from two snapshots.** A nav badge can disagree
  with the table beside it by one sync. -> Accepted and deliberate. Each decision is internally
  consistent and no decision borrows another's owner edges, which is the defect. The
  alternative is one union snapshot on every gate, rejected above. The window is sub-second and
  self-correcting.
- **The accessible-set memo depends on reference identity of an asset list.** A caller that
  allocates a new list per call re-resolves the set on every ask, which is a silent performance
  loss. -> The cache hands the same snapshot instance to every caller it serves, so it is the
  only source of asset lists and no such caller remains after this change.
- **The concurrency test is timing-shaped and could flake.** -> It does not race: the
  interleaving is deterministic, driven by a counted command hook rather than by a sleep, and
  the competing sync commits before the hooked statement returns.
- **The CLI's vendor listing composes two HTTP responses and can lag by one sync.** -> Accepted
  and out of scope. Each response is narrowed against its own snapshot and the client join drops
  a scope whose vendor is absent, so the residue is staleness rather than disclosure. The join
  property is ratified and pinned by a test so a refactor cannot turn it into a leak.

## Migration Plan

No database migration and no configuration change. The change is deploy-and-done.

Deploy order is unchanged. The gate path reads the `assets` table alone before and after, so an
unmigrated schema degrades the nav badge to unbadged and `/vendors` to its 503, and leaves writes
working. The established order - `freeboard system migrate`, then the app - is still the right
one and is still what the docs say.

Rollback is a redeploy of the previous build. The store's SQL is unchanged, so no data written
under either build is unreadable by the other.

## Resolved Questions

**Is `ComplianceReadSet.Assets` implicit in every snapshot? No - it stays an explicit flag.**
Making it implicit would save one repeated flag on the narrowing shapes and would put an asset
read on every shape that does not narrow: the collectors-only reads taken by the scheduler and
the credential endpoint, the collectors-and-connections read behind the startup warning, and the
three catalog reads. The scheduler takes its read on a loop. Charging a decision for a table it
does not narrow on is the exact cost this change exists to remove from the gate path, so
reintroducing it as a convenience would be incoherent. Explicit.

**Does the nav badge move to a lazily rendered fragment? Not in this change.** The badge is the
only surface reading `vendor_assurances` on every authenticated page render. On
`/compliance/vendors` it costs nothing, because the page's snapshot already covers its sets and
reuse serves it. On other pages it is one two-table read per render, which is what it costs
today. Deferring it would change an observable rendering behaviour that the vendor register
capability ratifies - the count is computed for the request that renders the rail - so it needs
its own delta and its own decision about what the rail shows before the fragment arrives. That
is a separate change. It is named here as the follow-up to make if the extra read proves to
matter, so the option is not lost.

**Does `GetCountsAsync` join the snapshot API? No.** It answers `/compliance/status`, which is
unnarrowed, and it is one statement answering one question. Pulling it in would make the
snapshot API cover a read that pairs with nothing. If a later change narrows those counts by the
accessible set, they have to join the snapshot then, rather than gain a second read alongside
it, and this paragraph is the note that says so.

## Open Questions

None. The questions above are answered in the design and the deltas.

## Two designs, and how they were reconciled

Two designs for this issue were written independently, without sight of each other. They agreed
on more than they differed on, and the agreement is recorded first because independent
convergence is the strongest evidence in this document.

**Agreed independently, and held with more confidence for it:**

- Several snapshots, not one growing one. Both rejected an ever-growing per-request snapshot,
  because the union of every surface's payload would land on every organisation gate.
- The accessible set has to be memoized per asset list for several snapshots to be honest. Both
  designs named that memo key as the root footgun, and both rested on it.
- The authorization gate path stays on the `assets` table alone, so organisation gates and write
  checks read no feature table. Verification against the code found one gate that cannot hold
  this - the scope write, whose organisation is only knowable from the stored row - and it takes
  a delta narrowing the ratified sentence rather than an unstated exception.
- The narrowing is NOT pushed into SQL. Both rejected it for the same reason: the ancestry
  rules, the rollout modes, and the retired-asset exclusion would become a second authorization
  system in a second language, with a second place for a fail-open bug.
- The write path is in scope even though the issue says "reads". The stored-owner lookup that
  authorizes a scope write composes a scope read with an asset read, which is the same defect.
- The four bypassing endpoints, the register's separate scope read, and the two Statement of
  Applicability pages all come into snapshots.

**Where they differed, and how each difference resolved:**

1. *The store API shape.* One design replaced the ten read methods with a single
   shape-parameterized snapshot read. The other kept typed projections named for the surfaces
   that consume them, on the grounds that a caller then cannot read a list it did not ask for.
   Resolved toward the single method. Neither shape closes the composed form, because both keep
   a public assets read for the gate path, so that ground was set aside as symmetric. The
   projections lose on the three remaining grounds: they put the web layer's page inventory into
   the persistence API, four of them cover eight needed shapes only by over-reading, and they
   carry no uniformly assertable record of the shape a surface read. The lost compile-time safety
   is real and is recorded as an accepted risk, mitigated by an
   uncatchable-as-store-failure exception and by a structural test per surface. The objection
   that a snapshot object leaks a transaction scope into page code is answered: the snapshot is
   a materialized record and the transaction never crosses the store boundary.
2. *Snapshot reuse.* One design reused a taken snapshot whose sets cover a later request. The
   other objected that reuse across differently-scoped decisions is what caused the defect.
   Resolved toward reuse. The objection misidentifies the mechanism: the defect was reuse of an
   accessible SET across two different asset lists, whereas superset reuse hands back one
   transaction's rows AND its asset list together. Two limits are written down to keep that
   true - never merge two snapshots, never reuse across a request.
3. *The CLI.* Only one design raised `freeboard vendor list`, which composes two endpoints
   across two HTTP requests and so cannot be made consistent by any server-side snapshot. The
   path is real. It resolved as out of scope with the reason written down, plus a ratified
   requirement and a test pinning the orphan-drop property that makes the composition safe. See
   the decision above.
4. *Which capabilities take a delta.* One design expected deltas for the collector register and
   the integration connection capabilities because those surfaces change. The other did not
   include them. Resolved toward not including them, on the ratified text: both requirements
   govern which FIELD is narrowed and delegate the accessible-set definition elsewhere, so
   neither is contradicted. The criterion is written down above so the next reader does not have
   to re-derive it.
5. *The acceptance wording.* One design observed that a snapshot opened before a sync may
   legitimately answer from the pre-sync state after it commits, so the guarantee is the absence
   of an impossible mixed decision rather than post-commit linearization. Correct, and the
   deltas that claimed the stronger property were tightened.
6. *The evidence ingest admission check.* Only one design brought it in. Verified against the
   code: the endpoint composes three separate reads - the collectors, the controls, and the flat
   Statement of Applicability inputs - into one admission decision, so it is a real instance of
   the same composition. Kept, for the reasons in its decision above.

**The unified approach**, in one paragraph: one shape-parameterized snapshot read replaces the
ten composable reads; the accessible-set memo is keyed by principal and asset list so several
snapshots can coexist honestly in one request; the request cache memoizes snapshots and serves a
later request from a taken snapshot that covers it; the authorization gate path reads the
`assets` table and nothing else, except in the stored-owner lookup, which derives its
organisation from a stored scope row and is reached from four call sites - the two DELETE
selectors and the two PUT handlers; every narrowed read surface, the stored-owner write path, and
the ingest admission check each name their decision's sets and take one snapshot; a structural
test per surface pins the sets, because the API shape records a decision's inputs rather than
making the composed form impossible; and the guarantee ratified is that no decision mixes two
states of the domain.
