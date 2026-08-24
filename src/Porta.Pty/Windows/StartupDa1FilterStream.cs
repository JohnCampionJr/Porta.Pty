// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Windows
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Hides the one startup query that <see cref="PtyProvider"/> has already answered on the
    /// consumer's behalf.
    /// </summary>
    /// <remarks>
    /// <para>Out-of-band ConPTY opens by asking the terminal what it is — Primary Device Attributes,
    /// <c>ESC[c</c> — and then blocks in <c>WaitUntilDA1(3000)</c> for the reply. A consumer that only
    /// reads never answers, so <c>AnswerDeviceAttributes</c> answers immediately after the spawn and
    /// saves it three seconds. Measured here, time from spawn to the child's first output:
    /// <c>3088ms</c> and <c>3097ms</c> unanswered, <c>109ms</c> answered.</para>
    ///
    /// <para>But ConPTY also FORWARDS that query downstream, and a consumer that is a real terminal
    /// emulator answers it, because answering DA1 is what terminals do. The handshake has already been
    /// satisfied by our reply, so the emulator's reply is not consumed as a handshake — ConPTY hands it
    /// to the child as keyboard input, and an interactive shell echoes it at the prompt:
    /// <c>^[[?1;2c</c> sitting next to the first prompt, which is what sent us looking.</para>
    ///
    /// <para>So the query is removed from the output. The invariant is one sentence: WE answered this
    /// question, so the consumer never sees it asked. Nothing is lost by hiding it — the emulator's
    /// answer was redundant, and the reply ConPTY needed has already been sent.</para>
    ///
    /// <para>Only the FIRST <c>ESC[c</c> is removed, and only near the start of the stream. A DA1 query
    /// from the CHILD — a program asking what terminal it is running under — is forwarded the same way
    /// and DOES need the consumer's answer, so it must pass through untouched. ConPTY's own query is
    /// distinguishable by being first: it is part of the startup handshake, and arrives before the
    /// child has produced anything.</para>
    /// </remarks>
    internal sealed class StartupDa1FilterStream : Stream
    {
        /// <summary>How far into the stream to keep looking, in bytes.</summary>
        /// <remarks>
        /// The query arrives within the first few dozen bytes — the handshake burst measured here is
        /// <c>ESC[1t</c> then <c>ESC[c ESC[?1004h ESC[?9001h ESC[?7l ESC[?7h</c> — so this is slack,
        /// not a guess at the real distance. It exists so a stream that never contains the query stops
        /// paying for the scan, and stops being able to hold a split-sequence prefix back.
        /// </remarks>
        private const int ScanBudget = 64 * 1024;

        private const byte Escape = 0x1B;

        private static readonly byte[] Da1Query = new[] { Escape, (byte)'[', (byte)'c' };

        private readonly Stream inner;

        /// <summary>A trailing partial <see cref="Da1Query"/> match, withheld until the next read says
        /// whether it was one. At most two bytes, and only while <see cref="scanning"/>.</summary>
        private readonly byte[] held = new byte[Da1Query.Length - 1];

        private byte[] scratch = Array.Empty<byte>();
        private int pendingOffset;
        private int pendingCount;
        private int heldCount;
        private int scanned;
        private bool scanning = true;

        /// <summary>
        /// Initializes a new instance of the <see cref="StartupDa1FilterStream"/> class.
        /// </summary>
        /// <param name="inner">The pseudoconsole output stream to filter.</param>
        public StartupDa1FilterStream(Stream inner)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        /// <inheritdoc/>
        public override bool CanRead => this.inner.CanRead;

        /// <inheritdoc/>
        public override bool CanSeek => false;

        /// <inheritdoc/>
        public override bool CanWrite => false;

        /// <inheritdoc/>
        public override long Length => throw new NotSupportedException();

        /// <inheritdoc/>
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <inheritdoc/>
        public override void Flush() => this.inner.Flush();

        /// <inheritdoc/>
        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateArguments(buffer, offset, count);

            while (true)
            {
                if (this.pendingCount > 0)
                {
                    return this.TakePending(buffer, offset, count);
                }

                if (!this.scanning && this.heldCount == 0)
                {
                    return this.inner.Read(buffer, offset, count);
                }

                if (count == 0)
                {
                    return 0;
                }

                var room = this.PrepareScratch(count);
                var read = this.inner.Read(this.scratch, this.heldCount, room);

                if (!this.Absorb(read))
                {
                    return 0;
                }
            }
        }

        /// <inheritdoc/>
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ValidateArguments(buffer, offset, count);

            while (true)
            {
                if (this.pendingCount > 0)
                {
                    return this.TakePending(buffer, offset, count);
                }

                if (!this.scanning && this.heldCount == 0)
                {
                    return await this.inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
                }

                if (count == 0)
                {
                    return 0;
                }

                var room = this.PrepareScratch(count);
                var read = await this.inner.ReadAsync(this.scratch, this.heldCount, room, cancellationToken).ConfigureAwait(false);

                if (!this.Absorb(read))
                {
                    return 0;
                }
            }
        }

        /// <inheritdoc/>
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc/>
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc/>
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private static void ValidateArguments(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);

            if (buffer.Length - offset < count)
            {
                throw new ArgumentException("The buffer is too small for the requested count.", nameof(buffer));
            }
        }

        /// <summary>
        /// Gets the length of the longest suffix of <paramref name="buffer"/> that is a PROPER prefix
        /// of <see cref="Da1Query"/> — the bytes that might be the front of a query split across two
        /// reads.
        /// </summary>
        private static int TrailingPartialMatch(byte[] buffer, int length)
        {
            var longest = Math.Min(Da1Query.Length - 1, length);

            for (var candidate = longest; candidate > 0; candidate--)
            {
                var matched = true;

                for (var i = 0; i < candidate; i++)
                {
                    if (buffer[length - candidate + i] != Da1Query[i])
                    {
                        matched = false;
                        break;
                    }
                }

                if (matched)
                {
                    return candidate;
                }
            }

            return 0;
        }

        private static int IndexOfQuery(byte[] buffer, int length)
        {
            for (var i = 0; i + Da1Query.Length <= length; i++)
            {
                if (buffer[i] == Da1Query[0] && buffer[i + 1] == Da1Query[1] && buffer[i + 2] == Da1Query[2])
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Sizes <see cref="scratch"/> for a read of <paramref name="count"/> bytes on top of whatever
        /// is held, copies the held bytes to the front, and returns the room left for the read.
        /// </summary>
        private int PrepareScratch(int count)
        {
            var needed = count + this.heldCount;

            if (this.scratch.Length < needed)
            {
                this.scratch = new byte[needed];
            }

            Array.Copy(this.held, 0, this.scratch, 0, this.heldCount);
            return this.scratch.Length - this.heldCount;
        }

        /// <summary>
        /// Folds a completed read into the pending buffer, removing the query if this is where it
        /// turned up.
        /// </summary>
        /// <param name="read">Bytes just read into <see cref="scratch"/> past the held ones.</param>
        /// <returns><see langword="false"/> when the stream has ended and nothing is left to hand
        /// out; otherwise <see langword="true"/>, though the caller may still have to read again
        /// because everything in this chunk was held back or removed.</returns>
        private bool Absorb(int read)
        {
            var total = this.heldCount + read;
            this.heldCount = 0;

            if (read <= 0)
            {
                // End of stream. Anything held back was ordinary data after all, so hand it over.
                this.scanning = false;
                this.pendingOffset = 0;
                this.pendingCount = total;
                return total > 0;
            }

            this.scanned += read;

            if (this.scanning)
            {
                var at = IndexOfQuery(this.scratch, total);

                if (at >= 0)
                {
                    Array.Copy(this.scratch, at + Da1Query.Length, this.scratch, at, total - at - Da1Query.Length);
                    total -= Da1Query.Length;
                    this.scanning = false;
                }
                else if (this.scanned >= ScanBudget)
                {
                    this.scanning = false;
                }
                else
                {
                    // The chunk may have ended mid-query. Withhold the prefix rather than let a
                    // split query slip through in two pieces.
                    this.heldCount = TrailingPartialMatch(this.scratch, total);
                    Array.Copy(this.scratch, total - this.heldCount, this.held, 0, this.heldCount);
                    total -= this.heldCount;
                }
            }

            this.pendingOffset = 0;
            this.pendingCount = total;
            return true;
        }

        private int TakePending(byte[] buffer, int offset, int count)
        {
            var take = Math.Min(count, this.pendingCount);
            Array.Copy(this.scratch, this.pendingOffset, buffer, offset, take);
            this.pendingOffset += take;
            this.pendingCount -= take;
            return take;
        }
    }
}
