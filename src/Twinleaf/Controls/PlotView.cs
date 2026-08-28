// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.UI;
using Twinleaf.Models;

namespace Twinleaf.Controls;

/// <summary>
/// The plot surface: axes, traces, legend and cursor readout, drawn with Win2D
/// (Direct2D). Mirrors the macOS app's <c>PlotCanvas</c>.
///
/// Data arrives already decimated by the Rust core — one point per screen
/// column pair in min/max mode — so rendering is a straight polyline per series
/// with no client-side downsampling.
/// </summary>
public sealed partial class PlotView : UserControl
{
    private readonly CanvasControl _canvas = new();

    private PlotFrame? _frame;
    private ViewConfig _view = new();
    private Point? _cursor;

    /// <summary>Plot area insets, leaving room for tick labels and the legend.</summary>
    private const float MarginLeft = 72;
    private const float MarginRight = 16;
    private const float MarginTop = 12;
    private const float MarginBottom = 40;

    private static readonly Color[] TraceColors =
    [
        Color.FromArgb(255, 0x2E, 0x7D, 0xD1),
        Color.FromArgb(255, 0xE8, 0x71, 0x0A),
        Color.FromArgb(255, 0x22, 0xA4, 0x5D),
        Color.FromArgb(255, 0xC4, 0x31, 0x4B),
        Color.FromArgb(255, 0x8B, 0x5C, 0xF6),
        Color.FromArgb(255, 0x0E, 0x9A, 0xA7),
        Color.FromArgb(255, 0xB7, 0x79, 0x1F),
        Color.FromArgb(255, 0xD6, 0x47, 0x8E),
    ];

    /// <summary>Resolves a display label for a column; supplied by the window.</summary>
    public Func<ColumnKey, string>? LabelProvider { get; set; }

    public PlotView()
    {
        Content = _canvas;
        _canvas.Draw += OnDraw;

        // Win2D holds device resources that must be released before the control
        // is collected, or the swap chain leaks for the process lifetime.
        Unloaded += (_, _) => _canvas.RemoveFromVisualTree();

        _canvas.PointerMoved += OnPointerMoved;
        _canvas.PointerExited += OnPointerExited;
    }

    /// <summary>Latest frame for the pane this view renders.</summary>
    public void Update(PlotFrame frame)
    {
        _frame = frame;
        _canvas.Invalidate();
    }

    public void UpdateView(ViewConfig view)
    {
        _view = view;
        _canvas.Invalidate();
    }

    public void Clear()
    {
        _frame = null;
        _canvas.Invalidate();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        _cursor = e.GetCurrentPoint(_canvas).Position;
        _canvas.Invalidate();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _cursor = null;
        _canvas.Invalidate();
    }

    // -----------------------------------------------------------------
    // Rendering
    // -----------------------------------------------------------------

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var session = args.DrawingSession;
        var width = (float)sender.ActualWidth;
        var height = (float)sender.ActualHeight;

        var palette = Palette.For(ActualTheme);

        var plotLeft = MarginLeft;
        var plotTop = MarginTop;
        var plotRight = width - MarginRight;
        var plotBottom = height - MarginBottom;

        if (plotRight <= plotLeft || plotBottom <= plotTop)
        {
            return;
        }

        session.FillRectangle(plotLeft, plotTop, plotRight - plotLeft, plotBottom - plotTop, palette.PlotBackground);

        var series = VisibleSeries();
        if (series.Count == 0)
        {
            DrawPlaceholder(session, palette, plotLeft, plotTop, plotRight, plotBottom);
            DrawFrame(session, palette, plotLeft, plotTop, plotRight, plotBottom);
            return;
        }

        var isFft = _frame!.Mode == PlotMode.Fft;
        var logX = isFft && _view.FftLogX;
        var logY = isFft ? _view.FftLogY : _view.LogY;

        // Timeseries x values are absolute epoch seconds. Plotting them raw
        // makes every tick label collapse to the same text — a 10 s window is
        // nine significant digits into a ~1.79e9 timestamp — so the axis is
        // drawn relative to the window end instead: 0 at the right edge,
        // negative seconds going back.
        var timeOrigin = isFft ? 0.0 : TimeOrigin(series);

        var (xMin, xMax, yMin, yMax) = Bounds(series, logX, logY, timeOrigin);

        if (!isFft)
        {
            // Anchor to the requested window rather than to what has arrived, so
            // the trace scrolls through a fixed span instead of rescaling with
            // every frame. Matches the macOS app's (end - window)...end range.
            xMin = -Math.Max(_view.WindowSeconds, 1e-6);
            xMax = 0;
        }

        var xAxis = new AxisScale(xMin, xMax, plotLeft, plotRight, logX);
        // Screen Y grows downward, so the pixel range is inverted.
        var yAxis = new AxisScale(yMin, yMax, plotBottom, plotTop, logY);

        DrawGrid(session, palette, xAxis, yAxis, plotLeft, plotTop, plotRight, plotBottom);
        DrawTraces(session, series, xAxis, yAxis, timeOrigin, plotLeft, plotTop, plotRight, plotBottom);
        DrawFrame(session, palette, plotLeft, plotTop, plotRight, plotBottom);
        DrawAxisTitles(session, palette, isFft, plotLeft, plotRight, plotBottom, height);
        DrawLegend(session, palette, series, plotLeft, plotTop, plotRight);
        DrawCursor(session, palette, xAxis, yAxis, plotLeft, plotTop, plotRight, plotBottom);
    }

    private List<PlotSeries> VisibleSeries()
    {
        if (_frame is null)
        {
            return [];
        }

        // Series flagged outside-window or warming-up arrive with no points;
        // they stay in the legend but contribute nothing to the axis bounds.
        return _frame.Series.Where(item => item.Points.Count > 0).ToList();
    }

    /// <summary>
    /// The absolute time that maps to x = 0. The core's <c>viewportEnd</c> is
    /// authoritative when present; otherwise the newest sample stands in, which
    /// is what the macOS app falls back to.
    /// </summary>
    private double TimeOrigin(IReadOnlyList<PlotSeries> series)
    {
        if (_frame?.ViewportEnd is { } end && double.IsFinite(end))
        {
            return end;
        }

        var newest = double.MinValue;
        foreach (var item in series)
        {
            for (var index = item.Points.Count - 1; index >= 0; index--)
            {
                if (double.IsFinite(item.Points[index].X))
                {
                    newest = Math.Max(newest, item.Points[index].X);
                    break;
                }
            }
        }

        return newest > double.MinValue ? newest : 0.0;
    }

    private static (double XMin, double XMax, double YMin, double YMax) Bounds(
        IReadOnlyList<PlotSeries> series,
        bool logX,
        bool logY,
        double timeOrigin)
    {
        var xMin = double.MaxValue;
        var xMax = double.MinValue;
        var yMin = double.MaxValue;
        var yMax = double.MinValue;

        foreach (var item in series)
        {
            foreach (var point in item.Points)
            {
                if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
                {
                    continue;
                }

                var x = point.X - timeOrigin;

                // A log axis cannot show non-positive samples; excluding them
                // from the bounds keeps the visible decades meaningful.
                if (logX && x <= 0) continue;
                if (logY && point.Y <= 0) continue;

                if (x < xMin) xMin = x;
                if (x > xMax) xMax = x;
                if (point.Y < yMin) yMin = point.Y;
                if (point.Y > yMax) yMax = point.Y;
            }
        }

        if (xMin > xMax)
        {
            return (0, 1, 0, 1);
        }

        if (!logY)
        {
            // Breathing room so traces never sit on the frame.
            var padding = (yMax - yMin) * 0.05;
            if (padding <= 0)
            {
                padding = Math.Abs(yMax) > 0 ? Math.Abs(yMax) * 0.05 : 0.5;
            }

            yMin -= padding;
            yMax += padding;
        }

        return (xMin, xMax, yMin, yMax);
    }

    private void DrawGrid(
        CanvasDrawingSession session,
        Palette palette,
        AxisScale xAxis,
        AxisScale yAxis,
        float left,
        float top,
        float right,
        float bottom)
    {
        using var format = new CanvasTextFormat
        {
            FontSize = 11,
            FontFamily = "Segoe UI",
        };

        // Tick density scaled to the plot size; ~80 px per label horizontally
        // and ~44 px vertically keeps labels from colliding.
        var xTicks = xAxis.Ticks(Math.Max(2, (int)((right - left) / 80)));
        var yTicks = yAxis.Ticks(Math.Max(2, (int)((bottom - top) / 44)));

        foreach (var (value, label) in xTicks)
        {
            var x = (float)xAxis.ToPixel(value);
            if (x < left - 0.5f || x > right + 0.5f)
            {
                continue;
            }

            session.DrawLine(x, top, x, bottom, palette.Grid, 1);

            // Draw the measured layout rather than re-issuing DrawText with the
            // same string: this runs on every frame while streaming, and
            // DrawText would lay the text out a second time just to place it.
            using var layout = new CanvasTextLayout(session, label, format, 200, 20);
            var textWidth = (float)layout.LayoutBounds.Width;
            session.DrawTextLayout(layout, x - textWidth / 2, bottom + 4, palette.AxisText);
        }

        foreach (var (value, label) in yTicks)
        {
            var y = (float)yAxis.ToPixel(value);
            if (y < top - 0.5f || y > bottom + 0.5f)
            {
                continue;
            }

            session.DrawLine(left, y, right, y, palette.Grid, 1);

            using var layout = new CanvasTextLayout(session, label, format, 200, 20);
            var textWidth = (float)layout.LayoutBounds.Width;
            var textHeight = (float)layout.LayoutBounds.Height;
            session.DrawTextLayout(layout, left - 8 - textWidth, y - textHeight / 2, palette.AxisText);
        }
    }

    private void DrawTraces(
        CanvasDrawingSession session,
        IReadOnlyList<PlotSeries> series,
        AxisScale xAxis,
        AxisScale yAxis,
        double timeOrigin,
        float left,
        float top,
        float right,
        float bottom)
    {
        using var clip = session.CreateLayer(1f, new Rect(left, top, right - left, bottom - top));

        for (var index = 0; index < series.Count; index++)
        {
            var item = series[index];
            var color = TraceColors[index % TraceColors.Length];
            DrawSeries(session, item, color, xAxis, yAxis, timeOrigin);
        }
    }

    private static void DrawSeries(
        CanvasDrawingSession session,
        PlotSeries series,
        Color color,
        AxisScale xAxis,
        AxisScale yAxis,
        double timeOrigin)
    {
        var points = series.Points;
        if (points.Count == 0)
        {
            return;
        }

        if (points.Count == 1)
        {
            var only = new Vector2((float)xAxis.ToPixel(points[0].X - timeOrigin), (float)yAxis.ToPixel(points[0].Y));
            session.FillCircle(only, 2f, color);
            return;
        }

        using var builder = new CanvasPathBuilder(session);
        var figureOpen = false;

        foreach (var point in points)
        {
            // A gap in the data (NaN) must break the line rather than draw a
            // straight segment across the missing span.
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
            {
                if (figureOpen)
                {
                    builder.EndFigure(CanvasFigureLoop.Open);
                    figureOpen = false;
                }

                continue;
            }

            var position = new Vector2((float)xAxis.ToPixel(point.X - timeOrigin), (float)yAxis.ToPixel(point.Y));
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y))
            {
                continue;
            }

            if (!figureOpen)
            {
                builder.BeginFigure(position);
                figureOpen = true;
            }
            else
            {
                builder.AddLine(position);
            }
        }

        if (figureOpen)
        {
            builder.EndFigure(CanvasFigureLoop.Open);
        }
        else
        {
            return;
        }

        using var geometry = CanvasGeometry.CreatePath(builder);
        session.DrawGeometry(geometry, color, 1.4f);
    }

    private void DrawFrame(
        CanvasDrawingSession session,
        Palette palette,
        float left,
        float top,
        float right,
        float bottom)
    {
        session.DrawRectangle(left, top, right - left, bottom - top, palette.Axis, 1);
    }

    private void DrawAxisTitles(
        CanvasDrawingSession session,
        Palette palette,
        bool isFft,
        float left,
        float right,
        float bottom,
        float height)
    {
        using var format = new CanvasTextFormat
        {
            FontSize = 11,
            FontFamily = "Segoe UI",
        };

        var title = isFft ? "Frequency (Hz)" : "Time (s)";
        using var layout = new CanvasTextLayout(session, title, format, 300, 20);
        var x = (left + right) / 2 - (float)layout.LayoutBounds.Width / 2;
        session.DrawText(title, x, height - 20, palette.AxisText, format);
    }

    private void DrawLegend(
        CanvasDrawingSession session,
        Palette palette,
        IReadOnlyList<PlotSeries> series,
        float left,
        float top,
        float right)
    {
        if (_frame is null || _frame.Series.Count == 0)
        {
            return;
        }

        using var format = new CanvasTextFormat
        {
            FontSize = 11,
            FontFamily = "Segoe UI",
        };

        var y = top + 6;
        var visibleIndex = 0;

        foreach (var item in _frame.Series)
        {
            var label = LabelProvider?.Invoke(item.Key) ?? item.Key.ToString();

            // Flagged series carry no points; say why rather than omitting them,
            // so a ticked channel never silently disappears from the plot.
            if (item.OutsideWindow)
            {
                label += "  (different time base)";
            }
            else if (item.WarmingUp)
            {
                label += "  (warming up)";
            }

            var hasData = item.Points.Count > 0;
            var color = hasData
                ? TraceColors[visibleIndex++ % TraceColors.Length]
                : palette.MutedText;

            var swatchX = right - 8 - 220;
            session.FillRectangle(swatchX, y + 4, 12, 3, color);
            session.DrawText(label, swatchX + 18, y, hasData ? palette.AxisText : palette.MutedText, format);

            y += 16;
        }
    }

    private void DrawCursor(
        CanvasDrawingSession session,
        Palette palette,
        AxisScale xAxis,
        AxisScale yAxis,
        float left,
        float top,
        float right,
        float bottom)
    {
        if (_cursor is not { } cursor)
        {
            return;
        }

        var x = (float)cursor.X;
        var y = (float)cursor.Y;
        if (x < left || x > right || y < top || y > bottom)
        {
            return;
        }

        session.DrawLine(x, top, x, bottom, palette.Crosshair, 1);
        session.DrawLine(left, y, right, y, palette.Crosshair, 1);

        using var format = new CanvasTextFormat
        {
            FontSize = 11,
            FontFamily = "Consolas",
        };

        var readout = string.Format(
            CultureInfo.CurrentCulture,
            "{0}   {1}",
            NumericDisplayPolicy.Reading(xAxis.ToValue(x)),
            NumericDisplayPolicy.Reading(yAxis.ToValue(y)));

        using var layout = new CanvasTextLayout(session, readout, format, 300, 20);
        var boxWidth = (float)layout.LayoutBounds.Width + 12;
        var boxHeight = (float)layout.LayoutBounds.Height + 8;

        // Flip the readout to the other side of the crosshair near the edges so
        // it never renders outside the plot area.
        var boxX = x + 8 + boxWidth > right ? x - 8 - boxWidth : x + 8;
        var boxY = y - 8 - boxHeight < top ? y + 8 : y - 8 - boxHeight;

        session.FillRectangle(boxX, boxY, boxWidth, boxHeight, palette.ReadoutBackground);
        session.DrawRectangle(boxX, boxY, boxWidth, boxHeight, palette.Axis, 1);
        session.DrawText(readout, boxX + 6, boxY + 4, palette.AxisText, format);
    }

    private void DrawPlaceholder(
        CanvasDrawingSession session,
        Palette palette,
        float left,
        float top,
        float right,
        float bottom)
    {
        using var format = new CanvasTextFormat
        {
            FontSize = 13,
            FontFamily = "Segoe UI",
        };

        const string message = "Tick a channel in the sidebar to plot it.";
        using var layout = new CanvasTextLayout(session, message, format, 400, 40);
        var x = (left + right) / 2 - (float)layout.LayoutBounds.Width / 2;
        var y = (top + bottom) / 2 - (float)layout.LayoutBounds.Height / 2;
        session.DrawText(message, x, y, palette.MutedText, format);
    }

    /// <summary>
    /// Theme-dependent plot colours. The canvas paints its own background rather
    /// than inheriting the page brush, so it needs an explicit pair.
    /// </summary>
    private readonly record struct Palette(
        Color PlotBackground,
        Color Grid,
        Color Axis,
        Color AxisText,
        Color MutedText,
        Color Crosshair,
        Color ReadoutBackground)
    {
        public static Palette For(ElementTheme theme) =>
            theme == ElementTheme.Dark
                ? new Palette(
                    PlotBackground: Color.FromArgb(255, 0x1C, 0x1C, 0x1C),
                    Grid: Color.FromArgb(40, 0xFF, 0xFF, 0xFF),
                    Axis: Color.FromArgb(120, 0xFF, 0xFF, 0xFF),
                    AxisText: Color.FromArgb(220, 0xFF, 0xFF, 0xFF),
                    MutedText: Color.FromArgb(140, 0xFF, 0xFF, 0xFF),
                    Crosshair: Color.FromArgb(110, 0xFF, 0xFF, 0xFF),
                    ReadoutBackground: Color.FromArgb(235, 0x2B, 0x2B, 0x2B))
                : new Palette(
                    PlotBackground: Color.FromArgb(255, 0xFC, 0xFC, 0xFC),
                    Grid: Color.FromArgb(30, 0x00, 0x00, 0x00),
                    Axis: Color.FromArgb(110, 0x00, 0x00, 0x00),
                    AxisText: Color.FromArgb(220, 0x00, 0x00, 0x00),
                    MutedText: Color.FromArgb(130, 0x00, 0x00, 0x00),
                    Crosshair: Color.FromArgb(90, 0x00, 0x00, 0x00),
                    ReadoutBackground: Color.FromArgb(235, 0xFF, 0xFF, 0xFF));
    }
}
