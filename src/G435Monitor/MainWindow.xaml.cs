using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

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
    private readonly List<(DateTime Time, int Pct)> _dischargeHistory = new();
    private bool _inferredCharging;
    private int _chargingPeakMv;
    private int? _frozenPct;
    private bool _wasCharging;

    private static readonly int[] LowBatteryThresholds = { 20, 10, 5 };
    private const int LowBatteryResetPercent = 25;
    private static readonly TimeSpan DischargeWindow = TimeSpan.FromMinutes(60);
    private readonly HashSet<int> _notifiedThresholds = new();

    private const int WmDeviceChange = 0x0219;
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDeviceRemoveComplete = 0x8004;

    private HwndSource? _hwndSource;
    private bool _deviceRefreshScheduled;
    private DateTime _lastDeviceRefresh = DateTime.MinValue;

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

        SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;

        Closing += Window_Closing;
        Closed += Window_Closed;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Observa chegada/remoção de USB para reler quando o dongle voltar.
        _hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _hwndSource?.AddHook(WndProc);
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
        SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;

        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }

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
            ShowHint(ex.Message);
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
            string? remaining = UpdateRemainingEstimate(charging, mv, pct);
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
            RemainingText.Text = $"Tempo estimado: {remaining ?? "aguardando leituras"}";
            StatusText.Text = "Conectado";
            StatusText.Foreground = new SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x7B, 0xD8, 0x8A));
            StatusPill.Background = new SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0x33, 0x2E, 0x7D, 0x32));
            StatusPill.ToolTip = null;
            HideHint();
            UpdatedText.Text = $"Última atualização: {DateTime.Now:HH:mm:ss}";

            _tray?.Update(shown, mv, connected: true, remaining);

            if (!charging)
                CheckLowBatteryNotifications(shown);

            return;
        }

        // Conectado/desconectado apenas; o detalhe técnico fica no tooltip.
        StatusText.Text = "Desconectado";
        StatusText.Foreground = new SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xF2, 0x6D, 0x6D));
        StatusPill.Background = new SolidColorBrush(
            System.Windows.Media.Color.FromArgb(0x33, 0xB3, 0x26, 0x1E));
        StatusPill.ToolTip = reading.Error;
        ShowHint(reading.Error);
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

    private string? UpdateRemainingEstimate(bool charging, int mv, int rawPct)
    {
        if (charging)
        {
            _dischargeHistory.Clear();
            return null;
        }

        // _wasCharging ainda é a leitura anterior: limpa o histórico de carga
        // antes de adicionar a primeira leitura pós-carga.
        if (_wasCharging)
            _dischargeHistory.Clear();

        DateTime now = DateTime.UtcNow;
        _dischargeHistory.RemoveAll(p => now - p.Time > DischargeWindow);
        _dischargeHistory.Add((now, BatteryService.MillivoltsToPercent(mv)));

        return RemainingEstimator.Format(
            RemainingEstimator.EstimateMinutes(_dischargeHistory, rawPct));
    }

    private void CheckLowBatteryNotifications(int shown)
    {
        // Recarga real: permite notificar de novo no próximo ciclo de descarga.
        if (shown >= LowBatteryResetPercent)
        {
            _notifiedThresholds.Clear();
            return;
        }

        if (!_settings.LowBatteryNotifications || _tray is null)
            return;

        // Somente o limiar mais severo aplicável nesta leitura.
        int threshold = 0;
        foreach (int candidate in LowBatteryThresholds)
        {
            if (shown <= candidate && !_notifiedThresholds.Contains(candidate)
                && (threshold == 0 || candidate < threshold))
            {
                threshold = candidate;
            }
        }

        if (threshold == 0)
            return;

        // Limiares pulados nesta queda não devem alertar depois.
        foreach (int candidate in LowBatteryThresholds)
        {
            if (candidate >= threshold)
                _notifiedThresholds.Add(candidate);
        }

        _tray.ShowBatteryNotification(
            threshold <= 5 ? "Bateria crítica" : "Bateria baixa",
            $"G435: {shown}% restante. Conecte o cabo USB-C para carregar.",
            warning: threshold <= 10);
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
        _dischargeHistory.Clear();
        _inferredCharging = false;
        _chargingPeakMv = 0;
        _frozenPct = null;
        _wasCharging = false;
        RemainingText.Text = "Tempo estimado: aguardando leituras";
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

    private void SystemEvents_PowerModeChanged(
        object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume || _disposed || Dispatcher.HasShutdownStarted)
            return;

        // O evento chega em thread do sistema; a leitura roda na UI.
        Dispatcher.BeginInvoke(new Action(
            () => _ = DelayedRefreshAsync(TimeSpan.FromSeconds(2))));
    }

    private async Task DelayedRefreshAsync(TimeSpan delay)
    {
        await Task.Delay(delay);

        if (!_disposed)
            await RefreshAsync();
    }

    private IntPtr WndProc(
        IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmDeviceChange)
        {
            int eventType = wParam.ToInt32();
            if (eventType is DbtDeviceArrival or DbtDeviceRemoveComplete)
                ScheduleDeviceRefresh();
        }

        return IntPtr.Zero;
    }

    private void ScheduleDeviceRefresh()
    {
        // Eventos em rajada (ex.: vários dispositivos) geram uma só leitura.
        if (_disposed || _deviceRefreshScheduled)
            return;

        _deviceRefreshScheduled = true;
        _ = DebouncedDeviceRefreshAsync();
    }

    private async Task DebouncedDeviceRefreshAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1.5));
        }
        finally
        {
            _deviceRefreshScheduled = false;
        }

        if (_disposed)
            return;

        DateTime now = DateTime.UtcNow;
        if (now - _lastDeviceRefresh < TimeSpan.FromSeconds(5))
            return;

        _lastDeviceRefresh = now;
        await RefreshAsync();
    }

    private void ShowHint(string? error)
    {
        HintText.Text = FriendlyError(error);
        HintText.Visibility = Visibility.Visible;
    }

    private void HideHint()
    {
        HintText.Text = string.Empty;
        HintText.Visibility = Visibility.Collapsed;
    }

    private static string FriendlyError(string? error)
    {
        const string dongleMessage =
            "Dongle LIGHTSPEED não encontrado. Verifique o USB. " +
            "No modo Bluetooth o app não lê a bateria. " +
            "Feche G HUB/LogiTune se o problema persistir.";

        if (string.IsNullOrWhiteSpace(error))
            return "Não foi possível ler a bateria. Verifique o fone e o dongle.";

        // Fone desligado/fora de alcance ou sem confirmação do modo remoto.
        // Vem antes do dongle porque "não entrou em modo remoto" também cita o dongle.
        if (error.Contains("modo", StringComparison.OrdinalIgnoreCase)
            || error.Contains("SET_MODE", StringComparison.OrdinalIgnoreCase)
            || error.Contains("GET_MODE", StringComparison.OrdinalIgnoreCase)
            || error.Contains("GET_BATTERY", StringComparison.OrdinalIgnoreCase)
            || error.Contains("sem resposta", StringComparison.OrdinalIgnoreCase))
        {
            return "Fone desligado, fora de alcance ou ocupado. " +
                   "Ligue o G435 e tente novamente.";
        }

        // Dongle ausente ou interface HID inacessível.
        if (error.Contains("dongle", StringComparison.OrdinalIgnoreCase)
            || error.Contains("HID", StringComparison.OrdinalIgnoreCase)
            || error.Contains("SetupDi", StringComparison.OrdinalIgnoreCase))
        {
            return dongleMessage;
        }

        if (error.Contains("implausível", StringComparison.OrdinalIgnoreCase))
            return "Leitura inválida do dongle. Tente novamente.";

        return "Não foi possível ler a bateria. Verifique o fone e o dongle.";
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
