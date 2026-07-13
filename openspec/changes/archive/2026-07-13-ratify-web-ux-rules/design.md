## Context

Freeboard's job is to make a compliance posture legible and defensible: evidence collected
automatically, checks scored against it, and a trail an auditor can trust. The interface
either makes that legible or buries it. The design system in `src/Freeboard/stories/` states
the interface rules that keep it legible, and `UxPhilosophy.mdx` records why each rule
exists - the failure mode of compliance tooling it defends against. This document carries
that rationale so the ratified requirements in `specs/web-ux-conventions/spec.md` are applied
with judgment, not by rote.

The rules answer specific ways compliance tooling degrades as it grows:

- **Nav accretion** - modules sprout their own settings and report pages until the menu is a
  museum of past decisions. Answered by N1-N5.
- **Disagreeing task surfaces** - two lists of "what to do" showing different counts, so
  neither is trusted. Answered by T1-T2.
- **Duplicate records** - the same evidence shown as two unrelated things on two pages.
  Answered by O1, O4, O6.
- **Notification noise** - so many alerts the signal disappears. Answered by X1-X4.
- **Overwhelm and depth-hiding** - important flows buried inside a page, unreachable from the
  menu. Answered by N1, L1, S3.
- **Opaque derived numbers** - a score that jumps from bad to good in one step and reflects
  nothing between. Answered by S4.
- **Silent failure** - an upload that half-worked, discovered later by someone else. Answered
  by F1-F2, S6.

## Goals / Non-Goals

**Goals:**

- Make the numbered ruleset durable and citable: one requirement per rule, keyed by its
  stable ID, so a reviewer or test can name exactly what a screen violates.
- State every rule unconditionally as a normative requirement, so the ruleset reads as the
  standing contract independent of what the current codebase happens to implement.
- Give the UI migration (`tmp/ui-migration-critical-path.md`, phases P1-P7) a fixed target
  to build and test against, independent of the mutable Storybook prototype.

**Non-Goals:**

- Implementing any rule. This change ratifies the contract; the pixels come from later
  migration phases.
- Rewriting or re-deriving the rules. The transcription is faithful to the ratified set.
- Building the not-yet-built surfaces (task queue, notifications, trust center, assistant).

## Decisions

**One requirement per rule, ID in the title.** The alternative - one requirement per section
(Navigation, Lists, ...) with rules as bullets - reads shorter but breaks citation: "this
breaks L4" must resolve to exactly one requirement. Per-rule requirements keep the IDs as
first-class, testable units. The cost is a long spec (66 requirements: 65 rules plus the
purpose-and-citation meta-requirement); acceptable, because
the ruleset is the whole point of the capability.

**Rules are stated unconditionally; no aspirational label.** A rule is a rule regardless of
the current implementation state, so no requirement carries an enforceability tag. Whether a
surface exists today to exercise a rule is a property of the current codebase, not of the
rule's authority; that scoping belongs to the P7 conformance suite and the migration doc as
non-normative guidance (see Migration Plan), not to a per-rule tag. The P7 suite decides what
to test by looking at which surfaces exist, and adds coverage as surfaces land, without any
change to the rules themselves.

**Accessibility rules stay here as design requirements; enforcement stays in
`web-accessibility`.** A1-A6 are part of the numbered ruleset, so they belong in this
capability for citation. But the axe-core audit already lives in `web-accessibility`.
Duplicating the automated contract would create two homes that can disagree. Decision: state
A1-A6 here, and point the checks axe can measure - A1 (contrast) and A6's AA-in-both-themes
part - at `web-accessibility`, which this change does not modify. The rest (A2 keyboard path,
A3 reduced motion, A5 focus trap and restore, A6 semantic stability and personal setting) axe
does not measure,
and A4's Freeboard-specific hit-target thresholds (32px in dense tables, 44px elsewhere) are
not what axe's generic target-size rule enforces, so `web-ux-conventions` owns those
conformance checks. Ownership is stated once per rule, so nothing is enforced in two places
that can disagree.

**Rule IDs are frozen.** Downstream citations (code comments must not reference process, but
tests and reviews cite rule IDs; the migration doc cites them) depend on stable IDs. The
spec states the IDs SHALL NOT be renumbered. New rules get new IDs; they never reuse or
shift existing ones.

**Repeatable controls have one authoring mechanism, tiered by what the control needs.**
Several rules describe controls that carry an invariant or behaviour, not just a look:
S2/S3 (status is shape plus word; red reserved for fail/overdue), N6 (nav badges show only
actionable counts), O3 (uniform object anatomy), and A2/A5 (full keyboard path; drawers and
dialogs trap and restore focus). A CSS class carries look only, so if these controls are
left as raw markup their invariants get re-authored on every page and enforced nowhere. The
migration therefore builds controls three ways, chosen by what the control needs:

- **Tag helpers** for inline marks that carry an invariant and little or no data (status
  seal, provenance stamp, tag, badge, due date, chip, owner). The tone is a typed C# enum,
  so S2/S3 cannot be violated at author time; the markup and ARIA live in one unit-tested
  unit; the call site reads as the `fb-*` vocabulary. The infrastructure is already wired
  (`@addTagHelper *, Freeboard` in `_ViewImports.cshtml`).
- **View components** for shell pieces that need request data or authorization: the nav rail
  with its N6 counts, the breadcrumb, the workspace switcher, and the command-palette page
  index. They run inside the request with DI access to the authorization and organisation
  resolvers, and one nav map feeds the rail, the palette, and the breadcrumb. This matches
  the existing `OrgSelector` view component.
- **Alpine** for client behaviour (drawer and palette focus management, theme toggle) inside
  the rendered markup, which is the app's existing interactivity layer.

`app.css` stays the single style source: tag helpers and view components emit the `fb-*`
classes, they do not restyle. Partials remain for one-off page fragments that carry no
invariant and no reuse pressure.

The alternatives were rejected as second rendering runtimes added to do what the existing
stack already covers. Razor Components (Blazor) need a SignalR circuit (Server) or a WASM
runtime download (WebAssembly) to be interactive; static server-side rendering is more
machinery than a view component for identical HTML, with a cliff at the first interactive
need. Native web components are client-defined, in an app that runs almost no JS, and their
shadow DOM does not inherit the Tailwind tokens, which defeats the single-token-source goal
(A6). This change records the mechanism as the migration's design rule and builds no
controls; it is a refinement of migration phases P2 (components) and P3 (app shell), where
"rebuild `fb-*`" splits into `fb-*` CSS (look) plus this control layer (structure, invariant,
behaviour). It also sharpens the P7 drift check: the tag helper is the single Razor
implementation, so drift compares its rendered classes against the Storybook marks while
`app.css` stays the only style definition.

## Risks / Trade-offs

- **Recorded rules read as "already built".** -> The proposal's Non-goals and the Migration
  Plan's conformance-scope note make explicit that recording a rule is not implementing it;
  P7 tests a rule only where a surface exists to exercise it.
- **The spec drifts from Storybook.** -> The migration's P7 adds a drift check between the
  `fb-*` classes and the components layer; this capability is the ratified source, and the
  Storybook becomes the visual reference that must track it, not the other way round.
- **Long spec is tedious to maintain.** -> Rules change rarely and by ID; edits are local to
  one requirement. The length buys precise citation, which is the capability's reason to
  exist.
- **A/accessibility split confuses ownership.** -> Each A-rule names its check owner exactly:
  `web-accessibility` (axe) for A1 and A6's contrast part, `web-ux-conventions` for the rest
  (A2, A3, A5, A6 semantics and personal setting, and A4's specific 32/44px thresholds, which axe's generic
  target-size rule does not enforce). One owner per rule, one citation home.

## Migration Plan

Documentation-only change. On archive, `specs/web-ux-conventions/spec.md` folds into
`openspec/specs/web-ux-conventions/`. No deploy, no rollback, no code path. The migration
phases P1-P7 reference the resulting requirement IDs; nothing depends on this change at
runtime.

**Conformance scope today (non-normative).** The following rules have no current surface to
exercise, so the P7 conformance suite will not test them yet; it adds coverage as each
surface lands. This is scoping guidance for the suite, not a statement about the rules'
authority - the rules stand regardless. No current surface exists for the task queue (T1-T5,
T7), due-date rendering (T6), notifications (X1-X4), external surfaces (E1-E6), derived
scores (S4), snooze (S5), two-way relations (O6), evidence method-and-time rendering (P4),
report snapshots (P5), draft autosave (F4), and bulk actions (L4). Surfaces that do exist
today - the shell, lists,
status, provenance stamps and freshness (P1-P3, collector staleness is built), forms,
accessibility, and language - are testable now. N7's assistant-ask clause is not testable
until the assistant exists; its single-search-surface invariant is.

## Open Questions

- T6 (due-date rendering) and P4 (evidence method-and-time, read identically to owner and
  auditor) have no current surface to exercise them, so P7 does not test them yet (see the
  Migration Plan conformance-scope note). The Statement of Applicability shows collector
  status words, not due dates; the evidence-collector register shows collector configuration,
  not a piece of evidence's collection method and time, and its status read discards the
  collection timestamp. No auditor view exists. The near migration (P5) restyles these
  surfaces but adds neither, so P7 gains T6 or P4 coverage only once a surface renders exactly
  what the rule requires. The freshness/provenance rules that the built collector-staleness
  surface does exercise are P1-P3, which P7 can test now.
- Whether the accessibility requirements should eventually move wholesale into
  `web-accessibility` rather than being stated in both. Deferred: keeping them here preserves
  the complete numbered ruleset in one capability.
