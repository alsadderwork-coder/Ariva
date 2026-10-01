(function () {
  'use strict';
  var U = QUI;

  function build() {
    var role = U.role(), R = U.ROLE_BY_ID;
    var hidden = U.NAV.filter(function (n) { return U.screenAccess(n.id) === 'none'; });
    document.getElementById('kpis').innerHTML = [
      U.kpi({ hero: true, label: 'Current role', value: '<span style="font-size:28px">' + U.esc(U.roleName()) + '</span>', sub: U.esc(R[role].note) }),
      U.kpi({ label: 'Screens not available', value: String(hidden.length), sub: hidden.length ? U.esc(hidden.map(function (n) { return n.label; }).join(', ')) : 'Every screen, some with parts hidden' }),
      U.kpi({ label: 'Officer names or IDs shown', value: '0', sub: 'In any role, on any screen' })
    ].join('');
    var cards = U.ROLES.map(function (r) {
      return '<button type="button" class="rolecard" data-role="' + r.id + '" aria-pressed="' + (r.id === role) + '"><strong>' + U.esc(r.name) + '</strong><span>' + U.esc(r.note) + '</span></button>';
    }).join('');
    var cols = ['bss', 'tdm', 'hbm', 'demo'];
    var head = '<tr><th scope="col">Screen</th>' + cols.map(function (c) { return '<th scope="col"' + (c === role ? ' style="color:var(--accent)"' : '') + '>' + U.esc(R[c].name) + (c === role ? ' (current)' : '') + '</th>'; }).join('') + '</tr>';
    var rows = U.ACCESS_RULES.map(function (a) {
      return '<tr><th scope="row"><a href="' + a.href + '">' + U.esc(a.screen) + '</a></th>' + cols.map(function (c) {
        var txt = a[c], none = /^Not available/.test(txt);
        return '<td' + (c === role ? ' class="row-hl"' : '') + '>' + (none ? U.st('unknown', txt) : U.esc(txt)) + '</td>';
      }).join('') + '</tr>';
    }).join('');
    document.getElementById('accBody').innerHTML =
      '<section class="panel" aria-labelledby="bdTitle"><div class="panel-head"><div><h2 id="bdTitle">The data boundary</h2><p>In a real deployment the border authority and the airport operator each run their own instance. Data crosses in one direction only, as aggregates.</p></div></div>' +
      '<div class="boundary"><div class="bnd-box"><h3>Border deployment (border authority)</h3><ul><li>Desk states, per-desk interval aggregates</li><li>Lane mix from API passenger data</li><li>E-gate outcomes and reject reasons</li><li>Immigration sensors, zones and alarms</li><li>Officer-level data stays in the border system (AMAN), never in the QMS</li></ul></div>' +
      '<div class="bnd-arrow"><svg viewBox="0 0 140 26" aria-hidden="true"><path d="M2 13h128M120 4l12 9-12 9" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"/></svg><span>One-way feed: lane-level waits, queue lengths and a quality flag, every minute</span></div>' +
      '<div class="bnd-box"><h3>Airport deployment (airport operator)</h3><ul><li>Check-in, security and reclaim zones</li><li>Handler counters, SLA bins, penalties and disputes</li><li>Passenger displays for all checkpoints</li><li>Border waits as lane aggregates only</li><li>Handlers see only their own counters through a handler view</li></ul></div></div></section>' +
      '<section class="panel" aria-labelledby="rsTitle"><div class="panel-head"><div><h2 id="rsTitle">Switch role</h2><p>Same as the switcher in the top bar. Every screen changes to match.</p></div></div><div class="rolecards" id="roleCards">' + cards + '</div>' +
      '<p class="small muted" style="margin-block-start:10px"><strong>Demo presenter (all views)</strong> exists only in this prototype so one person can show every screen. It does not exist in a real deployment, because border and airport run as separate deployments with a one-way aggregate feed: no single login sees both sides in full.</p></section>' +
      '<section class="panel" aria-labelledby="rlTitle"><div class="panel-head"><div><h2 id="rlTitle">What each role sees</h2><p>These are the rules every screen enforces. Hidden content is replaced by a "Not available for this role" panel; greyed floor-plan areas say "Outside your view".</p></div></div>' +
      '<div class="table-wrap"><table class="tbl access-tbl"><thead>' + head + '</thead><tbody>' + rows + '</tbody></table></div></section>' +
      '<section class="panel" aria-labelledby="nvTitle"><div class="panel-head"><div><h2 id="nvTitle">Never shown, in any role</h2></div></div><ul class="small">' +
      '<li>Officer names, officer IDs or logins.</li><li>Processing times per officer. Desk figures are per desk position and per 15-minute interval.</li><li>Individual passenger records. The QMS counts people in zones; it does not identify them.</li></ul>' +
      '<p class="small muted">Desk IDs such as AR-08 identify a desk position, not a person. The "Officer analytics open in AMAN" button on the immigration screen is disabled on purpose: that analysis stays in the border system.</p></section>' +
      '<section class="panel" id="rights" aria-labelledby="crTitle"><div class="panel-head"><div><h2 id="crTitle">What each role can create</h2><p>Create buttons stay visible to every role; they are disabled with a one-line reason where the role has no right. The demo presenter has every right.</p></div></div>' +
      '<div class="table-wrap"><table class="tbl access-tbl"><thead>' + head + '</thead><tbody>' + rightsRows(cols, role) + '</tbody></table></div></section>' +
      '<section class="panel" id="users" aria-labelledby="usTitle"><div class="panel-head"><div><h2 id="usTitle">Users</h2><p>Fictional users named by position, never by person. Each belongs to one organisation and one deployment. "View as this user" switches to that user\'s role.</p></div><div class="panel-actions" id="usNew"></div></div><div id="usList"></div></section>' +
      '<section class="panel" id="audit" aria-labelledby="auTitle"><div class="panel-head"><div><h2 id="auTitle">Audit log</h2><p>Every create, sign, publish, calibration and acknowledgement, in simulated time. Each deployment keeps its own log; the demo presenter sees every entry.</p></div></div><div id="auList"></div></section>';
    document.getElementById('usNew').innerHTML = U.createBtn('user', 'Add user', 'usBtn');
    var ub = document.getElementById('usBtn');
    if (!ub.disabled) ub.addEventListener('click', function () { openUser(ub); });
    document.getElementById('usList').addEventListener('click', function (e) {
      var b = e.target.closest('[data-viewas]');
      if (b) { var u = U.list('users').filter(function (x) { return x.id === b.getAttribute('data-viewas'); })[0]; if (u) { U.setRole(u.role); U.status('Viewing as ' + u.name + ' (' + U.roleName() + ').'); var h = document.getElementById('usTitle'); if (h) { h.setAttribute('tabindex', '-1'); h.focus(); } } }
    });
    document.getElementById('roleCards').addEventListener('click', function (e) {
      var b = e.target.closest('[data-role]');
      if (b) U.setRole(b.getAttribute('data-role'));
    });
  }

  function rightsRows(cols, role) {
    var list = U.CREATE_TYPES.map(function (t) { return [t[1], t[2], function (c) { return U.createRight(t[0], c) ? (U.CREATE_SCOPE[t[0]][c] || 'Yes') : null; }]; });
    list.push(['Raise SLA disputes', 'sla.html', function (c) { return c === 'hbm' || c === 'demo' ? 'Handler B bins' : null; }]);
    list.push(['Decide SLA disputes', 'sla.html', function (c) { return c === 'tdm' || c === 'demo' ? 'All disputes' : null; }]);
    return list.map(function (r) {
      return '<tr><th scope="row"><a href="' + r[1] + '">' + U.esc(r[0]) + '</a></th>' + cols.map(function (c) {
        var v = r[2](c);
        return '<td' + (c === role ? ' class="row-hl"' : '') + '>' + (v ? U.st('good', v) : U.st('unknown', 'No')) + '</td>';
      }).join('') + '</tr>';
    }).join('');
  }

  var ORG_VIEW = { demo: null, bss: ['border'], tdm: ['airport', 'handlerB'], hbm: ['handlerB'] };
  var AUDIT_VIEW = { demo: null, bss: ['Border shift supervisor'], tdm: ['Terminal duty manager', 'Handler B station manager'], hbm: ['Handler B station manager'] };
  var SIGNIN = ['Directory single sign-on', 'Smart card', 'Password with MFA'];

  function usersHtml() {
    var ov = ORG_VIEW[U.role()];
    var list = U.list('users').filter(function (u) { return !ov || ov.indexOf(u.org) >= 0; });
    return '<div class="table-wrap"><table class="tbl stack-sm"><thead><tr><th scope="col">ID</th><th scope="col">Display name</th><th scope="col">Organisation</th><th scope="col">Role</th><th scope="col">Deployment</th><th scope="col">Sign-in</th><th scope="col">Expires</th><th scope="col">Actions</th></tr></thead><tbody>' +
      list.map(function (u) {
        var cur = u.role === U.role();
        return '<tr' + U.rowCls(u.id) + '><th scope="row" class="mono">' + u.id + '</th><td>' + U.esc(u.name) + (u.seeded ? '' : '<div class="small muted">Added by ' + U.esc(u.by) + ' at ' + U.hhmm(u.at) + '</div>') + '</td><td>' + U.esc(U.ORG[u.org]) + '</td><td>' + U.esc(U.roleName(u.role)) + '</td>' +
          '<td class="small">' + U.esc(U.ORG_DEPLOY[u.org]) + '</td><td class="small">' + U.esc(u.signin) + '</td><td class="mono small">' + (u.expiry || 'None') + '</td>' +
          '<td class="actions">' + (cur ? '<span class="small muted">Current role</span>' : '<button type="button" class="btn" data-viewas="' + u.id + '">View as this user</button>') + '</td></tr>';
      }).join('') + '</tbody></table></div>' + (ov ? '<p class="small muted" style="margin-block-start:6px">Users of the ' + (U.role() === 'bss' ? 'border' : 'airport') + ' deployment' + (U.role() === 'hbm' ? ', Handler B only' : '') + '. Other organisations\' users are managed in their own deployment.</p>' : '');
  }

  function auditHtml() {
    var av = AUDIT_VIEW[U.role()];
    var list = U.list('audit').filter(function (a) { return !av || av.indexOf(a.role) >= 0; }).slice();
    list.sort(function (a, b) { return a.date < b.date ? 1 : a.date > b.date ? -1 : b.t - a.t; });
    if (!list.length) return '<p class="small muted">No entries for this role yet.</p>';
    return '<p class="small" data-audit-count>' + list.length + ' entr' + (list.length === 1 ? 'y' : 'ies') + ', newest first.</p><div class="table-wrap scroll-y" tabindex="0" aria-label="Audit log, scrollable"><table class="tbl stack-sm"><thead><tr><th scope="col">When (simulated)</th><th scope="col">Role</th><th scope="col">Action</th><th scope="col">Object</th><th scope="col">Summary</th></tr></thead><tbody>' +
      list.map(function (a) {
        return '<tr><th scope="row" class="mono small">' + a.date + ' ' + U.hhmm(a.t) + '</th><td class="small">' + U.esc(a.role) + '</td><td>' + U.esc(a.action) + '</td><td class="mono">' + U.esc(a.id) + '</td><td class="small">' + U.esc(a.summary) + '</td></tr>';
      }).join('') + '</tbody></table></div>';
  }

  function openUser(trig) {
    var F = U.F, id = U.nextId('user');
    function roleOpts(org) {
      return [['bss', 'bss'], ['tdm', 'tdm'], ['hbm', 'hbm']].map(function (r) {
        var ok = U.ORG_ROLES[org].indexOf(r[0]) >= 0;
        return [r[0], U.roleName(r[0]) + (ok ? '' : r[0] === 'bss' ? ' (border deployment only)' : r[0] === 'tdm' ? ' (airport operator only)' : ' (Handler B only)'), !ok];
      });
    }
    var html = F.text('uid', 'User ID', id, { readonly: true }) +
      F.text('name', 'Display name', '', { req: true, placeholder: 'For example: Arrivals supervisor, shift C', hint: 'Fictional and named by position. Never a real person\'s name.' }) +
      F.select('org', 'Organisation', [['border', U.ORG.border], ['airport', U.ORG.airport], ['handlerB', U.ORG.handlerB]], 'border') +
      '<div data-role-f>' + F.select('role', 'Role', roleOpts('border'), 'bss') + '</div>' +
      F.text('deploy', 'Deployment', U.ORG_DEPLOY.border, { readonly: true, hint: '<span id="depWhy"></span>' }) +
      F.row(F.select('signin', 'Sign-in method', SIGNIN.map(function (x) { return [x, x]; }), SIGNIN[0]), F.text('expiry', 'Expiry (optional)', '', { placeholder: 'YYYY-MM-DD', inputmode: 'numeric' }));
    var WHY = {
      border: 'Derived from the organisation. The border deployment holds no check-in, handler or SLA data, so a border user cannot hold an airport role.',
      airport: 'Derived from the organisation. The airport deployment receives border waits as lane aggregates only, so an airport user cannot hold a border role.',
      handlerB: 'Derived from the organisation. Handler B users sign in to the airport deployment through a handler view limited to islands C and D.'
    };
    U.openDrawer({
      title: 'Add user', html: html, trigger: trig, returnFocus: 'usBtn',
      onOpen: function (form) {
        function sync() {
          var org = form.querySelector('#f-org').value;
          form.querySelector('#f-deploy').value = U.ORG_DEPLOY[org];
          document.getElementById('depWhy').textContent = WHY[org];
        }
        form.addEventListener('change', function (e) {
          if (e.target.name === 'org') {
            var org = e.target.value;
            form.querySelector('[data-role-f]').innerHTML = F.select('role', 'Role', roleOpts(org), U.ORG_ROLES[org][0]);
          }
          sync();
        });
        sync();
      },
      onSubmit: function (v) {
        var e = {};
        if (!v.name) e.name = 'Enter a display name.';
        else if (v.name.length < 3 || v.name.length > 60) e.name = 'Use 3 to 60 characters.';
        else if (/@/.test(v.name)) e.name = 'Use a position name, not an email address.';
        else if (U.list('users').some(function (u) { return u.name.toLowerCase() === v.name.toLowerCase(); })) e.name = 'A user with this display name already exists.';
        if (U.ORG_ROLES[v.org].indexOf(v.role) < 0) e.role = U.roleName(v.role) + ' is not a role of ' + U.ORG[v.org] + '. ' + WHY[v.org];
        if (v.expiry) {
          if (!U.V.date(v.expiry)) e.expiry = 'Enter the date as YYYY-MM-DD.';
          else if (v.expiry <= QSim.DATE) e.expiry = 'The expiry must be after today (' + QSim.DATE + ').';
        }
        if (Object.keys(e).length) return e;
        U.state.c.users.push({ id: id, name: v.name, org: v.org, role: v.role, signin: v.signin, expiry: v.expiry || '', by: U.roleName(), at: U.now() });
        U.created('user', id, v.name + ', ' + U.ORG[v.org] + ', ' + U.roleName(v.role) + ' (' + U.ORG_DEPLOY[v.org] + ')', function () { U.removeFrom('users', id); }, function () { U.draw(true); });
        return null;
      }
    });
  }

  function update() {
    U.html(document.getElementById('usList'), usersHtml());
    U.html(document.getElementById('auList'), auditHtml());
  }

  QUI.start({ build: build, update: update });
})();
