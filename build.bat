@echo off
chcp 65001 >nul
cd /d "%~dp0"
set FW=C:\Windows\Microsoft.NET\Framework64\v4.0.30319
"%FW%\csc.exe" /nologo /target:winexe /codepage:65001 /optimize+ /win32manifest:app.manifest /out:Panseo.exe /r:"%FW%\WPF\PresentationCore.dll" /r:"%FW%\WPF\PresentationFramework.dll" /r:"%FW%\WPF\WindowsBase.dll" /r:"%FW%\System.Xaml.dll" /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Core.dll /r:System.Web.Extensions.dll Panseo.cs
if errorlevel 1 (echo Build failed & pause & exit /b 1)
echo Build OK: Panseo.exe
