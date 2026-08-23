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

For an automated smoke check of the silo, functional activations, and the Dashboard HTTP API:

```bash
dotnet run --project examples/dashboard/Dashboard.fsproj -- --verify-silo
```

The check activates both actors through `/exercise`, requests
`/dashboard/DashboardCounters`, and inspects the JSON rows in `simpleGrainStats`.
It succeeds only when the exact `FunctionalGrainMarker<...CounterActor>` and
`FunctionalGrainMarker<...CartActor>` rows are both present with `ActivationCount > 0`.
It does not substitute a direct `IManagementGrain` query for the HTTP API result.
Because Dashboard statistics update asynchronously, the check polls that endpoint for up
to 10 seconds rather than assuming the first successful HTTP response is already current.

CI can assert the three stable result lines:

```text
DASHBOARD_API_OK endpoint=/dashboard/DashboardCounters collection=simpleGrainStats
DASHBOARD_API_ACTIVATION actor=CounterActor ActivationCount=1 grainType=Orleans.FSharp.FunctionalGrainMarker<Orleans.FSharp.Examples.Dashboard+CounterActor>
DASHBOARD_API_ACTIVATION actor=CartActor ActivationCount=1 grainType=Orleans.FSharp.FunctionalGrainMarker<Orleans.FSharp.Examples.Dashboard+CartActor>
```

The count may be greater than `1`; CI should require a positive value rather than
matching the sample count literally.

The dashboard package is an Orleans 10 preview feature. Protect the mapped endpoint with
authorization before exposing it outside a development environment.
