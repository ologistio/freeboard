// Compositions / Report. The board pack from the prototype: "Security and trust
// report to the board". A report is a lens over the ledger, not a hand-built
// deck - it leads with decisions requested, renders every trend as a delta
// against last quarter, keeps ambers honest, stamps figures with an as-of time,
// and the narrative is an agent draft the CISO owns. Reference only.

import { compPage } from "./_marks.js";

export default {
    title: "Compositions/Report",
    parameters: { layout: "fullscreen" },
};

const rm = (label, val, delta, dcls, sub) =>
    `<div class="fb-rm"><div class="rl">${label}</div><div class="rv">${val}<span class="fb-delta ${dcls}">${delta}</span></div><div class="rs">${sub}</div></div>`;

const spark = (heights) =>
    `<span class="fb-spark">${heights.map((v, i) => `<i class="${i === heights.length - 1 ? "now" : ""}" style="height:${v}%"></i>`).join("")}</span>`;

const trend = (heights, name, note, dcls, delta, pct) =>
    `<div class="fb-rrow">${spark(heights)}<span class="rname">${name}</span><span class="rnote">${note}</span><span class="fb-delta ${dcls}">${delta}</span><span class="fb-pct">${pct}</span></div>`;

const risk = (name, from, to, tgt, note, dcls, delta) =>
    `<div class="fb-rrow"><span class="rname" style="width:200px">${name}</span><span class="fb-risknums"><span class="from">${from}</span><span class="arrow">-></span><span class="to">${to}</span><span class="tgt">${tgt}</span></span><span class="rnote">${note}</span><span class="fb-delta ${dcls}">${delta}</span></div>`;

const decision = (title, body) =>
    `<div class="fb-decision"><div><div class="dtitle">${title}</div><div class="dbody">${body}</div></div><div class="dmeta"><span class="fb-tag fb-tag--warn">Decision</span><div class="fb-due" style="margin-top:6px">16 Jul</div></div></div>`;

const sec = (n, title, body, head) =>
    `<div class="fb-rsec"${n === "01" ? ' style="border-top:none;padding-top:22px"' : ""}><div class="fb-rsec-head"><div class="fb-eyebrow" style="margin:0">${n} / ${title}</div>${head || ""}</div>${body}</div>`;

const REPORT = `<div class="fb-pagehead">
    <div>
      <div class="fb-eyebrow">Platform / Reports</div>
      <h1>Security and trust report to the board</h1>
      <div class="fb-sub">Q2 FY26. Generated from the ledger; every figure resolves to a record.</div>
    </div>
    <div class="fb-headactions"><button type="button" class="fb-btn">Export PDF</button><button type="button" class="fb-btn fb-btn--brand">Lock and send</button></div>
  </div>
  <div class="fb-report"><div class="fb-rdoc">
    <div class="fb-fwmeta">
      <div class="m"><div class="ml">Period</div><div class="mv">01 Apr - 30 Jun 26</div></div>
      <div class="m"><div class="ml">Prepared by</div><div class="mv">M. Osei, CISO</div></div>
      <div class="m"><div class="ml">Audience</div><div class="mv">Board of directors</div></div>
      <div class="m"><div class="ml">Meeting</div><div class="mv">16 Jul</div></div>
      <div class="m"><div class="ml">Status</div><div class="mv">Draft, locks on send</div></div>
    </div>

    ${sec("01", "Summary",
        `<p class="fb-rp">The programme strengthened this quarter. SOC 2 readiness reached <b>96%</b> ahead of fieldwork opening 30 Jul, with 14 of 21 auditor requests accepted early. Both critical risks remain on treatment plans that are holding. ISO 27001 slipped two points on a degraded endpoint source; the fix is owned and dated 16 Jul.</p>
        <p class="fb-rp">Two decisions are requested below: the expiring acceptance of the payments key-person risk, and scope approval for ISO 42001. One near miss is disclosed in section 05. There is no budget ask this quarter.</p>`,
        `<span class="fb-stamp gen">AGENT DRAFT / EDITED M. OSEI</span>`)}

    ${sec("02", "Decisions requested",
        decision("Re-accept or fund R-019, key-person dependency in payments", "Acceptance expires 01 Dec. Re-accept for 12 months at inherent 9 with no mitigation, or fund cross-training at 0.2 FTE, reducing to 4 by Q2 FY27.") +
        decision("Approve ISO 42001 scope for the AI roadmap", "Adds 12 controls beyond ISO 27001 inheritance; certification targeted Q1 FY27. AI assurance appeared in 3 enterprise questionnaires this quarter."))}

    ${sec("03", "Posture and trend",
        `<div class="fb-rmetrics">
          ${rm("Monitoring pass rate", "99.2%", "+0.4", "good", "1,412 checks, hourly")}
          ${rm("Mean time to fix", "3.1d", "-1.5", "good", "SLA met 94%")}
          ${rm("Open criticals", "2", "0", "flat", "both on track")}
          ${rm("Questionnaire median", "2.1d", "-0.7", "good", "14 completed")}
        </div>
        <div style="margin-top:16px">
          ${trend([55, 65, 78, 96], "SOC 2", "fieldwork opens 30 Jul", "good", "+3", "96%")}
          ${trend([80, 85, 90, 88], "ISO 27001", "endpoint source degraded; fix by 16 Jul", "bad", "-2", "88%")}
          ${trend([78, 84, 88, 91], "GDPR", "ROPA current, 1 DPIA draft open", "good", "+1", "91%")}
          ${trend([40, 50, 58, 74], "HIPAA", "BAA programme closing the last 2 gaps", "good", "+9", "74%")}
        </div>`)}

    ${sec("04", "Risk",
        `<p class="fb-rp">24 open risks: 2 critical, 5 high (down one). Two new this quarter, both third-party, both surfaced by discovery rather than self-report.</p>
        <div style="margin-top:10px">
          ${risk("Loss of production data", "20", "9", "target 6", "1 linked control failing, fix in flight", "good", "-2")}
          ${risk("Vendor breach, processor", "16", "8", "target 8", "controls holding, at target", "flat", "0")}
        </div>
        <p class="fb-rp" style="margin-top:10px">Residual scores move with the share of linked controls actually holding; the derivation is inspectable on each risk.</p>`)}

    ${sec("05", "Incidents and near misses",
        `<p class="fb-rp">No reportable incidents this quarter. <b>One near miss:</b> a break-glass account was used on 06 Jul with no linked incident. Detected by the account inventory the same week; investigation open, closing with the Q3 access campaign.</p>`)}

    ${sec("06", "Third parties",
        `<p class="fb-rp">38 vendors, 3 critical-tier. Discovery surfaced 2 unmanaged apps; both triaged inside a week, and one review is overdue and gating its adoption. BAA gaps at 2 vendors keep HIPAA at amber; both closures are owned and dated. Reviews completed on time: 92%.</p>`)}

    ${sec("07", "Trust and commercial",
        `<p class="fb-rp">Security cleared 6 enterprise reviews this quarter. 14 questionnaires completed at a 2.1 day median against a 6.0 day manual baseline. The trust center granted 41 document requests under NDA with 3 pending inside SLA.</p>`)}

    ${sec("08", "Programme",
        `<p class="fb-rp">The annual policy acceptance cycle begins 15 Aug: 12 policies, all personnel, over two weeks. ISO 42001 scoping stands at 62% pending decision 02. No budget ask this quarter.</p>`)}

    <div class="fb-asof">ALL FIGURES AS OF 09 JUL, 09:00, AND RESOLVE TO LIVE RECORDS; STAMPS NAME THE SOURCE. THIS DRAFT LOCKS AND LANDS IN EVIDENCE WHEN SENT.</div>
  </div></div>`;

const SNIPPET = `<div class="fb-pagehead"> ...title + Export / Lock and send... </div>

<div class="fb-report"><div class="fb-rdoc">
  <div class="fb-fwmeta"> ...period, prepared by, audience, meeting, status... </div>

  <div class="fb-rsec" style="border-top:none;padding-top:22px">
    <div class="fb-rsec-head">
      <div class="fb-eyebrow">01 / Summary</div>
      <span class="fb-stamp gen">AGENT DRAFT / EDITED M. OSEI</span>
    </div>
    <p class="fb-rp">...</p>
  </div>

  <div class="fb-rsec">
    <div class="fb-rsec-head"><div class="fb-eyebrow">03 / Posture and trend</div></div>
    <div class="fb-rmetrics"><div class="fb-rm"><div class="rl">Monitoring pass rate</div><div class="rv">99.2%<span class="fb-delta good">+0.4</span></div><div class="rs">1,412 checks</div></div> ...</div>
    <div class="fb-rrow"><span class="fb-spark">...</span><span class="rname">SOC 2</span><span class="rnote">...</span><span class="fb-delta good">+3</span><span class="fb-pct">96%</span></div>
  </div>
  <!-- ...decisions, risk, incidents, third parties, trust, programme... -->

  <div class="fb-asof">All figures as of 09 Jul; a sent report locks a snapshot into Evidence.</div>
</div></div>`;

export const BoardPack = { render: () => compPage(REPORT, SNIPPET) };
