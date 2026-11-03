# Start Raw Accel with Windows

The optional **Raw Accel Background Service** restores your saved mouse configuration at boot, before sign-in, without opening the GUI. The existing kernel driver still processes mouse movement; the service only loads and saves configuration and otherwise waits idle.

## Enable from the GUI

1. Install the official Raw Accel driver and restart if necessary, as usual.
2. Run `rawaccel.exe` from the complete new build output. Apply the settings you want.
3. Select **Background > Start with Windows**, then accept Windows' administrator prompt.
4. Check **Background > Service status**. It should report `Running (startup enabled)` and successful restoration.

You can close the GUI. Future Apply operations also save the next boot's settings. **Reset** in service mode applies and saves the default configuration. **Save current settings for startup** captures the configuration currently in the driver, including changes made with other compatible tools. One machine-wide startup configuration is shared by Windows accounts.

Uncheck **Start with Windows** to stop and disable the service. Settings already applied to the driver remain active until reset or reboot. When the service is disabled or absent, the GUI uses its normal standalone behavior and portable `settings.json`. An enabled but stopped or unreachable service produces an error when applying settings; restart it or disable startup to return to standalone mode.

## Install from PowerShell

On a new computer, download the [official Raw Accel v1.7.1 release](https://github.com/RawAccelOfficial/rawaccel/releases/tag/v1.7.1), extract it, run its `installer.exe` as administrator, and restart Windows first. This package contains the background service and GUI; it does not include the driver installer. You can configure your preferred sensitivity in our `rawaccel.exe` before installing the service.

Run in an **administrator Windows PowerShell** in the extracted build output folder:

```powershell
.\Install-Service.ps1
```

On first installation, the installer captures the settings currently active in the installed driver, so you do not need to create or supply `settings.json`. If you have not configured Raw Accel yet, these will be the driver's current defaults. On an existing service installation, this command keeps the previously saved startup settings.

To import a settings file you already have, supply its actual path:

```powershell
.\Install-Service.ps1 -SettingsPath 'C:\path\to\your\settings.json'
```

The installer validates the supplied file before changing the installation. It copies the executable and its dependencies to `%ProgramFiles%\RawAccel\Service`, registers `RawAccelService` with automatic startup, and starts it under **LocalService**. Configuration and rotating logs live in `%ProgramData%\RawAccel`. Administrators and the service can modify these files; ordinary users can read them and submit validated settings through a local named pipe. Network pipe access is denied. No signed driver binary is changed by this installer.

Supplying `-SettingsPath '.\settings.json'` requires that file to exist in your current folder. Click **Apply** in `rawaccel.exe` to create the portable settings file while the service is absent or disabled, or omit `-SettingsPath` to use the first-install capture described above. If a driver upgrade is needed, use Raw Accel's normal driver installer separately. Moving the original download folder does not affect the installed service.

```powershell
# Disable automatic startup and stop the service:
.\Install-Service.ps1 -Disable

# Remove the service and its installed binaries (preserve settings and logs):
.\Install-Service.ps1 -Uninstall
```

The installer deliberately leaves unrelated Startup-folder shortcuts and scheduled tasks alone. Remove any old Raw Accel auto-launch entries if you previously created them, to avoid launching another configuration writer at sign-in.

## Diagnose problems

```powershell
.\RawAccelService.exe --status
.\RawAccelService.exe --export 'C:\path\to\settings.json'
.\RawAccelService.exe --validate 'C:\path\to\settings.json'
.\RawAccelService.exe --apply 'C:\path\to\settings.json'
Get-Content "$env:ProgramData\RawAccel\service.log" -Tail 30
```

The service retries driver availability for about one minute at boot. Invalid or missing settings produce a logged error without replacing them with defaults. If the primary settings file is corrupt, a validated `settings.json.lastgood` is used when available; save current settings to repair the primary file. The last successful configuration remains in the driver if a subsequent request fails validation. Failed persistence attempts roll back the live configuration. Resume from sleep reapplies the saved configuration. Stopping the service does not unload or reset the driver.

Windows restarts a crashed service after a delay. If it remains running but reports a restore error, correct the configuration or driver version and restart the service using Services, or apply valid settings from the GUI. Do not rely on the Services `Running` state alone to confirm settings were restored; check the application status or log.

## Build and validation

Build `rawaccel.sln` using the project's normal Visual Studio C++/CLI, .NET Framework 4.7.2 and driver build prerequisites. The new `service` project outputs `RawAccelService.exe`, its configuration and `Install-Service.ps1` alongside the existing user-mode binaries. Release output is also copied into `signed` using the repository's existing packaging convention. Run `service-tests.exe` from the `service-tests` project's output directory.

To build the C# components and run service tests without rebuilding the driver or C++ wrapper:

```powershell
.\tools\Build-UserMode.ps1 `
  -CscPath 'C:\path\to\a\CSharp7.3-or-newer\csc.exe' `
  -WrapperDirectory 'C:\path\to\matching\RawAccel-release'
```

Supply `wrapper.dll` and `Newtonsoft.Json.dll` compatible with this checkout and the selected architecture. For this checkout, the official v1.7.1 release contains the required wrapper APIs. This helper does not download dependencies or install a service. Output defaults to `artifacts\user-mode`.

Automated tests cover settings persistence, restart restoration, backup recovery, missing and invalid files, driver failure, rollback, concurrent writes, startup/GUI ordering, resume, message size limits, actual wrapper parsing and local pipe requests/shutdown. A real reboot, actual sleep/wake, mouse reconnection and UAC installation/removal must also be checked on Windows with the installed driver.

Run `.\tools\Test-Installer.ps1` to check first-install configuration capture, updates, missing files, missing drivers and validation failures without modifying any installed service or requiring administrator rights.
