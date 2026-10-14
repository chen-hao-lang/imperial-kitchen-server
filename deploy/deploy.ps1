# deploy.ps1 - One-shot deploy of KitchenChaos server to the Tencent Cloud Ubuntu host.
#
# Usage (run in PowerShell, from this deploy folder):
#     .\deploy.ps1
#     .\deploy.ps1 -Server 106.52.92.221 -User ubuntu
#     .\deploy.ps1 -Key C:\Users\CHF\.ssh\id_ed25519
#
# If execution is blocked, run once:
#     Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass

[CmdletBinding()]
param(
    [string]$Server   = '106.52.92.221',
    [string]$User     = 'ubuntu',
    [string]$Key      = '',
    [int]   $GamePort = 7777
)

$ErrorActionPreference = 'Stop'

$RepoRoot   = Split-Path -Parent $PSScriptRoot
$BinaryPath = Join-Path $RepoRoot 'publish\linux-x64\KitchenChaos-Server'
$UnitPath   = Join-Path $PSScriptRoot 'kitchenchaos-server.service'
$Remote     = "${User}@${Server}"

Write-Host "=== KitchenChaos server deploy ===" -ForegroundColor Cyan
Write-Host "target : $Remote"
Write-Host "binary : $BinaryPath"

if (-not (Get-Command scp -ErrorAction SilentlyContinue)) {
    throw 'scp not found. Enable Windows feature "OpenSSH Client".'
}
if (-not (Get-Command ssh -ErrorAction SilentlyContinue)) {
    throw 'ssh not found. Enable Windows feature "OpenSSH Client".'
}
if (-not (Test-Path $BinaryPath)) {
    throw "Publish output not found: $BinaryPath  (run the dotnet publish step first)"
}
if (-not (Test-Path $UnitPath)) {
    throw "systemd unit not found: $UnitPath"
}

$sizeMB = [math]::Round((Get-Item $BinaryPath).Length / 1MB, 1)

# Build the -i arguments only when a key path was explicitly given.
$scpArgs = @()
$sshArgs = @('-t')
if ($Key) {
    $scpArgs += @('-i', $Key)
    $sshArgs += @('-i', $Key)
}

# ---------- Step 1: upload ----------
Write-Host ""
Write-Host "[1/3] Uploading binary ($sizeMB MB) ..." -ForegroundColor Yellow
& scp @scpArgs $BinaryPath "${Remote}:/tmp/KitchenChaos-Server"
if ($LASTEXITCODE -ne 0) { throw 'Upload of the binary failed.' }

Write-Host "[2/3] Uploading systemd unit ..." -ForegroundColor Yellow
& scp @scpArgs $UnitPath "${Remote}:/tmp/kitchenchaos-server.service"
if ($LASTEXITCODE -ne 0) { throw 'Upload of the unit file failed.' }

# ---------- Step 2: remote install ----------
$remoteScript = @"
sudo mkdir -p /opt/kitchenchaos-server/.bundle
sudo mv -f /tmp/KitchenChaos-Server /opt/kitchenchaos-server/KitchenChaos-Server
sudo chmod +x /opt/kitchenchaos-server/KitchenChaos-Server
sudo chown -R ${User}:${User} /opt/kitchenchaos-server
sudo mv -f /tmp/kitchenchaos-server.service /etc/systemd/system/kitchenchaos-server.service
sudo chmod 644 /etc/systemd/system/kitchenchaos-server.service
sudo systemctl daemon-reload
sudo systemctl enable kitchenchaos-server
sudo systemctl restart kitchenchaos-server
sleep 2
echo "----- is-active -----"
systemctl is-active kitchenchaos-server || true
echo "----- recent logs -----"
sudo journalctl -u kitchenchaos-server -n 10 --no-pager
echo "----- listening port -----"
ss -tln | grep -w ${GamePort} || echo "WARN: nothing listening on port ${GamePort}"
"@

Write-Host "[3/3] Installing and starting the service on the server ..." -ForegroundColor Yellow
& ssh @sshArgs $Remote $remoteScript
if ($LASTEXITCODE -ne 0) { throw 'Remote install step failed - see the output above.' }

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "Follow logs with:"
Write-Host "    ssh ${Remote} `"journalctl -u kitchenchaos-server -f`""
