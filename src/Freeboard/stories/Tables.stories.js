// Components / Tables. The workhorse list from the prototype: a panel-wrapped
// table with mono column headers, exceptions-first grouped rows (a red "hot"
// group for overdue), name/sub cells, status seals, provenance stamps, owner, and
// a one-click verb action, closing with a "Showing n of m" foot. Plus bulk
// selection and the filter/search toolbar. Reference only: story-scoped values.

import { SANS, MONO, page, section, example } from "./_ui.js";

export default {
    title: "Components/Tables",
    parameters: { layout: "fullscreen" },
};

const CSS = `
  .fb-tp { background:#fff; border:1px solid #e0e3dc; border-radius:10px; overflow:hidden; }
  .fb-scroll { overflow-x:auto; }
  .fb-tbl { width:100%; border-collapse:collapse; font-family:${SANS}; }
  .fb-tbl th { font:600 9.5px ${MONO}; letter-spacing:.12em; text-transform:uppercase; color:#8a938e; text-align:left; padding:9px 14px; border-bottom:1px solid #e0e3dc; white-space:nowrap; }
  .fb-tbl td { font-size:13px; color:#1a1d1c; padding:10px 14px; border-bottom:1px solid #e0e3dc; vertical-align:middle; }
  .fb-tbl tbody tr:last-child td { border-bottom:none; }
  .fb-tbl tr.fb-row:hover { background:#fafbf8; }
  .fb-tbl tr.fb-grp td { font:600 10px ${MONO}; letter-spacing:.12em; text-transform:uppercase; color:#616a66; background:#fafbf8; padding:6px 14px; }
  .fb-tbl tr.fb-grp.hot td { color:#b3372d; }
  .fb-tdname { font-weight:600; }
  .fb-tdsub { font-size:11.5px; color:#8a938e; margin-top:1px; }
  .fb-tfoot { font:500 10.5px ${MONO}; color:#8a938e; padding:9px 14px; border-top:1px solid #e0e3dc; }

  .fb-status { display:inline-flex; align-items:center; gap:7px; font-size:12.5px; font-weight:500; white-space:nowrap; }
  .fb-status.ok { color:#1b7a4e; } .fb-status.fail { color:#b3372d; } .fb-status.warn { color:#96690a; }
  .fb-seal { width:9px; height:9px; border-radius:2px; display:inline-block; flex:none; }
  .fb-seal.ok { background:#1b7a4e; } .fb-seal.fail { background:#b3372d; box-shadow:0 0 0 2px #f9e9e6; } .fb-seal.warn { background:#96690a; }
  .fb-stamp { font:500 10px ${MONO}; letter-spacing:.06em; text-transform:uppercase; color:#616a66; border:1px dashed #c9cec5; border-radius:4px; padding:1px 6px; white-space:nowrap; }
  .fb-stamp.manual { color:#96690a; border-color:rgba(150,105,10,.4); }
  .fb-owner { display:inline-flex; align-items:center; gap:7px; font-size:12.5px; color:#616a66; white-space:nowrap; }
  .fb-av { width:20px; height:20px; border-radius:99px; background:#eceeea; color:#616a66; font:700 9px ${SANS}; display:inline-flex; align-items:center; justify-content:center; flex:none; }
  .fb-due { font:500 11px ${MONO}; color:#616a66; white-space:nowrap; }
  .fb-due.soon { color:#96690a; } .fb-due.over { color:#b3372d; background:#f9e9e6; border-radius:4px; padding:1px 6px; }
  .fb-tbtn { font:600 11.5px ${SANS}; color:#1a1d1c; background:#fff; border:1px solid #c9cec5; border-radius:6px; padding:3px 9px; cursor:pointer; }
  .fb-tbtn:hover { border-color:#8a938e; }

  .fb-ck { width:14px; height:14px; accent-color:#4f46c8; cursor:pointer; }
  .fb-bulk { display:none; align-items:center; gap:10px; background:#1a1d1c; color:#f1f2ee; border-radius:6px; padding:7px 12px; margin:10px 14px; font-size:12.5px; }
  .fb-bulk__btn { font:600 11.5px ${SANS}; color:inherit; background:transparent; border:1px solid #7d8781; border-radius:6px; padding:3px 9px; cursor:pointer; }
  .fb-bulk__btn:hover { border-color:currentColor; }
  .fb-spacer { flex:1; }

  .fb-toolbar { display:flex; align-items:center; gap:10px; padding:11px 14px; border-bottom:1px solid #e0e3dc; flex-wrap:wrap; }
  .fb-search { font:400 12.5px ${SANS}; color:#1a1d1c; border:1px solid #e0e3dc; border-radius:6px; padding:5px 10px; background:#fafbf8; width:200px; }
  .fb-search::placeholder { color:#8a938e; }
  .fb-search:focus { outline:2px solid #4f46c8; outline-offset:0; }
  .fb-chip { font:500 12px ${SANS}; color:#616a66; border:1px solid #e0e3dc; border-radius:99px; padding:3px 11px; background:#fff; cursor:pointer; }
  .fb-chip:hover { border-color:#c9cec5; color:#1a1d1c; }
  .fb-chip.on { background:#1a1d1c; border-color:#1a1d1c; color:#f1f2ee; }
  .fb-chip .n { font:500 10.5px ${MONO}; opacity:.7; margin-left:4px; }
`;

const eyebrow = "Components / Tables";
const onField = (html) => `<div style="background:#f1f2ee;padding:20px;border-radius:8px;width:100%">${html}</div>`;

// Self-contained inline handlers so the interactive previews work without any
// global script; kept out of the copyable snippets, which show clean markup.
const ROWCK = "var w=this.closest('.fb-tp');var n=w.querySelectorAll('.fb-rowck:checked').length;var b=w.querySelector('.fb-bulk');b.style.display=n?'flex':'none';b.querySelector('.fb-bulk__n').textContent=n+' selected'";
const ALLCK = "var w=this.closest('.fb-tp');var c=w.querySelectorAll('.fb-rowck');for(var i=0;i<c.length;i++)c[i].checked=this.checked;var n=this.checked?c.length:0;var b=w.querySelector('.fb-bulk');b.style.display=n?'flex':'none';b.querySelector('.fb-bulk__n').textContent=n+' selected'";
const CLEARCK = "var w=this.closest('.fb-tp');var c=w.querySelectorAll('.fb-rowck,.fb-allck');for(var i=0;i<c.length;i++)c[i].checked=false;var b=w.querySelector('.fb-bulk');b.style.display='none';b.querySelector('.fb-bulk__n').textContent='0 selected'";
const CHIP = "var p=this.parentNode;var c=p.querySelectorAll('.fb-chip');for(var i=0;i<c.length;i++)c[i].classList.remove('on');this.classList.add('on')";

// core list
const TABLE_FULL = `<div class="fb-tp"><div class="fb-scroll"><table class="fb-tbl">
  <thead><tr><th>Status</th><th>Item</th><th>Source</th><th>Owner</th><th>Due</th><th></th></tr></thead>
  <tbody>
    <tr class="fb-grp hot"><td colspan="6">Overdue / 2</td></tr>
    <tr class="fb-row">
      <td><span class="fb-status fail"><span class="fb-seal fail"></span>Failing</span></td>
      <td><div class="fb-tdname">MFA enforced for all admin accounts</div><div class="fb-tdsub">CC6.1 / SOC 2, ISO 27001</div></td>
      <td><span class="fb-stamp">AUTO / 22M</span></td>
      <td><span class="fb-owner"><span class="fb-av">JS</span>J. Sarah</span></td>
      <td><span class="fb-due over">3d overdue</span></td>
      <td><button type="button" class="fb-tbtn">Fix</button></td>
    </tr>
    <tr class="fb-row">
      <td><span class="fb-status fail"><span class="fb-seal fail"></span>Overdue</span></td>
      <td><div class="fb-tdname">Access control policy approval</div><div class="fb-tdsub">waiting on approver</div></td>
      <td><span class="fb-stamp manual">MANUAL</span></td>
      <td><span class="fb-owner"><span class="fb-av">MO</span>M. Osei</span></td>
      <td><span class="fb-due over">1d overdue</span></td>
      <td><button type="button" class="fb-tbtn">Approve</button></td>
    </tr>
    <tr class="fb-grp"><td colspan="6">Due soon / 2</td></tr>
    <tr class="fb-row">
      <td><span class="fb-status fail"><span class="fb-seal fail"></span>Failing</span></td>
      <td><div class="fb-tdname">Storage blocks public access</div><div class="fb-tdsub">2 resources</div></td>
      <td><span class="fb-stamp">AUTO / 12M</span></td>
      <td><span class="fb-owner"><span class="fb-av">RB</span>R. Byrne</span></td>
      <td><span class="fb-due soon">SLA Fri</span></td>
      <td><button type="button" class="fb-tbtn">Fix</button></td>
    </tr>
    <tr class="fb-row">
      <td><span class="fb-status warn"><span class="fb-seal warn"></span>Due</span></td>
      <td><div class="fb-tdname">Pentest report renewal</div><div class="fb-tdsub">satisfies 2 checks</div></td>
      <td><span class="fb-stamp manual">MANUAL</span></td>
      <td><span class="fb-owner"><span class="fb-av">MO</span>M. Osei</span></td>
      <td><span class="fb-due soon">Due 18 Jul</span></td>
      <td><button type="button" class="fb-tbtn">Upload</button></td>
    </tr>
  </tbody>
</table></div>
<div class="fb-tfoot">Showing 4 of 14. Exceptions first: failing and due-soon on top.</div></div>`;

const TABLE_SNIPPET = `<div class="fb-tp">
  <table class="fb-tbl">
    <thead>
      <tr><th>Status</th><th>Item</th><th>Source</th><th>Owner</th><th>Due</th><th></th></tr>
    </thead>
    <tbody>
      <tr class="fb-grp hot"><td colspan="6">Overdue / 2</td></tr>
      <tr class="fb-row">
        <td><span class="fb-status fail"><span class="fb-seal fail"></span>Failing</span></td>
        <td>
          <div class="fb-tdname">MFA enforced for all admin accounts</div>
          <div class="fb-tdsub">CC6.1 / SOC 2</div>
        </td>
        <td><span class="fb-stamp">AUTO / 22M</span></td>
        <td><span class="fb-owner"><span class="fb-av">JS</span>J. Sarah</span></td>
        <td><span class="fb-due over">3d overdue</span></td>
        <td><button type="button" class="fb-tbtn">Fix</button></td>
      </tr>
    </tbody>
  </table>
  <div class="fb-tfoot">Showing 4 of 14.</div>
</div>`;

// selection
const SELECT_FULL = `<div class="fb-tp">
  <div class="fb-bulk"><span class="fb-bulk__n">0 selected</span><button type="button" class="fb-bulk__btn">Assign owner</button><button type="button" class="fb-bulk__btn">Snooze with reason</button><button type="button" class="fb-bulk__btn">Create issue</button><span class="fb-spacer"></span><button type="button" class="fb-bulk__btn" onclick="${CLEARCK}">Clear</button></div>
  <div class="fb-scroll"><table class="fb-tbl">
    <thead><tr>
      <th style="width:30px"><input type="checkbox" class="fb-ck fb-allck" aria-label="Select all rows" onchange="${ALLCK}"></th>
      <th>Check</th><th>Source</th><th>Status</th><th>Owner</th>
    </tr></thead>
    <tbody>
      <tr class="fb-row"><td><input type="checkbox" class="fb-ck fb-rowck" aria-label="Select row" onchange="${ROWCK}"></td><td><div class="fb-tdname">Admin accounts require MFA</div></td><td><span class="fb-stamp">AUTO / 22M</span></td><td><span class="fb-status fail"><span class="fb-seal fail"></span>Failing</span></td><td><span class="fb-owner"><span class="fb-av">JS</span>J. Sarah</span></td></tr>
      <tr class="fb-row"><td><input type="checkbox" class="fb-ck fb-rowck" aria-label="Select row" onchange="${ROWCK}"></td><td><div class="fb-tdname">Storage blocks public access</div></td><td><span class="fb-stamp">AUTO / 12M</span></td><td><span class="fb-status fail"><span class="fb-seal fail"></span>Failing</span></td><td><span class="fb-owner"><span class="fb-av">RB</span>R. Byrne</span></td></tr>
      <tr class="fb-row"><td><input type="checkbox" class="fb-ck fb-rowck" aria-label="Select row" onchange="${ROWCK}"></td><td><div class="fb-tdname">Databases encrypted at rest</div></td><td><span class="fb-stamp">AUTO / 12M</span></td><td><span class="fb-status ok"><span class="fb-seal ok"></span>Passing</span></td><td><span class="fb-owner"><span class="fb-av">RB</span>R. Byrne</span></td></tr>
    </tbody>
  </table></div>
</div>`;

const SELECT_SNIPPET = `<div class="fb-tp">
  <!-- Bulk bar: shown when a row is selected; the count and visibility are wired in code. -->
  <div class="fb-bulk">
    <span class="fb-bulk__n">2 selected</span>
    <button type="button" class="fb-bulk__btn">Assign owner</button>
    <button type="button" class="fb-bulk__btn">Snooze with reason</button>
    <span class="fb-spacer"></span>
    <button type="button" class="fb-bulk__btn">Clear</button>
  </div>
  <table class="fb-tbl">
    <thead>
      <tr>
        <th><input type="checkbox" class="fb-ck" aria-label="Select all rows"></th>
        <th>Check</th><th>Status</th>
      </tr>
    </thead>
    <tbody>
      <tr class="fb-row">
        <td><input type="checkbox" class="fb-ck" aria-label="Select row"></td>
        <td><div class="fb-tdname">Admin accounts require MFA</div></td>
        <td><span class="fb-status fail"><span class="fb-seal fail"></span>Failing</span></td>
      </tr>
    </tbody>
  </table>
</div>`;

// toolbar
const TOOLBAR_FULL = `<div class="fb-tp">
  <div class="fb-toolbar">
    <button type="button" class="fb-chip on" onclick="${CHIP}">All<span class="n">312</span></button>
    <button type="button" class="fb-chip" onclick="${CHIP}">Failing<span class="n">9</span></button>
    <button type="button" class="fb-chip" onclick="${CHIP}">Due soon<span class="n">6</span></button>
    <button type="button" class="fb-chip" onclick="${CHIP}">Ready<span class="n">297</span></button>
    <span class="fb-spacer"></span>
    <input type="search" class="fb-search" placeholder="Filter controls..." aria-label="Filter controls">
  </div>
  <div class="fb-scroll"><table class="fb-tbl">
    <tbody>
      <tr class="fb-row"><td><span class="fb-status fail"><span class="fb-seal fail"></span>Failing</span></td><td><div class="fb-tdname">MFA enforced for all admin accounts</div></td><td><span class="fb-stamp">AUTO / 22M</span></td></tr>
      <tr class="fb-row"><td><span class="fb-status ok"><span class="fb-seal ok"></span>Passing</span></td><td><div class="fb-tdname">Change management</div></td><td><span class="fb-stamp">AUTO / 8M</span></td></tr>
    </tbody>
  </table></div>
</div>`;

const TOOLBAR_SNIPPET = `<div class="fb-toolbar">
  <button type="button" class="fb-chip on">All<span class="n">312</span></button>
  <button type="button" class="fb-chip">Failing<span class="n">9</span></button>
  <button type="button" class="fb-chip">Due soon<span class="n">6</span></button>
  <button type="button" class="fb-chip">Ready<span class="n">297</span></button>
  <span class="fb-spacer"></span>
  <input type="search" class="fb-search" placeholder="Filter controls..." aria-label="Filter controls">
</div>`;

export const Table = {
    render: () =>
        page({
            eyebrow, title: "Tables", css: CSS,
            lead: "The workhorse list. Mono column headers, rows grouped exceptions-first with a red hot group for overdue, a name and sub per item, the status seal and provenance stamp inline, and a one-click verb action. It closes with an honest Showing n of m.",
            body:
                section("List", "Failing and due-soon rise to the top; each row shows what it is, whether it holds, where it came from, who owns it, and when it is due.",
                    example("Grouped list", "", TABLE_SNIPPET, onField(TABLE_FULL))),
        }),
};

export const Selection = {
    render: () =>
        page({
            eyebrow, title: "Selection", css: CSS,
            lead: "Select rows to act on many at once. The dark bulk bar appears on the first selection and names the actions; select-all covers the visible rows. In practice a bulk action reports its result per item and never fails part-way in silence.",
            body:
                section("Bulk selection", "Tick rows (or the header box) to reveal the bulk bar. The snippet shows the markup; the count and show/hide are wired in code.",
                    example("Selectable list", "", SELECT_SNIPPET, onField(SELECT_FULL))),
        }),
};

export const Toolbar = {
    render: () =>
        page({
            eyebrow, title: "Toolbar", css: CSS,
            lead: "Filter chips with counts sit above the list, the active set always visible without opening a menu, with search on the right. Passing counts inform; they never nag.",
            body:
                section("Toolbar", "Chips are single-select here; each carries its count. Click to switch the active filter.",
                    example("Filter and search", "", TOOLBAR_SNIPPET, onField(TOOLBAR_FULL))),
        }),
};
