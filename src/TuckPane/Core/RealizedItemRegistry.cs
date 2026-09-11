namespace TuckPane.Core;

internal sealed class RealizedItemRegistry<THost> where THost : class
{
    private readonly Dictionary<string, THost> _hosts = new(StringComparer.OrdinalIgnoreCase);

    internal int Count => _hosts.Count;

    internal void Register(string identity, THost host)
    {
        foreach ((string existingIdentity, THost existingHost) in _hosts.ToArray())
        {
            if (ReferenceEquals(existingHost, host)) _hosts.Remove(existingIdentity);
        }
        _hosts[identity] = host;
    }

    internal void Unregister(THost host)
    {
        foreach ((string identity, THost registeredHost) in _hosts.ToArray())
        {
            if (ReferenceEquals(registeredHost, host)) _hosts.Remove(identity);
        }
    }

    internal bool TryGet(string identity, out THost host)
    {
        if (_hosts.TryGetValue(identity, out THost? candidate))
        {
            host = candidate;
            return true;
        }
        host = null!;
        return false;
    }
}
