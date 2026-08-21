// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Windows
{
    using System;
    using System.ComponentModel;
    using System.Runtime.InteropServices;
    using System.IO;
    using System.Runtime.Versioning;
    // global:: is required, not stylistic. This namespace is Porta.Pty.WINDOWS, so an unqualified
    // `using Windows.Win32` binds relative to it and looks for Porta.Pty.Windows.Windows.Win32.
    // The alternative is moving the usings outside the namespace block, which is what Sylinko's fork
    // does by using file-scoped namespaces; keeping them inside preserves this file's upstream shape.
    using global::Windows.Win32;
    using global::Windows.Win32.Foundation;
    using global::Windows.Win32.System.Console;

    /// <summary>
    /// A pseudoconsole, from either of the two ConPTY implementations available on Windows, selected
    /// at RUNTIME.
    ///
    /// <para>Windows ships ConPTY in kernel32, backed by whatever conhost.exe the OS happens to have.
    /// Microsoft also ships it out of band as the <c>Microsoft.Windows.Console.ConPTY</c> package —
    /// <c>conpty.dll</c> plus <c>OpenConsole.exe</c>, the same implementation Windows Terminal
    /// carries. The two have identical entry points and an identical process model: in-box spawns a
    /// conhost.exe per pseudoconsole, out-of-band spawns an OpenConsole.exe per pseudoconsole.</para>
    ///
    /// <para>Both are wired up here, behind <c>PORTAPTY_CONPTY=oob</c>, so the choice can be MEASURED
    /// rather than argued. Two builds would have made the comparison worse: any difference could then
    /// be the build rather than the implementation. One build, one switch, one harness.</para>
    ///
    /// <para>The two paths differ in the DLL name and in nothing else: both take and return a plain
    /// IntPtr, and the in-box arm uses CsWin32's raw pointer overloads rather than its SafeHandle
    /// ones so the shapes match. A comparison where one arm goes through a different marshalling
    /// layer is not a comparison of ConPTY implementations.</para>
    ///
    /// <para>Out-of-band is the DEFAULT, with an automatic fallback to in-box when conpty.dll is not
    /// present beside the assembly. It measured 9 ms per pseudoconsole against in-box's 13 ms, and it
    /// pins behaviour to one implementation rather than to whatever Windows build the user happens to
    /// run — which for a shipped terminal is worth more than the 4 ms.</para>
    ///
    /// <para>It appeared for a long time to cost ~3.0 SECONDS per pseudoconsole. It does not, and the
    /// reason is load-bearing for anyone touching this: ConPTY asks the terminal what it is
    /// (Primary Device Attributes) and blocks for three seconds waiting for a reply. A consumer that
    /// only reads never answers. <see cref="PtyProvider"/> answers on this path so that consumers do
    /// not have to — see docs/conpty-out-of-band.md.</para>
    /// </summary>
    /// <remarks>
    /// Windows-only, and specifically Windows 10 1809 or later: ConPTY does not exist before that, and
    /// <see cref="NativeMethods.IsPseudoConsoleSupported"/> is the runtime gate that says so with a
    /// PlatformNotSupportedException naming the version.
    ///
    /// The VERSION in the annotation is not decoration. CsWin32's generated entry points carry their
    /// own floors (windows5.1.2600 for the job-object calls, windows6.0.6000 for the attribute-list
    /// ones), and a bare "windows" annotation does not satisfy them -- the platform-compatibility
    /// analyzer reported 22 warnings saying exactly that. Stating the real minimum satisfies all of
    /// them truthfully, where suppressing would have hidden a genuine question about which Windows
    /// versions this library supports.
    /// </remarks>
    [SupportedOSPlatform("windows10.0.17763")]
    internal sealed class PseudoConsole : IDisposable
    {
        private readonly bool outOfBand;
        private IntPtr handle;
        private bool disposed;

        private PseudoConsole(IntPtr handle, bool outOfBand)
        {
            this.handle = handle;
            this.outOfBand = outOfBand;
        }

        /// <summary>
        /// Gets a value indicating whether the out-of-band <c>conpty.dll</c> is selected.
        ///
        /// <para>Out-of-band is the DEFAULT, and falls back to in-box when <c>conpty.dll</c> is not
        /// beside the assembly. That fallback is what makes the default safe: a consumer that has not
        /// referenced the ConPTY package, or whose output is not RID-specific, gets the in-box
        /// implementation rather than a <see cref="DllNotFoundException"/> on its first terminal.</para>
        ///
        /// <para><c>PORTAPTY_CONPTY</c> overrides in either direction: <c>inbox</c> forces kernel32,
        /// <c>oob</c> forces conpty.dll and lets it throw if it is missing, which is what a consumer
        /// wants when it believes it is set up and would rather know it is not.</para>
        ///
        /// <para>Probed by absolute path rather than by attempting a call: the imports below pin the
        /// search to the assembly directory, and Windows 11 ships its own
        /// <c>System32\conpty.dll</c> — so an unqualified probe would find the OS copy and report
        /// available for something we would not be using.</para>
        ///
        /// <para>Read once: the answer cannot change within a process, and a per-spawn probe on the
        /// terminal-creation path buys nothing.</para>
        /// </summary>
        public static bool UseOutOfBand { get; } = ResolvePreference();

        private static bool ResolvePreference()
        {
            var preference = Environment.GetEnvironmentVariable("PORTAPTY_CONPTY");

            if (string.Equals(preference, "inbox", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.Equals(preference, "oob", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            try
            {
                return NativeLibrary.TryLoad(
                    Path.Combine(AppContext.BaseDirectory, "conpty.dll"), out _);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Gets a value indicating whether this pseudoconsole came from conpty.dll.</summary>
        public bool IsOutOfBand => this.outOfBand;

        /// <summary>Gets the raw HPCON, for the process-thread attribute list.</summary>
        public IntPtr Handle => this.handle;

        /// <summary>Gets which implementation produced this pseudoconsole, for diagnostics.</summary>
        public string Implementation => this.outOfBand ? "conpty.dll (out-of-band)" : "kernel32 (in-box)";

        /// <summary>Creates a pseudoconsole over the given pipe ends.</summary>
        public static PseudoConsole Create(short cols, short rows, IntPtr inPipe, IntPtr outPipe)
        {
            var size = new COORD { X = cols, Y = rows };
            bool oob = UseOutOfBand;

            int hr = oob
                ? OutOfBand.CreatePseudoConsole(size, inPipe, outPipe, 0, out IntPtr hpcon)
                : InBox.CreatePseudoConsole(size, inPipe, outPipe, 0, out hpcon);

            if (hr != NativeMethods.S_OK)
            {
                throw new InvalidOperationException(
                    $"Could not create pseudo console via {(oob ? "conpty.dll" : "kernel32")}: 0x{hr:x8}",
                    new Win32Exception(hr));
            }

            return new PseudoConsole(hpcon, oob);
        }

        /// <inheritdoc/>
        public void Resize(int cols, int rows)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);

            var size = new COORD { X = (short)cols, Y = (short)rows };
            int hr = this.outOfBand
                ? OutOfBand.ResizePseudoConsole(this.handle, size)
                : InBox.ResizePseudoConsole(this.handle, size);

            if (hr != NativeMethods.S_OK)
            {
                throw new InvalidOperationException(
                    $"Could not resize pseudo console: 0x{hr:x8}", new Win32Exception(hr));
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;

            if (this.handle == IntPtr.Zero)
            {
                return;
            }

            // Closed by the SAME implementation that created it. Crossing the two would hand
            // conpty.dll a handle allocated by conhost, or the reverse, which is undefined at best.
            if (this.outOfBand)
            {
                OutOfBand.ClosePseudoConsole(this.handle);
            }
            else
            {
                InBox.ClosePseudoConsole(this.handle);
            }

            this.handle = IntPtr.Zero;
        }

        /// <summary>
        /// ConPTY as shipped in the OS, backed by conhost.exe. Source-generated by CsWin32 from
        /// NativeMethods.txt; the raw pointer overloads are used rather than the SafeHandle-friendly
        /// ones so that both implementations here take and return the same plain IntPtr, and the two
        /// arms stay comparable.
        /// </summary>
        private static class InBox
        {
            internal static unsafe int CreatePseudoConsole(
                COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC)
            {
                HPCON hpcon;
                HRESULT hr = PInvoke.CreatePseudoConsole(size, (HANDLE)hInput, (HANDLE)hOutput, dwFlags, &hpcon);
                phPC = hpcon;
                return hr.Value;
            }

            internal static int ResizePseudoConsole(IntPtr hPC, COORD size) =>
                PInvoke.ResizePseudoConsole((HPCON)hPC, size).Value;

            internal static void ClosePseudoConsole(IntPtr hPC) => PInvoke.ClosePseudoConsole((HPCON)hPC);
        }

        /// <summary>
        /// ConPTY as shipped by the Microsoft.Windows.Console.ConPTY package, backed by
        /// OpenConsole.exe.
        ///
        /// <para><see cref="DefaultDllImportSearchPathsAttribute"/> with
        /// <see cref="DllImportSearchPath.AssemblyDirectory"/> is LOAD-BEARING, and leaving it off is
        /// silent rather than fatal. Recent Windows 11 builds ship their own
        /// <c>C:\Windows\System32\conpty.dll</c>, so an unqualified <c>DllImport("conpty.dll")</c> can
        /// resolve the OS copy — which is backed by conhost.exe, i.e. exactly the implementation this
        /// class exists to be an alternative to. Everything then works, nothing errors, and the
        /// "out-of-band" arm is quietly the in-box one.</para>
        ///
        /// <para>That is not hypothetical: it happened here. A ConPTY A/B across 32 samples on four
        /// contended Windows machines produced near-identical latencies, and a process census
        /// explained why — <c>OpenConsole.exe</c> peaked at ZERO on both arms, so both were conhost.
        /// The census is the only direct evidence of which implementation is live, because in-box
        /// spawns a conhost.exe per pseudoconsole and out-of-band spawns an OpenConsole.exe.</para>
        /// </summary>
        private static class OutOfBand
        {
            [DllImport("conpty.dll", EntryPoint = "CreatePseudoConsole")]
            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
            internal static extern int CreatePseudoConsole(
                COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

            [DllImport("conpty.dll", EntryPoint = "ResizePseudoConsole")]
            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
            internal static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

            [DllImport("conpty.dll", EntryPoint = "ClosePseudoConsole")]
            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
            internal static extern void ClosePseudoConsole(IntPtr hPC);
        }
    }
}
