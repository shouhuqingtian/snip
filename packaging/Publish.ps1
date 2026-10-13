#requires -Version 7.0
param(
    [switch]$Installer,
    [string]$MakensisPath = ""
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$portable = Join-Path $root 'artifacts/portable/win-x64'
Push-Location $root
try {
    dotnet build Snip.sln -c Release --maxcpucount:2
    if ($LASTEXITCODE -ne 0) { throw '构建失败' }
    dotnet test tests/Snip.Core.Tests/Snip.Core.Tests.csproj -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw '核心测试失败' }
    if ($IsWindows -or $env:OS -eq 'Windows_NT') {
        dotnet run --project tests/Snip.Windows.Checks/Snip.Windows.Checks.csproj -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw 'Windows 检查失败' }
    }
    dotnet publish src/Snip.App/Snip.App.csproj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None -p:DebugSymbols=false -o $portable
    if ($LASTEXITCODE -ne 0) { throw '发布失败' }
    Compress-Archive -Path (Join-Path $portable 'Snip.exe'), (Join-Path $root 'docs/使用说明.md') `
        -DestinationPath (Join-Path $root 'artifacts/自用截图-1.0.0-win-x64.zip') -Force
    if ($Installer) {
        if (-not $MakensisPath) {
            $command = Get-Command makensis -ErrorAction SilentlyContinue
            if ($command) { $MakensisPath = $command.Source }
        }
        if (-not $MakensisPath) {
            $candidates = @(
                (Join-Path ${env:ProgramFiles(x86)} 'NSIS/makensis.exe'),
                (Join-Path $env:ProgramFiles 'NSIS/makensis.exe')
            )
            $MakensisPath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
        }
        if (-not $MakensisPath -or -not (Test-Path $MakensisPath)) { throw '找不到 NSIS makensis，请使用 -MakensisPath 指定路径。' }
        New-Item -ItemType Directory -Path (Join-Path $root 'artifacts/installer') -Force | Out-Null
        & $MakensisPath (Join-Path $PSScriptRoot 'Snip.nsi')
        if ($LASTEXITCODE -ne 0) { throw '安装包编译失败' }
    }
    $artifactRoot = Join-Path $root 'artifacts'
    $files = Get-ChildItem $artifactRoot -File -Recurse | Where-Object { $_.Extension -in '.zip','.exe' }
    $lines = $files | ForEach-Object {
        $relative = $_.FullName.Substring($artifactRoot.Length + 1).Replace('\', '/')
        '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $relative
    }
    $lines | Set-Content (Join-Path $root 'artifacts/SHA256SUMS.txt') -Encoding utf8
} finally { Pop-Location }
