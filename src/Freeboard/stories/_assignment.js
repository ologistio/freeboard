// Behaviour for the Assignment widget (Components/Assignment and its use in
// compositions). Loaded once via .storybook/preview.js so inline handlers can
// call window.ua* - the picker is stateful (search, multi-select, bulk apply,
// keyboard) beyond what clean inline handlers manage, and rebuilds quoted markup
// on pick. State lives in the DOM (open classes, checkbox state); no framework.

(function () {
  const X = '<svg width="12" height="12" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.6" aria-hidden="true"><path d="M4 4l8 8M12 4l-8 8"/></svg>';
  const av = (kind, ini, lg) =>
    '<span class="fb-av ua-av-' + (kind === 'group' ? 'group' : 'person') + (lg ? ' ua-av-lg' : '') + '">' + ini + '</span>';
  const data = (el, k) => el.getAttribute('data-' + k);
  const resetMenu = (box) => {
    const s = box.querySelector('input');
    if (s) { s.value = ''; box.querySelectorAll('.ua-menuitem').forEach((i) => { i.style.display = ''; i.classList.remove('hi'); }); s.focus(); }
  };

  // ---- shared: filter a popover's items by text ----
  window.uaFilter = function (input) {
    const box = input.closest('.ua-menu, .ua-owner-edit, .ua-bulkpop, .ua-seatpalette');
    const q = input.value.toLowerCase();
    box.querySelectorAll('.ua-menuitem').forEach((i) => { i.style.display = i.textContent.toLowerCase().indexOf(q) > -1 ? '' : 'none'; });
  };
  window.uaClose = function (el) { const o = el.closest('.open'); if (o) o.classList.remove('open'); };

  // ---- 1a inline owner cell ----
  window.uaOpen = function (btn) {
    const cell = btn.closest('.ua-cell'), scope = btn.closest('.ua-scope');
    scope.querySelectorAll('.ua-cell.open').forEach((c) => { if (c !== cell) c.classList.remove('open'); });
    cell.classList.toggle('open');
    if (cell.classList.contains('open')) resetMenu(cell.querySelector('.ua-menu'));
  };
  window.uaPick = function (item) {
    const cell = item.closest('.ua-cell'), t = cell.querySelector('.ua-trigger');
    const kind = data(item, 'kind');
    t.innerHTML = av(kind, data(item, 'ini')) + '<span class="ua-oname">' + data(item, 'name') + '</span>' + (kind === 'group' ? ' <span class="fb-badge">GROUP</span>' : '');
    cell.classList.remove('open');
  };

  // ---- 1b drawer owner section ----
  const drawerCurrent = (kind, ini, name, dept) =>
    '<span class="ua-owner-who">' + av(kind, ini, true) +
    '<span><span class="ua-mname" style="font-size:13.5px;font-weight:600">' + name + '</span><span class="ua-msub">' + dept + '</span></span></span>' +
    '<button class="fb-btn fb-btn--sm" onclick="uaDrawerChange(this)">Change</button>';
  window.uaDrawerChange = function (btn) {
    const o = btn.closest('.ua-owner'); o.classList.add('editing'); resetMenu(o.querySelector('.ua-owner-edit'));
  };
  window.uaDrawerCancel = function (btn) { btn.closest('.ua-owner').classList.remove('editing'); };
  const drawerSet = (o, kind, ini, name, dept) => {
    o.querySelector('.ua-notice').innerHTML = '';
    o.querySelector('.ua-owner-current').innerHTML = drawerCurrent(kind, ini, name, dept);
    o.classList.remove('editing');
  };
  window.uaDrawerPick = function (item) {
    drawerSet(item.closest('.ua-owner'), data(item, 'kind'), data(item, 'ini'), data(item, 'name'), data(item, 'dept') || data(item, 'sub'));
  };
  window.uaDrawerAssignMe = function (btn) { drawerSet(btn.closest('.ua-owner'), 'person', 'SR', 'Sam Reyes', 'Security'); };
  window.uaDrawerUnassign = function (btn) {
    const o = btn.closest('.ua-owner');
    o.querySelector('.ua-notice').innerHTML = '<div class="fb-notice fb-notice--warn" style="margin-bottom:10px">This control needs an owner before it can be reviewed.</div>';
    o.querySelector('.ua-owner-current').innerHTML = '<button class="ua-assign" onclick="uaDrawerChange(this)"><span class="plus">+</span>Assign owner</button>';
    o.classList.remove('editing');
  };

  // ---- 1c bulk assign ----
  const bulkUpdate = (scope) => {
    const rows = scope.querySelectorAll('.ua-rowck');
    let n = 0; rows.forEach((c) => { if (c.checked) n++; });
    scope.querySelector('.fb-bulk').classList.toggle('show', n > 0);
    scope.querySelector('.ua-selcount').textContent = n;
    const all = scope.querySelector('.ua-allck'); if (all) all.checked = n > 0 && n === rows.length;
    if (n === 0) { const w = scope.querySelector('.ua-bulkwrap'); if (w) w.classList.remove('open'); }
  };
  window.uaBulkRow = function (cb) { cb.closest('.ua-scope').querySelector('.ua-donebox').innerHTML = ''; bulkUpdate(cb.closest('.ua-scope')); };
  window.uaBulkAll = function (cb) { const scope = cb.closest('.ua-scope'); scope.querySelectorAll('.ua-rowck').forEach((c) => { c.checked = cb.checked; }); scope.querySelector('.ua-donebox').innerHTML = ''; bulkUpdate(scope); };
  window.uaBulkOpen = function (btn) { const w = btn.closest('.ua-bulkwrap'); w.classList.add('open'); resetMenu(w.querySelector('.ua-bulkpop')); };
  window.uaBulkClear = function (btn) { const scope = btn.closest('.ua-scope'); scope.querySelectorAll('.ua-rowck').forEach((c) => { c.checked = false; }); bulkUpdate(scope); };
  window.uaBulkPick = function (item) {
    const scope = item.closest('.ua-scope'), kind = data(item, 'kind'), name = data(item, 'name');
    const cell = '<span class="fb-owner">' + av(kind, data(item, 'ini')) + name + '</span>';
    let n = 0;
    scope.querySelectorAll('.ua-rowck').forEach((c) => { if (c.checked) { c.closest('tr').querySelector('.ua-ownercell').innerHTML = cell; c.checked = false; n++; } });
    scope.querySelector('.ua-donebox').innerHTML = '<div class="fb-notice fb-notice--ok" style="margin:10px 0 0">Assigned ' + n + ' ' + (n === 1 ? 'task' : 'tasks') + ' to ' + name + '.</div>';
    bulkUpdate(scope);
  };

  // ---- 1d team seats ----
  const seatCount = (box) => {
    const n = box.querySelectorAll('.ua-seats .ua-seat').length;
    box.querySelector('.ua-seatcount').textContent = n + (n === 1 ? ' person' : ' people');
    box.querySelector('.ua-seats').style.display = n ? '' : 'none';
  };
  window.uaSeatOpen = function (btn) {
    const box = btn.closest('.ua-seatbox'); box.classList.add('open');
    const inp = box.querySelector('.ua-seatpalette input'); if (inp) { inp.value = ''; box.querySelectorAll('.ua-seatpalette .ua-menuitem').forEach((i, idx) => { i.style.display = ''; i.classList.toggle('hi', idx === 0); }); inp.focus(); }
  };
  window.uaSeatPick = function (item) {
    const box = item.closest('.ua-seatbox'), seats = box.querySelector('.ua-seats');
    const id = data(item, 'id'), seat = document.createElement('div');
    seat.className = 'ua-seat'; seat.setAttribute('data-id', id);
    seat.innerHTML = av('person', data(item, 'ini'), true) + '<span class="ua-seatinfo"><span class="ua-mname">' + data(item, 'name') + '</span><span class="ua-msub">' + data(item, 'dept') + '</span></span><button class="ua-x" title="Remove ' + data(item, 'name') + '" onclick="uaSeatRemove(this)">' + X + '</button>';
    seats.appendChild(seat);
    item.style.display = 'none';
    box.classList.remove('open');
    seatCount(box);
  };
  window.uaSeatRemove = function (btn) {
    const seat = btn.closest('.ua-seat'), box = btn.closest('.ua-seatbox'), id = seat.getAttribute('data-id');
    seat.remove();
    const it = box.querySelector('.ua-seatpalette .ua-menuitem[data-id="' + id + '"]'); if (it) it.style.display = '';
    seatCount(box);
  };
  window.uaSeatKey = function (input, e) {
    const box = input.closest('.ua-seatbox');
    const vis = [].slice.call(box.querySelectorAll('.ua-seatpalette .ua-menuitem')).filter((i) => i.style.display !== 'none');
    let cur = vis.findIndex((i) => i.classList.contains('hi'));
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault(); if (!vis.length) return;
      const n = e.key === 'ArrowDown' ? (cur < 0 ? 0 : (cur + 1) % vis.length) : (cur <= 0 ? vis.length - 1 : cur - 1);
      vis.forEach((i) => i.classList.remove('hi')); vis[n].classList.add('hi'); vis[n].scrollIntoView({ block: 'nearest' });
    } else if (e.key === 'Enter') { e.preventDefault(); if (vis[cur]) window.uaSeatPick(vis[cur]); }
    else if (e.key === 'Escape') { e.preventDefault(); box.classList.remove('open'); }
  };
})();
