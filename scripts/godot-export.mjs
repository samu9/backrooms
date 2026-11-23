// Builds the installable desktop client: copies the version from package.json into the Godot
// project, then exports the Linux and Windows presets (godot/Game/export_presets.cfg) to
// godot/build/{linux,windows}/ and zips each one.
//   GODOT=/path/to/godot-4.7-mono node scripts/godot-export.mjs [linux|windows ...]
// Needs the Godot 4.7 .NET export templates (Editor → Manage Export Templates, or extracted into
// $XDG_DATA_HOME/godot/export_templates/4.7.2.stable.mono) and the .NET 8 SDK on PATH.
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';

const root = resolve(import.meta.dirname, '..');
const game = join(root, 'godot/Game');
const godot = process.env.GODOT;
if (!godot) throw new Error('set GODOT to the Godot 4.7 .NET editor binary');
const { version } = JSON.parse(readFileSync(join(root, 'package.json'), 'utf8'));

// one version everywhere: package.json is the source
const sub = (file, re, to) => {
  const p = join(game, file);
  const s = readFileSync(p, 'utf8');
  const t = s.replace(re, to);
  if (t !== s) writeFileSync(p, t);
};
sub('project.godot', /^config\/version=".*"$/m, `config/version="${version}"`);
sub('export_presets.cfg', /^application\/file_version=".*"$/gm, `application/file_version="${version}"`);
sub('export_presets.cfg', /^application\/product_version=".*"$/gm, `application/product_version="${version}"`);

const presets = { linux: ['Linux', 'build/linux/backrooms.x86_64'], windows: ['Windows Desktop', 'build/windows/Backrooms.exe'] };
const want = process.argv.slice(2).length ? process.argv.slice(2) : Object.keys(presets);
for (const key of want) {
  const [name, rel] = presets[key] ?? [];
  if (!name) throw new Error(`unknown target ${key} (linux, windows)`);
  const out = join(root, 'godot', rel);
  rmSync(dirname(out), { recursive: true, force: true });
  mkdirSync(dirname(out), { recursive: true });
  console.log(`exporting ${name} ${version} -> ${out}`);
  execFileSync(godot, ['--headless', '--path', game, '--export-release', name, out], { stdio: 'inherit' });
  if (!existsSync(out)) throw new Error(`export of ${name} produced nothing`);
  const zip = join(root, 'godot/build', `backrooms-${version}-${key}.zip`);
  rmSync(zip, { force: true });
  execFileSync('zip', ['-qr', zip, '.'], { cwd: dirname(out) });
  console.log(`  ${readdirSync(dirname(out)).length} files, ${zip}`);
}
