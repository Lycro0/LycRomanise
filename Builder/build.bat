@echo off
rem Builds App\LycRomanise.exe (portable, self-contained) and App\LycRomanise_Setup.exe.
rem Needs the .NET 10 SDK and Inno Setup 6. Run from this folder.
cd /d "%~dp0"
dotnet publish "..\Source Code\LycRomanise\LycRomanise.csproj" -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=none -o ..\App || goto :fail
set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%LocalAppData%\Programs\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" (echo Inno Setup 6 not found - install it from https://jrsoftware.org/isdl.php & goto :fail)
"%ISCC%" installer.iss || goto :fail
echo.
echo Done: ..\App\LycRomanise.exe and ..\App\LycRomanise_Setup.exe
exit /b 0
:fail
echo Build failed.
exit /b 1
