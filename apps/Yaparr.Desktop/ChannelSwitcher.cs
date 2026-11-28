using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Yaparr.Chat;

namespace Yaparr.Desktop;

/// <summary>A box to type a few letters of a channel (or a room, or a category) and jump to it: every channel of every room.</summary>
public static class ChannelSwitcher
{
    public sealed record Item(ConversationVm Conv, ChatRoom Room, ChannelVm Channel, string RoomName, string ChannelName, string Notes, int Unread, long LastActive, bool InThisRoom)
    {
        public string Text => ChannelName + "   " + RoomName + (Unread > 0 ? $"   ({Unread})" : "") + (Notes.Length > 0 ? "   " + Notes : "");
        public string Haystack => (RoomName + " " + ChannelName).ToLowerInvariant();
    }

    /// <summary>What is listed for what was typed: every word must be somewhere in the room's (or category's) or channel's name; with nothing typed, channels with news first, then the ones used lately, those of the room on show before others.</summary>
    internal static List<Item> Matches(IEnumerable<Item> all, string? typed)
    {
        var words = (typed ?? "").Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var found = all.Where(i => words.All(w => i.Haystack.Contains(w))).ToList();
        return found.OrderByDescending(i => i.Unread > 0).ThenByDescending(i => i.InThisRoom).ThenByDescending(i => i.LastActive).ToList();
    }

    public static async Task<Item?> AskAsync(Window owner, IReadOnlyList<Item> all)
    {
        Item? result = null;
        var box = new TextBox { PlaceholderText = "Go to a channel…" };
        var list = new ListBox { Height = 300 };
        var shown = new List<Item>();
        var dialog = new Window
        {
            Title = "Go to a channel",
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        void Refill()
        {
            shown = Matches(all, box.Text);
            list.ItemsSource = shown.Select(i => i.Text).ToList();
            if (shown.Count > 0) list.SelectedIndex = 0;
        }
        void Pick()
        {
            if (list.SelectedIndex < 0 || list.SelectedIndex >= shown.Count) return;
            result = shown[list.SelectedIndex];
            dialog.Close();
        }
        box.TextChanged += (_, _) => Refill();
        list.DoubleTapped += (_, _) => Pick();
        // The keys work from the box: arrows move through the list, Enter goes there, Escape closes.
        dialog.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape) { dialog.Close(); e.Handled = true; }
            else if (e.Key == Key.Enter) { Pick(); e.Handled = true; }
            else if (e.Key is Key.Down or Key.Up && shown.Count > 0)
            {
                int next = Math.Clamp(list.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, shown.Count - 1);
                list.SelectedIndex = next;
                list.ScrollIntoView(next);
                e.Handled = true;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        dialog.Opened += (_, _) => box.Focus();
        Refill();
        dialog.Content = new StackPanel { Margin = new Thickness(16), Spacing = 10, Children = { box, list } };
        await dialog.ShowDialog(owner);
        return result;
    }
}
