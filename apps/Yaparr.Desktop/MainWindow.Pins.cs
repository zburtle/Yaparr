using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Yaparr.Chat;

namespace Yaparr.Desktop;

// Pinned rooms and chats: a pinned one stays at the top of its list (the rooms and the direct chats each have a list), in an order the person sets with the menu of the
// list (Move up, Move down) or by dragging it. The pins belong to the account, so they are the same on all the person's devices (ChatNode.Pins, kept as account entries);
// this file only shows them and asks the node to change them. Which one is where is decided by PinnedFirst, which leaves the others in the order they have.
public partial class MainWindow
{
    private static readonly DataFormat<string> PinnedFormat = DataFormat.CreateStringApplicationFormat("yaparr-pinned-conversation");

    private ConversationVm? _pinPressed;
    private Point _pinPressedAt;
    private PointerPressedEventArgs? _pinPressedEvent;
    private bool _pinDragging;

    // Once, from the constructor, after the lists are kept in step with the conversations (SyncConversationLists): a new conversation is put in its place, and the
    // lists start drags and take drops.
    private void WirePinning()
    {
        _conversations.CollectionChanged += (_, _) => RefreshPins();
        foreach (var list in new[] { RoomList, ChatList })
        {
            DragDrop.SetAllowDrop(list, true);
            list.AddHandler(PointerPressedEvent, OnPinPointerPressed, RoutingStrategies.Tunnel);
            list.AddHandler(PointerMovedEvent, OnPinPointerMoved, RoutingStrategies.Tunnel);
            list.AddHandler(PointerReleasedEvent, (_, _) => { if (!_pinDragging) _pinPressed = null; }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
            list.AddHandler(DragDrop.DragOverEvent, OnPinDragOver);
            list.AddHandler(DragDrop.DropEvent, OnPinDrop);
        }
    }

    // Marks which conversations are pinned and puts the lists in order: the pinned ones first, the others as they were.
    private void RefreshPins()
    {
        if (_node is not { } node) return;
        var pins = new PinnedFirst(node.Pins);
        foreach (var conv in _conversations) conv.IsPinned = pins.IsPinned(conv.IsRoom, conv.PinId);
        var keep = _selected;
        _selecting = true;
        try
        {
            PinnedFirst.Apply(_roomItems, c => pins.RankOf(true, c.PinId));
            PinnedFirst.Apply(_chatItems, c => pins.RankOf(false, c.PinId));
            // Moving rows about can make a list let go of the one that is selected: it is put back, without opening anything again.
            if (keep != null && (keep.IsRoom ? RoomList : ChatList) is { } list && !ReferenceEquals(list.SelectedItem, keep)) list.SelectedItem = keep;
        }
        finally { _selecting = false; }
    }

    // The pinned ones of the rooms (or of the direct chats) in the order they are shown, as the node holds them.
    private List<PinnedConversation> PinsShownIn(bool rooms)
    {
        if (_node is not { } node) return new();
        var held = node.Pins;
        return (rooms ? _roomItems : _chatItems)
            .Where(c => c.IsPinned && c.PinId != null)
            .Select(c => held.FirstOrDefault(p => p.IsRoom == c.IsRoom && p.Id == c.PinId))
            .OfType<PinnedConversation>().ToList();
    }

    // Puts a pinned conversation at a place among the pinned ones of its list (0 is the top). The lists follow when the node says the pins changed.
    private void MovePinTo(ConversationVm conv, int index)
    {
        if (_node is not { } node || conv.PinId is not { } id) return;
        var shown = PinsShownIn(conv.IsRoom);
        int from = shown.FindIndex(p => p.Id == id);
        if (from < 0) return;
        index = Math.Clamp(index, 0, shown.Count - 1);
        if (index == from) return;
        var (after, before) = PinnedFirst.Neighbours(shown, shown[from], index);
        node.MovePin(conv.IsRoom, id, after, before);
    }

    // ---- the menu of the lists ----

    // Shows what fits the conversation that was clicked: pin or unpin, the moves a pinned one can make, and the settings that only a room has.
    private void ConversationMenu_Opening(object? sender, CancelEventArgs e)
    {
        if (_selected is not { } conv || _node == null || sender is not ContextMenu menu) { e.Cancel = true; return; }
        var shown = PinsShownIn(conv.IsRoom);
        int at = shown.FindIndex(p => p.Id == conv.PinId);
        foreach (var item in menu.Items.OfType<Control>())
        {
            switch (item.Tag as string)
            {
                case "pin":
                    if (item is MenuItem pin) pin.Header = conv.IsPinned ? "Un_pin" : "_Pin to top";
                    break;
                case "up":
                    item.IsVisible = conv.IsPinned;
                    item.IsEnabled = at > 0;
                    break;
                case "down":
                    item.IsVisible = conv.IsPinned;
                    item.IsEnabled = at >= 0 && at < shown.Count - 1;
                    break;
                case "room":
                    item.IsVisible = conv.IsRoom;
                    break;
            }
        }
    }

    private void TogglePin_Click(object? sender, RoutedEventArgs e)
    {
        if (_node is not { } node || _selected is not { PinId: { } id } conv) return;
        if (conv.IsPinned) node.Unpin(conv.IsRoom, id); else node.Pin(conv.IsRoom, id);
    }

    private void MovePinUp_Click(object? sender, RoutedEventArgs e) => MoveSelectedPin(-1);

    private void MovePinDown_Click(object? sender, RoutedEventArgs e) => MoveSelectedPin(1);

    private void MoveSelectedPin(int step)
    {
        if (_selected is not { IsPinned: true, PinId: { } id } conv) return;
        int at = PinsShownIn(conv.IsRoom).FindIndex(p => p.Id == id);
        if (at >= 0) MovePinTo(conv, at + step);
    }

    // ---- dragging ----

    // A press on a pinned conversation is noted before the row takes it (it must still select), and moving on from it starts a drag.
    private void OnPinPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pinDragging = false;
        _pinPressed = null;
        if (sender is not ListBox list || !e.GetCurrentPoint(list).Properties.IsLeftButtonPressed) return;
        if ((e.Source as StyledElement)?.DataContext is ConversationVm { IsPinned: true } conv)
        {
            _pinPressed = conv;
            _pinPressedAt = e.GetPosition(list);
            _pinPressedEvent = e;
        }
    }

    private async void OnPinPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not ListBox list || _pinPressed is not { PinId: { } id } conv || _pinPressedEvent is not { } trigger || _pinDragging
            || !e.GetCurrentPoint(list).Properties.IsLeftButtonPressed) return;
        var at = e.GetPosition(list);
        if (Math.Abs(at.X - _pinPressedAt.X) < 6 && Math.Abs(at.Y - _pinPressedAt.Y) < 6) return;
        _pinDragging = true;
        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(PinnedFormat, (conv.IsRoom ? "r:" : "d:") + id));
            await DragDrop.DoDragDropAsync(trigger, data, DragDropEffects.Move);
        }
        finally
        {
            _pinDragging = false;
            _pinPressed = null;
        }
    }

    // The pinned conversation being dragged: its kind and id are what the drag carries.
    private ConversationVm? DraggedPin(DragEventArgs e)
    {
        if (e.DataTransfer.TryGetValue(PinnedFormat) is not { Length: > 2 } text) return null;
        bool room = text[0] == 'r';
        var id = text[2..];
        return _conversations.FirstOrDefault(c => c.IsRoom == room && c.IsPinned && c.PinId == id);
    }

    private bool IsListOf(ListBox list, ConversationVm conv) => ReferenceEquals(list, conv.IsRoom ? RoomList : ChatList);

    // A pinned conversation is moved within its own list only: a room cannot be dropped among the chats.
    private void OnPinDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = sender is ListBox list && DraggedPin(e) is { } conv && IsListOf(list, conv) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    // Dropped on a row it takes that row's place; dropped below the pinned ones (or among the others) it goes last of the pinned ones.
    private void OnPinDrop(object? sender, DragEventArgs e)
    {
        if (sender is not ListBox list || DraggedPin(e) is not { } conv || !IsListOf(list, conv)) return;
        e.Handled = true;
        MovePinTo(conv, IndexAt(list, e.GetPosition(list)));
    }

    // The place in the list of the row at a point of it (the last row for a point below all of them).
    private static int IndexAt(ListBox list, Point at)
    {
        int count = list.ItemCount;
        for (int i = 0; i < count; i++)
        {
            if (list.ContainerFromIndex(i) is not Control row) continue;
            if (row.TranslatePoint(new Point(0, 0), list) is { } top && at.Y < top.Y + row.Bounds.Height) return i;
        }
        return count - 1;
    }
}
