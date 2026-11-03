using Avalonia.Input;

namespace Yaparr.Desktop;

/// <summary>
/// What is held to talk: a keyboard key, or the middle, back or forward button of a mouse (the left and right buttons are not offered,
/// since they are needed for everything else). Kept in the settings as a key name such as "F13" or "Mouse4".
/// </summary>
internal readonly record struct PushToTalkTrigger(Key Key, int MouseButton)
{
    public const int Middle = 3, Back = 4, Forward = 5;

    public bool IsMouse => MouseButton != 0;

    public string Setting => IsMouse ? "Mouse" + MouseButton : Key.ToString();

    /// <summary>For the hint and the settings: "F13", "middle mouse button", "mouse button 4 (back)".</summary>
    public string Description => MouseButton switch
    {
        Middle => "the middle mouse button",
        Back => "mouse button 4 (back)",
        Forward => "mouse button 5 (forward)",
        _ => Key.ToString(),
    };

    public static PushToTalkTrigger? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (text.StartsWith("Mouse", StringComparison.Ordinal) && int.TryParse(text.AsSpan(5), out var button) && button is Middle or Back or Forward)
            return new PushToTalkTrigger(Key.None, button);
        return Enum.TryParse<Key>(text, out var key) && key != Key.None ? new PushToTalkTrigger(key, 0) : null;
    }

    /// <summary>The button of a pointer event, if it is one that can be held to talk.</summary>
    public static int ButtonOf(PointerPointProperties properties) => properties.PointerUpdateKind switch
    {
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => Middle,
        PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton1Released => Back,
        PointerUpdateKind.XButton2Pressed or PointerUpdateKind.XButton2Released => Forward,
        _ => 0,
    };
}
