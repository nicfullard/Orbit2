using System.Text;

namespace Orbit.Agent.Ldap;

public static class LdapFilter
{
    /// <summary>
    /// Escapes a value for use inside an LDAP search filter (RFC 4515 §3). Whatever someone types into the sign-in box
    /// ends up here, so without this a name like <c>*)(objectClass=*</c> would rewrite the filter instead of being
    /// searched for.
    /// </summary>
    public static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\5c"); break;
                case '*': sb.Append(@"\2a"); break;
                case '(': sb.Append(@"\28"); break;
                case ')': sb.Append(@"\29"); break;
                case '\0': sb.Append(@"\00"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Puts the escaped sign-in name into the configured filter, e.g. <c>(mail={0})</c>.</summary>
    public static string ForUser(string userFilter, string username) =>
        userFilter.Replace("{0}", Escape(username), StringComparison.Ordinal);
}
