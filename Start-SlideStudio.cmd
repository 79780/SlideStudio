@echo off
cd /d "%~dp0"
set "SlidesPath=%~dp0..\Lab Images"
echo Slide Studio: http://127.0.0.1:5188
echo Keep this window open. Press Ctrl+C to stop.
if exist "dist-range\SlideStudio.exe" (
  "dist-range\SlideStudio.exe" --contentRoot "%~dp0."
) else (
  dotnet run --project "%~dp0SlideStudio.csproj" --no-launch-profile
)
