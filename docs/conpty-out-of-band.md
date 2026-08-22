# Microsoft's out-of-band ConPTY (adopted)

Windows ships ConPTY in `kernel32`, backed by whatever `conhost.exe` the OS happens to have. Microsoft
also ships it out of band as [`Microsoft.Windows.Console.ConPTY`][pkg] — `conpty.dll` plus
`OpenConsole.exe`, the same implementation Windows Terminal carries.

Both are wired up here and selected at **runtime** by `PORTAPTY_CONPTY=oob`.

[pkg]: https://www.nuget.org/packages/Microsoft.Windows.Console.ConPTY

## The headline

Out-of-band ConPTY appeared to cost **~3.0 seconds per pseudoconsole**. It does not. It costs **9 ms**,
which is *faster* than in-box, and the three seconds was a handshake we were failing to answer.

```
                                    in-box              out-of-band
before answering the handshake   [27,15,13,13,13]   [3016,3012,3011,3013,3019]
after  answering the handshake   [27,15,13,13,13]   [  15,   9,   9,   8,   8]
```

**ConPTY asks the terminal what it is, and waits three seconds for a reply.** From Microsoft's own
source, `VtIo::StartIfNeeded` ([src/host/VtIo.cpp][vtio]) sends, on startup:

```
\x1b[6n        Device Status Report — cursor position
\x1b[c         Primary Device Attributes (DA1) — "what terminal are you?"
\x1b[?1004h    Focus Event Mode
\x1b[?9001h    Win32 Input Mode
```

and then calls `_pVtInputThread->WaitUntilDA1(3000)`, which blocks for up to **3000 ms** waiting for the
DA1 response. On timeout it transitions to `StartupFailed` and continues anyway. A consumer that only
READS a PTY never answers, so it pays that timeout on every single pseudoconsole.

[vtio]: https://github.com/microsoft/terminal/blob/main/src/host/VtIo.cpp

The fix is to answer. Immediately after spawning, write a well-formed DA1 response to the PTY's input:

```
\x1b[?1;2c        DA1 — VT100 with Advanced Video Option
```

Answering *reactively*, on observing `ESC[c` in the output, was measured and does **not** work — it
races the handshake and usually arrives after `WaitUntilDA1` has already given up. Send it up front,
unconditionally, before reading anything.

This is also why VS Code's terminal shows no such stall on OpenConsole: xterm.js answers DA1 in
milliseconds. Nothing about the implementation is slow.

## Adopted, and what a consumer has to know

**Out-of-band is now the default**, and `PtyProvider` answers the handshake itself so that consumers do
not have to. That second half is the load-bearing one: adopting out-of-band without it would hand every
read-only consumer a three-second stall per pseudoconsole.

Three things about the answer, each learned by getting it wrong first:

* It is sent **unconditionally and immediately**, not on observing `ESC[c`. Reacting to the query races
  the handshake and usually arrives after `WaitUntilDA1` has given up. Measured: reactive still cost
  ~3.01 s.
* It is sent **only on the out-of-band path.** In-box emits no query, so the same bytes would not be
  consumed by a handshake and would reach the child as keyboard input.
* It is **never fatal.** If the write fails the terminal still works; it just pays the timeout it would
  have paid anyway.

**The default falls back to in-box when `conpty.dll` is not beside the assembly.** That is what makes
defaulting safe — a consumer without the ConPTY package, or without a RID-specific output, degrades
instead of throwing `DllNotFoundException` on its first terminal. `PORTAPTY_CONPTY=inbox` forces
kernel32; `PORTAPTY_CONPTY=oob` forces conpty.dll and deliberately does **not** fall back, because a
consumer that believes it is set up would rather find out that it is not.

`PtyProvider.PseudoConsoleImplementation` reports what was actually chosen. Do not infer it from the
environment variable — that stops being the answer exactly when the fallback fires.

### For a consumer that wants out-of-band

The library ships the natives, but the SDK's default layout puts them where `conpty.dll` cannot find
its host. A consuming application needs a **RID-specific output** (`UseCurrentRuntimeIdentifier`, a
`RuntimeIdentifier`, or a RID-specific publish), which flattens `conpty.dll` into the app root beside
the `x64/` and `arm64/` host folders the package stages. Without it the fallback quietly selects in-box,
which is correct but is not what was asked for — check `PseudoConsoleImplementation` if it matters.

### For a consumer that only reads

Its two PTY consumers differ, and only one would ever have been affected: **terminal panes** have a real
emulator that answers DA1 like any terminal, while **run targets** (`the downstream task runner`) only ever read. The
library answering means neither has to change.

## How the three seconds was found

Recorded because the elimination was most of the work, and every step of it was a measurement:

| ruled out | how |
|---|---|
| process launch | a 10 ms process census showed `OpenConsole.exe` appearing immediately, not 2.9 s in |
| the child exiting | a child that stays alive after writing stalled identically |
| concurrency | n=1, n=8 and n=24 all cost ~3.0 s |
| the machine | ARM64-emulated and native x64 agreed to within noise |
| Defender / SmartScreen | a scan would be a launch cost, and the launch was immediate |
| our code vs Sylinko's | their spawn, connection and interop paths are functionally identical |

What identified it in the end was dumping every byte with arrival times:

```
in-box     13ms   \e[?9001h\e[?1004h
           15ms   <the child's output>

oob        14ms   \e[1t
           15ms   \e[c \e[?1004h \e[?9001h      <- the question
         3015ms   \e[?7l                        <- gives up, exactly 3s later
         3016ms   <the child's output>
```

A flat ~3.01 s with ±5 ms of variance was the tell throughout: real work does not land within five
milliseconds of exactly three seconds, eight times running. That is a timeout, and timeouts are waiting
for something.

## The original measurement, for the record — all of it with the handshake unanswered

Everything in this section predates the discovery above and is kept because the numbers are real; they
are simply all measuring the same unanswered DA1 handshake.

Native x64 (Blacksmith `windows-2025`), 20 samples per cell, median p50 time from spawn to the child's
output arriving. `DedicatedThread` reader throughout, so this measures ConPTY rather than thread-pool
starvation.

| spawns | shell | in-box | out-of-band | ratio |
|---|---|---|---|---|
| **1** | `cmd /c echo` | **27 ms** | **3048 ms** | **113x** |
| **1** | pwsh `-EncodedCommand` | **142 ms** | **3138 ms** | **22x** |
| 24 | `cmd /c echo` | 75 ms | 3096 ms | 41x |
| 24 | pwsh `-EncodedCommand` | 1336 ms | 4207 ms | 3.2x |

No output was lost by either implementation, at either concurrency, on any machine.

The shape matters more than the ratios: **out-of-band costs ~3.0 s whether you open one pseudoconsole
or twenty-four**, so twenty-four of them cost no more than one. That looked at first like a
once-per-process cost; the sequential measurement below shows it is not — it is per-pseudoconsole, and
concurrent spawns simply pay it in parallel.

The same comparison on ARM64 winrunners (four contended boxes, x64 SDK so `OpenConsole` runs emulated)
gave 8-15x with near-identical *absolute* out-of-band numbers, which is what showed the cost does not
depend on the machine.

## Why that decides it

A consumer's PTY surface is usually small. In the case that motivated this work it was two call sites:

* a task runner that starts dev servers and only ever READS their output
* an integrated terminal pane

`git`, `gh` and every agent CLI use `Process.Start`, not a PTY — which matters, since a test run forks
~2,600 git processes and none of them touch ConPTY.

So the realistic concurrency is one terminal at a time, and the number that decides this is n=1: a user
would wait **~3 seconds for their first terminal instead of 27 ms**. Disqualifying for a terminal UI,
against an in-box implementation that showed no defect in anything measured — including 80 for 80
delivery at the concurrency where output was being lost.

## Cold vs warm: the cost is per-pseudoconsole, and it is a timeout

Sequential spawns, eight in one process, native x64, minimal shell:

```
in-box   first=16ms    rest_p50=7ms     all=[16, 7, 7, 7, 7, 7, 8, 9]
oob      first=3033ms  rest_p50=3012ms  all=[3033,3010,3013,3014,3011,3014,3012,3009]
```

Two things follow, and the second is the more interesting.

**The out-of-band cost is per-pseudoconsole, not per-process.** In-box shows a textbook warm-up — 16 ms
cold, 7 ms warm. Out-of-band is flat. So a warm-up at application startup would hide nothing: every
terminal a user opens pays the full ~3 s. That was the open question after the concurrent A/B, where
~3.0 s at both n=1 and n=24 fit a per-process cost equally well, and it is now closed.

**~3.01 s with 5 ms of variance across eight spawns is a TIMEOUT, not work.** Real work does not land
within five milliseconds of exactly three seconds, eight times running. Something in the out-of-band
path waits out a fixed three-second timeout and then proceeds successfully — a handshake with
`OpenConsole.exe` that never arrives and falls back, most plausibly.

### What the stall is not

The timeout was then narrowed by elimination. Each of these is a measurement, not an argument:

| ruled out | how |
|---|---|
| process launch | a 10 ms process census shows `OpenConsole.exe` appearing immediately, not 2.9 s in |
| the child exiting | a child that stays alive after writing stalls identically — `[3018,3012,3021,3011,3012]` |
| concurrency | n=1, n=8 and n=24 all cost ~3.0 s |
| the machine | ARM64-emulated and native x64 agree to within noise |
| our code | Sylinko's fork's spawn, connection and interop paths are functionally identical to ours |

What remains is **a fixed ~3.01 s wait, ±5 ms, inside the `conpty.dll` ↔ `OpenConsole.exe` handshake**,
paid once per pseudoconsole regardless of workload.

The long-lived result is the one that mattered and it went the unhelpful way. Had the stall been tied
to the child exiting, it would never have mattered here — both consumers are long-lived, terminal
panes by definition and dev servers by nature. It is not, so it would be paid by every terminal a user
opens.

The root cause is not identified. Narrowing further wants ETW or Process Monitor on a real Windows box,
which CI does not give cheaply, and the answer would not change the recommendation: in-box has no defect
to fix and costs 8 ms. Recorded at this depth because the elimination is the expensive part and it is
done — anyone reopening this starts from "find the 3-second wait in the handshake", not from scratch.

## Two caveats, if this is ever revisited

* ~~The data cannot separate "fixed per process" from "fixed per pseudoconsole".~~ **Resolved** by the
  sequential measurement above: it is per-pseudoconsole, and a warm-up would hide nothing.
* **Defender/SmartScreen was the leading suspect and is now unlikely.** Scanning an unsigned 1 MB binary
  would be a launch cost, and the census puts `OpenConsole.exe` on screen immediately while the 3 s
  elapses afterwards. Not fully excluded — a scan could gate the handshake rather than the launch — but
  it no longer fits well.
* **The measurements are all on CI runners.** A signed, installed application on a normal desktop is a
  different environment, and this stall is the kind of thing that could be specific to one. Worth
  re-measuring there before treating the number as a property of the implementation.

## Making it actually run: three things, each silent on its own

This took four attempts. The first three produced *plausible, near-identical latency tables* that were
entirely worthless, because the "out-of-band" arm was quietly running conhost the whole time.

1. **`ConptyRequiresx64Host` / `ConptyRequiresARM64Host` must be set**, or no host is staged at all.
   The package derives `ConptyNativePlatform` from `PlatformTarget`, which for an ordinary managed
   library is `AnyCPU` — matching none of its `x86`/`x64`/`ARM64` branches. It then copies `conpty.dll`
   and nothing else, and `conpty.dll` with no host to launch **falls back to conhost without erroring**.
   Set the flags directly rather than via `ConptyNativePlatform`: they are read by the package's
   `.targets`, which imports after the csproj, whereas its `.props` has already run.
2. **`[DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]` on the `conpty.dll`
   imports.** Recent Windows 11 builds ship their own `C:\Windows\System32\conpty.dll`, so an
   unqualified `DllImport("conpty.dll")` can resolve the OS copy — which is backed by conhost, i.e.
   exactly the implementation this path exists to be an alternative to.
3. **A RID-specific output** (`UseCurrentRuntimeIdentifier`). `conpty.dll` launches `OpenConsole.exe`
   from an `<arch>/` subdirectory of **its own** directory, and the package stages the hosts at
   `<output>/x64` and `<output>/arm64`. The SDK's default layout puts the DLL under
   `runtimes/win-<rid>/native`, where no such subdirectory exists. This is also what makes (2)
   meaningful rather than inert, since the DLL is only then actually in the assembly directory.

Any consumer wanting the out-of-band path needs all three, including the RID-specific output.

## Verifying it is really out-of-band

**Do not trust the environment variable.** Count processes: in-box spawns a `conhost.exe` per
pseudoconsole, out-of-band spawns an `OpenConsole.exe` per pseudoconsole.

```
peakOpenConsole=0        → conhost, whatever PORTAPTY_CONPTY said
peakOpenConsole=n        → genuinely out-of-band for n pseudoconsoles
```

The A/B harness — process census, per-implementation runs, and the summary-file capture that MTP
requires (it surfaces a test's console output only when the test *fails*, and every number here comes
from a passing run) — is preserved commented-out in `.github/workflows/ci.yml`. The winrunner version,
which runs it four-wide on contended self-hosted hardware, lives in the downstream consumer's repo.

## Payload, for the record

Smaller than usually assumed: `conpty.dll` is 88-108 KB per architecture and `OpenConsole.exe` is
~1.0-1.1 MB per architecture, one-time. The process model is unchanged — one host process per
pseudoconsole either way, just `OpenConsole.exe` instead of `conhost.exe`.

## What a consumer of the package has to do: nothing

Worth stating explicitly, because it was not true until it was tested.

`Microsoft.Windows.Console.ConPTY` splits its payload across two locations, and only one half travels
transitively:

| path in the ConPTY package | reaches a transitive consumer? |
|---|---|
| `runtimes/win-<arch>/native/conpty.dll` | yes — ordinary native asset resolution |
| `build/native/runtimes/<arch>/OpenConsole.exe` | **no** — `build/` imports for a DIRECT reference only |

Porta.Pty references ConPTY directly, so its own build stages the host and its own tests pass. A consumer
of *Porta.Pty* got `conpty.dll` and nothing else — and `conpty.dll` with no host to launch does not fail.
It falls back to in-box conhost **silently**: no error, no warning, just the behaviour the out-of-band
package was taken to avoid. Nothing in a consumer's build or run output says so.

Measured by packing the library and building a `win-x64` consumer against it — the output directory held
`conpty.dll` alone.

`buildTransitive/Porta.Pty.targets` closes it, so there is no metadata for a consumer to discover. Two
details in there are load-bearing:

* **The ConPTY package directory is derived from the resolved `conpty.dll`**, not composed from
  `NuGetPackageRoot` plus a version literal. The consumer's graph decides which ConPTY version wins — a
  direct reference or a unification can move it — and a hard-coded version resolves to a path that
  quietly does not exist. Walking up from the file that actually resolved cannot disagree with it.
* **`DestinationSubDirectory`, not `TargetPath`.** `_CopyFilesMarkedCopyLocal` builds its destination as
  `$(OutDir)%(DestinationSubDirectory)%(Filename)%(Extension)` and never reads `TargetPath`. With
  `TargetPath` both hosts copy flat and the second overwrites the first: a `win-x64` consumer ended up
  with a single `OpenConsole.exe` at the output root, and it was the **ARM64** one.

Setting `ConptyRequiresx64Host` / `ConptyRequiresARM64Host` from a shipped `.props` cannot fix this — the
targets that read those flags are precisely the ones that never import.

To opt out (a consumer staging the host itself), set `PortaPtyStageConPtyHost=false`.
