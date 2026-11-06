using Microsoft.Win32;

namespace MouseBat;

public sealed class TrayApp : ApplicationContext
{
    private const int MaxTooltipLength = 127; // NotifyIcon throws above this.
    private static readonly int[] ThresholdChoices = [10, 15, 20, 25, 30];

    private readonly Settings _settings = Settings.Load();
    private VoltageSmoother _smoother = new();
    private readonly LowBatteryAlert _alert;
    private readonly FloatingWidget _widget;

    private readonly NotifyIcon _tray = new();
    private readonly ToolStripMenuItem _statusItem = new() { Enabled = false };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 60_000 };

    private ReadStatus _status = ReadStatus.NoReceiver;
    private string _mouseName = "Mouse";
    private int? _percent;
    private int? _millivolts;
    private bool _charging;
    private DateTime _lastSuccess;
    private bool _polling;
    private Icon? _currentIcon;

    public TrayApp()
    {
        _alert = new LowBatteryAlert(_settings.LowThreshold);

        _widget = new FloatingWidget(_settings);

        var menu = BuildMenu();
        _tray.ContextMenuStrip = menu;
        _widget.ContextMenuStrip = menu;

        UpdateTray();
        _tray.Visible = true;
        if (_settings.WidgetVisible) _widget.Show();

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
        _ = PollAsync();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Refresh now", null, async (_, _) => await PollAsync());

        var widgetItem = new ToolStripMenuItem("Show floating widget") { Checked = _settings.WidgetVisible, CheckOnClick = true };
        widgetItem.Click += (_, _) =>
        {
            _settings.WidgetVisible = widgetItem.Checked;
            _settings.Save();
            _widget.Visible = widgetItem.Checked;
        };
        menu.Items.Add(widgetItem);

        var thresholdMenu = new ToolStripMenuItem("Low battery alert");
        foreach (int choice in ThresholdChoices)
        {
            var item = new ToolStripMenuItem($"{choice}%") { Checked = choice == _settings.LowThreshold };
            item.Click += (_, _) =>
            {
                SetThreshold(choice);
                foreach (ToolStripMenuItem sibling in thresholdMenu.DropDownItems) sibling.Checked = sibling == item;
            };
            thresholdMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(thresholdMenu);

        menu.Items.Add("Send test notification", null, (_, _) =>
            _tray.ShowBalloonTip(5000, "MouseBat", "Notifications are working.", ToolTipIcon.Info));

        var autostartItem = new ToolStripMenuItem("Start with Windows") { Checked = Autostart.IsEnabled, CheckOnClick = true };
        autostartItem.Click += (_, _) => Autostart.IsEnabled = autostartItem.Checked;
        menu.Items.Add(autostartItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        return menu;
    }

    private void SetThreshold(int threshold)
    {
        _settings.LowThreshold = threshold;
        _settings.Save();
        _alert.SetThreshold(threshold);

        if (_status == ReadStatus.Ok && _percent is { } percent) NotifyIfLow(percent);
        UpdateTray();
    }

    private async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var reading = await BatteryReader.ReadAsync();
            _status = reading.Status;

            if (reading.Status == ReadStatus.Ok)
            {
                if (reading.DeviceName is { } name && name != _mouseName)
                {
                    // A different mouse answered, so readings and alerts so far don't apply.
                    _mouseName = name;
                    _smoother = new VoltageSmoother();
                    _alert.SetThreshold(_settings.LowThreshold);
                }

                _millivolts = reading.Millivolts is { } mv ? _smoother.Add(mv, reading.Charging) : null;
                _percent = reading.Percent != null && _millivolts is { } smoothed ? BatteryCurve.ToPercent(smoothed) : reading.Percent;
                _charging = reading.Charging;
                _lastSuccess = DateTime.Now;
                NotifyIfLow(_percent);
            }
            UpdateTray();
        }
        finally
        {
            _polling = false;
        }
    }

    private void NotifyIfLow(int? percent)
    {
        switch (_alert.Evaluate(percent, _charging))
        {
            case AlertLevel.Low:
                _tray.ShowBalloonTip(10_000, "Mouse battery low", $"{_mouseName} is at {percent}%.", ToolTipIcon.Warning);
                break;
            case AlertLevel.Critical:
                _tray.ShowBalloonTip(10_000, "Mouse battery critical", $"{_mouseName} is at {percent}%. Charge it soon.", ToolTipIcon.Error);
                break;
        }
    }

    private void UpdateTray()
    {
        string status = Describe();
        _statusItem.Text = status;
        string tooltip = $"{_mouseName}\n{status}";
        _tray.Text = tooltip.Length > MaxTooltipLength ? tooltip[..MaxTooltipLength] : tooltip;

        var icon = TrayIconRenderer.Render(IconText(), CurrentTone(), IsTaskbarLight(), SystemInformation.SmallIconSize.Width);
        _tray.Icon = icon;
        _currentIcon?.Dispose();
        _currentIcon = icon;

        _widget.UpdateReading(WidgetText(), _percent, CurrentTone(), IsTaskbarLight(), status);
    }

    private bool ChargingWithoutLevel => _status == ReadStatus.Ok && _charging && _percent == null;

    private string IconText() => _percent?.ToString() ?? (ChargingWithoutLevel ? "+" : "?");

    private string WidgetText() => _percent is { } percent ? $"{percent}%" : ChargingWithoutLevel ? "CHG" : "--";

    private IconTone CurrentTone()
    {
        if (_status != ReadStatus.Ok) return IconTone.Stale;
        if (_charging) return IconTone.Charging;
        if (_percent is not { } percent) return IconTone.Stale;
        if (percent <= _settings.LowThreshold / 2) return IconTone.Critical;
        return percent <= _settings.LowThreshold ? IconTone.Low : IconTone.Normal;
    }

    private string Describe() => _status switch
    {
        _ when ChargingWithoutLevel => "Charging. Battery level shows once unplugged.",
        ReadStatus.Ok =>$"{_percent}%{(_millivolts is { } mv ? $" ({mv / 1000.0:0.00} V)" : "")}{(_charging ? ", charging" : "")}",
        _ when _percent != null => $"Asleep or off. Last {_percent}% at {_lastSuccess:t}",
        ReadStatus.MouseUnreachable => "No mouse responding. Move it to wake it, or check it's supported.",
        _ => "No Logitech receiver or wired mouse found",
    };

    private static bool IsTaskbarLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is 1;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General) UpdateTray();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _timer.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _widget.Dispose();
            _currentIcon?.Dispose();
        }
        base.Dispose(disposing);
    }
}
