# ShipWalk — Empyrion Galactic Survival Mod

> **Development branch:** tracing and experimental passenger reconnect recovery are enabled here. Reconnect recovery is unfinished and is excluded from the public 0.4.16 release on `main`.

> **Development branch:** diagnostic builds keep automatic tracing. Packages have
> `-dev-` in their filenames. The 0.4.16 candidate corrects the reconnect hold
> observed in the 0.4.15 logs. It passes offline checks; live multiplayer
> acceptance is still pending. Published packages from `main` remain quiet.

Empyrion is a great game for building ships, exploring star systems, and going on adventures with friends. But one missing feature has always bothered me: **being able to walk around a ship while it is moving.**

Imagine building a huge capital vessel with a bridge, hangars, corridors, and rooms for your crew—then having to tell all your friends to sit down before you can go anywhere. For me, that breaks the feeling of being aboard a ship together.

I wanted the pilot to fly while the rest of the crew could move around and use the ship.

So, with the help of **GPT-6 Astra**, I built ShipWalk.

## What we implemented

ShipWalk uses a local reference-frame system to let players move relative to their ship. A separate local physics scene handles movement and interior collision, while the resulting position is translated back into the game world and synchronized with other players.

- **Walking aboard moving ships**
  Players can move around aboard CVs, SVs, and HVs while the vessel is moving.

- **Ship-relative movement**
  Player movement and interior collision are handled in a local reference frame, so the character moves with the ship while still being able to walk around independently.

- **Leaving open seats while moving**
  Players can leave supported open seats without first stopping the vessel. Enclosed cockpits retain their normal exit restrictions.

- **Motion after the pilot leaves the seat**
  The mod preserves the vessel’s motion across pilot exit so it can continue moving without someone constantly sitting at the controls. The game’s native unpiloted slowdown still applies.

- **Jumping, jetpack use, and elevators**
  These have been adapted to work with the ship-relative movement system, alongside fixes for camera movement and room lighting.

- **Docked-vessel collision**
  A docked SV or HV becomes part of the carrier’s local collision environment, so players cannot simply walk through it.

- **Reference-frame changes when docking or undocking**
  A player aboard a departing SV follows that SV. Players remaining aboard the CV stay with the CV. The handover preserves position, facing, and velocity.

- **Multiplayer passenger synchronization**
  The mod tracks which vessel each player occupies and synchronizes their ship-relative position and walking movement. Sitting down keeps the player registered as aboard.

- **Planet transitions and warp handling**
  Added handling for standing passengers during planet/orbit transitions, normal warp, and MicroWarp.

- **Returning to normal world collision**
  Players who leave or fall off a ship return to normal world physics, including terrain collision.

- **Automatic multiplayer startup**
  Server components initialize automatically, and clients enable the mod after connecting to a compatible ShipWalk server.

ShipWalk is a separate mod; it does not replace the official game assemblies.

## Reconnect control correction in 0.4.16

The 0.4.15 diagnostic logs exposed a reconnect check holding the character before the server had confirmed a saved passenger. That prevented normal boarding and left jetpack look input waiting on a controller that was not running.

The initial lookup now leaves movement, camera control and automatic boarding available. Only an authenticated restoration offer can begin a temporary hold. An unanswered lookup expires after eight seconds; late offers cannot take control back. Confirmed restoration retains its separate 90-second deadline. The dedicated manager resolves each player's account from that player's server record, and cancellation retries until acknowledged.

**This correction passes 212 offline checks and the native binding/API checks.** A local co-op test recorded normal boarding, walking, seat exits and jetpack rotation without the initial reconnect hold. Remote dedicated multiplayer and return to a moved vessel after logout remain unverified. Update participating clients, the dedicated manager and every playfield worker together when testing this development build.

## Docked seat-exit recovery in 0.4.15

This build addresses the case where leaving a docked SV/HV seat failed character placement and discarded the carrier's local frame. It checks the completed native exit position, resolves collision from explicit proposed capsule positions, and retains the prepared interior during a short placement retry. Nearby alternatives require floor support and a clear route. Standing on the CV floor updates the occupied vessel to the CV before publishing the walking state.

Seat-exit recovery follows the moving carrier for at most two seconds, then restores native control if placement remains blocked. Moving to a different position in the same ship can trigger a limited automatic retry. Search work is limited to one slice per physics step; it does not continually rebuild the interior. Public packages retain quiet output. If recovery expires, the last failure and blocking collider can be inspected with `mod exs status`.

**The 0.4.15 build passes 205 offline checks and the native binding/API checks. The dock/exit/walk/accelerate sequence still requires testing in a running dedicated-server session.** It also includes the reconnect work described below. Update participating clients, the dedicated manager and every playfield worker together.

## Reconnect recovery

The server now saves a validated passenger's position and facing relative to their occupied vessel. On reconnect, it looks up that vessel's current planet or space area, moves the player there, prepares local collision, and restores them aboard. Records live in the server's current save under `ShipWalk/passengers-v1.bin`, with a backup, so they can survive server restarts.

The record follows the actual occupied vessel: logging out inside a docked SV follows that SV if it undocks while the player is offline. A returning walking player is restored standing, without taking another player's seat or pilot controls. Existing native seating is retained if the game already restores the player in the saved vessel. Blocked positions are checked for nearby clearance. Failed recovery releases the temporary hold and attempts to restore the original login position.

Update the **dedicated manager, every playfield worker, and participating clients** together. Startup and recovery are automatic. Records begin after the updated client and server have observed the player aboard; this cannot recover a ship association from a logout made before the update. `mod exs off` clears the current association and disables recovery for that client until enabled again or the game restarts. Single-player reconnect recovery is outside this change.

**Reconnect recovery has not yet passed live multiplayer acceptance.** The 0.4.15 diagnostic runs exposed the initial-lookup hold described above; 0.4.16 addresses that code path. The 212 offline checks include reconnect coverage. The three-player testing described below covers the earlier movement implementation. Test logout/rejoin after movement, warp, server restart, and SV undocking before treating reconnect recovery as verified in your server setup.

## Known issues

This is still an experimental mod. Known issues include:

- **Sprinting does not currently work while using ship-relative movement.**
- **An unpiloted ship gradually loses speed even with auto-brake disabled.** The game applies additional native damping that this mod currently leaves unchanged.
- **Leaving the pilot seat at high speed can cause interaction problems.** You may be unable to target or interact with the seat again. The current workaround is to open the ship’s control panel with **`P`** and stop the engines before trying again.

There may be other bugs, especially with ship layouts or situations I have not tested.

## Testing so far

**I have tested with three players aboard the same ship on a dedicated server.**

That is the extent of my multiplayer testing so far. Larger groups, multiple occupied ships, and different server conditions may expose issues I have not encountered.

## Release and development builds

The **`main` branch and release builds** run without automatic ShipWalk popups or trace logging. Manual diagnostic commands remain available.

The **`dev` branch** retains the tracing implementation for debugging.
