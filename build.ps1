# Compila WMac y genera el instalador en .\dist  (uso: powershell -ExecutionPolicy Bypass -File .\build.ps1)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$version = '1.2.0'

if (-not (Test-Path "$root\src\WMac\Assets\WMac.ico")) { & "$root\tools\make-icon.ps1" }

dotnet build "$root\src\WMac\WMac.csproj" -c Release -nologo
if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación de WMac' }

$app = "$root\src\WMac\bin\Release\net48"
$payload = "$root\src\WMac.Setup\payload"
New-Item -ItemType Directory -Force $payload | Out-Null
Copy-Item "$app\WMac.exe" $payload -Force
if (Test-Path "$app\WMac.exe.config") { Copy-Item "$app\WMac.exe.config" $payload -Force }

dotnet build "$root\src\WMac.Setup\WMac.Setup.csproj" -c Release -nologo
if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación del instalador' }

$dist = "$root\dist"
New-Item -ItemType Directory -Force "$dist\portable" | Out-Null
Copy-Item "$root\src\WMac.Setup\bin\Release\net48\WMac-Setup.exe" "$dist\WMac-Setup-$version.exe" -Force
Copy-Item "$app\WMac.exe" "$dist\portable\" -Force
if (Test-Path "$app\WMac.exe.config") { Copy-Item "$app\WMac.exe.config" "$dist\portable\" -Force }

Get-ChildItem $dist -Recurse -File | ForEach-Object { '{0,-40} {1,8:N0} KB' -f $_.FullName.Substring($dist.Length + 1), ($_.Length / 1KB) }
