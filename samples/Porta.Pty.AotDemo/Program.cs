using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Porta.Pty;

if (RuntimeFeature.IsDynamicCodeSupported || RuntimeFeature.IsDynamicCodeCompiled)
{
    Console.Error.WriteLine("FAIL dynamic code is available; this is not a Native AOT executable.");
    return 1;
}

bool windows = OperatingSystem.IsWindows();
string rid = RuntimeInformation.RuntimeIdentifier;
string baseDir = AppContext.BaseDirectory;

Console.WriteLine($"PID={Environment.ProcessId}");
Console.WriteLine($"RID={rid}");
Console.WriteLine("NATIVE_AOT=true");

if (windows)
{
    foreach (string relative in new[]
    {
        "conpty.dll",
        Path.Combine("x64", "OpenConsole.exe"),
        Path.Combine("arm64", "OpenConsole.exe"),
    })
    {
        string fullPath = Path.Combine(baseDir, relative);
        Console.WriteLine(File.Exists(fullPath) ? $"STAGED {relative}" : $"ABSENT {relative}");
    }
}

string token = $"portapty-aot-{Guid.NewGuid():N}";
var options = new PtyOptions
{
    Name = "porta-pty-aot-demo",
    Rows = 25,
    Cols = 80,
    Cwd = Environment.CurrentDirectory,
    App = windows
        ? Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe"
        : Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh",
    CommandLine = windows
        ? ["/c", $"@echo {token}"]
        : ["-c", $"echo {token}"],
};

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

try
{
    using IPtyConnection pty = await PtyProvider.SpawnAsync(options, timeout.Token);
    var output = new StringBuilder();
    var buffer = new byte[4096];

    while (!output.ToString().Contains(token, StringComparison.Ordinal))
    {
        int bytesRead = await pty.ReaderStream.ReadAsync(buffer, timeout.Token);
        if (bytesRead == 0)
        {
            break;
        }

        output.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
    }

    if (!output.ToString().Contains(token, StringComparison.Ordinal))
    {
        Console.Error.WriteLine("FAIL the token never came back through the PTY.");
        return 1;
    }
}
catch (Exception exception)
{
    Console.Error.WriteLine($"FAIL {exception.GetType().Name}: {exception.Message}");
    return 1;
}

Console.WriteLine($"PASS Native AOT round trip through the PTY ({rid})");
return 0;
