$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Install Visual Studio C++ Build Tools and the Windows SDK to build the Explorer submenu.' }
$visualStudio = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $visualStudio) { throw 'Visual Studio C++ Build Tools were not found.' }
$environmentScript = Join-Path $visualStudio 'VC/Auxiliary/Build/vcvars64.bat'
$output = Join-Path $repo 'artifacts/native'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$source = Join-Path $repo 'native/FileShift.Shell/FileShift.Shell.cpp'
$definition = Join-Path $repo 'native/FileShift.Shell/FileShift.Shell.def'
$dll = Join-Path $output 'FileShift.Shell.dll'
$object = Join-Path $output 'FileShift.Shell.obj'
$importLibrary = Join-Path $output 'FileShift.Shell.lib'
$command = '"{0}" >nul && cl /nologo /std:c++20 /O2 /EHsc /W4 /WX /MT /LD /DUNICODE /D_UNICODE /DWIN32_LEAN_AND_MEAN /DNOMINMAX /guard:cf /Fo"{1}" "{2}" /link /DEF:"{3}" /OUT:"{4}" /IMPLIB:"{5}" /guard:cf shell32.lib shlwapi.lib advapi32.lib ole32.lib uuid.lib' -f $environmentScript,$object,$source,$definition,$dll,$importLibrary
& $env:ComSpec /d /s /c $command
if ($LASTEXITCODE -ne 0) { throw 'Native Explorer submenu build failed.' }
