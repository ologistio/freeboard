// Shared component stylesheet for the compositions. One source for the fb-* class
// styles (page header, buttons, panels, toolbar, table + marks, tabs, drawer) so
// compositions assemble components without re-inlining CSS. The per-component
// stories keep their own scoped CSS as the spec; compositions consume this.
// Reference only: literal prototype values, app.css untouched.

import { SANS, MONO, codeBlock } from "./_ui.js";

export const FB_CSS = `
  .fb-eyebrow { font:600 10px ${MONO}; letter-spacing:.14em; text-transform:uppercase; color:#8a938e; margin-bottom:6px; }
  .fb-pagehead { display:flex; align-items:flex-end; justify-content:space-between; gap:16px; margin-bottom:18px; flex-wrap:wrap; }
  .fb-pagehead h1 { font:700 21px ${SANS}; letter-spacing:-.015em; color:#1a1d1c; margin:0; }
  .fb-sub { font-size:13px; color:#616a66; margin-top:3px; max-width:62ch; }
  .fb-headactions { display:flex; gap:8px; flex:none; }

  .fb-btn { display:inline-flex; align-items:center; gap:6px; font:600 12.5px ${SANS}; color:#1a1d1c; background:#fff; border:1px solid #c9cec5; border-radius:6px; padding:6px 12px; cursor:pointer; }
  .fb-btn:hover { border-color:#8a938e; }
  .fb-btn--brand { background:#4f46c8; border-color:#4f46c8; color:#f1f2ee; }
  .fb-btn--brand:hover { background:#3d36a3; border-color:#3d36a3; }
  .fb-btn--quiet { background:transparent; border-color:transparent; color:#616a66; }
  .fb-btn--quiet:hover { background:rgba(26,29,28,.05); color:#1a1d1c; }
  .fb-btn--sm { padding:3px 9px; font-size:11.5px; }
  .fb-tbtn { font:600 11.5px ${SANS}; color:#1a1d1c; background:#fff; border:1px solid #c9cec5; border-radius:6px; padding:3px 9px; cursor:pointer; }
  .fb-tbtn:hover { border-color:#8a938e; }

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
  .fb-tbl tr.fb-rowlink { cursor:pointer; }
  .fb-tbl tr.fb-grp td { font:600 10px ${MONO}; letter-spacing:.12em; text-transform:uppercase; color:#616a66; background:#fafbf8; padding:6px 14px; }
  .fb-tbl tr.fb-grp.hot td { color:#b3372d; }
  .fb-tdname { font-weight:600; }
  .fb-tdsub { font-size:11.5px; color:#8a938e; margin-top:1px; }
  .fb-tfoot { font:500 10.5px ${MONO}; color:#8a938e; padding:9px 14px; border-top:1px solid #e0e3dc; }
  .fb-linkname { font:600 13px ${SANS}; color:#1a1d1c; background:none; border:none; padding:0; cursor:pointer; text-align:left; }
  .fb-linkname:hover { color:#3d36a3; text-decoration:underline; }

  .fb-status { display:inline-flex; align-items:center; gap:7px; font-size:12.5px; font-weight:500; white-space:nowrap; }
  .fb-status.ok { color:#1b7a4e; } .fb-status.fail { color:#b3372d; } .fb-status.warn { color:#96690a; } .fb-status.info { color:#2a6db0; }
  .fb-seal { width:9px; height:9px; border-radius:2px; display:inline-block; flex:none; }
  .fb-seal.ok { background:#1b7a4e; } .fb-seal.fail { background:#b3372d; box-shadow:0 0 0 2px #f9e9e6; } .fb-seal.warn { background:#96690a; } .fb-seal.info { background:#2a6db0; } .fb-seal.off { background:transparent; border:1.5px solid #c9cec5; }
  .fb-stamp { font:500 10px ${MONO}; letter-spacing:.06em; text-transform:uppercase; color:#616a66; border:1px dashed #c9cec5; border-radius:4px; padding:1px 6px; white-space:nowrap; }
  .fb-stamp.manual { color:#96690a; border-color:rgba(150,105,10,.4); } .fb-stamp.gen { color:#3d36a3; border-color:rgba(79,70,200,.4); }
  .fb-tag { display:inline-flex; font:500 11px ${SANS}; color:#616a66; background:#eceeea; border-radius:4px; padding:2px 8px; white-space:nowrap; }
  .fb-tag--brand { color:#3d36a3; background:#edecfa; } .fb-tag--ok { color:#1b7a4e; background:#e3f1e9; } .fb-tag--warn { color:#96690a; background:#f6eed6; } .fb-tag--fail { color:#b3372d; background:#f9e9e6; }
  .fb-owner { display:inline-flex; align-items:center; gap:7px; font-size:12.5px; color:#616a66; white-space:nowrap; }
  .fb-av { width:20px; height:20px; border-radius:99px; background:#eceeea; color:#616a66; font:700 9px ${SANS}; display:inline-flex; align-items:center; justify-content:center; flex:none; }
  .fb-due { font:500 11px ${MONO}; color:#616a66; white-space:nowrap; }
  .fb-due.soon { color:#96690a; } .fb-due.over { color:#b3372d; background:#f9e9e6; border-radius:4px; padding:1px 6px; }

  .fb-tabwrap { background:#fff; border:1px solid #e0e3dc; border-radius:10px; overflow:hidden; }
  .fb-tabs { display:flex; gap:2px; padding:0 14px; border-bottom:1px solid #e0e3dc; }
  .fb-tab { font:500 13px ${SANS}; color:#616a66; padding:10px 12px; border:none; background:none; border-bottom:2px solid transparent; margin-bottom:-1px; cursor:pointer; }
  .fb-tab:hover { color:#1a1d1c; }
  .fb-tab.on { color:#1a1d1c; font-weight:600; border-bottom-color:#4f46c8; }
  .fb-tab:focus-visible { outline:2px solid #4f46c8; outline-offset:-2px; border-radius:4px; }
  .fb-tab .n { font:500 10px ${MONO}; color:#8a938e; margin-left:4px; }
  .fb-pane { display:none; }
  .fb-pane.on { display:block; }
  .fb-pane__pad { padding:16px; font-size:13px; color:#616a66; line-height:1.5; }

  /* Drawer opens over the whole page (viewport-fixed), matching Components/Drawer. */
  .fb-demo { position:relative; }
  .fb-scrim { position:fixed; inset:0; background:rgba(26,29,28,.28); opacity:0; pointer-events:none; transition:opacity .15s; z-index:40; }
  .fb-scrim.show { opacity:1; pointer-events:auto; }
  .fb-drawer { position:fixed; top:0; right:0; bottom:0; width:440px; max-width:92vw; background:#fff; border-left:1px solid #e0e3dc; transform:translateX(100%); transition:transform .18s ease-out; z-index:50; display:flex; flex-direction:column; font-family:${SANS}; }
  .fb-drawer.open { transform:none; box-shadow:-12px 0 40px rgba(26,29,28,.12); }
  .fb-drawer:focus-visible { outline:2px solid #4f46c8; outline-offset:-2px; }
  .fb-dhead { padding:16px 18px 0; position:relative; }
  .fb-dbody { padding:14px 18px; overflow-y:auto; flex:1; }
  .fb-dfoot { display:flex; gap:8px; padding:12px 18px; border-top:1px solid #e0e3dc; }
  .fb-drawer h2 { font:700 16px ${SANS}; color:#1a1d1c; margin:4px 0 2px; }
  .fb-dsec { margin-top:16px; }
  .fb-dl { font:600 9.5px ${MONO}; letter-spacing:.13em; text-transform:uppercase; color:#8a938e; margin-bottom:7px; }
  .fb-dsec p { font-size:13px; color:#616a66; line-height:1.5; margin:0; }
  .fb-dlist { list-style:none; margin:0; padding:0; }
  .fb-dlist li { display:flex; align-items:center; justify-content:space-between; gap:8px; padding:7px 0; border-bottom:1px solid #e0e3dc; font-size:12.5px; color:#1a1d1c; }
  .fb-dlist li:last-child { border-bottom:none; }
  .fb-guidance { background:#fafbf8; border:1px dashed #c9cec5; border-radius:6px; padding:10px 12px; font-size:12.5px; color:#616a66; }
  .fb-xbtn { position:absolute; top:12px; right:14px; font:600 11.5px ${SANS}; color:#616a66; background:none; border:none; cursor:pointer; padding:4px 6px; border-radius:6px; }
  .fb-xbtn:hover { color:#1a1d1c; background:rgba(26,29,28,.05); }
  .fb-dbtn { font:600 12.5px ${SANS}; color:#1a1d1c; background:#fff; border:1px solid #c9cec5; border-radius:6px; padding:6px 12px; cursor:pointer; }
  .fb-dbtn:hover { border-color:#8a938e; }
  .fb-dbtn--brand { background:#4f46c8; border-color:#4f46c8; color:#f1f2ee; }
  .fb-dbtn--brand:hover { background:#3d36a3; border-color:#3d36a3; }

  .fb-app { display:grid; grid-template-columns:236px 1fr; height:100%; background:#f1f2ee; }
  .fb-stage { display:flex; flex-direction:column; min-width:0; }
  .fb-main { flex:1; overflow-y:auto; padding:22px; }
  .fb-rail { display:flex; flex-direction:column; border-right:1px solid #e0e3dc; padding:14px 10px 10px; overflow-y:auto; background:#f1f2ee; }
  .fb-brand { display:flex; align-items:baseline; gap:8px; padding:2px 8px 12px; }
  .fb-mark { font:600 13px ${MONO}; color:#f1f2ee; background:#1a1d1c; border-radius:6px; padding:3px 7px; letter-spacing:.02em; }
  .fb-name { font:700 15px ${SANS}; letter-spacing:-.01em; color:#1a1d1c; }
  .fb-rev { font:500 9.5px ${MONO}; color:#3d36a3; border:1px dashed #4f46c8; border-radius:4px; padding:1px 5px; text-transform:uppercase; letter-spacing:.08em; }
  .fb-search-entry { display:flex; align-items:center; justify-content:space-between; width:100%; padding:7px 10px; margin:0 0 10px; background:#fff; border:1px solid #e0e3dc; border-radius:6px; color:#8a938e; font-size:13px; text-align:left; cursor:pointer; }
  .fb-search-entry:hover { border-color:#c9cec5; color:#616a66; }
  .fb-kbd { font:500 10px ${MONO}; color:#616a66; border:1px solid #e0e3dc; border-bottom-width:2px; border-radius:4px; padding:1px 5px; background:#fafbf8; }
  .fb-navwrap { flex:1; display:flex; flex-direction:column; }
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
  .fb-topbar { display:flex; align-items:center; justify-content:space-between; height:52px; padding:0 22px; border-bottom:1px solid #e0e3dc; background:#f1f2ee; flex:none; }
  .fb-crumb { font-size:13px; color:#616a66; }
  .fb-crumb b { color:#1a1d1c; font-weight:600; }
  .fb-topbar-right { display:flex; align-items:center; gap:10px; }
  .fb-countdown { font:500 11px ${MONO}; color:#96690a; background:#f6eed6; border:1px solid rgba(150,105,10,.25); border-radius:99px; padding:3px 10px; letter-spacing:.02em; }
  .fb-iconbtn { position:relative; width:30px; height:30px; border-radius:6px; border:1px solid #e0e3dc; background:#fff; color:#616a66; display:inline-flex; align-items:center; justify-content:center; cursor:pointer; }
  .fb-iconbtn:hover { border-color:#c9cec5; color:#1a1d1c; }
  .fb-pip { position:absolute; top:-3px; right:-3px; width:8px; height:8px; border-radius:99px; background:#b3372d; border:2px solid #f1f2ee; }
  .fb-avatar { width:30px; height:30px; border-radius:99px; background:#edecfa; color:#3d36a3; font:700 11px ${SANS}; display:inline-flex; align-items:center; justify-content:center; border:none; cursor:pointer; }
`;

// Shared page frame for a composition: field ground, a reference-only ribbon, the
// rendered page, then its assembled markup in a copy block.
export const compPage = (rendered, snippet) => `
  <style>${FB_CSS}</style>
  <div style="background:#f1f2ee;min-height:100vh;padding:24px 22px;font-family:${SANS};color:#1a1d1c">
    <div style="max-width:1100px;margin:0 auto">
      <div style="display:flex;gap:8px;align-items:baseline;background:#edecfa;border:1px solid rgba(79,70,200,.25);border-radius:10px;padding:10px 14px;margin-bottom:20px;font-size:12.5px;color:#3d36a3">
        <strong>Reference only.</strong><span>A composition of existing components. Not wired into the app.</span>
      </div>
      ${rendered}
      <div style="font:600 10px/1 ${MONO};letter-spacing:.14em;text-transform:uppercase;color:#8a938e;margin:26px 0 10px">Assembled markup</div>
      ${codeBlock(snippet)}
    </div>
  </div>`;
