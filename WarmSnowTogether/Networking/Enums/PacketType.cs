namespace WarmSnowTogether.Networking.Enums
{
    public enum PacketType : byte
    {
        None = 0,
        PlayerPosition = 1,
        ConnectionRequest = 2,
        ConnectionAccepted = 3,
        ConnectionRejected = 4,
        Disconnect = 5,
        PlayerJoin = 6,
        PlayerLeave = 7,
        RoomChange = 8,
        EnemySpawn = 9,
        EnemyState = 10,
        EnemyDeath = 11,
        WorldState = 12,
        PlayerState = 13,
        LoadingState = 14,

        // [WST NetSync v1] host-authority additions
        SceneSync = 15,       // host -> client: switch to this Unity scene (requirement 1)
        DamageRequest = 16,   // client -> host: my hit on enemy X deals Y damage (requirement 4)
        SnapshotRequest = 17, // client -> host: send full enemy snapshot (requirements 3/4)

        // [WST Loot Sync v1] host-authoritative loot
        LootSpawn = 18,       // host -> client: a host-authoritative drop appeared (descriptor + position)
        LootConsumed = 19,    // any player -> host / host -> all: this drop was picked up (single shared copy)
    }
}
