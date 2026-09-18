# ShipWalk — Empyrion Galactic Survival Mod

Empyrion is a great game for building ships, exploring star systems, and going on adventures with friends. But one missing feature has always bothered me: **being able to walk around a ship while it is moving.**

Imagine building a huge capital vessel with a bridge, hangars, corridors, and rooms for your crew—then having to tell all your friends to sit down before you can go anywhere. For me, that breaks the feeling of being aboard a ship together.

I wanted the pilot to fly while the rest of the crew could move around and use the ship.

So, with the help of **GPT-6 Astra**, I built ShipWalk.

## What we implemented

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
