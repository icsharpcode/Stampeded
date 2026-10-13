<#
.SYNOPSIS
Takes the screenshots of the feature tours, through the app's own screenshot harness.

.DESCRIPTION
A shot is a text file under shots/, named like the image it produces under images/. Its lines
are the harness commands described in docs/ui.md (ScreenshotWatcher). Two things are added
here, because a command that opens something has not finished when the capture is taken:

    ---        ends one request and starts the next; the image is the last one's capture
    sleep:N    waits N seconds after the request it is written in
    screen     after that wait, takes the image from the operating system instead (Windows);
               requests after it still run, to tidy up, and no longer change the image
    screen:X,Y the same, with a tooltip drawn under the point X,Y of the window

The harness renders the window, and on Windows a tooltip, a flyout or the comment editor is
a window of its own that such a rendering does not contain. "screen" asks each of the app's
windows to print itself and puts them together - not a copy of the screen, so nothing that
happens to lie over the app can end up in an image. A tooltip opens where the real pointer
is, which a pointer moved by the harness is not; "screen:X,Y" puts it where it belongs.

The app is started once and driven shot by shot:

    ./shoot.ps1 -Start -Demo ../../../stampeded-demo -Pr 1 -Fresh
    ./shoot.ps1 01-*                # every shot of the first tour, in name order
    ./shoot.ps1 -Stop
    ./shoot.ps1 -Verify             # every image is referenced, every reference has an image

.PARAMETER Start
Starts the app on the demo clone with the window, zoom, theme and layout the tours are shot
at, and with nothing but the demo clone in the recent list - a start page shot must not show
what else the person shooting has been reviewing. The settings replaced are put back by -Stop.

.PARAMETER Fresh
With -Start: forgets what was read in the demo repository (viewed flags, drafts, pass heads).
#>
param(
	[Parameter(Position = 0)][string[]]$Shots,
	[switch]$Start,
	[string]$Demo,
	[int]$Pr,
	[switch]$Fresh,
	[switch]$Stop,
	[switch]$Verify
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path "$PSScriptRoot/../.."
$settings = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'stampeded'
$kept = Join-Path ([IO.Path]::GetTempPath()) 'stampeded-tour-settings'
$pidFile = Join-Path $kept 'pid'
$framed = 'window.txt', 'zoom.txt', 'theme.txt', 'diff-layout.txt', 'scope-mode.txt', 'tab-rows.txt', 'recent-repos.txt', 'delete-branch.txt', 'merge-method.txt'

if ($Start) {
	if (Test-Path $pidFile) { throw 'Already started. Run -Stop first.' }
	$Demo = Resolve-Path $Demo
	New-Item -ItemType Directory -Force $kept, $settings | Out-Null
	foreach ($name in $framed) {
		if (Test-Path "$settings/$name") { Move-Item "$settings/$name" "$kept/$name" -Force }
	}
	Set-Content "$settings/window.txt" '80 40 1600 1000 normal' -NoNewline
	Set-Content "$settings/theme.txt" 'Light' -NoNewline
	Set-Content "$settings/diff-layout.txt" 'unified' -NoNewline
	Set-Content "$settings/scope-mode.txt" 'whole' -NoNewline
	Set-Content "$settings/tab-rows.txt" 'single' -NoNewline
	Set-Content "$settings/recent-repos.txt" $Demo
	if ($Fresh) {
		Remove-Item "$settings/reviews/$(Split-Path $Demo -Leaf)_*" -ErrorAction Ignore
	}
	$exe = Get-ChildItem "$repo/src/Stampeded/bin/Debug/net10.0/Stampeded*" -Include 'Stampeded.exe', 'Stampeded' |
		Select-Object -First 1
	if (-not $exe) { throw 'Build the app first: dotnet build Stampeded.slnx' }
	$arguments = @("`"$Demo`"")
	if ($Pr) { $arguments += '--pr', $Pr }
	# The harness names its trigger file /tmp/..., which on Windows is \tmp on the drive of
	# the working directory - so the app is started from a known one.
	$app = Start-Process $exe -ArgumentList $arguments -WorkingDirectory $repo -PassThru
	Set-Content $pidFile $app.Id
	Write-Host "Started, pid $($app.Id). Give a review time to load before the first shot."
	return
}

if ($Stop) {
	if (Test-Path $pidFile) {
		Stop-Process -Id (Get-Content $pidFile) -ErrorAction Ignore
		Remove-Item $pidFile
	}
	foreach ($name in $framed) {
		Remove-Item "$settings/$name" -ErrorAction Ignore
		if (Test-Path "$kept/$name") { Move-Item "$kept/$name" "$settings/$name" }
	}
	Write-Host 'Stopped, settings restored.'
	return
}

if ($Verify) {
	$referenced = Get-ChildItem "$PSScriptRoot/*.md" | Select-String -Pattern '\(images/([^)]+\.png)\)' -AllMatches |
		ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
	$present = @(Get-ChildItem "$PSScriptRoot/images/*.png" | ForEach-Object Name)
	$missing = @($referenced | Where-Object { $_ -notin $present })
	$unused = @($present | Where-Object { $_ -notin $referenced })
	$missing | ForEach-Object { Write-Host "referenced, not there: $_" }
	$unused | ForEach-Object { Write-Host "there, not referenced: $_" }
	if ($missing -or $unused) { exit 1 }
	Write-Host "$($present.Count) images, all referenced."
	return
}

if (-not (Test-Path $pidFile)) { throw 'Not started. Run -Start first.' }
$trigger = [IO.Path]::GetFullPath("/tmp/stampeded-screenshot-request.$(Get-Content $pidFile)", $repo)
New-Item -ItemType Directory -Force (Split-Path $trigger), "$PSScriptRoot/images" | Out-Null
$scratch = Join-Path $kept 'intermediate.png'

Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing, System.Drawing.Common, System.Drawing.Primitives, System.Collections, System.Private.Windows.GdiPlus, System.Private.Windows.Core -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

public static class WindowShot
{
	delegate bool EnumProc(IntPtr window, IntPtr state);
	[StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
	[StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
	[DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr state);
	[DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
	[DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
	[DllImport("user32.dll")] static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
	[DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out RECT rect);
	[DllImport("user32.dll")] static extern bool GetClientRect(IntPtr window, out RECT rect);
	[DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr window, ref POINT point);
	[DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

	static Bitmap Print(IntPtr window, int width, int height, uint flags)
	{
		var bitmap = new Bitmap(width, height);
		using (var g = Graphics.FromImage(bitmap))
		{
			IntPtr dc = g.GetHdc();
			PrintWindow(window, dc, flags);
			g.ReleaseHdc(dc);
		}
		return bitmap;
	}

	/// The client area of a process's main window with every other visible window of that
	/// process drawn over it where it sits, scaled to the size the harness renders at.
	public static void Save(int process, IntPtr main, string path, int width, int height, int tipX, int tipY)
	{
		// Per-monitor aware, or every rectangle comes back in pretend pixels.
		SetThreadDpiAwarenessContext(new IntPtr(-4));
		var others = new List<IntPtr>();
		EnumWindows((window, _) => {
			uint owner;
			GetWindowThreadProcessId(window, out owner);
			if (owner == process && window != main && IsWindowVisible(window))
				others.Add(window);
			return true;
		}, IntPtr.Zero);
		RECT client;
		GetClientRect(main, out client);
		var origin = new POINT();
		ClientToScreen(main, ref origin);
		// 1 = client area only, 2 = render what the compositor holds, not what GDI last drew.
		using (var whole = Print(main, client.Right, client.Bottom, 3))
		{
			using (var g = Graphics.FromImage(whole))
			{
				// Enumeration is front to back; paint back to front.
				for (int i = others.Count - 1; i >= 0; i--)
				{
					RECT rect;
					GetWindowRect(others[i], out rect);
					if (rect.Right - rect.Left <= 0 || rect.Bottom - rect.Top <= 0)
						continue;
					// A tip sits below the pointer, clear of the line it is about.
					int x = tipX < 0 ? rect.Left - origin.X
						: Math.Max(0, Math.Min(tipX * client.Right / width, client.Right - (rect.Right - rect.Left) - 8));
					int y = tipX < 0 ? rect.Top - origin.Y : (tipY + 14) * client.Right / width;
					using (var popup = Print(others[i], rect.Right - rect.Left, rect.Bottom - rect.Top, 2))
						g.DrawImage(popup, x, y);
				}
			}
			using (var scaled = new Bitmap(width, height))
			{
				using (var g = Graphics.FromImage(scaled))
				{
					g.InterpolationMode = InterpolationMode.HighQualityBicubic;
					g.PixelOffsetMode = PixelOffsetMode.HighQuality;
					g.DrawImage(whole, 0, 0, width, height);
				}
				scaled.Save(path, System.Drawing.Imaging.ImageFormat.Png);
			}
		}
	}
}
'@

$files = $Shots -split ',' | ForEach-Object { Get-ChildItem "$PSScriptRoot/shots/$_.txt" } | Sort-Object Name
foreach ($file in $files) {
	$image = Join-Path "$PSScriptRoot/images" ($file.BaseName + '.png')
	$requests = @(((Get-Content $file -Raw) ?? '') -split '(?m)^---\s*$')
	$taken = $false
	for ($i = 0; $i -lt $requests.Count; $i++) {
		$lines = $requests[$i] -split '\r?\n' | Where-Object { $_.Trim() -and -not $_.StartsWith('#') }
		$sleep = $lines | Where-Object { $_ -match '^sleep:(\d+)$' } | ForEach-Object { [int]$Matches[1] }
		$target = ($i -eq $requests.Count - 1 -and -not $taken) ? $image : $scratch
		Remove-Item $target -ErrorAction Ignore
		Set-Content $trigger (@($target) + @($lines | Where-Object { $_ -notmatch '^(sleep:|screen)' }))
		$waited = 0
		while (-not (Test-Path $target)) {
			Start-Sleep -Milliseconds 250
			if (($waited += 250) -gt 15000) { throw "$($file.Name): no capture. Is the app still running?" }
		}
		if ($sleep) { Start-Sleep -Seconds ($sleep | Measure-Object -Sum).Sum }
		$screen = $lines | Where-Object { $_ -match '^screen(:(\d+),(\d+))?$' } | Select-Object -First 1
		if ($screen) {
			$null = $screen -match '^screen(:(\d+),(\d+))?$'
			$tipX, $tipY = $Matches[2] ? ([int]$Matches[2], [int]$Matches[3]) : (-1, -1)
			# The harness capture just taken says what size the image is to be.
			$size = [System.Drawing.Image]::FromFile($target)
			$width, $height = $size.Width, $size.Height
			$size.Dispose()
			$app = Get-Process -Id (Get-Content $pidFile)
			[WindowShot]::Save($app.Id, $app.MainWindowHandle, $image, $width, $height, $tipX, $tipY)
			$taken = $true
		}
	}
	Write-Host "$($file.BaseName).png"
}
