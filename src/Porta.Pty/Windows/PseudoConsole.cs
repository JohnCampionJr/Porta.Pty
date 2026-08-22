// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Windows
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Runtime.InteropServices;
    using System.IO;
    using System.Diagnostics.CodeAnalysis;
    using System.Reflection;
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
        /// Initializes static members of the <see cref="PseudoConsole"/> class.
        /// </summary>
        /// <remarks>
        /// Exists solely to install the import resolver, and it must run before any conpty.dll import
        /// does. Every one of them is reached through this type, so this is the earliest point that is
        /// also guaranteed to be reached — and, being a static constructor, to be reached once.
        /// </remarks>
        static PseudoConsole()
        {
            ConPtyImportResolver.Install();

            // Assigned HERE, in dependency order, rather than by field initializer. Static initializers
            // run in DECLARATION order, so UseOutOfBand — declared first, because it is the one callers
            // care about — would have read a still-null ConPtyPath and reported in-box unconditionally.
            // Nothing about that would have looked wrong: in-box is a legitimate answer, and the tests
            // that exercise this path force the mode explicitly.
            ConPtyPath = ResolveConPtyPath();
            OutOfBandHostPresent = ResolveHostPresent();
            UseOutOfBand = ResolvePreference();
        }

        /// <summary>
        /// Gets a value indicating whether the out-of-band <c>conpty.dll</c> is selected.
        ///
        /// <para>Out-of-band is the DEFAULT, and falls back to in-box when <c>conpty.dll</c> is not
        /// beside the assembly. That fallback is what makes the default safe: a consumer that has not
        /// referenced the ConPTY package gets the in-box implementation rather than a
        /// <see cref="DllNotFoundException"/> on its first terminal.</para>
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
        /// <para>And the directory probed is the one the IMPORTS resolve against, not
        /// <see cref="AppContext.BaseDirectory"/>. Those are the same path for an ordinary application
        /// and differ for a plugin or a custom load context, where this library sits somewhere other
        /// than the host's base directory. Probing the wrong one is wrong in both directions: it can
        /// report in-box while <c>conpty.dll</c> sits beside the assembly, or report out-of-band and
        /// then fail the actual import. The base directory is still probed as a fallback, because
        /// <see cref="Assembly.Location"/> is empty under single-file and native AOT.</para>
        ///
        /// <para>Read once: the answer cannot change within a process, and a per-spawn probe on the
        /// terminal-creation path buys nothing.</para>
        /// </summary>
        public static bool UseOutOfBand { get; }

        /// <summary>
        /// Gets the absolute path of the <c>conpty.dll</c> this process will use, or
        /// <see langword="null"/> when there isn't one.
        /// </summary>
        /// <remarks>
        /// Resolved ONCE and used for both the availability answer and the actual load, via the import
        /// resolver below. Those used to be separate lookups that could disagree — the probe read
        /// <see cref="AppContext.BaseDirectory"/> while the imports resolved against the assembly
        /// directory — and a probe that can disagree with the load is worse than no probe, because it
        /// reports confidently in both wrong directions.
        /// </remarks>
        public static string? ConPtyPath { get; }

        private static bool ResolvePreference()
        {
            var preference = Environment.GetEnvironmentVariable("PORTAPTY_CONPTY");

            if (string.Equals(preference, "inbox", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.Equals(preference, "oob", StringComparison.OrdinalIgnoreCase))
            {
                // Deliberately does not check availability: a consumer that believes it is set up would
                // rather have the load throw than be quietly downgraded.
                return true;
            }

            // BOTH halves, not just conpty.dll. Loading conpty.dll without its OpenConsole.exe does not
            // fail and does not warn — it falls back to conhost internally — so selecting it on the
            // strength of the DLL alone buys nothing over the in-box path and costs the ability to say
            // which one ran. Taking in-box deliberately in that case keeps the two arms genuinely
            // distinct, which is what makes any measurement of them mean anything.
            return ConPtyPath is not null && OutOfBandHostPresent;
        }

        private static string? ResolveConPtyPath()
        {
            foreach (var directory in ProbeDirectories())
            {
                try
                {
                    var candidate = Path.Combine(directory, "conpty.dll");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (Exception)
                {
                    // An unreadable candidate directory is not an answer about the others.
                }
            }

            return null;
        }

        /// <summary>
        /// Gets a value indicating whether the out-of-band host <c>OpenConsole.exe</c> is present.
        /// </summary>
        /// <remarks>
        /// <para>Separate from <see cref="UseOutOfBand"/>, and the distinction is the whole point.
        /// <c>conpty.dll</c> loads and works with no host beside it — it silently falls back to conhost.
        /// So "we selected conpty.dll" and "we are actually running out-of-band" are different claims,
        /// and reporting the first as the second produces the false A/B this class was written to
        /// measure: out-of-band against in-box with both arms conhost, agreeing beautifully.</para>
        ///
        /// <para>Looked for beside the RESOLVED <c>conpty.dll</c>, not beside the app, because that is
        /// how the two travel: the package stages the hosts into arch subdirectories of whatever
        /// directory <c>conpty.dll</c> lands in.</para>
        ///
        /// <para>Per-PROCESS architecture, not per-machine: an x64 process on ARM64 Windows runs under
        /// emulation and needs the x64 host, which is why both are shipped.</para>
        ///
        /// <para>Necessary, not sufficient — it says the file is where the loader will look, not that
        /// <c>conpty.dll</c> launched it. Only a process census proves that, which is what
        /// <c>scripts/Verify-ConPtyHost.ps1</c> does.</para>
        /// </remarks>
        public static bool OutOfBandHostPresent { get; }

        private static bool ResolveHostPresent()
        {
            var architecture = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X86 => "x86",
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                _ => null,
            };

            if (architecture is null || ConPtyPath is null)
            {
                return false;
            }

            try
            {
                var directory = Path.GetDirectoryName(ConPtyPath);
                return directory is not null
                    && File.Exists(Path.Combine(directory, architecture, "OpenConsole.exe"));
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Gets the directory containing this assembly, or <see langword="null"/> when there isn't one.
        /// </summary>
        /// <remarks>
        /// Split out so the IL3000 suppression covers only this, and so it is not applied to an iterator
        /// (where it would sit on the method rather than the generated state machine doing the work).
        /// The warning's advice — use <see cref="AppContext.BaseDirectory"/> — is what the caller falls
        /// back to; it cannot be the only answer, because the imports resolve against the ASSEMBLY
        /// directory and the two differ for a plugin or a custom load context.
        /// </remarks>
        [UnconditionalSuppressMessage(
            "SingleFile",
            "IL3000:Avoid accessing Assembly file path when publishing as a single file",
            Justification = "An empty Location is the single-file and AOT case, and is handled: the " +
                            "caller falls back to AppContext.BaseDirectory. Verified by publishing the " +
                            "sample with PublishAot on win-arm64 and win-x64 — both spawn correctly.")]
        private static string? AssemblyDirectory()
        {
            try
            {
                var location = typeof(PseudoConsole).Assembly.Location;
                return string.IsNullOrEmpty(location) ? null : Path.GetDirectoryName(location);
            }
            catch (Exception)
            {
                // Some hosts refuse Location outright; the base directory is the fallback either way.
                return null;
            }
        }

        /// <summary>
        /// Gets the directories that may contain <c>conpty.dll</c>, in preference order.
        /// </summary>
        /// <remarks>
        /// <para>The first two are the flattened layout a RID-specific build produces. The assembly's
        /// own directory leads because that is what the imports would resolve against unaided, and the
        /// base directory follows because <see cref="Assembly.Location"/> is empty under single-file and
        /// native AOT.</para>
        ///
        /// <para>The last two are the PORTABLE layout, and they are why a RID is no longer required. A
        /// build with no RuntimeIdentifier cannot flatten native assets — it has to be able to run
        /// anywhere — so it keeps the whole <c>runtimes/</c> tree and lets the host pick at run time.
        /// Ordinary P/Invoke handles that through deps.json, but ours cannot: the imports are pinned to
        /// <see cref="DllImportSearchPath.AssemblyDirectory"/>, deliberately, because Windows 11 ships
        /// its own conhost-backed <c>System32\conpty.dll</c> that an unpinned import will happily bind.
        /// Looking in <c>runtimes/win-&lt;arch&gt;/native/</c> ourselves gets the portable layout back
        /// without giving up that protection, since every load here is by absolute path.</para>
        /// </remarks>
        private static IEnumerable<string> ProbeDirectories()
        {
            var assemblyDirectory = AssemblyDirectory();
            var baseDirectory = AppContext.BaseDirectory;

            if (!string.IsNullOrEmpty(assemblyDirectory))
            {
                yield return assemblyDirectory!;
            }

            if (!string.IsNullOrEmpty(baseDirectory) && !SameDirectory(baseDirectory, assemblyDirectory))
            {
                yield return baseDirectory;
            }

            var rid = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X86 => "win-x86",
                Architecture.X64 => "win-x64",
                Architecture.Arm64 => "win-arm64",
                _ => null,
            };

            if (rid is null)
            {
                yield break;
            }

            var relative = Path.Combine("runtimes", rid, "native");

            if (!string.IsNullOrEmpty(assemblyDirectory))
            {
                yield return Path.Combine(assemblyDirectory!, relative);
            }

            if (!string.IsNullOrEmpty(baseDirectory) && !SameDirectory(baseDirectory, assemblyDirectory))
            {
                yield return Path.Combine(baseDirectory, relative);
            }
        }

        private static bool SameDirectory(string? left, string? right) =>
            string.Equals(
                left?.TrimEnd(Path.DirectorySeparatorChar),
                right?.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);

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
