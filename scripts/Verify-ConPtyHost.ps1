#Requires -Version 5.1
<#
.SYNOPSIS
    Verifies which console host a Porta.Pty consumer actually launches at RUNTIME.

.DESCRIPTION
    Verify-ConPtyConsumerStaging.ps1 proves the files are on disk. This proves they are used, which is
    a different claim: conpty.dll with no host available does not fail, it falls back to in-box conhost
    silently. Both cases look identical from the consumer's side - same API, same output, no warning.

    So the check is the process tree. Out-of-band ConPTY launches OpenConsole.exe as a descendant of
    the consumer process; the in-box path launches conhost.exe.

    -Mode inbox is the control, and it matters. A run that finds OpenConsole.exe proves nothing on its
    own unless the opposite setting produces conhost.exe - otherwise the check might simply be looking
    at something unrelated on the box.

.PARAMETER Rid
    Runtime identifier for the scratch consumer. On an ARM64 box run BOTH win-arm64 and win-x64: the
    x64 process runs under emulation and must get the x64 host.

.PARAMETER NoRid
    Build the consumer with NO RuntimeIdentifier — the portable layout, where native assets stay under
    runtimes/win-<arch>/native/ instead of being flattened into the app root.

    This decides whether Porta.Pty can be consumed by a RID-independent project and still get out-of-band
    ConPTY. The library's resolver finds conpty.dll there and the package stages OpenConsole.exe beside
    it, but whether conpty.dll LOOKS beside itself for its host is documented nowhere in the ConPTY
    package — only a process census answers it.

    Answered on Windows ARM64: it does. OpenConsole.exe in the tree with the default, conhost.exe with
    -Mode inbox as the control. Kept as a regression guard precisely because the rule it depends on is
    undocumented and could change under us.

.PARAMETER Mode
    auto  - the library's default (out-of-band, falling back to in-box if conpty.dll is absent)
    oob   - force out-of-band; throws rather than falling back, so a missing conpty.dll is visible
    inbox - force kernel32; the control case, expected to produce conhost.exe

.EXAMPLE
    ./scripts/Verify-ConPtyHost.ps1 -Rid win-arm64
    ./scripts/Verify-ConPtyHost.ps1 -Rid win-arm64 -Mode inbox   # control
    ./scripts/Verify-ConPtyHost.ps1 -Rid win-x64                 # emulated
#>
[CmdletBinding()]
param(
    [string] $Rid = 'win-arm64',
    [ValidateSet('auto', 'oob', 'inbox')]
    [string] $Mode = 'auto',
    [string] $Version = '1.0.0-verify',
    [switch] $NoRid,
    [int] $HoldSeconds = 20,
    [switch] $KeepScratch
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) "portapty-host-$([guid]::NewGuid().ToString('N').Substring(0,8))"
$consumer = Join-Path $scratch 'consumer'
$feed = Join-Path $scratch 'feed'

function Get-Descendants {
    # Walk the tree rather than checking direct children only: the host is launched by conpty.dll,
    # and which level of the tree it lands on is an implementation detail we should not assert on.
    param([int] $RootPid)
    $all = Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId, Name
    $seen = @{}
    $queue = [System.Collections.Queue]::new()
    $queue.Enqueue($RootPid)
    while ($queue.Count) {
        $current = $queue.Dequeue()
        foreach ($p in $all | Where-Object ParentProcessId -eq $current) {
            if (-not $seen.ContainsKey($p.ProcessId)) {
                $seen[$p.ProcessId] = $p
                $queue.Enqueue($p.ProcessId)
            }
        }
    }
    $seen.Values
}

try {
    New-Item -ItemType Directory -Force -Path $feed, $consumer | Out-Null

    Write-Host "`n>> packing Porta.Pty $Version" -ForegroundColor Cyan
    # GeneratePackageOnBuild=false is required: the library sets it true, and with it set the Pack
    # target does not depend on Build, so pack takes whatever is already in bin/Release. CI builds
    # Debug, so that is empty and pack dies with NU5026 naming Porta.Pty.dll. Passes locally after
    # any Release build, which is what makes it a CI-only failure.
    & dotnet pack (Join-Path $repo 'src\Porta.Pty\Porta.Pty.csproj') -c Release -o $feed `
        -p:Version=$Version -p:GeneratePackageOnBuild=false --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "pack failed" }

    # samples/Porta.Pty.Demo, the same consumer the other checks use. --hold keeps it alive after the
    # round trip so the process tree can be photographed while a pty is genuinely open.
    $layout = if ($NoRid) { 'portable, no RID' } else { $Rid }
    Write-Host ">> building samples/Porta.Pty.Demo against the packed library ($layout)" -ForegroundColor Cyan
    $demo = Join-Path $repo 'samples\Porta.Pty.Demo\Porta.Pty.Demo.csproj'
    $packages = Join-Path $scratch 'packages'

    # Sources via a generated config, not --source. On Windows the second --source argument reached
    # NuGet as a RELATIVE PATH rather than a URI, and it resolved it against the project directory:
    #   NU1301: The local source '...\samples\Porta.Pty.Demo\https:\api.nuget.org\v3\index.json' doesn't exist.
    # A config file has no such ambiguity, and --configfile also stops any NuGet.config in the tree from
    # contributing sources, so the check restores from exactly these two.
    $config = Join-Path $scratch 'nuget.config'
    @"
<configuration>
  <packageSources>
    <clear />
    <add key="verify-local" value="$feed" />
    <add key="nuget" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@ | Set-Content $config

    # UseCurrentRuntimeIdentifier has to be turned off explicitly: the sample sets it so a plain
    # `dotnet run` is honest about what a consumer gets, and it would otherwise reintroduce a RID here.
    # [string[]] is load-bearing. `$x = if (...) { @('one') }` UNWRAPS a single-element array to a
    # bare string, and splatting a string with @x enumerates its CHARACTERS — MSBuild received
    # '-', 'p', ':', 'U', ... as separate arguments. The cast keeps it an array in both branches.
    [string[]] $ridArgs = if ($NoRid) { @('-p:UseCurrentRuntimeIdentifier=false') } else { @("-p:RuntimeIdentifier=$Rid") }

    & dotnet restore $demo -p:PortaPtyPackageVersion=$Version @ridArgs `
        --configfile $config --packages $packages -v q
    if ($LASTEXITCODE -ne 0) { throw "consumer restore failed" }

    & dotnet build $demo -p:PortaPtyPackageVersion=$Version @ridArgs `
        --packages $packages --no-restore -c Release -o (Join-Path $consumer 'out') --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "consumer build failed" }

    $exe = Join-Path $consumer 'out\Porta.Pty.Demo.exe'
    $log = Join-Path $scratch 'consumer.log'

    Write-Host ">> running with PORTAPTY_CONPTY=$Mode" -ForegroundColor Cyan
    $env:PORTAPTY_CONPTY = if ($Mode -eq 'auto') { $null } else { $Mode }
    $proc = Start-Process -FilePath $exe -ArgumentList '--hold', $HoldSeconds -PassThru -NoNewWindow -RedirectStandardOutput $log

    # Give the spawn time to land before photographing the tree.
    Start-Sleep -Seconds 5
    $descendants = Get-Descendants -RootPid $proc.Id
    $hosts = $descendants | Where-Object { $_.Name -in @('OpenConsole.exe', 'conhost.exe') }

    Write-Host "`n  consumer said:" -ForegroundColor Cyan
    Get-Content $log -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "    $_" }

    Write-Host "  process tree under PID $($proc.Id):" -ForegroundColor Cyan
    if ($descendants) { $descendants | ForEach-Object { Write-Host ("    {0,-8} {1}" -f $_.ProcessId, $_.Name) } }
    else { Write-Host "    (none)" }

    try { $proc | Stop-Process -Force -ErrorAction SilentlyContinue } catch { }

    Write-Host ''
    $openConsole = @($hosts | Where-Object Name -eq 'OpenConsole.exe')
    $conhost = @($hosts | Where-Object Name -eq 'conhost.exe')

    if ($Mode -eq 'inbox') {
        if ($conhost.Count -and -not $openConsole.Count) {
            Write-Host "PASSED (control) - in-box path launched conhost.exe, as expected" -ForegroundColor Green; exit 0
        }
        Write-Host "UNEXPECTED (control) - PORTAPTY_CONPTY=inbox should launch conhost.exe" -ForegroundColor Red; exit 1
    }

    if ($openConsole.Count) {
        Write-Host "PASSED ($layout, $Mode) - out-of-band host OpenConsole.exe is what ran" -ForegroundColor Green; exit 0
    }
    if ($conhost.Count) {
        Write-Host "FAILED ($layout, $Mode) - fell back to in-box conhost.exe." -ForegroundColor Red
        Write-Host "  This is the silent failure: conpty.dll loaded but found no host to launch." -ForegroundColor Red
        Write-Host "  Run Verify-ConPtyConsumerStaging.ps1 -Rid $Rid to see which file is missing." -ForegroundColor Red
        exit 1
    }
    Write-Host "INCONCLUSIVE - no console host appeared under the consumer; did the pty spawn?" -ForegroundColor Yellow
    exit 2
}
finally {
    $env:PORTAPTY_CONPTY = $null
    if ($KeepScratch) { Write-Host "`nscratch kept at $scratch" -ForegroundColor DarkGray }
    elseif (Test-Path $scratch) { Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue }
}
