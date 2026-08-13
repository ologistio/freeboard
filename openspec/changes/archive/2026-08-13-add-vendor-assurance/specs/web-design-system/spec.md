## MODIFIED Requirements

### Requirement: Invariant-carrying marks are emitted by tag helpers

Marks that carry a status invariant or ARIA SHALL be authored as ASP.NET tag
helpers rather than re-inlined per page - status seal (S2/S3), provenance stamp
(P1/P2 provenance), badge, tag, due date (T6), chip (L2), and owner. The status mark
SHALL bind its word, its color/tone, and its ARIA to a single typed status kind that
covers the complete S1 status vocabulary - one canonical kind for every S1 status, with
the synonym pairs (Ready/Passing, Drifting/Degraded) each mapping to one kind - so every
status is drawn from S1, no label outside it is representable, a status word and its
color cannot be authored to disagree, and red is reachable only for failing or overdue
(S3). The generic tint marks (badge, tag) SHALL take a typed tone enum whose members
correspond one-to-one to the tint classes those marks emit, so no tone lacks a class and
no emitted tint class lacks a tone, and a tone that violates S3 is unrepresentable; the
filter chip carries a selected state and count rather than a tone. Each tag helper SHALL
emit the corresponding `fb-*` or redefined class, so `app.css` remains the single style
source. A mark with a single caller MAY remain a plain CSS class until a second caller
appears.

The provenance stamp SHALL likewise take a typed tone, drawn from its OWN enum rather
than the generic tint one. Throughout this requirement a RENDERING is the class the stamp
emits, not the pixels that class produces: two classes that declare identical colours are
two renderings, and the untinted base is the rendering emitted when no variant class is
added. The stamp emits two kinds of variant class and they are selected by two different
things: its PROVENANCE variants stay selected by the stamp's own provenance flag (the
hand-entered variant when it is set, the generated variant when it is not), and the tone
selects among the toned renderings. Each tone member SHALL correspond to exactly one toned
rendering and each toned rendering SHALL have a member, so no tone lacks a rendering and no
toned class is unreachable. Every variant class the stamp can emit SHALL be selected by
exactly one of the two - the tone or the provenance flag - so none is orphaned and none has
two selectors. Two classes sharing one declaration block SHALL NOT collapse into one
rendering for this test: they are distinct classes, reached by distinct selectors, and the
one-to-one map is over classes.

Each tone member SHALL be named for what it renders. The neutral member SHALL emit the
mark's untinted base rather than a provenance variant's colour: a member whose name states
a colour it does not emit is a defect in the enum, not a detail of it. The tone SHALL be
optional, and a stamp with no tone SHALL render exactly the provenance variant it rendered
before the tone existed, so no existing caller changes and no page's appearance shifts.
The tone SHALL select the stamp's variant class rather than add to it, so a toned stamp
never renders a provenance variant's colour underneath its tone.

The stamp's tone set SHALL carry no pass tone and no brand tone: a stamp reports a value's
origin and age rather than a Freeboard verdict, so green would claim a verdict the mark
does not hold, and the brand variant is a provenance rather than a tone. Red SHALL remain
reachable only for an overdue fact, in keeping with S3.

Where a toned variant would declare exactly the same colours as a provenance variant, the
two SHALL share one CSS rule rather than repeat the declaration, so one visual job resolves
to one declaration and the sameness is deliberate rather than accidental.

The stamp's source slot SHALL name where the displayed value came from, which is the
collecting integration or collector for an automated value and, for a certification held on
file, the standard that certification is against. P1 requires an automated value to name its
source and its age. A certification is not an automated value: it is hand-authored, so P2 is
the rule that governs it, and P2 as modified by this change requires a value that references
a named external record, shown with that record's own date, to name the record rather than
be stamped MANUAL. Naming the standard is therefore the rule, not an exception to it. The
tag helper's own documentation SHALL state both readings so an author is not left to infer
one. A stamp SHALL still carry an age or date in every case: a provenance with no date is
the claim the stamp exists to prevent.

#### Scenario: Status word and color cannot be authored to disagree

- **WHEN** an author places a status mark
- **THEN** its word and its color come from one typed status kind, so an informational
  or due-soon status cannot be rendered red and its word cannot contradict its color

#### Scenario: Every status in the vocabulary has a typed kind

- **WHEN** the status kinds are enumerated against the S1 vocabulary
- **THEN** every S1 status resolves to exactly one kind (the synonym pairs Ready/Passing
  and Drifting/Degraded each to one canonical kind), so no status is missing and no label
  outside S1 is representable

#### Scenario: Tint tone maps one-to-one to its classes

- **WHEN** the badge and tag tone enum is compared with the tint classes those marks
  emit
- **THEN** each tone maps to exactly one emitted class and each emitted tint class has a
  tone, with no info tone (no info tint class exists) and a brand tone present

#### Scenario: Stamp tone maps one-to-one to its toned renderings

- **WHEN** the stamp's tone enum is compared with the toned renderings the stamp emits
- **THEN** each tone maps to exactly one rendering and each toned rendering has a tone,
  with no pass tone and no brand tone present, so a green or brand-toned stamp is
  unrepresentable, and the neutral tone emits the mark's untinted base rather than a
  provenance variant's colour

#### Scenario: Every emitted variant class has exactly one selector

- **WHEN** the variant classes the stamp can emit are enumerated against the tone enum and
  the provenance flag
- **THEN** each class is selected by exactly one of the two, so no class the stylesheet
  defines is unreachable and no two selectors compete for one class

#### Scenario: Untoned stamp renders as before

- **WHEN** an existing stamp is placed with no tone
- **THEN** it emits the provenance class it emitted before the tone existed, so no caller
  changes and no page's appearance shifts

#### Scenario: Stamp names its origin and its date

- **WHEN** a stamp reports a certification held on file
- **THEN** it names that certification's standard in its source slot and the expiry in its
  age slot rather than being stamped MANUAL, and the tag helper's documentation states
  that the source slot names an integration for an automated value and a standard for a
  certification

#### Scenario: Tag helper output is the shared class vocabulary

- **WHEN** a mark tag helper renders
- **THEN** its output carries the same `fb-*` or redefined class the CSS defines, and
  no mark inlines its own colors

#### Scenario: Status carries shape and word, not color alone

- **WHEN** the status mark renders
- **THEN** it emits a shape and a word, so its meaning survives with color removed
  (S2)
