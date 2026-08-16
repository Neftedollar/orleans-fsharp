namespace Orleans.FSharp

open System
open Orleans.FSharp.FunctionalDiagnostics

/// <summary>
/// The logical identity of one attached persistent state: its state name, provider name,
/// and stored CLR type. Lookup and attachment validation compare this triple.
/// </summary>
type internal PersistentStateDescriptor =
    { StateName: string
      ProviderName: string
      StoredType: Type }

/// <summary>
/// An immutable logical descriptor of one named persistent state facet.
/// Created by <see cref="M:Orleans.FSharp.PersistentStateModule.create"/> and attached to a
/// definition with <c>stateFrom</c> or <c>usePersistentState</c>.
/// </summary>
[<Sealed>]
type PersistentStateRef<'State> internal (stateName: string, providerName: string) =

    let descriptor =
        { StateName = stateName
          ProviderName = providerName
          StoredType = typeof<'State> }

    /// <summary>The Orleans state name of this facet.</summary>
    member internal _.StateName = stateName

    /// <summary>The Orleans storage provider name of this facet.</summary>
    member internal _.ProviderName = providerName

    /// <summary>The exact stored CLR type of this facet.</summary>
    member internal _.StoredType = typeof<'State>

    /// <summary>The logical <c>(stateName, providerName, storedType)</c> identity of this facet.</summary>
    member internal _.Descriptor = descriptor

    override _.ToString() =
        $"PersistentStateRef(stateName = '{stateName}', providerName = '{providerName}', storedType = '{typeof<'State>.FullName}')"

/// <summary>Creation of immutable persistent-state descriptors.</summary>
[<RequireQualifiedAccess>]
module PersistentState =

    /// <summary>
    /// Create an immutable descriptor for a named persistent state facet.
    /// Blank or NUL-containing names and open generic stored types are rejected immediately.
    /// </summary>
    /// <param name="stateName">Orleans state name; unique within a definition.</param>
    /// <param name="providerName">Name of an <c>IGrainStorage</c> registration on every hosting silo.</param>
    let create<'State> (stateName: string) (providerName: string) : PersistentStateRef<'State> =
        if isBlank stateName then
            fail PersistentStage "stateName must be a non-blank string."

        if containsNul stateName then
            fail PersistentStage $"stateName '{stateName}' must not contain a NUL character."

        if isBlank providerName then
            fail PersistentStage $"providerName for stateName '{stateName}' must be a non-blank string."

        if containsNul providerName then
            fail PersistentStage $"providerName '{providerName}' must not contain a NUL character."

        if typeof<'State>.ContainsGenericParameters then
            fail
                PersistentStage
                $"the stored type '{typeof<'State>.FullName}' for stateName '{stateName}' must be a closed type."

        PersistentStateRef<'State>(stateName, providerName)
