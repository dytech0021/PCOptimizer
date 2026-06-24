@echo off
REM Recompila o ícone e o instalador usando o csc.exe que vem no Windows (.NET Framework 4)
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe

echo [1/2] Gerando skull.ico...
"%CSC%" /nologo /target:exe /codepage:65001 /out:MakeIcon.exe /reference:System.Drawing.dll MakeIcon.cs
if errorlevel 1 goto erro
MakeIcon.exe

echo [2/2] Compilando Instalar_Mod.exe...
"%CSC%" /nologo /target:winexe /codepage:65001 /optimize+ /win32icon:skull.ico /out:Instalar_Mod.exe ^
  /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll OptiInstaller.cs
if errorlevel 1 goto erro

echo.
echo OK - Instalar_Mod.exe gerado.
goto fim
:erro
echo.
echo ERRO na compilacao.
:fim
pause
