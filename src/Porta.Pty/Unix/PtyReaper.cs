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
        /// Stops watching a pid whose status will never be collected, typically because the
        /// connection was disposed.
        /// </summary>
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

                    // Anything other than "still running" ends the watch. A failure is almost always
                    // ECHILD, meaning something else already collected it, and there is no status to
                    // be had -- reporting the zero is better than watching a pid forever.
                    lock (this.gate)
                    {
                        this.children.Remove(pair.Key);
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

                this.registered.WaitOne(Interval);
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
