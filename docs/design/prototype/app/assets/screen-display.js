(function () {
  'use strict';
  var U = QUI, S = QSim, FF = U.F;
  var dspId = 'DSP-01', stale = false;
  var BANDS5 = [
    { en: 'Under 5 min', ar: 'أقل من 5 دقائق', cls: 'b0' },
    { en: '5 to 10 min', ar: '5 إلى 10 دقائق', cls: 'b1' },
    { en: '10 to 15 min', ar: '10 إلى 15 دقيقة', cls: 'b2' },
    { en: '15 to 20 min', ar: '15 إلى 20 دقيقة', cls: 'b3' },
    { en: '20 to 30 min', ar: '20 إلى 30 دقيقة', cls: 'b4' },
    { en: 'Over 30 min', ar: 'أكثر من 30 دقيقة', cls: 'b5' }
  ];
  var BANDS10 = [
    { en: 'Under 10 min', ar: 'أقل من 10 دقائق', cls: 'b1' },
    { en: '10 to 20 min', ar: '10 إلى 20 دقيقة', cls: 'b2' },
    { en: '20 to 30 min', ar: '20 إلى 30 دقيقة', cls: 'b4' },
    { en: 'Over 30 min', ar: 'أكثر من 30 دقيقة', cls: 'b5' }
  ];
  var NONE = { en: 'No estimate', ar: 'لا يوجد تقدير' };
  var CP = {
    'A-VIS': { en: 'Passport control: Visitors', ar: 'مراقبة الجوازات: الزوار', q: ['A-VIS'], level: 'arr' },
    'A-RES': { en: 'Passport control: Residents', ar: 'مراقبة الجوازات: المقيمون', q: ['A-RES'], level: 'arr' },
    'A-CIT': { en: 'Passport control: Citizens', ar: 'مراقبة الجوازات: المواطنون', q: ['A-CIT'], level: 'arr' },
    'A-EG': { en: 'E-gates', ar: 'البوابات الإلكترونية', q: ['A-EG'], level: 'arr' },
    'SEC-N': { en: 'Security North', ar: 'التفتيش الأمني الشمالي', q: ['SEC-N'], level: 'dep' },
    'SEC-S': { en: 'Security South', ar: 'التفتيش الأمني الجنوبي', q: ['SEC-S'], level: 'dep' },
    'D-PASS': { en: 'Passport control', ar: 'مراقبة الجوازات', q: ['D-CIT', 'D-RES', 'D-VIS'], level: 'dep' },
    'D-EG': { en: 'E-gates', ar: 'البوابات الإلكترونية', q: ['D-EG'], level: 'dep' },
    'CI-A': { en: 'Check-in, island A', ar: 'تسجيل الوصول: الجزيرة A', q: ['CI-A'], level: 'dep' },
    'CI-B': { en: 'Check-in, island B', ar: 'تسجيل الوصول: الجزيرة B', q: ['CI-B'], level: 'dep' },
    'CI-C': { en: 'Check-in, island C', ar: 'تسجيل الوصول: الجزيرة C', q: ['CI-C'], level: 'dep' },
    'CI-D': { en: 'Check-in, island D', ar: 'تسجيل الوصول: الجزيرة D', q: ['CI-D'], level: 'dep' }
  };
  var HALL = { arr: { en: 'Arrivals hall', ar: 'صالة الوصول' }, dep: { en: 'Departures hall', ar: 'صالة المغادرة' } };
  var LEVEL = { arr: 'Arrivals level', dep: 'Departures level' };

  function displays() { return U.list('displays'); }
  function cur() { return displays().filter(function (d) { return d.id === dspId; })[0] || displays()[0]; }
  function bands(d) { return d.band === 10 ? BANDS10 : BANDS5; }
  function bandOf(d, v) {
    if (d.band === 10) return v < 10 ? 0 : v < 20 ? 1 : v < 30 ? 2 : 3;
    return S.waitBand(v);
  }

  function value(row, m) {
    var v = null;
    row.q.forEach(function (id) {
      var s = U.day.state(S.QI[id], m);
      var x = s.degraded && s.band ? (s.band[0] + s.band[1]) / 2 : s.nowcast;
      if (x != null && (v == null || x > v)) v = x;
    });
    return v;
  }

  /* with hysteresis, the shown band changes only when the value has moved a full band since the last change */
  function hysteresis(d, row, now) {
    var shown = null, anchor = null, at = 0, step = d.band === 10 ? 10 : 5;
    for (var m = 0; m <= now; m++) {
      var v = value(row, m);
      if (v == null) continue;
      var b = bandOf(d, v);
      if (shown == null) { shown = b; anchor = v; at = m; continue; }
      if (b !== shown && (!d.hysteresis || Math.abs(v - anchor) >= step)) { shown = b; anchor = v; at = m; }
    }
    return { shown: shown, anchor: anchor, at: at, v: value(row, now) };
  }

  function build() {
    var stored = U.store.get('display.id', 'DSP-01');
    dspId = displays().some(function (d) { return d.id === stored; }) ? stored : 'DSP-01';
    var c = document.getElementById('dispControls');
    c.innerHTML = '<label class="field" for="dspSel">Display <select id="dspSel"></select></label>' +
      '<button type="button" class="btn" id="staleBtn" aria-pressed="' + stale + '">' + (stale ? 'Stale data on: restore live data' : 'Simulate stale data') + '</button>' +
      '<button type="button" class="btn" id="fsBtn">' + U.icon('full') + 'Full screen</button><span class="small muted" id="fsMsg" aria-live="polite"></span>';
    fillSel();
    document.getElementById('dspSel').addEventListener('change', function (e) { select(e.target.value); });
    document.getElementById('staleBtn').addEventListener('click', function (e) {
      stale = !stale; e.currentTarget.setAttribute('aria-pressed', String(stale));
      e.currentTarget.textContent = stale ? 'Stale data on: restore live data' : 'Simulate stale data';
      U.draw(true);
    });
    document.getElementById('fsBtn').addEventListener('click', function () {
      var el = document.getElementById('board'), msg = document.getElementById('fsMsg');
      try {
        var p = el.requestFullscreen ? el.requestFullscreen() : null;
        if (p && p.catch) p.catch(function () { msg.textContent = 'Full screen is not available in this viewer.'; });
        else if (!p) msg.textContent = 'Full screen is not available in this viewer.';
      } catch (e) { msg.textContent = 'Full screen is not available in this viewer.'; }
    });
    document.getElementById('dspNew').innerHTML = U.createBtn('display', 'Add display', 'dspBtn');
    var nb = document.getElementById('dspBtn');
    if (!nb.disabled) nb.addEventListener('click', function () { openDisplay(nb); });
    var list = document.getElementById('dspList');
    list.__html = null;
    if (!list.__bound) {
      list.addEventListener('click', function (e) {
        var b = e.target.closest('[data-dsp]');
        if (!b) return;
        var id = b.getAttribute('data-id');
        if (b.getAttribute('data-dsp') === 'show') { select(id); var bd = document.getElementById('board'); if (bd.scrollIntoView) bd.scrollIntoView(); return; }
        var d = U.state.c.displays.filter(function (x) { return x.id === id; })[0];
        if (!d || !U.canCreate('display')) return;
        U.removeFrom('displays', id); U.saveC(); U.audit('Removed', id, 'Display removed'); if (dspId === id) select('DSP-01'); else U.draw(true);
        U.toast('Removed ' + id, { undo: function () { U.state.c.displays.push(d); U.saveC(); U.audit('Restored', id, 'Display restored'); fillSel(); U.draw(true); } });
        var n2 = document.getElementById('dspBtn'); if (n2) n2.focus();
      });
      list.__bound = true;
    }
  }
  function fillSel() {
    var sel = document.getElementById('dspSel');
    if (!sel) return;
    sel.innerHTML = displays().map(function (d) { return '<option value="' + d.id + '"' + (d.id === dspId ? ' selected' : '') + '>' + d.id + ', ' + U.esc(d.name) + '</option>'; }).join('');
  }
  function select(id) {
    dspId = id; U.store.set('display.id', id);
    fillSel();
    var el = document.getElementById('board'); el.__html = null;
    U.draw(true);
  }

  function update(m) {
    var d = cur(), el = document.getElementById('board'), ar = d.lang === 'ar', B = bands(d);
    var rows = d.checkpoints.map(function (k) { return CP[k]; });
    var res = rows.map(function (r) { return hysteresis(d, r, m); });
    var wrap = el.parentNode;
    wrap.classList.toggle('portrait', d.orientation === 'portrait');
    el.classList.toggle('portrait', d.orientation === 'portrait');
    el.setAttribute('dir', ar ? 'rtl' : 'ltr');
    el.setAttribute('aria-label', 'Passenger wait-time board, ' + d.id + ', ' + d.name);
    var H = HALL[d.level];
    var tEn = '<div dir="ltr" lang="en"><div class="board-title-en">Waiting times</div><div class="board-sub">' + H.en + '</div></div>';
    var tAr = '<div dir="rtl" lang="ar"><div class="board-title-ar">أوقات الانتظار</div><div class="board-sub">' + H.ar + '</div></div>';
    var clk = '<div class="board-clock">' + S.clock(m) + '</div>';
    var head = '<div class="board-head">' + (ar ? tAr + clk + tEn : tEn + clk + tAr) + '</div>';
    var body;
    if (stale) {
      var en = '<div class="en" dir="ltr" lang="en">' + U.esc(d.fallbackEn) + '</div>', arx = '<div class="ar" dir="rtl" lang="ar">' + U.esc(d.fallbackAr) + '</div>';
      body = '<div class="board-stale" role="status">' + (ar ? arx + en : en + arx) +
        '<div class="sub"><span dir="ltr" lang="en">Please follow staff directions.</span> <span dir="rtl" lang="ar">يرجى اتباع إرشادات الموظفين.</span></div></div>';
    } else {
      body = '<div class="board-rows">' + rows.map(function (r, i) {
        var x = res[i], c = x.v == null ? null : x.shown, bd = c == null ? NONE : B[c];
        var long = c != null && (bd.cls === 'b3' || bd.cls === 'b4' || bd.cls === 'b5');
        var enB = '<div class="board-en' + (ar ? ' secondary' : '') + '" dir="ltr" lang="en"><div class="lbl">' + r.en + '</div><div class="band">' + (long ? 'Longer than usual' : 'Estimated wait') + '</div></div>';
        var arB = '<div class="board-ar' + (ar ? ' primary' : '') + '" dir="rtl" lang="ar"><div class="lbl">' + r.ar + '</div><div class="band">' + (long ? 'أطول من المعتاد' : 'وقت الانتظار المتوقع') + '</div></div>';
        var chEn = '<span class="en" dir="ltr" lang="en">' + bd.en + '</span>', chAr = '<span class="ar" dir="rtl" lang="ar">' + bd.ar + '</span>';
        var chip = '<div class="board-chip' + (c == null ? '' : ' ' + bd.cls) + '">' + (ar ? chAr + chEn : chEn + chAr) + '</div>';
        return '<div class="board-row">' + (ar ? arB + chip + enB : enB + chip + arB) + '</div>';
      }).join('') + '</div>';
    }
    var fEn = '<span dir="ltr" lang="en">' + (stale ? 'Last update over ' + d.stale + ' minutes ago' : 'Updated ' + S.clock(m)) + '</span>';
    var fAr = '<span dir="rtl" lang="ar">' + (stale ? 'آخر تحديث منذ أكثر من ' + d.stale + ' دقائق' : 'آخر تحديث ' + S.clock(m)) + '</span>';
    var foot = '<div class="board-foot">' + (ar ? fAr + fEn : fEn + fAr) + '</div><div class="board-mark" dir="ltr">Synthetic demo data</div>';
    U.html(el, head + body + foot);

    var trs = rows.map(function (r, i) {
      var x = res[i];
      return '<tr><th scope="row">' + r.en + '</th><td class="num">' + (x.v == null ? 'No estimate' : U.fmt(x.v, 1) + ' min') + '</td><td>' + (x.shown == null || x.v == null ? 'No estimate' : B[x.shown].en) + '</td><td class="num">' + (x.anchor == null ? 'n/a' : U.fmt(x.anchor, 1) + ' min') + '</td><td class="mono">' + S.clock(x.at) + '</td></tr>';
    });
    U.html(document.getElementById('hyst'), '<p class="small">' + U.esc(d.id + ', ' + d.name + ': ' + d.location) + '. The board shows ' + d.band + '-minute bands, not minutes. ' +
      (d.hysteresis ? 'A band changes only when the underlying wait has moved at least ' + d.band + ' minutes (a full band) since the last change, so the board does not flicker at a band edge.' : 'Hysteresis is off on this display, so the band follows the wait directly and can flicker at a band edge.') +
      ' Degraded zones use the middle of their estimated band. ' +
      (stale ? '<strong>Stale data is on:</strong> the board shows this display\'s fallback message in both languages.' : 'The board falls back to a neutral message when data is older than ' + d.stale + ' minutes; use "Simulate stale data" to see it.') + '</p>' +
      '<div class="table-wrap"><table class="tbl"><thead><tr><th scope="col">Checkpoint</th><th scope="col" class="num">Underlying now</th><th scope="col">Shown band</th><th scope="col" class="num">Value at last change</th><th scope="col">Last change</th></tr></thead><tbody>' + trs.join('') + '</tbody></table></div>' +
      '<p class="small muted" style="margin-block-start:6px">Passport control on the departures board uses the longest manual lane. Crew and diplomats are not shown to passengers.</p>');

    U.html(document.getElementById('dspList'), '<div class="table-wrap"><table class="tbl stack-sm"><thead><tr><th scope="col">ID</th><th scope="col">Display</th><th scope="col">Checkpoints</th><th scope="col">Layout</th><th scope="col">Bands</th><th scope="col">Stale after</th><th scope="col">Actions</th></tr></thead><tbody>' +
      displays().map(function (x) {
        return '<tr' + U.rowCls(x.id, x.id === d.id ? 'row-hl' : '') + '><th scope="row" class="mono">' + x.id + '</th><td><strong>' + U.esc(x.name) + '</strong><div class="small muted">' + LEVEL[x.level] + ', ' + U.esc(x.location) + '</div></td>' +
          '<td class="small">' + x.checkpoints.map(function (k) { return CP[k].en; }).join('; ') + '</td><td class="small">' + (x.orientation === 'portrait' ? 'Portrait' : 'Landscape') + ', ' + (x.lang === 'ar' ? 'Arabic first' : 'English first') + '</td>' +
          '<td class="small">' + x.band + ' min, hysteresis ' + (x.hysteresis ? 'on' : 'off') + '</td><td class="small">' + x.stale + ' min</td>' +
          '<td class="actions">' + (x.id === d.id ? '<span class="small muted">Shown above</span>' : '<button type="button" class="btn" data-dsp="show" data-id="' + x.id + '">Show</button>') +
          (!x.seeded && U.canCreate('display') ? '<button type="button" class="btn" data-dsp="remove" data-id="' + x.id + '">Remove</button>' : '') + '</td></tr>';
      }).join('') + '</tbody></table></div>');
  }

  function openDisplay(trig) {
    var id = U.nextId('display');
    function cps(lv) { return Object.keys(CP).filter(function (k) { return CP[k].level === lv; }).map(function (k) { return [k, CP[k].en]; }); }
    var html = FF.text('did', 'Display ID', id, { readonly: true }) +
      FF.text('name', 'Name', '', { req: true, placeholder: 'For example: Pier B arrivals board' }) +
      FF.row(FF.select('level', 'Level', [['arr', LEVEL.arr], ['dep', LEVEL.dep]], 'dep'), FF.text('location', 'Location', '', { req: true, placeholder: 'For example: pier B entrance' })) +
      FF.row(FF.radios('orientation', 'Orientation', [['landscape', 'Landscape'], ['portrait', 'Portrait']], 'landscape'), FF.radios('lang', 'Language order', [['en', 'English first'], ['ar', 'Arabic first']], 'en')) +
      '<div data-cps>' + FF.checks('checkpoints', 'Checkpoints shown', cps('dep'), ['SEC-N', 'SEC-S'], { req: true, hint: 'Up to 6 on a landscape board, 4 on a portrait board.' }) + '</div>' +
      FF.row(FF.radios('band', 'Band size', [['5', '5 minutes'], ['10', '10 minutes']], '5'), FF.checks('hyst', 'Hysteresis', [['on', 'Change a band only after a full band of movement']], ['on'])) +
      FF.text('stale', 'Stale threshold', 5, { type: 'number', min: 1, max: 30, unit: 'min', inputmode: 'numeric', hint: 'Show the fallback message when data is older than this.' }) +
      FF.text('fallbackEn', 'Fallback message, English', 'Waiting times are temporarily unavailable', { req: true }) +
      FF.text('fallbackAr', 'Fallback message, Arabic', 'أوقات الانتظار غير متاحة مؤقتاً', { req: true, ar: true });
    U.openDrawer({
      title: 'Add display', html: html, trigger: trig, returnFocus: 'dspBtn',
      onOpen: function (form) {
        var lastLv = 'dep';
        form.addEventListener('change', function (e) {
          if (e.target.name !== 'level' || e.target.value === lastLv) return;
          lastLv = e.target.value;
          form.querySelector('[data-cps]').innerHTML = FF.checks('checkpoints', 'Checkpoints shown', cps(lastLv), lastLv === 'arr' ? ['A-VIS', 'A-RES', 'A-CIT', 'A-EG'] : ['SEC-N', 'SEC-S'], { req: true, hint: 'Up to 6 on a landscape board, 4 on a portrait board.' });
        });
      },
      onSubmit: function (v) {
        var e = {}, cp = v.checkpoints || [], st = U.V.int(v.stale), max = v.orientation === 'portrait' ? 4 : 6;
        if (!v.name) e.name = 'Enter a name.';
        else if (v.name.length > 60) e.name = 'Keep the name under 60 characters.';
        else if (displays().some(function (d) { return d.name.toLowerCase() === v.name.toLowerCase(); })) e.name = 'A display with this name already exists.';
        if (!v.location) e.location = 'Enter where the display hangs.';
        if (!cp.length) e.checkpoints = 'Choose at least one checkpoint.';
        else if (cp.length > max) e.checkpoints = 'A ' + v.orientation + ' board fits up to ' + max + ' checkpoints; ' + cp.length + ' are selected.';
        else { var wrong = cp.filter(function (k) { return CP[k].level !== v.level; }); if (wrong.length) e.checkpoints = CP[wrong[0]].en + ' is not on the ' + LEVEL[v.level].toLowerCase() + '.'; }
        if (st == null || st < 1 || st > 30) e.stale = 'Stale threshold must be a whole number of minutes from 1 to 30.';
        if (!v.fallbackEn) e.fallbackEn = 'Enter the English fallback message.';
        else if (v.fallbackEn.length > 80) e.fallbackEn = 'Keep the English message under 80 characters.';
        if (!v.fallbackAr) e.fallbackAr = 'Enter the Arabic fallback message.';
        else if (!U.V.hasArabic(v.fallbackAr)) e.fallbackAr = 'The Arabic message must be written in Arabic script.';
        else if (v.fallbackAr.length > 80) e.fallbackAr = 'Keep the Arabic message under 80 characters.';
        if (Object.keys(e).length) return e;
        var d = { id: id, name: v.name, level: v.level, location: v.location, orientation: v.orientation, checkpoints: cp, lang: v.lang, band: +v.band, hysteresis: (v.hyst || []).indexOf('on') >= 0, stale: st,
          fallbackEn: v.fallbackEn, fallbackAr: v.fallbackAr, by: U.roleName(), at: U.now() };
        U.state.c.displays.push(d);
        U.created('display', id, v.name + ' (' + LEVEL[v.level].toLowerCase() + ', ' + v.orientation + ', ' + cp.length + ' checkpoints, ' + d.band + '-minute bands)', function () {
          U.removeFrom('displays', id); if (dspId === id) dspId = 'DSP-01';
        }, function (undone) { if (!undone) select(id); else { fillSel(); var el = document.getElementById('board'); el.__html = null; U.draw(true); } });
        return null;
      }
    });
  }

  QUI.start({ build: build, update: update });
})();
