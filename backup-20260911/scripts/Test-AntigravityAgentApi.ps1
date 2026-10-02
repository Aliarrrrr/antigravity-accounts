#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$Executable = "$env:LOCALAPPDATA\Programs\antigravity\Antigravity.exe",
    [ValidateRange(5, 60)]
    [int]$TimeoutSeconds = 15
)

$ErrorActionPreference = 'Stop'
$executablePath = [IO.Path]::GetFullPath($Executable)
if ([IO.Path]::GetFileName($executablePath) -cne 'Antigravity.exe' -or
    -not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    throw 'Executable must be an existing Antigravity.exe.'
}

$languageServerPath = Join-Path (Split-Path $executablePath -Parent) 'resources\bin\language_server.exe'
if (-not (Test-Path -LiteralPath $languageServerPath -PathType Leaf)) {
    throw "language_server.exe not found beside the selected installation."
}

$repositoryRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$artifactParent = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\agentapi-contract'))
$nonce = [guid]::NewGuid().ToString('N')
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $artifactParent $nonce))
if (-not $artifactRoot.StartsWith($artifactParent + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Unsafe artifact path.'
}
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
$scratchRoot = [IO.Path]::GetFullPath((Join-Path $artifactRoot 'scratch'))
if (-not $scratchRoot.StartsWith($artifactRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Unsafe scratch path.'
}
$geminiRoot = Join-Path $scratchRoot 'gemini'
New-Item -ItemType Directory -Path $geminiRoot -Force | Out-Null
$reportPath = Join-Path $artifactRoot 'report.json'

function Get-DistinctLoopbackPorts {
    $listeners = [Collections.Generic.List[Net.Sockets.TcpListener]]::new()
    $ports = [ordered]@{}
    try {
        foreach ($role in @('http', 'https', 'lsp')) {
            $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
            $listener.Start()
            $listeners.Add($listener)
            $ports[$role] = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
        }
        return [pscustomobject]$ports
    }
    finally {
        foreach ($listener in $listeners) {
            $listener.Stop()
        }
    }
}

function Test-LoopbackListener([int]$Port) {
    $client = [Net.Sockets.TcpClient]::new()
    try {
        $connectTask = $client.ConnectAsync('127.0.0.1', $Port)
        if (-not $connectTask.Wait(250)) {
            return $false
        }
        return $client.Connected
    }
    catch {
        return $false
    }
    finally {
        $client.Dispose()
    }
}

function Stop-ProcessTree([Diagnostics.Process]$Process) {
    if ($null -eq $Process) {
        return $true
    }
    try {
        if (-not $Process.HasExited) {
            $Process.Kill($true)
        }
        return $Process.WaitForExit(15000)
    }
    catch {
        return $false
    }
}

function Get-AgentApiSummary([string]$StandardOutput, [string]$StandardError, [int]$ExitCode) {
    $capturedText = if (-not [string]::IsNullOrWhiteSpace($StandardOutput)) {
        $StandardOutput.Trim()
    }
    else {
        $StandardError.Trim()
    }
    $keys = @()
    $errorKind = $null
    $diagnosticCode = $null
    $signatures = [Collections.Generic.List[string]]::new()
    $isJson = $false
    if (-not [string]::IsNullOrWhiteSpace($capturedText)) {
        try {
            $value = $capturedText | ConvertFrom-Json -ErrorAction Stop
            $isJson = $true
            $keys = @($value.PSObject.Properties.Name | Sort-Object -Unique)
            $errorText = [string]$value.error
            if (-not [string]::IsNullOrWhiteSpace($errorText)) {
                $errorKind = if ($errorText -match '(?i)not found|does not exist|unknown conversation') {
                    'not-found'
                }
                elseif ($errorText -match '(?i)unauthori|csrf|forbidden') {
                    'authorization-rejected'
                }
                elseif ($errorText -match '(?i)connect|refused|unavailable|timeout|deadline') {
                    'connection-failed'
                }
                else {
                    'other-redacted-error'
                }
            }
        }
        catch {
            $keys = @()
        }
    }

    $diagnosticCode = if ($capturedText -match '(?i)connection refused') {
        'connection-refused'
    }
    elseif ($capturedText -match '(?i)x509|certificate') {
        'certificate-rejected'
    }
    elseif ($capturedText -match '(?i)tls|handshake') {
        'tls-failed'
    }
    elseif ($capturedText -match '(?i)deadline|timed out|timeout') {
        'deadline-exceeded'
    }
    elseif ($capturedText -match '(?i)produced zero addresses') {
        'resolver-zero-addresses'
    }
    elseif ($capturedText -match '(?i)server preface') {
        'server-preface-failed'
    }
    elseif ($capturedText -match '(?i)unexpected EOF|\bEOF\b') {
        'unexpected-eof'
    }
    elseif ($capturedText -match '(?i)unimplemented') {
        'grpc-unimplemented'
    }
    elseif ($capturedText -match '(?i)unauthori|forbidden|csrf') {
        'authorization-rejected'
    }
    elseif ($capturedText -match '(?i)404|not found') {
        'not-found'
    }
    elseif ($capturedText -match '(?i)unavailable') {
        'service-unavailable'
    }
    else {
        'unclassified'
    }

    foreach ($signature in ([ordered]@{
        'resolver-zero-addresses' = 'produced zero addresses'
        'dial-failed' = 'error while dialing|failed to dial'
        'server-preface-failed' = 'server preface'
        'unexpected-eof' = '\bEOF\b'
        'http-response-to-https' = 'HTTP response to HTTPS client'
        'grpc-unimplemented' = 'unimplemented'
        'service-starting' = 'not ready|starting'
    }).GetEnumerator()) {
        if ($capturedText -match $signature.Value) {
            $signatures.Add($signature.Key)
        }
    }

    return [ordered]@{
        exitCode = $ExitCode
        json = $isJson
        topLevelKeys = $keys
        errorKind = $errorKind
        diagnosticCode = $diagnosticCode
        signatures = @($signatures)
    }
}

function Invoke-AgentApiProbe {
    param(
        [Parameter(Mandatory)]
        [string]$LanguageServerPath,
        [Parameter(Mandatory)]
        [int]$Port,
        [Parameter(Mandatory)]
        [string]$Csrf,
        [Parameter(Mandatory)]
        [int]$ProbeTimeoutSeconds
    )

    $agentProcess = $null
    $standardOutputTask = $null
    $standardErrorTask = $null
    $summary = $null
    $cleanupSucceeded = $true
    $standardOutput = $null
    $standardError = $null
    try {
        $agentInfo = [Diagnostics.ProcessStartInfo]::new()
        $agentInfo.FileName = $LanguageServerPath
        $agentInfo.UseShellExecute = $false
        $agentInfo.CreateNoWindow = $true
        $agentInfo.RedirectStandardOutput = $true
        $agentInfo.RedirectStandardError = $true
        foreach ($argument in @('agentapi', 'get-conversation-metadata', '00000000-0000-0000-0000-000000000000')) {
            $agentInfo.ArgumentList.Add($argument)
        }
        $agentInfo.Environment['ANTIGRAVITY_LS_ADDRESS'] = "127.0.0.1:$Port"
        $agentInfo.Environment['ANTIGRAVITY_CSRF_TOKEN'] = $Csrf

        $agentProcess = [Diagnostics.Process]::new()
        $agentProcess.StartInfo = $agentInfo
        if (-not $agentProcess.Start()) {
            throw 'agentapi probe did not start.'
        }
        $standardOutputTask = $agentProcess.StandardOutput.ReadToEndAsync()
        $standardErrorTask = $agentProcess.StandardError.ReadToEndAsync()
        if (-not $agentProcess.WaitForExit($ProbeTimeoutSeconds * 1000)) {
            $summary = [ordered]@{
                exitCode = $null
                json = $false
                topLevelKeys = @()
                errorKind = 'timeout'
                diagnosticCode = 'probe-timeout'
                signatures = @('deadline-exceeded')
            }
        }
        else {
            $agentProcess.WaitForExit()
            $standardOutput = $standardOutputTask.GetAwaiter().GetResult()
            $standardError = $standardErrorTask.GetAwaiter().GetResult()
            $summary = Get-AgentApiSummary $standardOutput $standardError $agentProcess.ExitCode
        }
    }
    catch {
        $summary = [ordered]@{
            exitCode = $null
            json = $false
            topLevelKeys = @()
            errorKind = 'probe-failed'
            diagnosticCode = 'probe-exception'
            signatures = @()
        }
    }
    finally {
        $cleanupSucceeded = Stop-ProcessTree $agentProcess
        if ($null -ne $standardOutputTask) {
            try { $null = $standardOutputTask.GetAwaiter().GetResult() } catch {}
        }
        if ($null -ne $standardErrorTask) {
            try { $null = $standardErrorTask.GetAwaiter().GetResult() } catch {}
        }
        if ($null -ne $agentProcess) {
            $agentProcess.Dispose()
        }
        $standardOutput = $null
        $standardError = $null
    }

    return [ordered]@{
        summary = $summary
        cleanupSucceeded = $cleanupSucceeded
    }
}

$ports = Get-DistinctLoopbackPorts
$endpointDefinitions = @(
    [pscustomobject]([ordered]@{
        role = 'http'
        port = [int]$ports.http
        listenerTransportExpectation = 'plaintext-http-or-h2c'
    })
    [pscustomobject]([ordered]@{
        role = 'https'
        port = [int]$ports.https
        listenerTransportExpectation = 'tls-https-ui'
    })
    [pscustomobject]([ordered]@{
        role = 'lsp'
        port = [int]$ports.lsp
        listenerTransportExpectation = 'lsp-framing-or-unknown'
    })
)
$csrf = [guid]::NewGuid().ToString('N')
$languageServer = $null
$languageServerOutputTask = $null
$languageServerErrorTask = $null
$languageServerCleanupSucceeded = $true
$scratchCleanupSucceeded = $false
$endpointResults = [Collections.Generic.List[object]]::new()
$listenerObserved = [ordered]@{ http = $false; https = $false; lsp = $false }
$result = 'failed'
$failureType = $null
$languageServerExitedBeforeProbes = $false

try {
    $version = (Get-Item -LiteralPath $executablePath).VersionInfo.ProductVersion
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $languageServerPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in @(
        '--standalone',
        '--headless',
        '--override_ide_name', 'antigravity',
        '--subclient_type', 'hub',
        '--override_ide_version', $version,
        '--override_user_agent_name', 'antigravity',
        '--http_server_port', ([string]$ports.http),
        '--https_server_port', ([string]$ports.https),
        '--lsp_port', ([string]$ports.lsp),
        '--csrf_token', $csrf,
        '--gemini_dir', $geminiRoot,
        '--app_data_dir', 'antigravity',
        '--disable_telemetry',
        '--use_mocked_data'
    )) {
        $startInfo.ArgumentList.Add($argument)
    }

    $languageServer = [Diagnostics.Process]::new()
    $languageServer.StartInfo = $startInfo
    if (-not $languageServer.Start()) {
        throw 'language_server.exe did not start.'
    }
    $languageServer.StandardInput.Close()
    $languageServerOutputTask = $languageServer.StandardOutput.ReadToEndAsync()
    $languageServerErrorTask = $languageServer.StandardError.ReadToEndAsync()

    $startupDeadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $startupDeadline -and -not $languageServer.HasExited) {
        foreach ($endpoint in $endpointDefinitions) {
            if (-not $listenerObserved[$endpoint.role]) {
                $listenerObserved[$endpoint.role] = Test-LoopbackListener $endpoint.port
            }
        }
        if (@($listenerObserved.Values | Where-Object { -not $_ }).Count -eq 0) {
            break
        }
        Start-Sleep -Milliseconds 50
    }
    $languageServerExitedBeforeProbes = $languageServer.HasExited

    foreach ($endpoint in $endpointDefinitions) {
        $probeArguments = @{
            LanguageServerPath = $languageServerPath
            Port = $endpoint.port
            Csrf = $csrf
            ProbeTimeoutSeconds = $TimeoutSeconds
        }
        $probe = Invoke-AgentApiProbe @probeArguments
        $endpointResults.Add([ordered]@{
            role = $endpoint.role
            connectDestination = [ordered]@{
                host = '127.0.0.1'
                port = $endpoint.port
                addressFormat = 'bare-host-port'
                source = 'probe-input'
            }
            agentApiTransportExpectation = 'plaintext-http2-grpc'
            listenerTransportExpectation = $endpoint.listenerTransportExpectation
            listenerObservedBeforeProbe = [bool]$listenerObserved[$endpoint.role]
            outcome = $probe.summary
            processCleanupSucceeded = $probe.cleanupSucceeded
        })
    }

    $contractRoles = @(
        $endpointResults |
            Where-Object { $_.outcome.errorKind -eq 'not-found' } |
            ForEach-Object { $_.role }
    )
    $result = if ($contractRoles.Count -gt 0) {
        'contract-observed'
    }
    else {
        'matrix-complete-no-contract'
    }
}
catch {
    $failureType = $_.Exception.GetType().FullName
}
finally {
    $languageServerCleanupSucceeded = Stop-ProcessTree $languageServer
    if ($null -ne $languageServerOutputTask) {
        try { $null = $languageServerOutputTask.GetAwaiter().GetResult() } catch {}
    }
    if ($null -ne $languageServerErrorTask) {
        try { $null = $languageServerErrorTask.GetAwaiter().GetResult() } catch {}
    }
    if ($null -ne $languageServer) {
        $languageServer.Dispose()
    }
    if (Test-Path -LiteralPath $scratchRoot) {
        Remove-Item -LiteralPath $scratchRoot -Recurse -Force
    }
    $scratchCleanupSucceeded = -not (Test-Path -LiteralPath $scratchRoot)
    if (-not $languageServerCleanupSucceeded -or
        -not $scratchCleanupSucceeded -or
        @($endpointResults | Where-Object { -not $_.processCleanupSucceeded }).Count -gt 0) {
        $failureType = 'OwnedProcessOrScratchCleanupFailure'
        $result = 'failed'
    }
    $csrf = $null
}

$contractEndpointRoles = @(
    $endpointResults |
        Where-Object { $_.outcome.errorKind -eq 'not-found' } |
        ForEach-Object { $_.role }
)
$distinctPortCount = @(@($ports.http, $ports.https, $ports.lsp) | Sort-Object -Unique).Count
$report = [ordered]@{
    schemaVersion = 2
    capturedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    result = $result
    mode = 'mocked-data-no-authentication'
    executable = [ordered]@{
        antigravityVersion = (Get-Item -LiteralPath $executablePath).VersionInfo.ProductVersion
        languageServerSha256 = (Get-FileHash -LiteralPath $languageServerPath -Algorithm SHA256).Hash
    }
    launchContract = [ordered]@{
        standalone = $true
        headless = $true
        subclientType = 'hub'
        loopbackPorts = [ordered]@{
            http = [int]$ports.http
            https = [int]$ports.https
            lsp = [int]$ports.lsp
            allDistinct = $distinctPortCount -eq 3
        }
        csrf = '<ephemeral-memory-only>'
        geminiDir = '<deleted-transient-scratch>\gemini'
        appDataDir = 'antigravity'
        telemetryDisabled = $true
        mockedData = $true
    }
    observed = [ordered]@{
        languageServerExitedBeforeProbes = $languageServerExitedBeforeProbes
        endpoints = @($endpointResults)
        contractEndpointRoles = $contractEndpointRoles
    }
    cleanup = [ordered]@{
        languageServerProcessTree = $languageServerCleanupSucceeded
        agentApiProcessTrees = @($endpointResults | ForEach-Object { $_.processCleanupSucceeded })
        transientScratchDeleted = $scratchCleanupSucceeded
    }
    sensitiveData = 'No raw vendor output, credential, token, OAuth query value, conversation content, or transient vendor file was persisted.'
    artifact = $artifactRoot
    failureType = $failureType
}
$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $reportPath -Encoding utf8
$report | ConvertTo-Json -Depth 10
if ($result -eq 'failed') {
    exit 1
}
