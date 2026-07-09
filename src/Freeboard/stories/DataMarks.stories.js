// Brand signature marks: how the product presents data. Two devices from the
// prototype - square status seals and dashed provenance stamps. This page shows
// the marks and the grammar; each gets its own interactive component story later
// under Components. Reference only: not wired into the Tailwind theme.

export default {
    title: "Brand/Data marks",
    parameters: { layout: "fullscreen" },
};

const MONO = "ui-monospace,'SF Mono',Menlo,Consolas,monospace";
const SANS = "system-ui,-apple-system,'Segoe UI',sans-serif";

const C = { ok: "#1b7a4e", fail: "#b3372d", warn: "#96690a", info: "#2a6db0", muted: "#616a66", ink: "#1a1d1c", faint: "#8a938e" };

const page = (title, lead, body) => `
  <div style="background:#f1f2ee;min-height:100vh;padding:26px 20px;font-family:${SANS};color:${C.ink}">
    <div style="max-width:1040px;margin:0 auto">
      <div style="font:600 10px/1 ${MONO};letter-spacing:.14em;text-transform:uppercase;color:${C.faint};margin-bottom:8px">Brand / Data marks</div>
      <h1 style="font-size:22px;font-weight:700;letter-spacing:-.015em;margin:0 0 6px">${title}</h1>
      <p style="font-size:14px;color:${C.muted};max-width:72ch;margin:0 0 14px">${lead}</p>
      ${body}
    </div>
  </div>`;

const section = (name, desc, body) => `
  <section style="margin-bottom:26px">
    <h2 style="font-size:15px;font-weight:700;margin:0 0 3px">${name}</h2>
    <p style="font-size:13px;color:${C.muted};margin:0 0 16px;max-width:74ch">${desc}</p>
    ${body}
  </section>`;

const card = (inner) => `<div style="background:#fff;border:1px solid #e0e3dc;border-radius:12px;padding:18px 20px">${inner}</div>`;

// ---- status seals ----
const SEAL_BASE = "width:10px;height:10px;border-radius:2px;display:inline-block;flex:none";
const seal = (type) => {
    const s = {
        ok: `background:${C.ok}`,
        fail: `background:${C.fail};box-shadow:0 0 0 2px #f9e9e6`,
        warn: `background:${C.warn}`,
        info: `background:${C.info}`,
        off: "background:transparent;border:1.5px solid #c9cec5",
    }[type];
    return `<span style="${SEAL_BASE};${s}"></span>`;
};
const statusPair = (word, color, type) =>
    `<span style="display:inline-flex;align-items:center;gap:8px;font:600 13px ${SANS};color:${color}">${seal(type)}${word}</span>`;

const SEAL_GLYPHS = [
    { type: "ok", label: "ok", use: "Holds. Passing, ready." },
    { type: "fail", label: "fail", use: "Broken. Failing, overdue. The soft ring pulls the eye." },
    { type: "warn", label: "warn", use: "Needs attention soon. Due, drifting, degraded." },
    { type: "info", label: "info", use: "In motion. In progress, informational." },
    { type: "off", label: "off", use: "Inert. Snoozed, draft, waiting, out of scope." },
];

const VOCAB = [
    { w: "Ready", c: C.ok, s: "ok" }, { w: "Passing", c: C.ok, s: "ok" },
    { w: "Failing", c: C.fail, s: "fail" }, { w: "Overdue", c: C.fail, s: "fail" },
    { w: "Due soon", c: C.warn, s: "warn" }, { w: "Drifting", c: C.warn, s: "warn" }, { w: "Degraded", c: C.warn, s: "warn" },
    { w: "In progress", c: C.info, s: "info" },
    { w: "Waiting", c: C.muted, s: "off" }, { w: "Snoozed", c: C.muted, s: "off" }, { w: "Draft", c: C.muted, s: "off" }, { w: "Out of scope", c: C.muted, s: "off" },
];

const glyphRow = (g, last) => `
  <div style="display:flex;align-items:center;gap:12px;padding:9px 0;${last ? "" : "border-bottom:1px solid #e0e3dc"}">
    <span style="width:30px;display:flex;justify-content:center">${seal(g.type)}</span>
    <span style="font:600 12.5px ${MONO};color:${C.ink};width:44px">${g.label}</span>
    <span style="font-size:12.5px;color:${C.muted}">${g.use}</span>
  </div>`;

// ---- provenance stamps ----
const stamp = (text, variant) => {
    const v = {
        auto: `color:${C.muted};border-color:#c9cec5`,
        manual: `color:${C.warn};border-color:rgba(150,105,10,.4)`,
        gen: "color:#3d36a3;border-color:rgba(79,70,200,.4)",
    }[variant];
    return `<span style="display:inline-block;font:500 10px ${MONO};letter-spacing:.06em;text-transform:uppercase;border:1px dashed;border-radius:4px;padding:2px 7px;white-space:nowrap;${v}">${text}</span>`;
};

const TAXONOMY = [
    { variant: "auto", ex: "AUTO / 22M", name: "AUTO", desc: "Collected automatically from a connected source. Carries the source name (the collector or integration) and its age.", when: "The default for anything a machine can observe." },
    { variant: "manual", ex: "MANUAL / 02 JUL", name: "MANUAL", desc: "Provided by a person. Carries who uploaded it and when. Manual is a provenance, not the absence of one.", when: "Documents and attestations a system cannot collect." },
    { variant: "gen", ex: "AGENT / 1H", name: "GENERATED", desc: "Drafted by the assistant from live data. Held for human approval before it counts as evidence.", when: "System descriptions, first-draft policies, questionnaire answers." },
];

const taxoRow = (t, last) => `
  <div style="display:flex;align-items:flex-start;gap:16px;padding:14px 0;${last ? "" : "border-bottom:1px solid #e0e3dc"}">
    <span style="width:130px;flex:none;padding-top:1px">${stamp(t.ex, t.variant)}</span>
    <div>
      <div style="font:700 12.5px ${SANS};color:${C.ink}">${t.name}</div>
      <div style="font-size:12.5px;color:${C.muted};margin-top:3px;line-height:1.45">${t.desc}</div>
      <div style="font-size:12px;color:${C.faint};margin-top:4px">${t.when}</div>
    </div>
  </div>`;

const freshChip = (text, label, stale) => `
  <div style="display:flex;flex-direction:column;gap:7px;align-items:flex-start">
    ${stamp(text, "auto")}
    <span style="font:500 10.5px ${MONO};color:${stale ? C.warn : C.faint};letter-spacing:.04em">${label}</span>
  </div>`;

const note = (text) => `<p style="font-size:12.5px;color:${C.muted};margin:14px 0 0;max-width:74ch">${text}</p>`;

export const StatusSeals = {
    render: () =>
        page(
            "Status seals",
            "A datum's state is a square seal plus a word, never colour alone. The square reads as a mark, not a button, and the word carries the meaning for anyone who cannot rely on colour.",
            section("Seal glyphs", "Five seals cover every state. Red is reserved for failing and overdue; amber for due-soon and degraded; the outline seal for inert states.", card(SEAL_GLYPHS.map((g, i) => glyphRow(g, i === SEAL_GLYPHS.length - 1)).join(""))) +
            section("Status vocabulary", "One vocabulary product-wide. Each status pairs its seal with its word in the status colour.", card(`<div style="display:grid;grid-template-columns:repeat(auto-fill,minmax(150px,1fr));gap:14px 18px">${VOCAB.map((v) => statusPair(v.w, v.c, v.s)).join("")}</div>`)),
        ),
};

export const ProvenanceStamps = {
    render: () =>
        page(
            "Provenance stamps",
            "Every automated value shows where it came from and how fresh it is, in a dashed mono stamp. The dashed border and monospace signal machine provenance at a glance; the stamp reads identically to the owner and the auditor.",
            section("Taxonomy", "Three provenances, three colours. Neutral for automated, amber for manual, violet for generated-and-unapproved.", card(TAXONOMY.map((t, i) => taxoRow(t, i === TAXONOMY.length - 1)).join(""))) +
            section("Freshness", "The stamp carries an age. Thresholds run fresh, aging, stale - and a stale source degrades every check that depends on it, so the object's seal turns amber while the stamp names the aging source.", card(`<div style="display:flex;gap:26px;flex-wrap:wrap">${freshChip("AUTO / 22M", "fresh", false)}${freshChip("AUTO / 3D", "aging", false)}${freshChip("AUTO / 14D", "stale -> degrades", true)}</div>`)) +
            section("Reading a value", "The two marks combine to present one datum: whether it holds (seal and word), what it is (name), and where it came from (stamp).", card(`<div style="display:flex;align-items:center;gap:14px;flex-wrap:wrap"><span style="display:inline-flex;align-items:center;gap:8px;font:600 13px ${SANS};color:${C.fail}">${seal("fail")}Failing</span><span style="font:600 13.5px ${SANS};color:${C.ink};flex:1;min-width:200px">MFA enforced for all admin accounts</span>${stamp("AUTO / 22M", "auto")}</div>${note("State on the left, name in the middle, provenance on the right - the same order in every row, table, and drawer.")}`)),
        ),
};
