param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName WindowsBase,PresentationCore,PresentationFramework,System.Xaml,System.Drawing
$releasePath = (Resolve-Path -LiteralPath $Executable).Path
$assembly = [Reflection.Assembly]::LoadFrom($releasePath)
if ($assembly.EntryPoint.DeclaringType.FullName -ne 'SidePage.Program') { throw 'Wrong application entry point.' }
$panelStream = $assembly.GetManifestResourceStream('SidePage.Panel.xaml')
try { $panel = [System.Windows.Markup.XamlReader]::Load($panelStream) } finally { $panelStream.Dispose() }
$iconStream = $assembly.GetManifestResourceStream('SidePage.ico')
try {
    $icon = [System.Drawing.Icon]::new($iconStream)
    try { if ($icon.Width -lt 16) { throw 'Missing app icon.' } } finally { $icon.Dispose() }
} finally { $iconStream.Dispose() }
$boardType = $assembly.GetType('SidePage.TaskBoard', $true)
$construct = $boardType.GetConstructor([Reflection.BindingFlags]'NonPublic,Instance', $null,
    [type[]]@([System.Windows.FrameworkElement],[string]), $null)
$testRoot = Join-Path $PSScriptRoot ('release-' + [Guid]::NewGuid().ToString('N'))
$parameters = New-Object 'object[]' 2
$parameters[0] = $panel.PSObject.BaseObject
$parameters[1] = $testRoot.PSObject.BaseObject
$board = $construct.Invoke($parameters)
$add = $panel.FindName('UrgentAdd')
$add.RaiseEvent([System.Windows.RoutedEventArgs]::new([System.Windows.Controls.Button]::ClickEvent,$add))
$panel.FindName('UrgentEntry').Text = 'packaged executable check'
$board.FinishEditing()
if ($board.UrgentTotal -ne 1 -or -not (Test-Path -LiteralPath (Join-Path $testRoot 'tasks.xml'))) {
    throw 'The packaged application could not persist its first task.'
}
Write-Output 'PASS final EXE entry point, embedded WPF panel, embedded native icon, native controls and saved task'
$savedTasks = [IO.File]::ReadAllText((Join-Path $testRoot 'tasks.xml'))
$journalType = $assembly.GetType('SidePage.ThoughtJournal', $true)
$ctor = $journalType.GetConstructors([Reflection.BindingFlags]'NonPublic,Public,Instance') | Where-Object { $_.GetParameters().Count -eq 4 } | Select-Object -First 1
$journalArgs = New-Object 'object[]' 4
$journalArgs[0] = $panel.PSObject.BaseObject
$journalArgs[1] = $testRoot.PSObject.BaseObject
$journalArgs[2] = [Func[bool]] { $false }
$journalArgs[3] = [Action] { $board.FinishEditing() }
$journal = $ctor.Invoke($journalArgs)
$panel.FindName('ThoughtEntry').Text = 'A quick note from the packaged application'
$submit = $panel.FindName('ThoughtSubmit')
$submit.RaiseEvent([System.Windows.RoutedEventArgs]::new([System.Windows.Controls.Button]::ClickEvent,$submit))
if ($panel.FindName('ThoughtCount').Text -ne '1' -or -not (Test-Path -LiteralPath (Join-Path $testRoot 'thoughts.xml'))) {
    throw 'Packaged journal failed to persist a capture.'
}
if ([IO.File]::ReadAllText((Join-Path $testRoot 'tasks.xml')) -ne $savedTasks) { throw 'Capturing a thought changed existing tasks.' }
if ($panel.FindName('BinArt').Children.Count -ne 1) { throw 'Missing three-dimensional bin renderer.' }
Write-Output 'PASS packaged journal and rendered bin; existing task data preserved'
