param(
    [string]$Installation = "$env:LOCALAPPDATA\Programs\antigravity",
    [string]$EvidenceRoot = "$PSScriptRoot\..\artifacts\desktop-endpoint"
)
$ErrorActionPreference = 'Stop'
$server = [IO.Path]::GetFullPath((Join-Path $Installation 'resources\bin\language_server.exe'))
if ((Get-FileHash -LiteralPath $server -Algorithm SHA256).Hash -ne '424ED4EF6FC9CB470CE785D8F4B392DE3FA94FD16D030E6FF83E043440659F94') { throw 'Unverified LS binary' }
$scratch = [IO.Path]::GetFullPath((Join-Path $EvidenceRoot ([Guid]::NewGuid().ToString('N'))))
$null = New-Item -ItemType Directory -Path $scratch
foreach ($name in @('home','local','roaming','temp','gemini')) { $null = New-Item -ItemType Directory -Path (Join-Path $scratch $name) }
$httpSocket = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
$httpsSocket = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
$httpSocket.Start(); $httpsSocket.Start()
$httpPort = $httpSocket.LocalEndpoint.Port; $httpsPort = $httpsSocket.LocalEndpoint.Port
$httpSocket.Stop(); $httpsSocket.Stop()
$psi = [Diagnostics.ProcessStartInfo]::new($server)
$psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
$psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.RedirectStandardInput = $true
$psi.WorkingDirectory = $scratch
foreach ($key in @('NODE_OPTIONS','ELECTRON_RUN_AS_NODE','DEV_URL','JETSKI_LS_PORT','ANTIGRAVITY_LS_ADDRESS','ANTIGRAVITY_CSRF_TOKEN')) { $null = $psi.Environment.Remove($key) }
$psi.Environment['USERPROFILE'] = Join-Path $scratch 'home'; $psi.Environment['HOME'] = $psi.Environment['USERPROFILE']
$psi.Environment['APPDATA'] = Join-Path $scratch 'roaming'; $psi.Environment['LOCALAPPDATA'] = Join-Path $scratch 'local'
$psi.Environment['TEMP'] = Join-Path $scratch 'temp'; $psi.Environment['TMP'] = $psi.Environment['TEMP']
foreach ($arg in @('--standalone','--headless','--use_mocked_data','--disable_telemetry','--override_ide_name','antigravity','--subclient_type','hub','--app_data_dir','antigravity','--gemini_dir',(Join-Path $scratch 'gemini'),'--http_server_port',"$httpPort",'--https_server_port',"$httpsPort",'--csrf_token',([Guid]::NewGuid().ToString('N')))) { $psi.ArgumentList.Add($arg) }
$process = [Diagnostics.Process]::new(); $process.StartInfo = $psi
$handler = [Net.Http.HttpClientHandler]::new()
$handler.ServerCertificateCustomValidationCallback = [Net.Http.HttpClientHandler]::DangerousAcceptAnyServerCertificateValidator
$client = [Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds(3)
try {
    if (-not $process.Start()) { throw 'LS start failed' }
    $process.StandardInput.Close()
    $stdout = $process.StandardOutput.BaseStream.CopyToAsync([IO.Stream]::Null)
    $stderr = $process.StandardError.BaseStream.CopyToAsync([IO.Stream]::Null)
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    $listeners = @()
    do {
        if ($process.HasExited) { throw 'LS exited before listeners' }
        $listeners = @(Get-NetTCPConnection -OwningProcess $process.Id -State Listen -ErrorAction SilentlyContinue)
        if ($listeners.LocalPort -contains $httpPort -and $listeners.LocalPort -contains $httpsPort) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    $rows = foreach ($endpoint in @(@{Scheme='http'; Port=$httpPort}, @{Scheme='https'; Port=$httpsPort})) {
        if (-not ($listeners | Where-Object { $_.LocalPort -eq $endpoint.Port -and $_.LocalAddress -eq '127.0.0.1' })) { throw 'Listener owner not established' }
        $response = $client.GetAsync("$($endpoint.Scheme)://127.0.0.1:$($endpoint.Port)/").GetAwaiter().GetResult()
        try {
            $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            [pscustomobject]@{ scheme=$endpoint.Scheme; owned=$true; status=[int]$response.StatusCode; html=($body -match '<html'); script=($body -match '<script'); bytes=$body.Length }
        } finally { $response.Dispose() }
    }
    $rows | ConvertTo-Json
} finally {
    try { if ($process.Id -and -not $process.HasExited) { $process.Kill($true); if (-not $process.WaitForExit(5000)) { throw 'LS cleanup timeout' } } }
    finally { $process.Dispose(); $client.Dispose() }
}
