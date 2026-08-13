## Context

A compliance read narrows its rows by the caller's ACCESSIBLE ASSET set. That set is not a SQL
predicate. The store reads the whole `assets` table, and
`IAssetAccess.AccessibleAssetIdsAsync(user, assets, ct)` closes the caller's organisation union
over the `parent` and `owner` edges on those rows. Each narrowed surface then filters its rows
in C# against the resulting id set.

`AuthzRequestCache` is registered SCOPED and holds two memos that matter here:

- the asset list, read once per request and handed to the authorizer, the write selectors, the
  page guards, and the organisation selector;
- the accessible set, resolved once per principal per request.

The asset list is currently read as `GetVendorAssuranceInputsAsync`, a repeatable-read snapshot
of the assets AND the vendor assurances. Three surfaces need that pairing: the register page,
`GET /vendors`, and the nav rail badge. Every other consumer of the cache needs the assets
alone, and pays for the assurance table anyway.

The cost is not a wasted read. It is a table on the authorization hot path. `IsStoreFailure`
turns a `DbException` into a 503 or a fail-closed gate, so an app running against a schema
without `vendor_assurances` refuses every gated compliance write with 403 and faults the
role-assignment page with 500. A total write outage follows from a table only two surfaces need.

Constraints that shape the design:

- `IAssetAccess.AccessibleAssetIdsAsync` requires the UNFILTERED asset list, so a caller cannot
  narrow before it calls.
- Resolving the set is a full pass over the asset tree with a per-node ancestry walk, and a page
  render asks for it at least twice. Memoization has to survive.
- The assurances and the assets must travel together for any decision that narrows one by the
  other. That is a ratified guarantee and this change keeps it.
- MIT. The code lives in `src/Freeboard`, which may not reference `src/Freeboard.Enterprise`.

## Goals / Non-Goals

**Goals:**

- Take `vendor_assurances` off the authorization gate path, so an unmigrated schema cannot close
  the compliance write path.
- Let several snapshots coexist in one request with each decision narrowing by its own rows.
- Keep the assurance surfaces narrowing their assurance rows with the owner edges of the same
  snapshot those rows came from.
- Keep the per-request read count unchanged on the page renders whose handler takes no asset read
  before the layout, and hold the rise to one read and one extra tree walk on the three that do.

**Non-Goals:**

- Changing the store's read API, or how many read methods it offers.
- Deriving the accessible set in SQL.
- Any change to what a caller may read, to endpoint JSON, or to page markup.
- A cross-request cache of any kind.

## Decisions

### The accessible-set memo is keyed by the asset list, not by the principal alone

This is the enabling mechanism, and it comes first because nothing else works without it.

`AuthzRequestCache.AccessibleAssetIdsAsync` keys on the principal today, so the first asset list
to reach the seam decides the set every later caller is served. That is why one snapshot had to
serve the whole request once the assurances needed pairing with the assets: two snapshots meant
the second surface narrowing with the first surface's owner edges.

The memo now keys on the principal AND the asset list instance it resolved from. Two decisions
on two asset lists each get a set resolved from their own rows.

Reference identity is the key, pinned by an explicit `ReferenceEqualityComparer` on the memo
rather than left to the default comparer. The default is reference equality only for as long as no
`IReadOnlyList<AssetNode>` implementation the store returns overrides `Equals`, which is a
property of code outside this seam. An authorization key must not depend on that.

The store returns a mutable `List<AssetNode>`, so a list mutated after its first resolution would
leave a memoized set wider than its current rows admit. No code mutates these lists today. The
seam states the requirement it depends on - an asset list handed to it is not mutated afterwards -
and a test pins it, rather than copying every list defensively on a hot path.

The cache hands the same list instance to every caller it serves, so identity is exact and no
hashing of the list is needed. A caller that allocates a fresh list per call gets a fresh
resolution: correct, and slower. That is the right failure direction.

The seam's public contract is unchanged. `IAssetAccess.AccessibleAssetIdsAsync(user, assets, ct)`
keeps its signature and still requires the unfiltered list. What changes is its documented
memoization, from "once per principal per request" to "once per principal per asset list per
request", and that wording is ratified in the deltas rather than left in a comment.

**Alternative considered: hash the asset list and key on the hash.** It would make two equal
lists share one resolution, which reference identity does not. It costs a pass over the whole
list on every ask to save a resolution that only happens when a caller allocates a second equal
list, and after this change no such caller exists. Rejected as work for a case that does not
arise.

### The shared asset read reads the assets and nothing else

`AuthzRequestCache.GetAssetsAsync` stops being served from the assurance snapshot. It reads the
assets directly, UNLESS the request has already taken the assurance snapshot for a surface that
needed one, in which case it serves that snapshot's asset list and takes no read of its own.

The first list the shared read serves is pinned for the request. Reuse populates the shared memo
rather than being re-evaluated per call, so a caller arriving after an assurance snapshot lands is
served the same list as the caller before it. Without that pinning one request could serve two
different lists from one shared read - a page handler narrowing by one and the organisation
selector by another - and the ancestry map built from the shared read could disagree with what a
later caller is served. The read counts are the same either way, so the numbers below do not settle
it and the rule is stated here.

The reuse is opportunistic and one-directional, and that is what keeps `vendor_assurances` off
the gate path. `GetAssetsAsync` never causes the assurance read; it only consumes a memo that is
already there. An assurance read that faults memoizes nothing, so a gate arriving after one still
reads the assets alone and still answers, and a gate arriving before one reads the assets alone by
definition. The write-outage fix therefore holds in every ordering, and reuse buys back the read
count without putting the table back on the gate path.

The consequences are worth stating, because this partly reverses the shape the assurance work
adopted:

- Every organisation gate, every compliance write selector, and both role-assignment guards stop
  reading `vendor_assurances`. An unmigrated schema degrades the nav badge, which already catches
  store failure and renders unbadged, and `/vendors`, which returns 503. It no longer answers 403
  on every gated write and 500 on the role-assignment page.
- An ordinary page render is unchanged: one read, one list, one tree walk. The rail renders before
  the organisation selector nested inside it, and the rail's badge takes the assurance snapshot, so
  the selector reuses it and both narrow from the same list.
- Three pages take the shared asset read in their handler, BEFORE the layout renders: the
  role-assignment page, whose gate resolves an organisation ancestry, and the collectors and
  integration-connections pages, which read the cache directly. Each pins the assets-only list
  first, so the rail's badge then takes the assurance read: two reads, two lists, and two tree
  walks where there is one of each today. The trade is one extra read and one extra walk on three
  pages against removing a payload table from every write authorization, and the write path wins.
- A gated write renders no layout, so it stays at one read - of the `assets` table alone.

### The assurance snapshot survives, scoped to the decisions that narrow on it

`GetVendorAssuranceInputsAsync` stays on the cache, memoized, and is still taken at most once per
request. The register page, `GET /vendors`, and the rail badge each take it and each narrow with
its asset list, so those three surfaces still pair the assurances with the owner edges that
decide their readability. That guarantee is what the assurance work established and it is not
what this change reverses.

What is reversed is only the SCOPE of the pairing. It belongs to the decisions that narrow on it,
not to every organisation gate in the application.

The persistence capability requires the snapshot's asset list to be "the same unified read every
other consumer uses ... so the narrowing decision resolves over the same tree everywhere". That is a
requirement on the SHAPE of the read, not on the instance: the sentence contrasts the list with "a
vendor-only or otherwise filtered one", and the tree it names is the unified asset projection. Both
reads here return that projection, so a request holding two instances of it still satisfies the
requirement and no delta is owed. Read as a requirement on the INSTANCE the sentence would forbid
this change, which is why the reading is recorded rather than left to be re-derived.

Sharing between those three surfaces no longer carries the correctness argument it used to carry.
It keeps the read count down. The correctness now comes from the memo key: a surface that reads
its own snapshot is narrowed by that snapshot's own owner edges whatever order the surfaces run
in. This is stated plainly because the ratified text currently gives sharing the correctness role,
and a reader who keeps that reading would not see why the gate path is safe to detach.

**What this deliberately gives up.** Two decisions rendered in one response may now come from two
reads taken microseconds apart. A nav badge counted from one can disagree with a table rendered
from another. That is a stale count, self-correcting on the next request. It is a different thing
from the defect it replaces: each decision is internally consistent, and no decision narrows with
another's owner edges.

**And it changes an audit count.** The Compat zero-grant read fallback writes an `authz.compat.read`
row inside the resolution the memo guards, so a zero-grant Compat caller now writes one row per
distinct asset list rather than one per request. Reuse holds that at one row on an ordinary page
render. It becomes two on the three pages that take the shared asset read before the layout, and
two on a Statement of Applicability page. The trail records a real fallback use each time, which is
what the capability asks of it - a row per use, not a row per request - so the rows stay true and
the count simply tracks resolutions. Deduplicating per principal per request was rejected: it would
add per-request state whose only job is to make the trail report fewer uses than occurred.

**Alternative considered: keep one snapshot per request and widen it further.** It is the shape
the code has today, extended. Every organisation gate and every compliance write would then read
the union of every surface's payload. Putting one such table on that path is what produced the
outage this change removes. Rejected.

### The Statement of Applicability pages stop seeding the memo for the rail

Today the two Statement of Applicability page handlers run before the layout renders, take their
own drill-down snapshot, and seed the per-principal memo from it. The rail badge then narrows ITS
assurance rows with the page's owner edges.

After the memo change the rail resolves its own set from its own asset list. Each read stays
internally consistent, the pages keep their own snapshot, and the cross-read pairing that was
wrong stops happening. No code in those page handlers changes.

The drill-down read does not go through the cache, so it pins nothing there and the organisation
selector still reuses the rail's assurance snapshot. These pages therefore keep today's two reads.
What they gain is a second tree walk, one per list, which is the price of each read being narrowed
by its own owner edges.

### What this change does not close

The register reads the unified scopes with a call of its own, outside the assurance snapshot,
and renders each excluded scope's justification behind vendor visibility. That is a straddle,
the persistence capability already states that it is open, and this change does not close it.
Closing it means changing which lists the store can serve in one read, which is a different
piece of work with a different blast radius. Naming it here keeps it from reading as an
oversight.

The four endpoints that read assets with a call of their own are in the same position.

Because those straddles remain, the delta ratifies only what lands: that no surface widens the
shared asset read to get a pairing, and that the assurance surfaces keep theirs. It does NOT
ratify the general rule that every surface narrowing a payload list reads that payload with its
assets, which this change would violate on the day it landed. That rule belongs to the work that
closes the straddles above, and it is stated there.

### Proving it

Two layers, because neither is enough alone.

1. **A memo test.** Two asset lists for one principal in one request resolve two accessible sets,
   and neither is served the other's answer. One asset list resolves one set however many
   surfaces ask, so a page render still walks the tree once.
2. **A gate-path test.** A store double that faults on the assurance read ALONE still answers a
   gated compliance write and still renders the role-assignment page, because neither reads that
   list. The assurance surfaces are the only ones that degrade. The existing double faults both
   reads together, which would pass whatever the code did, so the double needs an assurance-only
   failure before the test proves anything. This is the test that pins the behaviour a future
   change would otherwise undo by adding one more table to the shared read.

   The double must also hand out a distinct list instance per read. Today it returns one `Assets`
   property from both, which would collapse two memo keys into one and hide the very thing the
   memo test asserts.

## Risks / Trade-offs

- **The memo depends on reference identity of a list.** A caller that allocates a new list per
  call re-resolves the set every time, which is a silent performance loss. -> No caller allocates
  a second list within one decision: a surface reads once and passes that list to the seam. The
  comparer is pinned explicitly and the memo test pins the resolution count.
- **Three pages take one more read and one more tree walk than today**, and the two Statement of
  Applicability pages take one more walk. -> Accepted. It buys the removal of a payload table from
  every write authorization. Reuse holds every other page render at today's cost.
- **Read counts now depend on the order the surfaces of a request run in.** Reuse serves the
  shared asset read from an assurance snapshot only when that snapshot was taken first. -> Accepted.
  Ordering changes the cost, and which list a surface is served, never the honesty of a narrowing:
  each list carries its own resolution, and the gate-path guarantee holds in every order because
  reuse never triggers the assurance read.
- **Two decisions in one response can now come from two reads.** A nav badge can disagree with
  the table beside it by one sync. -> Accepted and deliberate. Each decision is internally
  consistent and no decision borrows another's owner edges. The window is sub-second and
  self-corrects on the next request.
- **This reverses part of a decision taken one step earlier, which reads as churn.** -> That
  decision needed one snapshot per request only because the memo was keyed per principal. Keying
  it per asset list removes the constraint and leaves only the cost. What survives from that work
  is named above alongside what does not.
- **The corrected spec text is law today, so the correction has to travel with the code.** ->
  The deltas do exactly that. A code change that left the ratified sentences standing would leave
  the capability describing behaviour the app no longer has.

## Migration Plan

No database migration and no configuration change. The change is deploy-and-done.

Deploy order relaxes rather than tightens. Before this change a new app against an unmigrated
schema answered 403 on every gated compliance write and 500 on the role-assignment page. After
it, the gate path reads the `assets` table alone, so an unmigrated schema degrades the nav badge
to unbadged and `/vendors` to its 503, and leaves writes working. The established order -
`freeboard system migrate`, then the app - is still the right one and is still what the docs say.

Rollback is a redeploy of the previous build. No SQL and no stored data changes.

## Open Questions

None.
