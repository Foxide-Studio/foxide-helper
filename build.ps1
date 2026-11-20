# Builds FoxideHelper.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
# No SDK or Visual Studio required.
# Usage:  .\build.ps1                    -> FoxideHelper.exe next to this script
#         .\build.ps1 -Out dist          -> dist\FoxideHelper.exe (+ FoxideHelper.exe.sha256) for a GitHub release
#         .\build.ps1 -Test              -> also obj\FoxideDisk.test.exe without the UAC prompt (--dump / --shot diagnostics)
param([switch]$Test, [string]$Out = '')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$src  = Join-Path $root 'src'
$obj  = Join-Path $root 'obj'
$csc  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
New-Item -ItemType Directory -Force $obj | Out-Null
$outDir = if ($Out) { if ([IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $root $Out } } else { $root }
New-Item -ItemType Directory -Force $outDir | Out-Null
$exe = Join-Path $outDir 'FoxideHelper.exe'

$refs = '/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Management.dll','/r:System.Web.Extensions.dll'
$common = '/nologo','/optimize+','/platform:anycpu','/utf8output','/codepage:65001'

# 1) icon generator -> app.ico
& $csc @common @refs /target:exe /out:"$obj\IconMaker.exe" "$src\Logo.cs" "$src\tools\IconMaker.cs"
if ($LASTEXITCODE) { throw 'IconMaker build failed' }
& "$obj\IconMaker.exe" "$obj\app.ico"

# 2) application
$files = @(Get-ChildItem $src -Filter *.cs) + @(Get-ChildItem (Join-Path $src 'lang') -Filter *.cs) | ForEach-Object FullName
& $csc @common @refs /unsafe /target:winexe /out:"$exe" /win32icon:"$obj\app.ico" /win32manifest:"$src\app.manifest" $files
if ($LASTEXITCODE) { throw 'FoxideHelper build failed' }
if ($Out) {
    $hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower()
    [IO.File]::WriteAllText("$exe.sha256", "$hash  FoxideHelper.exe`n")
}

# 3) optional non-elevated test build (for --dump / --shot diagnostics)
if ($Test) {
    (Get-Content "$src\app.manifest" -Raw) -replace 'requireAdministrator','asInvoker' | Set-Content "$obj\test.manifest" -Encoding UTF8
    & $csc @common @refs /unsafe /target:winexe /out:"$obj\FoxideDisk.test.exe" /win32icon:"$obj\app.ico" /win32manifest:"$obj\test.manifest" $files
    if ($LASTEXITCODE) { throw 'test build failed' }
}
Get-Item $exe | Select-Object FullName, @{n='KB';e={[math]::Round($_.Length/1KB)}}, @{n='Version';e={$_.VersionInfo.FileVersion}}
