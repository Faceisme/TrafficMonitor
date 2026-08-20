<#
Builds a throwaway copy of the app for visual review only.

Everything is tagged (-Tag) so a stuck instance from an earlier run can never block the next one:
the copy gets its own work folder, assembly name, mutex and settings folder, and it renders a fixed
set of demo rows so the process list can be reviewed without an elevated ETW session.

Test builds keep their tray icon and cannot elevate themselves, so they are always closeable.

This script never touches src/. Nothing here ships.
#>
param(
    [string]$Tag = "a",
    [switch]$SkipBuild,
    [switch]$Dark,
    [switch]$Settings,
    [switch]$Menu,
    [switch]$Flyout
)

$ErrorActionPreference = "Stop"
$src = Join-Path $PSScriptRoot "..\src\NetSpeed"
$work = Join-Path $env:TEMP "netspeed-uitest-$Tag"
$asm = "NetSpeedUiTest$Tag"

Get-Process $asm -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

if (-not $SkipBuild) {
    if (Test-Path $work) { Remove-Item $work -Recurse -Force }
    New-Item -ItemType Directory -Path $work | Out-Null

    robocopy $src $work /E /XD bin obj /NFL /NDL /NJH /NJS /NP | Out-Null

    function Patch([string]$rel, [string]$from, [string]$to) {
        $p = Join-Path $work $rel
        $t = [System.IO.File]::ReadAllText($p)
        if (-not $t.Contains($from)) { throw "patch anchor not found in ${rel}: $from" }
        [System.IO.File]::WriteAllText($p, $t.Replace($from, $to), (New-Object System.Text.UTF8Encoding($false)))
    }

    Patch "App.xaml.cs" `
        'private const string MutexName = "NetSpeed.SingleInstance.v1";' `
        ('private const string MutexName = "NetSpeed.SingleInstance.UiTest.' + $Tag + '";')

    Patch "Core\Settings.cs" `
        'Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetSpeed")' `
        ('Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetSpeed-UiTest-' + $Tag + '")')

    Patch "UI\PopupWindow.xaml.cs" `
        'bool etwOk = s.EtwState == EtwState.Running;' `
        'bool etwOk = true;'

    # An elevated leftover cannot be cleaned up from here, so test builds never elevate.
    Patch "App.xaml.cs" `
        '        string? exe = Environment.ProcessPath;' `
        "        Log.Info(`"elevation disabled in UI test build`"); if (true) return;`r`n        string? exe = Environment.ProcessPath;"

    Patch "Core\MonitorService.cs" @'
            else
            {
                rows = new List<ProcessRateRow>();
            }
'@ @'
            else
            {
                rows = DemoRows(_settings.TopCount);
            }
'@

    Patch "Core\MonitorService.cs" '    private void Tick()' @'
    private static readonly (string Name, string Exe, double Down, double Up)[] Demo =
    {
        ("Google Chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", 72600, 4100),
        ("Windows 服务主机", @"C:\Windows\System32\svchost.exe", 21400, 18800),
        ("QQ", @"C:\Windows\explorer.exe", 3220, 1610),
        ("ZSpaceSync", @"C:\Windows\System32\notepad.exe", 358, 740),
        ("Microsoft Defender", @"C:\Windows\System32\mspaint.exe", 96, 3),
        ("极空间", @"C:\Windows\System32\cmd.exe", 40, 12),
        ("Steam", @"C:\Windows\System32\wordpad.exe", 12, 4),
        ("ImeService", @"C:\Windows\System32\charmap.exe", 8, 2),
        ("Sync", @"C:\Windows\System32\calc.exe", 5, 1),
        ("OneDrive", @"C:\Windows\System32\dxdiag.exe", 2, 1),
    };

    private static List<ProcessRateRow> DemoRows(int count) =>
        Demo.Take(count)
            .Select(d => new ProcessRateRow(d.Name, d.Name, d.Exe, d.Exe, d.Up, d.Down))
            .ToList();

    private void Tick()
'@

    if ($Dark) {
        Patch "UI\Theme.cs" 'bool apps = ReadFlag("AppsUseLightTheme", true);' 'bool apps = false;'
        Patch "UI\Theme.cs" 'bool taskbar = ReadFlag("SystemUsesLightTheme", false);' 'bool taskbar = false;'
    }

    if ($Settings) {
        Patch "App.xaml.cs" @'
        ApplyTrayIcon();
        _monitor.Start();
'@ @'
        ApplyTrayIcon();
        _monitor.Start();

        var autoOpen = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        autoOpen.Tick += (_, _) => { autoOpen.Stop(); OpenSettings(); };
        autoOpen.Start();
'@
    }

    if ($Flyout) {
        # Pins the flyout open on startup, so a review run never grabs the cursor.
        Patch "App.xaml.cs" @'
        ApplyTrayIcon();
        _monitor.Start();
'@ @'
        ApplyTrayIcon();
        _monitor.Start();

        var autoFlyout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        autoFlyout.Tick += (_, _) => { autoFlyout.Stop(); OnWidgetClicked(); };
        autoFlyout.Start();
'@
    }

    if ($Menu) {
        # Opens the menu without touching the mouse, so a review run never grabs the cursor.
        Patch "App.xaml.cs" @'
        ApplyTrayIcon();
        _monitor.Start();
'@ @'
        ApplyTrayIcon();
        _monitor.Start();

        var autoMenu = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        autoMenu.Tick += (_, _) => { autoMenu.Stop(); ShowMenu(); };
        autoMenu.Start();
'@
        Patch "App.xaml.cs" 'Placement = PlacementMode.MousePoint,' 'Placement = PlacementMode.Top,'
    }

    Push-Location $work
    try {
        & dotnet build -c Release --nologo -p:AssemblyName=$asm -v q
        if ($LASTEXITCODE -ne 0) { throw "build failed" }
    } finally { Pop-Location }
}

# Park the test widget well clear of any other instance.
$cfgDir = Join-Path $env:APPDATA "NetSpeed-UiTest-$Tag"
New-Item -ItemType Directory -Path $cfgDir -Force | Out-Null
@'
{
  "SchemaVersion": 2,
  "ShowTrayIcon": true,
  "HideOnFullscreen": false,
  "WidgetGap": 300
}
'@ | Set-Content (Join-Path $cfgDir "settings.json") -Encoding utf8

$exe = Join-Path $work ("bin\Release\net8.0-windows\win-x64\" + $asm + ".exe")
Start-Process $exe
Start-Sleep -Seconds 4
Get-Process $asm | Select-Object Id, ProcessName
