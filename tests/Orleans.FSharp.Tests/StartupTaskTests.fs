module Orleans.FSharp.Tests.StartupTaskTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Microsoft.Extensions.DependencyInjection
open Orleans.FSharp.Runtime

[<Fact>]
let ``siloConfig CE default has no startup tasks`` () =
    let config = siloConfig { () }
    test <@ config.StartupTasks.Length = 0 @>

[<Fact>]
let ``siloConfig CE adds startup task`` () =
    let config =
        siloConfig {
            addStartupTask (fun _sp _ct -> Task.CompletedTask)
        }

    test <@ config.StartupTasks.Length = 1 @>

[<Fact>]
let ``siloConfig CE multiple startup tasks accumulate`` () =
    let config =
        siloConfig {
            addStartupTask (fun _sp _ct -> Task.CompletedTask)
            addStartupTask (fun _sp _ct -> Task.CompletedTask)
            addStartupTask (fun _sp _ct -> Task.CompletedTask)
        }

    test <@ config.StartupTasks.Length = 3 @>

[<Fact>]
let ``siloConfig CE startup task receives service provider`` () =
    task {
        let mutable receivedSp = false

        let config =
            siloConfig {
                addStartupTask (fun sp _ct ->
                    task {
                        receivedSp <- sp <> null
                        return ()
                    }
                    :> Task)
            }

        let sp = ServiceCollection().BuildServiceProvider() :> IServiceProvider
        do! config.StartupTasks.Head sp CancellationToken.None
        test <@ receivedSp @>
    }

[<Fact>]
let ``siloConfig CE startup task receives cancellation token`` () =
    task {
        let mutable receivedCt = CancellationToken.None

        let config =
            siloConfig {
                addStartupTask (fun _sp ct ->
                    task {
                        receivedCt <- ct
                        return ()
                    }
                    :> Task)
            }

        let cts = new CancellationTokenSource()
        let sp = ServiceCollection().BuildServiceProvider() :> IServiceProvider
        do! config.StartupTasks.Head sp cts.Token
        test <@ receivedCt = cts.Token @>
        cts.Dispose()
    }

[<Fact>]
let ``siloConfig CE startup tasks compose with other options`` () =
    let config =
        siloConfig {
            useLocalhostClustering
            addMemoryStorage "Default"
            addStartupTask (fun _sp _ct -> Task.CompletedTask)
        }

    test <@ config.ClusteringMode.IsSome @>
    test <@ config.StorageProviders |> Map.containsKey "Default" @>
    test <@ config.StartupTasks.Length = 1 @>

[<Fact>]
let ``siloConfig CE startup tasks execute in order`` () =
    task {
        let mutable order = []

        let config =
            siloConfig {
                addStartupTask (fun _sp _ct ->
                    task {
                        order <- order @ [ "first" ]
                        return ()
                    }
                    :> Task)

                addStartupTask (fun _sp _ct ->
                    task {
                        order <- order @ [ "second" ]
                        return ()
                    }
                    :> Task)
            }

        let sp = ServiceCollection().BuildServiceProvider() :> IServiceProvider

        for t in config.StartupTasks do
            do! t sp CancellationToken.None

        test <@ order = [ "first"; "second" ] @>
    }
