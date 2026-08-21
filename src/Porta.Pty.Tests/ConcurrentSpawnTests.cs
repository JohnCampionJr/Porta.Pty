// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Tests
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Reproduction for the defect this fork was created to chase: on Windows, a process that
    /// exits almost immediately sometimes delivers NONE of its output, while the connection still
    /// reports a clean exit code of 0. Downstream (the downstream consumer's run targets) that surfaces as a dev
    /// server whose console is empty but whose status says it ran and stopped normally — the worst
    /// possible shape, because nothing anywhere reports an error.
    ///
    /// Downstream measurement, 24 short-lived processes launched concurrently:
    ///
    ///   | build                       | Linux      | Windows           |
    ///   |-----------------------------|------------|-------------------|
    ///   | before the consumer's fixes | 23/24 lost | 21/24 lost        |
    ///   | after them                  | 0/24 (x5)  | 10, 0, 6, 0 of 24 |
    ///
    /// So the consumer's own races are fixed and Windows still loses output. These two tests ask
    /// the same question one layer down, of the library itself, with no consumer code involved. A
    /// failure here proves the defect is in the PTY layer; a pass proves it is not, and both
    /// answers are worth having.
    ///
    /// The two tests are deliberately different KINDS of question:
    ///
    ///   * <see cref="OutputSurvivesTheProcessExiting_WhenTheReaderAttachesAfterwards"/> is
    ///     mechanistic and single-process. It asks whether ConPTY holds a dead child's output for
    ///     a reader that shows up late. If this one fails, we have the mechanism outright and no
    ///     statistics are needed.
    ///   * <see cref="ShortLivedProcesses_SpawnedConcurrently_AllDeliverTheirOutput"/> is the
    ///     load-shaped one. Concurrency is what gives it teeth: downstream, the SEQUENTIAL version
    ///     of this same assertion passed 30 times in a row against code that was provably broken,
    ///     because the spawn and the exit almost never interleave when nothing else is competing
    ///     for the machine. Do not "simplify" it back into a loop.
    /// </summary>
    [TestClass]
    public class ConcurrentSpawnTests
    {

        private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        private static string ShellApp => IsWindows
            ? Path.Combine(Environment.SystemDirectory, "cmd.exe")
            : "/bin/sh";

        /// <summary>
        /// Gets how many processes to launch at once. Overridable so a CI leg can crank it up
        /// without a code change while we are narrowing this down.
        /// </summary>
        private static int SpawnCount =>
            int.TryParse(Environment.GetEnvironmentVariable("PORTAPTY_REPRO_SPAWNS"), out var n) && n > 0
                ? n
                : 24;

        // Four named facts rather than one Theory with four rows, so CI can put each combination in
        // its OWN dotnet test process. That is not fussiness: the hypothesis under test is thread-pool
        // starvation, and the pool is per-process and never shrinks on this timescale. The first
        // 24-spawn case forces the pool to grow to ~24 threads, and every later case in the same
        // process then runs against a warm pool that cannot starve. Sharing a process would hide the
        // very effect being measured.
        [TestMethod]
        public void Concurrent_MinimalShell_DedicatedThreadReader()
            => RunConcurrent(ShellMode.Minimal, ReaderMode.DedicatedThread, assert: true);

        /// <remarks>
        /// The pooled arms MEASURE; they do not assert. Asserting that they deliver would be asserting
        /// that a pattern we have proven broken nevertheless works, and it would fail for the right
        /// reason on exactly the machines where the finding is most worth having. Under MTP on a
        /// 4-vCPU Windows runner this arm delivers ZERO of 24 -- which is the point, not a regression.
        /// The library's contract is what the DedicatedThread arms assert; these two record what the
        /// bad pattern costs so a future change to it is visible in the numbers.
        /// </remarks>
        [TestMethod]
        public void Measure_MinimalShell_PoolAsyncReader()
            => RunConcurrent(ShellMode.Minimal, ReaderMode.PoolAsync, assert: false);

        [TestMethod]
        public void Concurrent_RealisticShell_DedicatedThreadReader()
            => RunConcurrent(ShellMode.Realistic, ReaderMode.DedicatedThread, assert: true);

        /// <inheritdoc cref="Measure_MinimalShell_PoolAsyncReader"/>
        [TestMethod]
        public void Measure_RealisticShell_PoolAsyncReader()
            => RunConcurrent(ShellMode.Realistic, ReaderMode.PoolAsync, assert: false);

        private void RunConcurrent(ShellMode mode, ReaderMode reader, bool assert)
        {
            int count = SpawnCount;

            // Two bags, not one. The first winrunner run of this test reported "1 of 96 did not
            // deliver their output" and the cause was a spawn that THREW -- a loud, entirely
            // different defect wearing the silent one's label. Counting them together is how a
            // repro lies to you about what it reproduced.
            var lostOutput = new ConcurrentBag<string>();
            var spawnFailed = new ConcurrentBag<string>();

            // Pass/fail is the wrong instrument for this. On the winrunners every combination passed,
            // and the PoolAsync ones took ~10 seconds longer than the dedicated-thread ones doing
            // identical work -- the starvation is plainly there, it just did not cross a 20s budget
            // with the box otherwise idle. Latency is the measurement; the assertion is only the
            // alarm for when latency runs past the budget entirely.
            var latencies = new ConcurrentBag<long>();

            // Every run gets its OWN thread rather than a thread-pool slot. With a pool, 24 runs on
            // a 4-vCPU runner would be serialised by the pool's injection rate — which both hides
            // the race we are hunting and makes any failure ambiguous between "output was lost" and
            // "this run never got a CPU". A dedicated thread makes the concurrency real.
            var threads = new List<Thread>(count);
            using var barrier = new Barrier(count);

            for (int i = 0; i < count; i++)
            {
                int index = i;
                var t = new Thread(() =>
                {
                    try
                    {
                        // Line every run up and release them together, so the spawns actually
                        // overlap instead of trickling out as the threads happen to start.
                        barrier.SignalAndWait();
                        var (kind, detail, ms) = RunOnceAsync(index, mode, reader, TimeSpan.FromSeconds(20))
                            .GetAwaiter().GetResult();
                        if (kind == Outcome.Delivered)
                        {
                            latencies.Add(ms);
                        }

                        if (kind == Outcome.LostOutput)
                        {
                            lostOutput.Add(detail!);
                        }
                        else if (kind == Outcome.SpawnFailed)
                        {
                            spawnFailed.Add(detail!);
                        }
                    }
                    catch (Exception ex)
                    {
                        spawnFailed.Add($"#{index:D2} threw {ex.GetType().Name}: {ex.Message}");
                    }
                })
                {
                    IsBackground = true,
                    Name = $"pty-repro-{i}",
                };
                threads.Add(t);
                t.Start();
            }

            foreach (var t in threads)
            {
                t.Join(TimeSpan.FromSeconds(120)).Should().BeTrue("thread {0} never finished", t.Name);
            }

            int delivered = count - lostOutput.Count - spawnFailed.Count;
            var sorted = latencies.OrderBy(x => x).ToArray();
            long Pct(double q) => sorted.Length == 0
                ? -1
                : sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))];

            Record(
                $"SUMMARY conpty={ConPtyImplementation} mode={mode} reader={reader} spawns={count} "
                + $"delivered={delivered} lostOutput={lostOutput.Count} spawnFailed={spawnFailed.Count} "
                + $"p50ms={Pct(0.50)} p90ms={Pct(0.90)} maxms={(sorted.Length == 0 ? -1 : sorted[^1])}");
            foreach (var f in lostOutput.Concat(spawnFailed).OrderBy(f => f))
            {
                Record(f);
            }

            if (!assert)
            {
                return;
            }

            (lostOutput.IsEmpty && spawnFailed.IsEmpty).Should().BeTrue(
                "mode={0} reader={1}: of {2} concurrent short-lived processes, {3} LOST OUTPUT and "
                + "{4} FAILED TO SPAWN:{5}{6}",
                mode,
                reader,
                count,
                lostOutput.Count,
                spawnFailed.Count,
                Environment.NewLine,
                string.Join(Environment.NewLine, lostOutput.Concat(spawnFailed).OrderBy(f => f)));
        }

        /// <summary>
        /// Spawns SEQUENTIALLY and reports the first pseudoconsole separately from the rest, which is
        /// the one thing the concurrent tests cannot show.
        ///
        /// <para>The out-of-band ConPTY measured ~3.0 s to stand up a pseudoconsole at BOTH n=1 and
        /// n=24. Two models fit that equally well: a fixed cost paid once per process, or a fixed cost
        /// per pseudoconsole that twenty-four of them pay concurrently. Concurrent spawns cannot tell
        /// them apart, because under either model all twenty-four land together at ~3 s.</para>
        ///
        /// <para>It matters because the two have different answers. A once-per-process cost can be hidden
        /// behind a warm-up at startup and then never noticed. A per-pseudoconsole cost is paid by every
        /// terminal a user opens, forever, and cannot be hidden at all.</para>
        ///
        /// <para>Sequential spawns separate them: under the per-process model the first is slow and the
        /// rest are fast, and under the per-pseudoconsole model they are all slow. The full ordered list
        /// is reported rather than just an average, because a gradual decay would mean something
        /// different again from a clean step.</para>
        /// </summary>
        [TestMethod]
        public void Sequential_ColdVersusWarm()
        {
            int count = int.TryParse(Environment.GetEnvironmentVariable("PORTAPTY_SEQUENCE"), out var n) && n > 1
                ? n
                : 8;

            // Minimal shell deliberately: PowerShell's own ~1.5 s startup would swamp the effect being
            // measured, and this test is about the pseudoconsole rather than about what runs inside it.
            var elapsed = new List<long>(count);
            for (int i = 0; i < count; i++)
            {
                var (kind, detail, ms) = RunOnceAsync(
                        i, ShellMode.Minimal, ReaderMode.DedicatedThread, TimeSpan.FromSeconds(20))
                    .GetAwaiter().GetResult();

                kind.Should().Be(Outcome.Delivered, "sequential spawn #{0} did not deliver: {1}", i, detail);
                elapsed.Add(ms);
            }

            var rest = elapsed.Skip(1).OrderBy(x => x).ToArray();
            Record(
                $"SEQUENCE conpty={ConPtyImplementation} n={count} first={elapsed[0]}ms "
                + $"rest_p50={rest[rest.Length / 2]}ms rest_min={rest[0]}ms rest_max={rest[^1]}ms "
                + $"all=[{string.Join(",", elapsed)}]");
        }

        /// <summary>
        /// The same cold/warm sequence, but with a child that STAYS ALIVE after it writes.
        ///
        /// <para>Every other measurement here uses a child that exits immediately, and out-of-band
        /// ConPTY costs a flat ~3.01 s for those — flat to within 5 ms across eight spawns, which is a
        /// timeout rather than work. A process census showed OpenConsole.exe appearing immediately
        /// rather than 2.9 s in, so the wait is not process creation; it is something after the host
        /// already exists.</para>
        ///
        /// <para>That leaves two candidates with very different consequences. If the wait is a
        /// connect/handshake, a long-lived child stalls too. If it is tied to the child EXITING —
        /// output only surfacing when the pseudoconsole tears down — a long-lived child is fast, and
        /// the stall never touches the case the downstream consumer actually cares about, since terminal panes are
        /// long-lived by definition.</para>
        ///
        /// <para>the downstream consumer's run targets are the other consumer, and dev servers are long-lived too. A
        /// fast result here would mean the 3 s applies only to fire-and-exit commands.</para>
        /// </summary>
        [TestMethod]
        public void Sequential_LongLivedChild_ColdVersusWarm()
        {
            int count = int.TryParse(Environment.GetEnvironmentVariable("PORTAPTY_SEQUENCE"), out var n) && n > 1
                ? n
                : 5;

            var elapsed = new List<long>(count);
            for (int i = 0; i < count; i++)
            {
                string marker = NewMarker(i);

                // Echo, then linger. `ping -n` rather than `timeout`, which needs a real console and
                // fails under a redirected one; `sleep` on POSIX.
                string command = IsWindows
                    ? $"echo {marker} & ping -n 6 127.0.0.1 > nul"
                    : $"echo {marker}; sleep 5";

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var clock = Stopwatch.StartNew();

                using IPtyConnection terminal = PtyProvider
                    .SpawnAsync(ShellCommand($"long-{i}", command, ShellMode.Minimal), cts.Token)
                    .GetAwaiter().GetResult();

                var capture = new Capture(terminal, marker, ReaderMode.DedicatedThread);
                bool arrived = capture.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
                clock.Stop();

                arrived.Should().BeTrue(
                    "a long-lived child's output must arrive while it is still running; captured={0}",
                    Escape(capture.Text));

                elapsed.Add(clock.ElapsedMilliseconds);

                // Kill rather than wait it out: the point was the FIRST output, not the lifetime.
                try
                {
                    terminal.Kill();
                }
                catch (Exception)
                {
                    // Already gone.
                }
            }

            var rest = elapsed.Skip(1).OrderBy(x => x).ToArray();
            Record(
                $"LONGLIVED conpty={ConPtyImplementation} n={count} first={elapsed[0]}ms "
                + $"rest_p50={rest[rest.Length / 2]}ms all=[{string.Join(",", elapsed)}]");
        }

        /// <summary>
        /// Dumps every byte the pseudoconsole emits, with the time it arrived, for both ConPTY
        /// implementations. Diagnostic rather than assertive — it exists to identify the ~3.01 s
        /// out-of-band stall, not to guard anything.
        ///
        /// <para>The hypothesis it tests: ConPTY emits terminal QUERIES on startup — Device Attributes,
        /// or a cursor-position report — and waits for the terminal to answer. This harness only ever
        /// READS; it never writes anything back. A query that never gets a reply, abandoned after a
        /// fixed timeout, would be flat, paid once per pseudoconsole, independent of what the child
        /// does and of whether the child is still alive. That is every property the stall has.</para>
        ///
        /// <para>If out-of-band emits an escape sequence at t≈0 and the child's own output only appears
        /// at t≈3000, the sequence it emitted is the question being waited on.</para>
        /// </summary>
        [TestMethod]
        public void Diagnostic_DumpEarlyOutputWithTimings()
        {
            string marker = NewMarker(0);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            var clock = Stopwatch.StartNew();
            using IPtyConnection terminal = PtyProvider
                .SpawnAsync(ShellCommand("dump", $"echo {marker}", ShellMode.Minimal), cts.Token)
                .GetAwaiter().GetResult();

            var chunks = new List<string>();
            var done = new ManualResetEventSlim(false);
            var thread = new Thread(() =>
            {
                var bytes = new byte[4096];
                var encoding = new UTF8Encoding(false);
                try
                {
                    while (true)
                    {
                        int read = terminal.ReaderStream.Read(bytes, 0, bytes.Length);
                        if (read <= 0)
                        {
                            break;
                        }

                        lock (chunks)
                        {
                            chunks.Add($"{clock.ElapsedMilliseconds,6}ms {Escape(encoding.GetString(bytes, 0, read))}");
                        }
                    }
                }
                catch
                {
                    // Torn down; the dump below reports whatever arrived.
                }
                finally
                {
                    done.Set();
                }
            })
            {
                IsBackground = true,
                Name = "pty-dump",
            };
            thread.Start();

            // Fixed window rather than "until the marker": the point is to see EVERYTHING that arrives
            // and when, including anything emitted before the child's own output.
            Thread.Sleep(5_000);

            Record($"DUMP conpty={ConPtyImplementation} marker={marker} chunks={chunks.Count}");
            lock (chunks)
            {
                foreach (var c in chunks)
                {
                    Record($"DUMP   {c}");
                }
            }
        }

        /// <summary>
        /// Guards the thing that makes out-of-band ConPTY usable at all: the LIBRARY answers the
        /// pseudoconsole's startup handshake, so a consumer that only reads does not stall.
        ///
        /// <para>ConPTY's <c>VtIo::StartIfNeeded</c> sends a Primary Device Attributes query and then
        /// blocks in <c>WaitUntilDA1(3000)</c> for the reply. Nothing in this test writes to the
        /// terminal — that is the point. Measured on Windows, sequential spawns:
        /// <c>[3016,3012,3011,3013,3019]</c> before <see cref="PtyProvider"/> answered, and
        /// <c>[15,9,9,8,8]</c> after.</para>
        ///
        /// <para>The ceiling is 1.5 s against a real cost of ~10 ms, so it is nowhere near a load-
        /// sensitive threshold; it exists to catch the answer going missing, which costs exactly three
        /// seconds and nothing else. It holds on in-box too, which never asks, and on POSIX.</para>
        /// </summary>
        [TestMethod]
        public void TheLibraryAnswersTheStartupHandshake_SoAReadOnlyConsumerDoesNotStall()
        {
            int count = int.TryParse(Environment.GetEnvironmentVariable("PORTAPTY_SEQUENCE"), out var n) && n > 1
                ? n
                : 5;

            var elapsed = new List<long>(count);
            for (int i = 0; i < count; i++)
            {
                var (kind, detail, ms) = RunOnceAsync(
                        i, ShellMode.Minimal, ReaderMode.DedicatedThread, TimeSpan.FromSeconds(20))
                    .GetAwaiter().GetResult();

                kind.Should().Be(Outcome.Delivered, "spawn #{0}: {1}", i, detail);
                elapsed.Add(ms);
            }

            var rest = elapsed.Skip(1).OrderBy(x => x).ToArray();
            long restP50 = rest[rest.Length / 2];
            Record(
                $"HANDSHAKE conpty={ConPtyImplementation} n={count} first={elapsed[0]}ms "
                + $"rest_p50={restP50}ms all=[{string.Join(",", elapsed)}]");

            restP50.Should().BeLessThan(
                1500,
                "a read-only consumer must not pay ConPTY's 3s DA1 timeout — PtyProvider answers it. "
                + "all=[{0}]",
                string.Join(",", elapsed));
        }

        [TestMethod]
        public async Task OutputSurvivesTheProcessExiting_WhenTheReaderAttachesAfterwards()
        {
            string marker = NewMarker(0);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            using IPtyConnection terminal = await PtyProvider.SpawnAsync(
                ShellCommand("late-attach", $"echo {marker}", ShellMode.Minimal), cts.Token);

            int pid = terminal.Pid;

            // Let the child come and go BEFORE reading a single byte. This is the shape that loses
            // output downstream: a command fast enough to be over before anything is listening.
            // ConPTY buffers in conhost and is documented to deliver a final frame even at
            // ClosePseudoConsole time, and POSIX ptys hold their output queue for a late reader, so
            // a reader arriving after the child died is supposed to still get everything.
            //
            // A settle, not WaitForExit — because on POSIX, WaitForExit does not come back at all
            // for a child that produced output and has no reader. That is a real defect and it has
            // its own test below; using it here would make THIS test fail for that reason instead
            // of the one it is asking about.
            Thread.Sleep(3_000);

            var capture = new Capture(terminal, marker, ReaderMode.DedicatedThread);
            bool arrived = await capture.WaitAsync(TimeSpan.FromSeconds(10));

            Record($"pid={pid} exit={SafeExitCode(terminal)} captured={Escape(capture.Text)}");

            arrived.Should().BeTrue(
                "a reader that attached after the process exited saw none of its output "
                + "(pid={0}, exit code={1}, captured={2})",
                pid,
                SafeExitCode(terminal),
                Escape(capture.Text));
        }

        /// <summary>
        /// Pins what exit reporting actually depends on, because the obvious expectation is wrong on
        /// POSIX and wrong in a way that looks like a library bug.
        ///
        /// A caller that DRAINS the output always learns the process ended. A caller that does not may
        /// never learn it, and on macOS reliably does not:
        ///
        ///   sh -c "exit 0"                  no reader   reaped in 25 ms
        ///   sh -c "echo &lt;24 bytes&gt;"    no reader   NOT reaped after 7101 ms
        ///   sh -c "printf 8 KB"             no reader   NOT reaped after 7050 ms
        ///
        /// Measured in pure C against the shim, with waitpid(WNOHANG) and no .NET anywhere, so it is
        /// not the runtime's SIGCHLD reaper -- which was the first and wrong explanation. It is not a
        /// buffer size either: 24 bytes and 8 KB behave identically. It is BSD tty semantics. Closing a
        /// terminal with queued output blocks until that output drains, so a child that wrote ANYTHING
        /// cannot finish exiting until somebody reads the controller side. Correct OS behaviour, and
        /// nothing the library can do about it.
        ///
        /// What follows for callers, and it is the useful part: draining the PTY is not optional and
        /// not merely how you collect output. It is what lets the process finish. Anything that wants
        /// to know a process ended without wanting its output must read and discard anyway.
        ///
        /// The assertion is on the positive, which must hold everywhere. The no-reader case is recorded
        /// rather than asserted -- it is a platform observation, and Windows differs because ConPTY
        /// buffers in conhost rather than in a tty line discipline.
        /// </summary>
        [TestMethod]
        public void ExitIsReported_WhenTheOutputIsBeingDrained()
        {
            string marker = NewMarker(0);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            // No reader. Recorded, never asserted: see the remarks above.
            using (var undrained = PtyProvider
                .SpawnAsync(ShellCommand("no-reader", $"echo {marker}", ShellMode.Minimal), cts.Token)
                .GetAwaiter().GetResult())
            {
                var clock = Stopwatch.StartNew();
                bool withoutReader = undrained.WaitForExit(4_000);
                Record(
                    $"OBSERVED no-reader exit reported={withoutReader} after {clock.ElapsedMilliseconds}ms "
                    + $"(macOS expects false: BSD ttys block the child's exit until the output drains)");
            }

            using IPtyConnection terminal = PtyProvider
                .SpawnAsync(ShellCommand("with-reader", $"echo {marker}", ShellMode.Minimal), cts.Token)
                .GetAwaiter().GetResult();

            var capture = new Capture(terminal, marker, ReaderMode.DedicatedThread);
            bool reported = terminal.WaitForExit(15_000);

            Record(
                $"OBSERVED with-reader exit reported={reported} captured={Escape(capture.Text)}");

            reported.Should().BeTrue(
                "a caller that drains the PTY must be told the process ended; captured={0}",
                Escape(capture.Text));
        }

        private enum Outcome
        {
            /// <summary>The process spawned and its output arrived.</summary>
            Delivered,

            /// <summary>The process spawned, exited, and none of its output ever arrived.</summary>
            LostOutput,

            /// <summary>SpawnAsync threw; the process never ran at all.</summary>
            SpawnFailed,
        }

        /// <summary>
        /// Runs one short-lived process and classifies the result. Classifying rather than
        /// asserting keeps every run's verdict — an assertion on the first failure would hide
        /// whether 1 or 20 of them went wrong, and the COUNT is the signal being tracked across
        /// builds and machines. Distinguishing LostOutput from SpawnFailed matters just as much:
        /// they are different defects, and only one of them is the silent one.
        /// </summary>
        private static async Task<(Outcome Kind, string? Detail, long Ms)> RunOnceAsync(
            int index, ShellMode mode, ReaderMode reader, TimeSpan budget)
        {
            string marker = NewMarker(index);
            using var cts = new CancellationTokenSource(budget);
            var clock = Stopwatch.StartNew();

            IPtyConnection terminal;
            try
            {
                terminal = await PtyProvider.SpawnAsync(
                    ShellCommand($"concurrent-{index}", $"echo {marker}", mode), cts.Token);
            }
            catch (Exception ex)
            {
                return (Outcome.SpawnFailed, $"#{index:D2} spawn threw {ex.GetType().Name}: {ex.Message}", clock.ElapsedMilliseconds);
            }

            using (terminal)
            {
                int pid = terminal.Pid;
                var capture = new Capture(terminal, marker, reader);

                bool arrived = await capture.WaitAsync(budget);
                clock.Stop();
                terminal.WaitForExit(5_000);

                if (!arrived)
                {
                    return (
                        Outcome.LostOutput,
                        $"#{index:D2} lost its output — pid={pid}, exit={SafeExitCode(terminal)}, "
                        + $"captured={Escape(capture.Text)}",
                        clock.ElapsedMilliseconds);
                }

                return (Outcome.Delivered, null, clock.ElapsedMilliseconds);
            }
        }

        /// <summary>
        /// Which shell the repro drives the PTY with.
        ///
        /// This is not a detail. The first version of these tests used <see cref="Minimal"/> only,
        /// and it was clean at 24 spawns on every machine tried — including the winrunners, where
        /// the downstream loss was measured at exactly 24. That is because `cmd /c echo` is the
        /// lightest process Windows can produce: it starts, writes, and dies inside the window that
        /// a heavier one spends still starting up.
        ///
        /// the downstream consumer does not spawn that. Its run targets go through ShellRun.CommandShell, which on
        /// Windows resolves to pwsh.exe or powershell.exe and hands over the script as
        /// -EncodedCommand, and on POSIX runs the user's shell as a LOGIN + INTERACTIVE pass
        /// (-il) so PATH/nvm/asdf resolve. Both are an order of magnitude slower to start, which
        /// widens every window in the spawn path.
        /// </summary>
        public enum ShellMode
        {
            /// <summary>cmd.exe /c on Windows, /bin/sh -c elsewhere. The fastest possible child.</summary>
            Minimal,

            /// <summary>What the downstream consumer's run targets actually spawn.</summary>
            Realistic,
        }

        private static PtyOptions ShellCommand(string name, string command, ShellMode mode)
        {
            var (app, args) = mode == ShellMode.Minimal
                ? (ShellApp, IsWindows ? new[] { "/c", command } : new[] { "-c", command })
                : RealisticShell(command);

            return new PtyOptions
            {
                Name = name,
                Cols = 120,
                Rows = 25,
                Cwd = Environment.CurrentDirectory,
                App = app,
                CommandLine = args,

                // Verbatim only for the minimal form. the downstream consumer passes an argument ARRAY and lets it be
                // quoted per element with MSVCRT rules, which is part of what is being reproduced.
                VerbatimCommandLine = mode == ShellMode.Minimal,
                Environment = new Dictionary<string, string>(),
            };
        }

        /// <summary>
        /// Mirrors the engine.ShellRun.CommandShell + CommandArgs. Deliberately a COPY rather than
        /// a reference: this repo must not depend on the downstream consumer, and a copy that drifts is visible here,
        /// whereas an approximation dressed up as the real thing is not.
        /// </summary>
        private static (string App, string[] Args) RealisticShell(string command)
        {
            if (!IsWindows)
            {
                string shell = Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } s && File.Exists(s)
                    ? s
                    : new[] { "/bin/bash", "/bin/sh" }.First(File.Exists);
                return (shell, new[] { "-il", "-c", command });
            }

            string powershell = Which("pwsh.exe")
                ?? Which("powershell.exe")
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "System32", "WindowsPowerShell", "v1.0", "powershell.exe");

            string script =
                "$ProgressPreference = 'SilentlyContinue'\n"
                + command + "\n"
                + "$ok = $?\n"
                + "if ($null -ne $LASTEXITCODE) { exit $LASTEXITCODE }\n"
                + "if ($ok) { exit 0 } else { exit 1 }";

            return (
                powershell,
                new[]
                {
                    "-NoProfile",
                    "-NonInteractive",
                    "-ExecutionPolicy", "Bypass",
                    "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
                });
        }

        private static string? Which(string exe) =>
            (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir =>
            {
                try
                {
                    return Path.Combine(dir.Trim('"'), exe);
                }
                catch (ArgumentException)
                {
                    return null;
                }
            })
            .FirstOrDefault(candidate => candidate is not null && File.Exists(candidate));

        /// <summary>
        /// Which ConPTY implementation the library ACTUALLY chose, echoed into every measurement line.
        ///
        /// <para>Read from the library, not from PORTAPTY_CONPTY. The default is out-of-band with an
        /// automatic fallback to in-box when conpty.dll is absent, so the environment variable stops
        /// being the answer exactly when the fallback fires — and a result table mislabelled that way
        /// is worse than no table, as an earlier ConPTY A/B here demonstrated at length.</para>
        /// </summary>
        private static string ConPtyImplementation => PtyProvider.PseudoConsoleImplementation;

        /// <summary>
        /// Emits a measurement line to stdout AND appends it to a file.
        ///
        /// The file is the load-bearing half. MTP surfaces a test's stdout only when the test FAILS,
        /// and every number worth having here comes from a run that passed -- so on CI the console
        /// form produced "NO-SUMMARY exit=0" for all 32 samples of a ConPTY A/B. --output Detailed
        /// does not change that. A file sidesteps the runner's output policy entirely.
        /// </summary>
        private static void Record(string line)
        {
            Console.WriteLine(line);

            var path = Environment.GetEnvironmentVariable("PORTAPTY_SUMMARY_FILE");
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            // Best effort, and deliberately never fatal: a measurement that cannot be written must not
            // fail the test it was measuring.
            try
            {
                lock (SummaryGate)
                {
                    File.AppendAllText(path, line + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"(could not append to PORTAPTY_SUMMARY_FILE '{path}': {ex.Message})");
            }
        }

        private static readonly object SummaryGate = new object();

        /// <summary>
        /// Markers are letters and digits only. ConPTY reflows output at the column width and
        /// interleaves VT sequences, so anything a shell might quote, expand or wrap is a way for
        /// this test to fail for a reason that has nothing to do with the defect.
        /// </summary>
        private static string NewMarker(int index) =>
            $"PORTAPTY{index:D2}MARK{Guid.NewGuid():N}".Substring(0, 24);

        private static string SafeExitCode(IPtyConnection terminal)
        {
            try
            {
                return terminal.ExitCode.ToString();
            }
            catch (Exception ex)
            {
                return $"<unavailable: {ex.GetType().Name}>";
            }
        }

        /// <summary>
        /// Renders captured bytes readable in a failure message. A raw dump of PTY output is mostly
        /// escape sequences and carriage returns, which a test runner then re-wraps into something
        /// unreadable — and "the output was empty" and "the output was there but wrapped oddly" are
        /// exactly the two cases this has to tell apart.
        /// </summary>
        private static string Escape(string s)
        {
            if (s.Length == 0)
            {
                return "<empty>";
            }

            var sb = new StringBuilder(s.Length + 16);
            foreach (char c in s)
            {
                sb.Append(c switch
                {
                    '\r' => "\\r",
                    '\n' => "\\n",
                    '\t' => "\\t",
                    '\u001b' => "\\e",
                    _ => c < ' ' ? $"\\x{(int)c:x2}" : c.ToString(),
                });
            }

            return $"'{sb}'";
        }

        /// <summary>
        /// How the output stream is drained. This is the variable the whole experiment turns on.
        /// </summary>
        public enum ReaderMode
        {
            /// <summary>
            /// A dedicated <see cref="Thread"/> doing blocking <c>Read</c> calls. Nothing this reader
            /// needs can be delayed by other work, so it isolates the PTY's own behaviour.
            /// </summary>
            DedicatedThread,

            /// <summary>
            /// What the downstream consumer's the downstream task runner actually does: a LongRunning task that then
            /// <c>await</c>s <c>ReaderStream.ReadAsync</c> in a loop.
            ///
            /// LongRunning owns a dedicated thread only up to the first await that yields; from there
            /// every continuation is scheduled on the THREAD POOL. And on Windows the stream it is
            /// awaiting is a FileStream opened with isAsync: false, whose ReadAsync performs no
            /// overlapped I/O -- it parks a pool thread in a blocking Read. ConPTY does not signal EOF
            /// while the pseudoconsole is open, so that thread stays parked for the whole life of the
            /// process, not merely until the child writes.
            ///
            /// Twenty-four concurrent runs therefore park twenty-four pool threads on a machine whose
            /// pool starts at ProcessorCount and grows at roughly one thread per second. On a 4-core
            /// box already running four CI jobs that is a ~20 second window in which a reader can be
            /// created and never scheduled -- against a 20 second test budget.
            /// </summary>
            PoolAsync,
        }

        /// <summary>
        /// Drains a connection and completes as soon as a needle appears.
        ///
        /// EOF is not the stopping condition. On Windows the write end of the pipe belongs to conhost,
        /// not to the child, so the read stays blocked long after the child is reaped -- waiting for
        /// EOF would hang until the connection is disposed. The needle is the signal; the timeout is
        /// the failure.
        /// </summary>
        private sealed class Capture
        {
            private readonly StringBuilder buffer = new StringBuilder();
            private readonly TaskCompletionSource<bool> seen =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public Capture(IPtyConnection terminal, string needle, ReaderMode mode)
            {

                if (mode == ReaderMode.DedicatedThread)
                {
                    var thread = new Thread(() => this.BlockingLoop(terminal, needle))
                    {
                        IsBackground = true,
                        Name = "pty-capture",
                    };
                    thread.Start();
                }
                else
                {
                    // Started exactly the way the downstream task runner starts its reader, LongRunning hint and all,
                    // so that if the hint turns out not to survive the first await, it fails to survive
                    // it here in the same way.
                    _ = Task.Factory.StartNew(
                        () => this.PoolLoopAsync(terminal, needle),
                        CancellationToken.None,
                        TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                        TaskScheduler.Default);
                }
            }

            public string Text
            {
                get
                {
                    lock (this.buffer)
                    {
                        return this.buffer.ToString();
                    }
                }
            }

            public async Task<bool> WaitAsync(TimeSpan timeout) =>
                await Task.WhenAny(this.seen.Task, Task.Delay(timeout)).ConfigureAwait(false) == this.seen.Task;

            private void Add(string text, string needle)
            {
                lock (this.buffer)
                {
                    this.buffer.Append(text);
                    if (this.buffer.ToString().Contains(needle))
                    {
                        this.seen.TrySetResult(true);
                    }
                }
            }

            private void BlockingLoop(IPtyConnection terminal, string needle)
            {
                var bytes = new byte[4096];
                var encoding = new UTF8Encoding(false);

                try
                {
                    while (true)
                    {
                        int read = terminal.ReaderStream.Read(bytes, 0, bytes.Length);
                        if (read <= 0)
                        {
                            break;
                        }

                        this.Add(encoding.GetString(bytes, 0, read), needle);
                    }
                }
                catch
                {
                    // Disposed out from under the read, or the pipe broke. The waiter's timeout decides.
                }
            }

            private async Task PoolLoopAsync(IPtyConnection terminal, string needle)
            {
                var buffer = new byte[0x10000];
                var decoder = Encoding.UTF8.GetDecoder();
                var chars = new char[buffer.Length + 16];

                try
                {
                    while (true)
                    {
                        int read = await terminal.ReaderStream
                            .ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                        if (read <= 0)
                        {
                            break;
                        }

                        int count = decoder.GetChars(buffer, 0, read, chars, 0);
                        if (count > 0)
                        {
                            this.Add(new string(chars, 0, count), needle);
                        }
                    }
                }
                catch
                {
                    // Same as above.
                }
            }
        }
    }
}