using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace G435Monitor;

public sealed class AppSettings
{
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public int IntervalMinutes { get; set; } = 5;

    public static readonly int[] AllowedIntervals = { 5, 10, 15, 30 };

    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "G435-Monitor", "config.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
                return new AppSettings();

            AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(ConfigPath));

            if (settings is null)
                return new AppSettings();

            settings.Normalize();
            return settings;
        }
        catch (Exception ex)
        {
            AppLog.Write($"settings: load falhou: {ex.Message}");
            return new AppSettings();
        }
    }

    public void Normalize()
    {
        if (Array.IndexOf(AllowedIntervals, IntervalMinutes) < 0)
            IntervalMinutes = 5;
    }

    public void Save()
    {
        try
        {
            Normalize();
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(
                ConfigPath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions
                {
                    WriteIndented = true
                }));

            AppLog.Write(
                $"settings: salvo start={StartWithWindows} tray={MinimizeToTray} " +
                $"interval={IntervalMinutes}");
        }
        catch (Exception ex)
        {
            AppLog.Write($"settings: save falhou: {ex.Message}");
        }
    }

    public void ApplyStartWithWindows()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run");

            if (key is null)
                return;

            const string valueName = "G435Monitor";

            if (StartWithWindows)
            {
                string exe = Path.Combine(AppContext.BaseDirectory, "G435Monitor.exe");
                if (!File.Exists(exe))
                    exe = Environment.ProcessPath ?? exe;

                key.SetValue(valueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"settings: autostart falhou: {ex.Message}");
        }
    }

    public AppSettings Clone() => new()
    {
        StartWithWindows = StartWithWindows,
        MinimizeToTray = MinimizeToTray,
        IntervalMinutes = IntervalMinutes
    };
}
