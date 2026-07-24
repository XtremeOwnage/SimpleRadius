using SimpleRadius.Models;

namespace SimpleRadius.Services;

/// <summary>Outcome of applying client policy to an Access-Request.</summary>
public sealed record AuthorizationResult(bool IsAccepted, ClientDevice? Client, int VlanId, string? Reason)
{
    public static AuthorizationResult Accept(ClientDevice client, int vlanId) =>
        new(true, client, vlanId, null);

    public static AuthorizationResult Reject(string reason, ClientDevice? client = null) =>
        new(false, client, 0, reason);
}
