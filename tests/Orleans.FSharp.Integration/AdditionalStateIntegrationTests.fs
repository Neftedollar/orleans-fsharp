/// <summary>
/// Integration tests for the <c>additionalState</c> CE keyword.
///
/// <para>
/// The <c>additionalState</c> keyword allows a grain to declare secondary named persistent
/// states alongside its primary state.  These tests verify end-to-end behaviour through the
/// <c>AdditionalStateTestGrainImpl</c> C# stub, which initialises additional states in
/// <c>OnActivateAsync</c> via <c>GrainDefinition.initAdditionalStates</c> and exposes them
/// to the F# handler via <c>GrainContextModule.forCSharp</c>.
/// </para>
///
/// <para>
/// Each test uses a unique string key (via <c>Guid.NewGuid()</c>) to get a fresh, isolated
/// grain activation so tests do not share mutable state.
/// </para>
/// </summary>
module Orleans.FSharp.Integration.AdditionalStateIntegrationTests

open System
open Xunit
open Swensen.Unquote
open Orleans.FSharp.Sample

/// Helper — retrieves the (counter, auditEventCount) tuple from a grain.
let getBoth (grain: IAdditionalStateTestGrain) =
    task {
        let! result = grain.HandleMessage(GetBoth)
        return unbox<int * int> result
    }

[<Collection("ClusterCollection")>]
type AdditionalStateIntegrationTests(fixture: ClusterFixture) =

    [<Fact>]
    member _.``initial state - counter and audit count start at zero`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IAdditionalStateTestGrain>(Guid.NewGuid().ToString("N"))
            let! (counter, audit) = getBoth grain
            test <@ counter = 0 @>
            test <@ audit = 0 @>
        }

    [<Fact>]
    member _.``IncrCounter increments primary counter`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IAdditionalStateTestGrain>(Guid.NewGuid().ToString("N"))
            let! _ = grain.HandleMessage(IncrCounter)
            let! (counter, _) = getBoth grain
            test <@ counter = 1 @>
        }

    [<Fact>]
    member _.``IncrCounter also increments audit event count`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IAdditionalStateTestGrain>(Guid.NewGuid().ToString("N"))
            let! _ = grain.HandleMessage(IncrCounter)
            let! (_, audit) = getBoth grain
            test <@ audit = 1 @>
        }

    [<Fact>]
    member _.``IncrAudit increments audit count without touching primary counter`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IAdditionalStateTestGrain>(Guid.NewGuid().ToString("N"))
            let! _ = grain.HandleMessage(IncrAudit)
            let! (counter, audit) = getBoth grain
            test <@ counter = 0 @>
            test <@ audit = 1 @>
        }

    [<Fact>]
    member _.``IncrCounter and IncrAudit are independent - both track separately`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IAdditionalStateTestGrain>(Guid.NewGuid().ToString("N"))
            // Two counter increments (each also bumps audit)
            let! _ = grain.HandleMessage(IncrCounter)
            let! _ = grain.HandleMessage(IncrCounter)
            // One pure audit increment
            let! _ = grain.HandleMessage(IncrAudit)
            let! (counter, audit) = getBoth grain
            // counter = 2, audit = 2 (from IncrCounter) + 1 (from IncrAudit) = 3
            test <@ counter = 2 @>
            test <@ audit = 3 @>
        }

    [<Fact>]
    member _.``ResetAll resets both primary counter and audit count to zero`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IAdditionalStateTestGrain>(Guid.NewGuid().ToString("N"))
            let! _ = grain.HandleMessage(IncrCounter)
            let! _ = grain.HandleMessage(IncrCounter)
            let! _ = grain.HandleMessage(IncrAudit)
            // Verify non-zero state before reset
            let! (cBefore, aBefore) = getBoth grain
            test <@ cBefore = 2 @>
            test <@ aBefore = 3 @>
            // Reset
            let! _ = grain.HandleMessage(ResetAll)
            let! (counter, audit) = getBoth grain
            test <@ counter = 0 @>
            test <@ audit = 0 @>
        }

    [<Fact>]
    member _.``GetBoth returns accurate snapshot of both states`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IAdditionalStateTestGrain>(Guid.NewGuid().ToString("N"))
            let! _ = grain.HandleMessage(IncrCounter)
            let! _ = grain.HandleMessage(IncrAudit)
            let! _ = grain.HandleMessage(IncrAudit)
            let! result = grain.HandleMessage(GetBoth)
            let (counter, audit) = unbox<int * int> result
            // IncrCounter: counter=1, audit=1
            // IncrAudit x2: counter=1, audit=3
            test <@ counter = 1 @>
            test <@ audit = 3 @>
        }

    [<Fact>]
    member _.``IncrCounter return value reflects new primary state`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IAdditionalStateTestGrain>(Guid.NewGuid().ToString("N"))
            let! result = grain.HandleMessage(IncrCounter)
            let state = unbox<AdditionalState> result
            test <@ state.Counter = 1 @>
        }

    [<Fact>]
    member _.``two independent grains do not share audit state`` () =
        task {
            let g1 = fixture.GrainFactory.GetGrain<IAdditionalStateTestGrain>(Guid.NewGuid().ToString("N"))
            let g2 = fixture.GrainFactory.GetGrain<IAdditionalStateTestGrain>(Guid.NewGuid().ToString("N"))

            let! _ = g1.HandleMessage(IncrCounter)
            let! _ = g1.HandleMessage(IncrCounter)
            let! _ = g1.HandleMessage(IncrCounter)
            // g2 has no mutations

            let! (c1, a1) = getBoth g1
            let! (c2, a2) = getBoth g2

            test <@ c1 = 3 @>
            test <@ a1 = 3 @>
            test <@ c2 = 0 @>
            test <@ a2 = 0 @>
        }

    [<Fact>]
    member _.``operations after reset accumulate from zero`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IAdditionalStateTestGrain>(Guid.NewGuid().ToString("N"))
            let! _ = grain.HandleMessage(IncrCounter)
            let! _ = grain.HandleMessage(ResetAll)
            let! _ = grain.HandleMessage(IncrCounter)
            let! _ = grain.HandleMessage(IncrAudit)
            let! (counter, audit) = getBoth grain
            // After reset: counter=0, audit=0
            // IncrCounter: counter=1, audit=1
            // IncrAudit: counter=1, audit=2
            test <@ counter = 1 @>
            test <@ audit = 2 @>
        }
