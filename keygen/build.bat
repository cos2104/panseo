@echo off
chcp 65001 >nul
cd /d "%~dp0"
set FW=C:\Windows\Microsoft.NET\Framework64\v4.0.30319
"%FW%\csc.exe" /nologo /target:winexe /codepage:65001 /optimize+ /out:PanseoKey.exe /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Core.dll PanseoKey.cs
if errorlevel 1 (echo Build failed & pause & exit /b 1)
echo Build OK: PanseoKey.exe
