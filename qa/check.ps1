[CmdletBinding()]
param([switch] $BuildDock)
$ErrorActionPreference = 'Stop'
$frameworkDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$sourceDir = Join-Path $PSScriptRoot '..\src'
$checkName = if ($BuildDock) { 'DockCheck' } else { 'PanelCheck' }
$compilerArgs = @('/nologo','/target:exe','/platform:anycpu','/langversion:5','/codepage:65001','/utf8output',"/main:SidePage.$checkName", "/out:$PSScriptRoot\$checkName.exe", "/win32manifest:$sourceDir\SidePage.manifest")
foreach ($name in @('System.dll','System.Core.dll','System.Xml.dll','System.Xaml.dll','System.Drawing.dll','System.Windows.Forms.dll')) {
    $compilerArgs += '/reference:' + (Join-Path $frameworkDir $name)
}
foreach ($name in @('WindowsBase','PresentationCore','PresentationFramework','UIAutomationTypes','UIAutomationProvider')) {
    $compilerArgs += '/reference:' + (Join-Path $frameworkDir "WPF\$name.dll")
}
$compilerArgs += @("$sourceDir\Tasks.cs", "$sourceDir\DiscardMotion.cs", "$sourceDir\Thoughts.cs", "$PSScriptRoot\$checkName.cs")
if ($BuildDock) { $compilerArgs += @("$sourceDir\Host.cs", "$sourceDir\TrayMenu.cs") }
& (Join-Path $frameworkDir 'csc.exe') @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Check build failed' }
if ($BuildDock) { return }
& "$PSScriptRoot\PanelCheck.exe" "$sourceDir\Panel.xaml" $PSScriptRoot
if ($LASTEXITCODE -ne 0) { throw 'Interaction check failed' }
