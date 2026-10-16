#!/usr/bin/env node
// Stand-ins for cosign, ORAS and docker, started by the self-tests of scripts/offline/verify.mjs and bundle.mjs (ARV-127) in place of
// the real programs, and the synthetic OCI images both self-tests use. Never used by a release or a verification.
//
//   node scripts/offline/stand-ins.mjs cosign <cosign arguments>     ARIVA_OFFLINE_STAND_IN names the state file
//   node scripts/offline/stand-ins.mjs oras <oras arguments>
//   node scripts/offline/stand-ins.mjs docker <docker arguments>        create, cp and rm of the web image only
//
// The state file plays the registry and the transparency log: which image tags and digests are signed and by which
// certificate (repository, ref, commit, trigger), which SBOM each image's attestation carries, and which manifest bytes
// were signed. Each signature's certificate is checked against the identity, issuer, repository, commit and trigger the
// call asks for, as cosign does, so the self-tests prove the scripts ask for the right signer.

import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const OCI_INDEX = 'application/vnd.oci.image.index.v1+json';
export const OCI_MANIFEST = 'application/vnd.oci.image.manifest.v1+json';
export const SIGSTORE_BUNDLE = 'application/vnd.dev.sigstore.bundle.v0.3+json';
export const SIGNATURE_PREDICATE = 'https://sigstore.dev/cosign/sign/v1';
export const SBOM_PREDICATE = 'https://cyclonedx.org/bom';

const sha256 = (bytes) => crypto.createHash('sha256').update(bytes).digest('hex');

function blob(content) {
  const bytes = Buffer.isBuffer(content) ? content : Buffer.from(JSON.stringify(content));
  return { bytes, digest: `sha256:${sha256(bytes)}`, size: bytes.length };
}

/**
 * A small image (config and one layer) whose bytes depend only on its name, so the stand-in and the self-test agree on its
 * digest; a signed one also carries a cosign 3 signature and a CycloneDX attestation as OCI referrers, shaped as cosign 3
 * writes them (checked against a local registry on 2026-10-16).
 */
export function syntheticImage(name, signed) {
  const config = blob({ architecture: 'amd64', os: 'linux', config: { Labels: { 'org.opencontainers.image.title': name } } });
  const layer = blob(crypto.createHash('sha512').update(`layer of ${name}`).digest());
  const manifest = blob({
    schemaVersion: 2,
    mediaType: OCI_MANIFEST,
    config: { mediaType: 'application/vnd.oci.image.config.v1+json', digest: config.digest, size: config.size },
    layers: [{ mediaType: 'application/vnd.oci.image.layer.v1.tar+gzip', digest: layer.digest, size: layer.size }]
  });
  const blobs = [config, layer, manifest];
  const referrers = [];
  if (signed) {
    for (const predicateType of [SIGNATURE_PREDICATE, SBOM_PREDICATE]) {
      const empty = blob({});
      const bundle = blob({ mediaType: SIGSTORE_BUNDLE, predicateType, subject: manifest.digest });
      const referrer = blob({
        schemaVersion: 2,
        mediaType: OCI_MANIFEST,
        artifactType: SIGSTORE_BUNDLE,
        config: { mediaType: 'application/vnd.oci.empty.v1+json', digest: empty.digest, size: empty.size },
        layers: [{ mediaType: SIGSTORE_BUNDLE, digest: bundle.digest, size: bundle.size }],
        subject: { mediaType: OCI_MANIFEST, digest: manifest.digest, size: manifest.size },
        annotations: { 'dev.sigstore.bundle.content': 'dsse-envelope', 'dev.sigstore.bundle.predicateType': predicateType }
      });
      blobs.push(empty, bundle, referrer);
      referrers.push({ ...referrer, predicateType });
    }
  }
  return { name, digest: manifest.digest, size: manifest.size, blobs, referrers };
}

/** Writes an image as `oras copy -r --to-oci-layout <folder>:<tag>` does: the tagged image, its referrers, every blob. */
export function writeLayout(folder, image, tag, { referrers = image.referrers } = {}) {
  fs.mkdirSync(path.join(folder, 'blobs', 'sha256'), { recursive: true });
  fs.mkdirSync(path.join(folder, 'ingest'), { recursive: true });
  fs.writeFileSync(path.join(folder, 'oci-layout'), JSON.stringify({ imageLayoutVersion: '1.0.0' }));
  const kept = new Set(referrers.map((r) => r.digest));
  for (const b of image.blobs) {
    const referrer = image.referrers.find((r) => r.digest === b.digest);
    if (!referrer || kept.has(b.digest)) fs.writeFileSync(path.join(folder, 'blobs', 'sha256', b.digest.slice(7)), b.bytes);
  }
  const index = {
    schemaVersion: 2,
    mediaType: OCI_INDEX,
    manifests: [
      { mediaType: OCI_MANIFEST, digest: image.digest, size: image.size, annotations: { 'org.opencontainers.image.ref.name': tag } },
      ...referrers.map((r) => ({ mediaType: OCI_MANIFEST, digest: r.digest, size: r.size, artifactType: 'application/vnd.oci.empty.v1+json' }))
    ]
  };
  fs.writeFileSync(path.join(folder, 'index.json'), JSON.stringify(index));
}

/** A CycloneDX document standing in for an image's SBOM. */
export function syntheticSbom(name) {
  return { bomFormat: 'CycloneDX', specVersion: '1.6', metadata: { component: { type: 'container', name } }, components: [] };
}

function fail(message) {
  process.stderr.write(`${message}\n`);
  process.exit(1);
}

const ISSUER = 'https://token.actions.githubusercontent.com';

/** Whether the certificate passes the checks the call asks for, as cosign applies them. */
function certificateMatches(value, certificate, workflow) {
  const identity = `https://github.com/${certificate.repository}/.github/workflows/${workflow}@${certificate.ref}`;
  const exact = value('--certificate-identity');
  const pattern = value('--certificate-identity-regexp');
  if (exact === undefined && pattern === undefined) return false;
  if (exact !== undefined && exact !== identity) return false;
  if (pattern !== undefined && !new RegExp(pattern).test(identity)) return false;
  if (value('--certificate-oidc-issuer') !== ISSUER || value('--certificate-github-workflow-repository') !== certificate.repository) return false;
  const sha = value('--certificate-github-workflow-sha');
  const trigger = value('--certificate-github-workflow-trigger');
  return (sha === undefined || sha === certificate.sha) && (trigger === undefined || trigger === certificate.trigger);
}

function cosign(state, args) {
  const value = (flag) => {
    const at = args.indexOf(flag);
    return at >= 0 ? args[at + 1] : undefined;
  };
  const command = args[0];
  const target = args[args.length - 1];
  const noMatch = 'Error: no matching signatures: none of the expected identities matched what was in the certificate';
  if (command === 'verify-blob') {
    const bytes = fs.readFileSync(target);
    const signature = JSON.parse(fs.readFileSync(value('--bundle'), 'utf8'));
    if (signature.sha256 !== sha256(bytes) || !state.log.includes(signature.sha256)) fail('Error: invalid signature when validating ASN.1 encoded signature');
    if (!signature.certificate || !certificateMatches(value, signature.certificate, 'offline-bundle.yml')) fail(noMatch);
    process.stdout.write('Verified OK\n');
    return;
  }
  const image = state.images.find((i) => target === `${i.source}:${i.tag}` || target === `${i.source}@${i.digest}`);
  if (!image || !image.signed) fail(`Error: no signatures found for ${target}`);
  if (!certificateMatches(value, image.certificate, 'images.yml')) fail(noMatch);
  if (command === 'verify') {
    const digest = image.reportDigest ?? image.digest;
    process.stdout.write(`${JSON.stringify([{ critical: { identity: { 'docker-reference': image.source }, image: { 'docker-manifest-digest': digest }, type: SIGNATURE_PREDICATE }, optional: {} }])}\n`);
    return;
  }
  if (command === 'verify-attestation' && value('--type') === 'cyclonedx' && target.includes('@')) {
    const statement = {
      _type: 'https://in-toto.io/Statement/v0.1',
      subject: [{ name: image.source, digest: { sha256: image.digest.slice(7) } }],
      predicateType: SBOM_PREDICATE,
      predicate: state.sboms[image.source]
    };
    const envelope = { payloadType: 'application/vnd.in-toto+json', payload: Buffer.from(JSON.stringify(statement)).toString('base64'), signatures: [{ keyid: '', sig: 'stand-in' }] };
    process.stdout.write(`${JSON.stringify(envelope)}\n`);
    return;
  }
  fail(`stand-in cosign: unexpected call ${args.join(' ')}`);
}

function oras(state, args) {
  // oras copy --recursive <source>@<digest> --to-oci-layout <folder>:<tag>
  if (args.length !== 5 || args[0] !== 'copy' || args[1] !== '--recursive' || args[3] !== '--to-oci-layout') fail(`stand-in oras: unexpected call ${args.join(' ')}`);
  const image = state.images.find((i) => args[2] === `${i.source}@${i.digest}`);
  if (!image) fail(`Error: ${args[2]}: not found`);
  const target = args[4];
  const colon = target.lastIndexOf(':');
  const synthetic = syntheticImage(image.name, image.signed);
  if (synthetic.digest !== image.digest) fail('stand-in oras: the state names a digest the synthetic image does not have');
  writeLayout(target.slice(0, colon), synthetic, target.slice(colon + 1));
  process.stdout.write(`Copied [registry] ${args[2]} => [oci-layout] ${target}\nDigest: ${image.digest}\n`);
}

/** The files of the synthetic web image, under the folder its Dockerfile copies the web build to. */
export const WEB_IMAGE_FILES = {
  'index.html': '<!doctype html><title>Ariva</title>\n',
  '_app/immutable/app.js': 'export {};\n',
  '_app/immutable/chunks/-CWa0sji.js': 'export {};\n',
  'nginx/default.conf': 'server { listen 3000; }\n'
};

function docker(state, args) {
  // docker create <source>@<digest> | docker cp <id>:/usr/share/nginx/html/web <folder> | docker rm <id>
  const id = 'c0ffee'.padEnd(64, '0');
  if (args[0] === 'create' && args.length === 2) {
    const image = state.images.find((i) => args[1] === `${i.source}@${i.digest}` && i.name === 'web');
    if (!image) fail(`Unable to find image '${args[1]}'`);
    process.stdout.write(`${id}\n`);
    return;
  }
  if (args[0] === 'cp' && args.length === 3 && args[1] === `${id}:/usr/share/nginx/html/web`) {
    if (state.webRootLink) {
      // docker cp copies a link as a link: the web image's path pointing at a folder outside the image
      const outside = `${args[2]}-outside`;
      fs.mkdirSync(outside, { recursive: true });
      fs.writeFileSync(path.join(outside, 'index.html'), 'outside\n');
      fs.symlinkSync(outside, args[2]);
      return;
    }
    for (const [file, content] of Object.entries(WEB_IMAGE_FILES)) {
      fs.mkdirSync(path.dirname(path.join(args[2], file)), { recursive: true });
      fs.writeFileSync(path.join(args[2], file), content);
    }
    if (state.webLink) fs.symlinkSync('/etc/hostname', path.join(args[2], 'linked.txt'));
    return;
  }
  if (args[0] === 'rm' && args.length === 2 && args[1] === id) return;
  fail(`stand-in docker: unexpected call ${args.join(' ')}`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const state = JSON.parse(fs.readFileSync(process.env.ARIVA_OFFLINE_STAND_IN, 'utf8'));
  const [tool, ...args] = process.argv.slice(2);
  // The self-tests check where the scripts start their programs: a private empty folder, never the current one.
  if (process.env.ARIVA_OFFLINE_STAND_IN_CWD) fs.appendFileSync(process.env.ARIVA_OFFLINE_STAND_IN_CWD, `${process.cwd()}\n`);
  if (tool === 'cosign') cosign(state, args);
  else if (tool === 'oras') oras(state, args);
  else if (tool === 'docker') docker(state, args);
  else fail('stand-ins: cosign, oras or docker');
}
