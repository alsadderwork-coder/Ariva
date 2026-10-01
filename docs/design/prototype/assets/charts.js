/*
  QCharts: small SVG chart helpers shared by the business plan (Part 1)
  and the product prototype (Part 2). No dependencies, works from file://.

  Colours come from CSS custom properties with safe fallbacks:
    --chart-ink, --chart-muted, --chart-grid, --chart-surface,
    --chart-highlight (shaded layer or phase), --chart-band (forecast band),
    --c1 .. --c6 (series), --good, --warn, --crit
  Each part's stylesheet defines them for light and dark themes.

  API (all functions take a host element or a CSS selector, and an options
  object; charts re-render when the host width changes):
    QCharts.line(host, o)          line chart, optional bands, reference lines
    QCharts.stackedBars(host, o)   stacked bars, horizontal or vertical
    QCharts.hbars(host, o)         horizontal bars
    QCharts.stackedArea(host, o)   stacked areas
    QCharts.marketMap(host, o)     layered market map with adjacent market
    QCharts.matrix(host, o)        2x2 matrix
    QCharts.roadmap(host, o)       phase roadmap with gates
    QCharts.sparkline(host, values, o)
  Helpers: QCharts.fmt, QCharts.niceTicks, QCharts.svg, QCharts.wrap, QCharts.textWidth
*/
(function (global) {
  'use strict';

  var NS = 'http://www.w3.org/2000/svg';
  var uid = 0;

  /* basics */

  function injectStyle() {
    if (document.getElementById('qcharts-style')) return;
    var s = document.createElement('style');
    s.id = 'qcharts-style';
    s.textContent =
      '.qc{display:block;max-width:100%;height:auto;overflow:visible;font-family:inherit}' +
      '.qc text{fill:var(--chart-ink,#1d2730);font-size:12px;font-variant-numeric:tabular-nums}' +
      '.qc .qc-muted{fill:var(--chart-muted,#5f6d79)}' +
      '.qc .qc-small{font-size:11px}' +
      '.qc .qc-strong{font-weight:600}' +
      '.qc .qc-big{font-size:14px;font-weight:600}' +
      '.qc .qc-grid{stroke:var(--chart-grid,#dde3e8);stroke-width:1;shape-rendering:crispEdges}' +
      '.qc .qc-axis{stroke:var(--chart-muted,#5f6d79);stroke-width:1;shape-rendering:crispEdges}' +
      '.qc .qc-zero{stroke:var(--chart-ink,#1d2730);stroke-width:1.25}' +
      '.qc .qc-box{fill:var(--chart-surface,#ffffff);stroke:var(--chart-grid,#dde3e8)}' +
      '.qc .qc-hl{fill:var(--chart-highlight,#e6f0f4)}' +
      '.qc .qc-chip{fill:var(--chart-surface,#ffffff);stroke:var(--chart-muted,#5f6d79);stroke-opacity:.45}' +
      '.qc-host{width:100%;min-width:0}';
    (document.head || document.documentElement).appendChild(s);
  }

  function svg(tag, attrs, parent) {
    var n = document.createElementNS(NS, tag);
    if (attrs) {
      for (var k in attrs) {
        if (!Object.prototype.hasOwnProperty.call(attrs, k) || attrs[k] == null) continue;
        if (k === 'text') n.textContent = attrs[k];
        else if (k === 'cls') n.setAttribute('class', attrs[k]);
        else n.setAttribute(k, attrs[k]);
      }
    }
    if (parent) parent.appendChild(n);
    return n;
  }

  function fmt(v, o) {
    o = o || {};
    if (v == null || typeof v !== 'number' || isNaN(v)) return o.empty || 'n/a';
    var d = o.decimals != null ? o.decimals : 0;
    var fixed = Math.abs(v).toFixed(d);
    if (Number(fixed) === 0) v = 0;
    var parts = fixed.split('.');
    parts[0] = parts[0].replace(/\B(?=(\d{3})+(?!\d))/g, ',');
    return (v < 0 ? '-' : '') + (o.prefix || '') + parts.join('.') + (o.suffix || '');
  }

  function textWidth(s, size) {
    return String(s == null ? '' : s).length * (size || 12) * 0.56;
  }

  function wrap(text, maxWidth, size) {
    var words = String(text || '').split(/\s+/), lines = [], cur = '';
    for (var i = 0; i < words.length; i++) {
      var tryLine = cur ? cur + ' ' + words[i] : words[i];
      if (cur && textWidth(tryLine, size) > maxWidth) { lines.push(cur); cur = words[i]; }
      else cur = tryLine;
    }
    if (cur) lines.push(cur);
    return lines;
  }

  function niceStep(range, count) {
    var raw = range / Math.max(1, count);
    if (!(raw > 0)) return 1;
    var mag = Math.pow(10, Math.floor(Math.log(raw) / Math.LN10));
    var norm = raw / mag;
    var step = norm < 1.5 ? 1 : norm < 3 ? 2 : norm < 7 ? 5 : 10;
    return step * mag;
  }

  function niceTicks(min, max, count) {
    if (!(isFinite(min) && isFinite(max))) { min = 0; max = 1; }
    if (min === max) { max = min + 1; }
    var step = niceStep(max - min, count || 5);
    var lo = Math.floor(min / step + 1e-9) * step;
    var hi = Math.ceil(max / step - 1e-9) * step;
    var out = [];
    for (var v = lo; v <= hi + step * 1e-6; v += step) out.push(Number(v.toFixed(10)));
    return out;
  }

  function resolveHost(target) {
    return typeof target === 'string' ? document.querySelector(target) : target;
  }

  /* mount: remembers the latest draw function and re-renders on width change */
  function mount(target, draw) {
    var host = resolveHost(target);
    if (!host) return null;
    injectStyle();
    host.classList.add('qc-host');
    host.__qcDraw = draw;
    if (!host.__qcRender) {
      host.__qcRender = function () {
        var w = Math.floor(host.clientWidth || (host.getBoundingClientRect && host.getBoundingClientRect().width) || 0);
        if (w < 10) w = 640;
        w = Math.max(260, w);
        host.__qcWidth = w;
        while (host.firstChild) host.removeChild(host.firstChild);
        host.__qcDraw(host, w);
      };
      if (typeof ResizeObserver !== 'undefined') {
        var raf = 0;
        new ResizeObserver(function () {
          var w = Math.floor(host.clientWidth);
          if (w < 10 || Math.abs(w - (host.__qcWidth || 0)) < 4) return;
          cancelAnimationFrame(raf);
          raf = requestAnimationFrame(host.__qcRender);
        }).observe(host);
      }
    }
    host.__qcRender();
    return host;
  }

  function root(host, w, h, o) {
    var id = 'qc' + (++uid);
    var label = (o.title || 'Chart') + (o.desc ? '. ' + o.desc : '');
    var s = svg('svg', {
      cls: 'qc', width: w, height: h, viewBox: '0 0 ' + w + ' ' + h,
      role: 'img', 'aria-label': label, focusable: 'false'
    }, host);
    svg('title', { id: id + 't', text: o.title || 'Chart' }, s);
    if (o.desc) svg('desc', { id: id + 'd', text: o.desc }, s);
    return s;
  }

  function spread(items, minGap, lo, hi) {
    /* items: [{y}] sorted in place by y; pushes labels apart vertically */
    items.sort(function (a, b) { return a.y - b.y; });
    for (var i = 1; i < items.length; i++) {
      if (items[i].y - items[i - 1].y < minGap) items[i].y = items[i - 1].y + minGap;
    }
    var over = items.length ? items[items.length - 1].y - hi : 0;
    if (over > 0) {
      for (var j = items.length - 1; j >= 0; j--) {
        items[j].y -= over;
        if (j > 0 && items[j].y - items[j - 1].y >= minGap) break;
        over = j > 0 ? items[j - 1].y - (items[j].y - minGap) : 0;
        if (over <= 0) break;
      }
    }
    for (var k = 0; k < items.length; k++) if (items[k].y < lo) items[k].y = lo;
    return items;
  }

  function xTickList(o, xmin, xmax, plotW) {
    var ticks;
    if (o.x && o.x.ticks) ticks = o.x.ticks.map(function (t) { return typeof t === 'object' ? t : { v: t }; });
    else ticks = niceTicks(xmin, xmax, Math.max(2, Math.floor(plotW / 90))).filter(function (v) { return v >= xmin && v <= xmax; }).map(function (v) { return { v: v }; });
    var f = (o.x && o.x.format) || function (v) { return fmt(v); };
    ticks.forEach(function (t) { if (t.label == null) t.label = f(t.v); });
    var maxW = 0;
    ticks.forEach(function (t) { maxW = Math.max(maxW, textWidth(t.label, 11)); });
    var room = ticks.length > 1 ? plotW / (ticks.length - 1) : plotW;
    var every = Math.max(1, Math.ceil((maxW + 10) / Math.max(1, room)));
    return ticks.filter(function (t, i) { return i % every === 0; });
  }

  /* line chart */
  /*
    o = {
      title, desc, height,
      x: { min, max, ticks: [v | {v,label}], format(v), label },
      y: { min, max, ticks: [..], count, format(v), label },
      series: [{ label, endText, points: [[x,y|null],...], color, dash, width, step, dots, endLabel }],
      bands:  [{ points: [[x,lo,hi],...], color, opacity, label }],
      hlines: [{ y, label, color, dash, strong }],
      vlines: [{ x, label, color, dash }],
      markers:[{ x, y, label, color }]
    }
  */
  function line(target, o) {
    return mount(target, function (host, W) {
      var H = o.height || 260;
      var series = o.series || [], bands = o.bands || [], hlines = o.hlines || [];
      var xs = [], ys = [];
      series.forEach(function (s) { s.points.forEach(function (p) { if (p[1] != null) { xs.push(p[0]); ys.push(p[1]); } }); });
      bands.forEach(function (b) { b.points.forEach(function (p) { xs.push(p[0]); if (p[1] != null) ys.push(p[1]); if (p[2] != null) ys.push(p[2]); }); });
      hlines.forEach(function (l) { ys.push(l.y); });
      if (!xs.length) { xs = [0, 1]; }
      if (!ys.length) { ys = [0, 1]; }
      var xmin = o.x && o.x.min != null ? o.x.min : Math.min.apply(null, xs);
      var xmax = o.x && o.x.max != null ? o.x.max : Math.max.apply(null, xs);
      if (xmax === xmin) xmax = xmin + 1;
      var yminD = Math.min.apply(null, ys), ymaxD = Math.max.apply(null, ys);
      if (o.y && o.y.min != null) yminD = o.y.min;
      if (o.y && o.y.max != null) ymaxD = o.y.max;
      var yTicks = (o.y && o.y.ticks) || niceTicks(yminD, ymaxD, (o.y && o.y.count) || Math.max(3, Math.min(6, Math.floor(H / 50))));
      var ymin = o.y && o.y.min != null ? o.y.min : Math.min(yTicks[0], yminD);
      var ymax = o.y && o.y.max != null ? o.y.max : Math.max(yTicks[yTicks.length - 1], ymaxD);
      yTicks = yTicks.filter(function (v) { return v >= ymin - 1e-9 && v <= ymax + 1e-9; });
      var yf = (o.y && o.y.format) || function (v) { return fmt(v); };

      var endW = 0, legendMode = false, legendItems = [];
      if (o.endLabels !== false) {
        series.forEach(function (s) {
          if (s.label && s.endLabel !== false) {
            endW = Math.max(endW, textWidth(s.endText || s.label, 12) * 1.08 + 12);
            legendItems.push({ label: s.endText || s.label, color: s.color || 'var(--c1,#1f6f8b)' });
          }
        });
      }
      if (endW > W * 0.42) { legendMode = true; endW = 0; }
      var legH = legendMode ? legendHeight(legendItems, W) : 0;
      var yLabW = 0;
      yTicks.forEach(function (v) { yLabW = Math.max(yLabW, textWidth(yf(v), 11)); });
      H += legH;
      var m = { t: legH + ((o.y && o.y.label) ? 24 : 12), r: Math.max(14, endW), b: 26 + ((o.x && o.x.label) ? 16 : 0), l: Math.ceil(yLabW) + 12 };
      var pw = W - m.l - m.r, ph = H - m.t - m.b;
      var X = function (v) { return m.l + (v - xmin) / (xmax - xmin) * pw; };
      var Y = function (v) { return m.t + (1 - (v - ymin) / (ymax - ymin)) * ph; };
      var s = root(host, W, H, o);
      if (legendMode) drawLegend(s, legendItems, 0, 2, W);

      if (o.y && o.y.label) svg('text', { x: m.l, y: legH + 12, cls: 'qc-muted qc-small', text: o.y.label }, s);
      yTicks.forEach(function (v) {
        var y = Math.round(Y(v)) + 0.5;
        svg('line', { x1: m.l, x2: m.l + pw, y1: y, y2: y, cls: v === 0 && ymin < 0 ? 'qc-zero' : 'qc-grid' }, s);
        svg('text', { x: m.l - 6, y: y + 4, 'text-anchor': 'end', cls: 'qc-muted qc-small', text: yf(v) }, s);
      });
      var xt = xTickList(o, xmin, xmax, pw);
      svg('line', { x1: m.l, x2: m.l + pw, y1: m.t + ph + 0.5, y2: m.t + ph + 0.5, cls: 'qc-axis' }, s);
      xt.forEach(function (t) {
        var x = X(t.v);
        svg('line', { x1: x, x2: x, y1: m.t + ph, y2: m.t + ph + 4, cls: 'qc-axis' }, s);
        var anchor = x < m.l + 12 ? 'start' : x > m.l + pw - 12 ? 'end' : 'middle';
        svg('text', { x: x, y: m.t + ph + 17, 'text-anchor': anchor, cls: 'qc-muted qc-small', text: t.label }, s);
      });
      if (o.x && o.x.label) svg('text', { x: m.l + pw, y: H - 4, 'text-anchor': 'end', cls: 'qc-muted qc-small', text: o.x.label }, s);

      bands.forEach(function (b) {
        var pts = b.points.filter(function (p) { return p[1] != null && p[2] != null; });
        if (pts.length < 2) return;
        var d = 'M' + pts.map(function (p) { return X(p[0]).toFixed(1) + ',' + Y(p[2]).toFixed(1); }).join('L');
        d += 'L' + pts.slice().reverse().map(function (p) { return X(p[0]).toFixed(1) + ',' + Y(p[1]).toFixed(1); }).join('L') + 'Z';
        svg('path', { d: d, style: 'fill:' + (b.color || 'var(--chart-band,#8fb8d0)') + ';fill-opacity:' + (b.opacity != null ? b.opacity : 0.28) + ';stroke:none' }, s);
        if (b.label) {
          var last = pts[pts.length - 1];
          svg('text', { x: X(last[0]) - 4, y: Y(last[2]) - 5, 'text-anchor': 'end', cls: 'qc-muted qc-small', text: b.label }, s);
        }
      });

      (o.vlines || []).forEach(function (l) {
        var x = Math.round(X(l.x)) + 0.5;
        svg('line', { x1: x, x2: x, y1: m.t, y2: m.t + ph, style: 'stroke:' + (l.color || 'var(--chart-muted,#5f6d79)') + ';stroke-width:1;' + (l.dash ? 'stroke-dasharray:' + l.dash : '') }, s);
        if (l.label) svg('text', { x: x + 4, y: m.t + 11, cls: 'qc-muted qc-small', text: l.label }, s);
      });

      hlines.forEach(function (l) {
        var y = Math.round(Y(l.y)) + 0.5;
        svg('line', { x1: m.l, x2: m.l + pw, y1: y, y2: y, style: 'stroke:' + (l.color || 'var(--chart-ink,#1d2730)') + ';stroke-width:' + (l.strong ? 1.5 : 1.2) + ';' + (l.dash ? 'stroke-dasharray:' + l.dash : '') }, s);
        if (l.label) svg('text', { x: l.align === 'left' ? m.l + 4 : m.l + pw - 4, y: y - 5, 'text-anchor': l.align === 'left' ? 'start' : 'end', cls: 'qc-small qc-strong', text: l.label }, s);
      });

      var ends = [];
      series.forEach(function (sr) {
        var d = '', pen = false, prev = null;
        sr.points.forEach(function (p) {
          if (p[1] == null) { pen = false; prev = null; return; }
          var x = X(p[0]).toFixed(1), y = Y(p[1]).toFixed(1);
          if (!pen) { d += 'M' + x + ',' + y; pen = true; }
          else if (sr.step) d += 'H' + x + 'V' + y;
          else d += 'L' + x + ',' + y;
          prev = p;
        });
        if (!d) return;
        var col = sr.color || 'var(--c1,#1f6f8b)';
        svg('path', { d: d, style: 'fill:none;stroke:' + col + ';stroke-width:' + (sr.width || 2) + ';stroke-linejoin:round;stroke-linecap:round;' + (sr.dash ? 'stroke-dasharray:' + sr.dash : '') }, s);
        if (sr.dots) sr.points.forEach(function (p) { if (p[1] != null) svg('circle', { cx: X(p[0]), cy: Y(p[1]), r: 3, style: 'fill:' + col }, s); });
        var lastP = null;
        for (var i = sr.points.length - 1; i >= 0; i--) if (sr.points[i][1] != null) { lastP = sr.points[i]; break; }
        if (lastP && sr.label && sr.endLabel !== false && o.endLabels !== false && !legendMode) {
          ends.push({ y: Y(lastP[1]) + 4, x: X(lastP[0]), text: sr.endText || sr.label, color: col });
        }
      });
      spread(ends, 14, m.t + 8, m.t + ph + 4).forEach(function (e) {
        svg('text', { x: e.x + 6, y: e.y, cls: 'qc-strong', style: 'fill:' + e.color, text: e.text }, s);
      });

      (o.markers || []).forEach(function (mk) {
        var cx = X(mk.x), cy = Y(mk.y);
        svg('circle', { cx: cx, cy: cy, r: 4.5, style: 'fill:' + (mk.color || 'var(--chart-ink,#1d2730)') + ';stroke:var(--chart-surface,#fff);stroke-width:1.5' }, s);
        if (mk.label) svg('text', { x: cx + (mk.align === 'left' ? -8 : 8), y: cy - 8, 'text-anchor': mk.align === 'left' ? 'end' : 'start', cls: 'qc-small qc-strong', text: mk.label }, s);
      });
    });
  }

  /* stacked bars */
  /*
    o = { title, desc, orientation: 'h' | 'v', categories: [..],
          series: [{ label, values: [..], color }], format(v), unit, height, totals: true }
  */
  function stackedBars(target, o) {
    return mount(target, function (host, W) {
      var cats = o.categories || [''], series = o.series || [];
      var f = o.format || function (v) { return fmt(v); };
      var totals = cats.map(function (_, i) { return series.reduce(function (a, sr) { return a + (sr.values[i] || 0); }, 0); });
      var maxT = Math.max.apply(null, totals.concat([1]));
      var horizontal = o.orientation !== 'v';
      var s, legendNeeded = false;

      if (horizontal) {
        var catW = 0;
        cats.forEach(function (c) { catW = Math.max(catW, textWidth(c, 12)); });
        catW = cats.length === 1 && !cats[0] ? 0 : Math.min(catW + 12, W * 0.3);
        var totW = o.totals === false ? 0 : textWidth(f(maxT), 12) + 14;
        var barH = o.barHeight || 34, gap = 18;
        var pw = W - catW - totW - 4;
        /* check whether segment labels fit inside */
        cats.forEach(function (c, i) {
          series.forEach(function (sr) {
            var segW = (sr.values[i] || 0) / maxT * pw;
            var need = Math.max(textWidth(sr.label, 11) * 1.1, textWidth(f(sr.values[i] || 0), 11)) + 16;
            if ((sr.values[i] || 0) > 0 && segW < need) legendNeeded = true;
          });
        });
        var legendH = legendNeeded ? legendHeight(series, W) : 0;
        var H = o.height || (cats.length * (barH + gap) - gap + 8 + legendH + (o.unit ? 18 : 0));
        s = root(host, W, H, o);
        var top = legendH + (o.unit ? 18 : 0);
        if (o.unit) svg('text', { x: catW, y: legendH + 12, cls: 'qc-muted qc-small', text: o.unit }, s);
        if (legendNeeded) drawLegend(s, series, 0, 2, W);
        cats.forEach(function (c, i) {
          var y = top + i * (barH + gap), x = catW;
          if (catW) svg('text', { x: catW - 8, y: y + barH / 2 + 4, 'text-anchor': 'end', text: c }, s);
          series.forEach(function (sr) {
            var v = sr.values[i] || 0, w = v / maxT * pw;
            if (w <= 0) return;
            svg('rect', { x: x, y: y, width: Math.max(0, w - 1), height: barH, style: 'fill:' + sr.color }, s);
            if (!legendNeeded) {
              svg('text', { x: x + 6, y: y + 14, cls: 'qc-small qc-strong', style: 'fill:var(--chart-on-color,#fff)', text: sr.label }, s);
              svg('text', { x: x + 6, y: y + 28, cls: 'qc-small', style: 'fill:var(--chart-on-color,#fff)', text: f(v) }, s);
            }
            x += w;
          });
          if (o.totals !== false) svg('text', { x: x + 8, y: y + barH / 2 + 4, cls: 'qc-strong', text: f(totals[i]) }, s);
        });
      } else {
        var H2 = o.height || 260;
        var yT = niceTicks(0, maxT, 4);
        var ymax = yT[yT.length - 1];
        var yLab = 0;
        yT.forEach(function (v) { yLab = Math.max(yLab, textWidth(f(v), 11)); });
        legendNeeded = true;
        var lh = legendHeight(series, W);
        var m = { t: lh + (o.unit ? 18 : 6) + 10, r: 8, b: 26, l: yLab + 12 };
        var pw2 = W - m.l - m.r, ph = H2 - m.t - m.b;
        s = root(host, W, H2, o);
        drawLegend(s, series, 0, 2, W);
        if (o.unit) svg('text', { x: m.l, y: lh + 14, cls: 'qc-muted qc-small', text: o.unit }, s);
        yT.forEach(function (v) {
          var y = Math.round(m.t + (1 - v / ymax) * ph) + 0.5;
          svg('line', { x1: m.l, x2: m.l + pw2, y1: y, y2: y, cls: 'qc-grid' }, s);
          svg('text', { x: m.l - 6, y: y + 4, 'text-anchor': 'end', cls: 'qc-muted qc-small', text: f(v) }, s);
        });
        var slot = pw2 / cats.length, bw = Math.min(56, slot * 0.62);
        cats.forEach(function (c, i) {
          var cx = m.l + slot * i + slot / 2, yb = m.t + ph;
          series.forEach(function (sr) {
            var v = sr.values[i] || 0, h = v / ymax * ph;
            if (h <= 0) return;
            svg('rect', { x: cx - bw / 2, y: yb - h, width: bw, height: Math.max(0, h - 1), style: 'fill:' + sr.color }, s);
            yb -= h;
          });
          if (o.totals !== false) svg('text', { x: cx, y: yb - 5, 'text-anchor': 'middle', cls: 'qc-small qc-strong', text: f(totals[i]) }, s);
          svg('text', { x: cx, y: m.t + ph + 17, 'text-anchor': 'middle', cls: 'qc-muted qc-small', text: c }, s);
        });
      }
    });
  }

  function legendHeight(series, W) {
    var x = 0, rows = 1;
    series.forEach(function (sr) {
      var w = textWidth(sr.label, 11) + 26;
      if (x + w > W && x > 0) { rows++; x = 0; }
      x += w;
    });
    return rows * 18 + 4;
  }

  function drawLegend(s, series, x0, y0, W) {
    var x = x0, y = y0;
    series.forEach(function (sr) {
      var w = textWidth(sr.label, 11) + 26;
      if (x + w > W && x > x0) { x = x0; y += 18; }
      svg('rect', { x: x, y: y + 3, width: 10, height: 10, rx: 2, style: 'fill:' + sr.color }, s);
      svg('text', { x: x + 15, y: y + 12, cls: 'qc-small', text: sr.label }, s);
      x += w;
    });
  }

  /* horizontal bars */
  /* o = { title, desc, items: [{ label, value, color, valueText }], format(v), max, unit } */
  function hbars(target, o) {
    return mount(target, function (host, W) {
      var items = o.items || [];
      var f = o.format || function (v) { return fmt(v); };
      var narrow = W < 460;
      var labW = 0;
      items.forEach(function (it) { labW = Math.max(labW, textWidth(it.label, 12)); });
      labW = narrow ? 0 : Math.min(labW + 12, W * 0.38);
      var valW = 0;
      items.forEach(function (it) { valW = Math.max(valW, textWidth(it.valueText || f(it.value), 12)); });
      valW += 10;
      var rowH = narrow ? 42 : 28, barH = 16;
      var H = items.length * rowH + (o.unit ? 18 : 0) + 4;
      var s = root(host, W, H, o);
      var top = o.unit ? 18 : 0;
      if (o.unit) svg('text', { x: labW, y: 12, cls: 'qc-muted qc-small', text: o.unit }, s);
      var max = o.max || Math.max.apply(null, items.map(function (it) { return it.value; }).concat([1]));
      var pw = W - labW - valW - 4;
      items.forEach(function (it, i) {
        var y = top + i * rowH;
        var by = narrow ? y + 18 : y + (rowH - barH) / 2;
        if (narrow) svg('text', { x: 0, y: y + 13, cls: 'qc-small', text: it.label }, s);
        else svg('text', { x: labW - 8, y: by + 12, 'text-anchor': 'end', text: it.label }, s);
        var w = Math.max(1, it.value / max * pw);
        svg('rect', { x: labW, y: by, width: w, height: barH, rx: 2, style: 'fill:' + (it.color || 'var(--c1,#1f6f8b)') }, s);
        svg('text', { x: labW + w + 6, y: by + 12, cls: 'qc-strong qc-small', text: it.valueText || f(it.value) }, s);
      });
    });
  }

  /* stacked area */
  /*
    o = { title, desc, height, xs: [x values], x: { ticks, format, label },
          y: { label, format }, series: [{ label, values: [..], color }] }
  */
  function stackedArea(target, o) {
    return mount(target, function (host, W) {
      var H = o.height || 260, xs = o.xs || [], series = o.series || [];
      var cum = xs.map(function () { return 0; });
      var layers = series.map(function (sr) {
        var lo = cum.slice();
        cum = cum.map(function (c, i) { return c + (sr.values[i] || 0); });
        return { sr: sr, lo: lo, hi: cum.slice() };
      });
      var ymaxD = Math.max.apply(null, cum.concat([1]));
      var yT = niceTicks(0, ymaxD, Math.max(3, Math.floor(H / 60)));
      var ymax = yT[yT.length - 1];
      var yf = (o.y && o.y.format) || function (v) { return fmt(v); };
      var yLab = 0;
      yT.forEach(function (v) { yLab = Math.max(yLab, textWidth(yf(v), 11)); });
      var endW = 0;
      series.forEach(function (sr) { endW = Math.max(endW, textWidth(sr.label, 11) + 10); });
      var useEnd = W >= 560;
      var lh = useEnd ? 0 : legendHeight(series, W);
      var m = { t: lh + ((o.y && o.y.label) ? 22 : 10), r: useEnd ? endW : 10, b: 26 + ((o.x && o.x.label) ? 16 : 0), l: yLab + 12 };
      var pw = W - m.l - m.r, ph = H - m.t - m.b;
      var xmin = xs[0], xmax = xs[xs.length - 1];
      if (xmax === xmin) xmax = xmin + 1;
      var X = function (v) { return m.l + (v - xmin) / (xmax - xmin) * pw; };
      var Y = function (v) { return m.t + (1 - v / ymax) * ph; };
      var s = root(host, W, H, o);
      if (!useEnd) drawLegend(s, series, 0, 2, W);
      if (o.y && o.y.label) svg('text', { x: m.l, y: lh + 12, cls: 'qc-muted qc-small', text: o.y.label }, s);
      yT.forEach(function (v) {
        var y = Math.round(Y(v)) + 0.5;
        svg('line', { x1: m.l, x2: m.l + pw, y1: y, y2: y, cls: 'qc-grid' }, s);
        svg('text', { x: m.l - 6, y: y + 4, 'text-anchor': 'end', cls: 'qc-muted qc-small', text: yf(v) }, s);
      });
      layers.forEach(function (L) {
        var d = 'M' + xs.map(function (x, i) { return X(x).toFixed(1) + ',' + Y(L.hi[i]).toFixed(1); }).join('L');
        d += 'L' + xs.slice().reverse().map(function (x, j) { var i = xs.length - 1 - j; return X(x).toFixed(1) + ',' + Y(L.lo[i]).toFixed(1); }).join('L') + 'Z';
        svg('path', { d: d, style: 'fill:' + L.sr.color + ';fill-opacity:.85;stroke:var(--chart-surface,#fff);stroke-width:.75' }, s);
      });
      if (useEnd) {
        var ends = layers.map(function (L) {
          var n = xs.length - 1, mid = (L.lo[n] + L.hi[n]) / 2;
          return { y: Y(mid) + 4, text: L.sr.label, color: L.sr.color };
        });
        spread(ends, 13, m.t + 6, m.t + ph).forEach(function (e) {
          svg('text', { x: m.l + pw + 6, y: e.y, cls: 'qc-small qc-strong', style: 'fill:' + e.color, text: e.text }, s);
        });
      }
      svg('line', { x1: m.l, x2: m.l + pw, y1: m.t + ph + 0.5, y2: m.t + ph + 0.5, cls: 'qc-axis' }, s);
      xTickList(o, xmin, xmax, pw).forEach(function (t) {
        var x = X(t.v);
        var anchor = x < m.l + 12 ? 'start' : x > m.l + pw - 12 ? 'end' : 'middle';
        svg('line', { x1: x, x2: x, y1: m.t + ph, y2: m.t + ph + 4, cls: 'qc-axis' }, s);
        svg('text', { x: x, y: m.t + ph + 17, 'text-anchor': anchor, cls: 'qc-muted qc-small', text: t.label }, s);
      });
      if (o.x && o.x.label) svg('text', { x: m.l + pw, y: H - 4, 'text-anchor': 'end', cls: 'qc-muted qc-small', text: o.x.label }, s);
    });
  }

  /* layered market map */
  /*
    o = { title, desc, layers: [{ name, items: [..], shaded }],
          adjacent: { name, items: [..] } }
    Layers are drawn top to bottom in the order given.
  */
  function marketMap(target, o) {
    return mount(target, function (host, W) {
      var layers = o.layers || [], adj = o.adjacent;
      var side = adj && W >= 680;
      var adjW = side ? Math.max(170, W * 0.24) : 0;
      var mainW = W - (side ? adjW + 16 : 0);
      var pad = 12, chipH = 24, chipGap = 6, numW = 30;

      function layout(items, width) {
        var rows = [], x = 0, row = [];
        items.forEach(function (t) {
          var w = textWidth(t, 12) + 18;
          if (x + w > width && row.length) { rows.push(row); row = []; x = 0; }
          row.push({ t: t, x: x, w: w });
          x += w + chipGap;
        });
        if (row.length) rows.push(row);
        return rows;
      }

      var blocks = layers.map(function (L) {
        var inner = mainW - numW - pad * 2;
        var nameLines = wrap(L.name, inner, 13);
        var rows = layout(L.items || [], inner);
        var h = pad + nameLines.length * 17 + 6 + rows.length * (chipH + chipGap) - chipGap + pad;
        return { L: L, nameLines: nameLines, rows: rows, h: h };
      });
      var adjBlock = null;
      if (adj) {
        var aw = side ? adjW : W;
        var innerA = aw - pad * 2 - (side ? 0 : numW);
        var aLines = wrap(adj.name, innerA, 13);
        var aRows = layout(adj.items || [], innerA);
        adjBlock = { lines: aLines, rows: aRows, h: pad + aLines.length * 17 + 6 + aRows.length * (chipH + chipGap) - chipGap + pad + 16 };
      }
      var gap = 8;
      var mainH = blocks.reduce(function (a, b) { return a + b.h + gap; }, 0) - gap;
      var H = side ? Math.max(mainH, adjBlock ? adjBlock.h : 0) : mainH + (adjBlock ? adjBlock.h + 16 : 0);
      var s = root(host, W, H + 2, o);

      var y = 1;
      blocks.forEach(function (b, i) {
        svg('rect', { x: 0.5, y: y, width: mainW - 1, height: b.h, rx: 6, cls: b.L.shaded ? 'qc-hl' : 'qc-box', style: b.L.shaded ? 'stroke:var(--chart-muted,#5f6d79);stroke-opacity:.35' : '' }, s);
        svg('text', { x: 14, y: y + pad + 13, cls: 'qc-big qc-muted', text: String(i + 1) }, s);
        var ty = y + pad + 13;
        b.nameLines.forEach(function (ln, k) { svg('text', { x: numW + pad, y: ty + k * 17, cls: 'qc-strong', text: ln }, s); });
        var cy = y + pad + b.nameLines.length * 17 + 6;
        b.rows.forEach(function (row) {
          row.forEach(function (c) {
            svg('rect', { x: numW + pad + c.x, y: cy, width: c.w, height: chipH, rx: 12, cls: 'qc-chip' }, s);
            svg('text', { x: numW + pad + c.x + 9, y: cy + 16, cls: 'qc-small', text: c.t }, s);
          });
          cy += chipH + chipGap;
        });
        y += b.h + gap;
      });

      if (adjBlock) {
        var ax = side ? mainW + 16 : 0, ay = side ? 1 : mainH + 17, aw2 = side ? adjW : W;
        svg('rect', { x: ax + 0.5, y: ay, width: aw2 - 1, height: adjBlock.h, rx: 6, style: 'fill:none;stroke:var(--chart-muted,#5f6d79);stroke-dasharray:5 4' }, s);
        svg('text', { x: ax + pad, y: ay + pad + 11, cls: 'qc-muted qc-small', text: 'Adjacent market' }, s);
        var ly = ay + pad + 11 + 17;
        adjBlock.lines.forEach(function (ln, k) { svg('text', { x: ax + pad, y: ly + k * 17, cls: 'qc-strong', text: ln }, s); });
        var cy2 = ly + (adjBlock.lines.length - 1) * 17 + 10;
        adjBlock.rows.forEach(function (row) {
          row.forEach(function (c) {
            svg('rect', { x: ax + pad + c.x, y: cy2, width: c.w, height: chipH, rx: 12, cls: 'qc-chip' }, s);
            svg('text', { x: ax + pad + c.x + 9, y: cy2 + 16, cls: 'qc-small', text: c.t }, s);
          });
          cy2 += chipH + chipGap;
        });
      }
    });
  }

  /* 2x2 matrix */
  /*
    o = { title, desc, xLabel, yLabel, xLevels: ['Medium','High'], yLevels: ['Medium','High'],
          items: [{ n, label, x, y }], tone: function(xLevel, yLevel) -> 'crit'|'warn'|'good'|null }
    yLevels are listed bottom to top.
  */
  function matrix(target, o) {
    return mount(target, function (host, W) {
      var xl = o.xLevels, yl = o.yLevels, items = o.items || [];
      var axisW = 26, axisH = 34;
      var cellW = (W - axisW - 4) / xl.length;
      var showText = cellW >= 190;
      var cells = {};
      items.forEach(function (it) {
        var k = it.x + '|' + it.y;
        (cells[k] = cells[k] || []).push(it);
      });
      var lineH = 16, pad = 10;
      function cellHeight(list) {
        if (!list || !list.length) return 70;
        if (!showText) return pad * 2 + Math.ceil(list.length / Math.max(1, Math.floor((cellW - pad * 2) / 30))) * 30 + 18;
        var h = pad + 18;
        list.forEach(function (it) { h += Math.max(1, wrap(it.label, cellW - pad * 2 - 30, 12).length) * lineH + 6; });
        return h + pad;
      }
      var rowH = yl.map(function (yv) {
        return Math.max(90, Math.max.apply(null, xl.map(function (xv) { return cellHeight(cells[xv + '|' + yv]); })));
      });
      var H = rowH.reduce(function (a, b) { return a + b; }, 0) + axisH;
      var s = root(host, W, H, o);
      var tone = o.tone || function (x, y) {
        var hx = xl.indexOf(x) === xl.length - 1, hy = yl.indexOf(y) === yl.length - 1;
        return hx && hy ? 'crit' : (hx || hy) ? 'warn' : null;
      };
      var yTop = 0;
      for (var r = yl.length - 1; r >= 0; r--) {
        var yv = yl[r], h = rowH[r];
        svg('text', { x: 12, y: yTop + h / 2, transform: 'rotate(-90 12 ' + (yTop + h / 2) + ')', 'text-anchor': 'middle', cls: 'qc-muted qc-small', text: (o.yLabel || '') + ': ' + yv }, s);
        xl.forEach(function (xv, c) {
          var x = axisW + c * cellW, t = tone(xv, yv);
          var fill = t === 'crit' ? 'var(--crit,#c0392b)' : t === 'warn' ? 'var(--warn,#b7791f)' : 'var(--chart-grid,#dde3e8)';
          svg('rect', { x: x + 1, y: yTop + 1, width: cellW - 2, height: h - 2, rx: 6, style: 'fill:' + fill + ';fill-opacity:' + (t ? 0.12 : 0.25) + ';stroke:var(--chart-grid,#dde3e8)' }, s);
          var list = cells[xv + '|' + yv] || [];
          var cy = yTop + pad + 12;
          svg('text', { x: x + pad, y: cy, cls: 'qc-muted qc-small', text: list.length + (list.length === 1 ? ' risk' : ' risks') }, s);
          cy += 10;
          if (showText) {
            list.forEach(function (it) {
              var lines = wrap(it.label, cellW - pad * 2 - 30, 12);
              svg('circle', { cx: x + pad + 10, cy: cy + 10, r: 10, style: 'fill:var(--chart-ink,#1d2730)' }, s);
              svg('text', { x: x + pad + 10, y: cy + 14, 'text-anchor': 'middle', cls: 'qc-small qc-strong', style: 'fill:var(--chart-surface,#fff)', text: String(it.n) }, s);
              lines.forEach(function (ln, k) { svg('text', { x: x + pad + 28, y: cy + 14 + k * lineH, text: ln }, s); });
              cy += Math.max(1, lines.length) * lineH + 6;
            });
          } else {
            var perRow = Math.max(1, Math.floor((cellW - pad * 2) / 30));
            list.forEach(function (it, i) {
              var cx = x + pad + 12 + (i % perRow) * 30, ccy = cy + 12 + Math.floor(i / perRow) * 30;
              svg('circle', { cx: cx, cy: ccy, r: 12, style: 'fill:var(--chart-ink,#1d2730)' }, s);
              svg('text', { x: cx, y: ccy + 4, 'text-anchor': 'middle', cls: 'qc-small qc-strong', style: 'fill:var(--chart-surface,#fff)', text: String(it.n) }, s);
            });
          }
        });
        yTop += h;
      }
      xl.forEach(function (xv, c) {
        svg('text', { x: axisW + c * cellW + cellW / 2, y: yTop + 20, 'text-anchor': 'middle', cls: 'qc-muted qc-small', text: (o.xLabel || '') + ': ' + xv }, s);
      });
    });
  }

  /* phase roadmap */
  /*
    o = { title, desc, phases: [{ name, dates, team, shaded }], gates: ['label', ...],
          note: 'Not to scale' }
    gates[i] sits between phases[i] and phases[i+1].
  */
  function roadmap(target, o) {
    return mount(target, function (host, W) {
      var ph = o.phases || [], gates = o.gates || [];
      var horizontal = W >= 760;
      var s, H;
      if (horizontal) {
        var gateW = 26, n = ph.length;
        var boxW = (W - gateW * (n - 1)) / n;
        var boxLines = ph.map(function (p) {
          return { name: wrap(p.name, boxW - 20, 13), dates: wrap(p.dates, boxW - 20, 11), team: wrap(p.team || '', boxW - 20, 11) };
        });
        var boxH = Math.max.apply(null, boxLines.map(function (b) { return 14 + b.name.length * 17 + 4 + b.dates.length * 15 + 4 + b.team.length * 15 + 12; }));
        var gLines = gates.map(function (g) { return wrap(g, boxW * 0.9, 11); });
        var gH = Math.max.apply(null, gLines.map(function (l) { return l.length; }).concat([1])) * 14 + 26;
        H = boxH + gH + (o.note ? 18 : 0) + 4;
        s = root(host, W, H, o);
        ph.forEach(function (p, i) {
          var x = i * (boxW + gateW), b = boxLines[i];
          svg('rect', { x: x + 0.5, y: 0.5, width: boxW - 1, height: boxH, rx: 6, cls: p.shaded ? 'qc-hl' : 'qc-box', style: p.shaded ? 'stroke:var(--chart-ink,#1d2730);stroke-width:1.5' : '' }, s);
          var y = 20;
          b.name.forEach(function (ln) { svg('text', { x: x + 10, y: y, cls: 'qc-strong', style: 'font-size:13px', text: ln }, s); y += 17; });
          y += 2;
          b.dates.forEach(function (ln) { svg('text', { x: x + 10, y: y, cls: 'qc-small', text: ln }, s); y += 15; });
          y += 2;
          b.team.forEach(function (ln) { svg('text', { x: x + 10, y: y, cls: 'qc-small qc-muted', text: ln }, s); y += 15; });
          if (i < gates.length && i < n - 1) {
            var gx = x + boxW + gateW / 2, gy = boxH / 2;
            svg('path', { d: 'M' + gx + ',' + (gy - 11) + 'L' + (gx + 11) + ',' + gy + 'L' + gx + ',' + (gy + 11) + 'L' + (gx - 11) + ',' + gy + 'Z', style: 'fill:var(--gate,#e3a008);stroke:var(--chart-ink,#1d2730);stroke-width:1' }, s);
            svg('line', { x1: gx, x2: gx, y1: gy + 11, y2: boxH + 12, style: 'stroke:var(--chart-ink,#1d2730);stroke-dasharray:2 3' }, s);
            gLines[i].forEach(function (ln, k) {
              svg('text', { x: gx, y: boxH + 26 + k * 14, 'text-anchor': 'middle', cls: 'qc-small qc-strong', text: ln }, s);
            });
          }
        });
        if (o.note) svg('text', { x: W, y: H - 4, 'text-anchor': 'end', cls: 'qc-small qc-muted', text: o.note }, s);
      } else {
        var bw = W - 2, y0 = 0, parts = [];
        ph.forEach(function (p, i) {
          var nl = wrap(p.name, bw - 20, 13), dl = wrap(p.dates, bw - 20, 11), tl = wrap(p.team || '', bw - 20, 11);
          var h = 14 + nl.length * 17 + 4 + dl.length * 15 + 4 + tl.length * 15 + 10;
          parts.push({ type: 'phase', p: p, nl: nl, dl: dl, tl: tl, y: y0, h: h });
          y0 += h;
          if (i < gates.length && i < ph.length - 1) {
            var gl = wrap(gates[i], bw - 50, 11);
            parts.push({ type: 'gate', gl: gl, y: y0, h: gl.length * 14 + 22 });
            y0 += gl.length * 14 + 22;
          }
        });
        H = y0 + (o.note ? 20 : 2);
        s = root(host, W, H, o);
        parts.forEach(function (pt) {
          if (pt.type === 'phase') {
            svg('rect', { x: 0.5, y: pt.y + 0.5, width: bw, height: pt.h, rx: 6, cls: pt.p.shaded ? 'qc-hl' : 'qc-box', style: pt.p.shaded ? 'stroke:var(--chart-ink,#1d2730);stroke-width:1.5' : '' }, s);
            var y = pt.y + 20;
            pt.nl.forEach(function (ln) { svg('text', { x: 10, y: y, cls: 'qc-strong', style: 'font-size:13px', text: ln }, s); y += 17; });
            y += 2;
            pt.dl.forEach(function (ln) { svg('text', { x: 10, y: y, cls: 'qc-small', text: ln }, s); y += 15; });
            y += 2;
            pt.tl.forEach(function (ln) { svg('text', { x: 10, y: y, cls: 'qc-small qc-muted', text: ln }, s); y += 15; });
          } else {
            var gx = 22, gy = pt.y + pt.h / 2;
            svg('path', { d: 'M' + gx + ',' + (gy - 9) + 'L' + (gx + 9) + ',' + gy + 'L' + gx + ',' + (gy + 9) + 'L' + (gx - 9) + ',' + gy + 'Z', style: 'fill:var(--gate,#e3a008);stroke:var(--chart-ink,#1d2730);stroke-width:1' }, s);
            pt.gl.forEach(function (ln, k) {
              svg('text', { x: 40, y: gy + 4 - (pt.gl.length - 1) * 7 + k * 14, cls: 'qc-small qc-strong', text: ln }, s);
            });
          }
        });
        if (o.note) svg('text', { x: W - 2, y: H - 4, 'text-anchor': 'end', cls: 'qc-small qc-muted', text: o.note }, s);
      }
    });
  }

  /* sparkline */
  /* o = { width, height, color, min, max, title, fill: true, dot: true } ; not responsive */
  function sparkline(target, values, o) {
    o = o || {};
    var host = resolveHost(target);
    if (!host) return null;
    injectStyle();
    var W = o.width || 96, H = o.height || 26;
    while (host.firstChild) host.removeChild(host.firstChild);
    var vals = (values || []).filter(function (v) { return v != null && isFinite(v); });
    var s = svg('svg', { cls: 'qc', width: W, height: H, viewBox: '0 0 ' + W + ' ' + H, role: 'img', 'aria-label': o.title || 'Trend' }, host);
    svg('title', { text: o.title || 'Trend' }, s);
    if (vals.length < 2) return s;
    var min = o.min != null ? o.min : Math.min.apply(null, vals);
    var max = o.max != null ? o.max : Math.max.apply(null, vals);
    if (max === min) max = min + 1;
    var n = values.length;
    var pts = [];
    values.forEach(function (v, i) {
      if (v == null || !isFinite(v)) return;
      pts.push([2 + i / (n - 1) * (W - 6), 2 + (1 - (v - min) / (max - min)) * (H - 4)]);
    });
    var col = o.color || 'var(--c1,#1f6f8b)';
    if (o.fill !== false) {
      svg('path', { d: 'M' + pts[0][0] + ',' + (H - 1) + 'L' + pts.map(function (p) { return p[0].toFixed(1) + ',' + p[1].toFixed(1); }).join('L') + 'L' + pts[pts.length - 1][0] + ',' + (H - 1) + 'Z', style: 'fill:' + col + ';fill-opacity:.15;stroke:none' }, s);
    }
    svg('path', { d: 'M' + pts.map(function (p) { return p[0].toFixed(1) + ',' + p[1].toFixed(1); }).join('L'), style: 'fill:none;stroke:' + col + ';stroke-width:1.5;stroke-linejoin:round' }, s);
    if (o.dot !== false) {
      var lp = pts[pts.length - 1];
      svg('circle', { cx: lp[0], cy: lp[1], r: 2.5, style: 'fill:' + col }, s);
    }
    return s;
  }

  global.QCharts = {
    line: line,
    stackedBars: stackedBars,
    hbars: hbars,
    stackedArea: stackedArea,
    marketMap: marketMap,
    matrix: matrix,
    roadmap: roadmap,
    sparkline: sparkline,
    fmt: fmt,
    niceTicks: niceTicks,
    svg: svg,
    wrap: wrap,
    textWidth: textWidth
  };
})(window);
