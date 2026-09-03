using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PandanciClone
{
    internal sealed class MainForm : Form
    {
        private readonly List<WordCard> _cards = new List<WordCard>();
        private readonly List<TextNote> _notes = new List<TextNote>();
        private readonly List<ImageItem> _images = new List<ImageItem>();
        private readonly List<ArrowItem> _arrows = new List<ArrowItem>();
        private readonly List<RawItem> _rawItems = new List<RawItem>();
        private readonly List<WordCard> _storedCards = new List<WordCard>();
        private readonly List<ImageItem> _storedImages = new List<ImageItem>();
        private readonly List<object> _storedItems = new List<object>();
        private readonly List<WordCard> _selectedCards = new List<WordCard>();
        private readonly List<TextNote> _selectedNotes = new List<TextNote>();
        private readonly Dictionary<WordCard, Point> _groupCardStarts = new Dictionary<WordCard, Point>();
        private readonly Dictionary<TextNote, Point> _groupNoteStarts = new Dictionary<TextNote, Point>();
        private readonly Dictionary<string, long> _syncWordTicks = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _syncWordSignatures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly DictionaryService _dictionary;
        private readonly SelectedTextService _selectedText;
        private readonly TranslationService _translation;
        private readonly AppSettings _settings;
        private readonly SemaphoreSlim _googleTranslationGate = new SemaphoreSlim(1, 1);
        private readonly object _googleTranslationCancellationLock = new object();

        private string _currentFile;
        private MapPanel _map;
        private MiniMapPanel _miniMap;
        private TextBox _definitionBox;
        private ListBox _historyBox;
        private TextBox _searchBox;
        private Label _statsLabel;
        private SplitContainer _rootSplit;
        private NotifyIcon _notifyIcon;
        private TranslationPopupForm _translationPopup;
        private LanSyncServer _lanSyncServer;
        private bool _allowExit;
        private bool _hotkeyRegistered;
        private bool _screenshotHotkeyRegistered;
        private int _translationRequestId;
        private CancellationTokenSource _googleTranslationCancellation;

        private WordCard _selectedCard;
        private TextNote _selectedNote;
        private ImageItem _selectedImage;
        private WordCard _linkStartCard;
        private WordCard _dragCard;
        private TextNote _dragNote;
        private ImageItem _dragImage;
        private TextNote _resizeNote;
        private ImageItem _resizeImage;
        private Point _dragStartMouse;
        private Point _dragStartLocation;
        private Point _lastMapMousePoint;
        private Point _selectionStart;
        private Rectangle _selectionRect;
        private Size _resizeStartSize;
        private bool _dragMoved;
        private bool _panning;
        private bool _boxSelecting;
        private bool _groupDragging;
        private Point _panStartClient;
        private Point _panStartScroll;

        private const int WmHotkey = 0x0312;
        private const int HotkeyTranslateSelection = 101;
        private const int HotkeyTranslateScreenshot = 102;
        private const uint ModAlt = 0x0001;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public MainForm()
        {
            _currentFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wordmap.wordmap");
            _dictionary = new DictionaryService(AppDomain.CurrentDomain.BaseDirectory);
            _selectedText = new SelectedTextService();
            _settings = AppSettings.Load(AppDomain.CurrentDomain.BaseDirectory);
            _translation = new TranslationService();
            _translation.GoogleProxyAddress = _settings.GoogleProxyAddress;
            BuildUi();
            BuildTray();

            string config = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.txt");
            if (File.Exists(config))
            {
                string configured = File.ReadAllText(config, Encoding.Default).Trim();
                if (configured.Length > 0) _currentFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configured);
            }

            if (File.Exists(_currentFile)) LoadWordMap(_currentFile);
            else LoadWordMap(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "blank.wordmap"));
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RegisterTranslateHotkey();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            UnregisterTranslateHotkey();
            base.OnHandleDestroyed(e);
        }

        private void BuildUi()
        {
            Text = "盘单词";
            Width = 1280;
            Height = 820;
            Font = new Font("Microsoft YaHei UI", 9F);

            TableLayoutPanel mainLayout = new TableLayoutPanel();
            mainLayout.Dock = DockStyle.Fill;
            mainLayout.ColumnCount = 1;
            mainLayout.RowCount = 3;
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            Controls.Add(mainLayout);

            MenuStrip menu = new MenuStrip();
            menu.Dock = DockStyle.Fill;
            menu.Margin = new Padding(0);
            ToolStripMenuItem file = new ToolStripMenuItem("文件");
            file.DropDownItems.Add("打开", null, OnOpen);
            file.DropDownItems.Add("保存", null, delegate { SaveCurrentFile(); });
            file.DropDownItems.Add("另存为", null, OnSaveAs);
            file.DropDownItems.Add("退出", null, delegate { Close(); });
            menu.Items.Add(file);
            ToolStripMenuItem settings = new ToolStripMenuItem("设置");
            settings.DropDownItems.Add("翻译代理...", null, OnSetTranslationProxy);
            settings.DropDownItems.Add("OCR 设置...", null, OnSetOcrSettings);
            ToolStripMenuItem lanSync = new ToolStripMenuItem("局域网同步");
            lanSync.DropDownItems.Add("开启同步主机", null, OnStartLanSyncServer);
            lanSync.DropDownItems.Add("停止同步主机", null, OnStopLanSyncServer);
            lanSync.DropDownItems.Add(new ToolStripSeparator());
            lanSync.DropDownItems.Add("连接同步主机...", null, OnConnectLanSyncHost);
            lanSync.DropDownItems.Add("立即同步", null, OnSyncNow);
            settings.DropDownItems.Add(lanSync);
            menu.Items.Add(settings);
            menu.Items.Add(new ToolStripMenuItem("帮助", null, delegate
            {
                MessageBox.Show("右键画布可添加单词、笔记或图片；右键单词可复习、查词、存储、关联或删除。\r\n双击单词查词；空白处按住左键可拖动画布；选中笔记或图片后拖右下角可调整大小。\r\nAlt+A：划词翻译。\r\nAlt+S：截屏 OCR 翻译。\r\nAlt+X：存储当前选中的单词或图片。\r\nAlt+V：释放最后存储的内容到鼠标当前位置。\r\nCtrl+V：剪贴板是图片时粘贴图片。\r\nCtrl+L：先选中起点单词，再选中目标单词，建立关联。\r\nCtrl+Shift+L：删除当前选中单词的所有关联线。\r\nCtrl+S：保存当前单词图。", "帮助");
            }));
            mainLayout.Controls.Add(menu, 0, 0);
            MainMenuStrip = menu;

            FlowLayoutPanel toolbar = new FlowLayoutPanel();
            toolbar.Dock = DockStyle.Fill;
            toolbar.Margin = new Padding(0);
            toolbar.Padding = new Padding(8, 8, 8, 4);
            toolbar.WrapContents = false;

            AddToolbarButton(toolbar, "下个复习", delegate { SelectNextDue(); });
            AddToolbarButton(toolbar, "统计", delegate { UpdateStats(); RefreshMapViews(); MessageBox.Show(_statsLabel.Text, "统计"); });
            AddToolbarButton(toolbar, "查词", delegate { LookupSelected(); });
            AddToolbarButton(toolbar, "已记住", delegate { MarkSelected(true); });
            AddToolbarButton(toolbar, "待学习", delegate { MarkSelected(false); });

            _statsLabel = new Label();
            _statsLabel.AutoSize = true;
            _statsLabel.Padding = new Padding(12, 8, 0, 0);
            toolbar.Controls.Add(_statsLabel);
            mainLayout.Controls.Add(toolbar, 0, 1);

            SplitContainer root = new SplitContainer();
            _rootSplit = root;
            root.Dock = DockStyle.Fill;
            root.Margin = new Padding(0);
            root.FixedPanel = FixedPanel.Panel1;
            root.Panel1MinSize = 110;
            root.SplitterWidth = 4;
            root.SplitterMoved += delegate { ClampLeftPanelWidth(); };
            mainLayout.Controls.Add(root, 0, 2);

            TableLayoutPanel left = new TableLayoutPanel();
            left.Dock = DockStyle.Fill;
            left.Margin = new Padding(0);
            left.ColumnCount = 1;
            left.RowCount = 6;
            left.Padding = new Padding(6, 4, 6, 6);
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 120F));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.Panel1.Controls.Add(left);

            Label searchLabel = new Label();
            searchLabel.Text = "搜索";
            searchLabel.Dock = DockStyle.Fill;
            searchLabel.TextAlign = ContentAlignment.MiddleLeft;
            left.Controls.Add(searchLabel, 0, 0);

            _searchBox = new TextBox();
            _searchBox.Dock = DockStyle.Fill;
            _searchBox.Margin = new Padding(0, 0, 0, 4);
            _searchBox.KeyDown += OnSearchKeyDown;
            _searchBox.TextChanged += delegate { UpdateSearchResults(_searchBox.Text.Trim()); };
            left.Controls.Add(_searchBox, 0, 1);

            Label historyLabel = new Label();
            historyLabel.Text = "搜索结果";
            historyLabel.Dock = DockStyle.Fill;
            historyLabel.TextAlign = ContentAlignment.MiddleLeft;
            left.Controls.Add(historyLabel, 0, 2);

            _historyBox = new ListBox();
            _historyBox.Dock = DockStyle.Fill;
            _historyBox.Margin = new Padding(0, 0, 0, 4);
            _historyBox.MouseClick += OnSearchResultClick;
            _historyBox.DoubleClick += delegate { SelectWord(Convert.ToString(_historyBox.SelectedItem)); };
            left.Controls.Add(_historyBox, 0, 3);

            Label previewLabel = new Label();
            previewLabel.Text = "释义预览";
            previewLabel.Dock = DockStyle.Fill;
            previewLabel.TextAlign = ContentAlignment.MiddleLeft;
            left.Controls.Add(previewLabel, 0, 4);

            _definitionBox = new TextBox();
            _definitionBox.Dock = DockStyle.Fill;
            _definitionBox.Margin = new Padding(0);
            _definitionBox.Multiline = true;
            _definitionBox.ScrollBars = ScrollBars.Vertical;
            _definitionBox.ReadOnly = true;
            left.Controls.Add(_definitionBox, 0, 5);

            TableLayoutPanel right = new TableLayoutPanel();
            right.Dock = DockStyle.Fill;
            right.Margin = new Padding(0);
            right.ColumnCount = 1;
            right.RowCount = 2;
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 92F));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.Panel2.Controls.Add(right);

            _miniMap = new MiniMapPanel();
            _miniMap.Dock = DockStyle.Fill;
            _miniMap.Margin = new Padding(0);
            _miniMap.Cards = _cards;
            _miniMap.Notes = _notes;
            _miniMap.Images = _images;
            _miniMap.Arrows = _arrows;
            _miniMap.BackColor = Color.White;
            _miniMap.MouseDown += OnMiniMapMouseDown;
            right.Controls.Add(_miniMap, 0, 0);

            _map = new MapPanel();
            _map.Dock = DockStyle.Fill;
            _map.Margin = new Padding(0);
            _map.TabStop = true;
            _map.AutoScroll = true;
            _map.BackColor = Color.White;
            _map.Cards = _cards;
            _map.Notes = _notes;
            _map.Images = _images;
            _map.Arrows = _arrows;
            _map.StoredCards = _storedCards;
            _map.StoredImages = _storedImages;
            _map.MultiSelectedCards = _selectedCards;
            _map.MultiSelectedNotes = _selectedNotes;
            _map.PasteFromClipboard = delegate { return TryPasteClipboardImageAt(_lastMapMousePoint); };
            _map.MouseDown += OnMapMouseDown;
            _map.MouseMove += OnMapMouseMove;
            _map.MouseUp += OnMapMouseUp;
            _map.MouseDoubleClick += OnMapMouseDoubleClick;
            _map.Scroll += delegate { RefreshMapViews(true); };
            _map.Resize += delegate { ClampLeftPanelWidth(); UpdateCanvasSize(); RefreshMapViews(true); };
            right.Controls.Add(_map, 0, 1);
            Shown += delegate { ClampLeftPanelWidth(); };
        }

        private void BuildTray()
        {
            ContextMenuStrip trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("打开盘单词", null, delegate { ShowMainWindow(); });
            trayMenu.Items.Add("隐藏窗口", null, delegate { Hide(); });
            trayMenu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem providerHint = new ToolStripMenuItem("Google + Bing 同时翻译");
            providerHint.Enabled = false;
            trayMenu.Items.Add(providerHint);

            ToolStripMenuItem hotkeyHint = new ToolStripMenuItem("Alt+A 划词翻译");
            hotkeyHint.Enabled = false;
            trayMenu.Items.Add(hotkeyHint);

            ToolStripMenuItem screenshotHint = new ToolStripMenuItem("Alt+S 截屏翻译");
            screenshotHint.Enabled = false;
            trayMenu.Items.Add(screenshotHint);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("退出", null, delegate
            {
                _allowExit = true;
                Close();
            });

            _notifyIcon = new NotifyIcon();
            _notifyIcon.Icon = SystemIcons.Application;
            _notifyIcon.Text = "盘单词 - Alt+A 划词翻译 / Alt+S 截屏翻译";
            _notifyIcon.ContextMenuStrip = trayMenu;
            _notifyIcon.Visible = true;
            _notifyIcon.DoubleClick += delegate { ShowMainWindow(); };
        }

        private void RegisterTranslateHotkey()
        {
            if (!IsHandleCreated) return;
            if (!_hotkeyRegistered)
            {
                _hotkeyRegistered = RegisterHotKey(Handle, HotkeyTranslateSelection, ModAlt, (uint)Keys.A);
                if (!_hotkeyRegistered)
                {
                    MessageBox.Show("Alt+A 全局热键注册失败，可能已被其他软件占用。", "划词翻译", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            if (!_screenshotHotkeyRegistered)
            {
                _screenshotHotkeyRegistered = RegisterHotKey(Handle, HotkeyTranslateScreenshot, ModAlt, (uint)Keys.S);
                if (!_screenshotHotkeyRegistered)
                {
                    MessageBox.Show("Alt+S 全局热键注册失败，可能已被其他软件占用。", "截屏翻译", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void UnregisterTranslateHotkey()
        {
            if (!IsHandleCreated) return;
            if (_hotkeyRegistered)
            {
                UnregisterHotKey(Handle, HotkeyTranslateSelection);
                _hotkeyRegistered = false;
            }
            if (_screenshotHotkeyRegistered)
            {
                UnregisterHotKey(Handle, HotkeyTranslateScreenshot);
                _screenshotHotkeyRegistered = false;
            }
        }

        private void ShowMainWindow()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        private TranslationPopupForm EnsureTranslationPopup()
        {
            if (_translationPopup == null || _translationPopup.IsDisposed)
            {
                _translationPopup = new TranslationPopupForm();
                if (_settings != null && _settings.HasPopupSize)
                {
                    int width = _settings.PopupSize.Width;
                    int height = _settings.PopupSize.Height;
                    if (width >= 390 && height >= 500)
                    {
                        width = 370;
                        height = 480;
                    }
                    width = Math.Max(_translationPopup.MinimumSize.Width, width);
                    height = Math.Max(_translationPopup.MinimumSize.Height, height);
                    _translationPopup.Size = new Size(width, height);
                }
                if (_settings != null && _settings.HasPopupLocation)
                {
                    _translationPopup.SetPreferredLocation(_settings.PopupLocation);
                }
                _translationPopup.SaveWordRequested += OnPopupSaveWordRequested;
                _translationPopup.PopupLocationChanged += OnPopupLocationChanged;
                _translationPopup.TranslateTextRequested += OnPopupTranslateTextRequested;
                _translationPopup.Dismissed += OnTranslationPopupDismissed;
            }
            return _translationPopup;
        }

        private void OnTranslationPopupDismissed(object sender, EventArgs e)
        {
            Interlocked.Increment(ref _translationRequestId);
            CancelGoogleTranslation();
        }

        private void OnSetTranslationProxy(object sender, EventArgs e)
        {
            string current = _settings.GoogleProxyAddress;
            string value = Prompt.Show("Google 翻译代理地址（留空为直连，例如 127.0.0.1:10801）", "翻译代理", current);
            if (value == null) return;

            _settings.GoogleProxyAddress = value.Trim();
            _translation.GoogleProxyAddress = _settings.GoogleProxyAddress;
            try
            {
                _settings.Save();
                MessageBox.Show(string.IsNullOrWhiteSpace(_settings.GoogleProxyAddress) ? "Google 翻译已设置为直连。" : "Google 翻译代理已保存：" + _settings.GoogleProxyAddress, "翻译代理");
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存翻译代理失败：" + ex.Message, "翻译代理", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnSetOcrSettings(object sender, EventArgs e)
        {
            string path = Prompt.Show("tesseract.exe 路径（留空为自动查找，也可以填安装目录）", "OCR 设置", _settings.TesseractPath);
            if (path == null) return;

            string currentLanguage = string.IsNullOrWhiteSpace(_settings.OcrLanguage) ? "eng+chi_sim" : _settings.OcrLanguage;
            string language = Prompt.Show("OCR 语言（例如 eng、chi_sim、eng+chi_sim）", "OCR 设置", currentLanguage);
            if (language == null) return;

            _settings.TesseractPath = path.Trim().Trim('"');
            _settings.OcrLanguage = string.IsNullOrWhiteSpace(language) ? "eng+chi_sim" : language.Trim();
            try
            {
                _settings.Save();
                MessageBox.Show("OCR 设置已保存。", "OCR 设置");
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存 OCR 设置失败：" + ex.Message, "OCR 设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnStartLanSyncServer(object sender, EventArgs e)
        {
            string portText = Prompt.Show("同步端口", "局域网同步", _settings.LanSyncPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (portText == null) return;
            int port;
            if (!int.TryParse(portText.Trim(), out port) || port <= 0 || port > 65535)
            {
                MessageBox.Show("端口无效。", "局域网同步", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                if (_lanSyncServer != null) _lanSyncServer.Dispose();
                _lanSyncServer = new LanSyncServer(port, HandleLanSyncRequest);
                _lanSyncServer.Start();
                _settings.LanSyncPort = port;
                _settings.Save();
                MessageBox.Show("局域网同步主机已开启。" + Environment.NewLine + "端口：" + port + Environment.NewLine + "其他电脑在同一局域网内连接本机 IP 即可同步。", "局域网同步");
            }
            catch (Exception ex)
            {
                MessageBox.Show("开启同步主机失败：" + ex.Message, "局域网同步", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnStopLanSyncServer(object sender, EventArgs e)
        {
            if (_lanSyncServer != null)
            {
                _lanSyncServer.Dispose();
                _lanSyncServer = null;
            }
            MessageBox.Show("局域网同步主机已停止。", "局域网同步");
        }

        private void OnConnectLanSyncHost(object sender, EventArgs e)
        {
            string host = Prompt.Show("同步主机 IP 或主机名", "局域网同步", _settings.LanSyncHost);
            if (host == null) return;
            host = host.Trim();
            if (string.IsNullOrWhiteSpace(host)) return;

            string portText = Prompt.Show("同步端口", "局域网同步", _settings.LanSyncPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (portText == null) return;
            int port;
            if (!int.TryParse(portText.Trim(), out port) || port <= 0 || port > 65535)
            {
                MessageBox.Show("端口无效。", "局域网同步", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _settings.LanSyncHost = host;
            _settings.LanSyncPort = port;
            try { _settings.Save(); }
            catch { }
            SyncWithLanHost(host, port);
        }

        private void OnSyncNow(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_settings.LanSyncHost))
            {
                OnConnectLanSyncHost(sender, e);
                return;
            }
            SyncWithLanHost(_settings.LanSyncHost, _settings.LanSyncPort);
        }

        private void SyncWithLanHost(string host, int port)
        {
            SyncPacket local = ExportSyncPacket();
            Cursor oldCursor = Cursor.Current;
            Cursor.Current = Cursors.WaitCursor;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    SyncPacket remote = LanSyncClient.Sync(host, port, local);
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        Cursor.Current = oldCursor;
                        int changed = ApplySyncPacket(remote);
                        SaveCurrentFile();
                        SaveSyncMetadata();
                        MessageBox.Show("同步完成，更新单词：" + changed, "局域网同步");
                    }));
                }
                catch (Exception ex)
                {
                    try
                    {
                        BeginInvoke(new MethodInvoker(delegate
                        {
                            Cursor.Current = oldCursor;
                            MessageBox.Show("同步失败：" + ex.Message, "局域网同步", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }));
                    }
                    catch
                    {
                    }
                }
            });
        }

        private SyncPacket HandleLanSyncRequest(SyncPacket remote)
        {
            if (InvokeRequired)
            {
                SyncPacket result = null;
                Exception error = null;
                Invoke(new MethodInvoker(delegate
                {
                    try { result = MergeAndExportSync(remote); }
                    catch (Exception ex) { error = ex; }
                }));
                if (error != null) throw error;
                return result;
            }
            return MergeAndExportSync(remote);
        }

        private SyncPacket MergeAndExportSync(SyncPacket remote)
        {
            int changed = ApplySyncPacket(remote);
            if (changed > 0) SaveCurrentFile();
            SaveSyncMetadata();
            return ExportSyncPacket();
        }

        private SyncPacket ExportSyncPacket()
        {
            RefreshSyncMetadataForCards();
            SaveSyncMetadata();
            SyncPacket packet = new SyncPacket();
            packet.DeviceId = _settings.SyncDeviceId;
            foreach (WordCard c in _cards)
            {
                string word = NormalizeStoredWord(c.Word);
                if (string.IsNullOrWhiteSpace(word)) continue;
                SyncWordRecord record = ToSyncRecord(c, word);
                packet.Words.Add(record);
            }
            return packet;
        }

        private int ApplySyncPacket(SyncPacket packet)
        {
            if (packet == null || packet.Words == null) return 0;
            RefreshSyncMetadataForCards();
            int changed = 0;
            foreach (SyncWordRecord record in packet.Words)
            {
                if (record == null) continue;
                string word = NormalizeStoredWord(record.Word);
                if (string.IsNullOrWhiteSpace(word)) continue;
                if (string.Equals(record.DeviceId, _settings.SyncDeviceId, StringComparison.OrdinalIgnoreCase)) continue;

                long localTicks;
                bool hasLocalTicks = _syncWordTicks.TryGetValue(word, out localTicks);
                WordCard card = FindWordCard(word);
                if (card != null && hasLocalTicks && record.UpdatedAtTicks <= localTicks) continue;

                if (card == null)
                {
                    card = new WordCard();
                    _cards.Add(card);
                }
                ApplyRecordToCard(record, word, card);
                _syncWordTicks[word] = record.UpdatedAtTicks;
                _syncWordSignatures[word] = BuildSyncSignature(card);
                changed++;
            }

            if (changed > 0)
            {
                UpdateCanvasSize();
                UpdateStats();
                RefreshMapViews();
            }
            return changed;
        }

        private SyncWordRecord ToSyncRecord(WordCard c, string word)
        {
            SyncWordRecord record = new SyncWordRecord();
            record.Word = word;
            record.X = c.X;
            record.Y = c.Y;
            record.Width = c.Width;
            record.Height = c.Height;
            record.LastReviewTicks = c.LastReview.Ticks;
            record.NextReviewTicks = c.NextReview.Ticks;
            record.Score = c.Score;
            record.Level = c.Level;
            record.Flag1 = c.Flag1;
            record.Flag2 = c.Flag2;
            record.DeviceId = _settings.SyncDeviceId;
            long ticks;
            record.UpdatedAtTicks = _syncWordTicks.TryGetValue(word, out ticks) ? ticks : DateTime.UtcNow.Ticks;
            return record;
        }

        private void ApplyRecordToCard(SyncWordRecord record, string word, WordCard card)
        {
            card.Word = word;
            card.X = record.X;
            card.Y = record.Y;
            card.Width = Math.Max(30, record.Width);
            card.Height = Math.Max(20, record.Height);
            card.LastReview = TicksToDate(record.LastReviewTicks);
            card.NextReview = TicksToDate(record.NextReviewTicks);
            card.Score = record.Score;
            card.Level = record.Level;
            card.Flag1 = record.Flag1;
            card.Flag2 = record.Flag2;
        }

        private static DateTime TicksToDate(long ticks)
        {
            if (ticks <= 0) return DateTime.MinValue;
            try { return new DateTime(ticks); }
            catch { return DateTime.MinValue; }
        }

        private void RefreshSyncMetadataForCards()
        {
            long now = DateTime.UtcNow.Ticks;
            foreach (WordCard c in _cards)
            {
                string word = NormalizeStoredWord(c.Word);
                if (string.IsNullOrWhiteSpace(word)) continue;
                if (!string.Equals(c.Word, word, StringComparison.Ordinal)) c.Word = word;
                string signature = BuildSyncSignature(c);
                string oldSignature;
                if (!_syncWordSignatures.TryGetValue(word, out oldSignature))
                {
                    _syncWordSignatures[word] = signature;
                    if (!_syncWordTicks.ContainsKey(word)) _syncWordTicks[word] = now;
                }
                else if (!string.Equals(oldSignature, signature, StringComparison.Ordinal))
                {
                    _syncWordSignatures[word] = signature;
                    _syncWordTicks[word] = now;
                }
            }
        }

        private void MarkSyncUpdated(WordCard card)
        {
            if (card == null) return;
            string word = NormalizeStoredWord(card.Word);
            if (string.IsNullOrWhiteSpace(word)) return;
            _syncWordTicks[word] = DateTime.UtcNow.Ticks;
            _syncWordSignatures[word] = BuildSyncSignature(card);
            SaveSyncMetadata();
        }

        private static string BuildSyncSignature(WordCard c)
        {
            if (c == null) return "";
            return string.Join("|", new string[]
            {
                NormalizeStoredWord(c.Word), c.X.ToString(), c.Y.ToString(), c.Width.ToString(), c.Height.ToString(),
                c.LastReview.Ticks.ToString(), c.NextReview.Ticks.ToString(), c.Score.ToString(), c.Level.ToString(),
                c.Flag1 ? "1" : "0", c.Flag2 ? "1" : "0"
            });
        }

        private string GetSyncMetadataPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PandanciClone.sync");
        }

        private void LoadSyncMetadata()
        {
            _syncWordTicks.Clear();
            _syncWordSignatures.Clear();
            string path = GetSyncMetadataPath();
            if (!File.Exists(path)) return;
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                string[] parts = line.Split('|');
                if (parts.Length < 4 || parts[0] != "W") continue;
                string word = DecodeSyncText(parts[1]);
                long ticks;
                if (!long.TryParse(parts[2], out ticks)) ticks = DateTime.UtcNow.Ticks;
                string signature = DecodeSyncText(parts[3]);
                if (!string.IsNullOrWhiteSpace(word))
                {
                    _syncWordTicks[word] = ticks;
                    _syncWordSignatures[word] = signature;
                }
            }
        }

        private void SaveSyncMetadata()
        {
            try
            {
                List<string> lines = new List<string>();
                foreach (KeyValuePair<string, long> item in _syncWordTicks)
                {
                    string signature;
                    _syncWordSignatures.TryGetValue(item.Key, out signature);
                    lines.Add("W|" + EncodeSyncText(item.Key) + "|" + item.Value.ToString() + "|" + EncodeSyncText(signature ?? ""));
                }
                File.WriteAllLines(GetSyncMetadataPath(), lines.ToArray(), Encoding.UTF8);
            }
            catch
            {
            }
        }

        private static string EncodeSyncText(string text)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(text ?? ""));
        }

        private static string DecodeSyncText(string text)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(text)); }
            catch { return ""; }
        }
        private void OnPopupLocationChanged(object sender, EventArgs e)
        {
            TranslationPopupForm popup = sender as TranslationPopupForm;
            if (popup == null || _settings == null) return;
            _settings.PopupLocation = popup.Location;
            _settings.HasPopupLocation = true;
            _settings.PopupSize = popup.Size;
            _settings.HasPopupSize = true;
            try
            {
                _settings.Save();
            }
            catch
            {
                // Popup placement is a convenience setting; ignore transient write failures.
            }
        }

        private void OnPopupTranslateTextRequested(object sender, EventArgs e)
        {
            TranslationPopupForm popup = EnsureTranslationPopup();
            string text = popup.SourceText;
            if (string.IsNullOrWhiteSpace(text)) return;

            int requestId = Interlocked.Increment(ref _translationRequestId);
            TranslationLanguageMode languageMode = popup.LanguageMode;
            popup.ShowLoading(text);
            StartProviderTranslation(requestId, text, TranslationProvider.Google, languageMode);
            StartProviderTranslation(requestId, text, TranslationProvider.Bing, languageMode);
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
            _images.Clear();
            _arrows.Clear();
            _rawItems.Clear();
            _selectedCard = null;
            _selectedNote = null;
            _selectedImage = null;
            _storedCards.Clear();
            _storedImages.Clear();
            _storedItems.Clear();
            ClearMultiSelection();
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
                else if (line.StartsWith("WLBImage|"))
                {
                    ImageItem image = ImageItem.Parse(line);
                    if (image != null) _images.Add(image);
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
            foreach (ImageItem image in _images) lines.Add(image.ToLine());
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
            if (keyData == (Keys.Alt | Keys.X))
            {
                if (StoreSelectedItem()) return true;
                return base.ProcessCmdKey(ref msg, keyData);
            }
            if (keyData == (Keys.Alt | Keys.V))
            {
                if (_storedItems.Count == 0) return base.ProcessCmdKey(ref msg, keyData);
                ReleaseStoredItemAt(_storedItems[_storedItems.Count - 1], _lastMapMousePoint);
                return true;
            }
            if (keyData == (Keys.Control | Keys.V))
            {
                if (TryPasteClipboardImageAt(_lastMapMousePoint)) return true;
                return base.ProcessCmdKey(ref msg, keyData);
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

        protected override void WndProc(ref Message m)
        {
            const int wmPaste = 0x0302;
            if (m.Msg == WmHotkey)
            {
                int id = m.WParam.ToInt32();
                if (id == HotkeyTranslateSelection)
                {
                    TranslateSelectedTextByHotkey();
                }
                else if (id == HotkeyTranslateScreenshot)
                {
                    TranslateScreenshotByHotkey();
                }
                return;
            }
            if (m.Msg == wmPaste && TryPasteClipboardImageAt(_lastMapMousePoint))
            {
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized)
            {
                Hide();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_allowExit && e.CloseReason == CloseReason.UserClosing)
            {
                try
                {
                    SaveCurrentFile();
                }
                catch (Exception ex)
                {
                    MessageBox.Show("自动保存失败：" + ex.Message, "自动保存", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                e.Cancel = true;
                Hide();
                return;
            }

            try
            {
                SaveCurrentFile();
            }
            catch (Exception ex)
            {
                MessageBox.Show("自动保存失败：" + ex.Message, "自动保存", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            if (_lanSyncServer != null)
            {
                _lanSyncServer.Dispose();
                _lanSyncServer = null;
            }
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
            CancelGoogleTranslation();
            if (_translationPopup != null)
            {
                _translationPopup.Dispose();
                _translationPopup = null;
            }
            base.OnFormClosing(e);
        }

        private void TranslateSelectedTextByHotkey()
        {
            int requestId = Interlocked.Increment(ref _translationRequestId);
            CancelGoogleTranslation();

            Thread captureThread = new Thread(new ThreadStart(delegate
            {
                string text;
                try
                {
                    text = _selectedText.CaptureSelectedText();
                }
                catch (Exception ex)
                {
                    BeginShowTranslationError(requestId, "读取选中文本失败：" + ex.Message);
                    return;
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    BeginShowTranslationError(requestId, "没有检测到选中文本。请先用鼠标选中单词，再按 Alt+A。");
                    return;
                }

                try
                {
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        if (requestId != _translationRequestId) return;
                        TranslationPopupForm popup = EnsureTranslationPopup();
                        popup.ShowReady(text, "Alt+A 已读取");
                    }));
                }
                catch (InvalidOperationException)
                {
                }
            }));
            captureThread.IsBackground = true;
            captureThread.SetApartmentState(ApartmentState.STA);
            captureThread.Start();
        }

        private void TranslateScreenshotByHotkey()
        {
            int requestId = Interlocked.Increment(ref _translationRequestId);
            CancelGoogleTranslation();
            Bitmap selectedBitmap = null;
            try
            {
                if (_translationPopup != null && !_translationPopup.IsDisposed && _translationPopup.Visible)
                {
                    _translationPopup.Hide();
                    Application.DoEvents();
                    Thread.Sleep(80);
                }

                using (DpiAwarenessScope.BeginPerMonitorAware())
                {
                    Rectangle virtualBounds;
                    using (Bitmap screenBitmap = CaptureVirtualScreen(out virtualBounds))
                    using (ScreenCaptureForm captureForm = new ScreenCaptureForm(screenBitmap, virtualBounds))
                    {
                        if (captureForm.ShowDialog() != DialogResult.OK) return;
                        selectedBitmap = captureForm.TakeSelectedBitmap();
                    }
                }
            }
            catch (Exception ex)
            {
                ShowTranslationError("截屏失败：" + ex.Message);
                if (selectedBitmap != null) selectedBitmap.Dispose();
                return;
            }

            if (selectedBitmap == null) return;

            EnsureTranslationPopup().ShowOcrReading();
            string tesseractPath = _settings.TesseractPath;
            string ocrLanguage = _settings.OcrLanguage;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string text;
                    using (selectedBitmap)
                    {
                        OcrService service = new OcrService(tesseractPath, ocrLanguage);
                        text = service.Recognize(selectedBitmap);
                    }
                    BeginTranslateRecognizedText(requestId, text);
                }
                catch (Exception ex)
                {
                    BeginShowTranslationError(requestId, "OCR 识别失败：" + ex.Message);
                }
            });
        }

        private static Bitmap CaptureVirtualScreen(out Rectangle bounds)
        {
            bounds = SystemInformation.VirtualScreen;
            Bitmap bitmap = new Bitmap(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height), PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            }
            return bitmap;
        }

        private void BeginTranslateRecognizedText(int requestId, string text)
        {
            try
            {
                BeginInvoke(new MethodInvoker(delegate
                {
                    if (requestId != _translationRequestId) return;
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        ShowTranslationError("OCR 没有识别到文字。");
                        return;
                    }

                    TranslationPopupForm popup = EnsureTranslationPopup();
                    popup.ShowReady(text, "Alt+S OCR 已识别");
                }));
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void StartProviderTranslation(int requestId, string text, TranslationProvider provider, TranslationLanguageMode languageMode)
        {
            if (provider == TranslationProvider.Google)
            {
                StartGoogleTranslation(requestId, text, languageMode);
                return;
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                TranslationService service = new TranslationService();
                TranslationResult result = service.Translate(text, provider, languageMode);
                BeginShowProviderResult(requestId, result);
            });
        }

        private void StartGoogleTranslation(int requestId, string text, TranslationLanguageMode languageMode)
        {
            string googleProxyAddress = _settings.GoogleProxyAddress;
            CancellationTokenSource cancellation = BeginGoogleTranslation();
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool enteredGate = false;
                try
                {
                    _googleTranslationGate.Wait(cancellation.Token);
                    enteredGate = true;

                    TranslationService service = new TranslationService();
                    service.GoogleProxyAddress = googleProxyAddress;
                    TranslationResult result = service.Translate(text, TranslationProvider.Google, languageMode, cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    BeginShowProviderResult(requestId, result);
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    if (enteredGate) _googleTranslationGate.Release();
                    CompleteGoogleTranslation(cancellation);
                }
            });
        }

        private CancellationTokenSource BeginGoogleTranslation()
        {
            CancellationTokenSource previous;
            CancellationTokenSource current = new CancellationTokenSource();
            lock (_googleTranslationCancellationLock)
            {
                previous = _googleTranslationCancellation;
                _googleTranslationCancellation = current;
            }
            TryCancelGoogleTranslation(previous);
            return current;
        }

        private void CancelGoogleTranslation()
        {
            CancellationTokenSource cancellation;
            lock (_googleTranslationCancellationLock)
            {
                cancellation = _googleTranslationCancellation;
                _googleTranslationCancellation = null;
            }
            TryCancelGoogleTranslation(cancellation);
        }

        private static void TryCancelGoogleTranslation(CancellationTokenSource cancellation)
        {
            if (cancellation == null) return;
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
        private void CompleteGoogleTranslation(CancellationTokenSource cancellation)
        {
            lock (_googleTranslationCancellationLock)
            {
                if (ReferenceEquals(_googleTranslationCancellation, cancellation))
                {
                    _googleTranslationCancellation = null;
                }
            }
            cancellation.Dispose();
        }

        private void BeginShowProviderResult(int requestId, TranslationResult result)
        {
            try
            {
                BeginInvoke(new MethodInvoker(delegate
                {
                    if (requestId != _translationRequestId) return;
                    TranslationPopupForm popup = _translationPopup;
                    if (popup == null || popup.IsDisposed || !popup.Visible) return;
                    popup.ShowProviderResult(result);
                }));
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void BeginShowTranslationError(int requestId, string message)
        {
            try
            {
                BeginInvoke(new MethodInvoker(delegate
                {
                    if (requestId != _translationRequestId) return;
                    ShowTranslationError(message);
                }));
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void ShowTranslationError(string message)
        {
            TranslationResult result = new TranslationResult();
            result.SourceText = "";
            result.Provider = _translation.Provider.ToString();
            result.Error = message;
            EnsureTranslationPopup().ShowResult(result);
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
            _map.Focus();
            Point p = PointToMap(e.Location);
            _lastMapMousePoint = p;
            WordCard card = HitCard(p);
            TextNote note = card == null ? HitNote(p) : null;
            ImageItem image = card == null && note == null ? HitImage(p) : null;

            if (e.Button == MouseButtons.Right)
            {
                SelectItem(card, note, image);
                ShowContextMenu(card, note, image, e.Location);
                return;
            }

            if (e.Button != MouseButtons.Left) return;
            _map.Capture = true;
            _dragStartMouse = p;
            _dragMoved = false;

            if ((ModifierKeys & Keys.Control) == Keys.Control)
            {
                _boxSelecting = true;
                _selectionStart = p;
                _selectionRect = Rectangle.Empty;
                ClearMultiSelection();
                _map.ShowSelectionBox = true;
                _map.SelectionBox = _selectionRect;
                _map.Cursor = Cursors.Cross;
                RefreshMapViews();
                return;
            }

            if (IsMultiSelected(card, note))
            {
                _groupDragging = true;
                _groupCardStarts.Clear();
                _groupNoteStarts.Clear();
                foreach (WordCard selected in _selectedCards) _groupCardStarts[selected] = new Point(selected.X, selected.Y);
                foreach (TextNote selected in _selectedNotes) _groupNoteStarts[selected] = new Point(selected.X, selected.Y);
                _map.Cursor = Cursors.SizeAll;
                return;
            }

            SelectItem(card, note);
            _dragCard = card;
            _dragNote = null;
            _dragImage = null;
            _resizeNote = null;
            _resizeImage = null;
            if (card != null) _dragStartLocation = new Point(card.X, card.Y);
            if (note != null)
            {
                if (HitNoteResizeHandle(note, p))
                {
                    _resizeNote = note;
                    _resizeStartSize = new Size(note.Width, note.Height);
                    _map.Cursor = Cursors.SizeNWSE;
                }
                else
                {
                    _dragNote = note;
                    _dragStartLocation = new Point(note.X, note.Y);
                }
            }
            if (image != null)
            {
                if (HitImageResizeHandle(image, p))
                {
                    _resizeImage = image;
                    _resizeStartSize = new Size(image.Width, image.Height);
                    _map.Cursor = Cursors.SizeNWSE;
                }
                else
                {
                    _dragImage = image;
                    _dragStartLocation = new Point(image.X, image.Y);
                }
            }
            if (card == null && note == null && image == null)
            {
                _panning = true;
                _panStartClient = e.Location;
                _panStartScroll = new Point(-_map.AutoScrollPosition.X, -_map.AutoScrollPosition.Y);
                _map.Cursor = Cursors.SizeAll;
            }
        }

        private void OnMapMouseMove(object sender, MouseEventArgs e)
        {
            _lastMapMousePoint = PointToMap(e.Location);

            if (e.Button == MouseButtons.None)
            {
                Point hoverPoint = _lastMapMousePoint;
                TextNote hoverNote = HitNote(hoverPoint);
                ImageItem hoverImage = hoverNote == null ? HitImage(hoverPoint) : null;
                bool overResize = (hoverNote != null && HitNoteResizeHandle(hoverNote, hoverPoint)) || (hoverImage != null && HitImageResizeHandle(hoverImage, hoverPoint));
                _map.Cursor = overResize ? Cursors.SizeNWSE : Cursors.Default;
            }

            if ((_dragCard != null || _dragNote != null || _dragImage != null || _resizeNote != null || _resizeImage != null) && e.Button == MouseButtons.Left)
            {
                AutoScrollWhileDragging(e.Location);
            }

            if (_boxSelecting && e.Button == MouseButtons.Left)
            {
                _selectionRect = NormalizeRect(_selectionStart, _lastMapMousePoint);
                _map.SelectionBox = _selectionRect;
                _map.ShowSelectionBox = true;
                RefreshMapViews(false);
                return;
            }

            if (_groupDragging && e.Button == MouseButtons.Left)
            {
                AutoScrollWhileDragging(e.Location);
                int groupDx = _lastMapMousePoint.X - _dragStartMouse.X;
                int groupDy = _lastMapMousePoint.Y - _dragStartMouse.Y;
                if (Math.Abs(groupDx) + Math.Abs(groupDy) > 2) _dragMoved = true;

                foreach (KeyValuePair<WordCard, Point> item in _groupCardStarts)
                {
                    MoveCard(item.Key, item.Value.X + groupDx, item.Value.Y + groupDy);
                }
                foreach (KeyValuePair<TextNote, Point> item in _groupNoteStarts)
                {
                    item.Key.X = item.Value.X + groupDx;
                    item.Key.Y = item.Value.Y + groupDy;
                }
                RefreshMapViews(false);
                return;
            }

            if (_resizeNote != null && e.Button == MouseButtons.Left)
            {
                Point resizePoint = PointToMap(e.Location);
                int resizeDx = resizePoint.X - _dragStartMouse.X;
                int resizeDy = resizePoint.Y - _dragStartMouse.Y;
                if (Math.Abs(resizeDx) + Math.Abs(resizeDy) > 2) _dragMoved = true;

                Rectangle resizeOldBounds = new Rectangle(_resizeNote.X, _resizeNote.Y, _resizeNote.Width, _resizeNote.Height);
                _resizeNote.Width = Math.Max(60, _resizeStartSize.Width + resizeDx);
                _resizeNote.Height = Math.Max(30, _resizeStartSize.Height + resizeDy);
                Rectangle resizeNewBounds = new Rectangle(_resizeNote.X, _resizeNote.Y, _resizeNote.Width, _resizeNote.Height);
                resizeOldBounds.Inflate(12, 12);
                resizeNewBounds.Inflate(12, 12);
                _map.Invalidate(ToClientRect(Rectangle.Union(resizeOldBounds, resizeNewBounds)));
                return;
            }

            if (_resizeImage != null && e.Button == MouseButtons.Left)
            {
                int resizeDx = _lastMapMousePoint.X - _dragStartMouse.X;
                int resizeDy = _lastMapMousePoint.Y - _dragStartMouse.Y;
                if (Math.Abs(resizeDx) + Math.Abs(resizeDy) > 2) _dragMoved = true;

                Rectangle resizeOldBounds = new Rectangle(_resizeImage.X, _resizeImage.Y, _resizeImage.Width, _resizeImage.Height);
                _resizeImage.Width = Math.Max(30, _resizeStartSize.Width + resizeDx);
                _resizeImage.Height = Math.Max(30, _resizeStartSize.Height + resizeDy);
                Rectangle resizeNewBounds = new Rectangle(_resizeImage.X, _resizeImage.Y, _resizeImage.Width, _resizeImage.Height);
                resizeOldBounds.Inflate(12, 12);
                resizeNewBounds.Inflate(12, 12);
                _map.Invalidate(ToClientRect(Rectangle.Union(resizeOldBounds, resizeNewBounds)));
                return;
            }

            if (_panning && e.Button == MouseButtons.Left)
            {
                int panDx = e.Location.X - _panStartClient.X;
                int panDy = e.Location.Y - _panStartClient.Y;
                _map.AutoScrollPosition = new Point(Math.Max(0, _panStartScroll.X - panDx), Math.Max(0, _panStartScroll.Y - panDy));
                RefreshMapViews(false);
                return;
            }

            if (_dragCard == null && _dragNote == null && _dragImage == null) return;
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
            if (_dragImage != null)
            {
                _dragImage.X = x;
                _dragImage.Y = y;
            }

            Rectangle newBounds = GetDraggedBounds();
            oldBounds.Inflate(80, 80);
            newBounds.Inflate(80, 80);
            _map.Invalidate(ToClientRect(Rectangle.Union(oldBounds, newBounds)));
        }

        private void OnMapMouseUp(object sender, MouseEventArgs e)
        {
            if (_boxSelecting)
            {
                _boxSelecting = false;
                _map.ShowSelectionBox = false;
                SelectItemsInRect(_selectionRect);
                _map.Cursor = Cursors.Default;
                _map.Capture = false;
                RefreshMapViews();
                return;
            }

            if (_panning)
            {
                _panning = false;
                _map.Cursor = Cursors.Default;
                RefreshMapViews();
            }
            _dragCard = null;
            _dragNote = null;
            _dragImage = null;
            _resizeNote = null;
            _resizeImage = null;
            _groupDragging = false;
            _groupCardStarts.Clear();
            _groupNoteStarts.Clear();
            _map.Cursor = Cursors.Default;
            _map.Capture = false;
            if (_dragMoved)
            {
                _dragMoved = false;
                UpdateCanvasSize();
                RefreshMapViews();
            }
        }

        private static Rectangle NormalizeRect(Point a, Point b)
        {
            return Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        }

        private bool IsMultiSelected(WordCard card, TextNote note)
        {
            if (_selectedCards.Count + _selectedNotes.Count <= 1) return false;
            if (card != null && _selectedCards.Contains(card)) return true;
            if (note != null && _selectedNotes.Contains(note)) return true;
            return false;
        }

        private void SelectItemsInRect(Rectangle rect)
        {
            ClearMultiSelection();
            if (rect.Width < 3 || rect.Height < 3) return;

            foreach (WordCard card in _cards)
            {
                Rectangle bounds = new Rectangle(card.X, card.Y, card.Width, card.Height);
                if (rect.IntersectsWith(bounds)) _selectedCards.Add(card);
            }
            foreach (TextNote note in _notes)
            {
                Rectangle bounds = new Rectangle(note.X, note.Y, note.Width, note.Height);
                if (rect.IntersectsWith(bounds)) _selectedNotes.Add(note);
            }

            _selectedCard = _selectedCards.Count > 0 ? _selectedCards[0] : null;
            _selectedNote = _selectedCard == null && _selectedNotes.Count > 0 ? _selectedNotes[0] : null;
            _selectedImage = null;
            _map.SelectedCard = _selectedCard;
            _map.SelectedNote = _selectedNote;
            _map.SelectedImage = null;
        }

        private bool HitNoteResizeHandle(TextNote note, Point p)
        {
            if (note == null) return false;
            Rectangle handle = new Rectangle(note.X + note.Width - 12, note.Y + note.Height - 12, 12, 12);
            return handle.Contains(p);
        }

        private bool HitImageResizeHandle(ImageItem image, Point p)
        {
            if (image == null) return false;
            Rectangle handle = new Rectangle(image.X + image.Width - 14, image.Y + image.Height - 14, 14, 14);
            return handle.Contains(p);
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

        private void ShowContextMenu(WordCard card, TextNote note, ImageItem image, Point screenPoint)
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            if (card != null)
            {
                menu.Items.Add("复习", null, delegate { SelectItem(card, null); ReviewSelected(); });
                menu.Items.Add("查词", null, delegate { SelectItem(card, null); LookupSelected(); });
                menu.Items.Add("存储单词", null, delegate { StoreCard(card); });
                menu.Items.Add("从此单词开始关联", null, delegate { _linkStartCard = card; SelectItem(card, null); });
                menu.Items.Add("关联到此单词", null, delegate { LinkTo(card); });
                menu.Items.Add("已记住", null, delegate { SelectItem(card, null); MarkSelected(true); });
                menu.Items.Add("待学习", null, delegate { SelectItem(card, null); MarkSelected(false); });
                menu.Items.Add("删除", null, delegate { DeleteCard(card); });
            }
            else if (note != null)
            {
                menu.Items.Add("编辑笔记", null, delegate { EditNote(note); });
                menu.Items.Add("删除笔记", null, delegate { _notes.Remove(note); _selectedNotes.Remove(note); _selectedNote = null; UpdateCanvasSize(); RefreshMapViews(); });
            }
            else if (image != null)
            {
                menu.Items.Add("存储图片", null, delegate { StoreImage(image); });
                menu.Items.Add("删除图片", null, delegate { DeleteImage(image); });
            }
            else
            {
                Point p = PointToMap(screenPoint);
                menu.Items.Add("添加单词", null, delegate { AddWordAt(p); });
                menu.Items.Add("添加笔记", null, delegate { AddNoteAt(p); });
                menu.Items.Add("添加图片", null, delegate { AddImageFromFileAt(p); });
                AddReleaseMenu(menu, p);
            }
            menu.Show(_map, screenPoint);
        }

        private bool StoreSelectedItem()
        {
            if (_selectedCard != null)
            {
                StoreCard(_selectedCard);
                return true;
            }
            if (_selectedImage != null)
            {
                StoreImage(_selectedImage);
                return true;
            }
            return false;
        }

        private void StoreCard(WordCard card)
        {
            if (card == null) return;
            SelectItem(card, null);
            if (!_storedCards.Contains(card)) _storedCards.Add(card);
            if (!_storedItems.Contains(card)) _storedItems.Add(card);
            RefreshMapViews();
        }

        private void StoreImage(ImageItem image)
        {
            if (image == null) return;
            SelectItem(null, null, image);
            if (!_storedImages.Contains(image)) _storedImages.Add(image);
            if (!_storedItems.Contains(image)) _storedItems.Add(image);
            RefreshMapViews();
        }

        private void AddReleaseMenu(ContextMenuStrip menu, Point target)
        {
            ToolStripMenuItem release = new ToolStripMenuItem("释放内容");
            if (_storedItems.Count == 0)
            {
                ToolStripMenuItem empty = new ToolStripMenuItem("无已存储内容");
                empty.Enabled = false;
                release.DropDownItems.Add(empty);
            }
            else
            {
                foreach (object stored in _storedItems.ToArray())
                {
                    object item = stored;
                    release.DropDownItems.Add(GetStoredItemText(item), null, delegate { ReleaseStoredItemAt(item, target); });
                }
            }
            menu.Items.Add(release);
        }

        private string GetStoredItemText(object item)
        {
            WordCard card = item as WordCard;
            if (card != null) return card.Word;
            ImageItem image = item as ImageItem;
            if (image != null) return "图片 " + Path.GetFileName(image.FilePath);
            return "内容";
        }

        private void ReleaseStoredItemAt(object item, Point target)
        {
            WordCard card = item as WordCard;
            if (card != null)
            {
                ReleaseCardAt(card, target);
                return;
            }

            ImageItem image = item as ImageItem;
            if (image != null) ReleaseImageAt(image, target);
        }

        private void ReleaseCardAt(WordCard card, Point target)
        {
            if (card == null || !_cards.Contains(card))
            {
                _storedCards.Remove(card);
                _storedItems.Remove(card);
                RefreshMapViews();
                return;
            }

            MoveCard(card, target.X, target.Y);
            _storedCards.Remove(card);
            _storedItems.Remove(card);
            SelectItem(card, null);
            UpdateCanvasSize();
            RefreshMapViews();
        }

        private void ReleaseImageAt(ImageItem image, Point target)
        {
            if (image == null || !_images.Contains(image))
            {
                _storedImages.Remove(image);
                _storedItems.Remove(image);
                RefreshMapViews();
                return;
            }

            image.X = target.X;
            image.Y = target.Y;
            _storedImages.Remove(image);
            _storedItems.Remove(image);
            SelectItem(null, null, image);
            UpdateCanvasSize();
            if (_miniMap != null) _miniMap.RebuildCache();
            RefreshMapViews();
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

        private ImageItem HitImage(Point p)
        {
            for (int i = _images.Count - 1; i >= 0; i--)
            {
                ImageItem image = _images[i];
                if (new Rectangle(image.X, image.Y, image.Width, image.Height).Contains(p)) return image;
            }
            return null;
        }

        private Rectangle GetDraggedBounds()
        {
            if (_dragCard != null) return new Rectangle(_dragCard.X, _dragCard.Y, _dragCard.Width, _dragCard.Height);
            if (_dragNote != null) return new Rectangle(_dragNote.X, _dragNote.Y, _dragNote.Width, _dragNote.Height);
            if (_dragImage != null) return new Rectangle(_dragImage.X, _dragImage.Y, _dragImage.Width, _dragImage.Height);
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
            SelectItem(card, note, null);
        }

        private void SelectItem(WordCard card, TextNote note, ImageItem image)
        {
            ClearMultiSelection();
            _selectedCard = card;
            _selectedNote = note;
            _selectedImage = image;
            _map.SelectedCard = card;
            _map.SelectedNote = note;
            _map.SelectedImage = image;
            RefreshMapViews();
        }

        private void ClearMultiSelection()
        {
            _selectedCards.Clear();
            _selectedNotes.Clear();
            if (_map != null)
            {
                _map.ShowSelectionBox = false;
                _map.SelectionBox = Rectangle.Empty;
            }
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
            if (remembered)
            {
                _selectedCard.Flag1 = true;
                _selectedCard.LastReview = DateTime.Now;
                _selectedCard.NextReview = DateTime.MinValue;
                _selectedCard.Score = 100;
            }
            else
            {
                ResetSelectedToPending();
                return;
            }
            MarkSyncUpdated(_selectedCard);
            UpdateStats();
            RefreshMapViews();
        }

        private void ReviewSelected()
        {
            if (_selectedCard == null) return;
            bool restart = _selectedCard.Flag1;
            _selectedCard.Flag1 = false;
            if (restart)
            {
                _selectedCard.LastReview = DateTime.MinValue;
                _selectedCard.NextReview = DateTime.MinValue;
                _selectedCard.Level = 0;
                _selectedCard.Score = 100;
            }
            _selectedCard.MarkReviewed(true);
            MarkSyncUpdated(_selectedCard);
            UpdateStats();
            RefreshMapViews();
        }

        private void ResetSelectedToPending()
        {
            if (_selectedCard == null) return;
            _selectedCard.Flag1 = false;
            _selectedCard.LastReview = DateTime.MinValue;
            _selectedCard.NextReview = DateTime.MinValue;
            _selectedCard.Score = 100;
            _selectedCard.Level = 3;
            MarkSyncUpdated(_selectedCard);
            UpdateStats();
            RefreshMapViews();
        }
        private void UpdateStats()
        {
            int due = 0;
            int pending = 0;
            int reviewing = 0;
            int mastered = 0;
            foreach (WordCard c in _cards)
            {
                if (c.Flag1)
                {
                    mastered++;
                    continue;
                }
                if (c.LastReview == DateTime.MinValue)
                {
                    pending++;
                    continue;
                }
                reviewing++;
                if (c.Due) due++;
            }
            int active = Math.Max(0, _cards.Count - mastered);
            _statsLabel.Text = "总数: " + _cards.Count + "  已记住: " + mastered + "  复习中: " + reviewing + "  到期: " + due + "  待学习: " + pending + "  其他: " + Math.Max(0, active - reviewing - pending);
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
            AddWordCardAt(p, word.Trim());
        }

        private void AddWordCardAt(Point p, string word)
        {
            AddOrMoveWordCardAt(p, word);
        }

        private WordCard AddOrMoveWordCardAt(Point p, string word)
        {
            string clean = NormalizeStoredWord(word);
            if (string.IsNullOrWhiteSpace(clean)) return null;

            Size size = MeasureWordCardSize(clean);
            WordCard existing = FindWordCard(clean);
            Point location = FindAvailableWordLocation(p, size, existing);

            if (existing != null)
            {
                existing.Word = clean;
                existing.Width = size.Width;
                existing.Height = size.Height;
                MoveCard(existing, location.X, location.Y);
                SelectItem(existing, null);
            }
            else
            {
                WordCard c = new WordCard();
                c.Word = clean;
                c.X = location.X;
                c.Y = location.Y;
                c.Width = size.Width;
                c.Height = size.Height;
                _cards.Add(c);
                existing = c;
                SelectItem(c, null);
            }

            UpdateCanvasSize();
            UpdateStats();
            RefreshMapViews();
            return existing;
        }

        private void OnPopupSaveWordRequested(object sender, EventArgs e)
        {
            if (_translationPopup == null) return;
            string word = NormalizePopupWord(_translationPopup.SourceText);
            if (string.IsNullOrWhiteSpace(word)) return;

            WordCard card = AddOrMoveWordCardAt(GetPopupSaveTarget(), word);
            if (card != null) ScrollTo(card.X, card.Y);
            SaveCurrentFile();
        }

        private Point GetPopupSaveTarget()
        {
            if (_lastMapMousePoint != Point.Empty) return _lastMapMousePoint;
            if (_map != null)
            {
                return new Point(
                    -_map.AutoScrollPosition.X - _map.OriginOffset.X + Math.Max(0, _map.ClientSize.Width / 2),
                    -_map.AutoScrollPosition.Y - _map.OriginOffset.Y + Math.Max(0, _map.ClientSize.Height / 2));
            }
            return new Point(80, 80);
        }

        private WordCard FindWordCard(string word)
        {
            foreach (WordCard c in _cards)
            {
                if (string.Equals(c.Word, word, StringComparison.OrdinalIgnoreCase)) return c;
            }
            return null;
        }

        private Size MeasureWordCardSize(string word)
        {
            return new Size(Math.Max(50, TextRenderer.MeasureText(word, Font).Width + 20), 28);
        }

        private Point FindAvailableWordLocation(Point desired, Size size, WordCard ignore)
        {
            const int gap = 8;
            int stepX = Math.Max(70, size.Width + gap);
            int stepY = Math.Max(36, size.Height + gap);
            for (int row = 0; row < 40; row++)
            {
                for (int col = 0; col < 10; col++)
                {
                    Point candidate = new Point(desired.X + col * stepX, desired.Y + row * stepY);
                    Rectangle bounds = new Rectangle(candidate, size);
                    if (!OverlapsWordCard(bounds, ignore, gap)) return candidate;
                }
            }
            return desired;
        }

        private bool OverlapsWordCard(Rectangle bounds, WordCard ignore, int gap)
        {
            Rectangle padded = bounds;
            padded.Inflate(gap, gap);
            foreach (WordCard c in _cards)
            {
                if (c == ignore) continue;
                Rectangle other = new Rectangle(c.X, c.Y, c.Width, c.Height);
                other.Inflate(gap, gap);
                if (padded.IntersectsWith(other)) return true;
            }
            return false;
        }

        private static string NormalizePopupWord(string text)
        {
            return NormalizeStoredWord(text);
        }

        private static string NormalizeStoredWord(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            string value = text.Trim();
            char[] trimChars = " \t\r\n.,;:!?\"'()[]{}".ToCharArray();
            value = value.Trim(trimChars).ToLowerInvariant();
            return value.Length > 80 ? value.Substring(0, 80) : value;
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

        private void AddImageFromFileAt(Point p)
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件 (*.*)|*.*";
                dlg.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                string storedPath = CopyImageIntoProject(dlg.FileName);
                AddImageItemAt(storedPath, p);
            }
        }

        private bool TryPasteClipboardImageAt(Point p)
        {
            try
            {
                if (!Clipboard.ContainsImage()) return false;
                using (Image image = Clipboard.GetImage())
                {
                    if (image == null) return false;

                    string storedPath = SaveClipboardImage(image);
                    AddImageItemAt(storedPath, p);
                    return true;
                }
            }
            catch (ExternalException)
            {
                MessageBox.Show("读取剪贴板图片失败，请稍后再试。", "添加图片", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return true;
            }
        }

        private string CopyImageIntoProject(string sourcePath)
        {
            string extension = Path.GetExtension(sourcePath);
            if (string.IsNullOrEmpty(extension)) extension = ".png";
            string targetPath = GetUniqueImagePath(extension);
            File.Copy(sourcePath, targetPath, true);
            return ToAppRelativePath(targetPath);
        }

        private string SaveClipboardImage(Image image)
        {
            string targetPath = GetUniqueImagePath(".png");
            image.Save(targetPath, ImageFormat.Png);
            return ToAppRelativePath(targetPath);
        }

        private string GetUniqueImagePath(string extension)
        {
            // 图片复制到程序目录下，wordmap 只保存相对路径，避免原始文件被移动后丢图。
            string imageDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "images");
            Directory.CreateDirectory(imageDir);
            return Path.Combine(imageDir, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + extension);
        }

        private string ToAppRelativePath(string fullPath)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(fullPath);
            if (full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase)) return full.Substring(baseDir.Length);
            return full;
        }

        private void AddImageItemAt(string storedPath, Point p)
        {
            Size size = GetImageDisplaySize(ResolveImagePath(storedPath));
            ImageItem item = new ImageItem();
            item.X = p.X;
            item.Y = p.Y;
            item.Width = size.Width;
            item.Height = size.Height;
            item.FilePath = storedPath;
            _images.Add(item);
            UpdateCanvasSize();
            if (_miniMap != null) _miniMap.RebuildCache();
            RefreshMapViews();
        }

        private Size GetImageDisplaySize(string path)
        {
            // 初始导入时限制最大显示尺寸，既保留比例，也避免大图铺满画布。
            using (Image image = Image.FromFile(path))
            {
                const int maxWidth = 360;
                const int maxHeight = 240;
                float scale = Math.Min(maxWidth / (float)image.Width, maxHeight / (float)image.Height);
                scale = Math.Min(1F, Math.Max(0.01F, scale));
                return new Size(Math.Max(20, (int)(image.Width * scale)), Math.Max(20, (int)(image.Height * scale)));
            }
        }

        private static string ResolveImagePath(string path)
        {
            if (Path.IsPathRooted(path)) return path;
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
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
            _storedCards.Remove(card);
            _storedItems.Remove(card);
            _selectedCards.Remove(card);
            _arrows.RemoveAll(delegate(ArrowItem a) { return (a.X1 == cx && a.Y1 == cy) || (a.X2 == cx && a.Y2 == cy); });
            if (_selectedCard == card) SelectItem(null, null);
            string deletedSyncWord = NormalizeStoredWord(card.Word);
            _syncWordTicks.Remove(deletedSyncWord);
            _syncWordSignatures.Remove(deletedSyncWord);
            SaveSyncMetadata();
            UpdateCanvasSize();
            UpdateStats();
            if (_miniMap != null) _miniMap.RebuildCache();
            RefreshMapViews();
        }

        private void DeleteImage(ImageItem image)
        {
            if (image == null) return;
            _images.Remove(image);
            _storedImages.Remove(image);
            _storedItems.Remove(image);
            if (_selectedImage == image) SelectItem(null, null, null);
            UpdateCanvasSize();
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
            foreach (ImageItem image in _images)
            {
                minX = Math.Min(minX, image.X);
                minY = Math.Min(minY, image.Y);
                maxX = Math.Max(maxX, image.X + image.Width);
                maxY = Math.Max(maxY, image.Y + image.Height);
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
        public List<ImageItem> Images;
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
                if (Images != null)
                {
                    using (Brush imageBrush = new SolidBrush(Color.FromArgb(225, 235, 245)))
                    {
                        foreach (ImageItem image in Images) g.FillRectangle(imageBrush, image.X, image.Y, image.Width, image.Height);
                    }
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
        public List<ImageItem> Images;
        public List<ArrowItem> Arrows;
        public List<WordCard> StoredCards;
        public List<ImageItem> StoredImages;
        public List<WordCard> MultiSelectedCards;
        public List<TextNote> MultiSelectedNotes;
        public WordCard SelectedCard;
        public TextNote SelectedNote;
        public ImageItem SelectedImage;
        public Point OriginOffset { get; set; }
        public Rectangle MapBounds = Rectangle.Empty;
        public Rectangle SelectionBox = Rectangle.Empty;
        public bool ShowSelectionBox;
        public Func<bool> PasteFromClipboard;
        private readonly Dictionary<string, Image> _imageCache = new Dictionary<string, Image>();

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

        protected override void WndProc(ref Message m)
        {
            const int wmPaste = 0x0302;
            if (m.Msg == wmPaste && PasteFromClipboard != null && PasteFromClipboard())
            {
                return;
            }
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (Image image in _imageCache.Values) image.Dispose();
                _imageCache.Clear();
            }
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Point offset = AutoScrollPosition;
            e.Graphics.TranslateTransform(offset.X + OriginOffset.X, offset.Y + OriginOffset.Y);
            Rectangle view = new Rectangle(-offset.X - OriginOffset.X, -offset.Y - OriginOffset.Y, ClientSize.Width, ClientSize.Height);

            DrawGrid(e.Graphics, view);
            DrawImages(e.Graphics, view);
            DrawArrows(e.Graphics, view);
            DrawNotes(e.Graphics, view);
            DrawCards(e.Graphics, view);
            DrawSelectionBox(e.Graphics);
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

        private void DrawImages(Graphics g, Rectangle view)
        {
            if (Images == null) return;
            using (Pen border = new Pen(Color.FromArgb(150, 150, 150)))
            using (Pen selectedBorder = new Pen(Color.RoyalBlue, 2F))
            using (Brush storedMarker = new SolidBrush(Color.RoyalBlue))
            using (Brush handleBrush = new SolidBrush(Color.RoyalBlue))
            using (Brush missing = new SolidBrush(Color.FromArgb(245, 245, 245)))
            using (Brush textBrush = new SolidBrush(Color.Gray))
            {
                foreach (ImageItem item in Images)
                {
                    Rectangle r = new Rectangle(item.X, item.Y, item.Width, item.Height);
                    if (!r.IntersectsWith(view)) continue;

                    Image image = GetCachedImage(item.FilePath);
                    if (image != null)
                    {
                        g.DrawImage(image, r);
                    }
                    else
                    {
                        g.FillRectangle(missing, r);
                        g.DrawString("图片缺失", Font, textBrush, r);
                    }
                    bool selected = item == SelectedImage;
                    if (StoredImages != null && StoredImages.Contains(item))
                    {
                        g.FillRectangle(storedMarker, r.X + 1, r.Y + 1, Math.Max(1, r.Width - 1), 5);
                    }
                    g.DrawRectangle(selected ? selectedBorder : border, r);
                    if (selected)
                    {
                        g.FillRectangle(handleBrush, item.X + item.Width - 9, item.Y + item.Height - 9, 8, 8);
                    }
                }
            }
        }

        private Image GetCachedImage(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string fullPath = Path.IsPathRooted(path) ? path : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
            Image cached;
            if (_imageCache.TryGetValue(fullPath, out cached)) return cached;
            if (!File.Exists(fullPath)) return null;

            // GDI+ 会锁定直接 FromFile 的文件，所以读入内存后 clone 一份用于长期缓存。
            using (Image loaded = Image.FromFile(fullPath))
            {
                cached = new Bitmap(loaded);
            }
            _imageCache[fullPath] = cached;
            return cached;
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

        private void DrawSelectionBox(Graphics g)
        {
            if (!ShowSelectionBox || SelectionBox.Width <= 0 || SelectionBox.Height <= 0) return;
            using (Brush fill = new SolidBrush(Color.FromArgb(35, Color.RoyalBlue)))
            using (Pen pen = new Pen(Color.RoyalBlue))
            {
                pen.DashStyle = DashStyle.Dash;
                g.FillRectangle(fill, SelectionBox);
                g.DrawRectangle(pen, SelectionBox);
            }
        }

        private void DrawNotes(Graphics g, Rectangle view)
        {
            if (Notes == null) return;
            using (Brush brush = new SolidBrush(Color.LemonChiffon))
            using (Pen pen = new Pen(Color.Goldenrod))
            using (Pen selectedPen = new Pen(Color.OrangeRed, 2F))
            using (Brush handleBrush = new SolidBrush(Color.OrangeRed))
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
                    bool selected = note == SelectedNote || (MultiSelectedNotes != null && MultiSelectedNotes.Contains(note));
                    g.DrawRectangle(selected ? selectedPen : pen, r);
                    RectangleF textRect = new RectangleF(note.X + 4, note.Y + 4, Math.Max(1, note.Width - 8), Math.Max(1, note.Height - 8));
                    g.DrawString(note.Text, Font, textBrush, textRect, format);
                    if (selected)
                    {
                        g.FillRectangle(handleBrush, note.X + note.Width - 8, note.Y + note.Height - 8, 7, 7);
                    }
                }
            }
        }

        private void DrawCards(Graphics g, Rectangle view)
        {
            if (Cards == null) return;
            using (Brush normal = new SolidBrush(Color.White))
            using (Brush due = new SolidBrush(Color.LightYellow))
            using (Brush reviewing = new SolidBrush(Color.Honeydew))
            using (Brush mastered = new SolidBrush(Color.Gainsboro))
            using (Brush storedMarker = new SolidBrush(Color.RoyalBlue))
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
                    Brush brush = GetCardBrush(card, normal, due, reviewing, mastered);
                    g.FillRectangle(brush, r);
                    if (StoredCards != null && StoredCards.Contains(card))
                    {
                        g.FillRectangle(storedMarker, r.X + 1, r.Y + 1, r.Width - 1, 4);
                    }
                    bool selected = card == SelectedCard || (MultiSelectedCards != null && MultiSelectedCards.Contains(card));
                    g.DrawRectangle(selected ? selectedBorder : border, r);
                    g.DrawString(card.Word, Font, textBrush, new RectangleF(r.X, r.Y + 1, r.Width, r.Height), format);
                }
            }
        }

        private static Brush GetCardBrush(WordCard card, Brush normal, Brush due, Brush reviewing, Brush mastered)
        {
            if (card.Flag1) return mastered;
            if (card.Due) return due;
            if (card.LastReview == DateTime.MinValue) return normal;
            return reviewing;
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






















