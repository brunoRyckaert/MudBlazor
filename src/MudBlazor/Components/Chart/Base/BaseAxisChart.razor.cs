// Copyright (c) MudBlazor 2021
// MudBlazor licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Numerics;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor.Interop;
using MudBlazor.Utilities;

namespace MudBlazor.Charts;

/// <summary>
/// Renders the SVG axes, grid lines, series paths, and tooltips shared by axis-based chart types.
/// </summary>
/// <typeparam name="T">The data type of the chart.</typeparam>
/// <typeparam name="TChartOptions">The type of chart options.</typeparam>
public partial class BaseAxisChart<T, TChartOptions> : MudComponentBase
    where T : struct, INumber<T>, IMinMaxValue<T>, IFormattable
    where TChartOptions : IAxisChartOptions, new()
{
    private const int AxisTitleOffset = 0;
    private static readonly DoubleEpsilonEqualityComparer _epsilonComparer = new(0.01);

    [Inject]
    private IJSRuntime JsRuntime { get; set; } = null!;

    private ElementReference _svgRef;
    private ElementReference? _xAxisGroupElementReference;
    private ElementReference? _yAxisGroupElementReference;
    private ElementSize? _lastYAxisLabelSize;
    private ElementSize? _lastXAxisLabelSize;
    private bool _touchToolTips;
    private MeasuredLabels? _measuredLabels;
    private bool _layoutShown;
    private LabelTexts? _showAfterRebuildWith;
    private MeasuredLabels? _rebuildPendingFor;

    // The labels as they were last measured, by count and by the first label of each axis, which is a new object each time the chart makes its labels.
    private sealed record MeasuredLabels(int HorizontalCount, SvgText? FirstHorizontal, int VerticalCount, SvgText? FirstVertical);

    // The text of the labels, which decides how big they are.
    private sealed record LabelTexts(string?[] Horizontal, string?[] Vertical)
    {
        public bool Matches(LabelTexts other) => Horizontal.SequenceEqual(other.Horizontal) && Vertical.SequenceEqual(other.Vertical);
    }

    private MeasuredLabels CurrentLabels => new(HorizontalValues.Count, HorizontalValues.FirstOrDefault(), VerticalValues.Count, VerticalValues.FirstOrDefault());

    private LabelTexts CurrentLabelTexts => new([.. HorizontalValues.Select(label => label.Value)], [.. VerticalValues.Select(label => label.Value)]);

    // A chart that fills its parent first lays out its labels for default sizes, so that layout is hidden until one built for their measured sizes replaces it.
    // It is laid out with labels only once the parent's size is known, which only an interactive render learns, so a static or prerendered chart is never hidden.
    // It stays hidden rather than removed, so the browser still lays out the labels and they can be measured.
    private bool HiddenUntilMeasured => MatchBoundsToSize && !_layoutShown && (HorizontalValues.Count > 0 || VerticalValues.Count > 0);

    /// <summary>
    /// The size of the Y-axis labels.
    /// </summary>
    [Parameter]
    [Category(CategoryTypes.Chart.Appearance)]
    public ElementSize? YAxisLabelSize { get; set; }

    /// <summary>
    /// The event callback for when the Y-axis label size changes.
    /// </summary>
    [Parameter]
    [Category(CategoryTypes.Chart.Behavior)]
    public EventCallback<ElementSize?> YAxisLabelSizeChanged { get; set; }

    /// <summary>
    /// The size of the X-axis labels.
    /// </summary>
    [Parameter]
    [Category(CategoryTypes.Chart.Appearance)]
    public ElementSize? XAxisLabelSize { get; set; }

    /// <summary>
    /// The event callback for when the X-axis label size changes.
    /// </summary>
    [Parameter]
    [Category(CategoryTypes.Chart.Behavior)]
    public EventCallback<ElementSize?> XAxisLabelSizeChanged { get; set; }

    /// <summary>
    /// The width of the chart.
    /// </summary>
    [Parameter]
    [EditorRequired]
    [Category(CategoryTypes.Chart.Appearance)]
    public string Width { get; set; }

    /// <summary>
    /// The height of the chart.
    /// </summary>
    [Parameter]
    [EditorRequired]
    [Category(CategoryTypes.Chart.Appearance)]
    public string Height { get; set; }

    /// <summary>
    /// The type of the chart.
    /// </summary>
    [Parameter]
    [EditorRequired]
    [Category(CategoryTypes.Chart.Appearance)]
    public ChartType? ChartType { get; set; }

    /// <summary>
    /// The CSS class of the chart.
    /// </summary>
    [Parameter]
    [EditorRequired]
    [Category(CategoryTypes.Chart.Appearance)]
    public string ChartClass { get; set; } = string.Empty;

    /// <summary>
    /// The series of data for the chart.
    /// </summary>
    [Parameter]
    [EditorRequired]
    [Category(CategoryTypes.Chart.Appearance)]
    public List<ChartSeries<T>> ChartSeries { get; set; } = [];

    /// <summary>
    /// The options for the chart.
    /// </summary>
    [Parameter]
    [EditorRequired]
    [Category(CategoryTypes.Chart.Appearance)]
    public TChartOptions ChartOptions { get; set; }

    /// <summary>
    /// The labels for the chart.
    /// </summary>
    [Parameter]
    [Category(CategoryTypes.Chart.Appearance)]
    public string[] ChartLabels { get; set; } = [];

    /// <summary>
    /// The width of the view box.
    /// </summary>
    [Parameter]
    [EditorRequired]
    [Category(CategoryTypes.Chart.Appearance)]
    public double ViewBoxWidth { get; set; }

    /// <summary>
    /// The height of the view box.
    /// </summary>
    [Parameter]
    [EditorRequired]
    [Category(CategoryTypes.Chart.Appearance)]
    public double ViewBoxHeight { get; set; }

    /// <summary>
    /// Make the chart fill the parent
    /// </summary>
    [Parameter]
    [Category(CategoryTypes.Chart.Appearance)]
    public bool MatchBoundsToSize { get; set; }

    /// <summary>
    /// The child content of the component.
    /// </summary>
    [Parameter]
    [Category(CategoryTypes.Chart.Appearance)]
    public RenderFragment? ChildContent { get; set; }

    /// <summary>
    /// The content for the series.
    /// </summary>
    [Parameter]
    [Category(CategoryTypes.Chart.Appearance)]
    public RenderFragment? SeriesContent { get; set; }

    /// <summary>
    /// The content for the tooltip.
    /// </summary>
    [Parameter]
    [Category(CategoryTypes.Chart.Appearance)]
    public RenderFragment? TooltipContent { get; set; }

    /// <summary>
    /// The custom graphics for the chart.
    /// </summary>
    [Parameter]
    [Category(CategoryTypes.Chart.Appearance)]
    public RenderFragment? CustomGraphics { get; set; }

    /// <summary>
    /// The currently hovered segment.
    /// </summary>
    [Parameter]
    [Category(CategoryTypes.Chart.Behavior)]
    public SvgPath? HoveredSegment { get; set; }

    /// <summary>
    /// The horizontal lines of the grid.
    /// </summary>
    [Parameter]
    [Category(CategoryTypes.Chart.Appearance)]
    public IList<SvgPath> HorizontalLines { get; set; } = [];

    /// <summary>
    /// The vertical lines of the grid.
    /// </summary>
    [Parameter]
    [Category(CategoryTypes.Chart.Appearance)]
    public IList<SvgPath> VerticalLines { get; set; } = [];

    /// <summary>
    /// The horizontal values of the grid.
    /// </summary>
    [Parameter]
    [Category(CategoryTypes.Chart.Appearance)]
    public IList<SvgText> HorizontalValues { get; set; } = [];

    /// <summary>
    /// The vertical values of the grid.
    /// </summary>
    [Parameter]
    [Category(CategoryTypes.Chart.Appearance)]
    public IList<SvgText> VerticalValues { get; set; } = [];

    /// <summary>
    /// The title of the X-axis.
    /// </summary>
    [Parameter]
    [Category(CategoryTypes.Chart.Appearance)]
    public string? XAxisTitle { get; set; }

    /// <summary>
    /// The title of the Y-axis.
    /// </summary>
    [Parameter]
    [Category(CategoryTypes.Chart.Appearance)]
    public string? YAxisTitle { get; set; }

    /// <summary>
    /// The event callback for when the axis changes.
    /// </summary>
    [Parameter]
    [Category(CategoryTypes.Chart.Behavior)]
    public EventCallback AxisChanged { get; set; }

    /// <summary>
    /// The event callback for when the element reference changes.
    /// </summary>
    [Parameter]
    [Category(CategoryTypes.Chart.Behavior)]
    public EventCallback<ElementReference> ElementRefChanged { get; set; }

    private string HoveredStylename =>
        new StyleBuilder()
            .AddStyle("overflow", "visible", HoveredSegment is not null)
            .AddStyle("visibility", "hidden", HiddenUntilMeasured)
            .Build();

    private string TooltipStylename =>
        new StyleBuilder()
            .AddStyle("display", "block", HoveredSegment is not null)
            .AddStyle("display", "none", HoveredSegment is null)
            .Build();

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        base.OnParametersSet();

        // The chart makes new labels each time it rebuilds, and every rebuild after a measurement uses the measured sizes.
        // A rebuild for new data lays out its new text for the sizes of the old, so the chart stays hidden until that text is measured too.
        if (_rebuildPendingFor is not null && CurrentLabels != _rebuildPendingFor)
        {
            _rebuildPendingFor = null;
            _layoutShown |= _showAfterRebuildWith?.Matches(CurrentLabelTexts) == true;
            _showAfterRebuildWith = null;
        }
    }

    /// <summary>
    /// Called after the component has been rendered.
    /// </summary>
    /// <param name="firstRender">True if this is the first time the component is rendering; otherwise, false.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await base.OnAfterRenderAsync(firstRender);

        if (firstRender)
        {
            await ElementRefChanged.InvokeAsync(_svgRef);
        }

        await UpdateTouchToolTipsAsync();

        // A chart makes new labels whenever it rebuilds, which a change of data, options, size or a parent render all do.
        // Hovering renders the chart again with the same labels, so measuring them again would only cost two calls into the browser.
        var labels = CurrentLabels;
        if (!firstRender && labels == _measuredLabels)
        {
            return;
        }

        _measuredLabels = labels;
        var texts = HiddenUntilMeasured ? CurrentLabelTexts : null;

        var yAxisLabelSize = _yAxisGroupElementReference != null ? (await JsRuntime.InvokeAsyncWithErrorHandling<ElementSize>("mudGetSvgBBox", _yAxisGroupElementReference)).value : null;
        var xAxisLabelSize = _xAxisGroupElementReference != null ? (await JsRuntime.InvokeAsyncWithErrorHandling<ElementSize>("mudGetSvgBBox", _xAxisGroupElementReference)).value : null;

        // A chart inside a hidden container measures its labels as an empty box, and CSS can show it later without rendering it again.
        // Forgetting those labels lets the next render, such as the first hover, measure them once the chart can be seen.
        if (MeasuredWhileHidden(yAxisLabelSize, HorizontalValues) || MeasuredWhileHidden(xAxisLabelSize, VerticalValues))
        {
            _measuredLabels = null;
        }

        var axisChanged = false;

        if (yAxisLabelSize != null)
        {
            var baseline = YAxisLabelSize ?? _lastYAxisLabelSize;
            if (baseline == null || !_epsilonComparer.Equals(yAxisLabelSize.Width, baseline.Width))
            {
                _lastYAxisLabelSize = yAxisLabelSize;
                if (YAxisLabelSizeChanged.HasDelegate)
                {
                    await YAxisLabelSizeChanged.InvokeAsync(yAxisLabelSize);
                }

                axisChanged = true;
            }
        }

        if (xAxisLabelSize != null)
        {
            var baseline = XAxisLabelSize ?? _lastXAxisLabelSize;
            if (baseline == null || !_epsilonComparer.Equals(xAxisLabelSize.Height, baseline.Height))
            {
                _lastXAxisLabelSize = xAxisLabelSize;
                if (XAxisLabelSizeChanged.HasDelegate)
                {
                    await XAxisLabelSizeChanged.InvokeAsync(xAxisLabelSize);
                }

                axisChanged = true;
            }
        }

        var rebuilding = axisChanged && AxisChanged.HasDelegate;

        // Only a measurement of the labels on screen decides, since a render that made newer labels measures them itself.
        if (HiddenUntilMeasured && labels == CurrentLabels)
        {
            // Measurements of the same labels can overlap, and once one has asked for a rebuild, the others find nothing changed.
            if (rebuilding || _rebuildPendingFor == labels)
            {
                _showAfterRebuildWith = texts;
            }
            else
            {
                // With no rebuild asked for, as after a failed measurement, no better layout is coming, and a hidden chart gets no hover to measure again.
                _layoutShown = true;
                StateHasChanged();
            }
        }

        if (rebuilding)
        {
            _rebuildPendingFor = CurrentLabels;
            await AxisChanged.InvokeAsync();
        }
    }

    // Labels with text only measure as an empty box when the browser has not laid them out.
    // A chart with no label text is left alone, so it keeps the saving on every hover.
    private static bool MeasuredWhileHidden(ElementSize? size, IList<SvgText> labels) =>
        size is { Width: 0d, Height: 0d } && labels.Any(label => !string.IsNullOrEmpty(label.Value));

    // A touch listener can only stop the page scrolling if it was on the chart before the finger landed, so the chart attaches it as soon as the option is on rather than on the first touch.
    private async Task UpdateTouchToolTipsAsync()
    {
        var touchToolTips = ChartOptions is IAxisLineChartOptions { ShowToolTips: true, ShowToolTipsOnTouch: true };
        if (touchToolTips == _touchToolTips)
        {
            return;
        }

        _touchToolTips = touchToolTips;

        if (touchToolTips)
        {
            // Line and time series points are read left to right, so a finger picks the nearest point along x; scatter points have no such order.
            await JsRuntime.InvokeVoidAsyncWithErrorHandling("mudChartTouch.attach", _svgRef, ChartOptions is not ScatterPlotChartOptions);
        }
        else
        {
            await JsRuntime.InvokeVoidAsyncWithErrorHandling("mudChartTouch.detach", _svgRef);
        }
    }
}
