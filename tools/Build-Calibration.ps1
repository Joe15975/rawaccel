#Requires -Version 5.1
<# Build the current C++/CLI wrapper, GUI, both settings hosts and all user-mode tests.
   Requires Visual Studio 2022 with C++/CLI v143, .NET Framework 4.7.2 targeting pack,
   Windows SDK and NuGet CLI. Does not build/install the kernel driver or change settings. #>
[CmdletBinding()]
param(
    [string]$NuGetPath = 'nuget.exe',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\calibration-release')
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio 2022 C++/CLI build tools are not installed.' }
$installation = & $vswhere -latest -version '[17.0,18.0)' -products '*' -requires Microsoft.Component.MSBuild Microsoft.VisualStudio.Component.VC.CLI.Support -property installationPath
if (!$installation) { throw 'Install the Visual Studio 2022 C++/CLI v143 component and .NET Framework 4.7.2 targeting pack.' }
$msbuild = Join-Path $installation 'MSBuild\Current\Bin\MSBuild.exe'
$compiler = Join-Path $installation 'MSBuild\Current\Bin\Roslyn\csc.exe'
$vstest = Join-Path $installation 'Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe'
if (!(Test-Path -LiteralPath $vstest)) { throw 'The Visual Studio Test Platform is required for wrapper regression tests.' }
$nuget = (Get-Command $NuGetPath -ErrorAction Stop).Source
$output = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\', '/') + '\'
New-Item -ItemType Directory -Path $output -Force | Out-Null
Push-Location $repo
try {
    foreach ($config in @('wrapper-deps\packages.config', 'wrapper-tests\packages.config')) {
        & $nuget restore $config -PackagesDirectory (Join-Path $repo 'packages') -NonInteractive
        if ($LASTEXITCODE -ne 0) { throw "NuGet restore failed for $config" }
    }
    $properties = @('/nologo', '/m', '/p:Configuration=Release', '/p:Platform=x64',
        ('/p:SolutionDir=' + $repo + '\'), ('/p:OutDir=' + $output), '/p:PostBuildEventUseInBuild=false')
    & $msbuild 'wrapper\wrapper.vcxproj' @properties /t:Rebuild
    if ($LASTEXITCODE -ne 0) { throw 'The current C++/CLI wrapper did not build.' }
    & (Join-Path $PSScriptRoot 'Build-UserMode.ps1') -CscPath $compiler -WrapperDirectory $output -OutputDirectory $output
    & $msbuild 'wrapper-tests\wrapper-tests.csproj' @properties /p:BuildProjectReferences=false /t:Rebuild
    if ($LASTEXITCODE -ne 0) { throw 'Wrapper regression tests did not build.' }
    $adapter = Get-ChildItem (Join-Path $repo 'packages\MSTest.TestAdapter.2.2.7') -Filter 'Microsoft.VisualStudio.TestPlatform.MSTest.TestAdapter.dll' -Recurse |
        Select-Object -First 1 -ExpandProperty DirectoryName
    if (!$adapter) { throw 'MSTest adapter was not restored.' }
    & $vstest (Join-Path $output 'wrapper-tests.dll') ('/TestAdapterPath:' + $adapter) /Platform:x64 /Logger:trx ('/ResultsDirectory:' + (Join-Path $output 'TestResults'))
    if ($LASTEXITCODE -ne 0) { throw 'Wrapper regression tests failed.' }
    Write-Output "Built current wrapper, GUI and settings hosts; all user-mode tests passed: $output"
}
finally { Pop-Location }
