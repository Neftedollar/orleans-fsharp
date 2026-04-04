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
/// Integration tests verifying that <c>GrainContext.primaryKeyString</c> returns the
/// correct value when called from inside a universal-pattern grain handler.
///
/// Before the <c>IGrainBase.GrainContext.GrainId</c> wiring, <c>GrainId</c> and
/// <c>PrimaryKey</c> were always <c>None</c> in the universal path, so
/// <c>primaryKeyString</c> would throw <c>InvalidOperationException</c>.
/// </summary>
[<Collection("ClusterCollection")>]
type GrainKeyIntegrationTests(fixture: ClusterFixture) =

    [<Fact>]
    member _.``primaryKeyString inside universal grain handler returns the grain key`` () =
        task {
            let keyValue = System.Guid.NewGuid().ToString("N")
            let grain =
                FSharpGrain.ref<GrainKeyState, GrainKeyCommand> fixture.GrainFactory keyValue

            let! returnedKey =
                FSharpGrain.ask<GrainKeyState, GrainKeyCommand, string> GetOwnPrimaryKey grain

            // The key returned from inside the handler must match the key we used to look up the grain.
            // Before IGrainBase wiring this would throw InvalidOperationException.
            test <@ returnedKey = keyValue @>
        }

    [<Fact>]
    member _.``two grains with different keys report different primaryKeyString values`` () =
        task {
            let key1 = System.Guid.NewGuid().ToString("N")
            let key2 = System.Guid.NewGuid().ToString("N")
            let g1 = FSharpGrain.ref<GrainKeyState, GrainKeyCommand> fixture.GrainFactory key1
            let g2 = FSharpGrain.ref<GrainKeyState, GrainKeyCommand> fixture.GrainFactory key2

            let! k1 = FSharpGrain.ask<GrainKeyState, GrainKeyCommand, string> GetOwnPrimaryKey g1
            let! k2 = FSharpGrain.ask<GrainKeyState, GrainKeyCommand, string> GetOwnPrimaryKey g2

            test <@ k1 = key1 @>
            test <@ k2 = key2 @>
            test <@ k1 <> k2 @>
        }

/// <summary>
/// Integration tests verifying that <c>GrainContext.primaryKeyGuid</c> returns the
/// correct value when called from inside a GUID-keyed universal grain handler
/// (<c>FSharpGrain.refGuid</c> / <c>FSharpGrainGuidImpl</c>).
///
/// Before the interface-aware key extraction fix, the primary key type for GUID-keyed
/// grains could be misclassified and <c>primaryKeyGuid</c> would throw.
/// </summary>
[<Collection("ClusterCollection")>]
type GrainGuidKeyIntegrationTests(fixture: ClusterFixture) =

    [<Fact>]
    member _.``primaryKeyGuid inside universal grain handler returns the grain guid key`` () =
        task {
            let guidKey = System.Guid.NewGuid()
            let grain =
                FSharpGrain.refGuid<GrainKeyState, GrainGuidKeyCommand> fixture.GrainFactory guidKey

            let! returnedKey =
                FSharpGrain.askGuid<GrainKeyState, GrainGuidKeyCommand, System.Guid> GetOwnPrimaryKeyGuid grain

            test <@ returnedKey = guidKey @>
        }

    [<Fact>]
    member _.``two grains with different guid keys report different primaryKeyGuid values`` () =
        task {
            let guid1 = System.Guid.NewGuid()
            let guid2 = System.Guid.NewGuid()
            let g1 = FSharpGrain.refGuid<GrainKeyState, GrainGuidKeyCommand> fixture.GrainFactory guid1
            let g2 = FSharpGrain.refGuid<GrainKeyState, GrainGuidKeyCommand> fixture.GrainFactory guid2

            let! k1 =
                FSharpGrain.askGuid<GrainKeyState, GrainGuidKeyCommand, System.Guid> GetOwnPrimaryKeyGuid g1

            let! k2 =
                FSharpGrain.askGuid<GrainKeyState, GrainGuidKeyCommand, System.Guid> GetOwnPrimaryKeyGuid g2

            test <@ k1 = guid1 @>
            test <@ k2 = guid2 @>
            test <@ k1 <> k2 @>
        }

/// <summary>
/// Integration tests verifying that <c>GrainContext.primaryKeyInt64</c> returns the
/// correct value when called from inside an integer-keyed universal grain handler
/// (<c>FSharpGrain.refInt</c> / <c>FSharpGrainIntImpl</c>).
/// </summary>
[<Collection("ClusterCollection")>]
type GrainIntKeyIntegrationTests(fixture: ClusterFixture) =

    [<Fact>]
    member _.``primaryKeyInt64 inside universal grain handler returns the grain int key`` () =
        task {
            // Use a key range (50000+) that does not overlap with UniversalGrainPatternTests
            // (which uses 1001L–9999L) to avoid shared FSharpGrainIntImpl instance conflicts.
            let intKey = 50001L
            let grain =
                FSharpGrain.refInt<GrainKeyState, GrainIntKeyCommand> fixture.GrainFactory intKey

            let! returnedKey =
                FSharpGrain.askInt<GrainKeyState, GrainIntKeyCommand, int64> GetOwnPrimaryKeyInt64 grain

            test <@ returnedKey = intKey @>
        }

    [<Fact>]
    member _.``two grains with different int keys report different primaryKeyInt64 values`` () =
        task {
            let key1 = 50002L
            let key2 = 50003L
            let g1 = FSharpGrain.refInt<GrainKeyState, GrainIntKeyCommand> fixture.GrainFactory key1
            let g2 = FSharpGrain.refInt<GrainKeyState, GrainIntKeyCommand> fixture.GrainFactory key2

            let! k1 =
                FSharpGrain.askInt<GrainKeyState, GrainIntKeyCommand, int64> GetOwnPrimaryKeyInt64 g1

            let! k2 =
                FSharpGrain.askInt<GrainKeyState, GrainIntKeyCommand, int64> GetOwnPrimaryKeyInt64 g2

            test <@ k1 = key1 @>
            test <@ k2 = key2 @>
            test <@ k1 <> k2 @>
        }

    [<Fact>]
    member _.``negative int key is preserved by primaryKeyInt64`` () =
        task {
            let negKey = -50001L
            let grain =
                FSharpGrain.refInt<GrainKeyState, GrainIntKeyCommand> fixture.GrainFactory negKey

            let! returnedKey =
                FSharpGrain.askInt<GrainKeyState, GrainIntKeyCommand, int64> GetOwnPrimaryKeyInt64 grain

            test <@ returnedKey = negKey @>
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
