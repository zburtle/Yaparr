using Yaparr.Chat;
using Yaparr.Identity;

namespace Yaparr.Screen;

/// <summary>
/// Frames put in by hand, for a <see cref="ScreenViewer"/> that is not watching a stream: the sharer's own preview takes
/// <see cref="ScreenSharer.FrameEncoded"/> and pushes each frame here, so the preview decodes exactly what is being sent.
/// </summary>
public sealed class LocalFrames : IMediaFrameSource
{
    private uint _id;

    public event Action<MediaFrame>? FrameReceived;

    /// <summary>Hands a frame to whoever is watching. The bytes are copied by the viewer.</summary>
    public void Push(byte[] frame, bool isKey) =>
        FrameReceived?.Invoke(new MediaFrame(default(PeerId), ++_id, frame, isKey));
}
