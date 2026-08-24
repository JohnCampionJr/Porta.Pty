// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Unix
{
    using System;
    using System.IO;
    using Microsoft.Win32.SafeHandles;

    /// <summary>
    /// A stream connected to a pty.
    /// </summary>
    internal sealed class PtyStream : FileStream
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PtyStream"/> class.
        /// </summary>
        /// <param name="fd">The fd to connect the stream to.</param>
        /// <param name="fileAccess">The access permissions to set on the fd.</param>
        /// <remarks>
        /// UNBUFFERED, matching the Windows connection's pipes. A FileStream write buffer sits in
        /// front of the pty and holds a write until it fills or something flushes, so
        /// WriterStream.Write("echo hi\n") reached the child on Windows and did nothing at all on
        /// Linux and macOS -- the shell simply sat at its prompt. Nothing reported an error, because
        /// buffering a write is not one.
        ///
        /// The 1024 came in with the original Microsoft-derived code and stayed; the Windows side was
        /// later rewritten to bufferSize: 0 for the same class of problem (see the comment in
        /// PseudoConsoleConnection) and Unix was never brought along.
        ///
        /// Reads lose their buffer too, which is the right trade here rather than a cost worth
        /// bearing: a terminal reads whatever a program just wrote, in the size it was written, and
        /// wants it now.
        /// </remarks>
        public PtyStream(int fd, FileAccess fileAccess)
            : base(new SafeFileHandle((IntPtr)fd, ownsHandle: false), fileAccess, bufferSize: 0, isAsync: false)
        {
        }

        /// <inheritdoc/>
        public override bool CanSeek => false;
    }
}
