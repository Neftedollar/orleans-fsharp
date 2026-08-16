/// <summary>
/// Orleans.FSharp.Runtime assembly: silo-side configuration and the functional grain runtime.
/// </summary>
namespace Orleans.FSharp.Runtime

open System.Runtime.CompilerServices

/// <summary>Grants internal visibility to the unit test project.</summary>
[<assembly: InternalsVisibleTo("Orleans.FSharp.Tests")>]
/// <summary>Grants internal visibility to the integration test project.</summary>
[<assembly: InternalsVisibleTo("Orleans.FSharp.Integration")>]
do ()
