/*
  boot.js: loaded in the head of every prototype page. Provides QStore, the demo's
  small state store: localStorage (keys prefixed qms.demo.) mirrored into window.name,
  so the demo keeps its clock, role and choices across pages even when storage is
  blocked or slow. Every access is guarded. Also applies the saved theme early.
*/
(function (g) {
  'use strict';
  var PREFIX = 'qms.demo.', NM = 'qmsdemo:';
  var has = Object.prototype.hasOwnProperty;
  var mirror = null;
  try { if (typeof g.name === 'string' && g.name.indexOf(NM) === 0) mirror = JSON.parse(g.name.slice(NM.length)); } catch (e) { mirror = null; }
  if (!mirror || typeof mirror !== 'object' || !mirror.s || typeof mirror.s !== 'object') mirror = { t: 0, s: {}, d: {}, c: false };
  if (!mirror.d) mirror.d = {};
  var lsT = 0;
  try { lsT = Number(g.localStorage.getItem(PREFIX + '_t')) || 0; } catch (e) { lsT = 0; }
  var preferMirror = mirror.t > lsT;

  function writeMirror() {
    mirror.t = Date.now();
    try { g.name = NM + JSON.stringify(mirror); } catch (e) { }
  }
  function lsSet(k, v) { try { g.localStorage.setItem(PREFIX + k, v); } catch (e) { } }
  function lsGet(k) { try { return g.localStorage.getItem(PREFIX + k); } catch (e) { return null; } }

  var Q = {
    get: function (k, def) {
      if (preferMirror) {
        if (has.call(mirror.s, k)) return mirror.s[k];
        if (mirror.d[k] || mirror.c) return def;
      }
      var v = lsGet(k);
      if (v != null) { try { return JSON.parse(v); } catch (e) { return def; } }
      if (has.call(mirror.s, k)) return mirror.s[k];
      return def;
    },
    set: function (k, v) {
      mirror.s[k] = v;
      delete mirror.d[k];
      writeMirror();
      lsSet(k, JSON.stringify(v));
      lsSet('_t', String(mirror.t));
    },
    del: function (k) {
      delete mirror.s[k];
      mirror.d[k] = 1;
      writeMirror();
      try { g.localStorage.removeItem(PREFIX + k); } catch (e) { }
      lsSet('_t', String(mirror.t));
    },
    clear: function (keep) {
      var kept = {};
      (keep || []).forEach(function (k) { var v = Q.get(k, null); if (v != null) kept[k] = v; });
      try {
        var keys = [];
        for (var i = 0; i < g.localStorage.length; i++) { var key = g.localStorage.key(i); if (key && key.indexOf(PREFIX) === 0) keys.push(key); }
        keys.forEach(function (key) { g.localStorage.removeItem(key); });
      } catch (e) { }
      mirror = { t: 0, s: {}, d: {}, c: true };
      preferMirror = true;
      writeMirror();
      for (var k in kept) Q.set(k, kept[k]);
      lsSet('_t', String(mirror.t));
    }
  };
  g.QStore = Q;
  if (Q.get('theme', 'dark') === 'light') document.documentElement.setAttribute('data-theme', 'light');
})(window);
