# Examples

**Runnable projects, organized by the question they answer.**

Every project below runs the current functional runtime. Several also retain a clearly marked
Legacy twin for migration comparison; the executable entry point uses the current API unless its
README says otherwise. Run commands are shown from the repository root.

| Example | What it proves | Run |
|---|---|---|
| [Hello World](https://github.com/Neftedollar/orleans-fsharp/tree/main/examples/hello-world) | Smallest `grainContract` + `grainFor` host | `dotnet run --project examples/hello-world/src/Silo` |
| [Chat Room](https://github.com/Neftedollar/orleans-fsharp/tree/main/examples/chat-room) | Persistent state, functional observers, one-way calls, and the same actor called from C# | `dotnet run --project examples/chat-room/src/Silo` |
| [Bank Account](https://github.com/Neftedollar/orleans-fsharp/tree/main/examples/bank-account) | `journaledGrainFor`, event replay, and provider behavior | `dotnet run --project examples/bank-account/src/Silo` |
| [Bank Transactions](https://github.com/Neftedollar/orleans-fsharp/tree/main/examples/bank-transactions) | Distributed Orleans transactions from functional handlers | `dotnet run --project examples/bank-transactions/src/Silo` |
| [Feature Tour](https://github.com/Neftedollar/orleans-fsharp/tree/main/examples/feature-tour) | Live status matrix for lifecycle, streams, observers, placement, migration, transactions, and event sourcing | `dotnet run --project examples/feature-tour/src/FeatureTour` |
| [Orleans Dashboard](https://github.com/Neftedollar/orleans-fsharp/tree/main/examples/dashboard) | How functional actor activations appear in Dashboard | `dotnet run --project examples/dashboard/Dashboard.fsproj` |
| [Order Processing](https://github.com/Neftedollar/orleans-fsharp/tree/main/examples/order-processing) | Domain DUs, typed failures, and persistent workflow state | `dotnet run --project examples/order-processing/src/Silo` |
| [Typesafe IDs](https://github.com/Neftedollar/orleans-fsharp/tree/main/examples/typesafe-ids) | Tagged keys and closed command/state models | `dotnet run --project examples/typesafe-ids/src/Silo` |
| [SignalR Realtime](https://github.com/Neftedollar/orleans-fsharp/tree/main/examples/signalr-realtime) | Functional actors behind an ASP.NET Core/SignalR boundary | `dotnet run --project examples/signalr-realtime/src/Web` |
| [Fable Fullstack](https://github.com/Neftedollar/orleans-fsharp/tree/main/examples/fable-fullstack) | Functional actors behind a server API prepared for an F# client | `dotnet run --project examples/fable-fullstack/src/Server` |

## Which example should I read first?

- New to the library: **Hello World**, then **Chat Room**.
- Evaluating Orleans feature coverage: **Feature Tour**.
- Building an event-sourced service: **Bank Account**.
- Calling from C#: run `examples/chat-room/src/Interop`.
- Checking production observability: **Orleans Dashboard**.
- Looking for domain-oriented F#: **Order Processing** and **Typesafe IDs**.

## Scripts and compact samples

The `samples/` directory contains focused patterns rather than full applications. In particular,
`samples/quickstart-functional.fsx` demonstrates an in-process silo from F# Interactive. Use the
full examples when behavior depends on process boundaries, providers, rolling updates, or hosting.

## Build every example

CI builds every example solution. Locally, build the one you plan to copy first:

```bash
dotnet build examples/chat-room/ChatRoom.sln
dotnet run --project examples/chat-room/src/Silo
```

For the complete functional surface and known limitations, use the
[Functional Runtime Reference](functional-grains.md) and [Orleans compatibility](compatibility.md).
