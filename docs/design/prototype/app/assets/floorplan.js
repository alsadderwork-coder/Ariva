/*
  floorplan.js: invented SVG floor plan of Demo International Airport (DMO).
  Not a real airport. Two levels (Departures, Arrivals), snake queues as polygons with
  entry and exit lines, desk rows, e-gates, security lanes, overflow bands and sensors.
  The drawing is data-driven: the screen passes a context that says what each zone,
  desk and sensor shows for the current role. Exposes QFloor.
*/
(function (global) {
  'use strict';

  var NS = 'http://www.w3.org/2000/svg';
  var uid = 0;
  var W = 1000, H = 600;

  function el(tag, attrs, parent) {
    var n = document.createElementNS(NS, tag);
    if (attrs) for (var k in attrs) {
      if (attrs[k] == null) continue;
      if (k === 'text') n.textContent = attrs[k];
      else if (k === 'cls') n.setAttribute('class', attrs[k]);
      else n.setAttribute(k, attrs[k]);
    }
    if (parent) parent.appendChild(n);
    return n;
  }
  function rect(x, y, w, h) { return [[x, y], [x + w, y], [x + w, y + h], [x, y + h]]; }
  function bbox(poly) {
    var x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
    poly.forEach(function (p) { x0 = Math.min(x0, p[0]); y0 = Math.min(y0, p[1]); x1 = Math.max(x1, p[0]); y1 = Math.max(y1, p[1]); });
    return { x0: x0, y0: y0, x1: x1, y1: y1, w: x1 - x0, h: y1 - y0, cx: (x0 + x1) / 2, cy: (y0 + y1) / 2 };
  }
  function area(poly) {
    var a = 0;
    for (var i = 0; i < poly.length; i++) { var p = poly[i], q = poly[(i + 1) % poly.length]; a += p[0] * q[1] - q[0] * p[1]; }
    return Math.abs(a / 2);
  }
  function pts(poly) { return poly.map(function (p) { return p[0].toFixed(1) + ',' + p[1].toFixed(1); }).join(' '); }
  function clone(o) { return JSON.parse(JSON.stringify(o)); }

  /* base geometry (zone profile v12) */

  var LANE_Y = { CRW: [45, 66], CIT: [69, 122], RES: [125, 179], VIS: [182, 466], EG: [478, 590] };
  var LANE_SHORT = { CRW: 'Crew', CIT: 'Citizens', RES: 'Residents', VIS: 'Visitors', EG: 'E-gates' };
  function deskY(k) { return 48 + (k - 1) * 19; }

  function laneZone(x0, x1, lane) {
    var y = LANE_Y[lane];
    return { poly: rect(x0, y[0], x1 - x0, y[1] - y[0]), entry: [[x0, y[0] + 3], [x0, Math.min(y[0] + 19, y[1] - 3)]], exit: [[x1, y[0] + 3], [x1, y[1] - 3]] };
  }

  var ISL_X = { A: 56, B: 168, C: 280, D: 392 };

  function baseGeometry() {
    var z = {};
    ['CRW', 'CIT', 'RES', 'VIS', 'EG'].forEach(function (ln) {
      z['A-' + ln] = laneZone(190, 480, ln);
      z['D-' + ln] = laneZone(746, 894, ln);
    });
    ['A', 'B', 'C', 'D'].forEach(function (i) {
      var x = ISL_X[i];
      z['CI-' + i] = { poly: rect(x, 56, 80, 370), entry: [[x + 6, 426], [x + 36, 426]], exit: [[x + 80, 60], [x + 80, 422]] };
    });
    z['SEC-N'] = { poly: rect(532, 50, 80, 200), entry: [[532, 222], [532, 246]], exit: [[612, 54], [612, 246]] };
    z['SEC-S'] = { poly: rect(532, 330, 80, 200), entry: [[532, 334], [532, 358]], exit: [[612, 334], [612, 526]] };
    return { zones: z };
  }

  var BASE_CAP = { 'A-CRW': 10, 'A-CIT': 40, 'A-RES': 40, 'A-VIS': 190, 'A-EG': 70, 'D-CRW': 8, 'D-CIT': 30, 'D-RES': 30, 'D-VIS': 140, 'D-EG': 50,
    'SEC-N': 90, 'SEC-S': 90, 'CI-A': 70, 'CI-B': 70, 'CI-C': 70, 'CI-D': 70 };
  var V12 = baseGeometry();
  var V12_AREA = {};
  Object.keys(V12.zones).forEach(function (k) { V12_AREA[k] = area(V12.zones[k].poly); });

  function draftV13() {
    var g = clone(V12);
    var v = g.zones['A-VIS'];
    v.poly[0][0] = 166; v.poly[3][0] = 166; v.entry = [[166, 185], [166, 205]];
    var c = g.zones['CI-C'];
    c.poly[2][1] = 440; c.poly[3][1] = 440; c.entry = [[286, 440], [316, 440]];
    return g;
  }

  function snakeCapacity(zoneId, geom) {
    var z = geom.zones[zoneId];
    if (!z) return null;
    if (z.meta) return z.meta.type === 'snake' && z.poly.length >= 3 ? Math.round(area(z.poly) / 400) : null;
    if (!BASE_CAP[zoneId]) return null;
    return Math.round(BASE_CAP[zoneId] * area(z.poly) / V12_AREA[zoneId]);
  }

  var REGIONS = {
    arr: [
      { id: 'imm', x: 14, y: 26, w: 542, h: 566, label: 'Arrival immigration' },
      { id: 'reclaim', x: 590, y: 26, w: 398, h: 566, label: 'Baggage reclaim' }
    ],
    dep: [
      { id: 'ciA', x: 48, y: 26, w: 232, h: 566, label: 'Check-in, Handler A' },
      { id: 'ciB', x: 280, y: 26, w: 232, h: 566, label: 'Check-in, Handler B' },
      { id: 'sec', x: 522, y: 26, w: 182, h: 566, label: 'Security' },
      { id: 'emi', x: 710, y: 26, w: 280, h: 566, label: 'Departure immigration' }
    ]
  };
  var ZONE_REGION = {};
  ['CRW', 'CIT', 'RES', 'VIS', 'EG'].forEach(function (ln) { ZONE_REGION['A-' + ln] = 'imm'; ZONE_REGION['D-' + ln] = 'emi'; });
  ZONE_REGION['CI-A'] = 'ciA'; ZONE_REGION['CI-B'] = 'ciA'; ZONE_REGION['CI-C'] = 'ciB'; ZONE_REGION['CI-D'] = 'ciB';
  ZONE_REGION['SEC-N'] = 'sec'; ZONE_REGION['SEC-S'] = 'sec';
  ZONE_REGION['A-OV'] = 'imm'; ZONE_REGION['D-OV'] = 'emi'; ZONE_REGION['SEC-OV'] = 'sec';
  function zoneLevel(id) { return /^A-/.test(id) ? 'arr' : 'dep'; }
  function zLevel(id, z) { return z && z.meta ? z.meta.level : zoneLevel(id); }
  function regionAt(level, x, y) {
    var best = null, bd = Infinity;
    (REGIONS[level] || []).forEach(function (r) {
      var dx = Math.max(r.x - x, 0, x - (r.x + r.w)), dy = Math.max(r.y - y, 0, y - (r.y + r.h)), d = dx + dy;
      if (d < bd) { bd = d; best = r.id; }
    });
    return best;
  }
  function regionOf(id, z) {
    if (ZONE_REGION[id]) return ZONE_REGION[id];
    if (z && z.meta) { var b = bbox(z.poly); return regionAt(z.meta.level, b.cx, b.cy); }
    return null;
  }

  var OVERFLOW = {
    'A-OV': { level: 'arr', poly: rect(116, 182, 62, 284), feeds: ['A-VIS', 'A-RES', 'A-CIT', 'A-CRW'] },
    'D-OV': { level: 'dep', poly: rect(718, 182, 20, 284), feeds: ['D-VIS', 'D-RES', 'D-CIT', 'D-CRW'] },
    'SEC-OV': { level: 'dep', poly: rect(532, 262, 80, 56), feeds: ['SEC-N', 'SEC-S'] },
    'CI-A-OV': { level: 'dep', poly: rect(56, 436, 80, 56), feeds: ['CI-A'] },
    'CI-B-OV': { level: 'dep', poly: rect(168, 436, 80, 56), feeds: ['CI-B'] },
    'CI-C-OV': { level: 'dep', poly: rect(280, 436, 80, 56), feeds: ['CI-C'] },
    'CI-D-OV': { level: 'dep', poly: rect(392, 436, 80, 56), feeds: ['CI-D'] }
  };
  ZONE_REGION['CI-A-OV'] = 'ciA'; ZONE_REGION['CI-B-OV'] = 'ciA'; ZONE_REGION['CI-C-OV'] = 'ciB'; ZONE_REGION['CI-D-OV'] = 'ciB';

  var HALLS = {
    arr: { id: 'A-HALL', region: 'imm', poly: rect(116, 45, 420, 545), queues: ['A-CRW', 'A-CIT', 'A-RES', 'A-VIS', 'A-EG'], name: 'Passport control, arrivals' },
    dep: { id: 'D-HALL', region: 'emi', poly: rect(718, 45, 260, 545), queues: ['D-CRW', 'D-CIT', 'D-RES', 'D-VIS', 'D-EG'], name: 'Passport control, departures' }
  };

  /* servers: desks, gates, counters, security lanes */
  function serverGeometry() {
    var s = {};
    for (var k = 1; k <= 22; k++) {
      var y = deskY(k), n = (k < 10 ? '0' : '') + k;
      s['AR-' + n] = { level: 'arr', x: 506, y: y, w: 30, h: 16, label: n, lx: 542 };
      s['DP-' + n] = { level: 'dep', x: 912, y: y, w: 30, h: 16, label: n, lx: 948 };
    }
    for (k = 1; k <= 6; k++) s['AG-' + k] = { level: 'arr', x: 506, y: 484 + (k - 1) * 17, w: 30, h: 13, label: 'G' + k, lx: 542 };
    for (k = 1; k <= 4; k++) s['DG-' + k] = { level: 'dep', x: 912, y: 486 + (k - 1) * 22, w: 30, h: 16, label: 'G' + k, lx: 948 };
    ['A', 'B', 'C', 'D'].forEach(function (i) {
      var bx = ISL_X[i] + 86;
      for (var j = 1; j <= 12; j++) s[i + (j < 10 ? '0' : '') + j] = { level: 'dep', x: bx + 3, y: 58 + (j - 1) * 30.6, w: 16, h: 26, island: i };
    });
    for (k = 1; k <= 5; k++) {
      s['N' + k] = { level: 'dep', x: 624, y: 58 + (k - 1) * 38, w: 72, h: 24, label: 'N' + k, inner: true };
      s['S' + k] = { level: 'dep', x: 624, y: 338 + (k - 1) * 38, w: 72, h: 24, label: 'S' + k, inner: true };
    }
    return s;
  }
  var SRV = serverGeometry();
  function serverRegion(id) {
    if (/^AR-|^AG-/.test(id)) return 'imm';
    if (/^DP-|^DG-/.test(id)) return 'emi';
    if (/^[NS]\d$/.test(id)) return 'sec';
    return id[0] === 'A' || id[0] === 'B' ? 'ciA' : 'ciB';
  }

  /* sensor positions spread over their zones */
  function sensorPositions(geom) {
    var out = {}, byZone = {};
    (global.QSim ? global.QSim.SENSORS : []).forEach(function (s) { (byZone[s.zone] = byZone[s.zone] || []).push(s); });
    Object.keys(byZone).forEach(function (zid) {
      var list = byZone[zid], poly;
      if (geom.zones[zid]) poly = geom.zones[zid].poly;
      else if (zid === 'A-OV') poly = OVERFLOW['A-OV'].poly;
      else if (zid === 'D-OV') poly = OVERFLOW['D-OV'].poly;
      else if (zid === 'SEC-OV') poly = OVERFLOW['SEC-OV'].poly;
      if (!poly) return;
      var b = bbox(poly), n = list.length;
      var cols = Math.max(1, Math.round(Math.sqrt(n * b.w / Math.max(b.h, 1))));
      cols = Math.min(cols, n);
      var rows = Math.ceil(n / cols);
      list.forEach(function (s, k) {
        var c = k % cols, r = Math.floor(k / cols);
        out[s.id] = { x: b.x0 + b.w * (c + 0.5) / cols, y: b.y0 + b.h * (r + 0.5) / rows, r: Math.min(b.w / cols, b.h / rows) * 0.62, zone: zid, level: zoneLevel(zid === 'SEC-OV' ? 'SEC-N' : zid) };
      });
    });
    return out;
  }

  /* the component */

  function FloorPlan(host, opt) {
    this.host = typeof host === 'string' ? document.querySelector(host) : host;
    this.opt = opt || {};
    this.mode = this.opt.mode || 'live';
    this.level = this.opt.level || 'arr';
    this.fid = 'fp' + (++uid);
    this.nodes = {};
    this.selected = this.opt.selected || null;
    this.buildShell();
  }

  FloorPlan.prototype.buildShell = function () {
    var self = this, h = this.host;
    h.innerHTML = '';
    h.classList.add('fp');
    var head = document.createElement('div');
    head.className = 'fp-head';
    var tabs = document.createElement('div');
    tabs.className = 'tabs';
    tabs.setAttribute('role', 'tablist');
    tabs.setAttribute('aria-label', 'Floor plan level');
    this.tabBtns = {};
    [['dep', 'Departures level'], ['arr', 'Arrivals level']].forEach(function (lv) {
      var b = document.createElement('button');
      b.type = 'button';
      b.setAttribute('role', 'tab');
      b.id = self.fid + '-tab-' + lv[0];
      b.setAttribute('aria-controls', self.fid + '-panel');
      b.textContent = lv[1];
      b.addEventListener('click', function () { self.setLevel(lv[0], true); });
      b.addEventListener('keydown', function (e) {
        if (e.key === 'ArrowRight' || e.key === 'ArrowLeft') {
          e.preventDefault();
          var other = lv[0] === 'dep' ? 'arr' : 'dep';
          self.setLevel(other, true);
          self.tabBtns[other].focus();
        }
      });
      tabs.appendChild(b);
      self.tabBtns[lv[0]] = b;
    });
    head.appendChild(tabs);
    this.headExtra = document.createElement('div');
    this.headExtra.className = 'row small muted';
    head.appendChild(this.headExtra);
    h.appendChild(head);
    var wrap = document.createElement('div');
    wrap.className = 'fp-wrap';
    var scroll = document.createElement('div');
    scroll.className = 'fp-scroll';
    scroll.id = this.fid + '-panel';
    scroll.setAttribute('role', 'tabpanel');
    scroll.tabIndex = -1;
    wrap.appendChild(scroll);
    this.tip = document.createElement('div');
    this.tip.className = 'tip';
    this.tip.setAttribute('role', 'tooltip');
    this.tip.id = this.fid + '-tip';
    wrap.appendChild(this.tip);
    h.appendChild(wrap);
    this.scroll = scroll;
    this.wrap = wrap;
    this.legend = document.createElement('div');
    this.legend.className = 'fp-legend';
    h.appendChild(this.legend);
    this.summary = document.createElement('div');
    h.appendChild(this.summary);
  };

  FloorPlan.prototype.setLevel = function (lv, user) {
    this.level = lv;
    if (user && this.opt.onLevel) this.opt.onLevel(lv);
    if (this.ctx) { this.render(this.ctx); }
  };

  FloorPlan.prototype.render = function (ctx) {
    var self = this;
    this.ctx = ctx;
    var lv = this.level;
    for (var k in this.tabBtns) {
      this.tabBtns[k].setAttribute('aria-selected', k === lv ? 'true' : 'false');
      this.tabBtns[k].tabIndex = k === lv ? 0 : -1;
    }
    this.scroll.setAttribute('aria-labelledby', this.fid + '-tab-' + lv);
    this.scroll.innerHTML = '';
    this.nodes = { zones: {}, servers: {}, sensors: {}, ovf: {}, hall: null, handles: [] };
    var svg = el('svg', { viewBox: '0 0 ' + W + ' ' + H, role: 'group', 'aria-label': ctx.ariaLabel || ('Floor plan, ' + (lv === 'arr' ? 'arrivals' : 'departures') + ' level. Invented layout, synthetic data.') });
    this.svg = svg;
    el('title', { text: 'Floor plan of Demo International Airport, ' + (lv === 'arr' ? 'arrivals' : 'departures') + ' level (invented layout)' }, svg);
    var defs = el('defs', null, svg);
    var pat = el('pattern', { id: this.fid + '-hatch', patternUnits: 'userSpaceOnUse', width: 8, height: 8, patternTransform: 'rotate(45)' }, defs);
    el('rect', { width: 8, height: 8, fill: 'var(--panel-2)' }, pat);
    el('line', { x1: 0, y1: 0, x2: 0, y2: 8, style: 'stroke:var(--unknown);stroke-width:3' }, pat);
    var geom = ctx.geometry;

    var gStatic = el('g', null, svg);
    this.drawStatic(gStatic, lv);

    var gOv = el('g', null, svg);
    Object.keys(OVERFLOW).forEach(function (id) {
      var o = OVERFLOW[id];
      if (o.level !== lv) return;
      var p = el('polygon', { points: pts(o.poly), cls: 'ovf' }, gOv);
      var b = bbox(o.poly);
      var t = el('text', { x: b.cx, y: b.y1 - 6, 'text-anchor': 'middle', cls: 'lbl-small', text: b.w > 50 ? 'Overflow' : '' }, gOv);
      if (b.w <= 50) el('text', { x: b.cx, y: b.cy, 'text-anchor': 'middle', cls: 'lbl-small', transform: 'rotate(-90 ' + b.cx + ' ' + b.cy + ')', text: 'Overflow' }, gOv);
      self.nodes.ovf[id] = { poly: p, text: t, def: o };
    });

    var gZ = el('g', null, svg);
    this.gZones = gZ;
    var zoneIds = Object.keys(geom.zones).filter(function (id) { return zLevel(id, geom.zones[id]) === lv; });
    var hall = HALLS[lv];
    var hallVis = ctx.regionVis(hall.region);
    if (this.mode === 'live' && hallVis === 'agg') {
      var hp = el('polygon', { points: pts(hall.poly), cls: 'zone z-neutral', tabindex: 0, 'data-zone': hall.id }, gZ);
      var hb = bbox(hall.poly);
      var t1 = el('text', { x: hb.cx, y: hb.cy - 22, 'text-anchor': 'middle', cls: 'lbl-zone', text: hall.name }, gZ);
      var t2 = el('text', { x: hb.cx, y: hb.cy + 2, 'text-anchor': 'middle', cls: 'lbl-wait', text: '' }, gZ);
      var t3 = el('text', { x: hb.cx, y: hb.cy + 22, 'text-anchor': 'middle', cls: 'lbl-small', text: '' }, gZ);
      this.nodes.hall = { poly: hp, t1: t1, t2: t2, t3: t3, def: hall };
      this.bindTip(hp, function () { return ctx.hallTip ? ctx.hallTip(lv) : null; });
      zoneIds = zoneIds.filter(function (id) { return regionOf(id, geom.zones[id]) !== hall.region; });
    }
    zoneIds.forEach(function (id) { self.drawZone(gZ, id, geom.zones[id]); });

    var gS = el('g', null, svg);
    if (this.mode !== 'editor') {
      Object.keys(SRV).forEach(function (sid) {
        var s = SRV[sid];
        if (s.level !== lv) return;
        var rv = ctx.regionVis(serverRegion(sid));
        if (rv !== 'full') return;
        self.drawServer(gS, sid, s);
      });
    }

    if (this.mode === 'devices') {
      var gSn = el('g', null, svg);
      var pos = sensorPositions(geom);
      Object.keys(pos).forEach(function (sid) {
        var p = pos[sid];
        if (p.level !== lv) return;
        if (ctx.regionVis(regionOf(p.zone, geom.zones[p.zone])) !== 'full') return;
        el('circle', { cx: p.x, cy: p.y, r: Math.max(12, Math.min(p.r, 46)), cls: 'sn-cov' }, gSn);
        var c = el('circle', { cx: p.x, cy: p.y, r: 7, cls: 'sensor sn-on', tabindex: 0 }, gSn);
        var t = el('text', { x: p.x, y: p.y + 19, 'text-anchor': 'middle', cls: 'lbl-small', text: sid, style: 'font-family:var(--mono);fill:var(--ink)' }, gSn);
        self.nodes.sensors[sid] = { c: c, t: t };
        self.bindTip(c, function () { return ctx.sensorTip ? ctx.sensorTip(sid) : null; });
      });
      (ctx.extraSensors || []).forEach(function (x) {
        if (x.level !== lv || ctx.regionVis(regionAt(lv, x.x, x.y)) !== 'full') return;
        drawFootprint(gSn, x);
        var c = el('circle', { cx: x.x, cy: x.y, r: 7, cls: 'sensor ' + (x.cls || 'sn-new'), tabindex: 0 }, gSn);
        el('text', { x: x.x, y: x.y + 19, 'text-anchor': 'middle', cls: 'lbl-small', text: x.id, style: 'font-family:var(--mono);fill:var(--ink)' }, gSn);
        self.nodes.sensors[x.id] = { c: c, extra: true };
        self.bindTip(c, function () { return ctx.sensorTip ? ctx.sensorTip(x.id) : null; });
      });
    }

    var gM = el('g', null, svg);
    REGIONS[lv].forEach(function (r) {
      var v = ctx.regionVis(r.id);
      if (v === 'hidden' || (v === 'agg' && self.mode !== 'live')) {
        el('rect', { x: r.x, y: r.y, width: r.w, height: r.h, rx: 4, cls: 'mask' }, gM);
        el('text', { x: r.x + r.w / 2, y: r.y + r.h / 2 - 4, 'text-anchor': 'middle', cls: 'mask-text', text: 'Outside your view' }, gM);
        el('text', { x: r.x + r.w / 2, y: r.y + r.h / 2 + 14, 'text-anchor': 'middle', cls: 'lbl-small', text: r.label }, gM);
      }
    });

    if (this.mode === 'editor') { this.gHandles = el('g', null, svg); this.drawHandles(); }

    this.scroll.appendChild(svg);
    this.drawLegend();
    this.update(ctx);
  };

  /* coverage footprint of a registered sensor, in plan units (10 units = 1 m) */
  function drawFootprint(g, x) {
    if (!x.fp) return;
    if (x.fp.r) el('circle', { cx: x.x, cy: x.y, r: x.fp.r * 10, cls: 'sn-fp' }, g);
    else el('rect', { x: x.x - x.fp.w * 5, y: x.y - x.fp.h * 5, width: x.fp.w * 10, height: x.fp.h * 10, cls: 'sn-fp' }, g);
  }

  FloorPlan.prototype.drawStatic = function (g, lv) {
    function label(x, y, text) { el('text', { x: x, y: y, cls: 'lbl-region', text: text }, g); }
    if (lv === 'arr') {
      el('rect', { x: 14, y: 26, width: 84, height: 566, rx: 4, cls: 'area' }, g);
      el('text', { x: 56, y: 309, 'text-anchor': 'middle', transform: 'rotate(-90 56 309)', cls: 'lbl-small', text: 'Pier from gates, passengers walk 8 to 15 min' }, g);
      label(14, 18, 'Pier');
      el('rect', { x: 106, y: 26, width: 450, height: 566, rx: 4, cls: 'region' }, g);
      label(106, 18, 'Arrival immigration');
      el('rect', { x: 590, y: 26, width: 398, height: 566, rx: 4, cls: 'area' }, g);
      label(590, 18, 'Baggage reclaim (zone only)');
      for (var i = 0; i < 4; i++) {
        el('rect', { x: 650, y: 70 + i * 128, width: 290, height: 64, rx: 32, cls: 'belt' }, g);
        el('text', { x: 795, y: 106 + i * 128, 'text-anchor': 'middle', cls: 'lbl-small', text: 'Belt ' + (i + 1) }, g);
      }
      el('path', { d: 'M562 309 h20 m-6 -5 l6 5 l-6 5', style: 'fill:none;stroke:var(--muted);stroke-width:1.5' }, g);
    } else {
      el('rect', { x: 8, y: 26, width: 34, height: 566, rx: 4, cls: 'area' }, g);
      el('text', { x: 25, y: 309, 'text-anchor': 'middle', transform: 'rotate(-90 25 309)', cls: 'lbl-small', text: 'Entrance from kerb' }, g);
      el('rect', { x: 48, y: 26, width: 464, height: 566, rx: 4, cls: 'region' }, g);
      label(48, 18, 'Check-in hall');
      el('text', { x: 166, y: 47, 'text-anchor': 'middle', cls: 'lbl-small', text: 'Handler A, islands A and B', style: 'fill:var(--ink)' }, g);
      el('text', { x: 390, y: 47, 'text-anchor': 'middle', cls: 'lbl-small', text: 'Handler B, islands C and D', style: 'fill:var(--ink)' }, g);
      ['A', 'B', 'C', 'D'].forEach(function (k) {
        var bx = ISL_X[k] + 86;
        el('rect', { x: bx, y: 56, width: 22, height: 370, rx: 3, cls: 'area' }, g);
        el('text', { x: bx + 11, y: 520, 'text-anchor': 'middle', cls: 'lbl-zone', text: k }, g);
      });
      el('text', { x: 280, y: 540, 'text-anchor': 'middle', cls: 'lbl-small', text: 'Islands of 12 counters; queue snakes in front of each island' }, g);
      el('rect', { x: 522, y: 26, width: 182, height: 566, rx: 4, cls: 'region' }, g);
      label(522, 18, 'Security');
      el('text', { x: 572, y: 46, 'text-anchor': 'middle', cls: 'lbl-small', text: 'North', style: 'fill:var(--ink)' }, g);
      el('text', { x: 572, y: 546, 'text-anchor': 'middle', cls: 'lbl-small', text: 'South', style: 'fill:var(--ink)' }, g);
      el('rect', { x: 710, y: 26, width: 280, height: 566, rx: 4, cls: 'region' }, g);
      label(710, 18, 'Departure immigration');
      el('text', { x: 972, y: 309, 'text-anchor': 'middle', transform: 'rotate(90 972 309)', cls: 'lbl-small', text: 'Airside, to gates' }, g);
    }
  };

  FloorPlan.prototype.drawZone = function (g, id, z) {
    var self = this, ctx = this.ctx;
    var vis = ctx.regionVis(regionOf(id, z));
    var clipId = this.fid + '-clip-' + id;
    var cp = el('clipPath', { id: clipId }, g);
    var cpp = el('polygon', { points: pts(z.poly) }, cp);
    var grp = el('g', null, g);
    var isLine = z.meta && z.meta.type === 'line';
    var poly = isLine ? el('polyline', { points: pts(z.poly), cls: 'zone z-neutral zline', 'data-zone': id }, grp) : el('polygon', { points: pts(z.poly), cls: 'zone z-neutral' + (z.meta ? ' zcustom' : ''), 'data-zone': id }, grp);
    var snake = el('path', { d: !z.meta || z.meta.type === 'snake' ? this.snakePath(z.poly) : 'M0,0', cls: 'snake', 'clip-path': 'url(#' + clipId + ')' }, grp);
    var entry = z.entry ? el('line', { x1: z.entry[0][0], y1: z.entry[0][1], x2: z.entry[1][0], y2: z.entry[1][1], cls: 'entry' }, grp) : null;
    var exit = z.exit ? el('line', { x1: z.exit[0][0], y1: z.exit[0][1], x2: z.exit[1][0], y2: z.exit[1][1], cls: 'exit' }, grp) : null;
    var b = bbox(z.poly);
    var small = b.h < 34 || isLine;
    var t1 = el('text', { x: b.cx, y: isLine ? b.cy - 6 : small ? b.cy + 4 : b.cy - 6, 'text-anchor': 'middle', cls: 'lbl-zone' }, grp);
    var t2 = el('text', { x: b.cx, y: b.cy + 12, 'text-anchor': 'middle', cls: 'lbl-wait' }, grp);
    var t3 = el('text', { x: b.cx, y: b.cy + 27, 'text-anchor': 'middle', cls: 'lbl-small zlbl' }, grp);
    if (vis === 'full' && this.mode === 'live') {
      poly.setAttribute('tabindex', '0');
      this.bindTip(poly, function () { return ctx.zoneTip ? ctx.zoneTip(id) : null; });
    }
    if (this.mode === 'editor') {
      poly.setAttribute('tabindex', '0');
      poly.setAttribute('role', 'button');
      poly.setAttribute('aria-label', 'Select zone ' + id + ' for editing');
      poly.style.cursor = 'pointer';
      poly.addEventListener('click', function () { if (ctx.editable && ctx.editable(id)) self.select(id); });
      if (z.meta) poly.setAttribute('aria-label', 'Select zone ' + z.meta.name + ' for editing');
      poly.addEventListener('keydown', function (e) { if ((e.key === 'Enter' || e.key === ' ') && ctx.editable && ctx.editable(id)) { e.preventDefault(); self.select(id); } });
    }
    this.nodes.zones[id] = { poly: poly, snake: snake, entry: entry, exit: exit, t1: t1, t2: t2, t3: t3, cp: cpp, small: small, vis: vis };
  };

  FloorPlan.prototype.snakePath = function (poly) {
    var b = bbox(poly), d = '', r = 0;
    var step = b.h < 30 ? 8 : 11;
    for (var y = b.y0 + 7; y <= b.y1 - 4; y += step, r++) {
      var x0 = b.x0 + (r % 2 ? 16 : 4), x1 = b.x1 - (r % 2 ? 4 : 16);
      if (x1 > x0) d += 'M' + x0.toFixed(1) + ',' + y.toFixed(1) + 'H' + x1.toFixed(1);
    }
    return d || 'M0,0';
  };

  FloorPlan.prototype.drawServer = function (g, sid, s) {
    var ctx = this.ctx;
    var grp = el('g', null, g);
    var r = el('rect', { x: s.x, y: s.y, width: s.w, height: s.h, rx: 2, cls: 'srv s-closed', tabindex: 0, 'data-srv': sid }, grp);
    var gl = el('g', { cls: 'glyph' }, grp);
    if (s.label && !s.inner) el('text', { x: s.lx, y: s.y + s.h - 4, cls: 'lbl-small', text: s.label }, grp);
    if (s.inner) el('text', { x: s.x + 5, y: s.y + s.h - 7, cls: 'lbl-small', text: s.label, style: 'fill:var(--ink)' }, grp);
    this.nodes.servers[sid] = { r: r, g: gl, s: s, state: null };
    this.bindTip(r, function () { return ctx.serverTip ? ctx.serverTip(sid) : null; });
  };

  function drawGlyph(g, s, state) {
    while (g.firstChild) g.removeChild(g.firstChild);
    var cx = s.inner ? s.x + s.w - 12 : s.x + s.w / 2, cy = s.y + s.h / 2;
    if (state === 'serving') el('rect', { x: cx - 3.5, y: cy - 3.5, width: 7, height: 7, rx: 1, cls: 'g-serving' }, g);
    else if (state === 'idle') el('circle', { cx: cx, cy: cy, r: 2.6, cls: 'g-idle' }, g);
    else if (state === 'paused') { el('rect', { x: cx - 4, y: cy - 4, width: 2.6, height: 8, cls: 'g-paused' }, g); el('rect', { x: cx + 1.4, y: cy - 4, width: 2.6, height: 8, cls: 'g-paused' }, g); }
    else if (state === 'unknown') el('text', { x: cx, y: cy + 3.5, 'text-anchor': 'middle', cls: 'g-unknown', text: '?' }, g);
    else if (state === 'oos') el('path', { d: 'M' + (cx - 4) + ',' + (cy - 4) + 'l8,8m0,-8l-8,8', cls: 'g-oos' }, g);
  }

  FloorPlan.prototype.update = function (ctx) {
    var self = this;
    this.ctx = ctx;
    Object.keys(this.nodes.zones).forEach(function (id) {
      var n = self.nodes.zones[id];
      var info = n.vis === 'full' && ctx.zoneInfo ? ctx.zoneInfo(id) : null;
      var cls = 'zone ' + (self.mode === 'editor' ? (ctx.editable && ctx.editable(id) ? (self.selected === id ? 'z-edit' : 'z-neutral') : 'z-lock') : info ? info.cls : 'z-off');
      if (info && info.hatch) { n.poly.setAttribute('class', 'zone z-ns'); n.poly.style.fill = 'url(#' + self.fid + '-hatch)'; n.poly.style.fillOpacity = '1'; }
      else { n.poly.setAttribute('class', cls); n.poly.style.fill = ''; n.poly.style.fillOpacity = ''; }
      var name = info ? info.name : ctx.zoneLabel ? ctx.zoneLabel(id) : (self.mode === 'editor' ? id : '');
      if (n.small) {
        if (self.mode === 'devices') name = '';
        n.t1.textContent = info ? name + '  ' + info.wait : name;
        n.t2.textContent = ''; n.t3.textContent = '';
      } else {
        n.t1.textContent = name;
        n.t2.textContent = info ? info.wait : '';
        n.t3.textContent = info && info.sub ? info.sub : '';
      }
      if (info && info.aria) n.poly.setAttribute('aria-label', info.aria);
    });
    if (this.nodes.hall && ctx.hallInfo) {
      var hi = ctx.hallInfo(this.level);
      var hn = this.nodes.hall;
      hn.poly.setAttribute('class', 'zone ' + hi.cls);
      hn.t2.textContent = hi.wait;
      hn.t3.textContent = hi.sub || '';
      hn.poly.setAttribute('aria-label', hi.aria || '');
    }
    Object.keys(this.nodes.ovf).forEach(function (id) {
      var o = self.nodes.ovf[id];
      var on = ctx.overflowOn ? ctx.overflowOn(id, o.def) : false;
      var hidden = ctx.regionVis(regionOf(id)) !== 'full' && !(self.mode === 'live' && ctx.regionVis(regionOf(id)) === 'agg');
      o.poly.setAttribute('class', 'ovf' + (on && !hidden ? ' ovf-on' : ''));
      if (on && !hidden) { o.poly.style.fill = 'url(#' + self.fid + '-hatch)'; } else o.poly.style.fill = '';
    });
    Object.keys(this.nodes.servers).forEach(function (sid) {
      var n = self.nodes.servers[sid];
      var inf = ctx.serverInfo ? ctx.serverInfo(sid) : null;
      var st = inf ? inf.state : 'closed';
      if (st !== n.state) {
        n.r.setAttribute('class', 'srv s-' + st);
        drawGlyph(n.g, n.s, st);
        n.state = st;
      }
      if (inf && inf.aria) n.r.setAttribute('aria-label', inf.aria);
    });
    Object.keys(this.nodes.sensors).forEach(function (sid) {
      var n = self.nodes.sensors[sid];
      var inf = ctx.sensorInfo ? ctx.sensorInfo(sid) : null;
      if (n.extra && !inf) return;
      n.c.setAttribute('class', 'sensor ' + (inf ? inf.cls : 'sn-on'));
      if (inf && inf.aria) n.c.setAttribute('aria-label', inf.aria);
    });
    if (this.tipFor && this.tipFn) this.showTip(this.tipFor, this.tipFn);
  };

  /* tooltips */

  FloorPlan.prototype.bindTip = function (node, fn) {
    var self = this;
    function show() { self.showTip(node, fn); }
    function hide() { self.hideTip(node); }
    node.addEventListener('pointerenter', show);
    node.addEventListener('pointerleave', hide);
    node.addEventListener('focus', show);
    node.addEventListener('blur', hide);
    node.setAttribute('aria-describedby', this.tip.id);
  };

  FloorPlan.prototype.showTip = function (node, fn) {
    var data = fn();
    if (!data) { this.tip.style.display = 'none'; return; }
    this.tipFor = node; this.tipFn = fn;
    var html = '<strong>' + esc(data.title) + '</strong>';
    if (data.rows && data.rows.length) {
      html += '<dl>' + data.rows.map(function (r) { return '<dt>' + esc(r[0]) + '</dt><dd>' + esc(r[1]) + '</dd>'; }).join('') + '</dl>';
    }
    if (data.note) html += '<div class="muted small" style="margin-block-start:4px">' + esc(data.note) + '</div>';
    this.tip.innerHTML = html;
    this.tip.style.display = 'block';
    var wr = this.wrap.getBoundingClientRect(), nr = node.getBoundingClientRect();
    var tw = this.tip.offsetWidth, th = this.tip.offsetHeight;
    var x = nr.left - wr.left + nr.width / 2 - tw / 2;
    x = Math.max(4, Math.min(x, wr.width - tw - 4));
    var y = nr.top - wr.top - th - 8;
    if (y < 4) y = nr.bottom - wr.top + 8;
    if (y + th > wr.height + 60) y = Math.max(4, wr.height - th - 4);
    this.tip.style.left = x + 'px';
    this.tip.style.top = y + 'px';
  };

  FloorPlan.prototype.hideTip = function (node) {
    if (this.tipFor === node) { this.tip.style.display = 'none'; this.tipFor = null; this.tipFn = null; }
  };

  function esc(s) { return String(s == null ? '' : s).replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); }

  /* legend */

  function sw(inner) { return '<svg viewBox="0 0 16 12" aria-hidden="true">' + inner + '</svg>'; }
  FloorPlan.prototype.drawLegend = function () {
    var items = [];
    if (this.mode === 'live' || this.mode === 'devices') {
      if (this.mode === 'live') {
        items.push('<span>' + sw('<rect x="1" y="1" width="14" height="10" rx="1" style="fill:var(--good);fill-opacity:.25;stroke:var(--good)"/>') + 'Under 10 min</span>');
        items.push('<span>' + sw('<rect x="1" y="1" width="14" height="10" rx="1" style="fill:var(--warn);fill-opacity:.3;stroke:var(--warn)"/>') + '10 to 15 min</span>');
        items.push('<span>' + sw('<rect x="1" y="1" width="14" height="10" rx="1" style="fill:var(--crit);fill-opacity:.3;stroke:var(--crit)"/>') + 'Above 15 min</span>');
        items.push('<span>' + sw('<rect x="1" y="1" width="14" height="10" style="fill:var(--panel-2);stroke:var(--unknown)"/><path d="M1 11L11 1M6 11L15 2" style="stroke:var(--unknown);stroke-width:2"/>') + 'Degraded, wait shown as a band</span>');
        items.push('<span>' + sw('<rect x="1" y="1" width="14" height="10" style="fill:var(--unknown);fill-opacity:.2;stroke:var(--unknown);stroke-dasharray:3 2"/>') + 'No service</span>');
        items.push('<span>' + sw('<line x1="1" y1="6" x2="15" y2="6" style="stroke:var(--accent);stroke-width:3;stroke-dasharray:4 2"/>') + 'Entry line</span>');
        items.push('<span>' + sw('<line x1="1" y1="6" x2="15" y2="6" style="stroke:var(--ink);stroke-width:3;stroke-opacity:.75"/>') + 'Exit line</span>');
        items.push('<span>' + sw('<rect x="3" y="1" width="10" height="10" style="fill:var(--accent);stroke:var(--accent)"/><rect x="6" y="4" width="4" height="4" style="fill:var(--accent-ink)"/>') + 'Serving</span>');
        items.push('<span>' + sw('<rect x="3" y="1" width="10" height="10" style="fill:var(--panel);stroke:var(--accent)"/><circle cx="8" cy="6" r="1.6" style="fill:var(--accent)"/>') + 'Idle</span>');
        items.push('<span>' + sw('<rect x="3" y="1" width="10" height="10" style="fill:var(--warn);fill-opacity:.25;stroke:var(--warn)"/><rect x="5.5" y="3" width="1.8" height="6" style="fill:var(--warn)"/><rect x="8.7" y="3" width="1.8" height="6" style="fill:var(--warn)"/>') + 'Paused</span>');
        items.push('<span>' + sw('<rect x="3" y="1" width="10" height="10" style="fill:none;stroke:var(--muted);stroke-dasharray:2 2"/>') + 'Closed</span>');
        items.push('<span>' + sw('<rect x="3" y="1" width="10" height="10" style="fill:var(--unknown);fill-opacity:.25;stroke:var(--unknown)"/><text x="8" y="9.5" text-anchor="middle" style="font-size:9px;font-weight:700;fill:var(--ink)">?</text>') + 'Unknown</span>');
        items.push('<span>' + sw('<rect x="3" y="1" width="10" height="10" style="fill:var(--crit);fill-opacity:.15;stroke:var(--crit)"/><path d="M5 3l6 6m0-6l-6 6" style="stroke:var(--crit);stroke-width:1.4"/>') + 'Out of service</span>');
      } else {
        items.push('<span>' + sw('<circle cx="8" cy="6" r="4.5" style="fill:var(--good);fill-opacity:.3;stroke:var(--good)"/>') + 'Online</span>');
        items.push('<span>' + sw('<circle cx="8" cy="6" r="4.5" style="fill:var(--warn);fill-opacity:.3;stroke:var(--warn)"/>') + 'Online, clock drift above 50 ms</span>');
        items.push('<span>' + sw('<circle cx="8" cy="6" r="4.5" style="fill:var(--crit);fill-opacity:.4;stroke:var(--crit)"/>') + 'Offline</span>');
        items.push('<span>' + sw('<circle cx="8" cy="6" r="5" style="fill:var(--accent);fill-opacity:.08;stroke:var(--accent);stroke-dasharray:2 2"/>') + 'Approximate coverage</span>');
      }
      items.push('<span>' + sw('<rect x="1" y="1" width="14" height="10" style="fill:var(--panel-2);stroke:var(--muted);stroke-dasharray:3 2"/>') + 'Outside your view</span>');
    } else {
      items.push('<span>' + sw('<rect x="1" y="1" width="14" height="10" style="fill:var(--accent);fill-opacity:.15;stroke:var(--accent);stroke-width:1.5"/>') + 'Selected zone</span>');
      items.push('<span>' + sw('<circle cx="8" cy="6" r="4" style="fill:var(--panel);stroke:var(--accent);stroke-width:2"/>') + 'Vertex handle</span>');
      items.push('<span>' + sw('<rect x="4" y="2" width="8" height="8" style="fill:var(--accent)"/>') + 'Entry or exit line end</span>');
      items.push('<span>' + sw('<rect x="1" y="1" width="14" height="10" style="fill:var(--panel-2);stroke:var(--line)"/>') + 'Not editable in this role</span>');
    }
    this.legend.innerHTML = items.join('');
  };

  /* editor */

  FloorPlan.prototype.select = function (id) {
    this.selected = id;
    if (this.opt.onSelect) this.opt.onSelect(id);
    this.update(this.ctx);
    this.drawHandles();
  };

  FloorPlan.prototype.svgPoint = function (evt) {
    var p = this.svg.createSVGPoint();
    p.x = evt.clientX; p.y = evt.clientY;
    var m = this.svg.getScreenCTM();
    if (!m) return { x: 0, y: 0 };
    var r = p.matrixTransform(m.inverse());
    return { x: Math.max(0, Math.min(W, r.x)), y: Math.max(0, Math.min(H, r.y)) };
  };

  FloorPlan.prototype.drawHandles = function () {
    var self = this, g = this.gHandles;
    if (!g) return;
    while (g.firstChild) g.removeChild(g.firstChild);
    var id = this.selected, ctx = this.ctx;
    if (!id || !ctx.geometry.zones[id] || zLevel(id, ctx.geometry.zones[id]) !== this.level) return;
    var z = ctx.geometry.zones[id];
    function attach(node, get, set, label) {
      node.setAttribute('tabindex', '0');
      node.setAttribute('role', 'slider');
      node.setAttribute('aria-label', label + '. Use arrow keys to move.');
      node.setAttribute('aria-valuetext', 'x ' + Math.round(get()[0]) + ', y ' + Math.round(get()[1]));
      node.addEventListener('pointerdown', function (e) {
        e.preventDefault();
        node.setPointerCapture(e.pointerId);
        node.classList.add('drag');
        function mv(ev) { var p = self.svgPoint(ev); set(Math.round(p.x), Math.round(p.y)); self.redrawSelected(); }
        function up(ev) {
          node.releasePointerCapture(ev.pointerId);
          node.classList.remove('drag');
          node.removeEventListener('pointermove', mv);
          node.removeEventListener('pointerup', up);
          node.removeEventListener('pointercancel', up);
          if (self.opt.onEdit) self.opt.onEdit(id);
        }
        node.addEventListener('pointermove', mv);
        node.addEventListener('pointerup', up);
        node.addEventListener('pointercancel', up);
      });
      node.addEventListener('keydown', function (e) {
        var d = e.shiftKey ? 10 : 2, v = get(), nx = v[0], ny = v[1];
        if (e.key === 'ArrowLeft') nx -= d; else if (e.key === 'ArrowRight') nx += d;
        else if (e.key === 'ArrowUp') ny -= d; else if (e.key === 'ArrowDown') ny += d; else return;
        e.preventDefault();
        set(Math.max(0, Math.min(W, nx)), Math.max(0, Math.min(H, ny)));
        self.redrawSelected();
        node.setAttribute('aria-valuetext', 'x ' + Math.round(get()[0]) + ', y ' + Math.round(get()[1]));
        if (self.opt.onEdit) self.opt.onEdit(id);
      });
    }
    this.nodes.handles = [];
    z.poly.forEach(function (p, k) {
      var hg = el('g', { cls: 'hgrp' }, g);
      var hit = el('circle', { cx: p[0], cy: p[1], r: 14, cls: 'hit' }, hg);
      var c = el('circle', { cx: p[0], cy: p[1], r: 6, cls: 'handle' }, hg);
      attach(hg, function () { return z.poly[k]; }, function (x, y) { z.poly[k][0] = x; z.poly[k][1] = y; }, 'Vertex ' + (k + 1) + ' of zone ' + id);
      self.nodes.handles.push({ kind: 'v', k: k, c: c, hit: hit });
    });
    ['entry', 'exit'].forEach(function (kind) {
      if (!z[kind]) return;
      z[kind].forEach(function (p, k) {
        var c = el('rect', { x: p[0] - 5, y: p[1] - 5, width: 10, height: 10, cls: 'handle-line' }, g);
        attach(c, function () { return z[kind][k]; }, function (x, y) { z[kind][k][0] = x; z[kind][k][1] = y; }, (kind === 'entry' ? 'Entry' : 'Exit') + ' line end ' + (k + 1) + ' of zone ' + id);
        self.nodes.handles.push({ kind: kind, k: k, c: c });
      });
    });
  };

  FloorPlan.prototype.redrawSelected = function () {
    var id = this.selected, z = this.ctx.geometry.zones[id], n = this.nodes.zones[id];
    if (!z || !n) return;
    n.poly.setAttribute('points', pts(z.poly));
    n.cp.setAttribute('points', pts(z.poly));
    if (!z.meta || z.meta.type === 'snake') n.snake.setAttribute('d', this.snakePath(z.poly));
    if (n.entry && z.entry) { n.entry.setAttribute('x1', z.entry[0][0]); n.entry.setAttribute('y1', z.entry[0][1]); n.entry.setAttribute('x2', z.entry[1][0]); n.entry.setAttribute('y2', z.entry[1][1]); }
    if (n.exit && z.exit) { n.exit.setAttribute('x1', z.exit[0][0]); n.exit.setAttribute('y1', z.exit[0][1]); n.exit.setAttribute('x2', z.exit[1][0]); n.exit.setAttribute('y2', z.exit[1][1]); }
    var b = bbox(z.poly);
    [n.t1, n.t2, n.t3].forEach(function (t, i) { t.setAttribute('x', b.cx); t.setAttribute('y', b.cy + [-6, 12, 27][i]); });
    this.nodes.handles.forEach(function (h) {
      if (h.kind === 'v') { var p = z.poly[h.k]; h.c.setAttribute('cx', p[0]); h.c.setAttribute('cy', p[1]); h.hit.setAttribute('cx', p[0]); h.hit.setAttribute('cy', p[1]); }
      else { var q = z[h.kind][h.k]; h.c.setAttribute('x', q[0] - 5); h.c.setAttribute('y', q[1] - 5); }
    });
  };

  /* a small plan used inside create drawers: click to place points; the overlay is redrawn by the caller */
  function Picker(host, opt) {
    var self = this;
    this.host = host; this.opt = opt || {};
    host.classList.add('fp');
    host.innerHTML = '';
    var wrap = document.createElement('div');
    wrap.className = 'fp-scroll picker';
    host.appendChild(wrap);
    this.svg = el('svg', { viewBox: '0 0 ' + W + ' ' + H, role: 'group', 'aria-label': this.opt.label || 'Plan picker. Click to place a point; the table below is the keyboard alternative.' }, null);
    wrap.appendChild(this.svg);
    this.draw();
    this.svg.addEventListener('click', function (e) {
      var p = FloorPlan.prototype.svgPoint.call(self, e);
      if (self.opt.onPick) self.opt.onPick(Math.round(p.x), Math.round(p.y));
    });
  }
  Picker.prototype.draw = function () {
    var svg = this.svg, lv = this.opt.level || 'arr', geom = this.opt.geometry || V12;
    while (svg.firstChild) svg.removeChild(svg.firstChild);
    var g = el('g', null, svg);
    FloorPlan.prototype.drawStatic.call(this, g, lv);
    Object.keys(geom.zones).forEach(function (id) {
      var z = geom.zones[id];
      if (zLevel(id, z) !== lv) return;
      var isLine = z.meta && z.meta.type === 'line';
      el(isLine ? 'polyline' : 'polygon', { points: pts(z.poly), cls: 'zone z-off', style: 'pointer-events:none' }, g);
      var b = bbox(z.poly);
      el('text', { x: b.cx, y: b.cy + 4, 'text-anchor': 'middle', cls: 'lbl-small', text: z.meta ? z.meta.name : id }, g);
    });
    this.overlay = el('g', null, svg);
    if (this.opt.drawOverlay) this.opt.drawOverlay(this.overlay, el);
  };
  Picker.prototype.setLevel = function (lv) { this.opt.level = lv; this.draw(); };
  Picker.prototype.redraw = function () {
    while (this.overlay.firstChild) this.overlay.removeChild(this.overlay.firstChild);
    if (this.opt.drawOverlay) this.opt.drawOverlay(this.overlay, el);
  };

  global.QFloor = {
    picker: function (host, opt) { return new Picker(host, opt); }, regionOf: regionOf, regionAt: regionAt, zLevel: zLevel, pts: pts, drawFootprint: drawFootprint,
    create: function (host, opt) { return new FloorPlan(host, opt); },
    V12: V12, draftV13: draftV13, baseGeometry: baseGeometry, clone: clone,
    area: area, bbox: bbox, snakeCapacity: snakeCapacity, BASE_CAP: BASE_CAP,
    ZONE_REGION: ZONE_REGION, REGIONS: REGIONS, HALLS: HALLS, OVERFLOW: OVERFLOW, SRV: SRV, serverRegion: serverRegion,
    sensorPositions: sensorPositions, zoneLevel: zoneLevel, LANE_SHORT: LANE_SHORT
  };
})(window);
