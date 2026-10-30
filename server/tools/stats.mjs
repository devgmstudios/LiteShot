// Prints LiteShot's download counts from the update server: `npm run stats` (add -- --json for the raw numbers).
//   LITESHOT_SERVER         default https://liteshot.app
//   LITESHOT_PUBLISH_TOKEN  default: publish-token.txt beside the signing key (%APPDATA%\Devgm\LiteShot Publishing)
import { readFileSync } from 'node:fs';
import { join } from 'node:path';

const server = (process.env.LITESHOT_SERVER ?? 'https://liteshot.app').replace(/\/+$/, '');
let token = process.env.LITESHOT_PUBLISH_TOKEN;
if (!token) {
  const file = join(process.env.APPDATA ?? '', 'Devgm', 'LiteShot Publishing', 'publish-token.txt');
  try {
    token = readFileSync(file, 'utf8').trim();
  } catch {
    console.error(`No publish token: set LITESHOT_PUBLISH_TOKEN or put it in ${file}`);
    process.exit(2);
  }
}

const res = await fetch(`${server}/v1/stats`, { headers: { Authorization: `Bearer ${token}` } });
const text = await res.text();
if (!res.ok) {
  console.error(`${server} answered ${res.status}: ${text}`);
  process.exit(1);
}
const s = JSON.parse(text);
if (process.argv.includes('--json')) {
  console.log(JSON.stringify(s, null, 2));
  process.exit(0);
}

const n = (v) => v.toLocaleString('en-US');
const split = (c) => `installer ${n(c.installer)} · portable ${n(c.portable)}`;
console.log(`\nLiteShot downloads (${server})\n`);
console.log(`  Downloads  ${n(s.downloads.total).padStart(7)}   ${split(s.downloads)}`);
console.log(`  Updates    ${n(s.updates.total).padStart(7)}   ${split(s.updates)}   (copies updating themselves)`);
console.log(`  Bots       ${n(s.bots).padStart(7)}   (crawlers and scanners, not counted above)`);

if (s.last30Days.length) {
  console.log('\nLast 30 days (downloads)');
  const most = Math.max(...s.last30Days.map((d) => d.installer + d.portable), 1);
  for (const d of s.last30Days) {
    const total = d.installer + d.portable;
    console.log(`  ${d.day}  ${n(total).padStart(6)}  ${'█'.repeat(Math.ceil((total / most) * 30))}`);
  }
}
if (s.versions.length) {
  console.log('\nBy version');
  for (const v of s.versions) console.log(`  ${v.version.padEnd(9)} downloads ${n(v.downloads.total).padStart(6)}   updates ${n(v.updates).padStart(6)}`);
}
console.log('');
