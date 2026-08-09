using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ATLiveOverlay;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.ThreadException += (_, e) => Log.Exception(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Exception(e.ExceptionObject as Exception ?? new Exception("Unknown fatal error"));
        Application.Run(new OverlayApplicationContext());
    }
}

internal sealed class OverlayApplicationContext : ApplicationContext
{
    private readonly AppSettings _settings;
    private readonly List<OverlayForm> _forms = new();
    private NotifyIcon? _tray;
    private CompanionServer? _server;
    private SplashForm? _splash;
    private System.Windows.Forms.Timer? _splashTimer;

    public OverlayApplicationContext()
    {
        _settings = SettingsStore.Load();
        MigrateSettingsForV220();
        BuildTray();
        ShowSplashThenStart();
    }

    private void MigrateSettingsForV220()
    {
        if (_settings.SchemaVersion >= 220) return;

        // v2.2.0 introduces a larger top-centre default layout and unified branding.
        // Apply it once to existing saved overlays so upgrades do not keep the old small corner layout.
        foreach (var overlay in _settings.Overlays)
        {
            overlay.X = -1;
            overlay.Y = 20;
            overlay.Width = 980;
            overlay.Height = 320;
        }

        _settings.SchemaVersion = 220;
        SettingsStore.Save(_settings);
    }

    private void ShowSplashThenStart()
    {
        _splash = new SplashForm();
        _splash.Show();
        _splash.BringToFront();
        _splash.Refresh();

        _splashTimer = new System.Windows.Forms.Timer { Interval = 1800 };
        _splashTimer.Tick += (_, _) =>
        {
            _splashTimer?.Stop();
            _splashTimer?.Dispose();
            _splashTimer = null;
            _splash?.Close();
            _splash?.Dispose();
            _splash = null;
            StartServicesAndWindows();
        };
        _splashTimer.Start();
    }

    private void StartServicesAndWindows()
    {
        try
        {
            _server = new CompanionServer(this, 8765);
            _server.Start();
        }
        catch (Exception ex)
        {
            Log.Exception(ex);
        }

        if (_settings.Overlays.Count == 0)
        {
            NewOverlay();
            return;
        }

        foreach (var overlay in _settings.Overlays.Where(x => x.Enabled).ToList())
            CreateOverlay(overlay, false);
    }

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("New overlay", null, (_, _) => NewOverlay());
        menu.Items.Add("Show all", null, (_, _) => _forms.ForEach(f => f.ShowOverlay()));
        menu.Items.Add("Hide all", null, (_, _) => _forms.ForEach(f => f.HideOverlay()));
        menu.Items.Add("Reload all", null, (_, _) => _forms.ForEach(f => f.ReloadPage()));
        menu.Items.Add("Close all overlays", null, (_, _) => CloseAllOverlays());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Remote control", null, (_, _) => RemoteControlForm.ShowRemote(_server));
        menu.Items.Add("About AT LiveOverlay", null, (_, _) => AboutForm.ShowAbout());
        menu.Items.Add("Open log folder", null, (_, _) => Log.OpenFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit AT LiveOverlay", null, (_, _) => ExitApplication());

        _tray = new NotifyIcon
        {
            Text = $"AT LiveOverlay v{BuildInfo.Version}",
            Icon = Branding.LoadAppIcon(),
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) =>
        {
            if (_forms.Count == 0) NewOverlay();
            else _forms.ForEach(f => f.ShowOverlay());
        };
    }

    public IReadOnlyList<OverlayForm> Forms => _forms;

    public void NewOverlay(string? initialUrl = null)
    {
        var previous = _settings.LastUrl ?? "http://10.100.70.101:4007/timer";
        var url = initialUrl ?? UrlPrompt.Show(previous);
        if (string.IsNullOrWhiteSpace(url)) return;

        var id = _settings.NextId++;
        var model = new OverlaySettings
        {
            Id = id,
            Name = $"Overlay {id}",
            Url = url.Trim(),
            X = -1,
            Y = 20 + ((_forms.Count * 24) % 120),
            Width = 980,
            Height = 320,
            Enabled = true,
            RefreshSeconds = 0
        };
        _settings.LastUrl = model.Url;
        _settings.Overlays.Add(model);
        SettingsStore.Save(_settings);
        CreateOverlay(model, true);
    }

    private OverlayForm? CreateOverlay(OverlaySettings model, bool editMode)
    {
        try
        {
            var form = new OverlayForm(model, SaveSettings, RemoveOverlay);
            _forms.Add(form);
            form.FormClosed += (_, _) => _forms.Remove(form);
            form.Show();
            if (editMode) form.EnterMoveMode();
            return form;
        }
        catch (Exception ex)
        {
            Log.Exception(ex);
            MessageBox.Show($"Overlay #{model.Id} could not be created.\n\n{ex.Message}", "AT LiveOverlay", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return null;
        }
    }

    private void RemoveOverlay(OverlayForm form)
    {
        form.Model.Enabled = false;
        _settings.Overlays.RemoveAll(x => x.Id == form.Model.Id);
        SaveSettings();
        form.ClosePermanently();
    }

    public OverlayForm? Find(int id) => _forms.FirstOrDefault(f => f.Model.Id == id);

    public void SaveSettings()
    {
        foreach (var form in _forms) form.CaptureBounds();
        SettingsStore.Save(_settings);
    }

    private void CloseAllOverlays()
    {
        foreach (var form in _forms.ToList()) RemoveOverlay(form);
    }

    public void ExitApplication()
    {
        _server?.Dispose();
        foreach (var form in _forms.ToList()) form.CloseForExit();
        SaveSettings();
        if (_tray is not null) _tray.Visible = false;
        _tray?.Dispose();
        ExitThread();
    }
}

internal sealed class OverlayForm : Form
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExToolWindow = 0x80;
    private const int WsExNoActivate = 0x08000000;

    private readonly Action _save;
    private readonly Action<OverlayForm> _remove;
    private readonly WebView2 _webView;
    private readonly FlowLayoutPanel _toolbar;
    private readonly Button _moveButton;
    private readonly NumericUpDown _refreshBox;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly System.Windows.Forms.Timer _hideToolbarTimer;
    private readonly System.Windows.Forms.Timer _hoverWatchTimer;
    private readonly NumericUpDown _opacityBox;
    private bool _moving;
    private bool _controlsPinned;
    private bool _mouseWasInside;
    private bool _exiting;
    private DateTime _lastToolbarActivity = DateTime.UtcNow;
    private Point _lastPointerPosition;

    public OverlaySettings Model { get; }

    public OverlayForm(OverlaySettings model, Action save, Action<OverlayForm> remove)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _save = save;
        _remove = remove;

        Text = $"AT LiveOverlay - {Model.Name}";
        Icon = Branding.LoadAppIcon();
        StartPosition = FormStartPosition.Manual;
        var requestedWidth = Math.Max(520, Model.Width);
        var requestedHeight = Math.Max(190, Model.Height);
        Bounds = Model.X < 0
            ? GetTopCentreBounds(requestedWidth, requestedHeight, Model.Y)
            : ValidateBounds(new Rectangle(Model.X, Model.Y, requestedWidth, requestedHeight));
        TopMost = true;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        MinimumSize = new Size(320, 140);
        BackColor = Color.Black;

        _toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Padding = new Padding(6),
            BackColor = Color.FromArgb(235, 22, 22, 22),
            Visible = false
        };

        _moveButton = AddButton("Move", ToggleMoveMode);
        AddButton("Reload", ReloadPage);
        AddButton("URL", ChangeUrl);

        var refreshLabel = new Label
        {
            Text = "Refresh",
            AutoSize = true,
            ForeColor = Color.White,
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = new Padding(8, 9, 2, 0)
        };
        _toolbar.Controls.Add(refreshLabel);

        _refreshBox = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 3600,
            Value = Math.Clamp(Model.RefreshSeconds, 0, 3600),
            Width = 68,
            Margin = new Padding(2, 4, 2, 2)
        };
        _refreshBox.ValueChanged += (_, _) =>
        {
            Model.RefreshSeconds = (int)_refreshBox.Value;
            ConfigureRefreshTimer();
            _save();
        };
        _toolbar.Controls.Add(_refreshBox);
        _toolbar.Controls.Add(new Label
        {
            Text = "sec",
            AutoSize = true,
            ForeColor = Color.White,
            Margin = new Padding(2, 9, 8, 0)
        });

        var opacityLabel = new Label
        {
            Text = "Opacity",
            AutoSize = true,
            ForeColor = Color.White,
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = new Padding(8, 9, 2, 0)
        };
        _toolbar.Controls.Add(opacityLabel);

        _opacityBox = new NumericUpDown
        {
            Minimum = 20,
            Maximum = 100,
            Value = Math.Clamp(Model.OpacityPercent, 20, 100),
            Width = 68,
            Margin = new Padding(2, 4, 2, 2)
        };
        _opacityBox.ValueChanged += (_, _) =>
        {
            Model.OpacityPercent = (int)_opacityBox.Value;
            Opacity = Model.OpacityPercent / 100.0;
            _save();
        };
        _toolbar.Controls.Add(_opacityBox);
        _toolbar.Controls.Add(new Label
        {
            Text = "%",
            AutoSize = true,
            ForeColor = Color.White,
            Margin = new Padding(2, 9, 8, 0)
        });

        AddButton("Pin", ToggleControlsPinned);
        AddButton("About", AboutForm.ShowAbout);
        AddButton("Hide", HideOverlay);
        AddButton("Close", () => _remove(this), Color.Firebrick);

        _webView = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black };
        Controls.Add(_webView);
        Controls.Add(_toolbar);

        Opacity = Model.OpacityPercent / 100.0;

        _refreshTimer = new System.Windows.Forms.Timer();
        _refreshTimer.Tick += (_, _) => ReloadPage();
        ConfigureRefreshTimer();

        _hideToolbarTimer = new System.Windows.Forms.Timer { Interval = 150 };
        _hideToolbarTimer.Tick += (_, _) =>
        {
            if (_controlsPinned || !_toolbar.Visible) return;
            if ((DateTime.UtcNow - _lastToolbarActivity).TotalMilliseconds < 2200) return;
            HideToolbar();
        };
        _hideToolbarTimer.Start();

        void MarkToolbarActivity()
        {
            _lastToolbarActivity = DateTime.UtcNow;
            ShowToolbar();
        }

        MouseEnter += (_, _) => MarkToolbarActivity();
        MouseMove += (_, _) => MarkToolbarActivity();
        _webView.MouseEnter += (_, _) => MarkToolbarActivity();
        _webView.MouseMove += (_, _) => MarkToolbarActivity();
        _toolbar.MouseEnter += (_, _) => MarkToolbarActivity();
        _toolbar.MouseMove += (_, _) => MarkToolbarActivity();
        foreach (Control control in _toolbar.Controls)
        {
            control.MouseEnter += (_, _) => MarkToolbarActivity();
            control.MouseMove += (_, _) => MarkToolbarActivity();
        }

        // Global pointer watcher is required when the overlay is click-through and WebView2
        // does not deliver normal WinForms mouse events. The toolbar returns when the pointer
        // enters the overlay or moves near its top edge, then hides after 2.2 seconds of inactivity.
        _lastPointerPosition = Cursor.Position;
        _hoverWatchTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _hoverWatchTimer.Tick += (_, _) =>
        {
            if (!Visible || WindowState != FormWindowState.Normal) return;
            var pointer = Cursor.Position;
            var inside = Bounds.Contains(pointer);
            var moved = pointer != _lastPointerPosition;
            var local = PointToClient(pointer);
            var nearTopEdge = inside && local.Y >= 0 && local.Y <= 48;

            if (inside && (moved || !_mouseWasInside || nearTopEdge))
            {
                _lastToolbarActivity = DateTime.UtcNow;
                ShowToolbar();
                if (!_moving) SetClickThrough(false);
            }
            else if (!inside && _toolbar.Visible && !_controlsPinned)
            {
                if ((DateTime.UtcNow - _lastToolbarActivity).TotalMilliseconds >= 2200)
                    HideToolbar();
            }

            _mouseWasInside = inside;
            _lastPointerPosition = pointer;
        };
        _hoverWatchTimer.Start();

        Shown += async (_, _) => await InitializeWebViewAsync();
        Move += (_, _) => CaptureBounds();
        ResizeEnd += (_, _) => { CaptureBounds(); _save(); };
        FormClosing += OnFormClosing;
        HandleCreated += (_, _) => ApplyWindowStyles();
    }

    private Button AddButton(string text, Action action, Color? backColor = null)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            MinimumSize = new Size(74, 30),
            Margin = new Padding(3),
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = backColor ?? Color.FromArgb(55, 55, 55)
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 90);
        button.Click += (_, _) => action();
        _toolbar.Controls.Add(button);
        return button;
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            if (!await WebView2Runtime.EnsureInstalledAsync(this))
            {
                ShowWebView2Error();
                return;
            }

            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AT LiveOverlay",
                "WebView2");
            Directory.CreateDirectory(userDataFolder);
            Log.Info($"Using WebView2 user data folder: {userDataFolder}");

            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await _webView.EnsureCoreWebView2Async(environment);
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.Source = NormalizeUri(Model.Url);
        }
        catch (Exception ex)
        {
            Log.Exception(ex);
            ShowWebView2Error();
        }
    }

    private static void ShowWebView2Error()
    {
        MessageBox.Show(
            "Microsoft Edge WebView2 could not be started.\n\n" +
            "AT LiveOverlay could not install or start the bundled Microsoft Edge WebView2 Runtime.\n\n" +
            "Please run AT LiveOverlay as administrator once, or repair WebView2 from Windows Installed Apps. A detailed log is saved under %LOCALAPPDATA%\\AT LiveOverlay\\Logs.",
            "AT LiveOverlay",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private static Uri NormalizeUri(string url)
    {
        if (!url.Contains("://", StringComparison.Ordinal)) url = "http://" + url;
        return new Uri(url, UriKind.Absolute);
    }

    private static Rectangle GetTopCentreBounds(int width, int height, int topOffset)
    {
        var screen = Screen.PrimaryScreen ?? Screen.AllScreens.First();
        var area = screen.WorkingArea;
        width = Math.Min(width, Math.Max(520, area.Width - 40));
        height = Math.Min(height, Math.Max(190, area.Height - 80));
        var x = area.Left + Math.Max(0, (area.Width - width) / 2);
        var y = area.Top + Math.Max(12, topOffset);
        return new Rectangle(x, y, width, height);
    }

    private static Rectangle ValidateBounds(Rectangle requested)
    {
        return Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(requested))
            ? requested
            : new Rectangle(Screen.PrimaryScreen!.WorkingArea.Left + 50, Screen.PrimaryScreen.WorkingArea.Top + 50, requested.Width, requested.Height);
    }

    private void ShowToolbar()
    {
        if (!_toolbar.Visible)
        {
            _toolbar.Visible = true;
            _toolbar.BringToFront();
        }
    }

    private void HideToolbar()
    {
        if (_controlsPinned) return;
        _toolbar.Visible = false;
        if (!_moving) SetClickThrough(Model.ClickThrough);
    }

    private bool IsMouseOverToolbar()
    {
        if (!_toolbar.Visible) return false;
        var screenBounds = _toolbar.RectangleToScreen(_toolbar.ClientRectangle);
        return screenBounds.Contains(Cursor.Position);
    }

    private void ScheduleToolbarHide()
    {
        if (_controlsPinned) return;
        _lastToolbarActivity = DateTime.UtcNow;
    }

    private void ToggleControlsPinned()
    {
        _controlsPinned = !_controlsPinned;
        var pinButton = _toolbar.Controls.OfType<Button>().FirstOrDefault(b => b.Text is "Pin" or "Unpin");
        if (pinButton is not null) pinButton.Text = _controlsPinned ? "Unpin" : "Pin";
        if (_controlsPinned) ShowToolbar(); else { _lastToolbarActivity = DateTime.UtcNow; ScheduleToolbarHide(); }
    }

    private void ToggleMoveMode()
    {
        if (_moving) ExitMoveMode(); else EnterMoveMode();
    }

    public void EnterMoveMode()
    {
        _moving = true;
        _moveButton.Text = "Done";
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        ShowToolbar();
        SetClickThrough(false);
        Activate();
    }

    public void ExitMoveMode()
    {
        CaptureBounds();
        _moving = false;
        _moveButton.Text = "Move";
        FormBorderStyle = FormBorderStyle.None;
        SetClickThrough(Model.ClickThrough);
        _save();
        ScheduleToolbarHide();
    }

    private void ChangeUrl()
    {
        // The overlay itself is always-on-top. Without an owner, the modal URL
        // dialog can fall behind it while still blocking the UI thread, which
        // makes the application appear frozen. Keep the dialog owned and above
        // the overlay, and pause toolbar/click-through behaviour while it is open.
        var wasClickThrough = Model.ClickThrough;
        var wasTopMost = TopMost;

        try
        {
            ShowToolbar();
            SetClickThrough(false);
            Log.Info("URL dialog opened.");

            var value = UrlPrompt.Show(this, Model.Url);
            if (string.IsNullOrWhiteSpace(value))
            {
                Log.Info("URL dialog cancelled.");
                return;
            }

            Model.Url = value.Trim();
            _webView.Source = NormalizeUri(Model.Url);
            _save();
            Log.Info("URL changed successfully.");
        }
        catch (Exception ex)
        {
            Log.Exception(ex);
            MessageBox.Show(this,
                "AT LiveOverlay could not change the webpage URL.\r\n\r\n" + ex.Message,
                "AT LiveOverlay", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            TopMost = wasTopMost;
            SetClickThrough(wasClickThrough);
            Activate();
            ScheduleToolbarHide();
            Log.Info("URL dialog closed.");
        }
    }

    public void ReloadPage()
    {
        try { _webView.CoreWebView2?.Reload(); } catch (Exception ex) { Log.Exception(ex); }
    }

    public void SetOpacityPercent(int value)
    {
        value = Math.Clamp(value, 20, 100);
        Model.OpacityPercent = value;
        _opacityBox.Value = value;
        Opacity = value / 100.0;
        _save();
    }

    public void SetRefreshSeconds(int value)
    {
        value = Math.Clamp(value, 0, 3600);
        Model.RefreshSeconds = value;
        _refreshBox.Value = value;
        ConfigureRefreshTimer();
        _save();
    }

    public void SetInteractionLocked(bool locked)
    {
        Model.ClickThrough = locked;
        if (locked && _moving) ExitMoveMode();
        SetClickThrough(locked);
        _save();
    }

    private void ConfigureRefreshTimer()
    {
        _refreshTimer.Stop();
        if (Model.RefreshSeconds <= 0) return;
        _refreshTimer.Interval = Math.Clamp(Model.RefreshSeconds, 1, 3600) * 1000;
        _refreshTimer.Start();
    }

    public void ShowOverlay()
    {
        Show();
        WindowState = FormWindowState.Normal;
        TopMost = true;
    }

    public void HideOverlay() => Hide();

    public void CaptureBounds()
    {
        if (WindowState != FormWindowState.Normal) return;
        Model.X = Left;
        Model.Y = Top;
        Model.Width = Width;
        Model.Height = Height;
    }

    private void ApplyWindowStyles() => SetClickThrough(Model.ClickThrough && !_moving);

    public void SetClickThrough(bool enabled)
    {
        if (!IsHandleCreated) return;
        var style = GetWindowLong(Handle, GwlExStyle);
        style |= WsExToolWindow;
        if (enabled) style |= WsExTransparent | WsExNoActivate;
        else style &= ~(WsExTransparent | WsExNoActivate);
        SetWindowLong(Handle, GwlExStyle, style);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_exiting) return;
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            BeginInvoke(new Action(() => _remove(this)));
        }
    }

    public void ClosePermanently()
    {
        _exiting = true;
        Close();
    }

    public void CloseForExit()
    {
        _exiting = true;
        CaptureBounds();
        Close();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}

internal sealed class SplashForm : Form
{
    public SplashForm()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(15, 15, 17);
        ClientSize = new Size(820, 520);
        MinimumSize = new Size(820, 520);
        Icon = Branding.LoadAppIcon();

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            Padding = new Padding(48, 34, 48, 30),
            ColumnCount = 1,
            RowCount = 6
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 230));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));

        var logo = new PictureBox
        {
            Image = Branding.LoadLogoImage(),
            SizeMode = PictureBoxSizeMode.Zoom,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 10),
            BackColor = Color.Transparent
        };

        var title = new Label
        {
            Text = "AT LiveOverlay",
            Font = new Font("Segoe UI", 31, FontStyle.Bold),
            ForeColor = Color.White,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            AutoEllipsis = false
        };

        var subtitle = new Label
        {
            Text = "Professional Presentation Overlay",
            Font = new Font("Segoe UI", 14),
            ForeColor = Color.Gainsboro,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter
        };

        var build = new Label
        {
            Text = $"Version {BuildInfo.Version}   •   Build {BuildInfo.Build}   •   {BuildInfo.BuiltOn}",
            Font = new Font("Segoe UI", 10),
            ForeColor = Color.DarkGray,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            AutoEllipsis = false
        };

        var loadingText = new Label
        {
            Text = "Starting AT LiveOverlay...",
            Font = new Font("Segoe UI", 10),
            ForeColor = Color.Silver,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomCenter,
            Padding = new Padding(0, 0, 0, 8)
        };

        var loading = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 24,
            Dock = DockStyle.Fill,
            Margin = new Padding(50, 2, 50, 2)
        };

        layout.Controls.Add(logo, 0, 0);
        layout.Controls.Add(title, 0, 1);
        layout.Controls.Add(subtitle, 0, 2);
        layout.Controls.Add(build, 0, 3);
        layout.Controls.Add(loadingText, 0, 4);
        layout.Controls.Add(loading, 0, 5);
        Controls.Add(layout);
    }
}

internal sealed class AboutForm : Form
{
    private AboutForm()
    {
        Text = "About AT LiveOverlay";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(780, 600);
        MinimumSize = new Size(780, 600);
        MaximumSize = new Size(780, 600);
        Icon = Branding.LoadAppIcon();
        BackColor = Color.White;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 190));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(20, 20, 20),
            Padding = new Padding(34, 24, 34, 22),
            ColumnCount = 2,
            RowCount = 2
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 40));

        var logo = new PictureBox
        {
            Image = Branding.LoadLogoImage(),
            SizeMode = PictureBoxSizeMode.Zoom,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 22, 0),
            BackColor = Color.Transparent
        };
        header.SetRowSpan(logo, 2);

        var title = new Label
        {
            Text = "AT LiveOverlay",
            Font = new Font("Segoe UI", 28, FontStyle.Bold),
            ForeColor = Color.White,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft,
            AutoEllipsis = false
        };

        var subtitle = new Label
        {
            Text = "Professional Presentation Overlay",
            Font = new Font("Segoe UI", 13),
            ForeColor = Color.FromArgb(220, 220, 220),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft,
            Padding = new Padding(2, 8, 0, 0)
        };

        header.Controls.Add(logo, 0, 0);
        header.Controls.Add(title, 1, 0);
        header.Controls.Add(subtitle, 1, 1);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(38, 28, 38, 20),
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.White
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 135));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var versionPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            Margin = Padding.Empty
        };
        versionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        versionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 3; i++) versionPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33f));

        void AddDetail(string labelText, string valueText, int row)
        {
            var label = new Label
            {
                Text = labelText,
                Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(55, 55, 55),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            };
            var value = new Label
            {
                Text = valueText,
                Font = new Font("Segoe UI", 10.5f),
                ForeColor = Color.FromArgb(35, 35, 35),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = false
            };
            versionPanel.Controls.Add(label, 0, row);
            versionPanel.Controls.Add(value, 1, row);
        }

        AddDetail("Version", BuildInfo.Version, 0);
        AddDetail("Build", BuildInfo.Build, 1);
        AddDetail("Built", BuildInfo.BuiltOn, 2);

        var description = new Label
        {
            Text = "AT LiveOverlay provides borderless, always-on-top webpage overlays for PowerPoint Presenter View, OnTime timers, conference workflows and other live AV applications.\r\n\r\n© Adam Tomlinson",
            Font = new Font("Segoe UI", 10.5f),
            ForeColor = Color.FromArgb(35, 35, 35),
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = ContentAlignment.TopLeft,
            Padding = new Padding(0, 12, 0, 0)
        };

        body.Controls.Add(versionPanel, 0, 0);
        body.Controls.Add(description, 0, 1);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Color.FromArgb(245, 245, 245),
            Padding = new Padding(24, 22, 24, 18)
        };

        var ok = new Button
        {
            Text = "Close",
            DialogResult = DialogResult.OK,
            Size = new Size(125, 44),
            Margin = new Padding(10, 0, 0, 0)
        };

        var copy = new Button
        {
            Text = "Copy version info",
            Size = new Size(175, 44),
            Margin = new Padding(10, 0, 0, 0)
        };
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText($"AT LiveOverlay\r\nVersion {BuildInfo.Version}\r\nBuild {BuildInfo.Build}\r\nBuilt {BuildInfo.BuiltOn}");
                copy.Text = "Copied";
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                MessageBox.Show("The version information could not be copied.", "AT LiveOverlay", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };

        var logs = new Button
        {
            Text = "Open log folder",
            Size = new Size(160, 44),
            Margin = new Padding(10, 0, 0, 0)
        };
        logs.Click += (_, _) => Log.OpenFolder();

        footer.Controls.Add(ok);
        footer.Controls.Add(copy);
        footer.Controls.Add(logs);

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(body, 0, 1);
        root.Controls.Add(footer, 0, 2);
        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = ok;
    }

    public static void ShowAbout()
    {
        using var form = new AboutForm();
        form.ShowDialog();
    }
}

internal static class UrlPrompt
{
    public static string? Show(string previous)
    {
        return ShowInternal(null, previous);
    }

    public static string? Show(IWin32Window owner, string previous)
    {
        return ShowInternal(owner, previous);
    }

    private static string? ShowInternal(IWin32Window? owner, string previous)
    {
        using var form = new Form
        {
            Text = "AT LiveOverlay - Webpage URL",
            StartPosition = owner is null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.Dpi,
            ClientSize = new Size(650, 230),
            MinimumSize = new Size(650, 230),
            Icon = Branding.LoadAppIcon(),
            TopMost = true
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20),
            ColumnCount = 1,
            RowCount = 4
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var label = new Label
        {
            Text = "Enter the webpage URL to display:",
            AutoSize = true,
            Font = new Font("Segoe UI", 11),
            Margin = new Padding(0, 0, 0, 10)
        };
        var box = new TextBox
        {
            Text = previous,
            Dock = DockStyle.Top,
            Font = new Font("Segoe UI", 11),
            Margin = new Padding(0, 0, 0, 16)
        };
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(110, 36) };
        var ok = new Button { Text = "Open overlay", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(125, 36) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        layout.Controls.Add(label, 0, 0);
        layout.Controls.Add(box, 0, 1);
        layout.Controls.Add(new Panel(), 0, 2);
        layout.Controls.Add(buttons, 0, 3);
        form.Controls.Add(layout);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        form.Shown += (_, _) =>
        {
            form.BringToFront();
            form.Activate();
            box.SelectAll();
            box.Focus();
        };
        return (owner is null ? form.ShowDialog() : form.ShowDialog(owner)) == DialogResult.OK ? box.Text : null;
    }
}

internal sealed class RemoteControlForm : Form
{
    private RemoteControlForm(CompanionServer? server)
    {
        Text = "AT LiveOverlay - Remote Control";
        Icon = Branding.LoadAppIcon();
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(760, 650);
        MinimumSize = new Size(680, 560);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 11);

        var localIp = CompanionServer.GetPreferredLocalIp();
        var baseUrl = $"http://{localIp}:8765";
        var status = server?.IsRunning == true ? "Running" : "Not running";

        var title = new Label { Text = "Remote control", AutoSize = true, Font = new Font("Segoe UI Semibold", 20), ForeColor = Color.FromArgb(238, 124, 25) };
        var info = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(690, 0),
            Text = $"Bitfocus Companion can control this computer using HTTP GET requests.\n\n" +
                   $"Status: {status}\nBase URL: {baseUrl}\n\n" +
                   "/status\n" +
                   "/overlay/1/show\n/overlay/1/hide\n/overlay/1/reload\n" +
                   "/overlay/1/edit\n/overlay/1/live\n/overlay/1/lock\n/overlay/1/unlock\n" +
                   "/overlay/1/close\n/overlay/1/seturl?url=http%3A%2F%2Fserver%2Ftimer\n" +
                   "/overlay/1/opacity?value=75\n/overlay/1/refresh?seconds=30\n" +
                   "/overlay/create?url=http%3A%2F%2Fserver%2Ftimer\n" +
                   "/overlay/all/show\n/overlay/all/hide\n/overlay/all/reload\n\n" +
                   "Allow TCP port 8765 through Windows Firewall for control from another device."
        };

        var firewall = new Button { Text = "Enable Windows Firewall access", AutoSize = true, MinimumSize = new Size(250, 42) };
        firewall.Click += (_, _) => EnableFirewall();
        var copy = new Button { Text = "Copy base URL", AutoSize = true, MinimumSize = new Size(150, 42) };
        copy.Click += (_, _) => { try { Clipboard.SetText(baseUrl); } catch { } };
        var close = new Button { Text = "Close", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(120, 42) };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = true };
        buttons.Controls.Add(close); buttons.Controls.Add(copy); buttons.Controls.Add(firewall);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28), ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(title, 0, 0);
        layout.Controls.Add(info, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);
        AcceptButton = close;
    }

    private static void EnableFirewall()
    {
        try
        {
            var args = "advfirewall firewall add rule name=\"AT LiveOverlay Companion\" dir=in action=allow protocol=TCP localport=8765 profile=private";
            Process.Start(new ProcessStartInfo("netsh", args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "AT LiveOverlay", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    public static void ShowRemote(CompanionServer? server)
    {
        using var form = new RemoteControlForm(server);
        form.ShowDialog();
    }
}

internal sealed class CompanionServer : IDisposable
{
    private readonly OverlayApplicationContext _app;
    private readonly System.Net.Sockets.TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly int _port;
    private volatile bool _isRunning;

    public CompanionServer(OverlayApplicationContext app, int port)
    {
        _app = app;
        _port = port;
        _listener = new System.Net.Sockets.TcpListener(IPAddress.Any, port);
    }

    public bool IsRunning => _isRunning;

    public static string GetPreferredLocalIp()
    {
        try
        {
            return Dns.GetHostEntry(Dns.GetHostName()).AddressList
                .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(a => a.ToString())
                .FirstOrDefault(a => !a.StartsWith("127.")) ?? "localhost";
        }
        catch { return "localhost"; }
    }

    public void Start()
    {
        if (_isRunning) return;
        _listener.Start();
        _isRunning = true;
        Log.Info($"Companion remote-control server started automatically on TCP port {_port}.");
        _ = Task.Run(ListenLoop);
    }

    private async Task ListenLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => HandleClient(client));
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_cts.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Log.Exception(ex);
                await Task.Delay(1000);
            }
        }
    }

    private async Task HandleClient(System.Net.Sockets.TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);

                var requestLine = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(requestLine)) return;

                // Consume HTTP headers.
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync())) { }

                var requestParts = requestLine.Split(' ');
                if (requestParts.Length < 2 || !requestParts[0].Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteResponse(stream, 405, "text/plain", "Only HTTP GET is supported.");
                    return;
                }

                var target = requestParts[1];
                if (!Uri.TryCreate("http://localhost" + target, UriKind.Absolute, out var uri))
                {
                    await WriteResponse(stream, 400, "text/plain", "Invalid request URL.");
                    return;
                }

                var path = uri.AbsolutePath.Trim('/').ToLowerInvariant();
                var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var query = ParseQuery(uri.Query);
                var statusCode = 200;
                var contentType = "text/plain; charset=utf-8";
                string response = "OK";

                if (path == "status")
                {
                    response = JsonSerializer.Serialize(new
                    {
                        product = "AT LiveOverlay",
                        version = BuildInfo.Version,
                        build = BuildInfo.Build,
                        apiPort = _port,
                        running = IsRunning,
                        overlays = _app.Forms.Select(f => new
                        {
                            f.Model.Id, f.Model.Name, f.Model.Url,
                            visible = f.Visible,
                            locked = f.Model.ClickThrough,
                            opacity = f.Model.OpacityPercent,
                            refreshSeconds = f.Model.RefreshSeconds
                        })
                    });
                    contentType = "application/json; charset=utf-8";
                }
                else if (path == "overlay/create")
                {
                    query.TryGetValue("url", out var url);
                    RunUi(() => _app.NewOverlay(url));
                }
                else if (parts.Length >= 3 && parts[0] == "overlay" && parts[1] == "all")
                {
                    var command = parts[2];
                    RunUi(() =>
                    {
                        foreach (var form in _app.Forms.ToList())
                        {
                            if (command == "show") form.ShowOverlay();
                            else if (command == "hide") form.HideOverlay();
                            else if (command == "reload") form.ReloadPage();
                        }
                    });
                }
                else if (parts.Length >= 3 && parts[0] == "overlay" && int.TryParse(parts[1], out var id))
                {
                    var command = parts[2];
                    RunUi(() =>
                    {
                        var form = _app.Find(id);
                        if (form is null) return;
                        switch (command)
                        {
                            case "show": form.ShowOverlay(); break;
                            case "hide": form.HideOverlay(); break;
                            case "reload": form.ReloadPage(); break;
                            case "move":
                            case "edit": form.EnterMoveMode(); break;
                            case "live": form.ExitMoveMode(); form.SetInteractionLocked(true); break;
                            case "lock": form.SetInteractionLocked(true); break;
                            case "unlock": form.SetInteractionLocked(false); break;
                            case "close": form.ClosePermanently(); break;
                            case "seturl":
                                if (query.TryGetValue("url", out var newUrl) && !string.IsNullOrWhiteSpace(newUrl))
                                {
                                    form.Model.Url = newUrl;
                                    form.ReloadPage();
                                    _app.SaveSettings();
                                }
                                break;
                            case "opacity":
                                if (query.TryGetValue("value", out var opacityValue) && int.TryParse(opacityValue, out var opacity))
                                    form.SetOpacityPercent(opacity);
                                break;
                            case "refresh":
                                if (query.TryGetValue("seconds", out var secondsValue) && int.TryParse(secondsValue, out var seconds))
                                    form.SetRefreshSeconds(seconds);
                                break;
                        }
                    });
                }
                else
                {
                    statusCode = 404;
                    response = "Not found";
                }

                await WriteResponse(stream, statusCode, contentType, response);
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query)) return result;
        foreach (var item in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = item.Split('=', 2);
            var key = WebUtility.UrlDecode(pair[0]);
            var value = pair.Length > 1 ? WebUtility.UrlDecode(pair[1]) : string.Empty;
            if (!string.IsNullOrEmpty(key)) result[key] = value;
        }
        return result;
    }

    private static async Task WriteResponse(Stream stream, int statusCode, string contentType, string body)
    {
        var data = Encoding.UTF8.GetBytes(body);
        var statusText = statusCode switch { 200 => "OK", 400 => "Bad Request", 404 => "Not Found", 405 => "Method Not Allowed", _ => "Error" };
        var headers = $"HTTP/1.1 {statusCode} {statusText}\r\n" +
                      $"Content-Type: {contentType}\r\n" +
                      $"Content-Length: {data.Length}\r\n" +
                      "Access-Control-Allow-Origin: *\r\n" +
                      "Connection: close\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(headers);
        await stream.WriteAsync(headerBytes);
        await stream.WriteAsync(data);
        await stream.FlushAsync();
    }

    private static void RunUi(Action action)
    {
        var form = Application.OpenForms.Cast<Form>().FirstOrDefault();
        if (form is not null && form.InvokeRequired) form.BeginInvoke(action);
        else action();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _isRunning = false;
        try { _listener.Stop(); } catch { }
        _cts.Dispose();
    }
}

internal sealed class AppSettings
{
    public int SchemaVersion { get; set; }
    public int NextId { get; set; } = 1;
    public string? LastUrl { get; set; }
    public List<OverlaySettings> Overlays { get; set; } = new();
}

internal sealed class OverlaySettings
{
    public int Id { get; set; }
    public string Name { get; set; } = "Overlay";
    public string Url { get; set; } = "http://10.100.70.101:4007/timer";
    public int X { get; set; } = -1;
    public int Y { get; set; } = 20;
    public int Width { get; set; } = 980;
    public int Height { get; set; } = 320;
    public bool Enabled { get; set; } = true;
    public bool ClickThrough { get; set; }
    public int RefreshSeconds { get; set; }
    public int OpacityPercent { get; set; } = 100;
}

internal static class SettingsStore
{
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AT LiveOverlay");
    private static readonly string FilePath = Path.Combine(Folder, "config.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Exception(ex);
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Exception(ex); }
    }
}

internal static class Branding
{
    public static Image? LoadLogoImage()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "app.png");
            if (File.Exists(path)) return Image.FromFile(path);
        }
        catch { }
        return null;
    }

    public static Icon LoadAppIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (File.Exists(path)) return new Icon(path);
        }
        catch { }
        return SystemIcons.Application;
    }
}

internal static class WebView2Runtime
{
    public static bool IsInstalled()
    {
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            Log.Info($"Detected WebView2 Runtime: {version}");
            return !string.IsNullOrWhiteSpace(version);
        }
        catch (Exception ex)
        {
            Log.Info($"WebView2 detection did not find a runtime: {ex.Message}");
            return false;
        }
    }

    public static async Task<bool> EnsureInstalledAsync(IWin32Window owner)
    {
        if (IsInstalled()) return true;

        // AT LiveOverlay runs normally. If WebView2 is missing, only the runtime installer requests elevation.
        var installers = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "MicrosoftEdgeWebView2RuntimeInstallerX64.exe"),
            Path.Combine(AppContext.BaseDirectory, "MicrosoftEdgeWebView2Setup.exe")
        }.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (installers.Count == 0)
        {
            Log.Exception(new FileNotFoundException("No bundled WebView2 installer was found in the application folder."));
            return false;
        }

        foreach (var installer in installers)
        {
            try
            {
                Log.Info($"Starting WebView2 installer: {installer}");
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = installer,
                    Arguments = "/silent /install",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = AppContext.BaseDirectory
                });

                if (process is null)
                {
                    Log.Info("WebView2 installer process could not be started.");
                    continue;
                }

                await process.WaitForExitAsync();
                Log.Info($"WebView2 installer exited with code {process.ExitCode}.");

                // Allow Edge Update time to register the runtime before creating WebView2.
                for (var attempt = 0; attempt < 120; attempt++)
                {
                    if (IsInstalled()) return true;
                    await Task.Delay(500);
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
        }

        return IsInstalled();
    }
}

internal static class Log
{
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AT LiveOverlay", "Logs");
    private static readonly object Sync = new();

    public static void Info(string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(Path.Combine(Folder, "app.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch { }
    }

    public static void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            Process.Start(new ProcessStartInfo { FileName = Folder, UseShellExecute = true });
        }
        catch { }
    }

    public static void Exception(Exception ex)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(Path.Combine(Folder, "crash.log"), $"{Environment.NewLine}============================================================{Environment.NewLine}{DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{ex}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
