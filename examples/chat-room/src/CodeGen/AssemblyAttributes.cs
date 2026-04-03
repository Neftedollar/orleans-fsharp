using Orleans;

// Generate Orleans code for the F# grains project
[assembly: GenerateCodeForDeclaringAssembly(typeof(ChatRoom.Grains.ChatState))]
