(function () {
  'use strict';
  var U = QUI, S = QSim, QC = QCharts;
  var area = null, rec = null, recKey = '', confirmOpen = false;

  function areasForRole() {
    return Object.keys(S.AREAS).filter(function (id) { return U.can(S.AREAS[id].perm); });
  }

  function build() {
    var list = areasForRole();
    var stored = U.store.get('fc.area.' + U.role(), null);
    area = list.indexOf(stored) >= 0 ? stored : list[0] || null;
    rec = null; recKey = ''; confirmOpen = false;
    var tabs = document.getElementById('areaTabs');
    ['kpis', 'fcBody', 'stBody'].forEach(function (id) { var e = document.getElementById(id); e.innerHTML = ''; e.__html = null; });
    tabs.innerHTML = list.map(function (id) {
      return '<button type="button" role="tab" id="atab-' + id + '" aria-selected="' + (id === area) + '" tabindex="' + (id === area ? 0 : -1) + '" data-area="' + id + '">' + U.esc(S.AREAS[id].name) + '</button>';
    }).join('');
    tabs.querySelectorAll('[role="tab"]').forEach(function (b, i, all) {
      b.addEventListener('click', function () { setArea(b.getAttribute('data-area')); });
      b.addEventListener('keydown', function (e) {
        if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
        e.preventDefault();
        var j = (i + (e.key === 'ArrowRight' ? 1 : -1) + all.length) % all.length;
        setArea(all[j].getAttribute('data-area')); all[j].focus();
      });
    });
    document.getElementById('fcBody').innerHTML =
      '<section class="panel" aria-labelledby="dmTitle"><div class="panel-head"><div><h2 id="dmTitle">Demand, next 24 hours</h2><p id="dmSub"></p></div></div><div id="demandChart" class="chart"></div></section>';
    document.getElementById('stBody').innerHTML =
      '<div id="accState"></div><div class="grid cols-2"><div><h3 style="margin-block-end:6px">' + 'Staffed positions per 15 minutes' + '</h3><div id="rosterChart" class="chart"></div></div>' +
      '<div><h3 style="margin-block-end:6px">Predicted P90 wait per 15 minutes</h3><div id="p90Chart" class="chart"></div></div></div>' +
      '<div class="table-wrap" id="recTable" style="margin-block-start:12px"></div>';
    var sb = document.getElementById('stBody');
    if (!sb.__bound) { sb.addEventListener('click', onClick); sb.__bound = true; }
    document.getElementById('wiBody').innerHTML = whatIf();
  }

  function setArea(id) {
    area = id; U.store.set('fc.area.' + U.role(), id); rec = null; recKey = ''; confirmOpen = false;
    document.querySelectorAll('#areaTabs [role="tab"]').forEach(function (b) { var on = b.getAttribute('data-area') === id; b.setAttribute('aria-selected', String(on)); b.tabIndex = on ? 0 : -1; });
    ['kpis', 'accState', 'recTable'].forEach(function (x) { var e = document.getElementById(x); if (e) e.__html = null; });
    U.draw(true);
  }

  function getRec(m) {
    var key = area + '|' + Math.floor(m / 15) + '|' + U.dayVersion;
    if (key !== recKey) { rec = U.day.recommend(m, area, 24); recKey = key; }
    return rec;
  }

  function demand(m) {
    var A = S.AREAS[area], qs = A.queues.slice();
    if (area === 'arr' || area === 'dep') qs.push(area === 'arr' ? 'A-EG' : 'D-EG');
    var d = U.day.demand(m, 24, 30, qs.map(function (id) { return S.QI[id]; }));
    var series = qs.map(function (id) { return { label: U.shortName(id), values: d.q[S.QI[id]], color: U.color(id) }; });
    var total = 0;
    series.forEach(function (s) { s.values.forEach(function (v) { total += v / 2; }); });
    QC.stackedArea(document.getElementById('demandChart'), {
      title: 'Expected demand, ' + A.name + ', next 24 hours', desc: 'Passengers per hour by ' + (area === 'arr' || area === 'dep' ? 'lane' : area === 'sec' ? 'checkpoint' : 'island') + ', in 30-minute steps. Synthetic.',
      height: 240, xs: d.xs, x: { ticks: U.timeTicks(d.xs[0], d.xs[d.xs.length - 1], 180) }, y: { label: 'Passengers per hour' }, series: series
    });
    document.getElementById('dmSub').textContent = (area === 'arr' ? 'From the schedule, estimated on-block times and API passenger counts by lane. ' : area === 'dep' ? 'Departing passengers reaching passport control, by lane. ' : 'From the schedule, booked loads and the show-up curve. ') + 'About ' + U.fmt(total) + ' passengers in the next 24 hours.';
    return total;
  }

  function staffing(m) {
    var r = getRec(m), A = S.AREAS[area], nb = r.blocks;
    var xs = [], plan = [], recT = [];
    for (var b = 0; b < nb; b++) {
      var p = 0, q = 0;
      A.queues.forEach(function (id) { p += r.planned[id][b]; q += r.rec[id][b]; });
      xs.push(r.from + b * 15); plan.push(p); recT.push(q);
    }
    var pp = [], rp = [], bands = [], cur = null;
    for (b = 0; b < nb; b++) {
      pp.push([xs[b], plan[b]], [xs[b] + 15, plan[b]]);
      rp.push([xs[b], recT[b]], [xs[b] + 15, recT[b]]);
      if (recT[b] > plan[b]) {
        if (!cur) { cur = { points: [], color: 'var(--warn)', opacity: 0.3 }; bands.push(cur); }
        cur.points.push([xs[b], plan[b], recT[b]], [xs[b] + 15, plan[b], recT[b]]);
      } else cur = null;
    }
    var ymax = 4;
    plan.concat(recT).forEach(function (v) { ymax = Math.max(ymax, v); });
    QC.line(document.getElementById('rosterChart'), {
      title: 'Planned roster and recommendation, ' + A.name, desc: 'Total ' + A.unit + ' per 15-minute block; shaded where the recommendation is above the roster.',
      height: 230, x: { min: r.from, max: r.until, ticks: U.timeTicks(r.from, r.until, 60) }, y: { min: 0, max: Math.ceil(ymax * 1.15), label: A.unit.charAt(0).toUpperCase() + A.unit.slice(1) },
      series: [{ label: 'Roster', points: pp, color: 'var(--c6)', width: 2 }, { label: 'Recommended', endText: 'Rec.', points: rp, color: 'var(--c1)', width: 2.5 }],
      bands: bands
    });
    var p90p = [], p90r = [], ym = 20;
    for (b = 0; b < nb; b++) {
      p90p.push([xs[b] + 7.5, Math.min(r.p90Plan[b], 90)]); p90r.push([xs[b] + 7.5, Math.min(r.p90Rec[b], 90)]);
      ym = Math.max(ym, Math.min(r.p90Plan[b], 90), Math.min(r.p90Rec[b], 90));
    }
    QC.line(document.getElementById('p90Chart'), {
      title: 'Predicted P90 wait under the roster and under the recommendation', desc: 'Longest lane per 15-minute block, Monte Carlo. Synthetic.',
      height: 230, x: { min: r.from, max: r.until, ticks: U.timeTicks(r.from, r.until, 60) }, y: { min: 0, max: Math.ceil(ym / 5) * 5, label: 'Minutes' },
      series: [{ label: 'Roster', points: p90p, color: 'var(--c6)', width: 2, dots: true }, { label: 'Recommended', endText: 'Rec.', points: p90r, color: 'var(--c1)', width: 2.5, dots: true }],
      hlines: [{ y: 15, label: 'Target 15 min', color: 'var(--crit)', dash: '6 4', align: 'left' }]
    });

    var rows = [];
    for (b = 0; b < nb; b++) {
      var gap = recT[b] - plan[b];
      var lanes = A.queues.map(function (id) {
        var pl = r.planned[id][b], rc = r.rec[id][b];
        return '<td class="num' + (rc > pl ? ' gap-short' : '') + '">' + pl + ' / ' + rc + '</td>';
      }).join('');
      rows.push('<tr' + (gap > 0 ? ' class="row-hl"' : '') + '><th scope="row" class="mono">' + S.clock(xs[b]) + '</th>' + lanes +
        '<td class="num">' + plan[b] + '</td><td class="num">' + recT[b] + '</td><td>' + (gap > 0 ? U.st('warn', 'Short by ' + gap) : gap < 0 ? '<span class="small muted">Spare ' + (-gap) + '</span>' : '<span class="small muted">Matches</span>') + '</td>' +
        '<td class="num">' + QC.fmt(r.p90Plan[b], { decimals: 0 }) + '</td><td class="num">' + QC.fmt(r.p90Rec[b], { decimals: 0 }) + '</td></tr>');
    }
    var head = '<tr><th scope="col">Block</th>' + A.queues.map(function (id) { return '<th scope="col" class="num">' + U.esc(U.shortName(id)) + '<br><span class="small muted">roster / rec.</span></th>'; }).join('') +
      '<th scope="col" class="num">Roster</th><th scope="col" class="num">Rec.</th><th scope="col">Gap</th><th scope="col" class="num">P90 roster, min</th><th scope="col" class="num">P90 rec., min</th></tr>';
    U.html(document.getElementById('recTable'), '<table class="tbl"><thead>' + head + '</thead><tbody>' + rows.join('') + '</tbody></table>');
    return { plan: plan, rec: recT, r: r };
  }

  function accState(m, st) {
    var acc = U.state.accepted.filter(function (a) { return a.area === area; })[0];
    var A = S.AREAS[area], r = st.r;
    var short = 0, first = null;
    for (var b = 0; b < r.blocks; b++) if (st.rec[b] > st.plan[b]) { short += st.rec[b] - st.plan[b]; if (first == null) first = r.from + b * 15; }
    var html = '';
    if (acc) {
      html = '<div class="callout info">' + U.icon('check') + '<div><strong>Recommendation accepted</strong> at ' + S.clock(acc.at) + ' by ' + U.esc(acc.by) + ' for ' + U.esc(A.name) + ', ' + S.clock(acc.from) + ' to ' + S.clock(acc.until) +
        '. The simulation re-ran from 00:00 with the new plan; times before ' + S.clock(acc.from) + ' are unchanged. <button type="button" class="btn" data-act="undo">Undo</button></div></div>';
    }
    if (confirmOpen) {
      html += '<div class="callout" role="group" aria-label="Confirm staffing change" style="margin-block-start:8px">' + U.icon('warn') + '<div><strong>Apply the recommendation?</strong> ' + U.esc(A.name) + ' will be staffed as recommended from ' + S.clock(r.from) + ' to ' + S.clock(r.until) +
        '. The simulation re-runs the day with the new plan. <div class="row" style="margin-block-start:8px"><button type="button" class="btn btn-primary" data-act="confirm" data-key="acc-confirm">Confirm and apply</button><button type="button" class="btn" data-act="cancel">Cancel</button></div></div></div>';
    } else {
      html += '<div class="row" style="margin-block:8px 12px"><button type="button" class="btn btn-primary" data-act="accept" data-key="acc-btn"' + (short ? '' : '') + '>' + U.icon('check') + 'Accept recommendation</button><span class="small muted">' +
        (short ? 'The roster is ' + short + ' ' + A.unit + '-blocks short over the next six hours, first at ' + S.clock(first) + '.' : 'The roster already covers the recommendation for the next six hours.') + ' Applies from ' + S.clock(r.from) + '.</span></div>';
    }
    U.html(document.getElementById('accState'), html);
  }

  function onClick(e) {
    var b = e.target.closest('[data-act]');
    if (!b) return;
    var act = b.getAttribute('data-act'), m = U.now();
    if (act === 'accept') { confirmOpen = true; U.draw(true); var c = document.querySelector('[data-key="acc-confirm"]'); if (c) c.focus(); }
    else if (act === 'cancel') { confirmOpen = false; U.draw(true); }
    else if (act === 'confirm') {
      var r = getRec(m);
      confirmOpen = false;
      U.accept({ area: area, from: r.from, until: r.until, counts: r.rec, at: m, by: U.roleName() });
      rec = null; recKey = '';
      U.status('Recommendation applied for ' + S.AREAS[area].name + '. The day was re-simulated.');
    } else if (act === 'undo') { U.unaccept(area); rec = null; recKey = ''; U.status('Accepted recommendation removed.'); }
  }

  function kpis(m, st) {
    var r = st.r, wp = 0, wpAt = r.from, wr = 0, gapMax = 0, gapAt = null;
    for (var b = 0; b < r.blocks; b++) {
      if (r.p90Plan[b] > wp) { wp = r.p90Plan[b]; wpAt = r.from + b * 15; }
      if (r.p90Rec[b] > wr) wr = r.p90Rec[b];
      var g = st.rec[b] - st.plan[b];
      if (g > gapMax) { gapMax = g; gapAt = r.from + b * 15; }
    }
    var lv = U.waitLevel(wp);
    U.html(document.getElementById('kpis'), [
      U.kpi({ hero: true, level: lv === 'good' ? '' : lv, label: 'Peak predicted P90 wait, next 6 hours, roster as planned', value: U.fmtWait(wp), sub: 'Block starting ' + S.clock(wpAt) + ', longest lane' }),
      U.kpi({ label: 'Same, with the recommendation', value: U.fmtWait(wr), sub: wr <= 15 ? U.st('good', 'Within 15 min') : U.st('warn', 'Still above 15 min') }),
      U.kpi({ label: 'Largest staffing gap', value: gapMax ? '+' + gapMax + ' <small>' + S.AREAS[area].unit + '</small>' : '0', sub: gapMax ? 'Block starting ' + S.clock(gapAt) : 'Roster covers the recommendation' })
    ].join(''));
  }

  function whatIf() {
    return '<div class="split"><div class="wide"><p class="small">In v2, planners will copy today\'s plan into a sandbox, change the inputs and replay the day side by side with the current plan, without touching live operations:</p><ul class="small">' +
      '<li>Schedule: add, cancel or move flights, change load factors.</li><li>Lane mix: change the share of Visitors or e-gate eligible passengers.</li>' +
      '<li>Resources: desks per lane, e-gates in service, counters per island, service times.</li><li>Output: P90 wait per 15 minutes, SLA bins at risk and desk-hours, for both plans.</li></ul>' +
      '<p class="small muted">Scheduled for v2 in the roadmap. Nothing in this panel runs.</p></div>' +
      '<div class="mock" aria-hidden="true"><div class="stack"><label class="field">Scenario <select disabled><option>Copy of today\'s plan</option></select></label>' +
      '<label class="field">Visitors share <input type="range" disabled value="35"></label><label class="field">E-gates in service <input type="number" disabled value="6"></label>' +
      '<button type="button" class="btn" disabled>Run what-if</button></div></div></div>';
  }

  function update(m) {
    if (!area) { document.getElementById('fcBody').innerHTML = U.denied('No forecast area is available.'); return; }
    demand(m);
    var st = staffing(m);
    accState(m, st);
    kpis(m, st);
    document.getElementById('stSub').textContent = S.AREAS[area].name + ': ' + S.AREAS[area].unit + ' per 15-minute block for the next six hours, from ' + S.clock(st.r.from) + '. The recommendation uses the fewest positions that keep the expected wait under ' + st.r.target + ' min and utilisation under 85%, staffs ahead of waves, and adds positions wherever the predicted P90 passes 13 min.';
  }

  /* roster overrides */

  var F = U.F, flDir = 'arr', flAll = false;

  function overridesHtml() {
    var list = U.state.c.overrides, allowed = U.createQueues('override');
    if (!list.length) return '<p class="small muted">No overrides yet. The roster is the day-ahead plan' + (U.state.accepted.length ? ' plus accepted recommendations' : '') + '.</p>';
    return '<div class="table-wrap"><table class="tbl stack-sm"><thead><tr><th scope="col">ID</th><th scope="col">Queue</th><th scope="col">Window</th><th scope="col" class="num">Planned</th><th scope="col">Reason</th><th scope="col">Added</th><th scope="col">Actions</th></tr></thead><tbody>' +
      list.map(function (o) {
        var d = S.QUEUES[S.QI[o.q]];
        return '<tr' + U.rowCls(o.id) + '><th scope="row" class="mono">' + o.id + '</th><td>' + U.esc(d.name) + '</td><td class="mono">' + S.clock(o.from) + ' to ' + (o.to >= 1440 ? '24:00' : S.clock(o.to)) + '</td><td class="num">' + o.n + ' ' + d.unit + '</td>' +
          '<td class="small">' + U.esc(o.reason) + '</td><td class="small">' + U.esc(o.by) + ' at ' + S.clock(o.at) + '</td><td class="actions">' +
          (U.canCreate('override') && allowed.indexOf(o.q) >= 0 ? '<button type="button" class="btn" data-ov-remove="' + o.id + '">Remove</button>' : '') + '</td></tr>';
      }).join('') + '</tbody></table></div>';
  }

  function openOverride(trig) {
    var allowed = U.createQueues('override');
    var A = S.AREAS[area];
    var def = A && allowed.indexOf(A.queues[0]) >= 0 ? A.queues[A.queues.length - 1] : allowed[0];
    var nb = Math.floor(U.now() / 15) + 1;
    var id = U.nextId('override');
    var html = F.text('oid', 'Override ID', id, { readonly: true }) +
      F.select('q', 'Queue', allowed.map(function (qid) { return [qid, U.qname(qid)]; }), def) +
      F.row(F.select('from', 'From', U.timeOptions(0, 1425, 15), Math.min(nb * 15, 1425)), F.select('to', 'To', U.timeOptions(15, 1440, 15), Math.min(nb * 15 + 60, 1440))) +
      F.text('n', 'Planned', '', { type: 'number', min: 0, inputmode: 'numeric', req: true, unit: 'desks' }) +
      F.text('reason', 'Reason', '', { req: true, placeholder: 'For example: second shift starts early' }) +
      '<div class="preview" id="ovPrev" aria-live="polite"></div>';
    U.openDrawer({
      title: 'Add roster override', html: html, trigger: trig, returnFocus: 'ovBtn',
      onOpen: function (form) {
        function sync() {
          var v = U.formValues(form), d = S.QUEUES[S.QI[v.q]], from = +v.from, to = +v.to;
          form.querySelector('[data-f="n"] .unit').textContent = d.unit;
          var lo = Infinity, hi = 0;
          for (var t = from; t < to; t += 15) { var n = U.day.plan(d.index, t); lo = Math.min(lo, n); hi = Math.max(hi, n); }
          document.getElementById('ovPrev').innerHTML = '<strong>Current plan</strong>' + (to > from ? U.esc(d.name) + ': ' + (lo === hi ? lo : lo + ' to ' + hi) + ' ' + d.unit + ' planned from ' + S.clock(from) + ' to ' + (to >= 1440 ? '24:00' : S.clock(to)) + ', of ' + d.servers.length + ' available.' : 'Choose a window.');
        }
        form.addEventListener('change', sync); sync();
      },
      onSubmit: function (v) {
        var e = {}, d = S.QUEUES[S.QI[v.q]], from = +v.from, to = +v.to, n = U.V.int(v.n);
        if (!(to > from)) e.to = 'The window must end after it starts.';
        if (n == null) e.n = 'Enter the number of ' + d.unit + ' planned.';
        else if (n < 0 || n > d.servers.length) e.n = d.name + ' has ' + d.servers.length + ' ' + d.unit + '; enter 0 to ' + d.servers.length + '.';
        if (!v.reason) e.reason = 'Enter a reason; it is kept in the audit log.';
        var clash = U.state.c.overrides.filter(function (o) { return o.q === v.q && from < o.to && to > o.from; })[0];
        if (clash && !e.to) e.from = clash.id + ' already sets ' + d.label + ' from ' + S.clock(clash.from) + ' to ' + S.clock(clash.to) + '. Remove it or choose another window.';
        if (Object.keys(e).length) return e;
        U.state.c.overrides.push({ id: id, q: v.q, from: from, to: to, n: n, reason: v.reason, by: U.roleName(), at: U.now() });
        U.saveC();
        U.rerun();
        U.created('override', id, d.name + ': ' + n + ' ' + d.unit + ' from ' + S.clock(from) + ' to ' + S.clock(to) + ' (' + v.reason + ')', function () { U.removeFrom('overrides', id); U.saveC(); U.rerun(); });
        return null;
      }
    });
  }

  /* flight schedule and ad-hoc flights */

  function flightsHtml(m) {
    var role = U.role(), day = U.day;
    var showArr = role !== 'hbm';
    if (!showArr) flDir = 'dep';
    var tabs = '<div class="tabs" role="tablist" aria-label="Direction">' + (showArr ? '<button type="button" role="tab" data-fl-dir="arr" aria-selected="' + (flDir === 'arr') + '">Arrivals</button>' : '') +
      '<button type="button" role="tab" data-fl-dir="dep" aria-selected="' + (flDir === 'dep') + '">Departures</button></div>' +
      ' <label class="chk small" style="margin-inline-start:12px"><input type="checkbox" data-fl-all' + (flAll ? ' checked' : '') + '> Whole day</label>';
    var list = flDir === 'arr' ? day.S.arr : day.S.dep.filter(function (f) { return role !== 'hbm' || f.handler === 'B'; });
    var t0 = m - 60, t1 = m + 240;
    list = list.filter(function (f) { var t = flDir === 'arr' ? f.eibt : f.std; return flAll || (t >= t0 && t <= t1) || f.adhoc; });
    var head = flDir === 'arr' ? '<th scope="col">Flight</th><th scope="col">Scheduled</th><th scope="col">On-block (est.)</th><th scope="col" class="num">Seats</th><th scope="col" class="num">Load</th><th scope="col" class="num">Passengers</th><th scope="col">Source</th>'
      : '<th scope="col">Flight</th><th scope="col">STD</th><th scope="col">Check-in</th><th scope="col" class="num">Seats</th><th scope="col" class="num">Load</th><th scope="col" class="num">Passengers</th><th scope="col">Source</th>';
    var rows = list.map(function (f) {
      var src = f.adhoc ? '<span class="pill warn">Ad-hoc ' + f.adhoc + '</span>' : '<span class="small muted">AODB</span>';
      if (flDir === 'arr') return '<tr' + U.rowCls(f.adhoc) + '><th scope="row" class="mono">' + U.esc(f.code) + '</th><td class="mono">' + S.clock(f.sched) + '</td><td class="mono">' + S.clock(f.eibt) + '</td><td class="num">' + f.seats + '</td><td class="num">' + Math.round(f.booked * 100) + '%</td><td class="num">' + U.fmt(f.pax) + '</td><td>' + src + '</td></tr>';
      return '<tr' + U.rowCls(f.adhoc) + '><th scope="row" class="mono">' + U.esc(f.code) + '</th><td class="mono">' + S.clock(f.std) + '</td><td>Island ' + f.island + ', Handler ' + f.handler + '</td><td class="num">' + f.seats + '</td><td class="num">' + Math.round(f.booked * 100) + '%</td><td class="num">' + U.fmt(f.pax) + '</td><td>' + src + '</td></tr>';
    });
    return tabs + '<p class="small muted" style="margin-block:8px">' + (flAll ? 'All ' : 'From ' + S.clock(t0) + ' to ' + S.clock(t1) + ': ') + list.length + ' flights. ' + (role === 'hbm' ? 'Handler B departures only.' : '') + '</p>' +
      '<div class="table-wrap scroll-y"><table class="tbl"><thead><tr>' + head + '</tr></thead><tbody>' + rows.join('') + '</tbody></table></div>';
  }

  var MIX_DEFAULT = { CIT: 35, RES: 20, VIS: 35, CRW: 2, TRF: 8 };
  function openFlight(trig) {
    var id = U.nextId('flight');
    var html = F.text('fid', 'Record ID', id, { readonly: true }) +
      F.row(F.text('code', 'Flight code', '', { req: true, placeholder: 'DM 902', hint: 'Two letters, a space and three or four digits.' }),
        F.radios('dir', 'Direction', [['arr', 'Arrival'], ['dep', 'Departure']], 'arr')) +
      F.row(F.text('time', 'On-block time', '', { req: true, placeholder: 'HH:MM', inputmode: 'numeric' }), F.select('reason', 'Reason', [['Diversion', 'Diversion'], ['Extra section', 'Extra section'], ['AODB feed stale', 'AODB feed stale'], ['Other', 'Other']], 'Diversion')) +
      F.row(F.text('seats', 'Seats', 220, { type: 'number', min: 120, max: 400, inputmode: 'numeric' }), F.text('load', 'Expected load', 85, { type: 'number', min: 50, max: 100, unit: '%', inputmode: 'numeric' })) +
      F.row(F.select('handler', 'Handler', [['A', 'Handler A'], ['B', 'Handler B']], 'B', { hidden: true }), F.select('island', 'Check-in island', [['A', 'Island A'], ['B', 'Island B'], ['C', 'Island C'], ['D', 'Island D']], 'C', { hidden: true })) +
      F.select('mix', 'Lane mix', [['default', 'Default synthetic mix'], ['custom', 'Custom']], 'default', { hint: 'Default: Citizens 35%, Residents 20%, Visitors 35%, crew and diplomats 2%, transfer 8%.' }) +
      '<div data-mix hidden>' + F.row(F.text('mCIT', 'Citizens', 35, { type: 'number', unit: '%' }), F.text('mRES', 'Residents', 20, { type: 'number', unit: '%' }), F.text('mVIS', 'Visitors', 35, { type: 'number', unit: '%' })) +
      F.row(F.text('mCRW', 'Crew and diplomats', 2, { type: 'number', unit: '%' }), F.text('mTRF', 'Transfer', 8, { type: 'number', unit: '%' })) + '</div>' +
      F.note('The flight joins today\'s schedule with its own seeded randomness, so no other flight changes. It appears in the arrival-wave strip and changes demand and the forecast.');
    U.openDrawer({
      title: 'Add ad-hoc flight', html: html, trigger: trig, returnFocus: 'flBtn',
      onOpen: function (form) {
        function sync() {
          var v = U.formValues(form), dep = v.dir === 'dep';
          form.querySelector('[data-f="time"] label').firstChild.textContent = dep ? 'STD (scheduled departure)' : 'On-block time';
          form.querySelector('[data-f="handler"]').hidden = !dep;
          form.querySelector('[data-f="island"]').hidden = !dep;
          form.querySelector('[data-mix]').hidden = v.mix !== 'custom';
        }
        form.addEventListener('change', sync);
        form.querySelector('#f-code').addEventListener('input', function (e) { var p = e.target.selectionStart; e.target.value = e.target.value.toUpperCase(); e.target.setSelectionRange(p, p); });
        sync();
      },
      onSubmit: function (v) {
        var e = {}, t = U.V.time(v.time), seats = U.V.int(v.seats), load = U.V.num(v.load);
        if (!/^[A-Z]{2} \d{3,4}$/.test(v.code)) e.code = 'Use two letters, a space and three or four digits, for example DM 902.';
        else {
          var dup = U.day.S.arr.concat(U.day.S.dep).filter(function (f) { return f.code === v.code; })[0];
          if (dup) e.code = v.code + ' is already on today\'s schedule (' + (dup.dir === 'arr' ? 'on-block ' + S.clock(dup.onBlock) : 'STD ' + S.clock(dup.std)) + ').';
        }
        if (t == null) e.time = 'Enter a time as HH:MM, for example 18:40.';
        if (seats == null || seats < 120 || seats > 400) e.seats = 'Seats must be a whole number from 120 to 400.';
        if (load == null || load < 50 || load > 100) e.load = 'Expected load must be between 50% and 100%.';
        if (v.dir === 'dep' && (v.handler === 'A') !== (v.island === 'A' || v.island === 'B')) e.island = 'Handler ' + v.handler + ' checks in at islands ' + (v.handler === 'A' ? 'A and B' : 'C and D') + '.';
        var mix = null;
        if (v.mix === 'custom') {
          var parts = ['CIT', 'RES', 'VIS', 'CRW', 'TRF'].map(function (k) { return U.V.num(v['m' + k]); });
          if (parts.some(function (x) { return x == null || x < 0 || x > 100; })) e.mCIT = 'Each share must be between 0% and 100%.';
          else if (Math.abs(parts.reduce(function (a, b) { return a + b; }, 0) - 100) > 0.5) e.mCIT = 'The lane shares must add up to 100%; they add up to ' + U.fmt(parts.reduce(function (a, b) { return a + b; }, 0), 1) + '%.';
          else mix = { CIT: parts[0] / 100, RES: parts[1] / 100, VIS: parts[2] / 100, CRW: parts[3] / 100, TRF: parts[4] / 100 };
        }
        if (Object.keys(e).length) return e;
        U.state.c.flights.push({ id: id, code: v.code, dir: v.dir, time: t, seats: seats, load: load / 100, mix: mix, handler: v.dir === 'dep' ? v.handler : null, island: v.dir === 'dep' ? v.island : null, reason: v.reason, by: U.roleName(), at: U.now() });
        U.saveC();
        U.rerun();
        flDir = v.dir;
        U.created('flight', id, v.code + ', ' + (v.dir === 'arr' ? 'arrival on-block ' : 'departure STD ') + S.clock(t) + ', ' + seats + ' seats (' + v.reason + ')', function () { U.removeFrom('flights', id); U.saveC(); U.rerun(); });
        return null;
      }
    });
  }

  function buildCreate() {
    var ovNew = document.getElementById('ovNew');
    ovNew.innerHTML = U.createBtn('override', 'Add roster override', 'ovBtn');
    var ob = document.getElementById('ovBtn');
    if (!ob.disabled) ob.addEventListener('click', function () { openOverride(ob); });
    var flNew = document.getElementById('flNew');
    flNew.innerHTML = U.createBtn('flight', 'Add ad-hoc flight', 'flBtn');
    var fb = document.getElementById('flBtn');
    if (!fb.disabled) fb.addEventListener('click', function () { openFlight(fb); });
    var ol = document.getElementById('ovList');
    ol.__html = null;
    if (!ol.__bound) {
      ol.addEventListener('click', function (e) {
        var b = e.target.closest('[data-ov-remove]');
        if (!b) return;
        var id = b.getAttribute('data-ov-remove'), o = U.state.c.overrides.filter(function (x) { return x.id === id; })[0];
        U.removeFrom('overrides', id); U.saveC(); U.audit('Removed', id, 'Roster override removed'); U.rerun();
        U.toast('Removed ' + id, { undo: function () { U.state.c.overrides.push(o); U.saveC(); U.audit('Restored', id, 'Roster override restored'); U.rerun(); } });
      });
      ol.__bound = true;
    }
    var fl = document.getElementById('flBody');
    fl.__html = null;
    if (!fl.__bound) {
      fl.addEventListener('click', function (e) {
        var t = e.target.closest('[data-fl-dir]');
        if (t) { flDir = t.getAttribute('data-fl-dir'); U.draw(true); }
      });
      fl.addEventListener('change', function (e) { if (e.target.hasAttribute('data-fl-all')) { flAll = e.target.checked; U.draw(true); } });
      fl.__bound = true;
    }
  }

  function updateCreate(m) {
    U.html(document.getElementById('ovList'), overridesHtml());
    U.html(document.getElementById('flBody'), flightsHtml(m));
  }

  QUI.start({ build: function (q) { build(q); buildCreate(); }, update: function (m, f) { update(m, f); updateCreate(m); } });
})();
