#!/usr/bin/env node
// ARV-225b (CWE-94): proves the two source encoding checks of Directory.Build.props with builds. A scratch project under the
// repository (.verify/source-encoding, ignored by git) takes Directory.Build.props as every project does; each case builds it
// from scratch (--no-incremental, so the compiler runs and its arguments are judged) and must pass or fail as listed: nothing
// set and 65001 pass; a code page given on the command line or by an environment variable, a compiler response file, a code
// page set by a target after the first check (AfterTargets of it, or BeforeTargets CoreCompile defined later), and the first
// check replaced by a target of the same name each fail, the last three by the second check, which reads what the compiler was
// given and deletes the assembly. Run by scripts/verify.mjs backend and CI's backend job. Every dotnet call takes an argument
// array, no shell (CWE-78).
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const DIR = path.join(ROOT, '.verify', 'source-encoding');
const BEFORE = 'A code page other than 65001 is set for the compiler (ARV-225b)';
const RESPONSE = 'A compiler response file is set (ARV-225b)';
const AFTER = 'The compiler was given';

const late = (target) => `<Project>\n  ${target}\n</Project>\n`;
const files = {
  'Probe.csproj': `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <RestorePackagesWithLockFile>false</RestorePackagesWithLockFile>
    <RestoreLockedMode>false</RestoreLockedMode>
  </PropertyGroup>
  <Import Project="after-check.targets" Condition="'$(ProbeAfterCheck)' == 'true'" />
  <Import Project="before-compile.targets" Condition="'$(ProbeBeforeCompile)' == 'true'" />
  <Import Project="replaced-check.targets" Condition="'$(ProbeReplacedCheck)' == 'true'" />
</Project>
`,
  'Probe.cs': 'namespace Probe;\n\npublic static class Sample\n{\n    public static int Value => 1;\n}\n',
  'empty.rsp': '\n',
  'after-check.targets': late('<Target Name="ProbeLate" AfterTargets="ArivaSourceEncodingCheck"><PropertyGroup><CodePage>1252</CodePage></PropertyGroup></Target>'),
  'before-compile.targets': late('<Target Name="ProbeLater" BeforeTargets="CoreCompile"><PropertyGroup><CodePage>1252</CodePage></PropertyGroup></Target>'),
  'replaced-check.targets': late('<Target Name="ArivaSourceEncodingCheck" />')
};

const cases = [
  { name: 'nothing set', args: [], passes: true },
  { name: 'code page 65001 on the command line', args: ['-p:CodePage=65001'], passes: true },
  { name: 'code page 1252 on the command line', args: ['-p:CodePage=1252'], fails: BEFORE },
  { name: 'code page 1252 in the environment', args: [], env: { CodePage: '1252' }, fails: BEFORE },
  { name: 'a compiler response file', args: [`-p:CompilerResponseFile=${path.join(DIR, 'empty.rsp')}`], fails: RESPONSE },
  { name: 'a code page set after the first check', args: ['-p:ProbeAfterCheck=true'], fails: AFTER },
  { name: 'a code page set by a later BeforeTargets of the compiler', args: ['-p:ProbeBeforeCompile=true'], fails: AFTER },
  { name: 'the first check replaced, a code page in the environment', args: ['-p:ProbeReplacedCheck=true'], env: { CodePage: '1252' }, fails: AFTER }
];

function build({ args, env = {} }) {
  const childEnv = { ...process.env, ...env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1', MSBUILDDISABLENODEREUSE: '1' };
  try {
    const out = execFileSync('dotnet', ['build', path.join(DIR, 'Probe.csproj'), '--no-incremental', '-nologo', '-v', 'q', '-nodeReuse:false', ...args],
      { cwd: DIR, env: childEnv, encoding: 'utf8', stdio: 'pipe', maxBuffer: 64 * 1024 * 1024 });
    return { ok: true, out };
  } catch (e) {
    return { ok: false, out: `${e.stdout ?? ''}${e.stderr ?? ''}` };
  }
}

fs.rmSync(DIR, { recursive: true, force: true });
fs.mkdirSync(DIR, { recursive: true });
for (const [name, text] of Object.entries(files)) fs.writeFileSync(path.join(DIR, name), text);
const problems = [];
for (const c of cases) {
  const result = build(c);
  const assembly = path.join(DIR, 'obj', 'Debug', 'net10.0', 'Probe.dll');
  if (c.passes && !result.ok) problems.push(`${c.name}: the build failed (${result.out.split('\n').find(l => /error/i.test(l))?.trim() ?? 'no error line'})`);
  if (c.fails && result.ok) problems.push(`${c.name}: the build passed`);
  if (c.fails && !result.ok && !result.out.includes(c.fails)) problems.push(`${c.name}: the build failed, but not for this check (${result.out.split('\n').find(l => /error/i.test(l))?.trim() ?? 'no error line'})`);
  if (c.fails === AFTER && fs.existsSync(assembly)) problems.push(`${c.name}: the assembly the compiler wrote was not deleted`);
}
fs.rmSync(DIR, { recursive: true, force: true });
if (problems.length) {
  console.error(`source encoding check FAILED:\n${problems.join('\n')}`);
  process.exit(1);
}
console.log(`source encoding check: ${cases.length} builds, ${cases.filter(c => c.passes).length} pass and ${cases.filter(c => c.fails).length} fail for their own check (a code page or a response file before the compiler; what the compiler was given after it, the assembly deleted).`);
