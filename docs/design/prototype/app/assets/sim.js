/*
  sim.js: deterministic, seeded queue simulation for the DMO prototype.
  Synthetic data only. Classic script, no dependencies. Exposes QSim.

  Model: fluid per-minute backlog recursion per queue,
    next = max(0, backlog + arrivals - capacity),
  capacity = sum over staffed, unpaused servers of 60 / service seconds.
  Realised waits come from cumulative arrival and departure curves (FIFO).
  The whole demo day is recomputed from 18:00 the previous evening on every
  page load, so the clock can jump anywhere and every page agrees.
*/
(function (global) {
  'use strict';

  var DAY = 1440, PRE = 360, POST = 420, N = PRE + DAY + POST;
  var DEFAULT_SEED = 9303;
  var DATE = '2026-09-28';

  /* seeded randomness */

  function mulberry32(a) {
    return function () {
      a = (a + 0x6D2B79F5) | 0;
      var t = Math.imul(a ^ (a >>> 15), 1 | a);
      t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
      return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
  }
  function mix32(h) {
    h = h | 0;
    h ^= h >>> 16; h = Math.imul(h, 0x7feb352d);
    h ^= h >>> 15; h = Math.imul(h, 0x846ca68b);
    h ^= h >>> 16;
    return h >>> 0;
  }
  function h3(a, b, c) {
    return mix32(mix32(mix32(a ^ 0x9e3779b9) ^ ((b + 0x632be5ab) | 0)) ^ ((c + 0x27d4eb2f) | 0)) / 4294967296;
  }
  function clamp(v, lo, hi) { return v < lo ? lo : v > hi ? hi : v; }
  function norm(a) { var s = 0, i; for (i = 0; i < a.length; i++) s += a[i]; return a.map(function (v) { return v / s; }); }
  function mod(a, n) { return ((a % n) + n) % n; }

  /* synthetic parameters (shown on the About panel) */

  var PARAMS = {
    date: DATE,
    mix: { CIT: 0.35, RES: 0.20, VIS: 0.35, CRW: 0.02, TRF: 0.08 },
    egateShare: 0.40,
    egateReject: 0.07,
    svc: { CRW: 20, CIT: 30, RES: 45, VIS: 95, EG: 18, CI: 150, SEC: 20 },
    walkMin: 8, walkMax: 15, hallSpread: 12,
    showFrom: 180, showTo: 45,
    online: 0.40,
    walkToSecurity: 3, walkToPassport: 2,
    target: 15, alertOn: 15, alertOff: 12, alertMinQueue: 10,
    seatsMin: 120, seatsMax: 300, loadMin: 0.70, loadMax: 0.95,
    mcRuns: 40, mcOnBlock: 6, mcLoad: 0.04, mcService: 0.08,
    recTarget: { arr: 10, dep: 10, ciA: 10, ciB: 10, sec: 8 }
  };

  var LANES = ['CRW', 'CIT', 'RES', 'VIS', 'EG'];
  var LANE_LABEL = { CRW: 'Crew and diplomats', CIT: 'Citizens', RES: 'Residents', VIS: 'Visitors', EG: 'E-gate eligible' };

  var CARRIERS = {
    DM: { handler: 'A', mix: { CIT: 0.47, RES: 0.22, VIS: 0.21, CRW: 0.02, TRF: 0.08 } },
    XR: { handler: 'B', island: 'C', mix: { CIT: 0.23, RES: 0.18, VIS: 0.49, CRW: 0.02, TRF: 0.08 } },
    QL: { handler: 'B', island: 'D', mix: { CIT: 0.35, RES: 0.20, VIS: 0.35, CRW: 0.02, TRF: 0.08 } }
  };

  /* queues and servers */

  var QDEF = [], QI = {}, SERVERS = {};
  function pad2(k) { return k < 10 ? '0' + k : String(k); }
  function range(prefix, a, b, padded) { var o = []; for (var k = a; k <= b; k++) o.push(prefix + (padded ? pad2(k) : k)); return o; }
  function addQ(o) {
    o.index = QDEF.length;
    QI[o.id] = o.index;
    o.servers.forEach(function (sid, k) { SERVERS[sid] = { id: sid, q: o.index, k: k }; });
    QDEF.push(o);
  }
  [['A', 'arr', 'Arrival immigration', 'AR-', 'AG-', 6], ['D', 'dep', 'Departure immigration', 'DP-', 'DG-', 4]].forEach(function (s) {
    var lv = s[1] === 'arr' ? 'arr' : 'dep';
    var ranges = { CRW: [1, 1], CIT: [2, 4], RES: [5, 7], VIS: [8, 22] };
    ['CRW', 'CIT', 'RES', 'VIS'].forEach(function (ln) {
      addQ({ id: s[0] + '-' + ln, area: s[1], group: 'imm', lane: ln, level: lv, svc: PARAMS.svc[ln],
        label: LANE_LABEL[ln], name: s[2] + ': ' + LANE_LABEL[ln], unit: 'desks',
        servers: range(s[3], ranges[ln][0], ranges[ln][1], true) });
    });
    addQ({ id: s[0] + '-EG', area: s[1], group: 'egate', lane: 'EG', level: lv, svc: PARAMS.svc.EG,
      label: 'E-gates', name: s[2] + ': E-gates', unit: 'e-gates', servers: range(s[4], 1, s[5], false) });
  });
  addQ({ id: 'SEC-N', area: 'sec', group: 'sec', level: 'dep', svc: PARAMS.svc.SEC, label: 'Security North', name: 'Security North', unit: 'lanes', servers: range('N', 1, 5, false) });
  addQ({ id: 'SEC-S', area: 'sec', group: 'sec', level: 'dep', svc: PARAMS.svc.SEC, label: 'Security South', name: 'Security South', unit: 'lanes', servers: range('S', 1, 5, false) });
  ['A', 'B', 'C', 'D'].forEach(function (isl) {
    var h = isl === 'A' || isl === 'B' ? 'A' : 'B';
    addQ({ id: 'CI-' + isl, area: h === 'A' ? 'ciA' : 'ciB', group: 'ci', handler: h, island: isl, level: 'dep', svc: PARAMS.svc.CI,
      label: 'Island ' + isl, name: 'Check-in island ' + isl + ' (Handler ' + h + ')', unit: 'counters', servers: range(isl, 1, 12, true) });
  });
  var NQ = QDEF.length;

  var AREAS = {
    arr: { id: 'arr', name: 'Arrival immigration', unit: 'desks', queues: ['A-CRW', 'A-CIT', 'A-RES', 'A-VIS'], perm: 'imm.forecast' },
    dep: { id: 'dep', name: 'Departure immigration', unit: 'desks', queues: ['D-CRW', 'D-CIT', 'D-RES', 'D-VIS'], perm: 'imm.forecast' },
    ciA: { id: 'ciA', name: 'Check-in, Handler A', unit: 'counters', queues: ['CI-A', 'CI-B'], perm: 'ci.A' },
    ciB: { id: 'ciB', name: 'Check-in, Handler B', unit: 'counters', queues: ['CI-C', 'CI-D'], perm: 'ci.B' },
    sec: { id: 'sec', name: 'Security', unit: 'lanes', queues: ['SEC-N', 'SEC-S'], perm: 'sec' }
  };

  var EG_SHARE_DEP = (function () {
    var m = PARAMS.mix, nt = 1 - m.TRF, e = PARAMS.egateShare;
    return { CRW: m.CRW / nt, CIT: m.CIT * (1 - e) / nt, RES: m.RES * (1 - e) / nt, VIS: m.VIS / nt, EG: (m.CIT + m.RES) * e / nt };
  })();

  /* sensors: 59 in total, as in the reference BOQ */
  var SENSORS = (function () {
    var list = [], n = 1;
    function add(zone, count, type, level) { for (var k = 0; k < count; k++) { list.push({ id: 'S-' + pad2(n), zone: zone, type: type, level: level, slot: k, slots: count }); n++; } }
    add('SEC-N', 5, 'Stereo', 'dep'); add('SEC-S', 5, 'Stereo', 'dep'); add('SEC-OV', 1, 'Stereo', 'dep');
    add('A-CRW', 1, 'Stereo', 'arr'); add('A-CIT', 2, 'Stereo', 'arr'); add('A-VIS', 6, 'Stereo', 'arr');
    add('A-RES', 2, 'Stereo', 'arr'); add('A-EG', 2, 'Stereo', 'arr'); add('A-OV', 2, 'Stereo', 'arr');
    add('D-CRW', 1, 'Stereo', 'dep'); add('D-CIT', 2, 'Stereo', 'dep'); add('D-RES', 2, 'Stereo', 'dep');
    add('D-VIS', 5, 'Stereo', 'dep'); add('D-EG', 2, 'Stereo', 'dep'); add('D-OV', 1, 'Stereo', 'dep');
    add('CI-A', 5, 'LiDAR', 'dep'); add('CI-B', 5, 'LiDAR', 'dep'); add('CI-C', 5, 'LiDAR', 'dep'); add('CI-D', 5, 'LiDAR', 'dep');
    return list;
  })();
  var OUTAGES = [{ sensor: 'S-17', zone: 'A-VIS', from: 1100, to: 1110, today: true }];
  var OUTAGE_HISTORY = [
    { sensor: 'S-17', date: '2026-08-14', from: '06:52', to: '06:59', minutes: 7, cause: 'PoE switch port reset' },
    { sensor: 'S-17', date: '2026-09-03', from: '21:14', to: '21:36', minutes: 22, cause: 'Firmware update, planned' }
  ];

  /* schedule */

  var W12 = norm([3, 6, 9, 11, 12, 12, 11, 10, 8, 7, 6, 5]);
  var SHOW_LEN = PARAMS.showFrom - PARAMS.showTo + 1;
  var SHOW = (function () {
    var w = [];
    for (var k = 0; k < SHOW_LEN; k++) { var x = (k + 0.5) / SHOW_LEN; w.push(Math.pow(x, 1.6) * Math.pow(1 - x, 1.3)); }
    return norm(w);
  })();
  var SEATS = [120, 150, 162, 180, 189, 210, 220, 240, 264, 280, 300];
  var ARR_BANKS = [
    { from: 60, to: 235, n: 13 },
    { from: 355, to: 540, n: 20 },
    { from: 600, to: 990, n: 8 },
    { from: 1120, to: 1320, n: 17 },
    { from: 1340, to: 1425, n: 3 }
  ];
  var DEP_BANKS = [
    { from: 65, to: 240, n: 11 },
    { from: 360, to: 545, n: 17 },
    { from: 620, to: 1060, n: 7 },
    { from: 1080, to: 1320, n: 18 },
    { from: 1335, to: 1430, n: 3 }
  ];

  /* the evening story: fixed flights and roster lines, tuned for the default seed */
  var SCRIPT = {
    arr: [
      { code: 'XR 207', carrier: 'XR', sched: 1030, onBlock: 1039, seats: 189, booked: 0.85, load: 0.88, walk: 11, eibtErr: 1 },
      { code: 'DM 214', carrier: 'DM', sched: 1035, onBlock: 1038, seats: 220, booked: 0.84, load: 0.86, walk: 10, eibtErr: 1 },
      { code: 'QL 342', carrier: 'QL', sched: 1055, onBlock: 1052, seats: 180, booked: 0.78, load: 0.80, walk: 12, eibtErr: -1 },
      { code: 'XR 331', carrier: 'XR', sched: 1060, onBlock: 1068, seats: 216, booked: 0.86, load: 0.91, walk: 8, eibtErr: 0 },
      { code: 'XR 417', carrier: 'XR', sched: 1080, onBlock: 1071, seats: 170, booked: 0.85, load: 0.89, walk: 8, eibtErr: 1 },
      { code: 'QL 118', carrier: 'QL', sched: 1085, onBlock: 1073, seats: 189, booked: 0.82, load: 0.86, walk: 8, eibtErr: 0 },
      { code: 'XR 509', carrier: 'XR', sched: 1065, onBlock: 1076, seats: 162, booked: 0.84, load: 0.90, walk: 14, eibtErr: 0 }
    ],
    dep: [
      { code: 'XR 332', carrier: 'XR', std: 1210, seats: 224, booked: 0.88, load: 0.92, shift: -3 },
      { code: 'XR 418', carrier: 'XR', std: 1230, seats: 204, booked: 0.86, load: 0.90, shift: 2 },
      { code: 'XR 206', carrier: 'XR', std: 1245, seats: 255, booked: 0.85, load: 0.91, shift: -2 },
      { code: 'XR 510', carrier: 'XR', std: 1265, seats: 187, booked: 0.84, load: 0.88, shift: 1 }
    ],
    roster: [
      { q: 'A-VIS', from: 1035, to: 1065, n: 11, note: 'Evening shift handover' },
      { q: 'A-VIS', from: 1065, to: 1095, n: 12, note: 'Evening shift handover' },
      { q: 'A-VIS', from: 1095, to: 1170, n: 13, note: 'Evening shift fully on' },
      { q: 'CI-C', from: 1140, to: 1185, n: 5, note: 'Handler B shift change' },
      { q: 'CI-C', from: 1185, to: 1260, n: 11, note: 'Handler B evening shift' }
    ],
    egateOos: { gate: 'AG-5', from: 1000, to: 1160, note: 'Planned maintenance' }
  };

  /* counter allocations already in place this morning (they match the plan, so they do not change the numbers) */
  var SEED_ALLOCATIONS = [
    { id: 'AL-001', flight: 'XR 332', island: 'C', fromCtr: 1, toCtr: 2, open: 1030, close: 1165, by: 'Handler B station manager' },
    { id: 'AL-002', flight: 'XR 418', island: 'C', fromCtr: 3, toCtr: 4, open: 1050, close: 1185, by: 'Handler B station manager' },
    { id: 'AL-003', flight: 'XR 206', island: 'C', fromCtr: 5, toCtr: 5, open: 1065, close: 1200, by: 'Handler B station manager' }
  ];

  function buildSchedule(seed) {
    var RA = mulberry32(mix32(seed ^ 0x5eed1234)), RD = mulberry32(mix32(seed ^ 0x0de9a27));
    var R = RA;
    var used = {};
    SCRIPT.arr.concat(SCRIPT.dep).forEach(function (f) { used[f.code] = 1; });
    function code(c) {
      for (;;) { var s = c + ' ' + (100 + Math.floor(R() * 880)); if (!used[s]) { used[s] = 1; return s; } }
    }
    function pickSeats() { return SEATS[Math.floor(R() * SEATS.length)]; }
    function carrier(w) { var r = R(); return r < w[0] ? 'DM' : r < w[0] + w[1] ? 'XR' : 'QL'; }
    function jitterMix(base) {
      var m = {}, s = 0;
      ['CIT', 'RES', 'VIS', 'CRW'].forEach(function (k) { m[k] = base[k] * (0.9 + R() * 0.2); s += m[k]; });
      var f = (1 - base.TRF) / s;
      ['CIT', 'RES', 'VIS', 'CRW'].forEach(function (k) { m[k] = m[k] * f; });
      m.TRF = base.TRF;
      return m;
    }
    var arr = [], dep = [];
    ARR_BANKS.forEach(function (b) {
      for (var j = 0; j < b.n; j++) {
        var sched = Math.round(b.from + (b.to - b.from) * (j + R()) / b.n);
        var c = carrier([0.4, 0.3]);
        var booked = Math.round((0.70 + R() * 0.25) * 100) / 100;
        var load = clamp(Math.round((booked + (R() - 0.5) * 0.08) * 100) / 100, 0.70, 0.95);
        var delay = clamp(Math.round((R() + R() + R() - 1.5) * 12), -20, 20);
        arr.push({ code: code(c), carrier: c, sched: sched, onBlock: sched + delay, seats: pickSeats(), booked: booked, load: load,
          walk: 8 + Math.floor(R() * 8), eibtErr: Math.round((R() - 0.5) * 6), mix: jitterMix(CARRIERS[c].mix) });
      }
    });
    SCRIPT.arr.forEach(function (s) {
      var f = {}; for (var k in s) f[k] = s[k];
      f.mix = CARRIERS[s.mixOf || s.carrier].mix;
      f.scripted = true;
      arr.push(f);
    });
    var dmToggle = 0;
    R = RD;
    DEP_BANKS.forEach(function (b) {
      for (var j = 0; j < b.n; j++) {
        var std = Math.round(b.from + (b.to - b.from) * (j + R()) / b.n);
        var c = carrier([0.5, 0.25]);
        if (c === 'XR' && std >= 1140 && std <= 1335) c = 'QL';
        var booked = Math.round((0.70 + R() * 0.25) * 100) / 100;
        var load = clamp(Math.round((booked + (R() - 0.5) * 0.08) * 100) / 100, 0.70, 0.95);
        var island = c === 'DM' ? (dmToggle++ % 2 ? 'B' : 'A') : CARRIERS[c].island;
        dep.push({ code: code(c), carrier: c, std: std, seats: pickSeats(), booked: booked, load: load,
          shift: Math.round((R() - 0.5) * 10), island: island, handler: CARRIERS[c].handler, mix: CARRIERS[c].mix });
      }
    });
    SCRIPT.dep.forEach(function (s) {
      var f = {}; for (var k in s) f[k] = s[k];
      f.island = 'C'; f.handler = 'B'; f.mix = CARRIERS.XR.mix; f.scripted = true;
      dep.push(f);
    });
    arr.forEach(function (f) { f.pax = Math.round(f.seats * f.load); f.eibt = f.onBlock + f.eibtErr; f.dir = 'arr'; });
    dep.forEach(function (f) { f.pax = Math.round(f.seats * f.load); f.dir = 'dep'; });
    arr.sort(function (a, b) { return a.onBlock - b.onBlock; });
    dep.sort(function (a, b) { return a.std - b.std; });
    return { arr: arr, dep: dep };
  }

  /* exogenous arrivals */

  function zeros(len) { var o = []; for (var q = 0; q < NQ; q++) o.push(new Float64Array(len)); return o; }
  var ARR_Q = ['A-CRW', 'A-CIT', 'A-RES', 'A-VIS', 'A-EG'].map(function (id) { return QI[id]; });

  function laneSplitArr(pax, mix) {
    var e = PARAMS.egateShare;
    return [pax * mix.CRW, pax * mix.CIT * (1 - e), pax * mix.RES * (1 - e), pax * mix.VIS, pax * (mix.CIT + mix.RES) * e];
  }

  function addArr(ext, off, len, seats, ob, load, walk, mix) {
    var lanes = laneSplitArr(seats * load, mix);
    for (var cp = -1; cp <= 1; cp++) {
      var start = Math.round(ob + walk) + cp * DAY + PRE - off;
      if (start + 12 <= 0 || start >= len) continue;
      for (var k = 0; k < 12; k++) {
        var j = start + k;
        if (j < 0 || j >= len) continue;
        for (var x = 0; x < 5; x++) ext[ARR_Q[x]][j] += lanes[x] * W12[k];
      }
    }
  }

  function addDep(ext, off, len, f, load, shift) {
    var pax = f.seats * load, m = f.mix;
    var nt = pax * (1 - m.TRF), crew = pax * m.CRW;
    var counter = (nt - crew) * (1 - PARAMS.online), direct = nt - counter;
    var ci = QI['CI-' + f.island], sec = f.island === 'A' || f.island === 'B' ? QI['SEC-N'] : QI['SEC-S'];
    var wk = PARAMS.walkToSecurity;
    for (var cp = -1; cp <= 1; cp++) {
      var base = Math.round(f.std - PARAMS.showFrom + shift) + cp * DAY + PRE - off;
      if (base + SHOW_LEN + wk <= 0 || base >= len) continue;
      for (var k = 0; k < SHOW_LEN; k++) {
        var j = base + k;
        if (j >= 0 && j < len) ext[ci][j] += counter * SHOW[k];
        var j2 = j + wk;
        if (j2 >= 0 && j2 < len) ext[sec][j2] += direct * SHOW[k];
      }
    }
  }

  /* mode: actual | expected | dayahead; pert: optional perturbation source (Monte Carlo) */
  function buildExt(S, mode, off, len, pert, now) {
    var ext = zeros(len);
    S.arr.forEach(function (f) {
      var ob, load, walk, mix;
      if (mode === 'actual') { ob = f.onBlock; load = f.load; walk = f.walk; mix = f.mix; }
      else if (mode === 'expected') { ob = f.eibt; load = f.load; walk = 11; mix = f.mix; }
      else { ob = f.sched; load = f.booked; walk = 11; mix = PARAMS.mix; }
      if (pert) {
        var landed = f.onBlock <= now;
        if (landed) {
          ob = f.onBlock;
          walk = f.onBlock + f.walk <= now ? f.walk : Math.max(now - f.onBlock + 1, clamp(11 + Math.round((pert() * 2 - 1) * 3), 8, 15));
        } else {
          ob = Math.max(now + 1, f.eibt + Math.round((pert() * 2 - 1) * PARAMS.mcOnBlock));
          walk = clamp(11 + Math.round((pert() * 2 - 1) * 3), 8, 15);
        }
        load = f.load * (1 + (pert() * 2 - 1) * PARAMS.mcLoad);
      }
      addArr(ext, off, len, f.seats, ob, load, walk, mix);
    });
    S.dep.forEach(function (f) {
      var load, shift;
      if (mode === 'actual') { load = f.load; shift = f.shift; }
      else { load = f.booked; shift = 0; }
      if (pert) { load = f.booked * (1 + (pert() * 2 - 1) * 0.05); shift = Math.round((pert() * 2 - 1) * PARAMS.mcOnBlock); }
      addDep(ext, off, len, f, load, shift);
    });
    return ext;
  }

  /* roster (day-ahead plan) */

  function buildRoster(S) {
    var ext = buildExt(S, 'dayahead', 0, N);
    var dem = ext.map(function (a) { return Float64Array.from(a); });
    var secN = QI['SEC-N'], secS = QI['SEC-S'];
    for (var i = 0; i < N; i++) {
      var j = i + 8;
      if (j < N) {
        dem[secN][j] += ext[QI['CI-A']][i] + ext[QI['CI-B']][i];
        dem[secS][j] += ext[QI['CI-C']][i] + ext[QI['CI-D']][i];
      }
    }
    for (i = 0; i < N; i++) {
      var tot = dem[secN][i] + dem[secS][i], j2 = i + 6;
      if (j2 >= N) continue;
      ['CRW', 'CIT', 'RES', 'VIS', 'EG'].forEach(function (ln) { dem[QI['D-' + ln]][j2] += tot * EG_SHARE_DEP[ln]; });
    }
    for (i = 1; i < N; i++) {
      dem[QI['A-VIS']][i] += PARAMS.egateReject * dem[QI['A-EG']][i - 1];
      dem[QI['D-VIS']][i] += PARAMS.egateReject * dem[QI['D-EG']][i - 1];
    }
    var roster = [];
    QDEF.forEach(function (def, q) {
      var r = new Int16Array(96), max = def.servers.length;
      var need = [];
      for (var b = 0; b < 96; b++) {
        var s = 0;
        for (var k = 0; k < 15; k++) s += dem[q][PRE + b * 15 + k];
        need.push(s / 15 * def.svc / 60 / 0.88 * 1.04);
      }
      for (var h = 0; h < 24; h++) {
        var n = 0, any = 0;
        for (var bb = h * 4 - 1; bb < h * 4 + 4; bb++) { var v = need[mod(bb, 96)]; n = Math.max(n, v); any += v; }
        if (def.group === 'sec') n = Math.ceil(any / 5 * 0.95 - 0.1);
        else if (def.area === 'dep') n = Math.ceil(n * 0.92 - 0.1);
        else if (def.group === 'ci') n = Math.ceil(n * 0.8 - 0.1);
        else n = Math.ceil(n - 0.05);
        if (def.group === 'imm') n = Math.max(n, def.lane === 'VIS' ? 2 : 1);
        else if (def.group === 'egate') n = max;
        else if (def.group === 'sec') n = Math.max(n, 1);
        else if (def.group === 'ci') n = any > 0.02 ? Math.max(n, 2) : 0;
        n = clamp(n, 0, max);
        for (var z = 0; z < 4; z++) r[h * 4 + z] = n;
      }
      roster.push(r);
    });
    SCRIPT.roster.forEach(function (o) {
      for (var m = o.from; m < o.to; m += 15) roster[QI[o.q]][Math.floor(m / 15)] = o.n;
    });
    return roster;
  }

  function strHash(str) {
    var h = 2166136261;
    for (var i = 0; i < str.length; i++) { h ^= str.charCodeAt(i); h = Math.imul(h, 16777619); }
    return h >>> 0;
  }

  /* ad-hoc flights: each draws from its own stream (seed plus flight ID), so existing flights never shift */
  function addAdhoc(S, seed, flights) {
    (flights || []).forEach(function (x) {
      if (!x || !x.id || !x.code || (x.dir !== 'arr' && x.dir !== 'dep')) return;
      var R = mulberry32(mix32(seed ^ strHash(x.id)));
      var mix = x.mix && typeof x.mix === 'object' ? x.mix : PARAMS.mix;
      var load = clamp(Number(x.load) || 0.8, 0.3, 1);
      var actual = clamp(Math.round((load + (R() - 0.5) * 0.04) * 100) / 100, 0.3, 1);
      var seats = Math.round(Number(x.seats) || 180), t = Math.round(Number(x.time) || 0);
      if (x.dir === 'arr') {
        S.arr.push({ code: x.code, carrier: x.code.slice(0, 2), sched: t, onBlock: t, seats: seats, booked: load, load: actual,
          walk: 8 + Math.floor(R() * 8), eibtErr: 0, eibt: t, mix: mix, pax: Math.round(seats * actual), dir: 'arr', adhoc: x.id });
      } else {
        var isl = /^[A-D]$/.test(x.island) ? x.island : (x.handler === 'A' ? 'A' : 'C');
        S.dep.push({ code: x.code, carrier: x.code.slice(0, 2), std: t, seats: seats, booked: load, load: actual, shift: Math.round((R() - 0.5) * 10),
          island: isl, handler: isl < 'C' ? 'A' : 'B', mix: mix, pax: Math.round(seats * actual), dir: 'dep', adhoc: x.id });
      }
    });
    S.arr.sort(function (a, b) { return a.onBlock - b.onBlock; });
    S.dep.sort(function (a, b) { return a.std - b.std; });
    return S;
  }

  function normOverrides(list) {
    return (list || []).filter(function (o) { return o && QI[o.q] != null && o.to > o.from; }).map(function (o) {
      return { q: QI[o.q], from: Number(o.from), to: Number(o.to), n: Math.max(0, Math.round(Number(o.n) || 0)) };
    });
  }

  /* counter allocations: allocated counters open during their window; the plan becomes at least the allocated count */
  function normAlloc(list) {
    var out = {};
    (list || []).forEach(function (a) {
      if (!a || !/^[A-D]$/.test(a.island)) return;
      var q = QI['CI-' + a.island], ks = [];
      for (var k = Math.max(1, a.fromCtr) ; k <= Math.min(12, a.toCtr); k++) ks.push(k - 1);
      if (!ks.length || !(a.close > a.open)) return;
      (out[q] = out[q] || []).push({ from: a.open, to: a.close, ks: ks, code: a.flight, id: a.id });
    });
    return out;
  }
  function openSetFor(al, m, n, max) {
    var A = {}, cnt = 0;
    for (var x = 0; x < al.length; x++) {
      var a = al[x];
      if (m >= a.from && m < a.to) a.ks.forEach(function (k) { if (!A[k]) { A[k] = 1; cnt++; } });
    }
    if (!cnt) return null;
    var total = Math.min(max, Math.max(n, cnt)), need = total - cnt, set = [];
    for (var k = 0; k < max; k++) { if (A[k]) set.push(k); else if (need > 0) { set.push(k); need -= 1; } }
    return set;
  }

  function normAccepted(list) {
    return (list || []).filter(function (a) { return a && a.counts && typeof a.from === 'number'; }).map(function (a) {
      var c = {};
      for (var qid in a.counts) if (QI[qid] != null) c[QI[qid]] = a.counts[qid];
      return { area: a.area, from: a.from, until: a.until, counts: c };
    });
  }

  function makePlan(roster, acc, ov) {
    ov = ov || [];
    return function (q, m) {
      var n = roster[q][Math.floor(mod(m, DAY) / 15)];
      for (var x = 0; x < acc.length; x++) {
        var a = acc[x];
        if (m >= a.from && m < a.until && a.counts[q]) {
          var v = a.counts[q][Math.floor((m - a.from) / 15)];
          if (v != null) n = v;
        }
      }
      for (var y = 0; y < ov.length; y++) { var o = ov[y]; if (o.q === q && m >= o.from && m < o.to) n = o.n; }
      return n;
    };
  }

  /* server behaviour */

  function serverData(seed) {
    return QDEF.map(function (def, q) {
      return def.servers.map(function (sid, k) {
        var key = q * 64 + k;
        var pause = new Uint8Array(N), unknown = new Uint8Array(N);
        var rate = def.group === 'imm' ? 1 / 110 : def.group === 'ci' ? 1 / 140 : def.group === 'sec' ? 1 / 240 : 0;
        var i = 0;
        while (i < N && rate > 0) {
          if (h3(seed ^ 0x1234567, key, i) < rate) {
            var len = 2 + Math.floor(h3(seed ^ 0x7654321, key, i) * 5);
            for (var z = 0; z < len && i + z < N; z++) pause[i + z] = 1;
            i += len + 3;
          } else i++;
        }
        if (def.group === 'imm' || def.group === 'ci') {
          for (i = 0; i < N; i++) {
            if (h3(seed ^ 0x2468ace, key, i) < 1 / 1100) {
              var ul = 1 + Math.floor(h3(seed ^ 0x13579bd, key, i) * 3);
              for (z = 0; z < ul && i + z < N; z++) unknown[i + z] = 1;
              i += ul;
            }
          }
        }
        if (sid === SCRIPT.egateOos.gate) {
          for (var m = SCRIPT.egateOos.from; m < SCRIPT.egateOos.to; m++) pause[m + PRE] = 1;
        }
        var factor = def.group === 'egate' ? 1 : def.group === 'sec' ? 0.94 + h3(seed ^ 0x55aa, key, 1) * 0.12 : 0.88 + h3(seed ^ 0x55aa, key, 1) * 0.3;
        return { id: sid, factor: factor, pause: pause, unknown: unknown, oos: sid === SCRIPT.egateOos.gate };
      });
    });
  }

  var PAUSE_FRAC = { imm: 0.035, ci: 0.03, sec: 0.02, egate: 0 };

  /* network step shared by all runs */

  function inflowAt(q, j, D, rejA, rejD, lagOk) {
    var def = QDEF[q];
    if (def.area === 'arr') return def.lane === 'VIS' && j >= 1 ? rejA * D[QI['A-EG']][j - 1] : 0;
    if (def.area === 'dep') {
      var sec = j >= 2 ? D[QI['SEC-N']][j - 2] + D[QI['SEC-S']][j - 2] : 0;
      var v = sec * EG_SHARE_DEP[def.lane];
      if (def.lane === 'VIS' && j >= 1) v += rejD * D[QI['D-EG']][j - 1];
      return v;
    }
    if (def.id === 'SEC-N') return j >= 3 ? D[QI['CI-A']][j - 3] + D[QI['CI-B']][j - 3] : 0;
    if (def.id === 'SEC-S') return j >= 3 ? D[QI['CI-C']][j - 3] + D[QI['CI-D']][j - 3] : 0;
    return 0;
  }

  function rejectRate(seed, side, i) {
    return PARAMS.egateReject * (0.6 + 0.8 * h3(seed ^ 0xe6a7e, side, i));
  }

  /* main run */

  function run(cfg) {
    cfg = cfg || {};
    var seed = (cfg.seed == null ? DEFAULT_SEED : Number(cfg.seed)) >>> 0;
    var S = buildSchedule(seed);
    var roster = buildRoster(S);
    addAdhoc(S, seed, cfg.flights);
    var acc = normAccepted(cfg.accepted);
    var plan = makePlan(roster, acc, normOverrides(cfg.overrides));
    var alloc = normAlloc(cfg.allocations);
    var ext = buildExt(S, 'actual', 0, N);
    for (var qq = 0; qq < NQ; qq++) for (var ii = 0; ii < N; ii++) ext[qq][ii] *= 0.5 + h3(seed ^ 0xa11ce, qq, ii);
    var srv = serverData(seed);
    var A = zeros(N), D = zeros(N), L = zeros(N), C = zeros(N), R5 = zeros(N);
    var OPEN = QDEF.map(function () { return new Int8Array(N); });
    var PAUSED = QDEF.map(function () { return new Int8Array(N); });
    var REJ = [new Float64Array(N), new Float64Array(N)];
    var rateSum = QDEF.map(function (def, q) {
      var o = [0];
      srv[q].forEach(function (s) { o.push(o[o.length - 1] + 60 / (def.svc * s.factor)); });
      return o;
    });

    for (var i = 0; i < N; i++) {
      var m = i - PRE;
      REJ[0][i] = rejectRate(seed, 1, i);
      REJ[1][i] = rejectRate(seed, 2, i);
      for (var q = 0; q < NQ; q++) {
        var def = QDEF[q];
        var n = Math.min(plan(q, m), def.servers.length), cap = 0, np = 0;
        var ks = alloc[q] ? openSetFor(alloc[q], m, n, def.servers.length) : null;
        if (ks) {
          for (var x2 = 0; x2 < ks.length; x2++) {
            var sd2 = srv[q][ks[x2]];
            if (sd2.pause[i]) { np++; continue; }
            cap += 60 / (def.svc * sd2.factor);
          }
          n = ks.length;
        } else {
          for (var k = 0; k < n; k++) {
            var sd = srv[q][k];
            if (sd.pause[i]) { np++; continue; }
            cap += 60 / (def.svc * sd.factor);
          }
        }
        cap *= 1 + 0.08 * (h3(seed ^ 0xc0ffee, q, i) - 0.5);
        var a = ext[q][i] + inflowAt(q, i, D, i ? REJ[0][i - 1] : 0.07, i ? REJ[1][i - 1] : 0.07);
        var back = i ? L[q][i - 1] : 0;
        var d = Math.min(back + a, cap);
        A[q][i] = a; D[q][i] = d; L[q][i] = Math.max(0, back + a - d); C[q][i] = cap;
        OPEN[q][i] = n; PAUSED[q][i] = np;
        var s5 = 0, c5 = 0;
        for (var z = Math.max(0, i - 4); z <= i; z++) { s5 += C[q][z]; c5++; }
        R5[q][i] = s5 / c5;
      }
    }

    var cumA = [], cumD = [], lastExit = [], meanWait = [];
    for (q = 0; q < NQ; q++) {
      var ca = new Float64Array(N + 1), cd = new Float64Array(N + 1);
      for (i = 0; i < N; i++) { ca[i + 1] = ca[i] + A[q][i]; cd[i + 1] = cd[i] + D[q][i]; }
      cumA.push(ca); cumD.push(cd);
      var le = new Int32Array(N), mw = new Float64Array(N);
      var j = 0;
      for (i = 0; i < N; i++) {
        if (A[q][i] < 1e-9) { le[i] = i; mw[i] = NaN; continue; }
        var pEnd = ca[i + 1] - 1e-7;
        while (j < N && cd[j + 1] < pEnd) j++;
        le[i] = j;
        mw[i] = Math.max(0, exitTimeIn(cd, D[q], ca[i] + A[q][i] * 0.5) - (i + 0.5));
      }
      lastExit.push(le); meanWait.push(mw);
    }

    var day = {
      seed: seed, S: S, roster: roster, accepted: acc, plan: plan, srv: srv, rateSum: rateSum, alloc: alloc,
      A: A, D: D, L: L, C: C, R5: R5, OPEN: OPEN, PAUSED: PAUSED, REJ: REJ,
      cumA: cumA, cumD: cumD, lastExit: lastExit, meanWait: meanWait,
      _binCache: {}
    };
    attach(day);
    attachRules(day);
    day.capsFn = typeof cfg.caps === 'function' ? cfg.caps : function (qid) { return DEFAULT_CAPS[qid] || null; };
    day.alertsAll = [];
    if (!cfg.lite) day.setRules(cfg.rules || SEED_RULES);
    return day;
  }

  function exitTimeIn(cd, d, p) {
    if (p > cd[N]) return Infinity;
    var lo = 0, hi = N - 1;
    while (lo < hi) { var mid = (lo + hi) >> 1; if (cd[mid + 1] < p) lo = mid + 1; else hi = mid; }
    var dd = d[lo];
    return lo + (dd > 1e-12 ? (p - cd[lo]) / dd : 0);
  }

  /* day object methods */

  function attach(day) {
    var seed = day.seed;

    day.idx = function (m) { return m + PRE; };

    /* current throughput: staffed servers at their expected service rate (what the border or DCS system reports) */
    day.thr = function (q, m) {
      var i = m + PRE, def = QDEF[q];
      if (day.OPEN[q][i] - day.PAUSED[q][i] <= 0) return 0;
      if (def.group === 'egate') return day.C[q][i];
      return day.rateSum[q][Math.min(day.OPEN[q][i], def.servers.length)] * (1 - PAUSE_FRAC[def.group]);
    };

    day.degraded = function (q, m) {
      var id = typeof q === 'number' ? QDEF[q].id : q;
      for (var x = 0; x < OUTAGES.length; x++) { var o = OUTAGES[x]; if (o.zone === id && m >= o.from && m < o.to) return o; }
      return null;
    };

    day.sensorOffline = function (sid, m) {
      for (var x = 0; x < OUTAGES.length; x++) { var o = OUTAGES[x]; if (o.sensor === sid && m >= o.from && m < o.to) return true; }
      return false;
    };

    /* the live state of one queue at clock minute m */
    day.state = function (q, m) {
      var i = m + PRE, len = day.L[q][i], rate = day.thr(q, m);
      var active = day.OPEN[q][i] - day.PAUSED[q][i];
      var nc = active > 0 && rate > 0.01 ? (len + 1) / rate : null;
      var dg = day.degraded(q, m);
      var band = null;
      if (dg && nc != null) {
        var lo = Math.floor(nc * 0.75 / 5) * 5, hi = Math.ceil(nc * 1.25 / 5) * 5;
        if (hi - lo < 10) hi = lo + 10;
        band = [lo, hi];
      }
      return { q: q, id: QDEF[q].id, len: len, rate: rate, cap: day.C[q][i], open: day.OPEN[q][i], paused: day.PAUSED[q][i],
        active: active, nowcast: nc, noService: nc == null, degraded: !!dg, band: band, arrivals: day.A[q][i], served: day.D[q][i] };
    };

    day.states = function (m) { var o = []; for (var q = 0; q < NQ; q++) o.push(day.state(q, m)); return o; };

    /* desk, counter, lane and gate states */
    /* which servers are open at minute m: allocated counters first, then the lowest others */
    day.openSet = function (q, m) {
      var i = m + PRE, def = QDEF[q];
      if (day.alloc[q]) {
        var ks = openSetFor(day.alloc[q], m, Math.min(day.plan(q, m), def.servers.length), def.servers.length);
        if (ks) { var o = {}; ks.forEach(function (k) { o[k] = 1; }); return o; }
      }
      var n = day.OPEN[q][i], o2 = {};
      for (var k = 0; k < n; k++) o2[k] = 1;
      return o2;
    };
    day.openCount = function (q, m, n) {
      var def = QDEF[q];
      n = Math.min(n, def.servers.length);
      if (!day.alloc[q]) return n;
      var ks = openSetFor(day.alloc[q], m, n, def.servers.length);
      return ks ? ks.length : n;
    };
    day.allocAt = function (q, k, m) {
      var al = day.alloc[q];
      if (!al) return null;
      for (var x = 0; x < al.length; x++) if (m >= al[x].from && m < al[x].to && al[x].ks.indexOf(k) >= 0) return al[x];
      return null;
    };

    day.servers = function (q, m) {
      var i = m + PRE, def = QDEF[q], out = [];
      var busyAll = day.L[q][i] > 0.5;
      var act = [], open = day.openSet(q, m);
      for (var k = 0; k < def.servers.length; k++) {
        var sd = day.srv[q][k];
        if (open[k] && !sd.pause[i]) act.push(k);
      }
      var perRate = act.length ? day.C[q][i] / act.length : 1;
      var busy = busyAll ? act.length : Math.min(act.length, Math.ceil(day.D[q][i] / Math.max(perRate, 0.01) - 0.05));
      var bi = 0;
      for (k = 0; k < def.servers.length; k++) {
        sd = day.srv[q][k];
        var st;
        if (sd.oos && sd.pause[i]) st = 'oos';
        else if (!open[k]) st = 'closed';
        else if (sd.unknown[i]) st = 'unknown';
        else if (sd.pause[i]) st = 'paused';
        else { st = bi < busy ? 'serving' : 'idle'; bi++; }
        out.push({ id: def.servers[k], state: st, k: k, svc: def.svc * sd.factor });
      }
      return out;
    };

    /* per-server interval aggregates for the 15-minute interval starting at bs, up to now */
    day.serverInterval = function (q, bs, now) {
      var def = QDEF[q], out = [];
      var end = Math.min(bs + 15, now + 1);
      for (var k = 0; k < def.servers.length; k++) {
        var sd = day.srv[q][k], pax = 0, mins = 0;
        for (var m = bs; m < end; m++) {
          var i = m + PRE;
          if (!day.openSet(q, m)[k] || sd.pause[i]) continue;
          var r = 60 / (def.svc * sd.factor);
          var totalR = day.C[q][i] > 0 ? day.C[q][i] : 1;
          pax += day.D[q][i] * (r * (1 + 0.08 * (h3(seed ^ 0xc0ffee, q, i) - 0.5))) / totalR;
          mins++;
        }
        var svc = mins ? def.svc * sd.factor * (0.96 + 0.08 * h3(seed ^ 0xbeef, q * 64 + k, bs)) : null;
        out.push({ id: def.servers[k], pax: pax, minutes: mins, svc: pax >= 1 ? svc : null });
      }
      return out;
    };

    day.exitTime = function (q, p) { return exitTimeIn(day.cumD[q], day.D[q], p); };

    function disp(q, i, k) { return 0.88 + 0.3 * h3(seed ^ 0x3c6ef372, q * 8 + k, i); }

    /* estimated or realised wait samples for one cohort (entry minute index i) as seen at clock index ni */
    function cohortSamples(q, i, ni, out) {
      var a = day.A[q][i];
      if (a < 1e-6) return 0;
      var ca = day.cumA[q][i], cd = day.cumD[q], rate = Math.max(day.R5[q][ni], 0.05);
      for (var k = 0; k < 5; k++) {
        var f = (k + 0.5) / 5, p = ca + a * f, entry = i + f;
        var ex = day.exitTime(q, p), w;
        if (ex <= ni + 1) w = ex - entry;
        else w = (ni + 1) + (p - cd[ni + 1]) / rate - entry;
        out.push([Math.max(0, w) * disp(q, i, k), a / 5]);
      }
      return a;
    }

    function wq(samples, pct) {
      if (!samples.length) return null;
      samples.sort(function (x, y) { return x[0] - y[0]; });
      var tot = 0, x;
      for (x = 0; x < samples.length; x++) tot += samples[x][1];
      var acc = 0;
      for (x = 0; x < samples.length; x++) { acc += samples[x][1]; if (acc >= pct * tot - 1e-9) return samples[x][0]; }
      return samples[samples.length - 1][0];
    }
    day.quantile = wq;

    /* one 15-minute bin (start bs) as seen at clock minute now */
    day.bin = function (q, bs, now) {
      var key = q * 100000 + bs + 5000;
      var c = day._binCache[key];
      if (c && now >= c.finalAt) return c;
      var ni = now + PRE, samples = [], pax = 0, fin = true, lastEx = 0;
      var open = now < bs + 14;
      var end = Math.min(bs + 15, now + 1);
      for (var m = bs; m < end; m++) {
        var i = m + PRE;
        var a = cohortSamples(q, i, ni, samples);
        if (a > 0) { pax += a; lastEx = Math.max(lastEx, day.lastExit[q][i] - PRE); if (day.lastExit[q][i] > ni) fin = false; }
      }
      if (open) fin = false;
      var res = { q: q, start: bs, p90: pax >= 0.5 ? wq(samples, 0.9) : null, pax: pax, status: fin ? 'final' : 'provisional', open: open,
        finalAt: fin ? Math.max(bs + 14, lastEx) : Infinity, profile: null };
      if (fin) day._binCache[key] = res;
      return res;
    };

    /* samples for a bin of any size, for contract evaluation */
    day.binStats = function (q, bs, size, now) {
      var ni = now + PRE, samples = [], pax = 0, fin = true;
      var end = Math.min(bs + size, now + 1);
      for (var m = bs; m < end; m++) {
        var i = m + PRE, a = cohortSamples(q, i, ni, samples);
        if (a > 0) { pax += a; if (day.lastExit[q][i] > ni) fin = false; }
      }
      if (now < bs + size - 1) fin = false;
      return { pax: pax, status: fin ? 'final' : 'provisional', samples: samples,
        pct: function (p) { return pax >= 0.5 ? wq(samples.slice(), p) : null; },
        share: function (t) { var tot = 0, ok = 0; samples.forEach(function (x) { tot += x[1]; if (x[0] <= t) ok += x[1]; }); return tot > 0 ? ok / tot : null; } };
    };

    /* realised mean wait per entry minute; provisional ones estimated at now */
    day.waitSeries = function (q, from, to, now) {
      var out = [], ni = now + PRE;
      for (var m = from; m <= Math.min(to, now); m++) {
        var i = m + PRE;
        if (day.A[q][i] < 1e-6) { out.push({ m: m, w: 0, final: true, empty: true }); continue; }
        if (day.lastExit[q][i] <= ni) out.push({ m: m, w: day.meanWait[q][i], final: true });
        else {
          var p = day.cumA[q][i] + day.A[q][i] * 0.5, ex = day.exitTime(q, p), w;
          if (ex <= ni + 1) w = ex - (i + 0.5);
          else w = (ni + 1) + (p - day.cumD[q][ni + 1]) / Math.max(day.R5[q][ni], 0.05) - (i + 0.5);
          out.push({ m: m, w: Math.max(0, w), final: false });
        }
      }
      return out;
    };

    /* arrival-weighted P90 over an arbitrary window of entry minutes (final data only unless now given) */
    day.p90 = function (q, from, to, now) {
      var samples = [], ni = (now == null ? DAY + POST - 2 : now) + PRE, pax = 0;
      for (var m = from; m < to; m++) pax += cohortSamples(q, m + PRE, ni, samples);
      return { p90: pax >= 0.5 ? wq(samples, 0.9) : null, pax: pax };
    };

    day.sum = function (arr, q, from, to) { var s = 0; for (var m = from; m < to; m++) s += arr[q][m + PRE]; return s; };

    day.egate = function (side, now, win) {
      var q = QI[side + '-EG'], w = win || 60, proc = 0, rej = 0, capS = 0;
      for (var m = now - w + 1; m <= now; m++) {
        var i = m + PRE;
        proc += day.D[q][i]; rej += day.D[q][i] * day.REJ[side === 'A' ? 0 : 1][i]; capS += day.C[q][i];
      }
      var sv = day.servers(q, now);
      var inService = sv.filter(function (s) { return s.state !== 'oos' && s.state !== 'closed'; }).length;
      return { processed: proc, rejects: rej, rejectRate: proc > 0 ? rej / proc : 0, util: capS > 0 ? proc / capS : 0, inService: inService, total: sv.length, servers: sv };
    };

    /* aggregated simulation (expected runs, Monte Carlo, recommendations) */

    function capAgg(q, m, n, sf) {
      var def = QDEF[q];
      n = day.openCount(q, m, n);
      if (def.group === 'egate') {
        var c = 0, i = m + PRE;
        for (var k = 0; k < n; k++) { var sd = day.srv[q][k]; if (!(sd.oos && i >= 0 && i < N && sd.pause[i])) c += 60 / def.svc; }
        return c * sf;
      }
      return day.rateSum[q][n] * (1 - PAUSE_FRAC[def.group]) * sf;
    }

    /* simulate from clock minute m0 (state known at m0) for H minutes */
    function simAgg(m0, H, ext, off, planFn, sf, onMinute) {
      var i0 = m0 + PRE, len = ext[0].length;
      var Dw = zeros(len), back = [];
      for (var q = 0; q < NQ; q++) {
        back.push(day.L[q][i0]);
        for (var j = 0; j < len; j++) { var ii = off + j; if (ii <= i0) Dw[q][j] = day.D[q][ii]; }
      }
      for (var h = 1; h <= H; h++) {
        var i = i0 + h, jj = i - off, m = i - PRE;
        for (q = 0; q < NQ; q++) {
          var cap = capAgg(q, m, planFn(q, m), sf ? sf[q] : 1);
          var a = ext[q][jj] + inflowAt(q, jj, Dw, PARAMS.egateReject, PARAMS.egateReject);
          var d = Math.min(back[q] + a, cap);
          back[q] = Math.max(0, back[q] + a - d);
          Dw[q][jj] = d;
          if (onMinute) onMinute(q, h, a, back[q], cap);
        }
      }
    }

    function windowExt(m0, H, pert) {
      var off = m0 + PRE - 4, len = H + 6;
      return { off: off, ext: buildExt(day.S, 'expected', off, len, pert || null, m0) };
    }

    /* Monte Carlo forecast from now: P10, P50, P90 of the joining wait per minute */
    day.forecast = function (now, opt) {
      opt = opt || {};
      var H = opt.horizon || 120, R = opt.runs || PARAMS.mcRuns, planFn = opt.plan || day.plan;
      var qs = opt.queues || QDEF.map(function (d, q) { return q; });
      var want = {}; qs.forEach(function (q) { want[q] = new Float32Array(R * H); });
      var wts = {}; qs.forEach(function (q) { wts[q] = new Float32Array(R * H); });
      for (var r = 0; r < R; r++) {
        var rng = mulberry32(mix32(day.seed ^ Math.imul(now + 7, 7919) ^ Math.imul(r + 1, 104729) ^ (opt.salt || 0)));
        var w = windowExt(now, H, rng);
        var sf = QDEF.map(function () { return 1 / (1 + (rng() * 2 - 1) * PARAMS.mcService); });
        simAgg(now, H, w.ext, w.off, planFn, sf, function (q, h, a, back, cap) {
          if (!want[q]) return;
          want[q][r * H + h - 1] = cap > 0.01 ? Math.min(120, (back + 0.5) / cap) : 120;
          wts[q][r * H + h - 1] = a;
        });
      }
      var out = { now: now, horizon: H, runs: R, q: {} };
      qs.forEach(function (q) {
        var p10 = [], p50 = [], p90 = [];
        for (var h = 0; h < H; h++) {
          var v = [];
          for (var r2 = 0; r2 < R; r2++) v.push(want[q][r2 * H + h]);
          v.sort(function (x, y) { return x - y; });
          p10.push(v[Math.floor(0.1 * (R - 1))]); p50.push(v[Math.floor(0.5 * (R - 1))]); p90.push(v[Math.ceil(0.9 * (R - 1))]);
        }
        out.q[q] = { p10: p10, p50: p50, p90: p90, raw: want[q], w: wts[q] };
      });
      return out;
    };

    /* expected (deterministic) full-day run from the schedule, API counts and the plan */
    var expCache = null;
    day.expected = function () {
      if (expCache) return expCache;
      var ext = buildExt(day.S, 'expected', 0, N);
      var Aexp = zeros(N), Lexp = zeros(N), Cexp = zeros(N), Dexp = zeros(N);
      var back = new Float64Array(NQ);
      for (var i = 0; i < N; i++) {
        var m = i - PRE;
        for (var q = 0; q < NQ; q++) {
          var cap = capAgg(q, m, day.plan(q, m), 1);
          var a = ext[q][i] + inflowAt(q, i, Dexp, PARAMS.egateReject, PARAMS.egateReject);
          var d = Math.min(back[q] + a, cap);
          back[q] = Math.max(0, back[q] + a - d);
          Aexp[q][i] = a; Dexp[q][i] = d; Lexp[q][i] = back[q]; Cexp[q][i] = cap;
        }
      }
      expCache = { A: Aexp, D: Dexp, L: Lexp, C: Cexp, ext: ext };
      return expCache;
    };

    /* day-ahead forecast from the schedule only (generic lane mix, booked loads, planned roster) */
    var daCache = null;
    day.dayAhead = function () {
      if (daCache) return daCache;
      var ext = buildExt(day.S, 'dayahead', 0, N);
      var W = zeros(N), Aa = zeros(N), Dd = zeros(N);
      var back = new Float64Array(NQ);
      for (var i = 0; i < N; i++) {
        var m = i - PRE;
        for (var q = 0; q < NQ; q++) {
          var cap = capAgg(q, m, day.roster[q][Math.floor(mod(m, DAY) / 15)], 1);
          var a = ext[q][i] + inflowAt(q, i, Dd, PARAMS.egateReject, PARAMS.egateReject);
          var d = Math.min(back[q] + a, cap);
          back[q] = Math.max(0, back[q] + a - d);
          Aa[q][i] = a; Dd[q][i] = d; W[q][i] = cap > 0.01 ? (back[q] + 0.5) / cap : 0;
        }
      }
      daCache = { A: Aa, W: W };
      return daCache;
    };

    /* staffing recommendation for an area, 15-minute blocks starting at the next block */
    day.recommend = function (now, areaId, blocks, opt) {
      opt = opt || {};
      var area = AREAS[areaId], nb = blocks || 24;
      var target = PARAMS.recTarget[areaId];
      var b0 = Math.floor(now / 15) + 1, start = b0 * 15;
      var exp = day.expected();
      var qs = area.queues.map(function (id) { return QI[id]; });
      var rec = {}, planned = {};
      qs.forEach(function (q) {
        var def = QDEF[q], maxK = def.servers.length, back0 = day.L[q][now + PRE];
        for (var m = now + 1; m < start; m++) back0 = Math.max(0, back0 + exp.A[q][m + PRE] - capAgg(q, m, day.plan(q, m), 1));
        var minBase = def.group === 'imm' ? (def.lane === 'VIS' ? 2 : 1) : def.group === 'sec' ? 1 : 0;
        var lam = [], k = [], b, t;
        for (b = 0; b < nb; b++) {
          var sum = 0;
          for (t = 0; t < 15; t++) sum += exp.A[q][start + b * 15 + t + PRE];
          lam.push(sum / 15);
        }
        for (b = 0; b < nb; b++) {
          var kk = minBase;
          if (def.group === 'ci') kk = lam[b] < 0.02 && (b === 0 || lam[b - 1] < 0.02) ? 0 : 1;
          while (kk < maxK && capAgg(q, start + b * 15, kk, 1) * 0.85 < lam[b]) kk++;
          k.push(kk);
        }
        function blockRun(bk, bs, kk) {
          var worst = 0;
          for (var tt = 0; tt < 15; tt++) {
            var mm = bs + tt, capk = capAgg(q, mm, kk, 1), a = exp.A[q][mm + PRE];
            bk = Math.max(0, bk + a - capk);
            var wv = capk > 0.01 ? (bk + 0.5) / capk : (bk > 0.3 ? 999 : 0);
            if (wv > worst) worst = wv;
          }
          return { back: bk, worst: worst };
        }
        for (var iter = 0; iter < 8; iter++) {
          var back = back0, bad = -1;
          for (b = 0; b < nb; b++) {
            var r0 = blockRun(back, start + b * 15, k[b]);
            while (r0.worst > target && k[b] < maxK) { k[b]++; r0 = blockRun(back, start + b * 15, k[b]); }
            if (r0.worst > target && bad < 0) bad = b;
            back = r0.back;
          }
          if (bad <= 0) break;
          var j = bad - 1;
          while (j >= 0 && k[j] >= maxK) j -= 1;
          if (j < 0) break;
          k[j] = maxK;
        }
        rec[def.id] = k;
        planned[def.id] = [];
        for (b = 0; b < nb; b++) planned[def.id].push(day.openCount(q, start + b * 15, day.plan(q, start + b * 15)));
      });
      var res = { area: areaId, from: start, until: start + nb * 15, blocks: nb, rec: rec, planned: planned, target: target };
      if (opt.predict !== false) {
        var recPlan = function (q, m) {
          var id = QDEF[q].id;
          if (rec[id] && m >= start && m < res.until) return rec[id][Math.floor((m - start) / 15)];
          return day.plan(q, m);
        };
        var H = res.until - now;
        var runs = opt.runs || 16;
        var lanesP90 = function (fc) {
          var out = {};
          qs.forEach(function (q) {
            var arr = [];
            for (var b2 = 0; b2 < nb; b2++) {
              var sp = [];
              for (var r = 0; r < runs; r++) {
                for (var t2 = 0; t2 < 15; t2++) {
                  var hIdx = start + b2 * 15 + t2 - now - 1;
                  if (hIdx < 0 || hIdx >= H) continue;
                  sp.push([fc.q[q].raw[r * H + hIdx], fc.q[q].w[r * H + hIdx] + 1e-6]);
                }
              }
              arr.push(wq(sp, 0.9) || 0);
            }
            out[QDEF[q].id] = arr;
          });
          return out;
        };
        var fp = day.forecast(now, { horizon: H, runs: runs, queues: qs, salt: 11 });
        var lp = lanesP90(fp), lr = null;
        for (var pass = 0; pass < 4; pass++) {
          var fr = day.forecast(now, { horizon: H, runs: runs, queues: qs, plan: recPlan, salt: 11 });
          lr = lanesP90(fr);
          var changed = false;
          qs.forEach(function (q) {
            var id = QDEF[q].id, maxK = QDEF[q].servers.length;
            for (var b3 = 0; b3 < nb; b3++) {
              if (lr[id][b3] > 13 && rec[id][b3] < maxK) {
                rec[id][b3]++; changed = true;
                if (b3 > 0 && rec[id][b3 - 1] < maxK && lr[id][b3] > 18) rec[id][b3 - 1]++;
              }
            }
          });
          if (!changed) break;
          if (pass === 3) { fr = day.forecast(now, { horizon: H, runs: runs, queues: qs, plan: recPlan, salt: 11 }); lr = lanesP90(fr); }
        }
        res.laneP90Plan = lp; res.laneP90Rec = lr; res.p90Plan = []; res.p90Rec = [];
        for (var b4 = 0; b4 < nb; b4++) {
          var wp = 0, wr = 0;
          qs.forEach(function (q) { var id = QDEF[q].id; wp = Math.max(wp, lp[id][b4]); wr = Math.max(wr, lr[id][b4]); });
          res.p90Plan.push(wp); res.p90Rec.push(wr);
        }
      }
      return res;
    };

    /* expected demand per queue per slot for the next hours (periodic day) */
    day.demand = function (now, hours, slot, qs) {
      var exp = day.expected(), out = { xs: [], q: {} };
      slot = slot || 30;
      qs.forEach(function (q) { out.q[q] = []; });
      var s0 = Math.floor(now / slot) * slot;
      for (var t = s0; t < now + hours * 60; t += slot) {
        out.xs.push(t);
        qs.forEach(function (q) {
          var s = 0;
          for (var k = 0; k < slot; k++) s += exp.A[q][mod(t + k, DAY) + PRE];
          out.q[q].push(s * 60 / slot);
        });
      }
      return out;
    };

    /* flights whose on-block estimate falls in [from, to) */
    day.flightsArriving = function (from, to) {
      return day.S.arr.filter(function (f) { return f.eibt >= from && f.eibt < to; }).sort(function (a, b) { return a.eibt - b.eibt; }).map(function (f) {
        var lanes = laneSplitArr(f.pax, f.mix);
        return { f: f, lanes: { CRW: lanes[0], CIT: lanes[1], RES: lanes[2], VIS: lanes[3], EG: lanes[4] }, hallFrom: f.eibt + 11 };
      });
    };

    /* predicted hall arrivals per lane per minute for [from, from+len) from the given flights */
    day.hallCurve = function (flights, from, len) {
      var ext = zeros(len + 6), off = from + PRE;
      flights.forEach(function (x) { addArr(ext, off, len + 6, x.f.seats, x.f.eibt, x.f.load, 11, x.f.mix); });
      var o = {};
      ['CRW', 'CIT', 'RES', 'VIS', 'EG'].forEach(function (ln, k) { o[ln] = Array.prototype.slice.call(ext[ARR_Q[k]], 0, len); });
      return o;
    };
  }

  /* alerts */

  var OWNER = { imm: 'Border shift supervisor', egate: 'Border shift supervisor', sec: 'Terminal duty manager', ciA: 'Terminal duty manager', ciB: 'Handler B station manager' };
  var ESCALATE = { 'Border shift supervisor': 'Border operations duty officer', 'Terminal duty manager': 'Airport operations centre lead', 'Handler B station manager': 'Terminal duty manager' };

  function permFor(def) {
    if (def.group === 'imm' || def.group === 'egate') return 'imm.alerts';
    if (def.group === 'sec') return 'sec';
    return def.handler === 'A' ? 'ci.A' : 'ci.B';
  }

  /* rules: every alert comes from a rule. R-001 to R-005 reproduce the seeded behaviour. */

  var IMM_IDS = ['A-CRW', 'A-CIT', 'A-RES', 'A-VIS', 'A-EG', 'D-CRW', 'D-CIT', 'D-RES', 'D-VIS', 'D-EG'];
  var AIR_IDS = ['CI-A', 'CI-B', 'CI-C', 'CI-D', 'SEC-N', 'SEC-S'];
  var OV_IDS = ['A-OV', 'D-OV', 'SEC-OV'];
  var DEFAULT_CAPS = { 'A-CRW': 10, 'A-CIT': 40, 'A-RES': 40, 'A-VIS': 190, 'A-EG': 70, 'D-CRW': 8, 'D-CIT': 30, 'D-RES': 30, 'D-VIS': 140, 'D-EG': 50,
    'SEC-N': 90, 'SEC-S': 90, 'CI-A': 70, 'CI-B': 70, 'CI-C': 70, 'CI-D': 70 };
  var METRICS = {
    nowcast: { label: 'Nowcast wait', unit: 'min', kind: 'nowcast' },
    p90bin: { label: 'Realised P90 per 15-minute bin', unit: 'min', kind: 'sla' },
    queue: { label: 'People queuing', unit: 'people', kind: 'queue' },
    overflow: { label: 'Overflow band occupied', unit: '', kind: 'overflow', bool: true },
    sensor: { label: 'Sensor offline', unit: '', kind: 'device', bool: true },
    desks: { label: 'Desks open below plan', unit: 'desks', kind: 'desks' }
  };
  var SEED_RULES = [
    { id: 'R-001', name: 'Nowcast above 15 min', scope: IMM_IDS.slice(), scopeLabel: 'Immigration lanes, arrivals and departures', metric: 'nowcast', op: 'gt', threshold: 15,
      minQueue: 10, clearBelow: 12, sustain: 1, clearAfter: 1, severity: 'critical', owner: 'Border shift supervisor', escalateAfter: 10, escalateTo: 'Border operations duty officer', email: '', seeded: true },
    { id: 'R-002', name: 'Overflow band occupied', scope: IMM_IDS.concat(AIR_IDS), scopeLabel: 'Every queue with an overflow band', metric: 'overflow', op: 'is', threshold: null,
      sustain: 3, clearAfter: 3, severity: 'warning', owner: 'zone', escalateAfter: 15, escalateTo: 'auto', email: '', seeded: true },
    { id: 'R-003', name: 'Sensor offline', scope: IMM_IDS.concat(AIR_IDS, OV_IDS), scopeLabel: 'All sensors', metric: 'sensor', op: 'is', threshold: null,
      sustain: 1, clearAfter: 1, severity: 'warning', owner: 'zone', escalateAfter: 15, escalateTo: 'systems', email: '', seeded: true },
    { id: 'R-004', name: 'Check-in P90 above SLA threshold', scope: ['CI-C', 'CI-D'], scopeLabel: 'Check-in, Handler B (contract C-001)', metric: 'p90bin', op: 'gt', threshold: 15,
      sustain: 1, clearAfter: 1, severity: 'critical', owner: 'Handler B station manager', escalateAfter: 15, escalateTo: 'Terminal duty manager', email: '', seeded: true },
    { id: 'R-005', name: 'Nowcast above 15 min, airport side', scope: AIR_IDS.slice(), scopeLabel: 'Check-in islands and security', metric: 'nowcast', op: 'gt', threshold: 15,
      minQueue: 10, clearBelow: 12, sustain: 1, clearAfter: 1, severity: 'critical', owner: 'zone', escalateAfter: 10, escalateTo: 'auto', email: '', seeded: true }
  ];

  function zoneDef(zone) {
    if (QI[zone] != null) return QDEF[QI[zone]];
    if (zone === 'A-OV') return QDEF[QI['A-VIS']];
    if (zone === 'D-OV') return QDEF[QI['D-VIS']];
    if (zone === 'SEC-OV') return QDEF[QI['SEC-N']];
    return null;
  }
  function zoneOwner(def) {
    if (def.group === 'imm' || def.group === 'egate') return 'Border shift supervisor';
    if (def.group === 'sec' || def.handler === 'A') return 'Terminal duty manager';
    return 'Handler B station manager';
  }
  function compareOp(v, op, t) {
    if (op === 'gt') return v > t;
    if (op === 'ge') return v >= t;
    if (op === 'lt') return v < t;
    if (op === 'le') return v <= t;
    return v > 0;
  }
  function ruleTargets(rule) {
    var scope = rule.scope || [];
    if (rule.metric === 'sensor') return SENSORS.filter(function (s) { return scope.indexOf(s.zone) >= 0; }).map(function (s) { return { sensor: s.id, zone: s.zone }; });
    return scope.filter(function (id) { return QI[id] != null; }).map(function (id) { return { q: QI[id], zone: id }; });
  }

  function attachRules(day) {
    var cache = {};
    function arr() { var a = new Float64Array(DAY); return a; }
    day.metric = function (metric, tg) {
      var key = metric + '|' + (tg.sensor || tg.zone);
      if (cache[key]) return cache[key];
      var v = arr(), q = tg.q, m;
      if (metric === 'nowcast') {
        for (m = 0; m < DAY; m++) { var st = day.state(q, m); v[m] = st.nowcast == null || day.degraded(q, m) ? NaN : st.nowcast; }
      } else if (metric === 'queue') {
        for (m = 0; m < DAY; m++) v[m] = day.L[q][m + PRE];
      } else if (metric === 'overflow') {
        var id = QDEF[q].id;
        for (m = 0; m < DAY; m++) { var cap = day.capsFn(id, m); v[m] = cap && day.L[q][m + PRE] > cap ? 1 : 0; }
      } else if (metric === 'desks') {
        for (m = 0; m < DAY; m++) { var i = m + PRE; v[m] = day.openCount(q, m, day.plan(q, m)) - (day.OPEN[q][i] - day.PAUSED[q][i]); }
      } else if (metric === 'sensor') {
        for (m = 0; m < DAY; m++) v[m] = day.sensorOffline(tg.sensor, m) ? 1 : 0;
      } else if (metric === 'p90bin') {
        var bins = new Int32Array(DAY);
        for (m = 0; m < DAY; m++) {
          var best = 0, bb = -1;
          for (var bs = Math.floor(Math.max(0, m - 150) / 15) * 15; bs <= m; bs += 15) {
            var b = day.bin(q, bs, m);
            if (b.status === 'provisional' && b.p90 != null && b.p90 > best) { best = b.p90; bb = bs; }
          }
          v[m] = best; bins[m] = bb;
        }
        v.bins = bins;
      }
      cache[key] = v;
      return v;
    };
    day.invalidateMetric = function (metric) { Object.keys(cache).forEach(function (k) { if (k.indexOf(metric + '|') === 0) delete cache[k]; }); };

    function mkAlert(rule, tg, m, v, series) {
      var def = tg.q != null ? QDEF[tg.q] : zoneDef(tg.zone);
      var owner = rule.owner === 'zone' ? zoneOwner(def) : rule.owner;
      var imm = def.group === 'imm' || def.group === 'egate';
      var esc = rule.escalateTo === 'auto' ? ESCALATE[owner] || 'Terminal duty manager' : rule.escalateTo === 'systems' ? (imm ? 'Border systems engineer' : 'Airport systems engineer') : rule.escalateTo;
      var perm = tg.sensor ? (imm ? 'imm.devices' : 'devices.air') : permFor(def);
      var text, M = METRICS[rule.metric];
      if (rule.metric === 'nowcast') text = def.name + ': nowcast ' + Math.round(v) + ' min';
      else if (rule.metric === 'p90bin') text = def.name + ': bin ' + clock(series.bins[m]) + ' in breach (provisional)';
      else if (rule.metric === 'queue') text = def.name + ': ' + Math.round(v) + ' people queuing';
      else if (rule.metric === 'overflow') text = def.name + ': queue beyond snake capacity, overflow band in use';
      else if (rule.metric === 'desks') text = def.name + ': ' + Math.round(v) + ' ' + (def.unit || 'desks') + ' below plan';
      else text = 'Sensor ' + tg.sensor + ' offline' + (OUTAGES.some(function (o) { return o.sensor === tg.sensor; }) ? '; zone degraded, wait shown as a band' : '');
      return { id: rule.id + ':' + (tg.sensor || def.id) + ':' + m, ruleId: rule.id, rule: rule.name, kind: M ? M.kind : rule.metric, metric: rule.metric,
        q: def.index, sensor: tg.sensor || null, zone: def.name, severity: rule.severity, owner: owner, escalateTo: esc, escalateAfter: rule.escalateAfter || 10,
        raisedAt: m, clearedAt: null, perm: perm, text: text, bin: series.bins ? series.bins[m] : null, email: rule.email || '' };
    }

    day.evalRule = function (rule) {
      var out = [];
      var sustain = Math.max(1, rule.sustain || 1), clearAfter = Math.max(1, rule.clearAfter || 1);
      ruleTargets(rule).forEach(function (tg) {
        var val = day.metric(rule.metric, tg);
        var len = rule.minQueue ? day.metric('queue', tg) : null;
        var armed = true, cnt = 0, fcnt = 0, cur = null;
        for (var m = 0; m < DAY; m++) {
          var v = val[m];
          if (v !== v) continue;
          var cond = compareOp(v, rule.op, rule.threshold) && (!len || len[m] >= rule.minQueue);
          if (armed) {
            if (cond) { cnt++; if (cnt >= sustain) { cur = mkAlert(rule, tg, m, v, val); out.push(cur); armed = false; fcnt = 0; } }
            else cnt = 0;
          } else {
            var clr = rule.clearBelow != null ? v < rule.clearBelow : !cond;
            if (clr) { fcnt++; if (fcnt >= clearAfter) { cur.clearedAt = m; armed = true; cnt = 0; } } else fcnt = 0;
          }
        }
      });
      out.sort(function (a, b) { return a.raisedAt - b.raisedAt; });
      return out;
    };

    day.backtest = function (rule) {
      var al = day.evalRule(rule);
      return { count: al.length, first: al.length ? al[0].raisedAt : null, alerts: al };
    };

    day.setRules = function (rules) {
      day.rules = rules;
      var all = [];
      rules.forEach(function (r) { if (r.enabled !== false) all = all.concat(day.evalRule(r)); });
      all.sort(function (a, b) { return a.raisedAt - b.raisedAt || (a.id < b.id ? -1 : 1); });
      day.alertsAll = all;
      return all;
    };
    day.setCaps = function (fn) { day.capsFn = fn; day.invalidateMetric('overflow'); };
  }

  /* helpers */

  function clock(m) {
    m = mod(Math.round(m), DAY);
    return pad2(Math.floor(m / 60)) + ':' + pad2(m % 60);
  }

  function waitBand(v) {
    if (v == null) return null;
    if (v < 5) return 0;
    if (v < 10) return 1;
    if (v < 15) return 2;
    if (v < 20) return 3;
    if (v < 30) return 4;
    return 5;
  }

  global.QSim = {
    DAY: DAY, PRE: PRE, POST: POST, N: N,
    DEFAULT_SEED: DEFAULT_SEED, DATE: DATE, PARAMS: PARAMS,
    QUEUES: QDEF, QI: QI, SERVERS: SERVERS, AREAS: AREAS, LANES: LANES, LANE_LABEL: LANE_LABEL,
    SENSORS: SENSORS, OUTAGES: OUTAGES, OUTAGE_HISTORY: OUTAGE_HISTORY, CARRIERS: CARRIERS,
    SEED_RULES: SEED_RULES, METRICS: METRICS, IMM_IDS: IMM_IDS, AIR_IDS: AIR_IDS, OV_IDS: OV_IDS, DEFAULT_CAPS: DEFAULT_CAPS,
    SEED_ALLOCATIONS: SEED_ALLOCATIONS, strHash: strHash,
    SCRIPT: SCRIPT, EG_SHARE_DEP: EG_SHARE_DEP,
    run: run, clock: clock, waitBand: waitBand, h3: h3, mulberry32: mulberry32, mix32: mix32
  };
})(typeof window !== 'undefined' ? window : this);
