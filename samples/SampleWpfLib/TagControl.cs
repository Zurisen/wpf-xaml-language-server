using System.Windows;
using System.Windows.Controls;

namespace SampleWpfLib;

/// <summary>A rounded label for tagging items.</summary>
public class TagControl : ContentControl
{
    public static readonly DependencyProperty IsHighlightedProperty =
        DependencyProperty.Register(nameof(IsHighlighted), typeof(bool), typeof(TagControl), new PropertyMetadata(false));

    /// <summary>Draws the tag with the accent color.</summary>
    public bool IsHighlighted
    {
        get => (bool)GetValue(IsHighlightedProperty);
        set => SetValue(IsHighlightedProperty, value);
    }
}
