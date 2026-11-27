# CI smoke test of the installer (AGENT-RUN 6.): install silently → service runs → bf status → uninstall → service gone, target untouched
param([string]$Setup, [string]$OldSetup)
$ErrorActionPreference = 'Stop'
$target = Join-Path $env:RUNNER_TEMP 'bf-smoke-target'
New-Item -ItemType Directory -Force $target | Out-Null
Set-Content (Join-Path $target 'keep.txt') 'backup data'
$cfg = Join-Path $env:RUNNER_TEMP 'bf-smoke.json'
$src = Join-Path $env:RUNNER_TEMP 'bf-smoke-src'
New-Item -ItemType Directory -Force $src | Out-Null
Set-Content (Join-Path $src 'a.txt') 'hello'
$config = [ordered]@{
  schema = 1; instance_name = 'SMOKE'; language = 'en'
  targets = @(@{ id = 't'; type = 'local'; path = $target })
  jobs = @(@{ id = 'docs'; name = 'Docs'; type = 'files'; schedule = @{ kind = 'manual' }
              sources = @(@{ path = $src }); target = @{ target_id = 't'; subdir = 'Docs' }; files = @{ vss = 'never' } })
}
[IO.File]::WriteAllText($cfg, ($config | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding $false))
$secrets = Join-Path $env:RUNNER_TEMP 'bf-smoke.secrets.json'
[IO.File]::WriteAllText($secrets, '{"smtp":"smoke-test-password-1"}', (New-Object Text.UTF8Encoding $false))

function Wait-Running {
  $svc = Get-Service BackupForge
  for ($i = 0; $i -lt 60 -and $svc.Status -ne 'Running'; $i++) { Start-Sleep 1; $svc.Refresh() }
  if ($svc.Status -ne 'Running') { throw "service not running: $($svc.Status)" }
}
$install = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',"/CONFIG=`"$cfg`"","/SECRETS=`"$secrets`"",'/LOG=setup-install.log')
if ($OldSetup) {
  # CR-002 A8: first the old single-file layout, then the new shared-folder installer over it (upgrade, no /CONFIG)
  Write-Host "installing the old layout $OldSetup"
  $p = Start-Process -FilePath $OldSetup -ArgumentList $install -Wait -PassThru
  if ($p.ExitCode -ne 0) { throw "old setup exit $($p.ExitCode)" }
  Wait-Running
  $install = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/LOG=setup-upgrade.log')
}
Write-Host "installing $Setup"
$p = Start-Process -FilePath $Setup -ArgumentList $install -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "setup exit $($p.ExitCode)" }
Wait-Running
if ($OldSetup -and -not (Test-Path (Join-Path ${env:ProgramFiles} 'BackupForge\BackupForge.Service.dll'))) { throw 'upgrade did not install the shared-folder layout' }
$sid = (& sc.exe qsidtype BackupForge) -join ' '
if ($sid -notmatch 'UNRESTRICTED') { throw "service SID type: $sid" }
# CR-002 A6: secrets.dat is SYSTEM only (no installing administrator)
$secretsDat = Join-Path $env:ProgramData 'BackupForge\secrets.dat'
if (-not (Test-Path $secretsDat)) { throw 'secrets.dat missing' }
$acl = Get-Acl $secretsDat
& icacls.exe $secretsDat
if (-not $acl.AreAccessRulesProtected) { throw 'secrets.dat inherits permissions' }
$others = @($acl.Access | Where-Object { $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -ne 'S-1-5-18' })
if ($others.Count -gt 0) { throw "secrets.dat ACL is not SYSTEM only: $(($others | ForEach-Object { $_.IdentityReference.Value }) -join ', ')" }
$bf = Join-Path ${env:ProgramFiles} 'BackupForge\bf.exe'
for ($i = 0; $i -lt 30; $i++) { & $bf status *> $null; if ($LASTEXITCODE -eq 0) { break }; Start-Sleep 1 }
& $bf status
if ($LASTEXITCODE -ne 0) { throw "bf status exit $LASTEXITCODE" }
& $bf run docs --wait
if ($LASTEXITCODE -ne 0) { throw "bf run exit $LASTEXITCODE" }
if (-not (Test-Path (Join-Path $target 'Docs\a.txt'))) { throw 'backup file missing' }
$status = Join-Path $env:ProgramData 'BackupForge\status\status.json'
for ($i = 0; $i -lt 30 -and -not (Test-Path $status); $i++) { Start-Sleep 1 }
if (-not (Test-Path $status)) { throw 'status.json missing' }

Write-Host 'uninstalling'
$un = Join-Path ${env:ProgramFiles} 'BackupForge\unins000.exe'
$p = Start-Process -FilePath $un -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "uninstall exit $($p.ExitCode)" }
Start-Sleep 3
if (Get-Service BackupForge -ErrorAction SilentlyContinue) { throw 'service still installed' }
if (-not (Test-Path (Join-Path $target 'keep.txt'))) { throw 'backup target was touched' }
if (-not (Test-Path (Join-Path $target 'Docs\a.txt'))) { throw 'backup target was touched' }
if (-not (Test-Path (Join-Path $env:ProgramData 'BackupForge\config.json'))) { throw 'settings were not kept (no /PURGE)' }
Write-Host 'smoke test ok'
