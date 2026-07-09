// Compositions / List page. The standard list page assembled from components: a
// page header (eyebrow, title, actions), a panel holding a filter/search toolbar,
// and the workhorse table with row actions, closing with a Showing n of m foot -
// the shape the Vendors, Controls, and People pages share. Reference only.

import { compPage } from "./_marks.js";

export default {
    title: "Compositions/List page",
    parameters: { layout: "fullscreen" },
};

const CHIP = "var p=this.parentNode;var c=p.querySelectorAll('.fb-chip');for(var i=0;i<c.length;i++)c[i].classList.remove('on');this.classList.add('on')";

const COMP = `<div class="fb-pagehead">
    <div>
      <div class="fb-eyebrow">Risk</div>
      <h1>Vendors</h1>
      <div class="fb-sub">One directory. Discovery, reviews, and procurement are stages of the same record.</div>
    </div>
    <div class="fb-headactions">
      <button type="button" class="fb-btn">Export</button>
      <button type="button" class="fb-btn fb-btn--brand">Add vendor</button>
    </div>
  </div>
  <div class="fb-tp">
    <div class="fb-toolbar">
      <button type="button" class="fb-chip on" onclick="${CHIP}">All<span class="n">38</span></button>
      <button type="button" class="fb-chip" onclick="${CHIP}">Critical<span class="n">3</span></button>
      <button type="button" class="fb-chip" onclick="${CHIP}">High<span class="n">6</span></button>
      <button type="button" class="fb-chip" onclick="${CHIP}">Review due<span class="n">4</span></button>
      <span class="fb-spacer"></span>
      <input type="search" class="fb-search" placeholder="Filter vendors..." aria-label="Filter vendors">
    </div>
    <div class="fb-scroll"><table class="fb-tbl">
      <thead><tr><th>Vendor</th><th>Tier</th><th>Data</th><th>Status</th><th>Next review</th><th>Owner</th><th></th></tr></thead>
      <tbody>
        <tr class="fb-row">
          <td><div class="fb-tdname">Cloud infrastructure</div><div class="fb-tdsub">Hosting</div></td>
          <td><span class="fb-tag fb-tag--fail">Critical</span></td>
          <td><span class="fb-tag">PII</span> <span class="fb-tag">Prod</span></td>
          <td><span class="fb-status ok"><span class="fb-seal ok"></span>Approved</span></td>
          <td><span class="fb-due">Mar 27</span></td>
          <td><span class="fb-owner"><span class="fb-av">RB</span>R. Byrne</span></td>
          <td><button type="button" class="fb-tbtn">View</button></td>
        </tr>
        <tr class="fb-row">
          <td><div class="fb-tdname">HR platform</div><div class="fb-tdsub">HRIS</div></td>
          <td><span class="fb-tag fb-tag--warn">High</span></td>
          <td><span class="fb-tag">PII</span></td>
          <td><span class="fb-status warn"><span class="fb-seal warn"></span>Review due</span></td>
          <td><span class="fb-due soon">28 Aug</span></td>
          <td><span class="fb-owner"><span class="fb-av">MO</span>M. Osei</span></td>
          <td><button type="button" class="fb-tbtn">Review</button></td>
        </tr>
        <tr class="fb-row">
          <td><div class="fb-tdname">Analytics</div><div class="fb-tdsub">discovered via SSO</div></td>
          <td><span class="fb-tag fb-tag--warn">High proposed</span></td>
          <td><span class="fb-tag">Prod read</span></td>
          <td><span class="fb-status fail"><span class="fb-seal fail"></span>Review overdue</span></td>
          <td><span class="fb-due over">2d overdue</span></td>
          <td><span class="fb-owner"><span class="fb-av">JS</span>J. Sarah</span></td>
          <td><button type="button" class="fb-tbtn">Review</button></td>
        </tr>
        <tr class="fb-row">
          <td><div class="fb-tdname">Issue tracker</div><div class="fb-tdsub">Issue tracking</div></td>
          <td><span class="fb-tag">Medium</span></td>
          <td><span class="fb-tag">Internal</span></td>
          <td><span class="fb-status ok"><span class="fb-seal ok"></span>Approved</span></td>
          <td><span class="fb-due">Jan 27</span></td>
          <td><span class="fb-owner"><span class="fb-av">MO</span>M. Osei</span></td>
          <td><button type="button" class="fb-tbtn">View</button></td>
        </tr>
      </tbody>
    </table></div>
    <div class="fb-tfoot">Showing 4 of 38.</div>
  </div>`;

const SNIPPET = `<div class="fb-pagehead">
  <div>
    <div class="fb-eyebrow">Risk</div>
    <h1>Vendors</h1>
    <div class="fb-sub">One directory...</div>
  </div>
  <div class="fb-headactions">
    <button type="button" class="fb-btn">Export</button>
    <button type="button" class="fb-btn fb-btn--brand">Add vendor</button>
  </div>
</div>

<div class="fb-tp">
  <div class="fb-toolbar">
    <button type="button" class="fb-chip on">All<span class="n">38</span></button>
    <button type="button" class="fb-chip">Critical<span class="n">3</span></button>
    <span class="fb-spacer"></span>
    <input type="search" class="fb-search" placeholder="Filter vendors..." aria-label="Filter vendors">
  </div>
  <table class="fb-tbl">
    <thead>
      <tr><th>Vendor</th><th>Tier</th><th>Status</th><th>Next review</th><th>Owner</th><th></th></tr>
    </thead>
    <tbody>
      <tr class="fb-row">
        <td><div class="fb-tdname">Cloud infrastructure</div><div class="fb-tdsub">Hosting</div></td>
        <td><span class="fb-tag fb-tag--fail">Critical</span></td>
        <td><span class="fb-status ok"><span class="fb-seal ok"></span>Approved</span></td>
        <td><span class="fb-due">Mar 27</span></td>
        <td><span class="fb-owner"><span class="fb-av">RB</span>R. Byrne</span></td>
        <td><button type="button" class="fb-tbtn">View</button></td>
      </tr>
      <!-- ...more rows... -->
    </tbody>
  </table>
  <div class="fb-tfoot">Showing 4 of 38.</div>
</div>`;

export const Vendors = { render: () => compPage(COMP, SNIPPET) };
