# MiHordeFlow

A drop-in replacement for `NavMeshAgent`'s built-in local avoidance, for scenes with a lot of agents.

Agents keep using `NavMeshAgent` for everything it is good at: path calculation, path following, staying on the mesh - and stop using it for the one thing that makes a crowd look bad. Instead of each agent solving RVO against its neighbours, a Burst job on the worker threads finds who is overlapping whom and pushes them apart over the following frames.

## Why

Unity's built-in avoidance is *predictive*: it tries to stop agents from ever overlapping. When it fails: and in a dense horde it fails constantly, it resolves the overlap by displacing agents hard, in one frame. That reads as snapping, and at high densities as agents visibly teleporting past each other.

This is *reactive* instead. Agents are allowed to walk into each other, and are pushed back out by a clamped, proportional force. A push proportional to overlap depth and capped at a maximum speed cannot produce a discontinuity, so a crowd squeezing through a doorway compresses and relaxes instead of popping.

**On performance, be honest about what you are getting.** Measured at 2000 agents on one machine, this costs about the same as the built-in solver: 2.99 ms vs 3.17 ms above an empty-scene floor of 6.66 ms. The built-in solver is native C++ and already runs across job threads; it was never main-thread-bound work waiting to be parallelised, and Burst has nothing to do with it because Burst compiles C#. So the reason to use this is that it **looks** better and that the cost is yours to tune: update interval, push strength, per-group settings - here the built-in solver's cost is not.

## Setup

1. Drop `SeparationSystem` on any always-loaded manager object. That is the whole scene setup.
2. On the enemy prefab, add `NavMeshSeparationAgent` next to the `NavMeshAgent`.
3. Optionally add `HordeEntity` for chase behaviour with freeze/resume.
4. Optionally drop `HordePathScheduler` on the same manager object to pace repathing by distance to goal.

Nothing else is required. With no settings asset assigned the system builds defaults in memory, so it works on first play. If bodies register into a scene with no `SeparationSystem`, you get a warning rather than silence.

### NavMeshSeparationAgent

Turns off the agent's own avoidance and feeds the separation push back in. It does not wrap or replace whatever script drives the agent's destination, so existing enemy code keeps working untouched.

`separationRadius` is deliberately its own value rather than a read of `NavMeshAgent.radius`. That radius is also the clearance the pathfinder keeps from walls: shrink it to let a horde pack tighter and you quietly change how everything paths around corners. `Reset` and the *Match Radius To Agent* context menu seed it from the agent, so the default matches and only a deliberate change diverges.

`applyMode` decides how the push reaches the agent:

| Mode | What it does |
| --- | --- |
| `NAVMESH_MOVE` | `agent.Move`, which projects the push onto the navmesh. Agents can never be pushed through a wall. Default. |
| `NEXT_POSITION` | Writes `agent.nextPosition` directly. Skips the projection, so a crowd pressed against a wall can be shoved off the mesh. |
| `NONE` | Computes the push and discards it. Only useful for measuring what the jobs cost with the hand-off removed. |

### HordeEntity

Chase behaviour and nothing else. Repathing is on an interval, and the countdown starts at a random point inside
its own interval, so a wave spawned in one frame does not then repath in lockstep forever.

```csharp
entity.SetTarget(player);        // chase a transform
entity.SetDestination(point);    // walk to a fixed point instead
entity.ForceRepath();            // ignore the interval and threshold
entity.Freeze();                 // stop moving and repathing
entity.Resume();                 // start again, pathing fresh next frame
entity.SetFrozen(inAttack);      // for driving from a state machine
```

`Freeze` sets `isStopped` rather than disabling the agent. A disabled agent leaves the navmesh and has to be warped back, and anything standing on it during those frames pathed straight through where it used to be. `isStopped` keeps the agent in place and on the mesh, and stops the steering and path following that are most of what an idle agent costs.

By default a frozen entity still takes part in separation, so an enemy stopped mid-attack still blocks the ones behind it. Clear `separateWhileFrozen` and it drops out of the roster entirely while frozen: cheaper, but the crowd walks through it.

## Path scheduling

Optional. Drop **`HordePathScheduler`** on a manager object and it takes over deciding when each `HordeEntity`
repaths. With no scheduler in the scene, entities pace themselves on their own `repathInterval` exactly as before.

Repathing a horde on one shared interval wastes most of it: the agents about to reach the player and the agents
still crossing the map get the same share of a budget only the first group can use. The scheduler paces each
entity by **how far it is from its goal**, so the near ranks update often and the far ones update rarely.

There is no sorting anywhere in this. If the *interval* is a function of distance, priority falls out for free — an
entity ten metres out is simply due ten times as often as one sixty metres out. Ranking them against each other
each frame would only change which entities win a frame that ran out of budget, and rotating where the scan starts
spreads that around more evenly than a sort would.

| Setting | What it does |
| --- | --- |
| `repathsPerFrame` | Ceiling on path requests per frame. The expensive part is the request itself, not the bookkeeping. |
| `nearInterval` / `nearDistance` | How often an entity this close to its goal repaths. |
| `farInterval` / `farDistance` | How often an entity this far out repaths. Interval lerps between the two. |
| `guaranteedInterval` | Nothing goes longer than this without a repath, whatever its distance. |

`guaranteedInterval` runs as a **separate first pass**, and it is not optional decoration. Distance pacing alone can
starve the far ranks indefinitely if the near ones keep filling the budget, and an entity that never repaths is one
walking at where the player used to be. Anything past the guarantee jumps the queue regardless of distance.

`RepathsLastFrame` reports what actually went out. If it sits pinned at `repathsPerFrame` every frame the crowd is
asking for more than it is being given — raise the budget or widen `nearInterval`.

The scheduler asks each entity for its distance as a *return value* from the repath rather than polling for it up
front. Working out how far an entity is from its goal means reading two transforms, and asking all of them in order
to choose a hundred would cost more than repathing them. Only the entities actually being repathed pay for it,
which is what makes the budget mean anything.

`IRepathBody` is the contract, so anything with a destination can be paced by this, not only a `NavMeshAgent`.

## Groups

A group is one independent crowd. **The settings asset is the group identity**: bodies pointing at the same `SeparationSettings` asset push against each other, bodies pointing at different assets never meet. There is no registry to keep in sync and no way to typo a body into a group that does not exist.

Leave the field empty on `NavMeshSeparationAgent` and the body lands in the system's default group.

This is what lets one scene run settings that would otherwise be a compromise:

| | Plane | Interval | Why |
| --- | --- | --- | --- |
| Flyers | `FULL_3D` | 1 | Genuinely need to separate vertically |
| Melee horde | `XZ` | 1 | Dense, and the thing you actually look at |
| Slow ranged | `XZ` | 3 | Barely moves, a third of the solves is unnoticeable |

Smaller groups are also cheaper than one big one, because a spatial hash gets cheaper faster than linearly as the population in it drops.

The cost is that a flyer and a walker in the same spot will not push each other out. That is usually what you want for flyers. Where it is not, put them in one group with a plane that suits both: do not try to make groups aware of each other.

Groups are created lazily, the first time a body registers with a given asset. `SetGroup` moves a body between them at runtime.

## SeparationSettings

`Assets > Create > MiHordeFlow > Separation Settings`.

**Scheduling**

- `updateInterval`: frames between solves. `1` is every frame, `3` is every third.
- `completionMode`: `NEXT_FRAME` never blocks and uses last frame's positions. `LATE_UPDATE` joins at the end of the same frame, so the main thread can stall on a slow solve. `POLLED` joins in `LateUpdate` only if the job has   already finished, otherwise it rolls into the next frame.
- `reusePushBetweenTicks`: whether the last push keeps being applied on the frames between solves, rather than dropping to zero. Leave this on with an interval above 1 or the crowd stutters.

**Grid**

- `plane`: `XZ` for a flat 3D navmesh, `XY` for 2D setups such as NavMeshPlus, `FULL_3D` for flyers. Inactive axes are not scanned at all, so `XZ` is a third of the cell lookups of `FULL_3D`, not the same work with a mask on it.
- `cellSize`: `0` derives it from the largest registered radius each tick, which is right unless your radii vary wildly.
- `initialCapacity`: how many bodies the native arrays are sized for up front, so a spawn wave does not reallocate.

**Push**

- `pushStrength`: world units per second of push per unit of overlap.
- `maxPushSpeed`: ceiling on the push any one body can receive, so an agent buried in a crowd is not launched.

## Profiling

Three markers, shared across groups so the rows stay readable:

- `MiHordeFlow.Separation.Tick`: sampling the roster and scheduling.
- `MiHordeFlow.Separation.Complete`: joining the job. Should be near zero in `NEXT_FRAME` mode; if it is not, the solve is not finishing inside a frame.
- `MiHordeFlow.Separation.Apply`: handing pushes back to the agents.

The job itself appears in neither, because it runs on the worker threads. `Apply` is the expensive one and it is main-thread by necessity: it is one native call per agent per frame into `NavMeshAgent`. At a few thousand agents that hand-off can cost more than the solve it feeds.

`SeparationSystem` runs a Burst probe once at startup and warns if the jobs fell back to plain IL, which is the failure that looks like success: everything still runs, the numbers are just several times worse, and nothing else tells you.

## Requirements

Unity 6, plus `com.unity.burst`, `com.unity.collections` and `com.unity.mathematics`. Nothing else, the runtime assembly does not reference `com.unity.ai.navigation` or any project code.

## Extending

`ISeparationBody` is the whole contract: position and radius in, push out. It mentions no `NavMeshAgent`, no `Rigidbody`, no `Transform` and no number of dimensions, which is what lets a 3D navmesh scene and a 2D NavMeshPlus scene share one system without either knowing about the other. `NavMeshSeparationAgent` is just the implementation that happens to ship with it.