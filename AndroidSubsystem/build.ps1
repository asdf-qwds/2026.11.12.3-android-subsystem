# 单文件发布脚本：生成 dist\android.exe
# 用法： powershell -ExecutionPolicy Bypass -File .\build.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'

if (-not (Test-Path -LiteralPath $dotnet)) {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $cmd) { throw '未找到 dotnet SDK，请先安装 .NET 8 SDK。' }
    $dotnet = $cmd.Source
}

$dist = Join-Path (Split-Path -Parent $root) 'dist'

& $dotnet publish (Join-Path $root 'AndroidSubsystem.csproj') `
    -c Release -r win-x64 --self-contained -o $dist

Write-Host ""
Write-Host "生成完毕：$dist\android.exe"
Get-ChildItem -LiteralPath $dist | Select-Object Name, @{N = '大小(MB)'; E = { [math]::Round($_.Length / 1MB, 1) } }
