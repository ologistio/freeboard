## ADDED Requirements

### Requirement: Every narrowed read draws its rows and its narrowing from one snapshot

Each read surface that narrows by the caller's accessible asset set SHALL take its rows and the
unified asset list that narrows them from ONE store snapshot, named with exactly the sets that
surface's decision needs.

Concretely: `GET /organisations` names the assets; the unified `GET /scopes` names the assets
and the scopes; `GET /vendors` names the assets and the vendor assurances; `GET /collectors`
names the assets and the collectors; `GET /integration-connections` names the assets and the
integration connections; the Statement of Applicability JSON endpoint names the assets, the
scopes, and the requirements. The server-rendered register pages name the same sets their own
narrowing needs, and the vendor register additionally names the unified scopes, because it
renders each excluded scope's justification behind the same vendor visibility.

A surface SHALL NOT read its rows and its asset list with two calls. This is the rule and not an
optimisation: the two calls land on separate connections, so a GitOps sync committing between
them pairs one side's rows with the other side's owner edges, and a vendor's `Out` justification
can survive a reparenting that put the vendor beyond the caller's reach.

The unnarrowed catalog reads - `GET /standards`, `GET /requirements`, `GET /controls` - and the
counts behind `GET /compliance/status` SHALL each answer from a snapshot of the set they need
and nothing else. They narrow nothing, so they have nothing to pair.

A surface MAY read an UNNARROWED catalog list outside its snapshot - the standards, the
requirements, or the controls - for a reference label, an existence check, or a set of rows it
presents whole. None of those reads is narrowed by the accessible asset set and none decides what
the caller may see: an unresolvable standard title already renders as the standard id, an existence
check decides not-found rather than visibility, and an unnarrowed row list is shown to every
authenticated caller alike. The criterion is participation in the visibility decision, not
participation in the response, so a surface SHALL NOT read a NARROWED list outside its snapshot
on the same reasoning.

Concretely, the vendor register reads the standards for assurance titles, the Statement of
Applicability page and endpoint and the control detail page read them for an existence check, and
the collector register page reads the controls as the unnarrowed rows it groups its collectors
under. All four stay outside.

#### Scenario: A narrowed endpoint takes one snapshot

- **WHEN** an authenticated caller reads `/scopes`, `/collectors`, or
  `/integration-connections`
- **THEN** the endpoint takes one snapshot naming the assets and its own rows, and makes no
  second store read to obtain the asset list it narrows with

#### Scenario: A sync committing mid-read cannot leak a hidden vendor

- **WHEN** a caller reads `/collectors` or `/integration-connections` while a GitOps sync
  commits a change of a vendor's `owner` that moves it out of the caller's reach
- **THEN** the `vendor` field on every row is decided by the owner edges of the same snapshot
  the rows came from, so the response cannot name a vendor that snapshot's own owner edges hide

#### Scenario: A scope and its justification cannot straddle the commit

- **WHEN** a caller reads `/scopes` while a sync reparents the scope's subject across the
  caller's accessible boundary
- **THEN** the scope row and the asset list that admits it are from ONE side of that commit, so
  the response never carries a scope, or its `Out` justification, that the owner edges it was
  read with do not admit; answering wholly from the pre-commit side is permitted, answering from
  both sides at once is not

#### Scenario: Catalog reads take a single-set snapshot

- **WHEN** an authenticated caller reads `/standards`, `/requirements`, or `/controls`
- **THEN** each answers from a snapshot of that one set, unnarrowed, exactly as before
