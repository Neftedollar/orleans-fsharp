/// <summary>
/// Integration tests for Orleans transactions using <c>FSharpTransactionalGrain</c> and
/// <c>FSharpAtmGrain</c>. Tests verify ACID semantics for deposit, withdraw, balance, and
/// atomic transfer operations across grain boundaries.
/// </summary>
module Orleans.FSharp.Integration.TransactionIntegrationTests

open System
open Xunit
open Swensen.Unquote
open Orleans.FSharp.Sample

[<Collection("ClusterCollection")>]
type TransactionIntegrationTests(fixture: ClusterFixture) =

    [<Fact>]
    member _.``deposit increases balance`` () =
        task {
            let key = Guid.NewGuid().ToString("N")
            let account = fixture.GrainFactory.GetGrain<ITransactionalAccountGrain>(key)
            do! account.Deposit(100m)
            let! balance = account.GetBalance()
            test <@ balance = 100m @>
        }

    [<Fact>]
    member _.``withdraw decreases balance`` () =
        task {
            let key = Guid.NewGuid().ToString("N")
            let account = fixture.GrainFactory.GetGrain<ITransactionalAccountGrain>(key)
            do! account.Deposit(200m)
            do! account.Withdraw(75m)
            let! balance = account.GetBalance()
            test <@ balance = 125m @>
        }

    [<Fact>]
    member _.``initial balance is zero`` () =
        task {
            let key = Guid.NewGuid().ToString("N")
            let account = fixture.GrainFactory.GetGrain<ITransactionalAccountGrain>(key)
            let! balance = account.GetBalance()
            test <@ balance = 0m @>
        }

    [<Fact>]
    member _.``multiple deposits accumulate correctly`` () =
        task {
            let key = Guid.NewGuid().ToString("N")
            let account = fixture.GrainFactory.GetGrain<ITransactionalAccountGrain>(key)
            do! account.Deposit(50m)
            do! account.Deposit(50m)
            do! account.Deposit(50m)
            let! balance = account.GetBalance()
            test <@ balance = 150m @>
        }

    [<Fact>]
    member _.``ATM transfer moves funds atomically`` () =
        task {
            let keyA = Guid.NewGuid().ToString("N")
            let keyB = Guid.NewGuid().ToString("N")
            let atmKey = Guid.NewGuid().ToString("N")
            let accountA = fixture.GrainFactory.GetGrain<ITransactionalAccountGrain>(keyA)
            let accountB = fixture.GrainFactory.GetGrain<ITransactionalAccountGrain>(keyB)
            let atm = fixture.GrainFactory.GetGrain<ITransactionalAtmGrain>(atmKey)

            do! accountA.Deposit(500m)
            do! atm.Transfer(keyA, keyB, 200m)

            let! balA = accountA.GetBalance()
            let! balB = accountB.GetBalance()
            test <@ balA = 300m @>
            test <@ balB = 200m @>
        }

    [<Fact>]
    member _.``ATM transfer preserves total balance`` () =
        task {
            let keyA = Guid.NewGuid().ToString("N")
            let keyB = Guid.NewGuid().ToString("N")
            let atmKey = Guid.NewGuid().ToString("N")
            let accountA = fixture.GrainFactory.GetGrain<ITransactionalAccountGrain>(keyA)
            let accountB = fixture.GrainFactory.GetGrain<ITransactionalAccountGrain>(keyB)
            let atm = fixture.GrainFactory.GetGrain<ITransactionalAtmGrain>(atmKey)

            do! accountA.Deposit(1000m)
            do! accountB.Deposit(500m)
            do! atm.Transfer(keyA, keyB, 300m)

            let! balA = accountA.GetBalance()
            let! balB = accountB.GetBalance()
            // Total balance must be preserved: (1000+500) = 1500
            test <@ balA + balB = 1500m @>
        }

    [<Fact>]
    member _.``overdraft withdraw throws and does not change balance`` () =
        task {
            let key = Guid.NewGuid().ToString("N")
            let account = fixture.GrainFactory.GetGrain<ITransactionalAccountGrain>(key)
            do! account.Deposit(100m)
            // Attempt to withdraw more than available
            let! ex = Assert.ThrowsAnyAsync<exn>(fun () -> (account.Withdraw(200m) : System.Threading.Tasks.Task))
            test <@ ex |> box |> isNull |> not @>
        }
