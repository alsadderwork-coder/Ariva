#!/usr/bin/env node
// Builds an Ariva offline install bundle (ARV-127; wiki 04 section 12): everything a site without access to the public
// registries needs to install one release, with a checksum manifest the release workflow signs. Run by the offline-bundle
// workflow from a version tag or main; a CI dry run (ci.yml) builds the same bundle without images on every pull request.
// Needs Node 22 and Helm 3; a real bundle also needs cosign 3, ORAS 1.3 and docker, with read access to the registry.
//
//   node scripts/offline/bundle.mjs --version <1.2.0> --build <image tag, v1.2.0-57> --out <new folder>
//        [--registry ghcr.io/<owner>/ariva] [--repository <owner/name>]
//   node scripts/offline/bundle.mjs --dry-run --version <0.0.0-ci.1> --build <ci-1> --web-build <folder> --out <new folder>
//   node scripts/offline/bundle.mjs --self-test
//
// The bundle (folder layout):
//   helm/helmfile.yaml.gotmpl         Platform/Cloud/Ariva.K8s/Helm/helmfile-offline.yaml.gotmpl
//   helm/charts/ariva-platform.tgz    the platform chart, packaged (helm package)
//   helm/charts/ariva-timescaledb.tgz the TimescaleDB chart, packaged
//   helm/values/*.yaml                the platform chart's environment values and the database chart's values; images.yaml
//                                     (release version, build number, the Ariva image digests), timescaledb-image.yaml
//                                     (the chart's TimescaleDB tag and digest) and clamav-image.yaml (the platform chart's
//                                     ClamAV tag and digest), which the Helmfile lists after a site's file
//   images/<name>/                    each image as an OCI image layout under its tag, by digest, with its OCI referrers:
//                                     the seven Ariva images with their cosign signature and SBOM attestation, TimescaleDB
//                                     and ClamAV (the malware scan beside api-main, ARV-092c)
//   sboms/<name>.cdx.json             each Ariva image's CycloneDX SBOM, taken from its verified attestation
//   web/                              the web build: in a release, the files of the signed web image (where its Dockerfile
//                                     copies the build); in a dry run, the build of the checkout (--web-build)
//   SHA256SUMS, manifest.json         every file with its SHA-256; manifest.json also names the release, commit and images
//   manifest.json.sigstore.json       written by the workflow's signing job: the manifest's keyless signature
//
// What it guarantees (CWE-494):
// - An Ariva image is taken only once cosign verifies its tag is signed by this repository's images workflow, on a version
//   tag or main, at the commit of this checkout; it is then copied by that digest, never by tag, and its SBOM is the one in
//   its verified attestation. So the images, the charts and the web build are one commit.
// - A third-party image (TimescaleDB, ClamAV) is copied by the digest its chart pins, so the bundle holds exactly what the
//   chart runs.
// - The web build of a release is copied out of the verified web image (docker create and cp; nothing in it runs), so no
//   npm code runs to make a release bundle.
// - Nothing is pushed or signed here, and no cluster is touched: the registries are only read.
// - A real bundle is built from a clean checkout only, so the charts are the commit the manifest names.
// - Programs start without a shell from argument arrays, in a private empty folder; every value is checked against a fixed
//   shape first (CWE-78).
// - Links in the web build are refused; the output folder must not exist, so nothing is overwritten.

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import assert from 'node:assert/strict';
import {
  ARIVA_IMAGES, DATABASE_IMAGE, DATABASE_IMAGE_VALUES, DEFAULT_REPOSITORY, IMAGES_VALUES, IMAGES_WORKFLOW, MANIFEST, PATTERNS, Refused,
  SCANNER_IMAGE, SCANNER_IMAGE_VALUES, SELF_TEST, SIGNATURE, TOOLS, UsageError, databaseImageValues, imagesValues, layoutProblems,
  pathAllowed, run, scannerImageValues, selfTestSign, selfTestState, trustArgs, verifyBundle, walk, writeManifest
} from './verify.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '..', '..');
const HELM_FOLDER = path.join(ROOT, 'Platform', 'Cloud', 'Ariva.K8s', 'Helm');
const OFFLINE_HELMFILE = path.join(HELM_FOLDER, 'helmfile-offline.yaml.gotmpl');
const WEB_DOCKERFILE = path.join(ROOT, 'Platform', 'Frontplane', 'Ariva.Web', 'Dockerfile');
// Where Platform/Frontplane/Ariva.Web/Dockerfile copies the web build (the self-test holds the Dockerfile to it).
const WEB_IMAGE_PATH = '/usr/share/nginx/html/web';
const CHARTS = [
  { folder: 'timescaledb', file: 'ariva-timescaledb.tgz', values: 'timescaledb.yaml' },
  { folder: 'platform', file: 'ariva-platform.tgz' }
];

TOOLS.helm ??= [process.env.HELM || 'helm'];
TOOLS.git ??= ['git'];
TOOLS.docker ??= ['docker'];

const firstLine = (text) => String(text).trim().split('\n').find((line) => line.trim()) ?? '(no output)';

function must(tool, args, what) {
  const result = run(tool, args);
  if (!result.ok) throw new Refused(`${what}: ${firstLine(result.stderr || result.stdout)}`);
  return result.stdout;
}

export function parseArgs(argv, env = process.env) {
  const options = { dryRun: false };
  const flags = { '--version': 'version', '--build': 'build', '--web-build': 'webBuild', '--out': 'out', '--registry': 'registry', '--repository': 'repository' };
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    if (arg === '--dry-run') options.dryRun = true;
    else if (flags[arg] && typeof argv[i + 1] === 'string' && !argv[i + 1].startsWith('--')) options[flags[arg]] = argv[++i];
    else throw new UsageError(`unexpected ${JSON.stringify(arg)}`);
  }
  options.repository ??= env.GITHUB_REPOSITORY || DEFAULT_REPOSITORY;
  if (!PATTERNS.repository.test(options.repository)) throw new UsageError('--repository must be owner/name');
  options.registry ??= `ghcr.io/${options.repository.split('/')[0].toLowerCase()}/ariva`;
  if (!PATTERNS.version.test(options.version ?? '')) throw new UsageError('--version must be a release version such as 1.2.0 or 1.2.0-rc.1');
  if (!PATTERNS.build.test(options.build ?? '')) throw new UsageError('--build must be the image tag the images workflow gave the build, such as v1.2.0-57');
  if (!PATTERNS.registry.test(options.registry)) throw new UsageError('--registry must be host[:port]/path in lower case, such as ghcr.io/<owner>/ariva');
  if (options.dryRun && !options.webBuild) throw new UsageError('a dry run needs --web-build: the web build folder (Platform/Frontplane/Ariva.Web/build after npm run build)');
  if (!options.dryRun && options.webBuild) throw new UsageError('a release takes its web build from the signed web image: --web-build is for dry runs only');
  if (!options.out) throw new UsageError('--out must name a folder that does not exist yet');
  return options;
}

/** name and version of a chart, from its Chart.yaml. */
export function chartInfo(folder) {
  const text = fs.readFileSync(path.join(folder, 'Chart.yaml'), 'utf8');
  const field = (name) => text.match(new RegExp(`^${name}:\\s*["']?([^"'\\s]+)["']?\\s*$`, 'm'))?.[1];
  const info = { name: field('name'), version: field('version') };
  if (!PATTERNS.name.test(info.name ?? '') || !PATTERNS.chartVersion.test(info.version ?? '')) throw new Refused(`${folder}/Chart.yaml: no plain name and version`);
  return info;
}

/** A third-party image pinned by a chart's values: its source registry path, the path under the site's mirror, tag and digest. */
function pinnedImage(name, where, [repository, tag, digest]) {
  const firstSegment = repository.split('/')[0];
  const hosted = repository.includes('/') && (firstSegment.includes('.') || firstSegment.includes(':') || firstSegment === 'localhost');
  const mirrorPath = hosted ? repository.slice(firstSegment.length + 1) : repository.includes('/') ? repository : `library/${repository}`;
  const source = hosted ? repository : `docker.io/${mirrorPath}`;
  if (!PATTERNS.registry.test(source) || !PATTERNS.imagePath.test(mirrorPath) || !PATTERNS.tag.test(tag) || !PATTERNS.digest.test(digest)) {
    throw new Refused(`${where}: the image is not a plain repository, tag and sha256 digest`);
  }
  return { name, source, tag, digest, mirrorPath, signed: false };
}

/**
 * The repository, tag and digest of the image block under the given top-level key of a chart's values (image: at the top
 * when key is null), the lines exactly as the charts write them; quotes around a value are dropped.
 */
export function imageBlock(text, key) {
  const lines = text.split(/\r?\n/);
  let start = 0;
  let indent = '';
  if (key !== null) {
    start = lines.findIndex((line) => line === `${key}:`);
    if (start === -1) return null;
    const block = lines.slice(start + 1).findIndex((line) => /^\S/.test(line));
    const end = block === -1 ? lines.length : start + 1 + block;
    const at = lines.slice(start + 1, end).findIndex((line) => line === '  image:');
    if (at === -1) return null;
    start = start + 1 + at;
    indent = '  ';
  } else {
    start = lines.findIndex((line) => line === 'image:');
    if (start === -1) return null;
  }
  const field = (offset, name) => lines[start + offset]?.match(new RegExp(`^${indent}  ${name}:\\s*"?([^"\\s]+)"?\\s*$`))?.[1];
  const found = [field(1, 'repository'), field(2, 'tag'), field(3, 'digest')];
  return found.every(Boolean) ? found : null;
}

/**
 * The third-party images the charts run, each pinned by digest: TimescaleDB (Charts/timescaledb/values.yaml, image) and ClamAV
 * (Charts/platform/values.yaml, uploadScan.image: the malware scan beside api-main, ARV-092c).
 */
export function thirdPartyImages() {
  const database = imageBlock(fs.readFileSync(path.join(HELM_FOLDER, 'Charts', 'timescaledb', 'values.yaml'), 'utf8'), null);
  if (!database) throw new Refused('Charts/timescaledb/values.yaml: no image block with repository, tag and digest');
  const scanner = imageBlock(fs.readFileSync(path.join(HELM_FOLDER, 'Charts', 'platform', 'values.yaml'), 'utf8'), 'uploadScan');
  if (!scanner) throw new Refused('Charts/platform/values.yaml: no uploadScan.image block with repository, tag and digest');
  return [pinnedImage(DATABASE_IMAGE, 'Charts/timescaledb/values.yaml', database), pinnedImage(SCANNER_IMAGE, 'Charts/platform/values.yaml uploadScan.image', scanner)];
}

/**
 * The helm folder of a bundle: the offline Helmfile, both charts packaged, the values files it names, images.yaml,
 * timescaledb-image.yaml and clamav-image.yaml. Also used by Platform/Cloud/Ariva.K8s/tests/chart-security.mjs, which renders it
 * against helmfile-k8s.yaml.gotmpl.
 */
export function stageHelm(out, { version, build, digests, database = thirdPartyImages().find((i) => i.name === DATABASE_IMAGE),
  scanner = thirdPartyImages().find((i) => i.name === SCANNER_IMAGE) }) {
  const helm = path.join(out, 'helm');
  fs.mkdirSync(path.join(helm, 'charts'), { recursive: true });
  fs.mkdirSync(path.join(helm, 'values'), { recursive: true });
  const helmfile = fs.readFileSync(OFFLINE_HELMFILE, 'utf8');
  fs.writeFileSync(path.join(helm, 'helmfile.yaml.gotmpl'), helmfile, { flag: 'wx' });
  const charts = [];
  const packaging = fs.mkdtempSync(path.join(os.tmpdir(), 'ariva-offline-charts-'));
  try {
    for (const chart of CHARTS) {
      const folder = path.join(HELM_FOLDER, 'Charts', chart.folder);
      const info = chartInfo(folder);
      must('helm', ['package', folder, '--destination', packaging], `helm package ${chart.folder}`);
      const packaged = path.join(packaging, `${info.name}-${info.version}.tgz`);
      if (!fs.existsSync(packaged)) throw new Refused(`helm package ${chart.folder} wrote no ${path.basename(packaged)}`);
      fs.copyFileSync(packaged, path.join(helm, 'charts', chart.file), fs.constants.COPYFILE_EXCL);
      if (chart.values) fs.copyFileSync(path.join(folder, 'values.yaml'), path.join(helm, 'values', chart.values), fs.constants.COPYFILE_EXCL);
      charts.push({ name: info.name, version: info.version, path: `helm/charts/${chart.file}` });
    }
  } finally {
    fs.rmSync(packaging, { recursive: true, force: true });
  }
  const environmentValues = [...helmfile.matchAll(/^\s+- valuesFile:\s*(\S+)\s*$/gm)].map((m) => m[1]);
  if (environmentValues.length === 0) throw new Refused('helmfile-offline.yaml.gotmpl names no environment values file');
  for (const file of environmentValues) {
    if (!/^values-[a-z0-9-]+\.yaml$/.test(file)) throw new Refused(`helmfile-offline.yaml.gotmpl: ${file} is not a values-<environment>.yaml name`);
    fs.copyFileSync(path.join(HELM_FOLDER, 'Charts', 'platform', file), path.join(helm, 'values', file), fs.constants.COPYFILE_EXCL);
  }
  fs.writeFileSync(path.join(out, ...IMAGES_VALUES.split('/')), imagesValues({ version, build, digests }), { flag: 'wx' });
  fs.writeFileSync(path.join(out, ...DATABASE_IMAGE_VALUES.split('/')), databaseImageValues(database), { flag: 'wx' });
  fs.writeFileSync(path.join(out, ...SCANNER_IMAGE_VALUES.split('/')), scannerImageValues(scanner), { flag: 'wx' });
  return charts;
}

/** Copies a web build: regular files with plain names only (the .gitkeep placeholder is left out); index.html required. */
export function copyWeb(source, target) {
  // The folder itself must be a folder, not a link to one (a link at the web image's path would lead out of the image).
  if (!fs.existsSync(source) || !fs.lstatSync(source).isDirectory()) throw new Refused(`${source} is not a folder (a link is refused)`);
  if (!fs.existsSync(path.join(source, 'index.html'))) throw new Refused(`${source} has no index.html: build the web first (npm run build)`);
  const { files, problems } = walk(source);
  if (problems.length) throw new Refused(`the web build: ${problems.join('; ')}`);
  let copied = 0;
  for (const file of files) {
    if (path.posix.basename(file) === '.gitkeep') continue;
    if (!pathAllowed(`web/${file}`)) throw new Refused(`the web build holds ${JSON.stringify(file)}, not a plain relative name`);
    const to = path.join(target, ...file.split('/'));
    fs.mkdirSync(path.dirname(to), { recursive: true });
    fs.copyFileSync(path.join(source, ...file.split('/')), to, fs.constants.COPYFILE_EXCL);
    copied++;
  }
  return copied;
}

/** The web build of a release: the files of the verified web image, copied out by digest (docker create and cp, never run). */
function webFromImage(reference, target) {
  const scratch = fs.mkdtempSync(path.join(os.tmpdir(), 'ariva-offline-web-'));
  let container = null;
  try {
    container = must('docker', ['create', reference], `docker create ${reference}`).trim();
    if (!/^[0-9a-f]{12,64}$/.test(container)) throw new Refused(`docker create ${reference} printed no container id`);
    must('docker', ['cp', `${container}:${WEB_IMAGE_PATH}`, path.join(scratch, 'web')], `docker cp ${WEB_IMAGE_PATH} out of ${reference}`);
    return copyWeb(path.join(scratch, 'web'), target);
  } finally {
    if (container && /^[0-9a-f]{12,64}$/.test(container)) run('docker', ['rm', container]);
    fs.rmSync(scratch, { recursive: true, force: true });
  }
}

/** cosign: the digest the images workflow signed under the tag at this commit, and the SBOM attested for that digest. */
function verifiedImage(source, build, repository, commit) {
  const trust = trustArgs(repository, IMAGES_WORKFLOW, { sha: commit });
  const signed = run('cosign', ['verify', ...trust, `${source}:${build}`]);
  if (!signed.ok) throw new Refused(`${source}:${build} is not signed by ${repository}'s images workflow on a version tag or main at commit ${commit.slice(0, 12)}: ${firstLine(signed.stderr)}`);
  let digests;
  try {
    digests = [...new Set(JSON.parse(signed.stdout).map((s) => s?.critical?.image?.['docker-manifest-digest']))];
  } catch {
    throw new Refused(`${source}:${build}: cosign verify printed no signatures`);
  }
  if (digests.length !== 1 || !PATTERNS.digest.test(String(digests[0]))) throw new Refused(`${source}:${build}: the signatures do not name one digest`);
  const digest = digests[0];
  const attested = run('cosign', ['verify-attestation', '--type', 'cyclonedx', ...trust, `${source}@${digest}`]);
  if (!attested.ok) throw new Refused(`${source}@${digest} has no CycloneDX attestation from the images workflow: ${firstLine(attested.stderr)}`);
  for (const line of attested.stdout.split('\n').filter((l) => l.trim())) {
    try {
      const statement = JSON.parse(Buffer.from(JSON.parse(line).payload, 'base64').toString('utf8'));
      if (statement.predicateType === 'https://cyclonedx.org/bom' && (statement.subject ?? []).some((s) => `sha256:${s?.digest?.sha256}` === digest)) {
        return { digest, sbom: statement.predicate };
      }
    } catch {
      // not an attestation envelope
    }
  }
  throw new Refused(`${source}@${digest}: no verified CycloneDX attestation names this digest`);
}

/** oras: the image by digest, with its referrers (signature, attestation), into an OCI layout under its tag. */
function copyImage(source, digest, tag, layout) {
  if (layout.includes(':')) throw new Refused(`${layout}: an output path with ":" cannot name an OCI layout (run the release on Linux)`);
  must('oras', ['copy', '--recursive', `${source}@${digest}`, '--to-oci-layout', `${layout}:${tag}`], `oras copy ${source}@${digest}`);
  const ingest = path.join(layout, 'ingest');
  if (fs.existsSync(ingest)) {
    if (fs.readdirSync(ingest).length > 0) throw new Refused(`${layout}/ingest holds partial downloads`);
    fs.rmdirSync(ingest);
  }
}

function gitCommit(dryRun) {
  const commit = must('git', ['-C', ROOT, 'rev-parse', 'HEAD'], 'git rev-parse').trim();
  if (!PATTERNS.commit.test(commit)) throw new Refused('git rev-parse printed no commit id');
  if (!dryRun && must('git', ['-C', ROOT, 'status', '--porcelain'], 'git status').trim() !== '') {
    throw new Refused('the checkout has changes: a bundle is built from a clean checkout of a version tag or main');
  }
  return commit;
}

const within = (parent, child) => {
  const relative = path.relative(parent, child);
  return relative === '' || (relative !== '..' && !relative.startsWith(`..${path.sep}`) && !path.isAbsolute(relative));
};

/** Builds the bundle folder and writes its manifest. Returns the manifest. */
export function buildBundle(options) {
  const { version, build, registry, repository, dryRun } = options;
  const out = path.resolve(options.out);
  if (fs.existsSync(out)) throw new Refused(`${out} exists: name a new folder`);
  if (!fs.existsSync(path.dirname(out))) throw new Refused(`${path.dirname(out)} does not exist`);
  const webBuild = dryRun ? path.resolve(options.webBuild) : null;
  if (dryRun) {
    if (!fs.existsSync(webBuild) || !fs.statSync(webBuild).isDirectory()) throw new Refused(`${webBuild} is not a folder`);
    if (within(webBuild, out)) throw new Refused('--out cannot be inside the web build');
  }
  const commit = options.commit ?? gitCommit(dryRun);
  const thirdParty = options.thirdParty ?? thirdPartyImages();
  const database = thirdParty.find((image) => image.name === DATABASE_IMAGE);
  if (!database) throw new Refused('no TimescaleDB image among the third-party images');
  const scanner = thirdParty.find((image) => image.name === SCANNER_IMAGE);
  if (!scanner) throw new Refused('no ClamAV image among the third-party images');

  fs.mkdirSync(out);
  const images = [];
  const digests = {};
  let webFiles;
  if (!dryRun) {
    for (const name of ARIVA_IMAGES) {
      const source = `${registry}/${name}`;
      const { digest, sbom } = verifiedImage(source, build, repository, commit);
      digests[name] = digest;
      fs.mkdirSync(path.join(out, 'sboms'), { recursive: true });
      fs.writeFileSync(path.join(out, 'sboms', `${name}.cdx.json`), JSON.stringify(sbom), { flag: 'wx' });
      copyImage(source, digest, build, path.join(out, 'images', name));
      images.push({ name, source, tag: build, digest, mirrorPath: `ariva/${name}`, signed: true, layout: `images/${name}`, sbom: `sboms/${name}.cdx.json` });
      console.log(`image      ${name} ${digest}`);
    }
    for (const image of thirdParty) {
      copyImage(image.source, image.digest, image.tag, path.join(out, 'images', image.name));
      images.push({ ...image, layout: `images/${image.name}`, sbom: null });
      console.log(`image      ${image.name} ${image.digest} (pinned by its chart)`);
    }
    webFiles = webFromImage(`${registry}/web@${digests.web}`, path.join(out, 'web'));
  } else {
    images.push(...ARIVA_IMAGES.map((name) => ({ name, source: `${registry}/${name}`, tag: build, digest: null, mirrorPath: `ariva/${name}`, signed: true, layout: null, sbom: null })));
    images.push(...thirdParty.map((image) => ({ ...image, layout: null, sbom: null })));
    webFiles = copyWeb(webBuild, path.join(out, 'web'));
  }
  const charts = stageHelm(out, { version, build, digests, database, scanner });
  const manifest = writeManifest(out, {
    version, build, commit, createdUtc: new Date().toISOString(), dryRun, source: { repository, registry }, charts, images,
    web: dryRun ? { from: 'build', digest: null } : { from: 'image', digest: digests.web }
  });
  if (!dryRun) {
    const hashes = new Map(manifest.files.map((f) => [f.path, f.sha256]));
    const problems = manifest.images.flatMap((image) => layoutProblems(out, image, hashes));
    if (problems.length) throw new Refused(`the image layouts are not complete: ${problems.join('; ')}`);
  }
  console.log(`charts     ${charts.map((c) => `${c.name} ${c.version}`).join(', ')}`);
  console.log(`web        ${webFiles} files ${dryRun ? 'from the checkout\'s build' : `from the web image ${digests.web}`}`);
  console.log(`manifest   ${manifest.files.length} files in ${out}`);
  return manifest;
}

export function main(argv) {
  if (argv[0] === '--self-test') return selfTest();
  const options = parseArgs(argv);
  buildBundle(options);
  console.log(options.dryRun
    ? 'Dry run: no image was pulled and nothing was signed or pushed. Check it with: node scripts/offline/verify.mjs --dry-run <folder>'
    : `Next: sign ${MANIFEST} (cosign sign-blob --bundle ${SIGNATURE}; the offline-bundle workflow's signing job does), then verify with scripts/offline/verify.mjs.`);
  return 0;
}

// ---------------------------------------------------------------------------------------------
// self-test (node scripts/verify.mjs security and CI): argument checks, the pinned third-party image, the web image's path,
// and two end to end bundles verified by verify.mjs: a dry run, and a release whose cosign, ORAS and docker are stand-ins
// (scripts/offline/stand-ins.mjs). The bundles need Helm (HELM or on PATH): required on GitHub Actions, skipped elsewhere
// when Helm is missing.

async function selfTest() {
  const standIns = path.join(HERE, 'stand-ins.mjs');
  const fixtures = await import(pathToFileURL(standIns).href);
  let checks = 0;
  const ok = (value, message) => {
    assert.ok(value, message);
    checks++;
  };
  const refuses = (action, pattern, label) => {
    assert.throws(action, (error) => (error instanceof Refused || error instanceof UsageError) && pattern.test(error.message), label);
    checks++;
  };

  // arguments: a release takes no web build folder, a dry run needs one
  const release = ['--version', '1.2.0', '--build', 'v1.2.0-57', '--out', 'o'];
  const dryRun = ['--dry-run', '--version', '0.0.0-ci.1', '--build', 'ci-1', '--web-build', 'w', '--out', 'o'];
  const parsed = parseArgs(release, {});
  ok(parsed.repository === DEFAULT_REPOSITORY && parsed.registry === 'ghcr.io/alsadderwork-coder/ariva' && parsed.dryRun === false, 'defaults: this repository and its GHCR path');
  ok(parseArgs([...release, '--repository', 'Some-Org/Ariva'], {}).registry === 'ghcr.io/some-org/ariva', 'the registry owner is lower case');
  ok(parseArgs(release, { GITHUB_REPOSITORY: 'o/r' }).repository === 'o/r', 'the workflow\'s repository by default');
  ok(parseArgs(dryRun, {}).dryRun && parseArgs(dryRun, {}).webBuild === 'w', 'a dry run with its web build');
  for (const [args, pattern] of [
    [['--version', '1.2', ...release.slice(2)], /--version/],
    [['--version', '1.2.0;id', ...release.slice(2)], /--version/],
    [[...release.slice(0, 2), '--build', '-rf', ...release.slice(4)], /unexpected|--build/],
    [[...release.slice(0, 2), '--build', 'v1/2', ...release.slice(4)], /--build/],
    [[...release, '--registry', 'GHCR.io/x'], /--registry/],
    [[...release, '--registry', 'https://ghcr.io/x'], /--registry/],
    [[...release, '--repository', 'a b/c'], /--repository/],
    [release.slice(0, 4), /--out/],
    [[...release, '--web-build', 'w'], /--web-build is for dry runs only/],
    [dryRun.filter((a) => a !== '--web-build' && a !== 'w'), /a dry run needs --web-build/],
    [[...release, '--nope'], /unexpected/]
  ]) refuses(() => parseArgs(args, {}), pattern, args.join(' '));

  // the third-party image is the one the chart pins, under the path the offline Helmfile points TimescaleDB at; the web
  // image's Dockerfile copies the build where a release takes it from
  const [timescale, clamav] = thirdPartyImages();
  ok(timescale.source === 'docker.io/timescale/timescaledb-ha' && PATTERNS.digest.test(timescale.digest), 'TimescaleDB from Docker Hub by digest');
  ok(fs.readFileSync(OFFLINE_HELMFILE, 'utf8').includes(`printf "%s/${timescale.mirrorPath}" $mirror`), 'the offline Helmfile takes TimescaleDB from <mirror>/timescale/timescaledb-ha');
  // ARV-092c: the malware scanner beside api-main, pinned by the platform chart, from <mirror>/clamav/clamav under that pin.
  ok(clamav.name === SCANNER_IMAGE && clamav.source === 'docker.io/clamav/clamav' && clamav.mirrorPath === 'clamav/clamav' && PATTERNS.digest.test(clamav.digest) && clamav.signed === false,
    'ClamAV from Docker Hub by the digest the platform chart pins');
  ok(fs.readFileSync(OFFLINE_HELMFILE, 'utf8').includes(`printf "%s/${clamav.mirrorPath}" $mirror`) && fs.readFileSync(OFFLINE_HELMFILE, 'utf8').includes('- ./values/clamav-image.yaml'),
    'the offline Helmfile takes ClamAV from <mirror>/clamav/clamav and lists clamav-image.yaml');
  ok(JSON.stringify(imageBlock('uploadScan:\n  enabled: true\n  image:\n    repository: clamav/clamav\n    tag: "1.4.3"\n    digest: sha256:abc\nother: 1\n', 'uploadScan')) === '["clamav/clamav","1.4.3","sha256:abc"]' &&
    imageBlock('uploadScan:\n  enabled: true\nimage:\n  repository: x/y\n  tag: "1"\n  digest: sha256:abc\n', 'uploadScan') === null, 'the image block is read under its own key only');
  ok(new RegExp(`^COPY --from=builder /app/build ${WEB_IMAGE_PATH}\\s*$`, 'm').test(fs.readFileSync(WEB_DOCKERFILE, 'utf8')), `the web Dockerfile copies the build to ${WEB_IMAGE_PATH}`);
  ok(imagesValues({ version: '1.2.0', build: 'v1-1', digests: {} }).includes('imageDigests: {}'), 'a dry run pins no digest');
  refuses(() => imagesValues({ version: '1.2.0', build: 'v1-1', digests: { web: 'sha256:x"\ny: 1' } }), /plain values/, 'images.yaml refuses a value that could change its shape');
  refuses(() => databaseImageValues({ tag: 'pg17\nimage: x', digest: timescale.digest }), /plain values/, 'timescaledb-image.yaml refuses a value that could change its shape');
  refuses(() => scannerImageValues({ tag: '1.4.3\nenabled: false', digest: clamav.digest }), /plain values/, 'clamav-image.yaml refuses a value that could change its shape');

  const helmFound = run('helm', ['version', '--short']).ok;
  if (!helmFound) {
    if (process.env.GITHUB_ACTIONS) throw new Error('offline bundle self-test: helm not found; failing because this is GitHub Actions');
    console.warn('offline bundle self-test: helm not found (set HELM); the end to end bundles are SKIPPED locally');
    console.log(`offline bundle self-test: ${checks} checks passed`);
    return 0;
  }

  const work = fs.mkdtempSync(path.join(os.tmpdir(), 'ariva-offline-bundle-selftest-'));
  const saved = { cosign: TOOLS.cosign, oras: TOOLS.oras, docker: TOOLS.docker, state: process.env.ARIVA_OFFLINE_STAND_IN };
  TOOLS.cosign = [process.execPath, standIns, 'cosign'];
  TOOLS.oras = [process.execPath, standIns, 'oras'];
  TOOLS.docker = [process.execPath, standIns, 'docker'];
  const quiet = console.log;
  try {
    const web = path.join(work, 'web-build');
    fs.mkdirSync(path.join(web, '_app', 'immutable'), { recursive: true });
    fs.writeFileSync(path.join(web, 'index.html'), '<!doctype html><title>Ariva</title>\n');
    fs.writeFileSync(path.join(web, '_app', 'immutable', 'app.js'), 'export {};\n');
    fs.writeFileSync(path.join(web, '_app', 'immutable', '-CWa0sji.js'), 'export {};\n'); // Vite chunk names can start with "-"
    fs.writeFileSync(path.join(web, '.gitkeep'), '');
    const state = selfTestState(work);
    process.env.ARIVA_OFFLINE_STAND_IN = state.file;
    const { registry, commit } = SELF_TEST;
    const options = (out, extra = {}) => ({ version: '1.2.0', build: 'v1.2.0-57', registry, repository: DEFAULT_REPOSITORY, out: path.join(work, out), commit, dryRun: false, ...extra });
    const flipByte = (file) => {
      const bytes = fs.readFileSync(file);
      bytes[bytes.length >> 1] ^= 0xff;
      fs.writeFileSync(file, bytes);
    };
    const save = () => fs.writeFileSync(state.file, JSON.stringify(state));
    console.log = () => {};

    // a dry run: charts, Helmfile, values and the checkout's web build; no image, no signature
    const dry = buildBundle(options('dry', { dryRun: true, version: '0.0.0-ci.1', build: 'ci-1', webBuild: web }));
    ok(verifyBundle(path.join(work, 'dry'), { dryRun: true }).ok, 'a dry run verifies with --dry-run');
    ok(dry.images.filter((i) => i.signed).every((i) => i.digest === null && i.layout === null) && dry.images.some((i) => i.name === DATABASE_IMAGE && i.digest === timescale.digest) &&
      dry.images.some((i) => i.name === SCANNER_IMAGE && i.digest === clamav.digest), 'a dry run lists the images: Ariva\'s unresolved, TimescaleDB and ClamAV by their pinned digests');
    for (const file of ['helm/helmfile.yaml.gotmpl', 'helm/charts/ariva-platform.tgz', 'helm/charts/ariva-timescaledb.tgz', 'helm/values/values-k8s-prd.yaml', 'helm/values/timescaledb.yaml',
      IMAGES_VALUES, DATABASE_IMAGE_VALUES, SCANNER_IMAGE_VALUES, 'web/index.html', 'web/_app/immutable/app.js', 'web/_app/immutable/-CWa0sji.js']) {
      ok(dry.files.some((f) => f.path === file), `the dry run carries ${file}`);
    }
    ok(dry.web.from === 'build' && !dry.files.some((f) => f.path.endsWith('.gitkeep')), 'the checkout\'s web build, without the .gitkeep placeholder');
    ok(!fs.existsSync(path.join(work, 'dry', 'images')) && !fs.existsSync(path.join(work, 'dry', SIGNATURE)), 'a dry run pulls and signs nothing');
    flipByte(path.join(work, 'dry', 'helm', 'charts', 'ariva-platform.tgz'));
    ok(!verifyBundle(path.join(work, 'dry'), { dryRun: true }).ok, 'a tampered chart archive fails the dry run');

    // a release: images verified at this commit and copied by digest, SBOMs from the attestations, the web build out of the
    // web image, then signed by the stand-in's signing run and verified as the site's connected machine does
    const certificate = { repository: DEFAULT_REPOSITORY, ref: 'refs/tags/v1.2.0', sha: commit, trigger: 'push' };
    state.images = [
      ...ARIVA_IMAGES.map((name) => ({ name, source: `${registry}/${name}`, tag: 'v1.2.0-57', digest: fixtures.syntheticImage(name, true).digest, signed: true, certificate })),
      { name: DATABASE_IMAGE, source: timescale.source, tag: timescale.tag, digest: timescale.digest, signed: false },
      { name: SCANNER_IMAGE, source: clamav.source, tag: clamav.tag, digest: clamav.digest, signed: false }
    ];
    for (const name of ARIVA_IMAGES) state.sboms[`${registry}/${name}`] = fixtures.syntheticSbom(name);
    save();
    // The stand-in registry cannot serve the real TimescaleDB digest the chart pins: the copy fails and stops the bundle.
    refuses(() => buildBundle(options('pinned')), /oras copy docker\.io\/timescale\/timescaledb-ha@sha256:/, 'a failed image copy stops the bundle');
    // From here TimescaleDB and ClamAV stand in as synthetic third-party images under their charts' repositories and tags.
    const synthetic = { ...timescale, digest: fixtures.syntheticImage(DATABASE_IMAGE, false).digest };
    const syntheticScanner = { ...clamav, digest: fixtures.syntheticImage(SCANNER_IMAGE, false).digest };
    state.images.find((i) => i.name === DATABASE_IMAGE).digest = synthetic.digest;
    state.images.find((i) => i.name === SCANNER_IMAGE).digest = syntheticScanner.digest;
    save();
    refuses(() => buildBundle(options('no-scanner', { thirdParty: [synthetic] })), /no ClamAV image among the third-party images/, 'a bundle without the scanner image is refused');
    const thirdParty = [synthetic, syntheticScanner];
    const built = buildBundle(options('release', { thirdParty }));
    ok(built.images.filter((i) => i.signed).every((i) => i.digest === state.images.find((s) => s.name === i.name).digest), 'each Ariva image is copied by its signed digest');
    ok(built.images.find((i) => i.name === DATABASE_IMAGE).digest === synthetic.digest && built.images.find((i) => i.name === SCANNER_IMAGE).digest === syntheticScanner.digest,
      'the third-party images are copied by their pinned digests');
    ok(built.web.from === 'image' && built.web.digest === built.images.find((i) => i.name === 'web').digest, 'the web build comes from the signed web image');
    ok(Object.entries(fixtures.WEB_IMAGE_FILES).every(([file, content]) => fs.readFileSync(path.join(work, 'release', 'web', ...file.split('/')), 'utf8') === content), 'the web files are the web image\'s');
    ok(fs.readFileSync(path.join(work, 'release', ...IMAGES_VALUES.split('/')), 'utf8').includes(`  api-main: "${built.images[0].digest}"`), 'images.yaml pins the digests');
    ok(fs.readFileSync(path.join(work, 'release', ...DATABASE_IMAGE_VALUES.split('/')), 'utf8').includes(`  digest: "${synthetic.digest}"`), 'timescaledb-image.yaml pins TimescaleDB');
    ok(fs.readFileSync(path.join(work, 'release', ...SCANNER_IMAGE_VALUES.split('/')), 'utf8') === scannerImageValues(syntheticScanner), 'clamav-image.yaml pins ClamAV');
    selfTestSign(path.join(work, 'release'), state, { repository: DEFAULT_REPOSITORY, ref: 'refs/tags/v1.2.0', sha: commit, trigger: 'workflow_dispatch' });
    const verified = verifyBundle(path.join(work, 'release'), { version: '1.2.0' });
    ok(verified.ok, `the release bundle verifies (${verified.problems.join(' | ')})`);
    const layer = fixtures.syntheticImage('web', true).blobs[1].digest.slice(7);
    flipByte(path.join(work, 'release', 'images', 'web', 'blobs', 'sha256', layer));
    ok(!verifyBundle(path.join(work, 'release'), { version: '1.2.0' }).ok, 'a tampered image archive fails');

    // refusals: images built at another commit, an unsigned image, a link in the web image, an existing folder, a web
    // build without index.html or with a link
    refuses(() => buildBundle(options('other-commit', { thirdParty, commit: 'b'.repeat(40) })), /api-main:v1\.2\.0-57 is not signed by .* at commit bbbbbbbbbbbb/,
      'images built at another commit than the checkout are refused');
    state.webLink = true;
    save();
    let linkable = os.platform() !== 'win32';
    if (linkable) refuses(() => buildBundle(options('web-link', { thirdParty })), /the web build: .*not a regular file or folder/, 'a link in the web image\'s files is refused');
    state.webLink = false;
    state.webRootLink = linkable;
    save();
    if (linkable) refuses(() => buildBundle(options('web-root-link', { thirdParty })), /is not a folder \(a link is refused\)/, 'a link at the web image\'s path is refused');
    state.webRootLink = false;
    state.images.find((i) => i.name === 'api-cronz').signed = false;
    save();
    refuses(() => buildBundle(options('unsigned', { thirdParty })), /api-cronz:v1\.2\.0-57 is not signed by alsadderwork-coder\/Ariva's images workflow/, 'an unsigned image is refused');
    refuses(() => buildBundle(options('dry', { dryRun: true, webBuild: web })), /exists: name a new folder/, 'an existing folder is refused');
    refuses(() => buildBundle(options('inside', { dryRun: true, webBuild: work, out: path.join(work, 'inside') })), /cannot be inside the web build/, 'an output inside the web build is refused');
    ok(!within(web, `${web}..x`) && within(web, path.join(web, 'x')), 'a sibling folder whose name starts with the web build\'s is not inside it');
    fs.rmSync(path.join(web, 'index.html'));
    refuses(() => buildBundle(options('no-index', { dryRun: true, webBuild: web })), /has no index\.html/, 'a web build without index.html is refused');
    fs.writeFileSync(path.join(web, 'index.html'), '<!doctype html>\n');
    try {
      fs.symlinkSync(os.platform() === 'win32' ? 'C:\\Windows\\win.ini' : '/etc/hostname', path.join(web, 'linked.txt'));
      linkable = true;
    } catch {
      linkable = false;
      console.warn('offline bundle self-test: this machine cannot create a symbolic link; the link cases are SKIPPED');
    }
    if (linkable) refuses(() => buildBundle(options('linked', { dryRun: true, webBuild: web })), /not a regular file or folder/, 'a link in the web build is refused');
  } finally {
    console.log = quiet;
    TOOLS.cosign = saved.cosign;
    TOOLS.oras = saved.oras;
    TOOLS.docker = saved.docker;
    if (saved.state === undefined) delete process.env.ARIVA_OFFLINE_STAND_IN;
    else process.env.ARIVA_OFFLINE_STAND_IN = saved.state;
    fs.rmSync(work, { recursive: true, force: true });
  }
  console.log(`offline bundle self-test: ${checks} checks passed`);
  return 0;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const code = main(process.argv.slice(2));
    if (code instanceof Promise) code.then((c) => { process.exitCode = c; }, (error) => { console.error(error.stack ?? error.message); process.exitCode = 1; });
    else process.exitCode = code;
  } catch (error) {
    if (error instanceof UsageError) {
      console.error(`bundle: ${error.message}\nusage: node scripts/offline/bundle.mjs --version <1.2.0> --build <v1.2.0-57> --out <new folder> [--registry <path>] [--repository <owner/name>]\n       node scripts/offline/bundle.mjs --dry-run --version <v> --build <tag> --web-build <folder> --out <new folder> | --self-test`);
      process.exitCode = 2;
    } else {
      console.error(error instanceof Refused ? `FAIL ${error.message}` : error.stack ?? error.message);
      process.exitCode = 1;
    }
  }
}
