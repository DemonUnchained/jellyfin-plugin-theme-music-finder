[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [Parameter(Mandatory = $true)][string]$JellyfinDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$server = (Resolve-Path -LiteralPath $JellyfinDirectory).Path
$executable = Join-Path $server 'jellyfin.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw 'Expected an extracted official Windows portable Jellyfin directory containing jellyfin.exe.'
}
$meta = Get-Content -LiteralPath (Join-Path $repo 'src/Jellyfin.Plugin.ThemeMusicFinder/meta.json') -Raw | ConvertFrom-Json
$expectedServerVersion = $meta.targetAbi -replace '\.0\.0$', ''
$run = Join-Path $repo ('artifacts/jellyfin-runtime/' + [Guid]::NewGuid().ToString('N'))
$data = Join-Path $run 'data'
$config = Join-Path $run 'config'
$cache = Join-Path $run 'cache'
$logs = Join-Path $run 'logs'
$plugin = Join-Path $data ('plugins/Theme Music Finder_' + $meta.version)
foreach ($directory in @($config, $cache, $logs, $plugin)) {
    [IO.Directory]::CreateDirectory($directory) | Out-Null
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory($package, $plugin)
$installedMeta = Get-Content -LiteralPath (Join-Path $plugin 'meta.json') -Raw | ConvertFrom-Json
if ($installedMeta.version -ne $meta.version -or $installedMeta.targetAbi -ne $meta.targetAbi) {
    throw 'The package version and ABI do not match the current source.'
}
foreach ($dll in @('Jellyfin.Plugin.ThemeMusicFinder.dll', 'YoutubeExplode.dll')) {
    if ($installedMeta.assemblies -notcontains $dll -or -not (Test-Path -LiteralPath (Join-Path $plugin $dll))) {
        throw "Missing runtime assembly or declaration: $dll"
    }
}

# Reserve an unused loopback port. This test never reads a real Jellyfin configuration
# or mounts a media library. Network discovery/remote access are disabled in this instance.
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()
@"
<?xml version="1.0" encoding="utf-8"?>
<NetworkConfiguration>
  <InternalHttpPort>$port</InternalHttpPort><PublicHttpPort>$port</PublicHttpPort>
  <EnableHttps>false</EnableHttps><EnableIPv4>true</EnableIPv4><EnableIPv6>false</EnableIPv6>
  <AutoDiscovery>false</AutoDiscovery><EnableRemoteAccess>false</EnableRemoteAccess>
  <LocalNetworkAddresses><string>127.0.0.1</string></LocalNetworkAddresses>
</NetworkConfiguration>
"@ | Set-Content -LiteralPath (Join-Path $config 'network.xml') -Encoding UTF8
$baseUri = "http://127.0.0.1:$port"
$observedVersion = $null

for ($startNumber = 1; $startNumber -le 2; $startNumber++) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $executable
    $start.WorkingDirectory = $server
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($key in @($start.Environment.Keys | Where-Object { $_ -like 'JELLYFIN_*' })) {
        $start.Environment.Remove($key) | Out-Null
    }
    foreach ($argument in @('--datadir', $data, '--configdir', $config, '--cachedir', $cache,
        '--logdir', $logs, '--nowebclient', '--nonetchange', '--ffmpeg', (Join-Path $server 'ffmpeg.exe'))) {
        $start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $stdout = $null
    $stderr = $null
    $started = $false
    try {
        $started = $process.Start()
        if (-not $started) { throw 'Isolated Jellyfin failed to start.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $deadline = [DateTime]::UtcNow.AddSeconds(90)
        $ready = $false
        while ([DateTime]::UtcNow -lt $deadline) {
            if ($process.HasExited) { throw "Isolated Jellyfin exited with code $($process.ExitCode). See $run" }
            try {
                $info = Invoke-RestMethod -Uri "$baseUri/System/Info/Public" -TimeoutSec 2
                $observedVersion = $info.Version
                $actualVersion = [Version]$observedVersion
                $expectedVersion = [Version]$expectedServerVersion
                if ($actualVersion.Major -ne $expectedVersion.Major -or $actualVersion.Minor -ne $expectedVersion.Minor) {
                    throw "Wrong server version: $observedVersion; expected $expectedServerVersion"
                }
                $health = Invoke-WebRequest -Uri "$baseUri/health" -TimeoutSec 2
                if ($health.StatusCode -eq 200) { $ready = $true; break }
            } catch [System.Net.Http.HttpRequestException] { }
              catch [System.Threading.Tasks.TaskCanceledException] { }
            Start-Sleep -Milliseconds 250
        }
        if (-not $ready) { throw "Isolated Jellyfin did not become healthy. See $run" }
    } finally {
        # Stop only the process this script created. The second start reuses its data,
        # exercising plugin persistence and recovery after abrupt process termination.
        if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $output = if ($stdout) { $stdout.GetAwaiter().GetResult() } else { '' }
        $errors = if ($stderr) { $stderr.GetAwaiter().GetResult() } else { '' }
        [IO.File]::WriteAllText((Join-Path $run "start-$startNumber.stdout.log"), $output)
        [IO.File]::WriteAllText((Join-Path $run "start-$startNumber.stderr.log"), $errors)
        $process.Dispose()
    }
    if ($output -notmatch 'Loaded plugin[^\r\n]*Theme Music Finder') {
        throw "Plugin loading was not confirmed on start $startNumber. See $run"
    }
    if ($output -match 'Could not load[^\r\n]*ThemeMusicFinder|Failed to load[^\r\n]*ThemeMusicFinder') {
        throw "Plugin loader reported a failure. See $run"
    }
    foreach ($dll in @('Jellyfin.Plugin.ThemeMusicFinder.dll', 'YoutubeExplode.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $plugin $dll))) { throw "Jellyfin removed $dll" }
    }
    Write-Host "PASS isolated Jellyfin $observedVersion start $startNumber and plugin persistence"
}
$result = [ordered]@{
    status = 'PASS'; pluginVersion = $meta.version; targetAbi = $meta.targetAbi
    serverVersion = $observedVersion; starts = 2; packageSha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
    restartMethod = 'Terminate only the isolated child process, then relaunch with the same data.'
    note = 'Official Windows portable runtime; no real library, live server, Linux container or audio-correctness validation.'
}
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'validation.json') -Encoding UTF8
$result | ConvertTo-Json | Write-Host
Write-Host "Evidence: $run"
