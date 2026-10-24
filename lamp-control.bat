@echo off
setlocal EnableExtensions

if not defined LAMP_URL set "LAMP_URL=http://desk-lamp.local"

if "%~1"=="" goto menu
if /i "%~1"=="on" goto on
if /i "%~1"=="off" goto off
if /i "%~1"=="status" goto status
if /i "%~1"=="brightness" goto brightness_argument
if /i "%~1"=="temperature" goto temperature_argument
if /i "%~1"=="temp" goto temperature_argument
goto usage

:on
call :request -X POST "%LAMP_URL%/api/lamp/on"
exit /b %ERRORLEVEL%

:off
call :request -X POST "%LAMP_URL%/api/lamp/off"
exit /b %ERRORLEVEL%

:status
call :request "%LAMP_URL%/api/lamp"
exit /b %ERRORLEVEL%

:brightness_argument
if "%~2"=="" (
    echo Usage: %~nx0 brightness 1-100
    exit /b 2
)
call :request -X POST "%LAMP_URL%/api/lamp?brightness=%~2"
exit /b %ERRORLEVEL%

:temperature_argument
if "%~2"=="" (
    echo Usage: %~nx0 temperature 2700-6500
    exit /b 2
)
call :request -X POST "%LAMP_URL%/api/lamp?temperature=%~2"
exit /b %ERRORLEVEL%

:menu
cls
echo ==============================
echo        Desk Lamp Control
echo ==============================
echo %LAMP_URL%
echo.
echo  1. Turn on
echo  2. Turn off
echo  3. Set brightness
echo  4. Set colour temperature
echo  5. Show status
echo  0. Exit
echo.
choice /c 123450 /n /m "Choose an option: "
if errorlevel 6 exit /b 0
if errorlevel 5 goto menu_status
if errorlevel 4 goto menu_temperature
if errorlevel 3 goto menu_brightness
if errorlevel 2 goto menu_off
if errorlevel 1 goto menu_on
goto menu

:menu_on
echo.
call :request -X POST "%LAMP_URL%/api/lamp/on"
pause
goto menu

:menu_off
echo.
call :request -X POST "%LAMP_URL%/api/lamp/off"
pause
goto menu

:menu_brightness
echo.
set "VALUE="
set /p "VALUE=Brightness 1-100 (blank cancels): "
if not defined VALUE goto menu
call :request -X POST "%LAMP_URL%/api/lamp?brightness=%VALUE%"
pause
goto menu

:menu_temperature
echo.
echo 2700 K = warm, 4000 K = neutral, 6500 K = cool
set "VALUE="
set /p "VALUE=Colour temperature 2700-6500 (blank cancels): "
if not defined VALUE goto menu
call :request -X POST "%LAMP_URL%/api/lamp?temperature=%VALUE%"
pause
goto menu

:menu_status
echo.
call :request "%LAMP_URL%/api/lamp"
pause
goto menu

:request
curl.exe --silent --show-error --fail-with-body --max-time 8 %*
set "RC=%ERRORLEVEL%"
echo.
if not "%RC%"=="0" (
    echo Could not control the lamp. Check its power and Wi-Fi connection.
    echo If desk-lamp.local does not work, edit LAMP_URL in this file
    echo and replace it with the lamp's IP address, for example:
    echo set "LAMP_URL=http://192.168.1.50"
)
exit /b %RC%

:usage
echo Usage:
echo   %~nx0                 Open the interactive menu
echo   %~nx0 on
echo   %~nx0 off
echo   %~nx0 status
echo   %~nx0 brightness 60
echo   %~nx0 temperature 4000
exit /b 2
