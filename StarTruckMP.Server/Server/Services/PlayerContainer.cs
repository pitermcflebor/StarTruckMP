using System.Collections.Concurrent;
using StarTruckMP.Server.Entities;

namespace StarTruckMP.Server.Server.Services;

public class PlayerContainer
{
    private readonly ConcurrentDictionary<int, Player> _players = new();

    public Player RegisterPlayer(int netId)
    {
        return _players.GetOrAdd(netId, static id => new Player(id));
    }

    public bool TryGetPlayer(int netId, out Player? player)
    {
        var found = _players.TryGetValue(netId, out var current);
        player = current;
        return found;
    }

    public bool RemovePlayer(int netId, out Player? player)
    {
        var removed = _players.TryRemove(netId, out var current);
        player = current;
        return removed;
    }

    public Player[] SnapshotPlayers()
    {
        return _players.Values.ToArray();
    }
}