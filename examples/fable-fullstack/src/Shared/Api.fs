namespace FableFullstack.Shared

open System

/// <summary>
/// A todo item shared between server (Orleans grain) and client (Fable/JS).
/// This type is compiled to both .NET and JavaScript.
/// </summary>
type Todo =
    {
        /// <summary>Unique identifier for the todo item.</summary>
        Id: Guid
        /// <summary>The text content of the todo item.</summary>
        Text: string
        /// <summary>Whether the todo item is completed.</summary>
        Done: bool
    }

/// <summary>
/// The shared API definition used by Fable.Remoting.
/// The server implements this interface backed by an Orleans grain.
/// The client calls this interface via Fable.Remoting.Client, which
/// generates HTTP requests automatically.
/// </summary>
type ITodoApi =
    {
        /// <summary>Get all todo items.</summary>
        getTodos: unit -> Async<Todo list>
        /// <summary>Add a new todo item with the given text.</summary>
        addTodo: string -> Async<Todo>
        /// <summary>Toggle the done status of a todo item by its id.</summary>
        toggleTodo: Guid -> Async<Todo option>
    }

/// <summary>
/// Route configuration for the Fable.Remoting API.
/// </summary>
module Route =

    /// <summary>
    /// The base route builder for Fable.Remoting.
    /// Generates routes like /api/ITodoApi/getTodos.
    /// </summary>
    /// <param name="typeName">The type name of the API interface.</param>
    /// <param name="methodName">The method name being called.</param>
    /// <returns>The full route path.</returns>
    let builder typeName methodName =
        sprintf "/api/%s/%s" typeName methodName
