## MODIFIED Requirements

### Requirement: S1 One status vocabulary product-wide

There SHALL be one status vocabulary product-wide: Ready/Passing, Failing, Due soon,
Overdue, Drifting/Degraded, Snoozed, Waiting, Draft, Out of scope.

One carve-out SHALL exist. An evidence collector row on the Statement of Applicability page
labels the state of a COLLECTION, not the state of an object. The product vocabulary names
object states and has no member for "the collection attempt failed" or for "the collector
stopped reporting". A row on that page MAY therefore use the evidence-status label set, which
SHALL be exactly these six labels and no others:

| Label | Meaning |
| --- | --- |
| `passing` | The newest collection cycle observed no failing check. |
| `soft failure` | The newest collection cycle observed a failing advisory check. |
| `hard failure` | The newest collection cycle observed a failing blocking check. |
| `collection stopped` | The newest collection cycle is older than the cadence window plus grace. |
| `collection failed` | A collection attempt in the newest cycle failed, so nothing was observed there. |
| `not collected` | The collector has produced no evidence at all. |

The carve-out is bounded three ways. It applies only to the evidence-status badge on a
collector row of the Statement of Applicability page. It admits no seventh label, so a new
evidence state amends this table rather than inventing a word at the page. It suspends no
other status rule: S2 still requires shape plus word, and S3 still reserves red, so only
`hard failure` may be red and every other label in the table SHALL be amber, brand, neutral,
or success.

Every other surface SHALL use the product vocabulary. The control anatomy that the drawer and
the control's direct link render is one such surface. It maps the same evidence states onto
product statuses, so `collection failed` reaches the reader there as the Drifting/Degraded
member rather than as its own word.

The restriction governs the status LABEL. A supplementary note that says in plain words what
the collection did is not a status label, and any surface MAY carry one beside the status it
already shows. The control anatomy already notes a stopped collection this way, and it notes a
failed one the same way. Such a note SHALL NOT replace the product status, SHALL NOT be
styled as the status, and SHALL NOT introduce a seventh evidence-status label.

#### Scenario: Status uses the shared vocabulary

- **WHEN** any surface shows a status
- **THEN** it uses a term from the single shared vocabulary

#### Scenario: A collector row labels its collection state

- **WHEN** the Statement of Applicability page renders the evidence-status badge on a
  collector row
- **THEN** the badge shows one of the six evidence-status labels, it carries a shape and a
  word, and red appears only for `hard failure`

#### Scenario: The evidence labels do not leak to other surfaces

- **WHEN** any surface other than that row badge shows the state of a collector
- **THEN** it uses the product vocabulary, so the control anatomy shows an errored collector
  as the Drifting/Degraded member rather than as an evidence-status label, and any plain-words
  note beside it supplements that status rather than standing in for it
