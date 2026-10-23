using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using StartupHub.Core;

namespace StartupHub.App;

public sealed class ThemePalette : INotifyPropertyChanged
{
    private readonly Dictionary<string, Color> colors = new();
    public Color this[string key] => colors.TryGetValue(key, out var color) ? color : Colors.Transparent;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Update(ResourceDictionary resources)
    {
        foreach (string key in resources.Keys) if (resources[key] is Color color) colors[key] = color;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }
}

public static class ThemeManager
{
    private static DataStore? store;
    private static bool listening;
    public static string Preference { get; private set; } = "System";
    public static string EffectiveTheme { get; private set; } = "Light";
    public static event EventHandler? Changed;

    public static void Initialize(DataStore dataStore)
    {
        store = dataStore;
        if (!listening)
        {
            SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnWindowLoaded));
            listening = true;
        }
        Apply(store.State.ThemePreference);
    }

    public static string Normalize(string preference) => preference.ToLowerInvariant() switch
    {
        "system" => "System", "light" => "Light", "dark" => "Dark",
        _ => throw new ArgumentException("主题应为 system、light 或 dark。")
    };

    public static void SetPreference(string preference)
    {
        string normalized = Normalize(preference);
        Apply(normalized);
        if (store != null) { store.State.ThemePreference = normalized; store.SaveState(); }
    }

    public static void Apply(string preference)
    {
        var app = Application.Current;
        app.Dispatcher.VerifyAccess();
        Preference = Normalize(preference);
        string effective = Preference == "System" ? ReadSystemTheme() : Preference;
        var dictionaries = app.Resources.MergedDictionaries;
        var theme = dictionaries.First(x => x.Source?.OriginalString.EndsWith("Theme.xaml", StringComparison.Ordinal) == true);
        var palette = new ResourceDictionary { Source = new Uri($"pack://application:,,,/StartupHub;component/Themes/Tokens.{effective}.xaml") };
        theme.MergedDictionaries[0] = palette;
        ((ThemePalette)theme["ThemeColors"]).Update(palette);
        var fluent = dictionaries.FirstOrDefault(x => x.Source?.OriginalString.Contains("PresentationFramework.Fluent", StringComparison.Ordinal) == true);
        if (fluent != null && EffectiveTheme != effective)
            dictionaries[dictionaries.IndexOf(fluent)] = new ResourceDictionary { Source = new Uri($"pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.{effective}.xaml") };
        EffectiveTheme = effective;
        foreach (Window window in app.Windows) ApplyWindowFrame(window);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static string ReadSystemTheme()
    {
        object? value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
        return value is int mode && mode == 0 ? "Dark" : "Light";
    }

    private static void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        var app = Application.Current;
        if (app == null || app.Dispatcher.HasShutdownStarted) return;
        app.Dispatcher.BeginInvoke(new Action(() => { if (Preference == "System") Apply("System"); }));
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window) ApplyWindowFrame(window);
    }

    private static void ApplyWindowFrame(Window window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        int dark = EffectiveTheme == "Dark" ? 1 : 0;
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        var palette = (ThemePalette)Application.Current.FindResource("ThemeColors");
        var background = palette["CanvasColor"];
        var foreground = palette["TextColor"];
        int caption = background.R | background.G << 8 | background.B << 16;
        int text = foreground.R | foreground.G << 8 | foreground.B << 16;
        DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
        DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
    }

    public static void Shutdown()
    {
        if (listening) SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        store = null;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
