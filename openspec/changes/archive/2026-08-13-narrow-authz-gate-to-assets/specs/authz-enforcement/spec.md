## ADDED Requirements

### Requirement: The accessible asset set is memoized per asset list, not per principal alone

The accessibility seam SHALL memoize a caller's accessible asset set per principal AND per asset
list within a request, and SHALL resolve it at most once for each such pair. It SHALL NOT serve a
set resolved from one asset list to a caller that supplied a different one.

The seam SHALL keep its contract that the caller passes the UNFILTERED asset list. A caller still
cannot narrow before it calls, and still cannot assume it will be served a set derived from the
list it just supplied unless that list is the one the set was resolved from.

Keying on the principal alone is what forces every surface in a request onto one store read: the
first asset list to reach the seam decides the set every later surface is served, so a surface
reading its own rows narrows them with another surface's owner edges. Keying on the pair is what
lets a decision take exactly the read its own inputs need.

#### Scenario: Two asset lists resolve two sets

- **WHEN** one request resolves the accessible set for one principal from two different asset
  lists
- **THEN** each resolution is derived from the list it was given, and neither is served the
  other's answer

#### Scenario: One asset list resolves one set however many callers ask

- **WHEN** several surfaces in one request each ask the seam for the accessible set and each
  passes the same asset list
- **THEN** the set is resolved once and the later asks are served that answer, so a page render
  does not repeat the ancestry walk

### Requirement: The shared asset read carries no payload table

The request's shared asset read SHALL NOT cause any table but the unified `assets` table to be
read. That read is the one every organisation gate, every compliance write selector, and every
role-assignment guard draws on. A decision that gates on an organisation narrows the asset tree
only, so no payload table SHALL become an input it depends on.

The shared asset read MAY be served from a snapshot the request has ALREADY taken for a surface
that needed one, because serving it that way costs no read at all. It SHALL NOT take such a
snapshot on its own behalf. A request whose first asset read is a gate therefore reads the
`assets` table alone, whatever the later surfaces of that request go on to read.

A surface SHALL NOT widen the shared asset read in order to obtain a pairing it needs. A surface
that reads a payload list together with the assets keeps that pairing in its OWN read, and the
accessible set is resolved per asset list, so that surface is narrowed by its own snapshot's
owner edges whatever order the surfaces of a request run in. Which surfaces owe that pairing is
stated by the capability that owns each of them.

This bounds the blast radius of a missing table. A payload table absent from the schema SHALL
degrade the surfaces that read it and SHALL NOT make an organisation gate or a compliance write
fail closed.

#### Scenario: An organisation gate reads no payload table

- **WHEN** a compliance write or a role-assignment page is gated on an organisation and the
  request has taken no other compliance read
- **THEN** the gate resolves its asset tree from a read of the `assets` table alone, and no vendor
  assurance row is read

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
