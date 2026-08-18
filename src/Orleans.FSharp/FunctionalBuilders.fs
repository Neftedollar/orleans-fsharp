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

    /// <summary>
    /// Start a contract expression for a domain key type and an API record type, with the API
    /// record itself serving as the actor brand -- the short form for the common case where one
    /// record belongs to exactly one grain type. <c>contract&lt;RoomId, RoomApi&gt;() { ... }</c>
    /// is the same contract as <c>grainContract&lt;RoomApi, RoomId, RoomApi&gt;() { ... }</c>;
    /// declare a separate brand (and use <c>grainContract</c>) when several grain types share one
    /// API record, or when the record type must be replaceable without moving the grain's
    /// transport identity.
    /// </summary>
    let contract<'Key, 'Api> () : GrainContractBuilder<'Api, 'Key, 'Api> =
        GrainContractBuilder<'Api, 'Key, 'Api>()

    /// <summary>Start a server-definition expression for a sealed contract.</summary>
    /// <param name="contract">The sealed contract to build a definition for.</param>
    /// <exception cref="System.InvalidOperationException">Thrown when <paramref name="contract"/> is null.</exception>
    let grainFor (contract: GrainContract<'Actor, 'Key, 'Api>) : FunctionalGrainDefinitionBuilder<'Actor, 'Key, 'Api> =
        if obj.ReferenceEquals(contract, null) then
            FunctionalDiagnostics.fail FunctionalDiagnostics.DefinitionStage "'grainFor' requires a contract value."

        FunctionalGrainDefinitionBuilder<'Actor, 'Key, 'Api>(contract)

    /// <summary>
    /// Start a journaled server-definition expression for a sealed contract: the grain's state is
    /// the fold of an event journal kept by an Orleans log-consistency provider, and handlers
    /// raise events instead of returning a replacement state.
    /// </summary>
    /// <param name="contract">The sealed contract to build a journaled definition for.</param>
    /// <exception cref="System.InvalidOperationException">Thrown when <paramref name="contract"/> is null.</exception>
    let journaledGrainFor
        (contract: GrainContract<'Actor, 'Key, 'Api>)
        : FunctionalJournaledGrainDefinitionBuilder<'Actor, 'Key, 'Api> =
        if obj.ReferenceEquals(contract, null) then
            FunctionalDiagnostics.fail
                FunctionalDiagnostics.DefinitionStage
                "'journaledGrainFor' requires a contract value."

        FunctionalJournaledGrainDefinitionBuilder<'Actor, 'Key, 'Api>(contract)
