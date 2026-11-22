using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace VrcCompanion.Controls;

/// <summary>
/// Wraps items into rows and realises only the rows a scroll could reach.
/// A plain <see cref="WrapPanel"/> inside the page's ScrollViewer is measured
/// with an infinite height, so it builds every card it is given and keeps it:
/// each card carries a decoded thumbnail, which added up to hundreds of
/// megabytes on large accounts. This panel reports the height of the whole
/// list so the page scrollbar stays honest, but it only keeps the cards in the
/// visible band (plus a little cache above and below) in the visual tree.
/// Containers that leave the band are recycled through the item generator, so
/// scrolling builds one card per newly revealed slot rather than one per item.
/// </summary>
public sealed class VirtualizingWrapPanel : VirtualizingPanel
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth),
        typeof(double),
        typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(236d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty EstimatedItemHeightProperty = DependencyProperty.Register(
        nameof(EstimatedItemHeight),
        typeof(double),
        typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(320d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>How many viewport heights of cards to keep ready on each side.</summary>
    public static readonly DependencyProperty CacheViewportsProperty = DependencyProperty.Register(
        nameof(CacheViewports),
        typeof(double),
        typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(0.75d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    // Used only when the panel has no ScrollViewer to ask (a design surface, or
    // before the first layout pass).
    private const double ViewportFallback = 720;

    // Measured height of every row that has been on screen. Rows that have not
    // been measured yet borrow the tallest row seen so far, so the scrollbar
    // only shifts by the small difference between cards with and without tags.
    private readonly Dictionary<int, double> _rowHeights = new();
    // Which item each realised card draws. The generator stops mapping a
    // recycled container back to an item, so the panel keeps its own answer:
    // without it a recycled card would never be arranged.
    private readonly Dictionary<UIElement, int> _cardItems = new();
    private ScrollViewer? _scrollViewer;
    private int _columns = 1;
    private double _tallestRow;
    private double _extent;
    private double _measuredBandStart;
    private double _measuredBandEnd;

    public VirtualizingWrapPanel()
    {
        Loaded += (_, _) => HookScrollViewer();
        Unloaded += (_, _) => UnhookScrollViewer();
    }

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double EstimatedItemHeight
    {
        get => (double)GetValue(EstimatedItemHeightProperty);
        set => SetValue(EstimatedItemHeightProperty, value);
    }

    public double CacheViewports
    {
        get => (double)GetValue(CacheViewportsProperty);
        set => SetValue(CacheViewportsProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var itemsControl = ItemsControl.GetItemsOwner(this);
        var itemCount = itemsControl?.Items.Count ?? 0;

        var width = availableSize.Width;
        if (double.IsInfinity(width) || width <= 0)
        {
            width = ActualWidth > 0 ? ActualWidth : ItemWidth;
        }

        var itemWidth = Math.Max(1, ItemWidth);
        _columns = Math.Max(1, (int)Math.Floor(width / itemWidth));

        if (itemCount == 0)
        {
            if (InternalChildren.Count > 0)
            {
                RemoveInternalChildRange(0, InternalChildren.Count);
            }

            _extent = 0;
            return new Size(width, 0);
        }

        var rowCount = (itemCount + _columns - 1) / _columns;

        var (bandStart, bandEnd) = VisibleBand();
        _measuredBandStart = bandStart;
        _measuredBandEnd = bandEnd;

        var cache = Math.Max(0, CacheViewports) * Math.Max(1, bandEnd - bandStart);
        var firstRow = RowAt(bandStart - cache, rowCount);
        var lastRow = Math.Max(firstRow, RowAt(bandEnd + cache, rowCount));

        var firstIndex = firstRow * _columns;
        var lastIndex = Math.Min(itemCount - 1, ((lastRow + 1) * _columns) - 1);

        // Release the cards that left the band before building the ones that
        // entered it, so a long scroll never pays for both bands at once.
        CleanUpItems(firstIndex, lastIndex);
        RealizeItems(firstIndex, lastIndex);

        // A card the generator no longer maps to an item cannot be arranged or
        // filled, so it must not stay in the panel pretending to be a card.
        for (var childIndex = InternalChildren.Count - 1; childIndex >= 0; childIndex--)
        {
            if (!_cardItems.ContainsKey(InternalChildren[childIndex]))
            {
                RemoveInternalChildRange(childIndex, 1);
            }
        }

        var extent = 0d;
        for (var row = 0; row < rowCount; row++)
        {
            extent += RowHeight(row);
        }

        _extent = extent;
        return new Size(width, extent);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var itemWidth = Math.Max(1, ItemWidth);

        foreach (UIElement child in InternalChildren)
        {
            if (!_cardItems.TryGetValue(child, out var index))
            {
                continue;
            }

            var row = index / _columns;
            var column = index % _columns;

            // Every card in a row gets the row's height so the cards line up
            // even though favicons, tags and the WEARING chip vary per avatar.
            child.Arrange(new Rect(column * itemWidth, RowTop(row), itemWidth, RowHeight(row)));
        }

        return new Size(finalSize.Width, _extent);
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
            case NotifyCollectionChangedAction.Move:
                RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;

            case NotifyCollectionChangedAction.Reset:
                // A filter, sort or refresh publishes a whole new collection, so
                // every card and every measured row belongs to the old list.
                RemoveInternalChildRange(0, InternalChildren.Count);
                _cardItems.Clear();
                _rowHeights.Clear();
                _tallestRow = 0;
                break;
        }

        InvalidateMeasure();
    }

    private void RealizeItems(int firstIndex, int lastIndex)
    {
        var generator = ItemContainerGenerator;
        var startPosition = generator.GeneratorPositionFromIndex(firstIndex);
        var childIndex = startPosition.Offset == 0 ? startPosition.Index : startPosition.Index + 1;
        var itemWidth = Math.Max(1, ItemWidth);

        using (generator.StartAt(startPosition, GeneratorDirection.Forward, true))
        {
            for (var index = firstIndex; index <= lastIndex; index++, childIndex++)
            {
                if (generator.GenerateNext(out var isNew) is not UIElement child)
                {
                    break;
                }

                if (isNew || VisualTreeHelper.GetParent(child) != this)
                {
                    if (childIndex >= InternalChildren.Count)
                    {
                        AddInternalChild(child);
                    }
                    else
                    {
                        InsertInternalChild(childIndex, child);
                    }
                }

                // A recycled card has to be re-prepared as well or it keeps the
                // item it showed last time.
                generator.PrepareItemContainer(child);
                _cardItems[child] = index;
                child.Measure(new Size(itemWidth, double.PositiveInfinity));
                RememberRowHeight(index / _columns, child.DesiredSize.Height);
            }
        }
    }

    private void CleanUpItems(int minIndex, int maxIndex)
    {
        var generator = ItemContainerGenerator;

        for (var childIndex = InternalChildren.Count - 1; childIndex >= 0; childIndex--)
        {
            // A realised container is addressed by its position in the panel's
            // child list, which is also the generator's realised order. Asking
            // for a position by item index instead hands back the wrong card.
            var child = InternalChildren[childIndex];
            var position = new GeneratorPosition(childIndex, 0);
            if (_cardItems.TryGetValue(child, out var itemIndex) &&
                itemIndex >= minIndex &&
                itemIndex <= maxIndex)
            {
                continue;
            }

            _cardItems.Remove(child);

            if (generator is IRecyclingItemContainerGenerator recycling)
            {
                // The card is kept for the next row that needs one, which is
                // what stops a fast scroll from building a new visual for every
                // avatar it passes. Recycle usually detaches the child itself;
                // removing it again in that case would take a neighbour with
                // it and leave the grid blank.
                recycling.Recycle(position, 1);
                if (VisualTreeHelper.GetParent(child) == this)
                {
                    RemoveInternalChildRange(childIndex, 1);
                }

                continue;
            }

            generator.Remove(position, 1);
            RemoveInternalChildRange(childIndex, 1);
        }
    }

    private void RememberRowHeight(int row, double height)
    {
        if (height <= 0 || double.IsInfinity(height))
        {
            return;
        }

        if (!_rowHeights.TryGetValue(row, out var known) || known + 0.5 < height)
        {
            _rowHeights[row] = height;
        }

        if (height > _tallestRow)
        {
            _tallestRow = height;
        }
    }

    private double RowHeight(int row)
        => _rowHeights.TryGetValue(row, out var height)
            ? height
            : UnmeasuredRowHeight();

    /// <summary>
    /// Height to assume for a row that has never been on screen. Once anything
    /// has been measured the average of those rows is the best guess: borrowing
    /// the tallest row made every unmeasured row too tall, so the extent fell as
    /// ordinary rows replaced it and the scrollbar visibly shrank mid-scroll.
    /// The estimated height is only used until a row has been measured.
    /// </summary>
    private double UnmeasuredRowHeight()
    {
        if (_rowHeights.Count == 0)
        {
            return Math.Max(EstimatedItemHeight, _tallestRow);
        }

        var total = 0d;
        foreach (var height in _rowHeights.Values)
        {
            total += height;
        }

        return total / _rowHeights.Count;
    }

    private double RowTop(int row)
    {
        var top = 0d;
        for (var index = 0; index < row; index++)
        {
            top += RowHeight(index);
        }

        return top;
    }

    private int RowAt(double y, int rowCount)
    {
        var top = 0d;
        for (var row = 0; row < rowCount; row++)
        {
            var height = RowHeight(row);
            if (y < top + height)
            {
                return row;
            }

            top += height;
        }

        return Math.Max(0, rowCount - 1);
    }

    /// <summary>The part of this panel currently on screen, in panel coordinates.</summary>
    private (double Start, double End) VisibleBand()
    {
        var scroll = _scrollViewer ?? HookScrollViewer();
        if (scroll is null || scroll.ViewportHeight <= 0)
        {
            return (0, ViewportFallback);
        }

        double topInViewport;
        try
        {
            topInViewport = TransformToAncestor(scroll).Transform(new Point(0, 0)).Y;
        }
        catch (InvalidOperationException)
        {
            return (0, scroll.ViewportHeight);
        }

        var start = -topInViewport;
        return (start, start + scroll.ViewportHeight);
    }

    private ScrollViewer? HookScrollViewer()
    {
        if (_scrollViewer is not null)
        {
            return _scrollViewer;
        }

        DependencyObject? current = this;
        while (current is not null)
        {
            current = VisualTreeHelper.GetParent(current);
            if (current is ScrollViewer scroll)
            {
                _scrollViewer = scroll;
                scroll.ScrollChanged += OnScrollChanged;
                return scroll;
            }
        }

        return null;
    }

    private void UnhookScrollViewer()
    {
        if (_scrollViewer is null)
        {
            return;
        }

        _scrollViewer.ScrollChanged -= OnScrollChanged;
        _scrollViewer = null;
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 && e.ViewportHeightChange == 0 && e.ExtentHeightChange == 0)
        {
            return;
        }

        // A different part of the list is on screen, so the band of cards that
        // should be realised has moved.
        var (start, end) = VisibleBand();
        if (Math.Abs(start - _measuredBandStart) < 0.5 && Math.Abs(end - _measuredBandEnd) < 0.5)
        {
            return;
        }

        InvalidateMeasure();
    }
}
