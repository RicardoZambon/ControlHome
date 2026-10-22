namespace ControlHome.Connectors.SmartThings.Runtime;

/// <summary>
/// Which integration of this process runs each (OAuth app, location) pair. SmartThings hands out the SAME tokens when a
/// location is authorized again with the same app, and its refresh tokens are single-use: two integrations of one
/// location and app would spend each other's refresh token (the second refresh fails with <c>invalid_grant</c>). So only
/// the first runtime to claim a pair runs; the setup flow refuses to add the pair a second time. Thread-safe; shared by
/// every integration of the connector factory.
/// </summary>
internal sealed class LocationClaims
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(string ClientId, string LocationId), (Guid IntegrationId, object Owner)> _claims = [];

    /// <summary>
    /// Claims the pair for one run (<paramref name="owner"/>) of <paramref name="integrationId"/>; <c>false</c> when another
    /// integration holds it. A newer run of the same integration takes the claim over.
    /// </summary>
    public bool TryClaim(string clientId, string locationId, Guid integrationId, object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (_lock)
        {
            if (_claims.TryGetValue((clientId, locationId), out var holder) && holder.IntegrationId != integrationId)
            {
                return false;
            }

            _claims[(clientId, locationId)] = (integrationId, owner);
            return true;
        }
    }

    /// <summary>Releases the pair if the run <paramref name="owner"/> still holds it.</summary>
    public void Release(string clientId, string locationId, object owner)
    {
        lock (_lock)
        {
            if (_claims.TryGetValue((clientId, locationId), out var holder) && ReferenceEquals(holder.Owner, owner))
            {
                _claims.Remove((clientId, locationId));
            }
        }
    }

    /// <summary>Whether an integration other than <paramref name="integrationId"/> (none: a new setup) runs the pair.</summary>
    public bool IsClaimedByOther(string clientId, string locationId, Guid? integrationId)
    {
        lock (_lock)
        {
            return _claims.TryGetValue((clientId, locationId), out var holder) && holder.IntegrationId != integrationId;
        }
    }
}
