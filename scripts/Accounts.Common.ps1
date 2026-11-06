#requires -Version 7.0

function Invoke-AccountsCommand {
    param([Parameter(Mandatory)][string]$FilePath, [string[]]$Arguments = @())
    $PSNativeCommandUseErrorActionPreference = $false
    $global:LASTEXITCODE = $null
    & $FilePath @Arguments | ForEach-Object { $_ }
    if ($null -eq $LASTEXITCODE -or $LASTEXITCODE -ne 0) { throw "Command failed: $FilePath (exit $LASTEXITCODE)" }
}

function Get-AccountsVersion {
    param([Parameter(Mandatory)][string]$Root)
    [xml]$project = Get-Content -LiteralPath (Join-Path $Root 'src/Antigravity.Accounts/Antigravity.Accounts.csproj') -Raw
    $versions = @($project.SelectNodes('/Project/PropertyGroup/Version'))
    if ($versions.Count -ne 1) { throw 'The Accounts project must define exactly one Version.' }
    $version = $versions[0].InnerText.Trim()
    if ($version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') { throw 'The Accounts project version is not SemVer.' }
    return $version
}

function Find-AccountsSdk {
    param([Parameter(Mandatory)][string]$Root, [string]$DotNet)
    $expected = (Get-Content -LiteralPath (Join-Path $Root 'global.json') -Raw | ConvertFrom-Json).sdk.version
    if ($expected -cne '10.0.400') { throw 'This build requires the reviewed .NET SDK 10.0.400.' }
    $candidates = @()
    if ($DotNet) { $candidates += $DotNet }
    elseif ($env:ACCOUNTS_DOTNET) { $candidates += $env:ACCOUNTS_DOTNET }
    $candidates += Join-Path $Root '.tools/dotnet/dotnet.exe'
    if ($env:DOTNET_ROOT) { $candidates += $env:DOTNET_ROOT }
    $onPath = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($onPath) { $candidates += $onPath.Source }
    if ($env:ProgramFiles) { $candidates += Join-Path $env:ProgramFiles 'dotnet/dotnet.exe' }
    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        try {
            $file = $candidate
            if (Test-Path -LiteralPath $file -PathType Container) { $file = Join-Path $file 'dotnet.exe' }
            if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
                $command = Get-Command $file -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($command) { $file = $command.Source }
            }
            if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw 'SDK executable not found.' }
            $file = (Resolve-Path -LiteralPath $file).ProviderPath
            $actual = ((Invoke-AccountsCommand $file @('--version')) -join "`n").Trim()
            if ($actual -cne $expected) { throw "Expected SDK $expected; selected $actual." }
            return $file
        } catch {
            if ($DotNet -or ($env:ACCOUNTS_DOTNET -and $candidate -eq $env:ACCOUNTS_DOTNET)) { throw }
        }
    }
    throw 'Install .NET SDK 10.0.400 or pass -DotNet <dotnet.exe>. No SDK is downloaded by this script.'
}

function Invoke-AccountsEnvironment {
    param([Parameter(Mandatory)][string]$Root, [string]$DotNet, [Parameter(Mandatory)][scriptblock]$Action)
    $names = @('DOTNET_CLI_HOME','NUGET_PACKAGES','NUGET_HTTP_CACHE_PATH','DOTNET_ROOT','DOTNET_ROOT_X64',
        'DOTNET_HOST_PATH','DOTNET_MULTILEVEL_LOOKUP','DOTNET_NOLOGO','DOTNET_CLI_TELEMETRY_OPTOUT',
        'DOTNET_SKIP_FIRST_TIME_EXPERIENCE','DOTNET_ADD_GLOBAL_TOOLS_TO_PATH','PATH')
    $saved = @{}
    foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
    $previousDirectory = [Environment]::CurrentDirectory
    Push-Location -LiteralPath $Root
    try {
        [Environment]::CurrentDirectory = $Root
        $env:DOTNET_CLI_HOME = Join-Path $Root '.dotnet-home'
        $env:NUGET_PACKAGES = Join-Path $Root '.nuget/packages'
        $env:NUGET_HTTP_CACHE_PATH = Join-Path $Root '.nuget/http-cache'
        $env:DOTNET_MULTILEVEL_LOOKUP = '0'
        $env:DOTNET_NOLOGO = '1'
        $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
        $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
        foreach ($directory in @($env:DOTNET_CLI_HOME,$env:NUGET_PACKAGES,$env:NUGET_HTTP_CACHE_PATH)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
        $sdk = Find-AccountsSdk -Root $Root -DotNet $DotNet
        $env:DOTNET_ROOT = Split-Path -Parent $sdk
        $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
        $env:DOTNET_HOST_PATH = $sdk
        $env:PATH = $env:DOTNET_ROOT + [IO.Path]::PathSeparator + $env:PATH
        & $Action ([pscustomobject]@{Root=$Root;DotNet=$sdk;SdkVersion='10.0.400';Version=(Get-AccountsVersion $Root)})
    } finally {
        foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
        [Environment]::CurrentDirectory = $previousDirectory
        Pop-Location
    }
}

function Get-AccountsLockState {
    param([Parameter(Mandatory)][string]$Root)
    $state = @{}
    foreach ($relative in @('src/Antigravity.Accounts/packages.lock.json','src/Antigravity.Accounts/packages.win-x64.lock.json','tests/Antigravity.Accounts.Tests/packages.lock.json')) {
        $file = Join-Path $Root $relative
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Checked-in dependency lock is missing: $relative" }
        $state[$file] = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    }
    return $state
}

function Assert-AccountsLockState {
    param([Parameter(Mandatory)]$State)
    foreach ($file in $State.Keys) {
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -cne $State[$file]) { throw "Restore changed a checked-in dependency lock: $file" }
    }
}

function Get-AccountsGitState {
    param([Parameter(Mandatory)][string]$Root)
    $commit = ((Invoke-AccountsCommand 'git' @('-C',$Root,'rev-parse','--verify','HEAD')) -join '').Trim()
    $status = @(Invoke-AccountsCommand 'git' @('-C',$Root,'status','--porcelain=v1','--untracked-files=all'))
    $tags = @(Invoke-AccountsCommand 'git' @('-C',$Root,'tag','--points-at','HEAD'))
    return [ordered]@{commit=$commit;dirty=($status.Count -ne 0);tags=@($tags)}
}

function Assert-AccountsRelease {
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$Version, [Parameter(Mandatory)]$State)
    if ($State.dirty) { throw 'Release packaging requires a clean checkout, including untracked files.' }
    if ($State.tags -cnotcontains "accounts-v$Version") { throw "Release HEAD must have tag accounts-v$Version." }
    $escaped = [regex]::Escape($Version)
    $changelog = Get-Content -LiteralPath (Join-Path $Root 'CHANGELOG.md') -Raw
    if ($changelog -notmatch "(?m)^##\s+(?:Accounts\s+)?(?:\[$escaped\]|$escaped)(?=\s|$)") { throw "CHANGELOG.md has no entry for $Version." }
}
