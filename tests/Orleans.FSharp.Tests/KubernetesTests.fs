module Orleans.FSharp.Tests.KubernetesTests

open System
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.Hosting
open Orleans.FSharp.Kubernetes

/// <summary>Tests for Kubernetes.fs — Kubernetes clustering helpers.</summary>

// --- Module existence tests ---

[<Fact>]
let ``Kubernetes module exists in the assembly`` () =
    let moduleType =
        typeof<Orleans.FSharp.AssemblyMarker>.Assembly.GetTypes()
        |> Array.tryFind (fun t -> t.Name = "Kubernetes" && t.IsAbstract && t.IsSealed)

    test <@ moduleType.IsSome @>

// --- useKubernetesClustering tests ---

[<Fact>]
let ``useKubernetesClustering is a function`` () =
    let f = Kubernetes.useKubernetesClustering
    let funcType = f.GetType()
    let expectedBase = typedefof<FSharpFunc<_, _>>.MakeGenericType(typeof<ISiloBuilder>, typeof<ISiloBuilder>)
    test <@ expectedBase.IsAssignableFrom(funcType) @>

[<Fact>]
let ``useKubernetesClustering throws when package not installed`` () =
    let f = Kubernetes.useKubernetesClustering
    let ex = Assert.Throws<InvalidOperationException>(fun () -> f (Unchecked.defaultof<ISiloBuilder>) |> ignore)
    test <@ ex.Message.Contains("Microsoft.Orleans.Hosting.Kubernetes") @>

[<Fact>]
let ``useKubernetesClustering error mentions UseKubernetesHosting method`` () =
    let f = Kubernetes.useKubernetesClustering
    let ex = Assert.Throws<InvalidOperationException>(fun () -> f (Unchecked.defaultof<ISiloBuilder>) |> ignore)
    test <@ ex.Message.Contains("UseKubernetesHosting") @>

// --- useKubernetesClusteringWithNamespace tests ---

[<Fact>]
let ``useKubernetesClusteringWithNamespace returns a function`` () =
    let f = Kubernetes.useKubernetesClusteringWithNamespace "my-namespace"
    let funcType = f.GetType()
    let expectedBase = typedefof<FSharpFunc<_, _>>.MakeGenericType(typeof<ISiloBuilder>, typeof<ISiloBuilder>)
    test <@ expectedBase.IsAssignableFrom(funcType) @>

[<Fact>]
let ``useKubernetesClusteringWithNamespace throws when package not installed`` () =
    let f = Kubernetes.useKubernetesClusteringWithNamespace "my-namespace"
    let ex = Assert.Throws<InvalidOperationException>(fun () -> f (Unchecked.defaultof<ISiloBuilder>) |> ignore)
    test <@ ex.Message.Contains("Microsoft.Orleans.Hosting.Kubernetes") @>

[<Fact>]
let ``useKubernetesClusteringWithNamespace with different namespaces produces distinct functions`` () =
    let f1 = Kubernetes.useKubernetesClusteringWithNamespace "ns1"
    let f2 = Kubernetes.useKubernetesClusteringWithNamespace "ns2"
    test <@ not (obj.ReferenceEquals(f1, f2)) @>

// --- Function reflection tests ---

[<Fact>]
let ``useKubernetesClustering exists as a member on the module`` () =
    let moduleType =
        typeof<Orleans.FSharp.AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "Kubernetes" && t.IsAbstract && t.IsSealed)

    let hasMember =
        moduleType.GetMembers()
        |> Array.exists (fun m -> m.Name = "useKubernetesClustering" || m.Name = "get_useKubernetesClustering")

    test <@ hasMember @>

[<Fact>]
let ``useKubernetesClusteringWithNamespace exists as a method on the module`` () =
    let moduleType =
        typeof<Orleans.FSharp.AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "Kubernetes" && t.IsAbstract && t.IsSealed)

    let method =
        moduleType.GetMethods()
        |> Array.tryFind (fun m -> m.Name = "useKubernetesClusteringWithNamespace")

    test <@ method.IsSome @>

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``useKubernetesClusteringWithNamespace produces distinct functions for distinct namespaces`` (n: PositiveInt) =
    let count = min n.Get 5
    let funcs = Array.init count (fun i -> Kubernetes.useKubernetesClusteringWithNamespace $"ns{i}")
    funcs |> Array.pairwise |> Array.forall (fun (a, b) -> not (obj.ReferenceEquals(a, b)))

[<Property>]
let ``useKubernetesClusteringWithNamespace throws with package name in message for any namespace`` (ns: NonEmptyString) =
    let f = Kubernetes.useKubernetesClusteringWithNamespace ns.Get
    let ex = Assert.Throws<InvalidOperationException>(fun () -> f (Unchecked.defaultof<ISiloBuilder>) |> ignore)
    ex.Message.Contains("Microsoft.Orleans.Hosting.Kubernetes")
