// Components / Drawer. The object-detail panel from the prototype: a sheet that
// slides in from the right over a scrim, carrying the uniform object anatomy -
// eyebrow and title, status, then assertion, relations, evidence with
// provenance, guidance, and history, closing with actions. Wired as an ARIA
// dialog (role dialog, aria-modal, labelled by the title; Escape and scrim
// close; focus moves in on open and back to the opener on close). Reference only.

import { SANS, MONO, page, section, example } from "./_ui.js";

export default {
    title: "Components/Drawer",
    parameters: { layout: "fullscreen" },
};

const CSS = `
  .fb-demo { position:relative; }
  .fb-scrim { position:fixed; inset:0; background:rgba(26,29,28,.28); opacity:0; pointer-events:none; transition:opacity .15s; z-index:40; }
  .fb-scrim.show { opacity:1; pointer-events:auto; }
  .fb-drawer { position:fixed; top:0; right:0; bottom:0; width:440px; max-width:92vw; background:#fff; border-left:1px solid #e0e3dc; transform:translateX(100%); transition:transform .18s ease-out; z-index:50; display:flex; flex-direction:column; font-family:${SANS}; }
  .fb-drawer.open { transform:none; box-shadow:-12px 0 40px rgba(26,29,28,.12); }
  .fb-drawer:focus-visible { outline:2px solid #4f46c8; outline-offset:-2px; }
  .fb-sheet { background:#fff; border:1px solid #e0e3dc; border-radius:10px; max-width:440px; width:100%; font-family:${SANS}; }
  .fb-dhead { padding:16px 18px 0; position:relative; }
  .fb-dbody { padding:14px 18px; overflow-y:auto; flex:1; }
  .fb-dfoot { display:flex; gap:8px; padding:12px 18px; border-top:1px solid #e0e3dc; }
  .fb-drawer h2, .fb-sheet h2 { font:700 16px ${SANS}; color:#1a1d1c; margin:4px 0 2px; }
  .fb-eyebrow { font:600 9.5px ${MONO}; letter-spacing:.13em; text-transform:uppercase; color:#8a938e; }
  .fb-dsec { margin-top:16px; }
  .fb-dl { font:600 9.5px ${MONO}; letter-spacing:.13em; text-transform:uppercase; color:#8a938e; margin-bottom:7px; }
  .fb-dsec p { font-size:13px; color:#616a66; line-height:1.5; margin:0; }
  .fb-dlist { list-style:none; margin:0; padding:0; }
  .fb-dlist li { display:flex; align-items:center; justify-content:space-between; gap:8px; padding:7px 0; border-bottom:1px solid #e0e3dc; font-size:12.5px; color:#1a1d1c; }
  .fb-dlist li:last-child { border-bottom:none; }
  .fb-guidance { background:#fafbf8; border:1px dashed #c9cec5; border-radius:6px; padding:10px 12px; font-size:12.5px; color:#616a66; }
  .fb-xbtn { position:absolute; top:12px; right:14px; font:600 11.5px ${SANS}; color:#616a66; background:none; border:none; cursor:pointer; padding:4px 6px; border-radius:6px; }
  .fb-xbtn:hover { color:#1a1d1c; background:rgba(26,29,28,.05); }

  .fb-status { display:inline-flex; align-items:center; gap:7px; font-size:12.5px; font-weight:500; white-space:nowrap; }
  .fb-status.fail { color:#b3372d; } .fb-status.ok { color:#1b7a4e; }
  .fb-seal { width:9px; height:9px; border-radius:2px; display:inline-block; flex:none; }
  .fb-seal.fail { background:#b3372d; box-shadow:0 0 0 2px #f9e9e6; } .fb-seal.ok { background:#1b7a4e; }
  .fb-tag { display:inline-flex; font:500 11px ${SANS}; color:#616a66; background:#eceeea; border-radius:4px; padding:2px 8px; white-space:nowrap; }
  .fb-tag--brand { color:#3d36a3; background:#edecfa; }
  .fb-stamp { font:500 10px ${MONO}; letter-spacing:.06em; text-transform:uppercase; color:#616a66; border:1px dashed #c9cec5; border-radius:4px; padding:1px 6px; white-space:nowrap; }
  .fb-stamp.manual { color:#96690a; border-color:rgba(150,105,10,.4); }
  .fb-due { font:500 11px ${MONO}; color:#8a938e; white-space:nowrap; }
  .fb-dbtn { font:600 12.5px ${SANS}; color:#1a1d1c; background:#fff; border:1px solid #c9cec5; border-radius:6px; padding:6px 12px; cursor:pointer; }
  .fb-dbtn:hover { border-color:#8a938e; }
  .fb-dbtn--brand { background:#4f46c8; border-color:#4f46c8; color:#f1f2ee; }
  .fb-dbtn--brand:hover { background:#3d36a3; border-color:#3d36a3; }
`;

const eyebrow = "Components / Drawer";
const onField = (html) => `<div style="background:#f1f2ee;padding:20px;border-radius:8px;width:100%">${html}</div>`;

// `inert` (toggled with open) keeps the closed, off-screen drawer's controls out
// of the tab order; aria-hidden alone does not remove focusability.
const OPEN = "var w=this.closest('.fb-demo');w.querySelector('.fb-scrim').classList.add('show');var d=w.querySelector('.fb-drawer');d.removeAttribute('inert');d.classList.add('open');d.setAttribute('aria-hidden','false');d.focus();";
const CLOSE = "var w=this.closest('.fb-demo');w.querySelector('.fb-scrim').classList.remove('show');var d=w.querySelector('.fb-drawer');d.classList.remove('open');d.setAttribute('aria-hidden','true');d.setAttribute('inert','');var o=w.querySelector('.fb-opener');if(o)o.focus();";
const ESC = "if(event.key==='Escape'){var w=this.closest('.fb-demo');w.querySelector('.fb-scrim').classList.remove('show');this.classList.remove('open');this.setAttribute('aria-hidden','true');this.setAttribute('inert','');var o=w.querySelector('.fb-opener');if(o)o.focus();}";

const BODY = `<div class="fb-dbody">
    <div class="fb-dsec"><div class="fb-dl">What this control asserts</div><p>All human access to production requires a second factor. Break-glass accounts are enumerated and reviewed quarterly.</p></div>
    <div class="fb-dsec"><div class="fb-dl">Satisfies</div><div style="display:flex;gap:6px;flex-wrap:wrap"><span class="fb-tag fb-tag--brand">SOC 2 CC6.1</span><span class="fb-tag">ISO 27001 A.5.17</span><span class="fb-tag">HIPAA 164.312</span></div></div>
    <div class="fb-dsec"><div class="fb-dl">Proving checks</div><ul class="fb-dlist">
      <li><span>Admin accounts require MFA</span><span class="fb-status fail"><span class="fb-seal fail"></span>Failing</span></li>
      <li><span>SSO enforced for production apps</span><span class="fb-status ok"><span class="fb-seal ok"></span>Passing</span></li>
      <li><span>Break-glass reviewed quarterly</span><span class="fb-status ok"><span class="fb-seal ok"></span>Passing</span></li>
    </ul></div>
    <div class="fb-dsec"><div class="fb-dl">Evidence</div><ul class="fb-dlist">
      <li><span>MFA policy export</span><span class="fb-stamp">AUTO / 22M</span></li>
      <li><span>Break-glass review minutes</span><span class="fb-stamp manual">MANUAL / 02 JUL</span></li>
    </ul></div>
    <div class="fb-dsec"><div class="fb-dl">Guidance</div><div class="fb-guidance">Two admin accounts lack a second factor. Enforce the sign-on policy for the Admins group, or exclude them with a documented reason and an expiry.</div></div>
    <div class="fb-dsec"><div class="fb-dl">History</div><ul class="fb-dlist">
      <li><span>Failing since 06 Jul, 14:02</span><span class="fb-due">3d</span></li>
      <li><span>Owner assigned</span><span class="fb-due">May</span></li>
    </ul></div>
  </div>`;

const FOOT = `<div class="fb-dfoot"><button type="button" class="fb-dbtn fb-dbtn--brand">Fix now</button><button type="button" class="fb-dbtn">Assign owner</button><button type="button" class="fb-dbtn">Create issue</button></div>`;

const SHEET = `<div class="fb-sheet">
    <div class="fb-dhead"><div class="fb-eyebrow">CC6.1</div><h2>Access requires MFA</h2><span class="fb-status fail"><span class="fb-seal fail"></span>Failing / SLA 3d over</span></div>
    ${BODY}${FOOT}
  </div>`;

const DRAWER_DEMO = `<div class="fb-demo">
  <button type="button" class="fb-dbtn fb-dbtn--brand fb-opener" aria-haspopup="dialog" onclick="${OPEN}">Open control detail</button>
  <div class="fb-scrim" aria-hidden="true" onclick="${CLOSE}"></div>
  <div class="fb-drawer" role="dialog" aria-modal="true" aria-labelledby="fbd-title" aria-hidden="true" inert tabindex="-1" onkeydown="${ESC}">
    <div class="fb-dhead"><button type="button" class="fb-xbtn" aria-label="Close" onclick="${CLOSE}">Close</button><div class="fb-eyebrow">CC6.1</div><h2 id="fbd-title">Access requires MFA</h2><span class="fb-status fail"><span class="fb-seal fail"></span>Failing / SLA 3d over</span></div>
    ${BODY}${FOOT}
  </div>
</div>`;

const ANATOMY_SNIPPET = `<div class="fb-sheet">
  <div class="fb-dhead">
    <div class="fb-eyebrow">CC6.1</div>
    <h2>Access requires MFA</h2>
    <span class="fb-status fail"><span class="fb-seal fail"></span>Failing / SLA 3d over</span>
  </div>
  <div class="fb-dbody">
    <div class="fb-dsec"><div class="fb-dl">What this control asserts</div><p>...</p></div>
    <div class="fb-dsec"><div class="fb-dl">Satisfies</div><span class="fb-tag fb-tag--brand">SOC 2 CC6.1</span> ...</div>
    <div class="fb-dsec"><div class="fb-dl">Proving checks</div><ul class="fb-dlist">...</ul></div>
    <div class="fb-dsec"><div class="fb-dl">Evidence</div><ul class="fb-dlist">...</ul></div>
    <div class="fb-dsec"><div class="fb-dl">Guidance</div><div class="fb-guidance">...</div></div>
    <div class="fb-dsec"><div class="fb-dl">History</div><ul class="fb-dlist">...</ul></div>
  </div>
  <div class="fb-dfoot">
    <button type="button" class="fb-dbtn fb-dbtn--brand">Fix now</button>
    <button type="button" class="fb-dbtn">Assign owner</button>
  </div>
</div>`;

const OVERLAY_SNIPPET = `<!-- The trigger opens the dialog; scrim, drawer, focus and Escape are wired in code. -->
<button type="button" aria-haspopup="dialog">Open control detail</button>

<div class="fb-scrim" aria-hidden="true"></div>
<div class="fb-drawer" role="dialog" aria-modal="true" aria-labelledby="drawer-title" inert tabindex="-1">
  <div class="fb-dhead">
    <button type="button" class="fb-xbtn" aria-label="Close">Close</button>
    <div class="fb-eyebrow">CC6.1</div>
    <h2 id="drawer-title">Access requires MFA</h2>
    <span class="fb-status fail"><span class="fb-seal fail"></span>Failing / SLA 3d over</span>
  </div>
  <div class="fb-dbody"><!-- object anatomy: see the Anatomy story --></div>
  <div class="fb-dfoot">
    <button type="button" class="fb-dbtn fb-dbtn--brand">Fix now</button>
    <button type="button" class="fb-dbtn">Assign owner</button>
  </div>
</div>`;

export const Anatomy = {
    render: () =>
        page({
            eyebrow, title: "Drawer", css: CSS,
            lead: "Opening an object shows this sheet. Every object uses the same anatomy in the same order: eyebrow and title, status, then the assertion, its relations, the evidence with provenance, guidance, and history, closing with actions. Learn it once and every drawer reads the same.",
            body:
                section("Object anatomy", "Shown here as a static sheet so the whole structure is visible. In the app it renders inside the sliding drawer (see Overlay).",
                    example("Control detail", "", ANATOMY_SNIPPET, onField(SHEET))),
        }),
};

export const Overlay = {
    render: () =>
        page({
            eyebrow, title: "Overlay", css: CSS,
            lead: "The drawer slides in from the right over a scrim. Open it below; press Escape or click the scrim to close. It is an ARIA dialog: focus moves into it on open and returns to the opener on close.",
            body:
                section("Slide-in behaviour", "A right-anchored dialog over a dimmed scrim. The copyable snippet shows the shell and its ARIA; the open/close, focus, and Escape handling are wired in code.",
                    example("Detail drawer", "", OVERLAY_SNIPPET, DRAWER_DEMO)),
        }),
};
