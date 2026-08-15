<#
.SYNOPSIS
    Fails the build when any proprietary or generated artifact is tracked in Git.

.DESCRIPTION
    Inspects tracked paths only (never file contents, so documentation may freely
    mention these names). By default it reads 'git ls-files -z'; pass -TrackedPathsFile
    to inspect an explicit NUL- or newline-separated list instead. Prints every
    violation and exits 1 when any is found, otherwise 0.
#>
[CmdletBinding()]
param(
    [string]$TrackedPathsFile
)

$ErrorActionPreference = 'Stop'

$prohibitedBasenames = @(
    'Cpp2IL.exe',
    'GameAssembly.dll',
    'global-metadata.dat',
    'Assembly-CSharp.dll',
    'atlas.db',
    'atlas.db-wal',
    'atlas.db-shm',
    'installation.json',
    'tool-manifest.json',
    'attempt.json',
    'input-manifest.json',
    'artifact-manifest.json',
    'validation.json',
    'extraction.json',
    'scene-manifest.json',
    'scene-index.manifest.json',
    'scene-validation.json',
    'complete.marker',
    'extraction.lock',
    'stdout.log',
    'stderr.log'
)

$prohibitedSegments = @(
    'candidate-output',
    'retained-output',
    'reconstructed',
    'decompiled',
    '.staging',
    'scene-indexes',
    'scene-staging',
    'scene-recovery'
)

if ($TrackedPathsFile) {
    $content = [System.IO.File]::ReadAllText($TrackedPathsFile)
} else {
    $content = & git ls-files -z
    if ($content -is [array]) {
        $content = ($content -join "`n")
    }
}

$paths = $content -split "[`0`n]" |
    ForEach-Object { $_.Trim() } |
    Where-Object { $_ -ne '' }

$violations = New-Object System.Collections.Generic.List[string]
foreach ($path in $paths) {
    $normalized = $path -replace '\\', '/'
    $basename = ($normalized -split '/')[-1]
    if ($prohibitedBasenames -contains $basename) {
        $violations.Add("$normalized (prohibited file '$basename')")
        continue
    }

    $segments = $normalized -split '/'
    foreach ($segment in $segments) {
        if ($prohibitedSegments -contains $segment) {
            $violations.Add("$normalized (prohibited path segment '$segment')")
            break
        }
    }
}

if ($violations.Count -gt 0) {
    Write-Output "Repository hygiene check failed. Tracked proprietary/generated paths:"
    foreach ($violation in $violations) {
        Write-Output "  - $violation"
    }

    exit 1
}

Write-Output "Repository hygiene check passed: no proprietary or generated paths are tracked."
exit 0
