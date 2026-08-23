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
    file, etc.). */
internal sealed class DocumentWatcher : IDisposable
{
    private readonly DispatcherTimer _debounce;
    private FileSystemWatcher? _fileSystemWatcher;

    internal event Action? ChangeSettled;

    internal DocumentWatcher()
    {
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
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

        _fileSystemWatcher = new FileSystemWatcher(directory)
        {
            Filter = Path.GetFileName(fullPath),
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
                         | NotifyFilters.Size | NotifyFilters.CreationTime,
        };
        _fileSystemWatcher.Changed += OnFileSystemEvent;
        _fileSystemWatcher.Created += OnFileSystemEvent;
        _fileSystemWatcher.Deleted += OnFileSystemEvent;
        _fileSystemWatcher.Renamed += OnFileSystemEvent;
        _fileSystemWatcher.EnableRaisingEvents = true;
    }

    internal void StopWatching()
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
        Dispatcher.UIThread.Post(() =>
        {
            _debounce.Stop();
            _debounce.Start();
        });
    }

    public void Dispose()
    {
        StopWatching();
        _debounce.Stop();
    }
}
