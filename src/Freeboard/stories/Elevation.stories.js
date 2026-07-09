// Brand elevation reference, from the product prototype. --shadow is the one
// named token (with a deeper dark-mode value); the drawer and palette shadows
// are literals. Reference only: not wired into the Tailwind theme.

export default {
    title: "Brand/Elevation",
    parameters: { layout: "fullscreen" },
};

const MONO = "ui-monospace,'SF Mono',Menlo,Consolas,monospace";
const SANS = "system-ui,-apple-system,'Segoe UI',sans-serif";

const page = (title, lead, body) => `
  <div style="background:#f1f2ee;min-height:100%;padding:26px 20px;font-family:${SANS};color:#1a1d1c">
    <div style="max-width:1040px;margin:0 auto">
      <div style="font:600 10px/1 ${MONO};letter-spacing:.14em;text-transform:uppercase;color:#8a938e;margin-bottom:8px">Brand / Elevation</div>
      <h1 style="font-size:22px;font-weight:700;letter-spacing:-.015em;margin:0 0 6px">${title}</h1>
      <p style="font-size:14px;color:#616a66;max-width:72ch;margin:0 0 14px">${lead}</p>
      <div style="display:flex;gap:8px;align-items:baseline;background:#edecfa;border:1px solid rgba(79,70,200,.25);border-radius:10px;padding:10px 14px;margin-bottom:22px;font-size:12.5px;color:#3d36a3">
        <strong>Reference only.</strong><span>Only --shadow is a named token; the overlay shadows are values in use. Not yet wired into the app theme.</span>
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

const level = (l) => `
  <div style="background:#fff;border:1px solid #e0e3dc;border-radius:12px;overflow:hidden">
    <div style="background:#f1f2ee;padding:30px 24px;display:flex;justify-content:center">
      <div style="width:200px;height:76px;background:#fff;border:1px solid #e0e3dc;border-radius:10px;box-shadow:${l.shadow}"></div>
    </div>
    <div style="padding:14px 16px 16px;border-top:1px solid #e0e3dc">
      <div style="font:700 13.5px ${SANS}">${l.label}</div>
      <div style="font:500 11px ${MONO};color:#8a938e;margin-top:2px">${l.tokenline}</div>
      <div style="font-size:12.5px;color:#616a66;margin-top:6px;line-height:1.45">${l.use}</div>
      <div style="font:500 10.5px ${MONO};color:#a2aba5;margin-top:7px;word-break:break-word">${l.value}</div>
    </div>
  </div>`;

const LEVELS = [
    { label: "Flat", tokenline: "default", use: "Panels, cards, and tables. The ledger stays flat; a hairline border defines the surface.", value: "border: 1px solid --line", shadow: "none" },
    { label: "Raised", tokenline: "--shadow", use: "Cards on hover and the trust-center preview. A gentle two-layer lift on interaction.", value: "0 1px 2px rgba(26,29,28,.05), 0 4px 14px rgba(26,29,28,.05)", shadow: "0 1px 2px rgba(26,29,28,.05), 0 4px 14px rgba(26,29,28,.05)" },
    { label: "Drawer", tokenline: "value in use", use: "The detail drawer sliding in from the right. Directional, cast toward the page it covers.", value: "-12px 0 40px rgba(26,29,28,.12)", shadow: "-12px 0 40px rgba(26,29,28,.12)" },
    { label: "Modal", tokenline: "value in use", use: "The command palette and modal surfaces, floating above everything.", value: "0 24px 60px rgba(26,29,28,.25)", shadow: "0 24px 60px rgba(26,29,28,.25)" },
];

const darkNote = `
  <div style="background:#1d2220;border:1px solid #2a302c;border-radius:12px;padding:16px 18px;display:flex;flex-wrap:wrap;gap:6px 14px;align-items:baseline">
    <span style="font:700 12.5px ${SANS};color:#e7eae6">On dark</span>
    <span style="font-size:12.5px;color:#a2aba5;max-width:70ch">--shadow deepens to black so it reads on the spruce ground; overlay shadows keep their values.</span>
    <span style="font:500 10.5px ${MONO};color:#7d8781;flex-basis:100%;margin-top:2px">--shadow: 0 1px 2px rgba(0,0,0,.4), 0 4px 14px rgba(0,0,0,.35)</span>
  </div>`;

export const Levels = {
    render: () =>
        page(
            "Elevation",
            "The audit ledger is mostly flat: surfaces are defined by a hairline border, not a shadow. Elevation is reserved for things that lift off the page - a card on hover, and the overlays. The higher the layer, the deeper and softer the cast.",
            section("Levels", "Flat by default; --shadow for a hover lift; deeper directional casts for the drawer and the palette.", `<div style="display:grid;grid-template-columns:repeat(auto-fill,minmax(240px,1fr));gap:18px;margin-bottom:18px">${LEVELS.map(level).join("")}</div>${darkNote}`),
        ),
};
