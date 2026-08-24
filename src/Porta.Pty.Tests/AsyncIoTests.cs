// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Covers <see cref="PtyOptions.UseAsyncIo"/>.
    /// </summary>
    /// <remarks>
    /// The assertion that matters is the thread count one. Everything else here would pass just as
    /// well against the blocking path, because the blocking path is CORRECT -- it is only expensive.
    /// A suite that checked reads still work would have said nothing about whether the option does
    /// anything at all.
    /// </remarks>
    [TestClass]
    public class AsyncIoTests
    {
        private static readonly int TestTimeoutMs = Debugger.IsAttached ? 300_000 : 60_000;

        private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        private static PtyOptions Shell(string name, bool useAsyncIo)
        {
            return new PtyOptions
            {
                Name = name,
                Cols = 120,
                Rows = 25,
                Cwd = Environment.CurrentDirectory,
                App = IsWindows
                    ? Path.Combine(Environment.SystemDirectory, "cmd.exe")
                    : "/bin/sh",
                CommandLine = Array.Empty<string>(),
                Environment = new Dictionary<string, string>(),
                UseAsyncIo = useAsyncIo,
            };
        }

        [TestMethod]
        public async Task AsyncIo_RoundTripsACommand()
        {
            using var cts = new CancellationTokenSource(TestTimeoutMs);
            using IPtyConnection terminal = await PtyProvider.SpawnAsync(Shell("AsyncRoundTrip", useAsyncIo: true), cts.Token);

            byte[] command = Encoding.UTF8.GetBytes("echo ASYNC_MARKER\n");
            await terminal.WriterStream.WriteAsync(command, 0, command.Length, cts.Token);

            string output = await ReadUntilAsync(terminal, "ASYNC_MARKER", TimeSpan.FromSeconds(15));
            output.Should().Contain("ASYNC_MARKER");

            terminal.Kill();
            terminal.WaitForExit(5000);
        }

        [TestMethod]
        public async Task AsyncIo_ReportsEndOfStream_WhenTheChildExits()
        {
            // The read has to end by itself. A pending read that never completes is the failure mode
            // that turns "no thread held" into "session leaked".
            using var cts = new CancellationTokenSource(TestTimeoutMs);
            using IPtyConnection terminal = await PtyProvider.SpawnAsync(Shell("AsyncEof", useAsyncIo: true), cts.Token);

            byte[] command = Encoding.UTF8.GetBytes("exit\n");
            await terminal.WriterStream.WriteAsync(command, 0, command.Length, cts.Token);

            var buffer = new byte[4096];
            var stopwatch = Stopwatch.StartNew();
            int last = -1;
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(15))
            {
                last = await terminal.ReaderStream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
                if (last == 0)
                {
                    break;
                }
            }

            last.Should().Be(0, "a read must reach end of stream once the child is gone");
        }

        [TestMethod]
        public async Task AsyncIo_CancelsAPendingRead()
        {
            using var cts = new CancellationTokenSource(TestTimeoutMs);
            using IPtyConnection terminal = await PtyProvider.SpawnAsync(Shell("AsyncCancel", useAsyncIo: true), cts.Token);

            await ReadUntilAsync(terminal, "$", TimeSpan.FromSeconds(5));

            using var readCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var buffer = new byte[4096];

            Func<Task> read = async () => await terminal.ReaderStream.ReadAsync(buffer, 0, buffer.Length, readCts.Token);

            await read.Should().ThrowAsync<OperationCanceledException>(
                "a caller polling many idle sessions needs to be able to stop waiting on one");

            terminal.Kill();
            terminal.WaitForExit(5000);
        }

        [TestMethod]
        public async Task AsyncIo_CostsFewerThreadsPerIdleSession_ThanBlockingIo()
        {
            // The only test here that can fail against the blocking path, and the only one that says
            // anything about whether the option is worth having. Measured both ways in one process
            // so the numbers are comparable.
            const int Sessions = 12;

            int blocking = await MeasureIdleThreadGrowthAsync(Sessions, useAsyncIo: false);
            int asyncIo = await MeasureIdleThreadGrowthAsync(Sessions, useAsyncIo: true);

            Console.WriteLine($"idle thread growth for {Sessions} sessions: blocking={blocking} asyncIo={asyncIo}");

            asyncIo.Should().BeLessThan(
                blocking,
                "reads that wait on the shared poller should not each hold a thread");
        }

        private static async Task<int> MeasureIdleThreadGrowthAsync(int sessions, bool useAsyncIo)
        {
            using var cts = new CancellationTokenSource(TestTimeoutMs);
            var connections = new List<IPtyConnection>();

            try
            {
                // Settle first: the pool grows and shrinks on its own, and a measurement taken while
                // it is still reacting to the previous phase is noise.
                await Task.Delay(1500, cts.Token);
                int before = Process.GetCurrentProcess().Threads.Count;

                for (var i = 0; i < sessions; i++)
                {
                    connections.Add(await PtyProvider.SpawnAsync(Shell($"Idle{i}", useAsyncIo), cts.Token));
                }

                await Task.WhenAll(connections.Select(c => ReadUntilAsync(c, "$", TimeSpan.FromSeconds(5))));

                var pending = connections
                    .Select(c => c.ReaderStream.ReadAsync(new byte[256], 0, 256, cts.Token))
                    .ToArray();

                await Task.Delay(2500, cts.Token);
                int during = Process.GetCurrentProcess().Threads.Count;

                foreach (var connection in connections)
                {
                    connection.Kill();
                }

                await Task.WhenAny(Task.WhenAll(pending), Task.Delay(5000, cts.Token));
                return during - before;
            }
            finally
            {
                foreach (var connection in connections)
                {
                    connection.Dispose();
                }
            }
        }

        private static async Task<string> ReadUntilAsync(IPtyConnection terminal, string needle, TimeSpan timeout)
        {
            var buffer = new byte[4096];
            var output = new StringBuilder();
            var encoding = new UTF8Encoding(false);
            var stopwatch = Stopwatch.StartNew();

            while (stopwatch.Elapsed < timeout)
            {
                using var readCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
                int read;
                try
                {
                    read = await terminal.ReaderStream.ReadAsync(buffer, 0, buffer.Length, readCts.Token);
                }
                catch (OperationCanceledException)
                {
                    continue;
                }

                if (read == 0)
                {
                    break;
                }

                output.Append(encoding.GetString(buffer, 0, read));
                if (output.ToString().Contains(needle))
                {
                    break;
                }
            }

            return output.ToString();
        }
    }
}
