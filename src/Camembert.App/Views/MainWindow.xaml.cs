using System.Windows;

namespace Camembert.App.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    void OnSettings(object sender, RoutedEventArgs e) => new SettingsWindow { Owner = this, DataContext = DataContext }.ShowDialog();
}
