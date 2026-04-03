# Chat Room

Real-time chat room using Orleans observers for push notifications. Demonstrates the `FSharpObserverManager` for managing subscriber lifecycles with automatic expiry of stale connections.

## How to run

```bash
dotnet run --project src/Silo
```

## Expected output

```
--- Chat Room: 2 subscribers connected ---

Alice: Hey everyone!
  [Bob sees] Alice: Hey everyone!
Bob: Hi Alice, how's it going?
  [Alice sees] Bob: Hi Alice, how's it going?
Alice: Great! Just trying out Orleans.FSharp observers.
  [Bob sees] Alice: Great! Just trying out Orleans.FSharp observers.
Bob: That's awesome, the DX is really clean.
  [Alice sees] Bob: That's awesome, the DX is really clean.

Bob left the chat.
Alice: Anyone still here?

Done. Shutting down...
```

## Key concepts

- **`FSharpObserverManager<T>`** manages observer subscriptions with auto-expiry
- **`Observer.createRef` / `Observer.deleteRef`** create and clean up observer references
- **C# CodeGen grain** implements `IChatGrain` with observer notify pattern
- **`useJsonFallbackSerialization`** enables clean F# types without serialization attributes

## Documentation

See the [Orleans.FSharp README](../../README.md) for full documentation.
