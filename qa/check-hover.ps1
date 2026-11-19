[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$source = Join-Path $PSScriptRoot '..\src'
$qa = (Get-Item -LiteralPath $PSScriptRoot).FullName
$fixture = (New-Item -ItemType Directory -Path (Join-Path $qa ('hover-' + [Guid]::NewGuid().ToString('N')))).FullName

try {
    # Only the QA copy gets synthetic input and time. Window geometry, timers and animation remain native.
    $copy = [IO.File]::ReadAllText((Join-Path $source 'Host.cs'))
    $hooks = @(
        @{ From = 'long now = clock.ElapsedMilliseconds;'; To = 'long now = HoverCheck.Now;'; Count = 1 },
        @{ From = 'handleButton.IsMouseOver'; To = 'HoverCheck.IsHandleHovered'; Count = 3 },
        @{ From = 'bool available = GetCursorPos(out native);'; To = 'bool available = HoverCheck.ReadPointer(out native.X, out native.Y);'; Count = 1 }
    )
    foreach ($hook in $hooks) {
        if ([regex]::Matches($copy, [regex]::Escape($hook.From)).Count -ne $hook.Count) {
            throw "Cannot install hover QA hook for $($hook.From)"
        }
        $copy = $copy.Replace($hook.From, $hook.To)
    }

    $testHost = Join-Path $fixture 'Host.HoverCheck.cs'
    [IO.File]::WriteAllText($testHost, $copy)
    $exe = Join-Path $fixture 'HoverCheck.exe'
    $compilerArgs = @('/nologo','/target:exe','/platform:anycpu','/langversion:5','/codepage:65001',
        '/utf8output','/main:SidePage.HoverCheck',"/out:$exe", "/win32manifest:$source\SidePage.manifest")
    foreach ($name in @('System.dll','System.Core.dll','System.Xml.dll','System.Xaml.dll','System.Drawing.dll','System.Windows.Forms.dll')) {
        $compilerArgs += '/reference:' + (Join-Path $framework $name)
    }
    foreach ($name in @('WindowsBase','PresentationCore','PresentationFramework','UIAutomationTypes','UIAutomationProvider')) {
        $compilerArgs += '/reference:' + (Join-Path $framework "WPF\$name.dll")
    }
    $compilerArgs += @("$source\Tasks.cs", "$source\DiscardMotion.cs", "$source\Thoughts.cs", "$source\TrayMenu.cs", $testHost, "$qa\DockCheck.cs", "$qa\HoverCheck.cs")
    & (Join-Path $framework 'csc.exe') @compilerArgs
    if ($LASTEXITCODE -ne 0) { throw 'Hover QA build failed.' }

    & $exe "$source\Panel.xaml" "$source\SidePage.ico" (Join-Path $fixture 'data')
    if ($LASTEXITCODE -ne 0) { throw 'Hover QA failed.' }
} finally {
    $expectedParent = $qa + [IO.Path]::DirectorySeparatorChar
    if ($fixture.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $fixture -Recurse -Force
    }
}
