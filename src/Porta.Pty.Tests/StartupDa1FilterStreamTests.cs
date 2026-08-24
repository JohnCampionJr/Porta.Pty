// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Porta.Pty.Windows;

    /// <summary>
    /// Pins the removal of ConPTY's startup Primary Device Attributes query.
    /// </summary>
    /// <remarks>
    /// <para>The defect these were written for: out-of-band ConPTY opens by asking <c>ESC[c</c> and
    /// blocking for the reply, <c>PtyProvider.AnswerDeviceAttributes</c> answers it so a read-only
    /// consumer does not pay three seconds, and ConPTY forwards the question downstream anyway. A
    /// consumer that is a terminal emulator then answers a question that has already been answered,
    /// and the surplus reply is delivered to the child as keyboard input — an interactive shell echoes
    /// it, and <c>^[[?1;2c</c> appears beside the first prompt.</para>
    ///
    /// <para>Driven over a scripted stream rather than a live pseudoconsole, because the case that
    /// matters most — the query arriving split across two reads — cannot be provoked on demand from a
    /// real one. <see cref="StartupDa1FilterStreamIntegrationTests"/> covers the real one.</para>
    /// </remarks>
    [TestClass]
    public class StartupDa1FilterStreamTests
    {
        private const string Esc = "\u001b";

        private const string Da1Query = Esc + "[c";

        [TestMethod]
        public void RemovesTheStartupQuery()
        {
            var read = Drain(Chunks(Esc + "[1t" + Da1Query + Esc + "[?1004h"));

            read.Should().Be(Esc + "[1t" + Esc + "[?1004h");
        }

        [TestMethod]
        public void RemovesTheStartupQuery_WhenItIsTheWholeChunk()
        {
            // The read that contains only the query yields nothing to hand out. Returning 0 there
            // would tell the caller the stream had ended, so the filter has to read again instead.
            var read = Drain(Chunks("before", Da1Query, "after"));

            read.Should().Be("beforeafter");
        }

        [TestMethod]
        public void RemovesTheStartupQuery_WhenSplitAfterTheEscape()
        {
            var read = Drain(Chunks("a" + Esc, "[c" + "b"));

            read.Should().Be("ab");
        }

        [TestMethod]
        public void RemovesTheStartupQuery_WhenSplitAfterTheBracket()
        {
            var read = Drain(Chunks("a" + Esc + "[", "c" + "b"));

            read.Should().Be("ab");
        }

        [TestMethod]
        public void RemovesTheStartupQuery_WhenEveryByteArrivesSeparately()
        {
            var read = Drain(Chunks("a", Esc, "[", "c", "b"));

            read.Should().Be("ab");
        }

        [TestMethod]
        public void LeavesALaterQueryAlone()
        {
            // The second one is the CHILD asking what terminal it is running under. Nobody has
            // answered that, and the consumer is the only one who can.
            var read = Drain(Chunks(Da1Query + "shell" + Da1Query));

            read.Should().Be("shell" + Da1Query);
        }

        [TestMethod]
        public void PassesThroughAStreamWithNoQuery()
        {
            var body = Esc + "[?25l" + Esc + "[2J" + Esc + "[H" + "Microsoft Windows" + Esc + "[4;1H";

            Drain(Chunks(body)).Should().Be(body);
        }

        [TestMethod]
        public void HoldsNothingBackAtTheEndOfTheStream()
        {
            // "ESC[" at the very end could have been the front of a query, so it is withheld while
            // there might still be a "c" coming. Once the stream ends there cannot be, and those
            // bytes are ordinary data that the consumer is still owed.
            Drain(Chunks("tail" + Esc + "[")).Should().Be("tail" + Esc + "[");
            Drain(Chunks("tail" + Esc)).Should().Be("tail" + Esc);
        }

        [TestMethod]
        public void ServesACallerBufferSmallerThanTheChunk()
        {
            var read = Drain(Chunks("0123456789" + Da1Query + "abcdefghij"), bufferSize: 3);

            read.Should().Be("0123456789abcdefghij");
        }

        [TestMethod]
        public void StopsLookingAfterTheStartupWindow()
        {
            // Bounded so a stream that never contains the query stops scanning, and stops being able
            // to withhold a partial match. 64 KiB in; the real query arrives within ~30 bytes.
            var filler = new string('x', 70 * 1024);
            var read = Drain(Chunks(filler + Da1Query));

            read.Should().Be(filler + Da1Query);
        }

        [TestMethod]
        public void StopsLookingAfterTheStartupWindow_EvenWhenOneReadSpansIt()
        {
            // The window is on where the query STARTS, not on which read it lands in. With a caller
            // buffer big enough to take the whole thing at once, a bound applied per-read instead of
            // per-offset would still strip this one -- and a query this late is the child's.
            var filler = new string('x', 70 * 1024);
            var read = Drain(Chunks(filler + Da1Query), bufferSize: 128 * 1024);

            read.Should().Be(filler + Da1Query);
        }

        [TestMethod]
        public async Task RemovesTheStartupQuery_ReadingAsynchronously()
        {
            // The consumer this was written for reads with ReadAsync, so the async path is not a
            // convenience wrapper here -- it is the one that runs.
            var read = await DrainAsync(Chunks("a" + Esc, "[c" + "b"));

            read.Should().Be("ab");
        }

        private static IEnumerable<byte[]> Chunks(params string[] chunks) =>
            chunks.Select(chunk => Encoding.ASCII.GetBytes(chunk));

        private static string Drain(IEnumerable<byte[]> chunks, int bufferSize = 4096)
        {
            using var filter = new StartupDa1FilterStream(new ScriptedStream(chunks));
            var buffer = new byte[bufferSize];
            var read = new MemoryStream();

            int n;
            while ((n = filter.Read(buffer, 0, buffer.Length)) > 0)
            {
                read.Write(buffer, 0, n);
            }

            return Encoding.ASCII.GetString(read.ToArray());
        }

        private static async Task<string> DrainAsync(IEnumerable<byte[]> chunks, int bufferSize = 4096)
        {
            using var filter = new StartupDa1FilterStream(new ScriptedStream(chunks));
            var buffer = new byte[bufferSize];
            var read = new MemoryStream();

            int n;
            while ((n = await filter.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None)) > 0)
            {
                read.Write(buffer, 0, n);
            }

            return Encoding.ASCII.GetString(read.ToArray());
        }

        /// <summary>
        /// A read-only stream that hands back exactly the chunks it was given, one per read — the
        /// control a pipe does not offer, and the only way to place a byte sequence across a read
        /// boundary on purpose.
        /// </summary>
        private sealed class ScriptedStream : Stream
        {
            private readonly Queue<byte[]> chunks;
            private byte[] current = Array.Empty<byte>();
            private int offset;

            public ScriptedStream(IEnumerable<byte[]> chunks)
            {
                this.chunks = new Queue<byte[]>(chunks);
            }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (this.offset >= this.current.Length)
                {
                    if (this.chunks.Count == 0)
                    {
                        return 0;
                    }

                    this.current = this.chunks.Dequeue();
                    this.offset = 0;
                }

                var take = Math.Min(count, this.current.Length - this.offset);
                Array.Copy(this.current, this.offset, buffer, offset, take);
                this.offset += take;
                return take;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                Task.FromResult(this.Read(buffer, offset, count));

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
