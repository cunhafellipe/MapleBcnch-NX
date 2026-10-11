@echo off
rem MCP stdio server for the MAP editor.
rem stdout is the JSON-RPC channel: nothing may be written there except
rem protocol messages. The server routes all logging to stderr.
rem
rem NOTE: keep this file pure ASCII. cmd.exe reads .cmd in the system OEM
rem codepage, and a CJK byte sequence inside a rem line can terminate it
rem early and execute the remainder as a command.
setlocal
set "ROOT=%~dp0.."

set "EXE=%ROOT%\MapleMcp\bin\Release\net10.0-windows\maple-mcp.exe"

if not exist "%EXE%" (
  echo maple-mcp: binary missing, building... 1>&2
  dotnet build "%ROOT%\MapleMcp\MapleMcp.csproj" -c Release -v q --nologo 1>&2
  if errorlevel 1 exit /b 1
)

rem Point it at your backend if it is not on the default port.
if not "%WZ_SERVER%"=="" (
  "%EXE%"
  exit /b %errorlevel%
)

"%EXE%"
exit /b %errorlevel%