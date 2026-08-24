// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Unix
{
    using System;
    using System.Collections.Generic;
    using System.Threading;

    /// <summary>
    /// Collects exit statuses for every pty child in the process from one thread.
    /// </summary>
    /// <remarks>
    /// The other half of not costing a thread per session, and the half that turned out to dominate.
    /// Making reads threadless left a watcher thread per connection sitting in a blocking waitpid,
    /// so twelve idle sessions still cost twelve threads; the reads were never the floor.
    ///
    /// waitpid(-1) would be the obvious way to write this and is the wrong one. It reaps ANY child of
    /// the process, including ones this library never spawned -- System.Diagnostics.Process on Unix
    /// reaps its own children, and a status collected here is a status it will never see, so its
    /// WaitForExit would hang on a process that had already exited. A library sharing an address
    /// space with code it does not control cannot claim every child. So this waits on the pids it was
    /// given, one at a time, with WNOHANG.
    ///
    /// Which makes it a poll, and the interval is a real cost: an exit is not observed until the next
    /// pass. That is affordable because it is not the signal anyone waits on -- the pty goes readable
    /// then EOF the moment the child dies, so a reader already knows. This is only collecting the
    /// exit CODE, and a hundred milliseconds of latency on that is invisible. The alternative that
    /// avoids polling entirely is per-pid kernel notification, which is kqueue EVFILT_PROC on macOS
    /// and pidfd_open plus poll on Linux -- two more platform paths for a latency nobody is measuring.
    /// </remarks>
    internal sealed class PtyReaper
    {
        /// <summary>
        /// How long a child may lie dead before its status is collected.
        /// </summary>
        private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

        private static readonly Lazy<PtyReaper> InstanceHolder =
            new(() => new PtyReaper(), LazyThreadSafetyMode.ExecutionAndPublication);

        private readonly object gate = new();
        private readonly Dictionary<int, Entry> children = new();
        private readonly AutoResetEvent registered = new(false);

        private PtyReaper()
        {
            var thread = new Thread(this.Loop)
            {
                IsBackground = true,
                Name = "Porta.Pty reaper",
            };
            thread.Start();
        }

        /// <summary>
        /// Attempts to collect the status of one child without blocking.
        /// </summary>
        /// <returns>The pid on success, 0 if it is still running, -1 on failure.</returns>
        internal delegate int ReapAttempt(int pid, ref int status);

        internal static PtyReaper Instance => InstanceHolder.Value;

        /// <summary>
        /// Watches <paramref name="pid"/> until it exits, then calls <paramref name="onExited"/> once
        /// with its raw wait status.
        /// </summary>
        internal void Register(int pid, ReapAttempt attempt, Action<int> onExited)
        {
            lock (this.gate)
            {
                this.children[pid] = new Entry(attempt, onExited);
            }

            // Wake the loop so a child that exits immediately is not held for a full interval.
            this.registered.Set();
        }

        /// <summary>
        /// Stops watching a pid.
        /// </summary>
        /// <remarks>
        /// Deliberately NOT called from PtyConnection.Dispose. A disposed connection has signalled
        /// its child but not collected it, and dropping the watch there left a zombie for the life
        /// of the host process. Kept for a caller that genuinely knows a pid is already reaped.
        /// </remarks>
        internal void Unregister(int pid)
        {
            lock (this.gate)
            {
                this.children.Remove(pid);
            }
        }

        private void Loop()
        {
            var finished = new List<(Entry Entry, int Status)>();

            while (true)
            {
                KeyValuePair<int, Entry>[] snapshot;
                lock (this.gate)
                {
                    snapshot = new KeyValuePair<int, Entry>[this.children.Count];
                    ((ICollection<KeyValuePair<int, Entry>>)this.children).CopyTo(snapshot, 0);
                }

                finished.Clear();

                foreach (var pair in snapshot)
                {
                    int status = 0;
                    int result;
                    try
                    {
                        result = pair.Value.Attempt(pair.Key, ref status);
                    }
                    catch
                    {
                        // A torn-down connection can race us; drop it rather than take the process
                        // down from a background thread.
                        result = -1;
                    }

                    if (result == 0)
                    {
                        continue;
                    }

                    // Anything other than "still running" ends the watch.
                    lock (this.gate)
                    {
                        this.children.Remove(pair.Key);
                    }

                    if (result < 0)
                    {
                        // Almost always ECHILD: something else collected the status and there is
                        // none to be had. Reporting the zero sitting in `status` would claim the
                        // child succeeded, which is a specific and wrong answer rather than an
                        // absent one -- so the watch just ends, and WaitForExit and ExitCode keep
                        // whatever they already had.
                        continue;
                    }

                    finished.Add((pair.Value, status));
                }

                // Dispatched outside the lock: these run consumer code, which is free to dispose the
                // connection and re-enter Unregister.
                foreach (var (entry, status) in finished)
                {
                    try
                    {
                        entry.OnExited(status);
                    }
                    catch
                    {
                        // A consumer's exit handler throwing is not this thread's to propagate.
                    }
                }

                // Wait without a deadline when there is nothing to watch. Register signals the
                // event, so a new child still wakes the loop immediately -- and an application that
                // opened one session and closed it does not keep a 10Hz wakeup for the life of the
                // process, which would work against the very thing this class is for.
                bool idle;
                lock (this.gate)
                {
                    idle = this.children.Count == 0;
                }

                this.registered.WaitOne(idle ? Timeout.InfiniteTimeSpan : Interval);
            }
        }

        private sealed class Entry
        {
            internal Entry(ReapAttempt attempt, Action<int> onExited)
            {
                this.Attempt = attempt;
                this.OnExited = onExited;
            }

            internal ReapAttempt Attempt { get; }

            internal Action<int> OnExited { get; }
        }
    }
}
