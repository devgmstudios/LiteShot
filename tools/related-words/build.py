"""Builds src/LiteShot/Words/related-words.gz, the table behind Find on screen's related words ("fish" finds salmon), from
Open English WordNet's LMF XML (https://github.com/globalwordnet/english-wordnet/releases, english-wordnet-<year>.xml.gz).

    python -I tools/related-words/build.py <english-wordnet-2025.xml.gz>

Only nouns, and only one-word lemmas (OCR reads single words). Format, gzipped UTF-8 text:
    line 1      header with the source and its license
    "S <n>"     then n lines: synset i's parents (hypernyms and instance hypernyms) as comma-separated numbers
    "W"         then one line per word: word<TAB>its synset numbers, comma-separated
The same file is written for the Windows app (src/LiteShot/Words/) and the Mac app's core (mac/Sources/LiteShotCore/Resources/).
"""
import gzip
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUT = [ROOT / "src" / "LiteShot" / "Words" / "related-words.gz", ROOT / "mac" / "Sources" / "LiteShotCore" / "Resources" / "related-words.gz"]
WORD = re.compile(r"^[a-z][a-z'-]*$")


def main(source: str) -> None:
    parents: dict[str, list[str]] = {}
    words: dict[str, list[str]] = {}
    with gzip.open(source, "rb") as f:
        for _, el in ET.iterparse(f, events=("end",)):
            tag = el.tag.rsplit("}", 1)[-1]
            if tag == "LexicalEntry":
                lemma = el.find("{*}Lemma")
                if lemma is None:
                    lemma = el.find("Lemma")
                if lemma is not None and lemma.get("partOfSpeech") == "n":
                    form = lemma.get("writtenForm", "").lower()
                    if WORD.match(form):
                        for sense in list(el.iter()):
                            if sense.tag.rsplit("}", 1)[-1] == "Sense" and sense.get("synset"):
                                words.setdefault(form, []).append(sense.get("synset"))
                el.clear()
            elif tag == "Synset":
                if el.get("partOfSpeech") == "n":
                    ups = [r.get("target") for r in el.iter()
                           if r.tag.rsplit("}", 1)[-1] == "SynsetRelation" and r.get("relType") in ("hypernym", "instance_hypernym")]
                    parents[el.get("id")] = ups
                el.clear()

    # Number the noun synsets, keeping only links between nouns.
    ids = {s: i for i, s in enumerate(sorted(parents))}
    lines = [f"LiteShot related words v1 · Open English WordNet 2025 (CC BY 4.0, github.com/globalwordnet/english-wordnet), derived from Princeton WordNet (WordNet License)",
             f"S {len(ids)}"]
    for s in sorted(parents):
        lines.append(",".join(str(ids[p]) for p in parents[s] if p in ids))
    lines.append("W")
    kept = 0
    for w in sorted(words):
        syn = sorted({ids[s] for s in words[w] if s in ids})
        if syn:
            lines.append(f"{w}\t{','.join(map(str, syn))}")
            kept += 1
    data = gzip.compress(("\n".join(lines) + "\n").encode("utf-8"), compresslevel=9, mtime=0)
    for path in OUT:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
    print(f"{len(ids)} noun synsets, {kept} words, {len(data) / 1024:.0f} KB -> " + ", ".join(str(p.relative_to(ROOT)) for p in OUT))


if __name__ == "__main__":
    main(sys.argv[1])
