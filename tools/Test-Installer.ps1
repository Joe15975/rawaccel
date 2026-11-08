#Requires -Version 5.1
# Exercise configuration selection without administrator rights or an installed driver.
$ErrorActionPreference = 'Stop'
$installer = Join-Path $PSScriptRoot '..\service\Install-Service.ps1'
$errorsFound = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    (Resolve-Path -LiteralPath $installer).Path, [ref]$null, [ref]$errorsFound)
if ($errorsFound) { throw $errorsFound[0] }
foreach ($functionName in @('Read-StartupSettings', 'Assert-DriverAvailable', 'Wait-ServiceReady')) {
    $function = $ast.Find({ param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq $functionName
    }, $true)
    if ($null -eq $function) { throw "Installer function $functionName was not found." }
    . ([ScriptBlock]::Create($function.Extent.Text))
}

$testDirectory = Join-Path ([IO.Path]::GetTempPath()) ('RawAccel-installer-tests-' + [Guid]::NewGuid())
New-Item -ItemType Directory -Path $testDirectory | Out-Null
try {
    # Only replace the driver CLI boundary; test the production selection/cleanup code itself.
    $fakeCli = Join-Path $testDirectory 'DriverCli.ps1'
    @'
param($Action, $Path)
$global:RawAccelInstallerTestState.Calls.Add([PSCustomObject]@{ Action = $Action; Path = $Path })
if ($Action -eq '--export') {
    [IO.File]::WriteAllText($Path, 'active-profile')
    $global:LASTEXITCODE = $global:RawAccelInstallerTestState.ExportExit
}
elseif ($Action -eq '--validate') { $global:LASTEXITCODE = $global:RawAccelInstallerTestState.ValidateExit }
elseif ($Action -eq '--check-driver') { $global:LASTEXITCODE = $global:RawAccelInstallerTestState.DriverExit }
elseif ($Action -eq '--check-ready') {
    $global:RawAccelInstallerTestState.ReadyCalls++
    if ($global:RawAccelInstallerTestState.ReadyThrows) { throw 'Service pipe not yet available.' }
    if ($global:RawAccelInstallerTestState.ReadyCalls -ge $global:RawAccelInstallerTestState.ReadyAfter) {
        Write-Output 'Saved settings applied.'
        $global:LASTEXITCODE = 0
    }
    else {
        Write-Output 'Waiting to restore startup settings.'
        $global:LASTEXITCODE = 1
    }
}
else { throw "Unexpected test CLI action: $Action" }
'@ | Set-Content -LiteralPath $fakeCli -Encoding UTF8
    $global:RawAccelInstallerTestState = [PSCustomObject]@{
        Calls = (New-Object 'System.Collections.Generic.List[object]')
        ExportExit = 0
        ValidateExit = 0
        DriverExit = 0
        ReadyCalls = 0
        ReadyAfter = 1
        ReadyThrows = $false
    }
    function Check([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
    function Expect-Error([scriptblock]$Action, [string]$Expected) {
        try { & $Action } catch {
            Check ($_.Exception.Message.Contains($Expected)) $_.Exception.Message
            return
        }
        throw "Expected an error containing: $Expected"
    }
    $saved = Join-Path $testDirectory 'saved.json'
    $requested = Join-Path $testDirectory 'requested.json'
    $missing = Join-Path $testDirectory 'missing.json'
    [IO.File]::WriteAllText($saved, 'saved-profile')
    [IO.File]::WriteAllText($requested, 'requested-profile')

    Expect-Error { Read-StartupSettings $missing $saved $fakeCli } 'was not found'
    Check ($global:RawAccelInstallerTestState.Calls.Count -eq 0) 'A missing explicit file silently fell back.'
    Check ([IO.File]::ReadAllText($saved) -eq 'saved-profile') 'Missing explicit file changed existing settings.'
    Write-Output 'PASS Missing explicit settings fail with instructions and preserve existing settings'

    $result = Read-StartupSettings $requested $saved $fakeCli
    Check ($result.Save -and $result.Text -eq 'requested-profile') 'Explicit settings were not selected.'
    Write-Output 'PASS Explicit settings take precedence'

    $global:RawAccelInstallerTestState.Calls.Clear()
    $result = Read-StartupSettings '' $saved $fakeCli
    Check (!$result.Save -and $result.Text -eq 'saved-profile') 'An update replaced saved settings.'
    Check ($global:RawAccelInstallerTestState.Calls.Count -eq 1) 'Update captured active settings unnecessarily.'
    Write-Output 'PASS Updating without a path retains saved settings'

    $global:RawAccelInstallerTestState.Calls.Clear()
    $result = Read-StartupSettings '' $missing $fakeCli
    Check ($result.Save -and $result.Text -eq 'active-profile') 'Fresh installation did not capture active settings.'
    $temporary = $global:RawAccelInstallerTestState.Calls[0].Path
    Check (!(Test-Path -LiteralPath $temporary)) 'Captured temporary settings were not cleaned up.'
    Check (!(Test-Path -LiteralPath $missing)) 'Selection changed the installation before validation completed.'
    Write-Output 'PASS Fresh installation captures active settings and cleans up its temporary file'

    $global:RawAccelInstallerTestState.Calls.Clear()
    $global:RawAccelInstallerTestState.ExportExit = 1
    Expect-Error { Read-StartupSettings '' $missing $fakeCli } 'install the official Raw Accel driver'
    Check (!(Test-Path -LiteralPath $global:RawAccelInstallerTestState.Calls[0].Path)) 'Failed export left a temporary file.'
    Write-Output 'PASS Missing driver produces fresh-computer setup instructions'

    $global:RawAccelInstallerTestState.ExportExit = 0
    $global:RawAccelInstallerTestState.ValidateExit = 1
    Expect-Error { Read-StartupSettings $requested $saved $fakeCli } 'nothing was installed'
    Check ([IO.File]::ReadAllText($saved) -eq 'saved-profile') 'Validation failure changed saved settings.'
    Write-Output 'PASS Validation failure preserves the previous installation'
    Assert-DriverAvailable $fakeCli
    Write-Output 'PASS Compatible driver passes preflight'
    $global:RawAccelInstallerTestState.DriverExit = 1
    Expect-Error { Assert-DriverAvailable $fakeCli } 'Nothing was installed'
    Write-Output 'PASS Unavailable or incompatible driver fails preflight with setup instructions'

    $global:RawAccelInstallerTestState.ReadyAfter = 2
    $status = Wait-ServiceReady $fakeCli
    Check ($global:RawAccelInstallerTestState.ReadyCalls -eq 2 -and $status -eq 'Saved settings applied.') 'Installer did not wait for restoration.'
    Write-Output 'PASS Installer waits for confirmed restoration before reporting success'

    $global:RawAccelInstallerTestState.ReadyCalls = 0
    Expect-Error { Wait-ServiceReady $fakeCli 0 } 'did not confirm that startup settings were restored'
    Write-Output 'PASS Running without restored settings fails installation verification'

    $global:RawAccelInstallerTestState.ReadyThrows = $true
    Expect-Error { Wait-ServiceReady $fakeCli 0 } 'Service pipe not yet available'
    Write-Output 'PASS Service connection errors are reported as readiness failures'
    Write-Output '11 installer tests passed.'
}
finally {
    Remove-Variable RawAccelInstallerTestState -Scope Global -ErrorAction SilentlyContinue
    $resolved = [IO.Path]::GetFullPath($testDirectory)
    $expectedPrefix = Join-Path ([IO.Path]::GetTempPath()) 'RawAccel-installer-tests-'
    if (!$resolved.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected test directory.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
exit 0
