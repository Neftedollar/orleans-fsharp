/// <summary>
/// Integration tests for the universal IFSharpGrain pattern — using FSharpGrain.ref/send/post
/// (string key), FSharpGrain.refGuid/sendGuid/postGuid (GUID key), and
/// FSharpGrain.refInt/sendInt/postInt (integer key) instead of per-grain interfaces.
/// Verifies that grains registered via AddFSharpGrain can be called from a client
/// with no per-grain C# stubs and no CodeGen project reference.
/// </summary>
module Orleans.FSharp.Integration.UniversalGrainPatternTests

open System
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

    // ── Field-carrying DU cases ───────────────────────────────────────────────

    [<Fact>]
    member _.``Field-carrying DU case Append is dispatched correctly`` () =
        task {
            let handle = FSharpGrain.ref<TextState, TextCommand> fixture.GrainFactory "text-append-1"
            let! state = handle |> FSharpGrain.send (Append "hello")
            test <@ state.Text = "hello" @>
        }

    [<Fact>]
    member _.``Multiple Append calls accumulate text`` () =
        task {
            let handle = FSharpGrain.ref<TextState, TextCommand> fixture.GrainFactory "text-append-2"
            let! _ = handle |> FSharpGrain.send (Append "foo")
            let! _ = handle |> FSharpGrain.send (Append "bar")
            let! state = handle |> FSharpGrain.send GetText
            test <@ state.Text = "foobar" @>
        }

    [<Fact>]
    member _.``GetText returns state without modification`` () =
        task {
            let handle = FSharpGrain.ref<TextState, TextCommand> fixture.GrainFactory "text-append-3"
            let! _ = handle |> FSharpGrain.send (Append "x")
            let! s1 = handle |> FSharpGrain.send GetText
            let! s2 = handle |> FSharpGrain.send GetText
            test <@ s1.Text = s2.Text @>
        }

    // ── Two different grain types share the same FSharpGrainImpl class ────────

    [<Fact>]
    member _.``PingGrain and TextGrain coexist in the same silo`` () =
        task {
            let ping = FSharpGrain.ref<PingState, PingCommand> fixture.GrainFactory "coexist-ping"
            let text = FSharpGrain.ref<TextState, TextCommand> fixture.GrainFactory "coexist-text"

            let! _ = ping |> FSharpGrain.send Ping
            let! _ = text |> FSharpGrain.send (Append "world")

            let! ps = ping |> FSharpGrain.send GetCount
            let! ts = text |> FSharpGrain.send GetText

            test <@ ps.Count = 1 @>
            test <@ ts.Text = "world" @>
        }

    // ── GUID key variant (FSharpGrain.refGuid / sendGuid / postGuid) ──────────

    [<Fact>]
    member _.``FSharpGrain.refGuid creates a working grain handle`` () =
        task {
            let key = Guid.NewGuid()
            let handle = FSharpGrain.refGuid<PingState, PingCommand> fixture.GrainFactory key
            let! state = handle |> FSharpGrain.sendGuid Ping
            test <@ state.Count = 1 @>
        }

    [<Fact>]
    member _.``FSharpGrain.sendGuid returns updated state`` () =
        task {
            let key = Guid.NewGuid()
            let handle = FSharpGrain.refGuid<PingState, PingCommand> fixture.GrainFactory key
            let! _ = handle |> FSharpGrain.sendGuid Ping
            let! _ = handle |> FSharpGrain.sendGuid Ping
            let! state = handle |> FSharpGrain.sendGuid GetCount
            test <@ state.Count = 2 @>
        }

    [<Fact>]
    member _.``FSharpGrain.postGuid fires without waiting for result`` () =
        task {
            let key = Guid.NewGuid()
            let handle = FSharpGrain.refGuid<PingState, PingCommand> fixture.GrainFactory key
            do! handle |> FSharpGrain.postGuid Ping
            let! state = handle |> FSharpGrain.sendGuid GetCount
            test <@ state.Count >= 1 @>
        }

    [<Fact>]
    member _.``Same GUID key returns same virtual actor`` () =
        task {
            let key = Guid.NewGuid()
            let h1 = FSharpGrain.refGuid<PingState, PingCommand> fixture.GrainFactory key
            let h2 = FSharpGrain.refGuid<PingState, PingCommand> fixture.GrainFactory key
            let! _ = h1 |> FSharpGrain.sendGuid Ping
            let! state = h2 |> FSharpGrain.sendGuid GetCount
            test <@ state.Count = 1 @>
        }

    // ── Integer key variant (FSharpGrain.refInt / sendInt / postInt) ──────────

    [<Fact>]
    member _.``FSharpGrain.refInt creates a working grain handle`` () =
        task {
            let handle = FSharpGrain.refInt<PingState, PingCommand> fixture.GrainFactory 1001L
            let! state = handle |> FSharpGrain.sendInt Ping
            test <@ state.Count = 1 @>
        }

    [<Fact>]
    member _.``FSharpGrain.sendInt returns updated state`` () =
        task {
            let handle = FSharpGrain.refInt<PingState, PingCommand> fixture.GrainFactory 2001L
            let! _ = handle |> FSharpGrain.sendInt Ping
            let! _ = handle |> FSharpGrain.sendInt Ping
            let! state = handle |> FSharpGrain.sendInt GetCount
            test <@ state.Count = 2 @>
        }

    [<Fact>]
    member _.``FSharpGrain.postInt fires without waiting for result`` () =
        task {
            let handle = FSharpGrain.refInt<PingState, PingCommand> fixture.GrainFactory 3001L
            do! handle |> FSharpGrain.postInt Ping
            let! state = handle |> FSharpGrain.sendInt GetCount
            test <@ state.Count >= 1 @>
        }

    [<Fact>]
    member _.``Same integer key returns same virtual actor`` () =
        task {
            let h1 = FSharpGrain.refInt<PingState, PingCommand> fixture.GrainFactory 4001L
            let h2 = FSharpGrain.refInt<PingState, PingCommand> fixture.GrainFactory 4001L
            let! _ = h1 |> FSharpGrain.sendInt Ping
            let! state = h2 |> FSharpGrain.sendInt GetCount
            test <@ state.Count = 1 @>
        }

    [<Fact>]
    member _.``String, GUID, and int key grain instances are independent`` () =
        task {
            let key = Guid.NewGuid()
            let gs = FSharpGrain.ref<PingState, PingCommand> fixture.GrainFactory "key-type-iso"
            let gg = FSharpGrain.refGuid<PingState, PingCommand> fixture.GrainFactory key
            let gi = FSharpGrain.refInt<PingState, PingCommand> fixture.GrainFactory 9999L

            let! _ = gs |> FSharpGrain.send Ping
            let! _ = gs |> FSharpGrain.send Ping
            let! _ = gg |> FSharpGrain.sendGuid Ping

            let! ss = gs |> FSharpGrain.send GetCount
            let! sg = gg |> FSharpGrain.sendGuid GetCount
            let! si = gi |> FSharpGrain.sendInt GetCount

            test <@ ss.Count = 2 @>
            test <@ sg.Count = 1 @>
            test <@ si.Count = 0 @>
        }
