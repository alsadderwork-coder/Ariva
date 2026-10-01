(function () {
  'use strict';
  var U = QUI, S = QSim, QC = QCharts;
  var side = 'A';
  var REASONS = [
    { id: 'face', label: 'Face match below threshold', cit: 0.36, res: 0.30 },
    { id: 'doc', label: 'Document chip or page not read', cit: 0.28, res: 0.26 },
    { id: 'elig', label: 'Document type not eligible', cit: 0.08, res: 0.18 },
    { id: 'manual', label: 'Referred for manual check', cit: 0.20, res: 0.18 },
    { id: 'other', label: 'Other or abandoned', cit: 0.08, res: 0.08 }
  ];

  function lanesOf(sd) { return ['CRW', 'CIT', 'RES', 'VIS', 'EG'].map(function (ln) { return sd + '-' + ln; }); }

  function build() {
    var acc = U.screenAccess('immigration');
    var tabs = document.getElementById('immTabs');
    var stored = U.store.get('imm.tab', 'A');
    side = stored === 'D' ? 'D' : 'A';
    ['kpis', 'lanes', 'desks', 'egBody', 'officerBtn'].forEach(function (id) { var e = document.getElementById(id); e.innerHTML = ''; e.__html = null; });
    if (acc === 'none') {
      tabs.innerHTML = '';
      tabs.hidden = true;
      document.getElementById('lanes').innerHTML = U.denied('Immigration is part of the Border module.');
      document.getElementById('desks').innerHTML = U.denied('Desk data stays in the border deployment.');
      document.getElementById('egBody').innerHTML = U.denied('E-gate data stays in the border deployment.');
      document.getElementById('desksSub').textContent = 'Not shown for this role.';
      return;
    }
    tabs.hidden = false;
    tabs.innerHTML = [['A', 'Arrivals'], ['D', 'Departures']].map(function (t) {
      return '<button type="button" role="tab" id="tab-' + t[0] + '" aria-selected="' + (t[0] === side) + '" tabindex="' + (t[0] === side ? 0 : -1) + '" data-side="' + t[0] + '">' + t[1] + '</button>';
    }).join('');
    tabs.querySelectorAll('[role="tab"]').forEach(function (b) {
      b.addEventListener('click', function () { setSide(b.getAttribute('data-side')); });
      b.addEventListener('keydown', function (e) {
        if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') { e.preventDefault(); var o = side === 'A' ? 'D' : 'A'; setSide(o); document.getElementById('tab-' + o).focus(); }
      });
    });
    if (U.can('imm.desks')) {
      document.getElementById('officerBtn').innerHTML = '<span class="tooltip-host"><button type="button" class="btn" aria-disabled="true" aria-describedby="offTip">' + U.icon('lock') + 'Officer analytics open in AMAN</button>' +
        '<span class="tipbox" role="tooltip" id="offTip">Officer-level data stays in the border system (AMAN). The QMS receives desk-level interval aggregates only: no officer names, IDs or per-officer times.</span></span>';
      document.getElementById('desksSub').textContent = 'State now and service time as 15-minute interval aggregates per desk position. Desk IDs identify positions, not people.';
    } else {
      document.getElementById('desks').innerHTML = U.denied('The desk grid and per-desk service times stay in the border deployment; the airport receives lane-level waits only.');
      document.getElementById('egBody').innerHTML = U.denied('E-gate utilisation and reject reasons stay in the border deployment.');
      document.getElementById('desksSub').textContent = 'Not shown for this role.';
    }
  }

  function setSide(sd) {
    side = sd; U.store.set('imm.tab', sd);
    document.querySelectorAll('#immTabs [role="tab"]').forEach(function (b) {
      var on = b.getAttribute('data-side') === sd;
      b.setAttribute('aria-selected', String(on)); b.tabIndex = on ? 0 : -1;
    });
    ['lanes', 'desks', 'egBody', 'kpis'].forEach(function (id) { document.getElementById(id).__html = null; });
    U.draw(true);
  }

  function kpis(m) {
    var day = U.day, ids = lanesOf(side), worst = null, wq = null, tot = 0, over = 0;
    ids.forEach(function (id) {
      var s = day.state(S.QI[id], m);
      tot += s.len;
      var v = s.degraded && s.band ? (s.band[0] + s.band[1]) / 2 : s.nowcast;
      if (v != null && v > 15) over++;
      if (v != null && (worst == null || v > worst)) { worst = v; wq = id; }
    });
    var ws = wq ? day.state(S.QI[wq], m) : null;
    var lv = U.waitLevel(worst);
    var t = [U.kpi({ hero: true, level: lv === 'good' ? '' : lv, label: 'Longest lane wait, ' + (side === 'A' ? 'arrivals' : 'departures'), value: ws ? U.esc(U.waitText(ws)) : 'n/a', sub: wq ? U.esc(U.shortName(wq)) + ' &nbsp; ' + U.waitSt(ws) : '' }),
      U.kpi({ label: 'People queuing', value: U.fmt(tot), sub: over ? over + ' lane' + (over > 1 ? 's' : '') + ' above 15 min' : 'No lane above 15 min' })];
    if (U.can('imm.desks')) {
      var open = 0, paused = 0;
      ids.slice(0, 4).forEach(function (id) { var s = day.state(S.QI[id], m); open += s.open; paused += s.paused; });
      t.push(U.kpi({ label: 'Desks staffed', value: open + ' <small>of 22</small>', sub: paused + ' paused now' }));
      var eg = day.egate(side, m);
      t.push(U.kpi({ label: 'E-gates in service', value: eg.inService + ' <small>of ' + eg.total + '</small>', sub: 'Utilisation ' + Math.round(eg.util * 100) + '% in the last 60 min' }));
    }
    U.html(document.getElementById('kpis'), t.join(''));
  }

  function lanes(m) {
    var day = U.day, full = U.can('imm.desks');
    var bs = Math.floor(m / 15) * 15 - 15;
    var rows = lanesOf(side).map(function (id) {
      var q = S.QI[id], s = day.state(q, m), def = S.QUEUES[q];
      var b = day.bin(q, bs, m);
      var binTxt = b.p90 == null ? '<span class="muted">No passengers</span>' : U.fmtWait(b.p90) + ' <span class="mark ' + (b.status === 'final' ? 'final' : 'prov') + '">' + (b.status === 'final' ? 'Final' : 'Provisional') + '</span>';
      return '<tr><th scope="row">' + U.esc(def.label) + '</th><td class="num">' + U.esc(U.waitText(s)) + '</td><td>' + U.waitSt(s) + '</td><td class="num">' + U.fmt(s.len) + '</td>' +
        (full ? '<td class="num">' + s.open + ' of ' + def.servers.length + (s.paused ? ' <span class="muted small">(' + s.paused + ' paused)</span>' : '') + '</td><td class="num">' + (s.rate > 0 ? U.fmt(s.rate, 1) : '0') + '</td>' : '') +
        '<td>' + binTxt + '</td><td><span class="spk" data-q="' + id + '"></span></td></tr>';
    });
    var html = '<div class="table-wrap"><table class="tbl"><thead><tr><th scope="col">Lane</th><th scope="col" class="num">Wait now</th><th scope="col">Status</th><th scope="col" class="num">Queuing</th>' +
      (full ? '<th scope="col" class="num">Open</th><th scope="col" class="num">Throughput per min</th>' : '') +
      '<th scope="col">P90, bin ' + S.clock(bs) + ' to ' + S.clock(bs + 15) + '</th><th scope="col">Last 2 hours</th></tr></thead><tbody>' + rows.join('') + '</tbody></table></div>' +
      (full ? '' : '<p class="small muted" style="margin-block-start:6px">Aggregate view: lane waits and queue lengths from the border feed. Desk counts, service times and e-gate data stay in the border deployment.</p>');
    var host = document.getElementById('lanes');
    host.innerHTML = html;
    host.querySelectorAll('.spk').forEach(function (sp) {
      var q = S.QI[sp.getAttribute('data-q')], vals = [];
      for (var t = m - 120; t <= m; t += 4) { var st = day.state(q, t); vals.push(st.nowcast == null ? 0 : Math.min(st.nowcast, 60)); }
      QC.sparkline(sp, vals, { width: 110, height: 24, min: 0, title: 'Nowcast over the last two hours' });
    });
  }

  function desks(m) {
    if (!U.can('imm.desks')) return;
    var day = U.day, bs = Math.floor(m / 15) * 15;
    var counts = { serving: 0, idle: 0, paused: 0, closed: 0, unknown: 0 };
    var cards = [];
    lanesOf(side).slice(0, 4).forEach(function (id) {
      var q = S.QI[id], def = S.QUEUES[q];
      var sv = day.servers(q, m), cur = day.serverInterval(q, bs, m), prev = day.serverInterval(q, bs - 15, bs - 1);
      sv.forEach(function (s, k) {
        counts[s.state] = (counts[s.state] || 0) + 1;
        var c = cur[k], p = prev[k];
        cards.push('<div class="desk"><div class="desk-top"><span class="desk-id">' + s.id + '</span><span class="small muted">' + U.esc(U.shortName(id)) + '</span></div>' +
          '<div style="margin-block-start:4px">' + U.stateChip(s.state) + '</div>' +
          '<dl><dt>Now</dt><dd>' + (c.minutes ? U.fmt(c.pax) + ' pax' + (c.svc ? ', ' + U.fmt(c.svc) + ' s' : '') : 'Closed') + '</dd>' +
          '<dt>Previous</dt><dd>' + (p.minutes ? U.fmt(p.pax) + ' pax' + (p.svc ? ', ' + U.fmt(p.svc) + ' s' : '') : 'Closed') + '</dd></dl></div>');
      });
    });
    var open = 22 - counts.closed;
    var head = '<p class="small" style="margin-block-end:8px">' + open + ' of 22 desks open: ' + counts.serving + ' serving, ' + counts.idle + ' idle, ' + counts.paused + ' paused' + (counts.unknown ? ', ' + counts.unknown + ' unknown (terminal not reporting)' : '') +
      '. Per desk position: passengers processed and mean service time in the current interval (Now, since ' + S.clock(bs) + ') and the previous one (' + S.clock(bs - 15) + ' to ' + S.clock(bs) + ').</p>';
    U.html(document.getElementById('desks'), head + '<div class="desk-grid">' + cards.join('') + '</div>' + U.stateLegend());
  }

  function egates(m) {
    if (!U.can('imm.egates')) return;
    var day = U.day, eg = day.egate(side, m, 60);
    var egDay = day.egate(side, m, m + 1);
    var gates = eg.servers.map(function (g) {
      var txt = g.state === 'oos' ? 'Out of service (' + S.SCRIPT.egateOos.note.toLowerCase() + ')' : U.stateText(g.state);
      return '<span class="tag"><span class="mono">' + g.id + '</span>' + U.glyph(g.state).replace('<svg', '<svg style="width:12px;height:12px"') + U.esc(txt) + '</span>';
    }).join(' ');
    var rej = egDay.rejects, cit = rej * 35 / 55, res = rej * 20 / 55;
    var reasonRows = REASONS.map(function (r) {
      var c = cit * r.cit, rr = res * r.res;
      return '<tr><th scope="row">' + U.esc(r.label) + '</th><td class="num">' + U.fmt(c) + '</td><td class="num">' + U.fmt(rr) + '</td><td class="num">' + U.fmt(c + rr) + '</td></tr>';
    }).join('');
    var exp = day.expected(), qEG = S.QI[side + '-EG'], next = 0;
    for (var t = m + 1; t <= m + 60; t++) next += exp.A[qEG][t + S.PRE];
    var extra = next * S.PARAMS.egateReject, deskMin = extra * S.PARAMS.svc.VIS / 60;
    var html = '<div class="kpis" style="margin-block-end:12px">' +
      U.kpi({ label: 'In service', value: eg.inService + ' <small>of ' + eg.total + '</small>', sub: 'Cycle ' + S.PARAMS.svc.EG + ' s per passenger' }) +
      U.kpi({ label: 'Utilisation, last 60 min', value: Math.round(eg.util * 100) + '%', sub: U.fmt(eg.processed) + ' passengers processed' }) +
      U.kpi({ label: 'Reject rate, last 60 min', value: QC.fmt(eg.rejectRate * 100, { decimals: 1 }) + '%', sub: U.fmt(eg.rejects) + ' rejected to the Visitors lane' }) +
      U.kpi({ label: 'Extra manual load, next 60 min', value: '+' + U.fmt(extra), sub: 'passengers, about ' + U.fmt(deskMin) + ' desk-minutes at ' + S.PARAMS.svc.VIS + ' s' }) +
      '</div><div class="row" style="margin-block-end:12px">' + gates + '</div>' +
      '<div class="split"><div class="wide"><h3 style="margin-block-end:6px">Reject reasons by category, today to ' + S.clock(m) + '</h3><div class="table-wrap"><table class="tbl"><thead><tr><th scope="col">Reason</th><th scope="col" class="num">Citizens</th><th scope="col" class="num">Residents</th><th scope="col" class="num">Total</th></tr></thead><tbody>' + reasonRows +
      '</tbody><tfoot><tr><th scope="row">Total</th><td class="num">' + U.fmt(cit) + '</td><td class="num">' + U.fmt(res) + '</td><td class="num">' + U.fmt(rej) + '</td></tr></tfoot></table></div></div>' +
      '<div><h3 style="margin-block-end:6px">Predicted extra load on manual desks</h3><p class="small">About ' + U.fmt(next) + ' e-gate eligible passengers are expected in the next 60 minutes. At a ' + Math.round(S.PARAMS.egateReject * 100) + '% reject rate, about ' + U.fmt(extra) + ' of them join the Visitors lane, which needs about ' + QC.fmt(deskMin / 60, { decimals: 1 }) + ' extra desk-hours.</p>' +
      '<p class="small muted">Reject reasons split by a synthetic reason mix. Gate vendors show their own gates only; here rejects are joined to the manual queue they create.</p></div></div>';
    U.html(document.getElementById('egBody'), html);
  }

  function update(m) {
    if (U.screenAccess('immigration') === 'none') return;
    kpis(m); lanes(m); desks(m); egates(m);
  }

  QUI.start({ build: build, update: update });
})();
