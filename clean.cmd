@echo off
setlocal
cd /d "%~dp0"
if exist build rmdir /s /q build
if exist dist\README.txt del /q dist\README.txt
if exist dist\CHANGELOG.txt del /q dist\CHANGELOG.txt
echo Build intermediates cleaned. dist\Glide.exe is intentionally preserved.
pause
