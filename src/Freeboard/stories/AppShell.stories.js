// Compositions / App shell. The full app frame from the prototype: the nav rail
// on the left, a top bar (breadcrumb, audit countdown, theme and notification
// icons, account), and a scrolling main that hosts a page - here the vendor list.
// The frame everything else renders inside. Reference only.

import { compPage, appFrame, railNav, shellTopbar } from "./_marks.js";

export default {
    title: "Compositions/App shell",
    parameters: { layout: "fullscreen" },
};

const CHIP = "var p=this.parentNode;var c=p.querySelectorAll('.fb-chip');for(var i=0;i<c.length;i++)c[i].classList.remove('on');this.classList.add('on')";

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
      <div class="fb-topbar-right"> ...countdown, icons, account... </div>
    </header>
    <main class="fb-main">
      <!-- any page renders here (see the other compositions) -->
    </main>
  </div>
</div>`;

export const Vendors = { render: () => compPage(appFrame(railNav("Vendors"), shellTopbar("Risk / <b>Vendors</b>"), PAGE), SNIPPET) };
