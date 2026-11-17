using System.Windows;

namespace Camembert.App.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    /// <summary>Double-clic ou Entrée sur un type : lister tous ses fichiers dans l'onglet Recherche.</summary>
    void OnTypeDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (System.Windows.Controls.ItemsControl.ContainerFromElement(TypeList, (DependencyObject)e.OriginalSource) is System.Windows.Controls.ListViewItem) SearchSelectedType();
    }

    void OnTypeKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        SearchSelectedType();
        e.Handled = true;
    }

    void SearchSelectedType()
    {
        if (DataContext is ViewModels.MainViewModel { SelectedType: { } row } main && main.SearchTypeCommand.CanExecute(row))
            main.SearchTypeCommand.Execute(row);
    }

    void OnSettings(object sender, RoutedEventArgs e) => new SettingsWindow { Owner = this, DataContext = DataContext }.ShowDialog();
}
