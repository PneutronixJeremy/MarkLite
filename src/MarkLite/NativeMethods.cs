using System;
using System.Runtime.InteropServices;

namespace MarkLite;

/*  The handful of Win32 calls MarkLite makes directly.

    They are all about the foreground, which Windows does not hand out on
    request: a process that does not already own it cannot take it, and
    SetForegroundWindow quietly degrades to flashing the taskbar button. The
    process that DOES own it after a double-click is the one Explorer just
    started — the secondary launch — so raising the primary's window takes both
    halves, the secondary granting the right away and the primary claiming it. */
internal static class NativeMethods
{
    /// <summary>ASFW_ANY: any process may take the foreground from this one.</summary>
    internal const int AsfwAny = -1;

    /*  DllImport rather than the LibraryImport source generator: the generated
        stubs need AllowUnsafeBlocks, which is not worth turning on across the
        project for two calls that marshal nothing but a handle and a bool.
        Plain P/Invoke of a Win32 entry point is AOT-safe as it stands. */
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr windowHandle);
}
