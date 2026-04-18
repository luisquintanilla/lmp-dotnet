# ralph.ps1 — Ralph Loop for LMP implementation via Copilot CLI
# Usage:
#   .\ralph.ps1 -Mode plan -MaxIterations 3         # Generate IMPLEMENTATION_PLAN.md
#   .\ralph.ps1 -Mode build -MaxIterations 30       # Implement from plan
#   .\ralph.ps1 -Mode overnight -MaxIterations 50   # Plan + build unattended
#
# Session transcripts are always saved to .internal/ralph-logs/ (never shared as gist).
#
# Sources:
#   - Ralph Loop pattern: https://ghuntley.com/loop/
#   - Copilot CLI non-interactive: https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-command-reference
#   - Copilot SDK cookbook: https://github.com/github/awesome-copilot/blob/main/cookbook/copilot-sdk/dotnet/ralph-loop.md

param(
    [ValidateSet("plan", "build", "overnight")]
    [string]$Mode = "build",

    [int]$MaxIterations = 30,

    [int]$PlanIterations = 3,

    [switch]$Yolo
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot  # ensure we're at repo root

$logDir = ".internal/ralph-logs"
New-Item -ItemType Directory -Path $logDir -Force | Out-Null

function Invoke-RalphLoop {
    param(
        [string]$LoopMode,
        [int]$Iterations,
        [string]$Permissions
    )

    $promptFile = if ($LoopMode -eq "plan") { "PROMPT_plan.md" } else { "PROMPT_build.md" }

    if (-not (Test-Path $promptFile)) {
        Write-Error "Missing $promptFile in repo root."
        return $false
    }

    $prompt = Get-Content $promptFile -Raw

    Write-Host ""
    Write-Host ("=" * 50) -ForegroundColor Cyan
    Write-Host "  Ralph Loop — $LoopMode mode" -ForegroundColor Cyan
    Write-Host ("=" * 50) -ForegroundColor Cyan
    Write-Host "  Prompt:         $promptFile"
    Write-Host "  Max iterations: $Iterations"
    Write-Host "  Permissions:    $Permissions"
    Write-Host "  Logs:           $logDir/"
    Write-Host ("=" * 50) -ForegroundColor Cyan

    for ($i = 1; $i -le $Iterations; $i++) {
        Write-Host "`n>>> $LoopMode iteration $i/$Iterations <<<" -ForegroundColor Yellow

        # Fresh Copilot CLI session — each invocation gets full context budget
        # --share saves transcript locally to .internal/ (gitignored, never gisted)
        & copilot -p $prompt $Permissions --share "$logDir/$LoopMode-iteration-$i.md"

        $exitCode = $LASTEXITCODE
        if ($exitCode -ne 0) {
            Write-Host "Copilot exited with code $exitCode" -ForegroundColor Red
        }

        # Exit condition for build: check FIRST LINE of plan for completion marker
        # (checking only first line avoids false-trigger on task description text)
        # Also exit if no ❌ tasks remain in the file.
        if ($LoopMode -eq "build" -and (Test-Path "IMPLEMENTATION_PLAN.md")) {
            $firstLine = (Get-Content "IMPLEMENTATION_PLAN.md" -TotalCount 1)
            if ($firstLine -match "ALL TASKS COMPLETE") {
                Write-Host "`nAll tasks complete!" -ForegroundColor Green
                return $true
            }
            $remaining = (Select-String -Path "IMPLEMENTATION_PLAN.md" -Pattern " ❌$" | Measure-Object).Count
            if ($remaining -eq 0) {
                Write-Host "`nNo ❌ tasks remaining — all done!" -ForegroundColor Green
                return $true
            }
        }

        Write-Host "$LoopMode iteration $i done." -ForegroundColor DarkGray
    }

    return $false
}

# Resolve permissions
$permissions = if ($Yolo) { "--yolo" } else { "--allow-all-tools" }

switch ($Mode) {
    "plan" {
        Invoke-RalphLoop -LoopMode "plan" -Iterations $MaxIterations -Permissions $permissions
    }
    "build" {
        Invoke-RalphLoop -LoopMode "build" -Iterations $MaxIterations -Permissions $permissions
    }
    "overnight" {
        # Overnight = plan then build, fully unattended
        # Forces --yolo since no human is watching
        $permissions = "--yolo"

        Write-Host ""
        Write-Host ("*" * 50) -ForegroundColor Magenta
        Write-Host "  OVERNIGHT MODE — going fully autonomous" -ForegroundColor Magenta
        Write-Host "  Phase 1: Plan ($PlanIterations iterations)" -ForegroundColor Magenta
        Write-Host "  Phase 2: Build ($MaxIterations iterations)" -ForegroundColor Magenta
        Write-Host "  Permissions: yolo (full auto)" -ForegroundColor Magenta
        Write-Host "  Logs: $logDir/" -ForegroundColor Magenta
        Write-Host ("*" * 50) -ForegroundColor Magenta
        Write-Host ""
        Write-Host "Starting in 10 seconds... (Ctrl+C to abort)" -ForegroundColor Yellow
        Start-Sleep -Seconds 10

        # Phase 1: Generate IMPLEMENTATION_PLAN.md
        Invoke-RalphLoop -LoopMode "plan" -Iterations $PlanIterations -Permissions $permissions

        if (-not (Test-Path "IMPLEMENTATION_PLAN.md")) {
            Write-Error "Planning phase did not create IMPLEMENTATION_PLAN.md. Aborting."
            exit 1
        }

        Write-Host "`nPlan generated. Starting build phase...`n" -ForegroundColor Green

        # Phase 2: Implement from plan
        $done = Invoke-RalphLoop -LoopMode "build" -Iterations $MaxIterations -Permissions $permissions

        if ($done) {
            Write-Host "`nOvernight run complete — all tasks done!" -ForegroundColor Green
        } else {
            Write-Host "`nOvernight run finished ($MaxIterations build iterations). Check IMPLEMENTATION_PLAN.md for remaining work." -ForegroundColor Yellow
        }
    }
}
