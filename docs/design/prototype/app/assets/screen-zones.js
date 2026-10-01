(function () {
  'use strict';
  var U = QUI, S = QSim, F = QFloor, FF = U.F, G = U.G;
  var fp = null, work = null, selected = null, confirmOpen = false, saveT = null;
  var NAMES = {};
  S.QUEUES.forEach(function (d) { NAMES[d.id] = d.name; });
  var TYPES = U.ZONE_TYPES;
  var LEVEL = { arr: 'Arrivals level', dep: 'Departures level' };
  var REGION_NAME = { imm: 'arrival immigration', reclaim: 'baggage reclaim', ciA: 'Handler A check-in', ciB: 'Handler B check-in', sec: 'security', emi: 'departure immigration' };

  function zname(id) { var z = work && work.zones[id]; return z && z.meta ? z.meta.name : NAMES[id] || id; }
  function editable(id) { return U.createRegions().indexOf(F.regionOf(id, work.zones[id])) >= 0; }
  function editableIds() { return Object.keys(work.zones).filter(editable); }
  function baseLabel() { var d = U.state.c.draft, ps = U.profiles(); return d ? d.label : ps.length ? ps[ps.length - 1].version + ', as published' : 'the v13 draft'; }

  /* the working draft: the seeded v13 draft until someone edits, creates a draft or publishes */
  function loadWork() {
    var d = U.state.c.draft;
    if (d) return d.geometry;
    var ps = U.profiles();
    return ps.length ? F.clone(ps[ps.length - 1].geometry) : F.draftV13();
  }
  function persist(now) {
    var c = U.state.c;
    if (!c.draft) { var ps = U.profiles(); c.draft = { label: ps.length ? 'a copy of ' + ps[ps.length - 1].version : 'the v13 draft', base: ps.length ? ps[ps.length - 1].version : 'v13', by: U.roleName(), at: U.now(), geometry: work }; }
    c.draft.geometry = work;
    clearTimeout(saveT);
    if (now) U.saveC(); else saveT = setTimeout(function () { U.saveC(); }, 300);
  }

  function changes() {
    var out = [];
    Object.keys(work.zones).forEach(function (id) {
      if (!F.V12.zones[id]) out.push({ id: id, added: true });
      else if (JSON.stringify(work.zones[id]) !== JSON.stringify(F.V12.zones[id])) out.push({ id: id, from: F.snakeCapacity(id, F.V12), to: F.snakeCapacity(id, work) });
    });
    return out;
  }

  function build() {
    var body = document.getElementById('zBody');
    body.__html = null;
    document.getElementById('kpis').innerHTML = '';
    document.getElementById('kpis').__html = null;
    confirmOpen = false;
    if (!U.can('zones')) { body.innerHTML = U.denied('Zone profiles are maintained by the border and airport operations teams.'); fp = null; return; }
    work = loadWork();
    var ids = editableIds();
    if (!selected || ids.indexOf(selected) < 0) selected = ids.indexOf('A-VIS') >= 0 ? 'A-VIS' : ids.indexOf('CI-C') >= 0 ? 'CI-C' : ids[0];
    body.innerHTML =
      '<section class="panel" id="editor" aria-labelledby="edTitle"><div class="panel-head"><div><h2 id="edTitle">Zone editor</h2><p>Select a zone, then drag its corner handles or the ends of its entry and exit lines. Handles also move with the arrow keys (Shift for bigger steps). Works with mouse, pen and touch. Edits are saved to the working draft.</p></div>' +
      '<div class="panel-actions" id="zNew"></div></div>' +
      '<div class="row" style="margin-block-end:8px"><label class="field" for="zSel">Zone <select id="zSel"></select></label><button type="button" class="btn" id="zReset">Reset zone to v12</button></div>' +
      '<div id="zfp"></div><div id="zRead" class="small" style="margin-block-start:8px"></div></section>' +
      '<div class="grid cols-2"><section class="panel" aria-labelledby="pfTitle"><div class="panel-head"><div><h2 id="pfTitle">Profiles</h2><p>Every result records the profile version that produced it.</p></div><div class="panel-actions" id="zDraft"></div></div><div id="pfList"></div></section>' +
      '<section class="panel" aria-labelledby="pbTitle"><div class="panel-head"><div><h2 id="pbTitle">Publish</h2><p>Publishing creates a new version and switches the live view to it.</p></div></div><div id="pub"></div></section></div>';
    document.getElementById('zNew').innerHTML = U.createBtn('zone', 'New zone', 'zNewBtn');
    document.getElementById('zDraft').innerHTML = U.createBtn('zone', 'New draft profile', 'zDraftBtn');
    var nb = document.getElementById('zNewBtn'), db = document.getElementById('zDraftBtn');
    if (!nb.disabled) nb.addEventListener('click', function () { openZone(nb); });
    if (!db.disabled) db.addEventListener('click', newDraft);
    fp = F.create(document.getElementById('zfp'), {
      mode: 'editor', level: F.zLevel(selected, work.zones[selected]), selected: selected,
      onSelect: function (id) { selected = id; var s = document.getElementById('zSel'); if (s) s.value = id; syncReset(); readout(); },
      onEdit: function () { persist(); readout(); U.draw(true); }
    });
    fp.render(ctxFor());
    fillSelect();
    document.getElementById('zSel').addEventListener('change', function (e) { selectZone(e.target.value); });
    document.getElementById('zReset').addEventListener('click', onReset);
    document.getElementById('pub').addEventListener('click', onPub);
    syncReset();
    readout();
  }

  function fillSelect() {
    var sel = document.getElementById('zSel');
    if (!sel) return;
    sel.innerHTML = editableIds().map(function (id) { return '<option value="' + id + '"' + (id === selected ? ' selected' : '') + '>' + U.esc(zname(id)) + (work.zones[id].meta ? ' (' + id + ')' : '') + '</option>'; }).join('');
  }
  function selectZone(id) {
    selected = id;
    var lv = F.zLevel(id, work.zones[id]);
    if (fp.level !== lv) { fp.level = lv; fp.render(ctxFor()); }
    fp.select(id);
    var s = document.getElementById('zSel'); if (s) s.value = id;
    syncReset(); readout();
  }
  function syncReset() {
    var b = document.getElementById('zReset');
    if (b) b.textContent = selected && work.zones[selected] && work.zones[selected].meta ? 'Delete zone from draft' : 'Reset zone to v12';
  }
  function onReset() {
    if (!selected) return;
    var z = work.zones[selected], id = selected;
    if (z.meta) {
      delete work.zones[id];
      persist(true);
      U.audit('Deleted', id, 'Zone ' + z.meta.name + ' deleted from the draft');
      selected = editableIds()[0];
      rerender();
      U.toast('Deleted ' + id + ' from the draft', { undo: function () { work.zones[id] = z; persist(true); U.audit('Restored', id, 'Zone restored to the draft'); selected = id; rerender(); } });
      return;
    }
    work.zones[id] = F.clone(F.V12.zones[id]);
    persist(true);
    fp.render(ctxFor()); fp.select(id); U.draw(true);
  }
  function rerender() {
    fillSelect();
    if (selected) fp.level = F.zLevel(selected, work.zones[selected]);
    fp.selected = selected;
    fp.render(ctxFor());
    if (selected) fp.select(selected);
    syncReset(); readout(); U.draw(true);
  }

  function ctxFor() {
    return { geometry: work, editable: editable, zoneLabel: function (id) { var z = work.zones[id]; return z && z.meta ? z.meta.name : U.shortName(id); },
      regionVis: function (r) { var v = U.regionVis(r); return v === 'agg' ? 'hidden' : v; },
      ariaLabel: 'Zone editor floor plan. Invented layout. Select a zone to show its handles.' };
  }

  function readout() {
    var el = document.getElementById('zRead');
    if (!el || !selected || !work.zones[selected]) { if (el) el.innerHTML = ''; return; }
    var z = work.zones[selected], b = F.bbox(z.poly);
    if (z.meta) {
      el.innerHTML = '<strong>' + U.esc(z.meta.name) + '</strong> (' + selected + ', ' + U.esc(TYPES[z.meta.type]) + ', new in this draft): ' + z.poly.length + ' points, ' +
        (z.meta.type === 'line' ? 'length ' + U.fmt(lineLen(z.poly) / 10, 1) + ' m' : 'area ' + U.fmt(F.area(z.poly) / 100, 1) + ' m²') + (z.meta.linked ? ', linked to ' + U.esc(NAMES[z.meta.linked]) : '') +
        '. Measured once a calibrated sensor covers it.';
      return;
    }
    var cap0 = F.snakeCapacity(selected, F.V12), cap = F.snakeCapacity(selected, work);
    el.innerHTML = '<strong>' + U.esc(NAMES[selected]) + '</strong>: ' + z.poly.length + ' vertices, bounding box ' + Math.round(b.w) + ' by ' + Math.round(b.h) + ' plan units. Snake capacity from area: <strong>' + U.fmt(cap) + '</strong> people (v12: ' + U.fmt(cap0) + '). ' +
      'Entry line from (' + z.entry[0].join(', ') + ') to (' + z.entry[1].join(', ') + '); exit line from (' + z.exit[0].join(', ') + ') to (' + z.exit[1].join(', ') + ').';
  }
  function lineLen(p) { var s = 0; for (var i = 1; i < p.length; i++) s += Math.hypot(p[i][0] - p[i - 1][0], p[i][1] - p[i - 1][1]); return s; }

  /* a new working draft from the active profile */
  function newDraft() {
    var prev = U.state.c.draft ? F.clone(U.state.c.draft) : null, m = U.now(), v = U.profileAt(m);
    work = F.clone(U.geometryAt(m));
    U.state.c.draft = { label: 'a copy of ' + v, base: v, by: U.roleName(), at: m, geometry: work };
    U.saveC();
    U.audit('Created draft', U.nextVersion(), 'New draft profile copied from the active profile ' + v);
    confirmOpen = false;
    if (!work.zones[selected] || !editable(selected)) selected = editableIds()[0];
    rerender();
    U.toast('Created a draft from ' + v, { undo: function () {
      U.state.c.draft = prev; work = loadWork(); U.saveC(); U.audit('Undid draft', U.nextVersion(), 'Draft copied from ' + v + ' discarded'); rerender();
    } });
  }

  /* new zone drawer: polygon by clicks or the vertex table, entry and exit lines on an edge */
  function openZone(trig) {
    var id = U.nextId('zone'), regs = U.createRegions();
    var levels = ['arr', 'dep'].filter(function (lv) { return F.REGIONS[lv].some(function (r) { return regs.indexOf(r.id) >= 0; }); });
    var lv0 = selected ? F.zLevel(selected, work.zones[selected]) : levels[0];
    if (levels.indexOf(lv0) < 0) lv0 = levels[0];
    var qs = U.createQueues('zone');
    var st = { pts: [], entry: '', exit: '', mode: 'v', level: lv0 };
    var html = FF.text('zid', 'Zone ID', id, { readonly: true }) +
      FF.row(FF.select('type', 'Type', Object.keys(TYPES).map(function (k) { return [k, TYPES[k]]; }), 'snake'), FF.text('name', 'Name', '', { req: true, placeholder: 'For example: Visitors overflow snake' })) +
      FF.row(FF.select('level', 'Level', levels.map(function (l) { return [l, LEVEL[l]]; }), lv0), FF.select('linked', 'Linked queue', [['', 'None']].concat(qs.map(function (q) { return [q, NAMES[q]]; })), '')) +
      FF.radios('mode', 'Clicks on the plan place', [['v', 'Points'], ['entry', 'Entry line'], ['exit', 'Exit line']], 'v') +
      '<div class="fld" data-f="poly"><div id="zPick"></div><div class="fld-hint" id="zPickHint">Click the plan to add points in order; the vertex table is the keyboard alternative. Coordinates are metres from the plan\'s top left corner.</div><div class="fld-err" id="f-poly-e"></div></div>' +
      '<div class="table-wrap"><table class="vtable" aria-label="Vertices"><thead><tr><th scope="col">Point</th><th scope="col">x, m</th><th scope="col">y, m</th><th scope="col"><span class="sr-only">Remove</span></th></tr></thead><tbody id="vBody"></tbody></table></div>' +
      '<div class="row" style="margin-block-end:12px"><button type="button" class="btn" id="vAdd">' + U.icon('plus') + 'Add point</button><button type="button" class="btn" id="vUndo">Remove last point</button><button type="button" class="btn" id="vClear">Clear</button></div>' +
      FF.row(FF.select('entry', 'Entry line on edge', [['', 'Not placed']], ''), FF.select('exit', 'Exit line on edge', [['', 'Not placed']], '')) +
      '<div class="preview" id="zPrev" aria-live="polite"></div>';
    var pick = null;
    U.openDrawer({
      title: 'New zone in the draft', html: html, trigger: trig, returnFocus: 'zNewBtn', wide: true,
      onOpen: function (form) {
        var vBody = form.querySelector('#vBody');
        function isLine() { return form.querySelector('#f-type').value === 'line'; }
        function edgeOpts() {
          var n = st.pts.length, o = '<option value="">Not placed</option>';
          if (!isLine() && n >= 3) for (var i = 0; i < n; i++) o += '<option value="' + i + '">Edge ' + (i + 1) + ': point ' + (i + 1) + ' to ' + ((i + 1) % n + 1) + '</option>';
          ['entry', 'exit'].forEach(function (k) {
            var s = form.querySelector('#f-' + k);
            s.innerHTML = o;
            if (st[k] !== '' && +st[k] < n && n >= 3) s.value = st[k]; else { st[k] = ''; s.value = ''; }
          });
        }
        function table() {
          vBody.innerHTML = st.pts.map(function (p, i) {
            return '<tr><th scope="row">' + (i + 1) + '</th><td><input type="number" step="0.1" inputmode="decimal" aria-label="Point ' + (i + 1) + ' x in metres" data-vi="' + i + '" data-ax="0" data-key="v' + i + 'x" value="' + (p[0] / 10) + '"></td>' +
              '<td><input type="number" step="0.1" inputmode="decimal" aria-label="Point ' + (i + 1) + ' y in metres" data-vi="' + i + '" data-ax="1" data-key="v' + i + 'y" value="' + (p[1] / 10) + '"></td>' +
              '<td><button type="button" class="btn btn-ghost icon-btn" data-vdel="' + i + '" aria-label="Remove point ' + (i + 1) + '">' + U.icon('close') + '</button></td></tr>';
          }).join('') || '<tr><td colspan="4" class="muted">No points yet.</td></tr>';
          edgeOpts();
        }
        function all() { table(); pick.redraw(); preview(); }
        function overlay(g, el) {
          if (!st.pts.length) return;
          var line = isLine(), bad = !line && st.pts.length >= 4 && G.selfIntersects(st.pts);
          if (st.pts.length >= 2) el(line || st.pts.length < 3 ? 'polyline' : 'polygon', { points: F.pts(st.pts), cls: line ? 'zone zline' : 'pick-poly' + (bad ? ' pick-bad' : ''), style: line || st.pts.length < 3 ? 'fill:none;stroke:var(--accent);stroke-width:2' : '' }, g);
          ['entry', 'exit'].forEach(function (k) { var s = seg(k); if (s) el('line', { x1: s[0][0], y1: s[0][1], x2: s[1][0], y2: s[1][1], cls: 'pick-' + k }, g); });
          st.pts.forEach(function (p, i) {
            el('circle', { cx: p[0], cy: p[1], r: 6, cls: 'pick-pt' }, g);
            el('text', { x: p[0] + 8, y: p[1] - 7, cls: 'lbl-small', text: String(i + 1), style: 'fill:var(--ink);font-weight:700' }, g);
          });
        }
        function seg(k) {
          var e = st[k], n = st.pts.length;
          if (e === '' || isLine() || n < 3 || +e >= n) return null;
          var a = st.pts[+e], b = st.pts[(+e + 1) % n];
          return [[Math.round(a[0] + (b[0] - a[0]) * 0.2), Math.round(a[1] + (b[1] - a[1]) * 0.2)], [Math.round(a[0] + (b[0] - a[0]) * 0.8), Math.round(a[1] + (b[1] - a[1]) * 0.8)]];
        }
        st.seg = seg;
        function nearestEdge(x, y) {
          var best = -1, bd = Infinity, n = st.pts.length;
          for (var i = 0; i < n; i++) { var d = G.distSeg([x, y], st.pts[i], st.pts[(i + 1) % n]); if (d < bd) { bd = d; best = i; } }
          return best;
        }
        function preview() {
          var box = document.getElementById('zPrev'), n = st.pts.length, line = isLine();
          var need = line ? 2 : 3;
          var msg = n < need ? 'Add at least ' + need + ' points (' + n + ' so far).' : line ? 'Count line of ' + U.fmt(lineLen(st.pts) / 10, 1) + ' m.' :
            (G.selfIntersects(st.pts) ? 'The outline crosses itself; move or remove a point.' : 'Area ' + U.fmt(F.area(st.pts) / 100, 1) + ' m²' + (form.querySelector('#f-type').value === 'snake' ? ', about ' + U.fmt(Math.round(F.area(st.pts) / 400)) + ' people of snake capacity' : '') + '.');
          box.innerHTML = '<strong>Outline</strong>' + msg + (needsLines() ? ' Entry line: ' + (st.entry === '' ? 'not placed' : 'edge ' + (+st.entry + 1)) + '; exit line: ' + (st.exit === '' ? 'not placed' : 'edge ' + (+st.exit + 1)) + '.' : '');
        }
        function needsLines() { var t = form.querySelector('#f-type').value; return t === 'snake' || t === 'overflow'; }
        pick = F.picker(document.getElementById('zPick'), { level: st.level, geometry: work, label: 'Plan for drawing the zone. Click to add points or, in entry or exit mode, click near an edge. The vertex table and edge selectors are the keyboard alternative.',
          drawOverlay: overlay,
          onPick: function (x, y) {
            if (st.mode === 'v') { st.pts.push([x, y]); all(); return; }
            if (isLine() || st.pts.length < 3) { U.status('Place at least three points before the entry and exit lines.'); return; }
            st[st.mode] = String(nearestEdge(x, y)); edgeOpts(); pick.redraw(); preview();
          } });
        form.addEventListener('change', function (e) {
          var t = e.target;
          if (t.name === 'mode') { st.mode = t.value; return; }
          if (t.name === 'level') { st.level = t.value; st.pts = []; pick.setLevel(st.level); all(); return; }
          if (t.name === 'entry' || t.name === 'exit') { st[t.name] = t.value; pick.redraw(); preview(); return; }
          if (t.name === 'type') {
            if (t.value === 'line') { st.mode = 'v'; form.querySelector('input[name="mode"][value="v"]').checked = true; }
            var nl = needsLines() || t.value === 'area'; form.querySelector('[data-f="entry"]').hidden = !nl; form.querySelector('[data-f="exit"]').hidden = !nl; form.querySelector('[data-f="mode"]').hidden = !nl; all(); return; }
          if (t.hasAttribute('data-vi')) {
            var v = U.V.num(t.value), i = +t.getAttribute('data-vi');
            if (v != null && st.pts[i]) { st.pts[i][+t.getAttribute('data-ax')] = Math.round(v * 10); pick.redraw(); preview(); }
          }
        });
        vBody.addEventListener('keydown', function (e) {
          if (e.key === 'Enter' && e.target.hasAttribute('data-vi')) { e.preventDefault(); e.stopPropagation(); e.target.dispatchEvent(new Event('change', { bubbles: true })); }
        });
        vBody.addEventListener('click', function (e) {
          var b = e.target.closest('[data-vdel]');
          if (!b) return;
          st.pts.splice(+b.getAttribute('data-vdel'), 1); all();
          var nx = vBody.querySelector('[data-vdel]') || form.querySelector('#vAdd'); nx.focus();
        });
        form.querySelector('#vAdd').addEventListener('click', function () {
          var last = st.pts[st.pts.length - 1], b = G.levelBounds[st.level];
          st.pts.push(last ? [Math.min(b[2], last[0] + 40), last[1]] : [Math.round((b[0] + b[2]) / 2), Math.round((b[1] + b[3]) / 2)]);
          all();
          var inp = vBody.querySelector('[data-key="v' + (st.pts.length - 1) + 'x"]'); if (inp) inp.focus();
        });
        form.querySelector('#vUndo').addEventListener('click', function () { st.pts.pop(); all(); });
        form.querySelector('#vClear').addEventListener('click', function () { st.pts = []; st.entry = ''; st.exit = ''; all(); });
        all();
      },
      onSubmit: function (v) {
        var e = {}, n = st.pts.length, line = v.type === 'line', lv = v.level;
        if (!v.name) e.name = 'Enter a name.';
        else if (v.name.length > 50) e.name = 'Keep the name under 50 characters.';
        else {
          var taken = Object.keys(work.zones).some(function (zid) { return zname(zid).toLowerCase() === v.name.toLowerCase(); }) || Object.keys(NAMES).some(function (q) { return NAMES[q].toLowerCase() === v.name.toLowerCase(); });
          if (taken) e.name = 'A zone called ' + v.name + ' already exists in this draft.';
        }
        if (v.linked && ((/^A-/.test(v.linked) ? 'arr' : 'dep') !== lv)) e.linked = NAMES[v.linked] + ' is on the ' + (lv === 'arr' ? 'departures' : 'arrivals') + ' level.';
        if (n < (line ? 2 : 3)) e.poly = line ? 'A count line needs at least two points.' : 'A zone needs at least three points.';
        else {
          var outside = st.pts.map(function (p, i) { return G.inLevel(p, lv) ? null : i + 1; }).filter(Boolean);
          var foreign = st.pts.map(function (p, i) { return U.createRegions().indexOf(F.regionAt(lv, p[0], p[1])) >= 0 ? null : i + 1; }).filter(Boolean);
          if (outside.length) e.poly = 'Point' + (outside.length > 1 ? 's ' : ' ') + outside.join(', ') + (outside.length > 1 ? ' are' : ' is') + ' outside the level.';
          else if (foreign.length) e.poly = 'Point' + (foreign.length > 1 ? 's ' : ' ') + foreign.join(', ') + ' fall' + (foreign.length > 1 ? '' : 's') + ' in the ' + REGION_NAME[F.regionAt(lv, st.pts[foreign[0] - 1][0], st.pts[foreign[0] - 1][1])] + ' area, which this role does not edit.';
          else if (!line && G.selfIntersects(st.pts)) e.poly = 'The outline crosses itself. Move or remove a point so the edges do not cross.';
          else if (!line && F.area(st.pts) < 400) e.poly = 'The zone is smaller than 4 m²; spread the points out.';
          else if (line && lineLen(st.pts) < 10) e.poly = 'The count line is shorter than 1 m.';
        }
        if (!line && (v.type === 'snake' || v.type === 'overflow') && !e.poly) {
          if (st.entry === '') e.entry = 'Place the entry line on an edge of the polygon.';
          if (st.exit === '') e.exit = 'Place the exit line on an edge of the polygon.';
          if (st.entry !== '' && st.entry === st.exit) e.exit = 'Entry and exit lines must be on different edges.';
          [['entry', st.seg('entry')], ['exit', st.seg('exit')]].forEach(function (x) { if (x[1] && !(G.onEdge(x[1][0], st.pts, 2) && G.onEdge(x[1][1], st.pts, 2))) e[x[0]] = 'The ' + x[0] + ' line must lie on the polygon edge.'; });
        }
        if (Object.keys(e).length) return e;
        var z = { poly: st.pts.map(function (p) { return [p[0], p[1]]; }), meta: { type: v.type, name: v.name, level: lv, linked: v.linked || null, by: U.roleName() } };
        if (!line && st.seg('entry')) z.entry = st.seg('entry');
        if (!line && st.seg('exit')) z.exit = st.seg('exit');
        work.zones[id] = z;
        persist(true);
        selected = id;
        U.created('zone', id, TYPES[v.type] + ' ' + v.name + ' added to the draft (' + LEVEL[lv].toLowerCase() + ', ' + n + ' points)', function () {
          delete work.zones[id]; persist(true); selected = editableIds()[0]; rerender();
        }, function (undone) { if (!undone) rerender(); });
        return null;
      }
    });
  }

  function onPub(e) {
    var b = e.target.closest('[data-act]');
    if (!b) return;
    var act = b.getAttribute('data-act');
    if (act === 'publish') { confirmOpen = true; U.draw(true); var c = document.querySelector('[data-key="pub-confirm"]'); if (c) c.focus(); }
    else if (act === 'cancel') { confirmOpen = false; U.draw(true); }
    else if (act === 'confirm') {
      confirmOpen = false;
      var ch = changes().map(function (c) { return c.id; });
      var v = U.publishProfile(work, U.now(), ch);
      U.state.c.draft = null; U.saveC();
      work = loadWork();
      U.status('Published ' + v + '. The live view uses ' + v + ' from ' + S.clock(U.now()) + '.');
    } else if (act === 'withdraw') {
      var last = U.profiles().slice(-1)[0];
      U.unpublishProfile(last.version);
      U.status(last.version + ' withdrawn. ' + U.profileAt(U.now()) + ' is active again.');
    }
  }

  function update(m) {
    if (!U.can('zones') || !work) return;
    var ps = U.profiles(), active = U.profileAt(m), P = ps.filter(function (p) { return p.version === active; })[0], ch = changes(), next = U.nextVersion(), draft = U.state.c.draft;
    U.html(document.getElementById('kpis'), [
      U.kpi({ hero: true, label: 'Active zone profile now', value: active, sub: P ? 'Published at ' + S.clock(P.publishedAt) + ' by ' + U.esc(P.by) : 'Published 2 Sep 2026; signed into Handler B\'s SLA' }),
      U.kpi({ label: 'Working copy', value: String(ch.length) + ' <small>zones changed</small>', sub: 'Changed or added against v12; based on ' + U.esc(baseLabel()) }),
      U.kpi({ label: 'Zones you can edit', value: String(editableIds().length), sub: U.esc(U.roleName()) })
    ].join(''));
    var rows = [['v12', active === 'v12' ? U.st('good', 'Active') : '<span class="muted small">Superseded</span>', '2 Sep 2026', 'Baseline after the August calibration campaign. Signed into Handler B\'s contract.']];
    if (!draft && !ps.length) rows.push(['v13', '<span class="pill warn">Draft</span>', '21 Sep 2026', 'Visitors snake extended into the overflow band; island C snake one row longer. Your edits start from this draft.']);
    else if (draft) rows.push([U.esc(next), '<span class="pill warn">Working draft</span>', 'Today ' + S.clock(draft.at) + ' (simulated)', 'Started from ' + U.esc(draft.label) + ' by ' + U.esc(draft.by) + '. ' + ch.length + ' zone' + (ch.length === 1 ? '' : 's') + ' changed or added against v12.']);
    ps.forEach(function (p) {
      var added = p.changes.filter(function (id) { return !F.V12.zones[id]; });
      rows.push([p.version, p.version === active ? U.st('good', 'Active') : p.publishedAt > m ? '<span class="muted small">Takes effect at ' + S.clock(p.publishedAt) + '</span>' : '<span class="muted small">Superseded</span>', 'Today ' + S.clock(p.publishedAt) + ' (simulated)',
        'Published by ' + U.esc(p.by) + '. Changed: ' + (p.changes.length ? p.changes.map(function (id) { return p.geometry.zones[id] && p.geometry.zones[id].meta ? p.geometry.zones[id].meta.name + ' (new)' : id; }).join(', ') : 'none') + '.' +
        (added.length ? ' New zones show "Not measured" until a calibrated sensor covers them.' : '')]);
    });
    U.html(document.getElementById('pfList'), '<div class="table-wrap"><table class="tbl"><thead><tr><th scope="col">Version</th><th scope="col">Status</th><th scope="col">Created</th><th scope="col">Notes</th></tr></thead><tbody>' +
      rows.map(function (r) { return '<tr' + U.rowCls(r[0] === next ? 'draft' : r[0]) + '><th scope="row" class="mono">' + r[0] + '</th><td>' + r[1] + '</td><td>' + r[2] + '</td><td class="small">' + r[3] + '</td></tr>'; }).join('') + '</tbody></table></div>');
    var chList = ch.length ? '<ul class="small">' + ch.map(function (c) {
      return '<li>' + (c.added ? U.esc(zname(c.id)) + ' (' + c.id + '): new ' + U.esc(TYPES[work.zones[c.id].meta.type].toLowerCase()) : U.esc(NAMES[c.id]) + ': snake capacity ' + U.fmt(c.from) + ' to ' + U.fmt(c.to) + ' people') + '</li>';
    }).join('') + '</ul>' : '<p class="small muted">No changes from v12 in the working copy.</p>';
    var html, last = ps[ps.length - 1];
    if (confirmOpen) {
      html = '<div class="callout" role="group" aria-label="Confirm publish">' + U.icon('warn') + '<div><strong>Publish the working copy as ' + next + '?</strong> From ' + S.clock(m) + ' every result uses ' + next + '; results before that keep ' + active + '. Handler B\'s contract is signed against v12, so the handler is notified of the change.' +
        chList + '<div class="row"><button type="button" class="btn btn-primary" data-act="confirm" data-key="pub-confirm">Publish ' + next + '</button><button type="button" class="btn" data-act="cancel">Cancel</button></div></div></div>';
    } else {
      html = (last ? '<p class="small">' + last.version + ' is published. Results from ' + S.clock(last.publishedAt) + ' onward show ' + last.version + '; earlier results keep the version active then.</p>' : '<p class="small">The working copy is saved as a draft in this demo until you publish.</p>') + chList +
        '<div class="row">' + (U.canCreate('zone') ? '<button type="button" class="btn btn-primary" data-act="publish">Publish as ' + next + '</button>' : '') +
        (last && U.canCreate('zone') ? '<button type="button" class="btn" data-act="withdraw">Withdraw ' + last.version + ' and return to ' + (ps.length > 1 ? ps[ps.length - 2].version : 'v12') + '</button>' : '') + '</div>';
    }
    U.html(document.getElementById('pub'), html);
  }

  QUI.start({ build: build, update: update });
})();
