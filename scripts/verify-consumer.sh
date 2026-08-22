#!/usr/bin/env bash
# Verifies that a NuGet CONSUMER of Porta.Pty works end to end on Linux/macOS.
#
# The test suite cannot answer this. It references the library by project, which bypasses the .nupkg
# entirely: native assets are not resolved through runtimes/, buildTransitive/ never applies, and any
# packaging defect is invisible from there. This packs the library and builds samples/Porta.Pty.Demo
# against a local feed, so what runs is what a consumer actually gets.
#
# Usage: scripts/verify-consumer.sh [version]
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version="${1:-1.0.0-verify}"
scratch="$(mktemp -d "${TMPDIR:-/tmp}/portapty-verify-XXXXXX")"
trap 'rm -rf "$scratch"' EXIT

feed="$scratch/feed"
mkdir -p "$feed"

# GeneratePackageOnBuild=false is required, not tidiness. The library sets it to true, and with it set
# the Pack target does NOT depend on Build (otherwise packing would recurse). `dotnet pack -c Release`
# then packs whatever is already in bin/Release -- and CI builds Debug, so there is nothing there:
#   NU5026: The file '.../bin/Release/net10.0/Porta.Pty.dll' to be packed was not found on disk.
# It passes locally after any Release build, which is exactly what makes it a CI-only failure.
echo ">> packing Porta.Pty $version"
dotnet pack "$repo/src/Porta.Pty/Porta.Pty.csproj" -c Release -o "$feed" \
    -p:Version="$version" -p:GeneratePackageOnBuild=false --nologo -v q

# The shim has to be IN the package. It is packed with Condition="Exists(...)", so a tree where
# src/Porta.Pty.Native/build.sh has not run produces a package that is silently missing its native and
# fails only in a consumer, at runtime, as DllNotFoundException.
echo ">> checking the package carries a native shim"
# Listed once into a variable, deliberately. `unzip -l ... | grep -q` under `set -o pipefail` reports
# failure even on a match: grep -q exits at the first hit, unzip takes SIGPIPE, and pipefail propagates
# that. The check then "fails" on a perfectly good package.
listing="$(unzip -l "$feed/Porta.Pty.$version.nupkg")"
if ! printf '%s\n' "$listing" | grep -E 'runtimes/(linux|osx)-[a-z0-9]+/native/libporta_pty\.(so|dylib)'; then
    echo "::error::the packed library contains no POSIX shim - did src/Porta.Pty.Native/build.sh run?"
    printf '%s\n' "$listing" | sed 's/^/    /'
    exit 1
fi

# PortaPtyPackageVersion swaps the sample's ProjectReference for a real PackageReference. --source on
# the restore keeps the local feed out of any committed NuGet.config, so a plain clone is unaffected.
echo ">> building samples/Porta.Pty.Demo against the packed library"
demo="$repo/samples/Porta.Pty.Demo/Porta.Pty.Demo.csproj"
out="$scratch/out"
# Sources via a generated config rather than --source: on Windows the second --source reached NuGet as
# a relative path instead of a URI (NU1301, resolved against the project directory), and --configfile
# also stops any NuGet.config in the tree from contributing sources. Kept the same here so both
# platforms restore identically.
cat > "$scratch/nuget.config" <<XML
<configuration>
  <packageSources>
    <clear />
    <add key="verify-local" value="$feed" />
    <add key="nuget" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
XML

dotnet restore "$demo" \
    -p:PortaPtyPackageVersion="$version" \
    --configfile "$scratch/nuget.config" \
    --packages "$scratch/packages" \
    -v q
dotnet build "$demo" \
    -p:PortaPtyPackageVersion="$version" \
    --packages "$scratch/packages" \
    --no-restore -c Release -o "$out" --nologo -v q

echo ">> running the demo"
"$out/Porta.Pty.Demo"
