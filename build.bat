@echo off
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /win32icon:MonitorLight.ico /out:MonitorLight.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll MonitorLight.cs
