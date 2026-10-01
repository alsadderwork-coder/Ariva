(function () {
  'use strict';
  var U = QUI, S = QSim, QC = QCharts;
  var ZONES = ['CI-C', 'CI-D'];
  var ALLOWANCE = U.SLA.ALLOWANCE, RATE = U.SLA.RATE, CAP = U.SLA.CAP;
  var MONTH = U.SLA.MONTH, QUIET_DAYS = 27 - MONTH.length, QUIET_BINS = U.SLA.QUIET_BINS;
  var REASONS = ['Counters closed on airport instruction', 'Sensor or zone fault', 'Flight disruption outside the handler\'s control', 'Security directive'];
  var STATUS_TEXT = U.SLA.STATUS_TEXT, EXT = U.SLA.EXT, KPI = U.SLA.KPI, PARTY_ISL = U.SLA.PARTY_ISL;
  var disputes = null, pending = null, geomHash = null, signing = null;
  var F = U.F;
  var evalContract = U.sla.evalContract, evaluate = U.sla.evaluate, exclusionsToday = U.sla.exclusionsToday, kpiText = U.sla.kpiText, disputeFor = U.sla.disputeFor;
  function seeded() { return U.SEEDS.contracts[0]; }
  function contracts() {
    return U.list('contracts').filter(function (c) { return U.role() !== 'hbm' || c.party === 'Handler B'; });
  }
  function loadDisputes() { disputes = U.sla.loadDisputes(); }
  function saveDisputes() { U.sla.saveDisputes(disputes); }

  function build() {
    var body = document.getElementById('slaBody');
    body.__html = null;
    document.getElementById('kpis').innerHTML = '';
    document.getElementById('kpis').__html = null;
    loadDisputes();
    pending = null;
    if (!U.can('sla')) { body.innerHTML = U.denied('SLA and penalty data concern the airport and its handlers.'); return; }
    body.innerHTML =
      '<div class="grid cols-2">' +
      '<section class="panel" aria-labelledby="ctTitle"><div class="panel-head"><div><h2 id="ctTitle">Contract card: Handler B, check-in</h2><p>Synthetic contract terms for the demo.</p></div></div><div id="contract"></div></section>' +
      '<section class="panel" aria-labelledby="evTitle"><div class="panel-head"><div><h2 id="evTitle">Evaluation, September 2026 to date</h2><p>Final bins only; provisional bins are held until final.</p></div></div><div id="evalTbl"></div></section>' +
      '</div>' +
      '<section class="panel" id="contracts" aria-labelledby="csTitle"><div class="panel-head"><div><h2 id="csTitle">Contracts</h2><p>Drafts can be signed once; signed terms are locked and a change needs a new contract. Signed contracts are evaluated on today\'s live bins.</p></div><div class="panel-actions" id="ctNew"></div></div><div id="ctList"></div></section>' +
      '<section class="panel" id="exclusions" aria-labelledby="exTitle"><div class="panel-head"><div><h2 id="exTitle">Exclusions</h2><p>Events that remove bins from the evaluation, such as a security directive. Today\'s exclusions recompute the evaluation and mark the bins they cover.</p></div><div class="panel-actions" id="exNew"></div></div><div id="exList"></div></section>' +
      '<section class="panel" aria-labelledby="tbTitle"><div class="panel-head"><div><h2 id="tbTitle">Today\'s bins at islands C and D</h2><p>Contract C-001. Bins at or above 10 minutes, every breach and every excluded bin. Provisional until everyone who entered has left the queue.</p></div></div><div id="todayTbl"></div></section>' +
      '<section class="panel" aria-labelledby="dpTitle"><div class="panel-head"><div><h2 id="dpTitle">Disputes</h2><p>Raised, Under review, then Upheld or Rejected. Upheld bins are excluded from the penalty.</p></div></div><div id="disputes"></div><div id="raise"></div></section>' +
      '<section class="panel" aria-labelledby="epTitle"><div class="panel-head"><div><h2 id="epTitle">Evidence pack</h2><p>A JSON file built in the browser: interval data, zone profile version, calibration record, exclusions, disputes and a SHA-256 content hash.</p></div>' +
      '<button type="button" class="btn btn-primary" id="packBtn">' + U.icon('download') + 'Evidence pack (JSON)</button></div><div id="packPanel"></div></section>';
    document.getElementById('packBtn').addEventListener('click', makePack);
    signing = null;
    document.getElementById('ctNew').innerHTML = U.createBtn('contract', 'New contract', 'ctBtn');
    var cb = document.getElementById('ctBtn');
    if (!cb.disabled) cb.addEventListener('click', function () { openContract(cb); });
    document.getElementById('exNew').innerHTML = U.createBtn('exclusion', 'Add exclusion', 'exBtn');
    var eb = document.getElementById('exBtn');
    if (!eb.disabled) eb.addEventListener('click', function () { openExclusion(eb); });
    document.getElementById('ctList').addEventListener('click', onContractClick);
    document.getElementById('exList').addEventListener('click', onExclusionClick);
    var dpEl = document.getElementById('disputes');
    if (!dpEl.__bound) { dpEl.addEventListener('click', onDisputeClick); dpEl.__bound = true; }
    geomHash = null;
    QHash.sha256(QHash.canonical({ 'CI-C': QFloor.V12.zones['CI-C'], 'CI-D': QFloor.V12.zones['CI-D'] })).then(function (h) { geomHash = h; U.draw(true); });
  }

  function contract() {
    var rows = [
      ['Parties', 'DMO airport operator and Handler B (both fictional)'],
      ['Scope', 'Check-in queues at islands C and D (zones CI-C and CI-D)'],
      ['KPI', 'Passenger wait from the zone\'s entry line to its exit line, measured by sensors'],
      ['Threshold', '15 minutes at P90 per 15-minute bin, arrival-weighted'],
      ['Evaluation window', 'Calendar month; bins with at least one passenger'],
      ['Bin status', 'Provisional until every passenger who entered has exited, then final. Penalties use final bins only.'],
      ['Exclusions', 'Sensor outage over the zone for more than 5 minutes in the bin; security directives; counters closed on airport instruction; upheld disputes'],
      ['Penalty schedule', ALLOWANCE + ' breached bins a month allowed; USD ' + RATE + ' per further breached bin; cap USD ' + U.fmt(CAP) + ' a month'],
      ['Dispute window', '5 working days after the bin becomes final'],
      ['Signed zone profile', 'v12, published 2 Sep 2026. Geometry hash ' + (geomHash ? geomHash.slice(0, 16) + '...' : 'computing')]
    ];
    return '<dl class="kv">' + rows.map(function (r) { return '<dt>' + U.esc(r[0]) + '</dt><dd>' + U.esc(r[1]) + '</dd>'; }).join('') + '</dl>';
  }

  function evalTable(ev) {
    var rows = MONTH.map(function (r) {
      var dp = r.dispute ? disputes.filter(function (x) { return x.id === r.dispute; })[0] : null;
      var ex = r.ex ? r.ex.breaches + ' (' + U.esc(r.ex.reason) + ')' : dp && dp.status === 'upheld' ? '1 (dispute ' + dp.id + ' upheld)' : '0';
      var held = dp && (dp.status === 'raised' || dp.status === 'review') ? '1 (' + dp.id + ', ' + STATUS_TEXT[dp.status].toLowerCase() + ')' : '0';
      return '<tr><th scope="row">' + r.d + ' Sep</th><td class="num">' + r.ev + '</td><td class="num">' + r.br + '</td><td>' + ex + '</td><td>' + held + '</td></tr>';
    });
    var t = ev.today;
    rows.push('<tr class="row-hl"><th scope="row">28 Sep (today, live)</th><td class="num">' + t.ev + (t.prov ? ' <span class="small muted">+' + t.prov + ' provisional</span>' : '') + '</td><td class="num">' + t.br +
      (t.provBr ? ' <span class="small muted">+' + t.provBr + ' provisional</span>' : '') + '</td><td>' + t.excluded + '</td><td>' + t.held + '</td></tr>');
    var mo = ev.month;
    return '<div class="table-wrap"><table class="tbl"><thead><tr><th scope="col">Day</th><th scope="col" class="num">Bins evaluated</th><th scope="col" class="num">Breaches</th><th scope="col">Excluded</th><th scope="col">Held (dispute open)</th></tr></thead><tbody>' + rows.join('') +
      '</tbody><tfoot><tr><th scope="row">Month</th><td class="num">' + U.fmt(mo.ev) + '</td><td class="num">' + mo.br + '</td><td>' + mo.excluded + '</td><td>' + mo.held + '</td></tr></tfoot></table></div>' +
      '<p class="small muted" style="margin-block-start:6px">' + QUIET_DAYS + ' other days had no breach (' + U.fmt(QUIET_BINS) + ' bins) and are not listed.</p>' +
      '<dl class="kv" style="margin-block-start:8px"><dt>Counted breaches</dt><dd>' + mo.counted + ' (after exclusions and open disputes)</dd><dt>Allowance</dt><dd>' + ALLOWANCE + ' a month</dd><dt>Penalised bins</dt><dd>' + mo.penalised + '</dd><dt>Resulting penalty</dt><dd><strong>USD ' + U.fmt(mo.penalty) + '</strong>' + (t.provBr ? ' <span class="small muted">(' + t.provBr + ' provisional breach' + (t.provBr > 1 ? 'es' : '') + ' not yet counted)</span>' : '') + '</dd></dl>';
  }

  function todayTable(ev) {
    var list = ev.bins.filter(function (b) { return b.p90 >= 10 || b.breach || b.ex; }).sort(function (a, b) { return a.start - b.start || (a.zone < b.zone ? -1 : 1); });
    if (!list.length) return '<p class="muted small">No bin at or above 10 minutes so far today.</p>';
    return '<div class="table-wrap"><table class="tbl"><thead><tr><th scope="col">Bin</th><th scope="col">Island</th><th scope="col" class="num">P90, min</th><th scope="col" class="num">Passengers</th><th scope="col">Status</th><th scope="col">Result</th><th scope="col">Dispute</th><th scope="col">Profile</th></tr></thead><tbody>' +
      list.map(function (b) {
        var dp = disputeFor(S.DATE, b.zone, S.clock(b.start));
        return '<tr><th scope="row" class="mono">' + S.clock(b.start) + ' to ' + S.clock(b.start + 15) + '</th><td>' + b.zone.slice(3) + '</td><td class="num">' + QC.fmt(b.p90, { decimals: 1 }) + '</td><td class="num">' + U.fmt(b.pax) + '</td>' +
          '<td><span class="mark ' + (b.status === 'final' ? 'final' : 'prov') + '">' + (b.status === 'final' ? 'Final' : 'Provisional') + '</span></td><td>' + (b.ex ? U.st('unknown', 'Excluded (' + b.ex.id + ')') + (b.breach ? '<div class="small muted">Breach not counted</div>' : '') : b.breach ? U.st('crit', 'Breach') : U.st('good', 'Met')) + '</td>' +
          '<td>' + (dp ? dp.id + ', ' + STATUS_TEXT[dp.status] : '') + '</td><td>' + b.profile + '</td></tr>';
      }).join('') + '</tbody></table></div>';
  }

  function stepper(d) {
    var order = ['raised', 'review'];
    var done = function (s) { return order.indexOf(s) < order.indexOf(d.status) || (d.status === 'upheld' || d.status === 'rejected'); };
    var items = order.map(function (s) {
      var cls = d.status === s ? 'cur' : done(s) ? 'done' : '';
      return '<li class="' + cls + '">' + STATUS_TEXT[s] + '</li>';
    });
    var fin = d.status === 'upheld' ? '<li class="cur ok">' + U.icon('check').replace('<svg', '<svg style="width:12px;height:12px"') + 'Upheld</li>' : d.status === 'rejected' ? '<li class="cur bad">Rejected</li>' : '<li>Upheld or Rejected</li>';
    return '<ol class="stepper" aria-label="Dispute status">' + items.join('') + fin + '</ol>';
  }

  function disputesHtml() {
    return disputes.map(function (d) {
      var acts = '';
      if (pending && pending.id === d.id) {
        acts = '<div class="confirm-inline" role="group" aria-label="Confirm decision">' + (pending.action === 'uphold' ? 'Uphold ' + d.id + '? The bin is excluded from the penalty.' : 'Reject ' + d.id + '? The breach counts toward the penalty.') +
          ' <button type="button" class="btn btn-primary" data-act="confirm" data-id="' + d.id + '" data-key="cf-' + d.id + '">Confirm</button><button type="button" class="btn" data-act="cancel" data-id="' + d.id + '">Cancel</button></div>';
      } else if (U.can('dispute.decide')) {
        if (d.status === 'raised') acts = '<button type="button" class="btn" data-act="review" data-id="' + d.id + '">Start review</button>';
        if (d.status === 'review') acts = '<button type="button" class="btn" data-act="uphold" data-id="' + d.id + '">Uphold</button><button type="button" class="btn" data-act="reject" data-id="' + d.id + '">Reject</button>';
      } else if (d.status === 'raised' || d.status === 'review') acts = '<span class="small muted">Waiting for the Terminal duty manager.</span>';
      return '<div class="dispute"><div class="row" style="justify-content:space-between"><strong>' + d.id + '</strong><span class="small muted">Bin ' + d.date + ' ' + d.bin + ', island ' + d.zone.slice(3) + '</span></div>' +
        stepper(d) + '<p class="small">' + U.esc(d.reason) + '</p><p class="small muted">Raised by ' + U.esc(d.raisedBy) + '. ' + d.history.map(function (h) { return STATUS_TEXT[h.status] + ' ' + h.at; }).join('; ') + '.</p>' +
        '<div class="row">' + acts + '</div></div>';
    }).join('');
  }

  function raiseHtml(ev) {
    if (!U.can('dispute.raise')) return '<p class="small muted" style="margin-block-start:10px">Only the handler can raise a dispute. The Terminal duty manager reviews and decides.</p>';
    var opts = ev.bins.filter(function (b) { return b.breach && b.status === 'final' && !disputeFor(S.DATE, b.zone, S.clock(b.start)); });
    if (!opts.length) return '<p class="small muted" style="margin-block-start:10px">No final breached bin today is open for a dispute yet. Disputes apply to final bins; play the clock past 19:30 to see today\'s breaches become final.</p>';
    return '<div class="row" style="margin-block-start:12px" role="group" aria-label="Raise a dispute"><label class="field" for="dpBin">Bin <select id="dpBin">' + opts.map(function (b) {
      return '<option value="' + b.zone + '|' + S.clock(b.start) + '">' + S.clock(b.start) + ', island ' + b.zone.slice(3) + ', P90 ' + QC.fmt(b.p90, { decimals: 1 }) + '</option>';
    }).join('') + '</select></label><label class="field" for="dpReason">Reason <select id="dpReason">' + REASONS.map(function (r) { return '<option>' + U.esc(r) + '</option>'; }).join('') + '</select></label>' +
      '<button type="button" class="btn btn-primary" id="dpRaise">Raise dispute</button></div>';
  }

  function onDisputeClick(e) {
    var b = e.target.closest('[data-act]');
    if (!b) return;
    var id = b.getAttribute('data-id'), act = b.getAttribute('data-act');
    var d = disputes.filter(function (x) { return x.id === id; })[0];
    if (!d) return;
    var now = S.DATE + ' ' + S.clock(U.now());
    if (act === 'review' && U.can('dispute.decide')) { d.status = 'review'; d.history.push({ status: 'review', at: now }); saveDisputes(); U.audit('Started review', d.id, 'Dispute under review'); }
    else if ((act === 'uphold' || act === 'reject') && U.can('dispute.decide')) { pending = { id: id, action: act }; }
    else if (act === 'cancel') pending = null;
    else if (act === 'confirm' && pending && U.can('dispute.decide')) {
      d.status = pending.action === 'uphold' ? 'upheld' : 'rejected';
      d.history.push({ status: d.status, at: now });
      pending = null; saveDisputes();
      U.audit(d.status === 'upheld' ? 'Upheld' : 'Rejected', d.id, 'Bin ' + d.date + ' ' + d.bin + ', island ' + d.zone.slice(3) + (d.status === 'upheld' ? ': excluded from the penalty' : ': breach counts toward the penalty'));
    }
    U.draw(true);
  }

  function bindRaise() {
    var btn = document.getElementById('dpRaise');
    if (!btn || btn.__b) return;
    btn.__b = true;
    btn.addEventListener('click', function () {
      var v = document.getElementById('dpBin').value.split('|');
      var n = disputes.filter(function (x) { return x.date === S.DATE; }).length + 1;
      disputes.push({ id: 'D-0928-' + n, date: S.DATE, bin: v[1], zone: v[0], reason: document.getElementById('dpReason').value, status: 'raised',
        raisedBy: 'Handler B station manager', history: [{ status: 'raised', at: S.DATE + ' ' + S.clock(U.now()) }] });
      saveDisputes();
      U.audit('Raised dispute', disputes[disputes.length - 1].id, 'Bin ' + S.DATE + ' ' + v[1] + ', island ' + v[0].slice(3) + ': ' + document.getElementById('dpReason').value);
      U.status('Dispute raised. The Terminal duty manager can now start the review.');
      U.draw(true);
    });
  }

  /* contracts */

  function termsHtml(c) {
    var rows = [['Parties', 'DMO airport operator and ' + c.party], ['Scope', 'Islands ' + c.islands.join(' and ') + ' (' + c.islands.map(function (i) { return 'CI-' + i; }).join(', ') + ')'],
      ['KPI', KPI[c.kpi]], ['Breach rule', kpiText(c)], ['Evaluation window', (c.window === 'weekly' ? 'Calendar week' : 'Calendar month') + '; bins with at least ' + c.minPax + ' passenger' + (c.minPax > 1 ? 's' : '')],
      ['Exclusions', c.exclusions.length ? c.exclusions.map(function (x) { return EXT[x]; }).join('; ') : 'None'],
      ['Penalty schedule', c.allowance + ' breached bins a ' + (c.window === 'weekly' ? 'week' : 'month') + ' allowed; USD ' + U.fmt(c.penalty) + ' per further breached bin; cap USD ' + U.fmt(c.cap)],
      ['Dispute window', c.disputeDays + ' working days after the bin becomes final'], ['Signed zone profile', c.profile], ['Effective', c.effective]];
    return '<dl class="kv">' + rows.map(function (r) { return '<dt>' + U.esc(r[0]) + '</dt><dd>' + U.esc(r[1]) + '</dd>'; }).join('') + '</dl>';
  }
  function contractsHtml(m) {
    var list = contracts();
    var conf = '';
    if (signing) {
      var sc = list.filter(function (c) { return c.id === signing; })[0];
      if (sc) conf = '<div class="confirm-inline" role="group" aria-label="Confirm signature" style="margin-block-end:10px">Sign ' + sc.id + ' with ' + U.esc(sc.party) + '? Signed terms are locked; a change needs a new contract. ' +
        '<button type="button" class="btn btn-primary" data-ct="confirm" data-id="' + sc.id + '" data-key="ctc-' + sc.id + '">Sign contract</button><button type="button" class="btn" data-ct="cancel" data-id="' + sc.id + '">Cancel</button></div>';
    }
    var rows = list.map(function (c) {
      var ev = evalContract(c, m), acts = '';
      if (c.status === 'draft' && U.canCreate('contract')) acts = '<button type="button" class="btn" data-ct="sign" data-id="' + c.id + '" data-key="cts-' + c.id + '">Sign</button><button type="button" class="btn" data-ct="delete" data-id="' + c.id + '">Delete draft</button>';
      var evTxt = !ev.active ? '<span class="small muted">' + U.esc(ev.why) + '</span>' :
        '<span data-ct-eval="' + c.id + '">' + ev.ev + ' final bins, ' + ev.br + ' breach' + (ev.br === 1 ? '' : 'es') + (ev.excluded ? ', ' + ev.excluded + ' excluded' : '') + '</span>' +
        '<div class="small muted">Counted ' + ev.counted + (ev.prov ? '; ' + ev.prov + ' provisional' + (ev.provBr ? ' (' + ev.provBr + ' in breach)' : '') : '') + '</div>';
      return '<tr' + U.rowCls(c.id) + '><th scope="row" class="mono">' + c.id + '</th><td><strong>' + U.esc(c.party) + '</strong><div class="small muted">Islands ' + c.islands.join(', ') + '</div>' +
        '<details class="small"><summary>Terms</summary>' + termsHtml(c) + '</details></td>' +
        '<td class="small">' + U.esc(kpiText(c)) + '</td><td class="small">' + c.allowance + ' allowed, USD ' + U.fmt(c.penalty) + ' per bin, cap USD ' + U.fmt(c.cap) + '</td>' +
        '<td>' + c.profile + '</td><td class="mono">' + c.effective + '</td>' +
        '<td>' + (c.status === 'signed' ? '<span class="pill ok">Signed</span><div class="small muted">' + U.esc(c.signedAt) + '</div>' : '<span class="pill warn">Draft</span>') + '</td>' +
        '<td class="small">' + evTxt + '</td><td class="actions">' + (acts || '<span class="small muted">' + (c.status === 'signed' ? 'Locked' : 'Read only') + '</span>') + '</td></tr>';
    });
    return conf + '<div class="table-wrap"><table class="tbl stack-sm"><thead><tr><th scope="col">ID</th><th scope="col">Party and scope</th><th scope="col">KPI</th><th scope="col">Penalty</th><th scope="col">Profile</th><th scope="col">Effective</th><th scope="col">Status</th><th scope="col">Today</th><th scope="col">Actions</th></tr></thead><tbody>' +
      rows.join('') + '</tbody></table></div>';
  }

  function onContractClick(e) {
    var b = e.target.closest('[data-ct]');
    if (!b) return;
    var id = b.getAttribute('data-id'), act = b.getAttribute('data-ct'), c = U.state.c;
    var ct = c.contracts.filter(function (x) { return x.id === id; })[0];
    if (!ct || !U.canCreate('contract')) return;
    if (act === 'sign' && ct.status === 'draft') { signing = id; U.draw(true); var cf = document.querySelector('[data-ct="confirm"]'); if (cf) cf.focus(); return; }
    if (act === 'cancel') { signing = null; U.draw(true); var sb = document.querySelector('[data-ct="sign"][data-id="' + id + '"]'); if (sb) sb.focus(); return; }
    if (act === 'confirm' && signing === id && ct.status === 'draft') {
      ct.status = 'signed'; ct.signedAt = S.DATE + ' ' + S.clock(U.now()); ct.by = U.roleName();
      signing = null; U.saveC();
      U.audit('Signed', id, ct.party + ' contract signed against zone profile ' + ct.profile + ', effective ' + ct.effective);
      U.flash(id); U.draw(true);
      U.toast('Signed ' + id + '; it is evaluated on today\'s bins');
      var h = document.getElementById('csTitle'); if (h) { h.setAttribute('tabindex', '-1'); h.focus(); }
      return;
    }
    if (act === 'delete' && ct.status === 'draft') {
      U.removeFrom('contracts', id); U.saveC(); U.audit('Deleted draft', id, ct.party + ' contract draft deleted'); U.draw(true);
      U.toast('Deleted draft ' + id, { undo: function () { U.state.c.contracts.push(ct); U.saveC(); U.audit('Restored draft', id, ct.party + ' contract draft restored'); U.draw(true); } });
      var nb = document.getElementById('ctBtn'); if (nb) nb.focus();
    }
  }

  function openContract(trig) {
    var id = U.nextId('contract');
    var vers = U.publishedVersions();
    var html = F.text('cid', 'Contract ID', id, { readonly: true }) +
      F.row(F.select('party', 'Party', [['Handler A', 'Handler A'], ['Handler B', 'Handler B']], 'Handler A'),
        F.checks('islands', 'Scope (islands)', [['A', 'Island A'], ['B', 'Island B'], ['C', 'Island C'], ['D', 'Island D']], ['A', 'B'], { req: true })) +
      F.section('Measure and threshold') +
      F.select('kpi', 'KPI', [['p90', KPI.p90], ['share', KPI.share], ['overflow', KPI.overflow]], 'p90') +
      F.row(F.text('threshold', 'Threshold', 15, { type: 'number', min: 1, max: 120, unit: 'min', inputmode: 'numeric', req: true }),
        F.text('pct', 'Percentile', 90, { type: 'number', min: 50, max: 99, unit: 'th', inputmode: 'numeric', req: true })) +
      F.row(F.select('binSize', 'Bin size', [[15, '15 minutes'], [30, '30 minutes'], [60, '60 minutes']], 15), F.select('window', 'Evaluation window', [['monthly', 'Calendar month'], ['weekly', 'Calendar week']], 'monthly')) +
      F.text('minPax', 'Minimum passengers per bin', 1, { type: 'number', min: 1, max: 50, inputmode: 'numeric', hint: 'Bins with fewer passengers are not evaluated.' }) +
      F.checks('exclusions', 'Exclusions', Object.keys(EXT).map(function (k) { return [k, EXT[k]]; }), ['sensor', 'security', 'instruction', 'dispute']) +
      F.section('Penalties') +
      F.row(F.text('allowance', 'Allowance', 6, { type: 'number', min: 0, max: 200, unit: 'bins', inputmode: 'numeric' }), F.text('penalty', 'Penalty per breached bin', 350, { type: 'number', min: 0, max: 100000, unit: 'USD', inputmode: 'numeric' })) +
      F.row(F.text('cap', 'Cap per window', 10000, { type: 'number', min: 0, max: 1000000, unit: 'USD', inputmode: 'numeric' }), F.text('disputeDays', 'Dispute window', 5, { type: 'number', min: 1, max: 30, unit: 'working days', inputmode: 'numeric' })) +
      F.section('Signature') +
      F.row(F.select('profile', 'Signed zone profile', vers.map(function (v) { return [v, v + (v === 'v12' ? ' (published 2 Sep 2026)' : ' (published in this demo)')]; }), vers[vers.length - 1], { hint: 'Only published versions can be signed.' }),
        F.text('effective', 'Effective date', S.DATE, { req: true, placeholder: 'YYYY-MM-DD', inputmode: 'numeric' })) +
      F.note('The contract is saved as a draft. Sign it from the list; signed terms are locked.');
    U.openDrawer({
      title: 'New contract', html: html, trigger: trig, returnFocus: 'ctBtn', submitLabel: 'Create draft',
      onOpen: function (form) {
        var lastParty = 'Handler A';
        function sync() {
          var v = U.formValues(form);
          if (v.party !== lastParty) {
            lastParty = v.party;
            form.querySelectorAll('input[name="islands"]').forEach(function (x) { x.checked = PARTY_ISL[v.party].indexOf(x.value) >= 0; });
          }
          var pl = form.querySelector('[data-f="pct"] label').firstChild, pu = form.querySelector('[data-f="pct"] .unit'), tu = form.querySelector('[data-f="threshold"] .unit');
          form.querySelector('[data-f="pct"]').hidden = v.kpi === 'overflow';
          pl.textContent = v.kpi === 'share' ? 'Target share' : 'Percentile';
          pu.textContent = v.kpi === 'share' ? '%' : 'th';
          tu.textContent = v.kpi === 'overflow' ? 'min of overflow' : 'min';
        }
        form.addEventListener('change', sync); sync();
      },
      onSubmit: function (v) {
        var e = {}, n = function (k) { return U.V.num(v[k]); }, i = function (k) { return U.V.int(v[k]); };
        var isl = v.islands || [], own = PARTY_ISL[v.party];
        if (!isl.length) e.islands = 'Choose at least one island.';
        else { var bad = isl.filter(function (x) { return own.indexOf(x) < 0; }); if (bad.length) e.islands = 'Island ' + bad.join(' and ') + (bad.length > 1 ? ' belong' : ' belongs') + ' to the other handler; ' + v.party + ' checks in at islands ' + own.join(' and ') + '.'; }
        var size = +v.binSize, th = n('threshold'), pct = i('pct');
        if (v.kpi === 'overflow') { if (th == null || th < 1 || th > size) e.threshold = 'Overflow minutes must be between 1 and the bin size (' + size + ').'; }
        else if (th == null || th < 1 || th > 120) e.threshold = 'Threshold must be between 1 and 120 minutes.';
        if (v.kpi === 'p90' && (pct == null || pct < 50 || pct > 99)) e.pct = 'Percentile must be a whole number from 50 to 99.';
        if (v.kpi === 'share' && (pct == null || pct < 50 || pct > 100)) e.pct = 'Target share must be a whole number from 50 to 100%.';
        var mp = i('minPax'); if (mp == null || mp < 1 || mp > 50) e.minPax = 'Minimum passengers must be a whole number from 1 to 50.';
        var al = i('allowance'); if (al == null || al < 0 || al > 200) e.allowance = 'Allowance must be a whole number from 0 to 200 bins.';
        var pe = n('penalty'); if (pe == null || pe < 0 || pe > 100000) e.penalty = 'Penalty must be between 0 and 100,000 USD.';
        var cap = n('cap'); if (cap == null || cap < 0) e.cap = 'Enter a cap in USD.'; else if (pe != null && cap < pe) e.cap = 'The cap must be at least one penalty (USD ' + U.fmt(pe) + ').';
        var dd = i('disputeDays'); if (dd == null || dd < 1 || dd > 30) e.disputeDays = 'Dispute window must be 1 to 30 working days.';
        if (U.publishedVersions().indexOf(v.profile) < 0) e.profile = 'Choose a published zone profile.';
        if (!U.V.date(v.effective)) e.effective = 'Enter the date as YYYY-MM-DD, for example 2026-10-01.';
        else {
          var same = U.list('contracts').filter(function (c) { return c.party === v.party && c.effective === v.effective; })[0];
          if (same) e.effective = same.id + ' for ' + v.party + ' already takes effect on ' + v.effective + '. A new contract needs a later effective date.';
        }
        if (Object.keys(e).length) return e;
        var ct = { id: id, party: v.party, islands: isl.slice().sort(), kpi: v.kpi, threshold: th, pct: v.kpi === 'overflow' ? null : pct, binSize: size, window: v.window, minPax: mp,
          exclusions: v.exclusions || [], allowance: al, penalty: pe, cap: cap, disputeDays: dd, profile: v.profile, effective: v.effective, status: 'draft', createdBy: U.roleName(), createdAt: U.now() };
        U.state.c.contracts.push(ct);
        U.created('contract', id, 'Draft contract with ' + v.party + ', islands ' + ct.islands.join(' and ') + ': ' + kpiText(ct), function () { U.removeFrom('contracts', id); }, function () { U.draw(true); });
        return null;
      }
    });
  }

  /* exclusions */

  function exclusionsHtml(m) {
    var list = U.list('exclusions').filter(function (x) { return U.role() !== 'hbm' || x.zones.some(function (z) { return z === 'CI-C' || z === 'CI-D'; }); });
    var today = evaluate(m).today;
    var rows = list.map(function (x) {
      var tdy = x.date === S.DATE;
      var eff = tdy ? (today.exBins[x.id] || 0) + ' bin' + ((today.exBins[x.id] || 0) === 1 ? '' : 's') + ' of C-001 today' : (x.seeded ? 'In the September evaluation' : '');
      return '<tr' + U.rowCls(x.id) + '><th scope="row" class="mono">' + x.id + '</th><td>' + U.esc(EXT[x.type]) + '</td><td>' + x.zones.map(function (z) { return 'Island ' + z.slice(3); }).join(', ') + '</td>' +
        '<td class="mono">' + x.date + '<div>' + S.clock(x.from) + ' to ' + (x.to >= 1440 ? '24:00' : S.clock(x.to)) + '</div></td><td class="small">' + U.esc(x.reason) + '</td><td class="mono small">' + U.esc(x.ref) + '</td>' +
        '<td class="small" data-ex-eff="' + x.id + '">' + eff + '</td><td class="actions">' + (!x.seeded && U.canCreate('exclusion') ? '<button type="button" class="btn" data-ex="remove" data-id="' + x.id + '">Remove</button>' : '<span class="small muted">' + U.esc(x.by || '') + '</span>') + '</td></tr>';
    });
    return '<div class="table-wrap"><table class="tbl stack-sm"><thead><tr><th scope="col">ID</th><th scope="col">Type</th><th scope="col">Zones</th><th scope="col">Window</th><th scope="col">Reason</th><th scope="col">Reference</th><th scope="col">Effect</th><th scope="col">Actions</th></tr></thead><tbody>' + rows.join('') + '</tbody></table></div>';
  }

  function onExclusionClick(e) {
    var b = e.target.closest('[data-ex="remove"]');
    if (!b || !U.canCreate('exclusion')) return;
    var id = b.getAttribute('data-id'), x = U.state.c.exclusions.filter(function (y) { return y.id === id; })[0];
    if (!x) return;
    U.removeFrom('exclusions', id); U.saveC(); U.audit('Removed', id, 'Exclusion removed; evaluation recomputed'); U.draw(true);
    U.toast('Removed ' + id, { undo: function () { U.state.c.exclusions.push(x); U.saveC(); U.audit('Restored', id, 'Exclusion restored'); U.draw(true); } });
    var nb = document.getElementById('exBtn'); if (nb) nb.focus();
  }

  function openExclusion(trig) {
    var id = U.nextId('exclusion'), bs = Math.floor(U.now() / 15) * 15;
    var html = F.text('xid', 'Exclusion ID', id, { readonly: true }) +
      F.select('type', 'Type', Object.keys(EXT).filter(function (k) { return k !== 'dispute'; }).map(function (k) { return [k, EXT[k]]; }), 'security') +
      F.checks('zones', 'Zones', [['CI-A', 'Island A'], ['CI-B', 'Island B'], ['CI-C', 'Island C'], ['CI-D', 'Island D']], ['CI-C'], { req: true }) +
      F.text('date', 'Date', S.DATE, { readonly: true, hint: 'Exclusions entered here apply to the simulated day.' }) +
      F.row(F.select('from', 'From', U.timeOptions(0, 1425, 15), Math.max(0, bs - 15)), F.select('to', 'To', U.timeOptions(15, 1440, 15), Math.min(1440, bs + 30))) +
      F.text('reason', 'Reason', '', { req: true, placeholder: 'For example: security directive, all screening slowed' }) +
      F.text('ref', 'Reference', '', { req: true, placeholder: 'SD-2026-0928', hint: 'Directive, incident or instruction number.' }) +
      '<div class="preview" id="exPrev" aria-live="polite"></div>';
    U.openDrawer({
      title: 'Add exclusion', html: html, trigger: trig, returnFocus: 'exBtn',
      onOpen: function (form) {
        function sync() {
          var v = U.formValues(form), from = +v.from, to = +v.to, ct = seeded(), m = U.now();
          if (!(to > from) || !(v.zones || []).length) { document.getElementById('exPrev').innerHTML = '<strong>Effect on C-001 today</strong>Choose zones and a window that ends after it starts.'; return; }
          var bins = evalContract(ct, m).bins.filter(function (b) { return (v.zones || []).indexOf(b.zone) >= 0 && from < b.start + 15 && to > b.start; });
          var br = bins.filter(function (b) { return b.breach && b.status === 'final'; }).length;
          var applies = ct.exclusions.indexOf(v.type) >= 0;
          document.getElementById('exPrev').innerHTML = '<strong>Effect on C-001 today</strong>' + (!applies ? 'C-001 does not list this exclusion type, so its evaluation would not change.' :
            bins.length + ' evaluated bin' + (bins.length === 1 ? '' : 's') + ' so far in this window' + (br ? ', including ' + br + ' final breach' + (br > 1 ? 'es' : '') + ' that would no longer count' : '') + '.');
        }
        form.addEventListener('change', sync); sync();
      },
      onSubmit: function (v) {
        var e = {}, from = +v.from, to = +v.to, zones = v.zones || [];
        if (!zones.length) e.zones = 'Choose at least one zone.';
        if (!(to > from)) e.to = 'The window must end after it starts.';
        if (!v.reason || v.reason.length < 5) e.reason = 'Enter a reason of at least 5 characters.';
        if (!v.ref) e.ref = 'Enter a reference.';
        else if (!/^[A-Za-z0-9][A-Za-z0-9 ._\/-]{1,39}$/.test(v.ref)) e.ref = 'Use letters, digits, spaces, dots, slashes or dashes (up to 40 characters).';
        else if (U.list('exclusions').some(function (x) { return x.ref.toLowerCase() === v.ref.toLowerCase(); })) e.ref = 'Reference ' + v.ref + ' is already used by another exclusion.';
        if (!e.to && zones.length) {
          var clash = exclusionsToday().filter(function (x) { return x.type === v.type && x.from < to && x.to > from && x.zones.some(function (z) { return zones.indexOf(z) >= 0; }); })[0];
          if (clash) e.from = clash.id + ' already excludes ' + EXT[clash.type].toLowerCase() + ' from ' + S.clock(clash.from) + ' to ' + S.clock(clash.to) + ' on an overlapping zone.';
        }
        if (Object.keys(e).length) return e;
        var x = { id: id, type: v.type, zones: zones, date: S.DATE, from: from, to: to, reason: v.reason, ref: v.ref, by: U.roleName(), at: U.now() };
        U.state.c.exclusions.push(x);
        U.created('exclusion', id, EXT[v.type] + ', ' + zones.join(', ') + ', ' + S.clock(from) + ' to ' + S.clock(to) + ' (' + v.ref + ')', function () { U.removeFrom('exclusions', id); }, function () { U.draw(true); });
        return null;
      }
    });
  }

  function calRecords() {
    return U.state.c.calibrations.map(function (r) {
      var sn = U.state.c.sensors.filter(function (x) { return x.id === r.sensor; })[0];
      if (!sn || ZONES.indexOf(sn.zone) < 0) return null;
      return { sensor: r.sensor, type: sn.type, zone: sn.zone, last_calibration: r.date + ' ' + S.clock(r.at), method: r.method, sample_size: r.sample, count_accuracy_pct: r.accuracy, wait_error_min: r.waitErr,
        pass_threshold_pct: r.threshold, result: r.pass ? 'pass' : 'fail', record: r.id, notes: r.notes || '' };
    }).filter(Boolean);
  }

  function makePack() {
    var m = U.now(), ev = evaluate(m), day = U.day;
    var sensors = S.SENSORS.filter(function (s) { return s.zone === 'CI-C' || s.zone === 'CI-D'; }).map(function (s) {
      var k = parseInt(s.id.slice(2), 10);
      return { sensor: s.id, type: s.type, zone: s.zone, last_calibration: '2026-09-' + (10 + (k % 12)), method: 'Manual count validation, 2 sessions of 30 min', count_accuracy_pct: 96 + (k % 4) * 0.6 };
    });
    var pack = {
      schema: 'dmo-qms/evidence-pack/1',
      synthetic: true,
      notice: 'Prototype, synthetic data. Not a real evaluation. Demo International Airport and Handler B are fictional.',
      airport: 'DMO',
      generated_at_simulated: S.DATE + 'T' + S.clock(m),
      seed: day.seed,
      contract: { handler: 'Handler B', zones: ZONES, kpi: 'Wait from entry line to exit line', threshold_min: 15, statistic: 'P90 per 15-minute bin, arrival-weighted', window: 'monthly',
        allowance_bins: ALLOWANCE, penalty_usd_per_bin: RATE, cap_usd: CAP },
      zone_profile: { version: U.profileAt(m), signed_version: 'v12', signed: '2026-09-02', geometry_hash_sha256: geomHash,
        geometry: { 'CI-C': U.geometryAt(m).zones['CI-C'], 'CI-D': U.geometryAt(m).zones['CI-D'] } },
      calibration: sensors.concat(calRecords()),
      exclusions: U.list('exclusions').filter(function (x) { return x.zones.some(function (z) { return ZONES.indexOf(z) >= 0; }); }).map(function (x) {
        var r = MONTH.filter(function (d) { return x.seeded && x.date === '2026-09-' + (d.d < 10 ? '0' : '') + d.d; })[0];
        var tb = ev.bins.filter(function (b) { return b.ex && b.ex.id === x.id; });
        return { id: x.id, date: x.date, window: S.clock(x.from) + '-' + (x.to >= 1440 ? '24:00' : S.clock(x.to)), type: x.type, zones: x.zones, reason: x.reason, reference: x.ref,
          bins: r && r.ex ? r.ex.bins : tb.length, breaches_excluded: r && r.ex ? r.ex.breaches : tb.filter(function (b) { return b.breach && b.status === 'final'; }).length };
      }),
      intervals: ev.bins.map(function (b) { var o = { zone: b.zone, bin_start: S.DATE + 'T' + S.clock(b.start), bin_end: S.DATE + 'T' + S.clock(b.start + 15), passengers: Math.round(b.pax), p90_wait_min: Math.round(b.p90 * 10) / 10, status: b.status, breach: b.breach, profile: b.profile }; if (b.ex) o.excluded_by = b.ex.id; return o; }),
      evaluation: { month: '2026-09', bins_evaluated: ev.month.ev, breaches: ev.month.br, excluded: ev.month.excluded, held_for_dispute: ev.month.held, counted: ev.month.counted, penalised_bins: ev.month.penalised, penalty_usd: ev.month.penalty,
        provisional_breaches_not_counted: ev.today.provBr },
      disputes: disputes,
      hash_method: 'SHA-256 over canonical JSON (keys sorted, no whitespace) of this object without the content_hash field'
    };
    QHash.sha256(QHash.canonical(pack)).then(function (h) {
      pack.content_hash = h;
      var text = JSON.stringify(pack, null, 2);
      U.offerFile(document.getElementById('packPanel'), { filename: 'evidence-pack-handler-b-' + S.DATE + '-' + S.clock(m).replace(':', '') + '.json', mime: 'application/json', text: text, title: 'Evidence pack (JSON)',
        note: 'content_hash: ' + h + '. To verify, remove content_hash, serialise with sorted keys and no whitespace, and hash with SHA-256.' });
    });
  }

  function update(m) {
    if (!U.can('sla')) return;
    var ev = evaluate(m), mo = ev.month, t = ev.today;
    U.html(document.getElementById('kpis'), [
      U.kpi({ hero: true, level: mo.penalised > 0 ? 'crit' : '', label: 'Breached bins this month, counted after exclusions', value: String(mo.counted), sub: 'Allowance ' + ALLOWANCE + '; ' + mo.excluded + ' excluded, ' + mo.held + ' held for dispute' }),
      U.kpi({ label: 'Penalty to date', value: 'USD ' + U.fmt(mo.penalty), sub: mo.penalised + ' penalised bin' + (mo.penalised === 1 ? '' : 's') + ' at USD ' + RATE }),
      U.kpi({ label: 'Today\'s breaches', value: String(t.br + t.provBr), sub: t.br + ' final, ' + t.provBr + ' provisional' }),
      U.kpi({ label: 'Open disputes', value: String(disputes.filter(function (d) { return d.status === 'raised' || d.status === 'review'; }).length), sub: 'Decided by the Terminal duty manager' })
    ].join(''));
    U.html(document.getElementById('contract'), contract());
    U.html(document.getElementById('evalTbl'), evalTable(ev));
    U.html(document.getElementById('todayTbl'), todayTable(ev));
    U.html(document.getElementById('ctList'), contractsHtml(m));
    U.html(document.getElementById('exList'), exclusionsHtml(m));
    U.html(document.getElementById('disputes'), disputesHtml());
    U.html(document.getElementById('raise'), raiseHtml(ev));
    bindRaise();
  }

  QUI.start({ build: build, update: update });
})();
