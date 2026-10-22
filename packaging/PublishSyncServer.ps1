#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$revision = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw '无法读取同步服务源码提交。' }
$dirty = [bool](& git -C $root status --porcelain)
if ($dirty) { throw '同步服务只打包干净工作区；请先提交并验证。' }
$folder = Join-Path $root 'artifacts/sync-server'
if (Test-Path $folder) { Remove-Item $folder -Recurse -Force }
New-Item -ItemType Directory (Join-Path $folder 'app') -Force | Out-Null
Push-Location $root
try {
    dotnet publish src/Snip.Sync.Server/Snip.Sync.Server.csproj -c Release -p:UseAppHost=false -o (Join-Path $folder 'app')
    if ($LASTEXITCODE -ne 0) { throw '同步服务发布失败。' }
    foreach ($name in @('Dockerfile','compose.yaml','.dockerignore','.env.example','setup.sh','nginx-location.conf','README.md')) {
        Copy-Item (Join-Path $PSScriptRoot "account-sync/$name") (Join-Path $folder $name)
    }
    foreach ($name in @('setup.sh','.dockerignore','.env.example','Dockerfile','compose.yaml','nginx-location.conf')) {
        $target = Join-Path $folder $name
        [System.IO.File]::WriteAllText($target, (Get-Content $target -Raw).Replace("`r`n","`n"), [System.Text.UTF8Encoding]::new($false))
    }
    Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.txt') $folder
    @{ Version=$version; Commit=$revision; SourceDirty=$false; Target='Linux/Windows portable .NET 8 managed server'; BuiltUtc=[DateTimeOffset]::UtcNow.ToString('o') } |
        ConvertTo-Json | Set-Content (Join-Path $folder 'server-build-info.json') -Encoding utf8
    # ZipFile includes dotfiles needed for Docker exclusions and env examples on Linux.
    $zip = Join-Path $root "artifacts/self-snip-sync-server-$version.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($folder, $zip)
    "{0}  {1}" -f (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower(), [System.IO.Path]::GetFileName($zip) |
        Add-Content (Join-Path $root 'artifacts/SHA256SUMS.txt') -Encoding utf8
} finally { Pop-Location }
