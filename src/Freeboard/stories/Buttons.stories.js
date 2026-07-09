// Components / Buttons. The button component from the prototype: brand (primary),
// default (secondary), quiet (tertiary), with a small size. No danger button by
// design - destructive actions are quiet plus a confirmation that names the blast
// radius. Each example carries copyable markup. Reference only.

import { SANS, MONO, page, section, card, grid, cell, example, codeView } from "./_ui.js";

export default {
    title: "Components/Buttons",
    parameters: { layout: "fullscreen" },
};

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

const ICON_PLUS = `<svg width="13" height="13" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.8" aria-hidden="true"><path d="M8 3v10M3 8h10"/></svg>`;

const eyebrow = "Components / Buttons";

export const Variants = {
    render: () =>
        page({
            eyebrow, title: "Buttons", css: CSS,
            lead: "Three levels of emphasis and nothing more. One brand button per view carries the main action; default sits beside it; quiet handles low-emphasis and in-table actions. Copy any example straight into a template.",
            body:
                section("Variants", "Emphasis descends brand -> default -> quiet. Never two brand buttons competing in one view.",
                    example("Brand", "Primary action. One per view - the thing you most want done.", `<button class="fb-btn fb-btn--brand">Save changes</button>`) +
                    example("Default", "Secondary action. Neutral, sits beside the primary.", `<button class="fb-btn">Cancel</button>`) +
                    example("Quiet", "Tertiary. In-table and footer actions, including destructive ones behind a confirmation.", `<button class="fb-btn fb-btn--quiet">Dismiss</button>`)) +
                section("With icon", "A leading icon sits 6px before the label. Icons clarify, they do not replace the word.",
                    example("Icon and label", "", `<button class="fb-btn fb-btn--brand">${ICON_PLUS}New control</button>`)) +
                section("No danger button", "Destructive actions are quiet buttons paired with a confirmation that names what will break. Colour alone never carries the warning.",
                    codeView(`<button class="fb-btn fb-btn--quiet" style="color:#b3372d">Delete evidence</button>`)),
        }),
};

export const Sizes = {
    render: () =>
        page({
            eyebrow, title: "Sizes", css: CSS,
            lead: "Two sizes. Default everywhere; small for dense contexts like table rows and toolbars.",
            body:
                section("Sizes", "The small size trims padding and drops to 11.5px; the shape and weight are unchanged.",
                    example("Default", "12.5px. The standard size.", `<button class="fb-btn fb-btn--brand">Save changes</button>`) +
                    example("Small", "11.5px. Table rows, toolbars, dense panels.", `<button class="fb-btn fb-btn--brand fb-btn--sm">Fix</button>`)),
        }),
};

export const States = {
    render: () =>
        page({
            eyebrow, title: "States", css: CSS,
            lead: "Hover and focus are live - interact with any button to see them. Shown here side by side for reference. Focus uses a 2px brand ring and only appears for keyboard users (focus-visible).",
            body:
                section("States", "Rest, hover, keyboard focus, and disabled.", card(grid([
                    cell("Rest", "", `<button class="fb-btn fb-btn--brand">Save changes</button>`),
                    cell("Hover", "Brand darkens to brand-ink.", `<button class="fb-btn fb-btn--brand is-hover">Save changes</button>`),
                    cell("Focus", "2px brand ring, keyboard only.", `<button class="fb-btn fb-btn--brand is-focus">Save changes</button>`),
                    cell("Disabled", "50% opacity, not-allowed cursor.", `<button class="fb-btn fb-btn--brand" disabled>Save changes</button>`),
                ]))),
        }),
};
