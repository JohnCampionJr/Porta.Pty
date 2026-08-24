// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Windows
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Runtime.Versioning;
    using Microsoft.Win32.SafeHandles;

    /// <summary>
    /// A connection to a pseudoterminal spawned by native windows APIs.
    /// </summary>
    /// <remarks>
    /// Windows-only, and specifically Windows 10 1809 or later: ConPTY does not exist before that, and
    /// <see cref="NativeMethods.IsPseudoConsoleSupported"/> is the runtime gate that says so with a
    /// PlatformNotSupportedException naming the version.
    ///
    /// The VERSION in the annotation is not decoration. CsWin32's generated entry points carry their
    /// own floors (windows5.1.2600 for the job-object calls, windows6.0.6000 for the attribute-list
    /// ones), and a bare "windows" annotation does not satisfy them -- the platform-compatibility
    /// analyzer reported 22 warnings saying exactly that. Stating the real minimum satisfies all of
    /// them truthfully, where suppressing would have hidden a genuine question about which Windows
    /// versions this library supports.
    /// </remarks>
    [SupportedOSPlatform("windows10.0.17763")]
    internal sealed class PseudoConsoleConnection : IPtyConnection
    {
        private readonly Process process;
        private readonly object disposeLock = new object();
        private PseudoConsoleConnectionHandles? handles;
        private bool isDisposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="PseudoConsoleConnection"/> class.
        /// </summary>
        /// <param name="handles">The set of handles associated with the pseudoconsole.</param>
        /// <param name="hideStartupDa1Query">
        /// Whether ConPTY's startup Primary Device Attributes query should be removed from the output.
        /// True exactly when this library answers that query itself, so that the consumer is never
        /// asked something already answered. See <see cref="PtyOptions.AnswerDeviceAttributes"/>.
        /// </param>
        public PseudoConsoleConnection(PseudoConsoleConnectionHandles handles, bool hideStartupDa1Query = false)
        {
            // Use FileStream with the pipe handles for direct access
            // This avoids the buffering issues that can occur with AnonymousPipeClientStream
            Stream reader = new FileStream(
                new SafeFileHandle(handles.OutPipeOurSide.DangerousGetHandle(), ownsHandle: false),
                System.IO.FileAccess.Read,
                bufferSize: 0,  // No buffering
                isAsync: false);

            // The other half of PtyProvider.AnswerDeviceAttributes, which is why the caller decides
            // both with one value: having answered ConPTY's startup question for the consumer, we owe
            // it never being asked. A consumer that is a terminal emulator would otherwise answer it
            // too, and the surplus answer reaches the child as keyboard input.
            this.ReaderStream = hideStartupDa1Query
                ? new StartupDa1FilterStream(reader)
                : reader;


            this.WriterStream = new FileStream(
                new SafeFileHandle(handles.InPipeOurSide.DangerousGetHandle(), ownsHandle: false),
                System.IO.FileAccess.Write,
                bufferSize: 0,  // No buffering - writes go directly to pipe
                isAsync: false);

            this.handles = handles;
            this.Pid = handles.Pid;
            this.process = Process.GetProcessById(this.Pid);
            this.process.Exited += this.Process_Exited;
            this.process.EnableRaisingEvents = true;
        }

        /// <inheritdoc/>
        public event EventHandler<PtyExitedEventArgs>? ProcessExited;

        /// <inheritdoc/>
        public Stream ReaderStream { get; }

        /// <inheritdoc/>
        public Stream WriterStream { get; }

        /// <inheritdoc/>
        public int Pid { get; }

        /// <inheritdoc/>
        public int ExitCode => this.process.ExitCode;

        /// <inheritdoc/>
        public void Dispose()
        {
            lock (this.disposeLock)
            {
                if (this.isDisposed)
                {
                    return;
                }

                this.isDisposed = true;
            }

            // Unsubscribe from events first to prevent callbacks during disposal
            this.process.Exited -= this.Process_Exited;

            // ConPTY cleanup order (per Microsoft documentation):
            // 1. Close the PseudoConsole handle - signals conhost to shut down
            // 2. Close the pipes - allows pending I/O to complete
            // 3. Close process/thread handles
            // 4. Close job object last - terminates any remaining processes

            if (this.handles != null)
            {
                // Step 1: Close the pseudo console first (calls ClosePseudoConsole)
                // This signals conhost.exe to shut down gracefully
                this.handles.PseudoConsoleHandle?.Dispose();

                // Step 2: Close the pipes
                // Close our side of the pipes - this will cause any pending reads to complete
                this.handles.InPipeOurSide?.Dispose();
                this.handles.OutPipeOurSide?.Dispose();

                // Step 3: Close process and thread handles
                this.handles.MainThreadHandle?.Dispose();
                this.handles.ProcessHandle?.Dispose();

                // Step 4: Dispose the job object last - this will terminate any remaining
                // child processes due to JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                this.handles.JobObjectHandle?.Dispose();

                this.handles = null;
            }

            // Dispose streams (they don't own the underlying handles)
            this.ReaderStream?.Dispose();
            this.WriterStream?.Dispose();

            // Dispose the Process object
            this.process?.Dispose();
        }

        /// <inheritdoc/>
        public void Kill()
        {
            this.process.Kill();
        }

        /// <inheritdoc/>
        public void Resize(int cols, int rows)
        {
            var handles = this.handles;
            if (handles == null || this.isDisposed)
            {
                throw new ObjectDisposedException(nameof(PseudoConsoleConnection));
            }

            handles.PseudoConsoleHandle.Resize(cols, rows);
        }

        /// <inheritdoc/>
        public bool WaitForExit(int milliseconds)
        {
            return this.process.WaitForExit(milliseconds);
        }

        private void Process_Exited(object? sender, EventArgs e)
        {
            // Check if we're disposed to avoid raising events during/after disposal
            if (this.isDisposed)
            {
                return;
            }

            this.ProcessExited?.Invoke(this, new PtyExitedEventArgs(this.process.ExitCode));
        }

        /// <summary>
        /// handles to resources creates when a pseudoconsole is spawned.
        /// </summary>
        internal sealed class PseudoConsoleConnectionHandles
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="PseudoConsoleConnectionHandles"/> class.
            /// </summary>
            /// <param name="inPipeOurSide">the input pipe on the local side (we write to this).</param>
            /// <param name="outPipeOurSide">the output pipe on the local side (we read from this).</param>
            /// <param name="pseudoConsoleHandle">the handle to the pseudoconsole.</param>
            /// <param name="processHandle">the handle to the spawned process.</param>
            /// <param name="pid">the process ID.</param>
            /// <param name="mainThreadHandle">the handle to the main thread.</param>
            /// <param name="jobObjectHandle">the handle to the job object that manages process lifetime.</param>
            public PseudoConsoleConnectionHandles(
                SafeFileHandle inPipeOurSide,
                SafeFileHandle outPipeOurSide,
                PseudoConsole pseudoConsoleHandle,
                SafeFileHandle processHandle,
                int pid,
                SafeFileHandle mainThreadHandle,
                SafeFileHandle jobObjectHandle)
            {
                this.InPipeOurSide = inPipeOurSide;
                this.OutPipeOurSide = outPipeOurSide;
                this.PseudoConsoleHandle = pseudoConsoleHandle;
                this.ProcessHandle = processHandle;
                this.Pid = pid;
                this.MainThreadHandle = mainThreadHandle;
                this.JobObjectHandle = jobObjectHandle;
            }

            /// <summary>
            /// Gets the input pipe on the local side (we write to this to send to console).
            /// </summary>
            internal SafeFileHandle InPipeOurSide { get; }

            /// <summary>
            /// Gets the output pipe on the local side (we read from this to get console output).
            /// </summary>
            internal SafeFileHandle OutPipeOurSide { get; }

            /// <summary>
            /// Gets the handle to the pseudoconsole.
            /// </summary>
            internal PseudoConsole PseudoConsoleHandle { get; }

            /// <summary>
            /// Gets the handle to the spawned process.
            /// </summary>
            internal SafeFileHandle ProcessHandle { get; }

            /// <summary>
            /// Gets the process ID.
            /// </summary>
            internal int Pid { get; }

            /// <summary>
            /// Gets the handle to the main thread.
            /// </summary>
            internal SafeFileHandle MainThreadHandle { get; }

            /// <summary>
            /// Gets the handle to the job object that manages process lifetime.
            /// When this handle is closed, all processes assigned to the job are terminated.
            /// </summary>
            internal SafeFileHandle JobObjectHandle { get; }
        }
    }
}
