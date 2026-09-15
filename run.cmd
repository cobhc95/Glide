@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>nul || (echo ERROR: .NET SDK not found.& exit /b 1)
dotnet run --project src\Glide.App\Glide.App.csproj -- %*
