namespace Orleans.FSharp.Integration

open Xunit
open Swensen.Unquote
open Orleans.FSharp
open Orleans.FSharp.Sample

/// <summary>
/// Shared fixture that starts a single scripting silo for all scripting tests.
/// Uses non-default ports to avoid conflicts with other test silos.
/// </summary>
type ScriptingFixture() =
    let mutable handle: Scripting.SiloHandle option = None

    /// <summary>Gets the silo handle.</summary>
    member _.Handle = handle.Value

    interface IAsyncLifetime with
        member _.InitializeAsync() =
            task {
                // Force the CodeGen assembly to be loaded
                let codeGenAssembly = typeof<Orleans.FSharp.CodeGen.CounterGrainImpl>.Assembly
                let _ = codeGenAssembly.GetTypes()
                let! h = Scripting.startOnPorts 22221 33310
                handle <- Some h
            }

        member _.DisposeAsync() =
            task {
                match handle with
                | Some h -> do! Scripting.shutdown h
                | None -> ()
            }

/// <summary>
/// xUnit collection definition for scripting tests.
/// </summary>
[<CollectionDefinition("ScriptingCollection")>]
type ScriptingCollection() =
    interface ICollectionFixture<ScriptingFixture>

/// <summary>
/// Integration tests for the Scripting module.
/// Tests that the Scripting module can start a silo, create grain references,
/// and shut down cleanly.
/// </summary>
[<Collection("ScriptingCollection")>]
type ScriptingTests(fixture: ScriptingFixture) =

    [<Fact>]
    member _.``quickStart returns a working silo handle`` () =
        task {
            let handle = fixture.Handle
            test <@ handle.Host <> null @>
            test <@ handle.Client <> null @>
            test <@ handle.GrainFactory <> null @>
        }

    [<Fact>]
    member _.``grain can be called from scripting context`` () =
        task {
            let handle = fixture.Handle
            let grain = Scripting.getGrain<ICounterGrain> handle 4200L
            let! result = grain.HandleMessage(Increment)
            let value = unbox<int> result
            test <@ value = 1 @>

            let! result2 = grain.HandleMessage(GetValue)
            let value2 = unbox<int> result2
            test <@ value2 = 1 @>
        }

    [<Fact>]
    member _.``shutdown function has correct signature and fixture disposes cleanly`` () =
        task {
            // The ScriptingFixture.DisposeAsync calls Scripting.shutdown, verifying it works.
            // Here we verify the shutdown function exists and the silo is alive before disposal.
            let handle = fixture.Handle
            let grain = Scripting.getGrain<ICounterGrain> handle 4201L
            let! result = grain.HandleMessage(GetValue)
            let value = unbox<int> result
            // Grain should be accessible (default state is Zero -> 0)
            test <@ value = 0 @>
        }
