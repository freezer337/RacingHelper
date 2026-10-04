# Builds a ready-to-run copy of Racing Helper into .\dist and creates a desktop shortcut.
# Usage (PowerShell):  .\build.ps1            (add -NoShortcut to skip the shortcut)
param([switch]$NoShortcut)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $root 'dist'

Write-Host 'Building Racing Helper (Release)...'
dotnet publish (Join-Path $root 'src\RacingHelper\RacingHelper.csproj') -c Release -r win-x64 --self-contained false -o $dist -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

$exe = Join-Path $dist 'RacingHelper.exe'
Write-Host "Built: $exe"

if (-not $NoShortcut) {
    $lnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Racing Helper.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $s = $shell.CreateShortcut($lnk)
    $s.TargetPath = $exe
    $s.WorkingDirectory = $dist
    $s.Description = 'Racing Helper - personal race engineer for iRacing'
    $s.Save()
    Write-Host "Desktop shortcut: $lnk"
}
