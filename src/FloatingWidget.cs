using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MouseBat;

/// <summary>
/// An always-on-top pill showing the battery level, for monitors without a system tray.
/// Never takes focus unprompted, so it can't interrupt a game or whatever you're typing in.
/// </summary>
public sealed class FloatingWidget : Form
{
    private const int LogicalWidth = 82, LogicalHeight = 30;

    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExTopmost = 0x00000008, WsExToolWindow = 0x00000080, WsExNoActivate = 0x08000000;
    private const int DwmwaWindowCornerPreference = 33, DwmwcpRound = 2;

    private readonly Settings _settings;
    private readonly ToolTip _toolTip = new() { ShowAlways = true };

    private string _text = "--";
    private int? _percent;
    private IconTone _tone = IconTone.Stale;
    private bool _lightTheme;

    private bool _dragging;
    private Size _dragOffset;

    public FloatingWidget(Settings settings)
    {
        _settings = settings;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
        Cursor = Cursors.SizeAll;
        Text = "MouseBat";

        // Windows sends this when a monitor is unplugged or rearranged, and again when it
        // returns, so the widget goes back to its saved spot once that monitor is back.
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        ApplySize();
        ApplyPosition();
    }

    public void UpdateReading(string text, int? percent, IconTone tone, bool lightTheme, string status)
    {
        _text = text;
        _percent = percent;
        _tone = tone;
        _lightTheme = lightTheme;
        _toolTip.SetToolTip(this, status);
        Invalidate();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            // A popup, because Windows forces normal windows up to its minimum size (136x39).
            parameters.Style |= WsPopup;
            parameters.ExStyle |= WsExTopmost | WsExToolWindow | WsExNoActivate;
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = DwmwcpRound;
        DwmSetWindowAttribute(Handle, DwmwaWindowCornerPreference, ref round, sizeof(int));
        ApplySize(); // The monitor's real DPI is only known once the window exists.
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplySize();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _dragging = true;
            _dragOffset = new Size(Cursor.Position.X - Left, Cursor.Position.Y - Top);
        }
        else if (e.Button == MouseButtons.Right)
        {
            // A menu is dismissed by losing focus, so this window needs focus for it to close
            // when you click elsewhere. Clicking away returns focus to the other app.
            SetForegroundWindow(Handle);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) Location = Cursor.Position - _dragOffset;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        EndDrag();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        EndDrag();
    }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        _settings.WidgetX = Left;
        _settings.WidgetY = Top;
        _settings.Save();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Alt+F4 would otherwise destroy the window while the app still expects it to exist.
        if (e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
        base.OnFormClosing(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        float scale = DeviceDpi / 96f;
        int Px(float logical) => (int)Math.Round(logical * scale);

        var graphics = e.Graphics;
        graphics.Clear(_lightTheme ? Color.FromArgb(0xF3, 0xF3, 0xF3) : Color.FromArgb(0x20, 0x20, 0x20));

        var color = TrayIconRenderer.ColorFor(_tone, _lightTheme);

        // Battery outline, nub and fill
        var body = new Rectangle(Px(10), (Height - Px(12)) / 2, Px(21), Px(12));
        int stroke = Math.Max(1, Px(1));
        using (var pen = new Pen(color, stroke) { Alignment = PenAlignment.Inset })
        using (var brush = new SolidBrush(color))
        {
            graphics.DrawRectangle(pen, body.X, body.Y, body.Width - 1, body.Height - 1);
            graphics.FillRectangle(brush, body.Right, body.Y + (body.Height - Px(6)) / 2, Px(2), Px(6));

            if (_percent is { } percent && percent > 0)
            {
                int inset = stroke + Math.Max(1, Px(1));
                int maxWidth = body.Width - inset * 2;
                int fill = Math.Max(1, (int)Math.Round(maxWidth * percent / 100f));
                graphics.FillRectangle(brush, body.X + inset, body.Y + inset, fill, body.Height - inset * 2);
            }
        }

        using var font = new Font("Segoe UI Semibold", 14f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        var textBounds = new Rectangle(body.Right + Px(2), 0, Width - body.Right - Px(2) - Px(8), Height);
        TextRenderer.DrawText(graphics, _text, font, textBounds, color,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
    }

    private void ApplySize()
    {
        float scale = DeviceDpi / 96f;
        ClientSize = new Size((int)Math.Round(LogicalWidth * scale), (int)Math.Round(LogicalHeight * scale));
    }

    private void ApplyPosition()
    {
        if (_settings.WidgetX is { } x && _settings.WidgetY is { } y)
        {
            var center = new Point(x + Width / 2, y + Height / 2);
            if (Screen.AllScreens.Any(s => s.WorkingArea.Contains(center)))
            {
                Location = new Point(x, y);
                return;
            }
        }

        // First run, or the saved monitor is gone: bottom-right of the main monitor.
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 800, 600);
        Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => ApplyPosition();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
