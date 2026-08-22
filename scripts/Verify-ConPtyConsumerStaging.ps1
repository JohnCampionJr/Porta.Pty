#Requires -Version 5.1
<#
.SYNOPSIS
    Verifies that a NuGet CONSUMER of Porta.Pty receives the out-of-band ConPTY host.

.DESCRIPTION
    Microsoft.Windows.Console.ConPTY splits its payload across two locations, and only one half
    travels transitively:

        runtimes/win-<arch>/native/conpty.dll          ordinary native asset - flows to anyone
        build/native/runtimes/<arch>/OpenConsole.exe   staged by the package's build/ targets

    NuGet imports a package's build/ folder for a DIRECT PackageReference only, and ConPTY ships no
    buildTransitive/. Porta.Pty references it directly, so THIS repo's own build and tests are not
    evidence either way - they stage the host through the direct reference and pass regardless.

    The consumer case is what this script measures, and its failure mode is silent: conpty.dll with
    no host to launch does not error, it falls back to in-box conhost. Nothing in the build output
    or the run says so. That is why this is a script and not a code review.

.PARAMETER Rid
    Runtime identifier for the scratch consumer. Run it for BOTH win-arm64 and win-x64 on an ARM64
    box: an x64 process runs there under emulation and needs the x64 host, and that is the case a
    flat copy silently broke.

.EXAMPLE
    ./scripts/Verify-ConPtyConsumerStaging.ps1 -Rid win-arm64
    ./scripts/Verify-ConPtyConsumerStaging.ps1 -Rid win-x64
#>
[CmdletBinding()]
param(
    [string] $Rid = 'win-arm64',
    [string] $Version = '1.0.0-verify',
    [switch] $KeepScratch
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) "portapty-verify-$([guid]::NewGuid().ToString('N').Substring(0,8))"
$feed = Join-Path $scratch 'feed'
$consumer = Join-Path $scratch 'consumer'
$failures = @()

function Assert-Staged {
    param([string] $Root, [string] $Label)
    # All three, every time. conpty.dll alone is the exact shape of the bug: it is what a consumer
    # got before buildTransitive/Porta.Pty.targets existed, and it looks like success.
    foreach ($rel in @('conpty.dll', 'x64\OpenConsole.exe', 'arm64\OpenConsole.exe')) {
        $path = Join-Path $Root $rel
        if (Test-Path $path) {
            Write-Host ("    OK      {0,-24} {1,10:N0} bytes" -f $rel, (Get-Item $path).Length) -ForegroundColor Green
        }
        else {
            Write-Host ("    MISSING {0}" -f $rel) -ForegroundColor Red
            $script:failures += "$Label`: $rel"
        }
    }
    # Distinct sizes, because the two hosts are different binaries. Equal sizes means both entries
    # resolved to the same file - the TargetPath-instead-of-DestinationSubDirectory bug, where the
    # second host overwrites the first and a win-x64 consumer ends up holding the ARM64 one.
    $x64 = Join-Path $Root 'x64\OpenConsole.exe'
    $arm = Join-Path $Root 'arm64\OpenConsole.exe'
    if ((Test-Path $x64) -and (Test-Path $arm) -and (Get-Item $x64).Length -eq (Get-Item $arm).Length) {
        Write-Host "    SUSPECT x64 and arm64 hosts are the same size - one may have overwritten the other" -ForegroundColor Red
        $script:failures += "$Label`: hosts are not distinct binaries"
    }
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

    # samples/Porta.Pty.Demo, the same consumer Linux and macOS run through scripts/verify-consumer.sh.
    # PortaPtyPackageVersion swaps its ProjectReference for a real PackageReference — a ProjectReference
    # bypasses the .nupkg and so cannot observe any of this. --source keeps the local feed out of any
    # committed NuGet.config, so a plain clone is unaffected.
    Write-Host ">> building samples/Porta.Pty.Demo against the packed library ($Rid)" -ForegroundColor Cyan
    $demo = Join-Path $repo 'samples\Porta.Pty.Demo\Porta.Pty.Demo.csproj'
    $packages = Join-Path $scratch 'packages'

    & dotnet restore $demo -p:PortaPtyPackageVersion=$Version -p:RuntimeIdentifier=$Rid `
        --source $feed --source 'https://api.nuget.org/v3/index.json' --packages $packages -v q
    if ($LASTEXITCODE -ne 0) { throw "consumer restore failed" }

    & dotnet build $demo -p:PortaPtyPackageVersion=$Version -p:RuntimeIdentifier=$Rid `
        --packages $packages --no-restore -c Release -o (Join-Path $consumer 'out') --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "consumer build failed" }
    Write-Host "  build output:" -ForegroundColor Cyan
    Assert-Staged -Root (Join-Path $consumer 'out') -Label 'build'

    # Publish separately: it is a different item pipeline (CopyToPublishDirectory), so passing on
    # build says nothing about it.
    & dotnet publish $demo -p:PortaPtyPackageVersion=$Version -p:RuntimeIdentifier=$Rid `
        --packages $packages --no-restore -c Release -o (Join-Path $consumer 'pub') --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "consumer publish failed" }
    Write-Host "  publish output:" -ForegroundColor Cyan
    Assert-Staged -Root (Join-Path $consumer 'pub') -Label 'publish'

    # Files on disk is the weaker claim. Run the thing: a pty round trip is what a consumer actually
    # needs, and it is the only check that would notice a staged-but-unusable host.
    #
    # Only when this box can execute that RID, though. An x64 binary runs on ARM64 Windows under
    # emulation, but not the reverse — so an ARM64 leg on an x64 runner (which is what GitHub's
    # windows-latest is) can verify staging and nothing more. Skipping is reported rather than passed
    # over: a run that quietly checked less than it looks like it did is worse than one that says so.
    $hostArch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    $ridArch = $Rid.Split('-')[-1].ToLowerInvariant()
    $canRun = ($ridArch -eq $hostArch) -or ($hostArch -eq 'arm64' -and $ridArch -eq 'x64')

    if ($canRun) {
        Write-Host "  running the demo:" -ForegroundColor Cyan
        & (Join-Path $consumer 'out\Porta.Pty.Demo.exe')
        if ($LASTEXITCODE -ne 0) {
            Write-Host "    demo exited $LASTEXITCODE" -ForegroundColor Red
            $failures += "demo round trip failed (exit $LASTEXITCODE)"
        }
    }
    else {
        Write-Host "  SKIPPED the round trip - a $ridArch binary cannot run on a $hostArch host" -ForegroundColor Yellow
        Write-Host "  (staging above was still verified)" -ForegroundColor Yellow
    }
}
finally {
    if ($KeepScratch) { Write-Host "`nscratch kept at $scratch" -ForegroundColor DarkGray }
    elseif (Test-Path $scratch) { Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue }
}

Write-Host ''
if ($failures.Count) {
    Write-Host "FAILED ($Rid)" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}
Write-Host "PASSED ($Rid) - a package consumer receives conpty.dll and both hosts" -ForegroundColor Green
