# Publishes the service, bf and the GUI for the installer (CR-002 A8.).
#   default : ONE self-contained folder <Out>\app (the .NET runtime once — the installer shrinks to about half)
#   -SingleFile: the old layout (three self-contained single-file exes in <Out>\service|cli|gui), only to test upgrades
param([string]$Out = 'artifacts/publish', [string]$Version = '', [switch]$SingleFile)
$ErrorActionPreference = 'Stop'
$common = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:DebugType=none')
if ($Version) { $common += "-p:Version=$Version" }
if ($SingleFile) {
  $common += @('-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true')
  foreach ($p in @(@('Service', 'service'), @('Cli', 'cli'), @('Gui', 'gui'))) {
    dotnet publish "src/BackupForge.$($p[0])" @common -o (Join-Path $Out $p[1])
    if ($LASTEXITCODE -ne 0) { throw "publish $($p[0]) failed" }
  }
  return
}
$app = Join-Path $Out 'app'
# the GUI last: its WPF runtime pack carries the full WindowsBase / EventLog / … that the console apps only have as facades
foreach ($p in 'Service', 'Cli', 'Gui') {
  dotnet publish "src/BackupForge.$p" @common -o $app
  if ($LASTEXITCODE -ne 0) { throw "publish $p failed" }
}
# every app's deps.json must be satisfied by the files that ended up in the shared folder
$bad = @()
foreach ($deps in Get-ChildItem $app -Filter '*.deps.json') {
  $j = Get-Content $deps.FullName -Raw | ConvertFrom-Json
  $target = $j.targets.PSObject.Properties | Select-Object -Last 1
  foreach ($lib in $target.Value.PSObject.Properties) {
    $rt = $lib.Value.runtime
    if (-not $rt) { continue }
    foreach ($asset in $rt.PSObject.Properties) {
      $file = Join-Path $app (Split-Path $asset.Name -Leaf)
      $want = $asset.Value.assemblyVersion
      if (-not (Test-Path $file)) { $bad += "$($deps.Name): missing $($asset.Name)"; continue }
      if (-not $want) { continue }
      try { $have = [Reflection.AssemblyName]::GetAssemblyName($file).Version } catch { continue }
      if ($have -lt [version]$want) { $bad += "$($deps.Name): $(Split-Path $file -Leaf) is $have, needs $want" }
    }
  }
}
if ($bad.Count -gt 0) { $bad | ForEach-Object { Write-Host $_ }; throw "shared publish folder is inconsistent ($($bad.Count))" }
foreach ($exe in 'BackupForge.Service', 'bf', 'BackupForge') {
  foreach ($ext in '.exe', '.dll', '.deps.json', '.runtimeconfig.json') {
    if (-not (Test-Path (Join-Path $app "$exe$ext"))) { throw "missing $exe$ext" }
  }
}
$mb = [math]::Round(((Get-ChildItem $app -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "shared publish folder: $mb MB, $((Get-ChildItem $app -Recurse -File).Count) files"
