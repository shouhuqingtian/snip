$ErrorActionPreference = 'Stop'
# Public, pinned Electron fixture only; never installed into the shipped application.
$folder = Join-Path $PSScriptRoot '../artifacts/check-tools/electron-38.0.0'
$exe = Join-Path $folder 'electron.exe'
if (!(Test-Path $exe)) {
    $zip = Join-Path ([IO.Path]::GetTempPath()) 'selfsnip-electron-38.0.0.zip'
    try {
        Invoke-WebRequest 'https://github.com/electron/electron/releases/download/v38.0.0/electron-v38.0.0-win32-x64.zip' -OutFile $zip
        if ((Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant() -ne '950591d9f3f97700973d5b594ae3833728192b9efd32b94be4d44ca0ef435e40') { throw 'Electron fixture checksum mismatch.' }
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
        Expand-Archive $zip -DestinationPath $folder -Force
    } finally { Remove-Item $zip -Force -ErrorAction SilentlyContinue }
}
$env:SNIP_CHECK_ELECTRON = (Resolve-Path $exe).Path
if ($env:GITHUB_ENV) { "SNIP_CHECK_ELECTRON=$env:SNIP_CHECK_ELECTRON" | Add-Content $env:GITHUB_ENV }
