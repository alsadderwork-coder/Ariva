#!/usr/bin/env node
// Verifies an Ariva offline install bundle (ARV-127; wiki 04 section 12) on a connected machine, before it is carried into
// the site. Needs Node 22 and cosign 3 (https://github.com/sigstore/cosign), with read access to the registry that holds
// the images (GitHub Container Registry: docker login ghcr.io with a token that can read packages).
//
//   node scripts/offline/verify.mjs <bundle folder> --version <1.2.0> [--from-main] [--repository <owner/name>]
//   node scripts/offline/verify.mjs --dry-run <bundle folder>        a CI dry run's bundle: files and hashes only
//   node scripts/offline/verify.mjs --self-test
//
// Run this file from the repository at the release tag, never a copy that travelled with the bundle (a tampered bundle can
// carry a tampered verifier), and from outside the bundle folder. --version names the release the operator expects: the
// bundle must have been signed by the workflow run on the tag v<version>, or on main with --from-main. On PASS it prints the
// SHA-256 of manifest.json and of SHA256SUMS: record them, since the check inside the site compares against them.
//
// What it checks, in this order; any failure fails the bundle (CWE-494, CWE-345, CWE-347):
// 1. It runs from outside the bundle folder, and starts every program in a private empty folder, so no program in the
//    bundle can run in place of cosign (on Windows the current folder is searched first).
// 2. The folder holds regular files and folders only: a link, device or pipe is refused, so nothing outside the folder is
//    followed (a hard link is a regular file, and its content must still match the signed hash).
// 3. manifest.json is signed, keyless, by this repository's offline-bundle workflow, run by hand on the tag v<version> (or
//    on main with --from-main). The bytes verified are copied into a private folder first and are the bytes parsed
//    afterwards; nothing in the manifest is used before that signature holds. Then the manifest must name that version, and
//    a second verification requires the signing run's commit to be the commit the manifest names.
// 4. Every file the manifest names exists with its size and SHA-256, no other file is present, and SHA256SUMS says the
//    same. Paths are relative, plain names only (no "..", no leading dot, no drive or root), and are only ever matched
//    against what the folder walk found. The values files that decide what the pods pull (images.yaml and
//    timescaledb-image.yaml) say exactly what the manifest says.
// 5. Each image's OCI layout: index.json names the image's digest under its tag, once; every blob the image needs is
//    present; every blob hashes to its name; and an Ariva image's layout carries its signature and SBOM attestation
//    (cosign 3 Sigstore bundles as OCI referrers), so the site's mirror receives them.
// 6. Each Ariva image digest is signed by this repository's images workflow at the manifest's commit, on a version tag or
//    main, and its CycloneDX attestation verifies, in the registry the manifest names; the SBOM in the bundle is the
//    attested one. Third-party images (TimescaleDB) carry no Dalil signature: their digest is the one the chart pins,
//    recorded in the signed manifest.

import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath, pathToFileURL } from 'node:url';
import assert from 'node:assert/strict';

export const FORMAT = 'ariva-offline-bundle/1';
export const MANIFEST = 'manifest.json';
export const SIGNATURE = 'manifest.json.sigstore.json';
export const SUMS = 'SHA256SUMS';
export const IMAGES_VALUES = 'helm/values/images.yaml';
export const DATABASE_IMAGE_VALUES = 'helm/values/timescaledb-image.yaml';
export const DEFAULT_REPOSITORY = 'alsadderwork-coder/Ariva';
export const ISSUER = 'https://token.actions.githubusercontent.com';
export const BUNDLE_WORKFLOW = 'offline-bundle.yml';
export const BUNDLE_TRIGGER = 'workflow_dispatch';
export const IMAGES_WORKFLOW = 'images.yml';
export const ARIVA_IMAGES = ['api-main', 'api-ingest', 'api-stream', 'api-cronz', 'api-integration', 'simulation', 'web'];
export const DATABASE_IMAGE = 'timescaledb';

const SIGSTORE_BUNDLE = 'application/vnd.dev.sigstore.bundle.v0.3+json';
const SIGNATURE_PREDICATE = 'https://sigstore.dev/cosign/sign/v1';
const SBOM_PREDICATE = 'https://cyclonedx.org/bom';
const INDEX_TYPES = new Set(['application/vnd.oci.image.index.v1+json', 'application/vnd.docker.distribution.manifest.list.v2+json']);
const MANIFEST_TYPES = new Set(['application/vnd.oci.image.manifest.v1+json', 'application/vnd.docker.distribution.manifest.v2+json']);
const REF_NAME = 'org.opencontainers.image.ref.name';
const MiB = 1024 * 1024;
const LIMITS = { manifest: 16 * MiB, signature: 1 * MiB, json: 4 * MiB, sbom: 64 * MiB, files: 100_000, blobsPerImage: 10_000 };

export const PATTERNS = {
  version: /^\d{1,4}\.\d{1,4}\.\d{1,6}(-[0-9A-Za-z.-]{1,64})?$/,
  build: /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/,
  tag: /^[A-Za-z0-9_][A-Za-z0-9._-]{0,127}$/,
  repository: /^[A-Za-z0-9][A-Za-z0-9-]{0,38}\/[A-Za-z0-9_][A-Za-z0-9._-]{0,99}$/,
  // host[:port] and one to four path segments, lower case (registry/path of an image)
  registry: /^[a-z0-9]([a-z0-9.-]{0,251}[a-z0-9])?(:[0-9]{1,5})?(\/[a-z0-9]([a-z0-9._-]{0,126}[a-z0-9])?){1,4}$/,
  imagePath: /^[a-z0-9]([a-z0-9._-]{0,126}[a-z0-9])?(\/[a-z0-9]([a-z0-9._-]{0,126}[a-z0-9])?){0,3}$/,
  name: /^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$/,
  chartVersion: /^\d{1,4}\.\d{1,4}\.\d{1,6}(-[0-9A-Za-z.-]{1,64})?$/,
  commit: /^[0-9a-f]{40}$/,
  digest: /^sha256:[0-9a-f]{64}$/,
  sha256: /^[0-9a-f]{64}$/,
  // a relative path of plain names: no empty, "." or ".." segment, no leading dot, no backslash, drive or root. A name may
  // start with "-" (Vite's chunk hashes do); paths are only matched against the folder walk, never passed to a program.
  file: /^[A-Za-z0-9_-][A-Za-z0-9._-]{0,199}(\/[A-Za-z0-9_-][A-Za-z0-9._-]{0,199}){0,15}$/
};

export class UsageError extends Error {}

export class Refused extends Error {}

// The programs this tool starts. The self-tests replace cosign and ORAS with stand-ins (scripts/offline/stand-ins.mjs).
export const TOOLS = { cosign: ['cosign'], oras: ['oras'] };

// Every program starts in a private empty folder: Windows looks for a program in the current folder before PATH, and
// the current folder could be a bundle carrying its own cosign.exe (CWE-427).
let runFolder = null;
function privateRunFolder() {
  if (runFolder === null) {
    runFolder = fs.mkdtempSync(path.join(os.tmpdir(), 'ariva-offline-run-'));
    const folder = runFolder;
    process.once('exit', () => fs.rmSync(folder, { recursive: true, force: true }));
  }
  return runFolder;
}

export function run(tool, args) {
  const [program, ...before] = TOOLS[tool];
  const result = spawnSync(program, [...before, ...args], { cwd: privateRunFolder(), encoding: 'utf8', shell: false, maxBuffer: 512 * MiB });
  if (result.error) return { ok: false, stdout: '', stderr: `${tool}: ${result.error.message}` };
  return { ok: result.status === 0, stdout: result.stdout ?? '', stderr: result.stderr ?? '' };
}

const firstLine = (text) => String(text).trim().split('\n').find((line) => line.trim()) ?? '(no output)';

/** Who may have signed: the named workflow of this repository, on a version tag or main (wiki 04 section 6.5). */
export function signer(repository, workflow) {
  if (!PATTERNS.repository.test(repository)) throw new UsageError('the repository must be owner/name');
  return `^https://github\\.com/${repository.replaceAll('.', '\\.')}/\\.github/workflows/${workflow.replaceAll('.', '\\.')}@refs/(tags/v.+|heads/main)$`;
}

/** The exact identity of the named workflow run on one ref (refs/tags/v<version> or refs/heads/main). */
export function identityOf(repository, workflow, ref) {
  if (!PATTERNS.repository.test(repository)) throw new UsageError('the repository must be owner/name');
  if (!/^refs\/(heads\/main|tags\/v\d{1,4}\.\d{1,4}\.\d{1,6}(-[0-9A-Za-z.-]{1,64})?)$/.test(ref)) throw new UsageError(`${ref} is not a version tag or main`);
  return `https://github.com/${repository}/.github/workflows/${workflow}@${ref}`;
}

/** The ref a bundle for this version was built on: its version tag, or main when the operator says so. */
export const bundleRef = ({ version, fromMain }) => (fromMain ? 'refs/heads/main' : `refs/tags/v${version}`);

/**
 * cosign's certificate checks: the identity (exact when a ref is given, else any version tag or main), the issuer and the
 * repository, and when given the run's commit and trigger.
 */
export function trustArgs(repository, workflow, { ref = null, sha = null, trigger = null } = {}) {
  if (sha !== null && !PATTERNS.commit.test(sha)) throw new UsageError('a commit is 40 hexadecimal digits');
  const identity = ref === null ? ['--certificate-identity-regexp', signer(repository, workflow)] : ['--certificate-identity', identityOf(repository, workflow, ref)];
  return [...identity, '--certificate-oidc-issuer', ISSUER, '--certificate-github-workflow-repository', repository,
    ...(sha === null ? [] : ['--certificate-github-workflow-sha', sha]), ...(trigger === null ? [] : ['--certificate-github-workflow-trigger', trigger])];
}

export function pathAllowed(value) {
  return typeof value === 'string' && value.length <= 1000 && PATTERNS.file.test(value);
}

const join = (root, relative) => path.join(root, ...relative.split('/'));

/** Every regular file under the folder (relative, "/" separated, sorted); links, devices and the like are problems. */
export function walk(root) {
  const files = [];
  const problems = [];
  const visit = (folder, relative) => {
    const entries = fs.readdirSync(folder, { withFileTypes: true }).sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
    for (const entry of entries) {
      const name = relative ? `${relative}/${entry.name}` : entry.name;
      if (entry.isDirectory()) visit(path.join(folder, entry.name), name);
      else if (entry.isFile()) files.push(name);
      else problems.push(`${JSON.stringify(name)} is not a regular file or folder (links, devices and pipes are refused)`);
      if (files.length > LIMITS.files) throw new Refused(`more than ${LIMITS.files} files`);
    }
  };
  visit(root, '');
  return { files, problems };
}

/** Opens a regular file for reading: never through a link (O_NOFOLLOW), never blocking on a pipe (O_NONBLOCK). */
function openRegular(file) {
  const flags = fs.constants.O_RDONLY | (fs.constants.O_NOFOLLOW ?? 0) | (fs.constants.O_NONBLOCK ?? 0);
  const fd = fs.openSync(file, flags);
  if (!fs.fstatSync(fd).isFile()) {
    fs.closeSync(fd);
    throw new Refused(`${path.basename(file)} is not a regular file`);
  }
  return fd;
}

/** SHA-256 and size of one open file, read in 1 MiB pieces. */
export function hashFile(file) {
  const fd = openRegular(file);
  try {
    const hash = crypto.createHash('sha256');
    const buffer = Buffer.allocUnsafe(MiB);
    let size = 0;
    for (let read; (read = fs.readSync(fd, buffer, 0, buffer.length, null)) > 0;) {
      hash.update(buffer.subarray(0, read));
      size += read;
    }
    return { sha256: hash.digest('hex'), size };
  } finally {
    fs.closeSync(fd);
  }
}

export function readCapped(file, maxBytes) {
  const fd = openRegular(file);
  try {
    const { size } = fs.fstatSync(fd);
    if (size > maxBytes) throw new Refused(`${path.basename(file)} is larger than ${maxBytes} bytes`);
    const buffer = Buffer.alloc(size);
    let offset = 0;
    for (let read; offset < size && (read = fs.readSync(fd, buffer, offset, size - offset, null)) > 0;) offset += read;
    return buffer.subarray(0, offset);
  } finally {
    fs.closeSync(fd);
  }
}

const sha256Of = (bytes) => crypto.createHash('sha256').update(bytes).digest('hex');
const byPath = (a, b) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0);

/** The files a manifest lists: every regular file of the folder except the manifest and its signature, hashed. */
export function describe(root) {
  const { files, problems } = walk(root);
  if (problems.length) throw new Refused(problems.join('; '));
  return files.filter((file) => file !== MANIFEST && file !== SIGNATURE).map((file) => {
    if (!pathAllowed(file)) throw new Refused(`${JSON.stringify(file)} is not a plain relative path`);
    return { path: file, ...hashFile(join(root, file)) };
  }).sort(byPath).map(({ path: file, size, sha256 }) => ({ path: file, size, sha256 }));
}

/** SHA256SUMS: one "<sha256>  <path>" line per file (the format sha256sum -c reads), itself left out. */
export function sumsText(files) {
  return files.filter((f) => f.path !== SUMS).sort(byPath).map((f) => `${f.sha256}  ${f.path}\n`).join('');
}

const plain = (value) => {
  if (typeof value !== 'string' || !/^[A-Za-z0-9.:_-]+$/.test(value)) throw new Refused('the values files take plain values only');
  return value;
};

/**
 * helm/values/images.yaml: what the platform's pods run. The bundle's Helmfile lists it last for the platform release and
 * refuses to render unless it pins all seven Ariva digests, so a dry run (which pins none) cannot be installed.
 */
export function imagesValues({ version, build, digests }) {
  const pinned = ARIVA_IMAGES.filter((name) => digests?.[name]);
  return [
    '# The images of this bundle (ARV-127), written by scripts/offline/bundle.mjs. The pods pull each Ariva image by the',
    '# digest the release verified; the Helmfile\'s state value mirror sets imageRepository. A dry run pins no digest, and',
    '# the bundle\'s Helmfile refuses to render without all seven.',
    `releaseVersion: "${plain(version)}"`,
    `buildNumber: "${plain(build)}"`,
    ...(pinned.length === 0 ? ['imageDigests: {}'] : ['imageDigests:', ...pinned.map((name) => `  ${name}: "${plain(digests[name])}"`)])
  ].join('\n') + '\n';
}

/** helm/values/timescaledb-image.yaml: the tag and digest the database chart pins, listed after the site's file. */
export function databaseImageValues({ tag, digest }) {
  return [
    '# The TimescaleDB image of this bundle (ARV-127), written by scripts/offline/bundle.mjs: the tag and digest its chart',
    '# pins, listed after the site\'s databaseValues so no site file can change them; the Helmfile\'s state value mirror sets',
    '# image.repository.',
    'image:',
    `  tag: "${plain(tag)}"`,
    `  digest: "${plain(digest)}"`
  ].join('\n') + '\n';
}

/** Writes SHA256SUMS and manifest.json for a finished bundle folder; the release workflow then signs manifest.json. */
export function writeManifest(root, head) {
  for (const name of [MANIFEST, SIGNATURE, SUMS]) {
    if (fs.existsSync(path.join(root, name))) throw new Refused(`${name} already exists in the bundle folder`);
  }
  fs.writeFileSync(path.join(root, SUMS), sumsText(describe(root)), { flag: 'wx' });
  const manifest = { format: FORMAT, ...head, files: describe(root) };
  const problems = manifestProblems(manifest, head.dryRun);
  if (problems.length) throw new Refused(problems.join('; '));
  fs.writeFileSync(path.join(root, MANIFEST), `${JSON.stringify(manifest, null, 2)}\n`, { flag: 'wx' });
  return manifest;
}

/** Every rule a manifest must meet before any of it is used; the derived fields must be exactly what they derive from. */
export function manifestProblems(m, dryRun) {
  if (!m || typeof m !== 'object' || Array.isArray(m)) return ['manifest.json is not a JSON object'];
  const problems = [];
  const need = (ok, message) => {
    if (!ok) problems.push(`manifest.json: ${message}`);
  };
  const text = (value, pattern) => typeof value === 'string' && pattern.test(value);
  need(m.format === FORMAT, `format must be ${FORMAT}`);
  need(text(m.version, PATTERNS.version), 'version is not a release version (1.2.0 or 1.2.0-rc.1)');
  need(text(m.build, PATTERNS.build), 'build is not an image tag');
  need(text(m.commit, PATTERNS.commit), 'commit is not a full commit id');
  need(typeof m.createdUtc === 'string' && !Number.isNaN(Date.parse(m.createdUtc)), 'createdUtc is not a time');
  need(m.dryRun === dryRun, dryRun ? 'not a dry run bundle: verify it without --dry-run' : 'a dry run bundle (no images, not signed) cannot be installed');
  const registry = m.source?.registry;
  need(text(m.source?.repository, PATTERNS.repository), 'source.repository is not owner/name');
  need(text(registry, PATTERNS.registry), 'source.registry is not a registry path');

  const files = Array.isArray(m.files) ? m.files : [];
  need(Array.isArray(m.files), 'files is not a list');
  const listed = new Set();
  for (const f of files) {
    if (!f || !pathAllowed(f.path) || !Number.isSafeInteger(f.size) || f.size < 0 || !text(f.sha256, PATTERNS.sha256)) {
      need(false, `the file entry ${JSON.stringify(f?.path ?? null)} is not a plain relative path with a size and SHA-256`);
      continue;
    }
    need(f.path !== SIGNATURE, `${f.path} cannot be listed`);
    need(!listed.has(f.path), `${f.path} is listed twice`);
    listed.add(f.path);
  }
  for (const file of [SUMS, IMAGES_VALUES, DATABASE_IMAGE_VALUES]) need(listed.has(file), `${file} is not listed`);

  const charts = Array.isArray(m.charts) ? m.charts : [];
  need(charts.length > 0, 'charts lists no chart');
  for (const chart of charts) {
    need(text(chart?.name, PATTERNS.name) && text(chart?.version, PATTERNS.chartVersion) && pathAllowed(chart?.path) && listed.has(chart.path),
      `the chart entry ${JSON.stringify(chart?.name ?? null)} needs a name, a version and a listed path`);
  }

  const images = Array.isArray(m.images) ? m.images : [];
  const names = images.map((image) => image?.name);
  for (const name of [...ARIVA_IMAGES, DATABASE_IMAGE]) need(names.filter((n) => n === name).length === 1, `images must list ${name} once`);
  for (const image of images) {
    const label = `image ${JSON.stringify(image?.name ?? null)}`;
    if (!image || !text(image.name, PATTERNS.name)) {
      need(false, `${label} has no valid name`);
      continue;
    }
    need(names.filter((n) => n === image.name).length === 1, `${label} is listed twice`);
    const ariva = ARIVA_IMAGES.includes(image.name);
    need(image.signed === ariva, `${label}: signed must be ${ariva} (Ariva images are signed by the images workflow; third-party images are pinned by the chart's digest)`);
    if (ariva) {
      need(image.source === `${registry}/${image.name}`, `${label}: source must be ${registry}/${image.name}`);
      need(image.tag === m.build, `${label}: tag must be the build ${m.build}`);
      need(image.mirrorPath === `ariva/${image.name}`, `${label}: mirrorPath must be ariva/${image.name}`);
    } else {
      need(text(image.source, PATTERNS.registry), `${label}: source is not a registry path`);
      need(text(image.tag, PATTERNS.tag), `${label}: tag is not an image tag`);
      need(text(image.mirrorPath, PATTERNS.imagePath) && !image.mirrorPath.startsWith('ariva/'), `${label}: mirrorPath is not an image path outside ariva/`);
    }
    if (dryRun) {
      need(image.layout === null && image.sbom === null, `${label}: a dry run carries no image layout or SBOM`);
      need(ariva ? image.digest === null : text(image.digest, PATTERNS.digest), `${label}: ${ariva ? 'a dry run resolves no Ariva image digest' : 'digest must be the sha256 digest the chart pins'}`);
    } else {
      need(text(image.digest, PATTERNS.digest), `${label}: digest is not a sha256 digest`);
      need(image.layout === `images/${image.name}`, `${label}: layout must be images/${image.name}`);
      need(image.sbom === (ariva ? `sboms/${image.name}.cdx.json` : null), `${label}: sbom must be ${ariva ? `sboms/${image.name}.cdx.json` : 'null'}`);
      if (ariva) need(listed.has(image.sbom), `${label}: ${image.sbom} is not listed`);
    }
  }
  // The web build: the files of the signed web image (a release), or the build of the checkout (a dry run).
  const webDigest = images.find((image) => image?.name === 'web')?.digest ?? null;
  need(dryRun ? m.web?.from === 'build' && m.web?.digest === null : m.web?.from === 'image' && m.web?.digest === webDigest,
    dryRun ? 'web must be { from: "build", digest: null } in a dry run' : 'web must be { from: "image" } with the web image\'s digest');
  return problems;
}

function manifestSignature(root, manifestBytes, repository, certificate) {
  const folder = fs.mkdtempSync(path.join(os.tmpdir(), 'ariva-offline-verify-'));
  try {
    const manifestCopy = path.join(folder, MANIFEST);
    const signatureCopy = path.join(folder, SIGNATURE);
    fs.writeFileSync(manifestCopy, manifestBytes, { flag: 'wx', mode: 0o600 });
    fs.writeFileSync(signatureCopy, readCapped(path.join(root, SIGNATURE), LIMITS.signature), { flag: 'wx', mode: 0o600 });
    return run('cosign', ['verify-blob', '--bundle', signatureCopy, ...trustArgs(repository, BUNDLE_WORKFLOW, certificate), manifestCopy]);
  } finally {
    fs.rmSync(folder, { recursive: true, force: true });
  }
}

/** The OCI layout of one image: tagged digest, every blob it needs, and (Ariva images) its signature and SBOM referrers. */
export function layoutProblems(root, image, hashes) {
  const problems = [];
  const blobPath = (digest) => `${image.layout}/blobs/sha256/${digest.slice(7)}`;
  const readJson = (relative) => JSON.parse(readCapped(join(root, relative), LIMITS.json).toString('utf8'));
  try {
    for (const file of ['oci-layout', 'index.json']) {
      if (!hashes.has(`${image.layout}/${file}`)) throw new Refused(`${image.layout}/${file} is missing`);
    }
    if (readJson(`${image.layout}/oci-layout`)?.imageLayoutVersion !== '1.0.0') problems.push(`${image.layout}/oci-layout is not an OCI image layout 1.0.0`);
    const descriptors = readJson(`${image.layout}/index.json`)?.manifests;
    if (!Array.isArray(descriptors)) throw new Refused(`${image.layout}/index.json lists no manifests`);
    const tagged = descriptors.filter((d) => d?.annotations?.[REF_NAME] === image.tag);
    if (tagged.length !== 1 || tagged[0].digest !== image.digest) {
      problems.push(`${image.layout}/index.json does not name ${image.digest} as ${image.tag}, once`);
      return problems;
    }
    const queue = [{ digest: image.digest, mediaType: tagged[0].mediaType }];
    const seen = new Set();
    while (queue.length > 0) {
      const { digest, mediaType } = queue.pop();
      if (typeof digest !== 'string' || !PATTERNS.digest.test(digest)) {
        problems.push(`${image.layout}: a descriptor names ${JSON.stringify(digest)}, not a sha256 digest`);
        continue;
      }
      if (seen.has(digest)) continue;
      seen.add(digest);
      if (seen.size > LIMITS.blobsPerImage) throw new Refused(`${image.layout}: more than ${LIMITS.blobsPerImage} blobs`);
      if (!hashes.has(blobPath(digest))) {
        problems.push(`${blobPath(digest)} is missing (${image.name} needs it)`);
        continue;
      }
      if (INDEX_TYPES.has(mediaType)) {
        for (const child of readJson(blobPath(digest))?.manifests ?? []) queue.push({ digest: child?.digest, mediaType: child?.mediaType });
      } else if (MANIFEST_TYPES.has(mediaType)) {
        const manifest = readJson(blobPath(digest));
        for (const part of [manifest?.config, ...(manifest?.layers ?? [])]) if (part) queue.push({ digest: part.digest, mediaType: part.mediaType });
      }
    }
    if (image.signed) {
      const found = new Map([[SIGNATURE_PREDICATE, 0], [SBOM_PREDICATE, 0]]);
      for (const d of descriptors) {
        if (!MANIFEST_TYPES.has(d?.mediaType) || typeof d.digest !== 'string' || !PATTERNS.digest.test(d.digest) || !hashes.has(blobPath(d.digest))) continue;
        const referrer = readJson(blobPath(d.digest));
        if (referrer?.subject?.digest !== image.digest || referrer.artifactType !== SIGSTORE_BUNDLE) continue;
        const type = referrer.annotations?.['dev.sigstore.bundle.predicateType'];
        const complete = (referrer.layers ?? []).every((l) => typeof l?.digest === 'string' && PATTERNS.digest.test(l.digest) && hashes.has(blobPath(l.digest)));
        if (found.has(type) && complete) found.set(type, found.get(type) + 1);
      }
      if (!found.get(SIGNATURE_PREDICATE)) problems.push(`${image.layout} carries no signature of ${image.digest}`);
      if (!found.get(SBOM_PREDICATE)) problems.push(`${image.layout} carries no SBOM attestation of ${image.digest}`);
    }
  } catch (error) {
    problems.push(error instanceof Refused ? error.message : `${image.layout}: ${error.message}`);
  }
  return problems;
}

/**
 * The registry's word on one Ariva image: its digest signed by the images workflow at the bundle's commit, its SBOM
 * attested and equal to the bundle's.
 */
export function registryProblems(root, image, repository, commit) {
  const reference = `${image.source}@${image.digest}`;
  const trust = trustArgs(repository, IMAGES_WORKFLOW, { sha: commit });
  const signed = run('cosign', ['verify', ...trust, reference]);
  if (!signed.ok) return [`${reference} is not signed by ${repository}'s images workflow at commit ${commit.slice(0, 12)}: ${firstLine(signed.stderr)}`];
  try {
    const digests = JSON.parse(signed.stdout).map((s) => s?.critical?.image?.['docker-manifest-digest']);
    if (digests.length === 0 || digests.some((d) => d !== image.digest)) return [`${reference}: the signatures name another digest`];
  } catch {
    return [`${reference}: cosign verify printed no signatures`];
  }
  const attested = run('cosign', ['verify-attestation', '--type', 'cyclonedx', ...trust, reference]);
  if (!attested.ok) return [`${reference} has no CycloneDX attestation from ${repository}'s images workflow at commit ${commit.slice(0, 12)}: ${firstLine(attested.stderr)}`];
  let ours;
  try {
    ours = JSON.stringify(JSON.parse(readCapped(join(root, image.sbom), LIMITS.sbom).toString('utf8')));
  } catch (error) {
    return [`${image.sbom}: ${error.message}`];
  }
  const predicates = [];
  for (const line of attested.stdout.split('\n').filter((l) => l.trim())) {
    try {
      const statement = JSON.parse(Buffer.from(JSON.parse(line).payload, 'base64').toString('utf8'));
      const subject = (statement.subject ?? []).some((s) => `sha256:${s?.digest?.sha256}` === image.digest);
      if (statement.predicateType === SBOM_PREDICATE && subject) predicates.push(JSON.stringify(statement.predicate));
    } catch {
      // a line that is not an attestation envelope proves nothing
    }
  }
  if (predicates.length === 0) return [`${reference}: no verified CycloneDX attestation names this digest`];
  if (!predicates.includes(ours)) return [`${image.sbom} is not the SBOM attested for ${reference}`];
  return [];
}

// Paths as the system resolves them; Windows compares them without case (C:\\Bundles and c:\\bundles are one folder).
const canonical = (target) => {
  const real = (fs.realpathSync.native ?? fs.realpathSync)(target);
  return process.platform === 'win32' ? real.toLowerCase() : real;
};
const inside = (root, candidate) => candidate === root || candidate.startsWith(root.endsWith(path.sep) ? root : root + path.sep);

/**
 * Verifies a bundle folder. Returns { ok, problems, manifest, manifestSha256, sumsSha256 }. Throws UsageError for a bad
 * call; every finding about the bundle itself is a problem.
 */
export function verifyBundle(folder, { dryRun = false, repository = DEFAULT_REPOSITORY, version = null, fromMain = false, log = () => {} } = {}) {
  if (!PATTERNS.repository.test(repository)) throw new UsageError('--repository must be owner/name');
  if (!dryRun && !PATTERNS.version.test(version ?? '')) throw new UsageError('--version must name the release you expect, such as 1.2.0');
  if (!fs.existsSync(folder) || !fs.statSync(folder).isDirectory()) throw new UsageError(`${folder} is not a folder`);
  const root = fs.realpathSync(path.resolve(folder));
  const done = (problems, manifest = null, hashes = {}) => ({ ok: problems.length === 0, problems, manifest, ...hashes });
  if (inside(canonical(root), canonical(process.cwd()))) return done(['run the verifier from outside the bundle folder: a program in the bundle must never be started in place of cosign']);
  let walked;
  try {
    walked = walk(root);
  } catch (error) {
    return done([error.message]);
  }
  const { files } = walked;
  const problems = [...walked.problems];
  if (!files.includes(MANIFEST)) return done([...problems, `${MANIFEST} is missing`]);
  let manifestBytes;
  try {
    manifestBytes = readCapped(path.join(root, MANIFEST), LIMITS.manifest);
  } catch (error) {
    return done([...problems, error.message]);
  }
  const ref = dryRun ? null : bundleRef({ version, fromMain });
  if (dryRun) {
    if (files.includes(SIGNATURE)) problems.push(`${SIGNATURE} is present: a dry run is never signed`);
  } else {
    if (!files.includes(SIGNATURE)) return done([...problems, `${SIGNATURE} is missing: the bundle is not signed`]);
    const signed = manifestSignature(root, manifestBytes, repository, { ref, trigger: BUNDLE_TRIGGER });
    if (!signed.ok) return done([...problems, `${MANIFEST} is not signed by ${repository}'s offline-bundle workflow run on ${ref}: ${firstLine(signed.stderr)}`]);
    log(`signature  ${MANIFEST} signed by ${repository}, workflow ${BUNDLE_WORKFLOW}, run on ${ref}`);
  }
  let manifest;
  try {
    manifest = JSON.parse(manifestBytes.toString('utf8'));
  } catch {
    return done([...problems, `${MANIFEST} is not JSON`]);
  }
  const invalid = manifestProblems(manifest, dryRun);
  if (invalid.length) return done([...problems, ...invalid]);
  if (!dryRun) {
    if (manifest.version !== version) return done([...problems, `${MANIFEST} is the bundle of ${manifest.version}, not ${version}`]);
    if (manifest.source.repository !== repository) return done([...problems, `${MANIFEST} names ${manifest.source.repository}, not ${repository}`]);
    const atCommit = manifestSignature(root, manifestBytes, repository, { ref, trigger: BUNDLE_TRIGGER, sha: manifest.commit });
    if (!atCommit.ok) return done([...problems, `${MANIFEST} names commit ${manifest.commit}, but its signing run was at another commit: ${firstLine(atCommit.stderr)}`]);
    log(`commit     the signing run was at ${manifest.commit}, the commit the manifest names`);
  }

  const listed = new Map(manifest.files.map((f) => [f.path, f]));
  const present = new Set(files.filter((f) => f !== MANIFEST && f !== SIGNATURE));
  for (const file of present) if (!listed.has(file)) problems.push(`${JSON.stringify(file)} is not in the manifest`);
  const hashes = new Map();
  for (const [file, entry] of listed) {
    if (!present.has(file)) {
      problems.push(`${file} is missing`);
      continue;
    }
    let actual;
    try {
      actual = hashFile(join(root, file));
    } catch (error) {
      problems.push(`${file}: ${error.message}`);
      continue;
    }
    hashes.set(file, actual.sha256);
    if (actual.size !== entry.size || actual.sha256 !== entry.sha256) {
      problems.push(`${file} does not match the manifest (SHA-256 ${actual.sha256}, ${actual.size} bytes; expected ${entry.sha256}, ${entry.size} bytes)`);
    }
  }
  log(`files      ${hashes.size} of ${listed.size} files hashed`);
  const textOf = (file) => (hashes.has(file) ? readCapped(join(root, file), LIMITS.manifest).toString('utf8') : null);
  if (hashes.has(SUMS) && textOf(SUMS) !== sumsText(manifest.files)) problems.push(`${SUMS} does not list the manifest's files and hashes`);
  const digests = Object.fromEntries(manifest.images.filter((i) => i.signed && i.digest).map((i) => [i.name, i.digest]));
  if (hashes.has(IMAGES_VALUES) && textOf(IMAGES_VALUES) !== imagesValues({ version: manifest.version, build: manifest.build, digests })) {
    problems.push(`${IMAGES_VALUES} does not pin the manifest's version, build and image digests`);
  }
  const database = manifest.images.find((i) => i.name === DATABASE_IMAGE);
  if (hashes.has(DATABASE_IMAGE_VALUES) && textOf(DATABASE_IMAGE_VALUES) !== databaseImageValues(database)) {
    problems.push(`${DATABASE_IMAGE_VALUES} does not pin the manifest's TimescaleDB tag and digest`);
  }
  for (const [file, sha256] of hashes) {
    const blob = /^images\/[^/]+\/blobs\/sha256\/([0-9a-f]{64})$/.exec(file);
    if (blob && blob[1] !== sha256) problems.push(`${file} does not hash to its name (${sha256})`);
  }
  const anchors = { manifestSha256: sha256Of(manifestBytes), sumsSha256: hashes.get(SUMS) ?? null };
  if (dryRun) {
    for (const file of present) if (file.startsWith('images/') || file.startsWith('sboms/')) problems.push(`${file}: a dry run carries no images or SBOMs`);
    return done(problems, manifest, anchors);
  }
  for (const image of manifest.images) problems.push(...layoutProblems(root, image, hashes));
  if (problems.length > 0) {
    log('registry   not asked: the bundle already fails');
    return done(problems, manifest, anchors);
  }
  for (const image of manifest.images.filter((i) => i.signed)) {
    const found = registryProblems(root, image, repository, manifest.commit);
    problems.push(...found);
    if (found.length === 0) log(`image      ${image.name} ${image.digest} signed at the bundle's commit and SBOM attested`);
  }
  return done(problems, manifest, anchors);
}

export function parseArgs(argv) {
  const options = { dryRun: false, fromMain: false, repository: DEFAULT_REPOSITORY, version: null, folder: null };
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    if (arg === '--dry-run') options.dryRun = true;
    else if (arg === '--from-main') options.fromMain = true;
    else if (arg === '--repository' || arg === '--version') options[arg.slice(2)] = argv[++i];
    else if (arg.startsWith('--') || options.folder !== null) throw new UsageError(`unexpected ${JSON.stringify(arg)}`);
    else options.folder = arg;
  }
  if (!options.folder) throw new UsageError('name the bundle folder');
  if (typeof options.repository !== 'string' || !PATTERNS.repository.test(options.repository)) throw new UsageError('--repository must be owner/name');
  if (options.dryRun && (options.version !== null || options.fromMain)) throw new UsageError('a dry run takes no --version or --from-main');
  if (!options.dryRun && (typeof options.version !== 'string' || !PATTERNS.version.test(options.version))) {
    throw new UsageError('--version must name the release you expect, such as 1.2.0 (with --from-main for a bundle built from main)');
  }
  return options;
}

export function main(argv) {
  if (argv[0] === '--self-test') return selfTest();
  const options = parseArgs(argv);
  const result = verifyBundle(options.folder, { ...options, log: (line) => console.log(line) });
  if (!result.ok) {
    for (const problem of result.problems) console.error(`FAIL ${problem}`);
    console.error(`${result.problems.length} problem(s): do not carry this bundle into the site.`);
    return 1;
  }
  const m = result.manifest;
  if (options.dryRun) {
    console.log(`PASS dry run ${m.version} (${m.build}, ${m.commit.slice(0, 12)}): ${m.files.length} files match the manifest. An unsigned CI check, not a release: never install it.`);
  } else {
    console.log(`PASS bundle ${m.version} (${m.build}, commit ${m.commit}, ${options.fromMain ? 'built from main' : `tag v${m.version}`}): signed manifest, ${m.files.length} files, ${m.images.length} images.`);
    if (options.fromMain) console.log('NOTE built from main, not from a release tag.');
    console.log(`Record these for the check inside the site (wiki 04 section 12.4):\n  manifest.json SHA-256  ${result.manifestSha256}\n  SHA256SUMS SHA-256     ${result.sumsSha256}`);
  }
  return 0;
}

// ---------------------------------------------------------------------------------------------
// self-test (node scripts/verify.mjs security and CI): a signed bundle passes; every kind of tampering, a wrong release, a
// wrong commit and a verifier started inside the bundle fail with a finding that names it; cosign is a stand-in that plays
// the registry, the transparency log and the certificates (scripts/offline/stand-ins.mjs).

function selfTest() {
  const standIns = path.join(path.dirname(fileURLToPath(import.meta.url)), 'stand-ins.mjs');
  return import(pathToFileURL(standIns).href).then((fixtures) => runSelfTest(fixtures, standIns));
}

export const SELF_TEST = { version: '1.2.0', build: 'v1.2.0-57', commit: 'a'.repeat(40), registry: 'ghcr.io/alsadderwork-coder/ariva' };

/**
 * A synthetic bundle as bundle.mjs writes one, signed by the stand-in. ref and commit are the signing run's; images are
 * signed by the images workflow at imagesCommit.
 */
export function selfTestBundle(fixtures, root, state, options = {}) {
  const { dryRun = false, repository = DEFAULT_REPOSITORY, version = SELF_TEST.version, build = SELF_TEST.build, commit = SELF_TEST.commit } = options;
  const ref = options.ref ?? `refs/tags/v${version}`;
  const imagesCommit = options.imagesCommit ?? commit;
  const registry = SELF_TEST.registry;
  const write = (relative, content) => {
    fs.mkdirSync(path.dirname(join(root, relative)), { recursive: true });
    fs.writeFileSync(join(root, relative), content);
  };
  write('helm/charts/ariva-platform.tgz', crypto.createHash('sha512').update('platform chart').digest());
  write('helm/charts/ariva-timescaledb.tgz', crypto.createHash('sha512').update('database chart').digest());
  write('web/index.html', '<!doctype html><title>Ariva</title>\n');
  write('web/_app/immutable/app.js', 'export {};\n');
  const timescale = { name: DATABASE_IMAGE, source: 'docker.io/timescale/timescaledb-ha', tag: 'pg17-ts2.30', mirrorPath: 'timescale/timescaledb-ha', signed: false };
  const images = [...ARIVA_IMAGES.map((name) => ({ name, source: `${registry}/${name}`, tag: build, mirrorPath: `ariva/${name}`, signed: true })), timescale]
    .map((image) => {
      const synthetic = fixtures.syntheticImage(image.name, image.signed);
      if (dryRun) return { ...image, digest: image.signed ? null : synthetic.digest, layout: null, sbom: null };
      fixtures.writeLayout(join(root, `images/${image.name}`), synthetic, image.tag);
      fs.rmdirSync(join(root, `images/${image.name}/ingest`));
      const sbom = image.signed ? `sboms/${image.name}.cdx.json` : null;
      if (sbom) {
        state.sboms[image.source] = fixtures.syntheticSbom(image.name);
        write(sbom, JSON.stringify(state.sboms[image.source]));
      }
      return { ...image, digest: synthetic.digest, layout: `images/${image.name}`, sbom };
    });
  const digests = Object.fromEntries(images.filter((i) => i.signed && i.digest).map((i) => [i.name, i.digest]));
  write(IMAGES_VALUES, imagesValues({ version, build, digests }));
  write(DATABASE_IMAGE_VALUES, databaseImageValues(images.find((i) => i.name === DATABASE_IMAGE)));
  state.images = images.map(({ name, source, tag, digest, signed }) => ({
    name, source, tag, digest, signed, certificate: { repository, ref: `refs/tags/v${version}`, sha: imagesCommit, trigger: 'push' }
  }));
  const web = images.find((i) => i.name === 'web');
  writeManifest(root, {
    version, build, commit, createdUtc: '2026-10-16T12:00:00.000Z', dryRun, source: { repository, registry },
    charts: [{ name: 'ariva-platform', version: '0.1.0', path: 'helm/charts/ariva-platform.tgz' }, { name: 'ariva-timescaledb', version: '0.2.0', path: 'helm/charts/ariva-timescaledb.tgz' }],
    images, web: dryRun ? { from: 'build', digest: null } : { from: 'image', digest: web.digest }
  });
  if (!dryRun) selfTestSign(root, state, { repository, ref, sha: commit, trigger: BUNDLE_TRIGGER });
}

/** The stand-in's signing: the manifest's SHA-256 goes into the log, and the "Sigstore bundle" carries its certificate. */
export function selfTestSign(root, state, certificate) {
  const sha256 = sha256Of(fs.readFileSync(path.join(root, MANIFEST)));
  state.log.push(sha256);
  fs.writeFileSync(path.join(root, SIGNATURE), JSON.stringify({ sha256, certificate }));
  fs.writeFileSync(state.file, JSON.stringify(state));
}

export function selfTestState(folder) {
  const state = { file: path.join(folder, 'stand-in-state.json'), log: [], images: [], sboms: {} };
  fs.writeFileSync(state.file, JSON.stringify(state));
  return state;
}

function runSelfTest(fixtures, standIns) {
  let checks = 0;
  const ok = (value, message) => {
    assert.ok(value, message);
    checks++;
  };
  const work = fs.mkdtempSync(path.join(os.tmpdir(), 'ariva-offline-selftest-'));
  const saved = { cosign: TOOLS.cosign, state: process.env.ARIVA_OFFLINE_STAND_IN, cwd: process.cwd() };
  TOOLS.cosign = [process.execPath, standIns, 'cosign'];
  try {
    // trust: the identities a deployment pins (wiki 04 section 6.5); the repository is checked, its dots escaped
    assert.equal(signer('alsadderwork-coder/Ariva', 'images.yml'), '^https://github\\.com/alsadderwork-coder/Ariva/\\.github/workflows/images\\.yml@refs/(tags/v.+|heads/main)$');
    assert.equal(signer('o/a.b', BUNDLE_WORKFLOW), '^https://github\\.com/o/a\\.b/\\.github/workflows/offline-bundle\\.yml@refs/(tags/v.+|heads/main)$');
    for (const bad of ['o', 'o/a b', 'o/a;id', '../a', 'o/a/b', '-o/a']) assert.throws(() => signer(bad, IMAGES_WORKFLOW), UsageError, bad);
    assert.deepEqual(trustArgs('o/r', BUNDLE_WORKFLOW, { ref: 'refs/tags/v1.2.0', sha: 'b'.repeat(40), trigger: BUNDLE_TRIGGER }), [
      '--certificate-identity', 'https://github.com/o/r/.github/workflows/offline-bundle.yml@refs/tags/v1.2.0', '--certificate-oidc-issuer', ISSUER,
      '--certificate-github-workflow-repository', 'o/r', '--certificate-github-workflow-sha', 'b'.repeat(40), '--certificate-github-workflow-trigger', BUNDLE_TRIGGER]);
    for (const bad of ['refs/tags/v1.2', 'refs/heads/feature', 'refs/tags/v1.2.0/../x', 'refs/pull/1/merge']) assert.throws(() => identityOf('o/r', BUNDLE_WORKFLOW, bad), UsageError, bad);
    assert.throws(() => trustArgs('o/r', IMAGES_WORKFLOW, { sha: 'HEAD' }), UsageError);
    checks += 6;
    // paths: plain relative names only (CWE-22)
    for (const good of ['manifest.json', 'web/_app/immutable/a-B_1.js', 'web/_app/immutable/chunks/-CWa0sji.js', 'images/api-main/blobs/sha256/' + 'a'.repeat(64)]) ok(pathAllowed(good), good);
    for (const bad of ['../x', 'a/../b', '/etc/passwd', 'C:\\x', 'a\\b', '.hidden', 'a/.git/config', 'a//b', 'a/', '', 'a/./b', 'C:/x']) ok(!pathAllowed(bad), `refuses ${JSON.stringify(bad)}`);
    // arguments: a release names the version the operator expects
    assert.deepEqual(parseArgs(['b', '--version', '1.2.0', '--repository', 'o/r']), { dryRun: false, fromMain: false, repository: 'o/r', version: '1.2.0', folder: 'b' });
    assert.deepEqual(parseArgs(['--dry-run', 'b']), { dryRun: true, fromMain: false, repository: DEFAULT_REPOSITORY, version: null, folder: 'b' });
    for (const bad of [[], ['b'], ['b', 'c', '--version', '1.2.0'], ['b', '--version', '1.2'], ['b', '--version', '1.2.0', '--repository', 'o r'], ['--dry-run', 'b', '--version', '1.2.0'], ['--nope', 'b']]) {
      assert.throws(() => parseArgs(bad), UsageError, bad.join(' '));
    }
    checks += 3;

    let n = 0;
    const fresh = (options = {}) => {
      const folder = path.join(work, `case-${++n}`);
      fs.mkdirSync(folder);
      const state = selfTestState(folder);
      process.env.ARIVA_OFFLINE_STAND_IN = state.file;
      const root = path.join(folder, 'bundle');
      fs.mkdirSync(root);
      selfTestBundle(fixtures, root, state, options);
      return { root, state };
    };
    const release = { version: SELF_TEST.version };
    const verify = (root, options = release) => verifyBundle(root, options);
    const failsWith = (result, pattern, label) => {
      ok(!result.ok, `${label}: refused`);
      ok(result.problems.some((p) => pattern.test(p)), `${label}: a finding matches ${pattern} (${result.problems.join(' | ')})`);
    };
    // A manifest a buggy release could still sign: changed, then signed again by a run on the same tag and commit.
    const rewrite = (root, state, change) => {
      const manifest = JSON.parse(fs.readFileSync(path.join(root, MANIFEST), 'utf8'));
      change(manifest);
      fs.writeFileSync(path.join(root, MANIFEST), `${JSON.stringify(manifest, null, 2)}\n`);
      selfTestSign(root, state, { repository: DEFAULT_REPOSITORY, ref: `refs/tags/v${SELF_TEST.version}`, sha: SELF_TEST.commit, trigger: BUNDLE_TRIGGER });
    };
    // Lists one changed file's new hash, and SHA256SUMS with it, so that only the check under test can fail.
    const relist = (root, manifest, file) => {
      Object.assign(manifest.files.find((f) => f.path === file), hashFile(join(root, file)));
      fs.writeFileSync(path.join(root, SUMS), sumsText(manifest.files));
      Object.assign(manifest.files.find((f) => f.path === SUMS), hashFile(path.join(root, SUMS)));
    };
    const flipByte = (file) => {
      const bytes = fs.readFileSync(file);
      bytes[bytes.length >> 1] ^= 0xff;
      fs.writeFileSync(file, bytes);
    };

    // a signed bundle passes, every image checked in the registry; the anchors for the check inside the site are returned;
    // every program started in the one private empty folder, never in the current folder (CWE-427)
    {
      const { root } = fresh();
      const lines = [];
      const started = path.join(work, 'started-in.txt');
      process.env.ARIVA_OFFLINE_STAND_IN_CWD = started;
      let result;
      try {
        result = verify(root, { ...release, log: (line) => lines.push(line) });
      } finally {
        delete process.env.ARIVA_OFFLINE_STAND_IN_CWD;
      }
      const folders = [...new Set(fs.readFileSync(started, 'utf8').split('\n').filter(Boolean))];
      ok(folders.length === 1 && path.basename(folders[0]).startsWith('ariva-offline-run-') && canonical(path.dirname(folders[0])) === canonical(os.tmpdir()),
        `every program started in one private folder under the temporary folder (${folders.join(', ')})`);
      ok(canonical(folders[0]) !== canonical(process.cwd()) && fs.readdirSync(folders[0]).length === 0, 'that folder is not the current folder and holds nothing');
      ok(result.ok, `a signed bundle passes (${result.problems.join(' | ')})`);
      ok(lines.some((l) => l.startsWith('signature')) && lines.some((l) => l.startsWith('commit')) && lines.filter((l) => l.startsWith('image')).length === ARIVA_IMAGES.length,
        'the signature, the commit and every Ariva image are reported');
      ok(result.manifestSha256 === sha256Of(fs.readFileSync(path.join(root, MANIFEST))) && result.sumsSha256 === sha256Of(fs.readFileSync(path.join(root, SUMS))),
        'the SHA-256 of manifest.json and SHA256SUMS are returned');
      ok(fs.readFileSync(path.join(root, SUMS), 'utf8') === sumsText(result.manifest.files), 'SHA256SUMS lists every file');
    }
    // the release: another version, a bundle built from main, a manifest naming another commit
    {
      const { root } = fresh();
      failsWith(verify(root, { version: '1.2.1' }), /is not signed by alsadderwork-coder\/Ariva's offline-bundle workflow run on refs\/tags\/v1\.2\.1/, 'another version');
      failsWith(verify(root, { ...release, fromMain: true }), /run on refs\/heads\/main/, 'a tag bundle verified as one from main');
    }
    {
      const { root } = fresh({ ref: 'refs/heads/main' });
      failsWith(verify(root), /run on refs\/tags\/v1\.2\.0/, 'a bundle from main verified as the tag\'s');
      ok(verify(root, { ...release, fromMain: true }).ok, 'a bundle from main passes with --from-main');
      // from main the tag pins no version: the manifest's own must be the one expected
      failsWith(verify(root, { version: '1.3.0', fromMain: true }), /manifest\.json is the bundle of 1\.2\.0, not 1\.3\.0/, 'a main bundle of another version');
    }
    {
      const { root, state } = fresh();
      const manifest = JSON.parse(fs.readFileSync(path.join(root, MANIFEST), 'utf8'));
      manifest.commit = 'b'.repeat(40);
      fs.writeFileSync(path.join(root, MANIFEST), `${JSON.stringify(manifest, null, 2)}\n`);
      selfTestSign(root, state, { repository: DEFAULT_REPOSITORY, ref: 'refs/tags/v1.2.0', sha: SELF_TEST.commit, trigger: BUNDLE_TRIGGER });
      failsWith(verify(root), /names commit b{40}, but its signing run was at another commit/, 'a manifest naming another commit');
    }
    {
      const { root } = fresh({ imagesCommit: 'e'.repeat(40) });
      failsWith(verify(root), /ariva\/api-main@sha256:[0-9a-f]{64} is not signed by alsadderwork-coder\/Ariva's images workflow at commit aaaaaaaaaaaa/, 'images built at another commit');
    }
    // a tampered image archive fails on its hash and its content address
    {
      const { root, state } = fresh();
      const layer = fixtures.syntheticImage('api-main', true).blobs[1].digest.slice(7);
      flipByte(join(root, `images/api-main/blobs/sha256/${layer}`));
      const result = verify(root);
      failsWith(result, new RegExp(`images/api-main/blobs/sha256/${layer} does not match the manifest`), 'tampered layer');
      failsWith(result, /does not hash to its name/, 'tampered layer');
      ok(!result.problems.some((p) => /images workflow at commit/.test(p)), 'tampered layer: the registry is not asked once the bundle fails');
      ok(state.log.length === 1, 'the stand-in log holds the one signature');
    }
    // a tampered chart archive, an added file and a removed file fail
    {
      const { root } = fresh();
      flipByte(join(root, 'helm/charts/ariva-platform.tgz'));
      failsWith(verify(root), /helm\/charts\/ariva-platform\.tgz does not match the manifest/, 'tampered chart');
    }
    {
      const { root } = fresh();
      fs.writeFileSync(join(root, 'web/_app/immutable/extra.js'), 'alert(1)\n');
      failsWith(verify(root), /"web\/_app\/immutable\/extra\.js" is not in the manifest/, 'added file');
    }
    {
      const { root } = fresh();
      fs.rmSync(join(root, 'web/index.html'));
      failsWith(verify(root), /web\/index\.html is missing/, 'removed file');
    }
    // the manifest: edited to cover a tampered file, unsigned, or signed for another repository
    {
      const { root } = fresh();
      flipByte(join(root, 'helm/charts/ariva-platform.tgz'));
      const manifest = JSON.parse(fs.readFileSync(path.join(root, MANIFEST), 'utf8'));
      const entry = manifest.files.find((f) => f.path === 'helm/charts/ariva-platform.tgz');
      entry.sha256 = hashFile(join(root, entry.path)).sha256;
      fs.writeFileSync(path.join(root, MANIFEST), `${JSON.stringify(manifest, null, 2)}\n`);
      const result = verify(root);
      failsWith(result, /manifest\.json is not signed by alsadderwork-coder\/Ariva's offline-bundle workflow/, 'edited manifest');
      ok(result.problems.length === 1, 'edited manifest: nothing in it is used once its signature fails');
    }
    {
      const { root } = fresh();
      fs.rmSync(path.join(root, SIGNATURE));
      failsWith(verify(root), /manifest\.json\.sigstore\.json is missing/, 'unsigned bundle');
    }
    {
      const { root } = fresh();
      failsWith(verify(root, { ...release, repository: 'someone-else/Ariva' }), /is not signed by someone-else\/Ariva's offline-bundle workflow/, 'another repository');
    }
    // the registry: an image no longer signed there, signatures naming another digest, an SBOM that is not the attested one
    {
      const { root, state } = fresh();
      state.images.find((i) => i.name === 'web').signed = false;
      fs.writeFileSync(state.file, JSON.stringify(state));
      failsWith(verify(root), /ghcr\.io\/alsadderwork-coder\/ariva\/web@sha256:[0-9a-f]{64} is not signed by/, 'unsigned image');
    }
    {
      const { root, state } = fresh();
      state.images.find((i) => i.name === 'api-main').reportDigest = fixtures.syntheticImage('web', true).digest;
      fs.writeFileSync(state.file, JSON.stringify(state));
      failsWith(verify(root), /ariva\/api-main@sha256:[0-9a-f]{64}: the signatures name another digest/, 'signatures naming another digest');
    }
    {
      const { root, state } = fresh();
      fs.writeFileSync(join(root, 'sboms/api-cronz.cdx.json'), JSON.stringify({ bomFormat: 'CycloneDX', components: [{ name: 'planted' }] }));
      rewrite(root, state, (m) => relist(root, m, 'sboms/api-cronz.cdx.json'));
      failsWith(verify(root), /sboms\/api-cronz\.cdx\.json is not the SBOM attested/, 'planted SBOM');
    }
    // the values the pods pull must be the manifest's, even in a signed manifest
    {
      const { root, state } = fresh();
      fs.writeFileSync(join(root, IMAGES_VALUES), fs.readFileSync(join(root, IMAGES_VALUES), 'utf8').replace(/api-main: "sha256:[0-9a-f]{64}"/, `api-main: "sha256:${'f'.repeat(64)}"`));
      rewrite(root, state, (m) => relist(root, m, IMAGES_VALUES));
      failsWith(verify(root), /helm\/values\/images\.yaml does not pin the manifest's version, build and image digests/, 'images.yaml with another digest');
    }
    {
      const { root, state } = fresh();
      fs.writeFileSync(join(root, DATABASE_IMAGE_VALUES), fs.readFileSync(join(root, DATABASE_IMAGE_VALUES), 'utf8').replace('pg17-ts2.30', 'pg17-other'));
      rewrite(root, state, (m) => relist(root, m, DATABASE_IMAGE_VALUES));
      failsWith(verify(root), /timescaledb-image\.yaml does not pin the manifest's TimescaleDB tag and digest/, 'timescaledb-image.yaml with another tag');
    }
    // layouts a buggy release could write, even signed: a missing attestation, another digest or a second tag, a lost layer
    {
      const { root, state } = fresh();
      const index = JSON.parse(fs.readFileSync(join(root, 'images/api-ingest/index.json'), 'utf8'));
      const attestation = fixtures.syntheticImage('api-ingest', true).referrers.find((r) => r.predicateType === 'https://cyclonedx.org/bom');
      index.manifests = index.manifests.filter((d) => d.digest !== attestation.digest);
      fs.writeFileSync(join(root, 'images/api-ingest/index.json'), JSON.stringify(index));
      rewrite(root, state, (m) => relist(root, m, 'images/api-ingest/index.json'));
      failsWith(verify(root), /images\/api-ingest carries no SBOM attestation/, 'missing attestation');
    }
    {
      const { root, state } = fresh();
      const index = JSON.parse(fs.readFileSync(join(root, 'images/simulation/index.json'), 'utf8'));
      index.manifests[0].digest = fixtures.syntheticImage('web', true).digest;
      fs.writeFileSync(join(root, 'images/simulation/index.json'), JSON.stringify(index));
      rewrite(root, state, (m) => relist(root, m, 'images/simulation/index.json'));
      failsWith(verify(root), /images\/simulation\/index\.json does not name sha256:[0-9a-f]{64} as v1\.2\.0-57, once/, 'another digest under the tag');
    }
    {
      const { root, state } = fresh();
      const index = JSON.parse(fs.readFileSync(join(root, 'images/api-cronz/index.json'), 'utf8'));
      index.manifests.push({ ...index.manifests[0], digest: fixtures.syntheticImage('web', true).digest });
      fs.writeFileSync(join(root, 'images/api-cronz/index.json'), JSON.stringify(index));
      rewrite(root, state, (m) => relist(root, m, 'images/api-cronz/index.json'));
      failsWith(verify(root), /images\/api-cronz\/index\.json does not name sha256:[0-9a-f]{64} as v1\.2\.0-57, once/, 'a second manifest under the tag');
    }
    {
      const { root, state } = fresh();
      const layer = fixtures.syntheticImage('api-stream', true).blobs[1].digest.slice(7);
      fs.rmSync(join(root, `images/api-stream/blobs/sha256/${layer}`));
      rewrite(root, state, (m) => {
        m.files = m.files.filter((f) => f.path !== `images/api-stream/blobs/sha256/${layer}`);
        relist(root, m, 'images/api-stream/index.json');
      });
      failsWith(verify(root), new RegExp(`images/api-stream/blobs/sha256/${layer} is missing \\(api-stream needs it\\)`), 'layer left out');
    }
    // a signed manifest that names a path outside the folder is refused before any file is opened (CWE-22)
    {
      const { root, state } = fresh();
      rewrite(root, state, (m) => m.files.push({ path: '../outside.txt', size: 1, sha256: 'b'.repeat(64) }));
      failsWith(verify(root), /the file entry "\.\.\/outside\.txt" is not a plain relative path/, 'path outside the folder');
    }
    {
      const { root, state } = fresh();
      rewrite(root, state, (m) => {
        m.files = m.files.filter((f) => f.path !== 'web/index.html');
      });
      failsWith(verify(root), /SHA256SUMS does not list the manifest's files/, 'SHA256SUMS out of step');
    }
    // a pipe put in a file's place after the walk is refused at once, never waited on
    if (os.platform() !== 'win32' && spawnSync('mkfifo', [path.join(work, 'pipe')]).status === 0) {
      assert.throws(() => hashFile(path.join(work, 'pipe')), (error) => error instanceof Refused && /not a regular file/.test(error.message));
      assert.throws(() => readCapped(path.join(work, 'pipe'), 10), (error) => error instanceof Refused && /not a regular file/.test(error.message));
      checks += 2;
    } else {
      console.warn('offline verify self-test: no mkfifo on this machine; the pipe case is SKIPPED');
    }
    // links are refused wherever they are; the verifier never runs from inside the bundle (CWE-427)
    {
      const { root } = fresh();
      let linked = true;
      try {
        fs.symlinkSync(os.platform() === 'win32' ? 'C:\\Windows\\win.ini' : '/etc/hostname', join(root, 'web/linked.txt'));
      } catch {
        linked = false;
        console.warn('offline verify self-test: this machine cannot create a symbolic link; the link case is SKIPPED');
      }
      if (linked) failsWith(verify(root), /"web\/linked\.txt" is not a regular file or folder/, 'symbolic link');
    }
    {
      const { root } = fresh();
      fs.writeFileSync(path.join(root, os.platform() === 'win32' ? 'cosign.exe' : 'cosign'), 'planted');
      process.chdir(join(root, 'web'));
      try {
        failsWith(verify(root), /run the verifier from outside the bundle folder/, 'started inside the bundle');
      } finally {
        process.chdir(saved.cwd);
      }
    }
    // dry runs: verified with --dry-run only, never signed, no images; a signed dry run is no release either
    {
      const { root } = fresh({ dryRun: true });
      const result = verify(root, { dryRun: true });
      ok(result.ok, `a dry run passes with --dry-run (${result.problems.join(' | ')})`);
      ok(fs.readFileSync(join(root, IMAGES_VALUES), 'utf8').includes('imageDigests: {}'), 'a dry run pins no digest');
      failsWith(verify(root), /manifest\.json\.sigstore\.json is missing/, 'dry run without --dry-run');
      fs.writeFileSync(path.join(root, SIGNATURE), '{}');
      failsWith(verify(root, { dryRun: true }), /a dry run is never signed/, 'signed dry run');
    }
    {
      const { root, state } = fresh({ dryRun: true });
      selfTestSign(root, state, { repository: DEFAULT_REPOSITORY, ref: 'refs/tags/v1.2.0', sha: SELF_TEST.commit, trigger: BUNDLE_TRIGGER });
      failsWith(verify(root), /a dry run bundle \(no images, not signed\) cannot be installed/, 'a signed dry run verified as a release');
    }
    {
      const { root } = fresh();
      failsWith(verify(root, { dryRun: true }), /not a dry run bundle/, 'release bundle verified as a dry run');
    }
    {
      const { root } = fresh({ dryRun: true });
      flipByte(join(root, 'helm/charts/ariva-timescaledb.tgz'));
      failsWith(verify(root, { dryRun: true }), /helm\/charts\/ariva-timescaledb\.tgz does not match the manifest/, 'tampered dry run');
    }
  } finally {
    process.chdir(saved.cwd);
    TOOLS.cosign = saved.cosign;
    if (saved.state === undefined) delete process.env.ARIVA_OFFLINE_STAND_IN;
    else process.env.ARIVA_OFFLINE_STAND_IN = saved.state;
    fs.rmSync(work, { recursive: true, force: true });
  }
  console.log(`offline verify self-test: ${checks} checks passed`);
  return 0;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const code = main(process.argv.slice(2));
    if (code instanceof Promise) code.then((c) => { process.exitCode = c; }, (error) => { console.error(error.stack ?? error.message); process.exitCode = 1; });
    else process.exitCode = code;
  } catch (error) {
    if (error instanceof UsageError) {
      console.error(`verify: ${error.message}\nusage: node scripts/offline/verify.mjs <bundle folder> --version <1.2.0> [--from-main] [--repository <owner/name>] | --dry-run <bundle folder> | --self-test`);
      process.exitCode = 2;
    } else {
      console.error(error instanceof Refused ? `FAIL ${error.message}` : error.stack ?? error.message);
      process.exitCode = 1;
    }
  }
}
