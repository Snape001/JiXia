[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$source = Join-Path $PSScriptRoot '..\src'
$qa = (Get-Item -LiteralPath $PSScriptRoot).FullName
$fixture = (New-Item -ItemType Directory -Path (Join-Path $qa ('thoughts-' + [Guid]::NewGuid().ToString('N')))).FullName

try {
    $exe = Join-Path $fixture 'ThoughtsCheck.exe'
    $compilerArgs = @('/nologo','/target:exe','/platform:anycpu','/langversion:5','/codepage:65001',
        '/utf8output','/main:SidePage.ThoughtsCheck',"/out:$exe", "/win32manifest:$source\SidePage.manifest")
    foreach ($name in @('System.dll','System.Core.dll','System.Xml.dll','System.Xaml.dll','System.Drawing.dll','System.Windows.Forms.dll')) {
        $compilerArgs += '/reference:' + (Join-Path $framework $name)
    }
    foreach ($name in @('WindowsBase','PresentationCore','PresentationFramework','UIAutomationTypes','UIAutomationProvider')) {
        $compilerArgs += '/reference:' + (Join-Path $framework "WPF\$name.dll")
    }
    $compilerArgs += @("$source\Tasks.cs", "$source\DiscardMotion.cs", "$source\Host.cs", "$source\TrayMenu.cs", "$source\Thoughts.cs",
        "$qa\DockCheck.cs", "$qa\ThoughtsCheck.cs")
    & (Join-Path $framework 'csc.exe') @compilerArgs
    if ($LASTEXITCODE -ne 0) { throw 'Thoughts QA build failed.' }

    & $exe "$source\Panel.xaml" "$source\SidePage.ico" (Join-Path $fixture 'data')
    if ($LASTEXITCODE -ne 0) { throw 'Thoughts QA failed.' }
} finally {
    $expectedParent = $qa + [IO.Path]::DirectorySeparatorChar
    if ($fixture.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $fixture -Recurse -Force
    }
}
