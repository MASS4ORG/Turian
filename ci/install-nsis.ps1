[CmdletBinding()]
param(
    [ValidateRange(1, 5)]
    [int]$Attempts = 3,
    [ValidateRange(0, 60)]
    [int]$RetryDelaySeconds = 15
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

function Find-MakeNsis {
    $command = Get-Command makensis.exe -CommandType Application -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    foreach ($programDirectory in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
        if ([string]::IsNullOrWhiteSpace($programDirectory)) { continue }
        foreach ($relativePath in @('NSIS/makensis.exe', 'NSIS/Bin/makensis.exe')) {
            $candidate = Join-Path $programDirectory $relativePath
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
        }
    }
}

$compiler = Find-MakeNsis
$installExitCode = $null
for ($attempt = 1; -not $compiler -and $attempt -le $Attempts; $attempt++) {
    Write-Host "Installing NSIS (attempt $attempt/$Attempts)."
    & choco install nsis --no-progress -y
    $installExitCode = $LASTEXITCODE
    $compiler = Find-MakeNsis
    if (-not $compiler -and $attempt -lt $Attempts) {
        Write-Warning "makensis is unavailable after Chocolatey exited with code $installExitCode; retrying."
        Start-Sleep -Seconds ($RetryDelaySeconds * $attempt)
    }
}

if (-not $compiler) {
    throw "NSIS setup failed after $Attempts attempts: makensis.exe was not found (Chocolatey exit code $installExitCode)."
}

$compilerDirectory = Split-Path -Parent $compiler
$env:PATH = $compilerDirectory + [IO.Path]::PathSeparator + $env:PATH
if ($env:GITHUB_PATH) {
    Add-Content -LiteralPath $env:GITHUB_PATH -Value $compilerDirectory -Encoding utf8
}
Write-Host "Using NSIS compiler: $compiler"
exit 0
