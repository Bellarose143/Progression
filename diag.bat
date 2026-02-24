@echo off
cd /d "%~dp0"
echo Running diagnostic (1 year, seed 42)...
dotnet run --project CivSim.Diagnostics -- --ticks 8640 --seed 42 --verbosity summary --no-pause > nul
echo Done! Check diagnostics\ folder for .html dashboard.
pause
