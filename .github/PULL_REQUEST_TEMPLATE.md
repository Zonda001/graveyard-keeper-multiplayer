<!-- Thanks for contributing! Keep PRs focused on one thing. -->

## What does this PR do?


## How was it tested?
<!-- Sync behavior CANNOT be verified single-player. Ideally a live 2-player
     session: say who did what, and what the logs showed on both sides. -->


## Protocol
- [ ] No packet wire-format change
- [ ] Wire format changed **and** I bumped `PROTOCOL_VERSION`

## Checklist
- [ ] Builds locally (`dotnet build Multiplayer/Multiplayer.csproj -c Release`)
- [ ] Any new developer diagnostics are gated behind `Multiplayer.DebugMode`
- [ ] Matches the surrounding code style
