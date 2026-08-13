## MODIFIED Requirements

### Requirement: P2 Manual values are stamped MANUAL and dated

Manual values SHALL be stamped MANUAL and dated. Manual is a provenance, not the absence of
one.

A value that is a REFERENCE to a named record held outside the system, displayed together
with that record's own date, SHALL name that record instead of being stamped MANUAL, and
SHALL still carry the date. A certification a vendor holds is such a value: the stamp names
the standard the certificate is against and the date that certificate expires. MANUAL would
name the act of transcribing the record and drop the record itself, which is the part the
reader came for.

That carve-out SHALL NOT extend to content a person supplies. An uploaded evidence
document, a note, or an answer originates with the person who entered it, whatever external
thing it describes, so it stays MANUAL and dated. The line is between a reference to a
named external record shown with that record's own date, and content a person composed or
supplied.

#### Scenario: Manual value shows provenance

- **WHEN** a person enters or uploads the content of a value by hand
- **THEN** it is stamped MANUAL with a date

#### Scenario: A reference to an external record names that record

- **WHEN** a displayed value is a reference to a record held outside the system, shown with
  that record's own date - a vendor's certification against a named standard, with the
  date it expires
- **THEN** it is stamped with the record it references and that date, not with MANUAL
