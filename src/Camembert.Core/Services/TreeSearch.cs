using System.Text;
using Camembert.Core.Models;

namespace Camembert.Core.Services;

public enum EntryKind { All, Files, Directories }

public enum SortKey { Name, Size, Modified }

/// <summary>Critères de recherche. Les critères vides ne filtrent rien.</summary>
/// <param name="Name">Partie du nom, sans distinction de casse.</param>
/// <param name="Extensions">Extensions séparées par des espaces ou des virgules, avec ou sans point (« jpg, .png »).</param>
public sealed record SearchQuery(
    string Name = "",
    string Extensions = "",
    FileCategory? Category = null,
    long MinSize = 0,
    long MaxSize = long.MaxValue,
    EntryKind Kind = EntryKind.All)
{
    public bool IsEmpty => Name.Trim().Length == 0 && Extensions.Trim().Length == 0 && Category is null &&
                           MinSize <= 0 && MaxSize == long.MaxValue && Kind == EntryKind.All;
}

/// <summary>Recherche dans un arbre déjà analysé, sans accès disque.</summary>
public sealed class TreeSearch(FileTree tree)
{
    byte[]? _lowered; // noms en minuscules, mêmes positions que dans l'arbre

    /// <summary>Identifiants des éléments correspondant aux critères (la racine analysée est exclue), dans l'ordre de l'arbre.</summary>
    public int[] Find(SearchQuery query, CancellationToken cancellation = default)
    {
        var lowered = _lowered ??= BuildLoweredNames();
        var needle = Encoding.UTF8.GetBytes(query.Name.Trim().ToLowerInvariant());
        var suffixes = ParseExtensions(query.Extensions);
        var categorySuffixes = query.Category is { } category
            ? FileTypes.Extensions(category).Select(e => Encoding.UTF8.GetBytes(e)).ToArray()
            : null;
        bool filesOnly = query.Kind == EntryKind.Files || suffixes.Length > 0 || categorySuffixes is not null;
        var matches = new List<int>();

        if (needle.Length == 0)
        {
            for (int id = 1; id < tree.Count; id++)
            {
                if ((id & 0xFFFF) == 0) cancellation.ThrowIfCancellationRequested();
                if (Accept(id)) matches.Add(id);
            }
            return matches.ToArray();
        }

        // Un seul balayage vectorisé de tous les noms ; chaque occurrence est ensuite rattachée à son nœud.
        int position = tree.NameStart(0) + tree.NameLength(0), checks = 0;
        while (position < lowered.Length)
        {
            if ((++checks & 0xFFF) == 0) cancellation.ThrowIfCancellationRequested();
            int hit = lowered.AsSpan(position).IndexOf(needle);
            if (hit < 0) break;
            hit += position;
            int id = NodeAt(hit);
            int end = tree.NameStart(id) + tree.NameLength(id);
            if (hit + needle.Length > end)
            {
                position = hit + 1; // occurrence à cheval sur deux noms
                continue;
            }
            if (Accept(id)) matches.Add(id);
            position = end;
        }
        return matches.ToArray();

        bool Accept(int id)
        {
            if (tree.IsRemoved(id)) return false;
            bool isDirectory = tree.IsDirectory(id);
            if (isDirectory ? filesOnly : query.Kind == EntryKind.Directories) return false;
            long size = tree.Size(id);
            if (size < query.MinSize || size > query.MaxSize) return false;
            var name = lowered.AsSpan(tree.NameStart(id), tree.NameLength(id));
            return (suffixes.Length == 0 || EndsWithAny(name, suffixes)) &&
                   (categorySuffixes is null || EndsWithAny(name, categorySuffixes));
        }
    }

    /// <summary>Trie des identifiants sur place.</summary>
    public static void Sort(FileTree tree, int[] ids, SortKey key, bool descending)
    {
        switch (key)
        {
            case SortKey.Name:
                Array.Sort(Array.ConvertAll(ids, tree.Name), ids, StringComparer.OrdinalIgnoreCase);
                break;
            case SortKey.Size:
                Array.Sort(Array.ConvertAll(ids, tree.Size), ids);
                break;
            case SortKey.Modified:
                Array.Sort(Array.ConvertAll(ids, tree.ModifiedTicks), ids);
                break;
        }
        if (descending) Array.Reverse(ids);
    }

    static bool EndsWithAny(ReadOnlySpan<byte> name, byte[][] suffixes)
    {
        foreach (var suffix in suffixes)
            if (name.EndsWith(suffix)) return true;
        return false;
    }

    static byte[][] ParseExtensions(string text) =>
        text.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(e => Encoding.UTF8.GetBytes("." + e.TrimStart('*').TrimStart('.').ToLowerInvariant()))
            .ToArray();

    /// <summary>Dernier nœud dont le nom commence à <paramref name="position"/> ou avant (les noms sont rangés par identifiant croissant).</summary>
    int NodeAt(int position)
    {
        int low = 0, high = tree.Count - 1;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (tree.NameStart(middle) <= position) low = middle;
            else high = middle - 1;
        }
        return low;
    }

    byte[] BuildLoweredNames()
    {
        var source = tree.NameBytes;
        var lowered = new byte[source.Length];
        for (int id = 0; id < tree.Count; id++)
        {
            var name = source.Slice(tree.NameStart(id), tree.NameLength(id));
            var target = lowered.AsSpan(tree.NameStart(id), name.Length);
            if (Ascii.IsValid(name))
            {
                Ascii.ToLower(name, target, out _);
                continue;
            }
            // ponytail: si la mise en minuscules change la longueur en UTF-8 (rarissime), le nom reste tel quel et n'est trouvé qu'avec sa casse exacte.
            var lower = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(name).ToLowerInvariant());
            (lower.Length == name.Length ? lower : name.ToArray()).CopyTo(target);
        }
        return lowered;
    }
}
