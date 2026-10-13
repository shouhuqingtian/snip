#requires -Version 7.0
param(
    [ValidateSet('Core','Desktop','All')][string]$Scope = 'All',
    [switch]$NoBuild,
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
    function Check([string]$Name, [string[]]$Arguments, [switch]$AllowsMissingOcr) {
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $output "$Name.log") | Out-Host
        $code = $LASTEXITCODE
        $outcome = if ($code -eq 0) { 'Passed' } elseif ($AllowsMissingOcr -and $code -eq 2) { 'Unrun' } else { 'Failed' }
        $results.Add([pscustomobject]@{ Name = $Name; Outcome = $outcome; ExitCode = $code; ElapsedSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 2) })
        $results | ConvertTo-Json -Depth 4 -AsArray | Set-Content (Join-Path $output "validation-$($Scope.ToLower()).json") -Encoding utf8
        if ($outcome -eq 'Unrun') { Write-Output '::warning::Native English OCR unrun: Windows language component is missing.' }
    }
    if ($Scope -in 'Core','All') {
        Check 'core' @('test','tests/Snip.Core.Tests/Snip.Core.Tests.csproj','-c','Release','--no-build','--logger','trx','--results-directory',$output)
    }
    if (($Scope -in 'Desktop','All') -and $IsWindows) {
        # Independent checks all run, with their real exit codes preserved.
        Check 'windows' @('run','--project','tests/Snip.Windows.Checks/Snip.Windows.Checks.csproj','-c','Release','--no-build')
        Check 'translation' @('run','--project','tests/Snip.Translation.Checks/Snip.Translation.Checks.csproj','-c','Release','--no-build','--',$ModelPath,'--install')
        Check 'chrome' @('run','--project','tests/Snip.Windows.Checks/Snip.Windows.Checks.csproj','-c','Release','--no-build','--','--chrome')
        Check 'ocr' @('run','--project','tests/Snip.Windows.Checks/Snip.Windows.Checks.csproj','-c','Release','--no-build','--','--ocr') -AllowsMissingOcr
    } elseif ($Scope -eq 'Desktop') { throw 'Desktop 检查必须在 Windows 交互桌面运行。' }
    if ($env:GITHUB_STEP_SUMMARY) {
        @('| Check | Outcome | Seconds |','| --- | --- | --- |') + @($results | ForEach-Object { "| $($_.Name) | $($_.Outcome) | $($_.ElapsedSeconds) |" }) |
            Add-Content $env:GITHUB_STEP_SUMMARY
    }
    if ($results.Where({ $_.Outcome -eq 'Failed' }).Count) { throw '验证失败，禁止打包和发布；见 artifacts/validation。' }
    if (-not $results.Count) { throw '没有执行任何检查。' }
} finally { Pop-Location }
