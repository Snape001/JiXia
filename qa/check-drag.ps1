[CmdletBinding()]
param([string] $PreviewDirectory)

$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$source = Join-Path $PSScriptRoot '..\src'
$qa = New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot ('drag-' + [Guid]::NewGuid().ToString('N')))
$original = [IO.File]::ReadAllText((Join-Path $source 'Tasks.cs'))
$hook = '(?<![\w.])DragDrop\.DoDragDrop(?=\()'
if ([regex]::Matches($original, $hook).Count -ne 1) {
    throw 'Expected exactly one entry to the production drag loop.'
}
$testController = Join-Path $qa.FullName 'Tasks.DragCheck.cs'
[IO.File]::WriteAllText($testController, ($original -replace $hook, 'DragCheck.RunOleLoop'))
$exe = Join-Path $qa.FullName 'DragCheck.exe'
$compilerArgs = @('/nologo','/target:exe','/platform:anycpu','/langversion:5','/codepage:65001','/utf8output',"/out:$exe")
foreach ($name in @('System.dll','System.Core.dll','System.Xml.dll','System.Xaml.dll')) {
    $compilerArgs += '/reference:' + (Join-Path $framework $name)
}
foreach ($name in @('WindowsBase','PresentationCore','PresentationFramework','UIAutomationTypes','UIAutomationProvider')) {
    $compilerArgs += '/reference:' + (Join-Path $framework "WPF\$name.dll")
}
$compilerArgs += @($testController, (Join-Path $source 'DiscardMotion.cs'), (Join-Path $source 'Thoughts.cs'), (Join-Path $PSScriptRoot 'DragCheck.cs'), (Join-Path $PSScriptRoot 'InteractionPreview.cs'))
& (Join-Path $framework 'csc.exe') @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Drag QA build failed.' }
$runArgs = @((Join-Path $source 'Panel.xaml'), (Join-Path $qa.FullName 'data'))
if ($PreviewDirectory) { $runArgs += $PreviewDirectory }
& $exe @runArgs
if ($LASTEXITCODE -ne 0) { throw 'Drag QA failed.' }
