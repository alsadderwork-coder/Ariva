(function () {
  'use strict';
  var U = QUI, S = QSim, F = QFloor, FF = U.F;
  var fp = null, fpProfile = null;
  var TYPE = { stereo: 'Stereo', lidar: 'LiDAR' };
  var TYPE_LONG = { stereo: 'Stereo overhead sensor, generic certified family', lidar: '3D LiDAR, generic certified family' };
  var LIDAR_R = 10;
  var ZNAME = { 'A-OV': 'Arrival immigration: overflow band', 'D-OV': 'Departure immigration: overflow band', 'SEC-OV': 'Security: overflow band' };
  S.QUEUES.forEach(function (d) { ZNAME[d.id] = d.name; });

  function pad(n) { return n < 10 ? '0' + n : String(n); }
  var META = {};
  S.SENSORS.forEach(function (s) {
    var k = parseInt(s.id.slice(2), 10);
    var off = Math.round((S.h3(4242, k, 7) - 0.5) * 36);
    if (s.id === 'S-44') off = 64;
    var cal = s.id === 'S-17' ? '2026-09-03' : k % 3 === 0 ? '2026-08-' + pad(12 + (k % 17)) : '2026-09-' + pad(1 + (k * 5) % 24);
    META[s.id] = { fps: s.type === 'LiDAR' ? 10 : 12.5, offset: off, cal: cal };
  });

  function visible(s) { return U.regionVis(F.ZONE_REGION[s.zone]) === 'full'; }
  function madeVisible(x) { return U.regionVis(U.sensorRegion(x)) === 'full'; }
  function made() { return U.state.c.sensors.filter(madeVisible); }
  function canManage(x) { return U.canCreate('sensor') && U.createRegions().indexOf(U.sensorRegion(x)) >= 0; }
  function r1(v) { return Math.round(v * 10) / 10; }

  /* coverage footprint from the mounting height, using the BOQ's assumed footprints */
  function footprint(type, h) {
    if (type === 'lidar') return { r: LIDAR_R, text: LIDAR_R + ' m radius', note: 'Assumed radius; the BOQ has no LiDAR footprint' };
    if (h >= 4 && h <= 6) return { w: 10, h: 10, text: '10 x 10 m', note: 'Assumed, from the BOQ (4 to 6 m)' };
    if (h >= 10 && h <= 14) return { w: 12, h: 9, text: '12 x 9 m', note: 'Assumed, from the BOQ (10 to 14 m)' };
    var w, d;
    if (h > 6 && h < 10) { var t = (h - 6) / 4; w = 10 + 2 * t; d = 10 - t; }
    else if (h < 4) { w = 10 * h / 4; d = w; }
    else { w = 12 * h / 14; d = 9 * h / 14; }
    return { w: r1(w), h: r1(d), text: r1(w) + ' x ' + r1(d) + ' m', note: 'Estimate, ' + (h > 6 && h < 10 ? 'interpolated between' : 'scaled from') + ' the BOQ\'s assumed footprints' };
  }
  function zonePoly(zid) {
    var g = U.latestGeometry();
    if (g.zones[zid]) return g.zones[zid].poly;
    if (F.OVERFLOW[zid]) return F.OVERFLOW[zid].poly;
    return null;
  }
  function zoneLv(zid) {
    var g = U.latestGeometry();
    if (F.OVERFLOW[zid]) return F.OVERFLOW[zid].level;
    return F.zLevel(zid, g.zones[zid]);
  }
  function zoneOptions() {
    var regs = U.createRegions(), g = U.latestGeometry(), out = { arr: [], dep: [] };
    Object.keys(g.zones).concat(['A-OV', 'D-OV', 'SEC-OV']).forEach(function (zid) {
      var z = g.zones[zid], reg = F.ZONE_REGION[zid] || (z ? F.regionOf(zid, z) : null);
      if (regs.indexOf(reg) < 0 || !zonePoly(zid)) return;
      out[zoneLv(zid)].push([zid, U.zoneName(zid) + (S.QI[zid] != null || F.OVERFLOW[zid] ? ' (' + zid + ')' : '')]);
    });
    var groups = [];
    if (out.arr.length) groups.push({ group: 'Arrivals level', options: out.arr });
    if (out.dep.length) groups.push({ group: 'Departures level', options: out.dep });
    return groups;
  }
  function seededIp(n) { return '10.20.0.' + n; }
  function ipOwner(ip) {
    var m = /^10\.20\.0\.(\d+)$/.exec(ip);
    if (m && +m[1] >= 1 && +m[1] <= 59) return 'S-' + (+m[1] < 10 ? '0' : '') + (+m[1]);
    var x = U.state.c.sensors.filter(function (y) { return y.ip === ip; })[0];
    return x ? x.id : null;
  }
  function fpOverlaps(fpv, x, y, poly) {
    var b = F.bbox(poly), hw = fpv.r ? fpv.r * 10 : fpv.w * 5, hh = fpv.r ? fpv.r * 10 : fpv.h * 5;
    return x + hw > b.x0 && x - hw < b.x1 && y + hh > b.y0 && y - hh < b.y1;
  }
  function status(s, m) {
    if (U.day.sensorOffline(s.id, m)) return 'off';
    if (Math.abs(META[s.id].offset) > 50) return 'warn';
    return 'on';
  }
  var STATUS_TXT = { on: 'Online', warn: 'Online, clock drift', off: 'Offline' };
  var STATUS_LV = { on: 'good', warn: 'warn', off: 'crit' };

  function build() {
    var body = document.getElementById('dBody');
    body.__html = null;
    document.getElementById('kpis').innerHTML = '';
    document.getElementById('kpis').__html = null;
    if (U.screenAccess('devices') === 'none') { body.innerHTML = U.denied('Sensor health is managed by the border and airport systems teams.'); fp = null; return; }
    body.innerHTML =
      '<section class="panel" aria-labelledby="dfTitle"><div class="panel-head"><div><h2 id="dfTitle">Sensors on the floor plan</h2><p>Circles show each sensor and its approximate coverage. Hover or focus a sensor for details.</p></div></div><div id="dfp"></div></section>' +
      '<div class="grid cols-2"><section class="panel" aria-labelledby="s17Title"><div class="panel-head"><div><h2 id="s17Title">S-17 outage history</h2><p>Stereo sensor over the arrivals Visitors snake.</p></div></div><div id="s17"></div></section>' +
      '<section class="panel" aria-labelledby="alTitle"><div class="panel-head"><div><h2 id="alTitle">Device alarms today</h2><p>Visible to the current role.</p></div></div><div id="dAlarms"></div></section></div>' +
      '<section class="panel" id="registry" aria-labelledby="regTitle"><div class="panel-head"><div><h2 id="regTitle">Sensor registry</h2><p>Frame rate, clock offset against the site time server, and last calibration. Synthetic values. Sensors registered here are listed first and stay Commissioning until a calibration passes.</p></div><div class="panel-actions" id="snNew"></div></div><div id="reg"></div></section>';
    document.getElementById('snNew').innerHTML = U.createBtn('sensor', 'Register sensor', 'snBtn');
    var sb = document.getElementById('snBtn');
    if (!sb.disabled) sb.addEventListener('click', function () { openSensor(sb); });
    document.getElementById('reg').addEventListener('click', onRegClick);
    var role = U.role();
    fp = F.create(document.getElementById('dfp'), { mode: 'devices', level: role === 'tdm' ? 'dep' : 'arr' });
    fpProfile = null;
  }

  function ctx(m) {
    var base = U.fpContext(m);
    base.sensorInfo = function (sid) {
      var mx = U.state.c.sensors.filter(function (x) { return x.id === sid; })[0];
      if (mx) return { cls: mx.status === 'online' ? 'sn-on' : mx.status === 'failed' ? 'sn-fail' : 'sn-new', aria: sid + ', ' + TYPE[mx.type] + ', ' + U.SENSOR_STATUS[mx.status] };
      var s = S.SENSORS.filter(function (x) { return x.id === sid; })[0], st = status(s, m);
      return { cls: 'sn-' + st, aria: sid + ', ' + s.type + ', ' + STATUS_TXT[st] };
    };
    base.extraSensors = made().map(function (x) { return { id: x.id, level: x.level, x: x.x, y: x.y, fp: x.fp, cls: x.status === 'online' ? 'sn-on' : x.status === 'failed' ? 'sn-fail' : 'sn-new' }; });
    base.sensorTip = function (sid) {
      var mx = U.state.c.sensors.filter(function (x) { return x.id === sid; })[0];
      if (mx) return { title: 'Sensor ' + sid, rows: [['Type', TYPE[mx.type]], ['Zone', U.zoneName(mx.zone)], ['Status', U.SENSOR_STATUS[mx.status]], ['Mounting height', mx.height + ' m'], ['Footprint', mx.fp.text + ' (' + mx.fp.note + ')'], ['Network', mx.ip + ', ' + mx.clock]],
        note: mx.status === 'online' ? null : 'Not counting until a calibration passes.' };
      var s = S.SENSORS.filter(function (x) { return x.id === sid; })[0], st = status(s, m), mt = META[sid];
      return { title: 'Sensor ' + sid, rows: [['Type', s.type], ['Zone', ZNAME[s.zone]], ['Status', STATUS_TXT[st]], ['Frame rate', st === 'off' ? '0 fps' : mt.fps + ' fps'], ['Clock offset', st === 'off' ? 'No data' : (mt.offset > 0 ? '+' : '') + mt.offset + ' ms'], ['Last calibration', mt.cal]] };
    };
    base.zoneInfo = null;
    base.zoneLabel = U.shortName;
    return base;
  }

  function update(m) {
    if (!fp) return;
    var list = S.SENSORS.filter(visible), mine = made();
    var prof = U.profileAt(m), c = ctx(m), sig = prof + '|' + mine.map(function (x) { return x.id + x.status; }).join();
    if (fpProfile !== sig) { fp.render(c); fpProfile = sig; } else fp.update(c);
    var on = 0, off = [], drift = 0, oldest = '9999';
    list.forEach(function (s) { var st = status(s, m); if (st === 'off') off.push(s.id); else on++; if (st === 'warn') drift++; if (META[s.id].cal < oldest) oldest = META[s.id].cal; });
    var mOn = mine.filter(function (x) { return x.status === 'online'; }).length, comm = mine.filter(function (x) { return x.status !== 'online'; });
    U.html(document.getElementById('kpis'), [
      U.kpi({ hero: true, level: off.length ? 'crit' : '', label: 'Sensors online', value: (on + mOn) + ' <small>of ' + (list.length + mine.length) + '</small>', sub: off.length ? U.st('crit', 'Offline: ' + off.join(', ')) : comm.length ? U.st('warn', 'Commissioning: ' + comm.map(function (x) { return x.id; }).join(', ')) : U.st('good', 'All reporting') }),
      U.kpi({ label: 'Clock offset above 50 ms', value: String(drift), sub: drift ? 'Resync scheduled overnight' : 'All within 50 ms' }),
      U.kpi({ label: 'Oldest calibration', value: '<span style="font-size:20px">' + oldest + '</span>', sub: 'Recalibrate every 90 days' }),
      U.kpi({ label: 'Types', value: (list.filter(function (s) { return s.type === 'Stereo'; }).length + mine.filter(function (x) { return x.type === 'stereo'; }).length) + ' <small>stereo</small> ' +
        (list.filter(function (s) { return s.type === 'LiDAR'; }).length + mine.filter(function (x) { return x.type === 'lidar'; }).length) + ' <small>LiDAR</small>', sub: 'Vendor-agnostic registry, generic certified families' })
    ].join(''));

    var mrows = mine.map(function (x) {
      var cal = U.state.c.calibrations.filter(function (r) { return r.sensor === x.id; }).slice(-1)[0], ok = x.status === 'online';
      var acts = canManage(x) ? (ok ? '' : '<button type="button" class="btn" data-sn="cal" data-id="' + x.id + '">Record calibration</button><button type="button" class="btn" data-sn="remove" data-id="' + x.id + '">Remove</button>') : '';
      return '<tr' + U.rowCls(x.id) + ' data-sensor="' + x.id + '"><th scope="row" class="mono">' + x.id + '</th><td>' + TYPE[x.type] + '<div class="small muted">' + x.height + ' m, ' + x.fp.text + '</div></td><td>' + U.esc(U.zoneName(x.zone)) + '</td>' +
        '<td>' + U.st(ok ? 'good' : x.status === 'failed' ? 'crit' : 'warn', U.SENSOR_STATUS[x.status]) + '</td><td class="num">' + (ok ? (x.type === 'lidar' ? '10.0' : '12.5') + ' fps' : 'Not counting') + '</td>' +
        '<td class="num">' + (ok ? (x.clock === 'PTP' ? '+0' : '+4') + ' ms' : x.clock) + '</td><td class="mono">' + (cal ? cal.date + (cal.pass ? '' : ' (failed)') : 'None') + '</td><td class="actions">' + acts + '</td></tr>';
    });
    var rows = list.map(function (s) {
      var st = status(s, m), mt = META[s.id];
      return '<tr' + (st === 'off' ? ' class="row-hl"' : '') + '><th scope="row" class="mono">' + s.id + '</th><td>' + s.type + '</td><td>' + U.esc(ZNAME[s.zone]) + '</td><td>' + U.st(STATUS_LV[st], STATUS_TXT[st]) + '</td>' +
        '<td class="num">' + (st === 'off' ? '0' : mt.fps.toFixed(1)) + ' fps</td><td class="num">' + (st === 'off' ? 'No data' : (mt.offset > 0 ? '+' : '') + mt.offset + ' ms') + '</td><td class="mono">' + mt.cal + '</td><td></td></tr>';
    });
    U.html(document.getElementById('reg'), '<div class="table-wrap scroll-y" tabindex="0" aria-label="Sensor registry, scrollable"><table class="tbl"><thead><tr><th scope="col">ID</th><th scope="col">Type</th><th scope="col">Zone</th><th scope="col">Status</th><th scope="col" class="num">Frame rate</th><th scope="col" class="num">Clock offset</th><th scope="col">Last calibration</th><th scope="col"><span class="sr-only">Actions</span></th></tr></thead><tbody>' + mrows.join('') + rows.join('') + '</tbody></table></div>');

    var s17 = document.getElementById('s17');
    if (!U.can('imm.devices')) U.html(s17, U.denied('S-17 is an immigration hall sensor.'));
    else {
      var o = S.OUTAGES[0];
      var hist = S.OUTAGE_HISTORY.map(function (h) { return '<tr><th scope="row" class="mono">' + h.date + '</th><td class="mono">' + h.from + ' to ' + h.to + '</td><td class="num">' + h.minutes + ' min</td><td>' + U.esc(h.cause) + '</td><td>' + U.st('good', 'Recovered') + '</td></tr>'; });
      if (m >= o.from) {
        var live = m < o.to;
        hist.push('<tr class="row-hl"><th scope="row" class="mono">' + S.DATE + '</th><td class="mono">' + S.clock(o.from) + ' to ' + (live ? 'now' : S.clock(o.to)) + '</td><td class="num">' + (live ? m - o.from : o.to - o.from) + ' min</td><td>PoE switch port flapping</td><td>' +
          (live ? U.st('crit', 'Offline now; Visitors zone degraded') : U.st('good', 'Recovered at ' + S.clock(o.to))) + '</td></tr>');
      }
      U.html(s17, '<div class="table-wrap"><table class="tbl"><thead><tr><th scope="col">Date</th><th scope="col">Window</th><th scope="col" class="num">Duration</th><th scope="col">Cause</th><th scope="col">Result</th></tr></thead><tbody>' + hist.join('') + '</tbody></table></div>' +
        '<p class="small muted" style="margin-block-start:6px">While S-17 is offline the Visitors zone is marked degraded and its wait is shown as a band. SLA bins over a zone with more than 5 minutes of outage are excluded from penalties.</p>');
    }
    var al = U.alertsAt(m).filter(function (a) { return a.kind === 'device'; });
    U.html(document.getElementById('dAlarms'), al.length ? '<ul class="alerts">' + al.map(function (a) {
      return '<li class="alert sev-warning' + (a.status === 'cleared' ? ' is-cleared' : '') + '"><div class="alert-top"><span class="alert-rule">' + U.esc(a.rule) + ': ' + a.sensor + '</span><span class="mono small">' + S.clock(a.raisedAt) + '</span></div>' +
        '<div class="alert-meta">' + U.esc(a.text) + '. Owner: ' + U.esc(a.owner) + '.</div><div class="alert-actions">' + (a.status === 'cleared' ? U.st('good', 'Cleared at ' + S.clock(a.clearedAt)) : U.st('crit', 'Open')) + '</div></li>';
    }).join('') + '</ul>' : '<p class="small muted">No device alarms so far today for this role.</p>');
  }

  /* register a sensor */
  function openSensor(trig) {
    var id = U.nextId('sensor'), n = parseInt(id.slice(2), 10);
    var zopts = zoneOptions(), z0 = zopts.length ? zopts[0].options[0][0] : '';
    var pref = U.role() === 'tdm' ? 'CI-C' : 'A-VIS';
    zopts.forEach(function (g) { g.options.forEach(function (o) { if (o[0] === pref) z0 = pref; }); });
    var html = FF.text('sid', 'Sensor ID', id, { readonly: true, hint: 'Assigned automatically.' }) +
      FF.select('type', 'Type', [['stereo', TYPE_LONG.stereo], ['lidar', TYPE_LONG.lidar]], U.role() === 'tdm' ? 'lidar' : 'stereo') +
      FF.row(FF.select('zone', 'Zone', zopts, z0), FF.text('height', 'Mounting height', 5, { type: 'number', min: 2.5, max: 16, step: 0.5, unit: 'm', inputmode: 'decimal', hint: '2.5 to 16 m.' })) +
      '<div class="fld" data-f="pos"><label id="f-pos-l">Position on the plan</label><div id="snPick"></div><div class="fld-hint">Click the plan to place the sensor, or type x and y below (metres from the plan\'s top left corner).</div></div>' +
      FF.row(FF.text('x', 'x', '', { type: 'number', min: 0, max: 100, step: 0.1, unit: 'm', inputmode: 'decimal', req: true }), FF.text('y', 'y', '', { type: 'number', min: 0, max: 60, step: 0.1, unit: 'm', inputmode: 'decimal', req: true })) +
      '<div class="preview" id="snPrev" aria-live="polite"></div>' +
      FF.row(FF.text('ip', 'Network address', '10.20.1.' + Math.max(1, n - 59), { req: true, inputmode: 'decimal', hint: 'IPv4 on the sensor network.' }), FF.radios('clock', 'Clock source', [['PTP', 'PTP'], ['NTP', 'NTP']], 'PTP')) +
      FF.note('The sensor is Commissioning until a calibration passes; until then it does not count and does not cover its zone.');
    var pick = null;
    U.openDrawer({
      title: 'Register sensor', html: html, trigger: trig, returnFocus: 'snBtn', wide: true, submitLabel: 'Register',
      onOpen: function (form) {
        var fx = form.querySelector('#f-x'), fy = form.querySelector('#f-y');
        function vals() { var v = U.formValues(form); return { v: v, x: U.V.num(v.x), y: U.V.num(v.y), h: U.V.num(v.height) }; }
        function center(zid) { var p = zonePoly(zid); if (!p) return; var b = F.bbox(p); fx.value = r1(b.cx / 10); fy.value = r1(b.cy / 10); }
        function overlay(g, el) {
          var st = vals(), zp = zonePoly(st.v.zone);
          var pos = F.sensorPositions(U.latestGeometry());
          Object.keys(pos).forEach(function (k) { var p = pos[k]; if (p.level === zoneLv(st.v.zone)) el('circle', { cx: p.x, cy: p.y, r: 4, style: 'fill:var(--muted);opacity:.6' }, g); });
          if (zp) el('polygon', { points: F.pts(zp), cls: 'pick-poly' }, g);
          if (st.x == null || st.y == null || st.h == null) return;
          var fpv = footprint(st.v.type, Math.max(2.5, Math.min(16, st.h)));
          F.drawFootprint(g, { x: st.x * 10, y: st.y * 10, fp: fpv });
          el('circle', { cx: st.x * 10, cy: st.y * 10, r: 7, cls: 'pick-pt' }, g);
        }
        pick = F.picker(document.getElementById('snPick'), { level: zoneLv(z0), geometry: U.latestGeometry(), label: 'Plan for placing the sensor. Click to set its position; the x and y fields are the keyboard alternative.',
          onPick: function (x, y) { fx.value = r1(x / 10); fy.value = r1(y / 10); sync(); }, drawOverlay: overlay });
        document.getElementById('snPick').querySelector('svg').setAttribute('aria-labelledby', 'f-pos-l');
        var lastZone = z0, lastType = null;
        function sync() {
          var st = vals();
          if (st.v.zone !== lastZone) { lastZone = st.v.zone; pick.setLevel(zoneLv(st.v.zone)); center(st.v.zone); st = vals(); }
          if (st.v.type !== lastType) { if (lastType) form.querySelector('#f-height').value = st.v.type === 'lidar' ? 8 : 5; lastType = st.v.type; st = vals(); }
          pick.redraw();
          var box = document.getElementById('snPrev');
          if (st.h == null || st.h < 2.5 || st.h > 16) { box.innerHTML = '<strong>Coverage footprint</strong>Enter a mounting height from 2.5 to 16 m.'; return; }
          var fpv = footprint(st.v.type, st.h);
          box.innerHTML = '<strong>Coverage footprint: <span data-fp>' + fpv.text + '</span></strong><span data-fp-note>' + U.esc(fpv.note) + '</span>.' +
            (st.x != null && st.y != null && zonePoly(st.v.zone) && !fpOverlaps(fpv, st.x * 10, st.y * 10, zonePoly(st.v.zone)) ? ' <span class="fld-err" style="display:inline">The footprint does not reach the zone yet.</span>' : '');
        }
        center(z0);
        form.addEventListener('change', sync); form.addEventListener('input', sync);
        sync();
      },
      onSubmit: function (v) {
        var e = {}, h = U.V.num(v.height), x = U.V.num(v.x), y = U.V.num(v.y);
        var zp = zonePoly(v.zone), lv = zoneLv(v.zone);
        if (!v.zone || !zp) e.zone = 'Choose a zone.';
        if (h == null || h < 2.5 || h > 16) e.height = 'Mounting height must be between 2.5 and 16 m.';
        if (x == null || y == null) e.x = 'Place the sensor on the plan or enter x and y in metres.';
        else if (!U.G.inLevel([x * 10, y * 10], lv)) e.x = 'The position is outside the ' + (lv === 'arr' ? 'arrivals' : 'departures') + ' level (x 0 to 100 m, y 3 to 59 m).';
        else {
          var reg = F.regionAt(lv, x * 10, y * 10);
          if (U.createRegions().indexOf(reg) < 0) e.x = 'This position is in an area this role does not manage.';
          else if (zp && h != null && h >= 2.5 && h <= 16 && !fpOverlaps(footprint(v.type, h), x * 10, y * 10, zp)) e.x = 'The footprint does not reach ' + U.zoneName(v.zone) + '. Move the sensor over the zone.';
        }
        if (!U.V.ipv4(v.ip)) e.ip = 'Enter an IPv4 address, for example 10.20.1.4.';
        else { var own = ipOwner(v.ip); if (own) e.ip = v.ip + ' is already assigned to ' + own + '.'; }
        if (Object.keys(e).length) return e;
        var fpv = footprint(v.type, h);
        var sn = { id: id, type: v.type, zone: v.zone, level: lv, height: h, x: Math.round(x * 10), y: Math.round(y * 10), ip: v.ip, clock: v.clock, fp: fpv, status: 'commissioning', by: U.roleName(), at: U.now() };
        U.state.c.sensors.push(sn);
        U.created('sensor', id, TYPE[v.type] + ' sensor over ' + U.zoneName(v.zone) + ' at ' + h + ' m, footprint ' + fpv.text + ' (' + fpv.note + '); Commissioning', function () { U.removeFrom('sensors', id); }, function () { fpProfile = null; U.draw(true); });
        return null;
      }
    });
  }

  /* record a calibration */
  function openCalibration(sn, trig) {
    var k = U.state.c.calibrations.filter(function (r) { return r.sensor === sn.id; }).length + 1, rid = 'CAL-' + sn.id + '-' + k;
    var html = FF.text('cid', 'Record', rid, { readonly: true }) +
      FF.select('method', 'Method', [['Manual count comparison, tally counters', 'Manual count comparison, tally counters'], ['Manual count comparison, two observers', 'Manual count comparison, two observers']], 'Manual count comparison, tally counters') +
      FF.row(FF.text('sample', 'Sample size', 200, { type: 'number', min: 50, max: 5000, unit: 'passengers', inputmode: 'numeric' }), FF.text('threshold', 'Pass threshold', 95, { type: 'number', min: 50, max: 100, step: 0.1, unit: '% accuracy', inputmode: 'decimal' })) +
      FF.row(FF.text('accuracy', 'Counting accuracy', '', { type: 'number', min: 0, max: 100, step: 0.1, unit: '%', inputmode: 'decimal', req: true }), FF.text('waitErr', 'Wait-time error', '', { type: 'number', min: 0, max: 30, step: 0.1, unit: 'min', inputmode: 'decimal', req: true, hint: 'Mean absolute error against timed samples.' })) +
      FF.textarea('notes', 'Notes', '', { rows: 2 }) +
      '<div class="preview" id="calPrev" aria-live="polite"></div>';
    U.openDrawer({
      title: 'Record calibration for ' + sn.id, sub: U.esc(TYPE[sn.type] + ' over ' + U.zoneName(sn.zone)), html: html, trigger: trig, returnFocus: 'snBtn', submitLabel: 'Record',
      onOpen: function (form) {
        function sync() {
          var v = U.formValues(form), a = U.V.num(v.accuracy), t = U.V.num(v.threshold);
          document.getElementById('calPrev').innerHTML = '<strong>Result</strong>' + (a == null || t == null ? 'Enter the counting accuracy.' : a >= t ? 'Pass: ' + a + '% is at or above ' + t + '%. ' + sn.id + ' goes Online and covers its zone.' : 'Fail: ' + a + '% is below ' + t + '%. ' + sn.id + ' stays out of service until a new calibration passes.');
        }
        form.addEventListener('input', sync); sync();
      },
      onSubmit: function (v) {
        var e = {}, smp = U.V.int(v.sample), a = U.V.num(v.accuracy), we = U.V.num(v.waitErr), t = U.V.num(v.threshold);
        if (smp == null || smp < 50 || smp > 5000) e.sample = 'Sample size must be a whole number from 50 to 5,000 passengers.';
        if (a == null || a < 0 || a > 100) e.accuracy = 'Counting accuracy must be between 0 and 100%.';
        if (we == null || we < 0 || we > 30) e.waitErr = 'Wait-time error must be between 0 and 30 minutes.';
        if (t == null || t < 50 || t > 100) e.threshold = 'Pass threshold must be between 50 and 100%.';
        if (Object.keys(e).length) return e;
        var pass = a >= t, prev = sn.status;
        var rec = { id: rid, sensor: sn.id, date: S.DATE, at: U.now(), method: v.method, sample: smp, accuracy: a, waitErr: we, threshold: t, pass: pass, notes: v.notes, by: U.roleName() };
        U.state.c.calibrations.push(rec);
        sn.status = pass ? 'online' : 'failed';
        U.saveC();
        U.audit('Recorded calibration', sn.id, (pass ? 'Passed: ' : 'Failed: ') + a + '% counting accuracy against a ' + t + '% threshold, wait error ' + we + ' min, ' + smp + ' passengers (' + rid + ')');
        U.flash(sn.id);
        fpProfile = null;
        U.draw(true);
        U.toast(pass ? sn.id + ' passed calibration and is Online' : sn.id + ' failed calibration', { undo: function () {
          U.removeFrom('calibrations', rid); sn.status = prev; U.saveC(); U.audit('Undid calibration', sn.id, rid + ' removed'); fpProfile = null; U.draw(true);
        } });
        return null;
      }
    });
  }

  function onRegClick(e) {
    var b = e.target.closest('[data-sn]');
    if (!b) return;
    var id = b.getAttribute('data-id'), sn = U.state.c.sensors.filter(function (x) { return x.id === id; })[0];
    if (!sn || !canManage(sn)) return;
    if (b.getAttribute('data-sn') === 'cal') openCalibration(sn, b);
    else {
      U.removeFrom('sensors', id); U.saveC(); U.audit('Removed', id, 'Sensor removed before commissioning completed'); fpProfile = null; U.draw(true);
      U.toast('Removed ' + id, { undo: function () { U.state.c.sensors.push(sn); U.saveC(); U.audit('Restored', id, 'Sensor restored'); fpProfile = null; U.draw(true); } });
      var nb = document.getElementById('snBtn'); if (nb) nb.focus();
    }
  }

  QUI.start({ build: build, update: update });
})();
