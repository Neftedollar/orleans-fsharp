/// <summary>
/// Integration tests for the universal IFSharpGrain pattern — using FSharpGrain.ref/send/post
/// instead of per-grain interfaces. Verifies that grains registered via AddFSharpGrain can be
/// called from a client with no per-grain C# stubs and no CodeGen project reference.
/// </summary>
module Orleans.FSharp.Integration.UniversalGrainPatternTests

open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.FSharp

[<Collection("ClusterCollection")>]
type UniversalPatternTests(fixture: ClusterFixture) =

    // ── FSharpGrain.ref round-trip ────────────────────────────────────────────

    [<Fact>]
    member _.``FSharpGrain.ref creates a working grain handle`` () =
        task {
            let handle = FSharpGrain.ref<PingState, PingCommand> fixture.GrainFactory "universal-ping-1"
            let! state = handle |> FSharpGrain.send Ping
            test <@ state.Count = 1 @>
        }

    [<Fact>]
    member _.``FSharpGrain.send returns updated state`` () =
        task {
            let handle = FSharpGrain.ref<PingState, PingCommand> fixture.GrainFactory "universal-ping-2"
            let! _ = handle |> FSharpGrain.send Ping
            let! _ = handle |> FSharpGrain.send Ping
            let! state = handle |> FSharpGrain.send Ping
            test <@ state.Count = 3 @>
        }

    [<Fact>]
    member _.``FSharpGrain.post fires without waiting for result`` () =
        task {
            let handle = FSharpGrain.ref<PingState, PingCommand> fixture.GrainFactory "universal-ping-3"
            do! handle |> FSharpGrain.post Ping
            // post returns Task (not Task<'State>) — just verify it completes
            let! state = handle |> FSharpGrain.send GetCount
            test <@ state.Count >= 1 @>
        }

    [<Fact>]
    member _.``Multiple grain instances are independent`` () =
        task {
            let g1 = FSharpGrain.ref<PingState, PingCommand> fixture.GrainFactory "universal-ping-ind-1"
            let g2 = FSharpGrain.ref<PingState, PingCommand> fixture.GrainFactory "universal-ping-ind-2"

            let! _ = g1 |> FSharpGrain.send Ping
            let! _ = g1 |> FSharpGrain.send Ping
            let! state1 = g1 |> FSharpGrain.send GetCount
            let! state2 = g2 |> FSharpGrain.send GetCount

            test <@ state1.Count = 2 @>
            test <@ state2.Count = 0 @>
        }

    // ── Pipeline operator ergonomics ─────────────────────────────────────────

    [<Fact>]
    member _.``Pipeline operator composes naturally`` () =
        task {
            let send cmd = FSharpGrain.send<PingState, PingCommand> cmd
            let handle = FSharpGrain.ref<PingState, PingCommand> fixture.GrainFactory "universal-pipe-1"

            let! state =
                task {
                    let! _ = handle |> send Ping
                    return! handle |> send GetCount
                }

            test <@ state.Count = 1 @>
        }

    // ── Concurrent calls ─────────────────────────────────────────────────────

    [<Fact>]
    member _.``Concurrent sends are processed correctly`` () =
        task {
            let handle = FSharpGrain.ref<PingState, PingCommand> fixture.GrainFactory "universal-concurrent-1"
            let tasks = Array.init 10 (fun _ -> handle |> FSharpGrain.post Ping)
            do! Task.WhenAll(tasks)
            let! state = handle |> FSharpGrain.send GetCount
            test <@ state.Count = 10 @>
        }

    // ── No-CodeGen verification ───────────────────────────────────────────────

    [<Fact>]
    member _.``IFSharpGrain is the underlying grain interface`` () =
        // FSharpGrain.ref wraps IFSharpGrain — verify no per-grain interface is used
        let handle = FSharpGrain.ref<PingState, PingCommand> fixture.GrainFactory "universal-iface-1"
        Assert.IsAssignableFrom<IFSharpGrain>(handle.Grain) |> ignore

    [<Fact>]
    member _.``Same grain key returns same virtual actor`` () =
        task {
            let h1 = FSharpGrain.ref<PingState, PingCommand> fixture.GrainFactory "universal-same-key"
            let h2 = FSharpGrain.ref<PingState, PingCommand> fixture.GrainFactory "universal-same-key"
            let! _ = h1 |> FSharpGrain.send Ping
            let! state = h2 |> FSharpGrain.send GetCount
            // Both references target the same virtual actor
            test <@ state.Count = 1 @>
        }
