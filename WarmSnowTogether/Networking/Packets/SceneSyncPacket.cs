using System.IO;
using UnityEngine;
using WarmSnowTogether.Networking.Enums;
using WarmSnowTogether.Networking.Interfaces;

namespace WarmSnowTogether.Networking.Packets
{
    // [WST NetSync v1] Host -> client: "we are in this Unity scene now" + host anchor position.
    // Owner: lead. Requirement 1 (both players load the same level scene).
    public class SceneSyncPacket : INetworkPacket
    {
        public PacketType Type => PacketType.SceneSync;

        public int SceneIndex;        // SceneManager.GetActiveScene().buildIndex
        public string SceneName;      // for logs only
        public Vector2 HostPosition;  // host player world position, client teleports next to it
        public float HostRotation;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(SceneIndex);
            writer.Write(SceneName ?? string.Empty);
            writer.Write(HostPosition.x);
            writer.Write(HostPosition.y);
            writer.Write(HostRotation);
        }

        public void Deserialize(BinaryReader reader)
        {
            SceneIndex = reader.ReadInt32();
            SceneName = reader.ReadString();
            HostPosition = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            HostRotation = reader.ReadSingle();
        }
    }
}
