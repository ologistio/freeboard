// Compositions / Home dashboard. The flagship page inside the app shell: a
// posture stat row per group, the exceptions-first shared queue (grouped by
// urgency, with a hot overdue group and area filter chips that prune empty group
// headers), framework posture, and a change feed that doubles as an audit trail.
// Reference only.

import { compPage, appFrame, railNav, shellTopbar } from "./_marks.js";

export default {
    title: "Compositions/Home dashboard",
    parameters: { layout: "fullscreen" },
};

// Queue filter: activate the chip, show rows for its area, and hide group headers
// whose rows all went away (L3 - empty groups disappear).
const QFILTER = "var chip=this,row=chip.parentNode,cs=row.querySelectorAll('.fb-chip');for(var i=0;i<cs.length;i++)cs[i].classList.remove('on');chip.classList.add('on');var wrap=chip.closest('.fb-panel'),area=chip.getAttribute('data-area'),rows=wrap.querySelectorAll('tr.fb-row');for(var r=0;r<rows.length;r++){var s=area==='all'||rows[r].getAttribute('data-area')===area;rows[r].style.display=s?'':'none';}var gs=wrap.querySelectorAll('tr.fb-grp');for(var g=0;g<gs.length;g++){var any=false,n=gs[g].nextElementSibling;while(n&&!n.classList.contains('fb-grp')){if(n.style.display!=='none'){any=true;break;}n=n.nextElementSibling;}gs[g].style.display=any?'':'none';}";

const qchip = (area, label, count) =>
    `<button type="button" class="fb-chip${area === "all" ? " on" : ""}" data-area="${area}" onclick="${QFILTER}">${label}<span class="n">${count}</span></button>`;

const qrow = (area, kind, name, sub, stamp, due, dueCls, action) =>
    `<tr class="fb-row" data-area="${area}"><td><span class="fb-kind">${kind}</span></td><td><div class="fb-tdname">${name}</div><div class="fb-tdsub">${sub}</div></td><td>${stamp}</td><td><span class="fb-due ${dueCls}">${due}</span></td><td><button type="button" class="fb-tbtn">${action}</button></td></tr>`;

const fwrow = (name, pct, warn) =>
    `<div class="fb-fwrow"><span class="fb-fwname">${name}</span><div class="fb-bar"><i class="${warn ? "warn" : ""}" style="width:${pct}%"></i></div><span class="fb-pct">${pct}%</span></div>`;

const QUEUE = `<div class="fb-panel">
    <div class="fb-panel__head">
      <h2>Needs attention</h2>
      <div class="fb-chiprow">
        ${qchip("all", "All", "9")}${qchip("comply", "Comply", "5")}${qchip("risk", "Risk", "2")}${qchip("trust", "Trust", "1")}${qchip("people", "People", "1")}
      </div>
    </div>
    <table class="fb-tbl"><tbody>
      <tr class="fb-grp hot"><td colspan="5">Overdue / 3</td></tr>
      ${qrow("comply", "Check", "MFA enforced for all admin accounts", "Fails CC6.1 / SOC 2, ISO 27001", `<span class="fb-stamp">AUTO / 22M</span>`, "3d overdue", "over", "Fix")}
      ${qrow("comply", "Policy", "Access control policy awaiting approval", "Approver: M. Osei", `<span class="fb-stamp manual">MANUAL</span>`, "1d overdue", "over", "Approve")}
      ${qrow("risk", "Vendor", "Analytics security review incomplete", "Detected via SSO, tier High proposed", `<span class="fb-stamp">AUTO / 2d</span>`, "2d overdue", "over", "Review")}
      <tr class="fb-grp"><td colspan="5">Due this week / 4</td></tr>
      ${qrow("comply", "Check", "Storage blocks public access", "2 resources failing / CC6.6", `<span class="fb-stamp">AUTO / 12M</span>`, "SLA Fri", "soon", "Fix")}
      ${qrow("trust", "Qstn", "Northwind DDQ, 12 answers drafted", "Agent drafts held for approval", `<span class="fb-stamp gen">AGENT / 1H</span>`, "Due Fri", "soon", "Review")}
      ${qrow("people", "Person", "2 new starters missing security training", "Onboarding day 5 of 14", `<span class="fb-stamp">AUTO / 4h</span>`, "Due 13 Jul", "soon", "Nudge")}
      ${qrow("comply", "Doc", "Pentest report renewal", "Expires in 9 days / satisfies 2 checks", `<span class="fb-stamp manual">MANUAL</span>`, "Due 18 Jul", "soon", "Upload")}
      <tr class="fb-grp"><td colspan="5">Upcoming / 2</td></tr>
      ${qrow("comply", "Audit", "SOC 2 audit room opens", "Request list import ready", `<span class="fb-stamp manual">SCHEDULED</span>`, "30 Jul", "", "Prepare")}
      ${qrow("risk", "Review", "Q3 access review campaign", "148 accounts across 2 apps", `<span class="fb-stamp manual">SCHEDULED</span>`, "1 Aug", "", "Plan")}
    </tbody></table>
    <div class="fb-tfoot">One task model everywhere. The same items, scoped to you, appear in My work.</div>
  </div>`;

const SIDE = `<div>
    <div class="fb-panel">
      <div class="fb-panel__head"><h2>Framework posture</h2><span class="fb-panel__meta">shared controls</span></div>
      <div class="fb-panel__body" style="padding-top:6px">
        ${fwrow("SOC 2", 96, false)}${fwrow("ISO 27001", 88, false)}${fwrow("GDPR", 91, false)}${fwrow("HIPAA", 74, true)}${fwrow("ISO 42001", 62, true)}
      </div>
    </div>
    <div class="fb-panel" style="margin-top:16px">
      <div class="fb-panel__head"><h2>Recent changes</h2><span class="fb-panel__meta">audit trail</span></div>
      <div class="fb-panel__body" style="padding-top:6px">
        <ul class="fb-feed">
          <li><span class="t">09:12</span><span><b>Check recovered.</b> Storage versioning enabled on data-eu. Evidence attached automatically.</span></li>
          <li><span class="t">08:40</span><span><b>New vendor detected.</b> Screen recorder, via SSO. Awaiting triage.</span></li>
          <li><span class="t">Yest</span><span><b>Policy approved.</b> Incident response v3, by M. Osei. Acceptance cycle queued.</span></li>
          <li><span class="t">2d</span><span><b>Integration degraded.</b> Endpoint token expires in 7 days. 14 device checks depend on it.</span></li>
        </ul>
      </div>
    </div>
  </div>`;

const HOME = `<div class="fb-pagehead">
    <div>
      <div class="fb-eyebrow">Thursday 9 July / Your org</div>
      <h1>Program is 91% ready. 6 items need action this week.</h1>
    </div>
    <div class="fb-headactions"><button type="button" class="fb-btn">Report center</button><button type="button" class="fb-btn fb-btn--brand">Ask Freeboard</button></div>
  </div>
  <div class="fb-posture">
    <div class="fb-stat"><div class="fb-stat__label">Comply</div><div class="fb-stat__main">96%<small>SOC 2</small></div><div class="fb-stat__sub">ISO 27001 88% / HIPAA 74%</div><div class="fb-bar"><i style="width:96%"></i></div></div>
    <div class="fb-stat"><div class="fb-stat__label">Monitoring</div><div class="fb-stat__main">1,412<small>checks</small></div><div class="fb-stat__sub"><b>12 failing</b> / 3 inside SLA</div><div class="fb-bar"><i style="width:99%"></i></div></div>
    <div class="fb-stat"><div class="fb-stat__label">Risk</div><div class="fb-stat__main">24<small>open</small></div><div class="fb-stat__sub"><b>2 critical</b> / 4 vendor reviews due</div><div class="fb-bar"><i class="warn" style="width:62%"></i></div></div>
    <div class="fb-stat"><div class="fb-stat__label">Trust</div><div class="fb-stat__main">3<small>requests</small></div><div class="fb-stat__sub">2 questionnaires in progress</div><div class="fb-bar"><i style="width:80%"></i></div></div>
  </div>
  <div class="fb-cols">${QUEUE}${SIDE}</div>`;

const SNIPPET = `<!-- Inside <main class="fb-main"> of the app shell -->
<div class="fb-pagehead"> ...date eyebrow, headline, actions... </div>

<div class="fb-posture">
  <div class="fb-stat">
    <div class="fb-stat__label">Comply</div>
    <div class="fb-stat__main">96%<small>SOC 2</small></div>
    <div class="fb-stat__sub">ISO 27001 88% / HIPAA 74%</div>
    <div class="fb-bar"><i style="width:96%"></i></div>
  </div>
  <!-- ...Monitoring, Risk, Trust... -->
</div>

<div class="fb-cols">
  <div class="fb-panel">
    <div class="fb-panel__head">
      <h2>Needs attention</h2>
      <div class="fb-chiprow"><button type="button" class="fb-chip on" data-area="all">All<span class="n">9</span></button> ...</div>
    </div>
    <table class="fb-tbl"><tbody>
      <tr class="fb-grp hot"><td colspan="5">Overdue / 3</td></tr>
      <tr class="fb-row" data-area="comply"> ...kind, item, source, due, action... </tr>
    </tbody></table>
    <div class="fb-tfoot">One task model everywhere.</div>
  </div>
  <div>
    <div class="fb-panel"> ...framework posture rows... </div>
    <div class="fb-panel"> ...recent changes feed... </div>
  </div>
</div>`;

export const Home = { render: () => compPage(appFrame(railNav("Home"), shellTopbar("<b>Home</b>"), HOME), SNIPPET) };
