using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace VrcCompanion.Controls;

/// <summary>
/// Opt-in text selection for the whole app. When <see cref="IsEnabled"/> is on,
/// dragging across any ordinary TextBlock paints a highlight and Ctrl+C (or the
/// right-click menu) copies the characters under the selection. Rows, buttons,
/// links and text fields are deliberately left alone so clicking them keeps
/// doing what it always did.
/// </summary>
public static class TextSelection
{
    private static readonly Brush SelectionBrush = CreateBrush(Color.FromArgb(0x73, 0xA7, 0x8B, 0xFA));
    private static readonly HashSet<Window> HookedWindows = new();

    private static bool _enabled;
    private static bool _handlersRegistered;
    private static Selection? _selection;
    private static TextBlock? _dragBlock;
    private static int _anchor;

    public static bool IsEnabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }

            _enabled = value;
            if (value)
            {
                EnsureHandlers();
            }
            else
            {
                ClearSelection();
            }
        }
    }

    /// <summary>The text the user currently has highlighted, if any.</summary>
    public static string? SelectedText => _selection?.SelectedText;

    public static void ClearSelection()
    {
        _dragBlock = null;
        if (_selection is null)
        {
            return;
        }

        _selection.Release();
        _selection = null;
    }

    /// <summary>Highlights a character range; used by the right-click menu and tests.</summary>
    internal static string SelectRange(TextBlock block, int start, int end)
    {
        var text = GetText(block);
        var layout = new TextLayoutMirror(block, text);
        ShowSelection(block, layout, start, end);
        return _selection?.SelectedText ?? string.Empty;
    }

    internal static string GetText(TextBlock block)
    {
        if (block.Inlines.Count == 0)
        {
            return block.Text ?? string.Empty;
        }

        var builder = new StringBuilder();
        AppendInlines(block.Inlines, builder);
        return builder.ToString();
    }

    private static void AppendInlines(InlineCollection inlines, StringBuilder builder)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run:
                    builder.Append(run.Text);
                    break;
                case LineBreak:
                    builder.Append('\n');
                    break;
                case Span span:
                    AppendInlines(span.Inlines, builder);
                    break;
            }
        }
    }

    private static void EnsureHandlers()
    {
        if (_handlersRegistered)
        {
            return;
        }

        _handlersRegistered = true;
        EventManager.RegisterClassHandler(
            typeof(TextBlock),
            UIElement.MouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnMouseLeftButtonDown));
        EventManager.RegisterClassHandler(
            typeof(TextBlock),
            UIElement.MouseMoveEvent,
            new MouseEventHandler(OnMouseMove));
        EventManager.RegisterClassHandler(
            typeof(TextBlock),
            UIElement.MouseLeftButtonUpEvent,
            new MouseButtonEventHandler(OnMouseLeftButtonUp));
        EventManager.RegisterClassHandler(
            typeof(TextBlock),
            FrameworkElement.ContextMenuOpeningEvent,
            new ContextMenuEventHandler(OnContextMenuOpening));
    }

    private static void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_enabled || sender is not TextBlock block || _dragBlock is not null)
        {
            return;
        }

        if (e.ChangedButton != MouseButton.Left || !CanSelect(block))
        {
            return;
        }

        var text = GetText(block);
        if (text.Length == 0)
        {
            return;
        }

        HookWindow(block);
        var layout = new TextLayoutMirror(block, text);
        var caret = layout.CaretIndexAt(e.GetPosition(block));

        if (e.ClickCount >= 2)
        {
            var word = layout.WordAt(caret);
            ShowSelection(block, layout, word.Start, word.End);
            e.Handled = true;
            return;
        }

        _anchor = caret;
        _dragBlock = block;
        ShowSelection(block, layout, caret, caret);
        block.CaptureMouse();
        e.Handled = true;
    }

    private static void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_enabled ||
            _dragBlock is null ||
            !ReferenceEquals(sender, _dragBlock) ||
            e.LeftButton != MouseButtonState.Pressed ||
            _selection is null)
        {
            return;
        }

        var caret = _selection.Layout.CaretIndexAt(e.GetPosition(_dragBlock));
        ShowSelection(_dragBlock, _selection.Layout, Math.Min(_anchor, caret), Math.Max(_anchor, caret));
        e.Handled = true;
    }

    private static void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragBlock is null || !ReferenceEquals(sender, _dragBlock))
        {
            return;
        }

        _dragBlock.ReleaseMouseCapture();
        _dragBlock = null;
        e.Handled = true;
    }

    private static void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (!_enabled || sender is not TextBlock block || !CanSelect(block))
        {
            return;
        }

        var text = GetText(block);
        if (text.Length == 0)
        {
            return;
        }

        HookWindow(block);
        var menu = new ContextMenu { PlacementTarget = block };

        var copy = new MenuItem { Header = "Copy text" };
        copy.Click += (_, _) =>
        {
            var selected = ReferenceEquals(_selection?.Block, block) ? _selection.SelectedText : null;
            CopyToClipboard(string.IsNullOrEmpty(selected) ? text : selected);
        };
        menu.Items.Add(copy);

        var selectAll = new MenuItem { Header = "Select all" };
        selectAll.Click += (_, _) => ShowSelection(block, new TextLayoutMirror(block, text), 0, text.Length);
        menu.Items.Add(selectAll);

        menu.IsOpen = true;
        e.Handled = true;
    }

    private static void HookWindow(TextBlock block)
    {
        var window = Window.GetWindow(block);
        if (window is null || !HookedWindows.Add(window))
        {
            return;
        }

        window.PreviewKeyDown += OnWindowPreviewKeyDown;
        window.Closed += (_, _) =>
        {
            HookedWindows.Remove(window);
            ClearSelection();
        };
    }

    private static void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_enabled)
        {
            return;
        }

        if (e.Key == Key.Escape && _selection is not null)
        {
            ClearSelection();
            return;
        }

        if (e.Key != Key.C || (Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }

        // Text fields already handle their own copy.
        if (Keyboard.FocusedElement is TextBoxBase or PasswordBox)
        {
            return;
        }

        var selected = _selection?.SelectedText;
        if (string.IsNullOrEmpty(selected))
        {
            return;
        }

        CopyToClipboard(selected);
        e.Handled = true;
    }

    private static void CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            // Another process can hold the clipboard open; never crash over it.
        }
    }

    private static void ShowSelection(TextBlock block, TextLayoutMirror layout, int start, int end)
    {
        start = Math.Clamp(start, 0, layout.Text.Length);
        end = Math.Clamp(end, 0, layout.Text.Length);
        if (end < start)
        {
            (start, end) = (end, start);
        }

        if (_selection is null || !ReferenceEquals(_selection.Block, block))
        {
            _selection?.Release();
            _selection = new Selection(block, layout);
        }
        else
        {
            _selection.Layout = layout;
        }

        _selection.SetRange(start, end);
    }

    private static bool CanSelect(TextBlock block)
    {
        if (!block.IsVisible || block.ActualWidth <= 0 || block.ActualHeight <= 0)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(GetText(block)))
        {
            return false;
        }

        if (HasInteractiveAncestor(block) || ContainsHyperlink(block.Inlines))
        {
            return false;
        }

        return true;
    }

    private static bool HasInteractiveAncestor(DependencyObject element)
    {
        for (var parent = VisualTreeHelper.GetParent(element);
             parent is not null;
             parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is Window)
            {
                return false;
            }

            if (parent is ButtonBase or TextBoxBase or PasswordBox or ListBoxItem or ComboBoxItem or
                TreeViewItem or ListViewItem or DataGridCell or MenuItem or TabItem or ScrollBar or Thumb)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsHyperlink(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is Hyperlink)
            {
                return true;
            }

            if (inline is Span span && ContainsHyperlink(span.Inlines))
            {
                return true;
            }
        }

        return false;
    }

    private static Brush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private sealed class Selection
    {
        private AdornerLayer? _layer;
        private SelectionAdorner? _adorner;
        private ScrollViewer? _scrollViewer;

        public Selection(TextBlock block, TextLayoutMirror layout)
        {
            Block = block;
            Layout = layout;
            block.Unloaded += OnBlockUnloaded;
            _scrollViewer = FindAncestorScrollViewer(block);
            if (_scrollViewer is not null)
            {
                _scrollViewer.ScrollChanged += OnScrollChanged;
            }
        }

        public TextBlock Block { get; }

        public TextLayoutMirror Layout { get; set; }

        public string SelectedText => Layout.Text.Substring(Start, Math.Max(0, End - Start));

        public int Start { get; private set; }

        public int End { get; private set; }

        public void SetRange(int start, int end)
        {
            Start = start;
            End = end;

            var rects = Layout.Highlight(start, end);
            if (rects.Count == 0)
            {
                RemoveAdorner();
                return;
            }

            var offset = new Vector(Block.Padding.Left, Block.Padding.Top);
            var shifted = new List<Rect>(rects.Count);
            foreach (var rect in rects)
            {
                shifted.Add(new Rect(rect.X + offset.X, rect.Y + offset.Y, rect.Width, rect.Height));
            }

            if (_adorner is null || _layer is null)
            {
                RemoveAdorner();
                _layer = AdornerLayer.GetAdornerLayer(Block);
                if (_layer is null)
                {
                    return;
                }

                _adorner = new SelectionAdorner(Block);
                _layer.Add(_adorner);
            }

            _adorner.Rects = shifted;
            _adorner.InvalidateVisual();
        }

        public void RemoveAdorner()
        {
            if (_adorner is not null && _layer is not null)
            {
                _layer.Remove(_adorner);
            }

            _adorner = null;
            _layer = null;
        }

        public void Release()
        {
            Block.Unloaded -= OnBlockUnloaded;
            if (_scrollViewer is not null)
            {
                _scrollViewer.ScrollChanged -= OnScrollChanged;
                _scrollViewer = null;
            }

            RemoveAdorner();
        }

        private void OnBlockUnloaded(object sender, RoutedEventArgs e)
        {
            if (ReferenceEquals(_selection, this))
            {
                ClearSelection();
            }
            else
            {
                Release();
            }
        }

        // The highlight is painted by an adorner above the scroll content, so it
        // cannot follow the text when the list or page scrolls.
        private void OnScrollChanged(object sender, ScrollChangedEventArgs e) => ClearSelection();

        private static ScrollViewer? FindAncestorScrollViewer(DependencyObject element)
        {
            for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is ScrollViewer scrollViewer)
                {
                    return scrollViewer;
                }
            }

            return null;
        }
    }

    private sealed class SelectionAdorner : Adorner
    {
        public SelectionAdorner(UIElement adornedElement)
            : base(adornedElement)
        {
            IsHitTestVisible = false;
        }

        public IReadOnlyList<Rect> Rects { get; set; } = Array.Empty<Rect>();

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            drawingContext.PushClip(new RectangleGeometry(new Rect(AdornedElement.RenderSize)));
            foreach (var rect in Rects)
            {
                drawingContext.DrawRoundedRectangle(SelectionBrush, null, rect, 2, 2);
            }

            drawingContext.Pop();
        }
    }
}

/// <summary>
/// Mirrors a TextBlock's text with its own measurements so a mouse position can
/// be turned into a character index and a character range into highlight bars.
/// </summary>
internal sealed class TextLayoutMirror
{
    private readonly Rect[] _bounds;

    public TextLayoutMirror(TextBlock block, string text)
    {
        Text = text;
        _bounds = new Rect[text.Length];
        if (text.Length == 0)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(block);
        var typeface = new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch);
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            block.FlowDirection,
            typeface,
            block.FontSize,
            block.Foreground,
            dpi.PixelsPerDip)
        {
            TextAlignment = block.TextAlignment
        };

        if (block.TextWrapping != TextWrapping.NoWrap)
        {
            var width = block.ActualWidth - block.Padding.Left - block.Padding.Right;
            formatted.MaxTextWidth = Math.Max(1, width);
        }

        if (!double.IsNaN(block.LineHeight) && block.LineHeight > 0)
        {
            formatted.LineHeight = block.LineHeight;
        }

        for (var index = 0; index < text.Length; index++)
        {
            var geometry = formatted.BuildHighlightGeometry(new Point(0, 0), index, 1);
            var bounds = geometry?.Bounds ?? Rect.Empty;
            if (!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0)
            {
                _bounds[index] = bounds;
            }
        }

        // Whitespace has no painted geometry. Give it a caret-sized band so a
        // drag across a space still lands on the right characters.
        for (var index = 0; index < text.Length; index++)
        {
            if (!_bounds[index].IsEmpty)
            {
                continue;
            }

            if (index > 0 && !_bounds[index - 1].IsEmpty)
            {
                var previous = _bounds[index - 1];
                _bounds[index] = new Rect(previous.Right, previous.Top, 0.01, previous.Height);
            }
            else
            {
                _bounds[index] = new Rect(0, 0, 0.01, Math.Max(1, formatted.Height));
            }
        }
    }

    public string Text { get; }

    /// <summary>The caret position nearest a point in the TextBlock's coordinates.</summary>
    public int CaretIndexAt(Point point)
    {
        if (Text.Length == 0)
        {
            return 0;
        }

        var bestLineDistance = double.MaxValue;
        var candidates = new List<int>();
        for (var index = 0; index < _bounds.Length; index++)
        {
            var rect = _bounds[index];
            if (rect.IsEmpty)
            {
                continue;
            }

            var distance = Math.Abs(rect.Top + (rect.Height / 2) - point.Y);
            if (distance < bestLineDistance - 0.75)
            {
                bestLineDistance = distance;
                candidates.Clear();
                candidates.Add(index);
            }
            else if (distance <= bestLineDistance + 0.75)
            {
                candidates.Add(index);
            }
        }

        if (candidates.Count == 0)
        {
            return 0;
        }

        var best = candidates[0];
        var bestHorizontal = double.MaxValue;
        foreach (var index in candidates)
        {
            var rect = _bounds[index];
            var distance = point.X < rect.Left
                ? rect.Left - point.X
                : point.X > rect.Right
                    ? point.X - rect.Right
                    : 0;
            if (distance < bestHorizontal)
            {
                bestHorizontal = distance;
                best = index;
            }
        }

        var chosen = _bounds[best];
        return point.X <= chosen.Left + (chosen.Width / 2) ? best : best + 1;
    }

    public (int Start, int End) WordAt(int caret)
    {
        if (Text.Length == 0)
        {
            return (0, 0);
        }

        var index = Math.Clamp(caret, 0, Text.Length - 1);
        if (!IsWordCharacter(Text[index]))
        {
            var forward = index;
            while (forward < Text.Length && !IsWordCharacter(Text[forward]))
            {
                forward++;
            }

            if (forward >= Text.Length)
            {
                return (index, index);
            }

            index = forward;
        }

        var start = index;
        while (start > 0 && IsWordCharacter(Text[start - 1]))
        {
            start--;
        }

        var end = index + 1;
        while (end < Text.Length && IsWordCharacter(Text[end]))
        {
            end++;
        }

        return (start, end);
    }

    public IReadOnlyList<Rect> Highlight(int start, int end)
    {
        start = Math.Clamp(start, 0, Text.Length);
        end = Math.Clamp(end, 0, Text.Length);
        if (end <= start)
        {
            return Array.Empty<Rect>();
        }

        var result = new List<Rect>();
        Rect? current = null;
        for (var index = start; index < end; index++)
        {
            var rect = _bounds[index];
            if (rect.IsEmpty)
            {
                continue;
            }

            if (rect.Width <= 0.02)
            {
                // A space only occupies a caret-width band; the neighbouring
                // glyphs already cover the visible highlight.
                if (current is { } pending)
                {
                    result.Add(pending);
                    current = null;
                }

                continue;
            }

            if (current is { } band && Math.Abs(band.Top - rect.Top) <= 1)
            {
                var left = Math.Min(band.Left, rect.Left);
                var right = Math.Max(band.Right, rect.Right);
                current = new Rect(left, band.Top, right - left, Math.Max(band.Height, rect.Height));
            }
            else
            {
                if (current is { } previous)
                {
                    result.Add(previous);
                }

                current = rect;
            }
        }

        if (current is { } last)
        {
            result.Add(last);
        }

        return result;
    }

    private static bool IsWordCharacter(char value) => !char.IsWhiteSpace(value);
}
