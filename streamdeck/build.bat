@echo off
chcp 65001 >nul
cd /d "%~dp0"
set FW=C:\Windows\Microsoft.NET\Framework64\v4.0.30319
set OUT=com.cos2104.panseo.sdPlugin
if not exist %OUT% mkdir %OUT%
"%FW%\csc.exe" /nologo /target:winexe /codepage:65001 /optimize+ /out:%OUT%\PanseoDeck.exe /r:System.Drawing.dll /r:System.Core.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll PanseoDeck.cs
if errorlevel 1 (echo Build failed & exit /b 1)
start /wait "" %OUT%\PanseoDeck.exe --setup %OUT%
start /wait "" %OUT%\PanseoDeck.exe --pack %OUT% dist\com.cos2104.panseo.streamDeckPlugin
echo Build OK: dist\com.cos2104.panseo.streamDeckPlugin
