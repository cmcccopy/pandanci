using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace PandanciClone
{
    internal sealed class DictionaryService
    {
        private readonly string _baseDir;
        private readonly Dictionary<string, DictEntry> _userDict = new Dictionary<string, DictEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly object _cacheLock = new object();
        private readonly Dictionary<string, List<DictEntry>> _cache = new Dictionary<string, List<DictEntry>>(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<string> _cacheOrder = new Queue<string>();

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
            word = NormalizeQuery(word);
            if (word.Length == 0 || word.Length > 160) return entries;
            lock (_cacheLock)
            {
                List<DictEntry> cached;
                if (_cache.TryGetValue(word, out cached)) return new List<DictEntry>(cached);
            }

            DictEntry entry;
            if (_userDict.TryGetValue(word, out entry)) entries.Add(entry);

            AddLegacy(entries, delegate { return LookupDictDb(Path.Combine(_baseDir, "dict.db"), word); });
            AddLegacy(entries, delegate { return LookupDictionaryDb(Path.Combine(_baseDir, "Dictionary.db"), word); });
            try { LookupInstalled(word, entries); }
            catch (SQLiteException ex)
            {
                entries.Add(new DictEntry(word, "", "扩展词典读取失败：" + ex.Message, "词典错误"));
            }
            lock (_cacheLock)
            {
                if (!_cache.ContainsKey(word)) _cacheOrder.Enqueue(word);
                _cache[word] = new List<DictEntry>(entries);
                while (_cache.Count > 256 && _cacheOrder.Count > 0) _cache.Remove(_cacheOrder.Dequeue());
            }
            return entries;
        }

        private static void AddLegacy(List<DictEntry> entries, Func<DictEntry> lookup)
        {
            try
            {
                DictEntry entry = lookup();
                if (entry != null) entries.Add(entry);
            }
            catch (SQLiteException ex) { entries.Add(new DictEntry("", "", ex.Message, "词典错误")); }
        }

        private static string NormalizeQuery(string query)
        {
            return (query ?? "").Normalize(NormalizationForm.FormKC).Trim(' ', '\r', '\n', '\t', '.', ',', ';', ':', '!', '?',
                '"', '\'', '“', '”', '‘', '’', '。', '，', '；', '：', '！', '？', '(', ')', '（', '）');
        }

        private static bool ContainsChinese(string text)
        {
            foreach (char c in text) if (c >= '\u3400' && c <= '\u9fff') return true;
            return false;
        }

        private static SQLiteConnection OpenDictionary(string path)
        {
            SQLiteConnectionStringBuilder options = new SQLiteConnectionStringBuilder();
            options.DataSource = path;
            options.ReadOnly = true;
            options.FailIfMissing = true;
            options.Pooling = false;
            SQLiteConnection connection = new SQLiteConnection(options.ToString());
            try { connection.Open(); return connection; }
            catch { connection.Dispose(); throw; }
        }

        private void LookupInstalled(string word, List<DictEntry> entries)
        {
            string directory = Path.Combine(_baseDir, "dictionaries");
            string ecdictPath = Path.Combine(directory, "ecdict.db");
            string indexPath = Path.Combine(directory, "offline-index.db");
            bool chinese = ContainsChinese(word);
            HashSet<string> englishWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!chinese) englishWords.Add(word);
            if (File.Exists(indexPath))
            {
                using (SQLiteConnection index = OpenDictionary(indexPath))
                {
                    string simplified = word;
                    using (SQLiteCommand command = index.CreateCommand())
                    {
                        if (chinese)
                        {
                            command.CommandText = "SELECT simplified,traditional,pinyin,definition FROM cedict WHERE simplified=@word OR traditional=@word ORDER BY length(definition) LIMIT 12";
                            command.Parameters.AddWithValue("@word", word);
                        }
                        else
                        {
                            // A quoted, tokenized MATCH query cannot inject FTS syntax.
                            string[] tokens = Regex.Matches(word.ToLowerInvariant(), "[a-z0-9]+")
                                .Count == 0 ? new string[0] : Regex.Split(word.ToLowerInvariant(), "[^a-z0-9]+");
                            StringBuilder phrase = new StringBuilder();
                            foreach (string token in tokens)
                            {
                                if (token.Length == 0) continue;
                                if (phrase.Length > 0) phrase.Append(' ');
                                phrase.Append(token);
                            }
                            if (phrase.Length > 0)
                            {
                                command.CommandText = "SELECT c.simplified,c.traditional,c.pinyin,c.definition FROM cedict_english f JOIN cedict c ON c.id=f.rowid WHERE cedict_english MATCH @word ORDER BY length(c.definition),length(c.simplified) LIMIT 5";
                                command.Parameters.AddWithValue("@word", "\"" + phrase + "\"");
                            }
                        }
                        if (!string.IsNullOrEmpty(command.CommandText))
                        using (SQLiteDataReader reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string simple = Convert.ToString(reader["simplified"]);
                                string traditional = Convert.ToString(reader["traditional"]);
                                if (chinese && simplified == word) simplified = simple;
                                string headword = simple == traditional ? simple : simple + " / " + traditional;
                                entries.Add(new DictEntry(headword, "[" + Convert.ToString(reader["pinyin"]) + "]", Convert.ToString(reader["definition"]),
                                    chinese ? "CC-CEDICT 汉英" : "CC-CEDICT 英文释义反查"));
                            }
                        }
                    }
                    using (SQLiteCommand command = index.CreateCommand())
                    {
                        command.CommandText = chinese
                            ? "SELECT word FROM chinese_terms WHERE term=@word ORDER BY rank,word LIMIT 10"
                            : "SELECT DISTINCT word FROM word_forms WHERE form=@word LIMIT 3";
                        command.Parameters.AddWithValue("@word", chinese ? simplified : word.ToLowerInvariant());
                        using (SQLiteDataReader reader = command.ExecuteReader())
                            while (reader.Read()) englishWords.Add(Convert.ToString(reader[0]));
                    }
                }
            }
            if (File.Exists(ecdictPath))
            {
                using (SQLiteConnection ecdict = OpenDictionary(ecdictPath))
                foreach (string englishWord in englishWords)
                {
                    using (SQLiteCommand command = ecdict.CreateCommand())
                    {
                        command.CommandText = "SELECT word,phonetic,definition,translation,exchange FROM stardict WHERE word=@word COLLATE NOCASE LIMIT 1";
                        command.Parameters.AddWithValue("@word", englishWord);
                        using (SQLiteDataReader reader = command.ExecuteReader())
                        {
                            if (!reader.Read()) continue;
                            string translation = Convert.ToString(reader["translation"]).Replace("\\n", Environment.NewLine);
                            string definition = Convert.ToString(reader["definition"]).Replace("\\n", Environment.NewLine);
                            string exchange = Convert.ToString(reader["exchange"]);
                            string body = translation + (definition.Length == 0 ? "" : Environment.NewLine + "英义：" + definition)
                                + (exchange.Length == 0 ? "" : Environment.NewLine + "词形：" + exchange);
                            string source = chinese ? "ECDICT 中文释义反查" : (string.Equals(englishWord, word, StringComparison.OrdinalIgnoreCase) ? "ECDICT 英汉" : "ECDICT 词形 → " + englishWord);
                            entries.Add(new DictEntry(Convert.ToString(reader["word"]), Convert.ToString(reader["phonetic"]), body.Trim(), source));
                        }
                    }
                }
            }
        }

        public TranslationResult LookupText(string text, TranslationLanguageMode languageMode, CancellationToken cancellationToken)
        {
            TranslationResult result = new TranslationResult();
            result.Provider = "LocalDictionary";
            result.SourceText = text ?? "";
            bool chinese = ContainsChinese(result.SourceText);
            result.TargetLanguage = chinese ? "en" : "zh-CN";
            result.DirectionText = chinese ? "本地汉英词典" : "本地英汉词典";
            cancellationToken.ThrowIfCancellationRequested();
            string query = NormalizeQuery(result.SourceText);
            List<DictEntry> entries = LookupAll(query);
            bool segmented = false;
            if (entries.Count == 0 && query.Length > 0)
            {
                segmented = true;
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (chinese)
                {
                    string indexPath = Path.Combine(_baseDir, "dictionaries", "offline-index.db");
                    if (File.Exists(indexPath))
                    using (SQLiteConnection index = OpenDictionary(indexPath))
                    {
                        for (int position = 0; position < Math.Min(240, query.Length) && seen.Count < 12; )
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (query[position] < '\u3400' || query[position] > '\u9fff') { position++; continue; }
                            string match = FindLongestChineseWord(index, query, position);
                            if (match == null) { position++; continue; }
                            if (seen.Add(match)) entries.AddRange(LookupAll(match));
                            position += match.Length;
                        }
                    }
                }
                else
                {
                    foreach (Match match in Regex.Matches(query, "[A-Za-z]+(?:['-][A-Za-z]+)*"))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!seen.Add(match.Value)) continue;
                        entries.AddRange(LookupAll(match.Value));
                        if (seen.Count >= 12) break;
                    }
                }
            }
            if (entries.Count == 0)
            {
                result.Error = "本地词典未收录该词。可尝试单词或较短词组；本地词典不执行整句机器翻译。";
                if (!File.Exists(Path.Combine(_baseDir, "dictionaries", "offline-index.db"))) result.Error += " 扩展词库未安装，请运行 scripts/install-dictionaries.ps1。";
                return result;
            }
            StringBuilder output = new StringBuilder();
            if (segmented) output.AppendLine("按词/词组查找（不是整句机器翻译）：").AppendLine();
            foreach (DictEntry entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (output.Length > 32000) { output.AppendLine("更多释义请缩小查询范围。"); break; }
                if (output.Length > 0) output.AppendLine();
                output.AppendLine("[" + entry.Source + "]");
                output.Append(entry.Word);
                if (!string.IsNullOrWhiteSpace(entry.Phonograph)) output.Append(" ").Append(entry.Phonograph);
                output.AppendLine().AppendLine(entry.Definition);
            }
            result.TranslatedText = output.ToString().Trim();
            return result;
        }

        private static string FindLongestChineseWord(SQLiteConnection index, string query, int position)
        {
            using (SQLiteCommand command = index.CreateCommand())
            {
                List<string> parameters = new List<string>();
                for (int length = 1; length <= Math.Min(16, query.Length - position); length++)
                {
                    string name = "@w" + length;
                    parameters.Add(name);
                    command.Parameters.AddWithValue(name, query.Substring(position, length));
                }
                string candidates = string.Join(",", parameters.ToArray());
                command.CommandText = "SELECT simplified,traditional FROM cedict WHERE simplified IN (" + candidates + ") OR traditional IN (" + candidates + ") ORDER BY length(simplified) DESC LIMIT 1";
                using (SQLiteDataReader reader = command.ExecuteReader())
                {
                    if (!reader.Read()) return null;
                    string simple = Convert.ToString(reader[0]);
                    return query.IndexOf(simple, position, StringComparison.Ordinal) == position ? simple : Convert.ToString(reader[1]);
                }
            }
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
