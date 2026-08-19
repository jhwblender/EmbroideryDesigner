using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using Path = System.Windows.Shapes.Path; // disambiguate from System.IO.Path

namespace EmbroideryDesigner;

public partial class MainWindow : Window
{
    // ---- Lattice / model state ----
    private Lattice _lattice = new(30, 30, 24);
    private readonly Dictionary<(HoleIndex, HoleIndex), ThreadLine> _lines = new();
    private readonly Dictionary<(HoleIndex, HoleIndex), Line> _lineVisuals = new();
    private string? _currentFilePath;

    private static string AppDataDir =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EmbroideryDesigner");
    private static string PrefsPath   => System.IO.Path.Combine(AppDataDir, "prefs.json");
    private static string AutosavePath => System.IO.Path.Combine(AppDataDir, "autosave.etp.json");

    // ---- Rendering layers (children of RootCanvas) ----
    private Path _holesPath = new();
    private readonly Canvas _threadsLayer = new();
    private readonly Canvas _holeFillsLayer = new(); // pie-sector fills drawn inside endpoint holes, z=15
    private readonly Line _previewLine = new()
    {
        Stroke = Brushes.Gray,
        StrokeThickness = 2,
        StrokeDashArray = new DoubleCollection { 4, 3 },
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false
    };
    private readonly Ellipse _hoverHighlight = new()
    {
        Width = 14,
        Height = 14,
        Stroke = Brushes.OrangeRed,
        StrokeThickness = 2,
        Fill = Brushes.Transparent,
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false
    };

    // ---- Palette ----
    // White replaces black as the first/default swatch: with a black default background,
    // a black thread would be invisible.
    private readonly List<Color> _palette = new()
    {
        Colors.White, Colors.Firebrick, Colors.RoyalBlue, Colors.ForestGreen,
        Colors.Goldenrod, Colors.DarkOrchid, Colors.SaddleBrown, Colors.DeepPink,
        Colors.Teal, Colors.LightGray
    };
    private Color _currentColor = Colors.White;
    private Border? _selectedSwatch;
    private Color _backgroundColor = Colors.Black;
    private double _holeRadius = 6.0; // local (un-zoomed) pixels; user-adjustable via HoleSizeSlider
    private Color _holeOutlineColor = (Color)ColorConverter.ConvertFromString("#404040")!; // dark gray
    private double _holeOutlineThickness = 1.0; // local (un-zoomed) pixels; user-adjustable via HoleOutlineThicknessSlider
    private double _threadThickness = 3.0; // local (un-zoomed) pixels; user-adjustable via ThreadThicknessSlider

    // ---- Tool mode ----
    private enum DrawTool { Draw, Segment, Freeform }
    private DrawTool _drawTool = DrawTool.Draw;
    private bool _isSelectMode = false;

    // ---- Freeform pen state ----
    private HoleIndex _freeformLastHole;
    private readonly List<(HoleIndex A, HoleIndex B, Color C)> _freeformThreads = new();

    // ---- Select tool: rubber-band state ----
    private bool _isSelecting = false;
    private Point _selectAnchorLocal;
    private readonly HashSet<(HoleIndex, HoleIndex)> _selectedKeys = new();
    private readonly Canvas _selectionLayer = new();
    private readonly Rectangle _rubberBand = new()
    {
        Stroke = Brushes.DodgerBlue,
        StrokeThickness = 1,
        StrokeDashArray = new DoubleCollection { 4, 2 },
        Fill = new SolidColorBrush(Color.FromArgb(40, 70, 130, 230)),
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false
    };

    // ---- Select tool: move-selection state ----
    private bool _isMovingSelection = false;
    private Point _moveAnchorLocal;
    // Per-hole snapped mapping for the current drag: oldHole → newHole.
    // Built fresh each frame from the pixel offset so that stagger-row shape is preserved.
    private Dictionary<HoleIndex, HoleIndex> _moveHoleMapping = new();

    // ---- Touch state ----
    private int _touchCount = 0;
    private readonly Dictionary<int, Point> _activeTouchPoints = new();
    private Point _lastTouchMid;
    private double _lastTouchSpan;

    // ---- Batch-suppress hole fill redraw during multi-add/remove ----
    private bool _suppressHoleFillRedraw = false;

    // ---- Drag / pan state ----
    private bool _isDrawing;
    private HoleIndex _dragStart;
    private bool _isPanning;
    private Point _panStartMouse;
    private double _panStartTranslateX, _panStartTranslateY;

    private const double MinZoom = 0.15;
    private const double MaxZoom = 8.0;

    // ---- Clipboard (copy/paste) ----
    // Pixel offsets relative to copy-center so parity-row stagger is handled correctly on paste.
    private sealed record ClipThread(double dX_A, double dY_A, double dX_B, double dY_B, Color Color);
    private List<ClipThread>? _clipboard;
    private HoleIndex _clipboardCenter;

    // ---- Float-paste state ----
    private bool _isPasting = false;
    private HoleIndex _pasteHoverCenter;

    // ---- Undo / redo history (covers thread add/remove and Clear All Threads) ----
    private sealed record UndoableAction(Action Undo, Action Redo);
    private readonly Stack<UndoableAction> _undoStack = new();
    private readonly Stack<UndoableAction> _redoStack = new();

    // ---- Persistent preferences ----
    private sealed class Prefs
    {
        public string? LastFilePath { get; set; }
        public double HoleRadius { get; set; } = 6.0;
        public double HoleOutlineThickness { get; set; } = 1.0;
        public string HoleOutlineColor { get; set; } = "#404040";
        public double ThreadThickness { get; set; } = 3.0;
        public string BackgroundColor { get; set; } = "#000000";
        public List<string> Palette { get; set; } = new();
        public int DefaultCols { get; set; } = 30;
        public int DefaultRows { get; set; } = 30; // logical rows (internal lattice = 2×)
        public double DefaultSpacing { get; set; } = 24.0;
    }
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private DispatcherTimer? _prefsSaveTimer;

    public MainWindow()
    {
        InitializeComponent();

        _holesPath.IsHitTestVisible = false;
        Panel.SetZIndex(_threadsLayer, 10);
        Panel.SetZIndex(_holeFillsLayer, 15); // sector fills sit above threads but below hole outlines
        Panel.SetZIndex(_holesPath, 20);      // hole outline ring on top; transparent fill lets sectors show
        Panel.SetZIndex(_selectionLayer, 30); // rubber-band + selection highlights + ghosts
        Panel.SetZIndex(_hoverHighlight, 50);
        Panel.SetZIndex(_previewLine, 100);

        RootCanvas.Children.Add(_holesPath);
        RootCanvas.Children.Add(_threadsLayer);
        RootCanvas.Children.Add(_holeFillsLayer);
        RootCanvas.Children.Add(_selectionLayer);
        RootCanvas.Children.Add(_hoverHighlight);
        RootCanvas.Children.Add(_previewLine);

        _selectionLayer.Children.Add(_rubberBand); // rubber-band lives permanently in the layer

        _prefsSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _prefsSaveTimer.Tick += (_, _) => { _prefsSaveTimer.Stop(); SavePrefs(); };

        BuildPaletteUi();
        SetCurrentColor(_palette[0]);
        RegenerateLattice(30, 60, 24, keepExistingLines: false);
        Loaded += (_, _) => { FitToView(); TryRestoreLastSession(); };
    }

    // ======================================================================
    //  Grid generation
    // ======================================================================

    private void RegenerateLattice(int cols, int rows, double spacing, bool keepExistingLines)
    {
        cols = Math.Clamp(cols, 2, 400);
        rows = Math.Clamp(rows, 2, 400);
        spacing = Math.Clamp(spacing, 4, 200);

        _lattice = new Lattice(cols, rows, spacing);

        // Grid changes invalidate hole indices — clear selection and history.
        _selectedKeys.Clear();
        _undoStack.Clear();
        _redoStack.Clear();
        CommandManager.InvalidateRequerySuggested();

        if (!keepExistingLines)
        {
            foreach (var vis in _lineVisuals.Values) _threadsLayer.Children.Remove(vis);
            _lines.Clear();
            _lineVisuals.Clear();
        }
        else
        {
            // Drop any threads whose endpoints no longer exist in the resized grid.
            var toRemove = _lines.Keys.Where(k => !_lattice.InBounds(k.Item1) || !_lattice.InBounds(k.Item2)).ToList();
            foreach (var key in toRemove) RemoveThreadInternal(key);
        }

        DrawHoles();
        RedrawAllThreads();
        UpdateLegend();
        SetStatus($"Grid: {cols} cols × {rows / 2} rows, spacing {spacing:0.#}px.");
    }

    private void DrawHoles()
    {
        var geo = new GeometryGroup();
        for (int i = 0; i < _lattice.Cols; i++)
        {
            for (int j = 0; j < _lattice.Rows; j++)
            {
                var p = _lattice.Position(i, j);
                geo.Children.Add(new EllipseGeometry(p, _holeRadius, _holeRadius));
            }
        }
        _holesPath.Data = geo;
        _holesPath.Fill = Brushes.Transparent; // transparent so sector fills below show through
        _holesPath.Stroke = new SolidColorBrush(_holeOutlineColor);
        _holesPath.StrokeThickness = _holeOutlineThickness;
        RedrawAllHoleFills(); // hole radius may have changed
    }

    private void HoleSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _holeRadius = e.NewValue;
        if (HoleSizeLabel != null) HoleSizeLabel.Text = _holeRadius.ToString("0");
        DrawHoles();
        SchedulePrefsSave();
    }

    private void HoleOutlineThicknessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _holeOutlineThickness = e.NewValue;
        if (HoleOutlineThicknessLabel != null) HoleOutlineThicknessLabel.Text = _holeOutlineThickness.ToString("0.#");
        DrawHoles();
        SchedulePrefsSave();
    }

    private void HoleOutlineColorButton_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(_holeOutlineColor.R, _holeOutlineColor.G, _holeOutlineColor.B)
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _holeOutlineColor = Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B);
            DrawHoles();
            SchedulePrefsSave();
        }
    }

    private void ThreadThicknessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _threadThickness = e.NewValue;
        if (ThreadThicknessLabel != null) ThreadThicknessLabel.Text = _threadThickness.ToString("0.#");
        foreach (var vis in _lineVisuals.Values) vis.StrokeThickness = _threadThickness;
        SchedulePrefsSave();
    }

    // ======================================================================
    //  Thread add / remove
    // ======================================================================

    /// <summary>Adds/removes a thread as a direct user action (drag between two holes), recorded on the undo stack.</summary>
    private void ToggleThread(HoleIndex a, HoleIndex b)
    {
        var key = ThreadLine.Key(a, b);
        if (_lines.TryGetValue(key, out var existing))
        {
            var color = existing.Color;
            RemoveThreadInternal(key);
            PushUndo(
                undo: () => AddThreadInternal(a, b, color),
                redo: () => RemoveThreadInternal(key));
        }
        else
        {
            var color = _currentColor;
            AddThreadInternal(a, b, color);
            PushUndo(
                undo: () => RemoveThreadInternal(key),
                redo: () => AddThreadInternal(a, b, color));
        }
        UpdateLegend();
        AutoSave();
    }

    /// <summary>Raw mutation: adds one thread's data + visual. Does not touch the undo stack.</summary>
    private void AddThreadInternal(HoleIndex a, HoleIndex b, Color color)
    {
        var key = ThreadLine.Key(a, b);
        var line = new ThreadLine(a, b, color);
        _lines[key] = line;

        var pa = _lattice.Position(a);
        var pb = _lattice.Position(b);
        var visual = new Line
        {
            X1 = pa.X,
            Y1 = pa.Y,
            X2 = pb.X,
            Y2 = pb.Y,
            Stroke = new SolidColorBrush(line.Color),
            StrokeThickness = _threadThickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        };
        _lineVisuals[key] = visual;
        _threadsLayer.Children.Add(visual);
        if (!_suppressHoleFillRedraw) RedrawAllHoleFills();
    }

    private void RemoveThreadInternal((HoleIndex, HoleIndex) key)
    {
        if (_lineVisuals.TryGetValue(key, out var vis))
        {
            _threadsLayer.Children.Remove(vis);
            _lineVisuals.Remove(key);
        }
        _lines.Remove(key);
        if (!_suppressHoleFillRedraw) RedrawAllHoleFills();
    }

    private void RedrawAllThreads()
    {
        _threadsLayer.Children.Clear();
        _lineVisuals.Clear();
        foreach (var line in _lines.Values)
        {
            var pa = _lattice.Position(line.A);
            var pb = _lattice.Position(line.B);
            var visual = new Line
            {
                X1 = pa.X,
                Y1 = pa.Y,
                X2 = pb.X,
                Y2 = pb.Y,
                Stroke = new SolidColorBrush(line.Color),
                StrokeThickness = _threadThickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false
            };
            _lineVisuals[line.Key()] = visual;
            _threadsLayer.Children.Add(visual);
        }
        RedrawAllHoleFills();
    }

    // ======================================================================
    //  Hole sector fills
    // ======================================================================

    private void RedrawAllHoleFills()
    {
        _holeFillsLayer.Children.Clear();

        var holeThreads = new Dictionary<HoleIndex, List<ThreadLine>>();
        foreach (var thread in _lines.Values)
        {
            if (!holeThreads.TryGetValue(thread.A, out var la)) { la = new List<ThreadLine>(); holeThreads[thread.A] = la; }
            if (!holeThreads.TryGetValue(thread.B, out var lb)) { lb = new List<ThreadLine>(); holeThreads[thread.B] = lb; }
            la.Add(thread);
            lb.Add(thread);
        }

        foreach (var (hole, threads) in holeThreads)
            DrawHoleFill(_holeFillsLayer, _lattice.Position(hole), _holeRadius, threads, hole);
    }

    private void DrawHoleFill(Canvas layer, Point center, double r, List<ThreadLine> threads, HoleIndex hole)
    {
        if (threads.Count == 0) return;

        if (threads.Count == 1)
        {
            layer.Children.Add(new Path
            {
                Data = new EllipseGeometry(center, r, r),
                Fill = new SolidColorBrush(threads[0].Color),
                IsHitTestVisible = false
            });
            return;
        }

        // Compute each thread's outbound angle from this hole, then divide the circle into
        // sectors meeting at the bisectors between adjacent angles.
        var sorted = threads
            .Select(t =>
            {
                var other = t.A.Equals(hole) ? t.B : t.A;
                var otherPos = _lattice.Position(other);
                double angle = Math.Atan2(otherPos.Y - center.Y, otherPos.X - center.X);
                return (Color: t.Color, Angle: angle);
            })
            .OrderBy(x => x.Angle)
            .ToList();

        int n = sorted.Count;
        // midpoints[i] = bisector between sorted[i] and sorted[i+1] (mod n)
        var midpoints = new double[n];
        for (int i = 0; i < n; i++)
        {
            double a = sorted[i].Angle;
            double b = i + 1 < n ? sorted[i + 1].Angle : sorted[0].Angle + 2 * Math.PI;
            midpoints[i] = (a + b) / 2.0;
        }

        for (int i = 0; i < n; i++)
        {
            double startAngle = i > 0 ? midpoints[i - 1] : midpoints[n - 1] - 2 * Math.PI;
            double endAngle   = midpoints[i];
            layer.Children.Add(CreateSectorPath(center, r, startAngle, endAngle, sorted[i].Color));
        }
    }

    private static Path CreateSectorPath(Point center, double r, double startAngle, double endAngle, Color color)
    {
        bool isLargeArc = (endAngle - startAngle) > Math.PI;
        var startPt = new Point(center.X + r * Math.Cos(startAngle), center.Y + r * Math.Sin(startAngle));
        var endPt   = new Point(center.X + r * Math.Cos(endAngle),   center.Y + r * Math.Sin(endAngle));

        var figure = new PathFigure { StartPoint = center, IsClosed = true, IsFilled = true };
        figure.Segments.Add(new LineSegment(startPt, false));
        figure.Segments.Add(new ArcSegment(endPt, new Size(r, r), 0, isLargeArc, SweepDirection.Clockwise, false));

        return new Path
        {
            Data = new PathGeometry(new[] { figure }),
            Fill = new SolidColorBrush(color),
            IsHitTestVisible = false
        };
    }

    /// <summary>Finds the thread line whose segment passes within `threshold` (local units) of `p`, if any.</summary>
    private bool TryFindNearestThread(Point p, double threshold, out (HoleIndex, HoleIndex) key)
    {
        double best = double.MaxValue;
        key = default;
        bool found = false;
        foreach (var kvp in _lines)
        {
            var a = _lattice.Position(kvp.Value.A);
            var b = _lattice.Position(kvp.Value.B);
            double d = DistancePointToSegment(p, a, b);
            if (d < best)
            {
                best = d;
                key = kvp.Key;
                found = true;
            }
        }
        return found && best <= threshold;
    }

    private static double DistancePointToSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        double lenSq = ab.LengthSquared;
        if (lenSq < 1e-9) return (p - a).Length;
        double t = ((p - a).X * ab.X + (p - a).Y * ab.Y) / lenSq;
        t = Math.Clamp(t, 0, 1);
        var proj = a + t * ab;
        return (p - proj).Length;
    }

    // ======================================================================
    //  Mouse interaction (draw / erase threads)
    // ======================================================================

    private double CurrentScale => ScaleT.ScaleX;

    private void RootCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_touchCount >= 2) return; // two-finger gesture active; ignore promoted mouse events
        Point local = e.GetPosition(RootCanvas);

        if (_isPasting) { CommitPaste(local); return; }   // float-paste: click commits
        if (_isSelectMode) { HandleSelectPress(local); return; }

        double holeSnap = _lattice.Spacing * 0.4;
        if (_lattice.TryFindNearestHole(local, holeSnap, out var hole))
        {
            _isDrawing = true;
            _dragStart = hole;
            if (_drawTool == DrawTool.Freeform)
            {
                _freeformLastHole = hole;
                _freeformThreads.Clear();
                _suppressHoleFillRedraw = true;
            }
            else
            {
                var p = _lattice.Position(hole);
                _previewLine.X1 = p.X; _previewLine.Y1 = p.Y;
                _previewLine.X2 = p.X; _previewLine.Y2 = p.Y;
                _previewLine.Visibility = Visibility.Visible;
            }
            RootCanvas.CaptureMouse();
            return;
        }

        double lineThreshold = 6.0 / CurrentScale;
        if (TryFindNearestThread(local, lineThreshold, out var key))
        {
            var (a, b) = key;
            var color = _lines[key].Color;
            RemoveThreadInternal(key);
            PushUndo(
                undo: () => AddThreadInternal(a, b, color),
                redo: () => RemoveThreadInternal(key));
            UpdateLegend();
            SetStatus("Thread removed.");
        }
    }

    private void RootCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_touchCount >= 2) return;
        Point local = e.GetPosition(RootCanvas);

        if (_isPasting) { HandlePasteCursor(local); return; }  // float-paste: update ghost
        if (_isSelectMode) { HandleSelectDrag(local); return; }

        double holeSnap = _lattice.Spacing * 0.4;

        if (_isDrawing)
        {
            if (_drawTool == DrawTool.Freeform)
            {
                double freeSnap = _lattice.Spacing * 0.5;
                if (_lattice.TryFindNearestHole(local, freeSnap, out var nearHole) && !nearHole.Equals(_freeformLastHole))
                {
                    var key = ThreadLine.Key(_freeformLastHole, nearHole);
                    if (!_lines.ContainsKey(key))
                    {
                        AddThreadInternal(_freeformLastHole, nearHole, _currentColor);
                        _freeformThreads.Add((_freeformLastHole, nearHole, _currentColor));
                    }
                    _freeformLastHole = nearHole;
                }
            }
            else
            {
                _previewLine.X2 = local.X;
                _previewLine.Y2 = local.Y;

                var startPx = _lattice.Position(_dragStart);
                double ex = local.X, ey = local.Y;
                if (_lattice.TryFindNearestHole(local, holeSnap, out var snapHole) && !snapHole.Equals(_dragStart))
                {
                    var snapPx = _lattice.Position(snapHole);
                    ex = snapPx.X; ey = snapPx.Y;
                }
                double dx = ex - startPx.X, dy = ey - startPx.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                double s = _lattice.Dx; // 1u = horizontal/vertical neighbor distance (= Spacing*√2)
                SetStatus($"Length: {len / s:0.0}u   Δx: {dx / s:0.0}u   Δy: {dy / s:0.0}u");
            }
        }

        if (_lattice.TryFindNearestHole(local, holeSnap, out var hole))
        {
            var p = _lattice.Position(hole);
            _hoverHighlight.Visibility = Visibility.Visible;
            Canvas.SetLeft(_hoverHighlight, p.X - _hoverHighlight.Width / 2);
            Canvas.SetTop(_hoverHighlight, p.Y - _hoverHighlight.Height / 2);
            if (!_isDrawing) SetStatus($"Hole {hole}    Threads: {_lines.Count}");
        }
        else
        {
            _hoverHighlight.Visibility = Visibility.Collapsed;
        }
    }

    private void RootCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_touchCount >= 2) return;
        Point local = e.GetPosition(RootCanvas);

        if (_isPasting) return;  // click-to-commit handled on MouseDown
        if (_isSelectMode) { HandleSelectRelease(local); return; }

        if (!_isDrawing) return;
        _isDrawing = false;
        RootCanvas.ReleaseMouseCapture();

        if (_drawTool == DrawTool.Freeform)
        {
            _suppressHoleFillRedraw = false;
            RedrawAllHoleFills();
            if (_freeformThreads.Count > 0)
            {
                var strokes = _freeformThreads.ToList();
                _freeformThreads.Clear();
                PushUndo(
                    undo: () => {
                        _suppressHoleFillRedraw = true;
                        try { foreach (var (a, b, _) in strokes) RemoveThreadInternal(ThreadLine.Key(a, b)); }
                        finally { _suppressHoleFillRedraw = false; RedrawAllHoleFills(); }
                        UpdateLegend(); AutoSave();
                    },
                    redo: () => {
                        _suppressHoleFillRedraw = true;
                        try { foreach (var (a, b, c) in strokes) AddThreadInternal(a, b, c); }
                        finally { _suppressHoleFillRedraw = false; RedrawAllHoleFills(); }
                        UpdateLegend(); AutoSave();
                    });
                UpdateLegend();
                AutoSave();
                SetStatus($"Freeform: {strokes.Count} segment(s). Ctrl+Z to undo.");
            }
            return;
        }

        _previewLine.Visibility = Visibility.Collapsed;

        double holeSnap = _lattice.Spacing * 0.4;
        if (_lattice.TryFindNearestHole(local, holeSnap, out var hole) && !hole.Equals(_dragStart))
        {
            if (_drawTool == DrawTool.Segment)
                DrawSegments(_dragStart, hole);
            else
                ToggleThread(_dragStart, hole);
        }
    }

    // ======================================================================
    //  Pan & zoom
    // ======================================================================

    private void ViewportBorder_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isPanning = true;
        _panStartMouse = e.GetPosition(ViewportBorder);
        _panStartTranslateX = TranslateT.X;
        _panStartTranslateY = TranslateT.Y;
        ViewportBorder.CaptureMouse();
    }

    private void ViewportBorder_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isPanning = false;
        ViewportBorder.ReleaseMouseCapture();
    }

    private void ViewportBorder_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        Point mouseScreen = e.GetPosition(ViewportBorder);
        double oldScale = ScaleT.ScaleX;
        double factor = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
        double newScale = Math.Clamp(oldScale * factor, MinZoom, MaxZoom);
        if (Math.Abs(newScale - oldScale) < 1e-6) return;

        double localX = (mouseScreen.X - TranslateT.X) / oldScale;
        double localY = (mouseScreen.Y - TranslateT.Y) / oldScale;

        ScaleT.ScaleX = newScale;
        ScaleT.ScaleY = newScale;
        TranslateT.X = mouseScreen.X - newScale * localX;
        TranslateT.Y = mouseScreen.Y - newScale * localY;
    }

    private void ViewportBorder_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // no auto re-fit on resize; user can click "Reset View"
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_isPanning)
        {
            Point cur = e.GetPosition(ViewportBorder);
            TranslateT.X = _panStartTranslateX + (cur.X - _panStartMouse.X);
            TranslateT.Y = _panStartTranslateY + (cur.Y - _panStartMouse.Y);
        }
    }

    private void FitToView()
    {
        var bounds = _lattice.ComputeBounds();
        double margin = _lattice.Spacing;
        double contentW = Math.Max(1, bounds.Width + margin * 2);
        double contentH = Math.Max(1, bounds.Height + margin * 2);

        double viewW = Math.Max(50, ViewportBorder.ActualWidth);
        double viewH = Math.Max(50, ViewportBorder.ActualHeight);

        double scale = Math.Min(viewW / contentW, viewH / contentH);
        scale = Math.Clamp(scale, MinZoom, MaxZoom);

        double centerLocalX = bounds.Left + bounds.Width / 2;
        double centerLocalY = bounds.Top + bounds.Height / 2;

        ScaleT.ScaleX = scale;
        ScaleT.ScaleY = scale;
        TranslateT.X = viewW / 2 - scale * centerLocalX;
        TranslateT.Y = viewH / 2 - scale * centerLocalY;
    }

    private void ResetViewButton_Click(object sender, RoutedEventArgs e) => FitToView();

    // ======================================================================
    //  Select tool
    // ======================================================================

    private void DrawModeButton_Click(object sender, RoutedEventArgs e)
    {
        _drawTool = DrawTool.Draw;
        DrawModeButton.IsChecked = true;
        SegmentModeButton.IsChecked = false;
        FreeformModeButton.IsChecked = false;
        if (_isSelectMode) { ClearSelection(); _isSelectMode = false; SelectModeButton.IsChecked = false; }
        ViewportBorder.Cursor = null;
        ToolHintText.Text = "Draw: drag between holes to stitch.  Click thread to remove.  Right-drag or two-finger: pan.";
    }

    private void SegmentModeButton_Click(object sender, RoutedEventArgs e)
    {
        _drawTool = DrawTool.Segment;
        SegmentModeButton.IsChecked = true;
        DrawModeButton.IsChecked = false;
        FreeformModeButton.IsChecked = false;
        if (_isSelectMode) { ClearSelection(); _isSelectMode = false; SelectModeButton.IsChecked = false; }
        ViewportBorder.Cursor = null;
        ToolHintText.Text = "Segment: drag across holes — line auto-splits where it crosses any hole.";
    }

    private void FreeformModeButton_Click(object sender, RoutedEventArgs e)
    {
        _drawTool = DrawTool.Freeform;
        FreeformModeButton.IsChecked = true;
        DrawModeButton.IsChecked = false;
        SegmentModeButton.IsChecked = false;
        if (_isSelectMode) { ClearSelection(); _isSelectMode = false; SelectModeButton.IsChecked = false; }
        ViewportBorder.Cursor = null;
        ToolHintText.Text = "Freeform: hold and drag across holes to draw connected segments.";
    }

    private void SelectModeButton_Click(object sender, RoutedEventArgs e)
    {
        _isSelectMode = SelectModeButton.IsChecked == true;
        if (_isSelectMode)
        {
            DrawModeButton.IsChecked = false;
            SegmentModeButton.IsChecked = false;
            FreeformModeButton.IsChecked = false;
            // Cancel any in-progress draw
            _isDrawing = false;
            _previewLine.Visibility = Visibility.Collapsed;
            _hoverHighlight.Visibility = Visibility.Collapsed;
            RootCanvas.ReleaseMouseCapture();
            ViewportBorder.Cursor = System.Windows.Input.Cursors.Cross;
            ToolHintText.Text = "Drag to select threads.  Drag selection to move it.  Esc: clear selection.";
        }
        else
        {
            ClearSelection();
            ViewportBorder.Cursor = null;
            DrawModeButton.IsChecked = (_drawTool == DrawTool.Draw);
            SegmentModeButton.IsChecked = (_drawTool == DrawTool.Segment);
            FreeformModeButton.IsChecked = (_drawTool == DrawTool.Freeform);
            ToolHintText.Text = "Draw: drag between holes to stitch.  Segment: auto-splits at crossed holes.  Select: rubber-band.";
            UpdateDeleteButton();
        }
    }

    private void DeleteSelectionButton_Click(object sender, RoutedEventArgs e) => DeleteSelectedThreads();

    private void ClearSelection()
    {
        _selectedKeys.Clear();
        _isSelecting = false;
        _isMovingSelection = false;
        _rubberBand.Visibility = Visibility.Collapsed;
        _moveHoleMapping.Clear();
        UpdateSelectionVisuals();
    }

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_isPasting && e.Key == System.Windows.Input.Key.Escape)
        {
            _isPasting = false;
            UpdatePasteGhost();
            ViewportBorder.Cursor = System.Windows.Input.Cursors.Cross;
            SetStatus("Paste cancelled.");
            e.Handled = true;
            return;
        }

        if (_isSelectMode)
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                ClearSelection();
                SetStatus("Selection cleared.");
                e.Handled = true;
            }
            else if ((e.Key == System.Windows.Input.Key.Delete || e.Key == System.Windows.Input.Key.Back)
                     && _selectedKeys.Count > 0)
            {
                DeleteSelectedThreads();
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.R && _selectedKeys.Count > 0)
            {
                RotateSelection(e.KeyboardDevice.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift) ? 3 : 1);
                e.Handled = true;
            }
        }
    }

    private void DeleteSelectedThreads()
    {
        var toDelete = _selectedKeys.Where(k => _lines.ContainsKey(k))
                                    .Select(k => _lines[k])
                                    .ToList();
        if (toDelete.Count == 0) return;

        void DoDelete()
        {
            _suppressHoleFillRedraw = true;
            try { foreach (var t in toDelete) RemoveThreadInternal(ThreadLine.Key(t.A, t.B)); }
            finally { _suppressHoleFillRedraw = false; RedrawAllHoleFills(); }
            _selectedKeys.Clear();
            UpdateSelectionVisuals();
            UpdateLegend();
            AutoSave();
        }

        void UndoDelete()
        {
            _suppressHoleFillRedraw = true;
            try { foreach (var t in toDelete) AddThreadInternal(t.A, t.B, t.Color); }
            finally { _suppressHoleFillRedraw = false; RedrawAllHoleFills(); }
            _selectedKeys.Clear();
            foreach (var t in toDelete) _selectedKeys.Add(ThreadLine.Key(t.A, t.B));
            UpdateSelectionVisuals();
            UpdateLegend();
        }

        DoDelete();
        PushUndo(undo: UndoDelete, redo: DoDelete);
        SetStatus($"Deleted {toDelete.Count} thread(s). Ctrl+Z to undo.");
        UpdateDeleteButton();
    }

    // ======================================================================
    //  Segment draw tool
    // ======================================================================

    /// <summary>
    /// Returns holes that lie within snapDist of segment (from→to), sorted by
    /// distance from `from`, excluding the endpoints themselves.
    /// </summary>
    private List<HoleIndex> FindIntermediateHoles(HoleIndex from, HoleIndex to)
    {
        var pa = _lattice.Position(from);
        var pb = _lattice.Position(to);
        var ab = pb - pa;
        double lenSq = ab.LengthSquared;
        if (lenSq < 1e-9) return new List<HoleIndex>();

        double snapDist = _lattice.Spacing * 0.35; // within 35% of spacing counts as "on the line"

        // Bounding box with margin
        double margin = snapDist + 1;
        double bx0 = Math.Min(pa.X, pb.X) - margin, bx1 = Math.Max(pa.X, pb.X) + margin;
        double by0 = Math.Min(pa.Y, pb.Y) - margin, by1 = Math.Max(pa.Y, pb.Y) + margin;

        var result = new List<(HoleIndex, double t)>();
        for (int col = 0; col < _lattice.Cols; col++)
        {
            for (int row = 0; row < _lattice.Rows; row++)
            {
                var h = new HoleIndex(col, row);
                if (h == from || h == to) continue;
                var p = _lattice.Position(h);
                if (p.X < bx0 || p.X > bx1 || p.Y < by0 || p.Y > by1) continue;

                // Parametric projection onto the segment
                double t = ((p - pa).X * ab.X + (p - pa).Y * ab.Y) / lenSq;
                if (t <= 0 || t >= 1) continue; // must be strictly between endpoints

                if (DistancePointToSegment(p, pa, pb) <= snapDist)
                    result.Add((h, t));
            }
        }

        return result.OrderBy(x => x.t).Select(x => x.Item1).ToList();
    }

    /// <summary>
    /// Draws (or toggles) a chain of thread segments: from → intermediate holes → to.
    /// All segment changes are grouped into one undoable action.
    /// </summary>
    private void DrawSegments(HoleIndex from, HoleIndex to)
    {
        var chain = new List<HoleIndex> { from };
        chain.AddRange(FindIntermediateHoles(from, to));
        chain.Add(to);

        if (chain.Count < 2) return;

        // Decide add vs remove for each consecutive pair
        var toAdd    = new List<(HoleIndex A, HoleIndex B, Color C)>();
        var toRemove = new List<(HoleIndex A, HoleIndex B, Color C)>();

        for (int i = 0; i < chain.Count - 1; i++)
        {
            var a = chain[i]; var b = chain[i + 1];
            var key = ThreadLine.Key(a, b);
            if (_lines.TryGetValue(key, out var existing))
                toRemove.Add((a, b, existing.Color));
            else
                toAdd.Add((a, b, _currentColor));
        }

        if (toAdd.Count == 0 && toRemove.Count == 0) return;

        void Do()
        {
            _suppressHoleFillRedraw = true;
            try
            {
                foreach (var (a, b, c) in toAdd)    AddThreadInternal(a, b, c);
                foreach (var (a, b, _) in toRemove) RemoveThreadInternal(ThreadLine.Key(a, b));
            }
            finally { _suppressHoleFillRedraw = false; RedrawAllHoleFills(); }
            UpdateLegend();
            AutoSave();
        }

        void Undo()
        {
            _suppressHoleFillRedraw = true;
            try
            {
                foreach (var (a, b, _) in toAdd)    RemoveThreadInternal(ThreadLine.Key(a, b));
                foreach (var (a, b, c) in toRemove) AddThreadInternal(a, b, c);
            }
            finally { _suppressHoleFillRedraw = false; RedrawAllHoleFills(); }
            UpdateLegend();
            AutoSave();
        }

        Do();
        PushUndo(undo: Undo, redo: Do);
        int segs = chain.Count - 1;
        SetStatus($"{segs} segment{(segs == 1 ? "" : "s")} drawn (added {toAdd.Count}, removed {toRemove.Count}).  Ctrl+Z to undo.");
    }

    private void UpdateDeleteButton()
    {
        bool hasSelection = _isSelectMode && _selectedKeys.Count > 0;
        if (DeleteSelectionButton  != null) DeleteSelectionButton.IsEnabled  = hasSelection;
        if (CopySelectionButton    != null) CopySelectionButton.IsEnabled    = hasSelection;
        if (RotateCwButton         != null) RotateCwButton.IsEnabled         = hasSelection;
        if (PasteButton            != null) PasteButton.IsEnabled            = _isSelectMode && _clipboard != null;
    }

    // ======================================================================
    //  Copy / Paste / Rotate
    // ======================================================================

    private void CopySelectionButton_Click(object sender, RoutedEventArgs e) => CopySelection();
    private void PasteButton_Click(object sender, RoutedEventArgs e) => PasteSelection();
    private void RotateCwButton_Click(object sender, RoutedEventArgs e) => RotateSelection(1);
    private void CopyCommand_CanExecute(object sender, System.Windows.Input.CanExecuteRoutedEventArgs e)
        => e.CanExecute = _isSelectMode && _selectedKeys.Count > 0;
    private void CopyCommand_Executed(object sender, System.Windows.Input.ExecutedRoutedEventArgs e) => CopySelection();
    private void PasteCommand_CanExecute(object sender, System.Windows.Input.CanExecuteRoutedEventArgs e)
        => e.CanExecute = _isSelectMode && _clipboard != null;
    private void PasteCommand_Executed(object sender, System.Windows.Input.ExecutedRoutedEventArgs e) => PasteSelection();

    private void CopySelection()
    {
        if (_selectedKeys.Count == 0) return;

        // Center = nearest hole to the pixel centroid of all selected endpoints
        var allHoles = _selectedKeys
            .Where(k => _lines.ContainsKey(k))
            .SelectMany(k => new[] { _lines[k].A, _lines[k].B })
            .Distinct().ToList();
        double avgI = allHoles.Average(h => h.I);
        double avgJ = allHoles.Average(h => h.J);
        _lattice.TryFindNearestHole(_lattice.Position((int)Math.Round(avgI), (int)Math.Round(avgJ)),
            double.MaxValue, out _clipboardCenter);

        var centerPx = _lattice.Position(_clipboardCenter);
        _clipboard = _selectedKeys
            .Where(k => _lines.ContainsKey(k))
            .Select(k => {
                var t = _lines[k];
                var pA = _lattice.Position(t.A);
                var pB = _lattice.Position(t.B);
                return new ClipThread(pA.X - centerPx.X, pA.Y - centerPx.Y,
                                      pB.X - centerPx.X, pB.Y - centerPx.Y, t.Color);
            })
            .ToList();

        UpdateDeleteButton();
        SetStatus($"Copied {_clipboard.Count} thread(s). Ctrl+V to paste.");
    }

    // Enters float-paste mode: ghost follows cursor until click commits (or Esc cancels).
    private void PasteSelection()
    {
        if (_clipboard == null || _clipboard.Count == 0) return;

        // Ensure select mode is active (paste works in select context)
        if (!_isSelectMode)
        {
            _isSelectMode = true;
            SelectModeButton.IsChecked = true;
            DrawModeButton.IsChecked = false;
            SegmentModeButton.IsChecked = false;
            ViewportBorder.Cursor = System.Windows.Input.Cursors.Cross;
        }

        _isPasting = true;

        // Seed the ghost at the current view center
        double cx = (ViewportBorder.ActualWidth  / 2 - TranslateT.X) / ScaleT.ScaleX;
        double cy = (ViewportBorder.ActualHeight / 2 - TranslateT.Y) / ScaleT.ScaleY;
        _lattice.TryFindNearestHole(new Point(cx, cy), double.MaxValue, out _pasteHoverCenter);
        UpdatePasteGhost();
        SetStatus($"Floating {_clipboard.Count} thread(s) — click to place, Esc to cancel.");
    }

    private void HandlePasteCursor(Point local)
    {
        _lattice.TryFindNearestHole(local, double.MaxValue, out var center);
        if (center != _pasteHoverCenter)
        {
            _pasteHoverCenter = center;
            UpdatePasteGhost();
        }
    }

    private void CommitPaste(Point local)
    {
        if (_clipboard == null) { _isPasting = false; return; }

        _lattice.TryFindNearestHole(local, double.MaxValue, out var pasteCenter);
        _isPasting = false;

        // Remove any conflicting existing threads first, then add all clipboard threads.
        // (No skipping — every entry in the clipboard is placed, overwriting conflicts.)
        var toRemove = new List<(HoleIndex A, HoleIndex B, Color C)>();
        var toAdd    = new List<(HoleIndex A, HoleIndex B, Color C)>();

        var pasteCenterPx = _lattice.Position(pasteCenter);
        foreach (var entry in _clipboard)
        {
            var rawA = new Point(pasteCenterPx.X + entry.dX_A, pasteCenterPx.Y + entry.dY_A);
            var rawB = new Point(pasteCenterPx.X + entry.dX_B, pasteCenterPx.Y + entry.dY_B);
            if (!_lattice.TryFindNearestHole(rawA, _lattice.Spacing * 0.6, out var a)) continue;
            if (!_lattice.TryFindNearestHole(rawB, _lattice.Spacing * 0.6, out var b)) continue;
            if (a == b) continue;
            var key = ThreadLine.Key(a, b);
            if (_lines.TryGetValue(key, out var existing)) toRemove.Add((existing.A, existing.B, existing.Color));
            toAdd.Add((a, b, entry.Color));
        }

        if (toAdd.Count == 0)
        {
            UpdatePasteGhost(); // clears ghost
            SetStatus("Nothing to paste (all out of bounds).");
            return;
        }

        void Do()
        {
            _suppressHoleFillRedraw = true;
            try
            {
                foreach (var (a, b, _) in toRemove) RemoveThreadInternal(ThreadLine.Key(a, b));
                foreach (var (a, b, c) in toAdd)    AddThreadInternal(a, b, c);
            }
            finally { _suppressHoleFillRedraw = false; RedrawAllHoleFills(); }
            _selectedKeys.Clear();
            foreach (var (a, b, _) in toAdd) _selectedKeys.Add(ThreadLine.Key(a, b));
            UpdateSelectionVisuals();
            UpdateLegend();
            AutoSave();
        }
        void Undo()
        {
            _suppressHoleFillRedraw = true;
            try
            {
                foreach (var (a, b, _) in toAdd)    RemoveThreadInternal(ThreadLine.Key(a, b));
                foreach (var (a, b, c) in toRemove) AddThreadInternal(a, b, c);
            }
            finally { _suppressHoleFillRedraw = false; RedrawAllHoleFills(); }
            _selectedKeys.Clear();
            UpdateSelectionVisuals();
            UpdateLegend();
            AutoSave();
        }

        Do();
        PushUndo(undo: Undo, redo: Do);
        UpdatePasteGhost(); // clear ghost now that paste is committed
        SetStatus($"Pasted {toAdd.Count} thread(s). Ctrl+Z to undo.");
    }

    // Draws (or clears) the paste ghost in _selectionLayer.
    private void UpdatePasteGhost()
    {
        // Rebuild selection layer with highlights + ghost on top
        UpdateSelectionVisuals();
        if (!_isPasting || _clipboard == null) return;

        var ghostCenterPx = _lattice.Position(_pasteHoverCenter);
        foreach (var entry in _clipboard)
        {
            var rawA = new Point(ghostCenterPx.X + entry.dX_A, ghostCenterPx.Y + entry.dY_A);
            var rawB = new Point(ghostCenterPx.X + entry.dX_B, ghostCenterPx.Y + entry.dY_B);
            if (!_lattice.TryFindNearestHole(rawA, _lattice.Spacing * 0.6, out var a)) continue;
            if (!_lattice.TryFindNearestHole(rawB, _lattice.Spacing * 0.6, out var b)) continue;
            if (a == b) continue;
            var pa = _lattice.Position(a);
            var pb = _lattice.Position(b);
            _selectionLayer.Children.Add(new Line
            {
                X1 = pa.X, Y1 = pa.Y, X2 = pb.X, Y2 = pb.Y,
                Stroke = new SolidColorBrush(Color.FromArgb(180, entry.Color.R, entry.Color.G, entry.Color.B)),
                StrokeThickness = _threadThickness,
                IsHitTestVisible = false,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
        }
    }

    private void RotateSelection(int quarterTurns)
    {
        if (_selectedKeys.Count == 0) return;
        quarterTurns = ((quarterTurns % 4) + 4) % 4;
        if (quarterTurns == 0) return;

        var bounds = GetSelectionBounds();
        var centerPx = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        var latBounds = _lattice.ComputeBounds();

        var mapping = new Dictionary<HoleIndex, HoleIndex>();
        foreach (var key in _selectedKeys)
        {
            if (!_lines.TryGetValue(key, out var line)) continue;
            foreach (var hole in new[] { line.A, line.B })
            {
                if (mapping.ContainsKey(hole)) continue;
                var rotated = RotatePoint(_lattice.Position(hole), centerPx, quarterTurns);
                var clamped = new Point(Math.Clamp(rotated.X, latBounds.Left, latBounds.Right),
                                        Math.Clamp(rotated.Y, latBounds.Top, latBounds.Bottom));
                _lattice.TryFindNearestHole(clamped, double.MaxValue, out var snapped);
                mapping[hole] = snapped;
            }
        }

        var moves = _selectedKeys
            .Where(k => _lines.ContainsKey(k))
            .Select(k => { var t = _lines[k];
                return (OldA: t.A, OldB: t.B, Color: t.Color,
                        NewA: mapping.GetValueOrDefault(t.A, t.A),
                        NewB: mapping.GetValueOrDefault(t.B, t.B)); })
            .ToList();

        void Do()
        {
            _suppressHoleFillRedraw = true;
            try
            {
                foreach (var m in moves) RemoveThreadInternal(ThreadLine.Key(m.OldA, m.OldB));
                foreach (var m in moves) AddThreadInternal(m.NewA, m.NewB, m.Color);
            }
            finally { _suppressHoleFillRedraw = false; RedrawAllHoleFills(); }
            _selectedKeys.Clear();
            foreach (var m in moves) _selectedKeys.Add(ThreadLine.Key(m.NewA, m.NewB));
            UpdateSelectionVisuals();
            UpdateLegend();
            AutoSave();
        }
        void Undo()
        {
            _suppressHoleFillRedraw = true;
            try
            {
                foreach (var m in moves) RemoveThreadInternal(ThreadLine.Key(m.NewA, m.NewB));
                foreach (var m in moves) AddThreadInternal(m.OldA, m.OldB, m.Color);
            }
            finally { _suppressHoleFillRedraw = false; RedrawAllHoleFills(); }
            _selectedKeys.Clear();
            foreach (var m in moves) _selectedKeys.Add(ThreadLine.Key(m.OldA, m.OldB));
            UpdateSelectionVisuals();
            UpdateLegend();
            AutoSave();
        }

        Do();
        PushUndo(undo: Undo, redo: Do);
        SetStatus($"Rotated {quarterTurns * 90}° CW. Ctrl+Z to undo.  (Shift+R = CCW)");
    }

    private static Point RotatePoint(Point p, Point center, int quarterTurns)
    {
        double x = p.X - center.X, y = p.Y - center.Y;
        for (int i = 0; i < quarterTurns; i++) { double nx = y; double ny = -x; x = nx; y = ny; } // 90° CW
        return new Point(center.X + x, center.Y + y);
    }

    // ======================================================================
    //  Recolor selected threads
    // ======================================================================

    private void RecolorSelectedThreads(Color newColor)
    {
        var toChange = _selectedKeys
            .Where(k => _lines.ContainsKey(k) && _lines[k].Color != newColor)
            .Select(k => (Key: k, OldColor: _lines[k].Color))
            .ToList();
        if (toChange.Count == 0) return;

        void Do()
        {
            foreach (var (key, _) in toChange)
            {
                if (!_lines.TryGetValue(key, out var t)) continue;
                t.Color = newColor;
                if (_lineVisuals.TryGetValue(key, out var vis)) vis.Stroke = new SolidColorBrush(newColor);
            }
            RedrawAllHoleFills();
            UpdateLegend();
            AutoSave();
        }
        void Undo()
        {
            foreach (var (key, oldColor) in toChange)
            {
                if (!_lines.TryGetValue(key, out var t)) continue;
                t.Color = oldColor;
                if (_lineVisuals.TryGetValue(key, out var vis)) vis.Stroke = new SolidColorBrush(oldColor);
            }
            RedrawAllHoleFills();
            UpdateLegend();
            AutoSave();
        }

        Do();
        PushUndo(undo: Undo, redo: Do);
        SetStatus($"Recolored {toChange.Count} thread(s). Ctrl+Z to undo.");
    }

    private void HandleSelectPress(Point local)
    {
        // Click within selection bounds → start moving; otherwise → new rubber-band
        if (_selectedKeys.Count > 0 && GetSelectionBounds().Contains(local))
        {
            _isMovingSelection = true;
            _moveAnchorLocal = local;
            _moveHoleMapping.Clear();
            RootCanvas.CaptureMouse();
        }
        else
        {
            _selectedKeys.Clear();
            _isSelecting = true;
            _selectAnchorLocal = local;
            Canvas.SetLeft(_rubberBand, local.X);
            Canvas.SetTop(_rubberBand, local.Y);
            _rubberBand.Width = 0;
            _rubberBand.Height = 0;
            _rubberBand.Visibility = Visibility.Visible;
            UpdateSelectionVisuals();
            RootCanvas.CaptureMouse();
        }
    }

    private void HandleSelectDrag(Point local)
    {
        if (_isSelecting)
        {
            double w = Math.Abs(local.X - _selectAnchorLocal.X);
            double h = Math.Abs(local.Y - _selectAnchorLocal.Y);
            double x = Math.Min(local.X, _selectAnchorLocal.X);
            double y = Math.Min(local.Y, _selectAnchorLocal.Y);
            Canvas.SetLeft(_rubberBand, x);
            Canvas.SetTop(_rubberBand, y);
            _rubberBand.Width  = w;
            _rubberBand.Height = h;

            int wCols = Math.Max(0, (int)Math.Round(w / _lattice.Dx));
            int hRows = Math.Max(0, (int)Math.Round(h / _lattice.Dy));
            SetStatus($"Selecting: {wCols} × {hRows} holes");
        }
        else if (_isMovingSelection)
        {
            // Snap each selected hole independently: pixel-displace then find nearest hole.
            // This preserves thread shape across stagger-row boundaries (which a uniform
            // (di,dj) index offset cannot do when dj is odd and endpoints span parity rows).
            var pixelOffset = local - _moveAnchorLocal;
            var newMapping = BuildMoveMapping(pixelOffset);

            // Only redraw when the snapped targets actually changed
            if (!MappingEquals(newMapping, _moveHoleMapping))
            {
                _moveHoleMapping = newMapping;
                UpdateSelectionVisuals();
            }
        }

        // Update cursor
        if (!_isSelecting && !_isMovingSelection && _selectedKeys.Count > 0)
            ViewportBorder.Cursor = GetSelectionBounds().Contains(local)
                ? System.Windows.Input.Cursors.SizeAll
                : System.Windows.Input.Cursors.Cross;
    }

    private void HandleSelectRelease(Point local)
    {
        RootCanvas.ReleaseMouseCapture();

        if (_isSelecting)
        {
            _isSelecting = false;
            _rubberBand.Visibility = Visibility.Collapsed;

            double x = Math.Min(local.X, _selectAnchorLocal.X);
            double y = Math.Min(local.Y, _selectAnchorLocal.Y);
            var selRect = new Rect(x, y,
                Math.Abs(local.X - _selectAnchorLocal.X),
                Math.Abs(local.Y - _selectAnchorLocal.Y));

            _selectedKeys.Clear();
            foreach (var kvp in _lines)
            {
                var pa = _lattice.Position(kvp.Value.A);
                var pb = _lattice.Position(kvp.Value.B);
                if (selRect.Contains(pa) && selRect.Contains(pb))
                    _selectedKeys.Add(kvp.Key);
            }
            UpdateSelectionVisuals();
            SetStatus($"{_selectedKeys.Count} thread(s) selected.");
        }
        else if (_isMovingSelection)
        {
            _isMovingSelection = false;
            bool anyMoved = _moveHoleMapping.Any(kvp => kvp.Key != kvp.Value);
            if (anyMoved)
                CommitSelectionMove();
            else
            {
                _moveHoleMapping.Clear();
                UpdateSelectionVisuals();
            }
        }
    }

    private Rect GetSelectionBounds()
    {
        if (_selectedKeys.Count == 0) return Rect.Empty;
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var key in _selectedKeys)
        {
            if (!_lines.TryGetValue(key, out var line)) continue;
            var pa = _lattice.Position(line.A);
            var pb = _lattice.Position(line.B);
            minX = Math.Min(minX, Math.Min(pa.X, pb.X));
            minY = Math.Min(minY, Math.Min(pa.Y, pb.Y));
            maxX = Math.Max(maxX, Math.Max(pa.X, pb.X));
            maxY = Math.Max(maxY, Math.Max(pa.Y, pb.Y));
        }
        double pad = Math.Max(_holeRadius * 2, _threadThickness);
        return new Rect(minX - pad, minY - pad, maxX - minX + pad * 2, maxY - minY + pad * 2);
    }

    private void UpdateSelectionVisuals()
    {
        UpdateDeleteButton();
        _selectionLayer.Children.Clear();
        _selectionLayer.Children.Add(_rubberBand);

        // Selection highlight: dashed white outline over each selected thread
        foreach (var key in _selectedKeys)
        {
            if (!_lines.TryGetValue(key, out var line)) continue;
            var pa = _lattice.Position(line.A);
            var pb = _lattice.Position(line.B);
            _selectionLayer.Children.Add(new Line
            {
                X1 = pa.X, Y1 = pa.Y, X2 = pb.X, Y2 = pb.Y,
                Stroke = Brushes.White,
                StrokeThickness = _threadThickness + 4,
                StrokeDashArray = new DoubleCollection { 3, 3 },
                Opacity = 0.6,
                IsHitTestVisible = false,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
        }

        // Ghost threads at the per-hole snapped positions
        if (_isMovingSelection && _moveHoleMapping.Count > 0)
        {
            foreach (var key in _selectedKeys)
            {
                if (!_lines.TryGetValue(key, out var line)) continue;
                if (!_moveHoleMapping.TryGetValue(line.A, out var newA)) continue;
                if (!_moveHoleMapping.TryGetValue(line.B, out var newB)) continue;
                var pa = _lattice.Position(newA);
                var pb = _lattice.Position(newB);
                _selectionLayer.Children.Add(new Line
                {
                    X1 = pa.X, Y1 = pa.Y, X2 = pb.X, Y2 = pb.Y,
                    Stroke = new SolidColorBrush(Color.FromArgb(180, line.Color.R, line.Color.G, line.Color.B)),
                    StrokeThickness = _threadThickness,
                    IsHitTestVisible = false,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                });
            }
        }
    }

    /// <summary>
    /// For each unique endpoint hole in the selection, pixel-displace it by <paramref name="pixelOffset"/>
    /// then snap to the nearest lattice hole. Shared endpoints map consistently.
    /// </summary>
    private Dictionary<HoleIndex, HoleIndex> BuildMoveMapping(Vector pixelOffset)
    {
        var mapping = new Dictionary<HoleIndex, HoleIndex>();
        var bounds = _lattice.ComputeBounds();

        foreach (var key in _selectedKeys)
        {
            if (!_lines.TryGetValue(key, out var line)) continue;
            foreach (var hole in new[] { line.A, line.B })
            {
                if (mapping.ContainsKey(hole)) continue;
                var pos = _lattice.Position(hole);
                var displaced = new Point(pos.X + pixelOffset.X, pos.Y + pixelOffset.Y);
                var clamped = new Point(
                    Math.Clamp(displaced.X, bounds.Left, bounds.Right),
                    Math.Clamp(displaced.Y, bounds.Top, bounds.Bottom));
                _lattice.TryFindNearestHole(clamped, double.MaxValue, out var snapped);
                mapping[hole] = snapped;
            }
        }
        return mapping;
    }

    private static bool MappingEquals(Dictionary<HoleIndex, HoleIndex> a, Dictionary<HoleIndex, HoleIndex> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kvp in a)
            if (!b.TryGetValue(kvp.Key, out var v) || v != kvp.Value) return false;
        return true;
    }

    private void CommitSelectionMove()
    {
        var mapping = _moveHoleMapping; // captured for undo/redo closures

        var moves = _selectedKeys
            .Where(k => _lines.ContainsKey(k))
            .Select(k => {
                var t = _lines[k];
                var newA = mapping.GetValueOrDefault(t.A, t.A);
                var newB = mapping.GetValueOrDefault(t.B, t.B);
                return (OldA: t.A, OldB: t.B, Color: t.Color, NewA: newA, NewB: newB);
            })
            .ToList();

        void DoMove()
        {
            _suppressHoleFillRedraw = true;
            try
            {
                foreach (var m in moves) RemoveThreadInternal(ThreadLine.Key(m.OldA, m.OldB));
                foreach (var m in moves) AddThreadInternal(m.NewA, m.NewB, m.Color);
            }
            finally { _suppressHoleFillRedraw = false; RedrawAllHoleFills(); }
            _selectedKeys.Clear();
            foreach (var m in moves) _selectedKeys.Add(ThreadLine.Key(m.NewA, m.NewB));
            _moveHoleMapping.Clear();
            UpdateSelectionVisuals();
            UpdateLegend();
            AutoSave();
        }

        void UndoMove()
        {
            _suppressHoleFillRedraw = true;
            try
            {
                foreach (var m in moves) RemoveThreadInternal(ThreadLine.Key(m.NewA, m.NewB));
                foreach (var m in moves) AddThreadInternal(m.OldA, m.OldB, m.Color);
            }
            finally { _suppressHoleFillRedraw = false; RedrawAllHoleFills(); }
            _selectedKeys.Clear();
            foreach (var m in moves) _selectedKeys.Add(ThreadLine.Key(m.OldA, m.OldB));
            _moveHoleMapping.Clear();
            UpdateSelectionVisuals();
            UpdateLegend();
        }

        DoMove();
        PushUndo(undo: UndoMove, redo: DoMove);
        SetStatus($"Moved {moves.Count} thread(s). Ctrl+Z to undo.");
    }

    // ======================================================================
    //  Touch support (two-finger pan + pinch-zoom)
    // ======================================================================

    private void ViewportBorder_TouchDown(object sender, TouchEventArgs e)
    {
        _activeTouchPoints[e.TouchDevice.Id] = e.GetTouchPoint(ViewportBorder).Position;
        _touchCount++;
        e.TouchDevice.Capture(ViewportBorder);

        if (_touchCount == 2)
        {
            // Second finger arrived — cancel any in-progress draw/select/paste gesture
            if (_isDrawing) { _isDrawing = false; _previewLine.Visibility = Visibility.Collapsed; RootCanvas.ReleaseMouseCapture(); }
            if (_isSelecting) { _isSelecting = false; _rubberBand.Visibility = Visibility.Collapsed; RootCanvas.ReleaseMouseCapture(); }
            if (_isMovingSelection) { _isMovingSelection = false; _moveHoleMapping.Clear(); UpdateSelectionVisuals(); RootCanvas.ReleaseMouseCapture(); }
            if (_isPasting) { _isPasting = false; UpdatePasteGhost(); }
            RefreshTouchReference();
            e.Handled = true; // suppress mouse events while two fingers are down
        }
    }

    private void ViewportBorder_TouchMove(object sender, TouchEventArgs e)
    {
        _activeTouchPoints[e.TouchDevice.Id] = e.GetTouchPoint(ViewportBorder).Position;

        if (_touchCount >= 2)
        {
            var pts = _activeTouchPoints.Values.Take(2).ToList();
            var p1 = pts[0]; var p2 = pts[1];
            var newMid  = new Point((p1.X + p2.X) / 2, (p1.Y + p2.Y) / 2);
            double newSpan = (p1 - p2).Length;

            if (_lastTouchSpan > 0.1)
            {
                double oldScale  = ScaleT.ScaleX;
                double factor    = Math.Clamp(newSpan / _lastTouchSpan, 0.5, 2.0);
                double newScale  = Math.Clamp(oldScale * factor, MinZoom, MaxZoom);

                // Zoom around the midpoint
                double localX = (_lastTouchMid.X - TranslateT.X) / oldScale;
                double localY = (_lastTouchMid.Y - TranslateT.Y) / oldScale;
                ScaleT.ScaleX = newScale;
                ScaleT.ScaleY = newScale;
                TranslateT.X  = _lastTouchMid.X - newScale * localX;
                TranslateT.Y  = _lastTouchMid.Y - newScale * localY;

                // Pan by midpoint translation
                TranslateT.X += newMid.X - _lastTouchMid.X;
                TranslateT.Y += newMid.Y - _lastTouchMid.Y;
            }

            _lastTouchMid  = newMid;
            _lastTouchSpan = newSpan;
            e.Handled = true;
        }
    }

    private void ViewportBorder_TouchUp(object sender, TouchEventArgs e)
    {
        _activeTouchPoints.Remove(e.TouchDevice.Id);
        _touchCount = Math.Max(0, _touchCount - 1);
        if (_touchCount == 1) RefreshTouchReference();
        else if (_touchCount == 0) _lastTouchSpan = 0;
        // Don't set Handled — allow mouse-up promotion for single-touch release
    }

    private void RefreshTouchReference()
    {
        var pts = _activeTouchPoints.Values.Take(2).ToList();
        if (pts.Count < 2) { _lastTouchSpan = 0; return; }
        _lastTouchMid  = new Point((pts[0].X + pts[1].X) / 2, (pts[0].Y + pts[1].Y) / 2);
        _lastTouchSpan = (pts[0] - pts[1]).Length;
    }

    // ======================================================================
    //  Palette UI
    // ======================================================================

    private void BuildPaletteUi()
    {
        PaletteItems.ItemsSource = null;
        PaletteItems.Items.Clear();
        foreach (var color in _palette)
        {
            PaletteItems.Items.Add(MakeSwatch(color));
        }
    }

    private Border MakeSwatch(Color color)
    {
        var border = new Border
        {
            Width = 26,
            Height = 26,
            Margin = new Thickness(2),
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(color),
            Cursor = Cursors.Hand,
            Tag = color
        };
        border.MouseLeftButtonDown += (_, _) => SetCurrentColor(color, border);
        return border;
    }

    private void SetCurrentColor(Color color, Border? swatch = null)
    {
        _currentColor = color;
        CurrentColorSwatchBrush.Color = color;

        if (_selectedSwatch != null) _selectedSwatch.BorderThickness = new Thickness(1);
        if (swatch == null)
        {
            foreach (var obj in PaletteItems.Items)
            {
                if (obj is Border b && b.Tag is Color c && c == color) { swatch = b; break; }
            }
        }
        if (swatch != null)
        {
            swatch.BorderThickness = new Thickness(3);
            swatch.BorderBrush = Brushes.OrangeRed;
        }
        _selectedSwatch = swatch;

        // Recolor active selection when user explicitly clicks a palette swatch
        if (swatch != null && _isSelectMode && _selectedKeys.Count > 0)
            RecolorSelectedThreads(color);
    }

    private void AddColorButton_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            var wc = Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B);
            _palette.Add(wc);
            var swatch = MakeSwatch(wc);
            PaletteItems.Items.Add(swatch);
            SetCurrentColor(wc, swatch);
            SchedulePrefsSave();
        }
    }

    private void BackgroundColorButton_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(_backgroundColor.R, _backgroundColor.G, _backgroundColor.B)
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _backgroundColor = Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B);
            ViewportBorder.Background = new SolidColorBrush(_backgroundColor);
            SchedulePrefsSave();
        }
    }

    private void RemoveColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSwatch == null || _selectedSwatch.Tag is not Color color) return;
        if (_palette.Count <= 1)
        {
            MessageBox.Show(this, "At least one color must remain in the palette.", "Cannot remove", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _palette.Remove(color);
        PaletteItems.Items.Remove(_selectedSwatch);
        _selectedSwatch = null;
        SetCurrentColor(_palette[0]);
    }

    // ======================================================================
    //  Legend
    // ======================================================================

    private sealed class LegendEntry
    {
        public required Brush Brush { get; init; }
        public int Count { get; init; }
        public double TotalLength { get; init; }
        public string TotalLengthDisplay => $"{TotalLength:0.0} u";
    }

    private void UpdateLegend()
    {
        var groups = _lines.Values
            .GroupBy(l => l.Color)
            .Select(g => new LegendEntry
            {
                Brush = new SolidColorBrush(g.Key),
                Count = g.Count(),
                TotalLength = g.Sum(l =>
                {
                    double di = l.B.I - l.A.I;
                    double dj = l.B.J - l.A.J;
                    return Math.Sqrt(di * di + dj * dj);
                })
            })
            .OrderByDescending(x => x.Count)
            .ToList();

        LegendList.ItemsSource = groups;
        SetStatus($"Threads: {_lines.Count}    (length shown in units of one hole-to-hole gap)");
    }

    private void SetStatus(string text) => StatusText.Text = text;

    // ======================================================================
    //  Toolbar: grid size / file / clear
    // ======================================================================

    private void ApplyGridButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(ColsBox.Text, out int cols) || !int.TryParse(RowsBox.Text, out int rows) ||
            !double.TryParse(SpacingBox.Text, out double spacing))
        {
            MessageBox.Show(this, "Columns/Rows must be whole numbers and Spacing must be a number.", "Invalid input", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_lines.Count > 0)
        {
            var result = MessageBox.Show(this,
                "Resizing the grid will remove any threads that fall outside the new size. Continue?",
                "Resize grid", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;
        }

        RegenerateLattice(cols, rows * 2, spacing, keepExistingLines: true);
        FitToView();
        SavePrefs();
    }

    private void ClearThreadsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lines.Count == 0) return;
        var result = MessageBox.Show(this, "Remove all threads from the current pattern?", "Clear all threads",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        var snapshot = _lines.Values.Select(l => (l.A, l.B, l.Color)).ToList();

        void DoClearAll()
        {
            foreach (var vis in _lineVisuals.Values) _threadsLayer.Children.Remove(vis);
            _lines.Clear();
            _lineVisuals.Clear();
            _selectedKeys.Clear();
            UpdateSelectionVisuals();
            RedrawAllHoleFills();
        }

        DoClearAll();
        PushUndo(
            undo: () => { foreach (var (a, b, color) in snapshot) AddThreadInternal(a, b, color); },
            redo: DoClearAll);
        UpdateLegend();
        AutoSave();
    }

    // ======================================================================
    //  Undo / redo
    // ======================================================================

    private void PushUndo(Action undo, Action redo)
    {
        _undoStack.Push(new UndoableAction(undo, redo));
        _redoStack.Clear();
        CommandManager.InvalidateRequerySuggested();
    }

    private void PerformUndo()
    {
        if (_undoStack.Count == 0) return;
        var action = _undoStack.Pop();
        action.Undo();
        _redoStack.Push(action);
        UpdateLegend();
        CommandManager.InvalidateRequerySuggested();
        AutoSave();
    }

    private void PerformRedo()
    {
        if (_redoStack.Count == 0) return;
        var action = _redoStack.Pop();
        action.Redo();
        _undoStack.Push(action);
        UpdateLegend();
        CommandManager.InvalidateRequerySuggested();
        AutoSave();
    }

    private void UndoCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _undoStack.Count > 0;
    private void UndoCommand_Executed(object sender, ExecutedRoutedEventArgs e) => PerformUndo();
    private void RedoCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _redoStack.Count > 0;
    private void RedoCommand_Executed(object sender, ExecutedRoutedEventArgs e) => PerformRedo();

    private void NewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lines.Count > 0)
        {
            var result = MessageBox.Show(this, "Start a new pattern? Unsaved changes will be lost.", "New pattern",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;
        }
        _currentFilePath = null;
        try { if (File.Exists(PrefsPath)) File.Delete(PrefsPath); } catch { }
        ColsBox.Text = "30"; RowsBox.Text = "60"; SpacingBox.Text = "24";
        RegenerateLattice(30, 60, 24, keepExistingLines: false);
        FitToView();
        Title = "Embroidery Thread Designer";
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Embroidery Pattern (*.etp.json)|*.etp.json|JSON files (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".etp.json"
        };
        if (dlg.ShowDialog(this) != true) return;
        LoadFileInto(dlg.FileName);
    }

    private void LoadFileInto(string path, bool isRestore = false)
    {
        try
        {
            var dto = PatternIo.Load(path);

            _backgroundColor = PatternIo.HexToColor(dto.BackgroundColor);
            ViewportBorder.Background = new SolidColorBrush(_backgroundColor);

            _holeRadius = Math.Clamp(dto.HoleRadius, HoleSizeSlider.Minimum, HoleSizeSlider.Maximum);
            HoleSizeSlider.Value = _holeRadius;
            _holeOutlineColor = PatternIo.HexToColor(dto.HoleOutlineColor);
            _holeOutlineThickness = Math.Clamp(dto.HoleOutlineThickness, HoleOutlineThicknessSlider.Minimum, HoleOutlineThicknessSlider.Maximum);
            HoleOutlineThicknessSlider.Value = _holeOutlineThickness;
            _threadThickness = Math.Clamp(dto.ThreadThickness, ThreadThicknessSlider.Minimum, ThreadThicknessSlider.Maximum);
            ThreadThicknessSlider.Value = _threadThickness;

            _palette.Clear();
            foreach (var hex in dto.Palette) _palette.Add(PatternIo.HexToColor(hex));
            if (_palette.Count == 0) _palette.Add(Colors.Black);
            BuildPaletteUi();
            SetCurrentColor(_palette[0]);

            ColsBox.Text = dto.Cols.ToString();
            RowsBox.Text = dto.Rows.ToString();  // logical rows (file stores logical, lattice uses 2×)
            SpacingBox.Text = dto.Spacing.ToString("0.#");

            RegenerateLattice(dto.Cols, dto.Rows * 2, dto.Spacing, keepExistingLines: false);

            foreach (var l in dto.Lines)
            {
                var a = new HoleIndex(l.I1, l.J1);
                var b = new HoleIndex(l.I2, l.J2);
                if (!_lattice.InBounds(a) || !_lattice.InBounds(b)) continue;
                var color = PatternIo.HexToColor(l.Color);
                var key = ThreadLine.Key(a, b);
                _lines[key] = new ThreadLine(a, b, color);
            }
            RedrawAllThreads();
            UpdateLegend();
            FitToView();

            if (isRestore)
            {
                // Autosave restore: don't treat the autosave file as a named save target
                _currentFilePath = null;
                Title = "Embroidery Thread Designer";
                SetStatus("Restored unsaved pattern from autosave.");
            }
            else
            {
                _currentFilePath = path;
                Title = $"Embroidery Thread Designer - {System.IO.Path.GetFileName(path)}";
                SetStatus($"Loaded {path}");
                SavePrefs();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open file:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // Ctrl+S: save silently if a path is already set; otherwise show the Save As dialog.
    private void SaveCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (_currentFilePath != null)
        {
            try
            {
                SaveToPath(_currentFilePath);
                SetStatus($"Saved {_currentFilePath}");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not save file:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        else
        {
            SaveButton_Click(sender, e); // no path yet — show dialog
        }
    }

    // Save As: always shows dialog.
    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Embroidery Pattern (*.etp.json)|*.etp.json|JSON files (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".etp.json",
            FileName = System.IO.Path.GetFileName(_currentFilePath ?? "pattern.etp.json")
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            SaveToPath(dlg.FileName);
            _currentFilePath = dlg.FileName;
            Title = $"Embroidery Thread Designer - {System.IO.Path.GetFileName(dlg.FileName)}";
            SetStatus($"Saved {dlg.FileName}");
            SavePrefs();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save file:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveToPath(string path) =>
        PatternIo.Save(path, _lattice.Cols, _lattice.Rows / 2, _lattice.Spacing,
            _backgroundColor, _holeRadius, _holeOutlineColor, _holeOutlineThickness,
            _threadThickness, _palette, _lines.Values);

    private void SchedulePrefsSave()
    {
        _prefsSaveTimer?.Stop();
        _prefsSaveTimer?.Start();
    }

    private void SavePrefs()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            var prefs = new Prefs
            {
                LastFilePath      = _currentFilePath,
                HoleRadius        = _holeRadius,
                HoleOutlineThickness = _holeOutlineThickness,
                HoleOutlineColor  = PatternIo.ColorToHex(_holeOutlineColor),
                ThreadThickness   = _threadThickness,
                BackgroundColor   = PatternIo.ColorToHex(_backgroundColor),
                Palette           = _palette.Select(PatternIo.ColorToHex).ToList(),
                DefaultCols       = _lattice.Cols,
                DefaultRows       = _lattice.Rows / 2, // store logical rows
                DefaultSpacing    = _lattice.Spacing
            };
            File.WriteAllText(PrefsPath, JsonSerializer.Serialize(prefs, JsonOpts));
        }
        catch { }
    }

    private void AutoSave()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            SaveToPath(AutosavePath);
            if (_currentFilePath != null)
                SaveToPath(_currentFilePath);
        }
        catch { }
    }

    private void TryRestoreLastSession()
    {
        try
        {
            Prefs? prefs = null;
            if (File.Exists(PrefsPath))
                prefs = JsonSerializer.Deserialize<Prefs>(File.ReadAllText(PrefsPath), JsonOpts);

            if (prefs != null) ApplyPrefs(prefs);

            if (prefs?.LastFilePath != null && File.Exists(prefs.LastFilePath))
            {
                LoadFileInto(prefs.LastFilePath);
                return;
            }

            if (File.Exists(AutosavePath))
                LoadFileInto(AutosavePath, isRestore: true);
        }
        catch { }
    }

    private void ApplyPrefs(Prefs prefs)
    {
        if (prefs.HoleRadius > 0)
        {
            _holeRadius = Math.Clamp(prefs.HoleRadius, HoleSizeSlider.Minimum, HoleSizeSlider.Maximum);
            HoleSizeSlider.Value = _holeRadius;
        }
        if (prefs.HoleOutlineThickness >= 0)
        {
            _holeOutlineThickness = Math.Clamp(prefs.HoleOutlineThickness, HoleOutlineThicknessSlider.Minimum, HoleOutlineThicknessSlider.Maximum);
            HoleOutlineThicknessSlider.Value = _holeOutlineThickness;
        }
        if (!string.IsNullOrEmpty(prefs.HoleOutlineColor))
            _holeOutlineColor = PatternIo.HexToColor(prefs.HoleOutlineColor);
        if (prefs.ThreadThickness > 0)
        {
            _threadThickness = Math.Clamp(prefs.ThreadThickness, ThreadThicknessSlider.Minimum, ThreadThicknessSlider.Maximum);
            ThreadThicknessSlider.Value = _threadThickness;
        }
        if (!string.IsNullOrEmpty(prefs.BackgroundColor))
        {
            _backgroundColor = PatternIo.HexToColor(prefs.BackgroundColor);
            ViewportBorder.Background = new SolidColorBrush(_backgroundColor);
        }
        if (prefs.Palette.Count > 0)
        {
            _palette.Clear();
            foreach (var hex in prefs.Palette) _palette.Add(PatternIo.HexToColor(hex));
            BuildPaletteUi();
            SetCurrentColor(_palette[0]);
        }
        if (prefs.DefaultCols > 0 && prefs.DefaultRows > 0 && prefs.DefaultSpacing > 0)
        {
            ColsBox.Text   = prefs.DefaultCols.ToString();
            RowsBox.Text   = prefs.DefaultRows.ToString();
            SpacingBox.Text = prefs.DefaultSpacing.ToString("0.#");
            RegenerateLattice(prefs.DefaultCols, prefs.DefaultRows * 2, prefs.DefaultSpacing, keepExistingLines: false);
            FitToView();
        }
    }

    private void ExportPngButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "PNG image (*.png)|*.png",
            DefaultExt = ".png",
            FileName = "pattern.png"
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var bounds = _lattice.ComputeBounds();
            double margin = _lattice.Spacing;
            double exportScale = 4.0; // supersample for crisp output regardless of on-screen zoom
            double width = (bounds.Width + margin * 2) * exportScale;
            double height = (bounds.Height + margin * 2) * exportScale;
            width = Math.Clamp(width, 1, 8000);
            height = Math.Clamp(height, 1, 8000);

            // Build a throwaway visual tree mirroring the pattern, independent of current pan/zoom.
            var exportCanvas = new Canvas
            {
                Width = width,
                Height = height,
                Background = new SolidColorBrush(_backgroundColor)
            };
            var group = new TransformGroup();
            group.Children.Add(new TranslateTransform(-bounds.Left + margin, -bounds.Top + margin));
            group.Children.Add(new ScaleTransform(exportScale, exportScale));
            // Order matters: translate into local content space first, then scale up.
            var content = new Canvas { RenderTransform = group };

            // Threads first, holes drawn on top -- matches the live canvas so a hole always
            // shows as a gap (background color + rim) even where a thread passes under it.
            foreach (var line in _lines.Values)
            {
                var pa = _lattice.Position(line.A);
                var pb = _lattice.Position(line.B);
                content.Children.Add(new Line
                {
                    X1 = pa.X,
                    Y1 = pa.Y,
                    X2 = pb.X,
                    Y2 = pb.Y,
                    Stroke = new SolidColorBrush(line.Color),
                    StrokeThickness = _threadThickness,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                });
            }

            // Hole sector fills (same logic as live canvas)
            var exportHoleThreads = new Dictionary<HoleIndex, List<ThreadLine>>();
            foreach (var line in _lines.Values)
            {
                if (!exportHoleThreads.TryGetValue(line.A, out var la)) { la = new List<ThreadLine>(); exportHoleThreads[line.A] = la; }
                if (!exportHoleThreads.TryGetValue(line.B, out var lb)) { lb = new List<ThreadLine>(); exportHoleThreads[line.B] = lb; }
                la.Add(line);
                lb.Add(line);
            }
            foreach (var (hole, threads) in exportHoleThreads)
                DrawHoleFill(content, _lattice.Position(hole), _holeRadius, threads, hole);

            var holesGeo = new GeometryGroup();
            for (int i = 0; i < _lattice.Cols; i++)
                for (int j = 0; j < _lattice.Rows; j++)
                    holesGeo.Children.Add(new EllipseGeometry(_lattice.Position(i, j), _holeRadius, _holeRadius));
            content.Children.Add(new Path
            {
                Data = holesGeo,
                Fill = Brushes.Transparent,
                Stroke = new SolidColorBrush(_holeOutlineColor),
                StrokeThickness = _holeOutlineThickness
            });

            exportCanvas.Children.Add(content);
            exportCanvas.Measure(new Size(width, height));
            exportCanvas.Arrange(new Rect(0, 0, width, height));

            var rtb = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(exportCanvas);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = new FileStream(dlg.FileName, FileMode.Create);
            encoder.Save(fs);

            SetStatus($"Exported {dlg.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not export PNG:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
