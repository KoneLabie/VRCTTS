[CmdletBinding()]
param([string]$DotnetPath = 'dotnet')

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$testArtifacts = Join-Path $repositoryRoot ('artifacts\tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testArtifacts -Force | Out-Null

# These are console integration-check projects, not dotnet test projects.
# No live-service, device-playback, driver-setup, or visible-window options are used.
$projects = @(
    'VRCTTS.Tests',
    'VoiceTests',
    'LayoutCheck',
    'QuickPhraseTests',
    'QuickPhraseCacheTests',
    'AudioFormatTests',
    'OfflineVoiceTests',
    'SettingsTests'
)

Push-Location $repositoryRoot
try {
    foreach ($project in $projects) {
        Write-Host "Checking $project"
        $arguments = @('run', '--project', (Join-Path $repositoryRoot $project), '--configuration', 'Release')
        if ($project -eq 'LayoutCheck') {
            $arguments += @('--', '--output', (Join-Path $testArtifacts 'layout-main.png'))
        }
        & $DotnetPath @arguments
        if ($LASTEXITCODE -ne 0) { throw "$project failed." }
    }
    Write-Host 'All eight check projects passed.'
} finally {
    Pop-Location
}
