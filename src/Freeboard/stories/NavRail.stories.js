// Components / Nav rail. The left sidebar from the prototype: brand block, the
// Ctrl-K search entry, two-level grouped navigation with actionable counts, an
// active-item indicator (inset brand bar), and a workspace switcher in the foot.
// Groups follow the product IA (Comply, Risk, Trust, Resources, Platform).
// Reference only: story-scoped literal values, app.css untouched.

import { SANS, MONO, page, section, example } from "./_ui.js";

export default {
    title: "Components/Nav rail",
    parameters: { layout: "fullscreen" },
};

const CSS = `
  .fb-rail { width:236px; height:600px; display:flex; flex-direction:column; background:#f1f2ee; border:1px solid #e0e3dc; border-radius:10px; padding:14px 10px 10px; font-family:${SANS}; }
  .fb-brand { display:flex; align-items:baseline; gap:8px; padding:2px 8px 12px; }
  .fb-mark { font:600 13px ${MONO}; color:#f1f2ee; background:#1a1d1c; border-radius:6px; padding:3px 7px; letter-spacing:.02em; }
  .fb-name { font:700 15px ${SANS}; letter-spacing:-.01em; color:#1a1d1c; }
  .fb-rev { font:500 9.5px ${MONO}; color:#3d36a3; border:1px dashed #4f46c8; border-radius:4px; padding:1px 5px; text-transform:uppercase; letter-spacing:.08em; }

  .fb-search-entry { display:flex; align-items:center; justify-content:space-between; width:100%; padding:7px 10px; margin:0 0 10px; background:#fff; border:1px solid #e0e3dc; border-radius:6px; color:#8a938e; font-size:13px; text-align:left; cursor:pointer; }
  .fb-search-entry:hover { border-color:#c9cec5; color:#616a66; }
  .fb-kbd { font:500 10px ${MONO}; color:#616a66; border:1px solid #e0e3dc; border-bottom-width:2px; border-radius:4px; padding:1px 5px; background:#fafbf8; }

  .fb-navwrap { flex:1; overflow-y:auto; display:flex; flex-direction:column; }
  .fb-navgroup { font:600 9.5px ${MONO}; letter-spacing:.14em; color:#8a938e; text-transform:uppercase; padding:14px 10px 4px; }
  .fb-navitem { display:flex; align-items:center; justify-content:space-between; gap:8px; width:100%; padding:6px 10px; border:none; background:none; border-radius:6px; font:400 13.5px ${SANS}; color:#616a66; text-align:left; cursor:pointer; }
  .fb-navitem:hover, .fb-navitem.is-hover { background:rgba(26,29,28,.05); color:#1a1d1c; }
  .fb-navitem.active { background:#fff; color:#1a1d1c; font-weight:600; box-shadow:inset 3px 0 0 #4f46c8, 0 1px 2px rgba(26,29,28,.06); }
  .fb-navitem:focus-visible { outline:2px solid #4f46c8; outline-offset:-2px; }
  .fb-navcount { font:500 10.5px ${MONO}; color:#b3372d; background:#f9e9e6; border-radius:99px; padding:0 7px; line-height:17px; flex:none; }
  .fb-navcount.calm { color:#616a66; background:#eceeea; }

  .fb-rail-foot { margin-top:auto; padding-top:12px; border-top:1px solid #e0e3dc; }
  .fb-wspick { display:flex; align-items:center; gap:8px; width:100%; padding:7px 10px; border:none; background:none; border-radius:6px; font-size:12.5px; color:#616a66; text-align:left; cursor:pointer; }
  .fb-wspick:hover { background:rgba(26,29,28,.05); }
  .fb-dot { width:8px; height:8px; border-radius:2px; background:#4f46c8; flex:none; }
  .fb-wspick b { color:#1a1d1c; font-weight:600; }
`;

const eyebrow = "Components / Nav rail";

const NAVCLICK = "var r=this.closest('.fb-rail');var it=r.querySelectorAll('.fb-navitem');for(var i=0;i<it.length;i++){it[i].classList.remove('active');it[i].removeAttribute('aria-current');}this.classList.add('active');this.setAttribute('aria-current','page');";

const ni = (label, count, tone, active) => {
    const badge = count ? `<span class="fb-navcount${tone === "calm" ? " calm" : ""}" aria-hidden="true">${count}</span>` : "";
    const al = count ? ` aria-label="${label}, ${count}${tone === "calm" ? "" : " need action"}"` : "";
    return `<button type="button" class="fb-navitem${active ? " active" : ""}"${active ? ' aria-current="page"' : ""}${al} onclick="${NAVCLICK}"><span>${label}</span>${badge}</button>`;
};
const grp = (label) => `<div class="fb-navgroup">${label}</div>`;

const RAIL_FULL = `<nav class="fb-rail" aria-label="Primary">
  <div class="fb-brand"><span class="fb-mark">F</span><span class="fb-name">Freeboard</span><span class="fb-rev">CE+</span></div>
  <button type="button" class="fb-search-entry" aria-haspopup="dialog"><span>Search or ask...</span><kbd class="fb-kbd">Ctrl K</kbd></button>
  <div class="fb-navwrap">
    ${ni("Home", null, null, true)}${ni("My work", "7", "hot")}
    ${grp("Comply")}${ni("Frameworks")}${ni("Controls")}${ni("Tests", "12", "hot")}${ni("Policies")}${ni("Evidence")}${ni("Audits")}
    ${grp("Risk")}${ni("Risks")}${ni("Vendors", "4", "calm")}${ni("Access reviews")}
    ${grp("Trust")}${ni("Trust Center")}${ni("Questionnaires", "2", "calm")}
    ${grp("Resources")}${ni("People")}${ni("Devices")}${ni("Infrastructure")}${ni("Vulnerabilities")}
    ${grp("Platform")}${ni("Reports")}${ni("Integrations")}${ni("Settings")}
  </div>
  <div class="fb-rail-foot"><button type="button" class="fb-wspick"><span class="fb-dot"></span><span><b>Your org</b> / Production</span></button></div>
</nav>`;

const RAIL_SNIPPET = `<nav class="fb-rail" aria-label="Primary">
  <div class="fb-brand">
    <span class="fb-mark">F</span><span class="fb-name">Freeboard</span><span class="fb-rev">CE+</span>
  </div>
  <button type="button" class="fb-search-entry" aria-haspopup="dialog">
    <span>Search or ask...</span><kbd class="fb-kbd">Ctrl K</kbd>
  </button>
  <div class="fb-navwrap">
    <button type="button" class="fb-navitem active" aria-current="page"><span>Home</span></button>
    <button type="button" class="fb-navitem" aria-label="My work, 7 need action"><span>My work</span><span class="fb-navcount" aria-hidden="true">7</span></button>

    <div class="fb-navgroup">Comply</div>
    <button type="button" class="fb-navitem"><span>Controls</span></button>
    <button type="button" class="fb-navitem" aria-label="Tests, 12 need action"><span>Tests</span><span class="fb-navcount" aria-hidden="true">12</span></button>
    <!-- ...remaining items and groups... -->
  </div>
  <div class="fb-rail-foot">
    <button type="button" class="fb-wspick"><span class="fb-dot"></span><span><b>Your org</b> / Production</span></button>
  </div>
</nav>
<!-- Active-item switching (aria-current follows the active item) is wired in code. -->`;

const ITEMS = `<div style="background:#fff;border:1px solid #e0e3dc;border-radius:10px;padding:14px;display:inline-block">
  <div class="fb-rail" style="height:auto;border:none;background:#f1f2ee;border-radius:8px;padding:8px 8px">
    <div class="fb-navwrap" style="overflow:visible">
      <button type="button" class="fb-navitem"><span>Rest</span></button>
      <button type="button" class="fb-navitem is-hover"><span>Hover</span></button>
      <button type="button" class="fb-navitem active"><span>Active</span></button>
      <button type="button" class="fb-navitem"><span>Tests</span><span class="fb-navcount">12</span></button>
      <button type="button" class="fb-navitem"><span>Vendors</span><span class="fb-navcount calm">4</span></button>
    </div>
  </div>
</div>`;

export const NavRail = {
    render: () =>
        page({
            eyebrow, title: "Nav rail", css: CSS,
            lead: "The primary navigation: a brand block, the single search-and-ask entry (Ctrl-K), two levels of grouped navigation, and a workspace switcher in the foot. Groups are jobs the company shares - Comply, Risk, Trust, Resources, Platform - never module names.",
            body:
                section("Rail", "Home and My work sit above the groups; the active item carries an inset brand bar and a panel background. Click an item to move it.",
                    example("Primary navigation", "", RAIL_SNIPPET, RAIL_FULL)),
        }),
};

export const Items = {
    render: () =>
        page({
            eyebrow, title: "Items", css: CSS,
            lead: "The nav item and its counts. Only actionable counts badge, and only in red; passing and total counts never nag.",
            body:
                section("Item states", "Rest, hover, and active. The active item gains weight, a panel ground, and the inset brand bar.",
                    ITEMS) +
                section("Counts", "Two count tones, one rule.",
                    `<div style="background:#fafbf8;border:1px dashed #c9cec5;border-radius:6px;padding:12px 14px;font-size:12.5px;color:#616a66;line-height:1.6;max-width:76ch">
                      A <b>red count</b> means items need action (failing checks, work waiting on you). A <b>calm count</b> is a neutral tally (open vendors, questionnaires in flight). A passing or total count never badges - the nav shows what is wrong, not what is fine. Give each badged item an <code>aria-label</code> that carries the meaning ("Tests, 12 need action"), and mark the number itself <code>aria-hidden</code> so it is not read twice.
                    </div>`),
        }),
};
