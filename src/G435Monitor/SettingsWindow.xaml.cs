using System.Windows;
using System.Windows.Controls;

namespace G435Monitor;

public partial class SettingsWindow : Wpf.Ui.Controls.FluentWindow
{
    public AppSettings Settings { get; private set; }

    public SettingsWindow(AppSettings current)
    {
        InitializeComponent();

        Settings = current.Clone();
        StartWithWindowsToggle.IsChecked = Settings.StartWithWindows;
        MinimizeToTrayToggle.IsChecked = Settings.MinimizeToTray;

        foreach (ComboBoxItem item in IntervalCombo.Items)
        {
            if (int.TryParse(item.Tag?.ToString(), out int value)
                && value == Settings.IntervalMinutes)
            {
                IntervalCombo.SelectedItem = item;
                break;
            }
        }

        if (IntervalCombo.SelectedItem is null)
            IntervalCombo.SelectedIndex = 0;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        int interval = 5;
        if (IntervalCombo.SelectedItem is ComboBoxItem item
            && int.TryParse(item.Tag?.ToString(), out int value))
        {
            interval = value;
        }

        Settings = new AppSettings
        {
            StartWithWindows = StartWithWindowsToggle.IsChecked == true,
            MinimizeToTray = MinimizeToTrayToggle.IsChecked == true,
            IntervalMinutes = interval
        };
        Settings.Normalize();

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
