using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Speedysearch.Windows
{
    public sealed class MainForm : Form
    {
        private const int HotkeyMessage = 0x0312;
        private const int HotkeyId = 0x5353;
        private const int NonClientHitTest = 0x0084;
        private const int NonClientLeftButtonDown = 0x00A1;
        private const int HitClient = 1;
        private const int HitCaption = 2;
        private const int HitLeft = 10;
        private const int HitRight = 11;
        private const int HitTop = 12;
        private const int HitTopLeft = 13;
        private const int HitTopRight = 14;
        private const int HitBottom = 15;
        private const int HitBottomLeft = 16;
        private const int HitBottomRight = 17;
        private const int SetCueBanner = 0x1501;

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll", EntryPoint = "SendMessage")]
        private static extern IntPtr SendWindowMessage(
            IntPtr window,
            int message,
            IntPtr parameter,
            IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
        private static extern IntPtr SendTextMessage(
            IntPtr window,
            int message,
            IntPtr parameter,
            string data);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(
            int left,
            int top,
            int right,
            int bottom,
            int ellipseWidth,
            int ellipseHeight);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr value);

        [DllImport("user32.dll")]
        private static extern bool ShowScrollBar(IntPtr window, int bar, bool show);

        private readonly SearchEngine _engine;
        private readonly AppConfig _config;
        private readonly Panel _titleBar;
        private readonly Label _brand;
        private readonly Label _localBadge;
        private readonly IconButton _closeButton;
        private readonly RoundedPanel _searchBox;
        private readonly Label _searchGlyph;
        private readonly TextBox _query;
        private readonly IconButton _clearButton;
        private readonly Panel _filterBar;
        private readonly FilterChip[] _filterChips;
        private readonly Label _resultCount;
        private readonly SpinnerControl _spinner;
        private readonly Panel _content;
        private readonly FlowLayoutPanel _results;
        private readonly EmptyStateControl _emptyState;
        private readonly Panel _footer;
        private readonly Label _status;
        private readonly Label _shortcutHint;
        private readonly System.Windows.Forms.Timer _searchTimer;
        private readonly System.Windows.Forms.Timer _spinnerTimer;
        private readonly NotifyIcon _tray;
        private readonly ToolTip _toolTips;
        private readonly List<SearchResult> _renderedResults;
        private GlobalHotkey _hotkey;
        private WatcherService _watcher;
        private CancellationTokenSource _indexCancellation;
        private int _searchGeneration;
        private int _selectedIndex;
        private SearchFilter _activeFilter;
        private bool _searching;
        private bool _allowClose;

        public MainForm(SearchEngine engine, AppConfig config, bool initiallyHidden)
        {
            _engine = engine;
            _config = config;
            _renderedResults = new List<SearchResult>();
            _filterChips = new FilterChip[4];
            _selectedIndex = 0;
            _activeFilter = SearchFilter.All;
            _searching = true;

            Text = "Speedysearch";
            ClientSize = new Size(680, 620);
            MinimumSize = new Size(520, 460);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.None;
            BackColor = LauncherTheme.Surface;
            ForeColor = LauncherTheme.Foreground;
            Font = new Font("Segoe UI", 10.0f);
            KeyPreview = true;
            TopMost = config.AlwaysOnTop;
            Opacity = config.Opacity;
            ShowInTaskbar = true;
            DoubleBuffered = true;

            _titleBar = new Panel
            {
                BackColor = LauncherTheme.Surface
            };
            _brand = new Label
            {
                AutoSize = false,
                Text = "SPEEDYSEARCH",
                Font = new Font("Segoe UI Semibold", 9.5f),
                ForeColor = LauncherTheme.Foreground,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _localBadge = new Label
            {
                AutoSize = false,
                Text = "LOCAL",
                Font = new Font("Segoe UI Semibold", 7.5f),
                ForeColor = LauncherTheme.Accent,
                BackColor = Color.FromArgb(38, 58, 56),
                TextAlign = ContentAlignment.MiddleCenter
            };
            _closeButton = new IconButton { Text = "\u00D7" };
            _closeButton.Click += delegate { HideLauncher(); };
            _titleBar.Controls.Add(_brand);
            _titleBar.Controls.Add(_localBadge);
            _titleBar.Controls.Add(_closeButton);
            Controls.Add(_titleBar);

            _searchBox = new RoundedPanel
            {
                Radius = 14,
                FillColor = LauncherTheme.Input,
                BorderColor = LauncherTheme.Border
            };
            _searchGlyph = new Label
            {
                AutoSize = false,
                Text = "\u2315",
                Font = new Font("Segoe UI Symbol", 18.0f),
                ForeColor = LauncherTheme.Accent,
                BackColor = LauncherTheme.Input,
                TextAlign = ContentAlignment.MiddleCenter
            };
            _query = new TextBox
            {
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 13.0f),
                BackColor = LauncherTheme.Input,
                ForeColor = LauncherTheme.Foreground,
                ShortcutsEnabled = true,
                TabIndex = 0
            };
            _clearButton = new IconButton
            {
                Text = "\u00D7",
                Visible = false
            };
            _clearButton.Click += delegate
            {
                _query.Clear();
                _query.Focus();
            };
            _searchBox.Click += delegate { ActivateSearch(); };
            _searchGlyph.Click += delegate { ActivateSearch(); };
            _searchBox.Controls.Add(_searchGlyph);
            _searchBox.Controls.Add(_query);
            _searchBox.Controls.Add(_clearButton);
            Controls.Add(_searchBox);

            _filterBar = new Panel { BackColor = LauncherTheme.Surface };
            string[] filterNames = { "All", "Apps", "Files", "Settings" };
            for (int index = 0; index < filterNames.Length; index++)
            {
                FilterChip chip = new FilterChip
                {
                    Text = filterNames[index],
                    Selected = index == 0,
                    Tag = index
                };
                chip.Click += FilterClicked;
                _filterChips[index] = chip;
                _filterBar.Controls.Add(chip);
            }
            _resultCount = new Label
            {
                AutoSize = false,
                ForeColor = LauncherTheme.Muted,
                Font = new Font("Segoe UI", 8.5f),
                TextAlign = ContentAlignment.MiddleRight,
                Visible = false
            };
            _spinner = new SpinnerControl();
            _filterBar.Controls.Add(_resultCount);
            _filterBar.Controls.Add(_spinner);
            Controls.Add(_filterBar);

            _content = new Panel { BackColor = LauncherTheme.Surface };
            _results = new FlowLayoutPanel
            {
                Dock = DockStyle.None,
                AutoScroll = true,
                BackColor = LauncherTheme.Surface,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };
            _results.Resize += delegate { ResizeResultCards(); };
            _results.HandleCreated += delegate { HideResultsScrollbar(); };
            _emptyState = new EmptyStateControl
            {
                Dock = DockStyle.Fill,
                BackColor = LauncherTheme.Surface,
                TitleText = "Warming up the index",
                DetailText = "Your files and apps will appear here shortly.",
                HintText = "Tip: fuzzy search handles small typos."
            };
            _content.Controls.Add(_results);
            _content.Controls.Add(_emptyState);
            _emptyState.BringToFront();
            Controls.Add(_content);

            _footer = new Panel { BackColor = LauncherTheme.Surface };
            _status = new Label
            {
                AutoSize = false,
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = LauncherTheme.Muted,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _shortcutHint = new Label
            {
                AutoSize = false,
                Text = "\u21B5 open   \u2191\u2193 navigate   esc close",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = LauncherTheme.Dimmed,
                TextAlign = ContentAlignment.MiddleRight
            };
            _footer.Controls.Add(_status);
            _footer.Controls.Add(_shortcutHint);
            Controls.Add(_footer);

            _toolTips = new ToolTip
            {
                AutoPopDelay = 5000,
                InitialDelay = 350,
                ReshowDelay = 100
            };
            _toolTips.SetToolTip(_closeButton, "Close");
            _toolTips.SetToolTip(_clearButton, "Clear search");

            _searchTimer = new System.Windows.Forms.Timer { Interval = 90 };
            _searchTimer.Tick += delegate
            {
                _searchTimer.Stop();
                SearchAsync();
            };
            _spinnerTimer = new System.Windows.Forms.Timer { Interval = 40 };
            _spinnerTimer.Tick += delegate { _spinner.Advance(); };

            _query.TextChanged += QueryChanged;
            _query.KeyDown += QueryKeyDown;
            KeyDown += FormKeyDown;
            Resize += delegate
            {
                LayoutLauncher();
                ApplyRoundedRegion();
            };
            Shown += delegate
            {
                WindowsShell.EnableImmersiveDarkMode(Handle);
                ApplyRoundedRegion();
                if (initiallyHidden)
                {
                    Hide();
                }
                else
                {
                    ActivateSearch();
                }
            };
            FormClosing += MainFormClosing;
            AttachWindowDrag(_titleBar);
            AttachWindowDrag(_brand);
            AttachWindowDrag(_localBadge);

            ContextMenuStrip trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("Show Speedysearch", null, delegate { ShowLauncher(); });
            trayMenu.Items.Add("Preferences", null, delegate { ShowPreferences(); });
            trayMenu.Items.Add("Reindex", null, delegate { StartReindex(true); });
            trayMenu.Items.Add("Exit", null, delegate { ExitApplication(); });
            _tray = new NotifyIcon
            {
                Text = "Speedysearch",
                Icon = SystemIcons.Application,
                Visible = true,
                ContextMenuStrip = trayMenu
            };
            _tray.DoubleClick += delegate { ToggleLauncher(); };

            _engine.IndexChanged += delegate
            {
                if (!IsDisposed && IsHandleCreated)
                {
                    BeginInvoke((MethodInvoker)delegate { QueueSearch(); });
                }
            };

            LayoutLauncher();
            ShowSearching(true);
            _watcher = _engine.CreateWatcher();
            SearchAsync();
            if (!_engine.LoadedCachedFiles)
            {
                StartReindex(false);
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _hotkey = new GlobalHotkey(Handle, HotkeyId);
            RegisterConfiguredHotkey();
            SendTextMessage(
                _query.Handle,
                SetCueBanner,
                (IntPtr)1,
                "Search apps, files and settings\u2026");
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == HotkeyMessage && message.WParam.ToInt32() == HotkeyId)
            {
                ToggleLauncher();
            }

            base.WndProc(ref message);

            if (message.Msg == NonClientHitTest && message.Result.ToInt32() == HitClient)
            {
                int packed = unchecked((int)(long)message.LParam);
                Point screen = new Point((short)(packed & 0xffff), (short)((packed >> 16) & 0xffff));
                Point client = PointToClient(screen);
                const int grip = 7;
                bool left = client.X <= grip;
                bool right = client.X >= ClientSize.Width - grip;
                bool top = client.Y <= grip;
                bool bottom = client.Y >= ClientSize.Height - grip;
                if (left && top) message.Result = (IntPtr)HitTopLeft;
                else if (right && top) message.Result = (IntPtr)HitTopRight;
                else if (left && bottom) message.Result = (IntPtr)HitBottomLeft;
                else if (right && bottom) message.Result = (IntPtr)HitBottomRight;
                else if (left) message.Result = (IntPtr)HitLeft;
                else if (right) message.Result = (IntPtr)HitRight;
                else if (top) message.Result = (IntPtr)HitTop;
                else if (bottom) message.Result = (IntPtr)HitBottom;
            }
        }

        private void LayoutLauncher()
        {
            const int padding = 18;
            int availableWidth = Math.Max(0, ClientSize.Width - (padding * 2));
            _titleBar.SetBounds(padding, padding, availableWidth, 24);
            _brand.SetBounds(0, 0, 116, 24);
            _localBadge.SetBounds(120, 3, 45, 18);
            _closeButton.SetBounds(Math.Max(0, _titleBar.Width - 28), 0, 28, 24);

            _searchBox.SetBounds(padding, _titleBar.Bottom + 12, availableWidth, 52);
            _searchGlyph.SetBounds(10, 6, 34, 40);
            _clearButton.SetBounds(Math.Max(44, _searchBox.Width - 38), 14, 28, 24);
            int queryHeight = _query.PreferredHeight;
            _query.SetBounds(
                48,
                Math.Max(4, (_searchBox.Height - queryHeight) / 2),
                Math.Max(20, _searchBox.Width - 94),
                queryHeight);

            _filterBar.SetBounds(padding, _searchBox.Bottom + 12, availableWidth, 30);
            for (int index = 0; index < _filterChips.Length; index++)
            {
                _filterChips[index].SetBounds(index * 76, 0, 68, 30);
            }
            _resultCount.SetBounds(Math.Max(0, _filterBar.Width - 100), 0, 100, 30);
            _spinner.SetBounds(Math.Max(0, _filterBar.Width - 24), 4, 22, 22);

            _footer.SetBounds(
                padding,
                Math.Max(_filterBar.Bottom + 22, ClientSize.Height - padding - 18),
                availableWidth,
                18);
            _shortcutHint.SetBounds(Math.Max(0, _footer.Width - 265), 0, 265, 18);
            _status.SetBounds(0, 0, Math.Max(0, _footer.Width - 270), 18);

            int contentTop = _filterBar.Bottom + 12;
            int contentBottom = _footer.Top - 10;
            _content.SetBounds(
                padding,
                contentTop,
                availableWidth,
                Math.Max(40, contentBottom - contentTop));
            _results.SetBounds(
                0,
                0,
                _content.ClientSize.Width + SystemInformation.VerticalScrollBarWidth,
                _content.ClientSize.Height);
            ResizeResultCards();
        }

        private void ResizeResultCards()
        {
            int width = Math.Max(120, _content.ClientSize.Width - 1);
            foreach (Control control in _results.Controls)
            {
                control.Width = width;
            }
            HideResultsScrollbar();
        }

        private void HideResultsScrollbar()
        {
            if (_results.IsHandleCreated)
            {
                ShowScrollBar(_results.Handle, 1, false);
            }
        }

        private void ApplyRoundedRegion()
        {
            if (Width <= 0 || Height <= 0)
            {
                return;
            }
            IntPtr shape = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 44, 44);
            if (shape == IntPtr.Zero)
            {
                return;
            }
            Region replacement = Region.FromHrgn(shape);
            DeleteObject(shape);
            Region previous = Region;
            Region = replacement;
            if (previous != null)
            {
                previous.Dispose();
            }
        }

        private void AttachWindowDrag(Control control)
        {
            control.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left)
                {
                    return;
                }
                ReleaseCapture();
                SendWindowMessage(Handle, NonClientLeftButtonDown, (IntPtr)HitCaption, IntPtr.Zero);
            };
        }

        private void QueryChanged(object sender, EventArgs e)
        {
            _clearButton.Visible = _query.TextLength > 0;
            _selectedIndex = 0;
            QueueSearch();
        }

        private void FilterClicked(object sender, EventArgs e)
        {
            FilterChip selected = sender as FilterChip;
            if (selected == null)
            {
                return;
            }
            int index = Convert.ToInt32(selected.Tag);
            _activeFilter = (SearchFilter)index;
            _selectedIndex = 0;
            for (int chip = 0; chip < _filterChips.Length; chip++)
            {
                _filterChips[chip].Selected = chip == index;
            }
            QueueSearch();
            ActivateSearch();
        }

        private void RegisterConfiguredHotkey()
        {
            string normalized;
            if (!_hotkey.Register(_config.GlobalHotkey, out normalized))
            {
                _status.Text = "Shortcut " + _config.GlobalHotkey + " is unavailable.";
            }
            else
            {
                _config.GlobalHotkey = normalized;
            }
        }

        private void QueueSearch()
        {
            _searchTimer.Stop();
            _searchTimer.Start();
            ShowSearching(true);
        }

        private void SearchAsync()
        {
            _searchTimer.Stop();
            ShowSearching(true);
            int generation = Interlocked.Increment(ref _searchGeneration);
            string query = _query.Text;
            SearchFilter filter = _activeFilter;
            Task.Factory.StartNew(delegate
            {
                return _engine.Query(query, filter);
            }).ContinueWith(delegate(Task<QueryResponse> task)
            {
                if (IsDisposed || generation != _searchGeneration)
                {
                    return;
                }
                if (task.IsFaulted)
                {
                    ShowSearchError(task.Exception.GetBaseException().Message);
                    return;
                }
                RenderResults(task.Result);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void ShowSearching(bool searching)
        {
            _searching = searching;
            _spinner.Visible = searching;
            _resultCount.Visible = !searching;
            _spinnerTimer.Enabled = searching;
            if (searching && _renderedResults.Count == 0)
            {
                ShowEmptyState(
                    "Warming up the index",
                    "Your files and apps will appear here shortly.",
                    "Tip: fuzzy search handles small typos.");
            }
        }

        private void ShowSearchError(string error)
        {
            ShowSearching(false);
            ClearResultCards();
            ShowEmptyState(
                "Search is offline",
                error,
                "Try again in a moment.");
            _status.Text = "Search failed";
        }

        private void RenderResults(QueryResponse response)
        {
            ShowSearching(false);
            ClearResultCards();
            _renderedResults.AddRange(response.Results);
            _resultCount.Text = _renderedResults.Count + " results";
            if (_renderedResults.Count == 0)
            {
                if (_query.TextLength == 0)
                {
                    ShowEmptyState(
                        "Ready when you are",
                        "Type a name, file extension, or setting.",
                        "Tip: fuzzy search handles small typos.");
                }
                else
                {
                    ShowEmptyState(
                        "No matches",
                        "Try fewer words or a different filter.",
                        "Tip: fuzzy search handles small typos.");
                }
            }
            else
            {
                _emptyState.Visible = false;
                _results.Visible = true;
                for (int index = 0; index < _renderedResults.Count; index++)
                {
                    ResultCardControl card = new ResultCardControl(_renderedResults[index])
                    {
                        Tag = index,
                        Selected = index == _selectedIndex
                    };
                    card.MouseEnter += ResultHovered;
                    card.MouseClick += ResultClicked;
                    _toolTips.SetToolTip(card, "Open \u00B7 right-click to copy location");
                    _results.Controls.Add(card);
                }
                _selectedIndex = Math.Min(_selectedIndex, _renderedResults.Count - 1);
                SetSelected(_selectedIndex, false);
                ResizeResultCards();
                BeginInvoke((MethodInvoker)HideResultsScrollbar);
            }

            _status.Text = response.IndexStale
                ? "Index is refreshing"
                : "Search completed in " + Math.Round(response.LatencyMilliseconds) + " ms";
        }

        private void ClearResultCards()
        {
            _renderedResults.Clear();
            while (_results.Controls.Count > 0)
            {
                _results.Controls[0].Dispose();
            }
        }

        private void ShowEmptyState(string title, string detail, string hint)
        {
            _emptyState.TitleText = title;
            _emptyState.DetailText = detail;
            _emptyState.HintText = hint;
            _emptyState.Invalidate();
            _results.Visible = false;
            _emptyState.Visible = true;
            _emptyState.BringToFront();
        }

        private void ResultHovered(object sender, EventArgs e)
        {
            ResultCardControl card = sender as ResultCardControl;
            if (card != null)
            {
                SetSelected(Convert.ToInt32(card.Tag), false);
            }
        }

        private void ResultClicked(object sender, MouseEventArgs e)
        {
            ResultCardControl card = sender as ResultCardControl;
            if (card == null)
            {
                return;
            }
            int index = Convert.ToInt32(card.Tag);
            SetSelected(index, false);
            if (e.Button == MouseButtons.Left)
            {
                OpenSelected();
            }
            else if (e.Button == MouseButtons.Right)
            {
                CopySelectedPath();
            }
        }

        private void QueryKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down)
            {
                MoveSelection(1);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Up)
            {
                MoveSelection(-1);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Enter)
            {
                OpenSelected();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                HideLauncher();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private void FormKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                HideLauncher();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private void MoveSelection(int delta)
        {
            if (_renderedResults.Count == 0)
            {
                return;
            }
            int next = _selectedIndex + delta;
            next = Math.Max(0, Math.Min(_renderedResults.Count - 1, next));
            SetSelected(next, true);
        }

        private void SetSelected(int index, bool ensureVisible)
        {
            if (_renderedResults.Count == 0)
            {
                _selectedIndex = 0;
                return;
            }
            _selectedIndex = Math.Max(0, Math.Min(_renderedResults.Count - 1, index));
            foreach (Control control in _results.Controls)
            {
                ResultCardControl card = control as ResultCardControl;
                if (card != null)
                {
                    card.Selected = Convert.ToInt32(card.Tag) == _selectedIndex;
                }
            }
            if (ensureVisible && _selectedIndex < _results.Controls.Count)
            {
                _results.ScrollControlIntoView(_results.Controls[_selectedIndex]);
            }
        }

        private void OpenSelected()
        {
            if (_selectedIndex < 0 || _selectedIndex >= _renderedResults.Count)
            {
                return;
            }
            SearchResult selected = _renderedResults[_selectedIndex];
            try
            {
                _engine.RecordSelection(_query.Text, selected.Entry.Id, _selectedIndex + 1);
                WindowsShell.Open(selected.Entry);
                HideLauncher();
            }
            catch (Exception error)
            {
                _status.Text = "Could not open " + selected.Entry.Name;
                MessageBox.Show(
                    this,
                    error.Message,
                    "Could not open result",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void CopySelectedPath()
        {
            if (_selectedIndex < 0 || _selectedIndex >= _renderedResults.Count)
            {
                return;
            }
            string path = _renderedResults[_selectedIndex].Entry.Path;
            if (String.IsNullOrEmpty(path))
            {
                return;
            }
            try
            {
                Clipboard.SetText(path);
                _status.Text = "Location copied";
            }
            catch (Exception error)
            {
                _status.Text = "Could not copy location: " + error.Message;
            }
        }

        private void ShowPreferences()
        {
            using (SettingsForm dialog = new SettingsForm(_config))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }
            }
            try
            {
                _config.Save();
                WindowsShell.ConfigureStartup(_config.RunAtStartup);
                _engine.ReloadRankingConfiguration();
                TopMost = _config.AlwaysOnTop;
                Opacity = _config.Opacity;
                RegisterConfiguredHotkey();
                if (_watcher != null)
                {
                    _watcher.Dispose();
                }
                _watcher = _engine.CreateWatcher();
                StartReindex(true);
            }
            catch (Exception error)
            {
                MessageBox.Show(
                    this,
                    error.Message,
                    "Could not save preferences",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void StartReindex(bool force)
        {
            if (_indexCancellation != null)
            {
                if (force)
                {
                    _indexCancellation.Cancel();
                }
                else
                {
                    return;
                }
            }
            _indexCancellation = new CancellationTokenSource();
            CancellationTokenSource current = _indexCancellation;
            _status.Text = "Indexing files in the background\u2026";
            _engine.ReindexFilesAsync(current.Token).ContinueWith(delegate(Task task)
            {
                if (IsDisposed)
                {
                    return;
                }
                _indexCancellation = null;
                if (task.IsFaulted)
                {
                    _status.Text = "Indexing failed: " + task.Exception.GetBaseException().Message;
                }
                else if (task.IsCanceled)
                {
                    _status.Text = "Indexing restarted.";
                }
                else
                {
                    QueueSearch();
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void ToggleLauncher()
        {
            if (Visible && WindowState != FormWindowState.Minimized)
            {
                HideLauncher();
            }
            else
            {
                ShowLauncher();
            }
        }

        private void ShowLauncher()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
            ActivateSearch();
        }

        private void HideLauncher()
        {
            Hide();
            if (_query.TextLength > 0)
            {
                _query.Clear();
            }
        }

        public void ActivateLauncher()
        {
            if (InvokeRequired)
            {
                BeginInvoke((MethodInvoker)ActivateLauncher);
                return;
            }
            ShowLauncher();
        }

        private void ActivateSearch()
        {
            _query.Focus();
            _query.SelectAll();
        }

        private void MainFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideLauncher();
            }
        }

        private void ExitApplication()
        {
            _allowClose = true;
            Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_indexCancellation != null)
                {
                    _indexCancellation.Cancel();
                    _indexCancellation.Dispose();
                }
                if (_watcher != null) _watcher.Dispose();
                if (_hotkey != null) _hotkey.Dispose();
                if (_tray != null) _tray.Dispose();
                if (_searchTimer != null) _searchTimer.Dispose();
                if (_spinnerTimer != null) _spinnerTimer.Dispose();
                if (_toolTips != null) _toolTips.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
