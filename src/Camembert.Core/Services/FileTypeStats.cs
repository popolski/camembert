using Camembert.Core.Models;

namespace Camembert.Core.Services;

/// <summary>Total d'un type de fichier (extension) sur toute l'analyse.</summary>
/// <param name="Extension">En minuscules, avec le point (« .mp4 ») ; vide pour les fichiers sans extension.</param>
public sealed record FileTypeTotal(string Extension, long Size, int Count);

/// <summary>
/// Répartition de l'espace par extension, calculée à partir de l'arbre (sans relire le disque). Le rang d'une
/// extension (0 = la plus volumineuse) sert à lui donner une couleur stable dans la liste et la vue rectangles.
/// </summary>
public sealed class FileTypeStats
{
    readonly Dictionary<string, int> _rank;

    FileTypeStats(List<FileTypeTotal> totals)
    {
        Totals = totals;
        _rank = new Dictionary<string, int>(totals.Count);
        for (int i = 0; i < totals.Count; i++) _rank[totals[i].Extension] = i;
    }

    /// <summary>Types du plus volumineux au plus petit.</summary>
    public IReadOnlyList<FileTypeTotal> Totals { get; }

    /// <summary>Rang du type de ce fichier (0 = le plus volumineux), ou -1 pour un dossier ou un élément retiré.</summary>
    public int RankOf(FileTree tree, int node) =>
        tree.IsDirectory(node) || tree.IsRemoved(node) ? -1 : _rank.GetValueOrDefault(ExtensionOf(tree.Name(node)), -1);

    public static FileTypeStats Compute(FileTree tree, CancellationToken cancellation = default)
    {
        var totals = new Dictionary<string, (long Size, int Count)>(StringComparer.Ordinal);
        for (int id = 1; id < tree.Count; id++)
        {
            if ((id & 0xFFFF) == 0) cancellation.ThrowIfCancellationRequested();
            if (tree.IsDirectory(id) || tree.IsRemoved(id)) continue;
            string extension = ExtensionOf(tree.Name(id));
            var (size, count) = totals.GetValueOrDefault(extension);
            totals[extension] = (size + tree.Size(id), count + 1);
        }
        return new FileTypeStats(totals
            .Select(t => new FileTypeTotal(t.Key, t.Value.Size, t.Value.Count))
            .OrderByDescending(t => t.Size).ThenBy(t => t.Extension, StringComparer.Ordinal)
            .ToList());
    }

    /// <summary>Extension en minuscules avec le point, selon la même règle que la colonne « Type » ; vide sinon.</summary>
    public static string ExtensionOf(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1 && name.Length - dot <= 12 ? name[dot..].ToLowerInvariant() : "";
    }
}
