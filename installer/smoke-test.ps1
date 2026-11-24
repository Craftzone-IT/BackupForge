# CI smoke test of the installer (AGENT-RUN 6.): install silently → service runs → bf status → uninstall → service gone, target untouched
param([string]$Setup)
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

Write-Host "installing $Setup"
$p = Start-Process -FilePath $Setup -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',"/CONFIG=`"$cfg`"",'/LOG=setup-install.log' -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "setup exit $($p.ExitCode)" }
$svc = Get-Service BackupForge
for ($i = 0; $i -lt 60 -and $svc.Status -ne 'Running'; $i++) { Start-Sleep 1; $svc.Refresh() }
if ($svc.Status -ne 'Running') { throw "service not running: $($svc.Status)" }
$sid = (& sc.exe qsidtype BackupForge) -join ' '
if ($sid -notmatch 'UNRESTRICTED') { throw "service SID type: $sid" }
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
