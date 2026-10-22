@echo off
cd /d "%~dp0"
echo Start your game and get in the car, then drive while this runs (60 seconds).
PitGlow.exe telemetry %*
pause
