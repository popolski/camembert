using System.Windows;
using System.Windows.Input;
using Camembert.App.Themes;
using Camembert.Core.Models;
using Camembert.Core.Services;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace Camembert.App.Controls;

/// <summary>
/// Vue « rectangles » : chaque fichier est un rectangle d'aire proportionnelle à sa taille, rangé dans le rectangle
/// de son dossier. Mêmes gestes que le diagramme circulaire : clic = sélection, double-clic ou molette vers l'avant =
/// entrer dans le dossier de premier niveau sous le pointeur, molette vers l'arrière ou Retour arrière = remonter,
/// clic droit = sélection puis menu. Couleurs par dossier (celles de la liste) ou par type de fichier.
/// Le dessin est mis en cache (SKPicture) ; survol, sélection et mise en évidence d'un type se dessinent par-dessus.
/// </summary>
public sealed class TreemapControl : SKElement
{
    public const int NoNode = -1;

    public static readonly DependencyProperty TreeProperty = Register<FileTree?>(nameof(Tree), null, layout: true);
    public static readonly DependencyProperty RootProperty = Register(nameof(Root), 0, layout: true);
    public static readonly DependencyProperty TopHuesProperty = Register<IReadOnlyDictionary<int, float>?>(nameof(TopHues), null, layout: true);
    public static readonly DependencyProperty TypeStatsProperty = Register<FileTypeStats?>(nameof(TypeStats), null, layout: true);
    public static readonly DependencyProperty ColorByTypeProperty = Register(nameof(ColorByType), false, layout: true);
    public static readonly DependencyProperty HighlightRankProperty = Register(nameof(HighlightRank), -1);
    public static readonly DependencyProperty SelectedNodeProperty = Register(nameof(SelectedNode), NoNode, twoWay: true);
    public static readonly DependencyProperty HoveredNodeProperty = Register(nameof(HoveredNode), NoNode, twoWay: true);
    public static readonly DependencyProperty EnterCommandProperty = Register<ICommand?>(nameof(EnterCommand), null);
    public static readonly DependencyProperty UpCommandProperty = Register<ICommand?>(nameof(UpCommand), null);

    public FileTree? Tree { get => (FileTree?)GetValue(TreeProperty); set => SetValue(TreeProperty, value); }
    /// <summary>Dossier affiché (dossier courant).</summary>
    public int Root { get => (int)GetValue(RootProperty); set => SetValue(RootProperty, value); }
    /// <summary>Teinte de chaque élément de premier niveau, celle du diagramme circulaire et de la liste.
    /// Renouvelée à chaque navigation (et après une suppression) : c'est elle qui déclenche le recalcul.</summary>
    public IReadOnlyDictionary<int, float>? TopHues { get => (IReadOnlyDictionary<int, float>?)GetValue(TopHuesProperty); set => SetValue(TopHuesProperty, value); }
    public FileTypeStats? TypeStats { get => (FileTypeStats?)GetValue(TypeStatsProperty); set => SetValue(TypeStatsProperty, value); }
    public bool ColorByType { get => (bool)GetValue(ColorByTypeProperty); set => SetValue(ColorByTypeProperty, value); }
    /// <summary>Rang du type de fichier mis en évidence (les autres sont estompés), ou -1.</summary>
    public int HighlightRank { get => (int)GetValue(HighlightRankProperty); set => SetValue(HighlightRankProperty, value); }
    public int SelectedNode { get => (int)GetValue(SelectedNodeProperty); set => SetValue(SelectedNodeProperty, value); }
    public int HoveredNode { get => (int)GetValue(HoveredNodeProperty); set => SetValue(HoveredNodeProperty, value); }
    public ICommand? EnterCommand { get => (ICommand?)GetValue(EnterCommandProperty); set => SetValue(EnterCommandProperty, value); }
    public ICommand? UpCommand { get => (ICommand?)GetValue(UpCommandProperty); set => SetValue(UpCommandProperty, value); }

    List<TreemapRect> _rects = [];
    int[] _ranks = [];
    SKPicture? _picture;
    SKSizeI _layoutSize;
    float _scale = 1;

    public TreemapControl()
    {
        ThemeService.Changed += () => { DropLayout(); InvalidateVisual(); };
    }

    static DependencyProperty Register<T>(string name, T defaultValue, bool layout = false, bool twoWay = false) =>
        DependencyProperty.Register(name, typeof(T), typeof(TreemapControl),
            new FrameworkPropertyMetadata(defaultValue,
                twoWay ? FrameworkPropertyMetadataOptions.BindsTwoWayByDefault : FrameworkPropertyMetadataOptions.None,
                (d, _) =>
                {
                    var control = (TreemapControl)d;
                    if (layout) control.DropLayout();
                    control.InvalidateVisual();
                }));

    void DropLayout()
    {
        _picture?.Dispose();
        _picture = null;
        _layoutSize = default;
    }

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        var tree = Tree;
        if (tree is null || ActualWidth <= 0 || Root >= tree.Count) return;
        _scale = (float)(e.Info.Width / ActualWidth);

        if (_picture is null || _layoutSize != e.Info.Size)
        {
            DropLayout();
            float margin = 4 * _scale;
            _rects = TreemapLayout.Build(tree, Root, e.Info.Width - 2 * margin, e.Info.Height - 2 * margin, minArea: 24 * _scale * _scale);
            for (int i = 0; i < _rects.Count; i++)
            {
                var r = _rects[i];
                _rects[i] = r with { X = r.X + margin, Y = r.Y + margin };
            }
            var stats = TypeStats;
            _ranks = _rects.Select(r => r.Leaf && r.Node >= 0 && stats is not null ? stats.RankOf(tree, r.Node) : -1).ToArray();
            using var recorder = new SKPictureRecorder();
            Draw(recorder.BeginRecording(new SKRect(0, 0, e.Info.Width, e.Info.Height)), tree);
            _picture = recorder.EndRecording();
            _layoutSize = e.Info.Size;
        }
        canvas.DrawPicture(_picture);

        using var paint = new SKPaint { IsAntialias = true };
        // Type mis en évidence : tout le reste est estompé.
        int rank = HighlightRank;
        if (rank >= 0)
        {
            paint.Color = ThemeService.IsDark ? new SKColor(0x10, 0x14, 0x18, 200) : new SKColor(0xF4, 0xF6, 0xF9, 215);
            canvas.DrawRect(new SKRect(0, 0, e.Info.Width, e.Info.Height), paint);
            for (int i = 0; i < _rects.Count; i++)
                if (_ranks[i] == rank) DrawLeaf(canvas, tree, _rects[i], _ranks[i], paint, byType: true); // couleur de la pastille de l'onglet Types
        }
        if (FindRect(HoveredNode) is { } hovered)
        {
            paint.Style = SKPaintStyle.Fill;
            paint.Color = Ink(ThemeService.IsDark ? (byte)60 : (byte)45);
            canvas.DrawRect(ToSk(hovered), paint);
        }
        if (FindRect(SelectedNode) is { } selected)
        {
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = 2 * _scale;
            paint.Color = Ink(255);
            canvas.DrawRect(ToSk(selected), paint);
        }
    }

    void Draw(SKCanvas canvas, FileTree tree)
    {
        using var paint = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill };
        using var outline = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
        for (int i = 0; i < _rects.Count; i++)
        {
            var r = _rects[i];
            if (r.Leaf) DrawLeaf(canvas, tree, r, _ranks[i], paint);
            else if (r.Depth <= 2)
            {
                // Dossier détaillé : fond discret dans sa marge, pour voir où il commence et finit.
                paint.Color = FolderColor(tree, r).WithAlpha(ThemeService.IsDark ? (byte)90 : (byte)110);
                canvas.DrawRect(ToSk(r), paint);
            }
        }
        DrawLabels(canvas, tree);
    }

    void DrawLeaf(SKCanvas canvas, FileTree tree, TreemapRect r, int rank, SKPaint paint, bool byType = false)
    {
        paint.Style = SKPaintStyle.Fill;
        paint.Color = (byType || ColorByType) && r.Node >= 0 && !tree.IsDirectory(r.Node) ? Palette.TypeColor(rank) : FolderColor(tree, r);
        // Un pixel de séparation, gommé, comme entre les segments du diagramme circulaire.
        var rect = ToSk(r);
        float gap = r.Width * _scale > 3 && r.Height * _scale > 3 ? 0.5f * _scale : 0;
        canvas.DrawRect(new SKRect(rect.Left + gap, rect.Top + gap, rect.Right - gap, rect.Bottom - gap), paint);
    }

    /// <summary>Teinte de l'élément de premier niveau qui contient ce rectangle ; plus claire en profondeur.</summary>
    SKColor FolderColor(FileTree tree, TreemapRect r)
    {
        if (r.Node < 0) return Palette.Color(-1, r.Depth, false, isGroup: true);
        int top = r.Node;
        while (top > FileTree.Root && tree.Parent(top) != Root) top = tree.Parent(top);
        float hue = TopHues is { } hues && hues.TryGetValue(top, out float h) ? h : -1;
        return Palette.Color(hue, Math.Min(r.Depth, 6), tree.IsDirectory(r.Node));
    }

    /// <summary>Noms des éléments de premier niveau assez grands pour les afficher.</summary>
    void DrawLabels(SKCanvas canvas, FileTree tree)
    {
        using var typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold);
        using var font = new SKFont(typeface, 12 * _scale);
        using var text = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 235) };
        using var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 150) };
        foreach (var r in _rects)
        {
            if (r.Depth != 1 || r.Node < 0 || r.Width < 70 * _scale || r.Height < 26 * _scale) continue;
            string label = tree.Name(r.Node);
            float max = r.Width - 12 * _scale;
            while (label.Length > 1 && font.MeasureText(label) > max) label = label[..^2] + "…";
            canvas.Save();
            canvas.ClipRect(ToSk(r));
            float x = r.X + 6 * _scale, y = r.Y + 16 * _scale;
            canvas.DrawText(label, x + _scale, y + _scale, SKTextAlign.Left, font, shadow);
            canvas.DrawText(label, x, y, SKTextAlign.Left, font, text);
            canvas.Restore();
        }
    }

    static SKColor Ink(byte alpha) => ThemeService.IsDark ? new SKColor(255, 255, 255, alpha) : new SKColor(0, 0, 0, alpha);

    static SKRect ToSk(TreemapRect r) => new(r.X, r.Y, r.X + r.Width, r.Y + r.Height);

    /// <summary>Rectangle d'un élément, ou de son plus proche dossier parent dessiné.</summary>
    TreemapRect? FindRect(int node)
    {
        var tree = Tree;
        if (tree is null || node < 0) return null;
        for (int n = node; n > FileTree.Root && n != Root; n = tree.Parent(n))
            foreach (var r in _rects)
                if (r.Node == n) return r;
        return null;
    }

    /// <summary>Rectangle le plus profond sous le point (les dossiers précèdent leur contenu dans la liste).</summary>
    TreemapRect? HitTest(Point position)
    {
        float x = (float)position.X * _scale, y = (float)position.Y * _scale;
        TreemapRect? hit = null;
        foreach (var r in _rects)
            if (r.Contains(x, y)) hit = r;
        return hit;
    }

    /// <summary>Élément de premier niveau sous le point (entrer dedans fait descendre d'un niveau à la fois).</summary>
    int TopNodeAt(Point position)
    {
        var tree = Tree;
        if (tree is null || HitTest(position) is not { Node: >= 0 } hit) return NoNode;
        int node = hit.Node;
        while (node > FileTree.Root && tree.Parent(node) != Root) node = tree.Parent(node);
        return node;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetCurrentValue(HoveredNodeProperty, HitTest(e.GetPosition(this)) is { Node: >= 0 } hit ? hit.Node : NoNode);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        SetCurrentValue(HoveredNodeProperty, NoNode);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 2)
        {
            int top = TopNodeAt(e.GetPosition(this));
            if (top != NoNode && Tree?.IsDirectory(top) == true) EnterCommand?.Execute(top);
            return;
        }
        SetCurrentValue(SelectedNodeProperty, HitTest(e.GetPosition(this)) is { Node: >= 0 } hit ? hit.Node : NoNode);
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        SetCurrentValue(SelectedNodeProperty, HitTest(e.GetPosition(this)) is { Node: >= 0 } hit ? hit.Node : NoNode);
    }

    protected override void OnContextMenuOpening(System.Windows.Controls.ContextMenuEventArgs e)
    {
        base.OnContextMenuOpening(e);
        if (SelectedNode == NoNode) e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        e.Handled = true;
        if (e.Delta < 0)
        {
            if (UpCommand?.CanExecute(null) == true) UpCommand.Execute(null);
            return;
        }
        int top = TopNodeAt(e.GetPosition(this));
        if (top != NoNode && Tree?.IsDirectory(top) == true) EnterCommand?.Execute(top);
    }
}
