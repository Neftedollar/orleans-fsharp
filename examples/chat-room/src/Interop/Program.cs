using System;
using System.Threading.Tasks;
using ChatRoom.Grains;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.FSharp;
using Orleans.Hosting;

namespace ChatRoom.Interop;

/// <summary>
/// The same functional chat room as <c>src/Silo/Program.fs</c>, hosted and called entirely from
/// C#. The grain is the F# definition -- this project adds no grain of its own -- and the only
/// F#-shaped values a caller here touches are the ones the grain's own replies carry.
/// </summary>
public static class Program
{
    /// <summary>Hosts a single-silo cluster and drives the room through the facade.</summary>
    public static async Task<int> Main()
    {
        var builder = Host.CreateApplicationBuilder();

        // Hosting a functional grain from C# is the ordinary Orleans silo builder plus one call:
        // AddFunctionalGrain registers the definition, the fixed functional transport, and the F#
        // serialization the contract's argument and reply types need.
        builder.UseOrleans(silo =>
            silo.UseLocalhostClustering()
                .AddMemoryGrainStorage("Default")
                .AddFunctionalGrain(RoomFunctionalDef.room));

        var host = builder.Build();
        await host.StartAsync();

        var factory = host.Services.GetRequiredService<IGrainFactory>();

        // One call binds the contract to a key and returns the interface. Every binding rule --
        // name mapping, argument shape, reply shape -- was checked before this line returned.
        //
        // RoomApiModule, not RoomApi: an F# module whose name collides with a type in the same
        // namespace (here the RoomApi record) carries a "Module" suffix in its CLR name, which is
        // the name C# sees. Nothing else about the contract changes.
        var room = FunctionalGrainInterop.For<IChatRoom>(RoomApiModule.contract, factory, "general");

        Console.WriteLine("--- Chat Room, called from C# through FunctionalGrainInterop ---");
        Console.WriteLine();

        await room.Join("Alice");
        await room.Join("Bob");
        Console.WriteLine($"Members: {await room.MemberCount()}");

        // A Result reply is an FSharpResult. IsOk / ResultValue / ErrorValue read it; the error is
        // the grain's own discriminated union, and its cases are ordinary properties in C#.
        var posted = await room.Say("Alice", "Hey everyone!");
        Console.WriteLine($"Alice says 'Hey everyone!' -> {Describe(posted)}");

        var rejected = await room.Say("Charlie", "Can I join in?");
        Console.WriteLine($"Charlie (not a member) -> {Describe(rejected)}");

        var empty = await room.Say("Alice", "   ");
        Console.WriteLine($"Alice posts whitespace -> {Describe(empty)}");

        await room.NotifyTyping("Bob", true);
        await room.Say("Bob", "Hi Alice!");
        await room.Leave("Bob");
        Console.WriteLine($"Bob left. Members: {await room.MemberCount()}");

        Console.WriteLine();
        Console.WriteLine("--- History (an F# list of F# tuples, read from C#) ---");
        foreach (var (sender, message, at) in await room.History(10))
        {
            Console.WriteLine($"  [{at:HH:mm:ss}] {sender}: {message}");
        }

        Console.WriteLine();
        Console.WriteLine("C# interop demo complete.");
        await host.StopAsync();
        return 0;
    }

    /// <summary>Renders a Result reply the way a C# consumer reads one.</summary>
    private static string Describe(Microsoft.FSharp.Core.FSharpResult<int, ChatError> reply) =>
        reply.IsOk
            ? $"Ok (message #{reply.ResultValue})"
            : $"Error ({(reply.ErrorValue.IsNotAMember ? "NotAMember" : "EmptyMessage")})";
}
