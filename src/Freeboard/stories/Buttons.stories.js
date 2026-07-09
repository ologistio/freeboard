// Components / Buttons. The button component from the product prototype: brand
// (primary), default (secondary), and quiet (tertiary), with a small size. There
// is no danger button by design - destructive actions use quiet plus a
// confirmation that names its blast radius. Reference only: the classes are
// story-scoped literal values, not app tokens, so the live app is unaffected.

export default {
    title: "Components/Buttons",
    parameters: { layout: "fullscreen" },
};

const MONO = "ui-monospace,'SF Mono',Menlo,Consolas,monospace";
const SANS = "system-ui,-apple-system,'Segoe UI',sans-serif";

const CSS = `
  .fb-btn {
    display:inline-flex; align-items:center; gap:6px;
    font:600 12.5px/1 ${SANS}; color:#1a1d1c;
    background:#fff; border:1px solid #c9cec5; border-radius:6px;
    padding:6px 12px; cursor:pointer;
    transition:border-color .12s, background .12s, color .12s;
  }
  .fb-btn:hover, .fb-btn.is-hover { border-color:#8a938e; }
  .fb-btn:focus-visible, .fb-btn.is-focus { outline:2px solid #4f46c8; outline-offset:2px; }
  .fb-btn:disabled { opacity:.5; cursor:not-allowed; }
  .fb-btn--brand { background:#4f46c8; border-color:#4f46c8; color:#f1f2ee; }
  .fb-btn--brand:hover, .fb-btn--brand.is-hover { background:#3d36a3; border-color:#3d36a3; }
  .fb-btn--quiet { background:transparent; border-color:transparent; color:#616a66; }
  .fb-btn--quiet:hover, .fb-btn--quiet.is-hover { background:rgba(26,29,28,.05); color:#1a1d1c; }
  .fb-btn--sm { padding:3px 9px; font-size:11.5px; }
`;

const page = (title, lead, body) => `
  <style>${CSS}</style>
  <div style="background:#f1f2ee;min-height:100%;padding:26px 20px;font-family:${SANS};color:#1a1d1c">
    <div style="max-width:1040px;margin:0 auto">
      <div style="font:600 10px/1 ${MONO};letter-spacing:.14em;text-transform:uppercase;color:#8a938e;margin-bottom:8px">Components / Buttons</div>
      <h1 style="font-size:22px;font-weight:700;letter-spacing:-.015em;margin:0 0 6px">${title}</h1>
      <p style="font-size:14px;color:#616a66;max-width:72ch;margin:0 0 14px">${lead}</p>
      <div style="display:flex;gap:8px;align-items:baseline;background:#edecfa;border:1px solid rgba(79,70,200,.25);border-radius:10px;padding:10px 14px;margin-bottom:22px;font-size:12.5px;color:#3d36a3">
        <strong>Reference only.</strong><span>Prototype values, story-scoped. Not yet wired into the app theme; the live app still uses its current buttons.</span>
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

const cell = (label, note, html) => `
  <div style="display:flex;flex-direction:column;gap:12px;align-items:flex-start">
    <div style="min-height:40px;display:flex;align-items:center">${html}</div>
    <div>
      <div style="font:600 12.5px ${SANS};color:#1a1d1c">${label}</div>
      ${note ? `<div style="font-size:12px;color:#616a66;margin-top:2px;max-width:34ch;line-height:1.4">${note}</div>` : ""}
    </div>
  </div>`;

const grid = (cells) => `<div style="display:grid;grid-template-columns:repeat(auto-fill,minmax(210px,1fr));gap:24px 22px">${cells.join("")}</div>`;

const ICON_PLUS = `<svg width="13" height="13" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.8" aria-hidden="true"><path d="M8 3v10M3 8h10"/></svg>`;
const ICON_CHECK = `<svg width="13" height="13" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.8" aria-hidden="true"><path d="M3 8.5l3.5 3.5L13 5"/></svg>`;

export const Variants = {
    render: () =>
        page(
            "Buttons",
            "Three levels of emphasis and nothing more. One brand button per view carries the main action; default sits beside it; quiet handles low-emphasis and in-table actions.",
            section("Variants", "Emphasis descends brand -> default -> quiet. Never two brand buttons competing in one view.", card(grid([
                cell("Brand", "Primary action. One per view - the thing you most want done.", `<button class="fb-btn fb-btn--brand">Save changes</button>`),
                cell("Default", "Secondary action. Neutral, sits beside the primary.", `<button class="fb-btn">Cancel</button>`),
                cell("Quiet", "Tertiary. In-table and footer actions, including destructive ones behind a confirmation.", `<button class="fb-btn fb-btn--quiet">Dismiss</button>`),
            ]))) +
            section("With icon", "A leading icon sits 6px before the label. Icons clarify, they do not replace the word.", card(`<div style="display:flex;gap:12px;flex-wrap:wrap">
                <button class="fb-btn fb-btn--brand">${ICON_PLUS}New control</button>
                <button class="fb-btn">${ICON_CHECK}Approve</button>
            </div>`)) +
            section("No danger button", "Destructive actions are quiet buttons paired with a confirmation that names what will break (see the UX rules on blast radius). Colour alone never carries the warning.", card(`<button class="fb-btn fb-btn--quiet" style="color:#b3372d">Delete evidence</button>`)),
        ),
};

export const Sizes = {
    render: () =>
        page(
            "Sizes",
            "Two sizes. Default everywhere; small for dense contexts like table rows and toolbars.",
            section("Sizes", "The small size trims padding and drops to 11.5px; the shape and weight are unchanged.", card(grid([
                cell("Default", "12.5px. The standard size.", `<button class="fb-btn fb-btn--brand">Save changes</button>`),
                cell("Small", "11.5px. Table rows, toolbars, dense panels.", `<button class="fb-btn fb-btn--brand fb-btn--sm">Fix</button>`),
                cell("Default, secondary", "", `<button class="fb-btn">Export</button>`),
                cell("Small, secondary", "", `<button class="fb-btn fb-btn--sm">Review</button>`),
            ]))),
        ),
};

export const States = {
    render: () =>
        page(
            "States",
            "Hover and focus are live - interact with any button above to see them. Shown here side by side for reference. Focus uses a 2px brand ring and only appears for keyboard users (focus-visible).",
            section("States", "Rest, hover, keyboard focus, and disabled.", card(grid([
                cell("Rest", "", `<button class="fb-btn fb-btn--brand">Save changes</button>`),
                cell("Hover", "Brand darkens to brand-ink; default deepens its border.", `<button class="fb-btn fb-btn--brand is-hover">Save changes</button>`),
                cell("Focus", "2px brand ring, keyboard only.", `<button class="fb-btn fb-btn--brand is-focus">Save changes</button>`),
                cell("Disabled", "50% opacity, not-allowed cursor.", `<button class="fb-btn fb-btn--brand" disabled>Save changes</button>`),
            ]))),
        ),
};
