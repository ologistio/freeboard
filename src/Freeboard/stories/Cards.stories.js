// Components / Cards & panels. The container primitives from the prototype: the
// panel (the default flat container, with an optional head and a padded body),
// the stat card (a labelled metric for dashboards), and the link card (a
// navigational summary that lifts on hover, plus a ghost add-affordance).
// Reference only: story-scoped literal values, app.css untouched.

import { SANS, MONO, page, section, example, codeView } from "./_ui.js";

export default {
    title: "Components/Cards & panels",
    parameters: { layout: "fullscreen" },
};

const CSS = `
  .fb-panel { background:#fff; border:1px solid #e0e3dc; border-radius:10px; }
  .fb-panel__head { display:flex; align-items:center; justify-content:space-between; gap:12px; padding:12px 16px; border-bottom:1px solid #e0e3dc; flex-wrap:wrap; }
  .fb-panel__head h2 { font:600 14.5px ${SANS}; color:#1a1d1c; margin:0; }
  .fb-panel__meta { font-size:12px; color:#616a66; }
  .fb-panel__body { padding:16px; font-size:13px; color:#616a66; line-height:1.5; }

  .fb-stat { background:#fff; border:1px solid #e0e3dc; border-radius:10px; padding:13px 15px; }
  .fb-stat__label { font:600 9.5px ${MONO}; letter-spacing:.13em; text-transform:uppercase; color:#8a938e; margin-bottom:8px; }
  .fb-stat__main { font:700 19px ${SANS}; letter-spacing:-.01em; color:#1a1d1c; }
  .fb-stat__main small { font:500 12px ${SANS}; color:#616a66; margin-left:4px; }
  .fb-stat__sub { font-size:12px; color:#616a66; margin-top:3px; }

  .fb-bar { height:5px; background:#eceeea; border-radius:99px; margin-top:10px; overflow:hidden; }
  .fb-bar > i { display:block; height:100%; background:#1b7a4e; border-radius:99px; }
  .fb-bar > i.warn { background:#96690a; }

  .fb-linkcard { display:block; width:100%; text-align:left; background:#fff; border:1px solid #e0e3dc; border-radius:10px; padding:15px 16px; cursor:pointer; transition:border-color .12s, box-shadow .12s; font-family:${SANS}; }
  .fb-linkcard:hover, .fb-linkcard.is-hover { border-color:#c9cec5; box-shadow:0 1px 2px rgba(26,29,28,.05), 0 4px 14px rgba(26,29,28,.05); }
  .fb-linkcard__top { display:flex; align-items:center; justify-content:space-between; gap:8px; margin-bottom:6px; }
  .fb-linkcard h3 { font:700 14.5px ${SANS}; color:#1a1d1c; margin:0; }
  .fb-linkcard__big { font:700 24px ${SANS}; letter-spacing:-.02em; color:#1a1d1c; margin:6px 0 2px; }
  .fb-linkcard__sub { font-size:12px; color:#616a66; }
  .fb-linkcard--ghost { border-style:dashed; color:#616a66; display:flex; align-items:center; justify-content:center; min-height:120px; font:600 13px ${SANS}; }
  .fb-linkcard--ghost:hover { color:#3d36a3; border-color:#4f46c8; box-shadow:none; }
  .fb-cardtag { font:500 11px ${SANS}; color:#96690a; background:#f6eed6; border-radius:4px; padding:1px 7px; white-space:nowrap; }
`;

const eyebrow = "Components / Cards & panels";
const onField = (html) => `<div style="background:#f1f2ee;padding:20px;border-radius:8px;width:100%">${html}</div>`;

const PANEL = `<div class="fb-panel">
  <div class="fb-panel__head">
    <h2>Recent activity</h2>
    <span class="fb-panel__meta">audit trail</span>
  </div>
  <div class="fb-panel__body">A panel is the default container: a hairline border, a 10px radius, an optional head with a title and meta, and a padded body. Tables and lists sit inside one.</div>
</div>`;

const STAT = `<div class="fb-stat">
  <div class="fb-stat__label">SOC 2</div>
  <div class="fb-stat__main">96%<small>ready</small></div>
  <div class="fb-stat__sub">82 of 85 controls</div>
  <div class="fb-bar"><i style="width:96%"></i></div>
</div>`;

const STAT_ROW = `<div style="display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:12px">
  <div class="fb-stat"><div class="fb-stat__label">SOC 2</div><div class="fb-stat__main">96%<small>ready</small></div><div class="fb-stat__sub">82 of 85 controls</div><div class="fb-bar"><i style="width:96%"></i></div></div>
  <div class="fb-stat"><div class="fb-stat__label">Monitoring</div><div class="fb-stat__main">1,412<small>checks</small></div><div class="fb-stat__sub">12 failing</div><div class="fb-bar"><i style="width:99%"></i></div></div>
  <div class="fb-stat"><div class="fb-stat__label">Risk</div><div class="fb-stat__main">24<small>open</small></div><div class="fb-stat__sub">2 critical</div><div class="fb-bar"><i class="warn" style="width:62%"></i></div></div>
</div>`;

const LINKCARD = `<button type="button" class="fb-linkcard" style="max-width:280px">
  <div class="fb-linkcard__top">
    <h3>SOC 2 Type II</h3>
    <span class="fb-cardtag">Audit in 21d</span>
  </div>
  <div class="fb-linkcard__big">96%</div>
  <div class="fb-linkcard__sub">82 of 85 controls ready</div>
  <div class="fb-bar"><i style="width:96%"></i></div>
</button>`;

const GHOST = `<button type="button" class="fb-linkcard fb-linkcard--ghost" style="max-width:280px">+ Add a framework</button>`;

const stateLabel = (t) => `<div style="font:600 11px ${MONO};color:#8a938e;margin-bottom:7px;text-transform:uppercase;letter-spacing:.08em">${t}</div>`;
const LINKCARD_HOVER = LINKCARD.replace('fb-linkcard"', 'fb-linkcard is-hover"');
const LINKCARD_STATES = `<div style="display:flex;gap:18px;flex-wrap:wrap">
  <div>${stateLabel("Rest")}${LINKCARD}</div>
  <div>${stateLabel("Hover")}${LINKCARD_HOVER}</div>
</div>`;

export const Panel = {
    render: () =>
        page({
            eyebrow, title: "Cards & panels", css: CSS,
            lead: "The panel is the container everything sits in: a flat, border-defined surface with an optional head (title plus meta) and a padded body. Elevation is reserved for cards that lift on hover, not for panels.",
            body:
                section("Panel", "Head with a title and a meta note, then a padded body. Tables, lists, and forms live inside the body.",
                    example("Panel", "", PANEL, onField(PANEL))),
        }),
};

export const StatCard = {
    render: () =>
        page({
            eyebrow, title: "Stat card", css: CSS,
            lead: "A labelled metric for dashboards: a mono eyebrow, a large figure with a unit, a supporting line, and an optional progress bar. The bar turns amber when the number is off target.",
            body:
                section("Stat card", "One metric per card. The unit sits small beside the figure; the sub line carries the exception.",
                    example("Single", "", STAT, onField(STAT))) +
                section("In context", "A row of stat cards forms the posture summary on a dashboard.",
                    example("Posture row", "", STAT_ROW, onField(STAT_ROW))),
        }),
};

export const LinkCard = {
    render: () =>
        page({
            eyebrow, title: "Link card", css: CSS,
            lead: "A navigational summary card: a title, a headline figure, a supporting line, and a hover lift that signals it is clickable. It is a real button, so it is keyboard reachable and announces its content.",
            body:
                section("Link card", "Title and a status tag up top, the headline figure below. The copyable markup is the rest state; hover lifts it with the raised shadow (shown right, and live on the card).",
                    example("Default", "", LINKCARD, onField(LINKCARD_STATES))) +
                section("Ghost", "A dashed add-affordance for creating a new item. Same footprint, low emphasis.",
                    example("Add", "", GHOST, onField(GHOST))),
        }),
};
