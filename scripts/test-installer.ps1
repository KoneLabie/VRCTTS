[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallerPath,
    [string]$ExpectedVersion
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$testId = [Guid]::NewGuid().ToString('N')
$testRoot = [IO.Path]::GetFullPath((Join-Path $artifactsRoot ('installer-smoke-' + $testId)))
$applicationDirectory = Join-Path $testRoot 'app'
$uninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{DE909B42-AD4C-4B60-9EB4-C1AE81914235}_is1'
$profileDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'VRCTTS'
$sentinelPath = Join-Path $profileDirectory ('installer-smoke-' + $testId + '.sentinel')
$sentinelCreated = $false
$installAttempted = $false
$uninstallVerified = $false
$failure = $null

function Assert-NoReparseAncestors([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to use a path containing a reparse point: $current"
            }
        }
        $parent = [IO.Directory]::GetParent($current)
        if ($null -eq $parent) { break }
        $current = $parent.FullName
    }
}

function Assert-OwnedTestPath([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $artifactsPrefix = $artifactsRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $ownedPrefix = $testRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not [IO.Path]::IsPathRooted($fullPath) -or
        -not $testRoot.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($testRoot) -ne ('installer-smoke-' + $testId) -or
        ($fullPath -ne $testRoot -and -not $fullPath.StartsWith($ownedPrefix, [StringComparison]::OrdinalIgnoreCase))) {
        throw 'Installer smoke-test path escaped its owned artifacts directory.'
    }
    Assert-NoReparseAncestors $fullPath
}

function Assert-NoReparseDescendants([string]$Path) {
    Assert-OwnedTestPath $Path
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $directories = New-Object 'System.Collections.Generic.Queue[string]'
    $directories.Enqueue($Path)
    while ($directories.Count -gt 0) {
        foreach ($item in Get-ChildItem -LiteralPath $directories.Dequeue() -Force) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing recursive cleanup containing a reparse point: $($item.FullName)"
            }
            if ($item.PSIsContainer) { $directories.Enqueue($item.FullName) }
        }
    }
}

function Get-InstallerRegistrations {
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
        $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $view)
        $key = $null
        try {
            $key = $baseKey.OpenSubKey($uninstallKey)
            if ($null -ne $key) {
                [PSCustomObject]@{
                    View = $view
                    DisplayName = [string]$key.GetValue('DisplayName')
                    DisplayVersion = [string]$key.GetValue('DisplayVersion')
                    InstallLocation = [string]$key.GetValue('InstallLocation')
                }
            }
        } finally {
            if ($null -ne $key) { $key.Dispose() }
            $baseKey.Dispose()
        }
    }
}

function Test-OwnedRegistration($Registration) {
    if ([string]::IsNullOrWhiteSpace($Registration.InstallLocation)) { return $false }
    $registeredDirectory = [IO.Path]::GetFullPath($Registration.InstallLocation).TrimEnd('\', '/')
    return $registeredDirectory.Equals($applicationDirectory.TrimEnd('\', '/'), [StringComparison]::OrdinalIgnoreCase)
}

function Get-ProfileSnapshot {
    foreach ($directoryName in @('VRCTTS', 'JustinBridge')) {
        $directory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) $directoryName
        foreach ($fileName in @('settings.json', 'quick-phrases.json')) {
            $path = Join-Path $directory $fileName
            Assert-NoReparseAncestors $path
            $exists = Test-Path -LiteralPath $path -PathType Leaf
            [PSCustomObject]@{
                Path = $path
                Exists = $exists
                Hash = $(if ($exists) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash } else { $null })
            }
        }
    }
}

function Assert-ProfilesUnchanged($Snapshot) {
    foreach ($entry in $Snapshot) {
        Assert-NoReparseAncestors $entry.Path
        $exists = Test-Path -LiteralPath $entry.Path -PathType Leaf
        if ($exists -ne $entry.Exists -or
            ($exists -and (Get-FileHash -LiteralPath $entry.Path -Algorithm SHA256).Hash -ne $entry.Hash)) {
            throw "Installer smoke test detected a changed preferences/phrases file: $($entry.Path)"
        }
    }
}

function Invoke-HiddenSetup([string]$Executable, [string[]]$Arguments) {
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -WorkingDirectory $testRoot -WindowStyle Hidden -PassThru
    try {
        if (-not $process.WaitForExit(300000)) {
            try { $process.Kill() } catch { Write-Warning 'Timed-out installer process could not be stopped.' }
            throw 'Installer process exceeded the five-minute timeout.'
        }
        if ($process.ExitCode -ne 0) { throw "Installer process returned exit code $($process.ExitCode)." }
    } finally {
        $process.Dispose()
    }
}

function Invoke-OwnedUninstaller {
    Assert-NoReparseDescendants $applicationDirectory
    $registrations = @(Get-InstallerRegistrations)
    foreach ($registration in $registrations) {
        if (-not (Test-OwnedRegistration $registration)) {
            throw 'Refusing cleanup because the AppId now belongs to a different installation.'
        }
    }
    $uninstaller = Join-Path $applicationDirectory 'unins000.exe'
    if (Test-Path -LiteralPath $uninstaller -PathType Leaf) {
        Invoke-HiddenSetup $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/LOG="' + (Join-Path $testRoot 'uninstall.log') + '"'))
    } elseif ($registrations.Count -gt 0) {
        throw 'The isolated installation is registered but has no uninstaller.'
    }
    # Inno's temporary uninstaller may finish deleting itself just after its parent exits.
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $payloadRemains = Test-Path -LiteralPath $applicationDirectory
        $registrationRemains = @(Get-InstallerRegistrations).Count -gt 0
        if (-not $payloadRemains -and -not $registrationRemains) { return }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Uninstall did not remove the isolated app directory and uninstall registration.'
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or -not [Environment]::Is64BitOperatingSystem) {
    throw 'The installer smoke test requires 64-bit Windows.'
}
$installer = Get-Item -LiteralPath $InstallerPath -Force
if ($installer.PSIsContainer -or $installer.Extension -ne '.exe') { throw 'InstallerPath must point to the built installer EXE.' }
Assert-NoReparseAncestors $installer.FullName
if ([string]::IsNullOrWhiteSpace($ExpectedVersion)) {
    [xml]$project = Get-Content -LiteralPath (Join-Path $repositoryRoot 'VRCTTS\VRCTTS.csproj') -Raw
    $ExpectedVersion = [string]$project.Project.PropertyGroup.Version
}
$numericVersion = [Version]($ExpectedVersion -split '[-+]', 2)[0]
$expectedFileVersion = New-Object Version($numericVersion.Major, $numericVersion.Minor, [Math]::Max(0, $numericVersion.Build), [Math]::Max(0, $numericVersion.Revision))
if (@(Get-InstallerRegistrations).Count -gt 0) {
    throw 'VRCTTS is already installed for this user. Refusing to replace its uninstall registration during the smoke test.'
}
Assert-OwnedTestPath $testRoot
Assert-NoReparseAncestors $profileDirectory
$profileSnapshot = @(Get-ProfileSnapshot)

try {
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $profileDirectory -Force | Out-Null
    $sentinel = [IO.File]::Open($sentinelPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $sentinelCreated = $true
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes('VRCTTS installer smoke test ' + $testId)
        $sentinel.Write($bytes, 0, $bytes.Length)
    } finally { $sentinel.Dispose() }
    $sentinelHash = (Get-FileHash -LiteralPath $sentinelPath -Algorithm SHA256).Hash

    Write-Host "Installing VRCTTS $ExpectedVersion into isolated smoke-test directory."
    $installAttempted = $true
    Invoke-HiddenSetup $installer.FullName @(
        '/CURRENTUSER', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS', '/TASKS=""',
        ('/DIR="' + $applicationDirectory + '"'), ('/LOG="' + (Join-Path $testRoot 'install.log') + '"')
    )
    Assert-NoReparseDescendants $applicationDirectory
    $requiredFiles = @('VRCTTS.exe', 'README.md', 'README.txt', 'THIRD-PARTY-NOTICES.txt', 'DOTNET-LICENSE.txt', 'DOTNET-NOTICES.txt', 'unins000.exe', 'unins000.dat')
    foreach ($fileName in $requiredFiles) {
        $path = Join-Path $applicationDirectory $fileName
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
            throw "Installed payload is missing a nonempty $fileName file."
        }
    }
    $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $applicationDirectory 'VRCTTS.exe'))
    $actualVersion = New-Object Version($fileVersion.FileMajorPart, $fileVersion.FileMinorPart, $fileVersion.FileBuildPart, $fileVersion.FilePrivatePart)
    if ($actualVersion -ne $expectedFileVersion) { throw "Installed EXE version $actualVersion does not match $expectedFileVersion." }
    $registrations = @(Get-InstallerRegistrations)
    if ($registrations.Count -eq 0) { throw 'The installer did not create a per-user uninstall registration.' }
    # Some HKCU registry branches are shared between the 32-bit and 64-bit views.
    foreach ($registration in $registrations) {
        if (-not (Test-OwnedRegistration $registration) -or
            $registration.DisplayName -ne 'VRCTTS' -or $registration.DisplayVersion -ne $ExpectedVersion) {
            throw 'The installer did not create the expected per-user uninstall registration.'
        }
    }
    Assert-ProfilesUnchanged $profileSnapshot

    Invoke-OwnedUninstaller
    $uninstallVerified = $true
    if (-not (Test-Path -LiteralPath $sentinelPath -PathType Leaf) -or
        (Get-FileHash -LiteralPath $sentinelPath -Algorithm SHA256).Hash -ne $sentinelHash) {
        throw 'Uninstall did not preserve the owned app-data sentinel.'
    }
    Assert-ProfilesUnchanged $profileSnapshot
} catch {
    $failure = $_
} finally {
    if ($installAttempted -and -not $uninstallVerified) {
        try {
            Invoke-OwnedUninstaller
            $uninstallVerified = $true
        } catch {
            Write-Warning "Isolated installation cleanup could not finish: $($_.Exception.Message)"
            if ($null -eq $failure) { $failure = $_ }
        }
    }
    try {
        Assert-ProfilesUnchanged $profileSnapshot
    } catch {
        Write-Warning "App-data preservation check failed: $($_.Exception.Message)"
        if ($null -eq $failure) { $failure = $_ }
    }
    try {
        if ($sentinelCreated -and (Test-Path -LiteralPath $sentinelPath)) {
            Assert-NoReparseAncestors $sentinelPath
            Remove-Item -LiteralPath $sentinelPath -Force
        }
    } catch {
        Write-Warning "Owned sentinel cleanup failed: $($_.Exception.Message)"
        if ($null -eq $failure) { $failure = $_ }
    }
    if ($null -eq $failure) {
        Assert-NoReparseDescendants $testRoot
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    } elseif (Test-Path -LiteralPath $testRoot) {
        Write-Warning "Smoke-test logs and any remaining isolated payload are preserved at $testRoot"
    }
}

if ($null -ne $failure) { throw $failure }
Write-Host 'Installer smoke test passed: version, support files, per-user registration, uninstall, and preserved preferences/phrases.'
