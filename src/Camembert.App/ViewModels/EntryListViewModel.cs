using System.Collections;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Camembert.Core.Models;
using Camembert.Core.Services;

namespace Camembert.App.ViewModels;

/// <summary>Ligne de liste. Les textes sont calculés à la demande : seules les lignes visibles coûtent quelque chose.</summary>
public sealed class EntryRow(FileTree tree, int id, long total, Brush color, bool showLocation)
{
    public int Id => id;
    public bool IsDirectory => tree.IsDirectory(id);
    public string Name => tree.Name(id) + (tree.IsInaccessible(id) ? " (inaccessible)" : "");
    public string Glyph => IsDirectory ? "" : "";
    public Brush Color => color;
    public string SizeText => SizeFormat.Format(tree.Size(id));
    public string PercentText => Percent(tree.Size(id), total);
    public string TypeText => FileTypes.Describe(tree.Name(id), IsDirectory);
    public string ModifiedText => tree.Modified(id)?.ToLocalTime().ToString("dd/MM/yyyy HH:mm") ?? "";
    public string Path => tree.GetPath(id);
    public string Location => showLocation ? tree.GetPath(tree.Parent(id)) : "";
    public Visibility LocationVisibility => showLocation ? Visibility.Visible : Visibility.Collapsed;

    public static string Percent(long size, long total) =>
        total <= 0 ? "0 %" : (100.0 * size / total).ToString(size * 10 >= total ? "0" : "0.0", System.Globalization.CultureInfo.GetCultureInfo("fr-FR")) + " %";

    // Deux lignes créées pour le même élément sont la même ligne (sélection, défilement).
    public override bool Equals(object? obj) => obj is EntryRow other && other.Id == id;
    public override int GetHashCode() => id;
    public override string ToString() => $"{Name}, {SizeText}"; // nom lu par les lecteurs d'écran
}

/// <summary>Liste virtuelle : WPF ne demande que les lignes affichées, créées à la volée à partir des identifiants.</summary>
public sealed class RowList(int[] ids, Func<int, EntryRow> create) : IList
{
    public int Count => ids.Length;
    public object? this[int index] { get => create(ids[index]); set => throw new NotSupportedException(); }
    public int IndexOf(object? value) => value is EntryRow row ? Array.IndexOf(ids, row.Id) : -1;
    public bool Contains(object? value) => IndexOf(value) >= 0;
    public IEnumerator GetEnumerator() => ids.Select(create).GetEnumerator();
    public void CopyTo(Array array, int index) { foreach (var row in this) array.SetValue(row, index++); }
    public bool IsFixedSize => true;
    public bool IsReadOnly => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
}

public enum ListKind { Content, Search, Basket }

/// <summary>Une liste d'éléments triable : contenu du dossier courant, résultats de recherche ou panier.</summary>
public sealed class EntryListViewModel : ViewModelBase
{
    FileTree? _tree;
    int[] _ids = [];
    Func<int, EntryRow>? _create;
    EntryRow? _selectedRow;

    public EntryListViewModel(ListKind kind)
    {
        Kind = kind;
        SortCommand = new RelayCommand(key =>
        {
            var newKey = Enum.Parse<SortKey>((string)key!);
            // Nouveau critère : du plus gros ou du plus récent d'abord, mais de A à Z pour les noms.
            Descending = newKey == Key ? !Descending : newKey != SortKey.Name;
            Key = newKey;
            if (_tree is not null) TreeSearch.Sort(_tree, _ids, Key, Descending);
            Publish();
        });
    }

    /// <summary>Contenu d'un dossier (double-clic : entrer) ; recherche ou panier (double-clic : afficher dans son dossier).</summary>
    public ListKind Kind { get; }
    public SortKey Key { get; private set; } = SortKey.Size;
    public bool Descending { get; private set; } = true;
    public ICommand SortCommand { get; }
    public IList Rows { get; private set; } = Array.Empty<EntryRow>();
    public int Count => _ids.Length;

    public string NameHeader => Header("Nom", SortKey.Name);
    public string SizeHeader => Header("Taille", SortKey.Size);
    public string ModifiedHeader => Header("Modifié le", SortKey.Modified);
    string Header(string label, SortKey key) => key != Key ? label : label + (Descending ? "  ▼" : "  ▲");

    /// <summary>Appelé quand l'utilisateur change la sélection dans la liste.</summary>
    public Action<EntryRow?>? UserSelected { get; set; }

    public EntryRow? SelectedRow
    {
        get => _selectedRow;
        set { if (Set(ref _selectedRow, value)) UserSelected?.Invoke(value); }
    }

    /// <summary>Change la ligne sélectionnée sans prévenir <see cref="UserSelected"/> (synchronisation depuis le diagramme).</summary>
    public void Select(EntryRow? row)
    {
        _selectedRow = row;
        Raise(nameof(SelectedRow));
    }

    /// <param name="ids">Identifiants déjà triés selon <see cref="Key"/> et <see cref="Descending"/>.</param>
    public void Load(FileTree? tree, int[] ids, Func<int, EntryRow>? create)
    {
        _tree = tree;
        _ids = ids;
        _create = create;
        _selectedRow = null;
        Publish();
    }

    void Publish()
    {
        Rows = _create is null ? Array.Empty<EntryRow>() : new RowList(_ids, _create);
        Raise(nameof(Rows));
        Raise(nameof(Count));
        Raise(nameof(SelectedRow));
        Raise(nameof(NameHeader));
        Raise(nameof(SizeHeader));
        Raise(nameof(ModifiedHeader));
    }
}
