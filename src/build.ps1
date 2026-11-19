[CmdletBinding()]
param([string] $Destination = (Join-Path $PSScriptRoot '..\outputs\记下.exe'))

$ErrorActionPreference = 'Stop'
$Destination = [IO.Path]::GetFullPath($Destination)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Destination)) | Out-Null
$frameworkDir = [System.Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()
if (-not (Test-Path (Join-Path $frameworkDir 'csc.exe'))) {
    $frameworkDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
}
$frameworkCompiler = Join-Path $frameworkDir 'csc.exe'
$compilerArgs = @('/nologo','/target:winexe','/optimize+','/platform:anycpu','/langversion:5','/codepage:65001','/utf8output',"/out:$Destination",
    "/win32manifest:$PSScriptRoot\SidePage.manifest", "/win32icon:$PSScriptRoot\SidePage.ico",
    "/resource:$PSScriptRoot\SidePage.ico,SidePage.ico", "/resource:$PSScriptRoot\Panel.xaml,SidePage.Panel.xaml")
foreach ($name in @('System.dll','System.Core.dll','System.Xml.dll','System.Xaml.dll','System.Drawing.dll','System.Windows.Forms.dll')) {
    $compilerArgs += '/reference:' + (Join-Path $frameworkDir $name)
}
foreach ($name in @('WindowsBase','PresentationCore','PresentationFramework','UIAutomationTypes','UIAutomationProvider')) {
    $compilerArgs += '/reference:' + (Join-Path $frameworkDir "WPF\$name.dll")
}
$compilerArgs += @('Main.cs','Host.cs','TrayMenu.cs','Tasks.cs','DiscardMotion.cs','Thoughts.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $frameworkCompiler @compilerArgs
if ($LASTEXITCODE -ne 0) { throw '编译失败' }
