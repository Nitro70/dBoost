using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DBoost.App.ViewModels;
using DBoost.Core;

namespace DBoost.App.Controls;

/// <summary>
/// Draws a file's waveform before (grey) and after (accent) the change, with clipping shown in red,
/// and lets you mark parts: drag across empty space to add one, drag its body to move it, drag its
/// edges to resize, scroll over it to change its dB, right-click or Delete to remove it.
/// A plain click sets where the preview starts.
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    public static readonly DependencyProperty WaveformProperty = Reg(nameof(Waveform), typeof(Waveform), null);
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(nameof(Segments), typeof(ObservableCollection<SegmentItem>), typeof(WaveformView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => ((WaveformView)d).OnSegmentsChanged(e)));
    public static readonly DependencyProperty SelectedSegmentProperty = DependencyProperty.Register(nameof(SelectedSegment), typeof(SegmentItem), typeof(WaveformView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty CursorSecondsProperty = DependencyProperty.Register(nameof(CursorSeconds), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty PlayheadProperty = Reg(nameof(Playhead), typeof(double?), null);
    public static readonly DependencyProperty GainDbProperty = Reg(nameof(GainDb), typeof(double), 0.0);
    public static readonly DependencyProperty LimiterProperty = Reg(nameof(Limiter), typeof(bool), true);
    public static readonly DependencyProperty CeilingDbProperty = Reg(nameof(CeilingDb), typeof(double), -1.0);
    public static readonly DependencyProperty MessageProperty = Reg(nameof(Message), typeof(string), null);

    private static DependencyProperty Reg(string name, Type type, object? def) =>
        DependencyProperty.Register(name, type, typeof(WaveformView), new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

    public Waveform? Waveform { get => (Waveform?)GetValue(WaveformProperty); set => SetValue(WaveformProperty, value); }
    public ObservableCollection<SegmentItem>? Segments { get => (ObservableCollection<SegmentItem>?)GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    public SegmentItem? SelectedSegment { get => (SegmentItem?)GetValue(SelectedSegmentProperty); set => SetValue(SelectedSegmentProperty, value); }
    public double CursorSeconds { get => (double)GetValue(CursorSecondsProperty); set => SetValue(CursorSecondsProperty, value); }
    public double? Playhead { get => (double?)GetValue(PlayheadProperty); set => SetValue(PlayheadProperty, value); }
    /// <summary>The overall change, so the "after" drawing matches what will be saved.</summary>
    public double GainDb { get => (double)GetValue(GainDbProperty); set => SetValue(GainDbProperty, value); }
    public bool Limiter { get => (bool)GetValue(LimiterProperty); set => SetValue(LimiterProperty, value); }
    public double CeilingDb { get => (double)GetValue(CeilingDbProperty); set => SetValue(CeilingDbProperty, value); }
    /// <summary>Shown instead of a waveform (loading, errors).</summary>
    public string? Message { get => (string?)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }

    public const double NewPartGainDb = 6;
    private const double AxisHeight = 18;
    private const double EdgeGrab = 6;

    private enum DragMode { None, Create, Move, ResizeStart, ResizeEnd }
    private DragMode mode;
    private SegmentItem? dragSegment;
    private double dragStartX, dragAnchorTime, dragOffset;
    private double createFrom, createTo;

    public WaveformView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        ToolTip = "Drag across the waveform to boost just that part.\nDrag a part's edges to resize it, scroll over it to change its dB, right-click to remove it.\nClick to choose where the preview starts.";
        ToolTipService.SetInitialShowDelay(this, 1200);
    }

    // ------------------------------------------------------------ segment change tracking

    private void OnSegmentsChanged(DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ObservableCollection<SegmentItem> old)
        {
            old.CollectionChanged -= OnCollectionChanged;
            foreach (var s in old) s.PropertyChanged -= OnSegmentChanged;
        }
        if (e.NewValue is ObservableCollection<SegmentItem> now)
        {
            now.CollectionChanged += OnCollectionChanged;
            foreach (var s in now) s.PropertyChanged += OnSegmentChanged;
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null) foreach (SegmentItem s in e.OldItems) s.PropertyChanged -= OnSegmentChanged;
        if (e.NewItems is not null) foreach (SegmentItem s in e.NewItems) s.PropertyChanged += OnSegmentChanged;
        if (e.Action == NotifyCollectionChangedAction.Reset && sender is ObservableCollection<SegmentItem> c)
            foreach (var s in c) { s.PropertyChanged -= OnSegmentChanged; s.PropertyChanged += OnSegmentChanged; }
        InvalidateVisual();
    }

    private void OnSegmentChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    // ------------------------------------------------------------ geometry helpers

    private double Duration => Math.Max(0, Waveform?.DurationSeconds ?? 0);
    private Rect Plot => new(0, 0, ActualWidth, Math.Max(0, ActualHeight - AxisHeight));
    private double XOf(double t) => Duration <= 0 ? 0 : t / Duration * ActualWidth;
    private double TimeOf(double x) => Duration <= 0 ? 0 : Math.Clamp(x / Math.Max(1, ActualWidth), 0, 1) * Duration;

    private Brush B(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    /// <summary>A frozen copy at some opacity (theme brushes can be bound and refuse to freeze when cloned).</summary>
    private static Brush Faded(Brush b, double opacity)
    {
        var color = b is SolidColorBrush s ? s.Color : Colors.DodgerBlue;
        var copy = new SolidColorBrush(color) { Opacity = opacity * b.Opacity };
        copy.Freeze();
        return copy;
    }

    private double SegmentFactorAt(double t)
    {
        double f = 1;
        if (Segments is null) return f;
        foreach (var s in Segments)
            if (t >= s.Start && t < s.End) f *= Decibels.ToAmplitude(s.GainDb);
        return f;
    }

    // ------------------------------------------------------------ drawing

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < AxisHeight + 8) return;

        var plot = Plot;
        var accent = B("AccentFillColorDefaultBrush", Brushes.DodgerBlue);
        var text2 = B("TextFillColorSecondaryBrush", Brushes.Gray);
        var text3 = B("TextFillColorTertiaryBrush", Brushes.DarkGray);
        var critical = B("SystemFillColorCriticalBrush", Brushes.IndianRed);
        var stroke = B("ControlStrokeColorDefaultBrush", Brushes.LightGray);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        dc.DrawRoundedRectangle(B("ControlFillColorSecondaryBrush", Brushes.Transparent), new Pen(stroke, 1), new Rect(0.5, 0.5, w - 1, plot.Height - 1), 6, 6);
        double mid = plot.Height / 2;
        double half = plot.Height / 2 - 6;
        dc.DrawLine(new Pen(stroke, 1), new Point(4, mid), new Point(w - 4, mid));

        var wave = Waveform;
        if (wave is null || wave.Buckets == 0 || Duration <= 0)
        {
            string msg = Message ?? "Select a file to see its waveform";
            var ft = Text(msg, 13, text2, dpi);
            dc.DrawText(ft, new Point((w - ft.Width) / 2, (plot.Height - ft.Height) / 2));
            return;
        }

        // Parts behind the waveform.
        if (Segments is not null)
        {
            foreach (var s in Segments)
            {
                double x0 = XOf(s.Start), x1 = Math.Max(x0 + 1, XOf(s.End));
                bool sel = s == SelectedSegment;
                var fill = Faded(accent, sel ? 0.30 : 0.16);
                dc.DrawRectangle(fill, sel ? new Pen(accent, 1.5) : null, new Rect(x0, 1, x1 - x0, plot.Height - 2));
            }
        }

        // Before (grey) and after (accent, red where it would clip).
        double gainAmp = Decibels.ToAmplitude(GainDb);
        double ceilingAmp = Decibels.ToAmplitude(Math.Clamp(CeilingDb, -24, 0));
        bool limiter = Limiter;

        var before = new StreamGeometry();
        var after = new StreamGeometry();
        var clipped = new StreamGeometry();
        using (var gb = before.Open())
        using (var ga = after.Open())
        using (var gc = clipped.Open())
        {
            int columns = (int)Math.Ceiling(w);
            for (int x = 0; x < columns; x += 2)
            {
                int b0 = (int)((long)x * wave.Buckets / columns);
                int b1 = Math.Max(b0 + 1, (int)((long)(x + 2) * wave.Buckets / columns));
                float peak = 0;
                for (int b = b0; b < b1 && b < wave.Buckets; b++) peak = Math.Max(peak, wave.Peaks[b]);

                double t = TimeOf(x + 1);
                double amp = peak * gainAmp * SegmentFactorAt(t);
                double orig = Math.Min(peak, 1.0);
                AddBar(gb, x, mid, orig * half);

                if (limiter)
                {
                    AddBar(ga, x, mid, Math.Min(amp, ceilingAmp) * half);
                }
                else
                {
                    AddBar(ga, x, mid, Math.Min(amp, 1.0) * half);
                    if (amp > 1.0) AddBar(gc, x, mid, half);
                }
            }
        }
        before.Freeze(); after.Freeze(); clipped.Freeze();

        dc.DrawGeometry(text3, null, before);
        var afterBrush = Faded(accent, 0.85);
        dc.DrawGeometry(afterBrush, null, after);
        if (!limiter) dc.DrawGeometry(critical, null, clipped);

        // Labels on parts.
        if (Segments is not null)
        {
            foreach (var s in Segments)
            {
                double x0 = XOf(s.Start), x1 = XOf(s.End);
                var ft = Text(s.GainText, 11, B("TextOnAccentFillColorPrimaryBrush", Brushes.White), dpi, bold: true);
                double lw = ft.Width + 10;
                double lx = Math.Clamp(x0 + 3, 0, Math.Max(0, w - lw));
                if (x1 - x0 < lw && s != SelectedSegment) continue;
                dc.DrawRoundedRectangle(accent, null, new Rect(lx, 4, lw, ft.Height + 2), 4, 4);
                dc.DrawText(ft, new Point(lx + 5, 5));
            }
        }

        // Part being drawn.
        if (mode == DragMode.Create)
        {
            double a = XOf(Math.Min(createFrom, createTo)), b = XOf(Math.Max(createFrom, createTo));
            var fill = Faded(accent, 0.22);
            var dash = new Pen(accent, 1) { DashStyle = DashStyles.Dash };
            dc.DrawRectangle(fill, dash, new Rect(a, 1, Math.Max(1, b - a), plot.Height - 2));
        }

        // Preview start and playhead.
        double cx = XOf(CursorSeconds);
        dc.DrawLine(new Pen(text2, 1), new Point(cx, 0), new Point(cx, plot.Height));
        var tri = new StreamGeometry();
        using (var g = tri.Open())
        {
            g.BeginFigure(new Point(cx - 5, 0), true, true);
            g.LineTo(new Point(cx + 5, 0), true, false);
            g.LineTo(new Point(cx, 7), true, false);
        }
        tri.Freeze();
        dc.DrawGeometry(text2, null, tri);

        if (Playhead is double ph)
        {
            double px = XOf(ph);
            dc.DrawLine(new Pen(accent, 2), new Point(px, 0), new Point(px, plot.Height));
        }

        // Time axis.
        double step = PickTickStep(Duration, w);
        for (double t = 0; t <= Duration + 1e-6; t += step)
        {
            double x = XOf(t);
            var ft = Text(SegmentItem.FormatTime(t).Replace(".0", ""), 10.5, text3, dpi);
            double lx = Math.Clamp(x - ft.Width / 2, 0, w - ft.Width);
            dc.DrawLine(new Pen(stroke, 1), new Point(x, plot.Height), new Point(x, plot.Height + 3));
            dc.DrawText(ft, new Point(lx, plot.Height + 3));
        }
    }

    private static void AddBar(StreamGeometryContext g, double x, double mid, double halfHeight)
    {
        halfHeight = Math.Max(0.5, halfHeight);
        g.BeginFigure(new Point(x, mid - halfHeight), true, true);
        g.LineTo(new Point(x + 1.5, mid - halfHeight), false, false);
        g.LineTo(new Point(x + 1.5, mid + halfHeight), false, false);
        g.LineTo(new Point(x, mid + halfHeight), false, false);
    }

    private static double PickTickStep(double duration, double width)
    {
        double[] steps = [0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600];
        foreach (double s in steps)
            if (duration / s * 70 <= width) return s;
        return 7200;
    }

    private FormattedText Text(string s, double size, Brush brush, double dpi, bool bold = false) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, brush, dpi);

    // ------------------------------------------------------------ mouse and keys

    private (SegmentItem? Segment, DragMode Mode) HitTest(double x)
    {
        if (Segments is null) return (null, DragMode.None);
        // Topmost (last added) first, edges before bodies.
        for (int i = Segments.Count - 1; i >= 0; i--)
        {
            var s = Segments[i];
            double x0 = XOf(s.Start), x1 = XOf(s.End);
            if (Math.Abs(x - x0) <= EdgeGrab) return (s, DragMode.ResizeStart);
            if (Math.Abs(x - x1) <= EdgeGrab) return (s, DragMode.ResizeEnd);
        }
        for (int i = Segments.Count - 1; i >= 0; i--)
        {
            var s = Segments[i];
            if (x > XOf(s.Start) && x < XOf(s.End)) return (s, DragMode.Move);
        }
        return (null, DragMode.None);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Duration <= 0) return;
        Focus();
        double x = e.GetPosition(this).X;
        var (seg, m) = HitTest(x);
        dragStartX = x;
        if (seg is not null)
        {
            mode = m;
            dragSegment = seg;
            SelectedSegment = seg;
            dragOffset = TimeOf(x) - seg.Start;
        }
        else
        {
            mode = DragMode.Create;
            dragAnchorTime = createFrom = createTo = TimeOf(x);
        }
        CaptureMouse();
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        double x = e.GetPosition(this).X;
        if (mode == DragMode.None)
        {
            Cursor = HitTest(x).Mode switch
            {
                DragMode.ResizeStart or DragMode.ResizeEnd => Cursors.SizeWE,
                DragMode.Move => Cursors.SizeAll,
                _ => Duration > 0 ? Cursors.IBeam : Cursors.Arrow,
            };
            return;
        }

        double t = TimeOf(x);
        const double MinLen = 0.05;
        switch (mode)
        {
            case DragMode.Create:
                createTo = t;
                InvalidateVisual();
                break;
            case DragMode.Move when dragSegment is not null:
                double len = dragSegment.End - dragSegment.Start;
                double start = Math.Clamp(t - dragOffset, 0, Math.Max(0, Duration - len));
                dragSegment.Start = start;
                dragSegment.End = start + len;
                break;
            case DragMode.ResizeStart when dragSegment is not null:
                dragSegment.Start = Math.Clamp(t, 0, dragSegment.End - MinLen);
                break;
            case DragMode.ResizeEnd when dragSegment is not null:
                dragSegment.End = Math.Clamp(t, dragSegment.Start + MinLen, Duration);
                break;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (mode == DragMode.None) return;
        double x = e.GetPosition(this).X;
        bool moved = Math.Abs(x - dragStartX) >= 4;

        if (mode == DragMode.Create)
        {
            if (moved && Segments is not null)
            {
                double a = Math.Min(dragAnchorTime, createTo), b = Math.Max(dragAnchorTime, createTo);
                var seg = new SegmentItem(a, b, NewPartGainDb);
                Segments.Add(seg);
                SelectedSegment = seg;
            }
            else
            {
                CursorSeconds = TimeOf(x);
                SelectedSegment = null;
            }
        }
        else if (!moved)
        {
            CursorSeconds = TimeOf(x); // a click inside a part also moves the preview start
        }

        mode = DragMode.None;
        dragSegment = null;
        ReleaseMouseCapture();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (mode != DragMode.None) { mode = DragMode.None; dragSegment = null; InvalidateVisual(); }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        var (seg, _) = HitTest(e.GetPosition(this).X);
        if (seg is null || Segments is null) return;
        SelectedSegment = seg;
        var menu = new ContextMenu();
        var remove = new MenuItem { Header = "Remove this part" };
        remove.Click += (_, _) => { Segments.Remove(seg); SelectedSegment = null; };
        menu.Items.Add(remove);
        foreach (double g in new[] { -6.0, 3, 6, 10, 20 })
        {
            var item = new MenuItem { Header = $"Set to {Decibels.Format(g)}" };
            item.Click += (_, _) => seg.GainDb = g;
            menu.Items.Add(item);
        }
        menu.PlacementTarget = this;
        menu.IsOpen = true;
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var (seg, _) = HitTest(e.GetPosition(this).X);
        if (seg is null) { base.OnMouseWheel(e); return; }
        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 5 : Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 0.1 : 1;
        seg.GainDb = Math.Round(seg.GainDb + Math.Sign(e.Delta) * step, 2);
        SelectedSegment = seg;
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if ((e.Key == Key.Delete || e.Key == Key.Back) && SelectedSegment is { } s && Segments is not null)
        {
            Segments.Remove(s);
            SelectedSegment = null;
            e.Handled = true;
        }
    }
}
