#requires -Version 7.0
param(
    [ValidateSet('Core','Desktop','All')][string]$Scope = 'All',
    [switch]$NoBuild,
    [switch]$FailFast,
    [string]$ModelPath = '.model-cache/translation'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    if (-not $NoBuild) {
        dotnet build Snip.sln -c Release --maxcpucount:2
        if ($LASTEXITCODE -ne 0) { throw '构建失败，未执行测试。' }
    }
    $output = Join-Path $root 'artifacts/validation'
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $results = [System.Collections.Generic.List[object]]::new()
    function Check([string]$Name, [string[]]$Arguments, [switch]$AllowsMissingOcr, [switch]$AllowsMissingShare, [switch]$AllowsMissingGpu, [switch]$AllowsMissingSpeech) {
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $output "$Name.log") | Out-Host
        $code = $LASTEXITCODE
        $outcome = if ($code -eq 0) { 'Passed' } elseif (($AllowsMissingOcr -or $AllowsMissingShare -or $AllowsMissingGpu -or $AllowsMissingSpeech) -and $code -eq 2) { 'Unrun' } else { 'Failed' }
        $results.Add([pscustomobject]@{ Name = $Name; Outcome = $outcome; ExitCode = $code; ElapsedSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 2) })
        $results | ConvertTo-Json -Depth 4 -AsArray | Set-Content (Join-Path $output "validation-$($Scope.ToLower()).json") -Encoding utf8
        if ($outcome -eq 'Unrun') { Write-Output "::warning::$Name unrun: required Windows capability is unavailable; see $Name.log." }
        if ($outcome -eq 'Failed' -and $env:GITHUB_ACTIONS -eq 'true') {
            # Readable through GitHub check annotations even when artifact/log download is unavailable.
            $detail = (Get-Content (Join-Path $output "$Name.log") -Tail 30 | Out-String)
            $detail = $detail.Substring(0, [Math]::Min(6000, $detail.Length)).Replace('%','%25').Replace("`r",'%0D').Replace("`n",'%0A')
            Write-Output "::error title=Validation $Name failed::$detail"
        }
        if ($outcome -eq 'Failed' -and $FailFast) { throw "检查 $Name 失败，已提前停止；修复后仍需全部检查通过才可发布。" }
    }
    if ($Scope -in 'Core','All') {
        Check 'core' @('test','tests/Snip.Core.Tests/Snip.Core.Tests.csproj','-c','Release','--no-build','--logger','trx','--results-directory',$output)
        Check 'account-sync-server' @('run','--project','tests/Snip.Sync.Checks/Snip.Sync.Checks.csproj','-c','Release','--no-build')
        Check 'online-protocol' @('run','--project','tests/Snip.Translation.Checks/Snip.Translation.Checks.csproj','-c','Release','--no-build','--','--online')
    }
    if (($Scope -in 'Desktop','All') -and $IsWindows) {
        # Independent checks all run, with their real exit codes preserved.
        Check 'windows' @('run','--project','tests/Snip.Windows.Checks/Snip.Windows.Checks.csproj','-c','Release','--no-build')
        Check 'desktop-scroll' @('run','--project','tests/Snip.Windows.Checks/Snip.Windows.Checks.csproj','-c','Release','--no-build','--','--desktop-scroll')
        Check 'annotation-style' @('run','--project','tests/Snip.Windows.Checks/Snip.Windows.Checks.csproj','-c','Release','--no-build','--','--annotation-style')
        Check 'editor-performance' @('run','--project','tests/Snip.Windows.Checks/Snip.Windows.Checks.csproj','-c','Release','--no-build','--','--editor-performance')
        Check 'share-ui' @('run','--project','tests/Snip.Windows.Checks/Snip.Windows.Checks.csproj','-c','Release','--no-build','--','--share-ui')
        Check 'share-native' @('run','--project','tests/Snip.Windows.Checks/Snip.Windows.Checks.csproj','-c','Release','--no-build','--','--share-native') -AllowsMissingShare
        Check 'dictionary' @('run','--project','tests/Snip.Translation.Checks/Snip.Translation.Checks.csproj','-c','Release','--no-build','--','--dictionary')
        Check 'image-text-selection' @('run','--project','tests/Snip.Windows.Checks/Snip.Windows.Checks.csproj','-c','Release','--no-build','--','--image-text-selection')
        Check 'account-sync' @('run','--project','tests/Snip.Windows.Checks/Snip.Windows.Checks.csproj','-c','Release','--no-build','--','--account-sync')
        Check 'configuration-transfer' @('run','--project','tests/Snip.Windows.Checks/Snip.Windows.Checks.csproj','-c','Release','--no-build','--','--configuration-transfer')
        Check 'application-settings' @('run','--project','tests/Snip.Windows.Checks/Snip.Windows.Checks.csproj','-c','Release','--no-build','--','--application-settings')
        Check 'online-settings' @('run','--project','tests/Snip.Windows.Checks/Snip.Windows.Checks.csproj','-c','Release','--no-build','--','--online-settings')
        Check 'pins' @('run','--project','tests/Snip.Windows.Checks/Snip.Windows.Checks.csproj','-c','Release','--no-build','--','--pins')
        Check 'hotkey' @('run','--project','tests/Snip.Windows.Checks/Snip.Windows.Checks.csproj','-c','Release','--no-build','--','--hotkey')
        Check 'window-placement' @('run','--project','tests/Snip.Windows.Checks/Snip.Windows.Checks.csproj','-c','Release','--no-build','--','--window-placement')
        Check 'speech-native' @('run','--project','tests/Snip.Windows.Checks/Snip.Windows.Checks.csproj','-c','Release','--no-build','--','--speech-native') -AllowsMissingSpeech
        Check 'text-ui' @('run','--project','tests/Snip.Windows.Checks/Snip.Windows.Checks.csproj','-c','Release','--no-build','--','--text-ui')
        Check 'clipboard-ui' @('run','--project','tests/Snip.Windows.Checks/Snip.Windows.Checks.csproj','-c','Release','--no-build','--','--clipboard-ui')
        Check 'ui-gallery' @('run','--project','tests/Snip.Windows.Checks/Snip.Windows.Checks.csproj','-c','Release','--no-build','--','--ui-gallery')
        Check 'translation' @('run','--project','tests/Snip.Translation.Checks/Snip.Translation.Checks.csproj','-c','Release','--no-build','--',$ModelPath,'--install')
        Check 'translation-performance' @('run','--project','tests/Snip.Translation.Checks/Snip.Translation.Checks.csproj','-c','Release','--no-build','--',$ModelPath,'--performance')
        Check 'gpu-runtime' @('run','--project','tests/Snip.Translation.Checks/Snip.Translation.Checks.csproj','-c','Release','--no-build','--',$ModelPath,'--gpu-runtime')
        Check 'gpu-inference' @('run','--project','tests/Snip.Translation.Checks/Snip.Translation.Checks.csproj','-c','Release','--no-build','--',$ModelPath,'--gpu-inference') -AllowsMissingGpu
        Check 'chrome' @('run','--project','tests/Snip.Windows.Checks/Snip.Windows.Checks.csproj','-c','Release','--no-build','--','--chrome')
        Check 'ocr' @('run','--project','tests/Snip.Windows.Checks/Snip.Windows.Checks.csproj','-c','Release','--no-build','--','--ocr') -AllowsMissingOcr
        Check 'ocr-ui' @('run','--project','tests/Snip.Windows.Checks/Snip.Windows.Checks.csproj','-c','Release','--no-build','--','--ocr-ui') -AllowsMissingOcr
        Check 'enhanced-ocr' @('run','--project','tests/Snip.Windows.Checks/Snip.Windows.Checks.csproj','-c','Release','--no-build','--','--enhanced-ocr')
    } elseif ($Scope -eq 'Desktop') { throw 'Desktop 检查必须在 Windows 交互桌面运行。' }
    if ($env:GITHUB_STEP_SUMMARY) {
        @('| Check | Outcome | Seconds |','| --- | --- | --- |') + @($results | ForEach-Object { "| $($_.Name) | $($_.Outcome) | $($_.ElapsedSeconds) |" }) |
            Add-Content $env:GITHUB_STEP_SUMMARY
    }
    if ($results.Where({ $_.Outcome -eq 'Failed' }).Count) { throw '验证失败，禁止打包和发布；见 artifacts/validation。' }
    if (-not $results.Count) { throw '没有执行任何检查。' }
    # GitHub's pwsh wrapper exits with LASTEXITCODE. Optional OCR exit 2 is recorded
    # as Unrun above, while the overall successful validation contract returns 0.
    $global:LASTEXITCODE = 0
} finally { Pop-Location }
