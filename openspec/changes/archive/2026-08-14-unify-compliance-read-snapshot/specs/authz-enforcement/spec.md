## ADDED Requirements

### Requirement: One authorization decision draws its inputs from one snapshot

An authorization decision on the compliance domain SHALL draw both its inputs from ONE store
snapshot: the ROWS it answers with, and the ASSET LIST whose `parent` and `owner` edges decide
which of those rows the caller may see.

This applies to every decision, read or write, that narrows by the accessible asset set or
that resolves an authorization anchor from a stored row: the narrowed read endpoints, the
server-rendered register pages, the Statement of Applicability page and endpoint, the shell's
organisation selector and nav badge, and the write-path selector that derives a scope row's
owning organisation before gating on it. A decision SHALL NOT pair rows read on one connection
with an asset list read on another, because an importer commit between the two reads produces a
combination that never existed: pre-commit owner edges deciding what post-commit rows may
disclose, or the reverse.

Where a gate resolves an organisation ancestry chain for a decision whose organisation was
itself derived from a stored row, that chain SHALL be resolved from the SAME snapshot the row
came from, not from a separately taken asset read.

A decision MAY draw a read that takes no part in deciding what the caller may see from outside
its snapshot. The criterion is participation in the visibility decision, not participation in
the response. A reference label that already degrades to an identifier when unresolvable, and an
existence check that decides not-found rather than visibility, are outside; any list the
decision narrows, or narrows BY, is inside.

Two decisions rendered in one response MAY come from two snapshots. Each SHALL be internally
consistent, and neither SHALL narrow with the other's asset list. This rests on the accessible
set being memoized per asset list, which this capability already requires: a decision is never
served a set resolved from another decision's rows. A count derived from one snapshot beside a
table derived from another is a stale count that self-corrects on the next request, which is a
different thing from a response that pairs one state's rows with another state's owner edges.

The guarantee is that no decision MIXES two states of the domain. It is NOT that a decision
always reflects the latest committed state. A snapshot opened before an importer commits SHALL
be permitted to answer wholly from the pre-commit state after that commit lands, and such an
answer SHALL be considered correct: it is a state the database really held, and the next
request reads the new one. What SHALL NOT happen is a single decision built from both states -
pre-commit owner edges deciding what post-commit rows disclose, or the reverse.

#### Scenario: Rows and the narrowing asset list come from one snapshot

- **WHEN** a narrowed compliance surface renders while a GitOps sync commits a reparenting that
  moves an asset across the caller's accessible boundary
- **THEN** the rows and the asset list that narrows them are both from ONE side of that commit,
  so the response contains no row, and no excluded-scope justification, that the owner edges it
  was read with do not admit

#### Scenario: A wholly pre-commit answer is not a violation

- **WHEN** a snapshot opens before an importer commit and the response returns after it
- **THEN** the response MAY show the pre-commit rows and the pre-commit owner edges together,
  because that pairing is a state the database held, and the requirement forbids a mixture
  rather than staleness

#### Scenario: A stored-row gate anchors on the snapshot it read the row from

- **WHEN** a write is gated by deriving the owning organisation from a stored scope row and then
  resolving that organisation's ancestry chain
- **THEN** the row and the ancestry chain come from one snapshot, so the write is not authorized
  against an ancestry the row's own snapshot never had

#### Scenario: Two decisions in one response are each internally consistent

- **WHEN** one page render takes two snapshots because the second decision needs a list the
  first did not read
- **THEN** each decision narrows with the asset list of its own snapshot, and neither is
  narrowed by the other's

#### Scenario: A reference label may sit outside the snapshot

- **WHEN** a surface renders a standard's title beside a narrowed row and reads the standards
  separately
- **THEN** that is permitted, because an unresolvable title renders as the standard id and the
  read decides no visibility

## MODIFIED Requirements

### Requirement: The shared asset read carries no payload table

The request's shared asset read SHALL NOT cause any table but the unified `assets` table to be
read. That read is the one every organisation gate, every compliance write selector, and every
role-assignment guard draws on. A decision that gates on an organisation narrows the asset tree
only, so no payload table SHALL become an input it depends on.

ONE class of gate is excepted, and only that one: a gate whose organisation is DERIVED FROM A
STORED ROW rather than named by the route or the request body. Such a gate cannot know which
organisation to authorize against until it has read the row, and the row and the asset that
resolves it SHALL come from one snapshot, so that gate's asset read names the row's table too.
Today that is the scope and requirement-scope writes, whose organisation comes from the stored
scope row, so their snapshot names the assets and the scopes. Every OTHER gate - route-anchored,
body-anchored, and both role-assignment guards - SHALL keep reading the `assets` table alone. A
request whose first asset read is one of those gates therefore reads the `assets` table alone,
whatever the later surfaces of that request go on to read.

The exception is bounded by what the excepted gate already needs. It SHALL extend only to the
table holding the row the gate reads to find its organisation, and that row's table SHALL be one
the gated write already requires. A gate SHALL NOT be widened with a table the write itself does
not need.

A surface SHALL NOT widen the shared asset read in order to obtain a pairing it needs. A surface
that reads a payload list together with the assets keeps that pairing in its OWN read, and the
accessible set is resolved per asset list, so that surface is narrowed by its own snapshot's
owner edges whatever order the surfaces of a request run in. Which surfaces owe that pairing is
stated by the capability that owns each of them.

This bounds the blast radius of a missing table. A payload table absent from the schema SHALL
degrade the surfaces that read it and SHALL NOT make an organisation gate or a compliance write
fail closed. The excepted gates give up no part of that: the table their snapshot names is the
one holding the row the write targets, so a schema without it fails that write either way, and
the failure moves from the store call to the gate rather than appearing where it did not before.

#### Scenario: An organisation gate reads no payload table

- **WHEN** a compliance write gated on an organisation named by the route or the request body,
  or a role-assignment page, is gated and the request has taken no other compliance read
- **THEN** the gate resolves its asset tree from a read of the `assets` table alone, and no
  vendor assurance row is read

#### Scenario: A stored-row gate names the row's table and nothing more

- **WHEN** a scope write is gated by deriving its organisation from the stored scope row, and the
  request has taken no other compliance read
- **THEN** the gate takes ONE snapshot of the assets and the scopes, so the row and the asset
  that resolves its subject cannot straddle a commit, and it names no other payload table

#### Scenario: The shared asset read is served from a snapshot already taken

- **WHEN** a request renders a surface that takes an asset-and-payload snapshot, and an
  organisation gate in the same request then asks for the shared asset list
- **THEN** the gate is served that snapshot's asset rows, no second read is taken, and it is
  narrowed by the accessible set resolved from those rows

#### Scenario: A failed payload read leaves the shared asset read intact

- **WHEN** a surface's asset-and-payload snapshot faults and an organisation gate in the same
  request then asks for the shared asset list
- **THEN** the gate reads the `assets` table alone and answers, because nothing was memoized for
  it to be served from

#### Scenario: A missing payload table does not close the write path

- **WHEN** the app runs against a schema whose vendor assurance table is absent
- **THEN** gated compliance writes and the role-assignment page keep working, and only the
  surfaces that read the assurances degrade
