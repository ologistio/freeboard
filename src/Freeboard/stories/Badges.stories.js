// Components / Badges. Two labelling marks from the prototype: the tag (a
// soft-ground label for categories and facts) and the certification badge (a
// mono bordered mark for external surfaces). A label is a fact; a state is a
// status seal (see Brand/Data marks), not a tag. Reference only: story-scoped
// literal values, app.css untouched.

export default {
    title: "Components/Badges",
    parameters: { layout: "fullscreen" },
};

const MONO = "ui-monospace,'SF Mono',Menlo,Consolas,monospace";
const SANS = "system-ui,-apple-system,'Segoe UI',sans-serif";

const CSS = `
  .fb-tag { display:inline-flex; align-items:center; font:500 11px ${SANS}; color:#616a66; background:#eceeea; border-radius:4px; padding:2px 8px; white-space:nowrap; }
  .fb-tag--brand { color:#3d36a3; background:#edecfa; }
  .fb-tag--ok { color:#1b7a4e; background:#e3f1e9; }
  .fb-tag--warn { color:#96690a; background:#f6eed6; }
  .fb-tag--fail { color:#b3372d; background:#f9e9e6; }
  .fb-badge { display:inline-flex; align-items:center; font:500 10px ${MONO}; letter-spacing:.06em; color:#616a66; border:1px solid #c9cec5; border-radius:4px; padding:2px 8px; white-space:nowrap; }
`;

const page = (title, lead, body) => `
  <style>${CSS}</style>
  <div style="background:#f1f2ee;min-height:100%;padding:26px 20px;font-family:${SANS};color:#1a1d1c">
    <div style="max-width:1040px;margin:0 auto">
      <div style="font:600 10px/1 ${MONO};letter-spacing:.14em;text-transform:uppercase;color:#8a938e;margin-bottom:8px">Components / Badges</div>
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

const cell = (label, note, html) => `
  <div style="display:flex;flex-direction:column;gap:11px;align-items:flex-start">
    <div style="min-height:24px;display:flex;align-items:center">${html}</div>
    <div>
      <div style="font:600 12.5px ${SANS};color:#1a1d1c">${label}</div>
      ${note ? `<div style="font-size:12px;color:#616a66;margin-top:2px;max-width:32ch;line-height:1.4">${note}</div>` : ""}
    </div>
  </div>`;

const grid = (cells) => `<div style="display:grid;grid-template-columns:repeat(auto-fill,minmax(190px,1fr));gap:22px">${cells.join("")}</div>`;

export const Tags = {
    render: () =>
        page(
            "Badges",
            "A tag is a soft-ground label for a fact: a category, a data class, a severity tier, a framework. It states what something is. For what state it is in - passing, failing, due - use a status seal, not a tag.",
            section("Variants", "Neutral by default; the four semantic grounds borrow the status palette to weight a fact without claiming it is a live state.", card(grid([
                cell("Neutral", "Categories, data classes, and plain facts.", `<span class="fb-tag">Operational</span>`),
                cell("Brand", "Framework or primary association.", `<span class="fb-tag fb-tag--brand">SOC 2</span>`),
                cell("Ok", "A positive fact as a label.", `<span class="fb-tag fb-tag--ok">Approved</span>`),
                cell("Warn", "Attention or caution as a label.", `<span class="fb-tag fb-tag--warn">Review due</span>`),
                cell("Fail", "A severe tier or blocking fact.", `<span class="fb-tag fb-tag--fail">Critical</span>`),
            ]))) +
            section("In context", "How tags read together on a row - a mix of category, tier, and data facts.", card(`<div style="display:flex;gap:7px;flex-wrap:wrap;align-items:center">
                <span class="fb-tag">Availability</span>
                <span class="fb-tag fb-tag--fail">Critical</span>
                <span class="fb-tag">PII</span>
                <span class="fb-tag fb-tag--brand">SOC 2</span>
                <span class="fb-tag fb-tag--warn">Sensitive</span>
                <span class="fb-tag">Excluded</span>
            </div>`)),
        ),
};

export const Certifications = {
    render: () =>
        page(
            "Certification badges",
            "A mono, bordered mark for verification on external surfaces like the trust center. The monospace and hairline border read as a formal stamp rather than an interface label.",
            section("Badges", "Used for the frameworks a company holds. Sentence of record, set in mono.", card(`<div style="display:flex;gap:8px;flex-wrap:wrap">
                <span class="fb-badge">SOC 2 TYPE II</span>
                <span class="fb-badge">ISO 27001</span>
                <span class="fb-badge">GDPR</span>
                <span class="fb-badge">HIPAA</span>
            </div>`)),
        ),
};
