// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Covers what a write to <see cref="IPtyConnection.WriterStream"/> actually delivers.
    /// </summary>
    /// <remarks>
    /// These run on every platform on purpose. The bug they pin was a DIFFERENCE between platforms
    /// -- an unflushed write reached the child on Windows and vanished on Unix -- so a Unix-only
    /// test would have described the fix rather than the property that makes it correct, which is
    /// that both behave the same.
    /// </remarks>
    [TestClass]
    public class WriterStreamTests
    {
        private static readonly int TestTimeoutMs = Debugger.IsAttached ? 300_000 : 30_000;

        private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        private static PtyOptions InteractiveShell(string name)
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
            };
        }

        [TestMethod]
        public async Task Write_ReachesTheChild_WithoutAnExplicitFlush()
        {
            using var cts = new CancellationTokenSource(TestTimeoutMs);
            using IPtyConnection terminal = await PtyProvider.SpawnAsync(InteractiveShell("NoFlush"), cts.Token);

            byte[] command = Encoding.UTF8.GetBytes("echo PORTA_NO_FLUSH\n");
            terminal.WriterStream.Write(command, 0, command.Length);

            // Deliberately no Flush(). With a FileStream write buffer in front of the pty this
            // returns having sent nothing, and the shell sits at its prompt forever.
            string output = await ReadUntilAsync(terminal, "PORTA_NO_FLUSH", TimeSpan.FromSeconds(10));

            output.Should().Contain(
                "PORTA_NO_FLUSH",
                "a write to the pty must not sit in a buffer waiting for a flush the caller has no reason to make");

            terminal.Kill();
            terminal.WaitForExit(5000);
        }

        [TestMethod]
        public async Task WriteAsync_ReachesTheChild_WithoutAnExplicitFlush()
        {
            using var cts = new CancellationTokenSource(TestTimeoutMs);
            using IPtyConnection terminal = await PtyProvider.SpawnAsync(InteractiveShell("NoFlushAsync"), cts.Token);

            byte[] command = Encoding.UTF8.GetBytes("echo PORTA_NO_FLUSH_ASYNC\n");
            await terminal.WriterStream.WriteAsync(command, 0, command.Length, cts.Token);

            string output = await ReadUntilAsync(terminal, "PORTA_NO_FLUSH_ASYNC", TimeSpan.FromSeconds(10));

            output.Should().Contain("PORTA_NO_FLUSH_ASYNC");

            terminal.Kill();
            terminal.WaitForExit(5000);
        }

        [TestMethod]
        public async Task Write_StillReachesTheChild_WhenTheCallerDoesFlush()
        {
            // Flushing must stay harmless: existing consumers all do it, and an unbuffered stream
            // still has to accept the call.
            using var cts = new CancellationTokenSource(TestTimeoutMs);
            using IPtyConnection terminal = await PtyProvider.SpawnAsync(InteractiveShell("WithFlush"), cts.Token);

            byte[] command = Encoding.UTF8.GetBytes("echo PORTA_WITH_FLUSH\n");
            terminal.WriterStream.Write(command, 0, command.Length);
            terminal.WriterStream.Flush();

            string output = await ReadUntilAsync(terminal, "PORTA_WITH_FLUSH", TimeSpan.FromSeconds(10));

            output.Should().Contain("PORTA_WITH_FLUSH");

            terminal.Kill();
            terminal.WaitForExit(5000);
        }

        [TestMethod]
        public async Task Write_DeliversEachOfSeveralWritesInOrder()
        {
            // A buffer would have made these arrive together, or not at all. Ordering is the part a
            // caller feeding a shell one line at a time actually depends on.
            using var cts = new CancellationTokenSource(TestTimeoutMs);
            using IPtyConnection terminal = await PtyProvider.SpawnAsync(InteractiveShell("Sequence"), cts.Token);

            foreach (var marker in new[] { "PORTA_ONE", "PORTA_TWO", "PORTA_THREE" })
            {
                byte[] command = Encoding.UTF8.GetBytes($"echo {marker}\n");
                terminal.WriterStream.Write(command, 0, command.Length);
            }

            string output = await ReadUntilAsync(terminal, "PORTA_THREE", TimeSpan.FromSeconds(10));

            output.Should().Contain("PORTA_ONE");
            output.Should().Contain("PORTA_TWO");
            output.Should().Contain("PORTA_THREE");
            output.IndexOf("PORTA_ONE", StringComparison.Ordinal)
                .Should().BeLessThan(
                    output.IndexOf("PORTA_THREE", StringComparison.Ordinal),
                    "writes must arrive in the order they were made");

            terminal.Kill();
            terminal.WaitForExit(5000);
        }

        private static async Task<string> ReadUntilAsync(IPtyConnection terminal, string needle, TimeSpan timeout)
        {
            var buffer = new byte[4096];
            var output = new StringBuilder();
            var encoding = new UTF8Encoding(false);
            var stopwatch = Stopwatch.StartNew();

            while (stopwatch.Elapsed < timeout)
            {
                var read = Task.Run(() => terminal.ReaderStream.Read(buffer, 0, buffer.Length));
                if (await Task.WhenAny(read, Task.Delay(250)) != read)
                {
                    continue;
                }

                if (read.Result > 0)
                {
                    output.Append(encoding.GetString(buffer, 0, read.Result));
                    if (output.ToString().Contains(needle))
                    {
                        break;
                    }
                }
            }

            return output.ToString();
        }
    }
}
