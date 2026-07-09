// Components / Tabs. The underline-indicator tab bar from the prototype: the
// active tab carries a brand underline and semibold weight, an optional mono
// count sits after the label, and panes switch beneath. Wired as an ARIA tablist
// (role tab/tabpanel, aria-selected/controls). Reference only: story-scoped.

import { SANS, MONO, page, section, example } from "./_ui.js";

export default {
    title: "Components/Tabs",
    parameters: { layout: "fullscreen" },
};

const CSS = `
  .fb-tabwrap { background:#fff; border:1px solid #e0e3dc; border-radius:10px; overflow:hidden; }
  .fb-tabs { display:flex; gap:2px; padding:0 14px; border-bottom:1px solid #e0e3dc; }
  .fb-tab { font:500 13px ${SANS}; color:#616a66; padding:10px 12px; border:none; background:none; border-bottom:2px solid transparent; margin-bottom:-1px; cursor:pointer; }
  .fb-tab:hover, .fb-tab.is-hover { color:#1a1d1c; }
  .fb-tab.on { color:#1a1d1c; font-weight:600; border-bottom-color:#4f46c8; }
  .fb-tab:focus-visible { outline:2px solid #4f46c8; outline-offset:-2px; border-radius:4px; }
  .fb-tab .n { font:500 10px ${MONO}; color:#8a938e; margin-left:4px; }
  .fb-pane { display:none; padding:16px; font-size:13px; color:#616a66; line-height:1.5; }
  .fb-pane.on { display:block; }
`;

const eyebrow = "Components / Tabs";
const onField = (html) => `<div style="background:#f1f2ee;padding:20px;border-radius:8px;width:100%">${html}</div>`;

// Self-contained switcher: activate the clicked tab, reveal its pane, hide the
// rest, and keep aria-selected and the hidden attribute in sync. Kept out of the
// copyable snippet, which shows clean ARIA markup.
const TABJS = "var t=this,bar=t.parentNode,ts=bar.querySelectorAll('.fb-tab');for(var i=0;i<ts.length;i++){ts[i].classList.remove('on');ts[i].setAttribute('aria-selected','false');}t.classList.add('on');t.setAttribute('aria-selected','true');var w=t.closest('.fb-tabwrap'),ps=w.querySelectorAll('.fb-pane'),id=t.getAttribute('aria-controls');for(var j=0;j<ps.length;j++){var on=ps[j].id===id;ps[j].classList.toggle('on',on);if(on){ps[j].removeAttribute('hidden');}else{ps[j].setAttribute('hidden','');}}";

const tab = (id, label, count, on) =>
    `<button type="button" class="fb-tab${on ? " on" : ""}" role="tab" aria-selected="${on ? "true" : "false"}" aria-controls="fbp-${id}" id="fbt-${id}" onclick="${TABJS}">${label}${count ? `<span class="n">${count}</span>` : ""}</button>`;

const pane = (id, label, body, on) =>
    `<div class="fb-pane${on ? " on" : ""}" role="tabpanel" id="fbp-${id}" aria-labelledby="fbt-${id}"${on ? "" : " hidden"}>${body}</div>`;

const TABS_FULL = `<div class="fb-tabwrap">
  <div class="fb-tabs" role="tablist" aria-label="Vendor views">
    ${tab("dir", "Directory", "38", true)}
    ${tab("dis", "Discovery", "2", false)}
    ${tab("rev", "Reviews", "3", false)}
    ${tab("pro", "Procurement", "1", false)}
  </div>
  ${pane("dir", "Directory", "38 vendors with tier, data class, status, and next review.", true)}
  ${pane("dis", "Discovery", "2 newly detected apps awaiting triage.", false)}
  ${pane("rev", "Reviews", "3 security reviews in flight.", false)}
  ${pane("pro", "Procurement", "1 intake request from the procurement form.", false)}
</div>`;

const TABS_SNIPPET = `<div class="fb-tabwrap">
  <div class="fb-tabs" role="tablist" aria-label="Vendor views">
    <button type="button" class="fb-tab on" role="tab" aria-selected="true" aria-controls="p-directory" id="t-directory">Directory<span class="n">38</span></button>
    <button type="button" class="fb-tab" role="tab" aria-selected="false" aria-controls="p-discovery" id="t-discovery">Discovery<span class="n">2</span></button>
  </div>
  <!-- Active pane shown; the rest carry hidden. Switching keeps aria-selected in sync; wired in code. -->
  <div class="fb-pane on" role="tabpanel" id="p-directory" aria-labelledby="t-directory">...</div>
  <div class="fb-pane" role="tabpanel" id="p-discovery" aria-labelledby="t-discovery" hidden>...</div>
</div>`;

const STATES = `<div class="fb-tabwrap">
  <div class="fb-tabs">
    <span class="fb-tab">Rest</span>
    <span class="fb-tab is-hover">Hover</span>
    <span class="fb-tab on">Active</span>
  </div>
  <div class="fb-pane on">Rest is muted; hover deepens to ink; the active tab adds a brand underline and semibold weight. Only the active tab carries colour on its label.</div>
</div>`;

export const Tabs = {
    render: () =>
        page({
            eyebrow, title: "Tabs", css: CSS,
            lead: "Sub-views of one page live as tabs, not separate nav items. An underline marks the active tab; a mono count can follow the label. Click a tab to switch panes - the demo keeps aria-selected and the hidden panes in sync.",
            body:
                section("Tabbed panel", "One tablist, one active tab, one visible pane. Counts inform without nagging.",
                    example("Tabs with counts", "", TABS_SNIPPET, onField(TABS_FULL))) +
                section("Keyboard", "How to make the tab bar keyboard-complete when you build it for real.",
                    `<div style="background:#fafbf8;border:1px dashed #c9cec5;border-radius:6px;padding:12px 14px;font-size:12.5px;color:#616a66;line-height:1.6;max-width:76ch">
                      Make the tablist a single tab stop with <b>roving tabindex</b>: the active tab is <code>tabindex="0"</code>, the rest <code>tabindex="-1"</code>. <b>Arrow Left/Right</b> move focus between tabs and wrap around; <b>Home</b> and <b>End</b> jump to the first and last. Activate <b>on focus</b> (automatic) when panes are cheap to render, or <b>on Enter/Space</b> (manual) when they are not - keeping <code>aria-selected="true"</code> on the focused-and-active tab and <code>tabindex="0"</code> moving with it. This reference wires click only; add the arrow-key handler in the component. Do not just give every tab <code>tabindex="0"</code> - roving tabindex is what keeps the tablist a single, predictable stop in the tab order.
                    </div>`),
        }),
};

export const States = {
    render: () =>
        page({
            eyebrow, title: "States", css: CSS,
            lead: "The three tab states. Illustrative only - these are spans, not an interactive tablist.",
            body:
                section("States", "Rest, hover, and active. The active tab is the only one that changes weight and gains the brand underline.",
                    onField(STATES)),
        }),
};
