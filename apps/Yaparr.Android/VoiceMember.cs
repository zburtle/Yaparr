using Avalonia.Media;

namespace Yaparr.AndroidApp;

/// <summary>Someone in a room's voice channel, as the voice bar shows them.</summary>
public sealed class VoiceMember : Observable
{
    private bool _speaking, _muted, _deafened;

    public VoiceMember(string id, string name, bool self)
    {
        Id = id;
        Name = name;
        IsSelf = self;
    }

    public string Id { get; }
    public string Name { get; }
    public bool IsSelf { get; }

    public string Label => IsSelf ? Name + " (you)" : Name;

    public bool Speaking
    {
        get => _speaking;
        set { if (_speaking == value) return; _speaking = value; Raise(); Raise(nameof(Fill)); Raise(nameof(Ink)); }
    }

    public bool Muted
    {
        get => _muted;
        set { if (_muted == value) return; _muted = value; Raise(); Raise(nameof(State)); }
    }

    public bool Deafened
    {
        get => _deafened;
        set { if (_deafened == value) return; _deafened = value; Raise(); Raise(nameof(State)); }
    }

    /// <summary>"muted" or "deafened" next to the name; empty when neither.</summary>
    public string State => _deafened ? "deafened" : _muted ? "muted" : "";

    /// <summary>A person who is talking is lit up.</summary>
    public IBrush Fill => _speaking ? Theme.Brush("Accent") : Theme.Brush("BubbleOther");

    public IBrush Ink => _speaking ? Brushes.White : Theme.Brush("Ink");
}
