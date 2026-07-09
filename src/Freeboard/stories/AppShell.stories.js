// Compositions / App shell. The full app frame from the prototype: the nav rail
// on the left, a top bar (breadcrumb, audit countdown, theme and notification
// icons, account), and a scrolling main that hosts a page - here the vendor list.
// The frame everything else renders inside. Shown in a fixed-height window so it
// reads as the app within the canvas. Reference only.

import { compPage } from "./_marks.js";

export default {
    title: "Compositions/App shell",
    parameters: { layout: "fullscreen" },
};

const NAVCLICK = "var r=this.closest('.fb-rail');var it=r.querySelectorAll('.fb-navitem');for(var i=0;i<it.length;i++){it[i].classList.remove('active');it[i].removeAttribute('aria-current');}this.classList.add('active');this.setAttribute('aria-current','page');";
const CHIP = "var p=this.parentNode;var c=p.querySelectorAll('.fb-chip');for(var i=0;i<c.length;i++)c[i].classList.remove('on');this.classList.add('on')";

const ni = (label, count, tone, active) => {
    const badge = count ? `<span class="fb-navcount${tone === "calm" ? " calm" : ""}" aria-hidden="true">${count}</span>` : "";
    const al = count ? ` aria-label="${label}, ${count}${tone === "calm" ? "" : " need action"}"` : "";
    return `<button type="button" class="fb-navitem${active ? " active" : ""}"${active ? ' aria-current="page"' : ""}${al} onclick="${NAVCLICK}"><span>${label}</span>${badge}</button>`;
};
const grp = (label) => `<div class="fb-navgroup">${label}</div>`;

const RAIL = `<nav class="fb-rail" aria-label="Primary">
    <div class="fb-brand"><span class="fb-mark">F</span><span class="fb-name">Freeboard</span><span class="fb-rev">CE+</span></div>
    <button type="button" class="fb-search-entry" aria-haspopup="dialog"><span>Search or ask...</span><kbd class="fb-kbd">Ctrl K</kbd></button>
    <div class="fb-navwrap">
      ${ni("Home", null, null, false)}${ni("My work", "7", "hot")}
      ${grp("Comply")}${ni("Frameworks")}${ni("Controls")}${ni("Tests", "12", "hot")}${ni("Policies")}${ni("Evidence")}${ni("Audits")}
      ${grp("Risk")}${ni("Risks")}${ni("Vendors", null, null, true)}${ni("Access reviews")}
      ${grp("Trust")}${ni("Trust Center")}${ni("Questionnaires", "2", "calm")}
      ${grp("Resources")}${ni("People")}${ni("Devices")}${ni("Infrastructure")}${ni("Vulnerabilities")}
      ${grp("Platform")}${ni("Reports")}${ni("Integrations")}${ni("Settings")}
    </div>
    <div class="fb-rail-foot"><button type="button" class="fb-wspick"><span class="fb-dot"></span><span><b>Your org</b> / Production</span></button></div>
  </nav>`;

const MOON = `<svg width="15" height="15" viewBox="0 0 16 16" fill="currentColor" aria-hidden="true"><path d="M13.8 10.4A6.5 6.5 0 0 1 5.6 2.2a6.5 6.5 0 1 0 8.2 8.2z"/></svg>`;
const BELL = `<svg width="15" height="15" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4" aria-hidden="true"><path d="M4.5 6.7a3.5 3.5 0 0 1 7 0c0 2.8 1 3.8 1.5 4.3h-10c.5-.5 1.5-1.5 1.5-4.3z"/><path d="M6.6 13a1.4 1.4 0 0 0 2.8 0"/></svg>`;

const TOPBAR = `<header class="fb-topbar">
    <div class="fb-crumb">Risk / <b>Vendors</b></div>
    <div class="fb-topbar-right">
      <span class="fb-countdown">SOC 2 AUDIT IN 21D</span>
      <button type="button" class="fb-iconbtn" aria-label="Toggle theme">${MOON}</button>
      <button type="button" class="fb-iconbtn" aria-label="Notifications">${BELL}<span class="fb-pip"></span></button>
      <button type="button" class="fb-avatar" aria-label="Account menu">JS</button>
    </div>
  </header>`;

const PAGE = `<div class="fb-pagehead">
      <div>
        <div class="fb-eyebrow">Risk</div>
        <h1>Vendors</h1>
        <div class="fb-sub">One directory. Discovery, reviews, and procurement are stages of the same record.</div>
      </div>
      <div class="fb-headactions"><button type="button" class="fb-btn">Export</button><button type="button" class="fb-btn fb-btn--brand">Add vendor</button></div>
    </div>
    <div class="fb-tp">
      <div class="fb-toolbar">
        <button type="button" class="fb-chip on" onclick="${CHIP}">All<span class="n">38</span></button>
        <button type="button" class="fb-chip" onclick="${CHIP}">Critical<span class="n">3</span></button>
        <button type="button" class="fb-chip" onclick="${CHIP}">Review due<span class="n">4</span></button>
        <span class="fb-spacer"></span>
        <input type="search" class="fb-search" placeholder="Filter vendors..." aria-label="Filter vendors">
      </div>
      <div class="fb-scroll"><table class="fb-tbl">
        <thead><tr><th>Vendor</th><th>Tier</th><th>Status</th><th>Next review</th><th>Owner</th><th></th></tr></thead>
        <tbody>
          <tr class="fb-row"><td><div class="fb-tdname">Cloud infrastructure</div><div class="fb-tdsub">Hosting</div></td><td><span class="fb-tag fb-tag--fail">Critical</span></td><td><span class="fb-status ok"><span class="fb-seal ok"></span>Approved</span></td><td><span class="fb-due">Mar 27</span></td><td><span class="fb-owner"><span class="fb-av">RB</span>R. Byrne</span></td><td><button type="button" class="fb-tbtn">View</button></td></tr>
          <tr class="fb-row"><td><div class="fb-tdname">HR platform</div><div class="fb-tdsub">HRIS</div></td><td><span class="fb-tag fb-tag--warn">High</span></td><td><span class="fb-status warn"><span class="fb-seal warn"></span>Review due</span></td><td><span class="fb-due soon">28 Aug</span></td><td><span class="fb-owner"><span class="fb-av">MO</span>M. Osei</span></td><td><button type="button" class="fb-tbtn">Review</button></td></tr>
          <tr class="fb-row"><td><div class="fb-tdname">Analytics</div><div class="fb-tdsub">discovered via SSO</div></td><td><span class="fb-tag fb-tag--warn">High proposed</span></td><td><span class="fb-status fail"><span class="fb-seal fail"></span>Review overdue</span></td><td><span class="fb-due over">2d overdue</span></td><td><span class="fb-owner"><span class="fb-av">JS</span>J. Sarah</span></td><td><button type="button" class="fb-tbtn">Review</button></td></tr>
        </tbody>
      </table></div>
      <div class="fb-tfoot">Showing 3 of 38.</div>
    </div>`;

const APP = `<div class="fb-app">
  ${RAIL}
  <div class="fb-stage">
    ${TOPBAR}
    <main class="fb-main">${PAGE}</main>
  </div>
</div>`;

const FRAME = `<div style="height:720px;border:1px solid #c9cec5;border-radius:12px;overflow:hidden;box-shadow:0 10px 34px rgba(26,29,28,.12)">${APP}</div>`;

const SNIPPET = `<div class="fb-app">
  <nav class="fb-rail" aria-label="Primary">
    <div class="fb-brand">...</div>
    <button type="button" class="fb-search-entry" aria-haspopup="dialog">Search or ask...<kbd class="fb-kbd">Ctrl K</kbd></button>
    <div class="fb-navwrap"> ...grouped nav items, active current page... </div>
    <div class="fb-rail-foot"> ...workspace switcher... </div>
  </nav>

  <div class="fb-stage">
    <header class="fb-topbar">
      <div class="fb-crumb">Risk / <b>Vendors</b></div>
      <div class="fb-topbar-right">
        <span class="fb-countdown">SOC 2 AUDIT IN 21D</span>
        <button type="button" class="fb-iconbtn" aria-label="Toggle theme">...</button>
        <button type="button" class="fb-iconbtn" aria-label="Notifications">...<span class="fb-pip"></span></button>
        <button type="button" class="fb-avatar" aria-label="Account menu">JS</button>
      </div>
    </header>
    <main class="fb-main">
      <!-- any page renders here (see the other compositions) -->
    </main>
  </div>
</div>`;

export const Vendors = { render: () => compPage(FRAME, SNIPPET) };
