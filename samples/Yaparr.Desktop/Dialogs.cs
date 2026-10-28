using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace Yaparr.Desktop;

/// <summary>Small replacements for the message boxes and file pickers that WPF gives for free.</summary>
public static class Dialogs
{
    public static Task MessageAsync(Window owner, string title, string text) =>
        ChoiceAsync(owner, title, text, "OK");

    public static async Task<bool> ConfirmAsync(Window owner, string title, string text, string yes = "Yes", string no = "No") =>
        await ChoiceAsync(owner, title, text, yes, no) == yes;

    public static async Task<string?> ChoiceAsync(Window owner, string title, string text, params string[] buttons)
    {
        string? result = null;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var dialog = new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 16,
                Children = { new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, panel },
            },
        };
        foreach (var label in buttons)
        {
            var button = new Button { Content = label, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            button.Click += (_, _) => { result = label; dialog.Close(); };
            if (panel.Children.Count == 0) button.Classes.Add("primary");
            panel.Children.Add(button);
        }
        await dialog.ShowDialog(owner);
        return result;
    }

    public static async Task<string?> PickOpenAsync(Window owner, string title, params FilePickerFileType[] types)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = types,
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public static async Task<string?> PickSaveAsync(Window owner, string title, string suggestedName, params FilePickerFileType[] types)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = types,
        });
        return file?.TryGetLocalPath();
    }

    public static readonly FilePickerFileType Zip = new("Zip archive") { Patterns = new[] { "*.zip" } };
    public static readonly FilePickerFileType WireGuard = new("WireGuard configuration") { Patterns = new[] { "*.conf" } };
    public static readonly FilePickerFileType AllFiles = new("All files") { Patterns = new[] { "*" } };

    public static async Task<bool> CopyAsync(Window owner, string text)
    {
        if (owner.Clipboard is not { } clipboard) return false;
        try { await clipboard.SetTextAsync(text); return true; }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { return false; }
    }

    public static async Task<string?> ClipboardTextAsync(Window owner)
    {
        if (owner.Clipboard is not { } clipboard) return null;
        try { return (await clipboard.TryGetTextAsync())?.Trim(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { return null; }
    }
}
