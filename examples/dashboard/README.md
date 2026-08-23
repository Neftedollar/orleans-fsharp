# Orleans Dashboard with functional F# actors

This runnable ASP.NET Core silo registers two current functional actors with explicit grain types:
`dashboard.counter` and `dashboard.cart`.

```bash
dotnet run --project examples/dashboard/Dashboard.fsproj
```

Open `http://127.0.0.1:5080/exercise` a few times to create calls, then open
`http://127.0.0.1:5080/dashboard/`.

On the Grains page, the actors appear as
`FunctionalGrainMarker<Dashboard+CounterActor>` and
`FunctionalGrainMarker<Dashboard+CartActor>`. See
`docs/dashboard.md` for the verified screenshot and the naming explanation.

For an automated smoke check of the silo, functional activations, and Dashboard HTTP API:

```bash
dotnet run --project examples/dashboard/Dashboard.fsproj -- --verify-silo
```

The dashboard package is an Orleans 10 preview feature. Protect the mapped endpoint with
authorization before exposing it outside a development environment.
