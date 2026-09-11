#requires -Version 5.1

function Get-HubVersion {
    param([Parameter(Mandatory = $true)][string]$Root)
    [xml]$props = Get-Content -LiteralPath (Join-Path $Root 'Directory.Build.props') -Raw
    $versions = @($props.SelectNodes('/Project/PropertyGroup/Version'))
    if ($versions.Count -ne 1) { throw 'Directory.Build.props 必须且只能定义一个 Version。' }
    $version = $versions[0].InnerText.Trim()
    $number = '(0|[1-9][0-9]*)'
    $identifier = '(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)'
    if ($version -cnotmatch "^$number\.$number\.$number(?:-$identifier(?:\.$identifier)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$") {
        throw "Version 不是有效的 SemVer：$version"
    }
    return $version
}

function Invoke-HubCommand {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$Arguments = @()
    )
    $PSNativeCommandUseErrorActionPreference = $false
    $global:LASTEXITCODE = $null
    & $FilePath @Arguments | ForEach-Object { $_ }
    $code = $LASTEXITCODE
    if ($null -eq $code) { throw "命令未返回退出码：$FilePath" }
    if ($code -ne 0) {
        $failure = New-Object System.Exception("命令失败（退出码 $code）：$FilePath")
        $failure.Data['ExitCode'] = $code
        throw $failure
    }
}

function Get-HubExitCode {
    param([System.Management.Automation.ErrorRecord]$Failure)
    if ($Failure.Exception.Data.Contains('ExitCode')) { return [int]$Failure.Exception.Data['ExitCode'] }
    return 1
}

function Find-HubSdk {
    param([string]$Root)
    $candidates = @()
    if ($env:AGPH_DOTNET) { $candidates += $env:AGPH_DOTNET }
    $candidates += Join-Path $Root '.tools/dotnet/dotnet.exe'
    if ($env:DOTNET_ROOT) { $candidates += $env:DOTNET_ROOT }
    $onPath = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($onPath) { $candidates += $onPath.Source }
    if ($env:ProgramFiles) { $candidates += Join-Path $env:ProgramFiles 'dotnet/dotnet.exe' }

    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        try {
            $path = $candidate
            if (Test-Path -LiteralPath $path -PathType Container) { $path = Join-Path $path 'dotnet.exe' }
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "找不到 $path" }
            $path = (Resolve-Path -LiteralPath $path).ProviderPath
            $sdks = @(Invoke-HubCommand -FilePath $path -Arguments @('--list-sdks'))
            if (-not ($sdks | Where-Object { "$_" -match '^10\.0\.[0-9]+\s+\[' })) {
                throw '没有稳定版 .NET 10 SDK；仅安装 runtime 不够。'
            }
            $selected = ((Invoke-HubCommand -FilePath $path -Arguments @('--version')) -join "`n").Trim()
            if ($selected -notmatch '^10\.0\.[0-9]+$') { throw "global.json 未选中稳定版 .NET 10 SDK：$selected" }
            return [pscustomobject]@{ Path = $path; Version = $selected }
        }
        catch {
            if ($env:AGPH_DOTNET -and $candidate -eq $env:AGPH_DOTNET) {
                throw "AGPH_DOTNET 指定的 SDK 不可用：$($_.Exception.Message)"
            }
        }
    }
    throw '找不到可用的稳定版 .NET 10 SDK。请设置 AGPH_DOTNET，或使用 .tools/dotnet、DOTNET_ROOT、PATH、ProgramFiles 中已安装的 SDK；脚本不会安装 SDK。'
}

function Invoke-HubEnvironment {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )
    $names = @('DOTNET_CLI_HOME', 'NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH', 'DOTNET_ROOT',
        'DOTNET_ROOT_X64', 'DOTNET_HOST_PATH', 'DOTNET_MULTILEVEL_LOOKUP', 'DOTNET_NOLOGO',
        'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE',
        'DOTNET_ADD_GLOBAL_TOOLS_TO_PATH', 'PATH')
    $saved = @{}
    foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
    $workingDirectory = [Environment]::CurrentDirectory
    $pushed = $false
    $PSNativeCommandUseErrorActionPreference = $false
    try {
        Push-Location -LiteralPath $Root
        $pushed = $true
        [Environment]::CurrentDirectory = $Root
        $env:DOTNET_CLI_HOME = Join-Path $Root '.dotnet-home'
        $env:NUGET_PACKAGES = Join-Path $Root '.nuget/packages'
        $env:NUGET_HTTP_CACHE_PATH = Join-Path $Root '.nuget/http-cache'
        $env:DOTNET_MULTILEVEL_LOOKUP = '0'
        $env:DOTNET_NOLOGO = '1'
        $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
        $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
        foreach ($directory in @($env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES, $env:NUGET_HTTP_CACHE_PATH)) {
            New-Item -ItemType Directory -Path $directory -Force -ErrorAction Stop | Out-Null
        }
        $sdk = Find-HubSdk -Root $Root
        $env:DOTNET_ROOT = Split-Path -Parent $sdk.Path
        $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
        $env:DOTNET_HOST_PATH = $sdk.Path
        $env:PATH = $env:DOTNET_ROOT + [IO.Path]::PathSeparator + $env:PATH
        $context = [pscustomobject]@{ Root = $Root; DotNet = $sdk.Path; SdkVersion = $sdk.Version; Version = (Get-HubVersion -Root $Root) }
        & $Action $context
    }
    finally {
        foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
        [Environment]::CurrentDirectory = $workingDirectory
        if ($pushed) { Pop-Location }
    }
}

function Assert-HubLockFiles {
    param([string]$Root)
    foreach ($directory in @('src/AGProfileHub.Core', 'src/AGProfileHub.Cli', 'src/AGProfileHub.Gui', 'tests/AGProfileHub.Tests')) {
        $lock = Join-Path $Root "$directory/packages.lock.json"
        if (-not (Test-Path -LiteralPath $lock -PathType Leaf)) {
            throw "锁定还原缺少 $lock。请先运行非锁定 build，审阅并将四个 packages.lock.json 纳入版本控制；锁定模式不会生成或更新锁文件。"
        }
    }
}

function Invoke-HubBuild {
    param($Context, [switch]$Test, [switch]$LockedRestore, [switch]$IncludeSystemTests)
    if ($LockedRestore) { Assert-HubLockFiles -Root $Context.Root }
    $solution = Join-Path $Context.Root 'AGProfileHub.slnx'
    $restore = @('restore', $solution)
    if ($LockedRestore) { $restore += '--locked-mode' }
    Invoke-HubCommand -FilePath $Context.DotNet -Arguments $restore
    Invoke-HubCommand -FilePath $Context.DotNet -Arguments @('build', $solution, '-c', 'Release', '--no-restore')
    if ($Test -or $IncludeSystemTests) {
        $testArguments = @('run', '--project', (Join-Path $Context.Root 'tests/AGProfileHub.Tests/AGProfileHub.Tests.csproj'),
            '-c', 'Release', '--no-build', '--no-restore')
        if ($IncludeSystemTests) { $testArguments += @('--', '--include-system') }
        Invoke-HubCommand -FilePath $Context.DotNet -Arguments $testArguments
    }
}

function Get-HubGitState {
    param([string]$Root)
    $commit = ((Invoke-HubCommand -FilePath 'git' -Arguments @('-C', $Root, 'rev-parse', '--verify', 'HEAD')) -join '').Trim()
    $status = @(Invoke-HubCommand -FilePath 'git' -Arguments @('-C', $Root, 'status', '--porcelain=v1', '--untracked-files=all'))
    $tags = @(Invoke-HubCommand -FilePath 'git' -Arguments @('-C', $Root, 'tag', '--points-at', 'HEAD'))
    return [pscustomobject]@{ Commit = $commit; Dirty = ($status.Count -ne 0); Tags = $tags }
}

function Assert-HubRelease {
    param([string]$Root, [string]$Version, $State)
    if ($State.Dirty) { throw '正式发布要求工作区严格干净（含未跟踪文件）；脚本不会提交或清理文件。' }
    if ($State.Tags -cnotcontains "v$Version") { throw "正式发布要求 HEAD 已有精确标签 v$Version；脚本不会创建标签。" }
    $changelog = Get-Content -LiteralPath (Join-Path $Root 'CHANGELOG.md') -Raw -Encoding UTF8
    $escaped = [regex]::Escape($Version)
    if ($changelog -notmatch "(?m)^##\s+(?:\[$escaped\]|$escaped)(?=\s|$)") {
        throw "CHANGELOG.md 缺少 $Version 的独立版本条目。"
    }
}
