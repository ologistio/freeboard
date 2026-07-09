// Shared chrome for the component stories: the reference-only page frame, section
// and card helpers, and a TailwindUI-style copyable code view (live preview + a
// dark code panel with a Copy button). Not a story file, so Storybook's glob
// ignores it. Reference only: components are story-scoped, app.css is untouched.

export const SANS = "system-ui,-apple-system,'Segoe UI',sans-serif";
export const MONO = "ui-monospace,'SF Mono',Menlo,Consolas,monospace";

export const esc = (s) => s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");

// Inline handler: copy the adjacent <pre> text, then flash "Copied". Works from
// the Storybook preview iframe on localhost (a user gesture in a secure context).
const COPY = "navigator.clipboard&&navigator.clipboard.writeText(this.nextElementSibling.textContent);var b=this,t=b.textContent;b.textContent='Copied';setTimeout(function(){b.textContent=t},1200)";

export const codeView = (snippet, preview) => `
  <div style="border:1px solid #e0e3dc;border-radius:12px;overflow:hidden;background:#fff">
    <div style="padding:22px;display:flex;flex-wrap:wrap;gap:12px;align-items:center">${preview || snippet}</div>
    <div style="border-top:1px solid #e0e3dc;background:#1d2220;position:relative">
      <button type="button" aria-label="Copy code to clipboard" onclick="${COPY}" style="position:absolute;top:9px;right:9px;font:600 11px ${MONO};color:#e7eae6;background:#2a302c;border:1px solid #3d4540;border-radius:6px;padding:4px 10px;cursor:pointer">Copy</button>
      <pre style="margin:0;padding:14px 52px 14px 16px;overflow-x:auto;font:500 12px/1.6 ${MONO};color:#c9cec5">${esc(snippet)}</pre>
    </div>
  </div>`;

// Code panel on its own (copy button + escaped source), without a preview above.
// Useful for compositions, where the rendered page is already shown full-size.
export const codeBlock = (snippet) => `
  <div style="border:1px solid #3d4540;border-radius:12px;overflow:hidden;background:#1d2220;position:relative">
    <button type="button" aria-label="Copy code to clipboard" onclick="${COPY}" style="position:absolute;top:9px;right:9px;font:600 11px ${MONO};color:#e7eae6;background:#2a302c;border:1px solid #3d4540;border-radius:6px;padding:4px 10px;cursor:pointer">Copy</button>
    <pre style="margin:0;padding:14px 52px 14px 16px;overflow-x:auto;font:500 12px/1.6 ${MONO};color:#c9cec5">${esc(snippet)}</pre>
  </div>`;

export const example = (name, usage, snippet, preview) => `
  <div style="margin-bottom:16px">
    <div style="font:600 13.5px ${SANS};color:#1a1d1c">${name}</div>
    ${usage ? `<div style="font-size:12.5px;color:#616a66;margin:2px 0 9px;max-width:70ch">${usage}</div>` : `<div style="height:9px"></div>`}
    ${codeView(snippet, preview)}
  </div>`;

export const section = (name, desc, body) => `
  <section style="margin-bottom:26px">
    <h2 style="font-size:15px;font-weight:700;margin:0 0 3px">${name}</h2>
    ${desc ? `<p style="font-size:13px;color:#616a66;margin:0 0 16px;max-width:74ch">${desc}</p>` : ""}
    ${body}
  </section>`;

export const card = (inner) => `<div style="background:#fff;border:1px solid #e0e3dc;border-radius:12px;padding:20px">${inner}</div>`;

export const grid = (cells, min = 190) => `<div style="display:grid;grid-template-columns:repeat(auto-fill,minmax(${min}px,1fr));gap:22px">${cells.join("")}</div>`;

export const cell = (label, note, html) => `
  <div style="display:flex;flex-direction:column;gap:11px;align-items:flex-start">
    <div style="min-height:24px;display:flex;align-items:center">${html}</div>
    <div>
      <div style="font:600 12.5px ${SANS};color:#1a1d1c">${label}</div>
      ${note ? `<div style="font-size:12px;color:#616a66;margin-top:2px;max-width:32ch;line-height:1.4">${note}</div>` : ""}
    </div>
  </div>`;

export const page = ({ eyebrow, title, lead, css, body }) => `
  ${css ? `<style>${css}</style>` : ""}
  <div style="background:#f1f2ee;min-height:100vh;padding:26px 20px;font-family:${SANS};color:#1a1d1c">
    <div style="max-width:1040px;margin:0 auto">
      <div style="font:600 10px/1 ${MONO};letter-spacing:.14em;text-transform:uppercase;color:#8a938e;margin-bottom:8px">${eyebrow}</div>
      <h1 style="font-size:22px;font-weight:700;letter-spacing:-.015em;margin:0 0 6px">${title}</h1>
      <p style="font-size:14px;color:#616a66;max-width:72ch;margin:0 0 14px">${lead}</p>
      <div style="display:flex;gap:8px;align-items:baseline;background:#edecfa;border:1px solid rgba(79,70,200,.25);border-radius:10px;padding:10px 14px;margin-bottom:22px;font-size:12.5px;color:#3d36a3">
        <strong>Reference only.</strong><span>Prototype values, story-scoped. Not yet wired into the app theme.</span>
      </div>
      ${body}
    </div>
  </div>`;
