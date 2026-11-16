using Camembert.Core.Models;

namespace Camembert.Core.Services;

/// <summary>
/// Panier : éléments que l'utilisateur envisage de supprimer. Rien n'y est supprimé.
/// Un élément et l'un de ses ancêtres n'y figurent jamais ensemble, donc le total ne compte rien deux fois.
/// </summary>
public sealed class Basket(FileTree tree)
{
    readonly List<int> _items = [];

    public IReadOnlyList<int> Items => _items;
    public long TotalSize => _items.Sum(tree.Size);

    /// <summary>Vrai si l'élément est dans le panier, lui-même ou par l'un de ses dossiers parents.</summary>
    public bool Covers(int id)
    {
        for (int i = id; i > FileTree.Root; i = tree.Parent(i))
            if (_items.Contains(i)) return true;
        return false;
    }

    /// <summary>
    /// Ajoute un élément. Sans effet (false) s'il est déjà couvert ou si c'est la racine analysée ;
    /// ajouter un dossier retire du panier ce qu'il contenait déjà.
    /// </summary>
    public bool Add(int id)
    {
        if (id <= FileTree.Root || Covers(id)) return false;
        _items.RemoveAll(item => IsInside(item, id));
        _items.Add(id);
        return true;
    }

    public bool Remove(int id) => _items.Remove(id);

    public void Clear() => _items.Clear();

    bool IsInside(int id, int ancestor)
    {
        for (int i = tree.Parent(id); i > FileTree.Root; i = tree.Parent(i))
            if (i == ancestor) return true;
        return false;
    }
}
