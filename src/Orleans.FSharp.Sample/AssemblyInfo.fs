namespace Orleans.FSharp.Sample

open System.Runtime.CompilerServices

[<assembly: InternalsVisibleTo("Orleans.FSharp.CodeGen")>]
[<assembly: InternalsVisibleTo("Orleans.FSharp.Integration")>]
do ()
