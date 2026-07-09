// Compositions / Tabbed page. The full Vendors page: a page header over a panel
// whose sub-views are tabs (Directory, Discovery, Reviews, Procurement), not
// separate nav items. The Directory tab holds the table; the others hold their
// own content. Tabs switch as an ARIA tablist. Reference only.

import { compPage } from "./_marks.js";

export default {
    title: "Compositions/Tabbed page",
    parameters: { layout: "fullscreen" },
};

const TABJS = "var t=this,bar=t.parentNode,ts=bar.querySelectorAll('.fb-tab');for(var i=0;i<ts.length;i++){ts[i].classList.remove('on');ts[i].setAttribute('aria-selected','false');}t.classList.add('on');t.setAttribute('aria-selected','true');var w=t.closest('.fb-tabwrap'),ps=w.querySelectorAll('.fb-pane'),id=t.getAttribute('aria-controls');for(var j=0;j<ps.length;j++){var on=ps[j].id===id;ps[j].classList.toggle('on',on);if(on){ps[j].removeAttribute('hidden');}else{ps[j].setAttribute('hidden','');}}";

const tab = (id, label, count, on) =>
    `<button type="button" class="fb-tab${on ? " on" : ""}" role="tab" aria-selected="${on ? "true" : "false"}" aria-controls="vp-${id}" id="vt-${id}" onclick="${TABJS}">${label}${count ? `<span class="n">${count}</span>` : ""}</button>`;

const DIRECTORY = `<div class="fb-scroll"><table class="fb-tbl">
    <thead><tr><th>Vendor</th><th>Tier</th><th>Status</th><th>Next review</th><th>Owner</th><th></th></tr></thead>
    <tbody>
      <tr class="fb-row"><td><div class="fb-tdname">Cloud infrastructure</div><div class="fb-tdsub">Hosting</div></td><td><span class="fb-tag fb-tag--fail">Critical</span></td><td><span class="fb-status ok"><span class="fb-seal ok"></span>Approved</span></td><td><span class="fb-due">Mar 27</span></td><td><span class="fb-owner"><span class="fb-av">RB</span>R. Byrne</span></td><td><button type="button" class="fb-tbtn">View</button></td></tr>
      <tr class="fb-row"><td><div class="fb-tdname">HR platform</div><div class="fb-tdsub">HRIS</div></td><td><span class="fb-tag fb-tag--warn">High</span></td><td><span class="fb-status warn"><span class="fb-seal warn"></span>Review due</span></td><td><span class="fb-due soon">28 Aug</span></td><td><span class="fb-owner"><span class="fb-av">MO</span>M. Osei</span></td><td><button type="button" class="fb-tbtn">Review</button></td></tr>
      <tr class="fb-row"><td><div class="fb-tdname">Analytics</div><div class="fb-tdsub">discovered via SSO</div></td><td><span class="fb-tag fb-tag--warn">High proposed</span></td><td><span class="fb-status fail"><span class="fb-seal fail"></span>Review overdue</span></td><td><span class="fb-due over">2d overdue</span></td><td><span class="fb-owner"><span class="fb-av">JS</span>J. Sarah</span></td><td><button type="button" class="fb-tbtn">Review</button></td></tr>
    </tbody>
  </table></div>
  <div class="fb-tfoot">Showing 3 of 38.</div>`;

const DISCOVERY = `<div class="fb-scroll"><table class="fb-tbl">
    <thead><tr><th>Detected app</th><th>Seen via</th><th>Users</th><th>First seen</th><th></th></tr></thead>
    <tbody>
      <tr class="fb-row"><td><div class="fb-tdname">Screen recorder</div></td><td><span class="fb-stamp">SSO</span></td><td>11</td><td><span class="fb-due">Today</span></td><td><button type="button" class="fb-tbtn">Triage</button></td></tr>
      <tr class="fb-row"><td><div class="fb-tdname">Analytics</div></td><td><span class="fb-stamp">SSO</span></td><td>6</td><td><span class="fb-due">2d ago</span></td><td><button type="button" class="fb-tbtn">Triage</button></td></tr>
    </tbody>
  </table></div>
  <div class="fb-tfoot">Triage promotes a detected app into the directory, or dismisses it with a reason.</div>`;

const COMP = `<div class="fb-pagehead">
    <div>
      <div class="fb-eyebrow">Risk</div>
      <h1>Vendors</h1>
      <div class="fb-sub">One directory. Discovery, reviews, and procurement are stages of the same record, not separate lists.</div>
    </div>
    <div class="fb-headactions"><button type="button" class="fb-btn fb-btn--brand">Add vendor</button></div>
  </div>
  <div class="fb-tabwrap">
    <div class="fb-tabs" role="tablist" aria-label="Vendor views">
      ${tab("dir", "Directory", "38", true)}
      ${tab("dis", "Discovery", "2", false)}
      ${tab("rev", "Reviews", "3", false)}
      ${tab("pro", "Procurement", "1", false)}
    </div>
    <div class="fb-pane on" role="tabpanel" id="vp-dir" aria-labelledby="vt-dir">${DIRECTORY}</div>
    <div class="fb-pane" role="tabpanel" id="vp-dis" aria-labelledby="vt-dis" hidden>${DISCOVERY}</div>
    <div class="fb-pane fb-pane__pad" role="tabpanel" id="vp-rev" aria-labelledby="vt-rev" hidden>3 security reviews in flight. The assistant extracts findings from reports and questionnaires; a human approves the risk rating before it lands on the record.</div>
    <div class="fb-pane fb-pane__pad" role="tabpanel" id="vp-pro" aria-labelledby="vt-pro" hidden>1 intake request from the procurement form. Approving it creates the vendor record, its review, and its owner in one step.</div>
  </div>`;

const SNIPPET = `<div class="fb-pagehead"> ...header with actions... </div>

<div class="fb-tabwrap">
  <div class="fb-tabs" role="tablist" aria-label="Vendor views">
    <button type="button" class="fb-tab on" role="tab" aria-selected="true" aria-controls="vp-dir" id="vt-dir">Directory<span class="n">38</span></button>
    <button type="button" class="fb-tab" role="tab" aria-selected="false" aria-controls="vp-dis" id="vt-dis">Discovery<span class="n">2</span></button>
    <!-- ...Reviews, Procurement... -->
  </div>

  <div class="fb-pane on" role="tabpanel" id="vp-dir" aria-labelledby="vt-dir">
    <table class="fb-tbl"> ...directory rows... </table>
    <div class="fb-tfoot">Showing 3 of 38.</div>
  </div>
  <div class="fb-pane fb-pane__pad" role="tabpanel" id="vp-rev" aria-labelledby="vt-rev" hidden>...</div>
  <!-- Switching keeps aria-selected and hidden in sync; wired in code (see Components / Tabs). -->
</div>`;

export const Vendors = { render: () => compPage(COMP, SNIPPET) };
