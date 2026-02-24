@echo off
cd /d "%~dp0"
echo Starting CivSim Dashboard at http://localhost:5000 ...
echo Press Ctrl+C to stop.
echo.
dotnet run --project CivSim.Diagnostics -- --dashboard
