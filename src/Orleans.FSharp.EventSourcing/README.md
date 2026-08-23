# Orleans.FSharp.EventSourcing — Legacy compatibility package

This package contains the original `eventSourcedGrain { }` authoring model. It remains available
for existing applications, but new code should use `journaledGrainFor` from `Orleans.FSharp` and
host the resulting definition with `AddFunctionalJournaledGrain` from `Orleans.FSharp.Runtime`.

The current API supports typed API records, a pure `apply` fold, Orleans LogStorage/StateStorage,
typed `IFunctionalJournalStorage<'Key,'State,'Event>`, per-grain and global snapshot policies, and
the same functional references and C# facade as ordinary functional grains.

See the current [Event Sourcing guide](https://github.com/Neftedollar/orleans-fsharp/blob/main/docs/event-sourcing.md).

## Legacy API

Maintenance documentation for `eventSourcedGrain { }`, `SnapshotStrategy`, `EventStore`, and the
CodeGen-based custom-storage bridge is isolated in
[Legacy Event Sourcing](https://github.com/Neftedollar/orleans-fsharp/blob/main/docs/legacy/event-sourcing.md)
and the [Legacy API reference](https://github.com/Neftedollar/orleans-fsharp/blob/main/docs/legacy/api-reference.md).

## Dependencies

- `Microsoft.Orleans.EventSourcing`
- `Orleans.FSharp`
- `Orleans.FSharp.Runtime`

## License

MIT
