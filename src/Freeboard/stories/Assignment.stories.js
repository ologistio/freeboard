// Components / Assignment. The user-assignment picker, imported from the design
// project and adapted to our light-theme design system. Four surfaces of one
// widget: an inline owner cell, a drawer owner section, a bulk-assign bar, and a
// keyboard-first team-seats palette. Behaviour lives in _assignment.js (loaded by
// preview.js) since the picker is stateful; markup calls window.ua* handlers.

import { SANS, MONO, page, section, codeBlock } from "./_ui.js";
import { FB_CSS } from "./_marks.js";

export default {
    title: "Components/Assignment",
    parameters: { layout: "fullscreen" },
};

const CSS = FB_CSS;
const eyebrow = "Components / Assignment";
const X = `<svg width="12" height="12" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.6" aria-hidden="true"><path d="M4 4l8 8M12 4l-8 8"/></svg>`;

const R = [
    ["mo", "MO", "Maya Osei", "Security"],
    ["dn", "DN", "Dara Nwosu", "Engineering"],
    ["pr", "PR", "Priya Rao", "IT operations"],
    ["tl", "TL", "Theo Lindqvist", "Legal"],
    ["jp", "JP", "Jun Park", "People ops"],
    ["sr", "SR", "Sam Reyes", "Security"],
];
const findR = (id) => R.find((p) => p[0] === id);
const cur = (id, self) => (id === self ? `<span class="fb-tag fb-tag--brand">Current</span>` : "");
const ip = (pick, p, sel) => `<button type="button" class="ua-menuitem" data-kind="person" data-id="${p[0]}" data-ini="${p[1]}" data-name="${p[2]}" data-dept="${p[3]}" onclick="${pick}(this)"><span class="fb-av ua-av-person">${p[1]}</span><span class="ua-mmid"><span class="ua-mname">${p[2]}</span><span class="ua-msub">${p[3]}</span></span>${cur(p[0], sel)}</button>`;
const ig = (pick, sel) => `<button type="button" class="ua-menuitem" data-kind="group" data-id="g-sec" data-ini="ST" data-name="Security team" data-dept="5 people" onclick="${pick}(this)"><span class="fb-av ua-av-group">ST</span><span class="ua-mmid"><span class="ua-mname">Security team</span><span class="ua-msub">5 people</span></span>${cur("g-sec", sel)}</button>`;
const menuList = (pick, sel) => ig(pick, sel) + R.map((p) => ip(pick, p, sel)).join("");

const ownerDisplay = (id) => {
    if (!id) return `<span class="ua-assign"><span class="plus">+</span>Assign owner</span>`;
    if (id === "g-sec") return `<span class="fb-av ua-av-group">ST</span><span class="ua-oname">Security team</span> <span class="fb-badge">GROUP</span>`;
    const p = findR(id);
    return `<span class="fb-av ua-av-person">${p[1]}</span><span class="ua-oname">${p[2]}</span>`;
};

// ---- 1a inline owner cell ----
const cell = (code, name, ownerId) => `<tr class="fb-row">
        <td><span class="fb-kind" style="width:auto">${code}</span></td>
        <td><span class="fb-tdname">${name}</span></td>
        <td class="ua-cell">
          <button type="button" class="ua-trigger" onclick="uaOpen(this)">${ownerDisplay(ownerId)}</button>
          <div class="ua-back" onclick="uaClose(this)"></div>
          <div class="ua-menu">
            <div class="ua-menu__head"><input class="fb-search" style="width:100%" placeholder="Search people or teams..." aria-label="Search people or teams" oninput="uaFilter(this)"></div>
            <div class="ua-list">${menuList("uaPick", ownerId)}</div>
          </div>
        </td>
      </tr>`;

const S1A = `<div class="ua-scope">
    <div class="fb-panel" style="width:520px;max-width:100%;overflow:visible">
      <div class="fb-panel__head"><h2>Controls</h2><span class="fb-panel__meta">Showing 4 of 38</span></div>
      <div style="overflow:visible">
        <table class="fb-tbl">
          <thead><tr><th style="width:52px">Kind</th><th>Control</th><th style="width:230px">Owner</th></tr></thead>
          <tbody>
            ${cell("CC6.1", "Access reviews", "mo")}
            ${cell("CC7.2", "Vulnerability scanning", null)}
            ${cell("CC6.6", "Encryption in transit", "g-sec")}
            ${cell("A.12.4", "Event logging", "dn")}
          </tbody>
        </table>
      </div>
    </div>
  </div>`;

const S1A_CODE = `<!-- Owner cell: trigger + searchable popover. Handlers in _assignment.js. -->
<td class="ua-cell">
  <button type="button" class="ua-trigger" onclick="uaOpen(this)">
    <span class="fb-av ua-av-person">MO</span><span class="ua-oname">Maya Osei</span>
  </button>
  <div class="ua-back" onclick="uaClose(this)"></div>
  <div class="ua-menu">
    <div class="ua-menu__head"><input class="fb-search" placeholder="Search people or teams..." oninput="uaFilter(this)"></div>
    <div class="ua-list">
      <button type="button" class="ua-menuitem" data-kind="person" data-ini="DN" data-name="Dara Nwosu" onclick="uaPick(this)">
        <span class="fb-av ua-av-person">DN</span>
        <span class="ua-mmid"><span class="ua-mname">Dara Nwosu</span><span class="ua-msub">Engineering</span></span>
      </button>
      <!-- ...more people and teams... -->
    </div>
  </div>
</td>
<!-- Unassigned trigger content: <span class="ua-assign"><span class="plus">+</span>Assign owner</span> -->`;

// ---- 1b drawer owner section ----
const S1B = `<div class="ua-scope">
    <div class="fb-sheet" style="width:440px;max-width:100%">
      <div class="fb-dhead"><div class="fb-eyebrow">Control / CC6.1</div><h2 style="font:700 16px ${SANS};margin:4px 0 2px">Access reviews</h2></div>
      <div class="fb-dbody">
        <div class="fb-dsec ua-owner">
          <div class="fb-dl">Owner</div>
          <div class="ua-notice"></div>
          <div class="ua-owner-current">
            <span class="ua-owner-who"><span class="fb-av ua-av-person ua-av-lg">MO</span><span><span class="ua-mname" style="font-size:13.5px;font-weight:600">Maya Osei</span><span class="ua-msub">Security</span></span></span>
            <button class="fb-btn fb-btn--sm" onclick="uaDrawerChange(this)">Change</button>
          </div>
          <div class="ua-owner-edit">
            <div class="ua-edit-head">
              <input class="fb-search" style="flex:1;width:auto" placeholder="Search people or teams..." aria-label="Search people or teams" oninput="uaFilter(this)">
              <button class="fb-btn fb-btn--sm" onclick="uaDrawerAssignMe(this)" style="flex:none">Assign to me</button>
            </div>
            <div class="ua-list" style="max-height:210px">${menuList("uaDrawerPick", "mo")}</div>
            <div class="ua-edit-foot">
              <button class="fb-btn fb-btn--quiet fb-btn--sm" onclick="uaDrawerUnassign(this)">Unassign</button>
              <button class="fb-btn fb-btn--quiet fb-btn--sm" onclick="uaDrawerCancel(this)">Cancel</button>
            </div>
          </div>
        </div>
        <div class="fb-dsec"><div class="fb-dl">Provenance</div><span class="fb-stamp manual">MANUAL / SET BY S. REYES</span></div>
      </div>
    </div>
  </div>`;

const S1B_CODE = `<div class="fb-dsec ua-owner">
  <div class="fb-dl">Owner</div>
  <div class="ua-notice"></div>
  <div class="ua-owner-current">
    <span class="ua-owner-who"><span class="fb-av ua-av-person ua-av-lg">MO</span><span>...name / dept...</span></span>
    <button class="fb-btn fb-btn--sm" onclick="uaDrawerChange(this)">Change</button>
  </div>
  <div class="ua-owner-edit">
    <div class="ua-edit-head">
      <input class="fb-search" placeholder="Search..." oninput="uaFilter(this)">
      <button class="fb-btn fb-btn--sm" onclick="uaDrawerAssignMe(this)">Assign to me</button>
    </div>
    <div class="ua-list"><!-- menu items call uaDrawerPick(this) --></div>
    <div class="ua-edit-foot">
      <button class="fb-btn fb-btn--quiet fb-btn--sm" onclick="uaDrawerUnassign(this)">Unassign</button>
      <button class="fb-btn fb-btn--quiet fb-btn--sm" onclick="uaDrawerCancel(this)">Cancel</button>
    </div>
  </div>
</div>`;

// ---- 1c bulk assign ----
const E = [
    ["MFA enforced on admin console", "SOC 2 / CC6.1", "mo"],
    ["Quarterly access review export", "SOC 2 / CC6.3", null],
    ["Backup restoration test log", "ISO 27001 / A.12.3", null],
    ["Vendor SOC 2 report on file", "SOC 2 / CC9.2", null],
    ["Endpoint encryption inventory", "ISO 27001 / A.8.1", "dn"],
    ["Incident response tabletop notes", "SOC 2 / CC7.4", null],
];
const ownerCell = (id) => {
    if (!id) return `<span style="font:500 11px ${MONO};letter-spacing:.06em;color:#8a938e;text-transform:uppercase">Unassigned</span>`;
    const p = findR(id);
    return `<span class="fb-owner"><span class="fb-av ua-av-person">${p[1]}</span>${p[2]}</span>`;
};
const erow = (e) => `<tr class="fb-row">
        <td><input type="checkbox" class="fb-ck ua-rowck" aria-label="Select task" onchange="uaBulkRow(this)"></td>
        <td><span class="fb-tdname">${e[0]}</span><div class="fb-tdsub">${e[1]}</div></td>
        <td class="ua-ownercell">${ownerCell(e[2])}</td>
      </tr>`;

const S1C = `<div class="ua-scope">
    <div class="fb-panel" style="width:560px;max-width:100%;overflow:visible">
      <div class="fb-panel__head"><h2>Evidence tasks</h2><span class="fb-panel__meta">Showing 6 of 24</span></div>
      <div class="ua-donebox" style="padding:0 16px"></div>
      <div style="overflow:visible">
        <table class="fb-tbl">
          <thead><tr><th style="width:34px"><input type="checkbox" class="fb-ck ua-allck" aria-label="Select all" onchange="uaBulkAll(this)"></th><th>Task</th><th style="width:190px">Owner</th></tr></thead>
          <tbody>${E.map(erow).join("")}</tbody>
        </table>
      </div>
      <div class="ua-bulkwrap">
        <div class="fb-bulk"><span><b style="font-weight:700"><span class="ua-selcount">0</span></b> selected</span><span class="fb-spacer"></span><button class="fb-bulk__btn" onclick="uaBulkOpen(this)">Assign owner</button><button class="fb-bulk__btn" onclick="uaBulkClear(this)">Clear</button></div>
        <div class="ua-back" onclick="uaClose(this)"></div>
        <div class="ua-bulkpop">
          <div class="ua-menu__head"><input class="fb-search" style="width:100%" placeholder="Assign selected tasks to..." aria-label="Assign selected tasks to" oninput="uaFilter(this)"></div>
          <div class="ua-list" style="max-height:220px">${menuList("uaBulkPick", null)}</div>
        </div>
      </div>
    </div>
  </div>`;

const S1C_CODE = `<!-- Row checkbox + owner cell; the bulk bar appears on selection. -->
<tr class="fb-row">
  <td><input type="checkbox" class="fb-ck ua-rowck" onchange="uaBulkRow(this)"></td>
  <td>...task...</td>
  <td class="ua-ownercell">Unassigned</td>
</tr>

<div class="ua-bulkwrap">
  <div class="fb-bulk">
    <span><b><span class="ua-selcount">0</span></b> selected</span>
    <span class="fb-spacer"></span>
    <button class="fb-bulk__btn" onclick="uaBulkOpen(this)">Assign owner</button>
    <button class="fb-bulk__btn" onclick="uaBulkClear(this)">Clear</button>
  </div>
  <div class="ua-bulkpop"><!-- search + menu items call uaBulkPick(this) --></div>
</div>`;

// ---- 1d team seats ----
const seat = (p) => `<div class="ua-seat" data-id="${p[0]}"><span class="fb-av ua-av-person ua-av-lg">${p[1]}</span><span class="ua-seatinfo"><span class="ua-mname">${p[2]}</span><span class="ua-msub">${p[3]}</span></span><button class="ua-x" title="Remove ${p[2]}" onclick="uaSeatRemove(this)">${X}</button></div>`;
const seatItem = (p) => `<button type="button" class="ua-menuitem" data-id="${p[0]}" data-ini="${p[1]}" data-name="${p[2]}" data-dept="${p[3]}" onclick="uaSeatPick(this)"><span class="fb-av ua-av-person">${p[1]}</span><span class="ua-mmid"><span class="ua-mname">${p[2]}</span><span class="ua-msub">${p[3]}</span></span></button>`;
const seated = ["mo", "dn"];

const S1D = `<div class="ua-scope">
    <div class="fb-panel" style="width:400px;max-width:100%">
      <div class="fb-panel__head"><h2>Assignees</h2><span class="fb-panel__meta"><span class="ua-seatcount">2 people</span></span></div>
      <div class="fb-panel__body ua-seatbox">
        <div class="ua-seats" style="display:flex;flex-direction:column;gap:2px;margin-bottom:12px">
          ${seat(findR("mo"))}${seat(findR("dn"))}
        </div>
        <button class="ua-addbtn" onclick="uaSeatOpen(this)"><span class="plus">+</span>Add assignee <span class="fb-kbd" style="margin-left:2px">K</span></button>
        <div class="ua-seatpalette">
          <input class="fb-palinput" placeholder="Add a teammate..." aria-label="Add a teammate" oninput="uaFilter(this)" onkeydown="uaSeatKey(this,event)">
          <div class="ua-list">${R.filter((p) => !seated.includes(p[0])).map(seatItem).join("")}</div>
          <div class="fb-palfoot">Up/Down navigate / Enter add / Esc close</div>
        </div>
      </div>
    </div>
  </div>`;

const S1D_CODE = `<div class="fb-panel__body ua-seatbox">
  <div class="ua-seats">
    <div class="ua-seat" data-id="mo">
      <span class="fb-av ua-av-person ua-av-lg">MO</span>
      <span class="ua-seatinfo"><span class="ua-mname">Maya Osei</span><span class="ua-msub">Security</span></span>
      <button class="ua-x" onclick="uaSeatRemove(this)">x</button>
    </div>
  </div>
  <button class="ua-addbtn" onclick="uaSeatOpen(this)"><span class="plus">+</span>Add assignee <span class="fb-kbd">K</span></button>
  <div class="ua-seatpalette">
    <input class="fb-palinput" placeholder="Add a teammate..." oninput="uaFilter(this)" onkeydown="uaSeatKey(this,event)">
    <div class="ua-list"><!-- items call uaSeatPick(this) --></div>
    <div class="fb-palfoot">Up/Down navigate / Enter add / Esc close</div>
  </div>
</div>`;

export const InlineOwnerCell = {
    render: () => page({
        eyebrow, title: "Inline owner cell", css: CSS,
        lead: "Compact owner per row. Click to reassign from a searchable list of people and teams. Covers unassigned (a dashed pill), person, and group owners.",
        body: section("Inline owner cell", "Click an owner to open the picker; type to filter; pick to reassign.", S1A) + section("Markup", "", codeBlock(S1A_CODE)),
    }),
};

export const DrawerOwner = {
    render: () => page({
        eyebrow, title: "Drawer owner", css: CSS,
        lead: "The Owner block inside a control detail drawer. Change reveals a search with an assign-to-me shortcut, unassign, and cancel.",
        body: section("Drawer owner section", "Change to edit; pick, assign to me, or unassign.", S1B) + section("Markup", "", codeBlock(S1B_CODE)),
    }),
};

export const BulkAssign = {
    render: () => page({
        eyebrow, title: "Bulk assign", css: CSS,
        lead: "Select rows, then assign one owner to all of them at once. Honest counts; unassigned rows fill in, and a notice confirms what happened.",
        body: section("Bulk assign bar", "Tick rows to reveal the bar; Assign owner opens the picker; picking fills every selected row.", S1C) + section("Markup", "", codeBlock(S1C_CODE)),
    }),
};

export const TeamSeats = {
    render: () => page({
        eyebrow, title: "Team seats", css: CSS,
        lead: "Multiple assignees as removable seats. Add with a keyboard-first palette (Up/Down to move, Enter to add, Esc to close). Hover a seat to reveal remove.",
        body: section("Command-palette team", "Add assignee opens the palette; type and press Enter, or click. Hover a seat to remove.", S1D) + section("Markup", "", codeBlock(S1D_CODE)),
    }),
};
