#requires -Version 7.0
[CmdletBinding()]
param([string]$DotNet, [string]$OutputRoot)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'Common.ps1')
$originalSdk = $env:AGPH_DOTNET
try {
    if ($DotNet) { $env:AGPH_DOTNET = $DotNet }
    Invoke-HubEnvironment -Root $root -Action {
        param($context)
        $version = '0.4.0-preview.7'
        $destination = if ($OutputRoot) { [IO.Path]::GetFullPath($OutputRoot) } else {
            Join-Path $root ('artifacts/accounts/' + $version + '/' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmss'))
        }
        if (Test-Path -LiteralPath $destination) { throw 'Choose a new output directory.' }
        $package = Join-Path $destination 'AntigravityAccounts'
        New-Item -ItemType Directory -Path $package -Force | Out-Null
        Invoke-HubCommand $context.DotNet @('publish',(Join-Path $root 'src/Antigravity.Accounts/Antigravity.Accounts.csproj'),'-c','Release','-r','win-x64','--self-contained','true','--no-restore','-p:PublishSingleFile=true',('-p:Version=' + $version),'-o',(Join-Path $package 'component'))
        foreach ($name in @('account-plugin','hostauth-adapter')) {
            $source = Join-Path $root ('packages/' + $name)
            $target = Join-Path $package $name
            New-Item -ItemType Directory -Path $target | Out-Null
            $included = if ($name -eq 'account-plugin') {
                @('plugin.json','README.md','assets','window.cjs','native-oauth.mjs','manage-install.mjs')
            } else { @('src','scripts','README.md','package.json') }
            foreach ($item in $included) { Copy-Item -LiteralPath (Join-Path $source $item) -Destination $target -Recurse }
        }
        Copy-Item -LiteralPath (Join-Path $root 'docs/ACCOUNTS_PLUGIN.md') -Destination (Join-Path $package 'README.md')
        Copy-Item -LiteralPath (Join-Path $root 'docs/ACCOUNTS_VALIDATION.md') -Destination (Join-Path $package 'VALIDATION.md')
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
        $launch = @('@echo off','setlocal','set "ACCOUNTS_APP_ROOT=%ANTIGRAVITY_INSTALL_ROOT%"','if not defined ACCOUNTS_APP_ROOT set "ACCOUNTS_APP_ROOT=%LOCALAPPDATA%\Programs\antigravity"','start "" "%ACCOUNTS_APP_ROOT%\Antigravity.exe" --accounts')
        [IO.File]::WriteAllText((Join-Path $package 'Start-With-Accounts.cmd'), ($launch -join "`r`n") + "`r`n", [Text.Encoding]::ASCII)
        $manifest = [ordered]@{
            application='AntigravityAccounts';version=$version;runtime='win-x64';selfContained=$true
            singleFileCompressed=$true;debugSymbols=$false;antigravityVersion='2.28.2'
            nativeRuntimeValidated=$false;nativeSwitchLocalValidated=$false
            realAccountSwitchMetadataValidated=$false;realQuotaValidated=$false
            previousVersionValidation=@{
                version='0.4.0-preview.5';nativeSwitchLocalValidated=$true
                realAccountSwitchMetadataValidated=$true;realQuotaValidated=$false
                scope='Previous version results only; the current package requires its own native runtime and account checks.'
            }
            accountSelection='native-runtime-with-project-refresh';unconfiguredStartup='registration-only'
            officialCredentialsImported=$false;files=@()
            interface='native-model-menu-account-groups';separateAccountWindow=$false
            marketplaceListed=$false;standardPluginOnly=$false
        }
        if (Get-ChildItem -LiteralPath $package -File -Recurse | Where-Object Extension -In @('.asar','.dpapi','.vscdb','.db')) {
            throw 'The distributable cannot contain prepared official archives or account data.'
        }
        $manifest.files = @(Get-ChildItem -LiteralPath $package -File -Recurse | ForEach-Object {
            @{path=[IO.Path]::GetRelativePath($package,$_.FullName).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
        })
        $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $package 'package-manifest.json') -Encoding utf8
        $zip = Join-Path $destination ('AntigravityAccounts-' + $version + '-win-x64.zip')
        Compress-Archive -LiteralPath $package -DestinationPath $zip
        @{file=[IO.Path]::GetFileName($zip);sha256=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash} | ConvertTo-Json | Set-Content -LiteralPath ($zip + '.sha256.json') -Encoding utf8
        Write-Output ('Account plugin package: ' + $zip)
    }
}
finally { $env:AGPH_DOTNET = $originalSdk }
