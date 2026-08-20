using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TecnalHub.Controls;

/// <summary>
/// An outline icon, drawn from a vector geometry.
/// </summary>
/// <remarks>
/// <para>
/// Geometries live in <c>Resources/Icons/Icons.xaml</c> and are authored in a 24 x 24
/// space. <b>Not an icon font</b> - see the header of that file for why this project
/// will not depend on one.
/// </para>
/// <para>
/// Set either <see cref="Geometry"/> directly, or <see cref="Key"/> with the short name
/// (<c>Bell</c>, <c>Vessel</c>). The string form exists so <c>NavigationItem.Glyph</c>
/// can stay a plain string in the ViewModel rather than dragging a WPF type into it.
/// </para>
/// <para>
/// The stroke defaults to <c>TextSecondaryBrush</c> through a resource reference, so an
/// icon follows a theme switch without the caller doing anything, and a caller that sets
/// <see cref="Stroke"/> explicitly still wins.
/// </para>
/// </remarks>
public partial class Icon : UserControl
{
    /// <summary>The space the geometries are authored in.</summary>
    private const double DesignSize = 24.0;

    /// <summary>Rendered stroke width, in device-independent pixels, at any size.</summary>
    private const double TargetStroke = 1.5;

    public static readonly DependencyProperty GeometryProperty =
        DependencyProperty.Register(
            nameof(Geometry), typeof(Geometry), typeof(Icon),
            new PropertyMetadata(null));

    public static readonly DependencyProperty KeyProperty =
        DependencyProperty.Register(
            nameof(Key), typeof(string), typeof(Icon),
            new PropertyMetadata(null, OnKeyChanged));

    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register(
            nameof(Size), typeof(double), typeof(Icon),
            new PropertyMetadata(20.0, OnSizeChanged));

    public static readonly DependencyProperty StrokeProperty =
        DependencyProperty.Register(
            nameof(Stroke), typeof(Brush), typeof(Icon),
            new PropertyMetadata(null));

    public static readonly DependencyProperty ScaleProperty =
        DependencyProperty.Register(
            nameof(Scale), typeof(double), typeof(Icon),
            new PropertyMetadata(20.0 / DesignSize));

    public static readonly DependencyProperty PenThicknessProperty =
        DependencyProperty.Register(
            nameof(PenThickness), typeof(double), typeof(Icon),
            new PropertyMetadata(TargetStroke * DesignSize / 20.0));

    public Icon()
    {
        InitializeComponent();

        // A resource reference rather than a literal: the icon then follows a runtime
        // theme switch. Set in the constructor so an explicit Stroke in XAML, which is
        // applied afterwards, still overrides it.
        SetResourceReference(StrokeProperty, "TextSecondaryBrush");
    }

    /// <summary>The path to draw. Takes precedence over <see cref="Key"/>.</summary>
    public Geometry? Geometry
    {
        get => (Geometry?)GetValue(GeometryProperty);
        set => SetValue(GeometryProperty, value);
    }

    /// <summary>
    /// Short icon name, resolved as <c>Icon{Key}Geometry</c> from application resources.
    /// </summary>
    public string? Key
    {
        get => (string?)GetValue(KeyProperty);
        set => SetValue(KeyProperty, value);
    }

    /// <summary>Rendered edge length. 16, 20 or 24.</summary>
    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    /// <summary>Line colour. Defaults to slate, and follows the theme.</summary>
    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>Geometry scale factor. Computed from <see cref="Size"/>.</summary>
    public double Scale
    {
        get => (double)GetValue(ScaleProperty);
        private set => SetValue(ScaleProperty, value);
    }

    /// <summary>
    /// Pen width in geometry units, pre-divided by <see cref="Scale"/> so the stroke
    /// renders at a constant width whatever the icon size.
    /// </summary>
    public double PenThickness
    {
        get => (double)GetValue(PenThicknessProperty);
        private set => SetValue(PenThicknessProperty, value);
    }

    private static void OnSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Icon icon || e.NewValue is not double size || size <= 0)
        {
            return;
        }

        icon.Scale = size / DesignSize;
        icon.PenThickness = TargetStroke * DesignSize / size;
    }

    private static void OnKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Icon icon || e.NewValue is not string key || key.Length == 0)
        {
            return;
        }

        // A missing key leaves the icon blank rather than throwing: a wrong glyph name
        // should not take down a window. IconTests is what makes sure none are missing.
        if (Application.Current?.TryFindResource($"Icon{key}Geometry") is Geometry geometry)
        {
            icon.Geometry = geometry;
        }
    }
}
