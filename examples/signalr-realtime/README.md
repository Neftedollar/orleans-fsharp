# SignalR Realtime

Orleans grains pushing real-time metrics to a browser dashboard via SignalR. A dashboard grain generates random system metrics every 2 seconds using a grain timer, and a SignalR hub forwards them to all connected browsers.

## How to run

```bash
dotnet run --project src/Web
```

Then open http://localhost:5000 in your browser to see live metrics updating every 2 seconds.

## Expected output (console)

```
--- SignalR Realtime: Dashboard grain timer started ---
Open http://localhost:5000 in your browser to see live metrics.
Press Ctrl+C to stop.
```

The browser will show a dark-themed dashboard with four live metric cards:
- **CPU** usage percentage
- **Memory** usage percentage
- **Requests per second**
- **Latency** in milliseconds

Values update every 2 seconds with a sequence counter.

## Key concepts

- **Grain timer** periodic metric generation via `RegisterGrainTimer` (fires every 2 seconds)
- **SignalR hub** receives grain updates and broadcasts to connected browsers
- **Co-hosted** Orleans silo + ASP.NET Core + SignalR in the same process
- **`IHubContext<T>`** used from within the grain to push data to SignalR clients
- **`grain {}`** computation expression for the dashboard grain state management
- **wwwroot/index.html** minimal HTML + JS using `@microsoft/signalr` client library
- **`useJsonFallbackSerialization`** enables clean F# record serialization

## Architecture

```
Browser (SignalR JS client)
    |
    v
ASP.NET Core (SignalR Hub)
    |
    v
Orleans Grain (timer generates metrics)
    |
    v
IHubContext pushes to all connected clients
```

## Documentation

See the [Orleans.FSharp README](../../README.md) for full documentation.
