// Compositions / List page. The standard list page assembled from components: a
// page header (eyebrow, title, actions), a panel holding a filter/search toolbar,
// and the workhorse table with row actions, closing with a Showing n of m foot -
// the shape the Vendors, Controls, and People pages share. Rendered full-size,
// with the assembled markup below. Reference only: story-scoped literal values.

import { SANS, MONO, codeBlock } from "./_ui.js";

export default {
    title: "Compositions/List page",
    parameters: { layout: "fullscreen" },
};

const CSS = `
  .fb-eyebrow { font:600 10px ${MONO}; letter-spacing:.14em; text-transform:uppercase; color:#8a938e; margin-bottom:6px; }
  .fb-pagehead { display:flex; align-items:flex-end; justify-content:space-between; gap:16px; margin-bottom:18px; flex-wrap:wrap; }
  .fb-pagehead h1 { font:700 21px ${SANS}; letter-spacing:-.015em; color:#1a1d1c; margin:0; }
  .fb-sub { font-size:13px; color:#616a66; margin-top:3px; max-width:62ch; }
  .fb-headactions { display:flex; gap:8px; flex:none; }
  .fb-btn { display:inline-flex; align-items:center; font:600 12.5px ${SANS}; color:#1a1d1c; background:#fff; border:1px solid #c9cec5; border-radius:6px; padding:6px 12px; cursor:pointer; }
  .fb-btn:hover { border-color:#8a938e; }
  .fb-btn--brand { background:#4f46c8; border-color:#4f46c8; color:#f1f2ee; }
  .fb-btn--brand:hover { background:#3d36a3; border-color:#3d36a3; }

  .fb-tp { background:#fff; border:1px solid #e0e3dc; border-radius:10px; overflow:hidden; }
  .fb-scroll { overflow-x:auto; }
  .fb-toolbar { display:flex; align-items:center; gap:10px; padding:11px 14px; border-bottom:1px solid #e0e3dc; flex-wrap:wrap; }
  .fb-search { font:400 12.5px ${SANS}; color:#1a1d1c; border:1px solid #e0e3dc; border-radius:6px; padding:5px 10px; background:#fafbf8; width:200px; }
  .fb-search::placeholder { color:#8a938e; }
  .fb-search:focus { outline:2px solid #4f46c8; outline-offset:0; }
  .fb-spacer { flex:1; }
  .fb-chip { font:500 12px ${SANS}; color:#616a66; border:1px solid #e0e3dc; border-radius:99px; padding:3px 11px; background:#fff; cursor:pointer; }
  .fb-chip:hover { border-color:#c9cec5; color:#1a1d1c; }
  .fb-chip.on { background:#1a1d1c; border-color:#1a1d1c; color:#f1f2ee; }
  .fb-chip .n { font:500 10.5px ${MONO}; opacity:.7; margin-left:4px; }

  .fb-tbl { width:100%; border-collapse:collapse; font-family:${SANS}; }
  .fb-tbl th { font:600 9.5px ${MONO}; letter-spacing:.12em; text-transform:uppercase; color:#8a938e; text-align:left; padding:9px 14px; border-bottom:1px solid #e0e3dc; white-space:nowrap; }
  .fb-tbl td { font-size:13px; color:#1a1d1c; padding:10px 14px; border-bottom:1px solid #e0e3dc; vertical-align:middle; }
  .fb-tbl tbody tr:last-child td { border-bottom:none; }
  .fb-tbl tr.fb-row:hover { background:#fafbf8; }
  .fb-tdname { font-weight:600; }
  .fb-tdsub { font-size:11.5px; color:#8a938e; margin-top:1px; }
  .fb-tfoot { font:500 10.5px ${MONO}; color:#8a938e; padding:9px 14px; border-top:1px solid #e0e3dc; }

  .fb-status { display:inline-flex; align-items:center; gap:7px; font-size:12.5px; font-weight:500; white-space:nowrap; }
  .fb-status.ok { color:#1b7a4e; } .fb-status.fail { color:#b3372d; } .fb-status.warn { color:#96690a; }
  .fb-seal { width:9px; height:9px; border-radius:2px; display:inline-block; flex:none; }
  .fb-seal.ok { background:#1b7a4e; } .fb-seal.fail { background:#b3372d; box-shadow:0 0 0 2px #f9e9e6; } .fb-seal.warn { background:#96690a; }
  .fb-tag { display:inline-flex; font:500 11px ${SANS}; color:#616a66; background:#eceeea; border-radius:4px; padding:2px 8px; white-space:nowrap; }
  .fb-tag--fail { color:#b3372d; background:#f9e9e6; } .fb-tag--warn { color:#96690a; background:#f6eed6; }
  .fb-owner { display:inline-flex; align-items:center; gap:7px; font-size:12.5px; color:#616a66; white-space:nowrap; }
  .fb-av { width:20px; height:20px; border-radius:99px; background:#eceeea; color:#616a66; font:700 9px ${SANS}; display:inline-flex; align-items:center; justify-content:center; flex:none; }
  .fb-due { font:500 11px ${MONO}; color:#616a66; white-space:nowrap; }
  .fb-due.soon { color:#96690a; } .fb-due.over { color:#b3372d; background:#f9e9e6; border-radius:4px; padding:1px 6px; }
  .fb-tbtn { font:600 11.5px ${SANS}; color:#1a1d1c; background:#fff; border:1px solid #c9cec5; border-radius:6px; padding:3px 9px; cursor:pointer; }
  .fb-tbtn:hover { border-color:#8a938e; }
`;

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
    <div class="fb-sub">One directory. Discovery, reviews, and procurement are stages of the same record.</div>
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

export const Vendors = {
    render: () => `
      <style>${CSS}</style>
      <div style="background:#f1f2ee;min-height:100vh;padding:24px 22px;font-family:${SANS};color:#1a1d1c">
        <div style="max-width:1100px;margin:0 auto">
          <div style="display:flex;gap:8px;align-items:baseline;background:#edecfa;border:1px solid rgba(79,70,200,.25);border-radius:10px;padding:10px 14px;margin-bottom:20px;font-size:12.5px;color:#3d36a3">
            <strong>Reference only.</strong><span>A composition of existing components (page header, buttons, toolbar, table). Not wired into the app.</span>
          </div>
          ${COMP}
          <div style="font:600 10px/1 ${MONO};letter-spacing:.14em;text-transform:uppercase;color:#8a938e;margin:26px 0 10px">Assembled markup</div>
          ${codeBlock(SNIPPET)}
        </div>
      </div>`,
};
