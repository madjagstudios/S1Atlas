<#
.SYNOPSIS
    Prints per-project test time and the slowest tests from trx result files.

.DESCRIPTION
    Reads *.trx files (recursively) under -ResultsDirectory, each produced by
    one 'dotnet test <project> --logger "trx;LogFileName=<project>.trx"' run.
    Prints (a) total wall time per test project, taken from each file's Times
    start/finish and labeled by file name, with pass/fail counts, and (b) the
    -TopCount slowest individual tests across all files. Exits 0 even when no
    trx files exist, so timing output never fails a job.
#>
[CmdletBinding()]
param(
    [string]$ResultsDirectory = 'TestResults',
    [int]$TopCount = 20
)

$ErrorActionPreference = 'Stop'

$trxNamespace = @{ trx = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }

function Get-TrxNodes($document, [string]$xpath) {
    # The leading comma keeps single-element results an array; a bare
    # 'return @(...)' unrolls to a scalar whose .Count is $null.
    return ,@(Select-Xml -Xml $document -XPath $xpath -Namespace $trxNamespace |
        ForEach-Object { $_.Node })
}

$files = @()
if (Test-Path $ResultsDirectory) {
    $files = @(Get-ChildItem $ResultsDirectory -Recurse -Filter '*.trx' -File |
        Sort-Object -Property FullName)
}

if ($files.Count -eq 0) {
    Write-Output "Test timing summary: no trx files found under '$ResultsDirectory'."
    exit 0
}

$projects = New-Object System.Collections.Generic.List[object]
$tests = New-Object System.Collections.Generic.List[object]
foreach ($file in $files) {
    $label = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)
    [xml]$document = Get-Content $file.FullName

    $wall = $null
    $times = Get-TrxNodes $document '/trx:TestRun/trx:Times'
    if ($times.Count -gt 0 -and $times[0].start -and $times[0].finish) {
        $wall = [datetimeoffset]::Parse($times[0].finish) - [datetimeoffset]::Parse($times[0].start)
    }

    $passed = 0
    $failed = 0
    $total = 0
    $counters = Get-TrxNodes $document '/trx:TestRun/trx:ResultSummary/trx:Counters'
    if ($counters.Count -gt 0) {
        $passed = [int]$counters[0].passed
        $failed = [int]($counters[0].failed) + [int]($counters[0].error)
        $total = [int]$counters[0].total
    }

    $projects.Add([pscustomobject]@{
            Name = $label
            Wall = $wall
            Passed = $passed
            Failed = $failed
            Total = $total
        })

    foreach ($node in (Get-TrxNodes $document '/trx:TestRun/trx:Results/trx:UnitTestResult')) {
        $duration = [timespan]::Zero
        if ($node.duration) {
            [timespan]::TryParse($node.duration, [ref]$duration) | Out-Null
        }

        $tests.Add([pscustomobject]@{
                Name = $node.testName
                Duration = $duration
                Outcome = $node.outcome
                Project = $label
            })
    }
}

Write-Output "Test timing summary ($($files.Count) files from '$ResultsDirectory'):"
$sequentialTotal = [timespan]::Zero
foreach ($project in ($projects | Sort-Object -Property Name)) {
    if ($project.Wall) {
        $sequentialTotal += $project.Wall
        $wallText = '{0:N1}s wall' -f $project.Wall.TotalSeconds
    } else {
        $wallText = 'wall unknown'
    }

    Write-Output ("  {0}: {1}, {2} passed, {3} failed ({4} total)" -f
        $project.Name, $wallText, $project.Passed, $project.Failed, $project.Total)
}

Write-Output ("Sequential-equivalent total: {0:N1}s." -f $sequentialTotal.TotalSeconds)
Write-Output "Slowest $TopCount tests:"
$rank = 0
foreach ($test in ($tests | Sort-Object -Property Duration -Descending | Select-Object -First $TopCount)) {
    $rank++
    Write-Output ("  {0}. {1:N2}s [{2}] {3} ({4})" -f
        $rank, $test.Duration.TotalSeconds, $test.Outcome, $test.Name, $test.Project)
}

exit 0
