#requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$FrameworkDependent,
    [switch]$LockedRestore,
    [switch]$IncludeSystemTests,
    [switch]$Release,
    [switch]$Installer
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
$root = Split-Path -Parent $PSScriptRoot
$code = 0
try {
    Invoke-HubEnvironment -Root $root -Action {
        param($context)
        $state = Get-HubGitState -Root $root
        if ($Release) { Assert-HubRelease -Root $root -Version $context.Version -State $state }
        $locked = $LockedRestore -or $Release
        Invoke-HubBuild -Context $context -Test -LockedRestore:$locked -IncludeSystemTests:$IncludeSystemTests

        $runtime = 'win-x64'
        $stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N')
        $stagingParent = Join-Path $root 'artifacts/.staging'
        $destinationParent = Join-Path $root "artifacts/package/$($context.Version)"
        New-Item -ItemType Directory -Path $stagingParent -Force | Out-Null
        $staging = Join-Path $stagingParent "$($context.Version)-$stamp"
        New-Item -ItemType Directory -Path $staging | Out-Null
        $packageRoot = Join-Path $staging 'AGProfileHub'
        New-Item -ItemType Directory -Path $packageRoot | Out-Null
        $selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
        foreach ($spec in @(@{ Name = 'Cli'; Directory = 'cli' }, @{ Name = 'Gui'; Directory = 'hub' })) {
            $project = Join-Path $root "src/AGProfileHub.$($spec.Name)/AGProfileHub.$($spec.Name).csproj"
            $restoreArgs = @('restore', $project, '-r', $runtime, "-p:SelfContained=$selfContained")
            if ($locked) { $restoreArgs += '--locked-mode' }
            Invoke-HubCommand -FilePath $context.DotNet -Arguments $restoreArgs
            $publishArgs = @('publish', $project, '-c', 'Release', '-r', $runtime, '--no-restore',
                '--self-contained', $selfContained, '-p:PublishSingleFile=false', '-o', (Join-Path $packageRoot $spec.Directory))
            Invoke-HubCommand -FilePath $context.DotNet -Arguments $publishArgs
        }

        # 仅复制显式列出的说明文件与可选探针，绝不复制数据根、配置或 Token。
        foreach ($name in @('README.md', 'SECURITY.md', 'CHANGELOG.md', 'docs/VERSIONING.md')) {
            $file = Join-Path $root $name
            if (Test-Path -LiteralPath $file -PathType Leaf) { Copy-Item -LiteralPath $file -Destination $packageRoot }
        }
        # 复制可选探针脚本（若存在），用于安装包结构与人工双账号验收。
        $scripts = Join-Path $packageRoot 'scripts'
        New-Item -ItemType Directory -Path $scripts | Out-Null
        foreach ($name in @('Probe-Antigravity.ps1')) {
            $source = Join-Path $PSScriptRoot $name
            if (Test-Path -LiteralPath $source -PathType Leaf) { Copy-Item -LiteralPath $source -Destination $scripts }
        }
        $finalState = Get-HubGitState -Root $root
        if ($finalState.Commit -cne $state.Commit -or (Get-HubVersion -Root $root) -cne $context.Version) {
            throw '构建期间 HEAD 或版本发生变化；不交付本次产物。'
        }
        if ($Release) { Assert-HubRelease -Root $root -Version $context.Version -State $finalState }
        $manifest = [ordered]@{
            schemaVersion = 2
            # 必须与 src/AGProfileHub.Core/Domain/Models.cs 的 SchemaVersions.Current 一致（当前为 1）。
            configSchemaVersion = 1
            version = $context.Version
            hubVersion = $context.Version
            commit = $finalState.Commit
            dirty = $finalState.Dirty
            runtime = $runtime
            builtAt = [DateTime]::UtcNow.ToString('o')
            sdkVersion = $context.SdkVersion
            selfContained = (-not $FrameworkDependent)
            release = [bool]$Release
            includeSystemTests = [bool]$IncludeSystemTests
            experimental = $true
            # 描述 0.4.0 起的真实隔离机制：每 Profile 独立 home + 语言服务文件令牌存储，
            # 启动时自证（fail-closed）。不再有任何沙盒产品依赖。
            authIsolation = 'per-profile-home-and-file-token-store-verified-on-desktop-2.12.2'
            externalDependency = 'official Antigravity Desktop 2.12.2; no sandbox product, no admin rights'
        }
        $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packageRoot 'package-manifest.json') -Encoding UTF8
        "@echo off`r`nstart `"`" `"%~dp0hub\AGProfileHub.Gui.exe`" %*`r`nexit /b %errorlevel%`r`n" |
            Set-Content -LiteralPath (Join-Path $packageRoot 'Start Hub.cmd') -Encoding ASCII
        "@echo off`r`n`"%~dp0cli\AGProfileHub.Cli.exe`" %*`r`nexit /b %errorlevel%`r`n" |
            Set-Content -LiteralPath (Join-Path $packageRoot 'agprofile.cmd') -Encoding ASCII
        $archiveName = "AGProfileHub-$($context.Version)-$runtime.zip"
        $zip = Join-Path $staging $archiveName
        Compress-Archive -LiteralPath $packageRoot -DestinationPath $zip
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash.ToLowerInvariant()
        "$hash  $archiveName" | Set-Content -LiteralPath ($zip + '.sha256') -Encoding ASCII
        [ordered]@{ Algorithm = 'SHA256'; Hash = $hash; File = $archiveName } | ConvertTo-Json |
            Set-Content -LiteralPath ($zip + '.sha256.json') -Encoding UTF8

        # 只有所有步骤成功才移入交付目录；Directory.Move 拒绝覆盖已有目录。
        New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
        $destination = Join-Path $destinationParent $stamp
        [IO.Directory]::Move($staging, $destination)
        Write-Output "Package: $(Join-Path $destination 'AGProfileHub')"
        Write-Output "Archive: $(Join-Path $destination $archiveName)"

        # 可选：本地构建每日使用安装包（需自包含包）。
        if ($Installer) {
            if ($FrameworkDependent) { throw 'The daily-use installer requires a self-contained package.' }
            & (Join-Path $PSScriptRoot 'Build-Installer.ps1') -PackageRoot (Join-Path $destination 'AGProfileHub')
            if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
        }
    }
}
catch {
    $code = Get-HubExitCode -Failure $_
    Write-Error -Message $_.Exception.Message -ErrorAction Continue
}
exit $code
