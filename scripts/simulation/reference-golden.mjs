#!/usr/bin/env node
// Golden fingerprints of the reference scenario engine (ARV-027).
//   node scripts/simulation/reference-golden.mjs [--check]
// Runs the prototype's sim.js (docs/design/prototype/app/assets/sim.js) for a fixed set of cases and writes
// SHA-256 fingerprints of every output family to Platform/Backplane/Ariva.UnitTests/Simulation/reference-golden.json.
// The C# port in Ariva.Simulation.Api/Scenarios/Engine must reproduce every fingerprint bit for bit
// (ScenarioParityTests). --check compares instead of writing and exits 1 on a difference.
// Fingerprint feed: each number as its IEEE 754 float64 bytes, little endian (NaN canonical, null as NaN);
// negative zero as zero (the reference rounds -0.3 to -0, the port keeps such minutes as integers);
// each string as UTF-8 followed by a zero byte. No dependencies.

import fs from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const SIM = path.join(ROOT, 'docs', 'design', 'prototype', 'app', 'assets', 'sim.js');
const OUT = path.join(ROOT, 'Platform', 'Backplane', 'Ariva.UnitTests', 'Simulation', 'reference-golden.json');

const ctx = {};
vm.createContext(ctx);
vm.runInContext(fs.readFileSync(SIM, 'utf8'), ctx, { filename: 'sim.js' });
const S = ctx.QSim;

class Feed {
  constructor() { this.h = crypto.createHash('sha256'); this.buf = Buffer.alloc(8); }
  num(v) {
    if (v === null || v === undefined || v !== v) this.buf.writeBigUInt64LE(0x7ff8000000000000n, 0);
    else this.buf.writeDoubleLE(v === 0 ? 0 : v, 0); // -0 reads as 0: integers the port keeps as int
    this.h.update(this.buf);
  }
  nums(a) { for (let i = 0; i < a.length; i++) this.num(a[i]); }
  str(s) { this.h.update(Buffer.from(String(s ?? ''), 'utf8')); this.h.update(Buffer.from([0])); }
  hex() { return this.h.digest('hex'); }
}

const mixOf = (f, m) => ['CIT', 'RES', 'VIS', 'CRW', 'TRF'].forEach(k => f.num(m[k]));

function fingerprints(cfg) {
  const day = S.run(cfg);
  const NQ = S.QUEUES.length, out = {};
  let f;

  f = new Feed();
  day.S.arr.forEach(x => { f.str(x.code); f.str(x.carrier); [x.sched, x.onBlock, x.eibt, x.seats, x.booked, x.load, x.walk, x.eibtErr, x.pax].forEach(v => f.num(v)); mixOf(f, x.mix); });
  day.S.dep.forEach(x => { f.str(x.code); f.str(x.carrier); [x.std, x.seats, x.booked, x.load, x.shift].forEach(v => f.num(v)); f.str(x.island); f.str(x.handler); f.num(x.pax); mixOf(f, x.mix); });
  out.schedule = f.hex();

  f = new Feed(); day.roster.forEach(r => f.nums(r)); out.roster = f.hex();

  f = new Feed();
  for (let q = 0; q < NQ; q++) ['A', 'D', 'L', 'C', 'R5', 'OPEN', 'PAUSED', 'meanWait', 'lastExit'].forEach(k => f.nums(day[k][q]));
  f.nums(day.REJ[0]); f.nums(day.REJ[1]);
  out.run = f.hex();

  f = new Feed();
  day.srv.forEach(list => list.forEach(s => { f.str(s.id); f.num(s.factor); f.nums(s.pause); f.nums(s.unknown); f.num(s.oos ? 1 : 0); }));
  out.servers = f.hex();

  f = new Feed(); const ex = day.expected(); for (let q = 0; q < NQ; q++) ['A', 'D', 'L', 'C'].forEach(k => f.nums(ex[k][q])); out.expected = f.hex();
  f = new Feed(); const da = day.dayAhead(); for (let q = 0; q < NQ; q++) { f.nums(da.A[q]); f.nums(da.W[q]); } out.dayAhead = f.hex();

  f = new Feed();
  day.alertsAll.forEach(a => { [a.id, a.ruleId, a.rule, a.kind, a.sensor, a.zone, a.severity, a.owner, a.escalateTo, a.perm, a.text].forEach(s => f.str(s)); [a.q, a.raisedAt, a.clearedAt, a.escalateAfter, a.bin].forEach(v => f.num(v)); });
  out.alerts = f.hex();
  out.alertCount = day.alertsAll.length;

  f = new Feed();
  for (let q = 0; q < NQ; q++) for (let bs = 0; bs < S.DAY; bs += 15) { const b = day.bin(q, bs, 1439); f.num(b.p90); f.num(b.pax); f.str(b.status); }
  out.bins = f.hex();

  f = new Feed();
  for (let m = 0; m < S.DAY; m += 7) for (let q = 0; q < NQ; q++) {
    const st = day.state(q, m);
    [st.len, st.rate, st.cap, st.open, st.paused, st.active, st.nowcast, st.arrivals, st.served].forEach(v => f.num(v));
    f.num(st.band ? st.band[0] : null); f.num(st.band ? st.band[1] : null); f.num(st.degraded ? 1 : 0);
  }
  out.states = f.hex();

  f = new Feed();
  const fc = day.forecast(1080);
  for (let q = 0; q < NQ; q++) { const x = fc.q[q]; f.nums(x.p10); f.nums(x.p50); f.nums(x.p90); f.nums(x.raw); f.nums(x.w); }
  out.forecast = f.hex();

  f = new Feed();
  const rec = day.recommend(1080, 'arr', 24);
  Object.keys(rec.rec).forEach(id => { f.str(id); f.nums(rec.rec[id]); f.nums(rec.planned[id]); f.nums(rec.laneP90Plan[id]); f.nums(rec.laneP90Rec[id]); });
  f.nums(rec.p90Plan); f.nums(rec.p90Rec); f.num(rec.from); f.num(rec.until);
  const rec2 = day.recommend(-1, 'ciB', 96, { predict: false });
  Object.keys(rec2.rec).forEach(id => { f.str(id); f.nums(rec2.rec[id]); f.nums(rec2.planned[id]); });
  out.recommend = f.hex();

  f = new Feed();
  const aVis = S.QI['A-VIS'], ciC = S.QI['CI-C'];
  day.waitSeries(aVis, 1000, 1200, 1150).forEach(p => { f.num(p.m); f.num(p.w); f.num(p.final ? 1 : 0); f.num(p.empty ? 1 : 0); });
  for (let q = 0; q < NQ; q++) { const p = day.p90(q, 0, S.DAY); f.num(p.p90); f.num(p.pax); const p2 = day.p90(q, 1000, 1200, 1150); f.num(p2.p90); f.num(p2.pax); }
  const bst = day.binStats(ciC, 1140, 60, 1300); f.num(bst.pax); f.str(bst.status); f.num(bst.pct(0.9)); f.num(bst.pct(0.5)); f.num(bst.share(15));
  out.waits = f.hex();

  f = new Feed();
  for (let q = 0; q < NQ; q++) {
    day.servers(q, 1100).forEach(s => { f.str(s.id); f.str(s.state); f.num(s.k); f.num(s.svc); });
    day.serverInterval(q, 1095, 1100).forEach(s => { f.str(s.id); f.num(s.pax); f.num(s.minutes); f.num(s.svc); });
  }
  ['A', 'D'].forEach(side => { const e = day.egate(side, 1100); [e.processed, e.rejects, e.rejectRate, e.util, e.inService, e.total].forEach(v => f.num(v)); });
  out.serversAt = f.hex();

  f = new Feed();
  const all = S.QUEUES.map((d, q) => q);
  const dm = day.demand(1080, 24, 30, all); f.nums(dm.xs); all.forEach(q => f.nums(dm.q[q]));
  const fl = day.flightsArriving(1020, 1110);
  fl.forEach(x => { f.str(x.f.code); ['CRW', 'CIT', 'RES', 'VIS', 'EG'].forEach(k => f.num(x.lanes[k])); f.num(x.hallFrom); });
  const hc = day.hallCurve(fl, 1020, 90); ['CRW', 'CIT', 'RES', 'VIS', 'EG'].forEach(k => f.nums(hc[k]));
  out.flows = f.hex();

  return out;
}

const CASES = [
  { name: 'reference', config: { seed: 9303, allocations: S.SEED_ALLOCATIONS } },
  { name: 'reference-without-allocations', config: { seed: 9303 } },
  { name: 'seed-1', config: { seed: 1, allocations: S.SEED_ALLOCATIONS } },
  { name: 'seed-2026', config: { seed: 2026 } },
  { name: 'seed-max', config: { seed: 4294967295, allocations: S.SEED_ALLOCATIONS } },
  {
    name: 'reference-with-changes',
    config: {
      seed: 9303,
      allocations: S.SEED_ALLOCATIONS,
      flights: [
        { id: 'ADH-1', code: 'ZZ 901', dir: 'arr', time: 1100, seats: 200, load: 0.9 },
        { id: 'ADH-2', code: 'ZZ 902', dir: 'dep', time: 1250, seats: 180, load: 0.85, island: 'D' }
      ],
      overrides: [{ q: 'SEC-N', from: 1000, to: 1100, n: 2 }],
      accepted: [{ area: 'arr', from: 1095, until: 1155, counts: { 'A-VIS': [14, 15, 15, 14] } }]
    }
  }
];

const golden = { source: 'docs/design/prototype/app/assets/sim.js', generator: 'scripts/simulation/reference-golden.mjs', cases: [] };
for (const c of CASES) golden.cases.push({ name: c.name, config: c.config, fingerprints: fingerprints(c.config) });
const text = JSON.stringify(golden, null, 2) + '\n';

if (process.argv.includes('--check')) {
  const current = fs.existsSync(OUT) ? fs.readFileSync(OUT, 'utf8').replace(/\r\n/g, '\n') : '';
  if (current !== text) { console.error(`${path.relative(ROOT, OUT)} is out of date; run node scripts/simulation/reference-golden.mjs`); process.exit(1); }
  console.log('reference golden is up to date');
} else {
  fs.mkdirSync(path.dirname(OUT), { recursive: true });
  fs.writeFileSync(OUT, text);
  console.log(`wrote ${path.relative(ROOT, OUT)} (${golden.cases.length} cases)`);
}
