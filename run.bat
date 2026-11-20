@echo off
chcp 65001 >nul
pushd "%~dp0"
dotnet run --project CncHmiDemo\CncHmiDemo.csproj
popd
