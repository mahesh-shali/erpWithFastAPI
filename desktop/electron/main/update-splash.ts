import { spawn } from 'node:child_process';
import { existsSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';

/**
 * Loading screen shown while a downloaded update installs (Windows). The app has quit by then, so a small
 * PowerShell/WinForms window draws the same screen as the app's own (index.html) right over the app window,
 * and closes itself as soon as the new version's window appears (or after a timeout). PowerShell ships with
 * every supported Windows version, so no extra binary is needed.
 */

export interface SplashOptions {
  version: string;
  /** Window bounds in physical screen pixels (screen.dipToScreenRect). */
  bounds: { x: number; y: number; width: number; height: number };
  scale: number;
  readyFile: string;
  timeoutSec?: number;
}

const SAFE_VERSION = /^[0-9A-Za-z.+-]{1,40}$/;

/** Command-line arguments for the splash script. Everything is validated: nothing reaches PowerShell as code. */
export function splashArgs(scriptPath: string, o: SplashOptions): string[] {
  const int = (n: number) => String(Math.round(Number.isFinite(n) ? n : 0));
  const scale = Number.isFinite(o.scale) && o.scale > 0.5 && o.scale < 5 ? o.scale : 1;
  return [
    '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', scriptPath,
    '-Version', SAFE_VERSION.test(o.version) ? o.version : '',
    '-X', int(o.bounds.x), '-Y', int(o.bounds.y),
    '-W', int(Math.max(o.bounds.width, 400)), '-H', int(Math.max(o.bounds.height, 300)),
    '-Scale', scale.toFixed(2),
    '-ReadyFile', o.readyFile,
    '-TimeoutSec', int(o.timeoutSec ?? 180),
  ];
}

/**
 * Starts the splash (detached, it outlives the app) and resolves once it is on screen, or after `waitMs`
 * so an update never hangs on it. Returns false on other platforms or if it did not appear in time.
 */
export async function showUpdateSplash(o: Omit<SplashOptions, 'readyFile'>, waitMs = 5000): Promise<boolean> {
  if (process.platform !== 'win32') return false;
  const dir = tmpdir();
  const script = path.join(dir, 'erp-platform-update-splash.ps1');
  const readyFile = path.join(dir, `erp-platform-update-splash-${process.pid}.ready`);
  try {
    rmSync(readyFile, { force: true });
    writeFileSync(script, `﻿${SPLASH_PS1}`, 'utf8'); // BOM: Windows PowerShell 5.1 reads it as UTF-8
    const child = spawn('powershell.exe', splashArgs(script, { ...o, readyFile }), {
      detached: true, stdio: 'ignore', windowsHide: true,
    });
    child.on('error', () => undefined);
    child.unref();
  } catch {
    return false;
  }
  const deadline = Date.now() + waitMs;
  while (Date.now() < deadline) {
    if (existsSync(readyFile)) return true;
    await new Promise((r) => setTimeout(r, 100));
  }
  return false;
}

/** The splash itself. Mirrors the .splash-* styles in index.html. */
export const SPLASH_PS1 = String.raw`param(
  [string]$Version = '',
  [int]$X = 0, [int]$Y = 0, [int]$W = 1360, [int]$H = 860,
  [double]$Scale = 1.0,
  [string]$ReadyFile = '',
  [int]$TimeoutSec = 180
)
$ErrorActionPreference = 'SilentlyContinue'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -Namespace ErpSplash -Name Native -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();'
[void][ErpSplash.Native]::SetProcessDPIAware()
[System.Windows.Forms.Application]::EnableVisualStyles()

$started = Get-Date
$ellipsis = [string][char]0x2026
$message = if ($Version) { "Updating to version $Version$ellipsis" } else { "Updating$ellipsis" }
$detail = 'ERP Platform will reopen where you left off.'

function C([int]$r, [int]$g, [int]$b, [int]$a = 255) { [System.Drawing.Color]::FromArgb($a, $r, $g, $b) }
function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = [Math]::Min($r * 2, [Math]::Min($w, $h))
  $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
  $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
  $p.CloseFigure(); return $p
}

$form = New-Object System.Windows.Forms.Form
$form.FormBorderStyle = 'None'
$form.StartPosition = 'Manual'
$form.Bounds = New-Object System.Drawing.Rectangle($X, $Y, $W, $H)
$form.BackColor = (C 245 247 251)
$form.ShowInTaskbar = $false
$form.TopMost = $true
$form.Text = 'ERP Platform'
$form.GetType().GetProperty('DoubleBuffered', [Reflection.BindingFlags]'Instance,NonPublic').SetValue($form, $true, $null)

$titleFont = New-Object System.Drawing.Font('Segoe UI', [float](20 * $Scale), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$msgFont = New-Object System.Drawing.Font('Segoe UI', [float](14 * $Scale), [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$detailFont = New-Object System.Drawing.Font('Segoe UI', [float](12.5 * $Scale), [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$center = New-Object System.Drawing.StringFormat
$center.Alignment = 'Center'
$script:phase = 0.0

$form.Add_Paint({
  param($sender, $e)
  $g = $e.Graphics
  $g.SmoothingMode = 'AntiAlias'
  $g.TextRenderingHint = 'ClearTypeGridFit'
  $cw = $form.ClientSize.Width; $ch = $form.ClientSize.Height
  $s = $Scale
  $logo = 72 * $s
  $top = ($ch / 2) - (100 * $s)
  $lx = ($cw - $logo) / 2

  # Logo: gradient rounded square with three rising bars (same as build/icon.svg)
  $k = $logo / 1024
  $bg = RoundRect ($lx + 32 * $k) ($top + 32 * $k) (960 * $k) (960 * $k) (216 * $k)
  $rect = New-Object System.Drawing.RectangleF(($lx + 32 * $k), ($top + 32 * $k), (960 * $k), (960 * $k))
  $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, (C 30 58 138), (C 47 91 234), [float]45)
  $g.FillPath($grad, $bg)
  foreach ($bar in @(@(236, 556, 232, 184), @(438, 420, 368, 219), @(640, 236, 552, 255))) {
    $b = New-Object System.Drawing.SolidBrush((C 255 255 255 $bar[3]))
    $g.FillPath($b, (RoundRect ($lx + $bar[0] * $k) ($top + $bar[1] * $k) (148 * $k) ($bar[2] * $k) (36 * $k)))
  }

  # Text
  $y = $top + $logo + 20 * $s
  $g.DrawString('ERP Platform', $titleFont, (New-Object System.Drawing.SolidBrush((C 29 35 48))), (New-Object System.Drawing.RectangleF(0, $y, $cw, 40 * $s)), $center)
  $y += 34 * $s
  $g.DrawString($message, $msgFont, (New-Object System.Drawing.SolidBrush((C 75 85 99))), (New-Object System.Drawing.RectangleF(0, $y, $cw, 30 * $s)), $center)
  $y += 26 * $s
  $g.DrawString($detail, $detailFont, (New-Object System.Drawing.SolidBrush((C 107 114 128))), (New-Object System.Drawing.RectangleF(0, $y, $cw, 30 * $s)), $center)

  # Indeterminate bar
  $y += 34 * $s
  $bw = 220 * $s; $bh = 4 * $s; $bx = ($cw - $bw) / 2
  $track = RoundRect $bx $y $bw $bh (2 * $s)
  $g.FillPath((New-Object System.Drawing.SolidBrush((C 227 232 244))), $track)
  $g.SetClip($track)
  $fw = $bw * 0.4
  $fx = $bx - $fw + $script:phase * ($bw + $fw)
  $g.FillPath((New-Object System.Drawing.SolidBrush((C 47 91 234))), (RoundRect $fx $y $fw $bh (2 * $s)))
  $g.ResetClip()
})

$script:ticks = 0
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 16
$timer.Add_Tick({
  $script:phase = ($script:phase + 0.016 / 1.2) % 1.0
  $form.Invalidate()
  $script:ticks++
  if ($script:ticks % 30 -eq 0) {
    # Close once the new version's window is up, or after the timeout.
    $app = Get-Process -Name 'ERP Platform' -ErrorAction SilentlyContinue |
      Where-Object { $_.StartTime -gt $started -and $_.MainWindowHandle -ne 0 }
    if ($app -or ((Get-Date) - $started).TotalSeconds -gt $TimeoutSec) { $timer.Stop(); $form.Close() }
  }
})
$form.Add_Shown({
  $form.Activate()
  if ($ReadyFile) { Set-Content -LiteralPath $ReadyFile -Value 'ready' }
  $timer.Start()
})
[System.Windows.Forms.Application]::Run($form)
if ($ReadyFile) { Remove-Item -LiteralPath $ReadyFile -Force -ErrorAction SilentlyContinue }
`;
