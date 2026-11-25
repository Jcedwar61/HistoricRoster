# HistoricRosterPlugin

AssettoServer plugin for the original Assetto Corsa. See the user guide in `../README.md`
(also installed with the in-game app as `USER_GUIDE.md`) for setup, commands and configuration.

Build: copy this folder into AssettoServer's `Plugins/` folder, then

    dotnet publish Plugins/HistoricRosterPlugin -c Release

`Shared/` is also compiled into `../RosterTool`; keep it free of AssettoServer/Serilog references.
