// Brand colour reference. Values are the literal target palette from the product
// prototype ("audit ledger" direction), shown for light and dark. This is a
// reference only: the palette is not yet wired into the Tailwind theme, so the
// swatches use inline values rather than app tokens by design.

export default {
    title: "Brand/Colors",
    parameters: { layout: "fullscreen" },
};

const MONO = "ui-monospace,'SF Mono',Menlo,Consolas,monospace";
const SANS = "system-ui,-apple-system,'Segoe UI',sans-serif";

const lightSq = (c) =>
    `<span style="width:15px;height:15px;border-radius:3px;background:${c};border:1px solid rgba(26,29,28,.12);display:block"></span>`;
const darkSq = (c) =>
    `<span style="display:inline-flex;padding:2px;background:#1d2220;border-radius:4px"><span style="width:13px;height:13px;border-radius:2px;background:${c};display:block"></span></span>`;
const val = (label, sq, hex) =>
    `<span style="display:inline-flex;align-items:center;gap:6px;font:500 11.5px ${MONO};color:#616a66"><span style="color:#8a938e">${label}</span>${sq}<span>${hex}</span></span>`;

const swatchCard = (t) => `
  <figure style="margin:0;border:1px solid #e0e3dc;border-radius:10px;overflow:hidden;background:#fff;box-shadow:0 1px 2px rgba(26,29,28,.05)">
    <div style="height:74px;background:${t.l}"></div>
    <div style="height:24px;background:#1d2220;position:relative"><span style="position:absolute;inset:0;background:${t.d}"></span></div>
    <figcaption style="padding:10px 12px 12px">
      <div style="font:600 12.5px ${MONO};color:#1a1d1c">--${t.n}</div>
      <div style="font-size:12.5px;color:#616a66;margin-top:3px;line-height:1.45">${t.u}</div>
      <div style="display:flex;flex-wrap:wrap;gap:6px 14px;margin-top:10px">
        ${val("L", lightSq(t.l), t.l)}
        ${val("D", darkSq(t.d), t.d)}
      </div>
    </figcaption>
  </figure>`;

const semanticCard = (t) => `
  <figure style="margin:0;border:1px solid #e0e3dc;border-radius:10px;overflow:hidden;background:#fff;box-shadow:0 1px 2px rgba(26,29,28,.05)">
    <div style="height:60px;background:${t.l}"></div>
    <div style="height:22px;background:#1d2220;position:relative"><span style="position:absolute;inset:0;background:${t.d}"></span></div>
    <figcaption style="padding:10px 12px 12px">
      <div style="font:600 12.5px ${MONO};color:#1a1d1c">--${t.n}</div>
      <div style="font-size:12.5px;color:#616a66;margin-top:3px;line-height:1.45">${t.u}</div>
      <div style="margin-top:9px">
        <span style="display:inline-flex;align-items:center;gap:7px;background:${t.sl};color:${t.il};font:600 11.5px ${SANS};padding:3px 10px;border-radius:99px">
          <span style="width:8px;height:8px;border-radius:2px;background:${t.l};display:block"></span>${t.word}
        </span>
      </div>
      <div style="display:flex;flex-wrap:wrap;gap:6px 14px;margin-top:10px">
        ${val("base L", lightSq(t.l), t.l)}
        ${val("base D", darkSq(t.d), t.d)}
      </div>
      <div style="display:flex;flex-wrap:wrap;gap:6px 14px;margin-top:6px">
        ${val("soft L", lightSq(t.sl), t.sl)}
        ${val("soft D", darkSq(t.sd), t.sd)}
      </div>
    </figcaption>
  </figure>`;

const section = (name, desc, cards, cardFn) => `
  <section style="margin-bottom:30px">
    <h2 style="font-size:15px;font-weight:700;margin:0 0 3px">${name}</h2>
    <p style="font-size:13px;color:#616a66;margin:0 0 14px;max-width:74ch">${desc}</p>
    <div style="display:grid;grid-template-columns:repeat(auto-fill,minmax(190px,1fr));gap:14px">${cards.map(cardFn).join("")}</div>
  </section>`;

const page = (title, lead, body) => `
  <div style="background:#f1f2ee;min-height:100vh;padding:26px 20px;font-family:${SANS};color:#1a1d1c">
    <div style="max-width:1100px;margin:0 auto">
      <div style="font:600 10px/1 ${MONO};letter-spacing:.14em;text-transform:uppercase;color:#8a938e;margin-bottom:8px">Brand / Colours</div>
      <h1 style="font-size:22px;font-weight:700;letter-spacing:-.015em;margin:0 0 6px">${title}</h1>
      <p style="font-size:14px;color:#616a66;max-width:72ch;margin:0 0 14px">${lead}</p>
      ${body}
    </div>
  </div>`;

const BRAND = [
    { n: "brand", l: "#4f46c8", d: "#8f88ee", u: "Primary brand. Actions, active nav, focus rings." },
    { n: "brand-ink", l: "#3d36a3", d: "#aca6f4", u: "Brand text on soft grounds; primary-button hover." },
    { n: "brand-soft", l: "#edecfa", d: "rgba(143,136,238,.16)", u: "Tint ground for brand chips and notices." },
];

const NEUTRALS = [
    { n: "field", l: "#f1f2ee", d: "#151917", u: "App background. The ledger-paper ground." },
    { n: "panel", l: "#ffffff", d: "#1d2220", u: "Cards, tables, panels, and menus." },
    { n: "panel-dim", l: "#fafbf8", d: "#181d1b", u: "Recessed rows, toolbars, table group headers." },
    { n: "ink", l: "#1a1d1c", d: "#e7eae6", u: "Primary text and high-emphasis marks." },
    { n: "muted", l: "#616a66", d: "#a2aba5", u: "Secondary text and inactive nav." },
    { n: "faint", l: "#66706b", d: "#828c86", u: "Tertiary text, eyebrows, mono labels." },
    { n: "line", l: "#e0e3dc", d: "#2a302c", u: "Default borders and dividers." },
    { n: "line-strong", l: "#c9cec5", d: "#3d4540", u: "Emphasis borders and control outlines." },
];

// il/id are the on-soft word colours (light/dark). The 8px seal keeps the base
// (l/d); the word is painted from il/id so status text clears WCAG AA on the soft
// ground and on panel, which the base alone does not for every pairing.
const SEMANTIC = [
    { n: "ok", word: "Ready", l: "#1b7a4e", d: "#55b586", il: "#1b7a4e", id: "#55b586", sl: "#e3f1e9", sd: "rgba(85,181,134,.16)", u: "Passing tests, ready controls, success." },
    { n: "warn", word: "Due soon", l: "#96690a", d: "#d3a24a", il: "#875e08", id: "#d3a24a", sl: "#f6eed6", sd: "rgba(211,162,74,.16)", u: "Due-soon, drifting, and degraded sources." },
    { n: "fail", word: "Failing", l: "#b3372d", d: "#e07068", il: "#b3372d", id: "#ec8a82", sl: "#f9e9e6", sd: "rgba(224,112,104,.16)", u: "Failing tests, overdue items, blockers." },
    { n: "info", word: "In progress", l: "#2a6db0", d: "#6ea8dd", il: "#2a6db0", id: "#6ea8dd", sl: "#e7f0f8", sd: "rgba(110,168,221,.16)", u: "In-progress and informational states." },
    { n: "neutral", word: "Snoozed", l: "#6a726e", d: "#97a09a", il: "#626a66", id: "#97a09a", sl: "#eceeea", sd: "rgba(151,160,154,.16)", u: "Snoozed, excluded, and inert states." },
];

export const Brand = {
    render: () =>
        page(
            "Brand",
            "One violet carries identity across the product: actions, active navigation, focus, and the tint grounds that mark brand surfaces. Ink on bone keeps it calm; the brand is the only strong hue outside status.",
            section("Brand", "The brand triad. brand for solid marks, brand-ink for text on tints and hover, brand-soft for the tint ground itself.", BRAND, swatchCard),
        ),
};

export const Neutrals = {
    render: () =>
        page(
            "Neutrals",
            "The operational surface: paper grounds, panels, four text weights, and two border strengths. These carry most of the interface so status and brand stay meaningful.",
            section("Surfaces and text", "Backgrounds ascend field -> panel; text descends ink -> muted -> faint; borders come in line and line-strong.", NEUTRALS, swatchCard),
        ),
};

export const Semantic = {
    render: () =>
        page(
            "Semantic",
            "Five status colours, each paired with a soft ground. Status is always a shape plus a word on a soft ground (never colour alone), which keeps dark text at a high contrast ratio and reads for colour-blind users.",
            section("Status", "Base colour for seals and text, soft ground for the pill behind them. Red is reserved for fail and overdue; amber for warn.", SEMANTIC, semanticCard),
        ),
};
