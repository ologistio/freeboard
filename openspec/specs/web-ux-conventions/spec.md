# web-ux-conventions Specification

## Purpose

The numbered UX ruleset for the Freeboard web UI (`src/Freeboard`): navigation depth and
grouping, the single object model and its detail anatomy, list and table behaviour, the
status-and-color vocabulary, the task model, provenance and freshness, notifications,
external surfaces, forms and errors, accessibility, and language. Each rule is a distinct
normative requirement carrying its stable ID (N, O, L, S, T, P, X, E, F, A, W series) so
reviews and tests cite it directly (for example "this breaks L4"). Rules are stated
unconditionally regardless of current implementation state; the conformance suite tests each
rule wherever a surface exists to exercise it. This capability is the ratified source the UI
migration builds and tests against.

## Requirements
### Requirement: Purpose and rule citation

The `web-ux-conventions` capability SHALL hold the numbered UX ruleset for the Freeboard
web UI (`src/Freeboard`), transcribed from the ratified design system. Each rule SHALL be a
distinct requirement whose title carries the rule's stable ID (for example `N1`, `L4`,
`A6`) so reviews and tests cite it directly. Every rule is a normative requirement
regardless of the current implementation state. The conformance suite tests each rule
wherever a surface exists to exercise it; the absence of a surface today does not weaken the
rule. The rule IDs SHALL NOT be renumbered, because downstream citations depend on them.

For the accessibility rules, ownership of the automated check is split by what axe-core can
measure. The `web-accessibility` capability's axe-core audit owns A1 (contrast) and the
AA-in-both-themes part of A6. Axe does not measure keyboard traversal, motion preference,
focus trap and restore, or theme-stable status semantics, and its generic target-size rule
does not enforce A4's Freeboard-specific thresholds (32px in dense tables, 44px elsewhere),
so A2, A3, A4, A5, and the semantic-stability and personal-setting parts of A6 are
conformance checks owned by this capability, not by `web-accessibility`.

#### Scenario: A rule is cited by ID in review

- **WHEN** a reviewer holds a screen against this capability and finds it violates a rule
- **THEN** they cite the rule by its stable ID, and the ID resolves to exactly one
  requirement in this spec

#### Scenario: Conformance tests where a surface exists

- **WHEN** the conformance suite selects rules to enforce
- **THEN** it checks each rule for which a surface exists to exercise it, and each check
  names the rule ID it enforces

### Requirement: N1 Navigation depth is two levels

Navigation depth SHALL be two levels: group, then page. Anything deeper SHALL be a tab or a
drawer on a page, never a nav item.

#### Scenario: No third nav level

- **WHEN** the primary navigation is rendered
- **THEN** every destination is a page under exactly one group, and no navigation control
  descends below group then page

### Requirement: N2 Each page belongs to one group

Every page SHALL belong to exactly one navigation group. No page SHALL appear twice in the
nav.

#### Scenario: No duplicate nav entries

- **WHEN** the navigation map is built
- **THEN** each page appears under one and only one group

### Requirement: N3 Group names are shared jobs or nouns

Group names SHALL be jobs or nouns the whole company shares. They SHALL NOT be internal
module or SKU names.

#### Scenario: Group label is not a module name

- **WHEN** a navigation group is named
- **THEN** the label is a shared job or noun, not an internal module or product-tier name

### Requirement: N4 Module settings do not exist

Module-specific settings pages SHALL NOT exist. All configuration SHALL live in Settings,
sectioned by module.

#### Scenario: Configuration lives in Settings

- **WHEN** a feature needs configuration
- **THEN** that configuration is a section within Settings, not a settings page attached to
  the feature's own nav entry

### Requirement: N5 Module report pages do not exist

Module-specific report pages SHALL NOT exist. All reporting SHALL live in Reports as saved,
scoped views.

#### Scenario: Reporting lives in Reports

- **WHEN** a feature exposes reporting
- **THEN** it is a saved, scoped view under Reports, not a report page on the feature's nav

### Requirement: N6 Nav badges show only actionable counts

Navigation badges SHALL show only actionable counts (failing, or waiting on the viewer).
Passing counts SHALL NOT badge.

#### Scenario: Passing counts do not badge

- **WHEN** a nav item has an associated count
- **THEN** a badge appears only if the count is actionable, and passing or informational
  totals show no badge

### Requirement: N7 One command palette

There SHALL be exactly one command palette (Ctrl-K) that jumps to pages and asks the
assistant. There SHALL NOT be a second search box elsewhere.

#### Scenario: Single search surface

- **WHEN** the app chrome is rendered
- **THEN** the command palette is the only global search or ask entry point

### Requirement: N8 Breadcrumb states group, page, detail

The breadcrumb SHALL always state group then page then detail, and each segment SHALL be a
link.

#### Scenario: Breadcrumb segments are links

- **WHEN** a page renders its breadcrumb
- **THEN** it shows group, page, and (where applicable) detail, each as a working link

### Requirement: N9 Back from detail preserves list state

Returning from a detail page SHALL return to the list with its filters intact.

#### Scenario: Filters survive the round trip

- **WHEN** the viewer opens a detail from a filtered list and navigates back
- **THEN** the list is shown with the same filters still applied

### Requirement: O1 One record per real-world thing

Each real-world thing SHALL be one object rendered in multiple places, never duplicated per
surface.

#### Scenario: Same object, one record

- **WHEN** the same document, check, or task appears on two surfaces
- **THEN** both render the one underlying record, not independent copies

### Requirement: O2 Every object carries the standard facets

Every object SHALL have status, owner, provenance, linked objects, and history. There SHALL
be no exceptions; an empty facet SHALL be shown as empty.

#### Scenario: Missing facet renders as empty

- **WHEN** an object lacks a value for a standard facet
- **THEN** the facet is shown as explicitly empty, not omitted

### Requirement: O3 Uniform object-detail anatomy

Every object detail SHALL use the same anatomy in the same order: assertion or description,
relations, evidence, guidance, history.

#### Scenario: Detail order is uniform

- **WHEN** any object detail is opened
- **THEN** its sections appear in the fixed order assertion, relations, evidence, guidance,
  history

### Requirement: O4 Objects open in a drawer or as a full page

Objects SHALL open in a drawer from lists and as a full page from direct links. Both SHALL
render the same record.

#### Scenario: Drawer and page render one record

- **WHEN** an object is opened from a list drawer and from its direct URL
- **THEN** both present the same record with the same content

### Requirement: O5 Edits and deletes name their references

Deleting or editing an object SHALL state, before confirmation, everything that references
it and what will happen to each reference.

#### Scenario: Blast radius shown before confirm

- **WHEN** the viewer initiates a delete or breaking edit
- **THEN** the confirmation names each referencing object and the effect on it

### Requirement: O6 Relations are two-way

Relations SHALL be two-way and visible from both ends: a control lists its checks; a check
names its controls.

#### Scenario: Relation visible from both ends

- **WHEN** two objects are related
- **THEN** each object's detail shows the relation to the other

### Requirement: L1 Default sort is exceptions first

The default list sort SHALL be exceptions first: failing, then due soon, then the rest.

#### Scenario: Failing rows sort to the top

- **WHEN** a list loads with its default sort
- **THEN** failing rows appear first, then due-soon rows, then the remainder

### Requirement: L2 Filters are chips with counts

Filters SHALL be chips with counts. The active filter set SHALL be visible without opening a
menu and SHALL persist per person, per view.

#### Scenario: Active filters are visible and counted

- **WHEN** a list is filtered
- **THEN** the active filters show as chips with counts, visible without opening a menu, and
  they persist for that person and view

### Requirement: L3 Empty group headers disappear

Group headers inside tables SHALL disappear when they have no visible rows.

#### Scenario: Header hidden when its group is empty

- **WHEN** filtering leaves a table group with no visible rows
- **THEN** that group's header is not shown

### Requirement: L4 Bulk actions are itemized, never silent

Every list SHALL support select-all-visible and bulk actions on the selection. Bulk actions
SHALL NOT fail partially in silence; results SHALL be itemized.

#### Scenario: Partial bulk failure is itemized

- **WHEN** a bulk action succeeds for some items and fails for others
- **THEN** the result lists the outcome per item, with no silent partial failure

### Requirement: L5 Truncated lists state the total

Truncated lists SHALL say so: "Showing n of m" with the total, always.

#### Scenario: Truncation shows n of m

- **WHEN** a list shows fewer rows than exist
- **THEN** it states "Showing n of m" with the real total

### Requirement: L6 Row primary action is a verb

A row's primary action SHALL be one click and named as a verb (Fix, Approve, Review,
Upload). It SHALL NOT be "View" when a decision is wanted.

#### Scenario: Actionable row names the decision

- **WHEN** a row needs a decision
- **THEN** its primary action is a one-click verb naming that decision, not "View"

### Requirement: L7 Empty lists teach and offer an action

Empty lists SHALL explain what would appear there and offer the first action.

#### Scenario: Empty state instructs

- **WHEN** a list has no rows
- **THEN** it explains what would appear and offers the first action

### Requirement: S1 One status vocabulary product-wide

There SHALL be one status vocabulary product-wide: Ready/Passing, Failing, Due soon,
Overdue, Drifting/Degraded, Snoozed, Waiting, Draft, Out of scope.

#### Scenario: Status uses the shared vocabulary

- **WHEN** any surface shows a status
- **THEN** it uses a term from the single shared vocabulary

### Requirement: S2 Status is shape plus word

Status SHALL always be shape plus word. Color SHALL be reinforcement, never the only signal.

#### Scenario: Status readable without color

- **WHEN** a status is rendered
- **THEN** it carries a shape and a word, and its meaning survives with color removed

### Requirement: S3 Red is reserved for failing and overdue

Red SHALL be reserved for failing and overdue. Degraded sources and approaching deadlines
SHALL be amber. Informational states SHALL NOT be red.

#### Scenario: Informational state is not red

- **WHEN** a state is degraded, due-soon, or informational
- **THEN** it uses amber or a neutral/info color, and red appears only for failing or overdue

### Requirement: S4 Derived scores move on a scale

A derived score SHALL move on a scale, not a binary switch, and SHALL show its derivation on
demand: which inputs, which weights.

#### Scenario: Score shows its derivation

- **WHEN** the viewer inspects a derived score
- **THEN** the score sits on a scale and can reveal its inputs and weights

### Requirement: S5 Snooze requires a reason and an end date

A snooze SHALL require a reason and an end date, and SHALL appear in the object's history and
the audit trail.

#### Scenario: Snooze is reasoned and dated

- **WHEN** the viewer snoozes an item
- **THEN** a reason and end date are required, and the snooze is recorded in history and the
  audit trail

### Requirement: S6 Nothing passes because its source went quiet

A stale source SHALL degrade every dependent object, with the dependency named. Nothing SHALL
pass because its source went quiet.

#### Scenario: Stale source degrades dependents

- **WHEN** a source becomes stale
- **THEN** each dependent object renders as degraded and names the stale dependency, and none
  are shown as passing on stale data

### Requirement: T1 One task model

There SHALL be one task model: anything with an owner and a due date or SLA is a task,
whatever module created it.

#### Scenario: All actionable items are the same task shape

- **WHEN** any module creates something with an owner and a due date or SLA
- **THEN** it is represented as the one shared task shape

### Requirement: T2 Program and personal queues cannot disagree

Home SHALL show the program queue; My work SHALL show the same queue scoped to the viewer.
The two SHALL NOT disagree on counts for the same scope.

#### Scenario: Counts reconcile for the same scope

- **WHEN** Home and My work are compared for the same scope
- **THEN** their counts match

### Requirement: T3 Every task names its origin object

Every task SHALL name its origin object and link to it in one click.

#### Scenario: Task links to its source

- **WHEN** a task is shown
- **THEN** it names its origin object and reaches it in one click

### Requirement: T4 Completing the condition completes the task

Completing the underlying condition SHALL complete the task automatically, and the change
feed SHALL record it.

#### Scenario: Condition met closes the task

- **WHEN** the condition a task tracks becomes satisfied
- **THEN** the task completes automatically and the change feed records it

### Requirement: T5 External tracker sync is two-way and stamped

Tasks pushed to external trackers SHALL sync state both ways; external state SHALL be stamped
with source and freshness like any automated value.

#### Scenario: External task state is stamped

- **WHEN** a task is mirrored to an external tracker
- **THEN** state changes sync both ways and the external state carries a source-and-freshness
  stamp

### Requirement: T6 Due dates are relative when near, absolute when far

Due-date rendering SHALL be relative when near ("SLA Fri", "3d overdue") and absolute when
far ("30 Jul"). Overdue SHALL always be explicit, never inferred from color.

#### Scenario: Overdue stated in words

- **WHEN** a due date is rendered
- **THEN** it reads relative if near and absolute if far, and overdue is stated in words, not
  by color alone

### Requirement: T7 World-changing decisions stay open until confirmed

A decision that changes the world (revoke access, remediate, offboard) SHALL stay open until
the source confirms the change happened.

#### Scenario: Attested reconciles with observed

- **WHEN** a world-changing decision is marked done
- **THEN** it remains open until the source confirms the change, so attested and true
  reconcile

### Requirement: P1 Automated values carry a source stamp

Every automated value SHALL carry a source stamp: collector or integration name plus age.

#### Scenario: Automated value is stamped

- **WHEN** a machine-collected value is shown
- **THEN** it carries the collector or integration name and its age

### Requirement: P2 Manual values are stamped MANUAL and dated

Manual values SHALL be stamped MANUAL and dated. Manual is a provenance, not the absence of
one.

#### Scenario: Manual value shows provenance

- **WHEN** a value is entered by hand
- **THEN** it is stamped MANUAL with a date

### Requirement: P3 Freshness thresholds are visible

Freshness thresholds SHALL be visible - fresh, aging, stale - with stale rendering as
degraded per S6.

#### Scenario: Freshness state is shown

- **WHEN** an automated value ages
- **THEN** its freshness (fresh, aging, stale) is visible, and stale renders as degraded

### Requirement: P4 Evidence reads identically to owner and auditor

Evidence SHALL show how it was collected and when, and SHALL read identically to the auditor
and the owner.

#### Scenario: Evidence provenance is uniform

- **WHEN** a piece of evidence is viewed
- **THEN** it shows collection method and time, and the same rendering serves owner and
  auditor

### Requirement: P5 Report figures resolve to records with an as-of time

Every figure in a report or export SHALL resolve to the record it came from and SHALL carry
an as-of time. A sent report SHALL lock a snapshot; live pages keep moving.

#### Scenario: Figure resolves and is dated

- **WHEN** a figure appears in a report or export
- **THEN** it resolves to its source record and carries an as-of time, and a sent report is a
  locked snapshot

### Requirement: X1 Default delivery is a digest

Default notification delivery SHALL be a digest. Real-time interrupts SHALL be opt-in per
category and reserved for failing, overdue, and external requests.

#### Scenario: Real-time is opt-in

- **WHEN** notifications are configured
- **THEN** the default is a digest and real-time interrupts are opt-in per category

### Requirement: X2 One event, one notification

One event SHALL produce one notification. State changes on the same object within a window
SHALL coalesce.

#### Scenario: Rapid changes coalesce

- **WHEN** an object changes state several times within the window
- **THEN** the notifications coalesce into one

### Requirement: X3 Notifications deep-link and can be muted at source

Every notification SHALL deep-link to the object and SHALL be mutable at the object,
category, or channel level from the notification itself.

#### Scenario: Mute from the notification

- **WHEN** the viewer opens a notification
- **THEN** it deep-links to the object and offers muting at the object, category, or channel
  level

### Requirement: X4 Notification rules live in Settings and preview their effect

Notification rules SHALL live in Settings in one place, per person, and SHALL preview their
effect ("this would have sent 4 messages last week").

#### Scenario: Rule previews its volume

- **WHEN** the viewer edits a notification rule
- **THEN** the rule lives in Settings and previews how many messages it would have sent

### Requirement: E1 External surfaces carry a persistent frame

Outward-facing surfaces (trust center, questionnaire responses, auditor views) SHALL carry a
persistent visual frame that internal pages never use.

#### Scenario: External surface is unmistakable

- **WHEN** an outward-facing surface is rendered
- **THEN** it carries a persistent external frame absent from internal pages

### Requirement: E2 Nothing publishes silently

Nothing SHALL publish silently. External changes SHALL queue for approval, and every publish
SHALL be logged with who, what, and when.

#### Scenario: Publish is approved and logged

- **WHEN** an external change is published
- **THEN** it passed an approval queue and is logged with who, what, and when

### Requirement: E3 Per-section visibility is explicit

Visibility per section SHALL be explicit - Public, NDA gate, or Hidden - shown as a label
next to its toggle, not encoded in toggle position alone.

#### Scenario: Visibility labelled, not positional

- **WHEN** a section's visibility is set
- **THEN** the current state shows as a Public, NDA gate, or Hidden label beside the toggle

### Requirement: E4 Auditor access is scoped, expiring, and logged

Auditor access SHALL be scoped and expiring, and the log of what was viewed SHALL be
available to the account owner.

#### Scenario: Auditor view is bounded and audited

- **WHEN** an auditor is granted access
- **THEN** the grant is scoped and expiring, and the owner can see what was viewed

### Requirement: E5 Assistant-drafted external answers are held for approval

Assistant-drafted external answers SHALL be held for human approval and SHALL cite their
knowledge-base sources.

#### Scenario: Drafted answer awaits approval

- **WHEN** the assistant drafts an external answer
- **THEN** it is held for human approval and cites its sources

### Requirement: E6 Externally visible surfaces offer view-as

Any surface another party can see SHALL offer a view-as switch, so internal users step into
the exact external perspective in one click; exposure SHALL NOT be guessed at.

#### Scenario: One-click external perspective

- **WHEN** an internal user opens a surface an external party can see
- **THEN** a view-as switch shows the exact external perspective in one click, so the internal
  user sees what the external party sees rather than guessing at the exposure

### Requirement: F1 Errors state what, which, and next

Errors SHALL state what happened, to which item, and the next step. Multi-item operations
SHALL report per item.

#### Scenario: Error names item and next step

- **WHEN** an action fails
- **THEN** the message states what failed, to which item, and the next step

### Requirement: F2 Nothing is silently dropped

Nothing SHALL be silently dropped. If 1 of 2 uploads fails, the form SHALL say so before the
person leaves the page.

#### Scenario: Partial upload failure is surfaced

- **WHEN** a multi-item submit partially fails
- **THEN** the form reports the failure before the person leaves the page

### Requirement: F3 Destructive actions name their blast radius

Destructive actions SHALL name their blast radius (per O5) and SHALL require confirmation
proportional to it.

#### Scenario: Confirmation scales with impact

- **WHEN** the viewer triggers a destructive action
- **THEN** the confirmation names what is affected and its friction matches the impact

### Requirement: F4 Drafts autosave and dirty forms warn once

Drafts SHALL autosave and say so. Leaving a dirty form SHALL warn once, specifically.

#### Scenario: Dirty form warns on leave

- **WHEN** the viewer leaves a form with unsaved changes
- **THEN** a single specific warning appears, and drafts autosave with a saved indicator

### Requirement: A1 AA contrast minimum

Text and interactive elements SHALL meet WCAG AA contrast minimum. The automated check is owned by the `web-accessibility` capability.

#### Scenario: Contrast clears AA

- **WHEN** a view is audited
- **THEN** text and interactive elements meet WCAG AA contrast

### Requirement: A2 Full keyboard path with visible focus

There SHALL be a full keyboard path through nav, tables, drawers, and the palette. Focus
SHALL always be visible.

#### Scenario: Everything reachable by keyboard

- **WHEN** the viewer navigates by keyboard only
- **THEN** nav, tables, drawers, and the palette are all reachable with visible focus

### Requirement: A3 Reduced motion disables non-essential animation

A reduced-motion preference SHALL disable all non-essential animation.

#### Scenario: Reduced motion honored

- **WHEN** the viewer prefers reduced motion
- **THEN** non-essential animation, including drawer and palette transitions, is disabled

### Requirement: A4 Adequate hit targets

Hit targets SHALL be at least 32px in either dimension in dense tables, and 44px elsewhere.

#### Scenario: Targets meet the minimum

- **WHEN** interactive targets are measured
- **THEN** they are at least 32px in dense tables and at least 44px elsewhere

### Requirement: A5 Drawers and dialogs trap and restore focus

Drawers and dialogs SHALL trap focus, restore it on close, and close on Escape.

#### Scenario: Focus trapped and restored

- **WHEN** a drawer or dialog opens and then closes
- **THEN** focus is trapped inside while open, Escape closes it, and focus returns to the
  opener

### Requirement: A6 Light and dark ship together from one token set

Light and dark SHALL ship together from one token set. Every component SHALL meet AA in both,
and status semantics SHALL NOT change with theme. Theme SHALL follow the system by default
and be a personal setting, not a workspace one; external surfaces keep their own theme.

#### Scenario: Both themes meet AA with stable semantics

- **WHEN** a component is rendered in light and in dark
- **THEN** both meet AA, status semantics are unchanged, and the theme follows the system
  unless the person overrides it

#### Scenario: Theme is personal, not per workspace

- **WHEN** a person with a chosen theme switches to a different workspace
- **THEN** their theme is unchanged, because theme is a personal setting, not a workspace one

#### Scenario: External surfaces keep their own theme

- **WHEN** an external surface is shown regardless of the viewer's chrome theme
- **THEN** it keeps its own theme rather than inheriting the internal chrome theme

### Requirement: W1 Sentence case everywhere

Text SHALL use sentence case everywhere except mono provenance stamps and group eyebrows.

#### Scenario: Headings are sentence case

- **WHEN** a heading or button label is written
- **THEN** it is sentence case, except mono provenance stamps and group eyebrows

### Requirement: W2 Buttons are verbs that survive the round trip

Buttons SHALL be verbs that survive the round trip: Publish leads to Published.

#### Scenario: Verb label resolves to its result

- **WHEN** a button action completes
- **THEN** the resulting state reads as the completed form of the button's verb

### Requirement: W3 Counts are honest

Counts SHALL be honest: "12 failing", not "some issues". A vague plural SHALL NOT stand in
when the number is known.

#### Scenario: Known count is stated

- **WHEN** a quantity is known
- **THEN** the exact number is stated rather than a vague plural

### Requirement: W4 Explanations live where the confusion happens

Explanations SHALL live where the confusion happens (an inline hint or a guidance box), not
in a separate help article about the relationship between two pages.

#### Scenario: Guidance is inline

- **WHEN** a screen is likely to confuse
- **THEN** the explanation appears inline as a hint or guidance box, not off in a help article

### Requirement: W5 Never "error occurred"

The product SHALL NOT say "error occurred". It SHALL say what happened and what to do now.

#### Scenario: Error says what and what next

- **WHEN** an error is shown to the viewer
- **THEN** it states what happened and the next step, never a bare "error occurred"

