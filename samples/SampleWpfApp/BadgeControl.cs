using System.Windows;
using System.Windows.Controls;

namespace SampleWpfApp;

/// <summary>Shows a small count badge.</summary>
public class BadgeControl : Control
{
    public static readonly DependencyProperty CountProperty =
        DependencyProperty.Register(nameof(Count), typeof(int), typeof(BadgeControl), new PropertyMetadata(0));

    /// <summary>The number shown in the badge.</summary>
    public int Count
    {
        get => (int)GetValue(CountProperty);
        set => SetValue(CountProperty, value);
    }
}
