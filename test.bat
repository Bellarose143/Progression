@echo off
cd /d "%~dp0"
dotnet run --project CivSim.Raylib -- --seed 16001
pause
