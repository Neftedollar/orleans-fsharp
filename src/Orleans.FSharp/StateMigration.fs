namespace Orleans.FSharp

open System

/// <summary>
/// Defines a state migration from one version to another.
/// Used to upgrade grain state schemas between deployments.
/// </summary>
/// <typeparam name="'TOld">The source state type before migration.</typeparam>
/// <typeparam name="'TNew">The target state type after migration.</typeparam>
type Migration<'TOld, 'TNew> =
    {
        /// <summary>The version number this migration upgrades from.</summary>
        FromVersion: int
        /// <summary>The version number this migration upgrades to.</summary>
        ToVersion: int
        /// <summary>The function that transforms the old state into the new state.</summary>
        Migrate: 'TOld -> 'TNew
    }

/// <summary>
/// Functions for defining and applying grain state migrations.
/// Enables upgrading grain state schemas across deployments by chaining
/// version-to-version transformations.
/// </summary>
[<RequireQualifiedAccess>]
module StateMigration =

    /// <summary>
    /// Creates a typed migration that operates on boxed (obj) values.
    /// The migration function casts from obj to 'TOld, applies the transform,
    /// and boxes the result back to obj.
    /// </summary>
    /// <param name="fromVer">The version number this migration upgrades from.</param>
    /// <param name="toVer">The version number this migration upgrades to.</param>
    /// <param name="migrate">The function that transforms the old state into the new state.</param>
    /// <typeparam name="'TOld">The source state type before migration.</typeparam>
    /// <typeparam name="'TNew">The target state type after migration.</typeparam>
    /// <returns>A migration that operates on boxed values, suitable for chaining.</returns>
    let migration<'TOld, 'TNew> (fromVer: int) (toVer: int) (migrate: 'TOld -> 'TNew) : Migration<obj, obj> =
        {
            FromVersion = fromVer
            ToVersion = toVer
            Migrate = fun (o: obj) -> (o :?> 'TOld) |> migrate |> box
        }

    /// <summary>
    /// Applies a chain of migrations to upgrade state from any older version to the current version.
    /// Migrations are sorted by FromVersion and applied sequentially from the given
    /// <paramref name="currentVersion"/> up to the latest available migration.
    /// </summary>
    /// <param name="migrations">The list of migrations to consider.</param>
    /// <param name="currentVersion">The version of the provided state.</param>
    /// <param name="state">The boxed state value at the given version.</param>
    /// <typeparam name="'T">The target state type after all migrations are applied.</typeparam>
    /// <returns>The migrated state value, unboxed to the target type.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no migration is found for a required version step.</exception>
    let applyMigrations<'T> (migrations: Migration<obj, obj> list) (currentVersion: int) (state: obj) : 'T =
        let sorted =
            migrations
            |> List.sortBy (fun m -> m.FromVersion)

        let applicable =
            sorted
            |> List.filter (fun m -> m.FromVersion >= currentVersion)

        let result =
            applicable
            |> List.fold (fun acc m -> m.Migrate acc) state

        result :?> 'T

    /// <summary>
    /// Validates that a list of migrations forms a contiguous chain from the starting
    /// version to the ending version, with no gaps or duplicates.
    /// </summary>
    /// <param name="migrations">The list of migrations to validate.</param>
    /// <returns>A list of validation error messages. Empty if the chain is valid.</returns>
    let validate (migrations: Migration<obj, obj> list) : string list =
        let sorted = migrations |> List.sortBy (fun m -> m.FromVersion)

        [
            // Check for duplicate FromVersion
            let duplicates =
                sorted
                |> List.groupBy (fun m -> m.FromVersion)
                |> List.filter (fun (_, group) -> group.Length > 1)

            for (ver, _) in duplicates do
                yield $"Duplicate migration from version {ver}."

            // Check that ToVersion = FromVersion of next migration (contiguous chain)
            for (a, b) in sorted |> List.pairwise do
                if a.ToVersion <> b.FromVersion then
                    yield $"Gap in migration chain: version {a.ToVersion} to {b.FromVersion}."
        ]

    /// <summary>
    /// Validates the migration chain and, if valid, applies all migrations to upgrade the state.
    /// Returns <c>Ok 'T</c> on success, or <c>Error (string list)</c> with validation errors if
    /// the chain has gaps or duplicate versions.
    /// </summary>
    /// <param name="migrations">The list of migrations to validate and apply.</param>
    /// <param name="currentVersion">The version of the provided state.</param>
    /// <param name="state">The boxed state value at the given version.</param>
    /// <typeparam name="'T">The target state type after all migrations are applied.</typeparam>
    /// <returns><c>Ok</c> with the migrated state, or <c>Error</c> with a list of validation errors.</returns>
    /// <example>
    /// <code>
    /// let migrations = [
    ///     StateMigration.migration&lt;StateV1, StateV2&gt; 1 2 (fun v1 -> { Name = v1.Name; Email = "" })
    /// ]
    /// match StateMigration.tryApplyMigrations&lt;StateV2&gt; migrations 1 (box oldState) with
    /// | Ok newState -> // use newState
    /// | Error errs  -> // log errs
    /// </code>
    /// </example>
    let tryApplyMigrations<'T>
        (migrations: Migration<obj, obj> list)
        (currentVersion: int)
        (state: obj)
        : Result<'T, string list> =
        let errors = validate migrations

        if errors.IsEmpty then
            Ok(applyMigrations<'T> migrations currentVersion state)
        else
            Error errors
