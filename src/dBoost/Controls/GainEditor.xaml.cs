using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DBoost.Core;

namespace DBoost.App.Controls;

/// <summary>
/// A number box for dB (and seconds, LUFS). Type any value, drag left/right on it to scrub, scroll the
/// wheel, use the arrow keys, or the - / + buttons. Shift makes every step 10x bigger, Ctrl 10x finer.
/// No limits unless <see cref="Minimum"/> / <see cref="Maximum"/> are set.
/// </summary>
public partial class GainEditor : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(GainEditor),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((GainEditor)d).ShowValue()));

    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(nameof(Step), typeof(double), typeof(GainEditor), new PropertyMetadata(0.5));
    public static readonly DependencyProperty ScrubPerPixelProperty = DependencyProperty.Register(nameof(ScrubPerPixel), typeof(double), typeof(GainEditor), new PropertyMetadata(0.1));
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(GainEditor), new PropertyMetadata(double.NegativeInfinity));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(GainEditor), new PropertyMetadata(double.PositiveInfinity));
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(nameof(Unit), typeof(string), typeof(GainEditor),
        new PropertyMetadata("dB", (d, e) => ((GainEditor)d).UnitText.Text = e.NewValue as string ?? ""));
    public static readonly DependencyProperty ShowSignProperty = DependencyProperty.Register(nameof(ShowSign), typeof(bool), typeof(GainEditor),
        new PropertyMetadata(true, (d, _) => ((GainEditor)d).ShowValue()));
    public static readonly DependencyProperty IsLargeProperty = DependencyProperty.Register(nameof(IsLarge), typeof(bool), typeof(GainEditor),
        new PropertyMetadata(false, (d, _) => ((GainEditor)d).ApplySize()));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public double ScrubPerPixel { get => (double)GetValue(ScrubPerPixelProperty); set => SetValue(ScrubPerPixelProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public bool ShowSign { get => (bool)GetValue(ShowSignProperty); set => SetValue(ShowSignProperty, value); }
    public bool IsLarge { get => (bool)GetValue(IsLargeProperty); set => SetValue(IsLargeProperty, value); }

    private Point pressAt;
    private double pressValue;
    private bool pressing, scrubbing;

    public GainEditor()
    {
        InitializeComponent();
        UnitText.Text = Unit;
        ApplySize();
        ShowValue();

        Box.PreviewMouseLeftButtonDown += Box_PreviewMouseLeftButtonDown;
        Box.PreviewMouseMove += Box_PreviewMouseMove;
        Box.PreviewMouseLeftButtonUp += Box_PreviewMouseLeftButtonUp;
        Box.LostMouseCapture += (_, _) => { pressing = false; scrubbing = false; };
        Box.LostKeyboardFocus += (_, _) => Commit();
        Box.PreviewKeyDown += Box_PreviewKeyDown;
        Box.GotKeyboardFocus += (_, _) => Box.Cursor = Cursors.IBeam;
        Box.LostKeyboardFocus += (_, _) => Box.Cursor = Cursors.SizeWE;
        Box.Cursor = Cursors.SizeWE;
        PreviewMouseWheel += OnWheel;
    }

    private void ApplySize()
    {
        bool large = IsLarge;
        Box.FontSize = large ? 30 : 14;
        Box.MinHeight = large ? 56 : 32;
        Box.Padding = large ? new Thickness(8, 2, 44, 2) : new Thickness(6, 2, 30, 2);
        UnitText.FontSize = large ? 18 : 13;
        UnitText.Margin = large ? new Thickness(0, 0, 14, 0) : new Thickness(0, 0, 10, 0);
        double button = large ? 48 : 30;
        foreach (var b in new[] { DownButton, UpButton })
        {
            b.Width = button;
            b.Height = button;
            b.FontSize = large ? 16 : 12;
        }
    }

    private double Clamp(double v) => Math.Clamp(v, Minimum, Maximum);

    private static double Modifier() =>
        Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 0.1 : 1;

    private void Nudge(double direction)
    {
        Commit();
        double v = Value + direction * Step * Modifier();
        Value = Clamp(Math.Round(v, 2));
    }

    private void Down_Click(object sender, RoutedEventArgs e) => Nudge(-1);
    private void Up_Click(object sender, RoutedEventArgs e) => Nudge(+1);

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        // The big dial always takes the wheel; small boxes only once clicked into, so scrolling the
        // settings column past them never changes a value by accident.
        if (!IsEnabled || (!IsLarge && !IsKeyboardFocusWithin)) return;
        Nudge(Math.Sign(e.Delta));
        e.Handled = true;
    }

    private void Box_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up: Nudge(+1); Box.SelectAll(); e.Handled = true; break;
            case Key.Down: Nudge(-1); Box.SelectAll(); e.Handled = true; break;
            case Key.Enter: Commit(); Box.SelectAll(); e.Handled = true; break;
            case Key.Escape: ShowValue(); Box.SelectAll(); e.Handled = true; break;
        }
    }

    // Press and drag sideways = scrub. Press and release without moving = start typing.
    private void Box_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Box.IsKeyboardFocusWithin) return; // already typing: let the text box place the caret
        pressing = true;
        scrubbing = false;
        pressAt = e.GetPosition(this);
        pressValue = Value;
        Box.CaptureMouse();
        e.Handled = true;
    }

    private void Box_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!pressing) return;
        double dx = e.GetPosition(this).X - pressAt.X;
        if (!scrubbing && Math.Abs(dx) < 3) return;
        scrubbing = true;
        double v = pressValue + dx * ScrubPerPixel * Modifier();
        Value = Clamp(Math.Round(v, Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 2 : 1));
        e.Handled = true;
    }

    private void Box_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!pressing) return;
        bool wasScrub = scrubbing;
        pressing = false;
        scrubbing = false;
        Box.ReleaseMouseCapture();
        if (!wasScrub)
        {
            Box.Focus();
            Box.SelectAll();
        }
        e.Handled = true;
    }

    private void Commit()
    {
        string text = Box.Text.Trim();
        if (!string.IsNullOrEmpty(Unit) && text.EndsWith(Unit, StringComparison.OrdinalIgnoreCase))
            text = text[..^Unit.Length];
        if (Unit == "s" && text.EndsWith("sec", StringComparison.OrdinalIgnoreCase)) text = text[..^3];

        if (Decibels.TryParse(text, out double v))
        {
            v = Clamp(v);
            if (v != Value) Value = v;
        }
        ShowValue();
    }

    private void ShowValue()
    {
        double v = Value;
        Box.Text = ShowSign ? Decibels.FormatNumber(v, 2) : v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
