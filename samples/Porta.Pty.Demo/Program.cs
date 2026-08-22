// A minimal Porta.Pty consumer, used as the end-to-end check that the PACKAGE works.
//
// It exists because the test suite cannot answer the question it answers. The tests reference the
// library by project, which bypasses the .nupkg entirely: buildTransitive/ never applies, native
// assets are not resolved through runtimes/, and every packaging defect this repo has hit is
// invisible from there. The verify scripts pack the library and build this against a local feed, so
// what runs here is what a consumer actually gets.
//
// Exit codes: 0 pass, 1 the round trip failed, 2 the process could not be spawned at all.

using System.Runtime.InteropServices;
using System.Text;
using Porta.Pty;

int hold = 0;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] is "--hold" && int.TryParse(args[i + 1], out int seconds))
    {
        hold = seconds;
    }
}

bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
string rid = RuntimeInformation.RuntimeIdentifier;
string baseDir = AppContext.BaseDirectory;

Console.WriteLine($"PID={Environment.ProcessId}");
Console.WriteLine($"RID={rid}");
Console.WriteLine($"BASE={baseDir}");

// Report the staged natives rather than assert on them. Their absence is not necessarily fatal --
// Porta.Pty falls back to in-box conhost -- so this is diagnostic context for a failure below, and
// the verify scripts are what turn it into a pass/fail claim.
if (windows)
{
    foreach (string relative in new[]
    {
        "conpty.dll",
        Path.Combine("x64", "OpenConsole.exe"),
        Path.Combine("arm64", "OpenConsole.exe"),
    })
    {
        string full = Path.Combine(baseDir, relative);
        Console.WriteLine(File.Exists(full)
            ? $"STAGED {relative} ({new FileInfo(full).Length:N0} bytes)"
            : $"ABSENT {relative}");
    }
}

// A token the shell cannot produce by accident, so matching it means the bytes really made the round
// trip through the pty rather than the check having found its own echo of the command line.
string token = $"portapty-{Guid.NewGuid():N}";
string echo = windows ? $"@echo {token}" : $"echo {token}";

var options = new PtyOptions
{
    Name = "porta-pty-demo",
    Rows = 25,
    Cols = 80,
    Cwd = Environment.CurrentDirectory,
    App = windows
        ? Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe"
        : Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh",
    CommandLine = windows ? new[] { "/c", echo } : new[] { "-c", echo },
};

using var spawnCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

IPtyConnection pty;
try
{
    pty = await PtyProvider.SpawnAsync(options, spawnCts.Token);
}
catch (Exception ex)
{
    // The interesting failure on a mis-packaged consumer: DllNotFoundException for the POSIX shim, or
    // a ConPTY creation failure on Windows. Name it plainly rather than letting a stack trace imply
    // the library is broken when it is the packaging that is.
    Console.WriteLine($"FAIL spawn threw {ex.GetType().Name}: {ex.Message}");
    return 2;
}

using (pty)
{
    Console.WriteLine($"PTY_PID={pty.Pid}");
    Console.Out.Flush();

    var seen = new StringBuilder();
    var buffer = new byte[4096];
    using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    bool matched = false;

    try
    {
        while (!matched && !readCts.IsCancellationRequested)
        {
            int read = await pty.ReaderStream.ReadAsync(buffer.AsMemory(), readCts.Token);
            if (read <= 0)
            {
                break;
            }

            seen.Append(Encoding.UTF8.GetString(buffer, 0, read));
            matched = seen.ToString().Contains(token, StringComparison.Ordinal);
        }
    }
    catch (OperationCanceledException)
    {
        // Falls through to the failure report below; a timeout is a failed round trip.
    }

    if (hold > 0)
    {
        // Keeps the process alive so an external observer (Verify-ConPtyHost.ps1) can photograph the
        // process tree and see which console host was launched.
        Console.WriteLine($"HOLDING {hold}s");
        Console.Out.Flush();
        await Task.Delay(TimeSpan.FromSeconds(hold));
    }

    if (!matched)
    {
        Console.WriteLine("FAIL the token never came back through the pty");
        Console.WriteLine($"  read {seen.Length} bytes: {Escape(seen.ToString())}");
        return 1;
    }

    Console.WriteLine($"PASS round trip through the pty ({rid})");
    return 0;
}

// Control bytes are the norm here -- a pty carries the shell's escape sequences alongside the text --
// so a failure dump has to be readable rather than raw.
static string Escape(string raw)
{
    var sb = new StringBuilder();
    foreach (char c in raw.Length > 400 ? raw[..400] : raw)
    {
        sb.Append(c switch
        {
            '\r' => "\\r",
            '\n' => "\\n",
            _ => c < ' ' ? $"\\x{(int)c:x2}" : c.ToString(),
        });
    }

    return sb.ToString();
}
