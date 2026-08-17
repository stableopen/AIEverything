using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace AIEverything.Content.Ipc;

public static class ContentPipeNaming
{
    public static string ForCurrentUser(string prefix = "aieverything-content")
    {
        if (string.IsNullOrWhiteSpace(prefix) ||
            prefix.IndexOfAny(['\\', '/']) >= 0)
        {
            throw new ArgumentException("Pipe prefix must be a simple local name.", nameof(prefix));
        }

        var identity = GetIdentity();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"{prefix}-{Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant()}";
    }

    private static string GetIdentity()
    {
        if (!OperatingSystem.IsWindows())
        {
            return $"{Environment.UserDomainName}\\{Environment.UserName}";
        }

        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ??
               $"{Environment.UserDomainName}\\{Environment.UserName}";
    }
}
