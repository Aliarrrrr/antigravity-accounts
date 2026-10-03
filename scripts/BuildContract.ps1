#requires -Version 7.0

# Only these SDK-supplied packs may differ from the tested application graph.
# All other packages, including System.* and unknown Microsoft.*, are application dependencies.
function Get-HubLockEvidence {
    param([string]$Root)
    $evidence = @{}
    foreach ($name in @('Core','Cli','Gui')) {
        foreach ($file in @('packages.lock.json','packages.win-x64.lock.json')) {
            $path = Join-Path $Root "src/AGProfileHub.$name/$file"
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Checked-in lock missing: $path" }
            $evidence[$path] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        }
    }
    return $evidence
}

function Assert-HubLockEvidence {
    param($Evidence)
    foreach ($path in $Evidence.Keys) {
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $Evidence[$path]) { throw "Restore/publish mutated a checked-in lock: $path" }
    }
}

function Invoke-HubRidRestore {
    param($Context)
    $evidence = Get-HubLockEvidence -Root $Context.Root
    foreach ($name in @('Core','Cli','Gui')) {
        $directory = Join-Path $Context.Root "src/AGProfileHub.$name"
        $null = Assert-HubApplicationGraph -TestedLock (Join-Path $directory 'packages.lock.json') -PublishedLock (Join-Path $directory 'packages.win-x64.lock.json')
    }
    try {
        foreach ($name in @('Cli','Gui')) {
            Invoke-HubCommand -FilePath $Context.DotNet -Arguments @('restore',(Join-Path $Context.Root "src/AGProfileHub.$name/AGProfileHub.$name.csproj"),'-r','win-x64','-p:SelfContained=true','--locked-mode')
        }
    } finally { Assert-HubLockEvidence -Evidence $evidence }
    foreach ($name in @('Core','Cli','Gui')) {
        $directory = Join-Path $Context.Root "src/AGProfileHub.$name"
        $null = Assert-HubResolvedGraph -Lock (Join-Path $directory 'packages.win-x64.lock.json') -Resolved (Join-Path $directory 'obj/project.assets.json')
    }
}

function Get-HubDependencyGraph {
    param([Parameter(Mandatory)][string]$Path, [ValidateSet('Lock','Assets','Deps')][string]$Format = 'Lock')
    $document = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json -AsHashtable
    $packages = @{}
    if ($Format -eq 'Lock') {
        if ($document.version -ne 1 -or $document.dependencies -isnot [System.Collections.IDictionary]) { throw "Invalid lock file: $Path" }
        foreach ($framework in $document.dependencies.Values) {
            foreach ($id in $framework.Keys) {
                $entry = $framework[$id]
                if ($entry.type -eq 'Project') { continue }
                if ($entry.type -notin @('Direct','Transitive')) { throw "Unknown dependency type in $Path" }
                $item = [ordered]@{id=$id.ToLowerInvariant();version=$entry.resolved;contentHash=$entry.contentHash}
                if (-not $item.version -or -not $item.contentHash) { throw "Incomplete dependency: $id" }
                if ($packages.ContainsKey($item.id) -and (($packages[$item.id] | ConvertTo-Json -Compress) -cne ($item | ConvertTo-Json -Compress))) { throw "Conflicting dependency: $id" }
                $packages[$item.id] = $item
            }
        }
    } else {
        if ($document.libraries -isnot [System.Collections.IDictionary]) { throw "Missing package libraries: $Path" }
        foreach ($key in $document.libraries.Keys) {
            $entry = $document.libraries[$key]
            if ($entry.type -notin @('package','runtimepack')) { continue }
            $parts = $key.Split('/')
            if ($parts.Count -ne 2) { throw "Incomplete dependency: $key" }
            $id = $parts[0].ToLowerInvariant()
            if ($entry.type -eq 'runtimepack') {
                $id = $id -creplace '^runtimepack\.', ''
                if ($id -notin @('microsoft.netcore.app.runtime.win-x64','microsoft.windowsdesktop.app.runtime.win-x64')) { throw "Unknown SDK runtime pack: $key" }
                $packages[$id] = [ordered]@{id=$id;version=$parts[1];contentHash=$null;source='exact-sdk';integrity='manifest-file-sha256'}
            } else {
                if (-not $entry.sha512) { throw "Incomplete dependency: $key" }
                $packages[$id] = [ordered]@{id=$id;version=$parts[1];contentHash=($entry.sha512 -creplace '^sha512-', '')}
            }
        }
    }
    $runtimeIds = @('microsoft.netcore.app.runtime.win-x64','microsoft.windowsdesktop.app.runtime.win-x64')
    $platformIds = @('microsoft.netcore.app.ref','microsoft.windowsdesktop.app.ref','microsoft.windows.sdk.net.ref')
    $graph = [ordered]@{application=@();runtime=@();platform=@()}
    foreach ($id in ($packages.Keys | Sort-Object)) {
        $kind = if ($id -in $runtimeIds) { 'runtime' } elseif ($id -in $platformIds) { 'platform' } else { 'application' }
        $graph[$kind] += $packages[$id]
    }
    return $graph
}

function Assert-HubGraphEqual {
    param($Expected, $Actual, [string]$Label)
    if (($Expected | ConvertTo-Json -Depth 8 -Compress) -cne ($Actual | ConvertTo-Json -Depth 8 -Compress)) { throw "Dependency graph differs: $Label" }
}

function Assert-HubApplicationGraph {
    param([Parameter(Mandatory)][string]$TestedLock, [Parameter(Mandatory)][string]$PublishedLock)
    $tested = Get-HubDependencyGraph -Path $TestedLock
    $published = Get-HubDependencyGraph -Path $PublishedLock
    Assert-HubGraphEqual $tested.application $published.application 'tested versus published application packages (id/version/contentHash)'
    Assert-HubGraphEqual $tested.platform $published.platform 'tested versus published platform packages'
    return $published
}

function Assert-HubResolvedGraph {
    param([Parameter(Mandatory)][string]$Lock, [Parameter(Mandatory)][string]$Resolved, [ValidateSet('Assets','Deps')][string]$Format='Assets')
    $expected = Get-HubDependencyGraph -Path $Lock
    $actual = Get-HubDependencyGraph -Path $Resolved -Format $Format
    Assert-HubGraphEqual $expected.application $actual.application "resolved application packages: $Resolved"
    Assert-HubGraphEqual $expected.platform $actual.platform "resolved platform packages: $Resolved"
    # SDK runtime packs are injected by publish; their version and manifest file SHA256 evidence are explicit.
    return $actual
}

function Get-HubPackagePath {
    param([string]$Root, [string]$Relative)
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative)) { throw 'Invalid package-relative path.' }
    $path = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if (-not $path.StartsWith($Root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest path escaped package.' }
    $current = $path
    while ($current -ne $Root) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Package path contains a link.' }
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    return $path
}

function Assert-HubPackageManifest {
    param([Parameter(Mandatory)][string]$PackageRoot)
    $package = [IO.Path]::GetFullPath($PackageRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ((Get-Item -LiteralPath $package -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Package root is a link.' }
    $manifest = Get-Content -LiteralPath (Join-Path $package 'package-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($claim in @('selfContained','experimental','manualAcceptance')) {
        if ($manifest.$claim -isnot [bool]) { throw "Package claim must be a JSON boolean: $claim" }
    }
    if ($manifest.provenance.dirty -isnot [bool]) { throw 'Dirty provenance must be a JSON boolean.' }
    if ($manifest.schemaVersion -ne 1 -or $manifest.application -cne 'AntigravityProfileHubNext' -or $manifest.runtime -cne 'win-x64' -or $manifest.selfContained -ne $true -or $manifest.experimental -ne $true -or $manifest.manualAcceptance -ne $false) { throw 'Unexpected package claims.' }
    if ($manifest.provenance.commit -cnotmatch '^[0-9a-f]{40}$' -or $manifest.provenance.sdk -cne '10.0.400' -or @($manifest.provenance.locks).Count -ne 6 -or @($manifest.provenance.dependencies).Count -ne 3) { throw 'Missing or invalid build provenance.' }
    if ((@($manifest.provenance.dependencies.project | Sort-Object) -join ',') -cne 'Cli,Core,Gui') { throw 'Missing or duplicated project provenance.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $manifest.files) {
        $path = Get-HubPackagePath -Root $package -Relative $file.path
        if (-not $seen.Add($path) -or $file.sha256 -cnotmatch '^[0-9A-Fa-f]{64}$' -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $file.sha256.ToUpperInvariant()) { throw 'Package digest mismatch or duplicate file.' }
    }
    foreach ($file in (Get-ChildItem -LiteralPath $package -Recurse -File -Force)) {
        if ($file.FullName -eq (Join-Path $package 'package-manifest.json')) { continue }
        if (-not $seen.Contains($file.FullName)) { throw 'Package contains an unlisted file.' }
    }
    foreach ($lock in $manifest.provenance.locks) {
        $path = Get-HubPackagePath -Root $package -Relative $lock.path
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $lock.sha256) { throw 'Provenance lock digest mismatch.' }
    }
    foreach ($dependency in $manifest.provenance.dependencies) {
        $requiredDeps = switch ($dependency.project) { 'Cli' { 'cli/agprofile-next.deps.json' } 'Gui' { 'hub/AGProfileHubNext.Gui.deps.json' } 'Core' { $null } }
        if ($dependency.depsPath -cne $requiredDeps) { throw 'Incorrect published dependency evidence path.' }
        $tested = @($manifest.provenance.locks | Where-Object { $_.project -ceq $dependency.project -and $_.graph -ceq 'tested' })
        $published = @($manifest.provenance.locks | Where-Object { $_.project -ceq $dependency.project -and $_.graph -ceq 'published' })
        if ($tested.Count -ne 1 -or $published.Count -ne 1) { throw 'Missing tested/published lock evidence.' }
        $graph = Assert-HubApplicationGraph -TestedLock (Get-HubPackagePath $package $tested[0].path) -PublishedLock (Get-HubPackagePath $package $published[0].path)
        Assert-HubGraphEqual $graph.application $dependency.application 'manifest application provenance'
        Assert-HubGraphEqual $graph.platform $dependency.platform 'manifest platform provenance'
        if ($dependency.depsPath) {
            $resolved = Assert-HubResolvedGraph -Lock (Get-HubPackagePath $package $published[0].path) -Resolved (Get-HubPackagePath $package $dependency.depsPath) -Format Deps
            Assert-HubGraphEqual $resolved.runtime $dependency.runtime 'manifest runtime provenance'
        } else { Assert-HubGraphEqual $graph.runtime $dependency.runtime 'manifest runtime provenance' }
    }
}
