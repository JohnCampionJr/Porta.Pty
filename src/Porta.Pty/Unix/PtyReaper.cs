// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Unix
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;
    using static Porta.Pty.Unix.NativeIo;

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
    /// Which makes it a poll, and the interval is a real, user-visible cost. An earlier version of
    /// this comment claimed otherwise -- that nobody waits on it, because a reader sees EOF the
    /// moment the child dies. That is wrong: this callback is the ONLY thing that sets the
    /// terminated event and raises ProcessExited, so WaitForExit and every event subscriber wait on
    /// exactly this interval. Budget up to Interval of extra latency on both, on top of however long
    /// the child took to die.
    ///
    /// It is still the right trade for a terminal, where a tenth of a second on a session ending is
    /// not perceptible, but it is a trade rather than a free lunch. Avoiding the poll means per-pid
    /// kernel notification -- kqueue EVFILT_PROC on macOS, pidfd_open plus poll on Linux -- which is
    /// two more platform paths, and worth taking if that latency ever matters to someone.
    /// </remarks>
    internal sealed class PtyReaper
    {
        /// <summary>
        /// How long a child may lie dead before its status is collected.
        /// </summary>
        private const int EINTR = 4;

        private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

        private static readonly Lazy<PtyReaper> InstanceHolder =
            new(() => new PtyReaper(), LazyThreadSafetyMode.ExecutionAndPublication);

        private readonly object gate = new();
        private readonly Dictionary<int, Entry> children = new();
        private readonly AutoResetEvent registered = new(false);
        private readonly int queue;

        private PtyReaper()
        {
            this.queue = pty_exit_queue();

            if (this.queue >= 0)
            {
                // No thread. The queue is a pollable descriptor, so waiting on it is one more
                // registration in the poll loop that already exists -- which is the point of this:
                // it removes the reaper thread rather than making it cleverer.
                _ = Task.Run(this.WatchAsync);
                return;
            }

            // The kernel cannot tell us, so we go back to asking. Linux below 5.3 has no
            // pidfd_open, and this is the path that serves it.
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

            if (this.queue >= 0 && pty_exit_watch(this.queue, pid) == 0)
            {
                return;
            }

            // Watching failed, and the likeliest reason is that the child has ALREADY exited --
            // both kernels report that as ESRCH, which is indistinguishable here from a real
            // failure. Collect it directly: an exit that happens between spawning and watching is
            // otherwise never reported, and WaitForExit would wait on a process already gone.
            this.CollectIfExited(pid);

            // Wake the fallback loop so a child that exits immediately is not held for a full
            // interval. Harmless when the kernel path is in use and nothing is waiting on it.
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

        /// <summary>
        /// Parks on the exit queue and collects whatever it reports. Holds no thread while waiting.
        /// </summary>
        private async Task WatchAsync()
        {
            var pids = new int[64];

            while (true)
            {
                try
                {
                    await PtyPoller.Instance.WaitReadableAsync(this.queue, CancellationToken.None).ConfigureAwait(false);

                    int count = pty_exit_drain(this.queue, pids, pids.Length);
                    for (var i = 0; i < count; i++)
                    {
                        this.CollectIfExited(pids[i]);
                    }
                }
                catch
                {
                    // Nothing useful to do with a failure here, and letting it escape would end the
                    // watch for every session in the process. Pause rather than spin.
                    await Task.Delay(Interval).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Reaps one pid if it has exited, and reports it.
        /// </summary>
        private void CollectIfExited(int pid)
        {
            Entry? entry;
            lock (this.gate)
            {
                if (!this.children.TryGetValue(pid, out entry))
                {
                    return;
                }
            }

            int status = 0;
            int result;
            int error = 0;
            try
            {
                result = entry!.Attempt(pid, ref status);
                error = result < 0 ? Marshal.GetLastPInvokeError() : 0;
            }
            catch
            {
                result = -1;
                error = 0;
            }

            if (result == 0 || (result < 0 && error == EINTR))
            {
                // Not collectable yet, or interrupted. Either way it stays registered.
                return;
            }

            lock (this.gate)
            {
                this.children.Remove(pid);
            }

            if (result < 0)
            {
                // ECHILD: something else collected it and there is no status to be had. Reporting
                // the zero would claim the child succeeded.
                return;
            }

            try
            {
                entry.OnExited(status);
            }
            catch
            {
                // A consumer's exit handler throwing is not ours to propagate.
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
                    int error = 0;
                    try
                    {
                        result = pair.Value.Attempt(pair.Key, ref status);
                        error = result < 0 ? Marshal.GetLastPInvokeError() : 0;
                    }
                    catch
                    {
                        // A torn-down connection can race us; drop it rather than take the process
                        // down from a background thread.
                        result = -1;
                        error = 0;
                    }

                    if (result == 0)
                    {
                        continue;
                    }

                    if (result < 0 && error == EINTR)
                    {
                        // A signal arrived during the call, which says nothing about the child.
                        // Dropping the registration here left it unreaped forever, and since this
                        // callback is what sets the terminated event, WaitForExit and ProcessExited
                        // would never complete. The blocking watcher retries on EINTR; so does this.
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
