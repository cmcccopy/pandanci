using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace PandanciClone
{
    internal sealed class MainForm : Form
    {
        private readonly List<WordCard> _cards = new List<WordCard>();
        private readonly List<TextNote> _notes = new List<TextNote>();
        private readonly List<ArrowItem> _arrows = new List<ArrowItem>();
        private readonly List<RawItem> _rawItems = new List<RawItem>();
        private readonly DictionaryService _dictionary;

        private string _currentFile;
        private MapPanel _map;
        private MiniMapPanel _miniMap;
        private TextBox _definitionBox;
        private ListBox _historyBox;
        private TextBox _searchBox;
        private Label _statsLabel;
        private SplitContainer _rootSplit;

        private WordCard _selectedCard;
        private TextNote _selectedNote;
        private WordCard _linkStartCard;
        private WordCard _dragCard;
        private TextNote _dragNote;
        private Point _dragStartMouse;
        private Point _dragStartLocation;
        private bool _dragMoved;
        private bool _panning;
        private Point _panStartClient;
        private Point _panStartScroll;

        public MainForm()
        {
            _currentFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wordmap.wordmap");
            _dictionary = new DictionaryService(AppDomain.CurrentDomain.BaseDirectory);
            BuildUi();

            string config = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.txt");
            if (File.Exists(config))
            {
                string configured = File.ReadAllText(config, Encoding.Default).Trim();
                if (configured.Length > 0) _currentFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configured);
            }

            if (File.Exists(_currentFile)) LoadWordMap(_currentFile);
            else LoadWordMap(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "blank.wordmap"));
        }

        private void BuildUi()
        {
            Text = "盘单词";
            Width = 1280;
            Height = 820;
            Font = new Font("Microsoft YaHei UI", 9F);

            MenuStrip menu = new MenuStrip();
            ToolStripMenuItem file = new ToolStripMenuItem("文件");
            file.DropDownItems.Add("打开", null, OnOpen);
            file.DropDownItems.Add("保存", null, delegate { SaveCurrentFile(); });
            file.DropDownItems.Add("另存为", null, OnSaveAs);
            file.DropDownItems.Add("退出", null, delegate { Close(); });
            menu.Items.Add(file);
            menu.Items.Add(new ToolStripMenuItem("帮助", null, delegate
            {
                MessageBox.Show("右键画布可添加单词或笔记；右键单词可复习、查词、关联或删除。\r\n双击单词查词；空白处按住左键可拖动画布。\r\nCtrl+L：先选中起点单词，再选中目标单词，建立关联。\r\nCtrl+Shift+L：删除当前选中单词的所有关联线。\r\nCtrl+S：保存当前单词图。", "帮助");
            }));
            Controls.Add(menu);
            MainMenuStrip = menu;

            FlowLayoutPanel toolbar = new FlowLayoutPanel();
            toolbar.Dock = DockStyle.Top;
            toolbar.Height = 46;
            toolbar.Padding = new Padding(8, 8, 8, 4);
            toolbar.WrapContents = false;

            AddToolbarButton(toolbar, "下个复习", delegate { SelectNextDue(); });
            AddToolbarButton(toolbar, "统计", delegate { UpdateStats(); MessageBox.Show(_statsLabel.Text, "统计"); });
            AddToolbarButton(toolbar, "查词", delegate { LookupSelected(); });
            AddToolbarButton(toolbar, "已记住", delegate { MarkSelected(true); });
            AddToolbarButton(toolbar, "未记住", delegate { MarkSelected(false); });

            _statsLabel = new Label();
            _statsLabel.AutoSize = true;
            _statsLabel.Padding = new Padding(12, 8, 0, 0);
            toolbar.Controls.Add(_statsLabel);
            Controls.Add(toolbar);

            SplitContainer root = new SplitContainer();
            _rootSplit = root;
            root.Dock = DockStyle.Fill;
            root.FixedPanel = FixedPanel.Panel1;
            root.Panel1MinSize = 110;
            root.SplitterWidth = 4;
            root.SplitterMoved += delegate { ClampLeftPanelWidth(); };
            Controls.Add(root);
            root.BringToFront();

            Panel left = new Panel();
            left.Dock = DockStyle.Fill;
            root.Panel1.Controls.Add(left);

            Label historyLabel = new Label();
            historyLabel.Text = "搜索结果";
            historyLabel.Dock = DockStyle.Top;
            historyLabel.Height = 24;
            left.Controls.Add(historyLabel);

            _historyBox = new ListBox();
            _historyBox.Dock = DockStyle.Top;
            _historyBox.Height = 120;
            _historyBox.MouseClick += OnSearchResultClick;
            _historyBox.DoubleClick += delegate { SelectWord(Convert.ToString(_historyBox.SelectedItem)); };
            left.Controls.Add(_historyBox);

            Label searchLabel = new Label();
            searchLabel.Text = "搜索";
            searchLabel.Dock = DockStyle.Top;
            searchLabel.Height = 24;
            left.Controls.Add(searchLabel);

            _searchBox = new TextBox();
            _searchBox.Dock = DockStyle.Top;
            _searchBox.KeyDown += OnSearchKeyDown;
            _searchBox.TextChanged += delegate { UpdateSearchResults(_searchBox.Text.Trim()); };
            left.Controls.Add(_searchBox);

            _definitionBox = new TextBox();
            _definitionBox.Dock = DockStyle.Fill;
            _definitionBox.Multiline = true;
            _definitionBox.ScrollBars = ScrollBars.Vertical;
            _definitionBox.ReadOnly = true;
            left.Controls.Add(_definitionBox);
            _definitionBox.BringToFront();

            Panel right = new Panel();
            right.Dock = DockStyle.Fill;
            root.Panel2.Controls.Add(right);

            _miniMap = new MiniMapPanel();
            _miniMap.Dock = DockStyle.Top;
            _miniMap.Height = 92;
            _miniMap.Cards = _cards;
            _miniMap.Notes = _notes;
            _miniMap.Arrows = _arrows;
            _miniMap.BackColor = Color.White;
            _miniMap.MouseDown += OnMiniMapMouseDown;
            right.Controls.Add(_miniMap);

            _map = new MapPanel();
            _map.Dock = DockStyle.Fill;
            _map.AutoScroll = true;
            _map.BackColor = Color.White;
            _map.Cards = _cards;
            _map.Notes = _notes;
            _map.Arrows = _arrows;
            _map.MouseDown += OnMapMouseDown;
            _map.MouseMove += OnMapMouseMove;
            _map.MouseUp += OnMapMouseUp;
            _map.MouseDoubleClick += OnMapMouseDoubleClick;
            _map.Scroll += delegate { RefreshMapViews(true); };
            _map.Resize += delegate { ClampLeftPanelWidth(); UpdateCanvasSize(); RefreshMapViews(true); };
            right.Controls.Add(_map);
            _miniMap.BringToFront();
            Shown += delegate { ClampLeftPanelWidth(); };
        }

        private static void AddToolbarButton(FlowLayoutPanel toolbar, string text, EventHandler click)
        {
            Button button = new Button();
            button.Text = text;
            button.AutoSize = true;
            button.Click += click;
            toolbar.Controls.Add(button);
        }

        private void ClampLeftPanelWidth()
        {
            if (_rootSplit == null || _rootSplit.Width <= 0) return;
            int desired = 250;
            int minRight = 300;
            int max = Math.Max(_rootSplit.Panel1MinSize, _rootSplit.Width - minRight - _rootSplit.SplitterWidth);
            int target = Math.Max(_rootSplit.Panel1MinSize, Math.Min(desired, max));
            if (_rootSplit.Width > target + _rootSplit.SplitterWidth && _rootSplit.SplitterDistance != target)
            {
                _rootSplit.SplitterDistance = target;
            }
        }

        private void LoadWordMap(string path)
        {
            _cards.Clear();
            _notes.Clear();
            _arrows.Clear();
            _rawItems.Clear();
            _selectedCard = null;
            _selectedNote = null;
            _currentFile = path;

            if (!File.Exists(path)) return;
            string[] lines = File.ReadAllLines(path, Encoding.Default);
            foreach (string line in lines)
            {
                if (line.StartsWith("WLBWordCard|"))
                {
                    WordCard c = WordCard.Parse(line);
                    if (c != null) _cards.Add(c);
                }
                else if (line.StartsWith("WLBTextNotes|"))
                {
                    TextNote n = TextNote.Parse(line);
                    if (n != null) _notes.Add(n);
                }
                else if (line.StartsWith("WLBArrow|"))
                {
                    ArrowItem a = ArrowItem.Parse(line);
                    if (a != null) _arrows.Add(a);
                    else _rawItems.Add(new RawItem(line));
                }
                else if (line.Trim().Length > 0)
                {
                    _rawItems.Add(new RawItem(line));
                }
            }

            UpdateCanvasSize();
            UpdateStats();
            Text = "盘单词  " + path;
            RefreshMapViews();
        }

        private void SaveWordMap(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                OnSaveAs(this, EventArgs.Empty);
                return;
            }

            List<string> lines = new List<string>();
            foreach (WordCard c in _cards) lines.Add(c.ToLine());
            foreach (TextNote n in _notes) lines.Add(n.ToLine());
            foreach (ArrowItem a in _arrows) lines.Add(a.ToLine());
            foreach (RawItem item in _rawItems) lines.Add(item.Line);
            File.WriteAllLines(path, lines.ToArray(), Encoding.Default);
            _currentFile = path;
            Text = "盘单词  " + path;
            WriteCurrentFileToConfig(path);
        }

        private void SaveCurrentFile()
        {
            if (string.IsNullOrEmpty(_currentFile))
            {
                _currentFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wordmap.wordmap");
            }
            SaveWordMap(_currentFile);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.S))
            {
                SaveCurrentFile();
                return true;
            }
            if (keyData == (Keys.Control | Keys.L))
            {
                StartOrFinishLink();
                return true;
            }
            if (keyData == (Keys.Control | Keys.Shift | Keys.L))
            {
                DeleteSelectedLinks();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void WriteCurrentFileToConfig(string path)
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string full = Path.GetFullPath(path);
                string value = string.Equals(Path.GetDirectoryName(full).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), baseDir, StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFileName(full)
                    : full;
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.txt"), value, Encoding.Default);
            }
            catch
            {
                // Saving the map is the important part; config persistence is best-effort.
            }
        }

        private void OnMapMouseDown(object sender, MouseEventArgs e)
        {
            Point p = PointToMap(e.Location);
            WordCard card = HitCard(p);
            TextNote note = card == null ? HitNote(p) : null;

            if (e.Button == MouseButtons.Right)
            {
                SelectItem(card, note);
                ShowContextMenu(card, note, e.Location);
                return;
            }

            if (e.Button != MouseButtons.Left) return;
            _map.Capture = true;
            SelectItem(card, note);
            _dragCard = card;
            _dragNote = note;
            _dragStartMouse = p;
            _dragMoved = false;
            if (card != null) _dragStartLocation = new Point(card.X, card.Y);
            if (note != null) _dragStartLocation = new Point(note.X, note.Y);
            if (card == null && note == null)
            {
                _panning = true;
                _panStartClient = e.Location;
                _panStartScroll = new Point(-_map.AutoScrollPosition.X, -_map.AutoScrollPosition.Y);
                _map.Cursor = Cursors.SizeAll;
            }
        }

        private void OnMapMouseMove(object sender, MouseEventArgs e)
        {
            if ((_dragCard != null || _dragNote != null) && e.Button == MouseButtons.Left)
            {
                AutoScrollWhileDragging(e.Location);
            }

            if (_panning && e.Button == MouseButtons.Left)
            {
                int panDx = e.Location.X - _panStartClient.X;
                int panDy = e.Location.Y - _panStartClient.Y;
                _map.AutoScrollPosition = new Point(Math.Max(0, _panStartScroll.X - panDx), Math.Max(0, _panStartScroll.Y - panDy));
                RefreshMapViews(false);
                return;
            }

            if (_dragCard == null && _dragNote == null) return;
            if (e.Button != MouseButtons.Left) return;

            Point p = PointToMap(e.Location);
            int dx = p.X - _dragStartMouse.X;
            int dy = p.Y - _dragStartMouse.Y;
            if (Math.Abs(dx) + Math.Abs(dy) > 2) _dragMoved = true;

            int x = _dragStartLocation.X + dx;
            int y = _dragStartLocation.Y + dy;

            Rectangle oldBounds = GetDraggedBounds();
            if (_dragCard != null) MoveCard(_dragCard, x, y);
            if (_dragNote != null)
            {
                _dragNote.X = x;
                _dragNote.Y = y;
            }

            Rectangle newBounds = GetDraggedBounds();
            oldBounds.Inflate(80, 80);
            newBounds.Inflate(80, 80);
            _map.Invalidate(ToClientRect(Rectangle.Union(oldBounds, newBounds)));
        }

        private void OnMapMouseUp(object sender, MouseEventArgs e)
        {
            WordCard clickedCard = _dragCard;
            if (_panning)
            {
                _panning = false;
                _map.Cursor = Cursors.Default;
                RefreshMapViews();
            }
            _dragCard = null;
            _dragNote = null;
            _map.Capture = false;
            if (_dragMoved)
            {
                _dragMoved = false;
                UpdateCanvasSize();
                RefreshMapViews();
            }
        }

        private void OnMapMouseDoubleClick(object sender, MouseEventArgs e)
        {
            Point p = PointToMap(e.Location);
            WordCard card = HitCard(p);
            if (card != null)
            {
                SelectItem(card, null);
                LookupSelected();
                return;
            }

            TextNote note = HitNote(p);
            if (note != null) EditNote(note);
        }

        private void ShowContextMenu(WordCard card, TextNote note, Point screenPoint)
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            if (card != null)
            {
                menu.Items.Add("复习", null, delegate { SelectItem(card, null); MarkSelected(true); });
                menu.Items.Add("查词", null, delegate { SelectItem(card, null); LookupSelected(); });
                menu.Items.Add("从此单词开始关联", null, delegate { _linkStartCard = card; SelectItem(card, null); });
                menu.Items.Add("关联到此单词", null, delegate { LinkTo(card); });
                menu.Items.Add("已记住", null, delegate { SelectItem(card, null); MarkSelected(true); });
                menu.Items.Add("未记住", null, delegate { SelectItem(card, null); MarkSelected(false); });
                menu.Items.Add("删除", null, delegate { DeleteCard(card); });
            }
            else if (note != null)
            {
                menu.Items.Add("编辑笔记", null, delegate { EditNote(note); });
                menu.Items.Add("删除笔记", null, delegate { _notes.Remove(note); _selectedNote = null; UpdateCanvasSize(); RefreshMapViews(); });
            }
            else
            {
                Point p = PointToMap(screenPoint);
                menu.Items.Add("添加单词", null, delegate { AddWordAt(p); });
                menu.Items.Add("添加笔记", null, delegate { AddNoteAt(p); });
            }
            menu.Show(_map, screenPoint);
        }

        private WordCard HitCard(Point p)
        {
            for (int i = _cards.Count - 1; i >= 0; i--)
            {
                WordCard c = _cards[i];
                if (new Rectangle(c.X, c.Y, c.Width, c.Height).Contains(p)) return c;
            }
            return null;
        }

        private Rectangle GetDraggedBounds()
        {
            if (_dragCard != null) return new Rectangle(_dragCard.X, _dragCard.Y, _dragCard.Width, _dragCard.Height);
            if (_dragNote != null) return new Rectangle(_dragNote.X, _dragNote.Y, _dragNote.Width, _dragNote.Height);
            return Rectangle.Empty;
        }

        private Rectangle ToClientRect(Rectangle mapRect)
        {
            Point offset = _map.AutoScrollPosition;
            return new Rectangle(mapRect.X + offset.X + _map.OriginOffset.X, mapRect.Y + offset.Y + _map.OriginOffset.Y, mapRect.Width, mapRect.Height);
        }

        private TextNote HitNote(Point p)
        {
            for (int i = _notes.Count - 1; i >= 0; i--)
            {
                TextNote n = _notes[i];
                if (new Rectangle(n.X, n.Y, n.Width, n.Height).Contains(p)) return n;
            }
            return null;
        }

        private void SelectItem(WordCard card, TextNote note)
        {
            _selectedCard = card;
            _selectedNote = note;
            _map.SelectedCard = card;
            _map.SelectedNote = note;
            RefreshMapViews();
        }

        private void LookupSelected()
        {
            if (_selectedCard == null)
            {
                if (_searchBox.Text.Trim().Length > 0) LookupWord(_searchBox.Text.Trim());
                return;
            }
            LookupWord(_selectedCard.Word);
        }

        private void LookupWord(string word)
        {
            List<DictEntry> entries = _dictionary.LookupAll(word);
            if (entries.Count == 0)
            {
                _definitionBox.Text = word + Environment.NewLine + "本地词典未找到该单词。";
            }
            else
            {
                StringBuilder text = new StringBuilder();
                foreach (DictEntry entry in entries)
                {
                    if (text.Length > 0) text.AppendLine().AppendLine();
                    text.AppendLine("[" + entry.Source + "]");
                    text.Append(entry.Word);
                    if (!string.IsNullOrWhiteSpace(entry.Phonograph)) text.Append(" ").Append(entry.Phonograph);
                    text.AppendLine();
                    text.AppendLine(entry.Definition);
                }
                _definitionBox.Text = text.ToString();
            }
        }

        private void MarkSelected(bool remembered)
        {
            if (_selectedCard == null) return;
            _selectedCard.MarkReviewed(remembered);
            UpdateStats();
            RefreshMapViews();
        }

        private void UpdateStats()
        {
            int due = 0;
            int newWords = 0;
            foreach (WordCard c in _cards)
            {
                if (c.Due) due++;
                if (c.LastReview == DateTime.MinValue) newWords++;
            }
            _statsLabel.Text = "总数: " + _cards.Count + "  到期: " + due + "  未复习: " + newWords + "  其他: " + Math.Max(0, _cards.Count - due - newWords);
        }

        private void SelectNextDue()
        {
            foreach (WordCard c in _cards)
            {
                if (c.Due)
                {
                    SelectItem(c, null);
                    ScrollTo(c.X, c.Y);
                    return;
                }
            }
            MessageBox.Show("当前没有到期需要复习的单词。", "复习");
        }

        private void SelectWord(string word)
        {
            if (string.IsNullOrEmpty(word)) return;
            string clean = ExtractWord(word);
            foreach (WordCard c in _cards)
            {
                if (string.Equals(c.Word, clean, StringComparison.OrdinalIgnoreCase))
                {
                    SelectItem(c, null);
                    ScrollTo(c.X, c.Y);
                    LookupWord(c.Word);
                    return;
                }
            }
            LookupWord(clean);
        }

        private void OnSearchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                string query = _searchBox.Text.Trim();
                UpdateSearchResults(query);
                SelectFirstSearchResult(query);
            }
        }

        private void OnSearchResultClick(object sender, MouseEventArgs e)
        {
            int index = _historyBox.IndexFromPoint(e.Location);
            if (index < 0) return;
            _historyBox.SelectedIndex = index;
            SelectWord(Convert.ToString(_historyBox.Items[index]));
        }

        private void OnMiniMapMouseDown(object sender, MouseEventArgs e)
        {
            Point target = _miniMap.ClientToMap(e.Location);
            ScrollToMapPoint(target.X, target.Y);
        }

        private void UpdateSearchResults(string query)
        {
            _historyBox.BeginUpdate();
            try
            {
                _historyBox.Items.Clear();
                if (string.IsNullOrEmpty(query)) return;
                int count = 0;
                foreach (WordCard c in _cards)
                {
                    if (c.Word.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _historyBox.Items.Add(c.Word);
                        count++;
                        if (count >= 500) break;
                    }
                }
            }
            finally
            {
                _historyBox.EndUpdate();
            }
        }

        private void SelectFirstSearchResult(string query)
        {
            if (_historyBox.Items.Count > 0)
            {
                _historyBox.SelectedIndex = 0;
                SelectWord(Convert.ToString(_historyBox.Items[0]));
            }
            else if (!string.IsNullOrWhiteSpace(query))
            {
                LookupWord(query);
            }
        }

        private static string ExtractWord(string listText)
        {
            if (string.IsNullOrEmpty(listText)) return "";
            int space = listText.IndexOf(' ');
            return space > 0 ? listText.Substring(0, space) : listText;
        }

        private void AddWordAt(Point p)
        {
            string word = Prompt.Show("单词", "添加单词");
            if (string.IsNullOrWhiteSpace(word)) return;
            WordCard c = new WordCard();
            c.Word = word.Trim();
            c.X = p.X;
            c.Y = p.Y;
            c.Width = Math.Max(50, TextRenderer.MeasureText(c.Word, Font).Width + 20);
            c.Height = 28;
            _cards.Add(c);
            SelectItem(c, null);
            UpdateCanvasSize();
            UpdateStats();
            RefreshMapViews();
        }

        private void AddNoteAt(Point p)
        {
            string text = Prompt.Show("笔记", "添加笔记");
            if (text == null) return;
            TextNote note = new TextNote();
            note.X = p.X;
            note.Y = p.Y;
            note.Width = 190;
            note.Height = 60;
            note.Text = string.IsNullOrWhiteSpace(text) ? "笔记" : text;
            _notes.Add(note);
            SelectItem(null, note);
            UpdateCanvasSize();
            RefreshMapViews();
        }

        private void EditNote(TextNote note)
        {
            string text = Prompt.Show("笔记", "编辑笔记", note.Text);
            if (text == null) return;
            note.Text = text;
            RefreshMapViews();
        }

        private void LinkTo(WordCard target)
        {
            if (_linkStartCard == null || target == null || _linkStartCard == target) return;
            if (HasLink(_linkStartCard, target))
            {
                _linkStartCard = null;
                return;
            }
            ArrowItem arrow = new ArrowItem();
            arrow.X1 = _linkStartCard.X + _linkStartCard.Width / 2;
            arrow.Y1 = _linkStartCard.Y + _linkStartCard.Height / 2;
            arrow.X2 = target.X + target.Width / 2;
            arrow.Y2 = target.Y + target.Height / 2;
            arrow.A = arrow.Y1;
            arrow.B = arrow.X1;
            arrow.C = 18;
            arrow.D = 15;
            _arrows.Add(arrow);
            _linkStartCard = null;
            if (_miniMap != null) _miniMap.RebuildCache();
            RefreshMapViews();
        }

        private void StartOrFinishLink()
        {
            if (_selectedCard == null) return;
            if (_linkStartCard == null || _linkStartCard == _selectedCard)
            {
                _linkStartCard = _selectedCard;
                return;
            }
            LinkTo(_selectedCard);
        }

        private void DeleteSelectedLinks()
        {
            if (_selectedCard == null) return;
            int cx = _selectedCard.X + _selectedCard.Width / 2;
            int cy = _selectedCard.Y + _selectedCard.Height / 2;
            int removed = _arrows.RemoveAll(delegate(ArrowItem a)
            {
                return (a.X1 == cx && a.Y1 == cy) || (a.X2 == cx && a.Y2 == cy);
            });
            if (removed > 0)
            {
                if (_miniMap != null) _miniMap.RebuildCache();
                RefreshMapViews();
            }
        }

        private bool HasLink(WordCard first, WordCard second)
        {
            int ax = first.X + first.Width / 2;
            int ay = first.Y + first.Height / 2;
            int bx = second.X + second.Width / 2;
            int by = second.Y + second.Height / 2;
            foreach (ArrowItem a in _arrows)
            {
                bool sameDirection = a.X1 == ax && a.Y1 == ay && a.X2 == bx && a.Y2 == by;
                bool reverseDirection = a.X1 == bx && a.Y1 == by && a.X2 == ax && a.Y2 == ay;
                if (sameDirection || reverseDirection) return true;
            }
            return false;
        }

        private void DeleteCard(WordCard card)
        {
            if (card == null) return;
            int cx = card.X + card.Width / 2;
            int cy = card.Y + card.Height / 2;
            _cards.Remove(card);
            _arrows.RemoveAll(delegate(ArrowItem a) { return (a.X1 == cx && a.Y1 == cy) || (a.X2 == cx && a.Y2 == cy); });
            if (_selectedCard == card) SelectItem(null, null);
            UpdateCanvasSize();
            UpdateStats();
            if (_miniMap != null) _miniMap.RebuildCache();
            RefreshMapViews();
        }

        private void MoveCard(WordCard card, int x, int y)
        {
            int oldCenterX = card.X + card.Width / 2;
            int oldCenterY = card.Y + card.Height / 2;
            card.X = x;
            card.Y = y;
            int newCenterX = card.X + card.Width / 2;
            int newCenterY = card.Y + card.Height / 2;
            foreach (ArrowItem a in _arrows)
            {
                if (a.X1 == oldCenterX && a.Y1 == oldCenterY)
                {
                    a.X1 = newCenterX;
                    a.Y1 = newCenterY;
                }
                if (a.X2 == oldCenterX && a.Y2 == oldCenterY)
                {
                    a.X2 = newCenterX;
                    a.Y2 = newCenterY;
                }
            }
        }

        private Point PointToMap(Point clientPoint)
        {
            return new Point(clientPoint.X - _map.AutoScrollPosition.X - _map.OriginOffset.X, clientPoint.Y - _map.AutoScrollPosition.Y - _map.OriginOffset.Y);
        }

        private void AutoScrollWhileDragging(Point clientPoint)
        {
            const int edge = 28;
            const int step = 32;
            int scrollX = -_map.AutoScrollPosition.X;
            int scrollY = -_map.AutoScrollPosition.Y;

            if (clientPoint.X < edge) scrollX = Math.Max(0, scrollX - step);
            else if (clientPoint.X > _map.ClientSize.Width - edge) scrollX += step;

            if (clientPoint.Y < edge) scrollY = Math.Max(0, scrollY - step);
            else if (clientPoint.Y > _map.ClientSize.Height - edge) scrollY += step;

            if (scrollX != -_map.AutoScrollPosition.X || scrollY != -_map.AutoScrollPosition.Y)
            {
                _map.AutoScrollPosition = new Point(scrollX, scrollY);
                RefreshMapViews(false);
            }
        }

        private void ScrollTo(int x, int y)
        {
            int targetX = Math.Max(0, x + _map.OriginOffset.X - _map.ClientSize.Width / 2);
            int targetY = Math.Max(0, y + _map.OriginOffset.Y - _map.ClientSize.Height / 2);
            _map.AutoScrollPosition = new Point(targetX, targetY);
            RefreshMapViews(true);
        }

        private void ScrollToMapPoint(int x, int y)
        {
            _map.AutoScrollPosition = new Point(Math.Max(0, x + _map.OriginOffset.X - _map.ClientSize.Width / 2), Math.Max(0, y + _map.OriginOffset.Y - _map.ClientSize.Height / 2));
            RefreshMapViews(true);
        }

        private void RefreshMapViews()
        {
            RefreshMapViews(true);
        }

        private void RefreshMapViews(bool updateMiniMap)
        {
            if (_map != null) _map.Invalidate();
            if (updateMiniMap && _miniMap != null && _map != null)
            {
                _miniMap.Viewport = new Rectangle(-_map.AutoScrollPosition.X - _map.OriginOffset.X, -_map.AutoScrollPosition.Y - _map.OriginOffset.Y, _map.ClientSize.Width, _map.ClientSize.Height);
                _miniMap.Invalidate();
            }
        }

        private void UpdateCanvasSize()
        {
            const int margin = 160;
            int minX = 0;
            int minY = 0;
            int maxX = _map.ClientSize.Width;
            int maxY = _map.ClientSize.Height;

            foreach (WordCard c in _cards)
            {
                minX = Math.Min(minX, c.X);
                minY = Math.Min(minY, c.Y);
                maxX = Math.Max(maxX, c.X + c.Width);
                maxY = Math.Max(maxY, c.Y + c.Height);
            }
            foreach (TextNote n in _notes)
            {
                minX = Math.Min(minX, n.X);
                minY = Math.Min(minY, n.Y);
                maxX = Math.Max(maxX, n.X + n.Width);
                maxY = Math.Max(maxY, n.Y + n.Height);
            }
            foreach (ArrowItem a in _arrows)
            {
                minX = Math.Min(minX, Math.Min(a.X1, a.X2));
                minY = Math.Min(minY, Math.Min(a.Y1, a.Y2));
                maxX = Math.Max(maxX, Math.Max(a.X1, a.X2));
                maxY = Math.Max(maxY, Math.Max(a.Y1, a.Y2));
            }

            Rectangle bounds = Rectangle.FromLTRB(minX - margin, minY - margin, maxX + margin, maxY + margin);
            _map.MapBounds = bounds;
            _map.OriginOffset = new Point(-bounds.Left, -bounds.Top);
            _map.AutoScrollMinSize = new Size(Math.Max(_map.ClientSize.Width, bounds.Width), Math.Max(_map.ClientSize.Height, bounds.Height));

            if (_miniMap != null)
            {
                _miniMap.MapBounds = bounds;
                _miniMap.MapSize = bounds.Size;
                _miniMap.Viewport = new Rectangle(-_map.AutoScrollPosition.X - _map.OriginOffset.X, -_map.AutoScrollPosition.Y - _map.OriginOffset.Y, _map.ClientSize.Width, _map.ClientSize.Height);
                _miniMap.RebuildCache();
            }
        }

        private void OnOpen(object sender, EventArgs e)
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Filter = "单词图 (*.wordmap)|*.wordmap|所有文件 (*.*)|*.*";
                dlg.InitialDirectory = AppDomain.CurrentDomain.BaseDirectory;
                if (dlg.ShowDialog(this) == DialogResult.OK) LoadWordMap(dlg.FileName);
            }
        }

        private void OnSaveAs(object sender, EventArgs e)
        {
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Filter = "单词图 (*.wordmap)|*.wordmap|所有文件 (*.*)|*.*";
                dlg.InitialDirectory = AppDomain.CurrentDomain.BaseDirectory;
                dlg.FileName = Path.GetFileName(_currentFile);
                if (dlg.ShowDialog(this) == DialogResult.OK) SaveWordMap(dlg.FileName);
            }
        }
    }

    internal sealed class MiniMapPanel : Panel
    {
        public List<WordCard> Cards;
        public List<TextNote> Notes;
        public List<ArrowItem> Arrows;
        public Size MapSize = new Size(1, 1);
        public Rectangle MapBounds = Rectangle.Empty;
        public Rectangle Viewport;
        private Bitmap _cache;
        private Size _cacheSize;
        private Size _cacheMapSize;
        private Rectangle _cacheMapBounds;

        public MiniMapPanel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public Point ClientToMap(Point p)
        {
            Rectangle area = GetMiniArea();
            if (area.Width <= 0 || area.Height <= 0) return Point.Empty;
            float scale = GetScale(area);
            int x = MapBounds.Left + (int)((p.X - area.X) / scale);
            int y = MapBounds.Top + (int)((p.Y - area.Y) / scale);
            return new Point(x, y);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.Clear(Color.White);
            Rectangle area = GetMiniArea();
            if (area.Width <= 0 || area.Height <= 0) return;

            float scale = GetScale(area);
            EnsureCache(area.Size, scale);
            if (_cache != null) e.Graphics.DrawImageUnscaled(_cache, area.Location);

            using (Pen viewPen = new Pen(Color.Red, 2F))
            {
                RectangleF view = new RectangleF(area.X + (Viewport.X - MapBounds.Left) * scale, area.Y + (Viewport.Y - MapBounds.Top) * scale, Viewport.Width * scale, Viewport.Height * scale);
                e.Graphics.DrawRectangle(viewPen, view.X, view.Y, view.Width, view.Height);
            }

            using (Pen border = new Pen(Color.Silver))
            {
                e.Graphics.DrawRectangle(border, area);
            }
        }

        public void RebuildCache()
        {
            if (_cache != null)
            {
                _cache.Dispose();
                _cache = null;
            }
            Invalidate();
        }

        private void EnsureCache(Size areaSize, float scale)
        {
            if (_cache != null && _cacheSize == areaSize && _cacheMapSize == MapSize && _cacheMapBounds == MapBounds) return;
            if (_cache != null) _cache.Dispose();

            _cacheSize = areaSize;
            _cacheMapSize = MapSize;
            _cacheMapBounds = MapBounds;
            _cache = new Bitmap(Math.Max(1, areaSize.Width), Math.Max(1, areaSize.Height));
            using (Graphics g = Graphics.FromImage(_cache))
            using (Brush background = new SolidBrush(Color.White))
            using (Pen cardPen = new Pen(Color.FromArgb(120, 145, 170)))
            using (Brush cardBrush = new SolidBrush(Color.FromArgb(210, 225, 240)))
            using (Brush noteBrush = new SolidBrush(Color.FromArgb(245, 225, 160)))
            using (Pen arrowPen = new Pen(Color.FromArgb(110, 140, 170), 1F))
            {
                g.FillRectangle(background, 0, 0, areaSize.Width, areaSize.Height);
                g.ScaleTransform(scale, scale);
                g.TranslateTransform(-MapBounds.Left, -MapBounds.Top);
                if (Arrows != null)
                {
                    foreach (ArrowItem a in Arrows) g.DrawLine(arrowPen, a.X1, a.Y1, a.X2, a.Y2);
                }
                if (Notes != null)
                {
                    foreach (TextNote n in Notes) g.FillRectangle(noteBrush, n.X, n.Y, n.Width, n.Height);
                }
                if (Cards != null)
                {
                    foreach (WordCard c in Cards)
                    {
                        Rectangle r = new Rectangle(c.X, c.Y, Math.Max(c.Width, 45), Math.Max(c.Height, 24));
                        g.FillRectangle(cardBrush, r);
                        g.DrawRectangle(cardPen, r);
                    }
                }
            }
        }

        private Rectangle GetMiniArea()
        {
            return new Rectangle(6, 6, Math.Max(1, ClientSize.Width - 12), Math.Max(1, ClientSize.Height - 12));
        }

        private float GetScale(Rectangle area)
        {
            int width = Math.Max(1, MapSize.Width);
            int height = Math.Max(1, MapSize.Height);
            return Math.Max(0.001F, Math.Min(area.Width / (float)width, area.Height / (float)height));
        }
    }

    internal sealed class MapPanel : Panel
    {
        public List<WordCard> Cards;
        public List<TextNote> Notes;
        public List<ArrowItem> Arrows;
        public WordCard SelectedCard;
        public TextNote SelectedNote;
        public Point OriginOffset { get; set; }
        public Rectangle MapBounds = Rectangle.Empty;

        public MapPanel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnScroll(ScrollEventArgs se)
        {
            base.OnScroll(se);
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Point offset = AutoScrollPosition;
            e.Graphics.TranslateTransform(offset.X + OriginOffset.X, offset.Y + OriginOffset.Y);
            Rectangle view = new Rectangle(-offset.X - OriginOffset.X, -offset.Y - OriginOffset.Y, ClientSize.Width, ClientSize.Height);

            DrawGrid(e.Graphics, view);
            DrawArrows(e.Graphics, view);
            DrawNotes(e.Graphics, view);
            DrawCards(e.Graphics, view);
            e.Graphics.ResetTransform();
        }

        private void DrawGrid(Graphics g, Rectangle view)
        {
            using (Pen pen = new Pen(Color.FromArgb(235, 235, 235)))
            {
                for (int x = (view.Left / 40) * 40; x < view.Right; x += 40) g.DrawLine(pen, x, view.Top, x, view.Bottom);
                for (int y = (view.Top / 40) * 40; y < view.Bottom; y += 40) g.DrawLine(pen, view.Left, y, view.Right, y);
            }
        }

        private void DrawArrows(Graphics g, Rectangle view)
        {
            if (Arrows == null) return;
            SmoothingMode oldMode = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(Color.SteelBlue, 2F))
            {
                pen.CustomEndCap = new AdjustableArrowCap(4, 6);
                foreach (ArrowItem a in Arrows)
                {
                    Rectangle bounds = Rectangle.FromLTRB(Math.Min(a.X1, a.X2), Math.Min(a.Y1, a.Y2), Math.Max(a.X1, a.X2) + 1, Math.Max(a.Y1, a.Y2) + 1);
                    bounds.Inflate(8, 8);
                    if (bounds.IntersectsWith(view)) g.DrawLine(pen, a.X1, a.Y1, a.X2, a.Y2);
                }
            }
            g.SmoothingMode = oldMode;
        }

        private void DrawNotes(Graphics g, Rectangle view)
        {
            if (Notes == null) return;
            using (Brush brush = new SolidBrush(Color.LemonChiffon))
            using (Pen pen = new Pen(Color.Goldenrod))
            using (Pen selectedPen = new Pen(Color.OrangeRed, 2F))
            using (Brush textBrush = new SolidBrush(Color.Black))
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = StringAlignment.Near;
                format.LineAlignment = StringAlignment.Near;
                format.Trimming = StringTrimming.EllipsisWord;
                foreach (TextNote note in Notes)
                {
                    Rectangle r = new Rectangle(note.X, note.Y, note.Width, note.Height);
                    if (!r.IntersectsWith(view)) continue;
                    g.FillRectangle(brush, r);
                    g.DrawRectangle(note == SelectedNote ? selectedPen : pen, r);
                    RectangleF textRect = new RectangleF(note.X + 4, note.Y + 4, Math.Max(1, note.Width - 8), Math.Max(1, note.Height - 8));
                    g.DrawString(note.Text, Font, textBrush, textRect, format);
                }
            }
        }

        private void DrawCards(Graphics g, Rectangle view)
        {
            if (Cards == null) return;
            using (Brush normal = new SolidBrush(Color.WhiteSmoke))
            using (Brush due = new SolidBrush(Color.MistyRose))
            using (Brush remembered = new SolidBrush(Color.Honeydew))
            using (Brush forgot = new SolidBrush(Color.LightYellow))
            using (Pen border = new Pen(Color.Gray))
            using (Pen selectedBorder = new Pen(Color.RoyalBlue, 2F))
            using (Brush textBrush = new SolidBrush(Color.Black))
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                format.Trimming = StringTrimming.EllipsisCharacter;
                format.FormatFlags = StringFormatFlags.NoWrap;
                foreach (WordCard card in Cards)
                {
                    Rectangle r = new Rectangle(card.X, card.Y, card.Width, card.Height);
                    if (!r.IntersectsWith(view)) continue;
                    Brush brush = GetCardBrush(card, normal, due, remembered, forgot);
                    g.FillRectangle(brush, r);
                    g.DrawRectangle(card == SelectedCard ? selectedBorder : border, r);
                    g.DrawString(card.Word, Font, textBrush, new RectangleF(r.X, r.Y + 1, r.Width, r.Height), format);
                }
            }
        }

        private static Brush GetCardBrush(WordCard card, Brush normal, Brush due, Brush remembered, Brush forgot)
        {
            if (card.Due) return due;
            if (card.LastReview == DateTime.MinValue) return normal;
            if (card.Score < 100) return forgot;
            return remembered;
        }
    }

    internal static class Prompt
    {
        public static string Show(string text, string caption)
        {
            return Show(text, caption, "");
        }

        public static string Show(string text, string caption, string defaultValue)
        {
            Form form = new Form();
            Label label = new Label();
            TextBox input = new TextBox();
            Button ok = new Button();
            Button cancel = new Button();

            form.Text = caption;
            label.Text = text;
            input.Left = 12;
            input.Top = 36;
            input.Width = 300;
            input.Text = defaultValue;
            label.Left = 12;
            label.Top = 12;
            label.AutoSize = true;
            ok.Text = "确定";
            ok.Left = 156;
            ok.Top = 72;
            ok.DialogResult = DialogResult.OK;
            cancel.Text = "取消";
            cancel.Left = 237;
            cancel.Top = 72;
            cancel.DialogResult = DialogResult.Cancel;
            form.ClientSize = new Size(326, 110);
            form.Controls.AddRange(new Control[] { label, input, ok, cancel });
            form.AcceptButton = ok;
            form.CancelButton = cancel;
            form.StartPosition = FormStartPosition.CenterParent;
            return form.ShowDialog() == DialogResult.OK ? input.Text : null;
        }
    }
}









