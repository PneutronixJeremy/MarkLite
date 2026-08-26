using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace MarkLite;

/// <summary>One debug command as it travels over the single-instance pipe.</summary>
internal readonly record struct DebugCommand(string Text);

/*  Single-instance handoff over a named pipe. The first process to create the
    pipe server becomes the primary; a later launch with a file argument
    connects as a client, sends the full path (one UTF-8 message per
    connection) and exits, and the primary opens it as a new tab. The pipe name
    is per-user so two Windows sessions don't fight over one instance. A
    secondary that cannot reach the primary falls back to running standalone —
    better a second window than a lost document. */
internal static class SingleInstance
{
    /*  Per-user pipe so two Windows sessions don't fight over one instance.
        MARKLITE_INSTANCE adds a second axis: a launch that sets it forms its
        own single-instance group, which is how verification scripts drive a
        MarkLite of their own while the user's copy keeps running untouched —
        without it, a scripted launch would hand its test files to whatever
        window the user already had open. */
    private static readonly string PipeName = BuildPipeName();

    private static string BuildPipeName()
    {
        var name = $"MarkLite-{Environment.UserName}";
        return Environment.GetEnvironmentVariable("MARKLITE_INSTANCE") is { Length: > 0 } instance
            ? $"{name}-{instance}"
            : name;
    }
    private static NamedPipeServerStream? _server;

    /*  Messages carrying this prefix are debug commands rather than file
        paths, which is how verification scripts drive a running instance
        without touching keyboard or mouse. A path can never collide with it:
        "cmd:" is not a legal Windows drive specifier. */
    private const string CommandPrefix = "cmd:";

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
        /*  This process was started by Explorer in response to the user's
            double-click, so for this moment it owns the foreground and is the
            only one that can give it away. Without this the primary's
            Activate() flashes the taskbar button and the document opens in a
            window the user still has to go and find.

            ASFW_ANY rather than the primary's process id: the id is not in the
            protocol, and the grant lapses at the next foreground change
            anyway. Deliberately NOT done for the debug-command overload —
            verification scripts run while the user is working and must never
            pull focus. */
        if (!NativeMethods.AllowSetForegroundWindow(NativeMethods.AsfwAny))
        {
            DebugLog.Write("handoff: foreground grant refused; the primary may only flash");
        }
        return Send(fullPath);
    }

    /// <summary>Secondary side: hands a debug command to the primary. True on success.</summary>
    internal static bool SendToPrimary(DebugCommand command)
    {
        return Send(CommandPrefix + command.Text);
    }

    private static bool Send(string payload)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeout: 2000);
            client.Write(Encoding.UTF8.GetBytes(payload));
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /*  Primary side: accepts handoffs for the process lifetime. One connection
        carries one path — or one "cmd:" debug command — and the callback runs
        on the UI thread. Errors on a single connection are logged and the
        accept loop continues: a broken client must not kill single-instance
        behavior. */
    internal static void StartServer(Action<string> onFileReceived, Action<DebugCommand>? onCommand = null)
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

                    var message = Encoding.UTF8.GetString(received.ToArray()).Trim();
                    if (message.StartsWith(CommandPrefix, StringComparison.Ordinal))
                    {
                        var command = new DebugCommand(message[CommandPrefix.Length..]);
                        Dispatcher.UIThread.Post(() => onCommand?.Invoke(command));
                    }
                    else if (message.Length > 0)
                    {
                        Dispatcher.UIThread.Post(() => onFileReceived(message));
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
