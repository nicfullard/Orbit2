namespace Orbit.Agents.Contracts;

/// <summary>Routes the agent uses on the Orbit server. Shared so the two sides can't drift apart.</summary>
public static class AgentProtocol
{
    /// <summary>SignalR hub the agent connects out to; Orbit sends commands back down the same connection.</summary>
    public const string HubPath = "/agent/hub";
    /// <summary>POST: exchange a one-time registration token for the agent's long-lived secret.</summary>
    public const string RegisterPath = "/agent/register";
    /// <summary>DELETE (agent-authenticated): the agent removes its own registration.</summary>
    public const string RegistrationPath = "/agent/registration";
}

/// <summary>SignalR method names.</summary>
public static class AgentMethods
{
    /// <summary>Orbit -> agent, returns <see cref="LdapAuthResult"/>.</summary>
    public const string Authenticate = "Authenticate";
    /// <summary>Orbit -> agent, returns <see cref="LdapTestResult"/>.</summary>
    public const string TestDirectory = "TestDirectory";
    /// <summary>Orbit -> agent, returns <see cref="LdapListUsersResult"/>. Used by the directory import (agent 1.1 and later).</summary>
    public const string ListDirectoryUsers = "ListDirectoryUsers";
    /// <summary>Agent -> Orbit, sent after every (re)connect.</summary>
    public const string Hello = "Hello";
}

/// <summary>
/// What an agent can do, announced in <see cref="AgentHello"/>. Orbit only sends a command to an agent that
/// lists the matching capability, so new command types can be added without breaking agents already deployed.
/// </summary>
public static class AgentCapabilities
{
    public const string LdapAuthenticate = "ldap.authenticate";
    public const string LdapTest = "ldap.test";
    public const string LdapListUsers = "ldap.list-users";
}
