using Camembert.Core.Models;

namespace Camembert.Core.Services;

/// <summary>
/// Segment d'anneau. Angles en degrés, 0 = haut, sens horaire. <see cref="Node"/> vaut -1 pour le
/// regroupement des petits éléments d'un dossier (il garde la teinte de ce dossier). <see cref="Hue"/> (0..1) suit
/// l'angle du milieu du segment : un enfant reste dans les teintes de son parent, et un dossier à enfant unique n'est pas monochrome.
/// </summary>
public readonly record struct SunburstSegment(int Node, int Depth, float Start, float Sweep, float Hue);

/// <summary>Modèle graphique du sunburst, calculé une fois par navigation puis mis en cache par l'appelant.</summary>
public static class SunburstLayout
{
    public const int MaxDepth = 6;
    public const float MinSweep = 0.5f; // en dessous, les éléments sont regroupés : pas de milliers de segments invisibles

    public static List<SunburstSegment> Build(FileTree tree, int root)
    {
        var segments = new List<SunburstSegment>();
        if (tree.Size(root) > 0) AddChildren(tree, root, 1, 0f, 360f, -1f, segments);
        return segments;
    }

    static void AddChildren(FileTree tree, int node, int depth, float start, float sweep, float hue, List<SunburstSegment> segments)
    {
        long total = tree.Size(node);
        if (total <= 0) return;
        float angle = start, end = start + sweep;
        foreach (int child in tree.ChildrenBySize(node))
        {
            float childSweep = (float)(sweep * ((double)tree.Size(child) / total));
            if (childSweep < MinSweep)
            {
                // Triés par taille : tous les suivants sont plus petits encore.
                if (end - angle >= MinSweep / 2) segments.Add(new(-1, depth, angle, end - angle, hue));
                return;
            }
            float childHue = (angle + childSweep / 2) / 360f;
            segments.Add(new(child, depth, angle, childSweep, childHue));
            if (depth < MaxDepth && tree.IsDirectory(child))
                AddChildren(tree, child, depth + 1, angle, childSweep, childHue, segments);
            angle += childSweep;
        }
    }
}
