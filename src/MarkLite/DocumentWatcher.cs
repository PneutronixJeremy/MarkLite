using System;
using System.IO;
using Avalonia.Threading;

namespace MarkLite;

/*  Watches the open document for external changes. Watches the containing
    directory filtered to the file name (not the file handle itself) so
    atomic-save patterns — write temp file, rename over the target — are seen
    as Created/Renamed events on the watched name. Editors fire several events
    per save, so changes are debounced and ChangeSettled fires once, on the UI
    thread; the consumer decides what a settled change means (reload, missing
    file, etc.).

    A FileSystemWatcher can stop delivering without warning — after sleep and
    resume it has been seen to go quiet on a local disk until restart — so the
    watcher is not trusted alone. It is rebuilt when it reports an error or the
    owner reports a resume, and a cheap stamp (exists / last write / length)
    taken at each settle lets the owner's periodic check catch a change the
    watcher never reported. */
internal sealed class DocumentWatcher : IDisposable
{
    private readonly DispatcherTimer _debounce;
    private FileSystemWatcher? _fileSystemWatcher;
    private string? _path;
    private FileStamp _stamp;

    internal event Action? ChangeSettled;

    internal DocumentWatcher()
    {
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            /*  Stamped before the consumer reads the file: a write landing in
                between leaves the stamp older than the content, which costs one
                redundant reload on the next check — never a missed one. */
            if (_path is not null)
            {
                _stamp = FileStamp.Read(_path);
            }
            ChangeSettled?.Invoke();
        };
    }

    internal void Watch(string fullPath)
    {
        StopWatching();

        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        _path = fullPath;
        _stamp = FileStamp.Read(fullPath);
        StartFileSystemWatcher(directory, Path.GetFileName(fullPath));
    }

    internal void StopWatching()
    {
        _path = null;
        DisposeFileSystemWatcher();
    }

    /*  Rebuilds the OS watch unconditionally, then looks for anything missed
        while it was down. For resume from sleep, where the old watcher may be
        alive in name only and raise nothing, error or otherwise. */
    internal void Rewatch()
    {
        if (_path is null)
        {
            return;
        }
        RestartFileSystemWatcher();
        SettleIfStampChanged("rewatch");
    }

    /*  The periodic safety net: a stat of one file. Skipped while a settle is
        already pending — the watcher saw the change, and the stamp is
        refreshed when it settles. */
    internal void CheckForMissedChange()
    {
        if (_path is null || _debounce.IsEnabled)
        {
            return;
        }
        if (_fileSystemWatcher is null)
        {
            // An earlier rebuild failed (e.g. folder gone); keep trying.
            RestartFileSystemWatcher();
        }
        if (SettleIfStampChanged("missed change"))
        {
            RestartFileSystemWatcher();
        }
    }

    // Debug command channel only: the watcher stays, but goes deaf.
    internal void MuteForTest()
    {
        if (_fileSystemWatcher is not null)
        {
            _fileSystemWatcher.EnableRaisingEvents = false;
        }
    }

    private bool SettleIfStampChanged(string reason)
    {
        if (_path is null || FileStamp.Read(_path) == _stamp)
        {
            return false;
        }
        DebugLog.Write($"watcher {reason}: {_path} changed unreported");
        ScheduleSettle();
        return true;
    }

    private void StartFileSystemWatcher(string directory, string fileName)
    {
        var watcher = new FileSystemWatcher(directory)
        {
            Filter = fileName,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
                         | NotifyFilters.Size | NotifyFilters.CreationTime,
        };
        watcher.Changed += OnFileSystemEvent;
        watcher.Created += OnFileSystemEvent;
        watcher.Deleted += OnFileSystemEvent;
        watcher.Renamed += OnFileSystemEvent;
        watcher.Error += OnFileSystemError;
        watcher.EnableRaisingEvents = true;
        _fileSystemWatcher = watcher;
    }

    private void RestartFileSystemWatcher()
    {
        if (_path is null)
        {
            return;
        }
        DisposeFileSystemWatcher();

        var directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }
        try
        {
            StartFileSystemWatcher(directory, Path.GetFileName(_path));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            /*  Folder missing or unreachable right now. The periodic check
                retries, and its stamp comparison still sees the file come back. */
            DebugLog.Write($"watcher restart failed: {_path}: {ex.Message}");
        }
    }

    private void DisposeFileSystemWatcher()
    {
        if (_fileSystemWatcher is not null)
        {
            _fileSystemWatcher.EnableRaisingEvents = false;
            _fileSystemWatcher.Dispose();
            _fileSystemWatcher = null;
        }
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        /*  Restarting the timer on every event collapses the burst an editor
            fires per save into a single settled notification. */
        Dispatcher.UIThread.Post(ScheduleSettle);
    }

    private void OnFileSystemError(object sender, ErrorEventArgs e)
    {
        /*  Buffer overflow or a broken watch handle. Either way events were
            lost, and for the latter .NET has already stopped this watcher, so
            rebuild it and settle once to pick up whatever was missed. A late
            error from a watcher already replaced is ignored. */
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(sender, _fileSystemWatcher))
            {
                return;
            }
            DebugLog.Write($"watcher error: {_path}: {e.GetException().Message}");
            RestartFileSystemWatcher();
            ScheduleSettle();
        });
    }

    private void ScheduleSettle()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    public void Dispose()
    {
        StopWatching();
        _debounce.Stop();
    }

    private readonly record struct FileStamp(bool Exists, DateTime LastWriteUtc, long Length)
    {
        internal static FileStamp Read(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? new FileStamp(true, info.LastWriteTimeUtc, info.Length) : default;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return default;
            }
        }
    }
}
