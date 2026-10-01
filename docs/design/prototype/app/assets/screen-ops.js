(function () {
  'use strict';
  var U = QUI, S = QSim, QC = QCharts;
  var fp = null, fpProfile = null, chartQ = null, lastAlertsKey = '';

  function chartQueues() {
    return S.QUEUES.filter(function (d) { return U.queueVis(d.id) !== 'hidden'; });
  }
  function defaultChartQ() {
    var r = U.role();
    return r === 'tdm' || r === 'hbm' ? 'CI-C' : 'A-VIS';
  }

  function build() {
    var role = U.role();
    var lvStored = U.store.get('fp.level.' + role, null);
    var level = lvStored === 'arr' || lvStored === 'dep' ? lvStored : (role === 'tdm' || role === 'hbm' ? 'dep' : 'arr');
    fp = QFloor.create(document.getElementById('fp'), { mode: 'live', level: level, onLevel: function (lv) { U.store.set('fp.level.' + role, lv); } });
    fp.summary.innerHTML = '<details class="small" style="margin-block-start:8px"><summary>Text summary of the floor plan (zones visible to this role)</summary><div class="table-wrap"><table class="tbl"><thead><tr><th scope="col">Zone</th><th scope="col" class="num">Wait</th><th scope="col" class="num">Queuing</th><th scope="col">Status</th></tr></thead><tbody id="fpSumBody"></tbody></table></div></details>';
    fpProfile = null;

    var qs = chartQueues();
    var stored = U.store.get('chart.zone', null);
    chartQ = qs.some(function (d) { return d.id === stored; }) ? stored : defaultChartQ();
    if (!qs.some(function (d) { return d.id === chartQ; })) chartQ = qs.length ? qs[0].id : null;
    var pick = document.getElementById('chartPick');
    pick.innerHTML = '<label class="field" for="chartSel">Zone <select id="chartSel">' + qs.map(function (d) {
      var agg = U.queueVis(d.id) === 'agg';
      return '<option value="' + d.id + '"' + (d.id === chartQ ? ' selected' : '') + '>' + U.esc(d.name) + (agg ? ' (aggregate)' : '') + '</option>';
    }).join('') + '</select></label>';
    document.getElementById('chartSel').addEventListener('change', function (e) { chartQ = e.target.value; U.store.set('chart.zone', chartQ); U.draw(true); });

    var alerts = document.getElementById('alerts');
    alerts.__html = null;
    if (!alerts.__bound) {
      alerts.addEventListener('click', function (e) {
        var b = e.target.closest('[data-ack]');
        if (b) U.ack(b.getAttribute('data-ack'));
      });
      alerts.__bound = true;
    }
    lastAlertsKey = '';

    var wb = document.getElementById('waveBody');
    wb.__html = null;
    if (!U.can('flights.arr')) {
      wb.innerHTML = U.denied('The arrival wave comes from inbound flights and border API data.');
      document.getElementById('waveSub').textContent = 'Not shown for this role.';
    } else {
      document.getElementById('waveSub').textContent = U.can('imm.api') ? 'Passengers by lane from API data, and the predicted arrival curve at the immigration hall.' : 'Flights and passenger totals from load messages. The lane split comes from border API data and stays in the border deployment.';
      wb.innerHTML = '<div id="waveCallout"></div><div class="split" style="margin-block-start:10px"><div class="wide"><div class="table-wrap" id="waveTable"></div></div><div><div id="waveChart" class="chart"></div></div></div>';
    }
  }

  function kpis(m) {
    var day = U.day, role = U.role(), tiles = [];
    var worst = null, wq = null, total = 0, zones = 0;
    S.QUEUES.forEach(function (d) {
      var v = U.queueVis(d.id);
      if (v === 'hidden') return;
      var s = day.state(d.index, m);
      total += s.len; zones++;
      var val = s.degraded && s.band ? (s.band[0] + s.band[1]) / 2 : s.nowcast;
      if (val != null && (worst == null || val > worst)) { worst = val; wq = d; }
    });
    var ws = wq ? day.state(wq.index, m) : null;
    var lv = U.waitLevel(worst);
    tiles.push(U.kpi({ hero: true, level: lv === 'good' ? '' : lv, label: 'Longest current wait',
      value: ws ? U.esc(U.waitText(ws)) : 'n/a',
      sub: wq ? U.esc(wq.name) + (U.queueVis(wq.id) === 'agg' ? ' (aggregate)' : '') + ' &nbsp; ' + U.waitSt(ws) : '' }));
    tiles.push(U.kpi({ label: 'People queuing', value: U.fmt(total), sub: 'across ' + zones + ' zones visible to this role' }));
    function openOf(ids) { var o = 0, p = 0; ids.forEach(function (id) { var s = day.state(S.QI[id], m); o += s.open; p += s.paused; }); return { open: o, paused: p }; }
    var immArr = ['A-CRW', 'A-CIT', 'A-RES', 'A-VIS'], immDep = ['D-CRW', 'D-CIT', 'D-RES', 'D-VIS'];
    if (role === 'demo' || role === 'bss') {
      var a = openOf(immArr), dd = openOf(immDep);
      tiles.push(U.kpi({ label: 'Immigration desks staffed', value: (a.open + dd.open) + ' <small>of 44</small>', sub: 'Arrivals ' + a.open + ', departures ' + dd.open + '; ' + (a.paused + dd.paused) + ' paused' }));
      var ea = day.egate('A', m), ed = day.egate('D', m);
      var proc = ea.processed + ed.processed, rej = ea.rejects + ed.rejects;
      tiles.push(U.kpi({ label: 'E-gates in use', value: (ea.inService + ed.inService) + ' <small>of 10</small>', sub: 'Reject rate ' + (proc > 0 ? QC.fmt(rej / proc * 100, { decimals: 1 }) : '0.0') + '% in the last 60 min' }));
    }
    if (role === 'demo' || role === 'tdm') {
      var c = openOf(['CI-A', 'CI-B', 'CI-C', 'CI-D']);
      tiles.push(U.kpi({ label: 'Check-in counters staffed', value: c.open + ' <small>of 48</small>', sub: 'Handler A ' + openOf(['CI-A', 'CI-B']).open + ', Handler B ' + openOf(['CI-C', 'CI-D']).open }));
    }
    if (role === 'tdm') {
      var sc = openOf(['SEC-N', 'SEC-S']);
      tiles.push(U.kpi({ label: 'Security lanes open', value: sc.open + ' <small>of 10</small>', sub: 'North ' + openOf(['SEC-N']).open + ', South ' + openOf(['SEC-S']).open }));
    }
    if (role === 'hbm') {
      var hb = openOf(['CI-C', 'CI-D']);
      tiles.push(U.kpi({ label: 'Counters staffed, Handler B', value: hb.open + ' <small>of 24</small>', sub: 'Island C ' + openOf(['CI-C']).open + ', island D ' + openOf(['CI-D']).open }));
      var bs = Math.floor(m / 15) * 15, bw = null, bi = null;
      ['CI-C', 'CI-D'].forEach(function (id) { var b = day.bin(S.QI[id], bs, m); if (b.p90 != null && (bw == null || b.p90 > bw)) { bw = b.p90; bi = id; } });
      tiles.push(U.kpi({ label: 'SLA, current bin at P90', value: bw == null ? 'n/a' : U.fmtWait(bw), sub: 'Bin ' + S.clock(bs) + ' to ' + S.clock(bs + 15) + (bi ? ', ' + U.shortName(bi).toLowerCase() : '') + ', provisional' }));
    }
    U.html(document.getElementById('kpis'), tiles.join(''));
  }

  function chart(m, fc) {
    var host = document.getElementById('waitChart');
    if (!chartQ) { host.innerHTML = ''; return; }
    var q = S.QI[chartQ], day = U.day;
    var ser = day.waitSeries(q, m - 120, m, m);
    var fin = [], prov = [], lastFinal = null;
    ser.forEach(function (p) {
      if (p.final) { fin.push([p.m, p.w]); prov.push([p.m, null]); lastFinal = p; }
      else { fin.push([p.m, null]); prov.push([p.m, p.w]); }
    });
    if (lastFinal) {
      for (var i = 0; i < prov.length; i++) if (prov[i][0] === lastFinal.m + 1 && prov[i][1] != null) { prov[i - 1] = [lastFinal.m, lastFinal.w]; break; }
    }
    var s = day.state(q, m);
    var f = fc.q[q];
    var p50 = [], band = [];
    var start = s.nowcast != null ? s.nowcast : null;
    if (start != null) { p50.push([m, start]); band.push([m, start, start]); }
    for (var h = 0; h < fc.horizon; h++) { p50.push([m + h + 1, f.p50[h]]); band.push([m + h + 1, f.p50[h], f.p90[h]]); }
    var ymax = 20;
    fin.concat(prov, p50).forEach(function (p) { if (p[1] != null) ymax = Math.max(ymax, p[1]); });
    band.forEach(function (b) { ymax = Math.max(ymax, b[2]); });
    ymax = Math.min(ymax, 90);
    var markers = [];
    if (s.nowcast != null) markers.push({ x: m, y: Math.min(s.nowcast, ymax), color: 'var(--c1)' });
    var d = S.QUEUES[q];
    QC.line(host, {
      title: 'Wait for ' + d.name, desc: 'Realised wait for the last two hours, nowcast now, and forecast P50 and P90 for the next two hours. Synthetic data.',
      height: 250,
      x: { min: m - 120, max: m + 120, ticks: U.timeTicks(m - 120, m + 120, 30) },
      y: { min: 0, max: Math.ceil(ymax / 5) * 5, label: 'Minutes' },
      series: [
        { label: 'Realised', points: fin, color: 'var(--c1)', width: 2, endLabel: false },
        { label: 'Provisional', points: prov, color: 'var(--c1)', width: 2, dash: '2 3', endLabel: false },
        { label: 'P50', points: p50, color: 'var(--c1)', width: 2, dash: '6 4', endText: 'P50' }
      ],
      bands: [{ points: band, label: 'P90', opacity: 0.22 }],
      hlines: [{ y: 15, label: 'Target 15 min', color: 'var(--crit)', dash: '6 4', align: 'left' }],
      vlines: [{ x: m, label: 'Now ' + S.clock(m) + ', nowcast ' + (s.noService ? 'none' : s.degraded && s.band ? U.bandText(s.band) : U.fmtWait(s.nowcast)), dash: '2 3' }],
      markers: markers
    });
    var note = U.queueVis(chartQ) === 'agg' ? 'Lane-level aggregate from the border feed. ' : '';
    if (s.noService) note += 'No desk is serving this zone right now, so there is no nowcast. ';
    if (s.degraded) note += 'A sensor over this zone is offline: the nowcast is shown as a band. ';
    document.getElementById('chartNote').textContent = note + 'Forecast: ' + fc.runs + ' seeded Monte Carlo runs from the current state. Zone profile ' + U.profileAt(m) + '.';
  }

  function alertsHtml(m) {
    var list = U.alertsAt(m).filter(function (a) { return a.raisedAt >= m - 360; });
    var open = list.filter(function (a) { return a.status !== 'cleared'; }).sort(function (a, b) { return b.raisedAt - a.raisedAt; });
    var cleared = list.filter(function (a) { return a.status === 'cleared'; }).sort(function (a, b) { return b.clearedAt - a.clearedAt; }).slice(0, 4);
    var all = open.concat(cleared);
    if (!all.length) return '<p class="muted small">No alerts in the last six hours for this role.</p>';
    return '<ul class="alerts">' + all.map(function (a) {
      var sev = a.severity === 'critical' ? U.st('crit', 'Critical') : U.st('warn', 'Warning');
      var stat;
      if (a.status === 'cleared') stat = U.st('good', 'Cleared at ' + S.clock(a.clearedAt));
      else if (a.status === 'acknowledged') stat = U.st('good', 'Acknowledged at ' + S.clock(a.ack.at) + ' by ' + a.ack.role);
      else if (a.status === 'escalated') stat = U.st('crit', 'Escalated to ' + a.escalateTo + ' at ' + S.clock(a.escAt));
      else stat = '<span class="small">Escalates to ' + U.esc(a.escalateTo) + ' in <span class="countdown">' + a.remaining + ' min</span></span>';
      var btn = '';
      if (a.status === 'active' || a.status === 'escalated') {
        btn = U.canAck(a) ? '<button type="button" class="btn" data-ack="' + a.id + '" data-key="ack-' + a.id + '">' + U.icon('check') + 'Acknowledge</button>' : '<span class="small muted">Owned by ' + U.esc(a.owner) + '</span>';
      }
      return '<li class="alert sev-' + a.severity + (a.status === 'cleared' ? ' is-cleared' : '') + '">' +
        '<div class="alert-top"><span class="alert-rule">' + U.esc(a.rule) + ' <span class="mono small muted">' + U.esc(a.ruleId || '') + '</span></span><span class="mono small">' + S.clock(a.raisedAt) + '</span></div>' +
        '<div class="alert-meta">' + sev + ' &nbsp; ' + U.esc(a.text) + '<br>Owner: ' + U.esc(a.owner) + (a.email ? '<br>Email to ' + U.esc(a.email) + ' (not sent in the demo)' : '') + '</div>' +
        '<div class="alert-actions">' + btn + stat + '</div></li>';
    }).join('') + '</ul>';
  }

  function wave(m, fc) {
    if (!U.can('flights.arr')) return;
    var day = U.day, lanes = U.can('imm.api');
    var fl = day.flightsArriving(m, m + 30);
    var tb = document.getElementById('waveTable');
    var rows = fl.map(function (x) {
      var f = x.f;
      var cells = '<th scope="row" class="mono">' + U.esc(f.code) + '</th><td class="mono">' + S.clock(f.eibt) + '</td><td class="num">' + U.fmt(f.pax) + '</td>';
      if (lanes) cells += ['CIT', 'RES', 'VIS', 'CRW', 'EG'].map(function (ln) { return '<td class="num">' + U.fmt(x.lanes[ln]) + '</td>'; }).join('');
      return '<tr>' + cells + '</tr>';
    });
    var tot = { pax: 0, CIT: 0, RES: 0, VIS: 0, CRW: 0, EG: 0 };
    fl.forEach(function (x) { tot.pax += x.f.pax; ['CIT', 'RES', 'VIS', 'CRW', 'EG'].forEach(function (ln) { tot[ln] += x.lanes[ln]; }); });
    var head = '<tr><th scope="col">Flight</th><th scope="col">On-block (est.)</th><th scope="col" class="num">Passengers</th>' + (lanes ? '<th scope="col" class="num">Citizens</th><th scope="col" class="num">Residents</th><th scope="col" class="num">Visitors</th><th scope="col" class="num">Crew</th><th scope="col" class="num">E-gate</th>' : '') + '</tr>';
    var foot = fl.length ? '<tfoot><tr><th scope="row">Total</th><td></td><td class="num">' + U.fmt(tot.pax) + '</td>' + (lanes ? ['CIT', 'RES', 'VIS', 'CRW', 'EG'].map(function (ln) { return '<td class="num">' + U.fmt(tot[ln]) + '</td>'; }).join('') : '') + '</tr></tfoot>' : '';
    U.html(tb, fl.length ? '<table class="tbl"><thead>' + head + '</thead><tbody>' + rows.join('') + '</tbody>' + foot + '</table>' + (lanes ? '<p class="small muted" style="margin-block-start:6px">Manual lanes after e-gate eligible passengers are taken out; transfer passengers stay airside. Crew are the flights\' crew and diplomats.</p>' : '') : '<p class="muted small">No flights on-block in the next 30 minutes.</p>');

    var curve = day.hallCurve(fl, m, 60);
    var xs = [];
    for (var k = 0; k < 60; k++) xs.push(m + k);
    var series = lanes ? [
      { label: 'Visitors', values: curve.VIS, color: U.color('A-VIS') },
      { label: 'Citizens', values: curve.CIT, color: U.color('A-CIT') },
      { label: 'Residents', values: curve.RES, color: U.color('A-RES') },
      { label: 'E-gate', values: curve.EG, color: U.color('A-EG') },
      { label: 'Crew', values: curve.CRW, color: U.color('A-CRW') }
    ] : [{ label: 'All passengers', values: xs.map(function (_, i) { return curve.VIS[i] + curve.CIT[i] + curve.RES[i] + curve.EG[i] + curve.CRW[i]; }), color: 'var(--c1)' }];
    QC.stackedArea(document.getElementById('waveChart'), {
      title: 'Predicted arrivals at the immigration hall, next 60 minutes', desc: 'Passengers per minute from flights landing in the next 30 minutes. Synthetic.',
      height: 220, xs: xs, x: { ticks: U.timeTicks(m, m + 59, 15) }, y: { label: 'Passengers per minute' }, series: series
    });

    var co = document.getElementById('waveCallout');
    var html;
    if (lanes && fc && fc.q[S.QI['A-VIS']]) {
      var f = fc.q[S.QI['A-VIS']], cross = null;
      var now = day.state(S.QI['A-VIS'], m);
      for (var h = 0; h < 45; h++) if (f.p50[h] > 15) { cross = m + h + 1; break; }
      var visTot = Math.round(tot.VIS);
      if (now.nowcast != null && now.nowcast > 15) html = '<div class="callout">' + U.icon('warn') + '<div><strong>Wave in progress.</strong> The arrivals Visitors lane is above 15 minutes now (' + U.esc(U.waitText(now)) + '). ' + (visTot ? U.fmt(visTot) + ' more Visitors land in the next 30 minutes.' : '') + '</div></div>';
      else if (cross) html = '<div class="callout">' + U.icon('warn') + '<div><strong>Arrival-wave alert.</strong> ' + U.fmt(tot.pax) + ' passengers on ' + fl.length + ' flights land in the next 30 minutes, ' + U.fmt(visTot) + ' of them Visitors. Predicted Visitors wait passes 15 minutes at about <span class="mono">' + S.clock(cross) + '</span> (P50). Open Visitors desks before the wave. <a href="forecast.html#staffing">See the recommendation</a></div></div>';
      else html = '<div class="callout info">' + U.icon('info') + '<div>No Visitors wait above 15 minutes predicted in the next 45 minutes (P50).</div></div>';
    } else {
      html = '<div class="callout info">' + U.icon('info') + '<div>' + U.fmt(tot.pax) + ' passengers on ' + fl.length + ' flights in the next 30 minutes. Lane mix and the wave alert are border functions.</div></div>';
    }
    U.html(co, html);
  }

  function update(m) {
    var day = U.day;
    var ctx = U.fpContext(m);
    var prof = U.profileAt(m);
    if (fpProfile !== prof) { fp.render(ctx); fpProfile = prof; } else fp.update(ctx);
    fp.headExtra.innerHTML = '<span class="tag">Zone profile <strong>' + prof + '</strong></span>';
    var sb = document.getElementById('fpSumBody');
    if (sb) {
      var rows = [];
      S.QUEUES.forEach(function (d) {
        var v = U.queueVis(d.id);
        if (v === 'hidden') return;
        var s = day.state(d.index, m);
        rows.push('<tr><th scope="row">' + U.esc(d.name) + (v === 'agg' ? ' <span class="muted small">(aggregate)</span>' : '') + '</th><td class="num">' + U.esc(U.waitText(s)) + '</td><td class="num">' + U.fmt(s.len) + '</td><td>' + U.waitSt(s) + '</td></tr>');
      });
      U.html(sb, rows.join(''));
    }
    kpis(m);
    var want = [];
    if (chartQ) want.push(S.QI[chartQ]);
    if (U.can('imm.api') && want.indexOf(S.QI['A-VIS']) < 0) want.push(S.QI['A-VIS']);
    var fc = want.length ? day.forecast(m, { queues: want }) : null;
    if (fc) chart(m, fc);
    U.html(document.getElementById('alerts'), alertsHtml(m));
    wave(m, fc);
  }

  QUI.start({ build: build, update: update });
})();
