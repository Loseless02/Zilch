# Builds Zilch, runs the self-tests and copies the exe to .\dist
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

dotnet build src/Zilch -c Release -nologo
if ($LASTEXITCODE) { exit $LASTEXITCODE }

dotnet build tests/Zilch.Tests -c Release -nologo
if ($LASTEXITCODE) { exit $LASTEXITCODE }
& tests/Zilch.Tests/bin/Release/net48/Zilch.Tests.exe
if ($LASTEXITCODE) { Write-Error "Self-tests failed"; exit 1 }

New-Item -ItemType Directory -Force dist | Out-Null
Copy-Item src/Zilch/bin/Release/net48/Zilch.exe dist/Zilch.exe -Force
$hash = (Get-FileHash dist/Zilch.exe -Algorithm SHA256).Hash
Write-Host "`ndist\Zilch.exe  $((Get-Item dist/Zilch.exe).Length) bytes  SHA256 $hash"
