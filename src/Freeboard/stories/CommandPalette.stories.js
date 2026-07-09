// Components / Command palette. The Ctrl-K overlay from the prototype: a centred
// box with a search input over a filterable list of results, each tagged Page,
// Command, or Agent. Wired as an ARIA combobox + listbox: the input keeps focus
// and aria-activedescendant tracks the highlighted option. Reference only.

import { SANS, MONO, page, section, example } from "./_ui.js";

export default {
    title: "Components/Command palette",
    parameters: { layout: "fullscreen" },
};

const CSS = `
  .fb-pal-demo { position:relative; }
  .fb-dbtn { display:inline-flex; align-items:center; font:600 12.5px ${SANS}; color:#1a1d1c; background:#fff; border:1px solid #c9cec5; border-radius:6px; padding:6px 12px; cursor:pointer; }
  .fb-dbtn:hover { border-color:#8a938e; }
  .fb-dbtn--brand { background:#4f46c8; border-color:#4f46c8; color:#f1f2ee; }
  .fb-dbtn--brand:hover { background:#3d36a3; border-color:#3d36a3; }
  .fb-kbd { font:600 10px ${MONO}; color:#e7eae6; border:1px solid rgba(255,255,255,.35); border-radius:4px; padding:1px 5px; margin-left:8px; }

  .fb-pal { position:fixed; inset:0; background:rgba(26,29,28,.32); display:none; z-index:60; padding-top:12vh; }
  .fb-pal.open { display:block; }
  .fb-palbox { width:560px; max-width:92vw; margin:0 auto; background:#fff; border:1px solid #c9cec5; border-radius:12px; overflow:hidden; box-shadow:0 24px 60px rgba(26,29,28,.25); font-family:${SANS}; }
  .fb-palinput { display:block; width:100%; font:400 14px ${SANS}; color:#1a1d1c; padding:13px 16px; border:none; border-bottom:1px solid #e0e3dc; outline:none; }
  .fb-palinput::placeholder { color:#8a938e; }
  .fb-palinput--static { color:#8a938e; }
  .fb-pallist { list-style:none; margin:0; padding:0; max-height:320px; overflow-y:auto; }
  .fb-pallist li { display:flex; align-items:center; justify-content:space-between; gap:12px; padding:9px 16px; font-size:13px; color:#1a1d1c; cursor:pointer; }
  .fb-pallist li:hover, .fb-pallist li.hi { background:#edecfa; }
  .fb-hint { font:500 10px ${MONO}; color:#8a938e; text-transform:uppercase; letter-spacing:.08em; flex:none; }
  .fb-palfoot { font:500 10px ${MONO}; color:#8a938e; padding:8px 16px; border-top:1px solid #e0e3dc; }
`;

const eyebrow = "Components / Command palette";
const onField = (html) => `<div style="background:#f1f2ee;padding:20px;border-radius:8px;width:100%">${html}</div>`;

const OPEN = "var w=this.closest('.fb-pal-demo');var p=w.querySelector('.fb-pal');p.classList.add('open');var inp=p.querySelector('.fb-palinput');inp.value='';var l=p.querySelectorAll('.fb-pallist > li');for(var i=0;i<l.length;i++){l[i].style.display='';l[i].classList.remove('hi');l[i].setAttribute('aria-selected','false');}l[0].classList.add('hi');l[0].setAttribute('aria-selected','true');inp.setAttribute('aria-activedescendant',l[0].id);inp.focus();";
const BACKCLOSE = "if(event.target===this){this.classList.remove('open');var o=this.closest('.fb-pal-demo').querySelector('.fb-pal-open');if(o)o.focus();}";
const ITEMCLICK = "var p=this.closest('.fb-pal');p.classList.remove('open');var o=p.closest('.fb-pal-demo').querySelector('.fb-pal-open');if(o)o.focus();";
const FILTER = "var q=this.value.toLowerCase();var p=this.closest('.fb-pal');var l=p.querySelectorAll('.fb-pallist > li');var f=null;for(var i=0;i<l.length;i++){var s=l[i].textContent.toLowerCase().indexOf(q)>-1;l[i].style.display=s?'':'none';l[i].classList.remove('hi');l[i].setAttribute('aria-selected','false');if(s&&!f)f=l[i];}if(f){f.classList.add('hi');f.setAttribute('aria-selected','true');this.setAttribute('aria-activedescendant',f.id);}else{this.setAttribute('aria-activedescendant','');}";
const PALKEYS = "var inp=this;var p=inp.closest('.fb-pal');var a=p.querySelectorAll('.fb-pallist > li');var l=[];for(var i=0;i<a.length;i++){if(a[i].style.display!=='none')l.push(a[i]);}var c=-1;for(var j=0;j<l.length;j++){if(l[j].classList.contains('hi'))c=j;}var k=event.key;if(k==='ArrowDown'||k==='ArrowUp'){event.preventDefault();if(!l.length)return;var n=k==='ArrowDown'?(c<0?0:(c+1)%l.length):(c<=0?l.length-1:c-1);if(c>=0){l[c].classList.remove('hi');l[c].setAttribute('aria-selected','false');}l[n].classList.add('hi');l[n].setAttribute('aria-selected','true');inp.setAttribute('aria-activedescendant',l[n].id);l[n].scrollIntoView({block:'nearest'});}else if(k==='Enter'||k==='Escape'){event.preventDefault();p.classList.remove('open');var o=p.closest('.fb-pal-demo').querySelector('.fb-pal-open');if(o)o.focus();}";

const OPTS = [
    ["Home", "Page"],
    ["My work", "Page"],
    ["Controls", "Page"],
    ["Evidence", "Page"],
    ["Toggle dark mode", "Command"],
    ["Ask: which controls block the SOC 2 audit?", "Agent"],
    ["Ask: which vendors touch PII?", "Agent"],
];
const optHtml = (o, i) =>
    `<li id="fb-opt-${i}" role="option" class="${i === 0 ? "hi" : ""}" aria-selected="${i === 0 ? "true" : "false"}" onclick="${ITEMCLICK}"><span>${o[0]}</span><span class="fb-hint">${o[1]}</span></li>`;

const OVERLAY_DEMO = `<div class="fb-pal-demo">
  <button type="button" class="fb-dbtn fb-dbtn--brand fb-pal-open" aria-haspopup="dialog" onclick="${OPEN}">Open command palette<kbd class="fb-kbd">Ctrl K</kbd></button>
  <div class="fb-pal" onclick="${BACKCLOSE}">
    <div class="fb-palbox" role="dialog" aria-modal="true" aria-label="Command palette">
      <input class="fb-palinput" type="text" role="combobox" aria-expanded="true" aria-controls="fb-pallist" aria-activedescendant="fb-opt-0" aria-autocomplete="list" aria-label="Command palette search" placeholder="Jump to a page or ask..." autocomplete="off" oninput="${FILTER}" onkeydown="${PALKEYS}">
      <ul class="fb-pallist" id="fb-pallist" role="listbox" aria-label="Results">
        ${OPTS.map(optHtml).join("")}
      </ul>
      <div class="fb-palfoot">Enter to open / Esc to close / arrows to move</div>
    </div>
  </div>
</div>`;

const STATIC_PREVIEW = `<div class="fb-palbox" style="margin:0 auto">
  <div class="fb-palinput fb-palinput--static">Jump to a page or ask...</div>
  <ul class="fb-pallist">
    <li class="hi"><span>Home</span><span class="fb-hint">Page</span></li>
    <li><span>Controls</span><span class="fb-hint">Page</span></li>
    <li><span>Toggle dark mode</span><span class="fb-hint">Command</span></li>
    <li><span>Ask: which controls block the SOC 2 audit?</span><span class="fb-hint">Agent</span></li>
    <li><span>Ask: which vendors touch PII?</span><span class="fb-hint">Agent</span></li>
  </ul>
  <div class="fb-palfoot">Enter to open / Esc to close / arrows to move</div>
</div>`;

const STATIC_SNIPPET = `<div class="fb-palbox" role="dialog" aria-modal="true" aria-label="Command palette">
  <input class="fb-palinput" type="text" role="combobox" aria-expanded="true"
         aria-controls="pal-list" aria-activedescendant="pal-opt-0" aria-autocomplete="list"
         aria-label="Command palette search" placeholder="Jump to a page or ask..." autocomplete="off">
  <ul class="fb-pallist" id="pal-list" role="listbox" aria-label="Results">
    <li id="pal-opt-0" role="option" class="hi" aria-selected="true"><span>Home</span><span class="fb-hint">Page</span></li>
    <li id="pal-opt-1" role="option" aria-selected="false"><span>Toggle dark mode</span><span class="fb-hint">Command</span></li>
    <li id="pal-opt-2" role="option" aria-selected="false"><span>Ask: which controls block the SOC 2 audit?</span><span class="fb-hint">Agent</span></li>
  </ul>
  <div class="fb-palfoot">Enter to open / Esc to close / arrows to move</div>
</div>`;

const OVERLAY_SNIPPET = `<button type="button" aria-haspopup="dialog">Open command palette</button>

<div class="fb-pal"><!-- dim backdrop; click outside the box to close -->
  <div class="fb-palbox" role="dialog" aria-modal="true" aria-label="Command palette">
    <input class="fb-palinput" type="text" role="combobox" aria-expanded="true"
           aria-controls="pal-list" aria-activedescendant="pal-opt-0" aria-autocomplete="list"
           aria-label="Command palette search" placeholder="Jump to a page or ask..." autocomplete="off">
    <ul class="fb-pallist" id="pal-list" role="listbox" aria-label="Results"> ... </ul>
    <div class="fb-palfoot">Enter to open / Esc to close / arrows to move</div>
  </div>
</div>
<!-- Filtering, arrow navigation, Enter/Esc, and focus are wired in code; see Keyboard. -->`;

export const Palette = {
    render: () =>
        page({
            eyebrow, title: "Command palette", css: CSS,
            lead: "One entry for everything: jump to a page, run a command, or ask the assistant. Each result carries a mono tag saying which it is. This is the single search surface - there is no second search box elsewhere.",
            body:
                section("Anatomy", "A search input over a result list, each row tagged Page, Command, or Agent, with a keyboard-hint foot. Shown static here; open the real one under Shortcut.",
                    example("Palette box", "", STATIC_SNIPPET, onField(STATIC_PREVIEW))),
        }),
};

export const Shortcut = {
    render: () =>
        page({
            eyebrow, title: "Shortcut", css: CSS,
            lead: "The palette opens over a dim scrim. Open it below, type to filter, arrow through the results, Enter to run, Esc to close. The input keeps focus while aria-activedescendant tracks the highlighted result.",
            body:
                section("Open and search", "The copyable snippet shows the shell and its ARIA; filtering, arrow navigation, and focus are wired in code.",
                    example("Command palette", "", OVERLAY_SNIPPET, OVERLAY_DEMO)) +
                section("Keyboard", "How the palette behaves for keyboard and screen-reader users.",
                    `<div style="background:#fafbf8;border:1px dashed #c9cec5;border-radius:6px;padding:12px 14px;font-size:12.5px;color:#616a66;line-height:1.6;max-width:76ch">
                      Open with <b>Ctrl-K</b> (Cmd-K on macOS) or <b>/</b> from anywhere - the app adds that document-level listener; this reference uses a trigger button. Inside, the input stays focused: <b>Arrow Up/Down</b> move the highlight and <code>aria-activedescendant</code> follows it (do not move DOM focus onto the options), <b>Enter</b> runs the highlighted result, <b>Esc</b> closes and returns focus to where you were. Typing filters the list and the first match becomes the highlight. Restore focus to the opener on close.
                    </div>`),
        }),
};
