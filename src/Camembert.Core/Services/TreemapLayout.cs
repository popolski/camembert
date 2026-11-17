using Camembert.Core.Models;

namespace Camembert.Core.Services;

/// <summary>
/// Rectangle de la vue « rectangles », en pixels. Les dossiers précèdent leur contenu dans la liste.
/// <see cref="Node"/> vaut -1 pour le regroupement des éléments trop petits pour être dessinés.
/// <see cref="Leaf"/> : rectangle dessiné (fichier, dossier trop petit pour être détaillé, ou regroupement) ;
/// sinon, simple contour d'un dossier détaillé, utile au survol et à la sélection.
/// </summary>
public readonly record struct TreemapRect(int Node, int Depth, float X, float Y, float Width, float Height, bool Leaf)
{
    public bool Contains(float x, float y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
}

/// <summary>
/// Disposition « squarified » (Bruls, Huizing, van Wijk) : les rectangles restent aussi proches du carré que possible.
/// Seuls les éléments d'au moins <c>minArea</c> pixels sont détaillés : le nombre de rectangles est borné par la taille
/// de l'écran, pas par le nombre de fichiers.
/// </summary>
public static class TreemapLayout
{
    /// <summary>Marge intérieure des dossiers des premiers niveaux, pour laisser voir leur contour.</summary>
    const float Padding = 3;
    const int PaddedDepth = 2;

    public static List<TreemapRect> Build(FileTree tree, int root, float width, float height, float minArea = 24)
    {
        var rects = new List<TreemapRect>();
        if (tree.Size(root) > 0 && width >= 1 && height >= 1)
            LayoutChildren(tree, root, 1, 0, 0, width, height, minArea, rects);
        return rects;
    }

    static void LayoutChildren(FileTree tree, int node, int depth, float x, float y, float width, float height, float minArea, List<TreemapRect> rects)
    {
        if (tree.Size(node) <= 0) return;
        double scale = width * (double)height / tree.Size(node); // pixels par octet

        // Un seul passage sur les enfants : seuls ceux qui seront dessinés sont triés (un dossier de 100 000 petits
        // fichiers ne coûte qu'un parcours) ; les trop petits sont regroupés en un seul rectangle, placé en dernier.
        var areas = new List<(int Node, double Area)>();
        double rest = 0;
        for (int i = 0, first = tree.FirstChild(node), count = tree.ChildCount(node); i < count; i++)
        {
            int child = first + i;
            if (tree.IsRemoved(child) || tree.Size(child) <= 0) continue;
            double area = tree.Size(child) * scale;
            if (area >= minArea) areas.Add((child, area));
            else rest += area;
        }
        areas.Sort((a, b) => b.Area.CompareTo(a.Area));
        if (rest > 0) areas.Add((-1, rest));
        if (areas.Count == 0) return;

        Squarify(areas, x, y, width, height, (child, cx, cy, cw, ch) =>
        {
            bool detailed = child >= 0 && tree.IsDirectory(child) && cw * ch >= minArea * 4;
            rects.Add(new TreemapRect(child, depth, cx, cy, cw, ch, Leaf: !detailed));
            if (!detailed) return;
            float pad = depth <= PaddedDepth && cw > Padding * 6 && ch > Padding * 6 ? Padding : 0;
            LayoutChildren(tree, child, depth + 1, cx + pad, cy + pad, cw - 2 * pad, ch - 2 * pad, minArea, rects);
        });
    }

    /// <summary>Range les aires (décroissantes) en rangées le long du côté le plus court du rectangle libre.</summary>
    static void Squarify(List<(int Node, double Area)> items, float x, float y, float width, float height,
        Action<int, float, float, float, float> place)
    {
        int start = 0;
        while (start < items.Count)
        {
            double side = Math.Min(width, height);
            if (side <= 0) return;
            // Agrandir la rangée tant que le pire rapport largeur/hauteur s'améliore.
            int end = start + 1;
            double rowArea = items[start].Area;
            double worst = Worst(items, start, end, rowArea, side);
            while (end < items.Count)
            {
                double candidateArea = rowArea + items[end].Area;
                double candidate = Worst(items, start, end + 1, candidateArea, side);
                if (candidate > worst) break;
                worst = candidate;
                rowArea = candidateArea;
                end++;
            }

            // Placer la rangée, puis réduire le rectangle libre d'autant.
            double thickness = rowArea / side;
            double offset = 0;
            bool horizontal = width >= height; // rangée verticale à gauche si le rectangle est plus large que haut
            for (int i = start; i < end; i++)
            {
                double length = items[i].Area / thickness;
                if (horizontal) place(items[i].Node, x, (float)(y + offset), (float)thickness, (float)length);
                else place(items[i].Node, (float)(x + offset), y, (float)length, (float)thickness);
                offset += length;
            }
            if (horizontal) { x += (float)thickness; width -= (float)thickness; }
            else { y += (float)thickness; height -= (float)thickness; }
            start = end;
        }
    }

    static double Worst(List<(int Node, double Area)> items, int start, int end, double rowArea, double side)
    {
        double max = items[start].Area, min = items[end - 1].Area; // aires décroissantes
        double s2 = side * side, r2 = rowArea * rowArea;
        return Math.Max(s2 * max / r2, r2 / (s2 * min));
    }
}
