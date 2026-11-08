using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Yaparr.Desktop;

/// <summary>
/// The reactions under a message, in one row no wider than the bubble. The ones that do not fit are left out and counted by the last chip ("+4"); that chip,
/// when clicked, lets the row wrap onto as many lines as it needs, and then reads "Show less". When everything fits, that chip is not shown at all.
/// </summary>
public sealed class ReactionStrip : Panel
{
    private const double Gap = 4;

    public static void Changed(Visual from)
    {
        for (Visual? v = from; v != null; v = v.GetVisualParent())
            if (v is ReactionStrip strip) { strip.InvalidateMeasure(); return; }
    }

    private static bool IsMore(Control c) => c.DataContext is ReactionChipVm { IsMore: true };

    protected override Size MeasureOverride(Size available)
    {
        var chips = new List<Control>();
        Control? more = null;
        foreach (var child in Children)
        {
            child.Measure(Size.Infinity);
            if (IsMore(child)) more = child; else chips.Add(child);
        }
        if (chips.Count == 0) return default;

        double room = double.IsInfinity(available.Width) ? double.MaxValue : available.Width;
        double all = chips.Sum(c => c.DesiredSize.Width) + Gap * (chips.Count - 1);
        double height = chips.Max(c => c.DesiredSize.Height);
        var vm = (more?.DataContext as ReactionChipVm);
        bool expanded = vm?.Owner.ReactionsExpanded == true;

        if (all <= room)
        {
            Layout(chips, null, chips.Count, wrap: false, room, height);
            SetHidden(vm, 0);
            return new Size(all, height);
        }

        if (expanded && more != null)
        {
            var rows = Layout(chips, more, chips.Count, wrap: true, room, height);
            SetHidden(vm, 0);
            return new Size(rows.Width, rows.Height);
        }

        // One row: as many chips as leave room for the "+N" chip, but at least one.
        double moreWidth = more?.DesiredSize.Width ?? 0;
        int shown = 0;
        double used = 0;
        for (int i = 0; i < chips.Count; i++)
        {
            double next = used + (i > 0 ? Gap : 0) + chips[i].DesiredSize.Width;
            if (next + Gap + moreWidth > room && shown >= 1) break;
            used = next; shown++;
        }
        SetHidden(vm, chips.Count - shown);
        var size = Layout(chips, more, shown, wrap: false, room, height);
        return new Size(size.Width, size.Height);
    }

    private (double Width, double Height) _extent;

    // Decides where every child goes (arranged later); children past the first "shown" chips are put out of sight.
    private readonly Dictionary<Control, Rect> _places = new();

    private (double Width, double Height) Layout(List<Control> chips, Control? more, int shown, bool wrap, double room, double height)
    {
        _places.Clear();
        double x = 0, y = 0, width = 0;
        void Put(Control c)
        {
            double w = c.DesiredSize.Width;
            if (wrap && x > 0 && x + w > room) { x = 0; y += height + Gap; }
            _places[c] = new Rect(x, y, w, c.DesiredSize.Height);
            x += w + Gap;
            width = Math.Max(width, x - Gap);
        }
        for (int i = 0; i < chips.Count; i++)
        {
            if (i < shown) Put(chips[i]);
            else _places[chips[i]] = new Rect(-100000, 0, 0, 0);
        }
        if (more != null)
        {
            bool needed = shown < chips.Count || wrap;
            if (needed) Put(more); else _places[more] = new Rect(-100000, 0, 0, 0);
        }
        _extent = (width, y + height);
        return _extent;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children)
            child.Arrange(_places.TryGetValue(child, out var place) ? place : new Rect(-100000, 0, 0, 0));
        return finalSize;
    }

    private static void SetHidden(ReactionChipVm? vm, int count)
    {
        if (vm == null || vm.Hidden == count) return;
        // Not while measuring: the chip's text changes, which asks for another pass.
        Dispatcher.UIThread.Post(() => vm.Hidden = count, DispatcherPriority.Background);
    }

    static ReactionStrip()
    {
        ClipToBoundsProperty.OverrideDefaultValue<ReactionStrip>(true);
    }
}
