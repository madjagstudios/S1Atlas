<#
.SYNOPSIS
    Fails the build when PR commits or added lines contain non-public content.

.DESCRIPTION
    Checks commit messages in 'BaseRef..HEAD' and added lines in
    'git diff BaseRef...HEAD' for AI attribution, machine-specific paths,
    non-AT ticket keys, agent-workflow phrases, tracker URLs, and AI tool or
    vendor names. Removed and context lines are never checked, so existing
    history and untouched product docs cannot trip the gate.

    Run from the repository root. Pass -CommitMessagesFile (NUL-separated
    messages), -DiffFile (unified diff), or -BranchName to check explicit
    inputs instead of git (used by tests); anything without an explicit input
    is derived from git using -BaseRef. The branch-name rule runs only when
    -BranchName is given. Prints every violation and exits 1 when any is
    found, otherwise 0.
#>
[CmdletBinding()]
param(
    [string]$CommitMessagesFile,
    [string]$DiffFile,
    [string]$BranchName,
    [string]$BaseRef
)

$ErrorActionPreference = 'Stop'

# Violation-pattern definitions. The self-exemption list below keeps this
# script from flagging its own patterns.
$aiToolPattern = '\b(Claude|Codex|Copilot|ChatGPT|Anthropic|OpenAI|Gemini|GPT-?\d+[a-z0-9]*)\b'
$ticketPattern = '\b[A-Z]{2,}-\d+\b'
$machinePathPattern = '[A-Za-z]:\\Users\\|/Users/[^/\s]+/|/home/[^/\s]+/'
$trackerPattern = 'atlassian\.net'
$workflowPhrases = @(
    'For agentic workers',
    'REQUIRED SUB-SKILL',
    'superpowers:'
)
$identifierAllowlist = @(
    'UTF-8',
    'UTF-16',
    'UTF-32',
    'SHA-1',
    'SHA-256',
    'SHA-512',
    'ISO-8601',
    'ISO-8859',
    'X86-64'
)

# Gate self-exemption: narrow, exact repo-relative paths. This script, its
# allowlist, and its tests define the violation patterns above, so their own
# added lines are exempt from every content rule. Nothing else is exempt here.
$selfExemptPaths = @(
    'scripts/verify-public-content.ps1',
    'scripts/public-content-allowlist.txt',
    'tests/S1Atlas.IntegrationTests/Repository/PublicContentScriptTests.cs'
)

function Get-AiToolMatch([string]$text) {
    $match = [regex]::Match(
        $text,
        $script:aiToolPattern,
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if ($match.Success) { return $match.Value }
    return $null
}

function Get-TicketViolations([string]$text) {
    $found = New-Object System.Collections.Generic.List[string]
    $ticketMatches = [regex]::Matches($text, $script:ticketPattern)
    foreach ($match in $ticketMatches) {
        $ticket = $match.Value
        if ($ticket -match '^AT-\d+$') { continue }
        if ($script:identifierAllowlist -contains $ticket) { continue }
        if ($ticket -match '^GPT-?\d+[a-z0-9]*$') { continue }
        $found.Add($ticket)
    }

    return $found
}

function Get-MachinePathMatch([string]$text) {
    $match = [regex]::Match(
        $text,
        $script:machinePathPattern,
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if ($match.Success) { return $match.Value }
    return $null
}

function Get-TrackerMatch([string]$text) {
    $match = [regex]::Match(
        $text,
        $script:trackerPattern,
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if ($match.Success) { return $match.Value }
    return $null
}

function Get-WorkflowPhraseMatch([string]$text) {
    foreach ($phrase in $script:workflowPhrases) {
        if ($text.IndexOf($phrase, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return $phrase
        }
    }

    return $null
}

function ConvertTo-GlobRegex([string]$glob) {
    return '^' + (([regex]::Escape($glob) -replace '\\\*', '.*') -replace '\\\?', '.') + '$'
}

function Test-Allowlisted([string]$path, [string[]]$patterns) {
    foreach ($pattern in $patterns) {
        if ([regex]::IsMatch(
                $path,
                $pattern,
                [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
            return $true
        }
    }

    return $false
}

if (-not $CommitMessagesFile -and -not $DiffFile -and -not $BranchName -and -not $BaseRef) {
    throw 'No inputs: pass -BaseRef, input files, or -BranchName.'
}

$allowlistFile = Join-Path $PSScriptRoot 'public-content-allowlist.txt'
$allowlistPatterns = @()
if (Test-Path $allowlistFile) {
    $allowlistPatterns = @(Get-Content $allowlistFile |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -ne '' -and -not $_.StartsWith('#') } |
        ForEach-Object { ConvertTo-GlobRegex $_ })
}

$violations = New-Object System.Collections.Generic.List[string]
$commitCount = 0
$addedLineCount = 0
$branchChecked = $false

function Add-CommitViolations([string]$label, [string]$message) {
    if ([regex]::IsMatch(
            $message,
            '^\s*Co-Authored-By:',
            [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor
            [System.Text.RegularExpressions.RegexOptions]::Multiline)) {
        $script:violations.Add("$label (AI attribution): 'Co-Authored-By' trailer is not allowed")
    }

    if ($message.IndexOf('Generated with', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        $script:violations.Add("$label (AI attribution): 'Generated with' line is not allowed")
    }

    $aiTool = Get-AiToolMatch $message
    if ($aiTool) {
        $script:violations.Add("$label (AI tool name): '$aiTool'")
    }
}

function Add-LineViolations([string]$path, [int]$lineNumber, [string]$line) {
    $location = "$path`:$lineNumber"
    $machinePath = Get-MachinePathMatch $line
    if ($machinePath) {
        $script:violations.Add("$location (machine path): '$machinePath'")
    }

    foreach ($ticket in Get-TicketViolations $line) {
        $script:violations.Add("$location (ticket key): '$ticket'")
    }

    $phrase = Get-WorkflowPhraseMatch $line
    if ($phrase) {
        $script:violations.Add("$location (agent-workflow phrase): '$phrase'")
    }

    $tracker = Get-TrackerMatch $line
    if ($tracker) {
        $script:violations.Add("$location (tracker URL): '$tracker'")
    }

    if (-not (Test-Allowlisted $path $script:allowlistPatterns)) {
        $aiTool = Get-AiToolMatch $line
        if ($aiTool) {
            $script:violations.Add("$location (AI tool name): '$aiTool'")
        }
    }
}

function Test-SelfExempt([string]$path) {
    return $script:selfExemptPaths -contains $path
}

# Commit messages.
$commitInputs = @()
if ($CommitMessagesFile) {
    $index = 0
    foreach ($entry in ([System.IO.File]::ReadAllText($CommitMessagesFile) -split "`0")) {
        $index++
        $commitInputs += @{ Label = "message $index"; Message = $entry }
    }
} elseif ($BaseRef) {
    $rangeShas = @(git rev-list "$BaseRef..HEAD")
    if ($BaseRef -match '^0+$' -and $rangeShas.Count -eq 0) {
        $rangeShas = @(git rev-parse HEAD)
    } elseif ($rangeShas.Count -eq 0) {
        git rev-parse --verify --quiet "$BaseRef^{commit}" > $null
        if ($LASTEXITCODE -ne 0) {
            throw "Base ref '$BaseRef' could not be resolved. Fetch enough history before running this check."
        }
    }

    foreach ($sha in $rangeShas) {
        $message = (git log -1 --format=%B $sha) -join "`n"
        $commitInputs += @{ Label = "commit $($sha.Substring(0, 8))"; Message = $message }
    }
}

foreach ($commit in $commitInputs) {
    $commitCount++
    Add-CommitViolations $commit.Label $commit.Message
}

# Added diff lines.
$diffLines = @()
if ($DiffFile) {
    $diffLines = @(Get-Content $DiffFile)
} elseif ($BaseRef) {
    if ($BaseRef -match '^0+$') {
        $diffLines = @(git show --format= --patch HEAD)
    } else {
        $diffLines = @(git diff "$BaseRef...HEAD")
    }
}

$currentPath = $null
$inHunk = $false
$newLineNumber = 0
foreach ($line in $diffLines) {
    if (-not $inHunk) {
        if ($line.StartsWith('+++ ')) {
            $currentPath = $line.Substring(4).Trim().Trim('"')
            if ($currentPath.StartsWith('b/')) {
                $currentPath = $currentPath.Substring(2)
            }

            if ($currentPath -eq '/dev/null') {
                $currentPath = $null
            }
        } elseif ($line.StartsWith('@@')) {
            $header = [regex]::Match($line, '\+(\d+)')
            if ($header.Success) {
                $newLineNumber = [int]$header.Groups[1].Value
                $inHunk = $true
            }
        } elseif ($line.StartsWith('diff --git')) {
            $currentPath = $null
        }

        continue
    }

    if ($line.StartsWith('@@')) {
        $header = [regex]::Match($line, '\+(\d+)')
        if ($header.Success) {
            $newLineNumber = [int]$header.Groups[1].Value
        }

        continue
    }

    if ($line.StartsWith('+')) {
        if ($currentPath -and -not (Test-SelfExempt $currentPath)) {
            $addedLineCount++
            Add-LineViolations $currentPath $newLineNumber $line.Substring(1)
        }

        $newLineNumber++
    } elseif ($line.StartsWith('-')) {
        # Removed lines are never checked.
    } elseif ($line.StartsWith(' ')) {
        $newLineNumber++
    } elseif ($line.StartsWith('diff --git')) {
        $currentPath = $null
        $inHunk = $false
    }
}

# Branch name.
if ($BranchName) {
    $branchChecked = $true
    $aiTool = Get-AiToolMatch $BranchName
    if ($aiTool) {
        $violations.Add("branch '$BranchName' (branch name): '$aiTool'")
    }
}

if ($violations.Count -gt 0) {
    Write-Output 'Public content check failed. Violations:'
    foreach ($violation in $violations) {
        Write-Output "  - $violation"
    }

    exit 1
}

$summary = "Public content check passed: $commitCount commit message(s), $addedLineCount added line(s) checked"
if ($branchChecked) {
    $summary += ", branch name checked"
}

Write-Output "$summary."
exit 0
