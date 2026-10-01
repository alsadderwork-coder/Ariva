(function () {
  'use strict';
  var U = QUI, S = QSim, QC = QCharts;
  var ISL = { A: 'CI-A', B: 'CI-B', C: 'CI-C', D: 'CI-D' };

  function visibleIslands() {
    var o = [];
    if (U.can('ci.A')) o.push('A', 'B');
    if (U.can('ci.B')) o.push('C', 'D');
    return o;
  }

  function build() {
    ['kpis', 'islands', 'ciChart', 'bins'].forEach(function (id) { var e = document.getElementById(id); e.innerHTML = ''; e.__html = null; });
    if (U.screenAccess('checkin') === 'none') {
      document.getElementById('islands').innerHTML = U.denied('Check-in and handler data belong to the airport deployment.');
      document.getElementById('ciChart').innerHTML = U.denied('');
      document.getElementById('bins').innerHTML = U.denied('');
    }
  }

  function kpis(m) {
    var day = U.day, isl = visibleIslands(), worst = null, wi = null, tot = 0, open = 0;
    isl.forEach(function (i) {
      var s = day.state(S.QI[ISL[i]], m);
      tot += s.len; open += s.open;
      if (s.nowcast != null && (worst == null || s.nowcast > worst)) { worst = s.nowcast; wi = i; }
    });
    var ws = wi ? day.state(S.QI[ISL[wi]], m) : null, lv = U.waitLevel(worst);
    var bs = Math.floor(m / 15) * 15, bw = null, bi = null;
    isl.forEach(function (i) { var b = day.bin(S.QI[ISL[i]], bs, m); if (b.p90 != null && (bw == null || b.p90 > bw)) { bw = b.p90; bi = i; } });
    var t = [
      U.kpi({ hero: true, level: lv === 'good' ? '' : lv, label: 'Longest island wait', value: ws ? U.esc(U.waitText(ws)) : 'n/a', sub: wi ? 'Island ' + wi + ' (Handler ' + (wi < 'C' ? 'A' : 'B') + ') &nbsp; ' + U.waitSt(ws) : '' }),
      U.kpi({ label: 'People queuing', value: U.fmt(tot), sub: 'at ' + isl.length + ' islands' }),
      U.kpi({ label: 'Counters staffed', value: open + ' <small>of ' + isl.length * 12 + '</small>', sub: 'Check-in ' + S.PARAMS.svc.CI + ' s per passenger' }),
      U.kpi({ label: 'Current bin at P90', value: bw == null ? 'n/a' : U.fmtWait(bw), sub: 'Bin ' + S.clock(bs) + ' to ' + S.clock(bs + 15) + (bi ? ', island ' + bi : '') + '; ' + (bw != null && bw > 15 ? U.st('crit', 'Above 15 min') : U.st('good', 'Within 15 min')) + ', provisional' })
    ];
    U.html(document.getElementById('kpis'), t.join(''));
  }

  function islands(m) {
    var day = U.day, groups = [];
    function card(i) {
      var q = S.QI[ISL[i]], s = day.state(q, m), sv = day.servers(q, m);
      var flights = day.S.dep.filter(function (f) { return f.island === i && m >= f.std - 180 && m <= f.std - 45; });
      var allocNow = {};
      var ctr = sv.map(function (x) {
        var al = day.allocAt(q, x.k, m);
        if (al) (allocNow[al.id] = allocNow[al.id] || { al: al, ks: [] }).ks.push(x.k);
        var code = al ? al.code.replace(' ', '') : '';
        return '<span class="ctr ' + x.state + (al ? ' alloc' : '') + '" title="' + x.id + ': ' + U.stateText(x.state) + (al ? ', allocated to ' + al.code : '') + '">' + U.glyph(x.state) + '<span>' + x.id.slice(1) + '</span>' +
          (al ? '<span class="ctr-code">' + code + '</span>' : '') + '<span class="sr-only">' + x.id + ' ' + U.stateText(x.state) + (al ? ', allocated to ' + al.code : '') + '</span></span>';
      }).join('');
      var allocLine = Object.keys(allocNow).map(function (k) {
        var a = allocNow[k], ks = a.ks.sort(function (x, y) { return x - y; });
        return i + String(ks[0] + 1).padStart(2, '0') + (ks.length > 1 ? ' to ' + i + String(ks[ks.length - 1] + 1).padStart(2, '0') : '') + ': <span class="mono">' + U.esc(a.al.code) + '</span> (' + k + ')';
      });
      return '<div class="island"><div class="island-head"><h4>Island ' + i + '</h4>' + U.waitSt(s) + '</div>' +
        '<div class="island-wait">' + U.esc(U.waitText(s)) + '</div>' +
        '<div class="small muted">' + U.fmt(s.len) + ' queuing, ' + s.open + ' of 12 counters open' + (s.rate > 0 ? ', ' + U.fmt(s.rate, 1) + ' per min' : '') + '</div>' +
        '<div class="counters" aria-label="Counters on island ' + i + '">' + ctr + '</div>' +
        (allocLine.length ? '<div class="small" style="margin-block-end:4px"><span class="muted">Allocated now: </span>' + allocLine.join('; ') + '</div>' : '') +
        '<div class="small"><span class="muted">Checking in now: </span>' + (flights.length ? flights.map(function (f) { return '<span class="mono">' + U.esc(f.code) + '</span> ' + S.clock(f.std); }).join(', ') : 'none') + '</div></div>';
    }
    if (U.can('ci.A')) groups.push('<div class="handler-group"><h3>Handler A, islands A and B</h3><div class="islands">' + card('A') + card('B') + '</div></div>');
    if (U.can('ci.B')) groups.push('<div class="handler-group"><h3>Handler B, islands C and D</h3><div class="islands">' + card('C') + card('D') + '</div></div>');
    if (!U.can('ci.A')) groups.push('<p class="small muted" style="margin-block-start:10px">Handler A\'s islands are not shown for this role.</p>');
    U.html(document.getElementById('islands'), groups.join('') + U.stateLegend());
  }

  function chart(m) {
    var day = U.day, isl = visibleIslands();
    var series = isl.map(function (i) {
      var q = S.QI[ISL[i]], pts = [];
      for (var t = m - 180; t <= m; t++) { var s = day.state(q, t); pts.push([t, s.nowcast == null ? null : Math.min(s.nowcast, 90)]); }
      return { label: 'Island ' + i, endText: i, points: pts, color: U.color(ISL[i]), width: 2 };
    });
    var ymax = 20;
    series.forEach(function (sr) { sr.points.forEach(function (p) { if (p[1] != null) ymax = Math.max(ymax, p[1]); }); });
    QC.line(document.getElementById('ciChart'), {
      title: 'Nowcast per island, last three hours', desc: 'Minutes, one line per island. Synthetic data.', height: 240,
      x: { min: m - 180, max: m, ticks: U.timeTicks(m - 180, m, 30) }, y: { min: 0, max: Math.ceil(ymax / 5) * 5, label: 'Minutes' },
      series: series, hlines: [{ y: 15, label: 'SLA 15 min', color: 'var(--crit)', dash: '6 4', align: 'left' }]
    });
  }

  function bins(m) {
    var day = U.day, isl = visibleIslands(), cur = Math.floor(m / 15) * 15, rows = [];
    for (var bs = cur; bs >= Math.max(0, cur - 165); bs -= 15) {
      var cells = isl.map(function (i) {
        var b = day.bin(S.QI[ISL[i]], bs, m);
        if (b.p90 == null) return '<td><span class="muted small">No passengers</span></td>';
        var breach = b.p90 > 15;
        var ex = U.sla.exclusionsToday().filter(function (x) { return x.zones.indexOf(ISL[i]) >= 0 && x.from < bs + 15 && x.to > bs; })[0];
        return '<td><div class="bin-cell"><strong>' + QC.fmt(b.p90, { decimals: 1 }) + '</strong> ' + (breach ? U.st('crit', 'Breach') : U.st('good', 'Met')) +
          ' <span class="mark ' + (b.status === 'final' ? 'final' : 'prov') + '" title="' + (b.status === 'final' ? 'Final: everyone who entered in this bin has left the queue' : 'Provisional: the bin is open or passengers who entered in it are still queuing') + '">' + (b.status === 'final' ? 'Final' : 'Provisional') + '</span>' +
          (ex ? ' <span class="mark final" title="' + U.esc(ex.reason) + '">Excluded, ' + ex.id + '</span>' : '') + '</div></td>';
      }).join('');
      rows.push('<tr' + (bs === cur ? ' class="row-hl"' : '') + '><th scope="row" class="mono">' + S.clock(bs) + ' to ' + S.clock(bs + 15) + '</th>' + cells + '<td class="small">' + U.profileAt(bs + 14 > m ? m : bs + 14) + '</td></tr>');
    }
    var html = '<div class="table-wrap"><table class="tbl"><thead><tr><th scope="col">Bin</th>' + isl.map(function (i) { return '<th scope="col">Island ' + i + ', P90 min</th>'; }).join('') + '<th scope="col">Profile</th></tr></thead><tbody>' + rows.join('') + '</tbody></table></div>' +
      '<p class="small muted" style="margin-block-start:6px">Arrival-weighted P90 of realised waits per bin; passengers still queuing are counted at their estimated wait until they leave. Handler B\'s contract uses this rule; the same rule is shown for Handler A for comparison.</p>';
    U.html(document.getElementById('bins'), html);
  }

  function update(m) {
    if (U.screenAccess('checkin') === 'none') return;
    kpis(m); islands(m); chart(m); bins(m);
  }

  /* counter allocations */

  var F = U.F;
  function allocList(m) {
    var isl = visibleIslands(), mine = U.createIslands();
    var list = U.list('allocations').filter(function (a) { return isl.indexOf(a.island) >= 0; });
    if (!list.length) return '<p class="small muted">No allocations on islands visible to this role.</p>';
    return '<div class="table-wrap"><table class="tbl stack-sm"><thead><tr><th scope="col">ID</th><th scope="col">Flight</th><th scope="col">Island</th><th scope="col">Counters</th><th scope="col">Window</th><th scope="col">Status now</th><th scope="col">Added by</th><th scope="col">Actions</th></tr></thead><tbody>' +
      list.map(function (a) {
        var live = m >= a.open && m < a.close;
        return '<tr' + U.rowCls(a.id) + '><th scope="row" class="mono">' + a.id + '</th><td class="mono">' + U.esc(a.flight) + '</td><td>' + a.island + '</td><td class="mono">' + a.island + String(a.fromCtr).padStart(2, '0') + (a.toCtr > a.fromCtr ? ' to ' + a.island + String(a.toCtr).padStart(2, '0') : '') + '</td>' +
          '<td class="mono">' + S.clock(a.open) + ' to ' + S.clock(a.close) + '</td><td>' + (live ? U.st('good', 'Open') : m < a.open ? '<span class="small muted">Opens ' + S.clock(a.open) + '</span>' : '<span class="small muted">Closed</span>') + '</td>' +
          '<td class="small">' + U.esc(a.by || '') + '</td><td class="actions">' + (!a.id.match(/^AL-00[1-3]$/) && U.canCreate('allocation') && mine.indexOf(a.island) >= 0 ? '<button type="button" class="btn" data-al-remove="' + a.id + '">Remove</button>' : '') + '</td></tr>';
      }).join('') + '</tbody></table></div>';
  }

  function openAlloc(trig) {
    var islands = U.createIslands(), m = U.now();
    var flights = U.day.S.dep.filter(function (f) { return islands.indexOf(f.island) >= 0 && f.std - 45 > m; });
    if (!flights.length) flights = U.day.S.dep.filter(function (f) { return islands.indexOf(f.island) >= 0; });
    var id = U.nextId('allocation'), f0 = flights[0];
    var html = F.text('aid', 'Allocation ID', id, { readonly: true }) +
      F.select('flight', 'Departing flight', flights.map(function (f) { return [f.code, f.code + ', STD ' + S.clock(f.std) + ', island ' + f.island]; }), f0 ? f0.code : '') +
      F.select('island', 'Island', islands.map(function (i) { return [i, 'Island ' + i + ' (Handler ' + (i < 'C' ? 'A' : 'B') + ')']; }), f0 ? f0.island : islands[0]) +
      F.row(F.select('fromCtr', 'First counter', range12(), 6), F.select('toCtr', 'Last counter', range12(), 8)) +
      F.row(F.text('open', 'Opens', f0 ? S.clock(f0.std - 180) : '', { placeholder: 'HH:MM', req: true }), F.text('close', 'Closes', f0 ? S.clock(f0.std - 45) : '', { placeholder: 'HH:MM', req: true })) +
      F.note('Defaults: open 3 hours before departure, close 45 minutes before. Counters already allocated for an overlapping time are rejected.') +
      '<div class="preview" id="alPrev" aria-live="polite"></div>';
    U.openDrawer({
      title: 'Allocate counters', html: html, trigger: trig, returnFocus: 'alBtn',
      onOpen: function (form) {
        var fl = form.querySelector('#f-flight');
        fl.addEventListener('change', function () {
          var f = U.day.S.dep.filter(function (x) { return x.code === fl.value; })[0];
          if (!f) return;
          form.querySelector('#f-open').value = S.clock(f.std - 180);
          form.querySelector('#f-close').value = S.clock(f.std - 45);
          if (islands.indexOf(f.island) >= 0) form.querySelector('#f-island').value = f.island;
          prev();
        });
        function prev() {
          var v = U.formValues(form), q = S.QI['CI-' + v.island], o = U.V.time(v.open), c = U.V.time(v.close);
          if (o == null || c == null || !(c > o)) { document.getElementById('alPrev').innerHTML = '<strong>Current plan</strong>Enter a valid window.'; return; }
          var lo = 99, hi = 0;
          for (var t = o; t < c; t += 15) { var n = U.day.plan(q, t); lo = Math.min(lo, n); hi = Math.max(hi, n); }
          document.getElementById('alPrev').innerHTML = '<strong>Current plan</strong>Island ' + v.island + ' plans ' + (lo === hi ? lo : lo + ' to ' + hi) + ' counters in this window. Staffed counters become at least the allocated counters.';
        }
        form.addEventListener('change', prev); form.addEventListener('input', prev); prev();
      },
      onSubmit: function (v) {
        var e = {}, o = U.V.time(v.open), c = U.V.time(v.close), a = +v.fromCtr, b = +v.toCtr;
        var f = U.day.S.dep.filter(function (x) { return x.code === v.flight; })[0];
        if (!f) e.flight = 'Choose a departing flight.';
        if (b < a) e.toCtr = 'The last counter must not be before the first.';
        if (o == null) e.open = 'Enter the opening time as HH:MM.';
        if (c == null) e.close = 'Enter the closing time as HH:MM.';
        else if (o != null && !(c > o)) e.close = 'Counters must close after they open.';
        else if (f && c > f.std) e.close = 'Counters must close before departure (STD ' + S.clock(f.std) + ').';
        if (!e.toCtr && o != null && c != null && c > o) {
          var clash = U.list('allocations').filter(function (x) { return x.island === v.island && a <= x.toCtr && b >= x.fromCtr && o < x.close && c > x.open; })[0];
          if (clash) e.fromCtr = 'Counters ' + v.island + String(Math.max(a, clash.fromCtr)).padStart(2, '0') + ' to ' + v.island + String(Math.min(b, clash.toCtr)).padStart(2, '0') + ' are already allocated to ' + clash.flight + ' (' + clash.id + ') from ' + S.clock(clash.open) + ' to ' + S.clock(clash.close) + '.';
        }
        if (Object.keys(e).length) return e;
        U.state.c.allocations.push({ id: id, flight: v.flight, island: v.island, fromCtr: a, toCtr: b, open: o, close: c, by: U.roleName(), at: U.now() });
        U.saveC();
        U.rerun();
        U.created('allocation', id, v.flight + ': counters ' + v.island + String(a).padStart(2, '0') + ' to ' + v.island + String(b).padStart(2, '0') + ', ' + S.clock(o) + ' to ' + S.clock(c), function () { U.removeFrom('allocations', id); U.saveC(); U.rerun(); });
        return null;
      }
    });
  }
  function range12() { var o = []; for (var k = 1; k <= 12; k++) o.push([k, String(k).padStart(2, '0')]); return o; }

  function buildAlloc() {
    var box = document.getElementById('alNew');
    if (U.screenAccess('checkin') === 'none') { box.innerHTML = ''; document.getElementById('alList').innerHTML = U.denied('Counter allocations belong to the airport deployment.'); return; }
    box.innerHTML = U.createBtn('allocation', 'Allocate counters', 'alBtn');
    var b = document.getElementById('alBtn');
    if (!b.disabled) b.addEventListener('click', function () { openAlloc(b); });
    var l = document.getElementById('alList');
    l.__html = null;
    if (!l.__bound) {
      l.addEventListener('click', function (e) {
        var t = e.target.closest('[data-al-remove]');
        if (!t) return;
        var id = t.getAttribute('data-al-remove'), a = U.state.c.allocations.filter(function (x) { return x.id === id; })[0];
        U.removeFrom('allocations', id); U.saveC(); U.audit('Removed', id, 'Counter allocation removed'); U.rerun();
        U.toast('Removed ' + id, { undo: function () { U.state.c.allocations.push(a); U.saveC(); U.audit('Restored', id, 'Counter allocation restored'); U.rerun(); } });
      });
      l.__bound = true;
    }
  }

  QUI.start({ build: function (q) { build(q); buildAlloc(); }, update: function (m) { update(m); if (U.screenAccess('checkin') !== 'none') U.html(document.getElementById('alList'), allocList(m)); } });
})();
