[CmdletBinding()]
param([string]$CacheDirectory)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($CacheDirectory)) { $CacheDirectory = Join-Path $repositoryRoot 'artifacts\tools\inno-7.1.1' }
$CacheDirectory = [IO.Path]::GetFullPath($CacheDirectory)
$compiler = Join-Path $CacheDirectory 'ISCC.exe'
if (Test-Path -LiteralPath $compiler -PathType Leaf) {
    $signature = Get-AuthenticodeSignature -LiteralPath $compiler
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Pyrsys') { throw 'Cached Inno Setup compiler has an unexpected signature.' }
    return $compiler
}

$downloadDirectory = Join-Path $repositoryRoot 'artifacts\tools\downloads'
New-Item -ItemType Directory -Path $downloadDirectory -Force | Out-Null
$bootstrap = Join-Path $downloadDirectory 'innosetup-7.1.1-x64.exe'
$expectedHash = '8A5A6711C3766A6E5FB74F280BA0E3551E719CE94538D901FDBD1F8F9920B3F0'
if (-not (Test-Path -LiteralPath $bootstrap) -or (Get-FileHash -LiteralPath $bootstrap -Algorithm SHA256).Hash -ne $expectedHash) {
    Invoke-WebRequest -Uri 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_1/innosetup-7.1.1-x64.exe' -OutFile $bootstrap
}
if ((Get-FileHash -LiteralPath $bootstrap -Algorithm SHA256).Hash -ne $expectedHash) { throw 'Official Inno Setup bootstrap checksum failed.' }
$signature = Get-AuthenticodeSignature -LiteralPath $bootstrap
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Pyrsys') { throw 'Official Inno Setup bootstrap signature failed.' }

# The vendor-supported portable mode creates no compiler uninstall registration.
$arguments = @('/PORTABLE=1','/CURRENTUSER','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS','/TASKS=', ('/DIR="' + $CacheDirectory + '"'))
$process = Start-Process -FilePath $bootstrap -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'Portable Inno Setup compiler extraction failed.' }
$compilerSignature = Get-AuthenticodeSignature -LiteralPath $compiler
if ($compilerSignature.Status -ne 'Valid' -or $compilerSignature.SignerCertificate.Subject -notmatch 'Pyrsys') { throw 'Extracted Inno Setup compiler signature failed.' }
return $compiler
