/*
  ui.js: shell, state, roles and shared helpers for the DMO QMS prototype.
  Synthetic data only. Every screen script calls QUI.start({ build, update }).
*/
(function (global) {
  'use strict';

  var QSim = global.QSim, QFloor = global.QFloor;
  var DEFAULT_CLOCK = 1060;

  /* storage (optional, every access guarded) */

  var QS = global.QStore || { get: function (k, d) { return d; }, set: function () { }, del: function () { }, clear: function () { } };
  function sget(k, def) { try { return QS.get(k, def); } catch (e) { return def; } }
  function sset(k, v) { try { QS.set(k, v); } catch (e) { } }
  function sdel(k) { try { QS.del(k); } catch (e) { } }
  function sclear() { try { QS.clear(['theme']); } catch (e) { } }

  /* roles and the data boundary */

  var ROLES = [
    { id: 'demo', name: 'Demo presenter (all views)', short: 'Demo presenter', note: 'Demo only. Does not exist in a real deployment.' },
    { id: 'bss', name: 'Border shift supervisor', short: 'Border shift supervisor', note: 'Border deployment' },
    { id: 'tdm', name: 'Terminal duty manager', short: 'Terminal duty manager', note: 'Airport deployment' },
    { id: 'hbm', name: 'Handler B station manager', short: 'Handler B station manager', note: 'Airport deployment, handler portal' }
  ];
  var ROLE_BY_ID = {};
  ROLES.forEach(function (r) { ROLE_BY_ID[r.id] = r; });

  var PERMS = {
    'imm.lanes': { demo: 'full', bss: 'full', tdm: 'agg' },
    'imm.desks': { demo: 1, bss: 1 },
    'imm.egates': { demo: 1, bss: 1 },
    'imm.api': { demo: 1, bss: 1 },
    'imm.alerts': { demo: 1, bss: 1 },
    'imm.devices': { demo: 1, bss: 1 },
    'imm.forecast': { demo: 1, bss: 1 },
    'imm.zones.edit': { demo: 1, bss: 1 },
    'imm.report': { demo: 1, bss: 1 },
    'flights.arr': { demo: 1, bss: 1, tdm: 1 },
    'ci.A': { demo: 1, tdm: 1 },
    'ci.B': { demo: 1, tdm: 1, hbm: 1 },
    'sec': { demo: 1, tdm: 1 },
    'sla': { demo: 1, tdm: 1, hbm: 1 },
    'dispute.raise': { demo: 1, hbm: 1 },
    'dispute.decide': { demo: 1, tdm: 1 },
    'devices.air': { demo: 1, tdm: 1 },
    'air.zones.edit': { demo: 1, tdm: 1 },
    'zones': { demo: 1, bss: 1, tdm: 1 }
  };

  var REGION_VIS = {
    imm: { demo: 'full', bss: 'full', tdm: 'agg', hbm: 'hidden' },
    emi: { demo: 'full', bss: 'full', tdm: 'agg', hbm: 'hidden' },
    reclaim: { demo: 'full', bss: 'full', tdm: 'full', hbm: 'hidden' },
    ciA: { demo: 'full', bss: 'hidden', tdm: 'full', hbm: 'hidden' },
    ciB: { demo: 'full', bss: 'hidden', tdm: 'full', hbm: 'full' },
    sec: { demo: 'full', bss: 'hidden', tdm: 'full', hbm: 'hidden' }
  };

  var NAV = [
    { id: 'ops', href: 'index.html', label: 'Live operations', icon: 'ops' },
    { id: 'rules', href: 'rules.html', label: 'Alert rules', icon: 'bell' },
    { id: 'immigration', href: 'immigration.html', label: 'Immigration', icon: 'imm' },
    { id: 'checkin', href: 'checkin.html', label: 'Check-in and handlers', icon: 'ci' },
    { id: 'sla', href: 'sla.html', label: 'SLA and penalties', icon: 'sla' },
    { id: 'forecast', href: 'forecast.html', label: 'Forecast and staffing', icon: 'fc' },
    { id: 'zones', href: 'zones.html', label: 'Zones', icon: 'zones' },
    { id: 'devices', href: 'devices.html', label: 'Devices', icon: 'dev' },
    { id: 'display', href: 'display.html', label: 'Passenger display', icon: 'disp' },
    { id: 'reports', href: 'reports.html', label: 'Reports', icon: 'rep' },
    { id: 'access', href: 'access.html', label: 'Access and data boundary', icon: 'acc' }
  ];

  var SCREEN_ACCESS = {
    ops: { demo: 'full', bss: 'partial', tdm: 'partial', hbm: 'partial' },
    rules: { demo: 'full', bss: 'partial', tdm: 'partial', hbm: 'partial' },
    immigration: { demo: 'full', bss: 'full', tdm: 'partial', hbm: 'none' },
    checkin: { demo: 'full', bss: 'none', tdm: 'full', hbm: 'partial' },
    sla: { demo: 'full', bss: 'none', tdm: 'full', hbm: 'full' },
    forecast: { demo: 'full', bss: 'partial', tdm: 'partial', hbm: 'partial' },
    zones: { demo: 'full', bss: 'partial', tdm: 'partial', hbm: 'none' },
    devices: { demo: 'full', bss: 'partial', tdm: 'partial', hbm: 'none' },
    display: { demo: 'full', bss: 'full', tdm: 'full', hbm: 'full' },
    reports: { demo: 'full', bss: 'partial', tdm: 'partial', hbm: 'partial' },
    access: { demo: 'full', bss: 'full', tdm: 'full', hbm: 'full' }
  };

  /* the rules access.html lists; every screen enforces the same rules through can() and REGION_VIS */
  var ACCESS_RULES = [
    { screen: 'Live operations', href: 'index.html',
      demo: 'Everything: all zones, desks, counters, alerts and the arrival wave by lane.',
      bss: 'Immigration zones with desk and e-gate states, border alerts (queues and immigration sensors), arrival wave by lane. Check-in and security greyed as "Outside your view".',
      tdm: 'Check-in, security and reclaim with counter and lane states; each immigration hall as one zone with its longest lane wait, no desks; airport alerts; arrival wave with flight totals only, no lane split.',
      hbm: 'Islands C and D with counter states and Handler B alerts. Everything else greyed. No arrival wave.' },
    { screen: 'Alert rules', href: 'rules.html',
      demo: 'Every rule; create, enable, disable and duplicate any of them.',
      bss: 'Rules on immigration queues and immigration sensors; creates and changes them.',
      tdm: 'Rules on check-in, security and airport-side sensors; creates and changes them. Immigration rules are hidden.',
      hbm: 'Rules that cover islands C and D, read only.' },
    { screen: 'Immigration', href: 'immigration.html',
      demo: 'Everything.',
      bss: 'Arrivals and departures: lane waits, desk grid with per-desk interval aggregates, e-gate utilisation and reject reasons.',
      tdm: 'Lane waits and queue lengths only (aggregates). Desk grid, per-desk service times and the e-gate panel are not available.',
      hbm: 'Not available.' },
    { screen: 'Check-in and handlers', href: 'checkin.html',
      demo: 'Everything.',
      bss: 'Not available: no check-in, no handler names or handler data.',
      tdm: 'Both handlers: all 48 counters, waits and SLA bins per island.',
      hbm: 'Islands C and D only. Handler A is not shown.' },
    { screen: 'SLA and penalties', href: 'sla.html',
      demo: 'Everything, including both dispute roles.',
      bss: 'Not available.',
      tdm: 'Handler B contract, evaluation, disputes (start review, uphold, reject) and the evidence pack.',
      hbm: 'Handler B contract, evaluation, disputes (raise only) and the evidence pack.' },
    { screen: 'Forecast and staffing', href: 'forecast.html',
      demo: 'All areas.',
      bss: 'Arrival and departure immigration: demand by lane, desk recommendation against the roster.',
      tdm: 'Check-in for both handlers, and security.',
      hbm: 'Check-in for Handler B only.' },
    { screen: 'Zones', href: 'zones.html',
      demo: 'Edit every zone.',
      bss: 'Edit immigration zones. Airport-side zones greyed.',
      tdm: 'Edit check-in and security zones. Immigration halls greyed.',
      hbm: 'Not available.' },
    { screen: 'Devices', href: 'devices.html',
      demo: 'All 59 sensors.',
      bss: 'Sensors over the immigration halls, including S-17 and its alarm.',
      tdm: 'Sensors over check-in and security.',
      hbm: 'Not available.' },
    { screen: 'Passenger display', href: 'display.html',
      demo: 'The public board.', bss: 'The public board.', tdm: 'The public board.', hbm: 'The public board.' },
    { screen: 'Reports', href: 'reports.html',
      demo: 'The full daily report and CSV.',
      bss: 'Border report: peak waits by lane, forecast against actual, desks against recommendation, e-gates. CSV of immigration intervals.',
      tdm: 'Airport report: check-in, security, Handler B SLA, plus border lane waits as aggregates. CSV without desk data.',
      hbm: 'Handler B section only (islands C and D, SLA). CSV of islands C and D.' },
    { screen: 'Access and data boundary', href: 'access.html',
      demo: 'This page, every user and the whole audit log.', bss: 'This page, border users and the border audit log.', tdm: 'This page, airport and Handler B users and the airport audit log.', hbm: 'This page, Handler B users and their audit entries.' }
  ];

  /* state */

  function validSeed(v) { v = parseInt(v, 10); return isFinite(v) && v >= 1 && v <= 999999 ? v : QSim.DEFAULT_SEED; }
  var CREATED_LISTS = ['rules', 'flights', 'overrides', 'allocations', 'sensors', 'calibrations', 'contracts', 'exclusions', 'reports', 'displays', 'users', 'audit'];
  function normCreated(c) {
    if (!c || typeof c !== 'object') c = {};
    CREATED_LISTS.forEach(function (k) { if (!Array.isArray(c[k])) c[k] = []; });
    if (!c.ruleState || typeof c.ruleState !== 'object') c.ruleState = {};
    if (!c.contractState || typeof c.contractState !== 'object') c.contractState = {};
    if (!c.draft || typeof c.draft !== 'object' || !c.draft.geometry || !c.draft.geometry.zones) c.draft = null;
    return c;
  }

  function loadState() {
    var clock = Number(sget('clock', DEFAULT_CLOCK));
    if (!isFinite(clock) || clock < 0 || clock >= 1440) clock = DEFAULT_CLOCK;
    var speed = Number(sget('speed', 60));
    if ([1, 60, 600].indexOf(speed) < 0) speed = 60;
    var role = sget('role', 'demo');
    if (!ROLE_BY_ID[role]) role = 'demo';
    var profs = sget('profiles', null);
    if (!Array.isArray(profs)) { profs = []; var old = sget('profile', null); if (old && old.geometry && old.geometry.zones) profs.push(old); }
    profs = profs.filter(function (p) { return p && /^v\d+$/.test(p.version) && p.geometry && p.geometry.zones && typeof p.publishedAt === 'number'; });
    var acc = sget('accepted', []);
    if (!Array.isArray(acc)) acc = [];
    var acks = sget('acks', {});
    if (!acks || typeof acks !== 'object') acks = {};
    return { clock: clock, speed: speed, playing: sget('playing', false) === true, seed: validSeed(sget('seed', QSim.DEFAULT_SEED)),
      role: role, profiles: profs, accepted: acc, acks: acks, theme: sget('theme', 'dark') === 'light' ? 'light' : 'dark', c: normCreated(sget('created', null)) };
  }
  var st = loadState();

  var QUI = {
    ROLES: ROLES, ROLE_BY_ID: ROLE_BY_ID, ACCESS_RULES: ACCESS_RULES, SCREEN_ACCESS: SCREEN_ACCESS, NAV: NAV, REGION_VIS: REGION_VIS,
    store: { get: sget, set: sset, del: sdel },
    state: st, day: null
  };

  QUI.role = function () { return st.role; };
  QUI.roleName = function (id) { return ROLE_BY_ID[id || st.role].name; };
  QUI.can = function (p) { var r = PERMS[p]; return !!(r && r[st.role]); };
  QUI.level = function (p) { var r = PERMS[p]; return r ? r[st.role] || null : null; };
  QUI.regionVis = function (r) { return (REGION_VIS[r] || {})[st.role] || 'hidden'; };
  QUI.queueVis = function (qid) { return QUI.regionVis(QFloor.ZONE_REGION[qid]); };
  QUI.screenAccess = function (id) { return (SCREEN_ACCESS[id] || {})[st.role] || 'none'; };
  QUI.now = function () { return Math.floor(st.clock); };

  /* formatting */

  function esc(s) { return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; }); }
  QUI.esc = esc;
  QUI.fmt = function (v, d) { return global.QCharts.fmt(v, { decimals: d || 0 }); };
  QUI.clock = function (m) { return QSim.clock(m); };
  QUI.fmtWait = function (v) {
    if (v == null) return 'No service';
    if (v < 1) return 'Under 1 min';
    return Math.round(v) + ' min';
  };
  QUI.waitLevel = function (v) {
    if (v == null) return 'unknown';
    var r = Math.round(v);
    return r > 15 ? 'crit' : r >= 10 ? 'warn' : 'good';
  };
  QUI.levelText = { good: 'Normal', warn: 'Warning', crit: 'Critical', unknown: 'No service', deg: 'Degraded' };
  QUI.bandText = function (b) { return b[0] + ' to ' + b[1] + ' min'; };
  QUI.shortName = function (qid) {
    var d = QSim.QUEUES[QSim.QI[qid]];
    if (d.group === 'imm' || d.group === 'egate') return QFloor.LANE_SHORT[d.lane];
    if (d.group === 'sec') return d.id === 'SEC-N' ? 'North' : 'South';
    return 'Island ' + d.island;
  };
  QUI.qname = function (qid) { return QSim.QUEUES[QSim.QI[qid]].name; };
  var LANE_COLOR = { VIS: 'var(--c1)', CIT: 'var(--c2)', RES: 'var(--c3)', EG: 'var(--c4)', CRW: 'var(--c6)' };
  var ZONE_COLOR = { 'CI-A': 'var(--c2)', 'CI-B': 'var(--c3)', 'CI-C': 'var(--c1)', 'CI-D': 'var(--c4)', 'SEC-N': 'var(--c1)', 'SEC-S': 'var(--c2)' };
  QUI.color = function (qid) { var d = QSim.QUEUES[QSim.QI[qid]]; return d.lane ? LANE_COLOR[d.lane] : ZONE_COLOR[qid] || 'var(--c6)'; };

  var ICONS = {
    ops: '<path d="M3 12h4l3-8 4 16 3-8h4"/>',
    imm: '<rect x="5" y="3" width="14" height="18" rx="2"/><circle cx="12" cy="10" r="3"/><path d="M9 16h6"/>',
    ci: '<rect x="3" y="7" width="18" height="13" rx="2"/><path d="M9 7V5a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2M3 12h18"/>',
    sla: '<path d="M7 3h7l5 5v13H7z"/><path d="M14 3v5h5"/><path d="M9.5 14l2 2 3.5-4"/>',
    fc: '<path d="M3 20h18"/><path d="M4 16l5-5 4 3 7-8"/><path d="M16 6h4v4"/>',
    zones: '<path d="M4 7l8-4 8 5-3 12H7z"/><circle cx="4" cy="7" r="1.6"/><circle cx="20" cy="8" r="1.6"/><circle cx="7" cy="20" r="1.6"/>',
    dev: '<circle cx="12" cy="12" r="2.5"/><path d="M6.3 6.3a8 8 0 0 0 0 11.4M17.7 6.3a8 8 0 0 1 0 11.4M8.8 8.8a4.5 4.5 0 0 0 0 6.4M15.2 8.8a4.5 4.5 0 0 1 0 6.4"/>',
    disp: '<rect x="3" y="4" width="18" height="12" rx="1.5"/><path d="M8 20h8M12 16v4"/>',
    rep: '<path d="M6 3h12v18H6z"/><path d="M9 8h6M9 12h6M9 16h4"/>',
    acc: '<path d="M12 3l8 3v6c0 4.5-3.4 8-8 9-4.6-1-8-4.5-8-9V6z"/><path d="M9.5 12l2 2 3.5-4"/>',
    bell: '<path d="M6 16V11a6 6 0 0 1 12 0v5l1.5 2h-15z"/><path d="M10 20a2 2 0 0 0 4 0"/>',
    plus: '<path d="M12 5v14M5 12h14"/>',
    close: '<path d="M6 6l12 12M18 6L6 18"/>',
    undo: '<path d="M9 14L4 9l5-5"/><path d="M4 9h10a6 6 0 0 1 0 12h-3"/>',
    lock: '<rect x="5" y="11" width="14" height="10" rx="2"/><path d="M8 11V8a4 4 0 0 1 8 0v3"/>',
    play: '<path d="M8 5l11 7-11 7z" fill="currentColor" stroke="none"/>',
    pause: '<path d="M7 5h3.5v14H7zM13.5 5H17v14h-3.5z" fill="currentColor" stroke="none"/>',
    info: '<circle cx="12" cy="12" r="9"/><path d="M12 11v6M12 7.5v.5"/>',
    menu: '<path d="M4 7h16M4 12h16M4 17h16"/>',
    sun: '<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/>',
    moon: '<path d="M20 14.5A8 8 0 1 1 9.5 4a6.5 6.5 0 0 0 10.5 10.5z"/>',
    back: '<path d="M15 5l-7 7 7 7"/>',
    why: '<circle cx="12" cy="12" r="9"/><path d="M9.5 9.5a2.5 2.5 0 1 1 3.5 2.3c-.7.3-1 .8-1 1.5v.7M12 17v.5"/>',
    warn: '<path d="M12 3l10 18H2z"/><path d="M12 10v5M12 18v.4"/>',
    good: '<circle cx="12" cy="12" r="9"/><path d="M8 12.5l2.7 2.7L16 10"/>',
    crit: '<path d="M8 2.5h8L21.5 8v8L16 21.5H8L2.5 16V8z"/><path d="M12 7.5v6M12 16.5v.4"/>',
    unknown: '<circle cx="12" cy="12" r="9" stroke-dasharray="3 2.5"/><path d="M9.5 9.5a2.5 2.5 0 1 1 3.5 2.3c-.7.3-1 .8-1 1.5v.4M12 17v.4"/>',
    deg: '<path d="M4 20L20 4M4 14L14 4M10 20L20 10"/><rect x="3" y="3" width="18" height="18" rx="2"/>',
    download: '<path d="M12 4v11M7 10l5 5 5-5M5 20h14"/>',
    copy: '<rect x="8" y="8" width="12" height="12" rx="2"/><path d="M16 8V5a1 1 0 0 0-1-1H5a1 1 0 0 0-1 1v10a1 1 0 0 0 1 1h3"/>',
    print: '<path d="M7 9V3h10v6"/><rect x="3" y="9" width="18" height="8" rx="1.5"/><path d="M7 14h10v7H7z"/>',
    reset: '<path d="M4 12a8 8 0 1 0 2.3-5.7"/><path d="M4 4v4h4"/>',
    check: '<path d="M5 12.5l4.5 4.5L19 7.5"/>',
    full: '<path d="M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5"/>'
  };
  QUI.icon = function (name, cls) {
    return '<svg class="' + (cls || '') + '" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false">' + (ICONS[name] || '') + '</svg>';
  };
  QUI.st = function (level, text) {
    var icon = level === 'good' ? 'good' : level === 'warn' ? 'warn' : level === 'crit' ? 'crit' : level === 'deg' ? 'deg' : 'unknown';
    var cls = level === 'deg' ? 'unknown' : level;
    return '<span class="st st-' + cls + '">' + QUI.icon(icon) + esc(text == null ? QUI.levelText[level] : text) + '</span>';
  };
  QUI.waitSt = function (s) {
    if (s.degraded && s.band) return QUI.st('deg', 'Degraded');
    var lv = QUI.waitLevel(s.nowcast);
    return QUI.st(lv);
  };
  QUI.waitText = function (s) { return s.degraded && s.band ? QUI.bandText(s.band) : QUI.fmtWait(s.nowcast); };

  QUI.denied = function (what) {
    return '<div class="denied" role="note">' + QUI.icon('lock') + '<div><strong>Not available for this role</strong><p>' +
      esc(what || '') + ' The ' + esc(QUI.roleName()) + ' role does not see this. Switch role in the top bar to compare views.</p></div></div>';
  };

  /* keeps focus on an element with the same data-key after replacing innerHTML */
  QUI.html = function (elm, html) {
    if (!elm) return;
    if (elm.__html === html) return;
    var ae = document.activeElement, key = null;
    if (ae && elm.contains(ae) && ae.getAttribute) key = ae.getAttribute('data-key');
    elm.innerHTML = html;
    elm.__html = html;
    if (QUI.stack) QUI.stack(elm);
    if (key) { var n = elm.querySelector('[data-key="' + key.replace(/"/g, '') + '"]'); if (n) n.focus(); }
  };

  /* zone profiles */

  function vnum(v) { return parseInt(String(v).slice(1), 10) || 0; }
  QUI.profiles = function () {
    return st.profiles.slice().sort(function (a, b) { return a.publishedAt - b.publishedAt || vnum(a.version) - vnum(b.version); });
  };
  function profileObjAt(m) {
    var ps = QUI.profiles(), cur = null;
    ps.forEach(function (p) { if (p.publishedAt <= m) cur = p; });
    return cur;
  }
  QUI.profileAt = function (m) { var p = profileObjAt(m); return p ? p.version : 'v12'; };
  QUI.geometryAt = function (m) { var p = profileObjAt(m); return p ? p.geometry : QFloor.V12; };
  QUI.publishedVersions = function () { return ['v12'].concat(QUI.profiles().map(function (p) { return p.version; })); };
  QUI.nextVersion = function () {
    var n = 13;
    st.profiles.forEach(function (p) { n = Math.max(n, vnum(p.version)); });
    return 'v' + (n + 1);
  };
  QUI.publishProfile = function (geometry, at, changes) {
    var v = QUI.nextVersion();
    st.profiles.push({ version: v, publishedAt: at, geometry: QFloor.clone(geometry), by: QUI.roleName(), changes: changes || [] });
    sset('profiles', st.profiles);
    QUI.audit('Published', v, 'Zone profile ' + v + ' published' + (changes && changes.length ? '; changed ' + changes.join(', ') : ''));
    capCache = {};
    if (QUI.day) QUI.day.setCaps(capsFn);
    QUI.refreshRules(true);
    return v;
  };
  QUI.unpublishProfile = function (version) {
    var v = version || (QUI.profiles().slice(-1)[0] || {}).version;
    st.profiles = st.profiles.filter(function (p) { return p.version !== v; });
    sset('profiles', st.profiles);
    QUI.audit('Withdrew', v, 'Zone profile ' + v + ' withdrawn');
    capCache = {};
    if (QUI.day) QUI.day.setCaps(capsFn);
    QUI.refreshRules(true);
  };

  /* simulation */

  var capCache = {};
  function capsFn(qid, m) {
    var v = QUI.profileAt(m), key = v + '|' + qid;
    if (!(key in capCache)) capCache[key] = QFloor.snakeCapacity(qid, QUI.geometryAt(m));
    return capCache[key];
  }
  QUI.allRules = function () {
    return QSim.SEED_RULES.map(function (r) {
      var x = {};
      for (var k in r) x[k] = r[k];
      if (st.c.ruleState[r.id] && st.c.ruleState[r.id].enabled === false) x.enabled = false;
      return x;
    }).concat(st.c.rules);
  };
  QUI.allAllocations = function () { return QSim.SEED_ALLOCATIONS.concat(st.c.allocations); };
  function runDay() {
    capCache = {};
    QUI.dayVersion = (QUI.dayVersion || 0) + 1;
    QUI.day = QSim.run({ seed: st.seed, accepted: st.accepted, flights: st.c.flights, overrides: st.c.overrides,
      allocations: QUI.allAllocations(), rules: QUI.allRules(), caps: capsFn });
  }
  QUI.rerun = function () { runDay(); QUI.refresh(true); };
  QUI.refreshRules = function (rebuild) { if (QUI.day) QUI.day.setRules(QUI.allRules()); QUI.refresh(rebuild !== false); };
  QUI.saveC = function () { sset('created', st.c); };
  QUI.audit = function (action, id, summary) {
    st.c.audit.push({ date: QSim.DATE, t: QUI.now(), role: QUI.roleName(), action: action, id: id, summary: summary || '' });
    QUI.saveC();
  };
  QUI.accept = function (entry) {
    st.accepted = st.accepted.filter(function (a) { return a.area !== entry.area; });
    st.accepted.push(entry);
    sset('accepted', st.accepted);
    QUI.audit('Accepted recommendation', entry.area, QSim.AREAS[entry.area].name + ', ' + QSim.clock(entry.from) + ' to ' + QSim.clock(entry.until));
    runDay();
    QUI.refresh(true);
  };
  QUI.unaccept = function (area) {
    st.accepted = st.accepted.filter(function (a) { return a.area !== area; });
    sset('accepted', st.accepted);
    QUI.audit('Removed recommendation', area, QSim.AREAS[area].name);
    runDay();
    QUI.refresh(true);
  };

  /* floor plan context */

  QUI.fpContext = function (now) {
    var day = QUI.day, geom = QUI.geometryAt(now), prof = QUI.profileAt(now);
    var srvCache = {};
    function srvState(sid) {
      if (!srvCache.__built) {
        QSim.QUEUES.forEach(function (d, q) { day.servers(q, now).forEach(function (s) { srvCache[s.id] = { q: q, st: s }; }); });
        srvCache.__built = true;
      }
      return srvCache[sid];
    }
    var stCache = {};
    function qs(id) { if (!stCache[id]) stCache[id] = day.state(QSim.QI[id], now); return stCache[id]; }
    var ctx = {
      geometry: geom,
      regionVis: QUI.regionVis,
      zoneInfo: function (id) {
        var cz = geom.zones[id];
        if (cz && cz.meta) {
          var cov = QUI.coveringSensor(id);
          if (!cov) return { cls: 'z-neutral', name: cz.meta.name, wait: 'Not measured', sub: 'no calibrated sensor', aria: cz.meta.name + ': not measured, no calibrated sensor' };
          if (cz.meta.linked && QSim.QI[cz.meta.linked] != null) {
            var ls = qs(cz.meta.linked), llv = QUI.waitLevel(ls.nowcast);
            return { cls: ls.noService ? 'z-ns' : 'z-' + llv, name: cz.meta.name, wait: QUI.waitText(ls), sub: 'measured by ' + cov.id, aria: cz.meta.name + ': ' + QUI.waitText(ls) + ', measured by ' + cov.id };
          }
          return { cls: 'z-good', name: cz.meta.name, wait: 'Measured', sub: 'by ' + cov.id, aria: cz.meta.name + ': measured by ' + cov.id };
        }
        var s = qs(id), name = QUI.shortName(id);
        if (s.degraded && s.band) return { cls: 'z-ns', hatch: true, name: name, wait: QUI.bandText(s.band), sub: 'Degraded, sensor offline', aria: QUI.qname(id) + ': degraded, estimated ' + QUI.bandText(s.band) };
        if (s.noService) return { cls: 'z-ns', name: name, wait: 'No service', sub: Math.round(s.len) + ' queuing', aria: QUI.qname(id) + ': no service' };
        var lv = QUI.waitLevel(s.nowcast);
        return { cls: 'z-' + lv, name: name, wait: QUI.fmtWait(s.nowcast), sub: Math.round(s.len) + ' queuing', aria: QUI.qname(id) + ': ' + QUI.fmtWait(s.nowcast) + ', ' + QUI.levelText[lv] + ', ' + Math.round(s.len) + ' queuing' };
      },
      zoneTip: function (id) {
        var cz = geom.zones[id];
        if (cz && cz.meta) {
          var cov = QUI.coveringSensor(id);
          return { title: cz.meta.name + ' (' + id + ')', rows: [['Type', ZONE_TYPES[cz.meta.type] || cz.meta.type], ['Linked queue', cz.meta.linked ? QUI.qname(cz.meta.linked) : 'None'],
            ['Measurement', cov ? 'Calibrated sensor ' + cov.id : 'Not measured: no calibrated sensor'], ['Zone profile', prof]] };
        }
        var s = qs(id), def = QSim.QUEUES[QSim.QI[id]], cap = QFloor.snakeCapacity(id, geom);
        var rows = [['Wait now (nowcast)', QUI.waitText(s)], ['Status', s.degraded ? 'Degraded' : QUI.levelText[QUI.waitLevel(s.nowcast)]], ['Queuing', QUI.fmt(s.len)]];
        if (QUI.queueVis(id) === 'full' && !(def.group === 'imm' && !QUI.can('imm.desks'))) {
          rows.push(['Throughput', s.rate > 0 ? QUI.fmt(s.rate, 1) + ' per min' : 'None']);
          rows.push([def.unit.charAt(0).toUpperCase() + def.unit.slice(1) + ' open', s.open + ' of ' + def.servers.length]);
        }
        if (cap) rows.push(['Snake capacity', QUI.fmt(cap) + ' people']);
        rows.push(['Zone profile', prof]);
        return { title: def.name, rows: rows, note: s.degraded ? 'A sensor over this zone is offline; the wait is shown as a band.' : null };
      },
      hallInfo: function (lv) {
        var hall = QFloor.HALLS[lv], worst = null, wq = null;
        hall.queues.forEach(function (id) { var s = qs(id); if (s.nowcast != null && (worst == null || s.nowcast > worst)) { worst = s.nowcast; wq = id; } });
        var anyDeg = hall.queues.some(function (id) { return qs(id).degraded; });
        var lvv = QUI.waitLevel(worst);
        return { cls: 'z-' + (worst == null ? 'ns' : lvv), wait: QUI.fmtWait(worst), sub: 'Longest lane: ' + (wq ? QUI.shortName(wq) : 'none') + (anyDeg ? ', one lane degraded' : ''),
          aria: hall.name + ': longest lane ' + QUI.fmtWait(worst) + ' (aggregate)' };
      },
      hallTip: function (lv) {
        var hall = QFloor.HALLS[lv];
        return { title: hall.name + ' (aggregate)', rows: hall.queues.map(function (id) { return [QUI.shortName(id), QUI.waitText(qs(id))]; }).concat([['Zone profile', prof]]),
          note: 'Lane-level waits only. Desk states and per-desk data stay in the border deployment.' };
      },
      serverInfo: function (sid) {
        var x = srvState(sid);
        if (!x) return null;
        return { state: x.st.state, aria: sid + ': ' + stateText(x.st.state) };
      },
      serverTip: function (sid) {
        var x = srvState(sid);
        if (!x) return null;
        var def = QSim.QUEUES[x.q];
        var rows = [['State', stateText(x.st.state)], ['Assigned to', def.label]];
        if (def.group === 'imm' || def.group === 'ci') {
          var bs = Math.floor(now / 15) * 15;
          var iv = day.serverInterval(x.q, bs, now)[x.st.k];
          rows.push(['Passengers this interval', QUI.fmt(iv.pax)]);
          rows.push(['Mean service time', iv.svc ? QUI.fmt(iv.svc) + ' s' : 'n/a']);
        }
        return { title: (def.group === 'imm' ? 'Desk ' : def.group === 'egate' ? 'E-gate ' : def.group === 'sec' ? 'Security lane ' : 'Counter ') + sid,
          rows: rows, note: def.group === 'imm' ? 'Desk-level interval aggregates. No officer identity.' : null };
      },
      overflowOn: function (id, o) {
        return o.feeds.some(function (qid) { var cap = QFloor.snakeCapacity(qid, geom); return cap && qs(qid).len > cap; });
      }
    };
    return ctx;
  };

  var GLYPH = {
    serving: '<rect x="2" y="2" width="12" height="12" rx="1.5" style="fill:var(--accent);stroke:var(--accent)"/><rect x="5.5" y="5.5" width="5" height="5" style="fill:var(--accent-ink)"/>',
    idle: '<rect x="2" y="2" width="12" height="12" rx="1.5" style="fill:none;stroke:var(--accent)"/><circle cx="8" cy="8" r="2" style="fill:var(--accent)"/>',
    paused: '<rect x="2" y="2" width="12" height="12" rx="1.5" style="fill:var(--warn);fill-opacity:.25;stroke:var(--warn)"/><rect x="5" y="4.5" width="2" height="7" style="fill:var(--warn)"/><rect x="9" y="4.5" width="2" height="7" style="fill:var(--warn)"/>',
    closed: '<rect x="2" y="2" width="12" height="12" rx="1.5" style="fill:none;stroke:var(--muted);stroke-dasharray:2 2"/>',
    unknown: '<rect x="2" y="2" width="12" height="12" rx="1.5" style="fill:var(--unknown);fill-opacity:.25;stroke:var(--unknown)"/><text x="8" y="11.5" text-anchor="middle" style="font-size:9px;font-weight:700;fill:var(--ink)">?</text>',
    oos: '<rect x="2" y="2" width="12" height="12" rx="1.5" style="fill:var(--crit);fill-opacity:.15;stroke:var(--crit)"/><path d="M5 5l6 6m0-6l-6 6" style="stroke:var(--crit);stroke-width:1.5"/>'
  };
  QUI.glyph = function (state) { return '<svg viewBox="0 0 16 16" aria-hidden="true" focusable="false">' + (GLYPH[state] || '') + '</svg>'; };
  QUI.stateChip = function (state) { return '<span class="st">' + QUI.glyph(state) + esc(stateText(state)) + '</span>'; };
  QUI.stateLegend = function (states) {
    return '<div class="fp-legend">' + (states || ['serving', 'idle', 'paused', 'closed', 'unknown']).map(function (x) { return '<span>' + QUI.glyph(x) + esc(stateText(x)) + '</span>'; }).join('') + '</div>';
  };

  var ZONE_TYPES = { snake: 'Snake queue', area: 'Service area', overflow: 'Overflow band', line: 'Count line' };
  QUI.ZONE_TYPES = ZONE_TYPES;
  QUI.coveringSensor = function (zoneId) {
    return st.c.sensors.filter(function (x) { return x.zone === zoneId && x.status === 'online'; })[0] || null;
  };

  function stateText(s) { return { serving: 'Serving', idle: 'Idle', paused: 'Paused', closed: 'Closed', unknown: 'Unknown', oos: 'Out of service' }[s] || s; }
  QUI.stateText = stateText;

  QUI.zoneSummary = function (now) {
    var rows = [];
    QSim.QUEUES.forEach(function (d) {
      var v = QUI.queueVis(d.id);
      if (v === 'hidden') return;
      if (v === 'agg' && d.lane === 'CRW') return;
      var s = QUI.day.state(d.index, now);
      rows.push('<tr><th scope="row">' + esc(d.name) + (v === 'agg' ? ' <span class="muted small">(aggregate)</span>' : '') + '</th><td class="num">' + esc(QUI.waitText(s)) + '</td><td class="num">' + QUI.fmt(s.len) + '</td><td>' + QUI.waitSt(s) + '</td></tr>');
    });
    return '<details class="small" style="margin-block-start:8px"><summary>Text summary of the floor plan (zones visible to this role)</summary><div class="table-wrap"><table class="tbl"><thead><tr><th scope="col">Zone</th><th scope="col" class="num">Wait</th><th scope="col" class="num">Queuing</th><th scope="col">Status</th></tr></thead><tbody>' + rows.join('') + '</tbody></table></div></details>';
  };

  /* alerts */

  QUI.alertsAt = function (now) {
    var out = [];
    QUI.day.alertsAll.forEach(function (a) {
      if (a.raisedAt > now || !QUI.can(a.perm)) return;
      var cleared = a.clearedAt != null && a.clearedAt <= now;
      var ack = st.acks[a.id];
      if (ack && !(ack.at <= now && ack.at >= a.raisedAt)) ack = null;
      var escAt = a.raisedAt + a.escalateAfter;
      var status = cleared ? 'cleared' : ack ? 'acknowledged' : now >= escAt ? 'escalated' : 'active';
      var x = {};
      for (var k in a) x[k] = a[k];
      x.status = status; x.ack = ack; x.escAt = escAt; x.remaining = escAt - now;
      out.push(x);
    });
    return out;
  };
  QUI.canAck = function (a) { return st.role === 'demo' || ROLE_BY_ID[st.role].name === a.owner; };
  QUI.ack = function (id) {
    st.acks[id] = { at: QUI.now(), role: QUI.roleName() };
    sset('acks', st.acks);
    var al = (QUI.day.alertsAll || []).filter(function (a) { return a.id === id; })[0];
    QUI.audit('Acknowledged', al ? al.ruleId : id, al ? al.rule + ', ' + al.zone + ' (raised ' + QSim.clock(al.raisedAt) + ')' : id);
    QUI.refresh(false);
  };

  /* files: real download plus an in-page copy panel */

  QUI.offerFile = function (container, opt) {
    var started = false;
    try {
      var blob = new Blob([opt.text], { type: opt.mime });
      var url = URL.createObjectURL(blob);
      var a = document.createElement('a');
      a.href = url; a.download = opt.filename; a.style.display = 'none';
      document.body.appendChild(a);
      a.click();
      started = true;
      setTimeout(function () { try { URL.revokeObjectURL(url); a.remove(); } catch (e) { } }, 5000);
    } catch (e) { started = false; }
    var id = 'fp-' + Math.floor(Math.random() * 1e9).toString(36);
    container.innerHTML = '<div class="filepanel" role="region" aria-label="' + esc(opt.title) + '">' +
      '<div class="filepanel-head"><div><strong>' + esc(opt.title) + '</strong><div class="small muted">' + esc(opt.filename) + ', ' + QUI.fmt(opt.text.length) + ' characters. ' +
      (started ? 'A download was started. ' : '') + 'If your viewer blocks downloads, copy the content below and save it as ' + esc(opt.filename) + '.</div></div>' +
      '<div class="row"><button type="button" class="btn" data-copy>' + QUI.icon('copy') + 'Copy</button><span class="small muted" data-copy-status aria-live="polite"></span></div></div>' +
      '<label class="sr-only" for="' + id + '">' + esc(opt.title) + ' content</label><textarea id="' + id + '" readonly spellcheck="false"></textarea>' +
      (opt.note ? '<div class="small muted" style="margin-block-start:6px">' + esc(opt.note) + '</div>' : '') + '</div>';
    var ta = container.querySelector('textarea');
    ta.value = opt.text;
    var stEl = container.querySelector('[data-copy-status]');
    container.querySelector('[data-copy]').addEventListener('click', function () {
      function fallback() { ta.focus(); ta.select(); stEl.textContent = 'Text selected. Press Ctrl+C or Cmd+C to copy.'; }
      try {
        if (navigator.clipboard && navigator.clipboard.writeText) {
          navigator.clipboard.writeText(opt.text).then(function () { stEl.textContent = 'Copied to the clipboard.'; }, fallback);
        } else fallback();
      } catch (e) { fallback(); }
    });
    return started;
  };

  /* shell */

  function buildSidebar() {
    var nav = document.getElementById('sidebar');
    if (!nav) return;
    var cur = document.body.getAttribute('data-screen');
    var items = NAV.map(function (n) {
      var acc = QUI.screenAccess(n.id);
      return '<li><a href="' + n.href + '"' + (n.id === cur ? ' aria-current="page"' : '') + (acc === 'none' ? ' class="locked"' : '') + ' title="' + esc(n.label) + '">' +
        QUI.icon(n.icon, 'nav-ico') + '<span class="nav-label">' + esc(n.label) + '</span>' +
        (acc === 'none' ? QUI.icon('lock', 'nav-lock') + '<span class="sr-only"> (not available for this role)</span>' : '') + '</a></li>';
    }).join('');
    nav.innerHTML = '<div class="brand"><span class="brand-mark">' + QUI.icon('ops') + '</span><span class="brand-text"><strong>DMO QMS</strong><span>Queue management prototype</span></span></div>' +
      '<ul class="nav">' + items + '</ul>' +
      '<div class="side-foot">Demo International Airport is fictional. All data on these screens is synthetic.</div>';
  }

  function buildTopbar() {
    var tb = document.getElementById('topbar');
    if (!tb) return;
    var roleOpts = ROLES.map(function (r) { return '<option value="' + r.id + '"' + (r.id === st.role ? ' selected' : '') + '>' + esc(r.name) + '</option>'; }).join('');
    tb.innerHTML =
      '<div class="tb-row">' +
        '<button type="button" class="btn menu-btn" id="menuBtn" aria-controls="sidebar" aria-expanded="false">' + QUI.icon('menu') + '<span>Menu</span></button>' +
        '<label class="field"><span class="sr-only">Airport and terminal</span><select id="airportSel"><option>DMO, Demo International Airport, Terminal 1</option></select></label>' +
        '<span class="badge-proto" title="Everything on these screens is generated by a simulation.">' + QUI.icon('warn') + 'Prototype, synthetic data</span>' +
        '<span class="tb-spacer"></span>' +
        '<label class="field" for="roleSel">Role <select id="roleSel">' + roleOpts + '</select></label>' +
        '<button type="button" class="btn" id="themeBtn" aria-pressed="' + (st.theme === 'light') + '"></button>' +
        '<a class="back-link" href="../index.html">' + '&larr; Back to business plan</a>' +
      '</div>' +
      '<div class="tb-row" role="group" aria-label="Simulation controls">' +
        '<span class="clock"><span class="clock-time" id="clockTime" aria-live="off">17:40</span><span class="clock-date">Mon 28 Sep 2026, simulated</span></span>' +
        '<button type="button" class="btn btn-play" id="playBtn" aria-pressed="false"></button>' +
        '<span class="seg" role="group" aria-label="Simulation speed">' + [1, 60, 600].map(function (s) {
          return '<button type="button" data-speed="' + s + '" aria-pressed="' + (s === st.speed) + '">' + s + 'x</button>';
        }).join('') + '</span>' +
        '<span class="jumps" role="group" aria-label="Jump to">' +
          '<button type="button" class="btn" data-jump="435" title="Jump to 07:15">Morning peak</button>' +
          '<button type="button" class="btn" data-jump="1060" title="Jump to 17:40, just before the evening wave">Evening peak</button>' +
          '<button type="button" class="btn" data-jump="150" title="Jump to 02:30">Night bank</button>' +
        '</span>' +
        '<span class="row" style="gap:6px"><label class="field" for="seedIn">Seed <input id="seedIn" type="number" min="1" max="999999" inputmode="numeric" value="' + st.seed + '"></label>' +
          '<button type="button" class="btn" id="seedBtn">Apply</button></span>' +
        '<button type="button" class="btn" id="aboutBtn">' + QUI.icon('info') + 'About this demo</button>' +
        '<span id="resetWrap"><button type="button" class="btn btn-ghost" id="resetBtn">' + QUI.icon('reset') + 'Reset demo</button></span>' +
        '<span class="small muted" id="simStatus" aria-live="polite"></span>' +
      '</div>';
    syncTopbar();
    document.getElementById('playBtn').addEventListener('click', function () {
      st.playing = !st.playing; sset('playing', st.playing); lastT = performance.now(); syncTopbar();
    });
    tb.querySelectorAll('[data-speed]').forEach(function (b) {
      b.addEventListener('click', function () { st.speed = Number(b.getAttribute('data-speed')); sset('speed', st.speed); syncTopbar(); });
    });
    tb.querySelectorAll('[data-jump]').forEach(function (b) {
      b.addEventListener('click', function () { st.clock = Number(b.getAttribute('data-jump')); sset('clock', st.clock); draw(true); });
    });
    document.getElementById('seedBtn').addEventListener('click', applySeed);
    document.getElementById('seedIn').addEventListener('keydown', function (e) { if (e.key === 'Enter') { e.preventDefault(); applySeed(); } });
    document.getElementById('roleSel').addEventListener('change', function (e) { QUI.setRole(e.target.value); });
    document.getElementById('themeBtn').addEventListener('click', function () {
      st.theme = st.theme === 'light' ? 'dark' : 'light';
      sset('theme', st.theme);
      if (st.theme === 'light') document.documentElement.setAttribute('data-theme', 'light');
      else document.documentElement.removeAttribute('data-theme');
      syncTopbar();
    });
    document.getElementById('aboutBtn').addEventListener('click', openAbout);
    document.getElementById('resetBtn').addEventListener('click', askReset);
    var menu = document.getElementById('menuBtn');
    menu.addEventListener('click', function () { toggleMenu(); });
  }

  function toggleMenu(force) {
    var sb = document.getElementById('sidebar'), menu = document.getElementById('menuBtn');
    var open = force != null ? force : !sb.classList.contains('open');
    sb.classList.toggle('open', open);
    menu.setAttribute('aria-expanded', String(open));
    var scrim = document.getElementById('scrim');
    if (open && !scrim) {
      scrim = document.createElement('div');
      scrim.id = 'scrim'; scrim.className = 'scrim';
      scrim.addEventListener('click', function () { toggleMenu(false); });
      document.body.appendChild(scrim);
      var first = sb.querySelector('a'); if (first) first.focus();
    } else if (!open && scrim) { scrim.remove(); menu.focus(); }
  }
  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape') { var sb = document.getElementById('sidebar'); if (sb && sb.classList.contains('open')) toggleMenu(false); }
  });

  function askReset() {
    var w = document.getElementById('resetWrap');
    w.innerHTML = '<span class="confirm-inline" role="group" aria-label="Confirm reset">Reset clock, speed, seed, role, zone profiles, accepted staffing, acknowledgements, disputes and everything created in the demo? ' +
      '<button type="button" class="btn btn-danger" id="resetYes">Reset</button><button type="button" class="btn" id="resetNo">Cancel</button></span>';
    document.getElementById('resetYes').addEventListener('click', function () {
      sclear();
      var theme = st.theme;
      var fresh = loadState();
      for (var k in fresh) st[k] = fresh[k];
      st.theme = theme;
      restoreReset();
      var rs = document.getElementById('roleSel'); if (rs) rs.value = st.role;
      var si = document.getElementById('seedIn'); if (si) si.value = st.seed;
      runDay();
      buildSidebar();
      syncTopbar();
      QUI.refresh(true);
      status('Demo reset to 17:40, paused, seed ' + st.seed + '.');
    });
    document.getElementById('resetNo').addEventListener('click', restoreReset);
    document.getElementById('resetYes').focus();
  }
  function restoreReset() {
    var w = document.getElementById('resetWrap');
    w.innerHTML = '<button type="button" class="btn btn-ghost" id="resetBtn">' + QUI.icon('reset') + 'Reset demo</button>';
    document.getElementById('resetBtn').addEventListener('click', askReset);
  }

  function status(t) { var e = document.getElementById('simStatus'); if (e) { e.textContent = t; clearTimeout(status.t); status.t = setTimeout(function () { e.textContent = ''; }, 6000); } }
  QUI.status = status;

  function applySeed() {
    var inp = document.getElementById('seedIn');
    var v = parseInt(inp.value, 10);
    if (!isFinite(v) || v < 1 || v > 999999) { status('Seed must be a whole number from 1 to 999,999.'); inp.value = st.seed; return; }
    if (v === st.seed) { status('Seed ' + v + ' is already running.'); return; }
    st.seed = v; sset('seed', v);
    runDay();
    QUI.refresh(true);
    status('Seed ' + v + (v === QSim.DEFAULT_SEED ? ' (default)' : '') + ': the whole day was recomputed.' + (v === QSim.DEFAULT_SEED ? '' : ' Scripted event times are guaranteed for seed ' + QSim.DEFAULT_SEED + ' only.'));
  }

  function syncTopbar() {
    var pb = document.getElementById('playBtn');
    if (pb) {
      pb.setAttribute('aria-pressed', String(st.playing));
      pb.innerHTML = st.playing ? QUI.icon('pause') + 'Pause' : QUI.icon('play') + 'Play';
    }
    document.querySelectorAll('[data-speed]').forEach(function (b) { b.setAttribute('aria-pressed', String(Number(b.getAttribute('data-speed')) === st.speed)); });
    var tbtn = document.getElementById('themeBtn');
    if (tbtn) {
      tbtn.setAttribute('aria-pressed', String(st.theme === 'light'));
      tbtn.innerHTML = st.theme === 'light' ? QUI.icon('moon') + '<span class="hide-narrow">Dark theme</span>' : QUI.icon('sun') + '<span class="hide-narrow">Light theme</span>';
      tbtn.setAttribute('aria-label', st.theme === 'light' ? 'Switch to dark theme' : 'Switch to light theme');
    }
  }

  QUI.setRole = function (r) {
    if (!ROLE_BY_ID[r]) return;
    st.role = r; sset('role', r);
    var rs = document.getElementById('roleSel'); if (rs && rs.value !== r) rs.value = r;
    buildSidebar();
    QUI.refresh(true);
    status('Role: ' + QUI.roleName() + '.');
  };

  /* About this demo */

  function openAbout() {
    var d = document.getElementById('aboutDlg');
    if (!d) {
      d = document.createElement('dialog');
      d.id = 'aboutDlg';
      d.className = 'about';
      d.setAttribute('aria-labelledby', 'aboutTitle');
      document.body.appendChild(d);
    }
    d.innerHTML = aboutHtml();
    d.querySelector('[data-close]').addEventListener('click', function () { d.close(); });
    try { d.showModal(); } catch (e) { d.setAttribute('open', ''); }
  }

  function aboutHtml() {
    var P = QSim.PARAMS, day = QUI.day, S = day.S;
    var paxA = S.arr.reduce(function (a, f) { return a + f.pax; }, 0), paxD = S.dep.reduce(function (a, f) { return a + f.pax; }, 0);
    function row(k, v) { return '<tr><th scope="row">' + esc(k) + '</th><td>' + esc(v) + '</td></tr>'; }
    return '<div class="about-head"><h2 id="aboutTitle">About this demo</h2><button type="button" class="btn" data-close>Close</button></div>' +
      '<div class="about-body">' +
      '<p>This is a front-end prototype of the Dalil Tech Airport Queue Management System running on synthetic data. Demo International Airport (DMO) is fictional and sized like the business plan\'s reference airport (about 8 million passengers a year). Airline codes, handlers and people are invented. Nothing here is measured.</p>' +
      '<section><h3>Airport</h3><div class="table-wrap"><table class="tbl"><tbody>' +
        row('Check-in', '48 counters in 4 islands of 12: Handler A on islands A and B, Handler B on islands C and D') +
        row('Security', '2 checkpoints (North, South), 5 lanes each, about 180 passengers per lane per hour') +
        row('Departure immigration', '22 desks and 4 e-gates') +
        row('Arrival immigration', '22 desks and 6 e-gates; baggage reclaim shown as a zone only') +
        row('Desk lanes', 'Crew and diplomats 1 desk, Citizens 3, Residents 3, Visitors up to 15') +
        row('Sensors', '59 (stereo over immigration and security, LiDAR over check-in)') +
      '</tbody></table></div></section>' +
      '<section><h3>Traffic (seed ' + day.seed + ')</h3><div class="table-wrap"><table class="tbl"><tbody>' +
        row('Arriving', QUI.fmt(paxA) + ' passengers on ' + S.arr.length + ' flights') +
        row('Departing', QUI.fmt(paxD) + ' passengers on ' + S.dep.length + ' flights') +
        row('Banks', 'Night 01:00 to 04:00, morning 06:00 to 09:00, evening 18:00 to 22:00, plus a few daytime flights') +
        row('Flights', 'Fictional codes (DM, XR, QL), 120 to 300 seats, load factors 70 to 95%') +
        row('Passenger mix', 'Citizens 35%, Residents 20%, Visitors 35%, Crew and diplomats 2%, transfer 8% (stay airside). Per flight the mix varies by carrier.') +
        row('E-gate eligible', '40% of Citizens and Residents') +
        row('Arrivals timing', 'Passengers reach the hall ' + P.walkMin + ' to ' + P.walkMax + ' min after on-block, spread over about ' + P.hallSpread + ' min') +
        row('Departures timing', 'Passengers show up from 3 hours to 45 min before departure on a smooth curve') +
        row('Online check-in', Math.round(P.online * 100) + '% of departing passengers check in online and go straight to security') +
        row('Randomness', 'Per-minute arrival noise, short desk pauses, rare unknown desk states, per-desk speed differences, all seeded') +
      '</tbody></table></div></section>' +
      '<section><h3>Service times (synthetic)</h3><div class="table-wrap"><table class="tbl"><tbody>' +
        row('Citizens', P.svc.CIT + ' s') + row('Residents', P.svc.RES + ' s') + row('Visitors', P.svc.VIS + ' s') + row('Crew and diplomats', P.svc.CRW + ' s') +
        row('E-gate cycle', P.svc.EG + ' s, 7% rejects join the Visitors lane') + row('Check-in', P.svc.CI + ' s per passenger') + row('Security', '180 passengers per lane per hour') +
      '</tbody></table></div></section>' +
      '<section><h3>Model and rules</h3><div class="table-wrap"><table class="tbl"><tbody>' +
        row('Queue model', 'Per-minute backlog recursion: next = max(0, backlog + arrivals minus capacity); capacity = staffed desks x 60 / service seconds') +
        row('Realised wait', 'From cumulative arrival and departure curves (first in, first out); provisional until everyone who entered in the interval has exited') +
        row('Nowcast', '(queue length + 1) / current throughput; "No service" when no desk is serving') +
        row('SLA KPI', '15 minutes at P90 per 15-minute bin, arrival-weighted, evaluated monthly') +
        row('Alerts', 'Every alert comes from a rule on the Alert rules screen. R-001: nowcast above 15 min with at least 10 people queuing, clearing below 12 min. New rules are backtested on the day before they are created') +
        row('Forecast', '40 Monte Carlo runs from the current state: on-block times plus or minus 6 min, loads plus or minus 4%, service times plus or minus 8%') +
        row('Recommendation', 'Fewest desks or counters per 15 minutes keeping the expected wait under 10 min and utilisation under 85%, staffing ahead of waves, then checked against the Monte Carlo P90 and raised wherever it passes 13 min') +
        row('Zone profiles', 'Editing zones changes geometry, snake capacity, overflow and the version shown on results. The synthetic counts do not depend on geometry.') +
      '</tbody></table></div></section>' +
      '<section><h3>Scripted evening (default seed ' + QSim.DEFAULT_SEED + ')</h3><ul class="small">' +
        '<li>18:05: a visitor-heavy wave (flights on-block 17:48 to 17:56) pushes the arrivals Visitors nowcast above 15 min; an alert fires.</li>' +
        '<li>18:20 to 18:30: sensor S-17 over the arrivals hall is offline; the Visitors zone is degraded and shows a band.</li>' +
        '<li>19:10: Handler B\'s island C breaches its SLA after a shift change leaves 5 of 12 counters open; bins 19:00, 19:15 and 19:30 breach.</li>' +
      '</ul><p class="small muted">These come from the schedule and the staffing plan, not from the screens. Another seed moves them by a few minutes or changes them. Accepting a staffing recommendation before they happen can prevent them.</p></section>' +
      '</div>';
  }

  /* lifecycle */

  var screen = null, lastT = 0, lastDrawn = -1, lastDrawT = 0, lastSaved = 0;

  function draw(force) {
    var m = QUI.now();
    lastDrawn = m; lastDrawT = performance.now();
    var ct = document.getElementById('clockTime');
    if (ct) ct.textContent = QSim.clock(m);
    document.querySelectorAll('[data-profile-tag]').forEach(function (e) {
      var v = QUI.profileAt(m);
      var t = 'Zone profile <strong>' + v + '</strong>';
      if (e.__t !== t) { e.innerHTML = t; e.__t = t; }
    });
    if (screen && screen.update) {
      try { screen.update(m, !!force); } catch (e) { if (global.console) console.error(e); }
    }
    if (performance.now() - lastSaved > 500) { sset('clock', st.clock); lastSaved = performance.now(); }
  }
  QUI.draw = draw;

  QUI.refresh = function (rebuild) {
    if (rebuild && screen && screen.build) {
      try { screen.build(QUI); } catch (e) { if (global.console) console.error(e); }
    }
    draw(true);
  };

  function tick() {
    var t = performance.now();
    if (st.playing) {
      st.clock += (t - lastT) / 1000 * st.speed / 60;
      if (st.clock >= 1440) st.clock -= 1440;
    }
    lastT = t;
    var m = Math.floor(st.clock);
    if (m !== lastDrawn && t - lastDrawT >= 250) draw(false);
  }

  QUI.start = function (def) {
    screen = def || {};
    buildSidebar();
    buildTopbar();
    runDay();
    try { if (screen.build) screen.build(QUI); } catch (e) { if (global.console) console.error(e); }
    draw(true);
    if (location.hash && /^#[A-Za-z0-9_-]+$/.test(location.hash)) {
      var target = document.getElementById(location.hash.slice(1));
      if (target && target.scrollIntoView) requestAnimationFrame(function () { target.scrollIntoView(); });
    }
    lastT = performance.now();
    setInterval(tick, 100);
    global.addEventListener('pagehide', function () { sset('clock', st.clock); });
    document.addEventListener('visibilitychange', function () { if (document.visibilityState === 'hidden') sset('clock', st.clock); lastT = performance.now(); });
  };

  /* chart axis helpers shared by screens */
  QUI.timeTicks = function (from, to, step) {
    var out = [], s = Math.ceil(from / step) * step;
    for (var t = s; t <= to; t += step) out.push({ v: t, label: QSim.clock(t) });
    return out;
  };

  QUI.kpi = function (o) {
    return '<div class="kpi' + (o.hero ? ' hero' : '') + (o.level && o.hero ? ' ' + o.level : '') + '"><div class="kpi-label">' + esc(o.label) + '</div>' +
      '<div class="kpi-value' + (o.hero && String(o.value).replace(/<[^>]+>/g, '').length > 8 ? ' long' : '') + '">' + o.value + '</div>' + (o.sub ? '<div class="kpi-sub">' + o.sub + '</div>' : '') + '</div>';
  };

  global.QUI = QUI;
})(window);
