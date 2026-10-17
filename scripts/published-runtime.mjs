// What a host image ships (ARV-184, CWE-1395). The hosts publish self-contained onto runtime-deps, which holds no .NET runtime:
// the runtime they run is the pair of runtime packs the SDK bundles into the publish output, at the SDK's own runtime version
// whatever the base image's tag says (SDK 10.0.103 bundled 10.0.3 onto runtime-deps 10.0.12). So this reads the publish output.
//   node scripts/published-runtime.mjs --check <publish folder> --runtime <x.y.z>
//       fails unless the app's deps.json names the Microsoft.NETCore.App and Microsoft.AspNetCore.App runtime packs at exactly
//       <x.y.z> (the Dockerfile's runtime-deps tag; scripts/base-images.mjs --check holds the two equal), <x.y.z> is at or above
//       FIXED_RUNTIME, and the file version of each assembly in FLOORS that ships is at or above its floor
//   node scripts/published-runtime.mjs --self-test
// Runs in every host image's build (the Dockerfile's "checked" stage, which the final stage copies from) and, self-tested, in
// the security gate of scripts/verify.mjs, CI and the Azure DevOps analysis. No dependencies, no child processes: the check
// only reads files (the self-test writes a temporary folder to prove the command line path).
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

/**
 * The advisories behind the floors (NuGet vulnerability data and nuget.org, 2026-10-15): each affects [10.0.0, 10.0.12] and is
 * fixed in 10.0.13 (published 2026-10-13). Pkcs and SignalR.Common also ship inside the ASP.NET Core runtime pack, so the
 * runtime packs must be 10.0.13 or later as well as the NuGet packages.
 */
export const ADVISORIES = [
	{ id: 'GHSA-846f-f764-76mx', severity: 'High', assembly: 'System.Security.Cryptography.Pkcs', fixed: '10.0.13' },
	{ id: 'GHSA-2fmv-rv2c-59x7', severity: 'Moderate', assembly: 'System.Security.Cryptography.Pkcs', fixed: '10.0.13' },
	{ id: 'GHSA-334f-vvp4-7g6p', severity: 'High', assembly: 'Microsoft.AspNetCore.SignalR.Common', fixed: '10.0.13' },
	{ id: 'GHSA-86xp-94pg-8r6r', severity: 'Moderate', assembly: 'Microsoft.AspNetCore.SignalR.Protocols.MessagePack', fixed: '10.0.13' }
];

/** The lowest runtime pack version a host may ship. */
export const FIXED_RUNTIME = '10.0.13';

/**
 * File versions of the 10.0.13 release (read from the NuGet packages and the linux-x64 runtime pack: every assembly of the
 * release carries 10.0.1326.50504). Required assemblies must be in every publish output; the others only where they ship.
 */
export const FLOORS = [
	{ file: 'System.Security.Cryptography.Pkcs.dll', fileVersion: '10.0.1326.50504', required: true },
	{ file: 'Microsoft.AspNetCore.SignalR.Common.dll', fileVersion: '10.0.1326.50504', required: true },
	{ file: 'Microsoft.AspNetCore.SignalR.Protocols.MessagePack.dll', fileVersion: '10.0.1326.50504', required: false }
];

const RUNTIME_PACKS = ['Microsoft.NETCore.App.Runtime', 'Microsoft.AspNetCore.App.Runtime'];
// The runtime identifier every host Dockerfile publishes for (dotnet publish -r linux-x64).
const RID = 'linux-x64';
const VERSION = /^\d+\.\d+\.\d+$/;

/** Compares dotted numeric versions part by part (missing parts count as 0). */
export function compareVersions(a, b) {
	const x = a.split('.').map(Number);
	const y = b.split('.').map(Number);
	for (let i = 0; i < Math.max(x.length, y.length); i++) {
		const d = (x[i] ?? 0) - (y[i] ?? 0);
		if (d !== 0) return Math.sign(d);
	}
	return 0;
}

/**
 * The file and product versions of a PE file's version resource (VS_FIXEDFILEINFO), found by walking the resource directory
 * to RT_VERSION rather than by searching the bytes. Throws when the file is not a PE file or has no version resource.
 */
export function peVersion(buf) {
	const fail = (why) => { throw new Error(`no version resource: ${why}`); };
	if (buf.length < 64 || buf.readUInt16LE(0) !== 0x5a4d) fail('not a PE file (MZ)');
	const pe = buf.readUInt32LE(0x3c);
	if (pe + 24 > buf.length || buf.readUInt32LE(pe) !== 0x00004550) fail('not a PE file (PE signature)');
	const sections = buf.readUInt16LE(pe + 6);
	const optionalSize = buf.readUInt16LE(pe + 20);
	const optional = pe + 24;
	const magic = buf.readUInt16LE(optional);
	if (magic !== 0x10b && magic !== 0x20b) fail('unknown optional header');
	const directories = optional + (magic === 0x20b ? 112 : 96);
	const resourceRva = buf.readUInt32LE(directories + 2 * 8);
	if (!resourceRva) fail('no resource directory');
	const table = optional + optionalSize;
	const toOffset = (rva) => {
		for (let i = 0; i < sections; i++) {
			const s = table + i * 40;
			const va = buf.readUInt32LE(s + 12);
			const size = Math.max(buf.readUInt32LE(s + 8), buf.readUInt32LE(s + 16));
			if (rva >= va && rva < va + size) return rva - va + buf.readUInt32LE(s + 20);
		}
		return fail(`RVA 0x${rva.toString(16)} outside every section`);
	};
	const root = toOffset(resourceRva);
	// Entries of the directory at `at`: [id or name, offset of the child relative to the root, child is a directory].
	const entries = (at) => {
		const count = buf.readUInt16LE(at + 12) + buf.readUInt16LE(at + 14);
		return Array.from({ length: count }, (_, i) => {
			const e = at + 16 + i * 8;
			const child = buf.readUInt32LE(e + 4);
			return { id: buf.readUInt32LE(e), at: root + (child & 0x7fffffff), directory: (child & 0x80000000) !== 0 };
		});
	};
	const type = entries(root).find((e) => e.id === 16 && e.directory) ?? fail('no RT_VERSION entry');
	const name = entries(type.at)[0] ?? fail('empty RT_VERSION directory');
	const language = name.directory ? entries(name.at)[0] ?? fail('empty RT_VERSION name directory') : name;
	if (language.directory) fail('unexpected resource depth');
	const data = toOffset(buf.readUInt32LE(language.at));
	// VS_VERSIONINFO: wLength, wValueLength, wType, "VS_VERSION_INFO" in UTF-16 with its terminator, padding to 32 bits.
	if (buf.toString('utf16le', data + 6, data + 36) !== 'VS_VERSION_INFO') fail('malformed VS_VERSIONINFO');
	const fixed = data + 40;
	if (buf.readUInt32LE(fixed) !== 0xfeef04bd) fail('malformed VS_FIXEDFILEINFO');
	const dotted = (ms, ls) => [ms >>> 16, ms & 0xffff, ls >>> 16, ls & 0xffff].join('.');
	return {
		fileVersion: dotted(buf.readUInt32LE(fixed + 8), buf.readUInt32LE(fixed + 12)),
		productVersion: dotted(buf.readUInt32LE(fixed + 16), buf.readUInt32LE(fixed + 20))
	};
}

/**
 * The runtime packs a deps.json names, as { 'Microsoft.NETCore.App.Runtime': ['10.0.13'], ... }: only the linux-x64 packs the
 * images publish (another RID or flavour, such as Microsoft.NETCore.App.Runtime.Mono.linux-x64, is not counted), with every
 * version listed so that a duplicate is seen rather than the last one winning.
 */
export function runtimePacks(depsJson) {
	const packs = {};
	for (const [key, library] of Object.entries(depsJson.libraries ?? {})) {
		if (library?.type !== 'runtimepack') continue;
		// Keys look like "runtimepack.Microsoft.NETCore.App.Runtime.linux-x64/10.0.13".
		const [name, version] = key.replace(/^runtimepack\./, '').split('/');
		const pack = RUNTIME_PACKS.find((p) => name === `${p}.${RID}`);
		if (pack) (packs[pack] ??= []).push(version);
	}
	return packs;
}

/**
 * Problems with a publish output, given its files as a map of relative name to Buffer (only the top folder is read: a
 * self-contained publish puts the app's deps.json and every assembly there).
 */
export function publishProblems(files, runtime) {
	const problems = [];
	if (!VERSION.test(runtime ?? '')) return [`--runtime must be a version like ${FIXED_RUNTIME}`];
	if (compareVersions(runtime, FIXED_RUNTIME) < 0) problems.push(`runtime ${runtime} is below ${FIXED_RUNTIME}, the fixed version of ${ADVISORIES.map((a) => a.id).join(', ')}`);
	const deps = Object.keys(files).filter((name) => name.endsWith('.deps.json'));
	if (deps.length !== 1) problems.push(`expected one app deps.json in the publish folder, found ${deps.length}`);
	else {
		let packs;
		try {
			packs = runtimePacks(JSON.parse(files[deps[0]].toString('utf8')));
		} catch (e) {
			packs = {};
			problems.push(`${deps[0]}: ${e.message}`);
		}
		for (const pack of RUNTIME_PACKS) {
			const versions = packs[pack] ?? [];
			if (!versions.length) problems.push(`${deps[0]}: no ${pack} runtime pack for ${RID} (not a self-contained ${RID} publish)`);
			else if (versions.length > 1) problems.push(`${deps[0]}: ${pack} listed ${versions.length} times (${versions.join(', ')})`);
			else if (versions[0] !== runtime) problems.push(`${deps[0]}: ${pack} ${versions[0]} ships, the runtime-deps base is ${runtime}; build with the SDK that bundles ${runtime}`);
		}
	}
	for (const floor of FLOORS) {
		const buf = files[floor.file];
		if (!buf) {
			if (floor.required) problems.push(`${floor.file} is missing from the publish output`);
			continue;
		}
		let version;
		try {
			version = peVersion(buf);
		} catch (e) {
			problems.push(`${floor.file}: ${e.message}`);
			continue;
		}
		const advisories = ADVISORIES.filter((a) => `${a.assembly}.dll` === floor.file).map((a) => `${a.id} ${a.severity}`).join(', ');
		if (compareVersions(version.fileVersion, floor.fileVersion) < 0) {
			problems.push(`${floor.file} file version ${version.fileVersion} (product ${version.productVersion}) is below ${floor.fileVersion}, the 10.0.13 release that fixes ${advisories}`);
		}
	}
	return problems;
}

function readPublish(folder) {
	const files = {};
	for (const entry of fs.readdirSync(folder, { withFileTypes: true })) {
		if (!entry.isFile()) continue;
		if (entry.name.endsWith('.deps.json') || FLOORS.some((f) => f.file === entry.name)) files[entry.name] = fs.readFileSync(path.join(folder, entry.name));
	}
	return files;
}

// The command line check: exit code 0 (clean), 1 (problems) or 2 (no such folder), with the text to print. Regular files only:
// a symbolic link in the publish folder counts as missing.
function checkFolder(folder, runtime) {
	if (!folder || !fs.existsSync(folder) || !fs.statSync(folder).isDirectory()) return { code: 2, text: `published runtime: ${folder ?? '(none)'} is not a folder` };
	const files = readPublish(folder);
	const problems = publishProblems(files, runtime);
	if (problems.length) return { code: 1, text: `published runtime: ${problems.length} problem(s) in ${folder}\n  ${problems.join('\n  ')}` };
	const shipped = FLOORS.filter((f) => files[f.file]).map((f) => `${f.file} ${peVersion(files[f.file]).fileVersion}`);
	return { code: 0, text: `published runtime: runtime packs ${runtime}; ${shipped.join('; ')}; at or above the 10.0.13 fixes.` };
}

function check(folder, runtime) {
	const { code, text } = checkFolder(folder, runtime);
	if (code) {
		console.error(text);
		process.exit(code);
	}
	console.log(text);
}

/**
 * A minimal PE32+ file (or PE32 with `pe32`, as the IL-only NuGet assemblies are) with one .rsrc section holding a version
 * resource, for the self-test.
 */
export function samplePe(fileVersion, productVersion = fileVersion, { pe32 = false } = {}) {
	const buf = Buffer.alloc(0x400);
	buf.writeUInt16LE(0x5a4d, 0);
	buf.writeUInt32LE(0x80, 0x3c);
	buf.writeUInt32LE(0x00004550, 0x80);
	buf.writeUInt16LE(1, 0x80 + 6); // one section
	const optionalSize = pe32 ? 224 : 240; // with 16 data directories
	buf.writeUInt16LE(optionalSize, 0x80 + 20);
	const optional = 0x80 + 24;
	buf.writeUInt16LE(pe32 ? 0x10b : 0x20b, optional);
	buf.writeUInt32LE(0x2000, optional + (pe32 ? 96 : 112) + 2 * 8); // resource directory RVA
	const section = optional + optionalSize;
	buf.write('.rsrc', section, 'latin1');
	buf.writeUInt32LE(0x200, section + 8);
	buf.writeUInt32LE(0x2000, section + 12);
	buf.writeUInt32LE(0x200, section + 16);
	buf.writeUInt32LE(0x200, section + 20);
	const root = 0x200;
	const directory = (at, id, child, isDirectory) => {
		buf.writeUInt16LE(1, at + 14);
		buf.writeUInt32LE(id, at + 16);
		buf.writeUInt32LE(((child - root) | (isDirectory ? 0x80000000 : 0)) >>> 0, at + 20);
	};
	directory(root, 16, root + 0x18, true);
	directory(root + 0x18, 1, root + 0x30, true);
	directory(root + 0x30, 0x409, root + 0x48, false);
	buf.writeUInt32LE(0x2000 + 0x60, root + 0x48); // data RVA
	const data = root + 0x60;
	buf.write('VS_VERSION_INFO', data + 6, 'utf16le');
	buf.writeUInt32LE(0xfeef04bd, data + 40);
	const write = (version, at) => {
		const [a, b, c, d] = version.split('.').map(Number);
		buf.writeUInt32LE(((a << 16) | b) >>> 0, at);
		buf.writeUInt32LE(((c << 16) | d) >>> 0, at + 4);
	};
	write(fileVersion, data + 48);
	write(productVersion, data + 56);
	return buf;
}

function selfTest() {
	const depsOf = (packs) => Buffer.from(JSON.stringify({
		libraries: { 'Ariva.Api.Main/1.0.0': { type: 'project' }, ...Object.fromEntries(packs.map((key) => [`runtimepack.${key}`, { type: 'runtimepack' }])) }
	}));
	const deps = (netcore, aspnet) => depsOf([
		...(netcore ? [`Microsoft.NETCore.App.Runtime.linux-x64/${netcore}`] : []),
		...(aspnet ? [`Microsoft.AspNetCore.App.Runtime.linux-x64/${aspnet}`] : [])
	]);
	const fixedFile = '10.0.1326.50504';
	const noVersionResource = samplePe(fixedFile);
	noVersionResource.writeUInt32LE(3, 0x200 + 16); // the root's only entry becomes RT_ICON
	const good = {
		'Ariva.Api.Main.deps.json': deps('10.0.13', '10.0.13'),
		'System.Security.Cryptography.Pkcs.dll': samplePe(fixedFile, '10.0.13.0'),
		'Microsoft.AspNetCore.SignalR.Common.dll': samplePe(fixedFile, '10.0.13.0'),
		'Microsoft.AspNetCore.SignalR.Protocols.MessagePack.dll': samplePe(fixedFile, '10.0.13.0')
	};
	const cases = [
		['good publish output', good, '10.0.13', []],
		['a later release', { ...good, 'Ariva.Api.Main.deps.json': deps('10.0.14', '10.0.14'), 'System.Security.Cryptography.Pkcs.dll': samplePe('10.0.1427.1001') }, '10.0.14', []],
		['MessagePack absent (not api-main)', Object.fromEntries(Object.entries(good).filter(([k]) => !k.includes('MessagePack'))), '10.0.13', []],
		// The images before ARV-184: SDK 10.0.103 bundled runtime packs 10.0.3 (Pkcs file version 10.0.326.7603) onto runtime-deps 10.0.12.
		['the SDK 10.0.103 images', { ...good, 'Ariva.Api.Main.deps.json': deps('10.0.3', '10.0.3'), 'System.Security.Cryptography.Pkcs.dll': samplePe('10.0.326.7603', '10.0.3.0'), 'Microsoft.AspNetCore.SignalR.Common.dll': samplePe('10.0.326.7603', '10.0.3.0') }, '10.0.12',
			['runtime 10.0.12 is below 10.0.13', 'Microsoft.NETCore.App.Runtime 10.0.3 ships', 'Microsoft.AspNetCore.App.Runtime 10.0.3 ships', 'System.Security.Cryptography.Pkcs.dll file version 10.0.326.7603', 'GHSA-846f-f764-76mx High, GHSA-2fmv-rv2c-59x7 Moderate', 'Microsoft.AspNetCore.SignalR.Common.dll file version 10.0.326.7603']],
		['runtime 10.0.12 packs', { ...good, 'Ariva.Api.Main.deps.json': deps('10.0.12', '10.0.12'), 'System.Security.Cryptography.Pkcs.dll': samplePe('10.0.1226.42308') }, '10.0.13',
			['Microsoft.NETCore.App.Runtime 10.0.12 ships, the runtime-deps base is 10.0.13', 'Pkcs.dll file version 10.0.1226.42308']],
		['one pack behind the other', { ...good, 'Ariva.Api.Main.deps.json': deps('10.0.13', '10.0.12') }, '10.0.13', ['Microsoft.AspNetCore.App.Runtime 10.0.12 ships']],
		['framework-dependent publish', { ...good, 'Ariva.Api.Main.deps.json': deps(undefined, undefined) }, '10.0.13', ['no Microsoft.NETCore.App.Runtime runtime pack', 'no Microsoft.AspNetCore.App.Runtime runtime pack']],
		['the vulnerable MessagePack package', { ...good, 'Microsoft.AspNetCore.SignalR.Protocols.MessagePack.dll': samplePe('10.0.1226.42308') }, '10.0.13', ['MessagePack.dll file version 10.0.1226.42308', 'GHSA-86xp-94pg-8r6r Moderate']],
		['Pkcs missing', Object.fromEntries(Object.entries(good).filter(([k]) => !k.includes('Pkcs'))), '10.0.13', ['System.Security.Cryptography.Pkcs.dll is missing']],
		['Pkcs not a PE file', { ...good, 'System.Security.Cryptography.Pkcs.dll': Buffer.from('not a dll') }, '10.0.13', ['Pkcs.dll: no version resource']],
		['no deps.json', Object.fromEntries(Object.entries(good).filter(([k]) => !k.endsWith('.deps.json'))), '10.0.13', ['expected one app deps.json']],
		['two deps.json', { ...good, 'Other.deps.json': deps('10.0.13', '10.0.13') }, '10.0.13', ['found 2']],
		['malformed deps.json', { ...good, 'Ariva.Api.Main.deps.json': Buffer.from('{') }, '10.0.13', ['Ariva.Api.Main.deps.json:']],
		['bad runtime argument', good, '10.0', ['--runtime must be a version']],
		['no runtime argument', good, undefined, ['--runtime must be a version']],
		['PE32 assemblies (IL-only NuGet copies)', { ...good, 'System.Security.Cryptography.Pkcs.dll': samplePe(fixedFile, '10.0.13.0', { pe32: true }) }, '10.0.13', []],
		['a vulnerable PE32 assembly', { ...good, 'Microsoft.AspNetCore.SignalR.Protocols.MessagePack.dll': samplePe('10.0.1226.42308', '10.0.12.0', { pe32: true }) }, '10.0.13', ['MessagePack.dll file version 10.0.1226.42308']],
		['Pkcs cut inside its optional header', { ...good, 'System.Security.Cryptography.Pkcs.dll': samplePe(fixedFile).subarray(0, 0x100) }, '10.0.13', ['System.Security.Cryptography.Pkcs.dll:']],
		['SignalR.Common cut inside its version data', { ...good, 'Microsoft.AspNetCore.SignalR.Common.dll': samplePe(fixedFile).subarray(0, 0x298) }, '10.0.13', ['Microsoft.AspNetCore.SignalR.Common.dll:']],
		['Pkcs without RT_VERSION', { ...good, 'System.Security.Cryptography.Pkcs.dll': noVersionResource }, '10.0.13', ['Pkcs.dll: no version resource: no RT_VERSION entry']],
		['a Mono runtime pack instead', { ...good, 'Ariva.Api.Main.deps.json': depsOf(['Microsoft.NETCore.App.Runtime.Mono.linux-x64/10.0.13', 'Microsoft.AspNetCore.App.Runtime.linux-x64/10.0.13']) }, '10.0.13', ['no Microsoft.NETCore.App.Runtime runtime pack for linux-x64']],
		['another runtime identifier', { ...good, 'Ariva.Api.Main.deps.json': depsOf(['Microsoft.NETCore.App.Runtime.linux-arm64/10.0.13', 'Microsoft.AspNetCore.App.Runtime.linux-arm64/10.0.13']) }, '10.0.13', ['no Microsoft.NETCore.App.Runtime runtime pack for linux-x64', 'no Microsoft.AspNetCore.App.Runtime runtime pack for linux-x64']],
		['a pack listed twice', { ...good, 'Ariva.Api.Main.deps.json': depsOf(['Microsoft.NETCore.App.Runtime.linux-x64/10.0.12', 'Microsoft.NETCore.App.Runtime.linux-x64/10.0.13', 'Microsoft.AspNetCore.App.Runtime.linux-x64/10.0.13']) }, '10.0.13', ['Microsoft.NETCore.App.Runtime listed 2 times (10.0.12, 10.0.13)']]
	];
	const failures = [];
	for (const [name, files, runtime, expected] of cases) {
		const found = publishProblems(files, runtime);
		const text = found.join('\n');
		if (!expected.length && found.length) failures.push(`${name}: expected clean, got ${text}`);
		for (const fragment of expected) if (!text.includes(fragment)) failures.push(`${name}: did not report "${fragment}" (got: ${text || 'nothing'})`);
	}
	for (const pe32 of [false, true]) {
		const read = peVersion(samplePe('10.0.1326.50504', '10.0.13.0', { pe32 }));
		if (read.fileVersion !== '10.0.1326.50504' || read.productVersion !== '10.0.13.0') failures.push(`peVersion (${pe32 ? 'PE32' : 'PE32+'}) read ${JSON.stringify(read)}`);
	}
	// The command line path on a real folder: exit 2 without the folder, 1 without --runtime, 1 when a required assembly is a
	// symbolic link (only regular files count), 0 on the good output.
	const folder = fs.mkdtempSync(path.join(os.tmpdir(), 'published-runtime-'));
	try {
		for (const [name, content] of Object.entries(good)) fs.writeFileSync(path.join(folder, name), content);
		const cli = [
			['missing folder', path.join(folder, 'absent'), '10.0.13', 2, 'is not a folder'],
			['missing --runtime', folder, undefined, 1, '--runtime must be a version'],
			['good folder', folder, '10.0.13', 0, 'runtime packs 10.0.13; System.Security.Cryptography.Pkcs.dll 10.0.1326.50504']
		];
		for (const [name, dir, runtime, code, fragment] of cli) {
			const result = checkFolder(dir, runtime);
			if (result.code !== code || !result.text.includes(fragment)) failures.push(`command line, ${name}: exit ${result.code}, ${result.text}`);
		}
		const pkcs = path.join(folder, 'System.Security.Cryptography.Pkcs.dll');
		fs.renameSync(pkcs, path.join(folder, 'real.bin'));
		let linked = true;
		try {
			fs.symlinkSync(path.join(folder, 'real.bin'), pkcs);
		} catch {
			linked = false; // no symbolic links on this machine (Windows without the privilege): nothing to prove
		}
		if (linked) {
			const result = checkFolder(folder, '10.0.13');
			if (result.code !== 1 || !result.text.includes('System.Security.Cryptography.Pkcs.dll is missing')) failures.push(`command line, symbolic link: exit ${result.code}, ${result.text}`);
		}
	} finally {
		fs.rmSync(folder, { recursive: true, force: true });
	}
	if (compareVersions('10.0.1326.50504', '10.0.326.7603') <= 0 || compareVersions('10.0.13', '10.0.13.0') !== 0) failures.push('compareVersions');
	if (failures.length) {
		console.error(`published runtime self-test failed:\n  ${failures.join('\n  ')}`);
		process.exit(1);
	}
	console.log(`published runtime self-test: ${cases.length} cases and the command line path passed.`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
	const args = process.argv.slice(2);
	const value = (name) => {
		const i = args.indexOf(name);
		return i >= 0 ? args[i + 1] : undefined;
	};
	if (args.includes('--self-test')) selfTest();
	else if (args.includes('--check')) check(value('--check'), value('--runtime'));
	else {
		console.error('usage: node scripts/published-runtime.mjs --check <publish folder> --runtime <x.y.z> | --self-test');
		process.exit(2);
	}
}
