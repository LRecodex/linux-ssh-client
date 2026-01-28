@echo off
setlocal
set CONFIG=Release
if not "%~1"=="" set CONFIG=%~1

dotnet build "%~dp0LrecodexTerm.Windows.csproj" -c %CONFIG%
if %errorlevel% neq 0 exit /b %errorlevel%

echo Build completed: %CONFIG%
endlocal
