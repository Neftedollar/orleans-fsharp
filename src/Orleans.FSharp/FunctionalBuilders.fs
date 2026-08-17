namespace Orleans.FSharp

/// <summary>Entry points for the functional contract and definition computation expressions.</summary>
[<AutoOpen>]
module FunctionalGrainBuilders =

    /// <summary>
    /// Start a contract expression for an actor brand, a domain key type, and an API record type,
    /// for example <c>grainContract&lt;RoomActor, RoomId, RoomApi&gt;() { ... }</c>.
    /// </summary>
    let grainContract<'Actor, 'Key, 'Api> () : GrainContractBuilder<'Actor, 'Key, 'Api> =
        GrainContractBuilder<'Actor, 'Key, 'Api>()

    /// <summary>Start a server-definition expression for a sealed contract.</summary>
    let grainFor (contract: GrainContract<'Actor, 'Key, 'Api>) : FunctionalGrainDefinitionBuilder<'Actor, 'Key, 'Api> =
        if obj.ReferenceEquals(contract, null) then
            FunctionalDiagnostics.fail FunctionalDiagnostics.DefinitionStage "'grainFor' requires a contract value."

        FunctionalGrainDefinitionBuilder<'Actor, 'Key, 'Api>(contract)
