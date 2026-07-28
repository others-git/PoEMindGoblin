# Publish the portable exe to publish/.
#
# Stops a running instance first: the single-file exe locks itself while running, and
# publishing into a locked path fails with UnauthorizedAccessException. Working around
# that by publishing to publish-v2, -v3, -v4... just litters the repo and leaves you
# guessing which build you are actually running.
param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

Get-Process MindGoblin -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "stopping running instance (pid $($_.Id))"
    Stop-Process -Id $_.Id -Force
    Start-Sleep -Milliseconds 500
}

if (-not $SkipTests) { dotnet test "$root" -v q }
dotnet publish "$root\src\MindGoblin\MindGoblin.csproj" -c Release -o "$root\publish"

Write-Host ""
Write-Host "-> $root\publish\MindGoblin.exe"
