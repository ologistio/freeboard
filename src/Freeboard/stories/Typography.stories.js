// Brand typography reference. The type system from the product prototype
// ("audit ledger" direction): two families with a strict division of labour.
// Reference only: not wired into the Tailwind theme. Specimens load the two
// faces from Google Fonts for preview; the app will self-host them on adoption.

export default {
    title: "Brand/Typography",
    parameters: { layout: "fullscreen" },
};

const SANS = "'Schibsted Grotesk',-apple-system,'Segoe UI',sans-serif";
const MONO = "'IBM Plex Mono',ui-monospace,'SF Mono',Menlo,monospace";
const FONTS = "https://fonts.googleapis.com/css2?family=Schibsted+Grotesk:wght@400;500;600;700&family=IBM+Plex+Mono:wght@400;500;600&display=swap";

const page = (title, lead, body) => `
  <style>@import url('${FONTS}');</style>
  <div style="background:#f1f2ee;min-height:100%;padding:26px 20px;font-family:${SANS};color:#1a1d1c">
    <div style="max-width:1040px;margin:0 auto">
      <div style="font:600 10px/1 ${MONO};letter-spacing:.14em;text-transform:uppercase;color:#8a938e;margin-bottom:8px">Brand / Typography</div>
      <h1 style="font-size:22px;font-weight:700;letter-spacing:-.015em;margin:0 0 6px">${title}</h1>
      <p style="font-size:14px;color:#616a66;max-width:72ch;margin:0 0 14px">${lead}</p>
      <div style="display:flex;gap:8px;align-items:baseline;background:#edecfa;border:1px solid rgba(79,70,200,.25);border-radius:10px;padding:10px 14px;margin-bottom:22px;font-size:12.5px;color:#3d36a3">
        <strong>Reference only.</strong><span>Target type system from the product prototype. Specimens load from Google Fonts for preview; not yet wired into the app theme.</span>
      </div>
      ${body}
    </div>
  </div>`;

const section = (name, desc, body) => `
  <section style="margin-bottom:30px">
    <h2 style="font-size:15px;font-weight:700;margin:0 0 3px">${name}</h2>
    <p style="font-size:13px;color:#616a66;margin:0 0 14px;max-width:74ch">${desc}</p>
    ${body}
  </section>`;

// ---- typefaces ----
const typeface = (t) => `
  <figure style="margin:0;border:1px solid #e0e3dc;border-radius:12px;overflow:hidden;background:#fff;box-shadow:0 1px 2px rgba(26,29,28,.05)">
    <div style="padding:22px 22px 8px;font-family:${t.stack}">
      <div style="font-size:64px;font-weight:700;line-height:1;letter-spacing:-.02em;color:#1a1d1c">Aa</div>
      <div style="font-size:16px;color:#1a1d1c;margin-top:16px">ABCDEFGHIJKLMNOPQRSTUVWXYZ</div>
      <div style="font-size:16px;color:#616a66">abcdefghijklmnopqrstuvwxyz</div>
      <div style="font-size:16px;color:#616a66">0123456789 &amp; % / . , : -</div>
    </div>
    <div style="padding:14px 22px 4px;border-top:1px solid #e0e3dc">
      <div style="font:700 15px ${SANS};color:#1a1d1c">${t.name}</div>
      <div style="font:400 13px ${SANS};color:#616a66;margin-top:2px;line-height:1.45">${t.role}</div>
    </div>
    <div style="display:flex;flex-wrap:wrap;gap:18px;padding:14px 22px 20px">
      ${t.weights.map((w) => `<div style="text-align:center"><div style="font-family:${t.stack};font-weight:${w.v};font-size:24px;color:#1a1d1c">Ag</div><div style="font:500 10px ${MONO};color:#8a938e;margin-top:4px;letter-spacing:.04em">${w.v} ${w.n}</div></div>`).join("")}
    </div>
  </figure>`;

const TYPEFACES = [
    {
        name: "Schibsted Grotesk",
        stack: SANS,
        role: "Sans. Everything a person writes or reads: display figures, headings, body, and UI labels. Sentence case throughout.",
        weights: [{ v: 400, n: "Regular" }, { v: 500, n: "Medium" }, { v: 600, n: "Semibold" }, { v: 700, n: "Bold" }],
    },
    {
        name: "IBM Plex Mono",
        stack: MONO,
        role: "Mono. Anything the system emits or that gives structure: provenance stamps, data values, eyebrows, table headers, object kinds. Uppercase with tracking is reserved for these.",
        weights: [{ v: 400, n: "Regular" }, { v: 500, n: "Medium" }, { v: 600, n: "Semibold" }],
    },
];

// ---- scale ----
const row = (r) => `
  <div style="padding:15px 0;border-bottom:1px solid #e0e3dc">
    <div style="${r.style}">${r.sample}</div>
    <div style="margin-top:9px">
      <span style="font:600 13px ${SANS};color:#1a1d1c">${r.name}</span>
      <span style="font:400 12.5px ${SANS};color:#616a66"> - ${r.use}</span>
    </div>
    <div style="font:500 11px ${MONO};color:#8a938e;letter-spacing:.02em;margin-top:3px">${r.family} / ${r.specs}</div>
  </div>`;

const SANS_SCALE = [
    { name: "Display", use: "framework scores, key metrics", family: "Sans", specs: "24px / Bold 700 / -0.02em", sample: "91% ready", style: `font:700 24px/1.1 ${SANS};letter-spacing:-.02em;color:#1a1d1c` },
    { name: "Page title", use: "the h1 on every page", family: "Sans", specs: "21px / Bold 700 / -0.015em", sample: "Frameworks", style: `font:700 21px/1.2 ${SANS};letter-spacing:-.015em;color:#1a1d1c` },
    { name: "Heading", use: "drawer and card titles", family: "Sans", specs: "16px / Bold 700", sample: "Access requires MFA", style: `font:700 16px/1.3 ${SANS};color:#1a1d1c` },
    { name: "Subhead", use: "panel headings", family: "Sans", specs: "14.5px / Semibold 600", sample: "Needs attention", style: `font:600 14.5px/1.3 ${SANS};color:#1a1d1c` },
    { name: "Body", use: "default reading text", family: "Sans", specs: "14px / Regular 400 / 1.5 line", sample: "Evidence and the checks it satisfies are the primary objects; frameworks are lenses over them.", style: `font:400 14px/1.5 ${SANS};color:#1a1d1c;max-width:66ch` },
    { name: "Body strong", use: "names and emphasis in rows", family: "Sans", specs: "14px / Semibold 600", sample: "MFA enforced for all admin accounts", style: `font:600 14px/1.4 ${SANS};color:#1a1d1c` },
    { name: "Secondary", use: "table cells, subtitles", family: "Sans", specs: "13px / Regular 400 / muted", sample: "Also satisfies ISO 27001 and HIPAA", style: `font:400 13px/1.45 ${SANS};color:#616a66` },
    { name: "Caption", use: "captions and hints", family: "Sans", specs: "11.5px / Medium 500 / faint", sample: "Onboarding day 5 of 14", style: `font:500 11.5px/1.4 ${SANS};color:#8a938e` },
];

const MONO_SCALE = [
    { name: "Eyebrow", use: "group labels above headings", family: "Mono", specs: "10px / Semibold 600 / +0.14em / uppercase", sample: "COMPLY", style: `font:600 10px ${MONO};letter-spacing:.14em;text-transform:uppercase;color:#8a938e` },
    { name: "Column header", use: "table column headers", family: "Mono", specs: "9.5px / Semibold 600 / +0.12em / uppercase", sample: "PROVENANCE", style: `font:600 9.5px ${MONO};letter-spacing:.12em;text-transform:uppercase;color:#8a938e` },
    { name: "Object kind", use: "the kind tag in queues", family: "Mono", specs: "9.5px / Semibold 600 / +0.1em / uppercase", sample: "CONTROL", style: `font:600 9.5px ${MONO};letter-spacing:.1em;text-transform:uppercase;color:#8a938e` },
    { name: "Provenance stamp", use: "source and freshness of automated values", family: "Mono", specs: "10px / Medium 500 / +0.06em / uppercase / dashed", sample: "AUTO / 22M", style: `display:inline-block;font:500 10px ${MONO};letter-spacing:.06em;text-transform:uppercase;color:#616a66;border:1px dashed #c9cec5;border-radius:4px;padding:2px 7px` },
    { name: "Data", use: "numerals and scores, aligned", family: "Mono", specs: "12px / Medium 500 / tabular", sample: "20 -> 9   target 6", style: `font:500 12px ${MONO};color:#616a66` },
];

export const Typefaces = {
    render: () =>
        page(
            "Typefaces",
            "Two families, one rule. Schibsted Grotesk carries human content; IBM Plex Mono carries anything the system emits - stamps, data, and structural labels - so the mono itself signals machine provenance.",
            section(
                "Families",
                "Schibsted Grotesk for prose and headings, IBM Plex Mono for the audit-ledger marks. These are the only two faces in the product.",
                `<div style="display:grid;grid-template-columns:repeat(auto-fit,minmax(320px,1fr));gap:16px">${TYPEFACES.map(typeface).join("")}</div>`,
            ),
        ),
};

export const Scale = {
    render: () =>
        page(
            "Scale",
            "The roles in use, rendered at their real size, weight, and tracking. Large display and titles carry tight negative tracking; small uppercase mono carries positive tracking.",
            section("Sans scale", "Schibsted Grotesk, from display down to caption.", `<div style="background:#fff;border:1px solid #e0e3dc;border-radius:12px;padding:4px 20px 6px;box-shadow:0 1px 2px rgba(26,29,28,.05)">${SANS_SCALE.map(row).join("")}</div>`) +
            section("Mono scale", "IBM Plex Mono for eyebrows, headers, kinds, provenance stamps, and data.", `<div style="background:#fff;border:1px solid #e0e3dc;border-radius:12px;padding:4px 20px 6px;box-shadow:0 1px 2px rgba(26,29,28,.05)">${MONO_SCALE.map(row).join("")}</div>`),
        ),
};
