$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$visualStudio = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$environmentScript = Join-Path $visualStudio 'VC/Auxiliary/Build/vcvars64.bat'
$output = Join-Path $repo 'artifacts/native'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$source = Join-Path $repo 'tests/FileShift.Shell.Tests/Tests.cpp'
$exe = Join-Path $output 'FileShift.Shell.Tests.exe'
$object = Join-Path $output 'FileShift.Shell.Tests.obj'
$command = '"{0}" >nul && cl /nologo /std:c++20 /O2 /EHsc /W4 /WX /MT /DNOMINMAX /DUNICODE /D_UNICODE /Fo"{1}" /Fe"{2}" "{3}" shell32.lib shlwapi.lib advapi32.lib ole32.lib uuid.lib' -f $environmentScript,$object,$exe,$source
& $env:ComSpec /d /s /c $command
if ($LASTEXITCODE -ne 0) { throw 'Native shell tests failed to compile.' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Native shell tests failed.' }
