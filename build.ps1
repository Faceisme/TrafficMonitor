<#
.SYNOPSIS
  Builds NetSpeed and drops a ready-to-run folder in .\publish

.EXAMPLE
  .\build.ps1                 # needs the .NET 8 Desktop Runtime on the target machine (~2 MB output)
  .\build.ps1 -SelfContained  # bundles the runtime, runs anywhere (~150 MB output)
#>
param(
    [switch]$SelfContained,
    [string]$Output = "$PSScriptRoot\publish"
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "src\NetSpeed\NetSpeed.csproj"

if (Get-Process NetSpeed -ErrorAction SilentlyContinue) {
    Write-Host "NetSpeed 正在运行，请先退出（托盘图标右键 -> 退出）后重试。" -ForegroundColor Yellow
    exit 1
}

# A sync client (this repo may live in Dropbox/OneDrive) can hold a handle on freshly written
# files. Publishing over the top is fine, so a failed wipe is a warning rather than an error.
if (Test-Path $Output) {
    try {
        Remove-Item $Output -Recurse -Force -ErrorAction Stop
    } catch {
        Start-Sleep -Seconds 2
        try {
            Remove-Item $Output -Recurse -Force -ErrorAction Stop
        } catch {
            Write-Host "输出目录被占用，改为覆盖发布（可能残留旧文件）。" -ForegroundColor Yellow
        }
    }
}

$args = @(
    "publish", $project,
    "-c", "Release",
    "-r", "win-x64",
    "-o", $Output,
    "--nologo"
)

if ($SelfContained) {
    $args += @("--self-contained", "true", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true")
} else {
    $args += @("--self-contained", "false")
}

& dotnet @args
if ($LASTEXITCODE -ne 0) { throw "build failed" }

Write-Host ""
Write-Host "完成: $Output\NetSpeed.exe" -ForegroundColor Green
Write-Host "提示: 需要显示进程流量时，请以管理员身份运行。" -ForegroundColor DarkGray
