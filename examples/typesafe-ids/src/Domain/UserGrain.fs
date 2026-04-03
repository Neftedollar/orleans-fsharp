/// <summary>
/// User grain demonstrating type-safe ID access.
/// The <c>getUser</c> function only accepts <c>int64&lt;UserId&gt;</c> -- passing an
/// <c>int64&lt;OrderId&gt;</c> is a compile error.
/// </summary>
namespace TypeSafeIds.Domain

open System.Threading.Tasks
open Orleans
open Orleans.FSharp
open TypeSafeIds.Domain.Ids

/// <summary>
/// State of a user grain.
/// </summary>
type UserState =
    {
        /// <summary>Display name of the user.</summary>
        Name: string
        /// <summary>Email address of the user.</summary>
        Email: string
        /// <summary>Total number of orders placed by this user.</summary>
        OrderCount: int
    }

/// <summary>
/// Commands that can be sent to the user grain.
/// Discriminated union with exhaustive matching -- the compiler catches unhandled cases.
/// </summary>
type UserCommand =
    /// <summary>Set the user profile name and email.</summary>
    | SetProfile of name: string * email: string
    /// <summary>Increment the order count by one.</summary>
    | IncrementOrders
    /// <summary>Query the current profile (read-only).</summary>
    | GetProfile

/// <summary>
/// Grain interface for the user grain.
/// </summary>
type IUserGrain =
    inherit IGrainWithStringKey

    /// <summary>Sends a command to the user grain and returns the result.</summary>
    abstract HandleMessage: UserCommand -> Task<obj>

/// <summary>
/// User grain definition and type-safe access functions.
/// </summary>
module UserGrainDef =

    /// <summary>
    /// The user grain: manages user profile state via typed commands.
    /// </summary>
    let user =
        grain {
            defaultState { Name = ""; Email = ""; OrderCount = 0 }

            handle (fun state cmd ->
                task {
                    match cmd with
                    | SetProfile(name, email) ->
                        let next = { state with Name = name; Email = email }
                        return next, box true
                    | IncrementOrders ->
                        let next = { state with OrderCount = state.OrderCount + 1 }
                        return next, box next.OrderCount
                    | GetProfile ->
                        return state, box state
                })
        }

    /// <summary>
    /// Type-safe grain access -- IMPOSSIBLE to pass wrong ID type.
    /// Passing <c>orderId 42L</c> instead of a <c>userId</c> is a compile error.
    /// </summary>
    /// <param name="factory">The Orleans grain factory.</param>
    /// <param name="id">A typed <c>int64&lt;UserId&gt;</c>.</param>
    /// <returns>A type-safe grain reference to the user grain.</returns>
    let getUser (factory: IGrainFactory) (id: int64<UserId>) =
        GrainRef.ofString<IUserGrain> factory (toStringKey id)
