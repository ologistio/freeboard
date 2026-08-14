## ADDED Requirements

### Requirement: The stored-owner authorization lookup reads its row and its asset from one snapshot

A scope write's stored-owner lookup SHALL read from ONE store snapshot naming the assets and
the scopes. A scope write is authorized against the organisation that owns the stored row.
Deriving that organisation reads the stored scope row and then resolves the row's `subject`
against the unified asset set to confirm the subject is an organisation. That pair of reads, and
the ancestry chain the resulting organisation gate anchors on, all come from that one snapshot.

This SHALL hold on EVERY path that derives an organisation from a stored scope row, not only on
the route filter that gates a delete. A scope upsert that MOVES a row between organisations
authorizes the stored owner in the handler, after the body-anchored gate has already run, so the
stored-owner lookup and the ancestry chain it gates on SHALL come from the same snapshot there
too. Resolving that chain from the request's pinned assets-only read instead would pair a row
from one snapshot with owner edges from another, which is the straddle this requirement closes.

Reading the row on one connection and the asset set on another lets a GitOps sync commit between
them. The write would then be authorized against an owner the row's own snapshot never had: a
subject that was an organisation before the commit and is not after it, or a subject whose
ancestry moved out of the caller's grants between the lookup and the gate. No scope row is
disclosed, because the lookup yields an organisation id rather than the row. The authorization
decision itself can go wrong in either direction: a write the caller may make is refused, or a
write the caller may not make is permitted. The store re-checks the stored owner under the write
lock, so some of the wrongly permitted cases end as a conflict or a not-found rather than a
write. That recheck is a backstop, not the guarantee, and the straddle is closed for the same
reason the read paths are.

The gate for a write whose organisation comes from the ROUTE or the BODY rather than from a
stored row SHALL keep reading the assets alone. Such a decision narrows nothing else, so
widening its snapshot would put a payload table on every organisation gate for no decision that
reads it. The stored-row exception to the shared asset read is stated by the authorization
enforcement capability and is bounded there to the table holding the row the gate reads.

#### Scenario: The stored row and its subject asset come from one snapshot

- **WHEN** a DELETE on a scope route resolves the stored row's owning organisation while a
  GitOps sync commits a change to that subject's `parent` chain
- **THEN** the scope row, the asset that resolves its subject, and the ancestry chain the gate
  authorizes on are all from one side of that commit

#### Scenario: A cross-organisation upsert anchors on the snapshot it read the row from

- **WHEN** a PUT on a scope or requirement-scope route finds a stored row owned by a different
  organisation than the request body names, and authorizes that stored owner
- **THEN** the stored row, the asset that resolves its subject, and the ancestry chain the
  stored-owner authorization anchors on all come from ONE snapshot, not from the request's
  separately pinned asset list

#### Scenario: A route-anchored gate reads the assets alone

- **WHEN** a write is gated on an organisation named by the route or the request body
- **THEN** the gate takes a snapshot of the unified assets and nothing else, so no payload table
  becomes an input to an organisation gate that does not read it
