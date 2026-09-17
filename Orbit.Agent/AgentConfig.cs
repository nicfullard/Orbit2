using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Orbit.Agent;

/// <summary>
/// Everything the agent knows about itself: where Orbit is and the credential Orbit issued it. Written once by
/// <c>configure</c>. There is deliberately nothing else - directory servers, filters and the rest are managed in
/// Orbit and arrive with each command.
/// </summary>
public sealed class AgentConfig
{
    public string Url { get; set; } = string.Empty;
    public Guid AgentId { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>The raw credential, in memory only. On disk it is <see cref="AgentConfigStore"/>'s job to protect it.</summary>
    public string Secret { get; set; } = string.Empty;

    public Uri Endpoint(string path) => new(new Uri(Url.TrimEnd('/') + "/"), path.TrimStart('/'));

    /// <summary>
    /// Outbound calls go through the machine's configured proxy (HTTPS_PROXY or the system setting), which is what a
    /// corporate network usually requires - except to an Orbit on this same machine (development), which no proxy can reach.
    /// </summary>
    public bool UseProxy => !new Uri(Url).IsLoopback;

    public HttpClient CreateHttpClient() =>
        new(new HttpClientHandler { UseProxy = UseProxy }) { Timeout = TimeSpan.FromSeconds(30) };
}

/// <summary>
/// Reads and writes <c>agent.json</c> beside the executable (or in <c>ORBIT_AGENT_HOME</c>), the same arrangement as a
/// GitHub Actions runner. The secret is encrypted with DPAPI (machine scope, so a service account other than the one
/// that ran <c>configure</c> can read it) on Windows, and protected by file mode 600 elsewhere.
/// </summary>
public static class AgentConfigStore
{
    private const string DpapiPrefix = "dpapi:";
    private const string PlainPrefix = "plain:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Orbit.Agent.Secret.v1");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string Path
    {
        get
        {
            var home = Environment.GetEnvironmentVariable("ORBIT_AGENT_HOME");
            return System.IO.Path.Combine(string.IsNullOrWhiteSpace(home) ? AppContext.BaseDirectory : home, "agent.json");
        }
    }

    public static bool Exists => File.Exists(Path);

    public static AgentConfig? Load()
    {
        if (!Exists) return null;
        var stored = JsonSerializer.Deserialize<StoredConfig>(File.ReadAllText(Path), Json);
        if (stored is null || string.IsNullOrWhiteSpace(stored.Url) || string.IsNullOrWhiteSpace(stored.Secret)) return null;
        return new AgentConfig { Url = stored.Url, AgentId = stored.AgentId, Name = stored.Name ?? string.Empty, Secret = Unprotect(stored.Secret) };
    }

    public static void Save(AgentConfig config)
    {
        var stored = new StoredConfig { Url = config.Url, AgentId = config.AgentId, Name = config.Name, Secret = Protect(config.Secret) };
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(stored, Json));
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public static void Delete()
    {
        if (Exists) File.Delete(Path);
    }

    private static string Protect(string secret)
    {
        if (OperatingSystem.IsWindows())
            return DpapiPrefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.LocalMachine));
        return PlainPrefix + secret;
    }

    private static string Unprotect(string stored)
    {
        if (stored.StartsWith(DpapiPrefix, StringComparison.Ordinal))
        {
            if (!OperatingSystem.IsWindows())
                throw new InvalidOperationException("agent.json was written on Windows and can't be read here. Run 'configure' again on this machine.");
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored[DpapiPrefix.Length..]), Entropy, DataProtectionScope.LocalMachine));
        }
        return stored.StartsWith(PlainPrefix, StringComparison.Ordinal) ? stored[PlainPrefix.Length..] : stored;
    }

    private sealed class StoredConfig
    {
        public string? Url { get; set; }
        public Guid AgentId { get; set; }
        public string? Name { get; set; }
        public string? Secret { get; set; }
    }
}
