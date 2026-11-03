#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$SettingsPath,
    [switch]$Disable,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$serviceName = 'RawAccelService'
$installDirectory = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'RawAccel\Service'
$dataDirectory = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'RawAccel'
$executable = Join-Path $installDirectory 'RawAccelService.exe'
$configPath = Join-Path $dataDirectory 'settings.json'
$payload = @('RawAccelService.exe', 'RawAccelService.exe.config', 'wrapper.dll', 'Newtonsoft.Json.dll')

function Assert-PlainPath([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($null -ne $item) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing to install through a linked path: $Path"
        }
    }
}

function Set-ProtectedFile([string]$Path, [bool]$ServiceMayWrite) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { return }
    $acl = New-Object System.Security.AccessControl.FileSecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($entry in @(@('S-1-5-18', 'FullControl'), @('S-1-5-32-544', 'FullControl'),
        @('S-1-5-19', $(if ($ServiceMayWrite) { 'Modify' } else { 'ReadAndExecute' })),
        @('S-1-5-32-545', 'ReadAndExecute'))) {
        $sid = New-Object System.Security.Principal.SecurityIdentifier($entry[0])
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
            $sid, $entry[1], [System.Security.AccessControl.AccessControlType]::Allow)))
    }
    $acl.SetOwner((New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')))
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Stop-RawAccelService($service) {
    if ($null -ne $service -and $service.Status -ne 'Stopped') {
        Stop-Service -InputObject $service
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
    }
}

function Set-ProtectedDirectory([string]$Path, [bool]$ServiceMayWrite) {
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    foreach ($entry in @(@('S-1-5-18', 'FullControl'), @('S-1-5-32-544', 'FullControl'),
        @('S-1-5-19', $(if ($ServiceMayWrite) { 'Modify' } else { 'ReadAndExecute' })),
        @('S-1-5-32-545', 'ReadAndExecute'))) {
        $sid = New-Object System.Security.Principal.SecurityIdentifier($entry[0])
        $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
            $sid, $entry[1], $inherit, [System.Security.AccessControl.PropagationFlags]::None,
            [System.Security.AccessControl.AccessControlType]::Allow)
        $acl.AddAccessRule($rule)
    }
    $acl.SetOwner((New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')))
    Set-Acl -LiteralPath $Path -AclObject $acl
}

try {
    if ($Disable -and $Uninstall) { throw 'Choose Disable or Uninstall.' }
    foreach ($target in @((Split-Path $installDirectory -Parent), $installDirectory, $dataDirectory)) {
        Assert-PlainPath $target
    }
    foreach ($name in $payload) { Assert-PlainPath (Join-Path $installDirectory $name) }
    foreach ($name in @('settings.json', 'settings.json.pending', 'settings.json.lastgood', 'service.log', 'service.log.old')) {
        Assert-PlainPath (Join-Path $dataDirectory $name)
    }
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -ne $service) {
        $registration = Get-CimInstance Win32_Service -Filter "Name='RawAccelService'"
        if ($registration.PathName -ne ('"' + $executable + '"') -or
            $registration.StartName -ne 'NT AUTHORITY\LocalService') {
            throw 'Existing service registration differs. Remove that registration before installing this build.'
        }
    }
    if ($Disable -or $Uninstall) {
        Stop-RawAccelService $service
        if ($null -ne $service) {
            if ($Uninstall) {
                & "$env:SystemRoot\System32\sc.exe" delete $serviceName
                if ($LASTEXITCODE -ne 0) { throw 'Could not remove the service registration.' }
                # Only remove known installed files. Keep settings and logs for reinstall/recovery.
                foreach ($name in $payload) {
                    $target = Join-Path $installDirectory $name
                    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }
                }
            }
            else { Set-Service -Name $serviceName -StartupType Disabled }
        }
        Write-Output 'Background startup disabled. Current driver settings remain active until reset or reboot.'
        exit 0
    }

    foreach ($name in $payload) {
        if (!(Test-Path -LiteralPath (Join-Path $PSScriptRoot $name) -PathType Leaf)) {
            throw "Missing $name. Run this script beside the complete service build output."
        }
    }
    if (!$SettingsPath -and !(Test-Path -LiteralPath $configPath)) {
        throw 'Supply -SettingsPath with the settings.json you want restored at startup.'
    }
    $sourceSettings = if ($SettingsPath) { (Resolve-Path -LiteralPath $SettingsPath).Path } else { $configPath }
    & (Join-Path $PSScriptRoot 'RawAccelService.exe') --validate $sourceSettings
    if ($LASTEXITCODE -ne 0) { throw 'Settings validation failed; nothing was installed.' }
    $settingsText = [IO.File]::ReadAllText($sourceSettings)

    Stop-RawAccelService $service
    Set-ProtectedDirectory $installDirectory $false
    Set-ProtectedDirectory $dataDirectory $true
    foreach ($name in @('settings.json', 'settings.json.pending', 'settings.json.lastgood', 'service.log', 'service.log.old')) {
        Set-ProtectedFile (Join-Path $dataDirectory $name) $true
    }
    foreach ($name in $payload) {
        $source = Join-Path $PSScriptRoot $name
        $target = Join-Path $installDirectory $name
        if ([IO.Path]::GetFullPath($source) -ne [IO.Path]::GetFullPath($target)) {
            Copy-Item -LiteralPath $source -Destination $target -Force
        }
        Set-ProtectedFile $target $false
    }
    if ($SettingsPath -or !(Test-Path -LiteralPath $configPath)) {
        $pending = $configPath + '.pending'
        [IO.File]::WriteAllText($pending, $settingsText, (New-Object Text.UTF8Encoding($false)))
        if (Test-Path -LiteralPath $configPath) { [IO.File]::Replace($pending, $configPath, $configPath + '.lastgood') }
        else { [IO.File]::Move($pending, $configPath) }
        Set-ProtectedFile $configPath $true
    }

    if ($null -eq $service) {
        $credential = New-Object System.Management.Automation.PSCredential(
            'NT AUTHORITY\LocalService', (New-Object Security.SecureString))
        New-Service -Name $serviceName -DisplayName 'Raw Accel Background Service' `
            -BinaryPathName ('"' + $executable + '"') -StartupType Automatic -Credential $credential | Out-Null
    }
    else {
        Set-Service -Name $serviceName -StartupType Automatic
    }
    Set-Service -Name $serviceName -Description 'Restores Raw Accel mouse settings at boot without opening the GUI.'
    & "$env:SystemRoot\System32\sc.exe" failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000
    if ($LASTEXITCODE -ne 0) { throw 'Could not configure service recovery.' }
    & "$env:SystemRoot\System32\sc.exe" failureflag $serviceName 1
    if ($LASTEXITCODE -ne 0) { throw 'Could not enable service recovery.' }
    Start-Service -Name $serviceName
    (Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(15))
    Write-Output "Installed and started. Startup settings: $configPath"
}
catch { Write-Error $_ -ErrorAction Continue; exit 1 }
