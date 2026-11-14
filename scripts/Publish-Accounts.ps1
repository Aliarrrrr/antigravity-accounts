#requires -Version 7.0
[CmdletBinding()]
param([string]$DotNet, [string]$OutputRoot, [switch]$Release)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'Accounts.Common.ps1')
Invoke-AccountsEnvironment -Root $root -DotNet $DotNet -Action {
        param($context)
        $version = $context.Version
        $git = Get-AccountsGitState $root
        if ($Release) { Assert-AccountsRelease -Root $root -Version $version -State $git }
        foreach ($metadata in @('packages/account-plugin/plugin.json','packages/hostauth-adapter/package.json')) {
            if ((Get-Content -LiteralPath (Join-Path $root $metadata) -Raw | ConvertFrom-Json).version -cne $version) { throw "Package version differs from the component: $metadata" }
        }
        $destination = if ($OutputRoot) { [IO.Path]::GetFullPath($OutputRoot) } else {
            Join-Path $root ('artifacts/accounts/' + $version + '/' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmss'))
        }
        if (Test-Path -LiteralPath $destination) { throw 'Choose a new output directory.' }
        $package = Join-Path $destination 'AntigravityAccounts'
        New-Item -ItemType Directory -Path $package -Force | Out-Null
        $project = Join-Path $root 'src/Antigravity.Accounts/Antigravity.Accounts.csproj'
        $locks = Get-AccountsLockState $root
        try {
            Invoke-AccountsCommand $context.DotNet @('restore',$project,'-p:RuntimeIdentifier=win-x64','-p:SelfContained=true','-p:PublishSingleFile=true','--locked-mode')
            Invoke-AccountsCommand $context.DotNet @('publish',$project,'-c','Release','-r','win-x64','--self-contained','true','--no-restore','-p:PublishSingleFile=true','-p:DebugType=none','-p:DebugSymbols=false','-o',(Join-Path $package 'component'))
        } finally { Assert-AccountsLockState $locks }
        foreach ($name in @('account-plugin','hostauth-adapter')) {
            $source = Join-Path $root ('packages/' + $name)
            $target = Join-Path $package $name
            New-Item -ItemType Directory -Path $target | Out-Null
            $included = if ($name -eq 'account-plugin') {
                @('plugin.json','README.md','assets','window.cjs','native-oauth.mjs','manage-install.mjs','update-recovery.mjs','recovery-and-launch.mjs','native-fallback.mjs','installer-lock.mjs','Hold-InstallLock.ps1','Update-Handoff.ps1','Process-WorkingDirectory.ps1')
            } else { @('src','scripts','README.md','package.json') }
            foreach ($item in $included) { Copy-Item -LiteralPath (Join-Path $source $item) -Destination $target -Recurse }
        }
        Copy-Item -LiteralPath (Join-Path $root 'docs/ACCOUNTS_PLUGIN.md') -Destination (Join-Path $package 'README.md')
        Copy-Item -LiteralPath (Join-Path $root 'docs/ACCOUNTS_VALIDATION.md') -Destination (Join-Path $package 'VALIDATION.md')
        Copy-Item -LiteralPath (Join-Path $root 'docs/COMPATIBILITY_UPDATES.md') -Destination (Join-Path $package 'COMPATIBILITY_UPDATES.md')
        Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $package
        foreach ($entry in @(@{Name='Prepare';Operation='prepare'},@{Name='Install-Preview';Operation='apply'},@{Name='Restore';Operation='restore'})) {
            $lines = @(
                '@echo off', 'setlocal',
                'set "ACCOUNTS_APP_ROOT=%ANTIGRAVITY_INSTALL_ROOT%"',
                'if not defined ACCOUNTS_APP_ROOT set "ACCOUNTS_APP_ROOT=%LOCALAPPDATA%\Programs\antigravity"',
                'if not exist "%ACCOUNTS_APP_ROOT%\Antigravity.exe" (echo Antigravity installation not found. Set ANTIGRAVITY_INSTALL_ROOT. & exit /b 1)',
                'set "ELECTRON_RUN_AS_NODE=1"',
                ('"%ACCOUNTS_APP_ROOT%\Antigravity.exe" "%~dp0account-plugin\manage-install.mjs" ' + $entry.Operation + ' --install-root "%ACCOUNTS_APP_ROOT%" --package-root "%~dp0."'),
                'set "ACCOUNTS_RESULT=%errorlevel%"', 'if not "%ACCOUNTS_RESULT%"=="0" echo Operation failed. Files were preserved; see README.md.',
                'pause', 'exit /b %ACCOUNTS_RESULT%'
            )
            [IO.File]::WriteAllText((Join-Path $package ($entry.Name + '.cmd')), ($lines -join "`r`n") + "`r`n", [Text.Encoding]::ASCII)
        }
        $launch = @(
            '@echo off','setlocal',
            'set "ACCOUNTS_APP_ROOT=%ANTIGRAVITY_INSTALL_ROOT%"',
            'if not defined ACCOUNTS_APP_ROOT set "ACCOUNTS_APP_ROOT=%LOCALAPPDATA%\Programs\antigravity"',
            'if not exist "%ACCOUNTS_APP_ROOT%\Antigravity.exe" (echo Antigravity installation not found. Set ANTIGRAVITY_INSTALL_ROOT. & exit /b 1)',
            'set "ELECTRON_RUN_AS_NODE=1"',
            '"%ACCOUNTS_APP_ROOT%\Antigravity.exe" "%~dp0account-plugin\recovery-and-launch.mjs" --install-root "%ACCOUNTS_APP_ROOT%" --package-root "%~dp0." --show-accounts true',
            'if errorlevel 1 (echo Accounts could not be prepared. Files were preserved; see README.md. & pause & exit /b 1)'
        )
        [IO.File]::WriteAllText((Join-Path $package 'Start-With-Accounts.cmd'), ($launch -join "`r`n") + "`r`n", [Text.Encoding]::ASCII)
        $manifest = [ordered]@{
            application='AntigravityAccounts';version=$version;runtime='win-x64';selfContained=$true
            singleFileCompressed=$true;debugSymbols=$false
            provenance=@{commit=$git.commit;dirty=$git.dirty;tags=@($git.tags);sdk=$context.SdkVersion;release=[bool]$Release}
            validationRecord='VALIDATION.md'
            accountSelection='native-runtime-with-project-refresh';unconfiguredStartup='registration-only'
            officialCredentialsImported=$false;files=@()
            interface='native-model-menu-account-groups';separateAccountWindow=$false
            marketplaceListed=$false;standardPluginOnly=$false
        }
        $manifest.files = @(Get-ChildItem -LiteralPath $package -File -Recurse | Sort-Object FullName | ForEach-Object {
            @{path=[IO.Path]::GetRelativePath($package,$_.FullName).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
        })
        $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $package 'package-manifest.json') -Encoding utf8
        & (Join-Path $PSScriptRoot 'Test-AccountsPackage.ps1') -PackageRoot $package
        $zip = Join-Path $destination ('AntigravityAccounts-' + $version + '-win-x64.zip')
        Compress-Archive -LiteralPath $package -DestinationPath $zip
        & (Join-Path $PSScriptRoot 'Test-AccountsPackage.ps1') -PackageRoot $package -ZipPath $zip
        $zipHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        @{file=[IO.Path]::GetFileName($zip);sha256=$zipHash} | ConvertTo-Json | Set-Content -LiteralPath ($zip + '.sha256.json') -Encoding utf8
        [IO.File]::WriteAllText((Join-Path $destination 'SHA256SUMS.txt'), ($zipHash + '  ' + [IO.Path]::GetFileName($zip) + "`n"), [Text.UTF8Encoding]::new($false))
        Write-Output ('Account plugin package: ' + $zip)
}
