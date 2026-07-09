// Compositions / Controls. A controls list page that uses the Assignment widget's
// inline owner cell (Components/Assignment) in the Owner column - click an owner
// to assign or reassign from a searchable people/teams list. Behaviour comes from
// _assignment.js (loaded by preview.js); the classes ship in the shared stylesheet.

import { compPage } from "./_marks.js";

export default {
    title: "Compositions/Controls",
    parameters: { layout: "fullscreen" },
};

const CHIP = "var p=this.parentNode;var c=p.querySelectorAll('.fb-chip');for(var i=0;i<c.length;i++)c[i].classList.remove('on');this.classList.add('on')";

const R = [
    ["mo", "MO", "Maya Osei", "Security"],
    ["dn", "DN", "Dara Nwosu", "Engineering"],
    ["pr", "PR", "Priya Rao", "IT operations"],
    ["tl", "TL", "Theo Lindqvist", "Legal"],
    ["jp", "JP", "Jun Park", "People ops"],
    ["sr", "SR", "Sam Reyes", "Security"],
];
const findR = (id) => R.find((p) => p[0] === id);
const sel = (id, s) => (id === s ? `<span class="fb-tag fb-tag--brand">Current</span>` : "");
const ip = (p, s) => `<button type="button" class="ua-menuitem" data-kind="person" data-id="${p[0]}" data-ini="${p[1]}" data-name="${p[2]}" data-dept="${p[3]}" onclick="uaPick(this)"><span class="fb-av ua-av-person">${p[1]}</span><span class="ua-mmid"><span class="ua-mname">${p[2]}</span><span class="ua-msub">${p[3]}</span></span>${sel(p[0], s)}</button>`;
const ig = (s) => `<button type="button" class="ua-menuitem" data-kind="group" data-id="g-sec" data-ini="ST" data-name="Security team" data-dept="5 people" onclick="uaPick(this)"><span class="fb-av ua-av-group">ST</span><span class="ua-mmid"><span class="ua-mname">Security team</span><span class="ua-msub">5 people</span></span>${sel("g-sec", s)}</button>`;
const menuList = (s) => ig(s) + R.map((p) => ip(p, s)).join("");

const ownerDisplay = (id) => {
    if (!id) return `<span class="ua-assign"><span class="plus">+</span>Assign owner</span>`;
    if (id === "g-sec") return `<span class="fb-av ua-av-group">ST</span><span class="ua-oname">Security team</span> <span class="fb-badge">GROUP</span>`;
    const p = findR(id);
    return `<span class="fb-av ua-av-person">${p[1]}</span><span class="ua-oname">${p[2]}</span>`;
};
const ownerCell = (id) => `<td class="ua-cell">
      <button type="button" class="ua-trigger" onclick="uaOpen(this)">${ownerDisplay(id)}</button>
      <div class="ua-back" onclick="uaClose(this)"></div>
      <div class="ua-menu">
        <div class="ua-menu__head"><input class="fb-search" style="width:100%" placeholder="Search people or teams..." aria-label="Search people or teams" oninput="uaFilter(this)"></div>
        <div class="ua-list">${menuList(id)}</div>
      </div>
    </td>`;

const row = (code, name, cls, word, satisfies, ownerId, ev) => `<tr class="fb-row">
      <td><span class="fb-status ${cls}"><span class="fb-seal ${cls}"></span>${word}</span></td>
      <td><div class="fb-tdname">${name}</div><div class="fb-tdsub">${code}</div></td>
      <td>${satisfies}</td>
      ${ownerCell(ownerId)}
      <td>${ev}</td>
    </tr>`;

const COMP = `<div class="ua-scope">
  <div class="fb-pagehead">
    <div>
      <div class="fb-eyebrow">Comply</div>
      <h1>Controls</h1>
      <div class="fb-sub">The primary object. Tests prove a control; frameworks borrow it. Assign an owner inline from any row.</div>
    </div>
    <div class="fb-headactions"><button type="button" class="fb-btn fb-btn--brand">New control</button></div>
  </div>
  <div class="fb-panel" style="overflow:visible">
    <div class="fb-toolbar">
      <button type="button" class="fb-chip on" onclick="${CHIP}">All<span class="n">312</span></button>
      <button type="button" class="fb-chip" onclick="${CHIP}">Unowned<span class="n">7</span></button>
      <button type="button" class="fb-chip" onclick="${CHIP}">Failing<span class="n">9</span></button>
      <span class="fb-spacer"></span>
      <input type="search" class="fb-search" placeholder="Filter controls..." aria-label="Filter controls">
    </div>
    <div style="overflow:visible">
      <table class="fb-tbl">
        <thead><tr><th>Status</th><th>Control</th><th>Satisfies</th><th style="width:240px">Owner</th><th>Evidence</th></tr></thead>
        <tbody>
          ${row("CC6.1", "Access reviews", "fail", "Failing", `<span class="fb-tag fb-tag--brand">SOC 2</span> <span class="fb-tag">+2</span>`, "mo", `<span class="fb-stamp">AUTO / 22M</span>`)}
          ${row("CC7.2", "Vulnerability scanning", "fail", "Failing", `<span class="fb-tag fb-tag--brand">SOC 2</span>`, null, `<span class="fb-stamp">AUTO / 2d</span>`)}
          ${row("CC6.6", "Encryption in transit", "ok", "Ready", `<span class="fb-tag fb-tag--brand">ISO 27001</span> <span class="fb-tag">+2</span>`, "g-sec", `<span class="fb-stamp">AUTO / 12M</span>`)}
          ${row("A.12.4", "Event logging", "ok", "Ready", `<span class="fb-tag fb-tag--brand">ISO 27001</span> <span class="fb-tag">+1</span>`, "dn", `<span class="fb-stamp">AUTO / 8M</span>`)}
        </tbody>
      </table>
    </div>
    <div class="fb-tfoot">Showing 4 of 312. Click an owner to assign or reassign.</div>
  </div>
</div>`;

const SNIPPET = `<div class="ua-scope">
  <div class="fb-pagehead"> ...Comply / Controls / New control... </div>

  <div class="fb-panel" style="overflow:visible">
    <div class="fb-toolbar"> ...chips + search... </div>
    <table class="fb-tbl">
      <thead><tr><th>Status</th><th>Control</th><th>Satisfies</th><th>Owner</th><th>Evidence</th></tr></thead>
      <tbody>
        <tr class="fb-row">
          <td>...status...</td>
          <td>...control name...</td>
          <td>...satisfies tags...</td>
          <!-- Assignment widget: inline owner cell (Components/Assignment) -->
          <td class="ua-cell">
            <button type="button" class="ua-trigger" onclick="uaOpen(this)">
              <span class="fb-av ua-av-person">MO</span><span class="ua-oname">Maya Osei</span>
            </button>
            <div class="ua-back" onclick="uaClose(this)"></div>
            <div class="ua-menu"><!-- search + people/teams, uaPick(this) --></div>
          </td>
          <td>...evidence...</td>
        </tr>
      </tbody>
    </table>
    <div class="fb-tfoot">Showing 4 of 312. Click an owner to assign or reassign.</div>
  </div>
</div>`;

export const List = { render: () => compPage(COMP, SNIPPET) };
