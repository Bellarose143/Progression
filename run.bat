@echo off
cd /d "%~dp0"
set /p SEED="Enter seed (or press Enter for random): "
if "%SEED%"=="" (
    dotnet run --project CivSim.Raylib
) else (
    dotnet run --project CivSim.Raylib -- --seed %SEED%
)
pause
