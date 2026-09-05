module Orleans.FSharp.Tests.GrainBatchTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.FSharp

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/// Always-succeeding task returning the input unchanged.
let private identity (n: int) : Task<int> = task { return n }

/// Always-failing task.
let private alwaysFail (_: int) : Task<int> =
    task { return raise (InvalidOperationException("always fails")) }

/// Task that returns None when the value is negative, Some value otherwise.
let private filterNegative (n: int) : Task<int option> =
    task {
        if n < 0 then return None
        else return Some n
    }

// ---------------------------------------------------------------------------
// GrainBatch.map tests
// ---------------------------------------------------------------------------

[<Fact>]
let ``map: empty sequence returns empty list`` () =
    task {
        let! results = GrainBatch.map [] identity
        test <@ results = [] @>
    }

[<Fact>]
let ``map: single element returns single-element list`` () =
    task {
        let! results = GrainBatch.map [ 42 ] identity
        test <@ results = [ 42 ] @>
    }

[<Fact>]
let ``map: preserves order of results`` () =
    task {
        let inputs = [ 1; 2; 3; 4; 5 ]
        let! results = GrainBatch.map inputs identity
        test <@ results = inputs @>
    }

[<Fact>]
let ``map: applies function to each element`` () =
    task {
        let inputs = [ 1; 2; 3 ]
        let! results = GrainBatch.map inputs (fun n -> task { return n * 2 })
        test <@ results = [ 2; 4; 6 ] @>
    }

[<Fact>]
let ``map: fails when any element throws`` () =
    task {
        let inputs = [ 1; 2; 3 ]
        let f (n: int) : Task<int> =
            task {
                if n = 2 then raise (InvalidOperationException("oops"))
                return n
            }

        let! ex = Assert.ThrowsAnyAsync<exn>(fun () -> GrainBatch.map inputs f :> Task)
        test <@ not (isNull ex) @>
    }

// ---------------------------------------------------------------------------
// GrainBatch.tryMap tests
// ---------------------------------------------------------------------------

[<Fact>]
let ``tryMap: empty sequence returns empty list`` () =
    task {
        let! results = GrainBatch.tryMap [] identity
        test <@ results = [] @>
    }

[<Fact>]
let ``tryMap: all successes gives Ok results`` () =
    task {
        let! results = GrainBatch.tryMap [ 1; 2; 3 ] identity
        test <@ results = [ Ok 1; Ok 2; Ok 3 ] @>
    }

[<Fact>]
let ``tryMap: failed elements give Error, others give Ok`` () =
    task {
        let f (n: int) : Task<int> =
            task {
                if n % 2 = 0 then raise (InvalidOperationException("even"))
                return n
            }

        let! results = GrainBatch.tryMap [ 1; 2; 3 ] f
        test <@ results.[0] = Ok 1 @>
        test <@ results.[1] |> Result.isError @>
        test <@ results.[2] = Ok 3 @>
    }

[<Fact>]
let ``tryMap: all failures gives all Error results`` () =
    task {
        let! results = GrainBatch.tryMap [ 1; 2; 3 ] alwaysFail
        test <@ results |> List.forall Result.isError @>
    }

[<Fact>]
let ``tryMap: preserves count of results`` () =
    task {
        let inputs = [ 1; 2; 3; 4; 5 ]
        let! results = GrainBatch.tryMap inputs alwaysFail
        test <@ results.Length = inputs.Length @>
    }

// ---------------------------------------------------------------------------
// GrainBatch.aggregate tests
// ---------------------------------------------------------------------------

[<Fact>]
let ``aggregate: computes sum of grain results`` () =
    task {
        let! total = GrainBatch.aggregate [ 1; 2; 3; 4 ] identity List.sum
        test <@ total = 10 @>
    }

[<Fact>]
let ``aggregate: empty sequence aggregates to initial value`` () =
    task {
        let! total = GrainBatch.aggregate [] identity List.sum
        test <@ total = 0 @>
    }

[<Fact>]
let ``aggregate: can collect to a list`` () =
    task {
        let! collected = GrainBatch.aggregate [ 3; 1; 2 ] identity id
        test <@ collected |> List.sort = [ 1; 2; 3 ] @>
    }

// ---------------------------------------------------------------------------
// GrainBatch.iter tests
// ---------------------------------------------------------------------------

[<Fact>]
let ``iter: empty sequence completes immediately`` () =
    task {
        let mutable count = 0
        do! GrainBatch.iter [] (fun _ -> task { count <- count + 1 })
        test <@ count = 0 @>
    }

[<Fact>]
let ``iter: calls function for each element`` () =
    task {
        let mutable count = 0
        let inputs = [ 1; 2; 3; 4; 5 ]
        do! GrainBatch.iter inputs (fun _ -> task { System.Threading.Interlocked.Increment(&count) |> ignore })
        test <@ count = 5 @>
    }

[<Fact>]
let ``iter: fails when any element throws`` () =
    task {
        let f (n: int) : Task =
            task { if n = 3 then raise (InvalidOperationException("three")) } :> Task

        let! ex = Assert.ThrowsAnyAsync<exn>(fun () -> GrainBatch.iter [ 1; 2; 3 ] f)
        test <@ not (isNull ex) @>
    }

// ---------------------------------------------------------------------------
// GrainBatch.tryIter tests
// ---------------------------------------------------------------------------

[<Fact>]
let ``tryIter: all successes gives Ok results`` () =
    task {
        let! results = GrainBatch.tryIter [ 1; 2; 3 ] (fun _ -> Task.CompletedTask)
        test <@ results = [ Ok(); Ok(); Ok() ] @>
    }

[<Fact>]
let ``tryIter: failed elements give Error, others give Ok`` () =
    task {
        let f (n: int) : Task =
            task { if n = 2 then raise (InvalidOperationException("two")) } :> Task

        let! results = GrainBatch.tryIter [ 1; 2; 3 ] f
        test <@ results.[0] = Ok() @>
        test <@ results.[1] |> Result.isError @>
        test <@ results.[2] = Ok() @>
    }

// ---------------------------------------------------------------------------
// GrainBatch.choose tests
// ---------------------------------------------------------------------------

[<Fact>]
let ``choose: filters out None values`` () =
    task {
        let! results = GrainBatch.choose [ 1; -2; 3; -4; 5 ] filterNegative
        test <@ results |> List.sort = [ 1; 3; 5 ] @>
    }

[<Fact>]
let ``choose: all Some values returns full list`` () =
    task {
        let! results = GrainBatch.choose [ 1; 2; 3 ] (fun n -> task { return Some n })
        test <@ results |> List.sort = [ 1; 2; 3 ] @>
    }

[<Fact>]
let ``choose: all None values returns empty list`` () =
    task {
        let! results = GrainBatch.choose [ 1; 2; 3 ] (fun _ -> task { return None : int option })
        test <@ results = [] @>
    }

// ---------------------------------------------------------------------------
// GrainBatch.partition tests
// ---------------------------------------------------------------------------

[<Fact>]
let ``partition: all successes gives full successes list, empty failures`` () =
    task {
        let! (successes, failures) = GrainBatch.partition [ 1; 2; 3 ] identity
        test <@ successes |> List.sort = [ 1; 2; 3 ] @>
        test <@ failures = [] @>
    }

[<Fact>]
let ``partition: all failures gives empty successes, full failures list`` () =
    task {
        let! (successes, failures) = GrainBatch.partition [ 1; 2; 3 ] alwaysFail
        test <@ successes = [] @>
        test <@ failures.Length = 3 @>
    }

[<Fact>]
let ``partition: mixed results correctly separates successes and failures`` () =
    task {
        let f (n: int) : Task<int> =
            task {
                if n % 2 = 0 then raise (InvalidOperationException("even"))
                return n
            }

        let! (successes, failures) = GrainBatch.partition [ 1; 2; 3; 4; 5 ] f
        test <@ successes |> List.sort = [ 1; 3; 5 ] @>
        test <@ failures.Length = 2 @>
    }

// ---------------------------------------------------------------------------
// Ownership and synchronous-failure regression tests (#33)
// ---------------------------------------------------------------------------

[<Fact>]
let ``map: waits for a late fault before preserving the first synchronous throw`` () =
    task {
        let pending = TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)
        let calls = ResizeArray<int>()
        let synchronousError = InvalidOperationException("sync failure")

        let f (n: int) : Task<int> =
            calls.Add(n)

            match n with
            | 1 -> pending.Task
            | 2 -> raise synchronousError
            | _ -> Task.FromResult(n * 10)

        let batch = GrainBatch.map [ 1; 2; 3 ] f
        let callsBeforeRelease = calls |> Seq.toList
        let completedBeforeRelease = batch.IsCompleted

        pending.SetException(InvalidOperationException("late failure"))
        let! error = Assert.ThrowsAnyAsync<exn>(fun () -> batch :> Task)

        test <@ callsBeforeRelease = [ 1; 2; 3 ] @>
        test <@ not completedBeforeRelease @>
        test <@ Object.ReferenceEquals(error, synchronousError) @>
    }

[<Fact>]
let ``map: synchronous throw outranks an immediate asynchronous fault`` () =
    task {
        let asynchronousFailure = TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)
        let calls = ResizeArray<int>()
        let synchronousError = InvalidOperationException("sync failure")
        let laterSynchronousError = InvalidOperationException("later sync failure")
        asynchronousFailure.SetException(InvalidOperationException("async failure"))

        let f (n: int) : Task<int> =
            calls.Add(n)

            match n with
            | 1 -> asynchronousFailure.Task
            | 2 -> raise synchronousError
            | 3 -> raise laterSynchronousError
            | _ -> Task.FromResult(n)

        let batch = GrainBatch.map [ 1; 2; 3; 4 ] f
        let! error = Assert.ThrowsAnyAsync<exn>(fun () -> batch :> Task)

        test <@ calls |> Seq.toList = [ 1; 2; 3; 4 ] @>
        test <@ Object.ReferenceEquals(error, synchronousError) @>
    }

[<Fact>]
let ``map: treats a null task as a tracked failure`` () =
    task {
        let pending = TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)
        let calls = ResizeArray<int>()

        let f (n: int) : Task<int> =
            calls.Add(n)

            match n with
            | 1 -> pending.Task
            | 2 -> null
            | _ -> Task.FromResult(n)

        let batch = GrainBatch.map [ 1; 2; 3 ] f
        let callsBeforeRelease = calls |> Seq.toList
        let completedBeforeRelease = batch.IsCompleted

        pending.SetResult(1)
        let! error = Assert.ThrowsAnyAsync<exn>(fun () -> batch :> Task)

        test <@ callsBeforeRelease = [ 1; 2; 3 ] @>
        test <@ not completedBeforeRelease @>
        test <@ error.GetType() = typeof<InvalidOperationException> @>
        test <@ error.Message = "GrainBatch operation returned a null Task." @>
    }

[<Fact>]
let ``iter: waits for a late fault before preserving the first synchronous throw`` () =
    task {
        let pending = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let calls = ResizeArray<int>()
        let synchronousError = InvalidOperationException("sync failure")

        let f (n: int) : Task =
            calls.Add(n)

            match n with
            | 1 -> pending.Task :> Task
            | 2 -> raise synchronousError
            | _ -> Task.CompletedTask

        let batch = GrainBatch.iter [ 1; 2; 3 ] f
        let callsBeforeRelease = calls |> Seq.toList
        let completedBeforeRelease = batch.IsCompleted

        pending.SetException(InvalidOperationException("late failure"))
        let! error = Assert.ThrowsAnyAsync<exn>(fun () -> batch)

        test <@ callsBeforeRelease = [ 1; 2; 3 ] @>
        test <@ not completedBeforeRelease @>
        test <@ Object.ReferenceEquals(error, synchronousError) @>
    }

[<Fact>]
let ``iter: synchronous throw outranks an immediate asynchronous fault`` () =
    task {
        let asynchronousFailure = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let calls = ResizeArray<int>()
        let synchronousError = InvalidOperationException("sync failure")
        let laterSynchronousError = InvalidOperationException("later sync failure")
        asynchronousFailure.SetException(InvalidOperationException("async failure"))

        let f (n: int) : Task =
            calls.Add(n)

            match n with
            | 1 -> asynchronousFailure.Task :> Task
            | 2 -> raise synchronousError
            | 3 -> raise laterSynchronousError
            | _ -> Task.CompletedTask

        let batch = GrainBatch.iter [ 1; 2; 3; 4 ] f
        let! error = Assert.ThrowsAnyAsync<exn>(fun () -> batch)

        test <@ calls |> Seq.toList = [ 1; 2; 3; 4 ] @>
        test <@ Object.ReferenceEquals(error, synchronousError) @>
    }

[<Fact>]
let ``iter: treats a null task as a tracked failure`` () =
    task {
        let pending = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let calls = ResizeArray<int>()

        let f (n: int) : Task =
            calls.Add(n)

            match n with
            | 1 -> pending.Task :> Task
            | 2 -> null
            | _ -> Task.CompletedTask

        let batch = GrainBatch.iter [ 1; 2; 3 ] f
        let callsBeforeRelease = calls |> Seq.toList
        let completedBeforeRelease = batch.IsCompleted

        pending.SetResult(())
        let! error = Assert.ThrowsAnyAsync<exn>(fun () -> batch)

        test <@ callsBeforeRelease = [ 1; 2; 3 ] @>
        test <@ not completedBeforeRelease @>
        test <@ error.GetType() = typeof<InvalidOperationException> @>
        test <@ error.Message = "GrainBatch operation returned a null Task." @>
    }

[<Fact>]
let ``map: materializes the input before invoking any operation`` () =
    task {
        let calls = ResizeArray<int>()

        let failingInput =
            seq {
                yield 1
                raise (InvalidOperationException("enumeration failure"))
            }

        let batch =
            GrainBatch.map failingInput (fun n ->
                calls.Add(n)
                Task.FromResult(n))

        let! error = Assert.ThrowsAnyAsync<exn>(fun () -> batch :> Task)

        test <@ calls |> Seq.isEmpty @>
        test <@ error.Message = "enumeration failure" @>
    }

[<Fact>]
let ``iter: materializes the input before invoking any operation`` () =
    task {
        let calls = ResizeArray<int>()

        let failingInput =
            seq {
                yield 1
                raise (InvalidOperationException("enumeration failure"))
            }

        let batch =
            GrainBatch.iter failingInput (fun n ->
                calls.Add(n)
                Task.CompletedTask)

        let! error = Assert.ThrowsAnyAsync<exn>(fun () -> batch)

        test <@ calls |> Seq.isEmpty @>
        test <@ error.Message = "enumeration failure" @>
    }

[<Fact>]
let ``tryMap: enumeration failure faults the batch before any operation starts`` () =
    task {
        let calls = ResizeArray<int>()

        let failingInput =
            seq {
                yield 1
                raise (InvalidOperationException("enumeration failure"))
            }

        let batch =
            GrainBatch.tryMap failingInput (fun n ->
                calls.Add(n)
                Task.FromResult(n))

        let! error = Assert.ThrowsAnyAsync<exn>(fun () -> batch :> Task)

        test <@ calls |> Seq.isEmpty @>
        test <@ error.Message = "enumeration failure" @>
    }

[<Fact>]
let ``tryIter: enumeration failure faults the batch before any operation starts`` () =
    task {
        let calls = ResizeArray<int>()

        let failingInput =
            seq {
                yield 1
                raise (InvalidOperationException("enumeration failure"))
            }

        let batch =
            GrainBatch.tryIter failingInput (fun n ->
                calls.Add(n)
                Task.CompletedTask)

        let! error = Assert.ThrowsAnyAsync<exn>(fun () -> batch)

        test <@ calls |> Seq.isEmpty @>
        test <@ error.Message = "enumeration failure" @>
    }

[<Fact>]
let ``tryMap: captures synchronous and asynchronous failures in input order`` () =
    task {
        let pending = TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)
        let asynchronousFailure = TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)
        let calls = ResizeArray<int>()

        let f (n: int) : Task<int> =
            calls.Add(n)

            match n with
            | 1 -> pending.Task
            | 2 -> raise (InvalidOperationException("sync failure"))
            | 3 -> asynchronousFailure.Task
            | 5 -> null
            | _ -> Task.FromResult(n * 10)

        let batch = GrainBatch.tryMap [ 1; 2; 3; 4; 5 ] f
        let callsBeforeRelease = calls |> Seq.toList
        let completedBeforeRelease = batch.IsCompleted

        pending.SetResult(10)
        asynchronousFailure.SetException(InvalidOperationException("async failure"))
        let! results = batch

        let normalized =
            results
            |> List.map (function
                | Ok value -> $"ok:{value}"
                | Error error -> $"error:{error.GetType().Name}:{error.Message}")

        test <@ callsBeforeRelease = [ 1; 2; 3; 4; 5 ] @>
        test <@ not completedBeforeRelease @>
        test
            <@
                normalized =
                    [ "ok:10"
                      "error:InvalidOperationException:sync failure"
                      "error:InvalidOperationException:async failure"
                      "ok:40"
                      "error:InvalidOperationException:GrainBatch operation returned a null Task." ]
            @>
    }

[<Fact>]
let ``tryIter: captures synchronous and asynchronous failures in input order`` () =
    task {
        let pending = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let asynchronousFailure = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let calls = ResizeArray<int>()

        let f (n: int) : Task =
            calls.Add(n)

            match n with
            | 1 -> pending.Task :> Task
            | 2 -> raise (InvalidOperationException("sync failure"))
            | 3 -> asynchronousFailure.Task :> Task
            | 5 -> null
            | _ -> Task.CompletedTask

        let batch = GrainBatch.tryIter [ 1; 2; 3; 4; 5 ] f
        let callsBeforeRelease = calls |> Seq.toList
        let completedBeforeRelease = batch.IsCompleted

        pending.SetResult(())
        asynchronousFailure.SetException(InvalidOperationException("async failure"))
        let! results = batch

        let normalized =
            results
            |> List.map (function
                | Ok() -> "ok"
                | Error error -> $"error:{error.GetType().Name}:{error.Message}")

        test <@ callsBeforeRelease = [ 1; 2; 3; 4; 5 ] @>
        test <@ not completedBeforeRelease @>
        test
            <@
                normalized =
                    [ "ok"
                      "error:InvalidOperationException:sync failure"
                      "error:InvalidOperationException:async failure"
                      "ok"
                      "error:InvalidOperationException:GrainBatch operation returned a null Task." ]
            @>
    }

[<Fact>]
let ``aggregate: inherits map task ownership`` () =
    task {
        let pending = TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)
        let calls = ResizeArray<int>()
        let synchronousError = InvalidOperationException("sync failure")

        let f (n: int) : Task<int> =
            calls.Add(n)

            match n with
            | 1 -> pending.Task
            | 2 -> raise synchronousError
            | _ -> Task.FromResult(n)

        let batch = GrainBatch.aggregate [ 1; 2; 3 ] f List.sum
        let callsBeforeRelease = calls |> Seq.toList
        let completedBeforeRelease = batch.IsCompleted

        pending.SetException(InvalidOperationException("late failure"))
        let! error = Assert.ThrowsAnyAsync<exn>(fun () -> batch :> Task)

        test <@ callsBeforeRelease = [ 1; 2; 3 ] @>
        test <@ not completedBeforeRelease @>
        test <@ Object.ReferenceEquals(error, synchronousError) @>
    }

[<Fact>]
let ``choose: inherits map task ownership`` () =
    task {
        let pending = TaskCompletionSource<int option>(TaskCreationOptions.RunContinuationsAsynchronously)
        let calls = ResizeArray<int>()
        let synchronousError = InvalidOperationException("sync failure")

        let f (n: int) : Task<int option> =
            calls.Add(n)

            match n with
            | 1 -> pending.Task
            | 2 -> raise synchronousError
            | _ -> Task.FromResult(Some n)

        let batch = GrainBatch.choose [ 1; 2; 3 ] f
        let callsBeforeRelease = calls |> Seq.toList
        let completedBeforeRelease = batch.IsCompleted

        pending.SetException(InvalidOperationException("late failure"))
        let! error = Assert.ThrowsAnyAsync<exn>(fun () -> batch :> Task)

        test <@ callsBeforeRelease = [ 1; 2; 3 ] @>
        test <@ not completedBeforeRelease @>
        test <@ Object.ReferenceEquals(error, synchronousError) @>
    }

[<Fact>]
let ``partition: inherits tryMap failure capture and ordering`` () =
    task {
        let calls = ResizeArray<int>()

        let f (n: int) : Task<int> =
            calls.Add(n)

            match n with
            | 1 -> Task.FromException<int>(InvalidOperationException("async first"))
            | 3 -> raise (InvalidOperationException("sync third"))
            | _ -> Task.FromResult(n * 10)

        let! (successes, failures) = GrainBatch.partition [ 1; 2; 3; 4 ] f

        test <@ calls |> Seq.toList = [ 1; 2; 3; 4 ] @>
        test <@ successes = [ 20; 40 ] @>
        test <@ failures |> List.map (fun error -> error.Message) = [ "async first"; "sync third" ] @>
    }

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``map: result count equals input count`` (inputs: int list) =
    let task = GrainBatch.map inputs identity
    task.Wait()
    task.Result.Length = inputs.Length

[<Property>]
let ``map: result values equal input values for identity function`` (inputs: int list) =
    let task = GrainBatch.map inputs identity
    task.Wait()
    task.Result = inputs

/// Returns a failed task when n is divisible by 3, otherwise returns n.
let private failOnMod3 (n: int) : Task<int> =
    if n % 3 = 0 then Task.FromException<int>(InvalidOperationException("mod3"))
    else Task.FromResult n

/// Returns a failed task when n is divisible by 2, otherwise returns n.
let private failOnEven (n: int) : Task<int> =
    if n % 2 = 0 then Task.FromException<int>(InvalidOperationException("even"))
    else Task.FromResult n

/// Returns a failed task when n is divisible by 5, otherwise returns n.
let private failOnMod5 (n: int) : Task<int> =
    if n % 5 = 0 then Task.FromException<int>(InvalidOperationException("mod5"))
    else Task.FromResult n

[<Property>]
let ``tryMap: result count always equals input count regardless of failures`` (inputs: int list) =
    let task = GrainBatch.tryMap inputs failOnMod3
    task.Wait()
    task.Result.Length = inputs.Length

[<Property>]
let ``tryMap: Ok count plus Error count equals total input count`` (inputs: int list) =
    let task = GrainBatch.tryMap inputs failOnMod3
    task.Wait()
    let okCount = task.Result |> List.sumBy (function Ok _ -> 1 | _ -> 0)
    let errCount = task.Result |> List.sumBy (function Error _ -> 1 | _ -> 0)
    okCount + errCount = inputs.Length

[<Property>]
let ``aggregate: sum equals List.sum of all inputs for identity`` (inputs: int list) =
    let task = GrainBatch.aggregate inputs identity List.sum
    task.Wait()
    task.Result = List.sum inputs

[<Property>]
let ``partition: success count plus failure count equals total count`` (inputs: int list) =
    let task = GrainBatch.partition inputs failOnEven
    task.Wait()
    let (successes, failures) = task.Result
    successes.Length + failures.Length = inputs.Length

[<Property>]
let ``choose: result count is at most input count`` (inputs: int list) =
    let task = GrainBatch.choose inputs filterNegative
    task.Wait()
    task.Result.Length <= inputs.Length

[<Property>]
let ``choose: all results satisfy the predicate (are non-negative)`` (inputs: int list) =
    let task = GrainBatch.choose inputs filterNegative
    task.Wait()
    task.Result |> List.forall (fun n -> n >= 0)

[<Property>]
let ``tryMap then partition: same partition as direct partition`` (inputs: NonNegativeInt list) =
    // Use NonNegativeInt to avoid tricky edge cases
    let nums = inputs |> List.map (fun n -> n.Get)
    let taskA = GrainBatch.partition nums failOnMod5
    taskA.Wait()
    let (successes, failures) = taskA.Result
    // Verify that successes have no multiples of 5 and failures count matches
    let failureCount = nums |> List.filter (fun n -> n % 5 = 0) |> List.length
    failures.Length = failureCount && successes |> List.forall (fun n -> n % 5 <> 0)
