namespace Orleans.FSharp.Testing

open FsCheck
open FsCheck.FSharp

/// <summary>
/// FsCheck integration helpers for property-based testing of Orleans grains.
/// Provides generators for command sequences and state machine property verification.
/// </summary>
[<RequireQualifiedAccess>]
module FsCheckHelpers =

    /// <summary>
    /// FsCheck Arbitrary for generating random non-empty command sequences.
    /// Uses the default ArbMap to generate commands, producing lists of 1+ commands.
    /// </summary>
    /// <typeparam name="'Command">The command type (must have FsCheck Arbitrary support).</typeparam>
    /// <returns>An Arbitrary that generates non-empty lists of commands.</returns>
    let commandSequenceArb<'Command> () : Arbitrary<'Command list> =
        let commandGen = ArbMap.defaults |> ArbMap.generate<'Command>
        let gen = FsCheck.FSharp.Gen.nonEmptyListOf commandGen
        Arb.fromGen gen

    /// <summary>
    /// Property helper: verifies that a state invariant holds after applying all commands
    /// to an initial state using a given transition function.
    /// </summary>
    /// <param name="initial">The initial state before any commands are applied.</param>
    /// <param name="apply">The state transition function that applies a command to a state.</param>
    /// <param name="invariant">The invariant predicate that must hold for every state.</param>
    /// <param name="commands">The list of commands to apply.</param>
    /// <typeparam name="'State">The state type.</typeparam>
    /// <typeparam name="'Command">The command type.</typeparam>
    /// <returns>True if the invariant holds after all commands are applied.</returns>
    let stateMachineProperty<'State, 'Command>
        (initial: 'State)
        (apply: 'State -> 'Command -> 'State)
        (invariant: 'State -> bool)
        (commands: 'Command list)
        : bool =
        let finalState = commands |> List.fold apply initial
        invariant finalState
