[CmdletBinding()]
param(
    [string]$DotnetPath = 'dotnet',
    [string]$OutputDirectory,
    [string]$ExpectedVersion,
    [string]$IsccPath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$projectPath = Join-Path $repositoryRoot 'VRCTTS\VRCTTS.csproj'
[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$version = [string]($project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'The project must declare a release version.' }
if ($ExpectedVersion -and $ExpectedVersion -cne $version) { throw "Expected version $ExpectedVersion does not match project version $version." }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $repositoryRoot 'artifacts\release' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

# Fresh staging directories avoid replacing an executable someone is running.
# This script never recursively removes files or directories.
$stagingRoot = Join-Path $repositoryRoot ('artifacts\package-' + [Guid]::NewGuid().ToString('N'))
$portablePublish = Join-Path $stagingRoot 'publish-portable'
$compactPublish = Join-Path $stagingRoot 'publish-compact'
& (Join-Path $repositoryRoot 'build.ps1') -Portable -DotnetPath $DotnetPath -OutputDirectory $portablePublish
& (Join-Path $repositoryRoot 'build.ps1') -DotnetPath $DotnetPath -OutputDirectory $compactPublish

$portableName = "VRCTTS-$version-win-x64"
$compactName = "VRCTTS-$version-compact-win-x64"
$sourceName = "VRCTTS-$version-Source"
$portableStage = Join-Path $stagingRoot $portableName
$compactStage = Join-Path $stagingRoot $compactName
$sourceStage = Join-Path $stagingRoot $sourceName
New-Item -ItemType Directory -Path $portableStage,$compactStage,$sourceStage -Force | Out-Null

$expectedFileVersion = ($version -split '-')[0] + '.0'
foreach ($published in @($portablePublish, $compactPublish)) {
    $executable = Join-Path $published 'VRCTTS.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Missing executable in $published." }
    $metadata = [Diagnostics.FileVersionInfo]::GetVersionInfo($executable)
    if ($metadata.FileVersion -ne $expectedFileVersion -or $metadata.ProductName -ne 'VRCTTS') {
        throw "Unexpected executable version or product in $published."
    }
}
Copy-Item -LiteralPath (Join-Path $portablePublish 'VRCTTS.exe') -Destination $portableStage
Copy-Item -LiteralPath (Join-Path $compactPublish 'VRCTTS.exe') -Destination $compactStage

$supportFiles = @('README.md','README.txt','THIRD-PARTY-NOTICES.txt','DOTNET-LICENSE.txt','DOTNET-NOTICES.txt')
foreach ($supportFile in $supportFiles) {
    $supportPath = Join-Path $repositoryRoot $supportFile
    if (-not (Test-Path -LiteralPath $supportPath -PathType Leaf)) { throw "Required release notice/document is missing: $supportFile" }
    foreach ($destination in @($portableStage,$compactStage,$sourceStage)) {
        Copy-Item -LiteralPath $supportPath -Destination $destination
    }
}

$setupName = "VRCTTS-$version-Setup.exe"
& (Join-Path $repositoryRoot 'build-installer.ps1') -AppSourceDirectory $portableStage -OutputDirectory $OutputDirectory -Version $version -IsccPath $IsccPath

# Explicit source allowlist: never traverse local settings, old packages, backups,
# generated audio, private voice installers, or repository credentials.
$projectDirectories = @('VRCTTS','VRCTTS.Tests','VoiceTests','LayoutCheck','QuickPhraseTests','QuickPhraseCacheTests','AudioFormatTests','OfflineVoiceTests','SettingsTests','CableProbe','RenameProbe')
foreach ($projectDirectory in $projectDirectories) {
    $directoryPath = Join-Path $repositoryRoot $projectDirectory
    if (-not (Test-Path -LiteralPath $directoryPath -PathType Container)) { throw "Missing source project: $projectDirectory" }
    foreach ($sourceFile in Get-ChildItem -LiteralPath $directoryPath -Recurse -File) {
        $relative = $sourceFile.FullName.Substring($repositoryRoot.Length + 1)
        if ($relative -match '(^|[\\/])(bin|obj)([\\/]|$)') { continue }
        $isEmbeddedDriver = $relative.Replace('\','/') -eq 'VRCTTS/Resources/VBCABLE_Driver_Pack45.zip'
        if ($sourceFile.Extension -notin @('.cs','.csproj','.xaml','.manifest') -and -not $isEmbeddedDriver) { continue }
        $destination = Join-Path $sourceStage $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $sourceFile.FullName -Destination $destination
    }
}
foreach ($buildFile in @('build.ps1','build-installer.ps1','package.ps1','global.json','.gitignore','.gitattributes','RELEASE_NOTES.md')) {
    $buildPath = Join-Path $repositoryRoot $buildFile
    if (-not (Test-Path -LiteralPath $buildPath -PathType Leaf)) { throw "Missing build file: $buildFile" }
    Copy-Item -LiteralPath $buildPath -Destination $sourceStage
}
foreach ($relativeDirectory in @('scripts','.github\workflows','installer')) {
    $directoryPath = Join-Path $repositoryRoot $relativeDirectory
    foreach ($sourceFile in Get-ChildItem -LiteralPath $directoryPath -File) {
        if ($sourceFile.Extension -notin @('.ps1','.yml','.yaml','.iss','.txt')) { continue }
        $relative = $sourceFile.FullName.Substring($repositoryRoot.Length + 1)
        $destination = Join-Path $sourceStage $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $sourceFile.FullName -Destination $destination
    }
}

$releaseArchives = @(
    @{ Stage = $portableStage; Name = "$portableName.zip" },
    @{ Stage = $compactStage; Name = "$compactName.zip" },
    @{ Stage = $sourceStage; Name = "$sourceName.zip" }
)
foreach ($release in $releaseArchives) {
    $archivePath = Join-Path $OutputDirectory $release.Name
    Compress-Archive -LiteralPath $release.Stage -DestinationPath $archivePath -Force
    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        if ($archive.Entries.Count -eq 0) { throw "Empty archive: $archivePath" }
        if ($release.Stage -eq $sourceStage) {
            $invalidEntry = $archive.Entries | Where-Object {
                $entryName = $_.FullName.Replace('\','/')
                $entryName -match '(^|/)(bin|obj|\.git|\.env|settings\.json|quick-phrases\.json)(/|$)|\.(mp3|pfx|pem|key)$'
            } | Select-Object -First 1
            if ($invalidEntry) { throw "Unexpected private/generated source entry: $($invalidEntry.FullName)" }
        }
    } finally {
        $archive.Dispose()
    }
}
$packageNames = @($releaseArchives.Name) + @($setupName)
$checksums = foreach ($packageName in $packageNames) {
    $hash = (Get-FileHash -LiteralPath (Join-Path $OutputDirectory $packageName) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $packageName"
}
[IO.File]::WriteAllLines((Join-Path $OutputDirectory 'SHA256SUMS.txt'), $checksums, [Text.UTF8Encoding]::new($false))
Write-Host "Packaged VRCTTS $version in $OutputDirectory"
Get-ChildItem -LiteralPath $OutputDirectory -File | Where-Object { $_.Name -in $packageNames + @('SHA256SUMS.txt') } | Select-Object Name,Length
