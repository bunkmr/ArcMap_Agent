$ErrorActionPreference = 'Stop'
$projDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

$binDir = Join-Path $projDir 'bin\Release\net48'
if (-not (Test-Path $binDir)) { $binDir = Join-Path $projDir 'bin\Debug\net48' }
$dll = Join-Path $binDir 'ArcMapAgent.dll'
if (-not (Test-Path $dll)) { Write-Error "找不到 ArcMapAgent.dll，请先构建（dotnet build -c Release）"; exit 1 }

# 官方 .esriaddin 布局：Config.xml + Images/ + Install/ArcMapAgent.dll
$stage = Join-Path $projDir '_stage'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $stage 'Install') | Out-Null

Copy-Item $dll (Join-Path $stage 'Install')
Copy-Item (Join-Path $projDir 'Config.xml') $stage
Copy-Item (Join-Path $projDir 'Images') $stage -Recurse

$out = Join-Path $projDir 'ArcMapAgent.esriaddin'
if (Test-Path $out) { Remove-Item $out -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $out -Force

Write-Host "已生成安装包: $out"
