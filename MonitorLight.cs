// Monitor Light tray app: one hotkey turns the Wi-Fi monitor light bar on and off; the panel sets brightness and colour temperature.
// Build: build.bat (uses the .NET Framework csc that ships with Windows)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program
{
    public static readonly int ShowPanelMessage = RegisterWindowMessage("MonitorLight.ShowPanel");

    [STAThread]
    static void Main(string[] args)
    {
        bool created;
        using (var mutex = new Mutex(true, "MonitorLightTray", out created))
        {
            if (!created)
            {
                // Already running: starting the exe again opens its panel.
                PostMessage((IntPtr)0xFFFF, ShowPanelMessage, IntPtr.Zero, IntPtr.Zero);
                return;
            }
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Ui.Init();
            Application.Run(new TrayApp(Array.IndexOf(args, "/startup") >= 0));
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
}

class LampState
{
    public bool On;
    public int Brightness = 100, Temperature = 4000;

    public LampState Copy() { return (LampState)MemberwiseClone(); }
}

enum Link { Connecting, Online, Offline }

class TrayApp : ApplicationContext
{
    const string RegKey = @"Software\MonitorLight";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string AppName = "MonitorLight";
    const string DefaultUrl = "http://desk-lamp.local";   // the firmware's mDNS name (firmware/monitor-light)

    readonly NotifyIcon tray = new NotifyIcon();
    readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer();
    readonly Icon iconOn, iconOff, iconOffline;
    readonly MessageWindow messages;
    readonly PanelForm panel;
    readonly Dictionary<string, string> pending = new Dictionary<string, string>();   // changes not sent yet, newest value wins

    ToolStripMenuItem miOn, miOff;
    LampState state = new LampState();
    Link link = Link.Connecting;
    bool sending, refreshing;

    public TrayApp(bool atStartup)
    {
        string env = Environment.GetEnvironmentVariable("LAMP_URL");
        Lamp.BaseUrl = string.IsNullOrEmpty(env) ? DefaultUrl : Lamp.Normalize(env);

        iconOn = MakeIcon(Ui.Lamp, true);
        iconOff = MakeIcon(Ui.Other, false);
        iconOffline = MakeIcon(Ui.Mix(Ui.Error, Ui.Bg, 0.25f), false);

        panel = new PanelForm(this);
        BuildMenu();
        tray.Icon = iconOffline;
        tray.Visible = true;
        tray.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left) panel.Toggle();
            else if (e.Button == MouseButtons.Middle) Toggle();
        };

        messages = new MessageWindow(Toggle, () => panel.ShowNearTray());

        poll.Interval = 4000;
        poll.Tick += (s, e) => Refresh();
        poll.Start();
        Refresh();

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionEnding += OnSessionEnding;

        if (!atStartup) panel.ShowNearTray();
        if (!HotkeyOk)
            tray.ShowBalloonTip(4000, "Monitor Light", "Ctrl+Alt+L is used by another app. Use the tray icon instead.", ToolTipIcon.Warning);
    }

    public LampState State { get { return state; } }
    public Link Link { get { return link; } }
    public bool Busy { get { return sending || pending.Count > 0; } }
    public bool HotkeyOk { get { return messages.HotkeyRegistered; } }
    public string Host { get { return Lamp.Host; } }

    void BuildMenu()
    {
        var menu = new ContextMenuStrip();
        miOn = new ToolStripMenuItem("Turn on", null, (s, e) => SetOn(true));
        miOff = new ToolStripMenuItem("Turn off", null, (s, e) => SetOn(false));
        var open = new ToolStripMenuItem("Open Monitor Light", null, (s, e) => panel.ShowNearTray());
        open.Font = new Font(open.Font, FontStyle.Bold);
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miOn);
        menu.Items.Add(miOff);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (s, e) => Quit());
        menu.Opening += (s, e) => UpdateUi();
        tray.ContextMenuStrip = menu;
    }

    // ---- settings, also used by the panel ----

    public bool OffWhenPcSleeps
    {
        get { return LoadSetting("OffWhenPcSleeps", "0") == "1"; }
        set { SaveSetting("OffWhenPcSleeps", value ? "1" : "0"); }
    }

    public bool StartWithWindows
    {
        get
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(AppName) != null;
        }
        set
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (value) k.SetValue(AppName, "\"" + Application.ExecutablePath + "\" /startup");
                else k.DeleteValue(AppName, false);
            }
        }
    }

    // ---- talking to the light ----

    // Hotkey, middle-click and the big button.
    public void Toggle()
    {
        if (link == Link.Offline) { Refresh(); return; }
        SetOn(!state.On);
    }

    public void SetOn(bool on) { Change("on", on ? "true" : "false"); }

    // Moving a slider while the light is off turns it on, like a dimmer.
    public void SetBrightness(int value) { Change("brightness", value.ToString()); if (!state.On) Change("on", "true"); }
    public void SetTemperature(int value) { Change("temperature", value.ToString()); if (!state.On) Change("on", "true"); }

    void Change(string name, string value)
    {
        // Show the change at once; the light's answer confirms it.
        if (name == "on") state.On = value == "true";
        else if (name == "brightness") state.Brightness = int.Parse(value);
        else state.Temperature = int.Parse(value);
        pending[name] = value;
        UpdateUi();
        Pump();
    }

    // One request at a time (the light serves one client at a time); changes made meanwhile are merged into the next one,
    // so dragging a slider never queues up a backlog.
    async void Pump()
    {
        if (sending) return;
        sending = true;
        try
        {
            while (pending.Count > 0)
            {
                var parts = new List<string>();
                foreach (var kv in pending) parts.Add(kv.Key + "=" + kv.Value);
                pending.Clear();
                string query = string.Join("&", parts.ToArray());
                try
                {
                    var answer = await Task.Run(() => Lamp.Post(query));
                    link = Link.Online;
                    if (pending.Count == 0) state = answer;
                }
                catch (Exception ex)
                {
                    pending.Clear();
                    link = Link.Offline;
                    tray.ShowBalloonTip(3000, "Monitor Light", Lamp.Describe(ex), ToolTipIcon.Warning);
                }
            }
        }
        finally
        {
            sending = false;
            UpdateUi();
        }
    }

    public async void Refresh()
    {
        if (sending || refreshing) return;
        refreshing = true;
        try
        {
            var answer = await Task.Run(() => Lamp.Get());
            link = Link.Online;
            if (!Busy && !panel.Dragging) state = answer;
        }
        catch (Exception)
        {
            link = Link.Offline;
        }
        finally
        {
            refreshing = false;
            UpdateUi();
        }
    }

    void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend && OffWhenPcSleeps) TurnOffNow();
        else if (e.Mode == PowerModes.Resume) Refresh();
    }

    void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        if (OffWhenPcSleeps) TurnOffNow();
    }

    // Blocking on purpose: Windows is about to sleep or shut down.
    void TurnOffNow()
    {
        if (!state.On) return;
        try { state = Lamp.Post("on=false"); } catch (Exception) { }
    }

    void UpdateUi()
    {
        bool online = link == Link.Online;
        tray.Icon = !online ? iconOffline : state.On ? iconOn : iconOff;
        tray.Text = "Monitor Light - " + StatusText(link, state);
        miOn.Checked = online && state.On;
        miOff.Checked = online && !state.On;
        panel.UpdateUi();
    }

    public static string StatusText(Link link, LampState s)
    {
        if (link == Link.Connecting) return "Connecting...";
        if (link == Link.Offline) return "Not reachable";
        return s.On ? "On · " + s.Brightness + "% · " + s.Temperature + " K" : "Off";
    }

    static string LoadSetting(string name, string fallback)
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RegKey))
        {
            var v = k == null ? null : k.GetValue(name) as string;
            return v ?? fallback;
        }
    }

    static void SaveSetting(string name, string value)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(RegKey)) k.SetValue(name, value);
    }

    void Quit()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionEnding -= OnSessionEnding;
        poll.Stop();
        messages.Dispose();
        panel.Dispose();
        tray.Visible = false;
        tray.Dispose();
        ExitThread();
    }

    static Icon MakeIcon(Color bg, bool lit)
    {
        using (var bmp = new Bitmap(32, 32))
        {
            using (var g = Graphics.FromImage(bmp))
            using (var brush = new SolidBrush(bg))
            using (var path = Ui.Round(new RectangleF(0, 0, 31, 31), 7))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillPath(brush, path);
                Ui.DrawLightGlyph(g, new RectangleF(0, 0, 32, 32), Color.White, lit);
            }
            IntPtr h = bmp.GetHicon();
            var icon = (Icon)Icon.FromHandle(h).Clone();
            DestroyIcon(h);
            return icon;
        }
    }

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
}

// ---------------------------------------------------------------- the light's HTTP API (see firmware/monitor-light/monitor-light.ino)

static class Lamp
{
    public const int MinTemperature = 2700, MaxTemperature = 6500;
    static readonly object gate = new object();
    static string baseUrl = "", resolvedUrl;

    public static string BaseUrl
    {
        get { return baseUrl; }
        set { lock (gate) { baseUrl = value; resolvedUrl = null; } }
    }

    public static string Host
    {
        get
        {
            Uri u;
            return Uri.TryCreate(baseUrl, UriKind.Absolute, out u) ? u.Authority : baseUrl;
        }
    }

    public static string Normalize(string text)
    {
        string url = (text ?? "").Trim().TrimEnd('/');
        if (url.Length == 0) return "";
        if (url.IndexOf("://", StringComparison.Ordinal) < 0) url = "http://" + url;
        return url;
    }

    public static LampState Get() { return Request("GET", "/api/lamp"); }
    public static LampState Post(string query) { return Request("POST", "/api/lamp?" + query); }

    static LampState Request(string method, string path)
    {
        lock (gate)
        {
            try { return Parse(Send(method, Resolved() + path)); }
            catch (Exception)
            {
                resolvedUrl = null;   // the light may have a new IP address; look it up again next time
                throw;
            }
        }
    }

    // ".local" names go through mDNS, which can take a second or more per request; look the address up once instead.
    static string Resolved()
    {
        if (resolvedUrl != null) return resolvedUrl;
        var uri = new Uri(baseUrl);
        string url = baseUrl;
        if (uri.HostNameType == UriHostNameType.Dns && uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var ip in Dns.GetHostAddresses(uri.Host))
            {
                if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
                url = new UriBuilder(uri) { Host = ip.ToString() }.Uri.ToString().TrimEnd('/');
                break;
            }
        }
        resolvedUrl = url;
        return url;
    }

    static string Send(string method, string url)
    {
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.Method = method;
        req.Timeout = req.ReadWriteTimeout = 4000;
        req.KeepAlive = false;
        req.Proxy = null;
        if (method == "POST") req.ContentLength = 0;
        try
        {
            using (var resp = req.GetResponse())
            using (var reader = new StreamReader(resp.GetResponseStream()))
                return reader.ReadToEnd();
        }
        catch (WebException ex)
        {
            if (ex.Response == null) throw;
            string body;
            using (var reader = new StreamReader(ex.Response.GetResponseStream())) body = reader.ReadToEnd();
            var m = Regex.Match(body, "\"error\"\\s*:\\s*\"([^\"]*)\"");
            throw new InvalidOperationException(m.Success ? "The light said: " + m.Groups[1].Value : ex.Message);
        }
    }

    static LampState Parse(string json)
    {
        var on = Regex.Match(json, "\"on\"\\s*:\\s*(true|false)");
        var b = Regex.Match(json, "\"brightness\"\\s*:\\s*(\\d+)");
        var t = Regex.Match(json, "\"temperature\"\\s*:\\s*(\\d+)");
        if (!on.Success || !b.Success || !t.Success) throw new InvalidOperationException("Unexpected answer from the light.");
        return new LampState { On = on.Groups[1].Value == "true", Brightness = int.Parse(b.Groups[1].Value), Temperature = int.Parse(t.Groups[1].Value) };
    }

    public static string Describe(Exception ex)
    {
        if (ex is InvalidOperationException) return ex.Message;
        return "Can't reach " + Host + ". Check the light's power and Wi-Fi.";
    }
}

// ---------------------------------------------------------------- look and feel

static class Ui
{
    public static readonly Color Bg = Color.FromArgb(28, 28, 30);
    public static readonly Color Card = Color.FromArgb(40, 40, 43);
    public static readonly Color CardHover = Color.FromArgb(52, 52, 56);
    public static readonly Color Line = Color.FromArgb(62, 62, 66);
    public static readonly Color Fg = Color.FromArgb(242, 242, 245);
    public static readonly Color Dim = Color.FromArgb(150, 150, 158);
    public static readonly Color Lamp = Color.FromArgb(240, 162, 46);
    public static readonly Color Ink = Color.FromArgb(34, 26, 14);   // text on the lamp colour
    public static readonly Color Other = Color.FromArgb(110, 110, 118);
    public static readonly Color Error = Color.FromArgb(226, 54, 54);

    public static float Scale = 1f;
    static string textFamily = "Segoe UI", displayFamily = "Segoe UI Semibold";

    public static void Init()
    {
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) Scale = g.DpiX / 96f;
        var installed = new HashSet<string>();
        using (var fonts = new InstalledFontCollection())
            foreach (var f in fonts.Families) installed.Add(f.Name);
        if (installed.Contains("Segoe UI Variable Text")) textFamily = "Segoe UI Variable Text";
        if (installed.Contains("Segoe UI Variable Display Semibold")) displayFamily = "Segoe UI Variable Display Semibold";
        else if (installed.Contains("Segoe UI Variable Display")) displayFamily = "Segoe UI Variable Display";
    }

    public static int S(float v) { return (int)Math.Round(v * Scale); }
    public static Font Text(float pt) { return new Font(textFamily, pt); }
    public static Font Strong(float pt) { return new Font(displayFamily, pt, displayFamily.EndsWith("Semibold") ? FontStyle.Regular : FontStyle.Bold); }

    public static Color Mix(Color a, Color b, float t)
    {
        return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    // Approximate colour of white light at a colour temperature, from candle-warm to daylight-cool.
    public static Color Light(int kelvin)
    {
        Color warm = Color.FromArgb(255, 168, 88), neutral = Color.FromArgb(255, 214, 170), cool = Color.FromArgb(214, 230, 255);
        if (kelvin <= 4000) return Mix(warm, neutral, Math.Max(0, kelvin - 2700) / 1300f);
        return Mix(neutral, cool, Math.Min(1f, (kelvin - 4000) / 2500f));
    }

    public static string TemperatureName(int kelvin)
    {
        return kelvin < 3300 ? "Warm" : kelvin < 4600 ? "Neutral" : kelvin < 5800 ? "Daylight" : "Cool";
    }

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void Smooth(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
    }

    // Small monitor with the light bar on top, in a 32x32 design box scaled into `box`; used by the tray icon.
    public static void DrawLightGlyph(Graphics g, RectangleF box, Color color, bool lit)
    {
        float k = box.Width / 32f;
        Func<float, float, PointF> P = (x, y) => new PointF(box.X + x * k, box.Y + y * k);
        using (var pen = new Pen(color, 2.2f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        using (var b = new SolidBrush(color))
        {
            g.DrawRectangle(pen, box.X + 5 * k, box.Y + 12 * k, 22 * k, 11 * k);   // screen
            g.DrawLine(pen, P(16, 23), P(16, 27));                                 // neck
            g.DrawLine(pen, P(11, 27.5f), P(21, 27.5f));                           // foot
            using (var bar = Round(new RectangleF(box.X + 7 * k, box.Y + 5.5f * k, 18 * k, 4 * k), 2 * k)) g.FillPath(b, bar);
            if (lit)
                using (var ray = new Pen(color, 1.4f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(ray, P(10.5f, 15.5f), P(9, 19.5f));
                    g.DrawLine(ray, P(16, 15.5f), P(16, 19.5f));
                    g.DrawLine(ray, P(21.5f, 15.5f), P(23, 19.5f));
                }
        }
    }
}

// Base for the owner-drawn controls: double-buffered, hover/press tracking.
class SmoothControl : Control
{
    protected bool Hover, Down;

    public SmoothControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { Hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { Hover = Down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { Down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { Down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
}

class RoundButton : SmoothControl
{
    public Color Fill = Ui.Card;
    public Color TextColor = Ui.Fg;
    public string Caption2;   // optional small line under the text

    public void SetFill(Color fill, Color text) { if (fill != Fill || text != TextColor) { Fill = fill; TextColor = text; Invalidate(); } }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        Ui.Smooth(g);
        Color c = !Enabled ? Ui.Mix(Fill, BackColor, 0.55f) : Down ? Ui.Mix(Fill, Color.Black, 0.18f) : Hover ? Ui.Mix(Fill, Color.White, 0.10f) : Fill;
        using (var path = Ui.Round(new RectangleF(0, 0, Width - 1, Height - 1), Ui.S(8)))
        using (var b = new SolidBrush(c))
            g.FillPath(b, path);
        Color fg = Enabled ? TextColor : Ui.Mix(TextColor, BackColor, 0.45f);
        if (string.IsNullOrEmpty(Caption2))
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        else
        {
            int h = Height / 2;
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, Ui.S(4), Width, h), fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom);
            using (var small = Ui.Text(8f))
                TextRenderer.DrawText(g, Caption2, small, new Rectangle(0, h + Ui.S(2), Width, h - Ui.S(6)), Ui.Mix(fg, c, 0.25f), TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
        }
    }
}

// Pill that can be selected.
class Chip : SmoothControl
{
    bool selected;
    public Color Accent = Ui.Lamp;
    public bool Selected { get { return selected; } set { if (selected != value) { selected = value; Invalidate(); } } }

    public Chip(string text, Font font)
    {
        Text = text;
        Font = font;
        Size = new Size(TextRenderer.MeasureText(text, font).Width + Ui.S(34), Ui.S(30));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        Ui.Smooth(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 2, Height - 2);
        Color fill = selected ? Ui.Mix(Accent, BackColor, 0.78f) : Hover ? Ui.CardHover : Ui.Card;
        Color border = selected ? Accent : Ui.Line;
        using (var path = Ui.Round(r, Height / 2f))
        using (var b = new SolidBrush(fill))
        using (var p = new Pen(border, selected ? 1.5f : 1f))
        {
            g.FillPath(b, path);
            g.DrawPath(p, path);
        }
        int dotX = Ui.S(12), dotY = Height / 2;
        using (var b = new SolidBrush(Accent))
            g.FillEllipse(b, dotX - Ui.S(4), dotY - Ui.S(4), Ui.S(8), Ui.S(8));
        TextRenderer.DrawText(g, Text, Font, new Rectangle(Ui.S(22), 0, Width - Ui.S(26), Height), selected ? Ui.Fg : Ui.Dim,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }
}

// Horizontal slider: drag, click, mouse wheel or arrow keys. ValueChanged fires while it moves.
class Slider : SmoothControl
{
    public int Minimum, Maximum = 100, Step = 1, WheelStep = 5;
    public Color Accent = Ui.Lamp;
    public Color[] Gradient;   // when set, the whole track shows this gradient instead of a filled part
    public event EventHandler ValueChanged;
    int value;

    public bool Dragging { get; private set; }
    public int Value { get { return value; } set { int v = Clamp(value); if (v != this.value) { this.value = v; Invalidate(); } } }

    public Slider() { SetStyle(ControlStyles.Selectable, true); TabStop = true; }

    int Clamp(int v)
    {
        v = Minimum + (int)Math.Round((v - Minimum) / (double)Step) * Step;
        return Math.Max(Minimum, Math.Min(Maximum, v));
    }

    float Pad { get { return Ui.S(10); } }

    void SetFromUser(int v)
    {
        v = Clamp(v);
        if (v == value) return;
        value = v;
        Invalidate();
        if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
    }

    void SetFromX(int x)
    {
        float t = (x - Pad) / (Width - Pad * 2);
        SetFromUser(Minimum + (int)Math.Round(Math.Max(0, Math.Min(1, t)) * (Maximum - Minimum)));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (e.Button == MouseButtons.Left) { Dragging = true; SetFromX(e.X); }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e) { if (Dragging) SetFromX(e.X); base.OnMouseMove(e); }
    protected override void OnMouseUp(MouseEventArgs e) { Dragging = false; base.OnMouseUp(e); }
    protected override void OnMouseWheel(MouseEventArgs e) { SetFromUser(value + Math.Sign(e.Delta) * WheelStep); base.OnMouseWheel(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override bool IsInputKey(Keys key)
    {
        return key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down || base.IsInputKey(key);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Down) SetFromUser(value - WheelStep);
        else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Up) SetFromUser(value + WheelStep);
        else if (e.KeyCode == Keys.Home) SetFromUser(Minimum);
        else if (e.KeyCode == Keys.End) SetFromUser(Maximum);
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        Ui.Smooth(g);
        float th = Ui.S(6), cy = Height / 2f;
        var track = new RectangleF(Pad, cy - th / 2, Width - Pad * 2, th);
        float t = Maximum > Minimum ? (value - Minimum) / (float)(Maximum - Minimum) : 0;
        float x = track.X + track.Width * t;
        Color accent = Enabled ? Accent : Ui.Other;

        using (var path = Ui.Round(track, th / 2))
        {
            if (Gradient != null && Enabled)
            {
                using (var lg = new LinearGradientBrush(track, Gradient[0], Gradient[Gradient.Length - 1], 0f))
                {
                    var blend = new ColorBlend(Gradient.Length) { Colors = Gradient, Positions = new float[Gradient.Length] };
                    for (int i = 0; i < Gradient.Length; i++) blend.Positions[i] = i / (float)(Gradient.Length - 1);
                    lg.InterpolationColors = blend;
                    g.FillPath(lg, path);
                }
            }
            else
            {
                using (var b = new SolidBrush(Ui.Line)) g.FillPath(b, path);
                if (x > track.X)
                    using (var filled = Ui.Round(new RectangleF(track.X, track.Y, Math.Max(th, x - track.X), th), th / 2))
                    using (var b = new SolidBrush(accent))
                        g.FillPath(b, filled);
            }
        }

        float r = Ui.S(Hover || Dragging ? 9.5f : 9f);
        using (var b = new SolidBrush(Color.White))
            g.FillEllipse(b, x - r, cy - r, r * 2, r * 2);
        float ir = Ui.S(Dragging ? 3.5f : 5f);
        using (var b = new SolidBrush(Gradient != null ? Ui.Mix(accent, Color.Black, 0.1f) : accent))
            g.FillEllipse(b, x - ir, cy - ir, ir * 2, ir * 2);
        if (Focused && ShowFocusCues)
            using (var p = new Pen(Ui.Mix(Ui.Fg, BackColor, 0.4f), 1f))
                g.DrawEllipse(p, x - r - Ui.S(3), cy - r - Ui.S(3), (r + Ui.S(3)) * 2, (r + Ui.S(3)) * 2);
    }
}

// Windows 11 style settings row: text on the left, switch on the right.
class ToggleRow : SmoothControl
{
    bool on;
    public event EventHandler CheckedChanged;
    public bool Checked { get { return on; } set { if (on != value) { on = value; Invalidate(); } } }

    protected override void OnClick(EventArgs e)
    {
        on = !on;
        Invalidate();
        if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
        base.OnClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        Ui.Smooth(g);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width - Ui.S(56), Height), Hover ? Ui.Fg : Ui.Mix(Ui.Fg, Ui.Dim, 0.35f),
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        int tw = Ui.S(40), th = Ui.S(20);
        var track = new RectangleF(Width - tw - 1, (Height - th) / 2f, tw, th);
        using (var path = Ui.Round(track, th / 2f))
        {
            if (on) using (var b = new SolidBrush(Hover ? Ui.Mix(Ui.Lamp, Color.White, 0.1f) : Ui.Lamp)) g.FillPath(b, path);
            else using (var p = new Pen(Ui.Dim, 1.2f)) g.DrawPath(p, path);
        }
        float k = on ? Ui.S(14) : Ui.S(10);
        float cx = on ? track.Right - th / 2f : track.X + th / 2f, cy = track.Y + th / 2f;
        using (var b = new SolidBrush(on ? Ui.Ink : Ui.Dim))
            g.FillEllipse(b, cx - k / 2, cy - k / 2, k, k);
    }
}

// Picture of the monitor and its light bar: the light falling on the desk follows the brightness and colour temperature.
class LightPreview : SmoothControl
{
    LampState state = new LampState();
    Link link;
    string host = "";

    public LightPreview() { Cursor = Cursors.Default; }

    public void SetData(LampState s, Link current, string lightHost)
    {
        state = s.Copy();
        link = current;
        host = lightHost;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        Ui.Smooth(g);
        var card = new RectangleF(0, 0, Width - 1, Height - 1);
        using (var path = Ui.Round(card, Ui.S(10)))
        {
            using (var b = new SolidBrush(Ui.Card)) g.FillPath(b, path);
            g.SetClip(path);
        }

        bool lit = link == Link.Online && state.On;
        Color light = Ui.Light(state.Temperature);
        float level = lit ? 0.25f + 0.75f * state.Brightness / 100f : 0f;
        Func<float, float, PointF> P = (x, y) => new PointF(Ui.S(x), Ui.S(y));
        float desk = (Height - Ui.S(30)) / Ui.Scale;   // desk line, in design units like every coordinate below
        Color body = lit ? Ui.Mix(Ui.Dim, light, 0.25f) : Ui.Mix(Ui.Dim, Ui.Card, 0.25f);

        using (var pen = new Pen(Ui.Line, Ui.S(1.5f)))
            g.DrawLine(pen, Ui.S(18), Ui.S(desk), Ui.S(210), Ui.S(desk));

        // the monitor: screen, neck and foot
        using (var screen = Ui.Round(new RectangleF(Ui.S(34), Ui.S(46), Ui.S(160), Ui.S(56)), Ui.S(4)))
        using (var b = new SolidBrush(Ui.Mix(Ui.Card, Color.Black, 0.35f)))
        using (var p = new Pen(Ui.Line, Ui.S(1.5f)))
        {
            g.FillPath(b, screen);
            g.DrawPath(p, screen);
        }
        using (var b = new SolidBrush(Ui.Line))
        {
            g.FillRectangle(b, Ui.S(109), Ui.S(102), Ui.S(10), Ui.S(desk - 104));
            using (var foot = Ui.Round(new RectangleF(Ui.S(92), Ui.S(desk - 5), Ui.S(44), Ui.S(5)), Ui.S(2))) g.FillPath(b, foot);
        }

        if (lit)
        {
            // the bar shines down in front of the screen onto the desk
            using (var cone = new GraphicsPath())
            {
                cone.AddPolygon(new[] { P(70, 42), P(158, 42), P(206, desk), P(22, desk) });
                using (var cb = new LinearGradientBrush(new RectangleF(0, Ui.S(41), Width, Ui.S(desk - 41) + 1),
                    Color.FromArgb((int)(95 * level), light), Color.FromArgb((int)(22 * level), light), 90f))
                    g.FillPath(cb, cone);
            }
            using (var gp = new GraphicsPath())
            {
                gp.AddEllipse(new RectangleF(Ui.S(14), Ui.S(desk - 10), Ui.S(200), Ui.S(20)));
                using (var pb = new PathGradientBrush(gp) { CenterColor = Color.FromArgb((int)(160 * level), light), SurroundColors = new[] { Color.FromArgb(0, light) } })
                    g.FillPath(pb, gp);
            }
        }

        // the light bar on top of the monitor, with its clamp over the screen edge
        using (var b = new SolidBrush(body))
        {
            using (var bar = Ui.Round(new RectangleF(Ui.S(64), Ui.S(35), Ui.S(100), Ui.S(8)), Ui.S(4))) g.FillPath(b, bar);
            using (var clamp = Ui.Round(new RectangleF(Ui.S(106), Ui.S(42), Ui.S(16), Ui.S(7)), Ui.S(2))) g.FillPath(b, clamp);
        }
        if (lit)
            using (var strip = Ui.Round(new RectangleF(Ui.S(70), Ui.S(40), Ui.S(88), Ui.S(3)), Ui.S(1.5f)))
            using (var b = new SolidBrush(Color.FromArgb(Math.Min(255, (int)(120 + 135 * level)), Ui.Mix(light, Color.White, 0.5f))))
                g.FillPath(b, strip);

        // readout on the right
        g.ResetClip();
        var text = new Rectangle(Ui.S(214), 0, Width - Ui.S(228), Height);
        string big, small;
        Color bigColor;
        if (link == Link.Offline) { big = "Offline"; small = host; bigColor = Ui.Mix(Ui.Error, Ui.Fg, 0.3f); }
        else if (link == Link.Connecting) { big = "…"; small = host; bigColor = Ui.Dim; }
        else if (!state.On) { big = "Off"; small = state.Brightness + "% · " + state.Temperature + " K"; bigColor = Ui.Dim; }
        else { big = state.Brightness + "%"; small = state.Temperature + " K · " + Ui.TemperatureName(state.Temperature); bigColor = Ui.Fg; }
        using (var bigFont = Ui.Strong(20f))
        using (var smallFont = Ui.Text(8f))
        {
            int bh = TextRenderer.MeasureText(big, bigFont).Height, sh = TextRenderer.MeasureText("0", smallFont).Height;
            int top = (Height - bh - sh) / 2 - Ui.S(4);
            TextRenderer.DrawText(g, big, bigFont, new Rectangle(text.X, top, text.Width, bh), bigColor, TextFormatFlags.Right | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(g, small, smallFont, new Rectangle(text.X - Ui.S(60), top + bh, text.Width + Ui.S(60), sh), Ui.Dim,
                TextFormatFlags.Right | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
    }
}

// "Ctrl + Alt + L  to turn on / off" drawn as key caps.
class KeyHint : SmoothControl
{
    public string[] Keys = { "Ctrl", "Alt", "L" };
    public string Suffix = "to turn on / off";
    public bool Unavailable;

    public KeyHint() { Cursor = Cursors.Default; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        Ui.Smooth(g);
        if (Unavailable)
        {
            TextRenderer.DrawText(g, string.Join("+", Keys) + " is used by another app", Font, ClientRectangle, Ui.Lamp, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            return;
        }
        var parts = new List<Size>();
        int total = 0;
        foreach (var k in Keys) { var s = TextRenderer.MeasureText(k, Font); parts.Add(s); total += s.Width + Ui.S(12) + Ui.S(6); }
        int plus = TextRenderer.MeasureText("+", Font).Width;
        int suffixW = TextRenderer.MeasureText(Suffix, Font).Width;
        total += plus * (Keys.Length - 1) + Ui.S(8) + suffixW;
        int x = (Width - total) / 2, h = Ui.S(22), y = (Height - h) / 2;
        for (int i = 0; i < Keys.Length; i++)
        {
            var cap = new RectangleF(x, y, parts[i].Width + Ui.S(12), h);
            using (var path = Ui.Round(cap, Ui.S(5)))
            using (var b = new SolidBrush(Ui.Card))
            using (var p = new Pen(Ui.Line))
            {
                g.FillPath(b, path);
                g.DrawPath(p, path);
            }
            TextRenderer.DrawText(g, Keys[i], Font, Rectangle.Round(cap), Ui.Fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            x += (int)cap.Width + Ui.S(3);
            if (i < Keys.Length - 1)
            {
                TextRenderer.DrawText(g, "+", Font, new Rectangle(x, 0, plus, Height), Ui.Dim, TextFormatFlags.VerticalCenter);
                x += plus + Ui.S(3);
            }
        }
        TextRenderer.DrawText(g, Suffix, Font, new Rectangle(x + Ui.S(8), 0, suffixW + 4, Height), Ui.Dim, TextFormatFlags.VerticalCenter);
    }
}

// ---------------------------------------------------------------- panel

// Dark flyout that opens above the tray, Windows 11 style (rounded corners, shadow).
class PanelForm : Form
{
    const int W = 360;
    static readonly int[] Presets = { 2700, 4000, 5000, 6500 };
    readonly TrayApp app;
    readonly Label lblTitle, lblStatus, lblBrightness, lblBrightnessValue, lblTemperature, lblTemperatureValue;
    readonly Panel statusDot;
    readonly SmoothControl close;
    readonly LightPreview preview;
    readonly RoundButton btnMain;
    readonly Slider sliderBrightness, sliderTemperature;
    readonly List<Chip> presetChips = new List<Chip>();
    readonly ToggleRow rowOffWhenSleeps, rowStartup;
    readonly KeyHint keys;
    DateTime hiddenAt = DateTime.MinValue;
    bool updating;

    public PanelForm(TrayApp app)
    {
        this.app = app;
        Text = "Monitor Light";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Ui.Bg;
        ForeColor = Ui.Fg;
        Font = Ui.Text(9.5f);
        DoubleBuffered = true;

        lblTitle = new Label { Text = "Monitor Light", Font = Ui.Strong(15f), AutoSize = true, Location = new Point(Ui.S(18), Ui.S(14)), ForeColor = Ui.Fg };
        close = new CloseButton { Size = new Size(Ui.S(32), Ui.S(32)), Location = new Point(Ui.S(W - 44), Ui.S(12)), BackColor = Ui.Bg };
        close.Click += (s, e) => HidePanel();

        statusDot = new Panel { Size = new Size(Ui.S(10), Ui.S(10)), Location = new Point(Ui.S(21), Ui.S(56)), BackColor = Ui.Bg };
        statusDot.Paint += (s, e) =>
        {
            Ui.Smooth(e.Graphics);
            using (var b = new SolidBrush(StateColor())) e.Graphics.FillEllipse(b, 0, 0, statusDot.Width - 1, statusDot.Height - 1);
        };
        lblStatus = new Label { AutoSize = true, Font = Ui.Text(9.5f), ForeColor = Ui.Dim, Location = new Point(Ui.S(36), Ui.S(51)) };

        preview = new LightPreview { Location = new Point(Ui.S(18), Ui.S(82)), Size = new Size(Ui.S(W - 36), Ui.S(140)), BackColor = Ui.Bg };

        btnMain = new RoundButton { Font = Ui.Strong(12f), Location = new Point(Ui.S(18), Ui.S(236)), Size = new Size(Ui.S(W - 36), Ui.S(58)), BackColor = Ui.Bg };
        btnMain.Click += (s, e) => app.Toggle();

        int y = 314;
        lblBrightness = SectionLabel("BRIGHTNESS", y);
        lblBrightnessValue = ValueLabel(y);
        sliderBrightness = new Slider { Minimum = 1, Maximum = 100, WheelStep = 1, Location = new Point(Ui.S(8), Ui.S(y + 20)), Size = new Size(Ui.S(W - 16), Ui.S(32)), BackColor = Ui.Bg };
        sliderBrightness.ValueChanged += (s, e) => { if (!updating) app.SetBrightness(sliderBrightness.Value); };

        y += 66;
        lblTemperature = SectionLabel("COLOUR TEMPERATURE", y);
        lblTemperatureValue = ValueLabel(y);
        sliderTemperature = new Slider
        {
            Minimum = Lamp.MinTemperature, Maximum = Lamp.MaxTemperature, Step = 100, WheelStep = 100,
            Gradient = new[] { Ui.Light(2700), Ui.Light(4000), Ui.Light(6500) },
            Location = new Point(Ui.S(8), Ui.S(y + 20)), Size = new Size(Ui.S(W - 16), Ui.S(32)), BackColor = Ui.Bg
        };
        sliderTemperature.ValueChanged += (s, e) => { if (!updating) app.SetTemperature(sliderTemperature.Value); };

        y += 60;
        var chipFont = Ui.Text(9f);
        int x = Ui.S(18);
        foreach (int k in Presets)
        {
            var chip = new Chip(Ui.TemperatureName(k), chipFont) { Accent = Ui.Light(k), BackColor = Ui.Bg, Location = new Point(x, Ui.S(y)) };
            int kelvin = k;
            chip.Click += (s, e) => { sliderTemperature.Value = kelvin; app.SetTemperature(kelvin); };
            presetChips.Add(chip);
            x += chip.Width + Ui.S(8);
        }

        y += 30 + 22;
        rowOffWhenSleeps = new ToggleRow { Text = "Off when the PC sleeps or shuts down", Font = Ui.Text(9.5f), Location = new Point(Ui.S(18), Ui.S(y)), Size = new Size(Ui.S(W - 36), Ui.S(36)), BackColor = Ui.Bg };
        rowOffWhenSleeps.CheckedChanged += (s, e) => { if (!updating) app.OffWhenPcSleeps = rowOffWhenSleeps.Checked; };
        y += 38;
        rowStartup = new ToggleRow { Text = "Start with Windows", Font = Ui.Text(9.5f), Location = new Point(Ui.S(18), Ui.S(y)), Size = new Size(Ui.S(W - 36), Ui.S(36)), BackColor = Ui.Bg };
        rowStartup.CheckedChanged += (s, e) => { if (!updating) app.StartWithWindows = rowStartup.Checked; };
        y += 48;
        keys = new KeyHint { Font = Ui.Text(8.5f), Location = new Point(Ui.S(18), Ui.S(y)), Size = new Size(Ui.S(W - 36), Ui.S(30)), BackColor = Ui.Bg };
        y += 42;
        ClientSize = new Size(Ui.S(W), Ui.S(y));

        Controls.AddRange(new Control[] { lblTitle, close, statusDot, lblStatus, preview, btnMain,
            lblBrightness, lblBrightnessValue, sliderBrightness, lblTemperature, lblTemperatureValue, sliderTemperature,
            rowOffWhenSleeps, rowStartup, keys });
        Controls.AddRange(presetChips.ToArray());

        Deactivate += (s, e) => HidePanel();
        FormClosing += (s, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; HidePanel(); } };
        KeyPreview = true;
        KeyDown += (s, e) => { if (e.KeyCode == System.Windows.Forms.Keys.Escape) HidePanel(); };
    }

    public bool Dragging { get { return sliderBrightness.Dragging || sliderTemperature.Dragging; } }

    static Label SectionLabel(string text, int y)
    {
        return new Label { Text = text, AutoSize = true, Font = Ui.Strong(8f), ForeColor = Ui.Dim, BackColor = Ui.Bg, Location = new Point(Ui.S(18), Ui.S(y)) };
    }

    static Label ValueLabel(int y)
    {
        return new Label { AutoSize = false, TextAlign = ContentAlignment.TopRight, Font = Ui.Text(9f), ForeColor = Ui.Fg, BackColor = Ui.Bg,
            Location = new Point(Ui.S(W - 178), Ui.S(y - 2)), Size = new Size(Ui.S(160), Ui.S(20)) };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x20000;   // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2, dark = 1, border = ColorTranslator.ToWin32(Ui.Line);
        DwmSetWindowAttribute(Handle, 33, ref round, 4);    // DWMWA_WINDOW_CORNER_PREFERENCE = round (Windows 11)
        DwmSetWindowAttribute(Handle, 20, ref dark, 4);     // DWMWA_USE_IMMERSIVE_DARK_MODE
        DwmSetWindowAttribute(Handle, 34, ref border, 4);   // DWMWA_BORDER_COLOR
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using (var p = new Pen(Ui.Line))
        {
            // thin separators above the settings and the key hint
            e.Graphics.DrawLine(p, Ui.S(18), rowOffWhenSleeps.Top - Ui.S(10), ClientSize.Width - Ui.S(18), rowOffWhenSleeps.Top - Ui.S(10));
            e.Graphics.DrawLine(p, Ui.S(18), keys.Top - Ui.S(6), ClientSize.Width - Ui.S(18), keys.Top - Ui.S(6));
        }
    }

    Color StateColor()
    {
        if (app.Link == Link.Offline) return Ui.Error;
        if (app.Link == Link.Connecting) return Ui.Other;
        return app.State.On ? Ui.Light(app.State.Temperature) : Ui.Other;
    }

    public void UpdateUi()
    {
        if (!Visible) return;
        updating = true;
        var s = app.State;
        var link = app.Link;
        bool online = link == Link.Online;
        lblStatus.Text = TrayApp.StatusText(link, s);
        lblStatus.ForeColor = online && s.On ? Ui.Fg : Ui.Dim;
        statusDot.Invalidate();
        preview.SetData(s, link, app.Host);

        if (link == Link.Offline)
        {
            btnMain.Text = "Try again";
            btnMain.Caption2 = "can't reach " + app.Host;
            btnMain.SetFill(Ui.Card, Ui.Fg);
        }
        else if (s.On)
        {
            btnMain.Text = "Turn off";
            btnMain.Caption2 = s.Brightness + "% · " + s.Temperature + " K";
            btnMain.SetFill(Color.FromArgb(58, 58, 62), Ui.Fg);
        }
        else
        {
            btnMain.Text = "Turn on";
            btnMain.Caption2 = "at " + s.Brightness + "% · " + Ui.TemperatureName(s.Temperature).ToLower() + " " + s.Temperature + " K";
            btnMain.SetFill(Ui.Lamp, Ui.Ink);
        }
        btnMain.Enabled = link != Link.Connecting;
        btnMain.Invalidate();

        if (!sliderBrightness.Dragging) sliderBrightness.Value = s.Brightness;
        if (!sliderTemperature.Dragging) sliderTemperature.Value = s.Temperature;
        sliderBrightness.Accent = Ui.Mix(Ui.Lamp, Ui.Light(s.Temperature), 0.5f);
        sliderBrightness.Enabled = sliderTemperature.Enabled = online;
        sliderBrightness.Invalidate();
        lblBrightnessValue.Text = s.Brightness + "%";
        lblTemperatureValue.Text = s.Temperature + " K · " + Ui.TemperatureName(s.Temperature);
        lblBrightnessValue.ForeColor = lblTemperatureValue.ForeColor = online ? Ui.Fg : Ui.Dim;
        foreach (var chip in presetChips)
        {
            chip.Selected = online && Ui.TemperatureName(s.Temperature) == chip.Text;
            chip.Enabled = online;
        }

        rowOffWhenSleeps.Checked = app.OffWhenPcSleeps;
        rowStartup.Checked = app.StartWithWindows;
        keys.Unavailable = !app.HotkeyOk;
        keys.Invalidate();
        updating = false;
    }

    public void Toggle()
    {
        if (Visible) HidePanel();
        else if ((DateTime.Now - hiddenAt).TotalMilliseconds > 300) ShowNearTray();   // the click that just closed it via Deactivate
    }

    public void ShowNearTray()
    {
        var wa = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(wa.Right - Width - Ui.S(12), wa.Bottom - Height - Ui.S(12));
        Show();
        Activate();
        ActiveControl = btnMain;
        UpdateUi();
        app.Refresh();
    }

    void HidePanel()
    {
        if (!Visible) return;
        hiddenAt = DateTime.Now;
        Hide();
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}

class CloseButton : SmoothControl
{
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        Ui.Smooth(g);
        if (Hover)
            using (var path = Ui.Round(new RectangleF(0, 0, Width - 1, Height - 1), Ui.S(6)))
            using (var b = new SolidBrush(Ui.CardHover))
                g.FillPath(b, path);
        float c = Width / 2f, k = Ui.S(5);
        using (var p = new Pen(Hover ? Ui.Fg : Ui.Dim, Ui.S(1.4f)))
        {
            g.DrawLine(p, c - k, c - k, c + k, c + k);
            g.DrawLine(p, c - k, c + k, c + k, c - k);
        }
    }
}

// Hidden window for the global hotkey and the "open the panel" message from a second launch.
class MessageWindow : NativeWindow, IDisposable
{
    const int WM_HOTKEY = 0x0312, MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000, ID = 1;
    readonly Action onHotkey, onShowPanel;
    public bool HotkeyRegistered { get; private set; }

    public MessageWindow(Action onHotkey, Action onShowPanel)
    {
        this.onHotkey = onHotkey;
        this.onShowPanel = onShowPanel;
        CreateHandle(new CreateParams());
        HotkeyRegistered = RegisterHotKey(Handle, ID, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (int)Keys.L);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == ID) onHotkey();
        else if (m.Msg == Program.ShowPanelMessage) onShowPanel();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (HotkeyRegistered) UnregisterHotKey(Handle, ID);
        DestroyHandle();
    }

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, int mods, int vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
