using System.Threading;
using System.Windows;

namespace G435Monitor;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\G435Monitor.SingleInstance";
    private const string ShowEventName = @"Local\G435Monitor.ShowWindow";
    private const string TrayArgument = "--tray";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private Thread? _listener;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(
            initiallyOwned: true,
            name: MutexName,
            createdNew: out bool created);

        if (!created)
        {
            // Segunda instância: pede à primeira para mostrar a janela.
            try
            {
                using EventWaitHandle signal = EventWaitHandle.OpenExisting(ShowEventName);
                signal.Set();
            }
            catch
            {
                // primeira instância ainda não criou o evento
            }

            Shutdown();
            return;
        }

        base.OnStartup(e);

        AppSettings settings = AppSettings.Load();
        settings.ApplyStartWithWindows();

        _window = new MainWindow(settings);
        MainWindow = _window;

        _showEvent = new EventWaitHandle(
            initialState: false,
            mode: EventResetMode.AutoReset,
            name: ShowEventName);

        _listener = new Thread(ListenForShowRequest)
        {
            IsBackground = true,
            Name = "G435Monitor.ShowWindowListener"
        };
        _listener.Start();

        // No autostart (--tray) a janela nunca aparece: só o ícone da bandeja.
        if (e.Args.Any(
                a => string.Equals(a, TrayArgument, StringComparison.OrdinalIgnoreCase)))
            _window.StartHidden();
        else
            _window.Show();
    }

    private void ListenForShowRequest()
    {
        while (true)
        {
            try
            {
                _showEvent!.WaitOne();
            }
            catch
            {
                return;
            }

            try
            {
                Dispatcher.Invoke(() => _window?.ShowFromTray());
            }
            catch
            {
                return;
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _showEvent?.Set();
        }
        catch
        {
            // ignore
        }

        _showEvent?.Dispose();
        _window?.DisposeResources();
        _mutex?.Dispose();

        base.OnExit(e);
    }
}
