#Requires -Version 5.1
<#
Build user-mode components against an existing matching wrapper.dll.
Does not rebuild or install the kernel driver. CscPath must support C# 7.3.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$CscPath,
    [Parameter(Mandatory = $true)][string]$WrapperDirectory,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\user-mode'),
    [ValidateSet('x64', 'ARM64')][string]$Platform = 'x64'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$CscPath = (Resolve-Path -LiteralPath $CscPath).Path
$WrapperDirectory = (Resolve-Path -LiteralPath $WrapperDirectory).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$framework = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

function Compile([string[]]$Arguments) {
    & $CscPath /nologo /langversion:7.3 @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'C# compilation failed.' }
}
foreach ($name in @('wrapper.dll', 'Newtonsoft.Json.dll')) {
    $source = Join-Path $WrapperDirectory $name
    $destination = Join-Path $OutputDirectory $name
    if ([IO.Path]::GetFullPath($source) -ne $destination) { Copy-Item -LiteralPath $source -Destination $destination -Force }
}
$common = @(('/platform:' + $Platform), ('/reference:' + (Join-Path $OutputDirectory 'wrapper.dll')),
    ('/reference:' + (Join-Path $OutputDirectory 'Newtonsoft.Json.dll')),
    ('/reference:' + (Join-Path $framework 'System.ServiceProcess.dll')))
$shared = @(Get-ChildItem (Join-Path $repo 'service-shared\*.cs') | ForEach-Object FullName)
$serviceSources = @(Get-ChildItem (Join-Path $repo 'service\*.cs') | ForEach-Object FullName)
Compile ($common + @('/target:exe', ('/out:' + (Join-Path $OutputDirectory 'RawAccelService.exe'))) + $serviceSources + $shared)
Copy-Item -LiteralPath (Join-Path $repo 'service\App.config') -Destination (Join-Path $OutputDirectory 'RawAccelService.exe.config')
Copy-Item -LiteralPath (Join-Path $repo 'service\Install-Service.ps1') -Destination $OutputDirectory

# Use the full-framework resource reader for WinForms .resx files.
$resourceSource = Join-Path $OutputDirectory 'ResourceBuilder.cs'
@'
using System.IO;
using System.Resources;
class ResourceBuilder {
    static void Main(string[] args) {
        using (var reader = new ResXResourceReader(args[0]))
        using (var writer = new ResourceWriter(args[1])) {
            reader.BasePath = Path.GetDirectoryName(args[0]);
            foreach (System.Collections.DictionaryEntry entry in reader)
                writer.AddResource((string)entry.Key, entry.Value);
        }
    }
}
'@ | Set-Content -LiteralPath $resourceSource -Encoding UTF8
$resourceBuilder = Join-Path $OutputDirectory 'ResourceBuilder.exe'
Compile @('/target:exe', ('/out:' + $resourceBuilder),
    ('/reference:' + (Join-Path $framework 'System.Windows.Forms.dll')), $resourceSource)
$resources = @()
foreach ($entry in @(@('Form1.resx', 'grapher.RawAcceleration'), @('AboutBox.resx', 'grapher.AboutBox'),
    @('Properties\Resources.resx', 'grapher.Properties.Resources'))) {
    $destination = Join-Path $OutputDirectory ($entry[1] + '.resources')
    & $resourceBuilder (Join-Path $repo ('grapher\' + $entry[0])) $destination
    if ($LASTEXITCODE -ne 0) { throw 'WinForms resource compilation failed.' }
    $resources += '/resource:' + $destination + ',' + $entry[1] + '.resources'
}
[xml]$project = Get-Content -LiteralPath (Join-Path $repo 'grapher\grapher.csproj')
$guiSources = @($project.Project.ItemGroup.Compile | Where-Object { $_.Include } |
    ForEach-Object { Join-Path $repo ('grapher\' + $_.Include) })
$references = @('System.Configuration', 'System.Data', 'System.Data.DataSetExtensions', 'System.Deployment',
    'System.Drawing', 'System.Windows.Forms', 'System.Windows.Forms.DataVisualization', 'System.Xml',
    'System.Xml.Linq', 'System.Net.Http', 'Microsoft.CSharp') |
    ForEach-Object { '/reference:' + (Join-Path $framework ($_ + '.dll')) }
Compile ($common + $references + @('/target:winexe', ('/out:' + (Join-Path $OutputDirectory 'rawaccel.exe')),
    ('/win32icon:' + (Join-Path $repo 'grapher\mouse.ico'))) + $resources + $guiSources)
Copy-Item -LiteralPath (Join-Path $repo 'grapher\App.config') -Destination (Join-Path $OutputDirectory 'rawaccel.exe.config')
$testSources = @('service-tests\Program.cs', 'service\SettingsEngine.cs', 'service\RawAccelService.cs',
    'service\AccelerationDriver.cs') | ForEach-Object { Join-Path $repo $_ }
Compile ($common + @('/target:exe', ('/out:' + (Join-Path $OutputDirectory 'service-tests.exe'))) + $testSources + $shared)
& (Join-Path $OutputDirectory 'service-tests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Service tests failed.' }
Write-Output "Built GUI and service; tests passed. Output: $OutputDirectory"
