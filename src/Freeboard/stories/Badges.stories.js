// Components / Badges. Two labelling marks from the prototype: the tag (a
// soft-ground label for categories and facts) and the certification badge (a mono
// bordered mark for external surfaces). A label is a fact; a state is a status
// seal (see Brand/Data marks), not a tag. Reference only.

import { SANS, MONO, page, section, card, grid, cell, codeView } from "./_ui.js";

export default {
    title: "Components/Badges",
    parameters: { layout: "fullscreen" },
};

const CSS = `
  .fb-tag { display:inline-flex; align-items:center; font:500 11px ${SANS}; color:#616a66; background:#eceeea; border-radius:4px; padding:2px 8px; white-space:nowrap; }
  .fb-tag--brand { color:#3d36a3; background:#edecfa; }
  .fb-tag--ok { color:#1b7a4e; background:#e3f1e9; }
  .fb-tag--warn { color:#96690a; background:#f6eed6; }
  .fb-tag--fail { color:#b3372d; background:#f9e9e6; }
  .fb-badge { display:inline-flex; align-items:center; font:500 10px ${MONO}; letter-spacing:.06em; color:#616a66; border:1px solid #c9cec5; border-radius:4px; padding:2px 8px; white-space:nowrap; }
`;

const eyebrow = "Components / Badges";

export const Tags = {
    render: () =>
        page({
            eyebrow, title: "Badges", css: CSS,
            lead: "A tag is a soft-ground label for a fact: a category, a data class, a severity tier, a framework. It states what something is. For what state it is in - passing, failing, due - use a status seal, not a tag.",
            body:
                section("Variants", "Neutral by default; the four semantic grounds borrow the status palette to weight a fact without claiming it is a live state.", card(grid([
                    cell("Neutral", "Categories, data classes, plain facts.", `<span class="fb-tag">Operational</span>`),
                    cell("Brand", "Framework or primary association.", `<span class="fb-tag fb-tag--brand">SOC 2</span>`),
                    cell("Ok", "A positive fact as a label.", `<span class="fb-tag fb-tag--ok">Approved</span>`),
                    cell("Warn", "Attention or caution as a label.", `<span class="fb-tag fb-tag--warn">Review due</span>`),
                    cell("Fail", "A severe tier or blocking fact.", `<span class="fb-tag fb-tag--fail">Critical</span>`),
                ]))) +
                section("Markup", "One class plus an optional modifier.",
                    codeView(`<span class="fb-tag">Operational</span>\n<span class="fb-tag fb-tag--brand">SOC 2</span>\n<span class="fb-tag fb-tag--ok">Approved</span>\n<span class="fb-tag fb-tag--warn">Review due</span>\n<span class="fb-tag fb-tag--fail">Critical</span>`)) +
                section("In context", "How tags read together on a row - a mix of category, tier, and data facts.",
                    codeView(`<span class="fb-tag">Availability</span>\n<span class="fb-tag fb-tag--fail">Critical</span>\n<span class="fb-tag">PII</span>\n<span class="fb-tag fb-tag--brand">SOC 2</span>\n<span class="fb-tag fb-tag--warn">Sensitive</span>`)),
        }),
};

export const Certifications = {
    render: () =>
        page({
            eyebrow, title: "Certification badges", css: CSS,
            lead: "A mono, bordered mark for verification on external surfaces like the trust center. The monospace and hairline border read as a formal stamp rather than an interface label.",
            body:
                section("Badges", "Used for the frameworks a company holds. Set in mono.",
                    codeView(`<span class="fb-badge">SOC 2 TYPE II</span>\n<span class="fb-badge">ISO 27001</span>\n<span class="fb-badge">GDPR</span>\n<span class="fb-badge">HIPAA</span>`)),
        }),
};
