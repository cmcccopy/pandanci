using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Text;

namespace PandanciClone
{
    internal sealed class DictionaryService
    {
        private readonly string _baseDir;
        private readonly Dictionary<string, DictEntry> _userDict = new Dictionary<string, DictEntry>(StringComparer.OrdinalIgnoreCase);

        public DictionaryService(string baseDir)
        {
            _baseDir = baseDir;
            LoadUserDict();
        }

        public DictEntry Lookup(string word)
        {
            if (string.IsNullOrWhiteSpace(word)) return null;
            List<DictEntry> entries = LookupAll(word);
            return entries.Count > 0 ? entries[0] : null;
        }

        public List<DictEntry> LookupAll(string word)
        {
            List<DictEntry> entries = new List<DictEntry>();
            if (string.IsNullOrWhiteSpace(word)) return entries;

            DictEntry entry;
            if (_userDict.TryGetValue(word.Trim(), out entry)) entries.Add(entry);

            entry = LookupDictDb(Path.Combine(_baseDir, "dict.db"), word);
            if (entry != null) entries.Add(entry);

            entry = LookupDictionaryDb(Path.Combine(_baseDir, "Dictionary.db"), word);
            if (entry != null) entries.Add(entry);

            return entries;
        }

        private void LoadUserDict()
        {
            string path = Path.Combine(_baseDir, "user-dict.txt");
            if (!File.Exists(path)) return;

            string[] lines = File.ReadAllLines(path, Encoding.Default);
            for (int i = 0; i < lines.Length; i++)
            {
                string first = lines[i].Trim();
                if (first.Length == 0 || first.StartsWith("#")) continue;
                if (i + 1 >= lines.Length) break;

                string phonograph = lines[++i].Trim();
                StringBuilder body = new StringBuilder();
                while (i + 1 < lines.Length)
                {
                    string next = lines[++i];
                    if (next.Trim() == "===") break;
                    body.AppendLine(next);
                }

                _userDict[first] = new DictEntry(first, phonograph, body.ToString().Trim(), "user-dict.txt");
            }
        }

        private static DictEntry LookupDictDb(string path, string word)
        {
            if (!File.Exists(path)) return null;
            using (SQLiteConnection conn = new SQLiteConnection("Data Source=" + path + ";Read Only=True;Version=3;"))
            {
                conn.Open();
                using (SQLiteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "select wordtext, phonograph, paraphrase, notes from words where lower(wordtext)=lower(@word) limit 1";
                    cmd.Parameters.AddWithValue("@word", word.Trim());
                    using (SQLiteDataReader r = cmd.ExecuteReader())
                    {
                        if (!r.Read()) return null;
                        string text = Convert.ToString(r["wordtext"]);
                        string phonograph = Convert.ToString(r["phonograph"]);
                        string paraphrase = Convert.ToString(r["paraphrase"]);
                        string notes = Convert.ToString(r["notes"]);
                        if (!string.IsNullOrWhiteSpace(notes)) paraphrase = paraphrase + Environment.NewLine + notes;
                        return new DictEntry(text, phonograph, paraphrase, "dict.db");
                    }
                }
            }
        }

        private static DictEntry LookupDictionaryDb(string path, string word)
        {
            if (!File.Exists(path)) return null;
            using (SQLiteConnection conn = new SQLiteConnection("Data Source=" + path + ";Read Only=True;Version=3;"))
            {
                conn.Open();
                using (SQLiteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "select word, wordtype, definition from entries where lower(word)=lower(@word) order by wordtype";
                    cmd.Parameters.AddWithValue("@word", word.Trim());
                    using (SQLiteDataReader r = cmd.ExecuteReader())
                    {
                        StringBuilder body = new StringBuilder();
                        string actual = word;
                        while (r.Read())
                        {
                            actual = Convert.ToString(r["word"]);
                            string type = Convert.ToString(r["wordtype"]);
                            string definition = Convert.ToString(r["definition"]);
                            body.Append(type).Append(" ").AppendLine(definition);
                        }
                        if (body.Length == 0) return null;
                        return new DictEntry(actual, "", body.ToString().Trim(), "Dictionary.db");
                    }
                }
            }
        }
    }

    internal sealed class DictEntry
    {
        public string Word;
        public string Phonograph;
        public string Definition;
        public string Source;

        public DictEntry(string word, string phonograph, string definition, string source)
        {
            Word = word;
            Phonograph = phonograph;
            Definition = definition;
            Source = source;
        }
    }
}
