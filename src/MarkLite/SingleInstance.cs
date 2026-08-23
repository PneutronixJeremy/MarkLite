using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace MarkLite;

/*  Single-instance handoff over a named pipe. The first process to create the
    pipe server becomes the primary; a later launch with a file argument
    connects as a client, sends the full path (one UTF-8 message per
    connection) and exits, and the primary opens it as a new tab. The pipe name
    is per-user so two Windows sessions don't fight over one instance. A
    secondary that cannot reach the primary falls back to running standalone —
    better a second window than a lost document. */
internal static class SingleInstance
{
    private static readonly string PipeName = $"MarkLite-{Environment.UserName}";
    private static NamedPipeServerStream? _server;

    /// <summary>Tries to claim the pipe; true means this process is the primary instance.</summary>
    internal static bool TryBecomePrimary()
    {
        try
        {
            _server = new NamedPipeServerStream(
                PipeName,
                PipeDirection.In,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Secondary side: hands a file path to the primary. True on success.</summary>
    internal static bool SendToPrimary(string fullPath)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeout: 2000);
            client.Write(Encoding.UTF8.GetBytes(fullPath));
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /*  Primary side: accepts handoffs for the process lifetime. One connection
        carries one path; the callback runs on the UI thread. Errors on a
        single connection are logged and the accept loop continues — a broken
        client must not kill single-instance behavior. */
    internal static void StartServer(Action<string> onFileReceived)
    {
        if (_server is null)
        {
            return;
        }

        var server = _server;
        Task.Run(async () =>
        {
            var buffer = new byte[8192];
            while (true)
            {
                try
                {
                    await server.WaitForConnectionAsync().ConfigureAwait(false);

                    using var received = new MemoryStream();
                    int read;
                    while ((read = await server.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                    {
                        received.Write(buffer, 0, read);
                    }

                    var path = Encoding.UTF8.GetString(received.ToArray()).Trim();
                    if (path.Length > 0)
                    {
                        Dispatcher.UIThread.Post(() => onFileReceived(path));
                    }
                }
                catch (Exception ex)
                {
                    DebugLog.Write($"pipe server error: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    /*  Disconnect must run even when the client already went
                        away — IsConnected turns false then, but the pipe
                        handle stays in connected state and the next
                        WaitForConnection would throw without this. */
                    try
                    {
                        server.Disconnect();
                    }
                    catch (Exception)
                    {
                        // Disconnect after a faulted connection can throw; the loop retries anyway.
                    }
                }
            }
        });
    }
}
