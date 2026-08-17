$ErrorActionPreference = 'Stop'

$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))

function Assert-LastExitCode([string] $step) {
    if ($LASTEXITCODE -ne 0) {
        throw "$step failed with exit code $LASTEXITCODE."
    }
}

dotnet restore (Join-Path $root 'AIEverything.sln')
Assert-LastExitCode 'dotnet restore'

dotnet test (Join-Path $root 'AIEverything.sln') `
    -c Release `
    --no-restore `
    --filter 'Category!=Integration' `
    --nologo `
    --verbosity minimal
Assert-LastExitCode 'dotnet test'

dotnet build (Join-Path $root 'AIEverything.sln') `
    -c Release `
    --no-restore `
    --nologo `
    --verbosity minimal
Assert-LastExitCode 'dotnet build'

$vulnerabilityOutput = dotnet list (Join-Path $root 'AIEverything.sln') `
    package --vulnerable --include-transitive 2>&1
Assert-LastExitCode 'NuGet vulnerability scan'
$vulnerabilityOutput | Write-Output
if (($vulnerabilityOutput | Out-String) -match 'has the following vulnerable packages') {
    throw 'NuGet vulnerability scan found one or more vulnerable packages.'
}

& (Join-Path $PSScriptRoot 'test-skill-contract.ps1')
Assert-LastExitCode 'skill contract'

& (Join-Path $PSScriptRoot 'check-open-source.ps1')
Assert-LastExitCode 'open-source audit'

git -C $root diff --check
Assert-LastExitCode 'git diff check'

Write-Output 'PASS: AIEverything verification completed.'
