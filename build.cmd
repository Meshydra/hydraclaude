@echo off
rem Builds HydraClaude with the in-box .NET Framework compiler - no SDK, no NuGet.
rem Output goes to %LOCALAPPDATA%\ClaudeProfiles, which is outside the Claude MSIX package
rem and so survives every Claude Desktop auto-update.
rem
rem Regenerate the icons first (needs Python + Pillow) with:  python tools\make_icons.py

setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set SRCDIR=%~dp0
set OUTDIR=%LOCALAPPDATA%\ClaudeProfiles
set OUT=%OUTDIR%\HydraClaude.exe

if not exist "%CSC%" (
  echo ERROR: csc.exe not found at %CSC%
  exit /b 1
)
if not exist "%SRCDIR%assets\hydra.ico" (
  echo ERROR: assets\hydra.ico missing - run: python tools\make_icons.py
  exit /b 1
)

if not exist "%OUTDIR%" mkdir "%OUTDIR%"

rem The tray glyphs are picked at runtime from the Windows light/dark setting, so all three
rem ship next to the exe.
copy /y "%SRCDIR%assets\hydra.ico" "%OUTDIR%\hydra.ico" >nul
copy /y "%SRCDIR%assets\hydra-tray-light.ico" "%OUTDIR%\hydra-tray-light.ico" >nul
copy /y "%SRCDIR%assets\hydra-tray-dark.ico" "%OUTDIR%\hydra-tray-dark.ico" >nul

"%CSC%" /nologo /target:winexe /optimize+ /warn:4 ^
  /win32icon:"%SRCDIR%assets\hydra.ico" ^
  /out:"%OUT%" ^
  /r:System.dll ^
  /r:System.Core.dll ^
  /r:System.Drawing.dll ^
  /r:System.Windows.Forms.dll ^
  /r:System.Management.dll ^
  /r:System.Runtime.Serialization.dll ^
  /r:System.Xml.dll ^
  /r:Microsoft.VisualBasic.dll ^
  "%SRCDIR%HydraClaude.cs" "%SRCDIR%Theme.cs" "%SRCDIR%Controls.cs" "%SRCDIR%HydraClaudeUi.cs"

if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)

echo Built %OUT%
endlocal
