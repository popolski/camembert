using System.Windows;

namespace Camembert.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    void OnClose(object sender, RoutedEventArgs e) => Close();
}
