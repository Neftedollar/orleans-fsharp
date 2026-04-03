/// <summary>
/// Orleans.FSharp assembly: idiomatic F# API for Microsoft Orleans.
/// </summary>
namespace Orleans.FSharp

open System.Runtime.CompilerServices

/// <summary>Grants internal visibility to the unit test project.</summary>
[<assembly: InternalsVisibleTo("Orleans.FSharp.Tests")>]
/// <summary>Grants internal visibility to the integration test project.</summary>
[<assembly: InternalsVisibleTo("Orleans.FSharp.Integration")>]
/// <summary>Grants internal visibility to the code generation project.</summary>
[<assembly: InternalsVisibleTo("Orleans.FSharp.CodeGen")>]
do ()
