// Builds LiteShot's big name lists (src/LiteShot/Names/*.txt) from public data. Run with Node 18+:
//   node tools/names/build-name-lists.mjs --download   # fetch the sources into tools/names/.cache, then build
//   node tools/names/build-name-lists.mjs              # build from the cache
//
// Sources (see src/LiteShot/Names/SOURCES.md for licences):
//   US first names  SSA baby names, top 1,000 per year and sex 1880-2008 (public domain; GitHub mirror, ssa.gov blocks scripts)
//   US surnames     2010 Census surnames with 500+ people (public domain)
//   UK, Europe, Scandinavia, US, Canada, Australia, NZ: given and family names of notable people in Wikidata (CC0)
//   english-words.txt  the lowercase Hunspell entries, used at run time too
//   Filters         English words (Hunspell en_US + en_GB, lowercase entries), Wikidata places, brands, nationalities
//
// Names that are also everyday words, places, brands or nationalities never become surnames; as first names they go to
// first-names-ambiguous.txt, which LiteShot only trusts next to a surname or an initial.
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const cache = join(here, '.cache');
const out = join(here, '..', '..', 'src', 'LiteShot', 'Names');

const MIN_CENSUS = 500;      // people with the surname in the 2010 Census
const MIN_WIKI_GIVEN = 10;   // notable people with the given name
const MIN_WIKI_FAMILY = 10;  // notable people with the family name
const COMMON_SURNAME_RANK = 2000;  // Census ranks this high stay surnames even when they're also a brand ("Ford")

// US, UK, Ireland, Canada, Australia, New Zealand, the Nordics, and the rest of Europe.
const COUNTRIES = 'Q30 Q145 Q16 Q408 Q664 Q27 Q20 Q34 Q35 Q33 Q189 Q183 Q142 Q38 Q29 Q45 Q55 Q31 Q39 Q40 Q36 Q213 Q214 Q28 Q218 Q219 Q41 Q224 Q403 Q215 Q37 Q211 Q191 Q212 Q159 Q184 Q32 Q233 Q229 Q222 Q221 Q225 Q236 Q217';
const PREFIXES = 'PREFIX wd: <http://www.wikidata.org/entity/> PREFIX wdt: <http://www.wikidata.org/prop/direct/> PREFIX rdfs: <http://www.w3.org/2000/01/rdf-schema#> PREFIX wikibase: <http://wikiba.se/ontology#>';
const people = (prop) => `${PREFIXES} SELECT ?label (COUNT(DISTINCT ?p) AS ?c) WHERE { VALUES ?country { ${COUNTRIES.split(' ').map((q) => 'wd:' + q).join(' ')} }
  ?p wdt:P27 ?country . ?p wdt:P31 wd:Q5 . ?p wdt:${prop} ?n . ?n rdfs:label ?label . FILTER(LANG(?label) = "en") } GROUP BY ?label HAVING (COUNT(DISTINCT ?p) >= 3)`;
const QUERIES = {
  'given.tsv': people('P735'),
  'family.tsv': people('P734'),
  // Countries, regions, and anywhere with 50,000+ people.
  'places.tsv': `${PREFIXES} SELECT DISTINCT ?label WHERE { { ?x wdt:P1082 ?pop . FILTER(?pop >= 50000) } UNION { ?x wdt:P31 wd:Q6256 } UNION { ?x wdt:P31 wd:Q3624078 }
    ?x rdfs:label ?label . FILTER(LANG(?label) = "en") }`,
  // Companies, brands, software, websites, games, apps, car makers... known in 15+ Wikipedias.
  'brands.tsv': `${PREFIXES} SELECT DISTINCT ?label WHERE { VALUES ?t { wd:Q4830453 wd:Q431289 wd:Q7397 wd:Q35127 wd:Q167270 wd:Q6881511 wd:Q891723 wd:Q7889 wd:Q9143 wd:Q9135 wd:Q620615 wd:Q786820 wd:Q1331793 wd:Q2085381 }
    ?x wdt:P31 ?t . ?x wikibase:sitelinks ?s . FILTER(?s >= 15) ?x rdfs:label ?label . FILTER(LANG(?label) = "en") }`,
  // Nationalities ("Swedish") and languages.
  'demonyms.tsv': `${PREFIXES} SELECT DISTINCT ?label WHERE { { ?c wdt:P31 wd:Q6256 . ?c wdt:P1549 ?label . } UNION { ?l wdt:P31 wd:Q34770 . ?l wikibase:sitelinks ?s . FILTER(?s >= 20) ?l rdfs:label ?label . }
    FILTER(LANG(?label) = "en") }`,
};
const FILES = {
  'baby-names.csv': 'https://raw.githubusercontent.com/hadley/data-baby-names/master/baby-names.csv',
  'census.zip': 'https://www2.census.gov/topics/genealogy/2010surnames/names.zip',
  'en_US.dic': 'https://raw.githubusercontent.com/wooorm/dictionaries/main/dictionaries/en/index.dic',
  'en_GB.dic': 'https://raw.githubusercontent.com/wooorm/dictionaries/main/dictionaries/en-GB/index.dic',
};

async function download() {
  mkdirSync(cache, { recursive: true });
  for (const [file, url] of Object.entries(FILES)) {
    const res = await fetch(url);
    if (!res.ok) throw new Error(`${url}: ${res.status}`);
    writeFileSync(join(cache, file), Buffer.from(await res.arrayBuffer()));
    console.log(`fetched ${file}`);
  }
  // Windows' own tar.exe reads zip files.
  execFileSync(process.platform === 'win32' ? 'C:\\Windows\\System32\\tar.exe' : 'unzip', process.platform === 'win32'
    ? ['-xf', join(cache, 'census.zip'), '-C', cache] : ['-o', join(cache, 'census.zip'), '-d', cache]);
  for (const [file, query] of Object.entries(QUERIES)) {
    const res = await fetch('https://qlever.dev/api/wikidata?' + new URLSearchParams({ query }), { headers: { Accept: 'text/tab-separated-values' } });
    if (!res.ok) throw new Error(`${file} query: ${res.status} ${await res.text()}`);
    writeFileSync(join(cache, file), await res.text());
    console.log(`queried ${file}`);
  }
}

if (process.argv.includes('--download')) await download();
if (!existsSync(join(cache, 'given.tsv'))) throw new Error('No data yet: run with --download first.');

const read = (file) => readFileSync(join(cache, file), 'utf8');
// One word of Latin letters (with ' and -), at least two letters; lowercase.
const WORD = /^[\p{Script=Latin}][\p{Script=Latin}'’-]*[\p{Script=Latin}]$/u;
const clean = (s) => (WORD.test(s) ? s.toLowerCase().replace(/’/g, "'") : null);
// The same name as OCR may read it without diacritics: "bjørn" → "bjorn".
const FOLD = { ø: 'o', æ: 'ae', œ: 'oe', ß: 'ss', ł: 'l', đ: 'd', þ: 'th', ð: 'd', ı: 'i' };
const fold = (s) => s.normalize('NFD').replace(/\p{M}/gu, '').replace(/[øæœßłđþðı]/g, (c) => FOLD[c]);
// Wikidata TSV rows: "Label"@en <tab> count.
function tsv(file) {
  const rows = [];
  for (const line of read(file).split('\n').slice(1)) {
    const m = /^"(.*)"@en(?:\t(\d+))?/.exec(line.trim());
    if (m) rows.push([m[1], Number(m[2] ?? 0)]);
  }
  return rows;
}

// Lowercase dictionary entries are everyday words; a plural or past tense of one counts too.
const dictionary = new Set();
for (const file of ['en_US.dic', 'en_GB.dic'])
  for (const line of read(file).split('\n').slice(1)) {
    const word = line.split('/')[0].trim();
    if (/^[a-z][a-z'-]*$/.test(word)) dictionary.add(word);
  }
const isWord = (w) => dictionary.has(w)
  || (w.endsWith('s') && dictionary.has(w.slice(0, -1)))
  || (w.endsWith('es') && dictionary.has(w.slice(0, -2)))
  || (w.endsWith('ies') && dictionary.has(w.slice(0, -3) + 'y'))
  || (w.endsWith('ed') && (dictionary.has(w.slice(0, -2)) || dictionary.has(w.slice(0, -1))));

// Single words only: "New York" doesn't stop "York", but "York" itself is a place.
const labels = (file) => new Set(tsv(file).map(([l]) => clean(l)).filter(Boolean));
const places = labels('places.tsv'), brands = labels('brands.tsv'), demonyms = labels('demonyms.tsv');
// The first word of a brand ("Norton" in "Norton AntiVirus") makes a first name too risky to trust on its own.
const brandWords = new Set(tsv('brands.tsv').map(([l]) => clean(l.split(' ')[0])).filter(Boolean));

const censusRank = new Map();
const surnames = new Set();
for (const line of read('Names_2010Census.csv').split('\n').slice(1)) {
  const [name, rank, count] = line.split(',');
  const w = clean(name ?? '');
  if (!w || Number(count) < MIN_CENSUS) continue;
  censusRank.set(w, Number(rank));
  surnames.add(w);
}
for (const [label, count] of tsv('family.tsv')) if (count >= MIN_WIKI_FAMILY && clean(label)) surnames.add(clean(label));

const given = new Set();
for (const line of read('baby-names.csv').split('\n').slice(1)) {
  const w = clean(line.split(',')[1]?.replace(/"/g, '') ?? '');
  if (w) given.add(w);
}
for (const [label, count] of tsv('given.tsv')) if (count >= MIN_WIKI_GIVEN && clean(label)) given.add(clean(label));

// Capitalized words that turn up as names in the data but are holidays or tech on screen.
const NOT_NAMES = new Set('christmas easter halloween thanksgiving hanukkah diwali ramadan eid kwanzaa passover advent lent docker kubernetes internet wifi bluetooth javascript typescript json html css api url sql ios macos linux unix'.split(' '));
const commonSurname = (w) => (censusRank.get(w) ?? Infinity) <= COMMON_SURNAME_RANK;
const risky = (w) => w.length <= 2 || NOT_NAMES.has(w) || isWord(w) || places.has(w) || demonyms.has(w) || (brands.has(w) && !commonSurname(w));
const withFolded = (set) => new Set([...set].flatMap((w) => [w, fold(w)]));

const riskyFirst = (w) => risky(w) || brandWords.has(w);
const firstSafe = [...withFolded(given)].filter((w) => !riskyFirst(w)).sort();
const firstRisky = [...withFolded(given)].filter((w) => riskyFirst(w) && w.length > 1).sort();
const surnameSafe = [...withFolded(surnames)].filter((w) => w.length > 2 && !risky(w)).sort();

const header = (what) => `# ${what}\n# Generated by tools/names/build-name-lists.mjs from public data; see SOURCES.md. One lowercase name per line.\n`;
mkdirSync(out, { recursive: true });
writeFileSync(join(out, 'first-names.txt'), header('Given names') + firstSafe.join('\n') + '\n');
writeFileSync(join(out, 'first-names-ambiguous.txt'), header('Given names that are also words, places, brands or nationalities: trusted only next to a surname or an initial') + firstRisky.join('\n') + '\n');
writeFileSync(join(out, 'surnames.txt'), header('Family names (everyday words, places, brands and nationalities left out)') + surnameSafe.join('\n') + '\n');
// Everyday English words, so a capitalized word like "Install" in "Install Norton" isn't taken for a first name.
writeFileSync(join(out, 'english-words.txt'), header('Lowercase English words (Hunspell en_US + en_GB): never the unfamiliar half of a name') + [...dictionary].sort().join('\n') + '\n');
console.log(`first names ${firstSafe.length}, ambiguous first names ${firstRisky.length}, surnames ${surnameSafe.length}`);

