using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace TecnalHub.Controls;

public partial class RangeSlider : UserControl
{
    public static readonly DependencyProperty MinimumProperty =
        DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(RangeSlider),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnRangeChanged));

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(RangeSlider),
            new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender, OnRangeChanged));

    public static readonly DependencyProperty LowerValueProperty =
        DependencyProperty.Register(nameof(LowerValue), typeof(double), typeof(RangeSlider),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender, OnLowerValueChanged));

    public static readonly DependencyProperty UpperValueProperty =
        DependencyProperty.Register(nameof(UpperValue), typeof(double), typeof(RangeSlider),
            new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender, OnUpperValueChanged));

    public static readonly DependencyProperty RangeBrushProperty =
        DependencyProperty.Register(nameof(RangeBrush), typeof(Brush), typeof(RangeSlider),
            new PropertyMetadata(null));

    public static readonly DependencyProperty ThumbBrushProperty =
        DependencyProperty.Register(nameof(ThumbBrush), typeof(Brush), typeof(RangeSlider),
            new PropertyMetadata(null));

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double LowerValue
    {
        get => (double)GetValue(LowerValueProperty);
        set => SetValue(LowerValueProperty, value);
    }

    public double UpperValue
    {
        get => (double)GetValue(UpperValueProperty);
        set => SetValue(UpperValueProperty, value);
    }

    public Brush? RangeBrush
    {
        get => (Brush?)GetValue(RangeBrushProperty);
        set => SetValue(RangeBrushProperty, value);
    }

    public Brush? ThumbBrush
    {
        get => (Brush?)GetValue(ThumbBrushProperty);
        set => SetValue(ThumbBrushProperty, value);
    }

    private const double ThumbRadius = 8.0; // Half of 16px default thumb width
    private bool _isUpdating;

    public RangeSlider()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdatePositions();
        Loaded += (_, _) =>
        {
            if (RangeBrush == null)
            {
                RangeBrush = Application.Current?.TryFindResource("AccentBrush") as Brush ?? Brushes.DodgerBlue;
            }
            if (ThumbBrush == null)
            {
                ThumbBrush = Application.Current?.TryFindResource("ControlThumbBrush") as Brush ?? Brushes.White;
            }
            UpdatePositions();
        };

        RangeHighlight.MouseLeftButtonDown += OnRangeHighlightMouseDown;
        RangeHighlight.MouseMove += OnRangeHighlightMouseMove;
        RangeHighlight.MouseLeftButtonUp += OnRangeHighlightMouseUp;
    }

    private Point _dragStartPoint;
    private double _startLower;
    private double _startUpper;
    private bool _isDraggingRange;

    private void OnRangeHighlightMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            _isDraggingRange = true;
            _dragStartPoint = e.GetPosition(this);
            _startLower = LowerValue;
            _startUpper = UpperValue;
            RangeHighlight.CaptureMouse();
            e.Handled = true;
        }
    }

    private void OnRangeHighlightMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingRange)
        {
            return;
        }

        var currentPoint = e.GetPosition(this);
        var dx = currentPoint.X - _dragStartPoint.X;
        var trackWidth = Math.Max(1, ActualWidth - 2 * ThumbRadius);
        var span = Maximum - Minimum;
        if (span <= 0)
        {
            return;
        }

        var dVal = (dx / trackWidth) * span;
        var rangeLen = _startUpper - _startLower;

        var newLower = Math.Clamp(_startLower + dVal, Minimum, Maximum - rangeLen);
        var newUpper = newLower + rangeLen;

        LowerValue = Math.Round(newLower, 1);
        UpperValue = Math.Round(newUpper, 1);
    }

    private void OnRangeHighlightMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingRange)
        {
            _isDraggingRange = false;
            RangeHighlight.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RangeSlider slider && !slider._isUpdating)
        {
            slider.UpdatePositions();
        }
    }

    private static void OnLowerValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RangeSlider slider && !slider._isUpdating)
        {
            slider.UpdatePositions();
        }
    }

    private static void OnUpperValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RangeSlider slider && !slider._isUpdating)
        {
            slider.UpdatePositions();
        }
    }

    private void OnLowerThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        var trackWidth = Math.Max(1, ActualWidth - 2 * ThumbRadius);
        var span = Maximum - Minimum;
        if (span <= 0)
        {
            return;
        }

        var dVal = (e.HorizontalChange / trackWidth) * span;
        var newVal = Math.Clamp(LowerValue + dVal, Minimum, UpperValue);
        LowerValue = Math.Round(newVal, 1);
    }

    private void OnUpperThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        var trackWidth = Math.Max(1, ActualWidth - 2 * ThumbRadius);
        var span = Maximum - Minimum;
        if (span <= 0)
        {
            return;
        }

        var dVal = (e.HorizontalChange / trackWidth) * span;
        var newVal = Math.Clamp(UpperValue + dVal, LowerValue, Maximum);
        UpperValue = Math.Round(newVal, 1);
    }

    private void OnThumbDragStarted(object sender, DragStartedEventArgs e) { }
    private void OnThumbDragCompleted(object sender, DragCompletedEventArgs e) { }

    private void UpdatePositions()
    {
        if (ActualWidth <= 0 || Maximum <= Minimum)
        {
            return;
        }

        _isUpdating = true;
        try
        {
            var trackWidth = Math.Max(0, ActualWidth - 2 * ThumbRadius);
            var span = Maximum - Minimum;

            var normLower = Math.Clamp((LowerValue - Minimum) / span, 0.0, 1.0);
            var normUpper = Math.Clamp((UpperValue - Minimum) / span, 0.0, 1.0);

            var lowerX = normLower * trackWidth;
            var upperX = normUpper * trackWidth;

            Canvas.SetLeft(LowerThumb, lowerX);
            Canvas.SetLeft(UpperThumb, upperX);

            var rangeLeft = lowerX + ThumbRadius;
            var rangeWidth = Math.Max(0, upperX - lowerX);

            Canvas.SetLeft(RangeHighlight, rangeLeft);
            RangeHighlight.Width = rangeWidth;
        }
        finally
        {
            _isUpdating = false;
        }
    }
}
