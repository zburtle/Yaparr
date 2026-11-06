namespace Yaparr.AndroidApp;

/// <summary>A voice channel of a room, with who is in it and a button to join it.</summary>
public sealed class VoiceChannelVm : Observable
{
    private string _members = "", _name;
    private bool _inCall;

    public VoiceChannelVm(string id, string name)
    {
        Id = id;
        _name = name;
    }

    public string Id { get; }

    public string Name
    {
        get => _name;
        set { if (_name == value) return; _name = value; Raise(); }
    }

    /// <summary>The names of the people in the channel; "No one" when it is empty.</summary>
    public string MembersText
    {
        get => _members;
        set { if (_members == value) return; _members = value; Raise(); }
    }

    /// <summary>You are in this channel on this phone.</summary>
    public bool InCall
    {
        get => _inCall;
        set { if (_inCall == value) return; _inCall = value; Raise(); Raise(nameof(CanJoin)); }
    }

    public bool CanJoin => !_inCall;
}
