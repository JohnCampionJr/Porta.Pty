// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Windows
{
    using System;
    using System.ComponentModel;
    using System.Runtime.InteropServices;
    using System.Runtime.Versioning;
    // global:: is required, not stylistic. This namespace is Porta.Pty.WINDOWS, so an unqualified
    // `using Windows.Win32` binds relative to it and looks for Porta.Pty.Windows.Windows.Win32.
    // The alternative is moving the usings outside the namespace block, which is what Sylinko's fork
    // does by using file-scoped namespaces; keeping them inside preserves this file's upstream shape.
    using global::Windows.Win32;
    using global::Windows.Win32.System.Threading;

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
    internal static class NativeMethods
    {
        public const int S_OK = 0;

        /// <summary>
        /// ProcThreadAttributePseudoConsole (22) | PROC_THREAD_ATTRIBUTE_INPUT (0x20000).
        /// Spelled out rather than taken from a generated enum: this is a Windows 10 1809 addition and
        /// neither Vanara's PROC_THREAD_ATTRIBUTE nor CsWin32's projection carries it.
        /// </summary>
        private const nuint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x20016;

        /// <summary>
        /// Whether this Windows build has ConPTY at all. Probed by looking for the entry point rather
        /// than by version number: a version check would have to be maintained, and this cannot be
        /// wrong. NativeLibrary replaces the LoadLibrary/GetProcAddress pair the Vanara version used,
        /// so neither needs generating.
        /// </summary>
        private static readonly Lazy<bool> IsPseudoConsoleSupportedLazy = new Lazy<bool>(
            () => NativeLibrary.TryLoad("kernel32.dll", out IntPtr kernel32)
                  && NativeLibrary.TryGetExport(kernel32, "CreatePseudoConsole", out _),
            isThreadSafe: true);

        internal static bool IsPseudoConsoleSupported => IsPseudoConsoleSupportedLazy.Value;

        /// <summary>
        /// Builds the process-thread attribute list that attaches a spawned process to a pseudoconsole.
        /// </summary>
        /// <param name="startupInfo">The startup info to populate.</param>
        /// <param name="pseudoConsoleHandle">
        /// The raw HPCON. Raw rather than a typed handle because the pseudoconsole may come from either
        /// ConPTY implementation, and only the in-box one produces a handle type the generated interop
        /// knows about. The attribute value was always a pointer-sized blob underneath.
        /// </param>
        internal static unsafe void InitAttributeListAttachedToConPTY(
            ref this STARTUPINFOEXW startupInfo, IntPtr pseudoConsoleHandle)
        {
            startupInfo.StartupInfo.cb = (uint)Marshal.SizeOf<STARTUPINFOEXW>();
            startupInfo.StartupInfo.dwFlags = STARTUPINFOW_FLAGS.STARTF_USESTDHANDLES;

            const uint AttributeCount = 1;
            nuint size = 0;

            // The first call is EXPECTED to fail; it is how the required size is obtained. Succeeding
            // here, or reporting a zero size, means something is wrong rather than that there is
            // nothing to do.
            PInvoke.InitializeProcThreadAttributeList(default, AttributeCount, 0, &size);
            if (size == 0)
            {
                throw new InvalidOperationException(
                    $"Couldn't get the size of the process attribute list for {AttributeCount} attributes",
                    new Win32Exception());
            }

            var list = (LPPROC_THREAD_ATTRIBUTE_LIST)(void*)Marshal.AllocHGlobal((int)size);
            startupInfo.lpAttributeList = list;

            if (!PInvoke.InitializeProcThreadAttributeList(list, AttributeCount, 0, &size))
            {
                Marshal.FreeHGlobal((IntPtr)(void*)list);
                startupInfo.lpAttributeList = default;
                throw new InvalidOperationException(
                    "Couldn't create new process attribute list", new Win32Exception());
            }

            if (!PInvoke.UpdateProcThreadAttribute(
                    list,
                    0,
                    PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    (void*)pseudoConsoleHandle,
                    (nuint)IntPtr.Size,
                    null,
                    null))
            {
                // Capture the error BEFORE freeing: DeleteProcThreadAttributeList and FreeHGlobal both
                // clobber the last-error value, so constructing the exception afterwards reports the
                // cleanup's success instead of the failure being reported.
                var failure = new Win32Exception();
                startupInfo.FreeAttributeList();
                throw new InvalidOperationException("Couldn't update process attribute list", failure);
            }
        }

        internal static unsafe void FreeAttributeList(ref this STARTUPINFOEXW startupInfo)
        {
            if (startupInfo.lpAttributeList != default)
            {
                PInvoke.DeleteProcThreadAttributeList(startupInfo.lpAttributeList);
                Marshal.FreeHGlobal((IntPtr)(void*)startupInfo.lpAttributeList);
                startupInfo.lpAttributeList = default;
            }
        }
    }
}
