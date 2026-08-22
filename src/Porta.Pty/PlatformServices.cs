// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;
    using System.Runtime.Versioning;

    /// <summary>
    /// Provides platform specific functionality.
    /// </summary>
    internal static class PlatformServices
    {
        private static readonly IDictionary<string, string> WindowsPtyEnvironment = new Dictionary<string, string>();
        private static readonly IDictionary<string, string> UnixPtyEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "TERM", "xterm-256color" },

                // Make sure we didn't start our server from inside tmux.
            { "TMUX", string.Empty },
            { "TMUX_PANE", string.Empty },

                // Make sure we didn't start our server from inside screen.
                // http://web.mit.edu/gnu/doc/html/screen_20.html
            { "STY", string.Empty },
            { "WINDOW", string.Empty },

                // These variables that might confuse our terminal
            { "WINDOWID", string.Empty },
            { "TERMCAP", string.Empty },
            { "COLUMNS", string.Empty },
            { "LINES", string.Empty },
        };

        /// <remarks>
        /// Constructed here rather than through three Lazy fields. The providers hold no state and cost
        /// nothing to build, so the laziness bought nothing -- and a field initializer's lambda is
        /// somewhere the platform-compatibility analyzer cannot follow a guard into, which left the one
        /// Windows construction site reporting as unguarded even after the Windows types were annotated.
        /// Building inside the branch, with SupportedOSPlatformGuard on IsWindows below, states the
        /// invariant that was always true: only the matching platform's provider is ever constructed.
        /// </remarks>
        static PlatformServices()
        {
            if (IsWindows)
            {
                // CA1416 suppressed here and only here, deliberately.
                //
                // The Windows types declare windows10.0.17763 because that is when ConPTY appeared,
                // and IsWindows can only assert "some Windows" -- RuntimeInformation has no version
                // to offer. Adding OperatingSystem.IsWindowsVersionAtLeast would satisfy the analyzer
                // and make the behaviour WORSE: this runs in a static constructor, so throwing here
                // surfaces as a TypeInitializationException with the real message buried one level
                // down. Constructing the provider on an older Windows is harmless; it is the SPAWN
                // that cannot work, and StartTerminalAsync already gates on
                // NativeMethods.IsPseudoConsoleSupported and throws a PlatformNotSupportedException
                // that names the version required.
#pragma warning disable CA1416
                PtyProvider = new Windows.PtyProvider();
#pragma warning restore CA1416
                EnvironmentVariableComparer = StringComparer.OrdinalIgnoreCase;
                PtyEnvironment = WindowsPtyEnvironment;
            }
            else if (IsMac)
            {
                PtyProvider = new Mac.PtyProvider();
                EnvironmentVariableComparer = StringComparer.Ordinal;
                PtyEnvironment = UnixPtyEnvironment;
            }
            else if (IsLinux)
            {
                PtyProvider = new Linux.PtyProvider();
                EnvironmentVariableComparer = StringComparer.Ordinal;
                PtyEnvironment = UnixPtyEnvironment;
            }
            else
            {
                throw new PlatformNotSupportedException();
            }
        }

        /// <summary>
        /// Gets the <see cref="IPtyProvider"/> for the current platform.
        /// </summary>
        public static IPtyProvider PtyProvider { get; }

        /// <summary>
        /// Gets the comparer to determine if two environment variable keys are equivalent on the current platform.
        /// </summary>
        public static StringComparer EnvironmentVariableComparer { get; }

        /// <summary>
        /// Gets specific environment variables that are needed when spawning the PTY.
        /// </summary>
        public static IDictionary<string, string> PtyEnvironment { get; }

        private static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

        private static bool IsMac => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        /// <remarks>
        /// SupportedOSPlatformGuard is what lets the analyzer see that the Windows branch of the static
        /// constructor really is Windows-only. Without it the guard is just a bool to the analyzer.
        /// </remarks>
        [SupportedOSPlatformGuard("windows")]
        private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    }
}
