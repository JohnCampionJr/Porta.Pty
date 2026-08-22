// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty
{
    using System;
    using System.Reflection;
    using System.Runtime.InteropServices;

    /// <summary>
    /// Points this assembly's <c>conpty.dll</c> imports at the copy the library actually resolved.
    /// </summary>
    /// <remarks>
    /// <para>Without this, the imports resolve on their own through
    /// <see cref="DllImportSearchPath.AssemblyDirectory"/>, which finds <c>conpty.dll</c> only in the
    /// FLATTENED layout — the one a RID-specific build produces. A build with no RuntimeIdentifier keeps
    /// native assets under <c>runtimes/win-&lt;arch&gt;/native/</c> so it can run anywhere, and the pinned
    /// import cannot see them: out-of-band ConPTY silently became in-box conhost for every portable
    /// consumer, correct but not what was asked for.</para>
    ///
    /// <para>Ordinary P/Invoke handles the portable layout through deps.json. Ours cannot use that,
    /// because the pin is deliberate: Windows 11 ships its own conhost-backed
    /// <c>System32\conpty.dll</c>, and an unpinned <c>DllImport("conpty.dll")</c> will bind it happily —
    /// giving a working pty on exactly the implementation out-of-band exists to replace, with nothing
    /// reporting anything wrong. A resolver keeps that protection because it loads by ABSOLUTE PATH.</para>
    ///
    /// <para>Declining (returning <see cref="IntPtr.Zero"/>) falls back to the normal pinned behaviour
    /// rather than to a bare name, so the System32 copy stays unreachable in that case too.</para>
    /// </remarks>
    internal static class ConPtyImportResolver
    {
        /// <summary>
        /// Installs the resolver. Called from <c>PseudoConsole</c>'s static constructor.
        /// </summary>
        /// <remarks>
        /// Not a <c>[ModuleInitializer]</c>, which CA2255 rightly objects to in a library: it would run
        /// on assembly load for every consumer, including those that never open a pty and those on
        /// platforms where none of this applies. A static constructor is enough because every import it
        /// affects is reached through <c>PseudoConsole</c>, so the registration cannot be late.
        ///
        /// <see cref="NativeLibrary"/> permits one resolver per assembly and throws on a second call, so
        /// this must happen exactly once — which is what a static constructor guarantees.
        /// </remarks>
        internal static void Install()
        {
            NativeLibrary.SetDllImportResolver(typeof(ConPtyImportResolver).Assembly, Resolve);
        }

        private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            // Everything else in this assembly — the POSIX shim above all — must keep its default
            // resolution. A resolver is registered per ASSEMBLY, not per library.
            if (!string.Equals(libraryName, "conpty.dll", StringComparison.OrdinalIgnoreCase))
            {
                return IntPtr.Zero;
            }

            if (!OperatingSystem.IsWindows())
            {
                return IntPtr.Zero;
            }

            // Same suppression and same reason as PlatformServices: the Windows types declare a version
            // floor that OperatingSystem.IsWindows() alone cannot assert, and reading a static string is
            // harmless on any Windows.
#pragma warning disable CA1416
            var path = Windows.PseudoConsole.ConPtyPath;
#pragma warning restore CA1416

            if (path is null)
            {
                return IntPtr.Zero;
            }

            return NativeLibrary.TryLoad(path, out var handle) ? handle : IntPtr.Zero;
        }
    }
}
