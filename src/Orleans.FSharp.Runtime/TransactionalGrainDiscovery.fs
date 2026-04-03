namespace Orleans.FSharp.Runtime

open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Concurrency
open Orleans.Transactions.Abstractions
open Orleans.FSharp

/// <summary>
/// Defines the transactional grain behavior: deposit, withdraw, and getBalance
/// pure functions that operate on a mutable state type.
/// </summary>
/// <typeparam name="'State">The mutable state type (must have a parameterless constructor).</typeparam>
type TransactionalGrainDefinition<'State> =
    {
        /// <summary>Performs a deposit operation, returning the updated state.</summary>
        Deposit: 'State -> decimal -> 'State
        /// <summary>Performs a withdrawal operation, returning the updated state. May throw on overdraft.</summary>
        Withdraw: 'State -> decimal -> 'State
        /// <summary>Extracts the balance from the state.</summary>
        GetBalance: 'State -> decimal
        /// <summary>Function to copy field values from source state into the target state object.</summary>
        CopyState: 'State -> 'State -> unit
    }

/// <summary>
/// Defines the ATM grain behavior: orchestrates atomic cross-grain transfers.
/// </summary>
/// <typeparam name="'TAccountGrain">The transactional account grain interface type.</typeparam>
type AtmGrainDefinition<'TAccountGrain when 'TAccountGrain :> IGrainWithStringKey> =
    {
        /// <summary>Performs a transfer by calling withdraw on source and deposit on destination.</summary>
        Transfer: 'TAccountGrain -> 'TAccountGrain -> decimal -> Task
    }

/// <summary>
/// A generic grain implementation that bridges F# transactional grain definitions to Orleans.
/// Uses [TransactionalState] for ACID guarantees on all reads and writes.
/// </summary>
/// <typeparam name="'State">The mutable state type for the transactional grain.</typeparam>
[<Reentrant>]
type FSharpTransactionalGrain< 'State when 'State: not struct and 'State: (new: unit -> 'State)>
    (
        [<TransactionalState("state", "TransactionStore")>] transactionalState: ITransactionalState<'State>,
        definition: TransactionalGrainDefinition<'State>,
        logger: ILogger<FSharpTransactionalGrain<'State>>
    ) =
    inherit Grain()

    /// <summary>
    /// Called when the grain is activated. Logs activation.
    /// </summary>
    override this.OnActivateAsync(_ct: CancellationToken) =
        Log.logInfo
            logger
            "FSharpTransactionalGrain {GrainType} activated {GrainId}"
            [| box (this.GetGrainId().Type.ToString()); box (this.GetGrainId().ToString()) |]

        Task.CompletedTask

    /// <summary>
    /// Deposit funds into the account within a transaction.
    /// </summary>
    member _.Deposit(amount: decimal) : Task =
        transactionalState.PerformUpdate(fun state ->
            let newState = definition.Deposit state amount
            definition.CopyState newState state)

    /// <summary>
    /// Withdraw funds from the account within a transaction.
    /// Throws on overdraft, which aborts the entire transaction.
    /// </summary>
    member _.Withdraw(amount: decimal) : Task =
        transactionalState.PerformUpdate(fun state ->
            let newState = definition.Withdraw state amount
            definition.CopyState newState state)

    /// <summary>
    /// Read the current balance within a transaction.
    /// </summary>
    member _.GetBalance() : Task<decimal> =
        transactionalState.PerformRead(fun state -> definition.GetBalance state)

/// <summary>
/// A generic grain implementation for orchestrating atomic cross-grain transfers.
/// The Transfer method creates a new transaction context.
/// </summary>
/// <typeparam name="'TAccountGrain">The transactional account grain interface type.</typeparam>
type FSharpAtmGrain<'TAccountGrain when 'TAccountGrain :> IGrainWithStringKey>
    (
        definition: AtmGrainDefinition<'TAccountGrain>,
        logger: ILogger<FSharpAtmGrain<'TAccountGrain>>
    ) =
    inherit Grain()

    /// <summary>
    /// Atomically transfers funds from one account to another.
    /// Both the withdrawal and deposit happen within a single Orleans transaction.
    /// </summary>
    [<Transaction(TransactionOption.Create)>]
    member this.Transfer(fromAccount: string, toAccount: string, amount: decimal) : Task =
        // Capture protected member before task CE
        let gf = this.GrainFactory

        task {
            Log.logInfo
                logger
                "ATM: Transferring {Amount} from {From} to {To}"
                [| box amount; box fromAccount; box toAccount |]

            let from = gf.GetGrain<'TAccountGrain>(fromAccount)
            let to' = gf.GetGrain<'TAccountGrain>(toAccount)

            do! definition.Transfer from to' amount

            Log.logInfo
                logger
                "ATM: Transfer of {Amount} from {From} to {To} completed"
                [| box amount; box fromAccount; box toAccount |]
        }

/// <summary>
/// Extension methods for IServiceCollection to register F# transactional grain definitions.
/// </summary>
[<AutoOpen>]
module TransactionalGrainSiloBuilderExtensions =

    type IServiceCollection with

        /// <summary>
        /// Registers an F# TransactionalGrainDefinition as a singleton service.
        /// </summary>
        /// <param name="definition">The transactional grain definition to register.</param>
        /// <returns>The service collection for chaining.</returns>
        member services.AddFSharpTransactionalGrain<'State>(definition: TransactionalGrainDefinition<'State>) : IServiceCollection =
            services.AddSingleton<TransactionalGrainDefinition<'State>>(definition)

        /// <summary>
        /// Registers an F# AtmGrainDefinition as a singleton service.
        /// </summary>
        /// <param name="definition">The ATM grain definition to register.</param>
        /// <returns>The service collection for chaining.</returns>
        member services.AddFSharpAtmGrain<'TAccountGrain when 'TAccountGrain :> IGrainWithStringKey>
            (definition: AtmGrainDefinition<'TAccountGrain>)
            : IServiceCollection =
            services.AddSingleton<AtmGrainDefinition<'TAccountGrain>>(definition)
