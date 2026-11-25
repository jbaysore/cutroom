# Scores narration cleanup against real recordings. Each case is a folder in tests\corpus (gitignored) with
# narration.* (WAV or OBS video), script.txt or script.docx, expected.txt (what the cleaned narration should say, in
# order: ad-libs in, retakes out) and optionally marks.txt (retake-mark seconds, one per line).
# Uses your Settings (whisper model, denoiser, GPU) and the speech models in %LOCALAPPDATA%\Cutroom\models.
param([string]$Corpus, [switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (!$Corpus) { $Corpus = Join-Path $projectRoot 'tests\corpus' }
if (!(Test-Path -LiteralPath $Corpus)) { throw "No corpus at $Corpus" }
if (!$NoBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -Configuration Debug | Out-Null }

$report = Join-Path $projectRoot 'artifacts\eval-cleanup.md'
$log = [IO.Path]::ChangeExtension($report, '.log')
Remove-Item -LiteralPath $report, $log -ErrorAction SilentlyContinue
$exe = Join-Path $projectRoot 'src/Cutroom.App/bin/Debug/net10.0-windows/win-x64/Cutroom.exe'
$p = Start-Process -FilePath $exe -ArgumentList @('--eval-cleanup', "`"$((Resolve-Path -LiteralPath $Corpus).Path)`"", "`"$report`"") -PassThru -WindowStyle Hidden
$p.WaitForExit()
if ($p.ExitCode -eq 2 -or !(Test-Path -LiteralPath $report)) { throw "Evaluation failed:`n$(Get-Content -LiteralPath $log -Raw -ErrorAction SilentlyContinue)" }
Get-Content -LiteralPath $report -Raw -Encoding UTF8
Write-Output "Report: $report"
if ($p.ExitCode -ne 0) { throw 'Cleanup removed substantive words in at least one case' }
