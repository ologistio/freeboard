## ADDED Requirements

### Requirement: Register, API, and CLI carry vendor tier and data classes

The vendor register page, the vendors API row, and the CLI vendor listing SHALL all
carry a vendor's `tier` and its `data_classes`. All three read the same store rows,
and read-model parity is an acceptance rule for this project, so the three surfaces
SHALL ship together rather than one leading the others.

The web register at `/compliance/vendors` SHALL render each readable vendor's tier
in its Tier column and its data classes in its Data column. A vendor with no tier
SHALL render the explicit empty "Not tracked" rather than a guessed or defaulted
value, and so SHALL a vendor with no data classes. Both facets SHALL render as
neutral-toned tags. Red SHALL NOT be used for any tier or data class, because red is
reserved for failing and overdue states and a static attribute is neither; amber
SHALL NOT be used either, for the same reason. The tag word carries the meaning. Data
classes SHALL NOT be tone-ranked against each other, because they name regulatory
regimes rather than severities and no ordering between them exists.

The register's row order SHALL NOT change: it remains vendor id. Tier is editable
config, so ordering by it would reshuffle the register whenever a vendor is
re-tiered. This change SHALL add no filtering, grouping, or sorting control, and
nothing SHALL read either field to derive a status, a review cadence, or a score.

`GET /api/v1/freeboard/vendors` SHALL return both fields on each vendor row it
already returns, keeping the endpoint's existing owner-narrowed read model: a vendor
hidden by owner narrowing discloses neither its tier nor its data classes, because
the row itself is absent.

`freeboard vendor list` SHALL print both on each vendor's existing line, printing
`-` for an absent value, matching the CLI's established placeholder for a missing
optional field. The command's exit-code behavior is unchanged: `0` on success, `1`
on a validation response, `3` on an operational failure.

#### Scenario: Register renders tier and data classes

- **WHEN** an authenticated user opens `/compliance/vendors` and a readable vendor
  carries a tier and one or more data classes
- **THEN** the Tier column shows that tier and the Data column shows each data
  class, every tag rendered in the neutral tone

#### Scenario: Absent values render as an explicit empty

- **WHEN** a readable vendor carries no tier, or no data classes
- **THEN** the corresponding column reads "Not tracked" rather than a default,
  a guess, or a blank cell

#### Scenario: Row order is unchanged

- **WHEN** the register lists vendors of differing tiers
- **THEN** the rows are ordered by vendor id, unaffected by tier

#### Scenario: Vendors API returns both fields

- **WHEN** an authorized caller requests `GET /api/v1/freeboard/vendors`
- **THEN** each returned vendor row carries its tier and its data classes, and a
  vendor excluded by owner narrowing is absent along with both of its values

#### Scenario: CLI prints both, with a placeholder when absent

- **WHEN** the user runs `freeboard vendor list` against a reachable API
- **THEN** each vendor line carries its tier and its data classes, printing `-` for
  either value the vendor does not carry, and the command exits `0`
