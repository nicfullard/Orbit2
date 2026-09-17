using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using Orbit.Agents.Contracts;

namespace Orbit.Agent;

/// <summary>The one-off commands: <c>configure</c> and <c>remove</c>. Modelled on a GitHub Actions runner's config script.</summary>
public static class Commands
{
    public const string Usage = """
        Orbit Agent - connects your network to Orbit so Orbit can check directory (LDAP / Active Directory) sign-ins.

        Usage:
          Orbit.Agent configure --url <orbit url> --token <registration token> [--replace]
              Registers this machine with Orbit. Get the command, token included, from Admin > Agents > New agent.
          Orbit.Agent run
              Connects to Orbit and waits for work. This is what a Windows service or systemd unit should start.
          Orbit.Agent remove
              De-registers this agent in Orbit and deletes its local configuration.

        The agent has no other settings: directory servers and the rest are managed in Orbit.
        """;

    public static async Task<int> ConfigureAsync(CliArgs args)
    {
        var url = args.Value("url");
        var token = args.Value("token");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token))
            return Fail("configure needs --url and --token. Copy the full command from Orbit: Admin > Agents > New agent.");

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var baseUri) || (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
            return Fail($"'{url}' is not a valid URL.");
        // User passwords travel over this connection. Plain http is accepted only for a development Orbit on this machine.
        if (baseUri.Scheme != Uri.UriSchemeHttps && !baseUri.IsLoopback)
            return Fail("The Orbit URL must be https:// so that passwords are never sent unencrypted.");

        if (AgentConfigStore.Exists && !args.Flag("replace"))
            return Fail($"This agent is already configured ({AgentConfigStore.Path}). Run 'remove' first, or pass --replace to overwrite it.");

        var config = new AgentConfig { Url = baseUri.GetLeftPart(UriPartial.Path).TrimEnd('/') };
        using var http = config.CreateHttpClient();
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(config.Endpoint(AgentProtocol.RegisterPath), new AgentRegistrationRequest
            {
                Token = token.Trim(),
                MachineName = Environment.MachineName,
                OsDescription = RuntimeInformation.OSDescription,
                Version = AgentWorker.Version
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return Fail($"Couldn't reach Orbit at {config.Url}: {ex.Message}");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return Fail("Orbit rejected the registration token. Tokens work once and expire after an hour - create a new one under Admin > Agents.");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return Fail("Orbit is rate-limiting registrations. Wait a minute and try again.");
            if (!response.IsSuccessStatusCode)
                return Fail($"Orbit answered {(int)response.StatusCode} {response.ReasonPhrase}. Check that the URL is your Orbit site.");

            var registration = await response.Content.ReadFromJsonAsync<AgentRegistrationResponse>();
            if (registration is null || string.IsNullOrEmpty(registration.Secret))
                return Fail("Orbit's answer wasn't understood. Check that the URL is your Orbit site and that the agent and Orbit versions match.");

            config.AgentId = registration.AgentId;
            config.Name = registration.Name;
            config.Secret = registration.Secret;
        }

        AgentConfigStore.Save(config);
        Console.WriteLine($"Registered with {config.Url} as \"{config.Name}\".");
        Console.WriteLine($"Configuration saved to {AgentConfigStore.Path}");
        Console.WriteLine("Start the agent with:  Orbit.Agent run   (or install it as a service - see deploy/README.md)");
        return 0;
    }

    public static async Task<int> RemoveAsync()
    {
        AgentConfig? config;
        try { config = AgentConfigStore.Load(); }
        catch (Exception ex) { config = null; Console.Error.WriteLine($"Couldn't read {AgentConfigStore.Path}: {ex.Message}"); }

        if (config is not null)
        {
            try
            {
                using var http = config.CreateHttpClient();
                using var request = new HttpRequestMessage(HttpMethod.Delete, config.Endpoint(AgentProtocol.RegistrationPath));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.Secret);
                using var response = await http.SendAsync(request);
                Console.WriteLine(response.IsSuccessStatusCode
                    ? "De-registered from Orbit."
                    : $"Orbit answered {(int)response.StatusCode}; the agent may already have been revoked there.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                Console.Error.WriteLine($"Couldn't reach Orbit to de-register ({ex.Message}). Revoke this agent under Admin > Agents.");
            }
        }

        AgentConfigStore.Delete();
        Console.WriteLine("Local configuration removed.");
        return 0;
    }

    public static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}

/// <summary>Minimal <c>--name value</c> / <c>--flag</c> parsing; not worth a dependency for three options.</summary>
public sealed class CliArgs
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

    public CliArgs(IEnumerable<string> args)
    {
        string? pending = null;
        foreach (var arg in args)
        {
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                pending = arg[2..];
                _options[pending] = null;
            }
            else if (pending is not null)
            {
                _options[pending] = arg;
                pending = null;
            }
        }
    }

    public string? Value(string name) => _options.GetValueOrDefault(name);
    public bool Flag(string name) => _options.ContainsKey(name);
}
