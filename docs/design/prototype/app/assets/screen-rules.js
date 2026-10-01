(function () {
  'use strict';
  var U = QUI, S = QSim, F = U.F;
  var OPS = { gt: 'above', ge: 'at or above', lt: 'below', le: 'at or below', is: 'is true' };
  var SEV = { info: 'Info', warning: 'Warning', critical: 'Critical' };
  var OWNERS = [['Border shift supervisor', 'Border shift supervisor'], ['Terminal duty manager', 'Terminal duty manager'], ['Handler B station manager', 'Handler B station manager'], ['zone', 'Owner of the zone (by zone)']];
  var ESCALATE = ['Border operations duty officer', 'Airport operations centre lead', 'Terminal duty manager', 'Border systems engineer', 'Airport systems engineer'];
  var GROUPS = [
    ['g:arr', 'Arrival immigration, all lanes', ['A-CRW', 'A-CIT', 'A-RES', 'A-VIS', 'A-EG']],
    ['g:dep', 'Departure immigration, all lanes', ['D-CRW', 'D-CIT', 'D-RES', 'D-VIS', 'D-EG']],
    ['g:imm', 'All immigration lanes', S.IMM_IDS],
    ['g:ciA', 'Check-in, Handler A islands', ['CI-A', 'CI-B']],
    ['g:ciB', 'Check-in, Handler B islands', ['CI-C', 'CI-D']],
    ['g:sec', 'Security checkpoints', ['SEC-N', 'SEC-S']],
    ['g:air', 'Check-in and security', S.AIR_IDS]
  ];
  var RANGE = { nowcast: [1, 180], p90bin: [1, 180], queue: [1, 2000], desks: [1, 15] };

  function ruleVisible(r) {
    return (r.scope || []).some(function (id) {
      var reg = QFloor.ZONE_REGION[id];
      return reg && U.regionVis(reg) === 'full';
    });
  }
  function ruleEditable(r) {
    if (!U.canCreate('rule')) return false;
    var allowed = U.createQueues('rule');
    return (r.scope || []).every(function (id) { return allowed.indexOf(id) >= 0 || (/-OV$/.test(id) && U.role() === 'demo'); });
  }
  function condText(r) {
    var M = S.METRICS[r.metric];
    if (M.bool) return M.label;
    return M.label + ' ' + OPS[r.op] + ' ' + U.fmt(r.threshold) + ' ' + M.unit + (r.minQueue ? ', with ' + r.minQueue + ' or more queuing' : '');
  }
  function scopeText(r) {
    if (r.scopeLabel) return r.scopeLabel;
    return r.scope.map(function (id) { return U.qname(id); }).join(', ');
  }
  function enabled(r) { return r.enabled !== false; }

  function build() {
    var h = document.getElementById('ruleNew');
    h.innerHTML = U.createBtn('rule', 'New rule', 'newRuleBtn');
    var b = document.getElementById('newRuleBtn');
    if (b && !b.disabled) b.addEventListener('click', function () { openRule(null, b); });
    var list = document.getElementById('ruleList');
    list.__html = null;
    if (!list.__bound) {
      list.addEventListener('click', function (e) {
        var t = e.target.closest('[data-act]');
        if (!t) return;
        var id = t.getAttribute('data-id'), r = U.allRules().filter(function (x) { return x.id === id; })[0];
        if (!r) return;
        var act = t.getAttribute('data-act');
        if (act === 'toggle') toggle(r);
        else if (act === 'dup') openRule(r, t);
      });
      list.__bound = true;
    }
  }

  function toggle(r) {
    var c = U.state.c, on = !enabled(r);
    if (r.seeded) { if (on) delete c.ruleState[r.id]; else c.ruleState[r.id] = { enabled: false }; }
    else { var x = c.rules.filter(function (y) { return y.id === r.id; })[0]; if (x) x.enabled = on; }
    U.saveC();
    U.audit(on ? 'Enabled' : 'Disabled', r.id, r.name);
    U.refreshRules(false);
    U.status((on ? 'Enabled ' : 'Disabled ') + r.id + '.');
  }

  function scopeOptions() {
    var allowed = U.createQueues('rule');
    var groups = GROUPS.filter(function (g) { return g[2].every(function (id) { return allowed.indexOf(id) >= 0; }); }).map(function (g) { return [g[0], g[1]]; });
    var singles = allowed.map(function (id) { return ['q:' + id, U.qname(id)]; });
    return [{ group: 'Groups', options: groups }, { group: 'Single queue', options: singles }];
  }
  function scopeIds(v) {
    if (!v) return [];
    if (v.indexOf('g:') === 0) { var g = GROUPS.filter(function (x) { return x[0] === v; })[0]; return g ? g[2].slice() : []; }
    return [v.slice(2)];
  }
  function scopeLabelOf(v) {
    if (v.indexOf('g:') === 0) return (GROUPS.filter(function (x) { return x[0] === v; })[0] || [])[1];
    return U.qname(v.slice(2));
  }

  function candidate(v, id) {
    var metric = v.metric, M = S.METRICS[metric];
    var sc = scopeIds(v.scope);
    if (metric === 'sensor') {
      var ov = [];
      if (sc.some(function (x) { return /^A-/.test(x); })) ov.push('A-OV');
      if (sc.some(function (x) { return /^D-/.test(x); })) ov.push('D-OV');
      if (sc.some(function (x) { return /^SEC-/.test(x); })) ov.push('SEC-OV');
      sc = sc.concat(ov);
    }
    return { id: id, name: v.name, scope: sc, scopeLabel: scopeLabelOf(v.scope || ''), metric: metric, op: M.bool ? 'is' : v.op, threshold: M.bool ? null : U.V.num(v.threshold),
      sustain: U.V.int(v.sustain) || 1, clearAfter: 5, severity: v.severity, owner: v.owner, escalateAfter: U.V.int(v.escalateAfter) || 10, escalateTo: v.escalateTo,
      email: v.email || '', enabled: true };
  }

  function validate(v) {
    var e = {}, M = S.METRICS[v.metric];
    if (!v.name) e.name = 'Enter a name.';
    else if (v.name.length > 60) e.name = 'Keep the name under 60 characters.';
    else if (U.allRules().some(function (r) { return r.name.toLowerCase() === v.name.toLowerCase(); })) e.name = 'A rule with this name already exists.';
    if (!v.scope || !scopeIds(v.scope).length) e.scope = 'Choose a scope.';
    if (!M.bool) {
      var t = U.V.num(v.threshold), rg = RANGE[v.metric];
      if (t == null) e.threshold = 'Enter a threshold in ' + M.unit + '.';
      else if (t < rg[0] || t > rg[1]) e.threshold = 'Threshold must be between ' + rg[0] + ' and ' + U.fmt(rg[1]) + ' ' + M.unit + '.';
    }
    var su = U.V.int(v.sustain);
    if (su == null || su < 1 || su > 120) e.sustain = 'Sustained for must be a whole number of minutes from 1 to 120.';
    var ea = U.V.int(v.escalateAfter);
    if (ea == null || ea < 1 || ea > 240) e.escalateAfter = 'Escalate after must be a whole number of minutes from 1 to 240.';
    var bad = U.V.emails(v.email);
    if (bad.length) e.email = 'Not a valid email address: ' + bad.join(', ') + '.';
    if (v.owner && v.owner !== 'zone' && v.scope) {
      var sc = scopeIds(v.scope), imm = sc.some(function (id) { return /^[AD]-/.test(id); }), air = sc.some(function (id) { return /^(CI|SEC)-/.test(id); });
      if (v.owner === 'Border shift supervisor' && air) e.owner = 'The Border shift supervisor does not see check-in or security, so cannot own this rule.';
      if (v.owner === 'Terminal duty manager' && imm) e.owner = 'Immigration rules belong to the border deployment; the Terminal duty manager cannot own them.';
      if (v.owner === 'Handler B station manager' && sc.some(function (id) { return id !== 'CI-C' && id !== 'CI-D'; })) e.owner = 'The Handler B station manager only sees islands C and D.';
    }
    return e;
  }

  function openRule(src, trig) {
    var allowed = U.createQueues('rule');
    var defScope = allowed.indexOf('A-VIS') >= 0 ? 'q:A-VIS' : allowed.indexOf('CI-C') >= 0 ? 'q:CI-C' : 'q:' + allowed[0];
    if (src && src.scope && src.scope.length === 1 && allowed.indexOf(src.scope[0]) >= 0) defScope = 'q:' + src.scope[0];
    else if (src) { var g = GROUPS.filter(function (x) { return x[2].join() === src.scope.filter(function (id) { return !/-OV$/.test(id); }).join(); })[0]; if (g && g[2].every(function (id) { return allowed.indexOf(id) >= 0; })) defScope = g[0]; }
    var v0 = src ? { name: 'Copy of ' + src.name, metric: src.metric, op: src.op === 'is' ? 'gt' : src.op, threshold: src.threshold == null ? '' : src.threshold, sustain: src.sustain || 1, severity: src.severity,
      owner: src.owner, escalateAfter: src.escalateAfter, escalateTo: src.escalateTo === 'auto' || src.escalateTo === 'systems' ? ESCALATE[0] : src.escalateTo, email: src.email || '' }
      : { name: '', metric: 'nowcast', op: 'gt', threshold: 12, sustain: 3, severity: 'warning', owner: U.role() === 'tdm' ? 'Terminal duty manager' : 'Border shift supervisor', escalateAfter: 10, escalateTo: U.role() === 'tdm' ? 'Airport operations centre lead' : 'Border operations duty officer', email: '' };
    var id = U.nextId('rule');
    var html =
      F.text('rid', 'Rule ID', id, { readonly: true, hint: 'Assigned automatically.' }) +
      F.text('name', 'Name', v0.name, { req: true, placeholder: 'For example: Visitors wait above 12 min' }) +
      F.select('scope', 'Scope', scopeOptions(), defScope, { hint: 'Queues this role may create rules for.' }) +
      F.select('metric', 'Metric', Object.keys(S.METRICS).map(function (k) { return [k, S.METRICS[k].label]; }), v0.metric) +
      F.row(F.select('op', 'Condition', [['gt', 'Above'], ['ge', 'At or above'], ['lt', 'Below'], ['le', 'At or below']], v0.op),
        F.text('threshold', 'Threshold', v0.threshold, { type: 'number', unit: 'min', min: 0, inputmode: 'decimal', req: true })) +
      F.row(F.text('sustain', 'Sustained for', v0.sustain, { type: 'number', unit: 'min', min: 1, max: 120, inputmode: 'numeric' }),
        F.select('severity', 'Severity', [['info', 'Info'], ['warning', 'Warning'], ['critical', 'Critical']], v0.severity)) +
      F.select('owner', 'Owner role', OWNERS, v0.owner) +
      F.row(F.text('escalateAfter', 'Escalate after', v0.escalateAfter, { type: 'number', unit: 'min', min: 1, max: 240, inputmode: 'numeric' }),
        F.select('escalateTo', 'Escalate to', ESCALATE.map(function (x) { return [x, x]; }), v0.escalateTo)) +
      F.checks('channels', 'Channels', [['screen', 'On-screen alert list (always)', true]], ['screen']) +
      F.text('email', 'Email addresses', v0.email, { placeholder: 'ops.desk@dmo.example', hint: 'Plain text, separated by commas. Not sent in the demo.' }) +
      '<div class="preview" id="btPreview" aria-live="polite"></div>' +
      F.note('The rule clears when its condition has been false for 5 minutes. The preview evaluates the rule on today\'s synthetic data; once created it fires on the operations screen at the same times.');
    U.openDrawer({
      title: src ? 'Duplicate rule ' + src.id : 'New alert rule', html: html, trigger: trig, returnFocus: 'newRuleBtn',
      onOpen: function (form) {
        var t = null;
        function sync() {
          var v = U.formValues(form), M = S.METRICS[v.metric];
          form.querySelector('[data-f="op"]').hidden = !!M.bool;
          form.querySelector('[data-f="threshold"]').hidden = !!M.bool;
          var u = form.querySelector('[data-f="threshold"] .unit'); if (u) u.textContent = M.unit;
          clearTimeout(t);
          t = setTimeout(function () { preview(form); }, 150);
        }
        form.addEventListener('input', sync);
        form.addEventListener('change', sync);
        sync();
        preview(form);
      },
      onSubmit: function (v) {
        var e = validate(v);
        if (Object.keys(e).length) return e;
        var rule = candidate(v, id);
        rule.createdBy = U.roleName(); rule.createdAt = U.now();
        U.state.c.rules.push(rule);
        U.refreshRules(false);
        U.created('rule', id, rule.name + ' on ' + rule.scopeLabel + ': ' + condText(rule),
          function () { U.removeFrom('rules', id); U.refreshRules(false); }, function () { U.draw(true); });
        return null;
      }
    });
  }

  function preview(form) {
    var box = document.getElementById('btPreview');
    if (!box) return;
    var v = U.formValues(form), M = S.METRICS[v.metric];
    if (!scopeIds(v.scope).length || (!M.bool && U.V.num(v.threshold) == null)) { box.innerHTML = '<strong>Backtest preview</strong>Complete the scope and threshold to see how often the rule would have fired.'; return; }
    var bt = U.day.backtest(candidate(v, 'preview'));
    var list = bt.alerts.slice(0, 5).map(function (a) { return '<li><span class="mono">' + S.clock(a.raisedAt) + '</span> ' + U.esc(a.sensor ? a.sensor + ', ' + a.zone : a.zone) + (a.clearedAt != null ? ', cleared ' + S.clock(a.clearedAt) : '') + '</li>'; }).join('');
    box.innerHTML = '<strong>Backtest preview, today (seed ' + U.day.seed + ')</strong>' + (bt.count ? 'Would have fired <span data-bt-count>' + bt.count + '</span> time' + (bt.count > 1 ? 's' : '') + ' today, first at <span class="mono" data-bt-first>' + S.clock(bt.first) + '</span>.' +
      '<ul>' + list + '</ul>' + (bt.count > 5 ? '<span class="small muted">and ' + (bt.count - 5) + ' more</span>' : '') : 'Would not have fired today.');
  }

  function update(m) {
    var rules = U.allRules().filter(ruleVisible);
    var alerts = U.alertsAt(m);
    var on = rules.filter(enabled).length, firing = alerts.filter(function (a) { return a.status !== 'cleared'; }).length;
    U.html(document.getElementById('kpis'), [
      U.kpi({ hero: true, label: 'Rules enabled', value: on + ' <small>of ' + rules.length + '</small>', sub: 'Rules that cover zones visible to this role' }),
      U.kpi({ label: 'Alerts so far today', value: String(alerts.length), sub: 'From these rules, visible to this role' }),
      U.kpi({ label: 'Open now', value: String(firing), sub: firing ? U.st('crit', 'See Live operations') : U.st('good', 'None open') })
    ].join(''));
    var rows = rules.map(function (r) {
      var fired = alerts.filter(function (a) { return a.ruleId === r.id; });
      var edit = ruleEditable(r);
      var acts = edit ? '<button type="button" class="btn" data-act="toggle" data-id="' + r.id + '">' + (enabled(r) ? 'Disable' : 'Enable') + '</button>' : '';
      if (U.canCreate('rule')) acts += ' <button type="button" class="btn" data-act="dup" data-id="' + r.id + '">Duplicate</button>';
      var owner = r.owner === 'zone' ? 'Zone owner' : r.owner;
      var esc = r.escalateTo === 'auto' ? 'the owner\'s duty lead' : r.escalateTo === 'systems' ? 'systems engineer' : r.escalateTo;
      return '<tr' + U.rowCls(r.id) + ' data-rule="' + r.id + '"><th scope="row" class="mono">' + r.id + '</th><td><strong>' + U.esc(r.name) + '</strong><div class="small muted">' + U.esc(scopeText(r)) + '</div></td>' +
        '<td class="small">' + U.esc(condText(r)) + (r.sustain > 1 ? ', for ' + r.sustain + ' min' : '') + '</td>' +
        '<td>' + (r.severity === 'critical' ? U.st('crit', 'Critical') : r.severity === 'warning' ? U.st('warn', 'Warning') : U.st('unknown', 'Info')) + '</td>' +
        '<td class="small">' + U.esc(owner) + '<div class="muted">Escalates after ' + r.escalateAfter + ' min to ' + U.esc(esc) + '</div></td>' +
        '<td class="small">On-screen' + (r.email ? '<div class="muted">Email ' + U.esc(r.email) + ' (not sent in the demo)</div>' : '') + '</td>' +
        '<td class="small">' + (fired.length ? fired.length + ', first ' + S.clock(fired[0].raisedAt) : 'None yet') + '</td>' +
        '<td>' + (enabled(r) ? '<span class="pill ok">Enabled</span>' : '<span class="pill off">Disabled</span>') + '<div class="small muted">' + (r.seeded ? 'Seeded' : 'Created by ' + U.esc(r.createdBy || '') + ' at ' + S.clock(r.createdAt || 0)) + '</div></td>' +
        '<td class="actions">' + (acts || '<span class="small muted">Read only</span>') + '</td></tr>';
    });
    U.html(document.getElementById('ruleList'), '<div class="table-wrap"><table class="tbl stack-sm"><thead><tr><th scope="col">ID</th><th scope="col">Rule and scope</th><th scope="col">Condition</th><th scope="col">Severity</th><th scope="col">Owner and escalation</th><th scope="col">Channels</th><th scope="col">Fired today</th><th scope="col">Status</th><th scope="col">Actions</th></tr></thead><tbody>' +
      rows.join('') + '</tbody></table></div>');
  }

  QUI.start({ build: build, update: update });
})();
