using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Camembert.App.ViewModels;

namespace Camembert.App.Views;

/// <summary>
/// Liste d'éléments (contenu d'un dossier ou résultats de recherche). La sélection multiple n'est pas
/// liable en WPF : les actions du menu sont donc transmises ici au ViewModel principal.
/// </summary>
public partial class EntryList : UserControl
{
    public EntryList() => InitializeComponent();

    EntryListViewModel? Model => DataContext as EntryListViewModel;
    MainViewModel? Main => Window.GetWindow(this)?.DataContext as MainViewModel;
    int[] SelectedIds => List.SelectedItems.Cast<EntryRow>().Select(r => r.Id).ToArray();
    EntryRow? Primary => List.SelectedItem as EntryRow;
    bool IsBasket => Model?.Kind == ListKind.Basket;

    void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (List.SelectedItem is { } item) List.ScrollIntoView(item);
    }

    void OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Ignore les double-clics sur la barre de défilement et les en-têtes.
        if (ItemsControl.ContainerFromElement(List, (DependencyObject)e.OriginalSource) is ListViewItem) Activate();
    }

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Activate();
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) Main?.CopyPathCommand.Execute(SelectedIds);
        else if (e.Key == Key.Space) Main?.PreviewCommand.Execute(Primary?.Id);
        // Suppr : supprimer (avec confirmation), comme dans l'Explorateur ; dans la liste à supprimer, en retirer.
        else if (e.Key == Key.Delete) (IsBasket ? Main?.RemoveFromBasketCommand : Main?.DeleteCommand)?.Execute(SelectedIds);
        else return;
        e.Handled = true;
    }

    /// <summary>Double-clic ou Entrée : entrer dans le dossier, ou, pour un résultat de recherche, l'afficher dans son dossier.</summary>
    void Activate()
    {
        if (Primary is not { } row || Main is not { } main) return;
        (Model?.Kind == ListKind.Content ? main.EnterCommand : main.LocateCommand).Execute(row.Id);
    }

    void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (Primary is not { } row)
        {
            e.Handled = true; // rien de sélectionné : pas de menu
            return;
        }
        bool single = List.SelectedItems.Count == 1, isContent = Model?.Kind == ListKind.Content;
        EnterItem.Visibility = single && row.IsDirectory && isContent ? Visibility.Visible : Visibility.Collapsed;
        LocateItem.Visibility = single && !isContent ? Visibility.Visible : Visibility.Collapsed;
        RevealItem.Visibility = PreviewItem.Visibility = single ? Visibility.Visible : Visibility.Collapsed;
        AddToBasketItem.Visibility = IsBasket ? Visibility.Collapsed : Visibility.Visible;
        DeleteItem.InputGestureText = IsBasket ? "" : "Suppr";
        RemoveFromBasketItem.Visibility = IsBasket ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnEnter(object sender, RoutedEventArgs e) => Main?.EnterCommand.Execute(Primary?.Id);
    void OnLocate(object sender, RoutedEventArgs e) => Main?.LocateCommand.Execute(Primary?.Id);
    void OnReveal(object sender, RoutedEventArgs e) => Main?.RevealCommand.Execute(Primary?.Id);
    void OnPreview(object sender, RoutedEventArgs e) => Main?.PreviewCommand.Execute(Primary?.Id);
    void OnDelete(object sender, RoutedEventArgs e) => Main?.DeleteCommand.Execute(SelectedIds);
    void OnAddToBasket(object sender, RoutedEventArgs e) => Main?.AddToBasketCommand.Execute(SelectedIds);
    void OnRemoveFromBasket(object sender, RoutedEventArgs e) => Main?.RemoveFromBasketCommand.Execute(SelectedIds);
    void OnCopyPath(object sender, RoutedEventArgs e) => Main?.CopyPathCommand.Execute(SelectedIds);
}
