using Yaparr.AppCore;
using Yaparr.Chat;
using Yaparr.Chat.Files;

namespace Yaparr.AndroidApp;

// A file offered in the message (docs/file-sharing.md): shown as a card with Download, progress, pause and cancel. Nothing of it is on the phone until the person taps Download.
public sealed partial class Line
{
    private FileProgress? _fileProgress;
    private string? _filePath;
    private string? _savedTo;

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
        : _filePath != null && !SharedFromHere && _filePath.Contains("://", StringComparison.Ordinal) ? FileTexts.Size(File?.Size ?? 0) + " · saved to " + (_savedTo ?? "the place you chose")
        : _filePath != null && !SharedFromHere && _savedTo != null ? FileTexts.Size(File?.Size ?? 0) + " · saved to " + _savedTo
        : _filePath != null && !SharedFromHere ? FileTexts.Size(File?.Size ?? 0) + " · kept inside Yaparr, where the Files app cannot see it. Save it to Downloads to find it."
        : _filePath != null ? FileTexts.Size(File?.Size ?? 0) + " · shared from this phone"
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
    public bool FileCanSaveCopy => _filePath != null && !SharedFromHere && !_filePath.Contains("://", StringComparison.Ordinal);

    /// <summary>The file is here, so it can be opened.</summary>
    public bool FileCanOpen => _filePath != null;

    /// <summary>The content address of the copy in Downloads, once one was made.</summary>
    public string? SavedAddress { get; private set; }

    /// <summary>This phone sent the message (not another of your devices, nor someone else), so its file is the one picked here.</summary>
    public bool SentHere { get; init; }

    // A file picked on this phone was sent from here. Anything else on this phone is a download (even of a file your own desktop sent), kept where the person chose.
    private bool SharedFromHere => _filePath != null && (SentHere || FileOffered != null);

    /// <summary>Says where a copy was saved (shown on the card from then on).</summary>
    public void SetSavedTo(string where, string? address = null)
    {
        _savedTo = where;
        if (address != null) SavedAddress = address;
        Raise(nameof(FileStatusText));
    }

    public void SetFile(FileProgress? progress, string? path)
    {
        _fileProgress = progress;
        _filePath = path;
        // After a restart the name of the chosen place is read from where it is kept.
        if (path != null && _savedTo == null && !SharedFromHere && path.Contains("://", StringComparison.Ordinal)) _savedTo = MainActivity.DisplayNameOf(path);
        foreach (var name in new[] { nameof(FileStatusText), nameof(FileShowsProgress), nameof(FilePercent), nameof(FileCanDownload), nameof(FileCanPause), nameof(FileCanResume), nameof(FileCanCancel), nameof(FileCanSaveCopy), nameof(FileCanOpen), nameof(FilePath), nameof(HasFile), nameof(FileName) })
            Raise(name);
    }
}
