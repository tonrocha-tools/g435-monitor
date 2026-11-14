using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace G435Monitor;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly BatteryService _service = new();
    private readonly DispatcherTimer _timer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private AppSettings _settings;
    private TrayIcon? _tray;
    private DateTime _lastRefresh = DateTime.MinValue;
    private BatteryReading? _last;
    private bool _exitRequested;
    private bool _disposed;
    private bool _settingsOpen;
    private int? _displayedPct;
    private readonly List<(DateTime Time, int Mv)> _voltageHistory = new();
    private bool _inferredCharging;
    private int _chargingPeakMv;
    private int? _frozenPct;
    private bool _wasCharging;

    public MainWindow() : this(new AppSettings())
    {
    }

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(_settings.IntervalMinutes)
        };
        _timer.Tick += async (_, _) => await RefreshAsync();

        _tray = new TrayIcon(
            onOpen: () => Dispatcher.BeginInvoke(new Action(ShowFromTray)),
            onRefresh: () => Dispatcher.BeginInvoke(
                new Action(async () => await RequestManualRefreshAsync())),
            onSettings: () => Dispatcher.BeginInvoke(new Action(OpenSettings)),
            onExit: () => Dispatcher.BeginInvoke(new Action(ExitApp)));

        Loaded += async (_, _) =>
        {
            RestartTimer();
            await RefreshAsync();
        };

        Closing += Window_Closing;
        Closed += Window_Closed;
    }

    public void ShowFromTray()
    {
        if (_disposed)
            return;

        if (!IsVisible)
            Show();

        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Activate();
        Topmost = true;
        Topmost = false;
    }

    public void DisposeResources()
    {
        if (_disposed)
            return;

        _disposed = true;
        _timer.Stop();
        _tray?.Dispose();
        _tray = null;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_settings.MinimizeToTray && !_exitRequested)
        {
            e.Cancel = true;
            Hide();
            _tray?.ShowBalloonOnce();
            return;
        }

        DisposeResources();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        DisposeResources();
        System.Windows.Application.Current.Shutdown();
    }

    private void ExitApp()
    {
        _exitRequested = true;
        Close();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSettings();
    }

    private void OpenSettings()
    {
        if (_settingsOpen || _disposed)
            return;

        _settingsOpen = true;
        try
        {
            var dialog = new SettingsWindow(_settings);
            if (dialog.ShowDialog() == true)
            {
                _settings = dialog.Settings;
                _settings.Save();
                _settings.ApplyStartWithWindows();
                RestartTimer();
            }
        }
        finally
        {
            _settingsOpen = false;
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RequestManualRefreshAsync();
    }

    private async Task RequestManualRefreshAsync()
    {
        if (_disposed)
            return;

        if (DateTime.UtcNow - _lastRefresh < TimeSpan.FromSeconds(30))
        {
            UpdatedText.Text = "Aguarde 30 s entre leituras.";
            return;
        }

        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_disposed)
            return;

        if (!await _gate.WaitAsync(0))
            return;

        try
        {
            _lastRefresh = DateTime.UtcNow;
            RefreshButton.IsEnabled = false;
            StatusText.Text = "Lendo bateria...";
            StatusText.Foreground = new SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x8F, 0xA3, 0xB8));
            StatusPill.Background = new SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0x22, 0x2A, 0x34, 0x40));
            StatusPill.ToolTip = null;

            BatteryReading reading = await Task.Run(_service.Read);
            Apply(reading);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Desconectado";
            StatusText.Foreground = new SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xF2, 0x6D, 0x6D));
            StatusPill.Background = new SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0x33, 0xB3, 0x26, 0x1E));
            StatusPill.ToolTip = ex.Message;
            UpdatedText.Text = $"Última tentativa: {DateTime.Now:HH:mm:ss}";
            ResetChargingState();
            _tray?.Update(null, null, connected: false);
            AppLog.Write($"ui: erro inesperado: {ex}");
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            _gate.Release();
            RestartTimer();
        }
    }

    private void Apply(BatteryReading reading)
    {
        if (reading.Success && reading.Millivolts is int mv && reading.Percent is int pct)
        {
            _last = reading;

            bool charging = UpdateChargingState(mv);
            int shown;

            if (charging)
            {
                // A tensão de carga não representa a carga real. Congela o
                // percentual no último valor conhecido antes da carga.
                _frozenPct ??= _displayedPct ?? pct;
                shown = _frozenPct.Value;
                _displayedPct = shown;
            }
            else
            {
                if (_wasCharging)
                {
                    // Fim da carga: a tensão agora representa a bateria real.
                    // Permite a ressincronização (inclusive para cima) uma vez.
                    _displayedPct = _frozenPct is int frozen && frozen > pct
                        ? frozen
                        : pct;
                    _frozenPct = null;
                }

                shown = ApplyHysteresis(pct);
            }

            _wasCharging = charging;

            RenderBattery(shown);
            MillivoltsText.Text = $"{mv} mV";
            StatusText.Text = "Conectado";
            StatusText.Foreground = new SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x7B, 0xD8, 0x8A));
            StatusPill.Background = new SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0x33, 0x2E, 0x7D, 0x32));
            StatusPill.ToolTip = null;
            UpdatedText.Text = $"Última atualização: {DateTime.Now:HH:mm:ss}";

            _tray?.Update(shown, mv, connected: true);
            return;
        }

        // Conectado/desconectado apenas; o detalhe técnico fica no tooltip.
        StatusText.Text = "Desconectado";
        StatusText.Foreground = new SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xF2, 0x6D, 0x6D));
        StatusPill.Background = new SolidColorBrush(
            System.Windows.Media.Color.FromArgb(0x33, 0xB3, 0x26, 0x1E));
        StatusPill.ToolTip = reading.Error;
        UpdatedText.Text = $"Última tentativa: {DateTime.Now:HH:mm:ss}";
        ResetChargingState();
        _tray?.Update(null, null, connected: false);

        BatteryText.Opacity = 0.5;
        BatteryBar.Opacity = 0.5;

        if (_last is null)
        {
            BatteryText.Text = "—";
            BatteryText.Foreground = System.Windows.Media.Brushes.White;
            BatteryBar.Value = 0;
            MillivoltsText.Text = "— mV";
        }
        else
        {
            MillivoltsText.Text = $"{_last.Millivolts} mV · última leitura";
        }
    }

    private int ApplyHysteresis(int rawPct)
    {
        if (_displayedPct is null)
        {
            _displayedPct = rawPct;
            return rawPct;
        }

        // Variações de até 2% são ignoradas. Fora da carga o valor nunca sobe;
        // a única subida permitida é a ressincronização após o fim da carga.
        if (rawPct < _displayedPct.Value - 2)
            _displayedPct = rawPct;

        return _displayedPct.Value;
    }

    private bool UpdateChargingState(int mv)
    {
        DateTime now = DateTime.UtcNow;
        _voltageHistory.RemoveAll(x => now - x.Time > TimeSpan.FromMinutes(45));

        int? previous = _voltageHistory.Count > 0 ? _voltageHistory[^1].Mv : null;
        bool previousRecent = _voltageHistory.Count > 0
            && now - _voltageHistory[^1].Time <= TimeSpan.FromMinutes(10);

        _voltageHistory.Add((now, mv));

        if (mv > _chargingPeakMv)
            _chargingPeakMv = mv;

        if (!_inferredCharging)
        {
            // Salto forte entre leituras próximas (ex.: cabo plugado).
            bool jump = previousRecent && previous is int prev && mv - prev >= 30;

            // Ou aumento sustentado: pelo menos 20 mV em 5+ minutos.
            (DateTime Time, int Mv)? baseline = null;
            foreach ((DateTime Time, int Mv) item in _voltageHistory)
            {
                if (now - item.Time >= TimeSpan.FromMinutes(5))
                {
                    baseline = item;
                    break;
                }
            }

            bool sustained = baseline is { } b && mv - b.Mv >= 20;

            if (jump || sustained)
            {
                _inferredCharging = true;
                _chargingPeakMv = mv;
            }
        }

        if (_inferredCharging && mv <= _chargingPeakMv - 40)
        {
            _inferredCharging = false;
            _chargingPeakMv = mv;
        }

        return _inferredCharging;
    }

    private void ResetChargingState()
    {
        _voltageHistory.Clear();
        _inferredCharging = false;
        _chargingPeakMv = 0;
        _frozenPct = null;
        _wasCharging = false;
    }

    private void RenderBattery(int shown)
    {
        BatteryText.Text = $"{shown}%";
        BatteryText.Opacity = 1;
        BatteryBar.Value = shown;
        BatteryBar.Opacity = 1;
        BatteryText.Foreground = shown switch
        {
            >= 50 => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7B, 0xD8, 0x8A)),
            >= 20 => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF2, 0xC9, 0x4C)),
            _ => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF2, 0x6D, 0x6D))
        };
        BatteryBar.Foreground = BatteryText.Foreground;
    }

    private void RestartTimer()
    {
        if (_disposed)
            return;

        _timer.Stop();
        _timer.Interval = TimeSpan.FromMinutes(_settings.IntervalMinutes);
        _timer.Start();
    }
}
