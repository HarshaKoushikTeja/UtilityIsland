@echo off
rem Builds one self-contained DynamicIsland.exe that runs on a PC without .NET installed.
rem Output: dist\DynamicIsland.exe  (send that single file to testers)
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -o dist
echo.
echo Done. Share dist\DynamicIsland.exe
pause
