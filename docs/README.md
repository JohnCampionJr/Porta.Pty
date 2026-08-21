# Notes on this fork

`JohnCampionJr/Porta.Pty` is a fork of [`tomlm/Porta.Pty`](https://github.com/tomlm/Porta.Pty),
made while using the library to drive terminal panes and long-running child processes in a desktop
application. It is published so the work is visible and can be taken, in whole or in part, by anyone
who wants it — the fixes are independent of each other and each one stands alone.

Everything here was measured rather than reasoned about. Where a number appears, it came from a run
on the hardware named.

## The four defects

**Every pseudoterminal leaked its controller fd.** `PtyStream` wraps the fd with `ownsHandle: false`
— correctly, since reader and writer are two streams over the same fd and either owning it would make
disposing both a double close. But nothing else closed it either: `pty_close` exists in the native
shim *and* in both platforms' `NativeMethods`, and had no callers anywhere in the library.

**Darwin's `forkpty` is not thread-safe.** Concurrent pty allocation fails outright on macOS. A pure-C
harness with no .NET involved, 24 threads calling `pty_spawn` at once, failed **5 of 24 on three
consecutive runs** — and **0 of 24 on three consecutive runs** with nothing changed but a mutex. It
resists diagnosis because it reports no usable errno: `forkpty` returns -1 and leaves `errno` at
**-6**, negative, so a kernel-style `-ENXIO` leaking out of the allocator rather than a POSIX errno.
`strerror` renders it "Undefined error: 0". It also looks exactly like fd exhaustion and is not —
measured mid-failure, the process held 30 descriptors against a limit of 1048576, and the system had
31 of 511 ptys in use. In practice: opening several terminals at once on macOS failed about one time
in five.

**On Windows, a child could outrun its job-object assignment.** `CreateProcessW` returned with the
process already running and `AssignProcessToJobObject` came afterwards, so a short-lived command could
be gone before the assignment executed — and an exited process cannot be assigned to a job. The spawn
then threw. Observed at 96 concurrent spawns: 95 of 96 delivered, one that never started. Fixed with
`CREATE_SUSPENDED` plus `ResumeThread` after the assignment, and the suspended child is terminated if
either step fails rather than left frozen in the process table.

**ConPTY's startup handshake was never answered**, which cost three seconds per pseudoconsole on the
out-of-band implementation. `VtIo::StartIfNeeded` sends a Primary Device Attributes query and then
blocks in `WaitUntilDA1(3000)` waiting for the reply; a consumer that only *reads* a PTY never answers.
See [`conpty-out-of-band.md`](conpty-out-of-band.md) — that document is mostly the record of how the
three seconds was found, because the elimination was the expensive part.

## The other changes

These are opinions rather than fixes, and are easier to leave than to take:

* **`net10.0` rather than `netstandard2.0`.** The reach was buying nothing here, and it meant the
  library was never compiled against the runtime its consumers run on — which matters in a codebase
  whose POSIX shim exists *because* a runtime version changed behaviour underneath it. It also turns
  on the platform-compatibility analyzer, which reported 116 unguarded Windows-only call sites.
* **CsWin32 instead of `Vanara.PInvoke.Kernel32`**, which takes the package's runtime dependency count
  to zero. The idea came from [Sylinko's fork](https://github.com/Sylinko/Porta.Pty), which did the
  same port.
* **MSTest on Microsoft Testing Platform instead of xunit.** House style, not an argument. It is also
  what surfaced two of the four defects above: MTP runs a module's tests in one process, where xunit
  spread classes across parallel collections, and four 24-spawn tests back to back in a single process
  is what exposed both the leaked fd and the `forkpty` race.
* **`ConcurrentSpawnTests`** is the measurement harness — concurrency, latency percentiles, cold vs
  warm, and the ConPTY A/B — rather than a conventional test file.

## Not included

The private working repository this was copied from carries packaging and CI specific to another
organisation. None of it is here.
