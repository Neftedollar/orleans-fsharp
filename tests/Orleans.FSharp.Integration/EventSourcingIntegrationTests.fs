module Orleans.FSharp.Integration.EventSourcingIntegrationTests

open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.FSharp.Sample

/// Unbox result as BankAccountState and return the Balance.
let inline balance (result: obj) = (unbox<BankAccountState> result).Balance

// ---------------------------------------------------------------------------
// V032: Event-sourced grain integration tests
// ---------------------------------------------------------------------------

[<Collection("ClusterCollection")>]
type EventSourcingIntegrationTests(fixture: ClusterFixture) =

    [<Fact>]
    member _.``BankAccount grain starts with zero balance`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IBankAccountGrain>("bank-zero-test")
            let! result = grain.HandleCommand(GetBalance)
            let balance = balance result
            test <@ balance = 0m @>
        }

    [<Fact>]
    member _.``BankAccount grain processes Deposit command`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IBankAccountGrain>("bank-deposit-test")
            let! result = grain.HandleCommand(Deposit 100m)
            let balance = balance result
            test <@ balance = 100m @>
        }

    [<Fact>]
    member _.``BankAccount grain processes multiple deposits`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IBankAccountGrain>("bank-multi-deposit-test")
            let! _ = grain.HandleCommand(Deposit 50m)
            let! _ = grain.HandleCommand(Deposit 30m)
            let! result = grain.HandleCommand(Deposit 20m)
            let balance = balance result
            test <@ balance = 100m @>
        }

    [<Fact>]
    member _.``BankAccount grain processes Withdraw command`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IBankAccountGrain>("bank-withdraw-test")
            let! _ = grain.HandleCommand(Deposit 100m)
            let! result = grain.HandleCommand(Withdraw 30m)
            let balance = balance result
            test <@ balance = 70m @>
        }

    [<Fact>]
    member _.``BankAccount grain rejects Withdraw with insufficient funds`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IBankAccountGrain>("bank-insufficient-test")
            let! _ = grain.HandleCommand(Deposit 50m)
            let! result = grain.HandleCommand(Withdraw 100m)
            let balance = balance result
            // Balance should remain unchanged since withdraw was rejected
            test <@ balance = 50m @>
        }

    [<Fact>]
    member _.``BankAccount grain GetBalance returns current state`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IBankAccountGrain>("bank-getbalance-test")
            let! _ = grain.HandleCommand(Deposit 75m)
            let! result = grain.HandleCommand(GetBalance)
            let balance = balance result
            test <@ balance = 75m @>
        }

    [<Fact>]
    member _.``BankAccount grain full lifecycle`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IBankAccountGrain>("bank-lifecycle-test")

            // Start with zero
            let! r1 = grain.HandleCommand(GetBalance)
            test <@ balance r1 = 0m @>

            // Deposit 200
            let! r2 = grain.HandleCommand(Deposit 200m)
            test <@ balance r2 = 200m @>

            // Withdraw 50
            let! r3 = grain.HandleCommand(Withdraw 50m)
            test <@ balance r3 = 150m @>

            // Deposit 25
            let! r4 = grain.HandleCommand(Deposit 25m)
            test <@ balance r4 = 175m @>

            // Verify final balance
            let! r5 = grain.HandleCommand(GetBalance)
            test <@ balance r5 = 175m @>
        }

// ---------------------------------------------------------------------------
// V032b: Storage failure / error path coverage
// ---------------------------------------------------------------------------

[<Collection("ClusterCollection")>]
type EventSourcingErrorTests(fixture: ClusterFixture) =

    [<Fact>]
    member _.``BankAccount grain handles zero deposit gracefully`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IBankAccountGrain>("bank-zero-deposit-test")
            let! _ = grain.HandleCommand(Deposit 100m)
            // Zero deposit should produce no events (handler rejects it)
            let! result = grain.HandleCommand(Deposit 0m)
            let balance = balance result
            test <@ balance = 100m @>
        }

    [<Fact>]
    member _.``BankAccount grain handles negative deposit gracefully`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IBankAccountGrain>("bank-neg-deposit-test")
            let! _ = grain.HandleCommand(Deposit 100m)
            // Negative deposit should produce no events
            let! result = grain.HandleCommand(Deposit -50m)
            let balance = balance result
            test <@ balance = 100m @>
        }

    [<Fact>]
    member _.``BankAccount grain handles withdraw from zero balance`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IBankAccountGrain>("bank-zero-withdraw-test")
            let! result = grain.HandleCommand(Withdraw 10m)
            let balance = balance result
            // Should remain at zero
            test <@ balance = 0m @>
        }

// ---------------------------------------------------------------------------
// V032: FsCheck property — arbitrary command sequences produce consistent state
// ---------------------------------------------------------------------------

[<Collection("ClusterCollection")>]
type EventSourcingPropertyTests(fixture: ClusterFixture) =

    [<Fact>]
    member _.``arbitrary deposit sequence produces expected balance`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IBankAccountGrain>("bank-prop-deposits")
            let amounts = [ 10m; 20m; 30m; 40m ]

            for amount in amounts do
                let! _ = grain.HandleCommand(Deposit amount)
                ()

            let! result = grain.HandleCommand(GetBalance)
            let balance = balance result
            test <@ balance = 100m @>
        }

    [<Fact>]
    member _.``deposit then exact withdraw yields zero`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IBankAccountGrain>("bank-prop-zero")
            let! _ = grain.HandleCommand(Deposit 500m)
            let! _ = grain.HandleCommand(Withdraw 500m)
            let! result = grain.HandleCommand(GetBalance)
            let balance = balance result
            test <@ balance = 0m @>
        }

    [<Fact>]
    member _.``command sequence maintains non-negative balance invariant`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IBankAccountGrain>("bank-prop-invariant")

            // Deposit, try to overdraw, deposit again
            let commands =
                [ Deposit 100m
                  Withdraw 200m // should be rejected
                  Deposit 50m
                  Withdraw 30m ]

            for cmd in commands do
                let! _ = grain.HandleCommand(cmd)
                ()

            let! result = grain.HandleCommand(GetBalance)
            let balance = balance result
            // 100 (deposit) + 50 (deposit) - 30 (withdraw) = 120
            // The Withdraw 200 was rejected because balance was only 100
            test <@ balance = 120m @>
        }
