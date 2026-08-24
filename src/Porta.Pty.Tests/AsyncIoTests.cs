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

        [TestInitialize]
        public void SkipUntilWindowsIsImplemented()
        {
            if (IsWindows)
            {
                // UseAsyncIo has no Windows implementation yet, so every promise this class makes is
                // knowingly false there. Worth being explicit rather than leaving it: the thread-cost
                // tests PASSED on Windows CI while the option did nothing, because the pool already
                // had threads to hand out. A test that passes for a reason unrelated to what it
                // claims is worse than one that fails.
                Assert.Inconclusive("UseAsyncIo is not implemented on Windows yet.");
            }
        }

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
        public async Task AsyncIo_ReportsTheRealExitCode()
        {
            // The shared reaper decodes the same wait status the per-connection watcher did, but by a
            // new route. A reaper that always reported 0 would satisfy every other test here.
            if (IsWindows)
            {
                Assert.Inconclusive("Unix-only: the shared reaper is the Unix waitpid path.");
                return;
            }

            using var cts = new CancellationTokenSource(TestTimeoutMs);
            using IPtyConnection terminal = await PtyProvider.SpawnAsync(Shell("AsyncExitCode", useAsyncIo: true), cts.Token);

            var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            terminal.ProcessExited += (_, e) => exited.TrySetResult(e.ExitCode);

            // Drain while waiting. An undrained pty eventually stops the child mid-write, and a
            // child stopped mid-write never reaches exit -- which looks exactly like a reaper that
            // never fired.
            var drain = ReadUntilAsync(terminal, "\u0000never\u0000", TimeSpan.FromSeconds(15));

            byte[] command = Encoding.UTF8.GetBytes("exit 42\n");
            await terminal.WriterStream.WriteAsync(command, 0, command.Length, cts.Token);

            var reported = await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(15), cts.Token));
            reported.Should().Be(exited.Task, "the reaper has to raise ProcessExited, not merely notice the exit");
            await drain;

            (await exited.Task).Should().Be(42, "the event must carry the child's real exit code");
            terminal.ExitCode.Should().Be(42);
            terminal.WaitForExit(5000).Should().BeTrue("WaitForExit must be satisfied by the shared reaper too");
        }

        [TestMethod]
        public async Task AsyncIo_ReportsExitCode_ForABlockingConnectionToo()
        {
            // The default path still uses its own watcher thread. Both routes decode the status, so
            // both are checked, or a change to one could silently diverge from the other.
            if (IsWindows)
            {
                Assert.Inconclusive("Unix-only: compares the two Unix waitpid routes.");
                return;
            }

            using var cts = new CancellationTokenSource(TestTimeoutMs);
            using IPtyConnection terminal = await PtyProvider.SpawnAsync(Shell("BlockingExitCode", useAsyncIo: false), cts.Token);

            var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            terminal.ProcessExited += (_, e) => exited.TrySetResult(e.ExitCode);

            var drain = ReadUntilAsync(terminal, "\u0000never\u0000", TimeSpan.FromSeconds(15));

            byte[] command = Encoding.UTF8.GetBytes("exit 42\n");
            terminal.WriterStream.Write(command, 0, command.Length);
            terminal.WriterStream.Flush();

            var reported = await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(15), cts.Token));
            reported.Should().Be(exited.Task);
            await drain;
            (await exited.Task).Should().Be(42);
        }

        [TestMethod]
        public async Task AsyncIo_SurvivesChurn_WithoutLeakingDescriptorsThreadsOrCpu()
        {
            // Three leaks are possible here and none of them announces itself. Descriptors: this
            // repo has already seen pty_spawn start failing with ENXIO after enough sessions came
            // and went, which reads as a system limit rather than a leak. Threads: a shared poller
            // and reaper are only shared if nothing per-session sneaks back in. CPU: a registration
            // left in the poll set after its child exits returns from every poll immediately,
            // because POLLHUP is reported whether or not it was asked for, and the loop spins.
            const int Rounds = 40;

            await WarmUpSharedThreadsAsync();
            using var cts = new CancellationTokenSource(TestTimeoutMs);

            int threadsBefore = Process.GetCurrentProcess().Threads.Count;
            TimeSpan cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
            var wall = Stopwatch.StartNew();

            for (var i = 0; i < Rounds; i++)
            {
                using IPtyConnection terminal = await PtyProvider.SpawnAsync(Shell($"Churn{i}", useAsyncIo: true), cts.Token);
                await ReadUntilAsync(terminal, "$", TimeSpan.FromSeconds(5));
                terminal.Kill();
                terminal.WaitForExit(2000);
            }

            // Idle afterwards, so what is measured below is the loop at rest rather than the work.
            await Task.Delay(2000, cts.Token);

            wall.Stop();
            int threadsAfter = Process.GetCurrentProcess().Threads.Count;
            TimeSpan cpuAfter = Process.GetCurrentProcess().TotalProcessorTime;

            double cpuRatio = (cpuAfter - cpuBefore).TotalMilliseconds / wall.Elapsed.TotalMilliseconds;
            Console.WriteLine(
                $"churn over {Rounds} sessions: threads {threadsBefore} -> {threadsAfter}, " +
                $"cpu {(cpuAfter - cpuBefore).TotalMilliseconds:F0}ms over {wall.Elapsed.TotalMilliseconds:F0}ms wall (ratio {cpuRatio:F2})");

            (threadsAfter - threadsBefore).Should().BeLessThan(
                5,
                "{0} sessions came and went; nothing should have accumulated", Rounds);

            cpuRatio.Should().BeLessThan(
                1.0,
                "a poll loop spinning on a hung-up descriptor would burn a core continuously");
        }

        [TestMethod]
        public async Task AsyncIo_ThreadCostDoesNotScaleWithSessionCount()
        {
            // The property worth pinning, and the one a comparison against blocking I/O does not
            // state: the cost is a CONSTANT -- one poller and one reaper for the process -- rather
            // than a smaller per-session number. Measured at two sizes because a per-session cost of
            // one thread and a constant cost of two are indistinguishable at a single size.
            await WarmUpSharedThreadsAsync();

            int small = await MeasureIdleThreadGrowthAsync(6, useAsyncIo: true);
            int large = await MeasureIdleThreadGrowthAsync(24, useAsyncIo: true);

            Console.WriteLine($"async idle thread growth: 6 sessions={small}, 24 sessions={large}");

            large.Should().BeLessThanOrEqualTo(
                small + 2,
                "quadrupling the session count must not multiply the thread count; the poller and reaper are shared");
            large.Should().BeLessThan(
                6,
                "24 idle sessions should cost a handful of threads at most, not one apiece");
        }

        [TestMethod]
        public async Task AsyncIo_CostsFewerThreadsPerIdleSession_ThanBlockingIo()
        {
            const int Sessions = 12;

            await WarmUpSharedThreadsAsync();

            int blocking = await MeasureIdleThreadGrowthAsync(Sessions, useAsyncIo: false);
            int asyncIo = await MeasureIdleThreadGrowthAsync(Sessions, useAsyncIo: true);

            Console.WriteLine($"idle thread growth for {Sessions} sessions: blocking={blocking} asyncIo={asyncIo}");

            asyncIo.Should().BeLessThan(
                blocking,
                "neither the reads nor the child watch should hold a thread per session");
        }

        /// <summary>
        /// Starts the shared poller and reaper before anything is measured.
        /// </summary>
        /// <remarks>
        /// They are created on first use, so without this the first measurement carries their two
        /// threads and reads as a per-session cost that is not one.
        /// </remarks>
        private static async Task WarmUpSharedThreadsAsync()
        {
            using var cts = new CancellationTokenSource(TestTimeoutMs);
            using IPtyConnection warm = await PtyProvider.SpawnAsync(Shell("WarmUp", useAsyncIo: true), cts.Token);
            await ReadUntilAsync(warm, "$", TimeSpan.FromSeconds(5));
            warm.Kill();
            warm.WaitForExit(5000);
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
                catch (IOException)
                {
                    // End of stream, on Linux. Reading a pty controller after the child exits gives
                    // EIO there and 0 on macOS, so the default blocking stream THROWS on one platform
                    // and returns cleanly on the other for the same event. NonBlockingPtyStream
                    // normalises EIO to 0; the default path does not, and this is the difference
                    // showing through. Not this branch's to fix, but worth knowing it exists.
                    break;
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
