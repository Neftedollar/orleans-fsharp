# Calling from C#

**A functional grain, called from C# through an interface you write yourself.**

A C# project can already call a functional grain without any of this: the bound API record is an
ordinary F# record of function values, so `RoomApiModule.@ref.Invoke(factory).Invoke("general")`
followed by `await api.say.Invoke(...)` compiles and runs. It is also not C# anyone wants to write.
`FunctionalGrainInterop.For<TFacade>` replaces it with a normal interface call.

## What you'll learn

- Which assemblies a C# consumer references, and what FSharp.Core actually requires
- How to declare the facade interface and bind it
- How each member maps to an operation, and how to override the mapping
- How to read `Result`, `Option`, and list replies from C#
- Everything a facade rejects, and why each rejection happens when the facade is created
- What the interop path costs

---

## Overview

```csharp
using Microsoft.FSharp.Core;
using Orleans.FSharp;

public interface IChatRoom
{
    Task Join(string user);
    Task<FSharpResult<int, ChatError>> Say(string sender, string message);
    Task<int> MemberCount();
}

var room = FunctionalGrainInterop.For<IChatRoom>(RoomApiModule.contract, factory, "general");

await room.Join("Alice");
var posted = await room.Say("Alice", "Hey everyone!");
```

Nothing generates `IChatRoom`. You write the members you want, and `For` checks every one of them
against the contract before it returns: name mapping, argument shape, reply shape, and the member
shapes a facade cannot dispatch. A mistake is an exception with the member's name in it at the
`For` call — never a failure on the first call, and never a silent mismatch.

A runnable end-to-end version of exactly this lives in
[`examples/chat-room/src/Interop`](https://github.com/Neftedollar/orleans-fsharp/tree/main/examples/chat-room/src/Interop): a C# console project that
hosts the F# chat room and drives it through the facade.

---

## References

A C# consumer needs two package references, and a third if it also hosts the silo:

| Package | Why |
| --- | --- |
| `Orleans.FSharp` | `FunctionalGrainInterop`, `[FunctionalOperation]`, and the contract type |
| The assembly declaring the contract | `RoomApiModule.contract` — your own F# project, or a package built from one |
| `Orleans.FSharp.Runtime` | only if this process hosts the grain (`AddFunctionalGrain`) |

### FSharp.Core

`Orleans.FSharp` declares `FSharp.Core` as a package dependency (currently `>= 10.1.201`, the
version it is compiled against), so **a C# project that references nothing else gets FSharp.Core
automatically and needs no explicit reference of its own.** Verified both ways: through a
`ProjectReference` to an F# project and through the packed `Orleans.FSharp` nuspec.

What does matter is the other direction. A *direct* `PackageReference` always wins over a
transitive one, including when it is lower, so a C# project that already references FSharp.Core
must reference at least the version `Orleans.FSharp` was built against. A lower one is a package
downgrade:

```
error NU1605: Detected package downgrade: FSharp.Core from 10.1.201 to 9.0.303.
  Consumer -> Orleans.FSharp -> FSharp.Core (>= 10.1.201)
  Consumer -> FSharp.Core (>= 9.0.303)
```

NU1605 is a warning by default and an error under `TreatWarningsAsErrors`. Naming FSharp.Core
explicitly is still worth doing when the consumer wants a direct dependency it controls — that is
the only way to *raise* the version.

### The `Module` suffix

An F# module whose name collides with a type in the same namespace carries a `Module` suffix in
its CLR name, and the CLR name is what C# sees:

```fsharp
type RoomApi = { join: string -> Task<unit>; ... }   // the record

[<RequireQualifiedAccess>]
module RoomApi =                                      // compiled as RoomApiModule
    let contract = grainContract<RoomActor, string, RoomApi> { ... }
```

```csharp
RoomApiModule.contract   // not RoomApi.contract
```

Nothing else about the contract changes. If you own the F# side and would rather not see this,
give the module a name the record does not use.

---

## Hosting from C#

The silo side is the ordinary Orleans builder plus one call. `AddFunctionalGrain` registers the
definition, the fixed functional transport, and the F# serialization the contract's argument and
reply types need:

```csharp
var builder = Host.CreateApplicationBuilder();

builder.UseOrleans(silo =>
    silo.UseLocalhostClustering()
        .AddMemoryGrainStorage("Default")
        .AddFunctionalGrain(RoomFunctionalDef.room));

var host = builder.Build();
await host.StartAsync();

var factory = host.Services.GetRequiredService<IGrainFactory>();
```

A client process that only *calls* functional grains uses `AddFunctionalGrainClient` on the client
builder instead, and needs no definition at all.

---

## Binding a facade

```csharp
var room = FunctionalGrainInterop.For<IChatRoom>(RoomApiModule.contract, factory, "general");
```

The three arguments are the contract, the grain factory of this client or activation, and the
domain key. `For` returns a fresh proxy on every call, exactly like `factory.GetGrain` — hold on to
it for as long as you would hold a grain reference.

The key is typed as `object` on purpose. C# has no partial type-argument inference: naming the
facade type explicitly (`For<IChatRoom>`) would otherwise force the caller to name the contract's
three type parameters too, which nobody wants to write. Instead the key is checked against the
contract's key type at the `For` call:

```
Orleans.FSharp functional interop: a facade over grain type 'chat-room.room.functional' requires
a domain key of type 'System.String', but a 'System.Int32' was supplied.
```

---

## How members map to operations

### By name, case-insensitively

A member matches an operation whose ID differs only by case, which is all a PascalCase C# member
needs to reach a camelCase F# record field:

| C# member | F# operation |
| --- | --- |
| `MemberCount()` | `memberCount: unit -> Task<int>` |
| `Join(string user)` | `join: string -> Task<unit>` |

The comparison is against the **operation ID**, which is the record field name unless the contract
overrode it with `operationId`.

### By attribute, exactly

`[FunctionalOperation("...")]` names the operation directly and wins over the name match:

```csharp
[FunctionalOperation("say")]
Task<FSharpResult<int, ChatError>> Post(string sender, string message);
```

The override is matched **exactly** (ordinal), unlike the default name match. That is deliberate:
the attribute is the way to disambiguate a contract whose operation IDs differ only by case, and a
case-folding override could not name either of them. `[FunctionalOperation("JOIN")]` against an
operation called `join` is rejected, and says so.

### Ambiguity

If two operations match one member case-insensitively, the facade refuses to guess:

```
Orleans.FSharp functional interop: member 'SAY' of facade interface 'IAmbiguousFacade' matches
2 operations of grain type 'interop.ambiguous' case-insensitively -- 'say', 'Say'. Name the
intended one with [FunctionalOperation("...")], which is matched exactly.
```

### Partial facades

**Operations no member maps to are left alone.** A facade over three of a contract's eight
operations is supported and common — bind a narrow interface for the caller that only reads, and a
wider one for the caller that writes. The reverse is not allowed: every *member* must map to an
operation, and one that does not is rejected with the candidates listed.

Two members may map to the same operation, which is how an alias is written:

```csharp
Task Join(string user);

[FunctionalOperation("join")]
Task Enter(string user);
```

C# *overloads* are not usable, though. Both overloads map to the same operation by name, and at
most one of them can match its argument shape, so the other is rejected.

---

## Argument shapes

An operation takes exactly one argument. Three member shapes express that:

| Operation argument | Member |
| --- | --- |
| `unit` | no parameters — `Task<int> MemberCount()` |
| a single type `T` | one parameter of type `T` — `Task Join(string user)` |
| a tuple `T1 * T2 * …` | that many parameters, in order — `Task Say(string sender, string message)` |

A tuple argument may also be taken as **one** parameter of the tuple type
(`Task Say(Tuple<string, string> post)`) when that reads better. Both forms are exact: a parameter
whose type is not the argument type, or a parameter count that is neither 1 nor the tuple's arity,
is rejected and the diagnostic names both sides:

```
Orleans.FSharp functional interop: member 'Say' of facade interface 'ITupleElementFacade' declares
('System.String', 'System.Int32'), but operation 'say' of grain type 'interop.room' takes either
one parameter of type 'System.Tuple`2[...]' or 2 parameters ('System.String', 'System.String').
```

Cooperative cancellation is not part of the facade surface: a trailing `CancellationToken`
parameter is simply an extra parameter and is rejected as one. A caller that needs remote
cancellation uses `FunctionalGrainRef.callCancellable` from F#.

---

## Reply shapes

A member returns `Task<TReply>`, where `TReply` is the operation's exact reply type. For a
`Task<unit>` operation, the member may return either `Task` — which is what a C# author writes — or
`Task<Unit>`. Both are accepted; `Task` is the idiomatic one, and `Task<Unit>` exists for generic
code that needs a value.

`void`, `ValueTask`, a bare `T`, and a `Task<T>` whose `T` is not the reply type are all rejected.

### Streaming operations

An operation whose API field returns `IAsyncEnumerable<'Item>` maps to a member returning the BCL
`IAsyncEnumerable<TItem>` — no `Task` wrapper, and nothing of this library's own in the signature:

```fsharp
// F#
type FeedApi =
    { post: string -> Task<int>
      tail: int -> IAsyncEnumerable<Entry> }
```

```csharp
// C#
public interface IFeed
{
    Task<int> Post(string text);
    IAsyncEnumerable<Entry> Tail(int count);
}

var feed = FunctionalGrainInterop.For<IFeed>(FeedApiModule.contract, client, "general");

await foreach (var entry in feed.Tail(20))
{
    Console.WriteLine(entry.text);
}
```

`await foreach` disposes the enumerator when the loop ends or is broken out of, and that disposal
travels to the target: it cancels the producer and runs its `finally` blocks. See
[Server-Streaming Replies](streaming-replies.md).

A `Task<IAsyncEnumerable<TItem>>` return is rejected, and so is `IAsyncEnumerable<TItem>` on a
member bound to a non-streaming operation — the diagnostic names the operation and the shape it
requires.

### Reading F# replies

The replies arrive as the F# types the grain returns. They are ordinary .NET types, and C# reads
them without conversion:

```csharp
// Result<int, ChatError>  ->  FSharpResult<int, ChatError>
var posted = await room.Say("Alice", "Hey everyone!");
if (posted.IsOk)
    Console.WriteLine($"message #{posted.ResultValue}");
else if (posted.ErrorValue.IsNotAMember)
    Console.WriteLine("not a member");

// (string * string * DateTimeOffset) list  ->  FSharpList<Tuple<string, string, DateTimeOffset>>
foreach (var (sender, message, at) in await room.History(10))
    Console.WriteLine($"[{at:HH:mm:ss}] {sender}: {message}");

// int option  ->  FSharpOption<int>   (illustrative: this contract has no option reply)
FSharpOption<int> maybe = await other.LastSeen("Alice");
if (FSharpOption<int>.get_IsSome(maybe)) Console.WriteLine(maybe.Value);
```

Every case of an F# discriminated union is reachable from C#: a nullary case such as `NotAMember`
is both `ChatError.NotAMember` (a static property returning the singleton) and `error.IsNotAMember`
(an instance test), and a case with fields exposes them as properties. `error.Tag` gives the case
index when a `switch` reads better than a chain of `Is…` tests. `FSharpList<T>` implements
`IEnumerable<T>`, so `foreach` and LINQ work on it directly, and `System.Tuple` deconstructs. An
`FSharpOption<T>` is `null` when it is `None`, so `maybe is null` is equivalent to the
`get_IsSome` test above — prefer whichever reads better, but do not call `.Value` without one.

If a reply type is awkward to read from C#, the better fix is usually on the F# side — return a
type whose shape is BCL-friendly — rather than a wrapper on the C# side.

---

## What a facade rejects

Every rule below is checked when the facade is created, so none of them can surface on a call.
Each diagnostic names the member and the interface that declared it, and a member inherited from
an extended interface names that interface too.

| Rejected | Why |
| --- | --- |
| A `TFacade` that is not an interface | the facade is a runtime proxy, which can only implement an interface |
| A generic member | an operation has one exact argument type and one exact reply type |
| A `ref`, `out`, or `in` parameter | a grain call carries one serialized argument; nothing can be written back |
| A property | an operation is a call; a property read would hide one |
| An event | use a functional observer for push |
| A default interface method | a facade dispatches abstract members, so the implementation would be unreachable |
| A static member | a facade dispatches instance members |
| A member matching no operation | the diagnostic lists the contract's operations |
| A member matching two operations by case | name the intended one with the attribute |
| An override naming no operation | the override is exact, so a case-folded ID does not resolve |
| A wrong argument or reply shape | see the two sections above |
| A streaming operation not returning `IAsyncEnumerable<TItem>` | the item type is BCL so that `await foreach` needs no wrapper |
| A null contract, factory, or key | and a key whose type is not the contract's key type |

Inherited interfaces are included in all of this, which is worth knowing before adding one:
`interface IChatRoom : IDisposable` is rejected, because `Dispose` returns `void`.

A facade interface does **not** have to be public. `DispatchProxy` emits the access-check
suppression a non-public interface needs, so an `internal` facade works and is a reasonable choice
for a console app or a single assembly.

---

## What the interop path costs

Everything type-related happens once, when the facade is created: the contract's operations are
resolved, the tuple packer is precomputed, and one invoker per member is closed over the
operation's exact argument and reply types. A call then performs one dictionary lookup, one
delegate call, and the same preclosed closure an F# caller reaches through the API record — no
reflection, no selector evaluation, and no generic closing. That is asserted by the test suite
against the runtime's own instrumentation counters, and by a counterweight test proving those
counters are non-zero while the facade is *created*.

What the interop path does add is `DispatchProxy`'s own per-call dispatch, and it is real: the
runtime-generated proxy boxes each argument into an `object[]` and returns the reply as `object`.
That is the price of writing the interface by hand instead of generating code, and it is paid once
per call on top of the grain call itself — small next to a remote call, not free next to a local
one. F# callers keep the direct route through the bound API record and pay none of it.

---

## What a facade does NOT change

A facade is not a second transport. `FunctionalGrainInterop.For` binds the contract exactly as an
F# caller does and installs, per interface member, the **same preclosed API-record field
closure** — so a facade call produces the same envelope: same grain type, same contract version,
same stable operation ID, same protocol token, same admission flags. Four consequences are worth
naming, because every feature they touch is decided on the target side from that envelope:

- **Interleaving.** Whether a call may enter a busy activation is decided by Orleans from the
  message, using the `reentrant` property or the `mayInterleave` predicate the contract declared.
  A facade call gets exactly the decision an F# call to the same operation gets — it can neither
  bypass the predicate nor be evaluated against it twice.
- **Version admission.** A facade cannot claim a version of its own: the contract version travels
  with the contract the facade was bound from. Bind the v3 contract through a facade and you send
  v3 requests and get the v3 admission decisions, `sinceVersion` refusals included.
- **Transactions.** The operation's `transactional` policy travels in the same admission byte, and
  the call site chooses the transactional invokable from it — so a facade member bound to a
  transactional operation joins or creates the transaction exactly as the F# API-record field does.
  A C# caller can drive a whole transfer, commit and abort, through facades alone
  (`tests/Orleans.FSharp.Integration/FunctionalPhaseDIntegrationTests.fs`, "the C# facade drives a
  transaction end to end").
- **The definition kind.** A facade is built from the **contract**, and a journaled definition
  (`journaledGrainFor`) shares the contract layer with an ordinary one — so a facade over a
  journaled grain names no journal concept at all and behaves identically. Depositing through a
  facade raises and confirms events exactly as an F# call does
  (`tests/Orleans.FSharp.Integration/FunctionalPhaseEIntegrationTests.fs`, "the C# facade over a
  journaled contract is transport-transparent"). See [Event Sourcing](event-sourcing.md).

See [Functional Grain Runtime](functional-grains.md), "Reentrancy", "Version tolerance", and
"Distributed ACID transactions", for what those decisions are.

---

## Not yet covered

**Functional observers.** A C# process can already be pushed to — the observer handle is an
ordinary operation argument — but there is no facade over `FunctionalObserverHandle` yet, so the
handler side is still an F# record. An observer facade is a possible future addition.

---

## Related

- [Functional Grain Runtime](functional-grains.md) — the contract and definition this facade binds
- [Event Sourcing](event-sourcing.md) — a facade over a journaled contract
- [Serialization](serialization.md) — which F# types cross the wire and how
- [Server-Streaming Replies](streaming-replies.md) — `await foreach` over a facade member
- [Streaming](streaming.md) — observers, streams, and broadcast channels
