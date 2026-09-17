using Orbit.Agent;
using Orbit.Agent.Ldap;

var verb = args.Length > 0 && !args[0].StartsWith('-') ? args[0].ToLowerInvariant() : "run";
var rest = args.Length > 0 && !args[0].StartsWith('-') ? args[1..] : args;

switch (verb)
{
    case "configure":
        return await Commands.ConfigureAsync(new CliArgs(rest));
    case "remove":
        return await Commands.RemoveAsync();
    case "help" or "-h" or "--help" or "/?":
        Console.WriteLine(Commands.Usage);
        return 0;
    case "run":
        break;
    default:
        Console.Error.WriteLine($"Unknown command '{verb}'.\n");
        Console.WriteLine(Commands.Usage);
        return 1;
}

AgentConfig? config;
try
{
    config = AgentConfigStore.Load();
}
catch (Exception ex)
{
    return Commands.Fail($"Couldn't read {AgentConfigStore.Path}: {ex.Message}");
}
if (config is null)
    return Commands.Fail($"This agent isn't configured yet ({AgentConfigStore.Path} not found). In Orbit go to Admin > Agents > New agent and run the 'configure' command it gives you.");

// A Windows service starts in System32, so anchor everything to the executable's own folder.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = rest, ContentRootPath = AppContext.BaseDirectory });
builder.Services.AddWindowsService(o => o.ServiceName = "Orbit Agent");
builder.Services.AddSystemd();
builder.Services.AddSingleton(config);
builder.Services.AddSingleton<LdapDirectory>();
builder.Services.AddHostedService<AgentWorker>();

await builder.Build().RunAsync();
return 0;
