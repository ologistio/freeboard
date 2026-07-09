// Components / Form controls. Inputs, choices, and inline messaging from the
// prototype: quiet panel-dim fields with a brand focus ring, a brand accent
// checkbox, the switch toggle, and notice/guidance panels across the semantic
// set. Errors state what happened and what to do (UX rules F1/W5). Each example
// carries copyable markup. Reference only.

import { SANS, MONO, page, section, example, codeView } from "./_ui.js";

export default {
    title: "Components/Form controls",
    parameters: { layout: "fullscreen" },
};

const CSS = `
  .fb-label { display:block; font:600 12.5px ${SANS}; color:#1a1d1c; margin-bottom:5px; }
  .fb-input { display:block; width:100%; font:400 13px ${SANS}; color:#1a1d1c; background:#fafbf8; border:1px solid #e0e3dc; border-radius:6px; padding:7px 10px; }
  .fb-input::placeholder { color:#8a938e; }
  .fb-input:focus, .fb-input.is-focus { outline:2px solid #4f46c8; outline-offset:0; border-color:#4f46c8; }
  .fb-input.is-error { border-color:#b3372d; }
  .fb-hint { font:400 12px ${SANS}; color:#616a66; margin-top:5px; }
  .fb-error { font:500 12px ${SANS}; color:#b3372d; margin-top:5px; }
  .fb-ck { width:15px; height:15px; accent-color:#4f46c8; cursor:pointer; }
  .fb-sw { position:relative; appearance:none; -webkit-appearance:none; width:34px; height:19px; background:#c9cec5; border-radius:99px; cursor:pointer; transition:background .15s; flex:none; }
  .fb-sw::after { content:""; position:absolute; top:2px; left:2px; width:15px; height:15px; background:#fff; border-radius:99px; transition:transform .15s; box-shadow:0 1px 2px rgba(0,0,0,.2); }
  .fb-sw:checked { background:#4f46c8; }
  .fb-sw:checked::after { transform:translateX(15px); }
  .fb-sw:focus-visible { outline:2px solid #4f46c8; outline-offset:2px; }
  .fb-notice { display:flex; align-items:center; gap:12px; background:#edecfa; border:1px solid rgba(79,70,200,.25); border-radius:10px; padding:11px 15px; font-size:13px; color:#3d36a3; }
  .fb-notice--ok { background:#e3f1e9; border-color:rgba(27,122,78,.3); color:#1b7a4e; }
  .fb-notice--warn { background:#f6eed6; border-color:rgba(150,105,10,.3); color:#96690a; }
  .fb-notice--fail { background:#f9e9e6; border-color:rgba(179,55,45,.3); color:#b3372d; }
  .fb-notice--titled { align-items:flex-start; }
  .fb-notice strong { font-weight:700; }
  .fb-notice p { margin:3px 0 0; font-weight:400; }
  .fb-guidance { background:#fafbf8; border:1px dashed #c9cec5; border-radius:6px; padding:10px 12px; font-size:12.5px; color:#616a66; }
`;

const eyebrow = "Components / Form controls";
const wrap = (w, html) => `<div style="width:100%;max-width:${w}px">${html}</div>`;

export const TextFields = {
    render: () =>
        page({
            eyebrow, title: "Form controls", css: CSS,
            lead: "Quiet fields on a recessed ground, so the values a person types stand out from the panel. A field states its label above and its help or error below - never a colour alone.",
            body:
                section("Text field", "Label bound with for/id; the hint is linked with aria-describedby. Focus draws a 2px brand ring.",
                    example("Field with hint", "", `<label class="fb-label" for="email">Work email</label>\n<input class="fb-input" id="email" type="email" placeholder="you@example.com" aria-describedby="email-hint">\n<div class="fb-hint" id="email-hint">Used for audit-room invitations.</div>`,
                        wrap(340, `<label class="fb-label" for="email">Work email</label><input class="fb-input" id="email" type="email" placeholder="you@example.com" aria-describedby="email-hint"><div class="fb-hint" id="email-hint">Used for audit-room invitations.</div>`))) +
                section("Error state", "An error names what happened and what to do. The field is marked aria-invalid and linked to the message with aria-describedby.",
                    example("Field with error", "", `<label class="fb-label" for="doc">Evidence name</label>\n<input class="fb-input is-error" id="doc" value="" aria-invalid="true" aria-describedby="doc-error">\n<div class="fb-error" id="doc-error">Enter a name so this evidence can be linked to a control.</div>`,
                        wrap(340, `<label class="fb-label" for="doc">Evidence name</label><input class="fb-input is-error" id="doc" value="" aria-invalid="true" aria-describedby="doc-error"><div class="fb-error" id="doc-error">Enter a name so this evidence can be linked to a control.</div>`))),
        }),
};

export const Choices = {
    render: () =>
        page({
            eyebrow, title: "Choices", css: CSS,
            lead: "A brand-accented checkbox for multi-select, and the switch toggle for a single on/off that takes effect immediately (like a gaps-only view).",
            body:
                section("Checkbox", "For selecting items and opting in. Uses the brand accent colour.",
                    example("Checkbox", "", `<label><input type="checkbox" class="fb-ck" checked> Include archived evidence</label>`,
                        `<label style="display:inline-flex;align-items:center;gap:9px;cursor:pointer;font-size:13px"><input type="checkbox" class="fb-ck" checked> Include archived evidence</label>`)) +
                section("Switch", "For an immediate on/off. Marked role=switch so it is announced as a switch, not a checkbox. Brand when on; the knob slides.",
                    example("Switch", "", `<label><input type="checkbox" class="fb-sw" role="switch" checked> Show gaps only</label>`,
                        `<label style="display:inline-flex;align-items:center;gap:11px;cursor:pointer;font-size:13px"><input type="checkbox" class="fb-sw" role="switch" checked> Show gaps only</label>`)),
        }),
};

export const Messaging = {
    render: () =>
        page({
            eyebrow, title: "Messaging", css: CSS,
            lead: "Inline panels that explain in place, where the confusion is, rather than in a help article. The soft ground carries the meaning; the text states it plainly. The warn public-surface banner lives with the external surfaces, not here.",
            body:
                section("Notices", "One shape, four grounds. Info is the default; the semantic grounds match the status palette. Never red for a merely informational aside.",
                    example("Info", "Neutral aside tied to the form.", `<div class="fb-notice" role="status">A sign-in link will be sent to this address. It expires in 15 minutes.</div>`, wrap(560, `<div class="fb-notice" role="status">A sign-in link will be sent to this address. It expires in 15 minutes.</div>`)) +
                    example("Success", "A positive outcome.", `<div class="fb-notice fb-notice--ok" role="status">Evidence attached. CC6.1 is now passing.</div>`, wrap(560, `<div class="fb-notice fb-notice--ok" role="status">Evidence attached. CC6.1 is now passing.</div>`)) +
                    example("Warning", "Something needs attention but is not yet broken.", `<div class="fb-notice fb-notice--warn" role="status">This source is stale. 14 checks depend on it and will degrade.</div>`, wrap(560, `<div class="fb-notice fb-notice--warn" role="status">This source is stale. 14 checks depend on it and will degrade.</div>`)) +
                    example("Error", "A dead end, with the way out named.", `<div class="fb-notice fb-notice--fail" role="alert">That sign-in link has expired. Request a new one to continue.</div>`, wrap(560, `<div class="fb-notice fb-notice--fail" role="alert">That sign-in link has expired. Request a new one to continue.</div>`))) +
                section("Notice with title", "A bold lead sets the headline, the body says what to do. Use when the message needs more than a sentence.",
                    example("Titled error", "", `<div class="fb-notice fb-notice--fail fb-notice--titled" role="alert">\n  <div>\n    <strong>Upload failed</strong>\n    <p>1 of 2 files did not attach. Re-upload the second file to complete the evidence.</p>\n  </div>\n</div>`,
                        wrap(560, `<div class="fb-notice fb-notice--fail fb-notice--titled" role="alert"><div><strong>Upload failed</strong><p>1 of 2 files did not attach. Re-upload the second file to complete the evidence.</p></div></div>`))) +
                section("Guidance", "A dashed, recessed panel for how-to guidance beside a decision.",
                    example("Guidance", "", `<div class="fb-guidance">Approve this policy version, or return it with comments. The current version stays in force until a new one is approved.</div>`,
                        wrap(560, `<div class="fb-guidance">Approve this policy version, or return it with comments. The current version stays in force until a new one is approved.</div>`))),
        }),
};
