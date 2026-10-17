@echo off
rem Launcher for the MCP server. Builds first (so a restart always picks up the latest
rem code), then starts the server. All build output goes to stderr: stdout is the MCP
rem protocol channel and must stay clean. The build is skipped silently when it fails
rem (e.g. another session still has the previous build loaded) and the last good build runs.
set "ROOT=%~dp0.."
dotnet build "%ROOT%\AssetTool.Mcp\AssetTool.Mcp.csproj" -c Release -o "%ROOT%\AssetTool.Mcp\bin\mcp" -v q --nologo 1>&2
"%ROOT%\AssetTool.Mcp\bin\mcp\asset-tool-mcp.exe"
