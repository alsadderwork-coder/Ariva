/*
  create.js: the shared create pattern for the DMO QMS prototype.
  A "New ..." button opens a side drawer (a modal dialog with a focus trap, Esc to close
  and focus returned to the button), with labelled fields, inline validation, a Create
  button and Cancel. Success shows a toast with Undo and highlights the new row. Every
  create writes an audit entry. Seeded objects, create rights and IDs live here too.
*/
(function (global) {
  'use strict';
  var U = global.QUI, S = global.QSim;
  var esc = U.esc;

  /* seeded objects that exist before anyone creates anything */

  var SEEDS = {
    contracts: [{ id: 'C-001', party: 'Handler B', islands: ['C', 'D'], kpi: 'p90', threshold: 15, pct: 90, binSize: 15, window: 'monthly', minPax: 1,
      exclusions: ['sensor', 'security', 'instruction', 'dispute'], allowance: 6, penalty: 350, cap: 10000, disputeDays: 5, profile: 'v12',
      effective: '2026-09-01', status: 'signed', signedAt: '2026-09-01 09:00', by: 'Terminal duty manager', seeded: true }],
    exclusions: [
      { id: 'EX-001', type: 'security', zones: ['CI-C', 'CI-D'], date: '2026-09-11', from: 840, to: 900, reason: 'Security directive: all screening slowed', ref: 'SD-2026-0911', by: 'Terminal duty manager', seeded: true },
      { id: 'EX-002', type: 'sensor', zones: ['CI-C'], date: '2026-09-22', from: 370, to: 400, reason: 'Sensor S-52 offline for more than 5 minutes in the bin', ref: 'INC-2026-0922-03', by: 'Terminal duty manager', seeded: true }
    ],
    reports: [
      { id: 'RP-001', name: 'Daily peaks, border', template: 'daily', scope: 'border', schedule: { type: 'daily', time: 360 }, format: 'PDF', recipients: 'border.ops@dmo.example', by: 'Border shift supervisor', seeded: true },
      { id: 'RP-002', name: 'Monthly SLA and penalties, Handler B', template: 'sla', scope: 'hb', schedule: { type: 'monthly', date: 1, time: 420 }, format: 'CSV', recipients: 'contracts@dmo.example, station@handler-b.example', by: 'Terminal duty manager', seeded: true }
    ],
    displays: [
      { id: 'DSP-01', name: 'Arrivals hall board', level: 'arr', location: 'Arrivals hall, before passport control', orientation: 'landscape', checkpoints: ['A-VIS', 'A-RES', 'A-CIT', 'A-EG'], lang: 'en', band: 5, hysteresis: true, stale: 5,
        fallbackEn: 'Waiting times are temporarily unavailable', fallbackAr: 'أوقات الانتظار غير متاحة مؤقتاً', seeded: true },
      { id: 'DSP-02', name: 'Departures hall board', level: 'dep', location: 'Departures hall, after check-in', orientation: 'landscape', checkpoints: ['SEC-N', 'SEC-S', 'D-PASS', 'D-EG'], lang: 'en', band: 5, hysteresis: true, stale: 5,
        fallbackEn: 'Waiting times are temporarily unavailable', fallbackAr: 'أوقات الانتظار غير متاحة مؤقتاً', seeded: true },
      { id: 'DSP-03', name: 'Security North approach', level: 'dep', location: 'Corridor to Security North', orientation: 'portrait', checkpoints: ['SEC-N', 'SEC-S'], lang: 'ar', band: 10, hysteresis: true, stale: 5,
        fallbackEn: 'Waiting times are temporarily unavailable', fallbackAr: 'أوقات الانتظار غير متاحة مؤقتاً', seeded: true },
      { id: 'DSP-04', name: 'Check-in hall, islands C and D', level: 'dep', location: 'Check-in hall, Handler B side', orientation: 'landscape', checkpoints: ['CI-C', 'CI-D', 'SEC-S'], lang: 'en', band: 5, hysteresis: true, stale: 5,
        fallbackEn: 'Waiting times are temporarily unavailable', fallbackAr: 'أوقات الانتظار غير متاحة مؤقتاً', seeded: true }
    ],
    users: [
      { id: 'U-001', name: 'Demo presenter', org: 'demo', role: 'demo', signin: 'Demo only', expiry: '', seeded: true },
      { id: 'U-002', name: 'Arrivals supervisor, shift A', org: 'border', role: 'bss', signin: 'Directory single sign-on', expiry: '', seeded: true },
      { id: 'U-003', name: 'Arrivals supervisor, shift B', org: 'border', role: 'bss', signin: 'Directory single sign-on', expiry: '', seeded: true },
      { id: 'U-004', name: 'Departures supervisor, shift A', org: 'border', role: 'bss', signin: 'Smart card', expiry: '', seeded: true },
      { id: 'U-005', name: 'Departures supervisor, shift B', org: 'border', role: 'bss', signin: 'Smart card', expiry: '', seeded: true },
      { id: 'U-006', name: 'Terminal duty manager, day', org: 'airport', role: 'tdm', signin: 'Directory single sign-on', expiry: '', seeded: true },
      { id: 'U-007', name: 'Terminal duty manager, night', org: 'airport', role: 'tdm', signin: 'Directory single sign-on', expiry: '', seeded: true },
      { id: 'U-008', name: 'Airport operations centre desk', org: 'airport', role: 'tdm', signin: 'Directory single sign-on', expiry: '', seeded: true },
      { id: 'U-009', name: 'Handler B station manager', org: 'handlerB', role: 'hbm', signin: 'Password with MFA', expiry: '', seeded: true },
      { id: 'U-010', name: 'Handler B duty officer, evening', org: 'handlerB', role: 'hbm', signin: 'Password with MFA', expiry: '', seeded: true },
      { id: 'U-011', name: 'Handler B duty officer, night', org: 'handlerB', role: 'hbm', signin: 'Password with MFA', expiry: '2026-12-31', seeded: true }
    ],
    audit: [
      { date: '2026-09-01', t: 540, role: 'Terminal duty manager', action: 'Signed', id: 'C-001', summary: 'Handler B check-in contract signed against zone profile v12', seeded: true },
      { date: '2026-09-02', t: 614, role: 'Terminal duty manager', action: 'Published', id: 'v12', summary: 'Zone profile v12 published after the August calibration campaign', seeded: true },
      { date: '2026-09-03', t: 1300, role: 'Border shift supervisor', action: 'Recorded calibration', id: 'S-17', summary: 'Passed: 97.2% counting accuracy after a planned firmware update', seeded: true },
      { date: '2026-09-18', t: 552, role: 'Handler B station manager', action: 'Raised dispute', id: 'D-0914', summary: 'Bin 2026-09-17 20:15, island C: counters closed on airport instruction', seeded: true },
      { date: '2026-09-18', t: 700, role: 'Terminal duty manager', action: 'Started review', id: 'D-0914', summary: 'Dispute under review', seeded: true },
      { date: '2026-09-21', t: 930, role: 'Border shift supervisor', action: 'Created draft', id: 'v13', summary: 'Draft zone profile v13: Visitors snake extended into the overflow band', seeded: true }
    ]
  };
  U.SEEDS = SEEDS;
  U.ORG = { border: 'Border authority', airport: 'Airport operator', handlerB: 'Handler B', demo: 'Demo only' };
  U.ORG_DEPLOY = { border: 'Border deployment', airport: 'Airport deployment', handlerB: 'Airport deployment (handler view)', demo: 'Demo only' };
  U.ORG_ROLES = { border: ['bss'], airport: ['tdm'], handlerB: ['hbm'], demo: ['demo'] };

  /* lists: seeded plus created, with state changes applied */
  U.list = function (type) {
    var c = U.state.c;
    if (type === 'contracts') return SEEDS.contracts.concat(c.contracts);
    if (type === 'exclusions') return SEEDS.exclusions.concat(c.exclusions);
    if (type === 'reports') return SEEDS.reports.concat(c.reports);
    if (type === 'displays') return SEEDS.displays.concat(c.displays);
    if (type === 'users') return SEEDS.users.concat(c.users);
    if (type === 'audit') return SEEDS.audit.concat(c.audit);
    if (type === 'allocations') return S.SEED_ALLOCATIONS.concat(c.allocations);
    if (type === 'rules') return U.allRules();
    return (c[type] || []).slice();
  };

  /* create rights: the demo presenter has all of them */
  var RIGHTS = {
    rule: { demo: 1, bss: 1, tdm: 1 },
    override: { demo: 1, bss: 1, tdm: 1, hbm: 1 },
    zone: { demo: 1, bss: 1, tdm: 1 },
    sensor: { demo: 1, bss: 1, tdm: 1 },
    contract: { demo: 1, tdm: 1 },
    exclusion: { demo: 1, tdm: 1 },
    flight: { demo: 1, tdm: 1 },
    allocation: { demo: 1, tdm: 1, hbm: 1 },
    display: { demo: 1, tdm: 1 },
    report: { demo: 1, bss: 1, tdm: 1, hbm: 1 },
    user: { demo: 1 }
  };
  var DENY = {
    rule: 'Handler B station managers do not create alert rules; the Terminal duty manager does.',
    override: 'This role cannot change the staffing plan.',
    zone: 'Handler B station managers do not edit zone profiles.',
    sensor: 'Handler B station managers do not register sensors.',
    contract: 'Only the Terminal duty manager signs handler contracts.',
    exclusion: 'Only the Terminal duty manager adds SLA exclusions.',
    flight: 'Only the Terminal duty manager adds ad-hoc flights to the schedule.',
    allocation: 'Border shift supervisors do not allocate check-in counters.',
    display: 'Only the Terminal duty manager adds passenger displays.',
    report: 'This role cannot schedule reports.',
    user: 'Only an administrator adds users; in this demo, the Demo presenter.'
  };
  U.CREATE_TYPES = [
    ['rule', 'Alert rules', 'rules.html'], ['override', 'Roster overrides', 'forecast.html#staffing'], ['zone', 'Zones and zone profiles', 'zones.html'],
    ['sensor', 'Sensors and calibrations', 'devices.html'], ['contract', 'SLA contracts', 'sla.html'], ['exclusion', 'SLA exclusions', 'sla.html'],
    ['flight', 'Ad-hoc flights', 'forecast.html'], ['allocation', 'Counter allocations', 'checkin.html'], ['display', 'Passenger displays', 'display.html'],
    ['report', 'Scheduled reports', 'reports.html'], ['user', 'Users', 'access.html']
  ];
  U.CREATE_SCOPE = {
    rule: { bss: 'Immigration queues and immigration sensors', tdm: 'Check-in, security and airport-side sensors', demo: 'All' },
    override: { bss: 'Immigration queues', tdm: 'Check-in and security', hbm: 'Islands C and D', demo: 'All queues' },
    zone: { bss: 'Immigration halls', tdm: 'Check-in, security and reclaim', demo: 'All levels' },
    sensor: { bss: 'Immigration halls', tdm: 'Check-in, security and reclaim', demo: 'All zones' },
    contract: { tdm: 'Both handlers', demo: 'Both handlers' },
    exclusion: { tdm: 'All check-in zones', demo: 'All check-in zones' },
    flight: { tdm: 'Arrivals and departures', demo: 'Arrivals and departures' },
    allocation: { tdm: 'All islands', hbm: 'Islands C and D', demo: 'All islands' },
    display: { tdm: 'All displays', demo: 'All displays' },
    report: { bss: 'Border view', tdm: 'Airport view plus border aggregates', hbm: 'Handler B view', demo: 'Whole airport' },
    user: { demo: 'All organisations' }
  };
  U.canCreate = function (type) { var r = RIGHTS[type]; return !!(r && r[U.role()]); };
  U.createReason = function (type) { return DENY[type] || 'Not available for this role.'; };
  U.createRight = function (type, role) { var r = RIGHTS[type]; return !!(r && r[role]); };
  U.createQueues = function (type) {
    var r = U.role();
    if (r === 'demo') return S.IMM_IDS.concat(S.AIR_IDS);
    if (r === 'bss') return S.IMM_IDS.slice();
    if (r === 'tdm') return S.AIR_IDS.slice();
    if (r === 'hbm' && (type === 'override' || type === 'allocation')) return ['CI-C', 'CI-D'];
    return [];
  };
  U.createRegions = function () {
    var r = U.role();
    if (r === 'demo') return ['imm', 'emi', 'reclaim', 'ciA', 'ciB', 'sec'];
    if (r === 'bss') return ['imm', 'emi'];
    if (r === 'tdm') return ['ciA', 'ciB', 'sec', 'reclaim'];
    return [];
  };
  U.createIslands = function () {
    var r = U.role();
    if (r === 'hbm') return ['C', 'D'];
    if (r === 'tdm' || r === 'demo') return ['A', 'B', 'C', 'D'];
    return [];
  };

  /* readable, deterministic IDs: the next number after the highest one in use */
  function pad(n, w) { var s = String(n); while (s.length < w) s = '0' + s; return s; }
  function maxNum(ids, re, floor) {
    var m = floor;
    ids.forEach(function (id) { var x = re.exec(id || ''); if (x) m = Math.max(m, parseInt(x[1], 10)); });
    return m;
  }
  U.nextId = function (type) {
    var c = U.state.c;
    function ids(list) { return list.map(function (o) { return o.id; }); }
    switch (type) {
      case 'rule': return 'R-' + pad(maxNum(ids(U.allRules()), /^R-(\d+)$/, 5) + 1, 3);
      case 'sensor': return 'S-' + (maxNum(ids(c.sensors), /^S-(\d+)$/, 59) + 1);
      case 'contract': return 'C-' + pad(maxNum(ids(U.list('contracts')), /^C-(\d+)$/, 1) + 1, 3);
      case 'exclusion': return 'EX-' + pad(maxNum(ids(U.list('exclusions')), /^EX-(\d+)$/, 2) + 1, 3);
      case 'override': return 'OV-' + pad(maxNum(ids(c.overrides), /^OV-(\d+)$/, 0) + 1, 3);
      case 'flight': return 'FL-X' + pad(maxNum(ids(c.flights), /^FL-X(\d+)$/, 0) + 1, 2);
      case 'allocation': return 'AL-' + pad(maxNum(ids(U.list('allocations')), /^AL-(\d+)$/, 3) + 1, 3);
      case 'report': return 'RP-' + pad(maxNum(ids(U.list('reports')), /^RP-(\d+)$/, 2) + 1, 3);
      case 'display': return 'DSP-' + pad(maxNum(ids(U.list('displays')), /^DSP-(\d+)$/, 4) + 1, 2);
      case 'user': return 'U-' + pad(maxNum(ids(U.list('users')), /^U-(\d+)$/, 11) + 1, 3);
      case 'zone': {
        var all = [];
        [c.draft ? c.draft.geometry : null].concat(U.profiles().map(function (p) { return p.geometry; })).forEach(function (g) { if (g) all = all.concat(Object.keys(g.zones)); });
        return 'Z-' + pad(maxNum(all, /^Z-(\d+)$/, 0) + 1, 2);
      }
    }
    return type + '-1';
  };

  /* highlight of the newest row, for a few seconds */
  var flash = { id: null, until: 0 };
  U.flash = function (id) { flash.id = id; flash.until = Date.now() + 9000; };
  U.isNew = function (id) { return id && flash.id === id && Date.now() < flash.until; };
  U.rowCls = function (id, extra) { var c = (extra || '') + (U.isNew(id) ? ' row-new' : ''); return c.trim() ? ' class="' + c.trim() + '"' : ''; };

  /* the create button: visible to every role, disabled with a one-line reason when the role may not create */
  U.createBtn = function (type, label, id, reason) {
    var ok = U.canCreate(type) && !reason;
    if (ok) return '<button type="button" class="btn btn-primary" id="' + id + '" data-create="' + type + '">' + U.icon('plus') + esc(label) + '</button>';
    return '<span class="gate"><button type="button" class="btn" id="' + id + '" disabled aria-describedby="' + id + '-why">' + U.icon('plus') + esc(label) + '</button>' +
      '<span class="gate-why" id="' + id + '-why">' + U.icon('lock') + esc(reason || U.createReason(type)) + '</span></span>';
  };

  /* fields */
  var F = {};
  function wrap(name, label, inner, o) {
    o = o || {};
    return '<div class="fld' + (o.cls ? ' ' + o.cls : '') + '" data-f="' + name + '"' + (o.hidden ? ' hidden' : '') + '>' +
      (label ? '<label for="f-' + name + '">' + esc(label) + (o.req ? '<span class="req" aria-hidden="true"> *</span>' : '') + '</label>' : '') + inner +
      (o.hint ? '<div class="fld-hint" id="f-' + name + '-h">' + o.hint + '</div>' : '') + '<div class="fld-err" id="f-' + name + '-e"></div></div>';
  }
  function ad(name, o) { return ' aria-describedby="' + (o && o.hint ? 'f-' + name + '-h ' : '') + 'f-' + name + '-e"' + (o && o.req ? ' aria-required="true"' : ''); }
  F.text = function (name, label, value, o) {
    o = o || {};
    var inp = '<input id="f-' + name + '" name="' + name + '" type="' + (o.type || 'text') + '" value="' + esc(value == null ? '' : value) + '"' +
      (o.min != null ? ' min="' + o.min + '"' : '') + (o.max != null ? ' max="' + o.max + '"' : '') + (o.step != null ? ' step="' + o.step + '"' : '') +
      (o.placeholder ? ' placeholder="' + esc(o.placeholder) + '"' : '') + (o.readonly ? ' readonly' : '') + (o.inputmode ? ' inputmode="' + o.inputmode + '"' : '') +
      (o.ar ? ' dir="rtl" lang="ar"' : '') + (o.autocomplete ? ' autocomplete="' + o.autocomplete + '"' : ' autocomplete="off"') + ad(name, o) + '>';
    if (o.unit) inp = '<span class="inp-unit">' + inp + '<span class="unit">' + esc(o.unit) + '</span></span>';
    return wrap(name, label, inp, o);
  };
  F.select = function (name, label, options, value, o) {
    o = o || {};
    var html = '<select id="f-' + name + '" name="' + name + '"' + ad(name, o) + '>' + options.map(function (op) {
      if (op.group) return '<optgroup label="' + esc(op.group) + '">' + op.options.map(function (x) { return '<option value="' + esc(x[0]) + '"' + (String(x[0]) === String(value) ? ' selected' : '') + '>' + esc(x[1]) + '</option>'; }).join('') + '</optgroup>';
      return '<option value="' + esc(op[0]) + '"' + (String(op[0]) === String(value) ? ' selected' : '') + (op[2] ? ' disabled' : '') + '>' + esc(op[1]) + '</option>';
    }).join('') + '</select>';
    return wrap(name, label, html, o);
  };
  F.textarea = function (name, label, value, o) {
    o = o || {};
    return wrap(name, label, '<textarea id="f-' + name + '" name="' + name + '" rows="' + (o.rows || 2) + '"' + (o.ar ? ' dir="rtl" lang="ar"' : '') + ad(name, o) + '>' + esc(value || '') + '</textarea>', o);
  };
  F.checks = function (name, label, options, values, o) {
    o = o || {};
    values = values || [];
    return '<fieldset class="fld fld-set" data-f="' + name + '" aria-describedby="f-' + name + '-e"><legend>' + esc(label) + (o.req ? '<span class="req" aria-hidden="true"> *</span>' : '') + '</legend><div class="checks">' +
      options.map(function (op, i) {
        return '<label class="chk"><input type="checkbox" name="' + name + '" value="' + esc(op[0]) + '"' + (values.indexOf(op[0]) >= 0 ? ' checked' : '') + (op[2] ? ' disabled' : '') + (i === 0 ? ' id="f-' + name + '"' : '') + '> ' + esc(op[1]) + '</label>';
      }).join('') + '</div>' + (o.hint ? '<div class="fld-hint">' + o.hint + '</div>' : '') + '<div class="fld-err" id="f-' + name + '-e"></div></fieldset>';
  };
  F.radios = function (name, label, options, value, o) {
    o = o || {};
    return '<fieldset class="fld fld-set" data-f="' + name + '" aria-describedby="f-' + name + '-e"><legend>' + esc(label) + '</legend><div class="checks">' +
      options.map(function (op, i) {
        return '<label class="chk"><input type="radio" name="' + name + '" value="' + esc(op[0]) + '"' + (String(op[0]) === String(value) ? ' checked' : '') + (i === 0 ? ' id="f-' + name + '"' : '') + '> ' + esc(op[1]) + '</label>';
      }).join('') + '</div>' + (o.hint ? '<div class="fld-hint">' + o.hint + '</div>' : '') + '<div class="fld-err" id="f-' + name + '-e"></div></fieldset>';
  };
  F.row = function () { return '<div class="fld-row">' + Array.prototype.slice.call(arguments).join('') + '</div>'; };
  F.note = function (html) { return '<p class="fld-note">' + html + '</p>'; };
  F.section = function (title) { return '<h3 class="fld-sec">' + esc(title) + '</h3>'; };
  U.F = F;

  U.formValues = function (form) {
    var v = {};
    Array.prototype.forEach.call(form.elements, function (e) {
      if (!e.name) return;
      if (e.type === 'checkbox') { if (!v[e.name]) v[e.name] = []; if (e.checked) v[e.name].push(e.value); }
      else if (e.type === 'radio') { if (e.checked) v[e.name] = e.value; else if (!(e.name in v)) v[e.name] = ''; }
      else v[e.name] = e.value.trim();
    });
    return v;
  };

  /* validation helpers */
  var V = {};
  V.time = function (s) { var m = /^([01]\d|2[0-3]):([0-5]\d)$/.exec(s || ''); return m ? parseInt(m[1], 10) * 60 + parseInt(m[2], 10) : null; };
  V.num = function (s) { if (s === '' || s == null) return null; var n = Number(s); return isFinite(n) ? n : null; };
  V.int = function (s) { var n = V.num(s); return n != null && Math.floor(n) === n ? n : null; };
  V.date = function (s) { return /^2\d{3}-(0[1-9]|1[0-2])-(0[1-9]|[12]\d|3[01])$/.test(s || ''); };
  V.emails = function (s) {
    if (!s) return [];
    return s.split(/[,;\s]+/).filter(Boolean).filter(function (e) { return !/^[^@\s]+@[^@\s]+\.[a-z]{2,}$/i.test(e); });
  };
  V.ipv4 = function (s) {
    var m = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.exec(s || '');
    return !!m && m.slice(1).every(function (x) { return +x <= 255; });
  };
  V.hasArabic = function (s) { return /[؀-ۿ]/.test(s || ''); };
  U.V = V;
  U.hhmm = function (m) { return S.clock(m); };
  U.timeOptions = function (from, to, step, withEnd) {
    var o = [];
    for (var t = from; t <= to; t += step) o.push([t, t >= 1440 ? '24:00' : S.clock(t)]);
    return o;
  };

  /* geometry checks for zones */
  var G = {};
  function orient(a, b, c) { return (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]); }
  G.segCross = function (a, b, c, d) {
    var o1 = orient(a, b, c), o2 = orient(a, b, d), o3 = orient(c, d, a), o4 = orient(c, d, b);
    return ((o1 > 0 && o2 < 0) || (o1 < 0 && o2 > 0)) && ((o3 > 0 && o4 < 0) || (o3 < 0 && o4 > 0));
  };
  G.selfIntersects = function (pts) {
    var n = pts.length;
    if (n < 4) return false;
    for (var i = 0; i < n; i++) for (var j = i + 1; j < n; j++) {
      if (Math.abs(i - j) <= 1 || (i === 0 && j === n - 1)) continue;
      if (G.segCross(pts[i], pts[(i + 1) % n], pts[j], pts[(j + 1) % n])) return true;
    }
    return false;
  };
  G.distSeg = function (p, a, b) {
    var dx = b[0] - a[0], dy = b[1] - a[1], L = dx * dx + dy * dy;
    var t = L ? Math.max(0, Math.min(1, ((p[0] - a[0]) * dx + (p[1] - a[1]) * dy) / L)) : 0;
    var x = a[0] + t * dx, y = a[1] + t * dy;
    return Math.sqrt((p[0] - x) * (p[0] - x) + (p[1] - y) * (p[1] - y));
  };
  G.onEdge = function (p, poly, tol) {
    for (var i = 0; i < poly.length; i++) if (G.distSeg(p, poly[i], poly[(i + 1) % poly.length]) <= (tol || 4)) return true;
    return false;
  };
  G.snapToEdge = function (p, poly) {
    var best = null, bd = Infinity;
    for (var i = 0; i < poly.length; i++) {
      var a = poly[i], b = poly[(i + 1) % poly.length], dx = b[0] - a[0], dy = b[1] - a[1], L = dx * dx + dy * dy;
      var t = L ? Math.max(0, Math.min(1, ((p[0] - a[0]) * dx + (p[1] - a[1]) * dy) / L)) : 0;
      var q = [Math.round(a[0] + t * dx), Math.round(a[1] + t * dy)], d = Math.hypot(q[0] - p[0], q[1] - p[1]);
      if (d < bd) { bd = d; best = q; }
    }
    return best;
  };
  G.levelBounds = { arr: [14, 26, 988, 592], dep: [8, 26, 990, 592] };
  G.inLevel = function (p, level) { var b = G.levelBounds[level]; return p[0] >= b[0] && p[0] <= b[2] && p[1] >= b[1] && p[1] <= b[3]; };
  U.G = G;

  /* the drawer */
  var cur = null;
  function focusables(root) {
    return Array.prototype.filter.call(root.querySelectorAll('a[href], button:not([disabled]), input:not([disabled]):not([type="hidden"]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'), function (e) {
      return e.offsetWidth > 0 || e.offsetHeight > 0 || e === document.activeElement;
    });
  }
  U.openDrawer = function (o) {
    U.closeDrawer(true);
    var trig = o.trigger || document.activeElement;
    var scrim = document.createElement('div');
    scrim.className = 'drawer-scrim';
    var d = document.createElement('aside');
    d.className = 'drawer' + (o.wide ? ' wide' : '');
    d.setAttribute('role', 'dialog');
    d.setAttribute('aria-modal', 'true');
    d.setAttribute('aria-labelledby', 'drawerTitle');
    d.innerHTML = '<header class="drawer-head"><div><h2 id="drawerTitle">' + esc(o.title) + '</h2>' + (o.sub ? '<p class="small muted">' + o.sub + '</p>' : '') + '</div>' +
      '<button type="button" class="btn btn-ghost icon-btn" data-drawer-close aria-label="Close">' + U.icon('close') + '</button></header>' +
      '<div class="drawer-body"><div class="form-summary" role="alert" hidden></div><form novalidate>' + o.html + '</form></div>' +
      '<footer class="drawer-foot"><button type="button" class="btn btn-primary" data-drawer-submit>' + esc(o.submitLabel || 'Create') + '</button>' +
      '<button type="button" class="btn" data-drawer-close>Cancel</button>' + (o.footNote ? '<span class="small muted">' + o.footNote + '</span>' : '') + '</footer>';
    document.body.appendChild(scrim);
    document.body.appendChild(d);
    var shell = document.querySelector('.shell');
    if (shell) { shell.inert = true; shell.setAttribute('aria-hidden', 'true'); }
    document.documentElement.classList.add('drawer-open');
    var form = d.querySelector('form');
    cur = { d: d, scrim: scrim, trig: trig, o: o, form: form };
    function submit() {
      var vals = U.formValues(form);
      var errs = o.onSubmit ? o.onSubmit(vals, form, d) : null;
      if (errs && Object.keys(errs).length) { U.showErrors(form, d, errs); return; }
      U.closeDrawer();
    }
    d.addEventListener('click', function (e) {
      if (e.target.closest('[data-drawer-close]')) U.closeDrawer();
      else if (e.target.closest('[data-drawer-submit]')) submit();
    });
    form.addEventListener('submit', function (e) { e.preventDefault(); submit(); });
    d.addEventListener('keydown', function (e) {
      if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); U.closeDrawer(); return; }
      if (e.key === 'Enter' && e.target.tagName === 'INPUT' && e.target.type !== 'checkbox' && e.target.type !== 'radio') { e.preventDefault(); submit(); return; }
      if (e.key !== 'Tab') return;
      var f = focusables(d);
      if (!f.length) return;
      var first = f[0], last = f[f.length - 1];
      if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
      else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
    });
    scrim.addEventListener('click', function () { U.closeDrawer(); });
    if (o.onOpen) o.onOpen(form, d);
    var firstField = form.querySelector('input:not([readonly]):not([type="hidden"]), select, textarea');
    (firstField || d.querySelector('[data-drawer-close]')).focus();
    return d;
  };
  U.closeDrawer = function (silent) {
    if (!cur) return;
    var c = cur;
    cur = null;
    c.d.remove(); c.scrim.remove();
    var shell = document.querySelector('.shell');
    if (shell) { shell.inert = false; shell.removeAttribute('aria-hidden'); }
    document.documentElement.classList.remove('drawer-open');
    if (c.o.onClose) c.o.onClose();
    if (!silent && c.trig && document.body.contains(c.trig) && c.trig.focus) c.trig.focus();
    else if (!silent && c.o.returnFocus) { var r = document.getElementById(c.o.returnFocus); if (r) r.focus(); }
  };
  U.drawerOpen = function () { return !!cur; };
  U.showErrors = function (form, d, errs) {
    form.querySelectorAll('.fld-err').forEach(function (e) { e.textContent = ''; });
    form.querySelectorAll('[aria-invalid]').forEach(function (e) { e.removeAttribute('aria-invalid'); });
    var names = Object.keys(errs), first = null;
    names.forEach(function (n) {
      var box = form.querySelector('#f-' + n + '-e');
      if (box) box.textContent = errs[n];
      var inp = form.querySelector('#f-' + n) || form.querySelector('[name="' + n + '"]');
      if (inp) { inp.setAttribute('aria-invalid', 'true'); if (!first) first = inp; }
    });
    var sum = d.querySelector('.form-summary');
    sum.hidden = false;
    sum.innerHTML = '<strong>' + (names.length === 1 ? 'Fix 1 field' : 'Fix ' + names.length + ' fields') + ' before creating.</strong><ul>' + names.map(function (n) { return '<li>' + esc(errs[n]) + '</li>'; }).join('') + '</ul>';
    if (first) first.focus();
  };

  /* toast with Undo */
  U.toast = function (msg, o) {
    o = o || {};
    var box = document.getElementById('toasts');
    if (!box) { box = document.createElement('div'); box.id = 'toasts'; box.className = 'toasts'; box.setAttribute('aria-live', 'polite'); document.body.appendChild(box); }
    var t = document.createElement('div');
    t.className = 'toast';
    t.setAttribute('role', 'status');
    t.innerHTML = U.icon('check') + '<span>' + esc(msg) + '</span>' + (o.undo ? '<button type="button" class="btn" data-undo>' + U.icon('undo') + 'Undo</button>' : '') +
      '<button type="button" class="btn btn-ghost icon-btn" data-dismiss aria-label="Dismiss">' + U.icon('close') + '</button>';
    box.appendChild(t);
    var timer = setTimeout(function () { t.remove(); }, o.ms || 10000);
    t.addEventListener('click', function (e) {
      if (e.target.closest('[data-undo]')) { clearTimeout(timer); t.remove(); o.undo(); }
      else if (e.target.closest('[data-dismiss]')) { clearTimeout(timer); t.remove(); }
    });
    return t;
  };

  /* one call for the success path: save, audit, toast with Undo, highlight */
  U.created = function (type, id, summary, undo, after) {
    U.saveC();
    U.audit('Created', id, summary);
    U.flash(id);
    U.toast('Created ' + id, { undo: function () { undo(); U.saveC(); U.audit('Undid create', id, summary); U.toast('Removed ' + id); if (after) after(true); } });
    if (after) after(false);
  };

  U.removeFrom = function (listName, id) {
    var c = U.state.c;
    c[listName] = c[listName].filter(function (o) { return o.id !== id; });
  };

  /* next run of a schedule, in simulated time, from the demo date and clock */
  var WD = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
  U.nextRun = function (sch, now) {
    var base = new Date(Date.UTC(2026, 8, 28));
    function fmt(d, t) { return WD[d.getUTCDay()] + ' ' + d.getUTCDate() + ' ' + ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'][d.getUTCMonth()] + ', ' + S.clock(t); }
    for (var k = 0; k < 62; k++) {
      var d = new Date(base.getTime() + k * 86400000);
      var ok = sch.type === 'daily' || (sch.type === 'weekly' && d.getUTCDay() === sch.weekday) || (sch.type === 'monthly' && d.getUTCDate() === sch.date);
      if (ok && (k > 0 || sch.time > now)) return fmt(d, sch.time);
    }
    return 'n/a';
  };
  U.WEEKDAYS = WD;

  /* phone layout for list tables: each cell carries its column name, shown by CSS below 640px */
  U.stack = function (root) {
    root.querySelectorAll('table.stack-sm').forEach(function (t) {
      var heads = Array.prototype.map.call(t.querySelectorAll('thead th'), function (h) { return h.textContent.trim(); });
      t.querySelectorAll('tbody tr').forEach(function (tr) {
        Array.prototype.forEach.call(tr.children, function (c, i) { if (heads[i] && !c.hasAttribute('colspan') && !c.hasAttribute('data-label')) c.setAttribute('data-label', heads[i]); });
      });
    });
  };

  /* sensors and zones shared by Devices, Reports and SLA */
  U.SENSOR_STATUS = { commissioning: 'Commissioning', online: 'Online', failed: 'Calibration failed' };
  U.latestGeometry = function () { var ps = U.profiles(); return ps.length ? ps[ps.length - 1].geometry : QFloor.V12; };
  var OVN = { 'A-OV': 'Arrival immigration: overflow band', 'D-OV': 'Departure immigration: overflow band', 'SEC-OV': 'Security: overflow band' };
  U.zoneName = function (zid) {
    if (S.QI[zid] != null) return U.qname(zid);
    if (OVN[zid]) return OVN[zid];
    var z = U.latestGeometry().zones[zid];
    return z && z.meta ? z.meta.name + ' (' + zid + ')' : zid;
  };
  U.sensorRegion = function (x) { return QFloor.ZONE_REGION[x.zone] || QFloor.regionAt(x.level, x.x, x.y); };

  /* SLA evaluation, shared by the SLA and Reports screens */
  var SLA = (function () {
    var MONTH = [
      { d: 4, ev: 136, br: 1 },
      { d: 9, ev: 135, br: 2 },
      { d: 11, ev: 134, br: 2, ex: { reason: 'Security directive, 14:00 to 15:00: all screening slowed', bins: 8, breaches: 2 } },
      { d: 15, ev: 131, br: 1 },
      { d: 17, ev: 133, br: 1, dispute: 'D-0914' },
      { d: 20, ev: 129, br: 1 },
      { d: 22, ev: 132, br: 1, ex: { reason: 'Sensor S-52 offline 06:10 to 06:40 (more than 5 min in the bin)', bins: 4, breaches: 1 } },
      { d: 25, ev: 134, br: 1 }
    ];
    var QUIET_BINS = 3552;
    var DEFAULT_DISPUTES = [{ id: 'D-0914', date: '2026-09-17', bin: '20:15', zone: 'CI-C', reason: 'Counters C07 to C09 closed on airport instruction (power fault at the island)',
      status: 'review', raisedBy: 'Handler B station manager', history: [{ status: 'raised', at: '2026-09-18 09:12' }, { status: 'review', at: '2026-09-18 11:40' }] }];
    var STATUS_TEXT = { raised: 'Raised', review: 'Under review', upheld: 'Upheld', rejected: 'Rejected' };
    var disputes = null;
    var EXT = { security: 'Security directive', sensor: 'Sensor outage over the zone', instruction: 'Counters closed on airport instruction', disruption: 'Flight disruption outside the handler\'s control', dispute: 'Upheld dispute' };
    var KPI = { p90: 'Percentile wait per bin', share: 'Share of passengers under the threshold', overflow: 'Overflow minutes per bin' };
    var PARTY_ISL = { 'Handler A': ['A', 'B'], 'Handler B': ['C', 'D'] };
    var memo = {}, memoV = -1;

    function exclusionsToday() { return U.list('exclusions').filter(function (x) { return x.date === S.DATE; }); }
    function exclusionFor(ct, zone, bs, size) {
      return exclusionsToday().filter(function (x) { return ct.exclusions.indexOf(x.type) >= 0 && x.zones.indexOf(zone) >= 0 && x.from < bs + size && x.to > bs; })[0] || null;
    }

    function loadDisputes() {
      var d = U.store.get('disputes', null);
      disputes = Array.isArray(d) ? d : JSON.parse(JSON.stringify(DEFAULT_DISPUTES));
      return disputes;
    }
    function saveDisputes(arr) { if (arr) disputes = arr; U.store.set('disputes', disputes); }

    function disputeFor(date, zone, bin) {
      if (!disputes) loadDisputes();
      return disputes.filter(function (x) { return x.date === date && x.zone === zone && x.bin === bin; })[0] || null;
    }

    /* one bin of one contract: the KPI value and whether it breaches */
    function binResult(ct, zone, bs, m) {
      var key = [ct.id, ct.kpi, ct.threshold, ct.pct, ct.binSize, zone, bs].join('|');
      if (memo[key] && memo[key].status === 'final') return memo[key];
      var q = S.QI[zone], size = ct.binSize, r;
      if (ct.kpi === 'p90' && size === 15 && ct.pct === 90) {
        var b = U.day.bin(q, bs, m);
        r = { pax: b.pax, status: b.status, value: b.p90 };
      } else {
        var st = U.day.binStats(q, bs, size, m), v = null;
        if (ct.kpi === 'p90') v = st.pct(ct.pct / 100);
        else if (ct.kpi === 'share') { var sh = st.share(ct.threshold); v = sh == null ? null : sh * 100; }
        else {
          var ov = U.day.metric('overflow', { q: q, zone: zone }); v = 0;
          for (var t = bs; t < Math.min(bs + size, m + 1); t++) if (ov[t]) v++;
        }
        r = { pax: st.pax, status: st.status, value: v };
      }
      r.breach = r.value != null && (ct.kpi === 'share' ? r.value < ct.pct : r.value > ct.threshold);
      memo[key] = r;
      return r;
    }

    /* a signed contract evaluated on today's live bins */
    function evalContract(ct, m) {
      var t = { ev: 0, br: 0, prov: 0, provBr: 0, excluded: 0, held: 0, exBins: {}, bins: [], active: true, why: '' };
      if (ct.status !== 'signed') { t.active = false; t.why = 'Draft: evaluated once signed.'; return t; }
      if (ct.effective > S.DATE) { t.active = false; t.why = 'Takes effect on ' + ct.effective + '; not evaluated today.'; return t; }
      var mv = U.dayVersion + '|' + U.profiles().length;
      if (memoV !== mv) { memo = {}; memoV = mv; }
      ct.islands.forEach(function (isl) {
        var zone = 'CI-' + isl;
        for (var bs = 0; bs <= m; bs += ct.binSize) {
          var r = binResult(ct, zone, bs, m);
          if (r.pax < 0.5 || Math.round(r.pax) < ct.minPax || r.value == null) continue;
          var ex = exclusionFor(ct, zone, bs, ct.binSize);
          var dp = ct.seeded && ct.binSize === 15 ? disputeFor(S.DATE, zone, S.clock(bs)) : null;
          var b = { zone: zone, start: bs, size: ct.binSize, value: r.value, p90: r.value, pax: r.pax, status: r.status, breach: r.breach, ex: ex, dp: dp, profile: U.profileAt(Math.min(bs + ct.binSize - 1, m)) };
          t.bins.push(b);
          if (ex) t.exBins[ex.id] = (t.exBins[ex.id] || 0) + 1;
          if (r.status === 'final') {
            t.ev++;
            if (r.breach) {
              t.br++;
              if (ex || (dp && dp.status === 'upheld' && ct.exclusions.indexOf('dispute') >= 0)) t.excluded++;
              else if (dp && (dp.status === 'raised' || dp.status === 'review')) t.held++;
            }
          } else { t.prov++; if (r.breach) t.provBr++; }
        }
      });
      t.counted = t.br - t.excluded - t.held;
      return t;
    }

    function seeded() { return U.SEEDS.contracts[0]; }

    function evaluate(m) {
      if (!disputes) loadDisputes();
      var t = evalContract(seeded(), m);
      var month = { ev: QUIET_BINS + t.ev, br: t.br, excluded: t.excluded, held: t.held };
      MONTH.forEach(function (r) {
        month.ev += r.ev; month.br += r.br;
        if (r.ex) month.excluded += r.ex.breaches;
        if (r.dispute) {
          var dp = disputes.filter(function (x) { return x.id === r.dispute; })[0];
          if (dp && dp.status === 'upheld') month.excluded += 1;
          else if (dp && (dp.status === 'raised' || dp.status === 'review')) month.held += 1;
        }
      });
      month.counted = month.br - month.excluded - month.held;
      month.penalised = Math.max(0, month.counted - 6);
      month.penalty = Math.min(10000, month.penalised * 350);
      return { today: t, month: month, bins: t.bins };
    }

    function kpiText(c) {
      if (c.kpi === 'p90') return 'P' + c.pct + ' wait per ' + c.binSize + '-minute bin above ' + c.threshold + ' min is a breach';
      if (c.kpi === 'share') return 'Under ' + c.pct + '% of passengers waiting ' + c.threshold + ' min or less in a ' + c.binSize + '-minute bin is a breach';
      return 'More than ' + c.threshold + ' min of overflow in a ' + c.binSize + '-minute bin is a breach';
    }
    return {
      consts: { MONTH: MONTH, QUIET_BINS: QUIET_BINS, STATUS_TEXT: STATUS_TEXT, EXT: EXT, KPI: KPI, PARTY_ISL: PARTY_ISL, DEFAULT_DISPUTES: DEFAULT_DISPUTES, ALLOWANCE: 6, RATE: 350, CAP: 10000 },
      api: { evalContract: evalContract, evaluate: evaluate, exclusionsToday: exclusionsToday, exclusionFor: exclusionFor, kpiText: kpiText, disputeFor: disputeFor,
        loadDisputes: loadDisputes, saveDisputes: saveDisputes, disputes: function () { if (!disputes) loadDisputes(); return disputes; } }
    };
  })();
  U.SLA = SLA.consts;
  U.sla = SLA.api;
})(window);
