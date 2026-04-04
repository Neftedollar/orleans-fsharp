module Orleans.FSharp.Integration.GrainLifecycleTests

open Xunit
open Swensen.Unquote
open Orleans.FSharp
open Orleans.FSharp.Sample

[<Collection("ClusterCollection")>]
type GrainLifecycleTests(fixture: ClusterFixture) =

    [<Fact>]
    member _.``Counter grain starts at zero`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<ICounterGrain>(100L)
            let! result = grain.HandleMessage(GetValue)
            let value = unbox<int> result
            test <@ value = 0 @>
        }

    [<Fact>]
    member _.``Counter grain increments correctly`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<ICounterGrain>(101L)
            let! _ = grain.HandleMessage(Increment)
            let! result = grain.HandleMessage(GetValue)
            let value = unbox<int> result
            test <@ value = 1 @>
        }

    [<Fact>]
    member _.``Counter grain increments multiple times`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<ICounterGrain>(102L)
            let! _ = grain.HandleMessage(Increment)
            let! _ = grain.HandleMessage(Increment)
            let! _ = grain.HandleMessage(Increment)
            let! result = grain.HandleMessage(GetValue)
            let value = unbox<int> result
            test <@ value = 3 @>
        }

    [<Fact>]
    member _.``Counter grain decrements correctly`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<ICounterGrain>(103L)
            let! _ = grain.HandleMessage(Increment)
            let! _ = grain.HandleMessage(Increment)
            let! _ = grain.HandleMessage(Decrement)
            let! result = grain.HandleMessage(GetValue)
            let value = unbox<int> result
            test <@ value = 1 @>
        }

    [<Fact>]
    member _.``Counter grain decrement at zero stays zero`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<ICounterGrain>(104L)
            let! _ = grain.HandleMessage(Decrement)
            let! result = grain.HandleMessage(GetValue)
            let value = unbox<int> result
            test <@ value = 0 @>
        }

    [<Fact>]
    member _.``Counter grain increment returns new value`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<ICounterGrain>(105L)
            let! result = grain.HandleMessage(Increment)
            let value = unbox<int> result
            test <@ value = 1 @>
        }

    [<Fact>]
    member _.``Counter grain state persists across calls`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<ICounterGrain>(106L)
            let! _ = grain.HandleMessage(Increment)
            let! _ = grain.HandleMessage(Increment)
            let! result = grain.HandleMessage(GetValue)
            let value = unbox<int> result
            test <@ value = 2 @>
        }

/// <summary>
/// Integration tests for type-safe grain references (GrainRef).
/// Tests grain-to-grain communication using GrainRef.ofString, ofInt64, and invoke.
/// </summary>
[<Collection("ClusterCollection")>]
type GrainRefIntegrationTests(fixture: ClusterFixture) =

    [<Fact>]
    member _.``GrainRef.ofString creates working echo grain reference`` () =
        task {
            let echoRef =
                GrainRef.ofString<IEchoGrain> fixture.GrainFactory "echo-test-1"

            let! result = GrainRef.invoke echoRef (fun g -> g.HandleMessage(Echo "hello"))
            let value = unbox<string> result
            test <@ value = "echo-test-1:hello" @>
        }

    [<Fact>]
    member _.``GrainRef.ofInt64 creates working counter grain reference`` () =
        task {
            let counterRef =
                GrainRef.ofInt64<ICounterGrain> fixture.GrainFactory 200L

            let! _ = GrainRef.invoke counterRef (fun g -> g.HandleMessage(Increment))
            let! result = GrainRef.invoke counterRef (fun g -> g.HandleMessage(GetValue))
            let value = unbox<int> result
            test <@ value = 1 @>
        }

    [<Fact>]
    member _.``GrainRef.key returns correct key`` () =
        let echoRef =
            GrainRef.ofString<IEchoGrain> fixture.GrainFactory "key-check"

        test <@ GrainRef.key echoRef = "key-check" @>

    [<Fact>]
    member _.``GrainRef.unwrap returns usable grain proxy`` () =
        task {
            let echoRef =
                GrainRef.ofString<IEchoGrain> fixture.GrainFactory "unwrap-test"

            let grain = GrainRef.unwrap echoRef
            let! result = grain.HandleMessage(Greet)
            let value = unbox<string> result
            test <@ value = "Hello from unwrap-test!" @>
        }

    [<Fact>]
    member _.``Two grains communicate via GrainRef - echo then counter`` () =
        task {
            // Create echo grain reference
            let echoRef =
                GrainRef.ofString<IEchoGrain> fixture.GrainFactory "comm-test"

            let! echoResult = GrainRef.invoke echoRef (fun g -> g.HandleMessage(Echo "ping"))
            let echoValue = unbox<string> echoResult
            test <@ echoValue = "comm-test:ping" @>

            // Create counter grain reference and verify it works independently
            let counterRef =
                GrainRef.ofInt64<ICounterGrain> fixture.GrainFactory 300L

            let! _ = GrainRef.invoke counterRef (fun g -> g.HandleMessage(Increment))
            let! _ = GrainRef.invoke counterRef (fun g -> g.HandleMessage(Increment))
            let! counterResult = GrainRef.invoke counterRef (fun g -> g.HandleMessage(GetValue))
            let counterValue = unbox<int> counterResult
            test <@ counterValue = 2 @>
        }

/// <summary>
/// Integration tests for <c>GrainContext.deactivateOnIdle</c> in the universal grain pattern.
///
/// Verifies that the <c>IGrainBase</c> parameter added to <c>IUniversalGrainHandler.Handle</c>
/// is correctly forwarded so that <c>DeactivateOnIdle</c> calls inside grain handlers
/// reach the Orleans runtime without throwing "not available outside Orleans runtime".
/// </summary>
[<Collection("ClusterCollection")>]
type DeactivationControlIntegrationTests(fixture: ClusterFixture) =

    [<Fact>]
    member _.``deactivateOnIdle inside universal grain handler does not throw`` () =
        task {
            let grain =
                FSharpGrain.ref<DeactivationCtrlState, DeactivationCtrlCommand>
                    fixture.GrainFactory
                    (System.Guid.NewGuid().ToString("N"))

            // Process some state so the grain is active
            do! FSharpGrain.post (DeactivCtrlProcess 5) grain

            // This call internally invokes GrainContext.deactivateOnIdle ctx.
            // Before the IGrainBase wiring it would throw InvalidOperationException.
            let! state =
                FSharpGrain.send DeactivCtrlRequestDeactivation grain

            test <@ state.Processed = 5 @>
        }

    [<Fact>]
    member _.``grain continues to process commands before deactivation takes effect`` () =
        task {
            let grain =
                FSharpGrain.ref<DeactivationCtrlState, DeactivationCtrlCommand>
                    fixture.GrainFactory
                    (System.Guid.NewGuid().ToString("N"))

            do! FSharpGrain.post (DeactivCtrlProcess 3) grain
            do! FSharpGrain.post (DeactivCtrlProcess 7) grain

            let! before = FSharpGrain.send DeactivCtrlGetProcessed grain
            test <@ before.Processed = 10 @>

            // Request deactivation — grain should still respond to this call
            let! afterRequest = FSharpGrain.send DeactivCtrlRequestDeactivation grain
            test <@ afterRequest.Processed = 10 @>
        }

/// <summary>
/// Tests for duplicate grain registration detection.
/// </summary>
type DuplicateRegistrationTests() =

    [<Fact>]
    member _.``Duplicate grain registration throws descriptive error`` () =
        let registry = Orleans.FSharp.Runtime.SiloBuilderExtensions.GrainRegistry()
        registry.Register("test-key", typeof<int>)

        let ex =
            Assert.Throws<System.InvalidOperationException>(fun () ->
                registry.Register("test-key", typeof<string>))

        test <@ ex.Message.Contains("Duplicate grain registration") @>
        test <@ ex.Message.Contains("test-key") @>
        test <@ ex.Message.Contains("Int32") @>
        test <@ ex.Message.Contains("String") @>
