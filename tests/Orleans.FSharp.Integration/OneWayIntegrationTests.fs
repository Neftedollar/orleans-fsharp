/// <summary>
/// Integration tests for the dedicated <c>[OneWay]</c> grain method that backs
/// <c>FSharpGrain.post</c> / <c>postGuid</c> / <c>postInt</c>.
///
/// The universal-grain pattern exposes a single <c>Task&lt;object&gt; HandleMessage(object)</c>
/// per interface, so <c>[OneWay]</c> cannot annotate it (that would make every call
/// fire-and-forget). Instead each universal interface declares a SECOND, dedicated
/// <c>[OneWay] Task HandleMessageOneWay(object)</c>, and <c>post</c> routes through it.
///
/// These tests verify two things:
/// <list type="number">
///   <item><description>The interface shape: each interface declares
///     <c>HandleMessageOneWay</c> returning a non-generic <c>Task</c> and carrying
///     <c>Orleans.Concurrency.OneWayAttribute</c> (so the generated proxy sends the
///     message fire-and-forget with no response marshalled).</description></item>
///   <item><description>End-to-end behaviour: a one-way <c>post</c> is delivered and
///     MUTATES grain state, observed deterministically via a follow-up two-way
///     <c>send</c>. A one-way call that silently no-opped would fail this.</description></item>
/// </list>
/// </summary>
module Orleans.FSharp.Integration.OneWayIntegrationTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.FSharp

// ── Interface-shape tests (no cluster needed — pure reflection) ───────────────

let private assertOneWayShape (t: Type) =
    let m = t.GetMethod("HandleMessageOneWay")
    // The dedicated one-way method must exist alongside HandleMessage.
    test <@ m <> null @>
    test <@ m.GetParameters().Length = 1 @>
    test <@ m.GetParameters().[0].ParameterType = typeof<obj> @>
    // True one-way: returns a non-generic Task — Orleans forbids Task<T> for [OneWay].
    test <@ m.ReturnType = typeof<Task> @>
    // Carries Orleans' [OneWay] so the proxy fires-and-forgets (no response message).
    test <@ m.IsDefined(typeof<Orleans.Concurrency.OneWayAttribute>, false) @>

[<Fact>]
let ``IFSharpGrain declares [OneWay] HandleMessageOneWay returning non-generic Task`` () =
    assertOneWayShape typeof<IFSharpGrain>

[<Fact>]
let ``IFSharpGrainWithGuidKey declares [OneWay] HandleMessageOneWay returning non-generic Task`` () =
    assertOneWayShape typeof<IFSharpGrainWithGuidKey>

[<Fact>]
let ``IFSharpGrainWithIntKey declares [OneWay] HandleMessageOneWay returning non-generic Task`` () =
    assertOneWayShape typeof<IFSharpGrainWithIntKey>

// ── End-to-end behaviour tests (real in-memory cluster via ClusterFixture) ────

[<Collection("ClusterCollection")>]
type OneWayBehaviorTests(fixture: ClusterFixture) =

    [<Fact>]
    member _.``post (string key) is one-way and mutates grain state`` () =
        task {
            let handle =
                FSharpGrain.ref<PingState, PingCommand> fixture.GrainFactory ("oneway-str-" + string (Guid.NewGuid()))

            // True one-way: returns a plain Task once the message is sent — no result marshalled.
            let fire: Task = handle |> FSharpGrain.post Ping
            do! fire

            // Observe the mutation deterministically via a follow-up two-way call.
            let! state = Eventually.until (fun s -> s.Count >= 1) (fun () -> handle |> FSharpGrain.send GetCount)
            test <@ state.Count = 1 @>
        }

    [<Fact>]
    member _.``postGuid is one-way and mutates grain state`` () =
        task {
            let handle =
                FSharpGrain.refGuid<PingState, PingCommand> fixture.GrainFactory (Guid.NewGuid())

            do! (handle |> FSharpGrain.postGuid Ping)

            let! state = Eventually.until (fun s -> s.Count >= 1) (fun () -> handle |> FSharpGrain.sendGuid GetCount)
            test <@ state.Count = 1 @>
        }

    [<Fact>]
    member _.``postInt is one-way and mutates grain state`` () =
        task {
            let key = 900_000L + int64 (Random.Shared.Next(1_000_000))

            let handle =
                FSharpGrain.refInt<PingState, PingCommand> fixture.GrainFactory key

            do! (handle |> FSharpGrain.postInt Ping)

            let! state = Eventually.until (fun s -> s.Count >= 1) (fun () -> handle |> FSharpGrain.sendInt GetCount)
            test <@ state.Count = 1 @>
        }

    [<Fact>]
    member _.``post one-way path delivers multiple messages and accumulates state`` () =
        task {
            let handle =
                FSharpGrain.ref<PingState, PingCommand> fixture.GrainFactory ("oneway-multi-" + string (Guid.NewGuid()))

            do! (handle |> FSharpGrain.post Ping)
            do! (handle |> FSharpGrain.post Ping)
            do! (handle |> FSharpGrain.post Ping)

            let! state = Eventually.until (fun s -> s.Count >= 3) (fun () -> handle |> FSharpGrain.send GetCount)
            test <@ state.Count = 3 @>
        }
