# Anglerfish Pilot checks

This is a separate, server-only test mod for Pilot 0.1.0 and Vintage Story
1.22.7. It is excluded from the production mod and is not installed in the
normal Mods folder.

Build `PilotSmoke.csproj` after building Underwater Horrors. Its references
expect the installed Pilot DLL extracted to `bin/pilot-inspect/Pilot.dll`.
Package `AnglerfishPilotSmoke.dll` beside a `modinfo.json` with the mod ID
`anglerfishpilotsmoke` and dependencies on `pilot` and `underwaterhorrors`.

Run in a **copy** of a deep-water test world with a separate `--dataPath`.
Ensure that the copied client settings' `modPaths` include the test mod and
that `pauseGameOnLostFocus` is false. Pilot needs an online player and an
unpaused world. Its bridge is local-only:

```powershell
Invoke-RestMethod http://127.0.0.1:4590/status
Invoke-RestMethod 'http://127.0.0.1:4590/run?filter=anglerfish'
```

The three checks spawn real entities in the loaded world and advance their
actual server behaviors with bounded tick intervals. They verify submerged
patrol and turn speed, immediate surface recovery during cooldown, and live
lure speed and settled spacing. Fixtures restore player position/game mode
and the observer setting, and remove their spawned fish and lure. They do
not measure client rendering smoothness or replace a visual multiplayer test.

Verified on 2026-09-09 with all three checks passing: patrol covered 15.12
blocks with a maximum 2.25-degree turn per 50 ms, recovery dived 22 blocks
during cooldown, and the lure settled 11.59 blocks from its swimmer with no
50 ms step greater than 0.400 blocks. The copied test instance was stopped
afterward; the original save was not used for these test mutations.
