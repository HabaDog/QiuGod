# GameThree AI boundaries

## Real-time Utility AI

`GameThreeUtilityAi` is the default server-side bot controller. It evaluates a
bounded authoritative observation every 100-1000ms (150ms by default) and
scores movement for bomb safety, pickups, territory and visible opponents. It
can request movement, bombs, dash and the selected ball skill; the match still
validates and executes every action.

Configure it under `GameThreeUtilityAi` in `appsettings.json`. Set
`DiagnosticsEnabled` to print sampled decisions with the selected action,
score, target and reason. Disabling it restores the basic random fallback.

The tactical boundary is `IGameThreeTacticalAi`. The implementation reads only
`GameThreeAiObservation`, so it can be replaced or removed without changing
physics, damage, cooldown or skill implementations.

## Optional LLM experiment

The module is disabled by default. It is an async planner only: match physics,
damage, cooldowns and visibility remain authoritative server rules.

To try it locally, put the key in `appsettings.LlmAi.local.json` under
`GameThreeLlmAi:ApiKey`. This file is ignored by Git and overrides the default
settings. The environment variable named by
`GameThreeLlmAi:ApiKeyEnvironmentVariable` (default: `OPENAI_API_KEY`) remains
the preferred deployment option. The endpoint and model are configuration
values, so they can be changed without editing match code.

The observation is deliberately bounded: a 9x9 local map, nearby bombs,
pickups, markers and clones, visible opponents, the ball's combat state and
round time. The system prompt explains the nine character skills and the win
condition. It never receives invisible opponents or unrestricted match state.

Set `GameThreeLlmAi:DiagnosticsEnabled` to `true` in the local file to print
request, HTTP status, timeout, parsing and accepted-decision events. It never
prints API keys, request bodies or response bodies.

The adapter falls back to Utility AI when a request times out, fails, or returns
invalid JSON. Remote LLM control is not suitable for the 150ms tactical loop;
leave it disabled for normal matches. To remove only this experiment, delete
`GameThreeLlmAi.cs` and remove its bootstrap call in `Hosting/Program.cs`, its
provider arguments in `GameModeContracts.cs`, and the LLM fields in
`GameThreeSession.cs`.
