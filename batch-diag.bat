@echo off
cd /d "%~dp0"
echo === CivSim Batch Diagnostic Run ===
echo Running 5 seeds (4 years each), console suppressed...
echo.

for %%s in (1001 2002 3003 4004 5005) do (
    echo --- Seed %%s ---
    dotnet run --project CivSim.Diagnostics -- --seed %%s --ticks 35040 --verbosity summary --no-pause > nul
    echo     Done.
)

echo.
echo === All runs complete ===
echo Check the diagnostics/ folder for .html dashboards.
pause
