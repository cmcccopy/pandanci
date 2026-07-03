using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PandanciClone
{
    internal sealed class TranslationPopupForm : Form
    {
        private const int WsExToolWindow = 0x00000080;
        private const int WmNcHitTest = 0x0084;
        private const int HtLeft = 10;
        private const int HtRight = 11;
        private const int HtTop = 12;
        private const int HtTopLeft = 13;
        private const int HtTopRight = 14;
        private const int HtBottom = 15;
        private const int HtBottomLeft = 16;
        private const int HtBottomRight = 17;
        private const int ResizeBorder = 8;
        private const int VkLButton = 0x01;
        private const int VkRButton = 0x02;
        private const int VkMButton = 0x04;

        private readonly Button _pinButton;
        private readonly Button _closeButton;
        private readonly Label _titleLabel;
        private readonly ComboBox _languageModeBox;
        private readonly TextBox _sourceBox;
        private readonly Button _speakSourceButton;
        private readonly Button _copySourceButton;
        private readonly Button _pasteButton;
        private readonly Button _normalizeButton;
        private readonly Button _clearButton;
        private readonly Button _translateButton;
        private readonly Label _sourceLangPill;
        private readonly Label _fromLangLabel;
        private readonly Label _swapLabel;
        private readonly Label _toLangLabel;
        private readonly Label _statusLabel;
        private readonly Button _copyAllButton;
        private readonly Button _saveButton;
        private readonly Label _resizeGrip;
        private readonly RoundedPanel _inputCard;
        private readonly RoundedPanel _directionCard;
        private readonly ResultCardUi _google;
        private readonly ResultCardUi _bing;
        private readonly Timer _outsideClickTimer;
        private readonly Timer _editDebounceTimer;
        private readonly ToolTip _toolTip;

        private string _sourceText = "";
        private string _translatedText = "";
        private string _googleText = "";
        private string _bingText = "";
        private bool _pinned;
        private bool _lastMouseDown;
        private bool _hasLocation;
        private bool _settingSourceText;
        private bool _showWithoutActivation = true;
        private bool _dragging;
        private bool _resizing;
        private bool _updatingLanguageMode;
        private Point _dragStartMouse;
        private Point _dragStartLocation;
        private Point _resizeStartMouse;
        private Size _resizeStartSize;

        public event EventHandler SaveWordRequested;
        public event EventHandler PopupLocationChanged;
        public event EventHandler TranslateTextRequested;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        public TranslationPopupForm()
        {
            Text = "划词翻译";
            Width = 370;
            Height = 480;
            MinimumSize = new Size(340, 400);
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Color.FromArgb(248, 249, 252);
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Font = new Font("Microsoft YaHei UI", 8.5F);

            _toolTip = new ToolTip();
            _toolTip.AutoPopDelay = 8000;
            _toolTip.InitialDelay = 350;
            _toolTip.ReshowDelay = 100;

            _titleLabel = new Label();
            _titleLabel.AutoSize = false;
            _titleLabel.Text = "划词翻译";
            _titleLabel.TextAlign = ContentAlignment.MiddleLeft;
            _titleLabel.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
            _titleLabel.ForeColor = Color.FromArgb(35, 35, 35);
            Controls.Add(_titleLabel);

            _languageModeBox = new ComboBox();
            _languageModeBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _languageModeBox.FlatStyle = FlatStyle.Flat;
            _languageModeBox.Font = new Font("Microsoft YaHei UI", 8.5F);
            _languageModeBox.Items.AddRange(new object[] { "自动识别", "English -> 简中", "简中 -> English" });
            _languageModeBox.SelectedIndex = 0;
            _languageModeBox.SelectedIndexChanged += OnLanguageModeChanged;
            Controls.Add(_languageModeBox);

            _pinButton = MakeIconButton("📌");
            _pinButton.Click += delegate { TogglePinned(); };
            Controls.Add(_pinButton);

            _closeButton = MakeIconButton("×");
            _closeButton.Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold);
            _closeButton.Click += delegate { Hide(); };
            Controls.Add(_closeButton);

            _inputCard = new RoundedPanel { BackColor = Color.White, Radius = 14 };
            Controls.Add(_inputCard);

            _sourceBox = new TextBox();
            _sourceBox.BorderStyle = BorderStyle.None;
            _sourceBox.BackColor = _inputCard.BackColor;
            _sourceBox.Multiline = true;
            _sourceBox.Font = new Font("Microsoft YaHei UI", 12.5F);
            _sourceBox.KeyDown += OnSourceBoxKeyDown;
            _sourceBox.TextChanged += OnSourceBoxTextChanged;
            _inputCard.Controls.Add(_sourceBox);

            _speakSourceButton = MakeCardButton("🔊");
            _speakSourceButton.Click += delegate { SpeakText(SourceText); };
            _inputCard.Controls.Add(_speakSourceButton);

            _copySourceButton = MakeCardButton("⧉");
            _copySourceButton.Click += delegate
            {
                if (!string.IsNullOrWhiteSpace(SourceText)) Clipboard.SetText(SourceText);
            };
            _inputCard.Controls.Add(_copySourceButton);

            _pasteButton = MakeCardButton("📋");
            _pasteButton.Click += delegate
            {
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        SetSourceText(Clipboard.GetText().Trim());
                        RaiseTranslateTextRequested();
                    }
                }
                catch
                {
                    System.Media.SystemSounds.Beep.Play();
                }
            };
            _inputCard.Controls.Add(_pasteButton);

            _normalizeButton = MakeCardButton("↵");
            _normalizeButton.Click += delegate { SetSourceText(NormalizeSourceText(SourceText)); RaiseTranslateTextRequested(); };
            _inputCard.Controls.Add(_normalizeButton);

            _clearButton = MakeCardButton("⌫");
            _clearButton.Click += delegate
            {
                SetSourceText("");
                _googleText = "";
                _bingText = "";
                UpdateResults();
                UpdateButtons();
            };
            _inputCard.Controls.Add(_clearButton);

            _sourceLangPill = new Label();
            _sourceLangPill.AutoSize = false;
            _sourceLangPill.TextAlign = ContentAlignment.MiddleCenter;
            _sourceLangPill.Font = new Font("Microsoft YaHei UI", 8.5F);
            _sourceLangPill.BackColor = Color.FromArgb(244, 247, 252);
            _inputCard.Controls.Add(_sourceLangPill);

            _translateButton = new Button();
            _translateButton.FlatStyle = FlatStyle.Flat;
            _translateButton.FlatAppearance.BorderSize = 0;
            _translateButton.BackColor = Color.FromArgb(72, 120, 232);
            _translateButton.ForeColor = Color.White;
            _translateButton.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
            _translateButton.Text = "翻译";
            _translateButton.Click += delegate { RaiseTranslateTextRequested(); };
            _inputCard.Controls.Add(_translateButton);

            _directionCard = new RoundedPanel { BackColor = Color.FromArgb(242, 245, 249), Radius = 12 };
            Controls.Add(_directionCard);

            _fromLangLabel = MakeDirectionLabel(ContentAlignment.MiddleLeft);
            _directionCard.Controls.Add(_fromLangLabel);

            _swapLabel = MakeDirectionLabel(ContentAlignment.MiddleCenter);
            _swapLabel.Text = "⇄";
            _swapLabel.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold);
            _swapLabel.Cursor = Cursors.Hand;
            _swapLabel.Click += delegate { SwapLanguageMode(); };
            _directionCard.Controls.Add(_swapLabel);

            _toLangLabel = MakeDirectionLabel(ContentAlignment.MiddleRight);
            _directionCard.Controls.Add(_toLangLabel);

            _google = MakeResultCard("Google");
            Controls.Add(_google.Card);

            _bing = MakeResultCard("Bing");
            Controls.Add(_bing.Card);

            _statusLabel = new Label();
            _statusLabel.AutoEllipsis = true;
            _statusLabel.Font = new Font("Microsoft YaHei UI", 8F);
            _statusLabel.ForeColor = Color.FromArgb(100, 100, 100);
            _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(_statusLabel);

            _copyAllButton = MakeBottomButton("复制全部");
            _copyAllButton.Click += delegate
            {
                string text = BuildCopyAllText();
                if (!string.IsNullOrWhiteSpace(text)) Clipboard.SetText(text);
            };
            Controls.Add(_copyAllButton);

            _saveButton = MakeBottomButton("存单词");
            _saveButton.Click += delegate
            {
                EventHandler handler = SaveWordRequested;
                if (handler != null) handler(this, EventArgs.Empty);
            };
            Controls.Add(_saveButton);

            _resizeGrip = new Label();
            _resizeGrip.AutoSize = false;
            _resizeGrip.Text = "◢";
            _resizeGrip.TextAlign = ContentAlignment.MiddleCenter;
            _resizeGrip.ForeColor = Color.FromArgb(105, 112, 125);
            _resizeGrip.Cursor = Cursors.SizeNWSE;
            _resizeGrip.MouseDown += OnResizeGripMouseDown;
            _resizeGrip.MouseMove += OnResizeGripMouseMove;
            _resizeGrip.MouseUp += delegate { _resizing = false; };
            Controls.Add(_resizeGrip);

            _outsideClickTimer = new Timer();
            _outsideClickTimer.Interval = 50;
            _outsideClickTimer.Tick += OnOutsideClickTimerTick;
            _outsideClickTimer.Start();

            _editDebounceTimer = new Timer();
            _editDebounceTimer.Interval = 650;
            _editDebounceTimer.Tick += delegate
            {
                _editDebounceTimer.Stop();
                RaiseTranslateTextRequested();
            };

            WireDrag(this);
            WireDrag(_titleLabel);
            WireDrag(_inputCard);
            WireDrag(_directionCard);
            WireDrag(_google.Card);
            WireDrag(_google.Header);
            WireDrag(_google.Title);
            WireDrag(_bing.Card);
            WireDrag(_bing.Header);
            WireDrag(_bing.Title);
            SetToolTips();
            LayoutControls();
            UpdateDirectionLabels();
            UpdateButtons();
        }

        public string SourceText
        {
            get { return _sourceBox.Text.Trim(); }
        }

        public bool Pinned
        {
            get { return _pinned; }
        }

        public TranslationLanguageMode LanguageMode
        {
            get { return GetLanguageMode(); }
        }

        public void SetPreferredLocation(Point location)
        {
            Location = ClampToScreen(location);
            _hasLocation = true;
        }

        protected override bool ShowWithoutActivation
        {
            get { return _showWithoutActivation; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WsExToolWindow;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmNcHitTest)
            {
                base.WndProc(ref m);
                if ((int)m.Result != 1) return;

                Point p = PointToClient(Cursor.Position);
                bool left = p.X <= ResizeBorder;
                bool right = p.X >= ClientSize.Width - ResizeBorder;
                bool top = p.Y <= ResizeBorder;
                bool bottom = p.Y >= ClientSize.Height - ResizeBorder;

                if (left && top) m.Result = (IntPtr)HtTopLeft;
                else if (right && top) m.Result = (IntPtr)HtTopRight;
                else if (left && bottom) m.Result = (IntPtr)HtBottomLeft;
                else if (right && bottom) m.Result = (IntPtr)HtBottomRight;
                else if (left) m.Result = (IntPtr)HtLeft;
                else if (right) m.Result = (IntPtr)HtRight;
                else if (top) m.Result = (IntPtr)HtTop;
                else if (bottom) m.Result = (IntPtr)HtBottom;
                return;
            }
            base.WndProc(ref m);
        }
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutControls();
            if (Width <= 0 || Height <= 0) return;
            using (GraphicsPath path = RoundedPanel.CreateRoundRectPath(new Rectangle(0, 0, Width, Height), 18))
            {
                Region = new Region(path);
            }
            EventHandler handler = PopupLocationChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_outsideClickTimer != null)
                {
                    _outsideClickTimer.Stop();
                    _outsideClickTimer.Dispose();
                }
                if (_editDebounceTimer != null)
                {
                    _editDebounceTimer.Stop();
                    _editDebounceTimer.Dispose();
                }
                if (_toolTip != null)
                {
                    _toolTip.Dispose();
                }
            }
            base.Dispose(disposing);
        }

        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            _hasLocation = true;
            EventHandler handler = PopupLocationChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        public void ShowLoading(string sourceText)
        {
            SetSourceText(sourceText ?? "");
            _translatedText = "";
            _googleText = "正在翻译...";
            _bingText = "正在翻译...";
            _sourceBox.ReadOnly = false;
            UpdateDirectionLabels();
            UpdateResults();
            _statusLabel.Text = TranslationService.DetectTarget(_sourceText, GetLanguageMode()).DirectionText + " · Google + Bing";
            UpdateButtons();
            ShowPopup(true);
        }

        public void ShowReading()
        {
            SetSourceText("正在读取选中文本...");
            _sourceBox.ReadOnly = true;
            _translatedText = "";
            _googleText = "";
            _bingText = "";
            _google.TextBox.Text = "正在读取选中文本...";
            _bing.TextBox.Text = "";
            _statusLabel.Text = "Alt+A";
            UpdateDirectionLabels();
            UpdateButtons();
            ShowPopup(false);
        }

        public void ShowOcrReading()
        {
            SetSourceText("正在识别截图文字...");
            _sourceBox.ReadOnly = true;
            _translatedText = "";
            _googleText = "";
            _bingText = "";
            _google.TextBox.Text = "正在识别截图文字...";
            _bing.TextBox.Text = "";
            _statusLabel.Text = "Alt+S OCR";
            UpdateDirectionLabels();
            UpdateButtons();
            ShowPopup(true);
        }

        public void ShowResult(TranslationResult result)
        {
            if (result == null) return;
            SetSourceText(result.SourceText ?? "");
            _sourceBox.ReadOnly = false;
            _translatedText = result.TranslatedText ?? "";
            _googleText = string.Equals(result.Provider, "Google", StringComparison.OrdinalIgnoreCase)
                ? (string.IsNullOrWhiteSpace(result.Error) ? result.TranslatedText : result.Error)
                : "";
            _bingText = string.Equals(result.Provider, "Bing", StringComparison.OrdinalIgnoreCase)
                ? (string.IsNullOrWhiteSpace(result.Error) ? result.TranslatedText : result.Error)
                : "";
            UpdateDirectionLabels();
            UpdateResults();
            _statusLabel.Text = result.Provider + (string.IsNullOrWhiteSpace(result.DetectedLanguage) ? "" : "  " + result.DetectedLanguage);
            UpdateButtons();
            ShowPopup(true);
        }

        public void ShowProviderResult(TranslationResult result)
        {
            if (result == null) return;
            if (!string.IsNullOrWhiteSpace(result.SourceText)) SetSourceText(result.SourceText);

            string text = string.IsNullOrWhiteSpace(result.Error) ? result.TranslatedText : result.Error;
            if (string.Equals(result.Provider, "Bing", StringComparison.OrdinalIgnoreCase)) _bingText = text;
            else _googleText = text;

            _sourceBox.ReadOnly = false;
            UpdateDirectionLabels();
            UpdateResults();
            _statusLabel.Text = (string.IsNullOrWhiteSpace(result.DirectionText) ? TranslationService.DetectTarget(_sourceText, GetLanguageMode()).DirectionText : result.DirectionText) + " · Google + Bing";
            UpdateButtons();
            ShowPopup(true);
        }

        private void LayoutControls()
        {
            if (_pinButton == null || _closeButton == null || _titleLabel == null || _languageModeBox == null
                || _inputCard == null || _directionCard == null || _pasteButton == null || _resizeGrip == null
                || _google == null || _bing == null || _statusLabel == null || _copyAllButton == null || _saveButton == null)
            {
                return;
            }

            int margin = 10;
            int contentWidth = Math.Max(1, ClientSize.Width - margin * 2);
            _titleLabel.SetBounds(16, 7, Math.Max(80, contentWidth - 220), 30);
            _languageModeBox.SetBounds(Math.Max(108, ClientSize.Width - 214), 10, 138, 26);
            _pinButton.SetBounds(ClientSize.Width - 72, 7, 30, 30);
            _closeButton.SetBounds(ClientSize.Width - 38, 7, 30, 30);

            _inputCard.SetBounds(margin, 46, contentWidth, 136);
            _sourceBox.SetBounds(18, 18, _inputCard.Width - 36, 58);
            _speakSourceButton.SetBounds(16, 98, 28, 28);
            _copySourceButton.SetBounds(48, 98, 28, 28);
            _pasteButton.SetBounds(80, 98, 28, 28);
            _normalizeButton.SetBounds(112, 98, 28, 28);
            _clearButton.SetBounds(144, 98, 28, 28);
            _sourceLangPill.SetBounds(178, 99, 70, 26);
            _translateButton.SetBounds(_inputCard.Width - 92, 92, 76, 36);

            _directionCard.SetBounds(margin, _inputCard.Bottom + 10, contentWidth, 44);
            int third = _directionCard.Width / 3;
            _fromLangLabel.SetBounds(18, 0, third - 18, _directionCard.Height);
            _swapLabel.SetBounds(third, 0, third, _directionCard.Height);
            _toLangLabel.SetBounds(third * 2, 0, _directionCard.Width - third * 2 - 18, _directionCard.Height);

            int resultTop = _directionCard.Bottom + 10;
            int bottomTop = ClientSize.Height - 38;
            int available = Math.Max(108, bottomTop - resultTop - 10);
            int googleHeight = _google.Collapsed ? 36 : Math.Max(78, available / (_bing.Collapsed ? 1 : 2));
            int bingHeight = _bing.Collapsed ? 36 : Math.Max(78, available - googleHeight - 8);
            if (_google.Collapsed && !_bing.Collapsed) bingHeight = Math.Max(78, available - googleHeight - 8);
            if (!_google.Collapsed && _bing.Collapsed) googleHeight = Math.Max(78, available - bingHeight - 8);

            _google.Card.SetBounds(margin, resultTop, contentWidth, googleHeight);
            LayoutResultCard(_google);
            _bing.Card.SetBounds(margin, _google.Card.Bottom + 8, contentWidth, bingHeight);
            LayoutResultCard(_bing);

            _statusLabel.SetBounds(margin + 2, bottomTop, Math.Max(80, contentWidth - 152), 26);
            _copyAllButton.SetBounds(ClientSize.Width - 142, bottomTop, 66, 28);
            _saveButton.SetBounds(ClientSize.Width - 72, bottomTop, 62, 28);
            _resizeGrip.SetBounds(ClientSize.Width - 22, ClientSize.Height - 22, 18, 18);
        }

        private void LayoutResultCard(ResultCardUi ui)
        {
            ui.Header.SetBounds(0, 0, ui.Card.Width, 32);
            ui.Title.SetBounds(14, 0, Math.Max(70, ui.Card.Width - 184), 32);
            int x = ui.Card.Width - 162;
            ui.SpeakButton.SetBounds(x, 3, 26, 26);
            ui.CopyButton.SetBounds(x + 29, 3, 26, 26);
            ui.BackButton.SetBounds(x + 58, 3, 26, 26);
            ui.RetryButton.SetBounds(x + 87, 3, 26, 26);
            ui.CollapseButton.SetBounds(ui.Card.Width - 32, 3, 26, 26);
            ui.TextBox.SetBounds(16, 40, ui.Card.Width - 32, Math.Max(22, ui.Card.Height - 48));
            ui.TextBox.Visible = !ui.Collapsed;
            ui.SpeakButton.Visible = !ui.Collapsed;
            ui.CopyButton.Visible = !ui.Collapsed;
            ui.BackButton.Visible = !ui.Collapsed;
            ui.RetryButton.Visible = !ui.Collapsed;
        }

        private void UpdateDirectionLabels()
        {
            TranslationLanguageMode mode = GetLanguageMode();
            TranslationTarget target = TranslationService.DetectTarget(_sourceText, mode);
            bool chinese = mode == TranslationLanguageMode.ChineseToEnglish
                || (mode == TranslationLanguageMode.Auto && target.GoogleCode == "en");
            _sourceLangPill.Text = chinese ? "● 中文" : "● 英语";
            _sourceLangPill.ForeColor = chinese ? Color.FromArgb(30, 105, 85) : Color.FromArgb(80, 54, 180);
            _fromLangLabel.Text = mode == TranslationLanguageMode.Auto ? "自动检测" : (chinese ? "简体中文" : "English");
            _toLangLabel.Text = chinese ? "English" : "简体中文";
        }

        private void UpdateResults()
        {
            _google.TextBox.Text = string.IsNullOrWhiteSpace(_googleText) ? "等待结果..." : _googleText;
            _bing.TextBox.Text = string.IsNullOrWhiteSpace(_bingText) ? "等待结果..." : _bingText;
            _translatedText = "Google" + Environment.NewLine + _google.TextBox.Text + Environment.NewLine + Environment.NewLine
                + "Bing" + Environment.NewLine + _bing.TextBox.Text;
        }

        private string BuildCopyAllText()
        {
            System.Text.StringBuilder text = new System.Text.StringBuilder();
            if (HasRealResult(_googleText))
            {
                text.AppendLine("Google");
                text.AppendLine(_googleText);
            }
            if (HasRealResult(_bingText))
            {
                if (text.Length > 0) text.AppendLine();
                text.AppendLine("Bing");
                text.AppendLine(_bingText);
            }
            return text.ToString().Trim();
        }

        private void UpdateButtons()
        {
            bool hasSource = !string.IsNullOrWhiteSpace(SourceText) && !_sourceBox.ReadOnly;
            _translateButton.Enabled = hasSource;
            _speakSourceButton.Enabled = hasSource;
            _copySourceButton.Enabled = hasSource;
            _pasteButton.Enabled = !_sourceBox.ReadOnly;
            _normalizeButton.Enabled = hasSource;
            _clearButton.Enabled = hasSource;
            _saveButton.Enabled = hasSource;
            _copyAllButton.Enabled = HasRealResult(_googleText) || HasRealResult(_bingText);
            UpdateResultButtons(_google, _googleText);
            UpdateResultButtons(_bing, _bingText);
        }

        private static void UpdateResultButtons(ResultCardUi ui, string text)
        {
            bool hasResult = HasRealResult(text);
            ui.SpeakButton.Enabled = hasResult;
            ui.CopyButton.Enabled = hasResult;
            ui.BackButton.Enabled = hasResult;
            ui.RetryButton.Enabled = true;
        }

        private static bool HasRealResult(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            return text != "正在翻译..." && text != "等待结果...";
        }

        private void TogglePinned()
        {
            _pinned = !_pinned;
            _pinButton.Text = _pinned ? "📍" : "📌";
            _pinButton.BackColor = _pinned ? Color.FromArgb(225, 235, 255) : Color.White;
            _statusLabel.Text = _pinned ? "已钉住" : "Google + Bing";
        }

        private void SetSourceText(string text)
        {
            _sourceText = text ?? "";
            if (string.Equals(_sourceBox.Text, _sourceText, StringComparison.Ordinal)) return;
            _settingSourceText = true;
            try
            {
                _sourceBox.Text = _sourceText;
                _sourceBox.SelectionStart = _sourceBox.TextLength;
            }
            finally
            {
                _settingSourceText = false;
            }
        }

        private void ShowPopup(bool activate)
        {
            _showWithoutActivation = !activate;
            if (!_hasLocation)
            {
                MoveNearCursor();
                _hasLocation = true;
            }
            if (!Visible)
            {
                if (activate)
                {
                    Show();
                    Activate();
                    _sourceBox.Focus();
                }
                else
                {
                    Show();
                }
            }
            else if (activate)
            {
                Activate();
                _sourceBox.Focus();
            }
        }

        private void OnSourceBoxKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                _editDebounceTimer.Stop();
                RaiseTranslateTextRequested();
            }
        }

        private void OnSourceBoxTextChanged(object sender, EventArgs e)
        {
            if (_settingSourceText || _sourceBox.ReadOnly) return;
            _sourceText = _sourceBox.Text.Trim();
            UpdateDirectionLabels();
            UpdateButtons();
            _editDebounceTimer.Stop();
            if (!string.IsNullOrWhiteSpace(_sourceText)) _editDebounceTimer.Start();
        }

        private void OnLanguageModeChanged(object sender, EventArgs e)
        {
            if (_updatingLanguageMode) return;
            UpdateDirectionLabels();
            if (Visible && !_sourceBox.ReadOnly && !string.IsNullOrWhiteSpace(SourceText))
            {
                _editDebounceTimer.Stop();
                RaiseTranslateTextRequested();
            }
        }

        private TranslationLanguageMode GetLanguageMode()
        {
            if (_languageModeBox == null) return TranslationLanguageMode.Auto;
            if (_languageModeBox.SelectedIndex == 1) return TranslationLanguageMode.EnglishToChinese;
            if (_languageModeBox.SelectedIndex == 2) return TranslationLanguageMode.ChineseToEnglish;
            return TranslationLanguageMode.Auto;
        }

        private void SetLanguageMode(TranslationLanguageMode mode)
        {
            if (_languageModeBox == null) return;
            int index = mode == TranslationLanguageMode.EnglishToChinese ? 1
                : mode == TranslationLanguageMode.ChineseToEnglish ? 2
                : 0;
            if (_languageModeBox.SelectedIndex == index) return;
            _updatingLanguageMode = true;
            try
            {
                _languageModeBox.SelectedIndex = index;
            }
            finally
            {
                _updatingLanguageMode = false;
            }
            UpdateDirectionLabels();
        }

        private void SwapLanguageMode()
        {
            TranslationLanguageMode mode = GetLanguageMode();
            if (mode == TranslationLanguageMode.EnglishToChinese)
            {
                SetLanguageMode(TranslationLanguageMode.ChineseToEnglish);
            }
            else if (mode == TranslationLanguageMode.ChineseToEnglish)
            {
                SetLanguageMode(TranslationLanguageMode.EnglishToChinese);
            }
            else
            {
                TranslationTarget target = TranslationService.DetectTarget(SourceText, TranslationLanguageMode.Auto);
                SetLanguageMode(target.GoogleCode == "en"
                    ? TranslationLanguageMode.EnglishToChinese
                    : TranslationLanguageMode.ChineseToEnglish);
            }
            if (!_sourceBox.ReadOnly && !string.IsNullOrWhiteSpace(SourceText)) RaiseTranslateTextRequested();
        }

        private void RaiseTranslateTextRequested()
        {
            _sourceText = _sourceBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(_sourceText)) return;
            EventHandler handler = TranslateTextRequested;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void SetToolTips()
        {
            _toolTip.SetToolTip(_languageModeBox, "选择自动识别、英译中或中译英");
            _toolTip.SetToolTip(_pinButton, "置顶固定，固定后点击外部不会关闭");
            _toolTip.SetToolTip(_closeButton, "关闭翻译窗口");
            _toolTip.SetToolTip(_speakSourceButton, "朗读输入文本");
            _toolTip.SetToolTip(_copySourceButton, "复制输入文本");
            _toolTip.SetToolTip(_pasteButton, "粘贴剪贴板文本并翻译");
            _toolTip.SetToolTip(_normalizeButton, "整理换行和 PDF 断词");
            _toolTip.SetToolTip(_clearButton, "清空输入文本");
            _toolTip.SetToolTip(_translateButton, "重新翻译当前输入");
            _toolTip.SetToolTip(_swapLabel, "反转翻译方向");
            _toolTip.SetToolTip(_copyAllButton, "复制全部结果");
            _toolTip.SetToolTip(_saveButton, "把当前输入保存为单词卡");
            _toolTip.SetToolTip(_resizeGrip, "拖动调整窗口大小");
            SetResultToolTips(_google);
            SetResultToolTips(_bing);
        }

        private void SetResultToolTips(ResultCardUi ui)
        {
            _toolTip.SetToolTip(ui.SpeakButton, "朗读结果");
            _toolTip.SetToolTip(ui.CopyButton, "复制结果");
            _toolTip.SetToolTip(ui.BackButton, "把结果放回输入框并反向翻译");
            _toolTip.SetToolTip(ui.RetryButton, "重新请求翻译");
            _toolTip.SetToolTip(ui.CollapseButton, "折叠或展开结果");
        }

        private void ToggleCard(ResultCardUi ui)
        {
            ui.Collapsed = !ui.Collapsed;
            ui.CollapseButton.Text = ui.Collapsed ? "⌄" : "⌃";
            LayoutControls();
        }

        private void CopyResult(string text)
        {
            if (HasRealResult(text)) Clipboard.SetText(text);
        }

        private void TranslateBack(string text)
        {
            if (!HasRealResult(text)) return;
            SetSourceText(text);
            RaiseTranslateTextRequested();
        }

        private void SpeakText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            try
            {
                Type type = Type.GetTypeFromProgID("SAPI.SpVoice");
                if (type == null) throw new InvalidOperationException("SAPI not available.");
                object voice = Activator.CreateInstance(type);
                type.InvokeMember("Speak", System.Reflection.BindingFlags.InvokeMethod, null, voice, new object[] { text, 1 });
            }
            catch
            {
                System.Media.SystemSounds.Beep.Play();
            }
        }

        private static string NormalizeSourceText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            string value = text.Replace("-\r\n", "").Replace("-\n", "").Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
            while (value.IndexOf("  ", StringComparison.Ordinal) >= 0) value = value.Replace("  ", " ");
            return value.Trim();
        }

        private void OnOutsideClickTimerTick(object sender, EventArgs e)
        {
            bool mouseDown = IsAnyMouseButtonDown();
            if (Visible && !_pinned && mouseDown && !_lastMouseDown && !Bounds.Contains(Cursor.Position))
            {
                Hide();
            }
            _lastMouseDown = mouseDown;
        }

        private void WireDrag(Control control)
        {
            control.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                if (_resizing) return;
                _dragging = true;
                _dragStartMouse = Cursor.Position;
                _dragStartLocation = Location;
            };
            control.MouseMove += delegate
            {
                if (!_dragging) return;
                Point p = Cursor.Position;
                Location = new Point(_dragStartLocation.X + p.X - _dragStartMouse.X, _dragStartLocation.Y + p.Y - _dragStartMouse.Y);
            };
            control.MouseUp += delegate { _dragging = false; };
        }

        private void OnResizeGripMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            _resizing = true;
            _dragging = false;
            _resizeStartMouse = Cursor.Position;
            _resizeStartSize = Size;
        }

        private void OnResizeGripMouseMove(object sender, MouseEventArgs e)
        {
            if (!_resizing) return;
            Point p = Cursor.Position;
            int width = Math.Max(MinimumSize.Width, _resizeStartSize.Width + p.X - _resizeStartMouse.X);
            int height = Math.Max(MinimumSize.Height, _resizeStartSize.Height + p.Y - _resizeStartMouse.Y);
            Size = new Size(width, height);
        }

        private ResultCardUi MakeResultCard(string title)
        {
            ResultCardUi ui = new ResultCardUi();
            ui.Card = new RoundedPanel { BackColor = Color.White, Radius = 12 };
            ui.Header = new Panel { BackColor = Color.FromArgb(244, 246, 249) };
            ui.Card.Controls.Add(ui.Header);

            ui.Title = new Label();
            ui.Title.Text = title;
            ui.Title.AutoSize = false;
            ui.Title.TextAlign = ContentAlignment.MiddleLeft;
            ui.Title.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
            ui.Title.ForeColor = Color.FromArgb(42, 48, 58);
            ui.Card.Controls.Add(ui.Title);

            ui.SpeakButton = MakeCardButton("🔊");
            ui.SpeakButton.Click += delegate { SpeakText(ui.TextBox.Text); };
            ui.Card.Controls.Add(ui.SpeakButton);

            ui.CopyButton = MakeCardButton("⧉");
            ui.CopyButton.Click += delegate { CopyResult(ui.TextBox.Text); };
            ui.Card.Controls.Add(ui.CopyButton);

            ui.BackButton = MakeCardButton("↔");
            ui.BackButton.Click += delegate { TranslateBack(ui.TextBox.Text); };
            ui.Card.Controls.Add(ui.BackButton);

            ui.RetryButton = MakeCardButton("↻");
            ui.RetryButton.Click += delegate { RaiseTranslateTextRequested(); };
            ui.Card.Controls.Add(ui.RetryButton);

            ui.CollapseButton = MakeCardButton("⌃");
            ui.CollapseButton.Click += delegate { ToggleCard(ui); };
            ui.Card.Controls.Add(ui.CollapseButton);

            ui.TextBox = new TextBox();
            ui.TextBox.BorderStyle = BorderStyle.None;
            ui.TextBox.BackColor = ui.Card.BackColor;
            ui.TextBox.Multiline = true;
            ui.TextBox.ReadOnly = true;
            ui.TextBox.ScrollBars = ScrollBars.Vertical;
            ui.TextBox.Font = new Font("Microsoft YaHei UI", 10.5F);
            ui.TextBox.ForeColor = Color.FromArgb(35, 35, 35);
            ui.TextBox.TabStop = false;
            ui.Card.Controls.Add(ui.TextBox);
            return ui;
        }

        private static Button MakeIconButton(string text)
        {
            Button button = new Button();
            button.Text = text;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = Color.FromArgb(248, 249, 252);
            button.ForeColor = Color.FromArgb(82, 88, 98);
            button.Font = new Font("Microsoft YaHei UI", 9F);
            return button;
        }

        private static Button MakeCardButton(string text)
        {
            Button button = MakeIconButton(text);
            button.BackColor = Color.White;
            button.ForeColor = Color.FromArgb(55, 62, 72);
            return button;
        }

        private static Button MakeBottomButton(string text)
        {
            Button button = new Button();
            button.Text = text;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Color.FromArgb(216, 222, 232);
            button.BackColor = Color.White;
            button.ForeColor = Color.FromArgb(45, 52, 64);
            button.Font = new Font("Microsoft YaHei UI", 8.8F);
            return button;
        }

        private static Label MakeDirectionLabel(ContentAlignment alignment)
        {
            Label label = new Label();
            label.AutoSize = false;
            label.TextAlign = alignment;
            label.Font = new Font("Microsoft YaHei UI", 9.5F);
            label.ForeColor = Color.FromArgb(45, 52, 64);
            return label;
        }

        private static bool IsAnyMouseButtonDown()
        {
            return IsKeyDown(VkLButton) || IsKeyDown(VkRButton) || IsKeyDown(VkMButton);
        }

        private static bool IsKeyDown(int key)
        {
            return (GetAsyncKeyState(key) & unchecked((short)0x8000)) != 0;
        }

        private void MoveNearCursor()
        {
            Point p = Cursor.Position;
            Location = ClampToScreen(new Point(p.X + 16, p.Y + 16));
        }

        private Point ClampToScreen(Point p)
        {
            Screen screen = Screen.FromPoint(p);
            int x = Math.Min(Math.Max(screen.WorkingArea.Left, p.X), screen.WorkingArea.Right - Width);
            int y = Math.Min(Math.Max(screen.WorkingArea.Top, p.Y), screen.WorkingArea.Bottom - Height);
            return new Point(x, y);
        }

        private sealed class ResultCardUi
        {
            public RoundedPanel Card;
            public Panel Header;
            public Label Title;
            public Button SpeakButton;
            public Button CopyButton;
            public Button BackButton;
            public Button RetryButton;
            public Button CollapseButton;
            public TextBox TextBox;
            public bool Collapsed;
        }
    }

    internal sealed class RoundedPanel : Panel
    {
        public int Radius = 12;

        public RoundedPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            if (Width <= 0 || Height <= 0) return;
            using (GraphicsPath path = CreateRoundRectPath(new Rectangle(0, 0, Width, Height), Radius))
            {
                Region = new Region(path);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (Brush brush = new SolidBrush(BackColor))
            using (GraphicsPath path = CreateRoundRectPath(new Rectangle(0, 0, Width - 1, Height - 1), Radius))
            {
                e.Graphics.FillPath(brush, path);
            }
            base.OnPaint(e);
        }

        public static GraphicsPath CreateRoundRectPath(Rectangle bounds, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int diameter = Math.Max(1, radius * 2);
            Rectangle arc = new Rectangle(bounds.Location, new Size(diameter, diameter));
            path.AddArc(arc, 180, 90);
            arc.X = bounds.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = bounds.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}




