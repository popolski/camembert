using System.Windows;
using Camembert.Core.Services;
using Microsoft.Win32;

namespace Camembert.App.Themes;

/// <summary>
/// Thème clair ou sombre. « Selon Windows » lit le réglage « Mode d'application » de Windows et suit ses changements.
/// Les contrôles standards viennent du thème Fluent de WPF ; les couleurs propres à l'application, de
/// <c>Themes/Dark.xaml</c> ou <c>Themes/Light.xaml</c>, à référencer en <c>DynamicResource</c>.
/// </summary>
public static class ThemeService
{
    static AppTheme _choice;
    static bool _listening;
    static ResourceDictionary? _colors;

    /// <summary>Déclenché après chaque changement effectif (le diagramme redessine ses surbrillances).</summary>
    public static event Action? Changed;

    public static bool IsDark { get; private set; } = true;

    public static void Apply(AppTheme choice)
    {
        _choice = choice;
        if (!_listening)
        {
            SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
            _listening = true;
        }
        bool dark = choice switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => WindowsUsesDarkApps(),
        };
        var app = Application.Current;
        // Thème des contrôles fixé explicitement (pas « System ») pour qu'il ne puisse jamais différer de nos couleurs.
        app.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
        var colors = new ResourceDictionary { Source = new Uri($"Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative) };
        if (_colors is not null) app.Resources.MergedDictionaries.Remove(_colors);
        app.Resources.MergedDictionaries.Add(colors);
        _colors = colors;
        IsDark = dark;
        Changed?.Invoke();
    }

    static void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_choice != AppTheme.System || e.Category != UserPreferenceCategory.General) return;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (WindowsUsesDarkApps() != IsDark) Apply(AppTheme.System);
        });
    }

    /// <summary>Paramètres Windows › Personnalisation › Couleurs › « Mode d'application par défaut ».</summary>
    static bool WindowsUsesDarkApps()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }
}
