using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using SyntheticDataGenerator.UI.Models;

namespace SyntheticDataGenerator.UI.Controls;

public partial class DiffTextPresenter : UserControl
{
    public static readonly DependencyProperty SegmentsProperty =
        DependencyProperty.Register(
            nameof(Segments),
            typeof(IReadOnlyList<DiffTextSegment>),
            typeof(DiffTextPresenter),
            new PropertyMetadata(null, OnSegmentsChanged));

    public static readonly DependencyProperty HighlightBrushProperty =
        DependencyProperty.Register(
            nameof(HighlightBrush),
            typeof(Brush),
            typeof(DiffTextPresenter),
            new PropertyMetadata(new SolidColorBrush(Color.FromRgb(0xFF, 0xCD, 0xD2)), OnSegmentsChanged));

    public DiffTextPresenter()
    {
        InitializeComponent();
    }

    public IReadOnlyList<DiffTextSegment>? Segments
    {
        get => (IReadOnlyList<DiffTextSegment>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public Brush HighlightBrush
    {
        get => (Brush)GetValue(HighlightBrushProperty);
        set => SetValue(HighlightBrushProperty, value);
    }

    private static void OnSegmentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DiffTextPresenter presenter)
            presenter.RenderSegments();
    }

    private void RenderSegments()
    {
        DisplayText.Inlines.Clear();

        if (Segments is null || Segments.Count == 0)
            return;

        foreach (var segment in Segments)
        {
            var run = new Run(segment.Text);
            if (segment.IsDifferent)
                run.Background = HighlightBrush;

            DisplayText.Inlines.Add(run);
        }
    }
}
