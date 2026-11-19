@echo off
rem Builds Stopwatch.exe with the .NET Framework compiler that ships with Windows.
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
"%CSC%" /nologo /target:winexe /optimize+ /out:"%~dp0Stopwatch.exe" "%~dp0Stopwatch.cs"
if errorlevel 1 exit /b 1
echo Built Stopwatch.exe
