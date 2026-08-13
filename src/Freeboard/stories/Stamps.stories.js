// Components / Stamps. The provenance stamp: a dashed mono mark naming where a
// value came from and how old it is. Two provenance variants (hand-entered and
// generated) selected by the provenance flag, and three tones selected by the
// tone. Each emitted class is reached by exactly one of the two. Reference only.

import { MONO, page, section, card, grid, cell, codeView } from "./_ui.js";

export default {
    title: "Components/Stamps",
    parameters: { layout: "fullscreen" },
};

const CSS = `
  .fb-stamp { display:inline-flex; align-items:center; font:500 10px ${MONO}; letter-spacing:.06em; text-transform:uppercase; color:#464b48; border:1px dashed #c9cec5; border-radius:4px; padding:1px 6px; white-space:nowrap; }
  .fb-stamp.manual, .fb-stamp.warn { color:#875e08; border-color:color-mix(in srgb, #96690a 40%, transparent); }
  .fb-stamp.fail { color:#932b22; border-color:color-mix(in srgb, #b3372d 40%, transparent); }
  .fb-stamp.gen { color:#3d36a3; border-color:color-mix(in srgb, #433bb6 40%, transparent); }
  .fb-stamp__age { opacity:.75; }
`;

const eyebrow = "Components / Stamps";

const stamp = (variant, label, age) =>
    `<span class="fb-stamp${variant ? " " + variant : ""}">${label}<span class="fb-stamp__age"> ${age}</span></span>`;

export const Provenance = {
    render: () =>
        page({
            eyebrow, title: "Provenance stamps", css: CSS,
            lead: "A stamp says where a value came from and how old it is. It never states a verdict: a value's state is a status seal. Every stamp carries a date - a provenance with no date is the claim the mark exists to prevent.",
            body:
                section("Provenance variants", "Selected by the provenance flag. Generated is the default; a hand-entered value is stamped MANUAL and dated.", card(grid([
                    cell("Generated", "Freeboard collected the value.", stamp("gen", "AWS Config", "2h ago")),
                    cell("Manual", "A person supplied the content.", stamp("manual", "MANUAL", "Mar 3")),
                ]))) +
                section("Tones", "Selected by the tone, which replaces the provenance variant rather than adding to it. There is no pass tone and no brand tone: a stamp reports origin and age, not a Freeboard verdict, so green would claim a verdict it does not hold.", card(grid([
                    cell("Neutral", "The untinted base. A fact on file Freeboard has not verified.", stamp(null, "SOC 2", "expires Mar 27")),
                    cell("Warn", "About to lapse. Shares one rule with MANUAL: the palette holds one amber.", stamp("warn", "ISO 27001", "expires in 5 days")),
                    cell("Fail", "Already lapsed. Red is earned because an expiry that has passed is a fact, not a judgement.", stamp("fail", "PCI DSS", "expired 3 days ago")),
                ]))) +
                section("Markup", "One class plus one variant, and the age in its own span.",
                    codeView(`<span class="fb-stamp gen">AWS Config<span class="fb-stamp__age"> 2h ago</span></span>\n<span class="fb-stamp manual">MANUAL<span class="fb-stamp__age"> Mar 3</span></span>\n<span class="fb-stamp">SOC 2<span class="fb-stamp__age"> expires Mar 27</span></span>\n<span class="fb-stamp warn">ISO 27001<span class="fb-stamp__age"> expires in 5 days</span></span>\n<span class="fb-stamp fail">PCI DSS<span class="fb-stamp__age"> expired 3 days ago</span></span>`)),
        }),
};
