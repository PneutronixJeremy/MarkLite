using System;

namespace MarkLite;

/*  Diagnostics channel gated by MARKLITE_DEBUG=1. Lines go to stderr so
    scripted verification can capture and assert on them; a normal double-click
    launch has no console attached and the writes are harmless no-ops. */
internal static class DebugLog
{
    internal static bool Enabled { get; } =
        Environment.GetEnvironmentVariable("MARKLITE_DEBUG") == "1";

    internal static void Write(string message)
    {
        if (Enabled)
        {
            Console.Error.WriteLine($"[marklite] {message}");
        }
    }
}
