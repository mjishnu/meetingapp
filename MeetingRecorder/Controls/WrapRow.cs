using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace MeetingRecorder.Controls;

/// <summary>
/// Lays children out left to right and wraps to a new line when they don't fit
/// so it stacks at narrow widths instead of clipping
/// </summary>
public sealed partial class WrapRow : Panel
{
    public static readonly DependencyProperty HorizontalSpacingProperty = DependencyProperty.Register(
        nameof(HorizontalSpacing), typeof(double), typeof(WrapRow), new PropertyMetadata(0d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty VerticalSpacingProperty = DependencyProperty.Register(
        nameof(VerticalSpacing), typeof(double), typeof(WrapRow), new PropertyMetadata(0d, OnLayoutPropertyChanged));

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((WrapRow)d).InvalidateMeasure();

    protected override Size MeasureOverride(Size availableSize)
    {
        double lineWidth = 0, lineHeight = 0, totalHeight = 0, maxWidth = 0;
        bool firstLine = true;
        foreach (var child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            if (child.Visibility == Visibility.Collapsed) continue;
            var size = child.DesiredSize;
            double needed = lineWidth == 0 ? size.Width : lineWidth + HorizontalSpacing + size.Width;
            if (lineWidth > 0 && needed > availableSize.Width)
            {
                totalHeight += (firstLine ? 0 : VerticalSpacing) + lineHeight;
                firstLine = false;
                maxWidth = Math.Max(maxWidth, lineWidth);
                lineWidth = size.Width;
                lineHeight = size.Height;
            }
            else
            {
                lineWidth = needed;
                lineHeight = Math.Max(lineHeight, size.Height);
            }
        }
        if (lineWidth > 0)
        {
            totalHeight += (firstLine ? 0 : VerticalSpacing) + lineHeight;
            maxWidth = Math.Max(maxWidth, lineWidth);
        }
        return new Size(Math.Min(maxWidth, availableSize.Width), totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var visible = Children.Where(c => c.Visibility != Visibility.Collapsed).ToList();
        double y = 0;
        int i = 0;
        while (i < visible.Count)
        {
            int start = i;
            double lineWidth = 0, lineHeight = 0;
            while (i < visible.Count)
            {
                var size = visible[i].DesiredSize;
                double needed = lineWidth == 0 ? size.Width : lineWidth + HorizontalSpacing + size.Width;
                if (lineWidth > 0 && needed > finalSize.Width) break;
                lineWidth = needed;
                lineHeight = Math.Max(lineHeight, size.Height);
                i++;
            }
            double x = 0;
            for (int j = start; j < i; j++)
            {
                var size = visible[j].DesiredSize;
                double w = Math.Min(size.Width, finalSize.Width);
                visible[j].Arrange(new Rect(x, y + (lineHeight - size.Height) / 2, w, size.Height));
                x += w + HorizontalSpacing;
            }
            y += lineHeight + VerticalSpacing;
        }
        return finalSize;
    }
}
