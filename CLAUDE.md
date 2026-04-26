# Underwater Horrors mod — Claude instructions

## Build after edits

After finishing any edit to this mod and before returning to the user,
always run a build:

```
dotnet build -c Release
```

The `.csproj` `Package` target zips the output and auto-deploys it to
`%APPDATA%\VintagestoryData\Mods\UnderwaterHorrors_<version>.zip`,
deleting older versioned zips as part of the build. The version is read
from `modinfo.json` at build time.

Report the build result (success/warnings/errors) in the final message
so the user knows the deployed zip is current.
