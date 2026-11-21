[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AppSourceDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$Version,
    [string]$IsccPath
)

$ErrorActionPreference = 'Stop'
$AppSourceDirectory = [IO.Path]::GetFullPath($AppSourceDirectory)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Invalid installer version.' }
$appExecutable = Join-Path $AppSourceDirectory 'VRCTTS.exe'
if (-not (Test-Path -LiteralPath $appExecutable -PathType Leaf)) { throw 'The installer requires the self-contained portable app.' }
$metadata = [Diagnostics.FileVersionInfo]::GetVersionInfo($appExecutable)
if ($metadata.FileVersion -ne (($Version -split '-')[0] + '.0')) { throw 'Installer payload version does not match.' }
if ([string]::IsNullOrWhiteSpace($IsccPath)) { $IsccPath = & (Join-Path $PSScriptRoot 'scripts\get-inno-setup.ps1') }
if (-not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) { throw 'Inno Setup compiler was not found.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$compilerArguments = @('/Qp', "/DAppVersion=$Version", ('/DAppNumericVersion=' + ($Version -split '-')[0] + '.0'), "/DAppSourceDir=$AppSourceDirectory", "/DInstallerOutputDir=$OutputDirectory", (Join-Path $PSScriptRoot 'installer\VRCTTS.iss'))
& $IsccPath @compilerArguments
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$installer = Join-Path $OutputDirectory "VRCTTS-$Version-Setup.exe"
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw 'Installer compilation did not produce the expected EXE.' }
Write-Host "Built installer: $installer"
