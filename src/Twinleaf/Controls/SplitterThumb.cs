// SPDX-License-Identifier: Apache-2.0

using System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Twinleaf.Controls;

/// <summary>
/// A drag handle that resizes the grid column to its left. The base Windows App
/// SDK ships no GridSplitter (it lives in the Community Toolkit) and the XAML
/// primitives are sealed, so this is a thin UserControl that captures the
/// pointer and drives the target column's width directly.
/// </summary>
public sealed partial class SplitterThumb : UserControl
{
    private bool _dragging;
    private double _startWidth;
    private double _startX;

    /// <summary>The column this handle resizes.</summary>
    public ColumnDefinition? TargetColumn { get; set; }

    /// <summary>
    /// Set for a handle on the <em>left</em> edge of its target — a right-hand
    /// sidebar — where dragging right must shrink the column rather than grow
    /// it.
    /// </summary>
    public bool Invert { get; set; }

    public SplitterThumb()
    {
        Width = 6;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        // UserControl paints nothing itself, so hit-testing needs real content:
        // a transparent Border fills the handle and receives the pointer.
        Content = new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += (_, _) => _dragging = false;

        // ProtectedCursor needs a realised element with an input site.
        Loaded += (_, _) =>
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (TargetColumn is null)
        {
            return;
        }

        // Measure against the window, not this element: once the pointer is
        // captured the local origin moves with the drag.
        _startX = e.GetCurrentPoint(null).Position.X;
        _startWidth = TargetColumn.ActualWidth;
        _dragging = CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || TargetColumn is null)
        {
            return;
        }

        var delta = e.GetCurrentPoint(null).Position.X - _startX;
        var width = Math.Max(TargetColumn.MinWidth, _startWidth + (Invert ? -delta : delta));
        TargetColumn.Width = new GridLength(width, GridUnitType.Pixel);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        ReleasePointerCapture(e.Pointer);
        _dragging = false;
        e.Handled = true;
    }
}
