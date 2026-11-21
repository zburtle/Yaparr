using Yaparr.AppCore;
using Yaparr.Chat;
using Yaparr.Chat.Files;

namespace Yaparr.AndroidApp;

// A file offered in the message (docs/file-sharing.md): shown as a card with Download, progress, pause and cancel. Nothing of it is on the phone until the person taps Download.
public sealed partial class Line
{
    private FileProgress? _fileProgress;
    private string? _filePath;

    /// <summary>The file this message offers, if it does.</summary>
    public FileOffer? File => Source?.File ?? FileOffered;

    /// <summary>The offer of a message just sent from this phone (it has no <see cref="Source"/> from the library).</summary>
    public FileOffer? FileOffered { get; set; }

    public bool HasFile => File != null;
    public string FileName => File?.Name ?? "";

    /// <summary>Where the file is on this phone, once it is here (a download), or its address when it was sent from here.</summary>
    public string? FilePath => _filePath;

    public string FileStatusText =>
        _fileProgress is { State: not (FileState.Canceled or FileState.Completed) } p ? FileTexts.Status(p)
        : _filePath != null ? FileTexts.Size(File?.Size ?? 0) + (Mine ? " · shared from this phone" : " · on this phone")
        : _fileProgress is { State: FileState.Canceled } ? FileTexts.Size(File?.Size ?? 0) + " · canceled"
        : FileTexts.Size(File?.Size ?? 0);

    private bool FileBusy => _fileProgress is { State: FileState.Downloading or FileState.Waiting or FileState.Paused };
    public bool FileShowsProgress => FileBusy;
    public double FilePercent => _fileProgress is { } p ? FileTexts.Percent(p) : 0;
    public bool FileCanDownload => HasFile && _filePath == null && !FileBusy;
    public bool FileCanPause => _fileProgress is { State: FileState.Downloading or FileState.Waiting };
    public bool FileCanResume => _fileProgress is { State: FileState.Paused };
    public bool FileCanCancel => FileBusy;

    /// <summary>A file that was downloaded can be saved somewhere else on the phone (where its own folder, which Android hides, is not wanted).</summary>
    public bool FileCanSaveCopy => _filePath != null && !Mine && !_filePath.Contains("://", StringComparison.Ordinal);

    public void SetFile(FileProgress? progress, string? path)
    {
        _fileProgress = progress;
        _filePath = path;
        foreach (var name in new[] { nameof(FileStatusText), nameof(FileShowsProgress), nameof(FilePercent), nameof(FileCanDownload), nameof(FileCanPause), nameof(FileCanResume), nameof(FileCanCancel), nameof(FileCanSaveCopy), nameof(FilePath), nameof(HasFile), nameof(FileName) })
            Raise(name);
    }
}
