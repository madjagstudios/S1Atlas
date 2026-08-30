[CmdletBinding()]
param(
    [string]$RepoRoot,
    [ValidateRange(1, 20)]
    [int]$Runs = 3
)

$ErrorActionPreference = 'Stop'

if (-not $RepoRoot) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}

function Get-ProcessTreeIds([int]$RootProcessId) {
    $processes = @(Get-CimInstance Win32_Process)
    $children = @{}
    foreach ($process in $processes) {
        $parentId = [int]$process.ParentProcessId
        if (-not $children.ContainsKey($parentId)) {
            $children[$parentId] = [System.Collections.Generic.List[int]]::new()
        }

        $children[$parentId].Add([int]$process.ProcessId)
    }

    $ids = [System.Collections.Generic.List[int]]::new()
    $pending = [System.Collections.Generic.Queue[int]]::new()
    $pending.Enqueue($RootProcessId)
    while ($pending.Count -gt 0) {
        $id = $pending.Dequeue()
        if ($ids.Contains($id)) {
            continue
        }

        $ids.Add($id)
        if ($children.ContainsKey($id)) {
            foreach ($childId in $children[$id]) {
                $pending.Enqueue($childId)
            }
        }
    }

    return $ids
}

function Read-McpResponse([System.IO.StreamReader]$Reader) {
    $line = $Reader.ReadLine()
    if ($null -eq $line) {
        throw 'MCP stdout closed before the response completed.'
    }

    return $line
}

function Invoke-McpLaunchBenchmark([string]$Name, [string[]]$Arguments) {
    $results = [System.Collections.Generic.List[object]]::new()
    for ($run = 1; $run -le $Runs; $run++) {
        $startInfo = [Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = 'dotnet'
        $startInfo.WorkingDirectory = $RepoRoot
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.RedirectStandardInput = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $startInfo.Arguments = ($Arguments | ForEach-Object { '"' + $_.Replace('"', '\\"') + '"' }) -join ' '

        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $startInfo
        $watch = [Diagnostics.Stopwatch]::StartNew()
        if (-not $process.Start()) {
            throw "Unable to start $Name."
        }

        $initialize = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"s1atlas-launch-benchmark","version":"1.0"}}}'
        $process.StandardInput.WriteLine($initialize)
        $process.StandardInput.Flush()
        $initializeResponse = Read-McpResponse $process.StandardOutput

        $process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}')
        $process.StandardInput.WriteLine('{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}')
        $process.StandardInput.Flush()
        $toolsResponse = Read-McpResponse $process.StandardOutput
        $watch.Stop()

        Start-Sleep -Milliseconds 100
        $treeIds = @(Get-ProcessTreeIds $process.Id)
        $workingSet = 0L
        foreach ($id in $treeIds) {
            try {
                $workingSet += (Get-Process -Id $id -ErrorAction Stop).WorkingSet64
            } catch [System.ArgumentException] {
                # A short-lived wrapper may exit between the process-tree and memory samples.
            }
        }

        $process.StandardInput.Close()
        if (-not $process.WaitForExit(2000)) {
            $process.Kill($true)
            $process.WaitForExit()
        }

        if ($initializeResponse -notmatch '"result"' -or $toolsResponse -notmatch '"tools"') {
            throw "MCP initialize/tool-list failed for ${Name}."
        }

        $results.Add([pscustomobject]@{
            Name = $Name
            Run = $run
            WallTimeMs = [math]::Round($watch.Elapsed.TotalMilliseconds, 1)
            ProcessCount = $treeIds.Count
            WorkingSetMiB = [math]::Round($workingSet / 1MB, 1)
        })
    }

    return $results
}

$project = Join-Path $RepoRoot 'src/S1Atlas.Mcp/S1Atlas.Mcp.csproj'
$dll = Join-Path $RepoRoot 'src/S1Atlas.Mcp/bin/Release/net8.0/S1Atlas.Mcp.dll'
if (-not (Test-Path -LiteralPath $dll)) {
    throw "Release DLL not found at $dll. Build it first with: dotnet build src/S1Atlas.Mcp/S1Atlas.Mcp.csproj --configuration Release --no-restore"
}

$direct = Invoke-McpLaunchBenchmark 'direct-dll' @($dll, 'mcp', 'serve')
$dotnetRun = Invoke-McpLaunchBenchmark 'dotnet-run-control' @('run', '--project', $project, '--configuration', 'Release', '--no-build', '--no-restore', '--', 'mcp', 'serve')
(@($direct) + @($dotnetRun)) | Format-Table -AutoSize
