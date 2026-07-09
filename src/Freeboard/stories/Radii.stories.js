// Brand corner-radius reference, from the product prototype. Only --r (10px) and
// --r-sm (6px) are named tokens there; the other corners (2/4/12/999px) are
// literals in use. Reference only: not wired into the Tailwind theme.

export default {
    title: "Brand/Radii",
    parameters: { layout: "fullscreen" },
};

const MONO = "ui-monospace,'SF Mono',Menlo,Consolas,monospace";
const SANS = "system-ui,-apple-system,'Segoe UI',sans-serif";

const page = (title, lead, body) => `
  <div style="background:#f1f2ee;min-height:100%;padding:26px 20px;font-family:${SANS};color:#1a1d1c">
    <div style="max-width:1040px;margin:0 auto">
      <div style="font:600 10px/1 ${MONO};letter-spacing:.14em;text-transform:uppercase;color:#8a938e;margin-bottom:8px">Brand / Radii</div>
      <h1 style="font-size:22px;font-weight:700;letter-spacing:-.015em;margin:0 0 6px">${title}</h1>
      <p style="font-size:14px;color:#616a66;max-width:72ch;margin:0 0 14px">${lead}</p>
      <div style="display:flex;gap:8px;align-items:baseline;background:#edecfa;border:1px solid rgba(79,70,200,.25);border-radius:10px;padding:10px 14px;margin-bottom:22px;font-size:12.5px;color:#3d36a3">
        <strong>Reference only.</strong><span>Only --r and --r-sm are named tokens in the prototype; the rest are values in use, tokenized on adoption. Not yet wired into the app theme.</span>
      </div>
      ${body}
    </div>
  </div>`;

const section = (name, desc, body) => `
  <section style="margin-bottom:30px">
    <h2 style="font-size:15px;font-weight:700;margin:0 0 3px">${name}</h2>
    <p style="font-size:13px;color:#616a66;margin:0 0 16px;max-width:74ch">${desc}</p>
    ${body}
  </section>`;

const tile = (t) => `
  <figure style="margin:0">
    <div style="height:80px;background:#fff;border:1px solid #c9cec5;border-radius:${t.px};box-shadow:0 1px 2px rgba(26,29,28,.05)"></div>
    <figcaption style="padding-top:11px">
      <div style="font:700 13px ${SANS};color:#1a1d1c">${t.label}</div>
      <div style="font:500 11px ${MONO};color:#8a938e;margin-top:2px">${t.token ? t.token + " = " + t.px : t.px + " (in use)"}</div>
      <div style="font-size:12.5px;color:#616a66;margin-top:6px;line-height:1.45">${t.use}</div>
    </figcaption>
  </figure>`;

const SCALE = [
    { label: "Seal", token: null, px: "2px", use: "Status seals and tiny square marks. The signature square paired with every status word." },
    { label: "Tag", token: null, px: "4px", use: "Tags, badges, provenance stamps, and keyboard hints." },
    { label: "Small", token: "--r-sm", px: "6px", use: "Buttons, inputs, nav items, and icon buttons." },
    { label: "Base", token: "--r", px: "10px", use: "Panels, cards, notices, and drawers. The default container radius." },
    { label: "Large", token: null, px: "12px", use: "Large overlays: the command palette and modal surfaces." },
    { label: "Full", token: null, px: "999px", use: "Filter chips, toggles, avatars, and progress bars." },
];

const demo = (label, note, html) => `
  <div style="display:flex;flex-direction:column;gap:12px;align-items:flex-start">
    <div style="min-height:54px;display:flex;align-items:center">${html}</div>
    <div>
      <div style="font:600 12.5px ${SANS};color:#1a1d1c">${label}</div>
      <div style="font:500 11px ${MONO};color:#8a938e;margin-top:1px">${note}</div>
    </div>
  </div>`;

const IN_CONTEXT = [
    demo("Panel", "--r / 10px", `<div style="width:154px;height:52px;background:#fff;border:1px solid #e0e3dc;border-radius:10px;box-shadow:0 1px 2px rgba(26,29,28,.05)"></div>`),
    demo("Button", "--r-sm / 6px", `<span style="display:inline-flex;background:#4f46c8;color:#fff;font:600 12.5px ${SANS};padding:7px 13px;border-radius:6px">Fix now</span>`),
    demo("Input", "--r-sm / 6px", `<span style="display:inline-flex;align-items:center;width:150px;background:#fafbf8;border:1px solid #e0e3dc;color:#8a938e;font:400 12.5px ${SANS};padding:6px 10px;border-radius:6px">Filter...</span>`),
    demo("Tag", "4px", `<span style="display:inline-flex;background:#eceeea;color:#616a66;font:500 11px ${SANS};padding:2px 8px;border-radius:4px">SOC 2</span>`),
    demo("Provenance stamp", "4px, dashed", `<span style="display:inline-flex;font:500 10px ${MONO};letter-spacing:.06em;text-transform:uppercase;color:#616a66;border:1px dashed #c9cec5;border-radius:4px;padding:2px 7px">AUTO / 22M</span>`),
    demo("Status seal", "2px", `<span style="display:inline-flex;align-items:center;gap:7px;font:600 12.5px ${SANS};color:#b3372d"><span style="width:9px;height:9px;border-radius:2px;background:#b3372d;display:block"></span>Failing</span>`),
    demo("Filter chip", "full / 999px", `<span style="display:inline-flex;background:#1a1d1c;color:#f1f2ee;font:500 12px ${SANS};padding:4px 12px;border-radius:999px">All</span>`),
    demo("Command palette", "12px", `<div style="width:160px;height:52px;background:#fff;border:1px solid #c9cec5;border-radius:12px;box-shadow:0 8px 22px rgba(26,29,28,.12)"></div>`),
];

export const Scale = {
    render: () =>
        page(
            "Radii",
            "Corners get rounder as surfaces get larger: a 2px square on a status seal, 10px on a panel, a full pill on a chip. The square seal is deliberate - it reads as a mark, not a button.",
            section("Scale", "From the 2px seal up to the full pill. --r-sm and --r carry most of the interface.", `<div style="display:grid;grid-template-columns:repeat(auto-fill,minmax(190px,1fr));gap:20px">${SCALE.map(tile).join("")}</div>`),
        ),
};

export const InContext = {
    render: () =>
        page(
            "Radii in context",
            "The same radii on the elements that use them, so the scale reads as shape, not just numbers.",
            section("In use", "Each element carries the radius noted beneath it.", `<div style="display:grid;grid-template-columns:repeat(auto-fill,minmax(180px,1fr));gap:24px 22px">${IN_CONTEXT.join("")}</div>`),
        ),
};
