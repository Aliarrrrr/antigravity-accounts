#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('help', 'build', 'test', 'run', 'cli', 'publish', 'version', 'check')]
    [string]$Command = 'help',
    [string]$DataRoot,
    [string[]]$CliArgs = @(),
    [switch]$Test,
    [switch]$LockedRestore,
    [switch]$IncludeSystemTests,
    [switch]$FrameworkDependent,
    [switch]$Release
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scripts/Common.ps1')
$root = $PSScriptRoot
$code = 0
try {
    if ($Command -eq 'help') {
        Write-Output @'
AGProfileHub 开发入口（PowerShell 5.1 / 7；不安装 SDK，不修改执行策略）
  .\hub.ps1 help
  .\hub.ps1 version
  .\hub.ps1 check [-LockedRestore] [-Release]
  .\hub.ps1 build [-Test] [-LockedRestore] [-IncludeSystemTests]
  .\hub.ps1 test [-LockedRestore] [-IncludeSystemTests]
  .\hub.ps1 run [-DataRoot <路径>] [-LockedRestore]
  .\hub.ps1 cli [-DataRoot <路径>] [-CliArgs @('list','--json')] [-LockedRestore]
  .\hub.ps1 publish [-FrameworkDependent] [-LockedRestore] [-IncludeSystemTests] [-Release]

build/test/publish 不启动真实应用；test 运行控制台测试器，不使用 dotnet test。
-IncludeSystemTests 显式传入测试器 --include-system，启用系统测试且隐含 -Test。
run/cli 才会启动程序；相对 DataRoot 按调用者当前目录解析。
publish 先构建和测试；-Release 还要求锁定还原、干净工作区、HEAD 精确版本标签及 changelog。
默认发布自包含 win-x64；-FrameworkDependent 需要目标机安装 .NET 10 Desktop Runtime。
'@
    }
    else {
        if (($DataRoot -or $CliArgs.Count -gt 0) -and $Command -notin @('run', 'cli')) { throw 'DataRoot/CliArgs 仅用于 run 或 cli。' }
        if ($CliArgs.Count -gt 0 -and $Command -ne 'cli') { throw 'CliArgs 仅用于 cli。' }
        if ($Release -and $Command -notin @('publish', 'check')) { throw 'Release 仅用于 publish 或 check。' }
        if ($FrameworkDependent -and $Command -ne 'publish') { throw 'FrameworkDependent 仅用于 publish。' }
        if ($Test -and $Command -notin @('build', 'test')) { throw 'Test 仅用于 build 或 test；publish 总会测试。' }
        if ($IncludeSystemTests -and $Command -notin @('build', 'test', 'publish')) { throw 'IncludeSystemTests 仅用于 build、test 或 publish。' }
        if ($LockedRestore -and $Command -eq 'version') { throw 'version 不执行还原。' }
        switch ($Command) {
            'version' { Write-Output (Get-HubVersion -Root $root) }
            { $_ -in @('build', 'test') } {
                $buildArgs = @{ Test = ($Test -or $Command -eq 'test'); LockedRestore = $LockedRestore; IncludeSystemTests = $IncludeSystemTests }
                & (Join-Path $root 'scripts/Build.ps1') @buildArgs
                $code = $LASTEXITCODE
                if ($code -ne 0) { throw "构建/测试失败（退出码 $code）。" }
            }
            'publish' {
                $publishArgs = @{ FrameworkDependent = $FrameworkDependent; LockedRestore = $LockedRestore; IncludeSystemTests = $IncludeSystemTests; Release = $Release }
                & (Join-Path $root 'scripts/Publish.ps1') @publishArgs
                $code = $LASTEXITCODE
                if ($code -ne 0) { throw "发布失败（退出码 $code）。" }
            }
            default {
                $applicationArgs = @()
                if ($DataRoot) {
                    $absoluteDataRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($DataRoot)
                    $applicationArgs += @('--data-root', $absoluteDataRoot)
                }
                $applicationArgs += $CliArgs
                Invoke-HubEnvironment -Root $root -Action {
                    param($context)
                    if ($Command -eq 'check') {
                        $state = Get-HubGitState -Root $root
                        if ($Release) { Assert-HubRelease -Root $root -Version $context.Version -State $state }
                        if ($LockedRestore -or $Release) { Assert-HubLockFiles -Root $root }
                        Write-Output "版本：$($context.Version)"
                        Write-Output "SDK：$($context.SdkVersion) ($($context.DotNet))"
                        Write-Output "Git：$($state.Commit)，dirty=$($state.Dirty)"
                        Write-Output '环境检查完成；未构建、未测试、未启动应用。'
                    }
                    else {
                        Invoke-HubBuild -Context $context -LockedRestore:$LockedRestore
                        $projectName = if ($Command -eq 'run') { 'Gui' } else { 'Cli' }
                        $project = Join-Path $root "src/AGProfileHub.$projectName/AGProfileHub.$projectName.csproj"
                        $runArgs = @('run', '--project', $project, '-c', 'Release', '--no-build', '--no-restore', '--') + $applicationArgs
                        Invoke-HubCommand -FilePath $context.DotNet -Arguments $runArgs
                    }
                }
            }
        }
    }
}
catch {
    if ($code -eq 0) { $code = Get-HubExitCode -Failure $_ }
    Write-Error -Message $_.Exception.Message -ErrorAction Continue
}
exit $code
