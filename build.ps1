$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $root "Program.cs"
$outDir = Join-Path $root "bin\Release"
$outFile = Join-Path $outDir "EC20电话短信工具.exe"
$compiler = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

& $compiler `
  /target:winexe `
  /platform:x64 `
  /optimize+ `
  /reference:System.dll `
  /reference:System.Drawing.dll `
  /reference:System.Windows.Forms.dll `
  /reference:System.Management.dll `
  /out:$outFile `
  $source

Write-Host "已生成：$outFile"
