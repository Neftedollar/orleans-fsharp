/// <summary>
/// Units of Measure for grain identity -- ZERO runtime cost, COMPILE-TIME safety.
/// In C#, UserId and OrderId would both be 'long' -- easy to mix up at runtime.
/// In F#, the compiler rejects <c>getUserGrain 42L&lt;OrderId&gt;</c> at compile time.
/// </summary>
module TypeSafeIds.Domain.Ids

/// <summary>Unit of measure tagging an int64 as a User identifier.</summary>
[<Measure>] type UserId

/// <summary>Unit of measure tagging an int64 as an Order identifier.</summary>
[<Measure>] type OrderId

/// <summary>Unit of measure tagging an int64 as an Account identifier.</summary>
[<Measure>] type AccountId

/// <summary>Unit of measure tagging an int64 as a Product identifier.</summary>
[<Measure>] type ProductId

/// <summary>
/// Create a typed user ID from a raw int64.
/// </summary>
/// <param name="raw">The raw int64 value.</param>
/// <returns>A compile-time tagged int64&lt;UserId&gt;.</returns>
let userId (raw: int64) : int64<UserId> = raw * 1L<UserId>

/// <summary>
/// Create a typed order ID from a raw int64.
/// </summary>
/// <param name="raw">The raw int64 value.</param>
/// <returns>A compile-time tagged int64&lt;OrderId&gt;.</returns>
let orderId (raw: int64) : int64<OrderId> = raw * 1L<OrderId>

/// <summary>
/// Create a typed account ID from a raw int64.
/// </summary>
/// <param name="raw">The raw int64 value.</param>
/// <returns>A compile-time tagged int64&lt;AccountId&gt;.</returns>
let accountId (raw: int64) : int64<AccountId> = raw * 1L<AccountId>

/// <summary>
/// Create a typed product ID from a raw int64.
/// </summary>
/// <param name="raw">The raw int64 value.</param>
/// <returns>A compile-time tagged int64&lt;ProductId&gt;.</returns>
let productId (raw: int64) : int64<ProductId> = raw * 1L<ProductId>

/// <summary>
/// Safely extract the raw int64 from any typed ID.
/// Erases the unit of measure, giving back the underlying numeric value.
/// </summary>
/// <param name="id">A unit-of-measure-tagged int64.</param>
/// <returns>The raw int64 value.</returns>
let rawId (id: int64<'u>) : int64 = int64 id

/// <summary>
/// Convert a typed ID to a string key suitable for Orleans grain lookup.
/// </summary>
/// <param name="id">A unit-of-measure-tagged int64.</param>
/// <returns>The string representation of the raw int64.</returns>
let toStringKey (id: int64<'u>) : string = string (int64 id)
