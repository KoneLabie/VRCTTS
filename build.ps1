[CmdletBinding()]
param(
    [switch]$Portable,
    [string]$DotnetPath = 'dotnet',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$projectFile = Join-Path $PSScriptRoot 'VRCTTS\VRCTTS.csproj'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $kind = if ($Portable) { 'portable' } else { 'compact' }
    $OutputDirectory = Join-Path $PSScriptRoot "artifacts\$kind"
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$buildDirectory = Join-Path $OutputDirectory '.build\bin\'
$intermediateDirectory = Join-Path $OutputDirectory '.build\obj\'

Push-Location $PSScriptRoot
try {
    if ($Portable) {
        & $DotnetPath publish $projectFile -c Release -r win-x64 --self-contained true -p:EnableCompressionInSingleFile=true "-p:OutputPath=$buildDirectory" "-p:IntermediateOutputPath=$intermediateDirectory" -o $OutputDirectory
    } else {
        & $DotnetPath publish $projectFile -c Release -r win-x64 --self-contained false -p:EnableCompressionInSingleFile=false "-p:OutputPath=$buildDirectory" "-p:IntermediateOutputPath=$intermediateDirectory" -o $OutputDirectory
    }
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
} finally {
    Pop-Location
}
