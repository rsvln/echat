# bump-versions.ps1
# Smart version bumping for EChat projects.
#
# Rules:
#   All projects - bump only if own source files changed since last publish,
#                  OR (for Web/MAUI) if EChat.Core or EChat.UI changed.
#
# Change detection: SHA256 per-file, stored in .src-hash per project.
#
# Usage:
#   .\bump-versions.ps1              - all projects (smart)
#   .\bump-versions.ps1 -Mode win   - Core+UI+MAUI  (no Web)
#   .\bump-versions.ps1 -Mode web   - Core+UI+Web   (no MAUI)
#   .\bump-versions.ps1 -Diagnose   - show exactly which files changed, no version bumps

param(
    [string]$Mode = "all",
    [switch]$Diagnose
)

$projects = switch ($Mode) {
    "win" {
        @{ Name = 'EChat.Core'; DependsOnShared = $false }
        @{ Name = 'EChat.UI';   DependsOnShared = $false }
        @{ Name = 'EChat.MAUI'; DependsOnShared = $true  }
    }
    "web" {
        @{ Name = 'EChat.Core'; DependsOnShared = $false }
        @{ Name = 'EChat.UI';   DependsOnShared = $false }
        @{ Name = 'EChat.Web';  DependsOnShared = $true  }
    }
    default {
        @{ Name = 'EChat.Core'; DependsOnShared = $false }
        @{ Name = 'EChat.UI';   DependsOnShared = $false }
        @{ Name = 'EChat.Web';  DependsOnShared = $true  }
        @{ Name = 'EChat.MAUI'; DependsOnShared = $true  }
    }
}

# Tracks whether EChat.Core or EChat.UI changed — used to force-bump dependents
$sharedChanged = $false

$utf8NoBom = [Text.UTF8Encoding]::new($false)

function Get-FileHash256([string]$path) {
    $bytes = [IO.File]::ReadAllBytes($path)
    $sha   = [Security.Cryptography.SHA256]::Create()
    $hash  = $sha.ComputeHash($bytes)
    return [BitConverter]::ToString($hash).Replace('-','').ToLower()
}

# Returns ordered dict: relative-path -> hash
function Get-SourceFileHashes([string]$projectDir) {
    $absDir = [IO.Path]::GetFullPath($projectDir)

    $result = [ordered]@{}
    Get-ChildItem $projectDir -Recurse -File |
        Where-Object { $_.Extension -in '.cs','.razor','.csproj','.html','.css','.js' } |
        Sort-Object FullName |
        ForEach-Object {
            $rel = $_.FullName.Substring($absDir.Length).TrimStart('\','/')
            # Skip bin/ and obj/ — generated files, not developer source
            # Use relative path + case-insensitive match (avoids StartsWith casing bugs)
            if ($rel -match '^(bin|obj)[/\\]') { return }
            $result[$rel] = Get-FileHash256 $_.FullName
        }
    return $result
}

# Serialise hash dict to a single string for storage
function Serialize-Hashes($hashes) {
    return ($hashes.Keys | ForEach-Object { "$_=$($hashes[$_])" }) -join "`n"
}

# Deserialise stored string back to dict
function Deserialize-Hashes([string]$text) {
    $result = [ordered]@{}
    foreach ($line in ($text -split "`n")) {
        $line = $line.Trim()
        if ($line -eq '') { continue }
        $eq = $line.IndexOf('=')
        if ($eq -gt 0) { $result[$line.Substring(0,$eq)] = $line.Substring($eq+1) }
    }
    return $result
}

foreach ($p in $projects) {
    $dir      = "src/$($p.Name)"
    $vf       = "$dir/version.txt"
    $hashFile = "$dir/.src-hash"

    if (-not (Test-Path $vf)) {
        Write-Host "  $($p.Name): version.txt not found - skipped"
        continue
    }

    $v = (Get-Content $vf).Trim()

    $current = Get-SourceFileHashes $dir
    $stored  = if (Test-Path $hashFile) { Deserialize-Hashes (Get-Content $hashFile -Raw) } else { @{} }

    # Diff own files
    $ownChanged = @()
    $ownAdded   = @()
    $ownRemoved = @()
    foreach ($f in $current.Keys) {
        if (-not $stored.Contains($f))          { $ownAdded   += $f }
        elseif ($stored[$f] -ne $current[$f])   { $ownChanged += $f }
    }
    foreach ($f in $stored.Keys) {
        if (-not $current.Contains($f))         { $ownRemoved += $f }
    }

    $ownDiff  = $ownAdded.Count -gt 0 -or $ownChanged.Count -gt 0 -or $ownRemoved.Count -gt 0
    $hasDiff  = $ownDiff -or ($p.DependsOnShared -and $sharedChanged)

    if ($Diagnose) {
        Write-Host ""
        Write-Host "=== $($p.Name) (v$v) ==="
        if (-not $ownDiff -and -not ($p.DependsOnShared -and $sharedChanged)) {
            Write-Host "  No changes."
        } else {
            foreach ($f in $ownAdded)   { Write-Host "  + $f" }
            foreach ($f in $ownChanged) { Write-Host "  ~ $f" }
            foreach ($f in $ownRemoved) { Write-Host "  - $f" }
            if ($p.DependsOnShared -and $sharedChanged -and -not $ownDiff) {
                Write-Host "  (bumped because EChat.Core or EChat.UI changed)"
            }
        }
        continue
    }

    if ($hasDiff) {
        $pts = $v.Split('.')
        $pts[2] = [int]$pts[2] + 1
        $nv = $pts -join '.'
        [IO.File]::WriteAllText((Resolve-Path $vf), $nv, $utf8NoBom)
        # Update hash only when own files changed (not when bumped solely due to shared)
        if ($ownDiff) {
            [IO.File]::WriteAllText((Join-Path ([IO.Path]::GetFullPath($dir)) '.src-hash'), (Serialize-Hashes $current), $utf8NoBom)
        }
        $reason = if ($ownDiff) { "" } else { " (Core/UI changed)" }
        Write-Host "  $($p.Name): $v -> $nv$reason"
        # Track that shared libs changed so dependents know
        if (-not $p.DependsOnShared) { $sharedChanged = $true }
    } else {
        Write-Host "  $($p.Name): $v (no changes, skipped)"
    }
}
