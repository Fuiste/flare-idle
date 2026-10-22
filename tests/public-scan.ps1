param(
    [string]$Root = (Split-Path $PSScriptRoot -Parent),
    [string]$Archive
)
$ErrorActionPreference = 'Stop'
$Root = [IO.Path]::GetFullPath($Root)
$findings = [Collections.Generic.List[string]]::new()
$patterns = [ordered]@{
    'absolute user profile path' = '(?i)[a-z]:[\\/]+Users[\\/]+(?!Public(?:[\\/]|$)|Default(?:[\\/]|$)|example(?:[\\/]|$))[^\\/\s"''<>]+'
    'private network address' = '(?<![\d.])(?:10(?:\.\d{1,3}){3}|192\.168(?:\.\d{1,3}){2}|172\.(?:1[6-9]|2\d|3[01])(?:\.\d{1,3}){2})(?![\d.])'
    'hardware UUID' = '(?i)GPU-(?!00000000-0000-0000-0000-000000000000)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}'
    'private key' = '-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----'
    'GitHub token' = '(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{30,})'
    'API token' = '\bsk-[A-Za-z0-9_-]{24,}\b'
    'literal bearer credential' = '(?i)Bearer\s+[A-Za-z0-9._-]{24,}'
    'wallet-like address' = '(?:\bbc1[qp][a-z0-9]{30,90}\b|\b[13][a-km-zA-HJ-NP-Z1-9]{25,34}\b|\b0x[0-9a-fA-F]{40}\b)'
}
$forbiddenNames = '(?i)(^|/)(config|settings|status)(?:-[^/]*)?\.json$|(^|/)(connection-[^/]+\.json|guardian-[^/]+\.ready|last-error\.txt|fatal-error\.txt)$'
$binaryExtensions = @('.exe','.dll','.zip','.7z','.tar','.gz','.pdb','.safetensors','.gguf','.ggml','.onnx','.pt','.pth','.ckpt','.bin')

function Inspect-Content([string]$Name, [byte[]]$Bytes) {
    if ($Bytes.Length -gt 16MB) { $findings.Add("$Name : oversized publication file"); return }
    # ASCII and UTF-16 scans also inspect literals embedded in the release executable.
    $texts = @([Text.Encoding]::UTF8.GetString($Bytes), [Text.Encoding]::Unicode.GetString($Bytes))
    foreach ($entry in $patterns.GetEnumerator()) {
        if ($texts | Where-Object { [regex]::IsMatch($_, $entry.Value) }) {
            $findings.Add("$Name : $($entry.Key)")
        }
    }
}

if ($Archive) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $allowed = @('FlareIdle.exe','README.md','LICENSE','THIRD_PARTY_NOTICES.md',
        'docs/compatibility.md','docs/security.md','docs/troubleshooting.md','docs/development.md')
    $zip = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($Archive))
    try {
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName.Replace('\','/')
            if (-not $seen.Add($name)) { $findings.Add("$name : duplicate archive entry") }
            if ($name -notin $allowed) { $findings.Add("$name : not on release allowlist"); continue }
            if ($entry.Length -gt 16MB) { $findings.Add("$name : oversized publication file"); continue }
            $stream = $entry.Open()
            $buffer = [IO.MemoryStream]::new()
            try { $stream.CopyTo($buffer); Inspect-Content $name $buffer.ToArray() }
            finally { $buffer.Dispose(); $stream.Dispose() }
        }
        foreach ($name in $allowed) { if (-not $seen.Contains($name)) { $findings.Add("$name : missing from archive") } }
    } finally { $zip.Dispose() }
} else {
    $tracked = @()
    if (Test-Path -LiteralPath (Join-Path $Root '.git')) {
        $tracked = @(& git -C $Root ls-files)
        if ($LASTEXITCODE -ne 0) { throw 'could not enumerate tracked files' }
    }
    if ($tracked.Count -eq 0) {
        $tracked = @(Get-ChildItem -LiteralPath $Root -Recurse -File | Where-Object {
            $relative = $_.FullName.Substring($Root.Length + 1).Replace('\','/')
            $relative -notmatch '^(\.git|bin|build|build-temp|dist)/' -and $relative -notmatch '^tests/[^/]+\.exe$'
        } | ForEach-Object { $_.FullName.Substring($Root.Length + 1).Replace('\','/') })
    }
    foreach ($name in $tracked) {
        $normalized = $name.Replace('\','/')
        if ($normalized -match $forbiddenNames) { $findings.Add("$normalized : local state must not be tracked") }
        $extension = [IO.Path]::GetExtension($normalized).ToLowerInvariant()
        if ($extension -in $binaryExtensions) { $findings.Add("$normalized : binary/model must not be tracked"); continue }
        $path = [IO.Path]::GetFullPath((Join-Path $Root $name))
        if (-not $path.StartsWith($Root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            $findings.Add("$normalized : path outside source root"); continue
        }
        Inspect-Content $normalized ([IO.File]::ReadAllBytes($path))
    }
}
if ($findings.Count) {
    $findings | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    throw "public content scan failed ($($findings.Count) findings)"
}
Write-Output 'public content scan passed'
