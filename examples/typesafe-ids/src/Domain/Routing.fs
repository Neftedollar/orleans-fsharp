/// <summary>
/// Active Patterns for message routing -- IMPOSSIBLE in C#.
/// Active Patterns allow custom pattern matching decomposition without modifying the matched type.
/// In C#, this would require if/else chains, visitor pattern, or strategy class hierarchies.
/// </summary>
module TypeSafeIds.Domain.Routing

open System

/// <summary>
/// An incoming message to the router grain.
/// </summary>
type IncomingMessage =
    {
        /// <summary>Numeric sender identifier.</summary>
        SenderId: int64
        /// <summary>The message body text.</summary>
        Content: string
        /// <summary>When the message was created.</summary>
        Timestamp: DateTime
        /// <summary>Whether the sender has VIP status.</summary>
        IsVip: bool
        /// <summary>Spam probability score between 0.0 and 1.0.</summary>
        SpamScore: float
    }

/// <summary>
/// Active Pattern: classify message priority without if/else chains.
/// In C#, this would be a series of if statements or a strategy pattern class hierarchy.
/// The compiler guarantees exhaustive matching on all four cases.
/// </summary>
/// <param name="msg">The incoming message to classify.</param>
/// <returns>One of HighPriority, Normal, LowPriority, or Spam.</returns>
let (|HighPriority|Normal|LowPriority|Spam|) (msg: IncomingMessage) =
    if msg.SpamScore > 0.8 then Spam
    elif msg.IsVip then HighPriority
    elif msg.Content.Length > 500 then LowPriority
    else Normal

/// <summary>
/// Active Pattern: detect message intent from content.
/// Decomposes a string into one of four intent categories.
/// </summary>
/// <param name="content">The message content to analyze.</param>
/// <returns>One of Question, Command, Greeting, or Unknown.</returns>
let (|Question|Command|Greeting|Unknown|) (content: string) =
    let lower = content.ToLowerInvariant()
    if lower.EndsWith("?") then Question
    elif lower.StartsWith("/") then Command
    elif lower.StartsWith("hi") || lower.StartsWith("hello") then Greeting
    else Unknown

/// <summary>
/// Route a message using composed active patterns -- reads like English.
/// Both the priority pattern and the intent pattern compose seamlessly via nested match.
/// </summary>
/// <param name="msg">The incoming message to route.</param>
/// <returns>A queue name string indicating where the message should be delivered.</returns>
let routeMessage (msg: IncomingMessage) : string =
    match msg with
    | Spam -> "dropped:spam"
    | HighPriority ->
        match msg.Content with
        | Question -> "vip:question-queue"
        | Command -> "vip:command-processor"
        | _ -> "vip:general"
    | Normal ->
        match msg.Content with
        | Question -> "standard:question-queue"
        | Command -> "standard:command-processor"
        | Greeting -> "standard:greeting-bot"
        | Unknown -> "standard:general"
    | LowPriority -> "batch:low-priority"
