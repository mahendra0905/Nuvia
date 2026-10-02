using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Nuvia.App.Controls;

/// <summary>
/// Adds Explorer-style mouse behaviour to a <see cref="ListView"/>:
/// <list type="bullet">
/// <item>Dragging from empty space draws a rubber-band rectangle and selects the items it covers.</item>
/// <item>Dragging from an item raises <see cref="_onItemDragStart"/> (used to start a files drag-out),
/// while preserving a multi-selection so several selected items can be dragged together.</item>
/// </list>
/// It is a pure input behaviour: it only reads containers and toggles their <c>IsSelected</c>. It never
/// touches Telegram, the index or files. Rubber-band selection covers the item containers that are
/// currently realised (the grid view is non-virtualising; the details view virtualises, so off-screen
/// rows are not marquee-selected — Ctrl/Shift-click still works for those).
/// </summary>
internal sealed class MarqueeSelector
{
    private enum Mode { None, PendingMarquee, Marquee, PendingItemDrag, Dragging }

    private readonly ListView _list;
    private readonly Action<ListView> _onItemDragStart;

    private Mode _mode;
    private Point _origin;
    private ListViewItem? _downItem;
    private bool _preserveSelection;
    private MarqueeAdorner? _adorner;
    private AdornerLayer? _layer;
    private readonly HashSet<object> _baseSelection = new();

    private MarqueeSelector(ListView list, Action<ListView> onItemDragStart)
    {
        _list = list;
        _onItemDragStart = onItemDragStart;

        _list.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        _list.PreviewMouseMove += OnPreviewMouseMove;
        _list.PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
        _list.LostMouseCapture += (_, _) => EndMarquee();
    }

    /// <summary>Attaches the behaviour to a list. Call once, e.g. from the window's Loaded handler.</summary>
    public static MarqueeSelector Attach(ListView list, Action<ListView> onItemDragStart) =>
        new(list, onItemDragStart);

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Never hijack the scrollbar or a details-view column header.
        if (IsWithin<System.Windows.Controls.Primitives.ScrollBar>(e.OriginalSource as DependencyObject)
            || IsWithin<GridViewColumnHeader>(e.OriginalSource as DependencyObject))
            return;

        _origin = e.GetPosition(_list);
        _downItem = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject);

        var modifier = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;

        if (_downItem is not null)
        {
            _mode = Mode.PendingItemDrag;

            // With no modifier, clicking one of several already-selected items would normally collapse the
            // selection to just that item on mouse-down — which would break dragging the whole group. Defer
            // that: keep the multi-selection now (in case this becomes a drag) and only collapse on mouse-up
            // if no drag happened.
            _preserveSelection = !modifier
                && _downItem.IsSelected
                && _list.SelectedItems.Count > 1;
            if (_preserveSelection)
                e.Handled = true;
        }
        else
        {
            // Empty space: begin a possible rubber-band. A plain click (no modifier) clears the selection,
            // matching Explorer; Ctrl/Shift keeps the current selection to extend it.
            _mode = Mode.PendingMarquee;
            _baseSelection.Clear();
            if (modifier)
            {
                foreach (var item in _list.SelectedItems)
                    _baseSelection.Add(item);
            }
            else
            {
                _list.SelectedItems.Clear();
            }
        }
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        var pos = e.GetPosition(_list);

        switch (_mode)
        {
            case Mode.PendingItemDrag:
                if (PassedThreshold(pos))
                {
                    _mode = Mode.Dragging;
                    // Blocking: runs the OLE drag loop until the user drops or cancels.
                    try { _onItemDragStart(_list); }
                    finally { _mode = Mode.None; }
                }
                break;

            case Mode.PendingMarquee:
                if (PassedThreshold(pos))
                    BeginMarquee();
                goto case Mode.Marquee;

            case Mode.Marquee:
                if (_mode == Mode.Marquee)
                {
                    var rect = new Rect(_origin, pos);
                    _adorner?.SetRect(rect);
                    ApplyMarquee(rect);
                    e.Handled = true;
                }
                break;
        }
    }

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        switch (_mode)
        {
            case Mode.Marquee:
                EndMarquee();
                e.Handled = true;
                break;

            case Mode.PendingItemDrag when _preserveSelection && _downItem is not null:
                // A click (not a drag) on one of several selected items: now collapse to just that item.
                _list.SelectedItems.Clear();
                _downItem.IsSelected = true;
                break;
        }

        _mode = Mode.None;
        _preserveSelection = false;
        _downItem = null;
    }

    private void BeginMarquee()
    {
        _mode = Mode.Marquee;
        _layer = AdornerLayer.GetAdornerLayer(_list);
        if (_layer is not null)
        {
            _adorner = new MarqueeAdorner(_list);
            _layer.Add(_adorner);
        }

        _list.CaptureMouse();
    }

    private void EndMarquee()
    {
        if (_adorner is not null && _layer is not null)
        {
            _layer.Remove(_adorner);
            _adorner = null;
        }

        if (_list.IsMouseCaptured)
            _list.ReleaseMouseCapture();

        if (_mode == Mode.Marquee)
            _mode = Mode.None;
    }

    /// <summary>Selects containers intersecting the rubber-band; keeps the base selection when extending.</summary>
    private void ApplyMarquee(Rect rect)
    {
        for (var i = 0; i < _list.Items.Count; i++)
        {
            if (_list.ItemContainerGenerator.ContainerFromIndex(i) is not ListViewItem container
                || !container.IsVisible)
                continue;

            bool inside;
            try
            {
                var bounds = container.TransformToAncestor(_list)
                    .TransformBounds(new Rect(default, container.RenderSize));
                inside = rect.IntersectsWith(bounds);
            }
            catch (InvalidOperationException)
            {
                continue; // container not connected to the same visual tree right now
            }

            var item = _list.Items[i];
            var shouldSelect = inside || _baseSelection.Contains(item);
            if (container.IsSelected != shouldSelect)
                container.IsSelected = shouldSelect;
        }
    }

    private bool PassedThreshold(Point pos) =>
        Math.Abs(pos.X - _origin.X) >= SystemParameters.MinimumHorizontalDragDistance
        || Math.Abs(pos.Y - _origin.Y) >= SystemParameters.MinimumVerticalDragDistance;

    private static T? FindAncestor<T>(DependencyObject? start) where T : DependencyObject
    {
        for (var node = start; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is T match)
                return match;
        }

        return null;
    }

    private static bool IsWithin<T>(DependencyObject? start) where T : DependencyObject =>
        FindAncestor<T>(start) is not null;

    /// <summary>The translucent grass-green selection rectangle drawn over the list while dragging.</summary>
    private sealed class MarqueeAdorner : Adorner
    {
        private static readonly Brush Fill = Frozen(Color.FromArgb(0x40, 0x7C, 0xB3, 0x42));
        private static readonly Pen Stroke = FrozenPen(Color.FromArgb(0xB0, 0x7C, 0xB3, 0x42));

        private Rect _rect;

        public MarqueeAdorner(UIElement adornedElement) : base(adornedElement)
        {
            IsHitTestVisible = false;
        }

        public void SetRect(Rect rect)
        {
            _rect = rect;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext) =>
            drawingContext.DrawRectangle(Fill, Stroke, _rect);

        private static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private static Pen FrozenPen(Color c)
        {
            var pen = new Pen(new SolidColorBrush(c), 1);
            pen.Freeze();
            return pen;
        }
    }
}
