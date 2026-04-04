module Orleans.FSharp.Tests.FSharpSerializationTests

open System
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.Hosting
open Orleans.FSharp.FSharpSerialization

/// <summary>Tests for FSharpSerialization.fs — F# serialization integration with Orleans.</summary>

// --- Module existence tests ---

[<Fact>]
let ``FSharpSerialization module exists in the assembly`` () =
    let moduleType =
        typeof<Orleans.FSharp.AssemblyMarker>.Assembly.GetTypes()
        |> Array.tryFind (fun t -> t.Name = "FSharpSerialization" && t.IsAbstract && t.IsSealed)

    test <@ moduleType.IsSome @>

// --- addFSharpSerialization tests ---

[<Fact>]
let ``addFSharpSerialization is a function`` () =
    let f = FSharpSerialization.addFSharpSerialization
    let funcType = f.GetType()
    let expectedBase = typedefof<FSharpFunc<_, _>>.MakeGenericType(typeof<ISiloBuilder>, typeof<ISiloBuilder>)
    test <@ expectedBase.IsAssignableFrom(funcType) @>

[<Fact>]
let ``addFSharpSerialization throws when package not installed`` () =
    let f = FSharpSerialization.addFSharpSerialization
    let ex = Assert.Throws<InvalidOperationException>(fun () -> f (Unchecked.defaultof<ISiloBuilder>) |> ignore)
    test <@ ex.Message.Contains("Microsoft.Orleans.Serialization.FSharp") @>

[<Fact>]
let ``addFSharpSerialization error mentions AddSerializationFSharpSupport method`` () =
    let f = FSharpSerialization.addFSharpSerialization
    let ex = Assert.Throws<InvalidOperationException>(fun () -> f (Unchecked.defaultof<ISiloBuilder>) |> ignore)
    test <@ ex.Message.Contains("AddSerializationFSharpSupport") @>

// --- Function reflection tests ---

[<Fact>]
let ``addFSharpSerialization exists as a member on the module`` () =
    let moduleType =
        typeof<Orleans.FSharp.AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "FSharpSerialization" && t.IsAbstract && t.IsSealed)

    let hasMember =
        moduleType.GetMembers()
        |> Array.exists (fun m -> m.Name = "addFSharpSerialization" || m.Name = "get_addFSharpSerialization")

    test <@ hasMember @>

[<Fact>]
let ``addFSharpSerialization returns ISiloBuilder to ISiloBuilder function`` () =
    let f = FSharpSerialization.addFSharpSerialization
    let funcType = f.GetType()
    let expectedBase = typedefof<FSharpFunc<_, _>>.MakeGenericType(typeof<ISiloBuilder>, typeof<ISiloBuilder>)
    test <@ expectedBase.IsAssignableFrom(funcType) @>

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``addFSharpSerialization always throws InvalidOperationException for any builder`` () =
    let f = FSharpSerialization.addFSharpSerialization
    let exn = Assert.Throws<InvalidOperationException>(fun () -> f (Unchecked.defaultof<ISiloBuilder>) |> ignore)
    exn.Message.Length > 0

[<Property>]
let ``FSharpSerialization module has at least 1 public method`` () =
    let moduleType =
        typeof<Orleans.FSharp.AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "FSharpSerialization" && t.IsAbstract && t.IsSealed)

    moduleType.GetMethods().Length >= 1
