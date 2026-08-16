// Orleans.FSharp Quick Start Script
// Run with: dotnet fsi quickstart.fsx
//
// Note: IHelloGrain below is a hand-written Orleans interface (the third, non-deprecated
// authoring style -- see docs/functional-grains.md's "Migrating from the grain { } CE" section).
// It is unaffected by the grain{} / FSharpGrain.ref deprecation. For a grain defined entirely in
// plain F# (contract + API record, no interface to write) -- and a demo that actually calls the
// grain and prints a reply, which this script cannot (see the comment above `getGrain` below) --
// see quickstart-functional.fsx beside this file, or docs/functional-grains.md.
#r "nuget: Orleans.FSharp"
#r "nuget: Orleans.FSharp.Runtime"

open System.Threading.Tasks
open Orleans
open Orleans.FSharp

// Define a simple grain interface
type IHelloGrain =
    inherit IGrainWithIntegerKey
    abstract SayHello: name: string -> Task<string>

// Orleans builds its grain manifest from assemblies carrying C#-source-generated
// [assembly: ApplicationPart] attributes, and takes that snapshot the FIRST time AddSerializer
// runs -- inside the UseOrleans call that Scripting.startOnPorts makes below. An assembly
// reached only through an F# hop (as MemoryGrainStorage, the in-memory reminder table, and the
// in-memory stream provider all are here -- Scripting.startOnPorts wires all three) is invisible
// to that snapshot unless something touches it first. Touching each one is cheap and idempotent;
// see docs/functional-grains.md, "Running a silo from a standalone F# process".
typeof<Orleans.Storage.MemoryGrainStorage>.Assembly |> ignore
typeof<Orleans.Hosting.SiloBuilderReminderMemoryExtensions>.Assembly |> ignore
typeof<Orleans.Streams.IStreamProvider>.Assembly |> ignore

// Start an in-process silo (silo port 11111, gateway port 30000)
let handle = (Scripting.startOnPorts 11111 30000).GetAwaiter().GetResult()
printfn "Silo started! GrainFactory ready."

// GetGrain<'T> resolves the interface-to-implementation mapping eagerly, at reference-creation
// time -- not lazily, on first call. Orleans' C# source generators never run over F# code (same
// section as above), so nothing in this single-file, pure-F# process can ever implement
// IHelloGrain: that needs a companion C# project generating the proxy and class, the role
// src/Orleans.FSharp.CodeGen plays for src/Orleans.FSharp.Sample's hand-written-interface demos.
// The line below is the script's whole point -- getting a reference to the interface defined
// above -- so it is kept, not deleted; it is expected to throw here, for the structural reason
// just explained, independent of anything the functional-runtime deprecation touches.
try
    Scripting.getGrain<IHelloGrain> handle 0L |> ignore
    printfn "grain reference obtained"
with :? System.ArgumentException as ex ->
    printfn "IHelloGrain has no registered implementation in this process (expected -- see the comment above): %s" ex.Message

// Clean up
Scripting.shutdown(handle).GetAwaiter().GetResult()
printfn "Silo stopped."
