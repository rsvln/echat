# bump-versions.ps1
# Smart version bumping for EChat projects.
#
# Rules:
#   EChat.Core / EChat.UI  - bump only if source files changed since last publish
#   EChat.Web  / EChat.MAUI - always bump
#
# Change detection: SHA256 per-file, stored in .src-hash per project.
#
# Usage:
#   .\bump-versions.ps1              - Core+UI (if changed) + Web + MAUI
#   .\bump-versions.ps1 -Mode win   - Core+UI (if changed) + MAUI  (no Web)
#   .\bump-versions.ps1 -Mode web   - Core+UI (if changed) + Web   (no MAUI)
#   .\bump-versions.ps1 -Diagnose   - show exactly which files changed, no version bumps

param(
    [string]$Mode = "all",
    [switch]$Diagnose
)

$projects = switch ($Mode) {
    "win" {
        @{ Name = 'EChat.Core'; Always = $false }
        @{ Name = 'EChat.UI';   Always = $false }
        @{ Name = 'EChat.MAUI'; Always = $true  }
    }
    "web" {
        @{ Name = 'EChat.Core'; Always = $false }
        @{ Name = 'EChat.UI';   Always = $false }
        @{ Name = 'EChat.Web';  Always = $true  }
    }
    default {
        @{ Name = 'EChat.Core'; Always = $false }
        @{ Name = 'EChat.UI';   Always = $false }
        @{ Name = 'EChat.Web';  Always = $true  }
        @{ Name = 'EChat.MAUI'; Always = $true  }
    }
}

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
        Where-Object { $_.Extension -in '.cs','.razor','.csproj' } |
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

    if ($p.Always -and -not $Diagnose) {
        # Always bump - no hash check needed
        $pts = $v.Split('.')
        $pts[2] = [int]$pts[2] + 1
        $nv = $pts -join '.'
        [IO.File]::WriteAllText((Resolve-Path $vf), $nv, $utf8NoBom)
        Write-Host "  $($p.Name): $v -> $nv"
        continue
    }

    $current = Get-SourceFileHashes $dir
    $stored  = if (Test-Path $hashFile) { Deserialize-Hashes (Get-Content $hashFile -Raw) } else { @{} }

    # Diff
    $changed = @()
    $added   = @()
    $removed = @()
    foreach ($f in $current.Keys) {
        if (-not $stored.Contains($f))          { $added   += $f }
        elseif ($stored[$f] -ne $current[$f])   { $changed += $f }
    }
    foreach ($f in $stored.Keys) {
        if (-not $current.Contains($f))         { $removed += $f }
    }

    $hasDiff = $added.Count -gt 0 -or $changed.Count -gt 0 -or $removed.Count -gt 0

    if ($Diagnose) {
        Write-Host ""
        Write-Host "=== $($p.Name) (v$v) ==="
        if (-not $hasDiff) {
            Write-Host "  No changes."
        } else {
            foreach ($f in $added)   { Write-Host "  + $f" }
            foreach ($f in $changed) { Write-Host "  ~ $f" }
            foreach ($f in $removed) { Write-Host "  - $f" }
        }
        continue
    }

    if ($hasDiff) {
        $pts = $v.Split('.')
        $pts[2] = [int]$pts[2] + 1
        $nv = $pts -join '.'
        [IO.File]::WriteAllText((Resolve-Path $vf), $nv, $utf8NoBom)
        [IO.File]::WriteAllText((Join-Path ([IO.Path]::GetFullPath($dir)) '.src-hash'), (Serialize-Hashes $current), $utf8NoBom)
        Write-Host "  $($p.Name): $v -> $nv"
    } else {
        Write-Host "  $($p.Name): $v (no changes, skipped)"
    }
}
