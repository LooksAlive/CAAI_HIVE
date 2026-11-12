[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'CAAI\debuggers')
)

$ErrorActionPreference = 'Stop'
if (-not [Environment]::Is64BitOperatingSystem -or $env:PROCESSOR_ARCHITECTURE -eq 'ARM64') {
    throw 'This installer requires Windows x64. Install a matching netcoredbg build and set CAAI_DEBUG_ADAPTER on other platforms.'
}
$version = '3.2.0-1092'
$expectedHash = '3C410A45FA502415203A94FCB88654AF65BF8E3DAC158A5527A722E7A6B9274A'
$archive = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString() + '.zip')
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -UseBasicParsing -Uri "https://github.com/Samsung/netcoredbg/releases/download/$version/netcoredbg-win64.zip" -OutFile $archive
    if ((Get-FileHash -Path $archive -Algorithm SHA256).Hash -ne $expectedHash) {
        throw 'The downloaded netcoredbg archive did not match the official release SHA-256 hash.'
    }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Expand-Archive -Path $archive -DestinationPath $Destination -Force
    $executable = Join-Path $Destination 'netcoredbg\netcoredbg.exe'
    if (-not (Test-Path $executable)) { throw "The debugger executable was not found at $executable" }
    Write-Output "Installed netcoredbg $version at $executable"
    Write-Output 'CAAI will discover this installation automatically. No administrator privileges are required.'
}
finally {
    if (Test-Path $archive) { Remove-Item -Path $archive -Force }
}