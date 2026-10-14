#requires -Version 7.0
param(
    [switch]$Installer,
    [string]$MakensisPath = '',
    [switch]$SkipChecks,
    [switch]$ReusePortable,
    [ValidateSet('Stable','Preview')][string]$Channel = 'Stable'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$artifactRoot = Join-Path $root 'artifacts'
$portable = Join-Path $artifactRoot 'portable/win-x64'
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$revision = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw '无法读取源码提交。' }
$dirty = [bool](& git -C $root status --porcelain)
if ($LASTEXITCODE -ne 0) { throw '无法读取源码状态。' }
$buildVersion = if ($Channel -eq 'Preview') { "$version-preview" } else { "$version" }
Push-Location $root
try {
    if ($ReusePortable) {
        $previous = Get-Content (Join-Path $portable 'build-info.json') -Raw | ConvertFrom-Json
        if ($dirty -or $previous.SourceDirty -or $previous.Commit -ne $revision -or $previous.Version -ne $buildVersion) { throw '便携包与当前提交/版本不一致，拒绝重用。' }
    }
    if (-not $SkipChecks -and -not $ReusePortable) { & "$PSScriptRoot/Validate.ps1" -Scope All }
    if (-not $ReusePortable) {
        dotnet publish src/Snip.App/Snip.App.csproj -c Release -r win-x64 --self-contained true `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
            -p:DebugType=None -p:DebugSymbols=false "-p:Version=$buildVersion" "-p:SourceRevisionId=$revision" -o $portable
        if ($LASTEXITCODE -ne 0) { throw '发布失败' }
        $info = [ordered]@{ Version = $buildVersion; Commit = $revision; SourceDirty = $dirty; Channel = $Channel; BuiltUtc = [DateTime]::UtcNow.ToString('o'); Platform = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription }
        $info | ConvertTo-Json | Set-Content (Join-Path $portable 'build-info.json') -Encoding utf8
        Copy-Item (Join-Path $root 'docs/使用说明.md'), (Join-Path $root 'THIRD-PARTY-NOTICES.txt') $portable -Force
        $licensesTarget = Join-Path $portable 'licenses'
        New-Item -ItemType Directory -Path $licensesTarget -Force | Out-Null
        Copy-Item (Join-Path $root 'docs/licenses/*') $licensesTarget -Force
        $validationTarget = Join-Path $portable 'validation'
        if (Test-Path $validationTarget) { Remove-Item $validationTarget -Recurse -Force }
        if (Test-Path (Join-Path $artifactRoot 'validation')) { Copy-Item (Join-Path $artifactRoot 'validation') $validationTarget -Recurse -Force }
        $zipName = if ($Channel -eq 'Preview') { 'self-snip-preview-win-x64.zip' } else { "自用截图-$version-win-x64.zip" }
        $payload = @('Snip.exe','build-info.json','使用说明.md','THIRD-PARTY-NOTICES.txt','licenses') | ForEach-Object { Join-Path $portable $_ }
        if (Test-Path (Join-Path $portable 'validation')) { $payload += Join-Path $portable 'validation' }
        Compress-Archive -Path $payload -DestinationPath (Join-Path $artifactRoot $zipName) -Force
        $previews = Join-Path $artifactRoot 'validation/ui-previews'
        if (Test-Path $previews) { Compress-Archive -Path (Join-Path $previews '*.png') -DestinationPath (Join-Path $artifactRoot 'ui-previews.zip') -Force }
        Add-Type -AssemblyName System.IO.Compression
        $sourcePath = Join-Path $artifactRoot 'self-snip-source.zip'
        $stream = [System.IO.File]::Open($sourcePath, [System.IO.FileMode]::Create)
        $source = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            $files = & git -c core.quotepath=false ls-files
            if ($LASTEXITCODE -ne 0) { throw '无法获取源码清单' }
            foreach ($file in $files) {
                if ($file -notlike 'downloads/*') { [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($source, (Join-Path $root $file), $file.Replace('\','/')) | Out-Null }
            }
        } finally { $source.Dispose(); $stream.Dispose() }
    }
    if (-not (Test-Path (Join-Path $portable 'Snip.exe'))) { throw '未找到已验证便携 EXE。' }
    if ($Installer) {
        if (-not $MakensisPath) {
            $command = Get-Command makensis -ErrorAction SilentlyContinue
            if ($command) { $MakensisPath = $command.Source }
        }
        if (-not $MakensisPath -and $IsWindows) {
            $MakensisPath = @("${env:ProgramFiles(x86)}/NSIS/makensis.exe", "$env:ProgramFiles/NSIS/makensis.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
        }
        if (-not $MakensisPath -or -not (Test-Path $MakensisPath)) { throw '找不到 NSIS makensis，请使用 -MakensisPath 指定路径。' }
        New-Item -ItemType Directory -Path (Join-Path $artifactRoot 'installer') -Force | Out-Null
        & $MakensisPath '-INPUTCHARSET' 'UTF8' "-DAPP_VERSION=$version" "-DAPP_VERSION_QUAD=$version.0" (Join-Path $PSScriptRoot 'Snip.nsi')
        if ($LASTEXITCODE -ne 0) { throw '安装包编译失败' }
    }
    $zipName = if ($Channel -eq 'Preview') { 'self-snip-preview-win-x64.zip' } else { "自用截图-$version-win-x64.zip" }
    $deliverables = @((Join-Path $artifactRoot $zipName), (Join-Path $artifactRoot 'self-snip-source.zip'))
    if (Test-Path (Join-Path $artifactRoot 'ui-previews.zip')) { $deliverables += Join-Path $artifactRoot 'ui-previews.zip' }
    if ($Installer) { $deliverables += Join-Path $artifactRoot "installer/SelfSnip-$version-Setup-x64.exe" }
    $deliverables | Where-Object { Test-Path $_ } | ForEach-Object {
        $relative = [System.IO.Path]::GetRelativePath($artifactRoot, $_).Replace('\','/')
        '{0}  {1}' -f (Get-FileHash $_ -Algorithm SHA256).Hash.ToLower(), $relative
    } | Set-Content (Join-Path $artifactRoot 'SHA256SUMS.txt') -Encoding utf8
} finally { Pop-Location }
