// Compositions / List with drawer. A Controls list page where clicking a control
// name opens the detail drawer over a scrim - the core object-interaction loop
// (list -> drawer). The drawer's top (id, title, status, assertion) fills from
// the clicked row; the rest of the anatomy is illustrative. Reference only.

import { compPage } from "./_marks.js";

export default {
    title: "Compositions/List with drawer",
    parameters: { layout: "fullscreen" },
};

const CHIP = "var p=this.parentNode;var c=p.querySelectorAll('.fb-chip');for(var i=0;i<c.length;i++)c[i].classList.remove('on');this.classList.add('on')";
const OPEN = "var t=this,w=t.closest('.fb-demo');w.querySelector('#fbc-eyebrow').textContent=t.getAttribute('data-cid');w.querySelector('#fbc-title').textContent=t.getAttribute('data-cname');w.querySelector('#fbc-desc').textContent=t.getAttribute('data-cdesc');var cls=t.getAttribute('data-ccls'),st=w.querySelector('#fbc-status');st.className='fb-status '+cls;st.querySelector('.fb-seal').className='fb-seal '+cls;w.querySelector('#fbc-word').textContent=t.getAttribute('data-cword');w.querySelector('.fb-scrim').classList.add('show');var d=w.querySelector('.fb-drawer');d.classList.add('open');d.setAttribute('aria-hidden','false');d.focus();";
const CLOSE = "var w=this.closest('.fb-demo');w.querySelector('.fb-scrim').classList.remove('show');var d=w.querySelector('.fb-drawer');d.classList.remove('open');d.setAttribute('aria-hidden','true');";
const ESC = "if(event.key==='Escape'){var w=this.closest('.fb-demo');w.querySelector('.fb-scrim').classList.remove('show');this.classList.remove('open');this.setAttribute('aria-hidden','true');}";

const nameBtn = (id, name, desc, word, cls) =>
    `<button type="button" class="fb-linkname" data-cid="${id}" data-cname="${name}" data-cdesc="${desc}" data-cword="${word}" data-ccls="${cls}" onclick="${OPEN}">${name}</button>`;

const DRAWER = `<div class="fb-scrim" onclick="${CLOSE}"></div>
  <aside class="fb-drawer" role="dialog" aria-modal="true" aria-labelledby="fbc-title" aria-hidden="true" tabindex="-1" onkeydown="${ESC}">
    <div class="fb-dhead">
      <button type="button" class="fb-xbtn" aria-label="Close" onclick="${CLOSE}">Close</button>
      <div class="fb-eyebrow" id="fbc-eyebrow">CC6.1</div>
      <h2 id="fbc-title">Access requires MFA</h2>
      <span class="fb-status fail" id="fbc-status"><span class="fb-seal fail"></span><span id="fbc-word">Failing</span></span>
    </div>
    <div class="fb-dbody">
      <div class="fb-dsec"><div class="fb-dl">What this control asserts</div><p id="fbc-desc">All human access to production requires a second factor.</p></div>
      <div class="fb-dsec"><div class="fb-dl">Satisfies</div><div style="display:flex;gap:6px;flex-wrap:wrap"><span class="fb-tag fb-tag--brand">SOC 2</span><span class="fb-tag">ISO 27001</span><span class="fb-tag">HIPAA</span></div></div>
      <div class="fb-dsec"><div class="fb-dl">Proving checks</div><ul class="fb-dlist">
        <li><span>Admin accounts require MFA</span><span class="fb-status fail"><span class="fb-seal fail"></span>Failing</span></li>
        <li><span>SSO enforced for production apps</span><span class="fb-status ok"><span class="fb-seal ok"></span>Passing</span></li>
      </ul></div>
      <div class="fb-dsec"><div class="fb-dl">Evidence</div><ul class="fb-dlist">
        <li><span>MFA policy export</span><span class="fb-stamp">AUTO / 22M</span></li>
        <li><span>Break-glass review minutes</span><span class="fb-stamp manual">MANUAL / 02 JUL</span></li>
      </ul></div>
      <div class="fb-dsec"><div class="fb-dl">Guidance</div><div class="fb-guidance">Open the row to see the control's full record. Fixing it once updates every framework that maps to it.</div></div>
    </div>
    <div class="fb-dfoot"><button type="button" class="fb-dbtn fb-dbtn--brand">Fix now</button><button type="button" class="fb-dbtn">Assign owner</button></div>
  </aside>`;

const COMP = `<div class="fb-demo">
  <div class="fb-pagehead">
    <div>
      <div class="fb-eyebrow">Comply</div>
      <h1>Controls</h1>
      <div class="fb-sub">The primary object. Tests prove a control; frameworks borrow it. Open a row for its full record.</div>
    </div>
    <div class="fb-headactions"><button type="button" class="fb-btn fb-btn--brand">New control</button></div>
  </div>
  <div class="fb-tp">
    <div class="fb-toolbar">
      <button type="button" class="fb-chip on" onclick="${CHIP}">All<span class="n">312</span></button>
      <button type="button" class="fb-chip" onclick="${CHIP}">Failing<span class="n">9</span></button>
      <button type="button" class="fb-chip" onclick="${CHIP}">Evidence due<span class="n">6</span></button>
      <button type="button" class="fb-chip" onclick="${CHIP}">Ready<span class="n">297</span></button>
      <span class="fb-spacer"></span>
      <input type="search" class="fb-search" placeholder="Filter controls..." aria-label="Filter controls">
    </div>
    <div class="fb-scroll"><table class="fb-tbl">
      <thead><tr><th>Status</th><th>Control</th><th>Satisfies</th><th>Tests</th><th>Owner</th><th>Evidence</th></tr></thead>
      <tbody>
        <tr class="fb-row">
          <td><span class="fb-status fail"><span class="fb-seal fail"></span>Failing</span></td>
          <td>${nameBtn("CC6.1", "Access requires MFA", "All human access to production requires a second factor. Break-glass accounts are enumerated and reviewed quarterly.", "Failing", "fail")}</td>
          <td><span class="fb-tag fb-tag--brand">SOC 2</span> <span class="fb-tag">+2</span></td>
          <td><span class="fb-tag fb-tag--fail">1 / 4 failing</span></td>
          <td><span class="fb-owner"><span class="fb-av">JS</span>J. Sarah</span></td>
          <td><span class="fb-stamp">AUTO / 22M</span></td>
        </tr>
        <tr class="fb-row">
          <td><span class="fb-status warn"><span class="fb-seal warn"></span>Evidence due</span></td>
          <td>${nameBtn("CC6.8", "Penetration testing", "An independent penetration test is performed at least annually; findings are tracked to closure.", "Evidence due", "warn")}</td>
          <td><span class="fb-tag fb-tag--brand">SOC 2</span> <span class="fb-tag">+3</span></td>
          <td><span class="fb-tag">1 document test</span></td>
          <td><span class="fb-owner"><span class="fb-av">MO</span>M. Osei</span></td>
          <td><span class="fb-stamp manual">MANUAL / 9d</span></td>
        </tr>
        <tr class="fb-row">
          <td><span class="fb-status ok"><span class="fb-seal ok"></span>Ready</span></td>
          <td>${nameBtn("CC8.1", "Change management", "Changes to production are peer reviewed, tested, and traceable to an approved request.", "Ready", "ok")}</td>
          <td><span class="fb-tag fb-tag--brand">SOC 2</span> <span class="fb-tag">+2</span></td>
          <td><span class="fb-tag">5 tests</span></td>
          <td><span class="fb-owner"><span class="fb-av">RB</span>R. Byrne</span></td>
          <td><span class="fb-stamp">AUTO / 8M</span></td>
        </tr>
      </tbody>
    </table></div>
    <div class="fb-tfoot">Showing 3 of 312. Click a control name to open its record.</div>
  </div>
  ${DRAWER}
</div>`;

const SNIPPET = `<div class="fb-demo">
  <div class="fb-pagehead"> ...header with actions... </div>

  <div class="fb-tp">
    <div class="fb-toolbar"> ...chips + search... </div>
    <table class="fb-tbl">
      <thead><tr><th>Status</th><th>Control</th><th>Tests</th><th>Owner</th></tr></thead>
      <tbody>
        <tr class="fb-row">
          <td><span class="fb-status fail"><span class="fb-seal fail"></span>Failing</span></td>
          <td><button type="button" class="fb-linkname" data-cid="CC6.1" data-cname="Access requires MFA" ...>Access requires MFA</button></td>
          <td><span class="fb-tag fb-tag--fail">1 / 4 failing</span></td>
          <td><span class="fb-owner"><span class="fb-av">JS</span>J. Sarah</span></td>
        </tr>
      </tbody>
    </table>
    <div class="fb-tfoot">Showing 3 of 312.</div>
  </div>

  <!-- The drawer (role="dialog") fills its top from the clicked row; open/close,
       focus, and Escape are wired in code. See Components / Drawer for the anatomy. -->
  <div class="fb-scrim"></div>
  <aside class="fb-drawer" role="dialog" aria-modal="true" aria-labelledby="drawer-title" tabindex="-1"> ... </aside>
</div>`;

export const Controls = { render: () => compPage(COMP, SNIPPET) };
