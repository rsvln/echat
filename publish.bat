@echo off
dotnet publish "src\EChat.MAUI\EChat.Maui.csproj" ^
  -f net10.0-windows10.0.19041.0 ^
  -c Release ^
  -p:RuntimeIdentifierOverride=win-x64 ^
  -p:WindowsPackageType=None ^
  -p:SelfContained=false

echo.
echo Done. Output: src\EChat.MAUI\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\
pause
