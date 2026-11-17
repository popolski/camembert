using System.Windows.Media;
using SkiaSharp;

namespace Camembert.App.Controls;

/// <summary>Couleurs partagées par le diagramme et la liste, pour qu'un même élément ait la même teinte des deux côtés.</summary>
public static class Palette
{
    public static readonly Brush SmallItemBrush = Frozen(new SKColor(0x6B, 0x6F, 0x7A));

    /// <param name="hue">Teinte 0..1 de l'ancêtre de premier niveau ; négative = gris (regroupement).</param>
    public static SKColor Color(float hue, int depth, bool isDirectory, bool isGroup = false)
    {
        float lightness = Math.Min(40 + depth * 5, 72);
        if (hue < 0) return SKColor.FromHsl(225, 6, lightness);
        return SKColor.FromHsl(hue * 360, isGroup ? 14 : isDirectory ? 62 : 34, lightness);
    }

    public static Brush Brush(float hue, bool isDirectory) => Frozen(Color(hue, 1, isDirectory));

    /// <summary>Nombre de types de fichiers qui reçoivent une couleur propre ; les suivants sont gris.</summary>
    public const int TypeColors = 16;

    /// <summary>Couleur d'un type de fichier selon son rang (0 = le plus volumineux) ; teintes espacées du nombre d'or.</summary>
    public static SKColor TypeColor(int rank) =>
        rank is >= 0 and < TypeColors ? SKColor.FromHsl(rank * 137.508f % 360, 58, rank < 8 ? 52 : 64) : SKColor.FromHsl(225, 6, 50);

    public static Brush TypeBrush(int rank) => Frozen(TypeColor(rank));

    static Brush Frozen(SKColor c)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(c.Red, c.Green, c.Blue));
        brush.Freeze();
        return brush;
    }
}
