// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Tests
{
    using System;
    using System.IO;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// The same claim as <see cref="StartupDa1FilterStreamTests"/>, made against a real pseudoconsole:
    /// ConPTY's startup Primary Device Attributes query does not reach the consumer.
    /// </summary>
    /// <remarks>
    /// <para>Worth having on top of the scripted tests because the scripted ones assume the shape of
    /// the startup burst, and this one does not. If out-of-band ConPTY ever asks differently — a
    /// parameterised <c>ESC[0c</c>, or the query moving out of the opening burst — the unit tests
    /// would go on passing against a burst that no longer happens, and this one would fail.</para>
    ///
    /// <para>In-box ConPTY never asks, so there is nothing to remove and nothing to assert; the test
    /// reports inconclusive rather than passing vacuously, since a silent pass is exactly how the A/B
    /// this fix came out of managed to measure conhost twice.</para>
    /// </remarks>
    [TestClass]
    public class StartupDa1FilterStreamIntegrationTests
    {
        private const string Da1Query = "\u001b[c";

        private const int ReadTimeoutMs = 15000;

        [TestMethod]
        public async Task TheStartupQueryNeverReachesTheConsumer()
        {
            if (!OperatingSystem.IsWindows())
            {
                Assert.Inconclusive("ConPTY, and so this query, is Windows-only.");
            }

            var implementation = PtyProvider.PseudoConsoleImplementation;

            if (implementation != "oob")
            {
                Assert.Inconclusive(
                    $"Only out-of-band ConPTY asks DA1 on startup; this process resolved '{implementation}'. " +
                    "conpty.dll and its OpenConsole.exe both have to be present for the arm under test to run.");
            }

            var options = new PtyOptions
            {
                Name = "cmd.exe",
                App = "cmd.exe",
                CommandLine = ["/c", "echo done"],
                Cols = 120,
                Rows = 30,
                Cwd = Environment.CurrentDirectory,
            };

            using var cts = new CancellationTokenSource(ReadTimeoutMs);
            using var connection = await PtyProvider.SpawnAsync(options, cts.Token);

            var output = await ReadUntilAsync(connection.ReaderStream, "done", cts.Token);

            output.Should().NotContain(
                Da1Query,
                "PtyProvider answers ConPTY's startup DA1 itself, so a consumer that is a terminal " +
                "emulator must not be asked it a second time — its reply would reach the child as input.");
        }

        [TestMethod]
        public async Task TheStartupQueryReachesAConsumerThatAnswersForItself()
        {
            // The mirror of the test above, and the reason the two halves are decided by one value.
            // A terminal emulator turns the answer off so it can advertise its own capabilities
            // instead of the canned "VT100 with Advanced Video Option" this library would claim --
            // which only works if the question reaches it.
            if (!OperatingSystem.IsWindows())
            {
                Assert.Inconclusive("ConPTY, and so this query, is Windows-only.");
            }

            var implementation = PtyProvider.PseudoConsoleImplementation;

            if (implementation != "oob")
            {
                Assert.Inconclusive(
                    $"Only out-of-band ConPTY asks DA1 on startup; this process resolved '{implementation}'.");
            }

            var options = new PtyOptions
            {
                Name = "cmd.exe",
                App = "cmd.exe",
                CommandLine = ["/c", "echo done"],
                Cols = 120,
                Rows = 30,
                Cwd = Environment.CurrentDirectory,
                AnswerDeviceAttributes = false,
            };

            using var cts = new CancellationTokenSource(ReadTimeoutMs);
            using var connection = await PtyProvider.SpawnAsync(options, cts.Token);

            var output = await ReadUntilAsync(connection.ReaderStream, Da1Query, cts.Token);

            output.Should().Contain(
                Da1Query,
                "a consumer that has taken the handshake on itself has to be able to see it asked.");
        }

        /// <summary>
        /// Reads until <paramref name="marker"/> shows up or the token fires, and returns everything
        /// seen. Bounded by the marker rather than by a byte count so the whole startup burst is
        /// certain to have been read.
        /// </summary>
        private static async Task<string> ReadUntilAsync(Stream stream, string marker, CancellationToken cancellationToken)
        {
            var buffer = new byte[4096];
            var seen = new StringBuilder();

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);

                    if (read <= 0)
                    {
                        break;
                    }

                    seen.Append(Encoding.UTF8.GetString(buffer, 0, read));

                    if (seen.ToString().Contains(marker, StringComparison.Ordinal))
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Whatever arrived before the timeout is still worth asserting on: the startup burst
                // is the first thing on the stream, so it is in there either way.
            }

            return seen.ToString();
        }
    }
}
