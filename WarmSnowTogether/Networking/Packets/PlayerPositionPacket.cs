using System.IO;
using UnityEngine;
using WarmSnowTogether.Networking.Interfaces;
using WarmSnowTogether.Networking.Enums;

namespace WarmSnowTogether.Networking.Packets
{
    public class PlayerPositionPacket : INetworkPacket
    {
        public PacketType Type => PacketType.PlayerPosition;

        public ulong PlayerId;
        public Vector2 Position;
        public Vector2 Velocity;
        public float Rotation;
        public bool IsGrounded;
        public bool IsDodgeRolling;
        public int AnimationState;
        public int SpriteId;
        public bool FlipX;

        // [WST NetSync v1] remote avatar visuals (requirement 2)
        public int DirIndex;      // (int)PlayerAnimControl.currentDir : UP=0 DOWN=1 LEFT=2 RIGHT=3
        public string AnimName;   // best-effort current skeleton animation name, may be empty

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(PlayerId);
            writer.Write(Position.x);
            writer.Write(Position.y);
            writer.Write(Velocity.x);
            writer.Write(Velocity.y);
            writer.Write(Rotation);
            writer.Write(IsGrounded);
            writer.Write(IsDodgeRolling);
            writer.Write(AnimationState);
            writer.Write(SpriteId);
            writer.Write(FlipX);
            writer.Write(DirIndex);
            writer.Write(AnimName ?? string.Empty);
        }

        public void Deserialize(BinaryReader reader)
        {
            PlayerId = reader.ReadUInt64();
            Position = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            Velocity = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            Rotation = reader.ReadSingle();
            IsGrounded = reader.ReadBoolean();
            IsDodgeRolling = reader.ReadBoolean();
            AnimationState = reader.ReadInt32();
            SpriteId = reader.ReadInt32();
            FlipX = reader.ReadBoolean();
            DirIndex = reader.ReadInt32();
            AnimName = reader.ReadString();
        }
    }
}
