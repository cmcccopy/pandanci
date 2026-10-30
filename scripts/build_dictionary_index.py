"""Install ECDICT and build indexed CC-CEDICT/reverse/morphology lookups.

Uses Python's standard library only. Original user dictionaries are never edited.
"""
import argparse
import datetime
import gzip
import hashlib
import json
import re
import shutil
import sqlite3
import tempfile
import zipfile
from contextlib import closing
from pathlib import Path

CEDICT_LINE = re.compile(r"^(\S+) (\S+) \[([^\]]+)\] /(.*)/$")
CJK_TERM = re.compile(r"[\u3400-\u9fff]{2,24}")


def sha256(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def build_index(ecdict_path, cedict_gzip, index_path):
    source = sqlite3.connect(f"file:{Path(ecdict_path).as_posix()}?mode=ro", uri=True)
    index = sqlite3.connect(index_path)
    try:
        index.executescript("""
            PRAGMA journal_mode=OFF;
            PRAGMA synchronous=OFF;
            CREATE TABLE cedict(id INTEGER PRIMARY KEY, traditional TEXT NOT NULL,
                simplified TEXT NOT NULL, pinyin TEXT NOT NULL, definition TEXT NOT NULL);
            CREATE VIRTUAL TABLE cedict_english USING fts4(definition);
            CREATE TABLE chinese_terms(term TEXT NOT NULL, word TEXT NOT NULL, rank INTEGER NOT NULL);
            CREATE TABLE word_forms(form TEXT NOT NULL, word TEXT NOT NULL);
            CREATE TABLE metadata(key TEXT PRIMARY KEY, value TEXT NOT NULL);
        """)
        cedict_count = 0
        headers = []
        with gzip.open(cedict_gzip, "rt", encoding="utf-8-sig") as stream:
            for line in stream:
                if line.startswith("#"):
                    headers.append(line.rstrip())
                    continue
                match = CEDICT_LINE.match(line.strip())
                if not match:
                    continue
                traditional, simplified, pinyin, definition = match.groups()
                definition = definition.replace("/", "; ")
                cursor = index.execute("INSERT INTO cedict(traditional,simplified,pinyin,definition) VALUES(?,?,?,?)",
                                       (traditional, simplified, pinyin, definition))
                index.execute("INSERT INTO cedict_english(rowid,definition) VALUES(?,?)", (cursor.lastrowid, definition))
                cedict_count += 1
        print(f"CC-CEDICT: {cedict_count:,} entries", flush=True)

        total = 0
        reverse_rows = 0
        form_rows = 0
        terms_batch = []
        forms_batch = []
        for word, translation, exchange, frq, bnc in source.execute("SELECT word,translation,exchange,frq,bnc FROM stardict"):
            total += 1
            word = str(word or "").strip()
            if not word:
                continue
            frequency = int(frq or 0) or int(bnc or 0) or 10000000
            terms = set(CJK_TERM.findall(str(translation or "")))
            terms_batch.extend((term, word, frequency) for term in terms)
            for item in str(exchange or "").split("/"):
                if ":" not in item:
                    continue
                kind, forms = item.split(":", 1)
                if kind not in ("p", "d", "i", "3", "r", "t", "s", "0"):
                    continue
                forms_batch.extend((form.strip().lower(), word) for form in forms.split(",")
                                   if form.strip() and form.strip().lower() != word.lower())
            if total % 10000 == 0:
                index.executemany("INSERT INTO chinese_terms VALUES(?,?,?)", terms_batch)
                index.executemany("INSERT INTO word_forms VALUES(?,?)", forms_batch)
                reverse_rows += len(terms_batch)
                form_rows += len(forms_batch)
                terms_batch.clear()
                forms_batch.clear()
            if total % 500000 == 0:
                print(f"Indexed {total:,} ECDICT entries...", flush=True)
        index.executemany("INSERT INTO chinese_terms VALUES(?,?,?)", terms_batch)
        index.executemany("INSERT INTO word_forms VALUES(?,?)", forms_batch)
        reverse_rows += len(terms_batch)
        form_rows += len(forms_batch)
        index.executescript("""
            CREATE INDEX idx_cedict_simplified ON cedict(simplified);
            CREATE INDEX idx_cedict_traditional ON cedict(traditional);
            CREATE INDEX idx_chinese_terms ON chinese_terms(term,rank,word);
            CREATE INDEX idx_word_forms ON word_forms(form,word);
            ANALYZE;
        """)
        counts = {"ecdict_entries": total, "cedict_entries": cedict_count,
                  "chinese_reverse_terms": reverse_rows, "word_forms": form_rows}
        index.executemany("INSERT INTO metadata VALUES(?,?)", ((key, str(value)) for key, value in counts.items()))
        index.commit()
        assert index.execute("PRAGMA quick_check").fetchone()[0] == "ok"
        return counts, headers
    finally:
        source.close()
        index.close()


def install(ecdict_archive, cedict_gzip, output_dir):
    output_dir = Path(output_dir).resolve()
    output_dir.mkdir(parents=True, exist_ok=True)
    (output_dir / "licenses").mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".work-", dir=output_dir) as work_dir:
        work = Path(work_dir)
        ecdict_path = work / "ecdict.db"
        with zipfile.ZipFile(ecdict_archive) as archive:
            databases = [item for item in archive.infolist()
                         if not item.is_dir() and item.filename.lower().endswith((".db", ".sqlite", ".sqlite3"))]
            if not databases:
                raise ValueError("The ECDICT archive does not contain a SQLite database")
            member = max(databases, key=lambda item: item.file_size)
            # Extract only the chosen file to a fixed path, never paths from the archive.
            with archive.open(member) as source, ecdict_path.open("wb") as destination:
                shutil.copyfileobj(source, destination)
        with closing(sqlite3.connect(ecdict_path)) as connection:
            connection.execute("CREATE INDEX IF NOT EXISTS pandanci_word_nocase ON stardict(word COLLATE NOCASE)")
            connection.commit()
            assert connection.execute("PRAGMA quick_check").fetchone()[0] == "ok"
        index_path = work / "offline-index.db"
        counts, cedict_headers = build_index(ecdict_path, cedict_gzip, index_path)
        manifest = {
            "installed_at_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
            "index_version": 1,
            "counts": counts,
            "sources": [
                {"name": "ECDICT", "version": "1.0.28", "license": "MIT (upstream repository)",
                 "url": "https://github.com/skywind3000/ECDICT/releases/download/1.0.28/ecdict-sqlite-28.zip",
                 "sha256": sha256(ecdict_archive)},
                {"name": "CC-CEDICT", "license": "CC BY-SA 4.0",
                 "url": "https://www.mdbg.net/chinese/export/cedict/cedict_1_0_ts_utf-8_mdbg.txt.gz",
                 "sha256": sha256(cedict_gzip)},
            ],
        }
        # All indexing/validation finishes before replacing installed managed files.
        ecdict_path.replace(output_dir / "ecdict.db")
        index_path.replace(output_dir / "offline-index.db")
        (output_dir / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        (output_dir / "licenses" / "CC-CEDICT-header.txt").write_text("\n".join(cedict_headers) + "\n", encoding="utf-8")
        print(json.dumps(counts), flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ecdict-archive", required=True, type=Path)
    parser.add_argument("--cedict-gzip", required=True, type=Path)
    parser.add_argument("--output-dir", required=True, type=Path)
    args = parser.parse_args()
    install(args.ecdict_archive, args.cedict_gzip, args.output_dir)
