// Components / Form controls. Inputs, choices, and inline messaging from the
// prototype: quiet panel-dim fields with a brand focus ring, a brand accent
// checkbox, the switch toggle, and the notice/guidance panels. Errors state what
// happened and what to do (UX rules F1/W5). Reference only: story-scoped literal
// values, app.css untouched.

export default {
    title: "Components/Form controls",
    parameters: { layout: "fullscreen" },
};

const MONO = "ui-monospace,'SF Mono',Menlo,Consolas,monospace";
const SANS = "system-ui,-apple-system,'Segoe UI',sans-serif";

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
  .fb-guidance { background:#fafbf8; border:1px dashed #c9cec5; border-radius:6px; padding:10px 12px; font-size:12.5px; color:#616a66; }
`;

const page = (title, lead, body) => `
  <style>${CSS}</style>
  <div style="background:#f1f2ee;min-height:100%;padding:26px 20px;font-family:${SANS};color:#1a1d1c">
    <div style="max-width:1040px;margin:0 auto">
      <div style="font:600 10px/1 ${MONO};letter-spacing:.14em;text-transform:uppercase;color:#8a938e;margin-bottom:8px">Components / Form controls</div>
      <h1 style="font-size:22px;font-weight:700;letter-spacing:-.015em;margin:0 0 6px">${title}</h1>
      <p style="font-size:14px;color:#616a66;max-width:72ch;margin:0 0 14px">${lead}</p>
      <div style="display:flex;gap:8px;align-items:baseline;background:#edecfa;border:1px solid rgba(79,70,200,.25);border-radius:10px;padding:10px 14px;margin-bottom:22px;font-size:12.5px;color:#3d36a3">
        <strong>Reference only.</strong><span>Prototype values, story-scoped. Not yet wired into the app theme.</span>
      </div>
      ${body}
    </div>
  </div>`;

const section = (name, desc, body) => `
  <section style="margin-bottom:26px">
    <h2 style="font-size:15px;font-weight:700;margin:0 0 3px">${name}</h2>
    <p style="font-size:13px;color:#616a66;margin:0 0 16px;max-width:74ch">${desc}</p>
    ${body}
  </section>`;

const card = (inner) => `<div style="background:#fff;border:1px solid #e0e3dc;border-radius:12px;padding:20px">${inner}</div>`;

export const TextFields = {
    render: () =>
        page(
            "Form controls",
            "Quiet fields on a recessed ground, so the values a person types stand out from the panel. A field states its label above and its help or error below - never a colour alone.",
            section("Text field", "Label, input, and an optional hint. Focus draws a 2px brand ring.", card(`<div style="display:grid;grid-template-columns:repeat(auto-fill,minmax(240px,1fr));gap:20px">
                <div>
                  <label class="fb-label" for="fb-email">Work email</label>
                  <input class="fb-input" id="fb-email" type="email" placeholder="you@example.com">
                  <div class="fb-hint">Used for audit-room invitations.</div>
                </div>
                <div>
                  <label class="fb-label" for="fb-focus">Focused</label>
                  <input class="fb-input is-focus" id="fb-focus" value="Access control policy">
                  <div class="fb-hint">The brand ring marks the active field.</div>
                </div>
              </div>`)) +
            section("Error state", "An error names what happened and what to do, next to the field it belongs to.", card(`<div style="max-width:320px">
                <label class="fb-label" for="fb-doc">Evidence name</label>
                <input class="fb-input is-error" id="fb-doc" value="">
                <div class="fb-error">Enter a name so this evidence can be linked to a control.</div>
              </div>`)),
        ),
};

export const Choices = {
    render: () =>
        page(
            "Choices",
            "A brand-accented checkbox for multi-select, and the switch toggle for a single on/off that takes effect immediately (like a gaps-only view).",
            section("Checkbox", "For selecting items and opting in. Uses the brand accent colour.", card(`<div style="display:flex;flex-direction:column;gap:12px">
                <label style="display:inline-flex;align-items:center;gap:9px;cursor:pointer;font-size:13px"><input type="checkbox" class="fb-ck" checked> Include archived evidence</label>
                <label style="display:inline-flex;align-items:center;gap:9px;cursor:pointer;font-size:13px"><input type="checkbox" class="fb-ck"> Notify the control owner</label>
              </div>`)) +
            section("Switch", "For an immediate on/off. Brand when on; the knob slides.", card(`<div style="display:flex;flex-direction:column;gap:14px">
                <label style="display:inline-flex;align-items:center;gap:11px;cursor:pointer;font-size:13px"><input type="checkbox" class="fb-sw" checked> Show gaps only</label>
                <label style="display:inline-flex;align-items:center;gap:11px;cursor:pointer;font-size:13px"><input type="checkbox" class="fb-sw"> Publish to trust center</label>
              </div>`)),
        ),
};

export const Messaging = {
    render: () =>
        page(
            "Messaging",
            "Inline panels that explain in place, where the confusion is, rather than in a help article. The warn public-surface banner lives with the external surfaces, not here.",
            section("Notice", "A brand-soft panel for an informational aside tied to the form.", card(`<div class="fb-notice">A sign-in link will be sent to this address. It expires in 15 minutes.</div>`)) +
            section("Guidance", "A dashed, recessed panel for how-to guidance beside a decision.", card(`<div class="fb-guidance" style="max-width:520px">Approve this policy version, or return it with comments. The current version stays in force until a new one is approved.</div>`)),
        ),
};
