using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Camembert.Core.Models;
using Camembert.Core.Services;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;
using Camembert.App.Themes;

namespace Camembert.App.Controls;

/// <summary>
/// Diagramme sunburst. Le disque central est le dossier courant ; chaque anneau est un niveau.
/// Souris : clic = sélection, double-clic sur un dossier = y entrer, clic au centre = remonter,
/// clic droit = sélection puis menu contextuel. Molette : vers l'avant = entrer dans le dossier survolé,
/// vers l'arrière = remonter (la molette navigue, elle ne zoome pas).
/// Au repos, les segments sont dessinés une fois dans un SKPicture ; survol et sélection ne redessinent que
/// leur surbrillance. Un changement de dossier est animé pendant 250 ms, sauf si l'utilisateur ou Windows a coupé les animations.
/// </summary>
public sealed class SunburstControl : SKElement
{
    public const int NoNode = -1;
    const int CenterHit = -2;
    const double AnimationMilliseconds = 250;

    public static readonly DependencyProperty TreeProperty = Register<FileTree?>(nameof(Tree), null, (c, _) => c.DropPicture());
    public static readonly DependencyProperty SegmentsProperty = Register<IReadOnlyList<SunburstSegment>?>(nameof(Segments), null,
        (c, e) => c.OnSegmentsChanged((IReadOnlyList<SunburstSegment>?)e.OldValue));
    public static readonly DependencyProperty SelectedNodeProperty = Register(nameof(SelectedNode), NoNode, twoWay: true);
    public static readonly DependencyProperty HoveredNodeProperty = Register(nameof(HoveredNode), NoNode, twoWay: true);
    public static readonly DependencyProperty EnterCommandProperty = Register<ICommand?>(nameof(EnterCommand), null);
    public static readonly DependencyProperty UpCommandProperty = Register<ICommand?>(nameof(UpCommand), null);
    public static readonly DependencyProperty AnimationsEnabledProperty = Register(nameof(AnimationsEnabled), true);

    public FileTree? Tree { get => (FileTree?)GetValue(TreeProperty); set => SetValue(TreeProperty, value); }
    public IReadOnlyList<SunburstSegment>? Segments { get => (IReadOnlyList<SunburstSegment>?)GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    public int SelectedNode { get => (int)GetValue(SelectedNodeProperty); set => SetValue(SelectedNodeProperty, value); }
    public int HoveredNode { get => (int)GetValue(HoveredNodeProperty); set => SetValue(HoveredNodeProperty, value); }
    public ICommand? EnterCommand { get => (ICommand?)GetValue(EnterCommandProperty); set => SetValue(EnterCommandProperty, value); }
    public ICommand? UpCommand { get => (ICommand?)GetValue(UpCommandProperty); set => SetValue(UpCommandProperty, value); }
    /// <summary>Réglage de l'application ; les animations restent coupées si Windows a désactivé les siennes.</summary>
    public bool AnimationsEnabled { get => (bool)GetValue(AnimationsEnabledProperty); set => SetValue(AnimationsEnabledProperty, value); }

    public SunburstControl()
    {
        // Les surbrillances (blanches sur fond sombre, noires sur fond clair) dépendent du thème.
        ThemeService.Changed += () =>
        {
            DropPicture();
            InvalidateVisual();
        };
    }

    /// <summary>Couleur des surbrillances : blanc sur fond sombre, noir sur fond clair.</summary>
    static SKColor Ink(byte alpha) => ThemeService.IsDark ? new SKColor(255, 255, 255, alpha) : new SKColor(0, 0, 0, alpha);

    SKPicture? _picture;
    SKSizeI _pictureSize;
    bool _centerHovered;
    // Géométrie en pixels du dernier rendu, et facteur pixels / unités WPF.
    float _centerX, _centerY, _innerRadius, _ringWidth, _scale = 1;
    // Animation : position de départ de chaque élément déjà visible avant le changement de dossier.
    Dictionary<int, SunburstSegment>? _from;
    readonly Stopwatch _animationClock = new();

    static DependencyProperty Register<T>(string name, T defaultValue,
        Action<SunburstControl, DependencyPropertyChangedEventArgs>? changed = null, bool twoWay = false) =>
        DependencyProperty.Register(name, typeof(T), typeof(SunburstControl),
            new FrameworkPropertyMetadata(defaultValue,
                twoWay ? FrameworkPropertyMetadataOptions.BindsTwoWayByDefault : FrameworkPropertyMetadataOptions.None,
                (d, e) =>
                {
                    var control = (SunburstControl)d;
                    changed?.Invoke(control, e);
                    control.InvalidateVisual();
                }));

    void DropPicture()
    {
        _picture?.Dispose();
        _picture = null;
    }

    void OnSegmentsChanged(IReadOnlyList<SunburstSegment>? old)
    {
        DropPicture();
        StopAnimation();
        // Respecte le réglage Windows « Afficher les animations ».
        if (!AnimationsEnabled || !SystemParameters.ClientAreaAnimation || !IsVisible || old is not { Count: > 0 } || Segments is not { Count: > 0 }) return;
        _from = [];
        foreach (var segment in old)
            if (segment.Node != NoNode) _from[segment.Node] = segment;
        _animationClock.Restart();
        CompositionTarget.Rendering += OnAnimationFrame;
    }

    void OnAnimationFrame(object? sender, EventArgs e) => InvalidateVisual();

    void StopAnimation()
    {
        if (_from is null) return;
        _from = null;
        CompositionTarget.Rendering -= OnAnimationFrame;
    }

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        var segments = Segments;
        var tree = Tree;
        if (tree is null || segments is null || ActualWidth <= 0) return;

        _scale = (float)(e.Info.Width / ActualWidth);
        _centerX = e.Info.Width / 2f;
        _centerY = e.Info.Height / 2f;
        float radius = Math.Min(e.Info.Width, e.Info.Height) / 2f - 6 * _scale;
        if (radius <= 20) return;
        _innerRadius = radius * 0.24f;
        _ringWidth = (radius - _innerRadius) / SunburstLayout.MaxDepth;

        if (_from is not null)
        {
            double progress = _animationClock.Elapsed.TotalMilliseconds / AnimationMilliseconds;
            if (progress < 1)
            {
                DrawSegments(canvas, tree, segments, _from, (float)(1 - Math.Pow(1 - progress, 3))); // décélération
                return;
            }
            StopAnimation();
        }

        if (_picture is null || _pictureSize != e.Info.Size)
        {
            DropPicture();
            using var recorder = new SKPictureRecorder();
            DrawSegments(recorder.BeginRecording(new SKRect(0, 0, e.Info.Width, e.Info.Height)), tree, segments, null, 1);
            _picture = recorder.EndRecording();
            _pictureSize = e.Info.Size;
        }
        canvas.DrawPicture(_picture);

        using var paint = new SKPaint { IsAntialias = true };
        if (_centerHovered && UpCommand?.CanExecute(null) == true)
        {
            paint.Color = Ink(28);
            canvas.DrawCircle(_centerX, _centerY, _innerRadius - 2 * _scale, paint);
        }
        int hovered = HoveredNode, selected = SelectedNode;
        if (hovered == NoNode && selected == NoNode) return;
        foreach (var segment in segments)
        {
            if (segment.Node == NoNode || (segment.Node != hovered && segment.Node != selected)) continue;
            using var path = SegmentPath(segment.Start, segment.Sweep, segment.Depth);
            if (segment.Node == hovered)
            {
                paint.Style = SKPaintStyle.Fill;
                paint.Color = Ink(ThemeService.IsDark ? (byte)60 : (byte)40);
                canvas.DrawPath(path, paint);
            }
            if (segment.Node == selected)
            {
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = 2 * _scale;
                paint.Color = Ink(255);
                canvas.DrawPath(path, paint);
            }
        }
    }

    /// <summary>
    /// Dessine tous les segments. Pendant une animation (<paramref name="from"/> non nul), un élément déjà visible
    /// glisse de son ancienne position vers la nouvelle ; un élément nouveau apparaît en fondu.
    /// </summary>
    void DrawSegments(SKCanvas canvas, FileTree tree, IReadOnlyList<SunburstSegment> segments, Dictionary<int, SunburstSegment>? from, float t)
    {
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        // Séparateurs « gommés » : laissent voir le fond de la fenêtre entre les segments.
        using var separator = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = _scale, BlendMode = SKBlendMode.Clear };

        fill.Color = Ink(ThemeService.IsDark ? (byte)18 : (byte)12);
        canvas.DrawCircle(_centerX, _centerY, _innerRadius - 2 * _scale, fill);
        foreach (var segment in segments)
        {
            bool isGroup = segment.Node == NoNode;
            var color = Palette.Color(segment.Hue, segment.Depth, !isGroup && tree.IsDirectory(segment.Node), isGroup);
            float start = segment.Start, sweep = segment.Sweep, depth = segment.Depth;
            if (from is not null)
            {
                if (!isGroup && from.TryGetValue(segment.Node, out var old))
                {
                    start = old.Start + (start - old.Start) * t;
                    sweep = old.Sweep + (sweep - old.Sweep) * t;
                    depth = old.Depth + (depth - old.Depth) * t;
                }
                else
                    color = color.WithAlpha((byte)(255 * t));
            }
            fill.Color = color;
            using var path = SegmentPath(start, sweep, depth);
            canvas.DrawPath(path, fill);
            canvas.DrawPath(path, separator);
        }
    }

    /// <param name="depth">Niveau de l'anneau, 1 = contre le disque central ; fractionnaire pendant une animation.</param>
    SKPath SegmentPath(float startAngle, float sweep, float depth)
    {
        float inner = _innerRadius + (depth - 1) * _ringWidth, outer = inner + _ringWidth;
        using var path = new SKPathBuilder();
        if (sweep >= 359.9f)
        {
            path.FillType = SKPathFillType.EvenOdd;
            path.AddCircle(_centerX, _centerY, outer);
            path.AddCircle(_centerX, _centerY, inner);
            return path.Detach();
        }
        float start = startAngle - 90; // Skia : 0° à droite ; le diagramme commence en haut
        path.ArcTo(Square(outer), start, sweep, true);
        path.ArcTo(Square(inner), start + sweep, -sweep, false);
        path.Close();
        return path.Detach();
    }

    SKRect Square(float radius) => new(_centerX - radius, _centerY - radius, _centerX + radius, _centerY + radius);

    /// <summary>Indice du segment sous le pointeur, <see cref="CenterHit"/> pour le disque central, sinon -1.</summary>
    int HitTest(Point position)
    {
        var segments = Segments;
        if (segments is null || _ringWidth <= 0) return -1;
        double dx = position.X * _scale - _centerX, dy = position.Y * _scale - _centerY;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance < _innerRadius) return CenterHit;
        int depth = 1 + (int)((distance - _innerRadius) / _ringWidth);
        double angle = Math.Atan2(dx, -dy) * 180 / Math.PI; // 0 = haut, sens horaire
        if (angle < 0) angle += 360;
        for (int i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            if (s.Depth == depth && angle >= s.Start && angle < s.Start + s.Sweep) return i;
        }
        return -1;
    }

    int NodeAt(Point position) => HitTest(position) is >= 0 and var hit ? Segments![hit].Node : NoNode;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hit = HitTest(e.GetPosition(this));
        bool center = hit == CenterHit;
        if (center != _centerHovered)
        {
            _centerHovered = center;
            InvalidateVisual();
        }
        Cursor = center && UpCommand?.CanExecute(null) == true ? Cursors.Hand : null;
        SetCurrentValue(HoveredNodeProperty, hit >= 0 ? Segments![hit].Node : NoNode);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _centerHovered = false;
        SetCurrentValue(HoveredNodeProperty, NoNode);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (HitTest(e.GetPosition(this)) == CenterHit)
        {
            if (e.ClickCount == 1) GoUp();
            return;
        }
        int node = NodeAt(e.GetPosition(this));
        if (e.ClickCount == 2 && node != NoNode && Tree?.IsDirectory(node) == true)
            EnterCommand?.Execute(node);
        else
            SetCurrentValue(SelectedNodeProperty, node);
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        SetCurrentValue(SelectedNodeProperty, NodeAt(e.GetPosition(this))); // le menu contextuel agit sur la sélection
    }

    protected override void OnContextMenuOpening(ContextMenuEventArgs e)
    {
        base.OnContextMenuOpening(e);
        if (SelectedNode == NoNode) e.Handled = true; // clic droit dans le vide : pas de menu
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        e.Handled = true;
        if (e.Delta < 0)
        {
            GoUp();
            return;
        }
        int node = NodeAt(e.GetPosition(this));
        if (node != NoNode && Tree?.IsDirectory(node) == true) EnterCommand?.Execute(node);
    }

    void GoUp()
    {
        if (UpCommand?.CanExecute(null) == true) UpCommand.Execute(null);
    }
}
