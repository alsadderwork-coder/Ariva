(function () {
  'use strict';
  var U = QUI, S = QSim, QC = QCharts;
  var cacheKey = '', data = null, F = U.F;
  var IMM = ['A-CIT', 'A-RES', 'A-VIS', 'A-EG', 'A-CRW', 'D-CIT', 'D-RES', 'D-VIS', 'D-EG', 'D-CRW'];
  var AIR = ['CI-A', 'CI-B', 'CI-C', 'CI-D', 'SEC-N', 'SEC-S'];
  var TPL = { daily: 'Daily peaks', weekly: 'Weekly service standard', sla: 'Monthly SLA and penalties', device: 'Device health' };
  var SCOPE = { all: 'Whole airport', border: 'Border view: immigration', airport: 'Airport view: check-in, security, border aggregates', hb: 'Handler B view: islands C and D' };
  var tpl = 'daily', scope = null, ranFrom = null;

  function ownScope() { return { demo: 'all', bss: 'border', tdm: 'airport', hbm: 'hb' }[U.role()]; }
  function scopeOptions() { return { demo: ['all', 'border', 'airport', 'hb'], bss: ['border'], tdm: ['airport', 'hb'], hbm: ['hb'] }[U.role()]; }
  function inScope(id, sc) {
    sc = sc || scope;
    if (sc === 'border') return IMM.indexOf(id) >= 0;
    if (sc === 'hb') return id === 'CI-C' || id === 'CI-D';
    return true;
  }
  function tplAllowed(t, sc) {
    if (t === 'sla') return U.can('sla') && sc !== 'border';
    if (t === 'device') return U.screenAccess('devices') !== 'none' && sc !== 'hb';
    return true;
  }
  function vis(id) { return U.queueVis(id) !== 'hidden' && inScope(id); }
  function serversVisible(id) {
    var d = S.QUEUES[S.QI[id]];
    if (d.group === 'imm' || d.group === 'egate') return U.can('imm.desks');
    return U.queueVis(id) === 'full';
  }

  function hourly(q, now) {
    var out = [];
    for (var h = 0; h * 60 <= now; h++) {
      var to = Math.min(h * 60 + 60, now + 1);
      out.push(U.day.p90(q, h * 60, to, now));
    }
    return out;
  }

  function dayAheadHourly(q, now) {
    var da = U.day.dayAhead(), out = [];
    for (var h = 0; h * 60 <= now; h++) {
      var smp = [], pax = 0;
      for (var m = h * 60; m < Math.min(h * 60 + 60, now + 1); m++) { var a = da.A[q][m + S.PRE]; if (a > 1e-6) { smp.push([da.W[q][m + S.PRE], a]); pax += a; } }
      out.push(pax >= 0.5 ? U.day.quantile(smp, 0.9) : null);
    }
    return out;
  }

  function compute(m) {
    var d = { m: m, rows: {}, peaks: [] };
    IMM.concat(AIR).forEach(function (id) {
      if (!vis(id)) return;
      var q = S.QI[id], hrs = hourly(q, m), best = null, bh = null;
      hrs.forEach(function (x, h) { if (x.p90 != null && x.pax >= 20 && (best == null || x.p90 > best.p90 + 1e-9 || (Math.abs(x.p90 - best.p90) < 1e-9 && x.pax > best.pax))) { best = x; bh = h; } });
      d.rows[id] = { hours: hrs, peak: best, peakHour: bh, dayP90: U.day.p90(q, 0, m + 1, m) };
    });
    return d;
  }

  function build() {
    cacheKey = '';
    U.sla.loadDisputes();
    if (!scope || scopeOptions().indexOf(scope) < 0) { scope = ownScope(); ranFrom = null; }
    if (!tplAllowed(tpl, scope)) tpl = 'daily';
    document.getElementById('rpNew').innerHTML = U.createBtn('report', 'New scheduled report', 'rpBtn');
    var rb = document.getElementById('rpBtn');
    if (!rb.disabled) rb.addEventListener('click', function () { openReport(rb); });
    var rl = document.getElementById('rpList');
    rl.__html = null;
    if (!rl.__bound) { rl.addEventListener('click', onScheduleClick); rl.__bound = true; }
    var tabs = document.getElementById('tplTabs');
    tabs.innerHTML = tabsHtml();
    if (!tabs.__bound) {
      tabs.addEventListener('click', function (e) { var b = e.target.closest('[data-tpl]'); if (b) setTpl(b.getAttribute('data-tpl'), null, null); });
      tabs.addEventListener('keydown', function (e) {
        if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
        var bs = Array.prototype.slice.call(tabs.querySelectorAll('[role="tab"]')), i = bs.indexOf(document.activeElement);
        if (i < 0) return;
        e.preventDefault();
        var n = bs[(i + (e.key === 'ArrowRight' ? 1 : bs.length - 1)) % bs.length];
        setTpl(n.getAttribute('data-tpl'), null, null);
        var f = document.getElementById(n.id); if (f) f.focus();
      });
      tabs.__bound = true;
    }
    var so = scopeOptions(), sw = document.getElementById('scopeWrap');
    sw.innerHTML = so.length > 1 ? '<label class="field" for="scopeSel">Scope <select id="scopeSel">' + so.map(function (o) { return '<option value="' + o + '"' + (o === scope ? ' selected' : '') + '>' + U.esc(SCOPE[o]) + '</option>'; }).join('') + '</select></label>' : '<span class="small muted">' + U.esc(SCOPE[scope]) + '</span>';
    var ss = document.getElementById('scopeSel');
    if (ss) ss.addEventListener('change', function () { scope = ss.value; if (!tplAllowed(tpl, scope)) tpl = 'daily'; setTpl(tpl, scope, null); });
    var btns = document.getElementById('expBtns');
    var framed = false;
    try { framed = window.self !== window.top; } catch (e) { framed = true; }
    btns.innerHTML = '<button type="button" class="btn btn-primary" id="csvBtn">' + U.icon('download') + 'Export CSV</button>' +
      (framed ? '<span class="print-note">Printing is available when this page is opened directly rather than inside an embedded viewer. Open the file in a browser tab, then use Print or save as PDF.</span>'
        : '<button type="button" class="btn" id="printBtn">' + U.icon('print') + 'Print or save as PDF</button>');
    document.getElementById('csvBtn').addEventListener('click', exportCsv);
    var pb = document.getElementById('printBtn');
    if (pb) pb.addEventListener('click', function () { window.print(); });
    document.getElementById('csvPanel').innerHTML = '';
  }

  function peakTable(ids, d) {
    return '<div class="table-wrap"><table class="tbl"><thead><tr><th scope="col">Zone</th><th scope="col">Peak hour</th><th scope="col" class="num">P90 in peak hour, min</th><th scope="col" class="num">Passengers in peak hour</th><th scope="col" class="num">P90 today, min</th><th scope="col">Status at peak</th></tr></thead><tbody>' +
      ids.filter(function (id) { return d.rows[id]; }).map(function (id) {
        var r = d.rows[id], p = r.peak;
        return '<tr><th scope="row">' + U.esc(U.qname(id)) + (U.queueVis(id) === 'agg' ? ' <span class="small muted">(aggregate)</span>' : '') + '</th><td class="mono">' + (p ? S.clock(r.peakHour * 60) + ' to ' + S.clock(r.peakHour * 60 + 60) : 'n/a') + '</td>' +
          '<td class="num">' + (p ? QC.fmt(p.p90, { decimals: 1 }) : 'n/a') + '</td><td class="num">' + (p ? U.fmt(p.pax) : '0') + '</td><td class="num">' + (r.dayP90.p90 != null ? QC.fmt(r.dayP90.p90, { decimals: 1 }) : 'n/a') + '</td>' +
          '<td>' + (p ? (p.p90 > 15 ? U.st('crit', 'Above 15 min') : p.p90 >= 10 ? U.st('warn', '10 to 15 min') : U.st('good', 'Under 10 min')) : '') + '</td></tr>';
      }).join('') + '</tbody></table></div>';
  }

  function staffingTable(areaIds, m) {
    var rows = areaIds.map(function (aid) {
      var A = S.AREAS[aid], r = U.day.recommend(-1, aid, 96, { predict: false });
      var tp = 0, tr = 0, short = [], over = 0;
      for (var h = 0; h * 60 <= m; h++) {
        var p = 0, q = 0;
        for (var b = h * 4; b < h * 4 + 4; b++) A.queues.forEach(function (id) { p += r.planned[id][b] / 4; q += r.rec[id][b] / 4; });
        tp += p; tr += q;
        if (q - p >= 1) short.push(S.clock(h * 60) + ' (+' + QC.fmt(q - p, { decimals: 1 }) + ')');
        else if (p - q >= 1) over++;
      }
      return '<tr><th scope="row">' + U.esc(A.name) + '</th><td>' + A.unit + '</td><td class="num">' + QC.fmt(tp, { decimals: 1 }) + '</td><td class="num">' + QC.fmt(tr, { decimals: 1 }) + '</td><td class="num">' + (tr - tp >= 0 ? '+' : '') + QC.fmt(tr - tp, { decimals: 1 }) + '</td>' +
        '<td>' + (short.length ? U.st('warn', short.length + ' hour' + (short.length > 1 ? 's' : '')) + '<div class="small">' + short.join(', ') + '</div>' : U.st('good', 'None')) + '</td><td class="num">' + over + '</td></tr>';
    });
    return '<div class="table-wrap"><table class="tbl"><thead><tr><th scope="col">Area</th><th scope="col">Unit</th><th scope="col" class="num">Rostered hours</th><th scope="col" class="num">Recommended hours</th><th scope="col" class="num">Difference</th><th scope="col">Hours short by one or more</th><th scope="col" class="num">Hours over by one or more</th></tr></thead><tbody>' + rows.join('') + '</tbody></table></div>' +
      '<p class="small muted" style="margin-block-start:6px">Day to ' + S.clock(m) + '. Recommendation as computed at 00:00 from the schedule and API counts, before any P90 correction; hours over are mostly overnight, when the roster keeps minimum cover.</p>';
  }

  function egateSection(m) {
    var rows = [];
    ['A', 'D'].forEach(function (sd) {
      var e = U.day.egate(sd, m, m + 1);
      rows.push('<tr><th scope="row">' + (sd === 'A' ? 'Arrivals, 6 gates' : 'Departures, 4 gates') + '</th><td class="num">' + U.fmt(e.processed) + '</td><td class="num">' + U.fmt(e.rejects) + '</td><td class="num">' + QC.fmt(e.rejectRate * 100, { decimals: 1 }) + '%</td><td class="num">' + Math.round(e.util * 100) + '%</td></tr>');
    });
    return '<div class="table-wrap"><table class="tbl"><thead><tr><th scope="col">E-gates</th><th scope="col" class="num">Processed today</th><th scope="col" class="num">Rejected to manual</th><th scope="col" class="num">Reject rate</th><th scope="col" class="num">Utilisation</th></tr></thead><tbody>' + rows.join('') + '</tbody></table></div>' +
      '<p class="small muted" style="margin-block-start:6px">Gate AG-5 was out of service for planned maintenance from ' + S.clock(S.SCRIPT.egateOos.from) + ' to ' + S.clock(S.SCRIPT.egateOos.to) + '.</p>';
  }

  function slaSection(m) {
    var rows = [], ev = 0, br = 0, prov = 0;
    ['CI-C', 'CI-D'].forEach(function (id) {
      for (var bs = 0; bs <= m; bs += 15) {
        var b = U.day.bin(S.QI[id], bs, m);
        if (b.pax < 0.5 || b.p90 == null) continue;
        if (b.status === 'final') ev++; else prov++;
        if (b.p90 > 15) { br++; rows.push('<tr><th scope="row" class="mono">' + S.clock(bs) + ' to ' + S.clock(bs + 15) + '</th><td>Island ' + id.slice(3) + '</td><td class="num">' + QC.fmt(b.p90, { decimals: 1 }) + '</td><td>' + (b.status === 'final' ? 'Final' : 'Provisional') + '</td></tr>'); }
      }
    });
    return '<p>' + ev + ' final bins and ' + prov + ' provisional bins at islands C and D so far today; ' + br + ' above 15 minutes at P90.</p>' +
      (rows.length ? '<div class="table-wrap"><table class="tbl"><thead><tr><th scope="col">Bin</th><th scope="col">Island</th><th scope="col" class="num">P90, min</th><th scope="col">Status</th></tr></thead><tbody>' + rows.join('') + '</tbody></table></div>' : '');
  }

  function head(title, m) {
    return '<header class="report-head"><div><h2 style="font-size:18px">' + title + '</h2><p class="muted small" style="margin:4px 0 0">Demo International Airport (DMO), Terminal 1. Synthetic data up to ' + S.clock(m) + ' simulated. ' + U.esc(SCOPE[scope]) + '; prepared for the ' + U.esc(U.roleName()) + ' role.' +
      (ranFrom ? ' Run now from scheduled report ' + ranFrom + '.' : '') + '</p></div>' +
      '<div class="small"><span class="tag">Zone profile <strong>' + U.profileAt(m) + '</strong></span> <span class="tag">Seed <strong>' + U.day.seed + '</strong></span></div></header>';
  }
  var FOOT = '<p class="small muted">Prototype, synthetic data. Generated in the browser from a seeded simulation; not a record of real operations. No officer names or IDs are included.</p>';

  function render(m) {
    var rep = document.getElementById('report');
    rep.setAttribute('aria-label', TPL[tpl] + ' report preview');
    if (tpl === 'weekly') { rep.innerHTML = renderWeekly(m); return; }
    if (tpl === 'sla') { rep.innerHTML = renderSla(m); return; }
    if (tpl === 'device') { rep.innerHTML = renderDevice(m); return; }
    renderDaily(m);
  }

  function renderDaily(m) {
    var d = data, role = U.role();
    var immIds = IMM.filter(function (id) { return d.rows[id]; }), airIds = AIR.filter(function (id) { return d.rows[id]; });
    var sec = [];
    sec.push(head('Daily report: Monday 28 September 2026', m));
    if (immIds.length) sec.push('<section aria-labelledby="r1"><h2 id="r1">Peak-hour waits by lane, passport control</h2>' + peakTable(immIds, d) + (U.can('imm.desks') ? '' : '<p class="small muted" style="margin-block-start:6px">Lane-level aggregates from the border feed.</p>') + '</section>');
    if (airIds.length) sec.push('<section aria-labelledby="r2"><h2 id="r2">Peak-hour waits, ' + (role === 'hbm' ? 'Handler B check-in' : 'check-in and security') + '</h2>' + peakTable(airIds, d) + '</section>');
    var fq = U.can('imm.api') && inScope('A-VIS') ? 'A-VIS' : 'CI-C';
    if (vis(fq)) sec.push('<section aria-labelledby="r3"><h2 id="r3">Forecast against actual: ' + U.esc(U.qname(fq)) + '</h2><div id="fvaChart" class="chart"></div><div id="fvaTable"></div></section>');
    var areas = [];
    if (U.can('imm.forecast') && inScope('A-VIS')) areas.push('arr', 'dep');
    if (U.can('ci.A') && inScope('CI-A')) areas.push('ciA');
    if (U.can('ci.B') && inScope('CI-C')) areas.push('ciB');
    if (U.can('sec') && inScope('SEC-N')) areas.push('sec');
    if (areas.length) sec.push('<section aria-labelledby="r4"><h2 id="r4">Staffing against the recommendation</h2>' + staffingTable(areas, m) + '</section>');
    if (U.can('imm.egates') && inScope('A-EG')) sec.push('<section aria-labelledby="r5"><h2 id="r5">E-gate performance</h2>' + egateSection(m) + '</section>');
    if (U.can('sla') && inScope('CI-C')) sec.push('<section aria-labelledby="r6"><h2 id="r6">Handler B SLA, today</h2>' + slaSection(m) + '</section>');
    sec.push(FOOT);
    var rep = document.getElementById('report');
    rep.innerHTML = sec.join('');
    if (document.getElementById('fvaChart')) fva(fq, m);
  }

  function fva(id, m) {
    var q = S.QI[id], act = data.rows[id].hours, fc = dayAheadHourly(q, m);
    var pa = [], pf = [], err = 0, n = 0;
    act.forEach(function (x, h) {
      pa.push([h * 60 + 30, x.p90]);
      pf.push([h * 60 + 30, fc[h]]);
      if (x.p90 != null && fc[h] != null && x.pax >= 20) { err += Math.abs(x.p90 - fc[h]); n++; }
    });
    var ym = 15;
    pa.concat(pf).forEach(function (p) { if (p[1] != null) ym = Math.max(ym, p[1]); });
    QC.line(document.getElementById('fvaChart'), {
      title: 'Hourly P90 wait, day-ahead forecast and actual, ' + U.qname(id), desc: 'Minutes per hour of day. Synthetic.', height: 220,
      x: { min: 0, max: Math.max(120, Math.ceil((m + 1) / 60) * 60), ticks: U.timeTicks(0, Math.max(120, Math.ceil((m + 1) / 60) * 60), 180) }, y: { min: 0, max: Math.max(20, Math.ceil(ym / 5) * 5 + 5), label: 'Minutes' },
      series: [{ label: 'Actual', points: pa, color: 'var(--c1)', width: 2, dots: true }, { label: 'Day-ahead forecast', endText: 'Forecast', points: pf, color: 'var(--c6)', width: 2, dash: '6 4' }],
      hlines: [{ y: 15, label: '15 min', color: 'var(--crit)', dash: '6 4', align: 'left' }]
    });
    document.getElementById('fvaTable').innerHTML = '<p class="small" style="margin-block-start:6px">Mean absolute error of the hourly P90, hours with at least 20 passengers: <strong>' + (n ? QC.fmt(err / n, { decimals: 1 }) + ' min' : 'n/a') + '</strong> over ' + n + ' hours. ' +
      (U.can('imm.api') ? 'The day-ahead forecast uses the schedule and a generic lane mix; the live forecast adds on-block estimates and API counts by lane, which is how it catches visitor-heavy waves that the day-ahead plan misses.' : 'The day-ahead forecast uses the schedule and booked loads.') + '</p>';
  }

  /* weekly service standard: the demo simulates one day, and Monday 28 September starts the week */
  function renderWeekly(m) {
    var ids = IMM.concat(AIR).filter(function (id) { return vis(id) && !(U.queueVis(id) === 'agg' && /CRW$/.test(id)); });
    var tot = { pax: 0, ok: 0, bins: 0, over: 0 };
    var rows = ids.map(function (id) {
      var q = S.QI[id], st = U.day.binStats(q, 0, m + 1, m), share = st.share(15), p = U.day.p90(q, 0, m + 1, m);
      var bins = 0, over = 0;
      for (var bs = 0; bs + 14 <= m; bs += 15) { var b = U.day.bin(q, bs, m); if (b.p90 == null || b.status !== 'final') continue; bins++; if (b.p90 > 15) over++; }
      tot.pax += st.pax; tot.ok += (share || 0) * st.pax; tot.bins += bins; tot.over += over;
      return '<tr><th scope="row">' + U.esc(U.qname(id)) + (U.queueVis(id) === 'agg' ? ' <span class="small muted">(aggregate)</span>' : '') + '</th><td class="num">' + U.fmt(st.pax) + '</td><td class="num">' + (share == null ? 'n/a' : QC.fmt(share * 100, { decimals: 1 }) + '%') + '</td>' +
        '<td class="num">' + (p.p90 == null ? 'n/a' : QC.fmt(p.p90, { decimals: 1 })) + '</td><td class="num">' + over + ' of ' + bins + '</td><td>' + (share == null ? '' : share >= 0.95 && !over ? U.st('good', 'Met') : share >= 0.9 ? U.st('warn', 'Near') : U.st('crit', 'Missed')) + '</td></tr>';
    });
    return head('Weekly service standard: week of 28 September 2026, to date', m) +
      '<section aria-labelledby="w1"><h2 id="w1">Share of passengers waiting 15 minutes or less</h2><p class="small">Service standard: 95% of passengers wait 15 minutes or less, and no 15-minute bin above 15 minutes at P90. The demo simulates one day, so the week to date is Monday 28 September.</p>' +
      '<div class="table-wrap"><table class="tbl"><thead><tr><th scope="col">Zone</th><th scope="col" class="num">Passengers</th><th scope="col" class="num">Within 15 min</th><th scope="col" class="num">P90 today, min</th><th scope="col" class="num">Final bins above 15 min</th><th scope="col">Standard</th></tr></thead><tbody>' + rows.join('') + '</tbody>' +
      '<tfoot><tr><th scope="row">All zones in scope</th><td class="num">' + U.fmt(tot.pax) + '</td><td class="num">' + (tot.pax ? QC.fmt(tot.ok / tot.pax * 100, { decimals: 1 }) + '%' : 'n/a') + '</td><td></td><td class="num">' + tot.over + ' of ' + tot.bins + '</td><td></td></tr></tfoot></table></div></section>' + FOOT;
  }

  function renderSla(m) {
    if (!tplAllowed('sla', scope)) return head('Monthly SLA and penalties', m) + U.denied('SLA and penalty data concern the airport and its handlers.') + FOOT;
    var cts = U.list('contracts').filter(function (c) { return (U.role() !== 'hbm' && scope !== 'hb') || c.party === 'Handler B'; });
    var ev = U.sla.evaluate(m), mo = ev.month, sec = [head('Monthly SLA and penalties: September 2026, to date', m)];
    sec.push('<section aria-labelledby="s1"><h2 id="s1">C-001, Handler B check-in, month to date</h2><div class="table-wrap"><table class="tbl"><tbody>' +
      [['Bins evaluated', U.fmt(mo.ev)], ['Breaches', mo.br], ['Excluded', mo.excluded], ['Held for an open dispute', mo.held], ['Counted', mo.counted], ['Allowance', U.SLA.ALLOWANCE], ['Penalised bins', mo.penalised], ['Penalty to date', 'USD ' + U.fmt(mo.penalty)]].map(function (r) {
        return '<tr><th scope="row">' + r[0] + '</th><td class="num">' + r[1] + '</td></tr>'; }).join('') + '</tbody></table></div>' +
      (ev.today.provBr ? '<p class="small muted">' + ev.today.provBr + ' provisional breach' + (ev.today.provBr > 1 ? 'es' : '') + ' today not yet counted.</p>' : '') + '</section>');
    var others = cts.filter(function (c) { return !c.seeded; });
    if (others.length) sec.push('<section aria-labelledby="s2"><h2 id="s2">Contracts created in this demo, today\'s bins</h2><div class="table-wrap"><table class="tbl"><thead><tr><th scope="col">Contract</th><th scope="col">Status</th><th scope="col" class="num">Final bins</th><th scope="col" class="num">Breaches</th><th scope="col" class="num">Excluded</th><th scope="col" class="num">Counted</th></tr></thead><tbody>' +
      others.map(function (c) {
        var t = U.sla.evalContract(c, m);
        return '<tr><th scope="row">' + c.id + ', ' + U.esc(c.party) + '</th><td>' + (t.active ? 'Signed' : U.esc(t.why)) + '</td><td class="num">' + (t.active ? t.ev : '') + '</td><td class="num">' + (t.active ? t.br : '') + '</td><td class="num">' + (t.active ? t.excluded : '') + '</td><td class="num">' + (t.active ? t.counted : '') + '</td></tr>';
      }).join('') + '</tbody></table></div><p class="small muted">Only today is simulated for contracts created in the demo.</p></section>');
    var ex = U.sla.exclusionsToday();
    sec.push('<section aria-labelledby="s3"><h2 id="s3">Exclusions today</h2>' + (ex.length ? '<ul class="small">' + ex.map(function (x) { return '<li>' + x.id + ': ' + U.esc(U.SLA.EXT[x.type]) + ', ' + x.zones.join(', ') + ', ' + S.clock(x.from) + ' to ' + S.clock(x.to) + ' (' + U.esc(x.ref) + ')</li>'; }).join('') + '</ul>' : '<p class="small muted">None.</p>') + '</section>');
    sec.push(FOOT);
    return sec.join('');
  }

  function renderDevice(m) {
    if (!tplAllowed('device', scope)) return head('Device health', m) + U.denied('Sensor health is managed by the border and airport systems teams.') + FOOT;
    var base = S.SENSORS.filter(function (x) { var r = QFloor.ZONE_REGION[x.zone]; return U.regionVis(r) === 'full' && (scope !== 'border' || r === 'imm' || r === 'emi' || r === 'reclaim'); });
    var made = U.state.c.sensors.filter(function (x) { var r = U.sensorRegion(x); return U.regionVis(r) === 'full' && (scope !== 'border' || r === 'imm' || r === 'emi' || r === 'reclaim'); });
    var off = base.filter(function (x) { return U.day.sensorOffline(x.id, m); });
    var outs = S.OUTAGES.filter(function (o) { return base.some(function (x) { return x.id === o.sensor; }) && m >= o.from; });
    var mins = 0; outs.forEach(function (o) { mins += Math.min(m, o.to) - o.from; });
    var sec = [head('Device health: Monday 28 September 2026', m)];
    sec.push('<section aria-labelledby="d1"><h2 id="d1">Summary</h2><div class="table-wrap"><table class="tbl"><tbody>' +
      [['Sensors in scope', base.length + made.length], ['Online now', base.length - off.length + made.filter(function (x) { return x.status === 'online'; }).length], ['Offline now', off.length ? off.map(function (x) { return x.id; }).join(', ') : '0'],
        ['Commissioning or failed calibration', made.filter(function (x) { return x.status !== 'online'; }).length], ['Outage minutes today', mins], ['Availability today', QC.fmt(100 - mins / Math.max(1, (base.length) * (m + 1)) * 100, { decimals: 3 }) + '%']].map(function (r) {
        return '<tr><th scope="row">' + r[0] + '</th><td class="num">' + r[1] + '</td></tr>'; }).join('') + '</tbody></table></div></section>');
    sec.push('<section aria-labelledby="d2"><h2 id="d2">Outages today</h2>' + (outs.length ? '<ul class="small">' + outs.map(function (o) { return '<li>' + o.sensor + ' (' + U.esc(U.qname(o.zone)) + '): ' + S.clock(o.from) + ' to ' + (m < o.to ? 'now' : S.clock(o.to)) + '</li>'; }).join('') + '</ul>' : '<p class="small muted">None so far today.</p>') + '</section>');
    if (made.length) sec.push('<section aria-labelledby="d3"><h2 id="d3">Sensors registered in this demo</h2><div class="table-wrap"><table class="tbl"><thead><tr><th scope="col">Sensor</th><th scope="col">Zone</th><th scope="col">Status</th><th scope="col">Last calibration</th></tr></thead><tbody>' +
      made.map(function (x) {
        var cal = U.state.c.calibrations.filter(function (r) { return r.sensor === x.id; }).slice(-1)[0];
        return '<tr><th scope="row" class="mono">' + x.id + '</th><td>' + U.esc(U.zoneName(x.zone)) + '</td><td>' + U.esc(U.SENSOR_STATUS[x.status]) + '</td><td>' + (cal ? (cal.pass ? 'Passed, ' : 'Failed, ') + QC.fmt(cal.accuracy, { decimals: 1 }) + '% at ' + S.clock(cal.at) : 'None') + '</td></tr>';
      }).join('') + '</tbody></table></div></section>');
    sec.push(FOOT);
    return sec.join('');
  }

  /* scheduled reports */

  function reportVisible(r) { return scopeOptions().indexOf(r.scope) >= 0 || (U.role() === 'tdm' && r.scope === 'hb'); }
  function scheduleText(sc) {
    if (sc.type === 'daily') return 'Daily at ' + S.clock(sc.time);
    if (sc.type === 'weekly') return 'Weekly on ' + ['Sundays', 'Mondays', 'Tuesdays', 'Wednesdays', 'Thursdays', 'Fridays', 'Saturdays'][sc.weekday] + ' at ' + S.clock(sc.time);
    return 'Monthly on day ' + sc.date + ' at ' + S.clock(sc.time);
  }
  function schedulesHtml(m) {
    var list = U.list('reports').filter(reportVisible);
    if (!list.length) return '<p class="small muted">No scheduled reports in this role\'s view.</p>';
    return '<div class="table-wrap"><table class="tbl stack-sm"><thead><tr><th scope="col">ID</th><th scope="col">Report</th><th scope="col">Template and scope</th><th scope="col">Schedule</th><th scope="col">Format and recipients</th><th scope="col">Next run</th><th scope="col">Actions</th></tr></thead><tbody>' +
      list.map(function (r) {
        return '<tr' + U.rowCls(r.id) + '><th scope="row" class="mono">' + r.id + '</th><td><strong>' + U.esc(r.name) + '</strong><div class="small muted">' + (r.seeded ? 'Seeded' : 'Created by ' + U.esc(r.by) + ' at ' + S.clock(r.at)) + '</div></td>' +
          '<td class="small">' + TPL[r.template] + '<div class="muted">' + U.esc(SCOPE[r.scope]) + '</div></td><td class="small">' + scheduleText(r.schedule) + '</td>' +
          '<td class="small">' + r.format + '<div class="muted">' + U.esc(r.recipients) + ' (not sent in the demo)</div></td><td class="small mono" data-next="' + r.id + '">' + U.nextRun(r.schedule, m) + '</td>' +
          '<td class="actions"><button type="button" class="btn" data-rp="run" data-id="' + r.id + '">Run now</button>' + (!r.seeded && U.canCreate('report') ? '<button type="button" class="btn" data-rp="remove" data-id="' + r.id + '">Remove</button>' : '') + '</td></tr>';
      }).join('') + '</tbody></table></div>';
  }

  function tabsHtml() {
    return Object.keys(TPL).filter(function (t) { return tplAllowed(t, scope); }).map(function (t) {
      return '<button type="button" role="tab" id="tpl-' + t + '" aria-controls="report" aria-selected="' + (t === tpl) + '" tabindex="' + (t === tpl ? 0 : -1) + '" data-tpl="' + t + '">' + TPL[t] + '</button>';
    }).join('');
  }
  function setTpl(t, sc, from) {
    tpl = t; if (sc) scope = sc; ranFrom = from || null;
    var tabs = document.getElementById('tplTabs');
    tabs.innerHTML = tabsHtml();
    var sel = document.getElementById('scopeSel');
    if (sel) sel.value = scope;
    cacheKey = '';
    U.draw(true);
  }

  function openReport(trig) {
    var id = U.nextId('report'), opts = scopeOptions();
    var html = F.text('rid', 'Report ID', id, { readonly: true }) +
      F.text('name', 'Name', '', { req: true, placeholder: 'For example: Evening peak summary' }) +
      F.row(F.select('template', 'Template', Object.keys(TPL).filter(function (t) { return t !== 'sla' || U.can('sla'); }).filter(function (t) { return t !== 'device' || U.screenAccess('devices') !== 'none'; }).map(function (t) { return [t, TPL[t]]; }), 'daily'),
        F.select('scope', 'Scope', opts.map(function (o) { return [o, SCOPE[o]]; }), opts[0], { hint: 'Limited to this role\'s view.' })) +
      F.row(F.select('type', 'Schedule', [['daily', 'Daily'], ['weekly', 'Weekly'], ['monthly', 'Monthly']], 'daily'), F.select('time', 'At', U.timeOptions(0, 1425, 15), 420)) +
      F.row(F.select('weekday', 'On', [[1, 'Monday'], [2, 'Tuesday'], [3, 'Wednesday'], [4, 'Thursday'], [5, 'Friday'], [6, 'Saturday'], [0, 'Sunday']], 1, { hidden: true }),
        F.text('date', 'Day of the month', 1, { type: 'number', min: 1, max: 28, inputmode: 'numeric', hidden: true, hint: '1 to 28, so every month has it.' })) +
      F.radios('format', 'Format', [['PDF', 'PDF'], ['CSV', 'CSV']], 'PDF') +
      F.text('recipients', 'Recipients', '', { req: true, placeholder: 'ops.desk@dmo.example', hint: 'Plain text, separated by commas. Not sent in the demo.' }) +
      '<div class="preview" id="rpPrev" aria-live="polite"></div>';
    U.openDrawer({
      title: 'New scheduled report', html: html, trigger: trig, returnFocus: 'rpBtn',
      onOpen: function (form) {
        function sync() {
          var v = U.formValues(form);
          form.querySelector('[data-f="weekday"]').hidden = v.type !== 'weekly';
          form.querySelector('[data-f="date"]').hidden = v.type !== 'monthly';
          var sc = { type: v.type, time: +v.time, weekday: +v.weekday, date: U.V.int(v.date) || 1 };
          document.getElementById('rpPrev').innerHTML = '<strong>Next run</strong>' + scheduleText(sc) + ': <span class="mono">' + U.nextRun(sc, U.now()) + '</span> (simulated time).';
        }
        form.addEventListener('change', sync); form.addEventListener('input', sync); sync();
      },
      onSubmit: function (v) {
        var e = {};
        if (!v.name) e.name = 'Enter a name.';
        else if (v.name.length > 60) e.name = 'Keep the name under 60 characters.';
        else if (U.list('reports').some(function (r) { return r.name.toLowerCase() === v.name.toLowerCase(); })) e.name = 'A scheduled report with this name already exists.';
        if (opts.indexOf(v.scope) < 0) e.scope = 'This scope is outside the role\'s view.';
        else if (!tplAllowed(v.template, v.scope)) e.template = TPL[v.template] + ' needs a scope that includes ' + (v.template === 'sla' ? 'check-in.' : 'sensors this role manages.');
        var d = U.V.int(v.date);
        if (v.type === 'monthly' && (d == null || d < 1 || d > 28)) e.date = 'Day of the month must be a whole number from 1 to 28.';
        if (!v.recipients) e.recipients = 'Enter at least one recipient.';
        else { var bad = U.V.emails(v.recipients); if (bad.length) e.recipients = 'Not a valid email address: ' + bad.join(', ') + '.'; }
        if (Object.keys(e).length) return e;
        var sc = { type: v.type, time: +v.time };
        if (v.type === 'weekly') sc.weekday = +v.weekday;
        if (v.type === 'monthly') sc.date = d;
        U.state.c.reports.push({ id: id, name: v.name, template: v.template, scope: v.scope, schedule: sc, format: v.format, recipients: v.recipients, by: U.roleName(), at: U.now() });
        U.created('report', id, v.name + ': ' + TPL[v.template] + ', ' + scheduleText(sc).toLowerCase() + ', ' + v.format, function () { U.removeFrom('reports', id); }, function () { U.draw(true); });
        return null;
      }
    });
  }

  function onScheduleClick(e) {
    var b = e.target.closest('[data-rp]');
    if (!b) return;
    var id = b.getAttribute('data-id'), r = U.list('reports').filter(function (x) { return x.id === id; })[0];
    if (!r) return;
    if (b.getAttribute('data-rp') === 'run') {
      var sc = scopeOptions().indexOf(r.scope) >= 0 ? r.scope : ownScope();
      setTpl(tplAllowed(r.template, sc) ? r.template : 'daily', sc, r.id);
      var rep = document.getElementById('report');
      rep.setAttribute('tabindex', '-1'); rep.focus();
      if (rep.scrollIntoView) rep.scrollIntoView();
      U.status('Preview of ' + r.id + ', ' + r.name + '.');
    } else if (!r.seeded && U.canCreate('report')) {
      U.removeFrom('reports', id); U.saveC(); U.audit('Removed', id, 'Scheduled report removed'); U.draw(true);
      U.toast('Removed ' + id, { undo: function () { U.state.c.reports.push(r); U.saveC(); U.audit('Restored', id, 'Scheduled report restored'); U.draw(true); } });
      var nb = document.getElementById('rpBtn'); if (nb) nb.focus();
    }
  }

  function csvField(v) { var s = v == null ? '' : String(v); return /[",\n]/.test(s) ? '"' + s.replace(/"/g, '""') + '"' : s; }

  function exportCsv() {
    var m = U.now(), day = U.day, lines = [];
    var header = ['date', 'bin_start', 'bin_end', 'zone_id', 'zone_name', 'view', 'passengers', 'mean_wait_min', 'p90_wait_min', 'status', 'servers_open_avg', 'profile_version', 'synthetic'];
    lines.push(header.join(','));
    S.QUEUES.forEach(function (d) {
      if (!vis(d.id)) return;
      var q = d.index, sv = serversVisible(d.id), view = U.queueVis(d.id) === 'agg' ? 'aggregate' : 'full';
      for (var bs = 0; bs <= m; bs += 15) {
        var b = day.bin(q, bs, m), ws = day.waitSeries(q, bs, Math.min(bs + 14, m), m);
        var sw = 0, sa = 0;
        ws.forEach(function (p) { var a = day.A[q][p.m + S.PRE]; sw += p.w * a; sa += a; });
        var open = 0, cnt = 0;
        for (var t = bs; t <= Math.min(bs + 14, m); t++) { open += day.OPEN[q][t + S.PRE]; cnt++; }
        lines.push([S.DATE, S.clock(bs), S.clock(bs + 15), d.id, d.name, view, Math.round(b.pax), sa > 0 ? (sw / sa).toFixed(1) : '', b.p90 == null ? '' : b.p90.toFixed(1),
          b.status, sv ? (open / cnt).toFixed(1) : '', U.profileAt(Math.min(bs + 14, m)), 'yes'].map(csvField).join(','));
      }
    });
    U.offerFile(document.getElementById('csvPanel'), { filename: 'dmo-qms-intervals-' + S.DATE + '-' + S.clock(m).replace(':', '') + '.csv', mime: 'text/csv', text: lines.join('\n') + '\n', title: 'CSV export, 15-minute intervals',
      note: (lines.length - 1) + ' rows for the zones visible to the ' + U.roleName() + ' role. servers_open_avg is empty where desk data is outside this role.' });
  }

  function update(m) {
    U.html(document.getElementById('rpList'), schedulesHtml(m));
    var key = Math.floor(m / 5) + '|' + U.role() + '|' + U.dayVersion + '|' + U.profileAt(m) + '|' + tpl + '|' + scope + '|' + ranFrom + '|' + U.state.c.sensors.map(function (x) { return x.status; }).join() + '|' + U.state.c.exclusions.length + '|' + U.state.c.contracts.map(function (c) { return c.status; }).join();
    if (key !== cacheKey) {
      cacheKey = key;
      data = compute(m);
      render(m);
      var all = Object.keys(data.rows), best = null, bid = null, over = 0;
      all.forEach(function (id) { var p = data.rows[id].peak; if (p && (best == null || p.p90 > best.p90)) { best = p; bid = id; } data.rows[id].hours.forEach(function (x) { if (x.p90 != null && x.p90 > 15 && x.pax >= 20) over++; }); });
      var al = U.alertsAt(m).length;
      var lv = best ? U.waitLevel(best.p90) : 'good';
      U.html(document.getElementById('kpis'), [
        U.kpi({ hero: true, level: lv === 'good' ? '' : lv, label: 'Worst peak-hour P90 today', value: best ? U.fmtWait(best.p90) : 'n/a', sub: bid ? U.esc(U.qname(bid)) + ', ' + S.clock(data.rows[bid].peakHour * 60) + ' to ' + S.clock(data.rows[bid].peakHour * 60 + 60) : '' }),
        U.kpi({ label: 'Zone-hours above 15 min at P90', value: String(over), sub: 'Zones visible to this role' }),
        U.kpi({ label: 'Alerts today', value: String(al), sub: 'Visible to this role' })
      ].join(''));
    }
  }

  QUI.start({ build: build, update: update });
})();
