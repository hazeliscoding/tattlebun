# Checks a running Tattlebun's tray icon through UI Automation: opens its menu, optionally saves a screenshot
# of it, chooses Quit Tattlebun and waits for the process to end. It moves the cursor briefly and restores it.
# Usage: powershell -NoProfile -File scripts/tray.ps1 [<screenshot.png>]
param([string] $Screenshot)

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -Namespace W -Name U -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern bool GetCursorPos(out System.Drawing.Point p);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, System.UIntPtr e);
'@ -ReferencedAssemblies System.Drawing

$A = [System.Windows.Automation.AutomationElement]; $S = [System.Windows.Automation.TreeScope]; $P = [System.Windows.Automation.PropertyCondition]
$root = $A::RootElement
$app = Get-Process Tattlebun -ErrorAction Stop | Select-Object -First 1

function Buttons($element) { @($element.FindAll($S::Descendants, (New-Object $P($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))) }
function Child($class) { $root.FindFirst($S::Children, (New-Object $P($A::ClassNameProperty, $class))) }
# The icon is on the taskbar itself once it's pinned there, and in the hidden-icons overflow until then.
function Place {
    $buttons = Buttons (Child 'Shell_TrayWnd')
    if ($buttons | Where-Object { $_.Current.Name -match '^Tattlebun' }) { return $buttons }
    $overflow = Child 'TopLevelWindowForOverflowXamlIsland'
    if (-not $overflow -or $overflow.Current.BoundingRectangle.Width -eq 0) {
        @($buttons | Where-Object { $_.Current.Name -match 'Hidden Icons' })[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 1000
        $overflow = Child 'TopLevelWindowForOverflowXamlIsland'
    }
    Buttons $overflow
}

$buttons = @(Place)
$icon = @($buttons | Where-Object { $_.Current.Name -match '^Tattlebun' })
if ($icon.Count -ne 1) { 'STOP: Tattlebun icon not found once'; exit 1 }
$b = $icon[0].Current.BoundingRectangle; $x = [int]($b.X + $b.Width / 2); $y = [int]($b.Y + $b.Height / 2)
$hits = @($buttons | Where-Object { $_.Current.BoundingRectangle.Contains((New-Object System.Windows.Point $x, $y)) })
if ($hits.Count -ne 1 -or $hits[0].Current.Name -notmatch '^Tattlebun') { 'STOP: the point is not only on Tattlebun'; exit 1 }

$saved = New-Object System.Drawing.Point; [W.U]::GetCursorPos([ref]$saved) | Out-Null
[W.U]::SetCursorPos($x, $y) | Out-Null; Start-Sleep -Milliseconds 150
[W.U]::mouse_event(0x0008, 0, 0, 0, [System.UIntPtr]::Zero); [W.U]::mouse_event(0x0010, 0, 0, 0, [System.UIntPtr]::Zero)

$menu = $null
for ($i = 0; $i -lt 20 -and -not $menu; $i++) {
    Start-Sleep -Milliseconds 150
    # Avalonia draws the tray menu as its own popup window, not a Win32 menu.
    foreach ($w in $root.FindAll($S::Children, [System.Windows.Automation.Condition]::TrueCondition)) { if ($w.Current.ClassName -eq 'TrayPopupRoot' -and $w.Current.ProcessId -eq $app.Id) { $menu = $w } }
}
# Avalonia places the menu at the cursor when it opens, so the cursor goes back only once it's open.
[W.U]::SetCursorPos($saved.X, $saved.Y) | Out-Null
if (-not $menu) { 'STOP: Tattlebun menu did not open'; exit 1 }

if ($Screenshot) {
    Start-Sleep -Milliseconds 300
    $m = $menu.Current.BoundingRectangle
    $left = [int][Math]::Min($m.X, $b.X) - 16; $top = [int][Math]::Min($m.Y, $b.Y) - 16
    $right = [int][Math]::Max($m.Right, $b.Right) + 16; $bottom = [int][Math]::Max($m.Bottom, $b.Bottom) + 16
    $bmp = New-Object System.Drawing.Bitmap ($right - $left), ($bottom - $top)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($left, $top, 0, 0, $bmp.Size); $bmp.Save($Screenshot); $g.Dispose(); $bmp.Dispose()
    'saved ' + $Screenshot
}

$quit = $menu.FindFirst($S::Descendants, (New-Object $P($A::NameProperty, 'Quit Tattlebun')))
if (-not $quit) { 'STOP: no Quit Tattlebun item'; exit 1 }
$quit.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
if (-not $app.WaitForExit(5000)) { 'STOP: Tattlebun still running 5 s after Quit'; exit 1 }
'chose Quit Tattlebun; the process ended'
