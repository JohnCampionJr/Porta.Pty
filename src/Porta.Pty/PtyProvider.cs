// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Provides the ability to spawn new processes under a pseudoterminal.
    /// </summary>
    public static class PtyProvider
    {
        private static readonly TraceSource Trace = new TraceSource(nameof(PtyProvider));

        /// <summary>
        /// Gets the pseudoconsole implementation this process will use, for diagnostics and logging.
        ///
        /// <para>Worth exposing because the choice is no longer a simple reading of the environment:
        /// on Windows the default is out-of-band with an automatic fallback to in-box when conpty.dll
        /// is absent, so <c>PORTAPTY_CONPTY</c> does not tell you what is actually in play. Anything
        /// reporting the implementation from the environment variable will be wrong exactly when the
        /// fallback fires.</para>
        /// </summary>
        public static string PseudoConsoleImplementation
        {
            get
            {
                if (!OperatingSystem.IsWindows())
                {
                    return "posix";
                }

                // Same suppression and same reason as PlatformServices: the Windows types declare a
                // version floor that OperatingSystem.IsWindows() alone cannot assert, and reading a
                // static bool is harmless on any Windows.
#pragma warning disable CA1416
                return Windows.PseudoConsole.UseOutOfBand ? "oob" : "inbox";
#pragma warning restore CA1416
            }
        }

        /// <summary>
        /// Spawn a new process connected to a pseudoterminal.
        /// </summary>
        /// <param name="options">The set of options for creating the pseudoterminal.</param>
        /// <param name="cancellationToken">The token to cancel process creation early.</param>
        /// <returns>A <see cref="Task{IPtyConnection}"/> that completes once the process has spawned.</returns>
        public static Task<IPtyConnection> SpawnAsync(
            PtyOptions options,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(options.App))
            {
                throw new ArgumentNullException(nameof(options.App));
            }

            if (string.IsNullOrEmpty(options.Cwd))
            {
                throw new ArgumentNullException(nameof(options.Cwd));
            }

            if (options.CommandLine == null)
            {
                throw new ArgumentNullException(nameof(options.CommandLine));
            }

            if (options.Environment == null)
            {
                throw new ArgumentNullException(nameof(options.Environment));
            }

            IDictionary<string, string> environment = MergeEnvironment(PlatformServices.PtyEnvironment, null);
            environment = MergeEnvironment(options.Environment, environment);

            options.Environment = environment;

            return PlatformServices.PtyProvider.StartTerminalAsync(options, Trace, cancellationToken);
        }

        private static IDictionary<string, string> MergeEnvironment(IDictionary<string, string> enviromentToMerge, IDictionary<string, string>? environment)
        {
            if (environment == null)
            {
                environment = new Dictionary<string, string>(PlatformServices.EnvironmentVariableComparer);
                foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
                {
                    // DictionaryEntry is the pre-generics shape: Key is object and Value is object?,
                    // so both ToString() calls were unguarded. In practice the environment yields
                    // neither a null key nor a null value, which is exactly why this would have
                    // surfaced as a NullReferenceException from inside a foreach on a good day and
                    // never on a bad one.
                    if (entry.Key.ToString() is not { } key)
                    {
                        continue;
                    }

                    environment[key] = entry.Value?.ToString() ?? string.Empty;
                }
            }

            foreach (var kvp in enviromentToMerge)
            {
                if (string.IsNullOrEmpty(kvp.Value))
                {
                    environment.Remove(kvp.Key);
                }
                else
                {
                    environment[kvp.Key] = kvp.Value;
                }
            }

            return environment;
        }
    }
}
