using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;
using Imaging = System.Drawing.Imaging;
using WinForms = System.Windows.Forms;

namespace G435Monitor;

internal sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly Dictionary<int, Drawing.Icon> _batteryIcons = new();
    private Drawing.Icon? _disconnectedIcon;
    private int? _lastPercent;
    private int? _lastMillivolts;
    private bool _balloonShown;

    public TrayIcon(Action onOpen, Action onRefresh, Action onSettings, Action onExit)
    {
        _menu = new WinForms.ContextMenuStrip();
        _menu.Items.Add(new WinForms.ToolStripMenuItem("Abrir", null, (_, _) => onOpen()));
        _menu.Items.Add(new WinForms.ToolStripMenuItem("Atualizar agora", null, (_, _) => onRefresh()));
        _menu.Items.Add(new WinForms.ToolStripMenuItem("Configurações", null, (_, _) => onSettings()));
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        _menu.Items.Add(new WinForms.ToolStripMenuItem("Sair", null, (_, _) => onExit()));

        _icon = new WinForms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "G435 Battery",
            Visible = true,
            ContextMenuStrip = _menu
        };

        _icon.DoubleClick += (_, _) => onOpen();
    }

    public void Update(
        int? percent, int? millivolts, bool connected, string? remaining = null)
    {
        if (connected && percent is int pct && millivolts is int mv)
        {
            _lastPercent = pct;
            _lastMillivolts = mv;

            _icon.Text = string.IsNullOrEmpty(remaining)
                ? $"G435: {pct}% · {mv} mV"
                : $"G435: {pct}% · {mv} mV · {remaining}";
            SetBatteryIcon(pct);
            return;
        }

        // Desconectado: nunca reaproveita o último ícone de percentual.
        _icon.Text = _lastPercent is int lastPct && _lastMillivolts is int lastMv
            ? $"G435: desconectado · última {lastPct}% · {lastMv} mV"
            : "G435: desconectado";
        _icon.Icon = GetDisconnectedIcon();
    }

    private void SetBatteryIcon(int percent)
    {
        // Passos de 10% deixam o ícone legível no tamanho da bandeja.
        int bucket = Math.Clamp(percent / 10 * 10, 0, 100);

        if (!_batteryIcons.TryGetValue(bucket, out Drawing.Icon? icon))
        {
            icon = CreateBatteryIcon(bucket);
            _batteryIcons[bucket] = icon;
        }

        _icon.Icon = icon;
    }

    private static Drawing.Icon CreateBatteryIcon(int percent)
    {
        const int size = 32;

        using var bitmap = new Drawing.Bitmap(
            size, size, Imaging.PixelFormat.Format32bppArgb);
        using Drawing.Graphics graphics = Drawing.Graphics.FromImage(bitmap);

        graphics.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(Drawing.Color.Transparent);

        bool lightTaskbar = UseLightTaskbar();
        Drawing.Color outlineColor = lightTaskbar
            ? Drawing.Color.FromArgb(0x1A, 0x22, 0x2C)
            : Drawing.Color.FromArgb(0xF2, 0xF6, 0xFA);

        Drawing.Color fillColor = percent <= 10
            ? Drawing.Color.FromArgb(0xE5, 0x39, 0x35) // vermelho
            : percent <= 30
                ? Drawing.Color.FromArgb(0xF2, 0xA9, 0x3B) // âmbar
                : Drawing.Color.FromArgb(0x4C, 0xAF, 0x50); // verde

        using var outline = new Drawing.Pen(outlineColor, 2f);
        using var fill = new Drawing.SolidBrush(fillColor);

        // Corpo da bateria (2..26 x 7..25) e conector (26..30 x 12..20).
        graphics.DrawRectangle(outline, 2, 7, 24, 18);
        graphics.FillRectangle(outline.Brush, 26, 12, 4, 8);

        // Área interna: 4..24 (20 px). Preenchimento proporcional.
        int fillWidth = (int)Math.Round(20 * (percent / 100.0));
        if (percent > 0)
            fillWidth = Math.Max(2, fillWidth);

        if (fillWidth > 0)
            graphics.FillRectangle(fill, 4, 9, fillWidth, 14);

        return ToIcon(bitmap);
    }

    private Drawing.Icon GetDisconnectedIcon()
    {
        // Um único ícone cinza com barra diagonal, reutilizado entre leituras.
        _disconnectedIcon ??= CreateDisconnectedIcon();
        return _disconnectedIcon!;
    }

    private static Drawing.Icon CreateDisconnectedIcon()
    {
        const int size = 32;

        using var bitmap = new Drawing.Bitmap(
            size, size, Imaging.PixelFormat.Format32bppArgb);
        using Drawing.Graphics graphics = Drawing.Graphics.FromImage(bitmap);

        graphics.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(Drawing.Color.Transparent);

        bool lightTaskbar = UseLightTaskbar();
        Drawing.Color outlineColor = lightTaskbar
            ? Drawing.Color.FromArgb(0x1A, 0x22, 0x2C)
            : Drawing.Color.FromArgb(0xF2, 0xF6, 0xFA);
        Drawing.Color bodyColor = lightTaskbar
            ? Drawing.Color.FromArgb(0xA6, 0xAE, 0xB8)
            : Drawing.Color.FromArgb(0x5F, 0x6E, 0x7E);

        using var outline = new Drawing.Pen(outlineColor, 2f);
        using var body = new Drawing.SolidBrush(bodyColor);

        // Corpo e conector da bateria, ambos em cinza.
        graphics.FillRectangle(body, 4, 9, 20, 14);
        graphics.DrawRectangle(outline, 2, 7, 24, 18);
        graphics.FillRectangle(outline.Brush, 26, 12, 4, 8);

        // Barra diagonal indicando desconexão.
        using var slash = new Drawing.Pen(outlineColor, 3.5f)
        {
            StartCap = Drawing2D.LineCap.Round,
            EndCap = Drawing2D.LineCap.Round
        };
        graphics.DrawLine(slash, 5, 25, 27, 7);

        return ToIcon(bitmap);
    }

    private static Drawing.Icon ToIcon(Drawing.Bitmap bitmap)
    {
        IntPtr handle = bitmap.GetHicon();
        try
        {
            using Drawing.Icon temporary = Drawing.Icon.FromHandle(handle);
            return (Drawing.Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static bool UseLightTaskbar()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            return key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public void ShowBalloonOnce()
    {
        if (_balloonShown)
            return;

        _balloonShown = true;
        _icon.ShowBalloonTip(
            timeout: 3000,
            tipTitle: "G435 Battery",
            tipText: "O app continua rodando na bandeja. Clique no ícone para abrir.",
            tipIcon: WinForms.ToolTipIcon.Info);
    }

    public void ShowBatteryNotification(string title, string message, bool warning)
    {
        _icon.ShowBalloonTip(
            timeout: 5000,
            tipTitle: title,
            tipText: message,
            tipIcon: warning ? WinForms.ToolTipIcon.Warning : WinForms.ToolTipIcon.Info);
    }

    private static Drawing.Icon LoadIcon()
    {
        try
        {
            System.Windows.Resources.StreamResourceInfo? resource =
                System.Windows.Application.GetResourceStream(
                    new Uri("app.ico", UriKind.Relative));

            if (resource?.Stream is Stream stream)
                return new Drawing.Icon(stream);
        }
        catch
        {
            // cai no fallback
        }

        try
        {
            string? exe = Environment.ProcessPath;
            if (exe is not null)
            {
                Drawing.Icon? icon = Drawing.Icon.ExtractAssociatedIcon(exe);
                if (icon is not null)
                    return icon;
            }
        }
        catch
        {
            // cai no fallback
        }

        return Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();

        foreach (Drawing.Icon icon in _batteryIcons.Values)
            icon.Dispose();

        _batteryIcons.Clear();
        _disconnectedIcon?.Dispose();
        _disconnectedIcon = null;
    }
}
