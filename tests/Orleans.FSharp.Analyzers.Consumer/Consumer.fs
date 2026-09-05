module Orleans.FSharp.Analyzers.Consumer

open Orleans.FSharp.Analyzers.AsyncUsageAnalyzer

let flagged () = async { return 42 }

let preferred () = task { return 42 }

[<AllowAsync>]
let suppressed () = async { return 42 }
