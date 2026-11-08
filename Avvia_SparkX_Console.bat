@echo off
chcp 65001 > nul
cd /d "%~dp0"
title SparkX i7 • Console Telemetria & Stampe

echo ============================================================
echo   SparkX i7 • Console Desktop Telemetria & Storico Stampe
echo   Stampante IP: 192.168.78.50:7125 (Moonraker)
echo ============================================================
echo.
echo Avvio in corso di SparkXPrintConsole.exe...
echo.

set "EXE_PATH=%~dp0SparkXPrintConsole\bin\Release\net8.0-windows\SparkXPrintConsole.exe"

if not exist "%EXE_PATH%" (
    echo [ATTENZIONE] Eseguibile Release non trovato.
    echo Avvio compilazione automatica in Release...
    dotnet build "%~dp0SparkXPrintConsole\SparkXPrintConsole.csproj" -c Release
    if %ERRORLEVEL% NEQ 0 (
        echo [ERRORE] Compilazione fallita.
        pause
        exit /b %ERRORLEVEL%
    )
    echo Compilazione completata con successo!
    echo.
)

start "" "%EXE_PATH%"

echo [OK] Applicazione SparkX Print Console avviata con successo!
echo.
echo Questa finestra del prompt dei comandi (CMD) puo' rimanere aperta
echo o essere chiusa in qualsiasi momento senza interrompere l'applicazione.
echo.
pause
