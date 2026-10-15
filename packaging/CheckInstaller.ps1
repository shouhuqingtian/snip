#requires -Version 7.0
# Runs only on an ephemeral GitHub Windows runner: deliberately exercises HKCU install/uninstall registration.
param([Parameter(Mandatory)][string]$Installer, [string]$RepositoryRoot = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or $env:GITHUB_ACTIONS -ne 'true') { throw 'Installer regression requires an isolated GitHub Windows runner.' }
$root = [IO.Path]::GetFullPath($RepositoryRoot)
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$portable = Join-Path $root 'artifacts/portable/win-x64'
$expected = Get-Content (Join-Path $portable 'build-info.json') -Raw | ConvertFrom-Json
$install = [IO.Path]::GetFullPath((Join-Path $env:RUNNER_TEMP ('SelfSnip installer check-' + [guid]::NewGuid() + '/程序')))
$appKey = 'HKCU:\Software\SelfSnip'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\SelfSnip'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$menu = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs/自用截图'))
$desktopLink = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('Desktop', 'DoNotVerify')) '自用截图.lnk'))
if ((Test-Path $appKey) -or (Test-Path $uninstallKey) -or (Test-Path $menu) -or (Test-Path $desktopLink) -or
    $null -ne (Get-ItemProperty $runKey -Name SelfSnip -ErrorAction SilentlyContinue)) { throw 'Runner contains an existing installation; refusing to touch it.' }

function Run-Process([string]$File, [string]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($File, $Arguments); $start.UseShellExecute = $false
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (-not $process.WaitForExit(30000)) { $process.Kill($true); throw 'Installer/installed EXE timed out.' }
        if ($process.ExitCode -ne 0) { throw "Installer/installed EXE failed: $($process.ExitCode)" }
    } finally { $process.Dispose() }
}
function Require([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }

# Unique sentinel files under the real model/settings root, never real screenshots or credentials.
$sentinel = Join-Path $env:LOCALAPPDATA ('SelfSnip/installer-regression-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $sentinel -Force | Out-Null
Set-Content (Join-Path $sentinel 'settings.json') '{"publicFixture":true}'
Set-Content (Join-Path $sentinel 'model.bin') 'PUBLIC-MODEL-SENTINEL'
try {
    # /D must be the last NSIS argument and unquoted, even when the directory contains spaces.
    Run-Process $Installer "/S /D=$install"
    $exe = Join-Path $install 'Snip.exe'
    Require ((Get-FileHash $exe).Hash -eq (Get-FileHash (Join-Path $portable 'Snip.exe')).Hash) 'Installer did not reuse the tested EXE.'
    Require ((Get-Content (Join-Path $install 'build-info.json') -Raw | ConvertFrom-Json).Commit -eq $expected.Commit) 'Installed build metadata differs.'
    Require ((Get-ItemProperty $uninstallKey).DisplayVersion -eq $version) 'Uninstall registration version differs.'
    $shortcutPath = [IO.Path]::GetFullPath((Join-Path $menu '自用截图.lnk'))
    Require (Test-Path $shortcutPath -PathType Leaf) 'Start menu shortcut file is missing.'
    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcutPath)
    Write-Output ("Shortcut: file exists; target empty=" + [string]::IsNullOrWhiteSpace($shortcut.TargetPath))
    Require (-not [string]::IsNullOrWhiteSpace($shortcut.TargetPath)) 'Start menu shortcut target is empty.'
    Require ([IO.Path]::GetFullPath($shortcut.TargetPath) -eq $exe) 'Start menu shortcut targets the wrong EXE.'
    Require ((Get-FileHash $shortcut.TargetPath).Hash -eq (Get-FileHash $exe).Hash) 'Shortcut target binary differs from the installed EXE.'
    foreach ($license in Get-ChildItem (Join-Path $portable 'licenses') -File) {
        Require ((Get-FileHash (Join-Path $install ('licenses/' + $license.Name))).Hash -eq (Get-FileHash $license.FullName).Hash) 'Installed license is missing or changed.'
    }
    & python (Join-Path $root 'packaging/CheckIcons.py') --icon (Join-Path $root 'src/Snip.Infrastructure/Assets/sheep.ico') --output (Join-Path $root 'artifacts/validation/installer-icons.json') $Installer $exe (Join-Path $install 'Uninstall.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Installed shell icon regression failed.' }
    $report = Join-Path $env:RUNNER_TEMP ('snip-installed-' + [guid]::NewGuid() + '.zip')
    Run-Process $exe ('--export-diagnostics "' + $report + '"')
    $reportFolder = $report + '.contents'; Expand-Archive $report $reportFolder
    $actual = Get-Content (Join-Path $reportFolder 'environment.json') -Raw | ConvertFrom-Json
    Require ($actual.Environment.AppVersion -eq "$version+$($expected.Commit)") 'Installed EXE version/commit is incorrect.'

    if (-not (Test-Path $runKey)) { New-Item $runKey -Force | Out-Null }
    $startup = '"' + $exe + '" --startup'
    Set-ItemProperty $runKey -Name SelfSnip -Value $startup
    Run-Process $Installer "/S /D=$install"
    Require ((Get-ItemProperty $runKey).SelfSnip -eq $startup) 'Cover installation changed startup preference.'
    Require ((Get-Content (Join-Path $sentinel 'model.bin') -Raw).Trim() -eq 'PUBLIC-MODEL-SENTINEL') 'Cover installation touched downloaded models.'

    Run-Process (Join-Path $install 'Uninstall.exe') '/S'
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while (((Test-Path $install) -or (Test-Path $uninstallKey)) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 200 }
    Require (-not (Test-Path $install) -and -not (Test-Path $appKey) -and -not (Test-Path $uninstallKey)) 'Uninstall left application files/registration behind.'
    Require (-not (Test-Path $menu) -and -not (Test-Path $desktopLink)) 'Uninstall left shortcuts behind.'
    Require ($null -eq (Get-ItemProperty $runKey -Name SelfSnip -ErrorAction SilentlyContinue)) 'Uninstall retained its own startup entry.'
    Require ((Get-Content (Join-Path $sentinel 'settings.json') -Raw).Trim() -eq '{"publicFixture":true}' -and (Get-Content (Join-Path $sentinel 'model.bin') -Raw).Trim() -eq 'PUBLIC-MODEL-SENTINEL') 'Uninstall removed user settings/models.'
    [ordered]@{ Version=$version; Commit=$expected.Commit; TestedExeReused=$true; SheepIcons=$true; InstalledDiagnosticExport=$true; AllLicenses=$true; CoverInstall=$true; StartupPreserved=$true; UninstallClean=$true; UserDataPreserved=$true } | ConvertTo-Json | Set-Content (Join-Path $root 'artifacts/validation/installer-check.json') -Encoding utf8
    Write-Output 'PASS actual installer, installed EXE, all sheep icons/licenses, cover install/startup and uninstall preserving user data.'
} finally {
    # Only remove fixture files this script created. Never delete the SelfSnip data root.
    Remove-Item $sentinel -Recurse -Force
}
