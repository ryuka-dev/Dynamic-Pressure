using System;
using System.IO;

namespace Ryuka.Sulfur.DynamicPressure
{
    internal enum CoopMessageKind : byte
    {
        SpawnNotice = 1,
        BossHealth = 2
    }

    /// <summary>The host added this unit; <see cref="Mutation"/> is 0 when it has none.</summary>
    internal struct SpawnNotice
    {
        public int SpawnId;
        public ushort UnitId;
        public int Mutation;
    }

    /// <summary>
    /// What the host tells clients, independent of how it gets there. Every message starts with
    /// a version byte; a client drops versions it does not know instead of guessing.
    /// </summary>
    internal static class CoopProtocol
    {
        public const byte Version = 1;

        private const int SpawnNoticeLength = 2 + 4 + 2 + 4;
        private const int BossHealthLength = 2 + 4;

        public static byte[] Encode(SpawnNotice notice)
        {
            using (MemoryStream stream = new MemoryStream(SpawnNoticeLength))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write(Version);
                writer.Write((byte)CoopMessageKind.SpawnNotice);
                writer.Write(notice.SpawnId);
                writer.Write(notice.UnitId);
                writer.Write(notice.Mutation);
                return stream.ToArray();
            }
        }

        public static byte[] EncodeBossHealth(float multiplier)
        {
            using (MemoryStream stream = new MemoryStream(BossHealthLength))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write(Version);
                writer.Write((byte)CoopMessageKind.BossHealth);
                writer.Write(multiplier);
                return stream.ToArray();
            }
        }

        /// <summary>Reads the header. False for an empty payload or a version this build does not speak.</summary>
        public static bool TryReadKind(byte[] payload, out CoopMessageKind kind, out byte version)
        {
            kind = 0;
            version = payload != null && payload.Length > 0 ? payload[0] : (byte)0;
            if (payload == null || payload.Length < 2 || version != Version)
            {
                return false;
            }

            kind = (CoopMessageKind)payload[1];
            return true;
        }

        public static bool TryDecode(byte[] payload, out SpawnNotice notice)
        {
            notice = default(SpawnNotice);
            if (payload == null || payload.Length != SpawnNoticeLength)
            {
                return false;
            }

            notice.SpawnId = BitConverter.ToInt32(payload, 2);
            notice.UnitId = BitConverter.ToUInt16(payload, 6);
            notice.Mutation = BitConverter.ToInt32(payload, 8);
            return notice.SpawnId > 0 && notice.UnitId != 0;
        }

        public static bool TryDecodeBossHealth(byte[] payload, out float multiplier)
        {
            multiplier = 0f;
            if (payload == null || payload.Length != BossHealthLength)
            {
                return false;
            }

            multiplier = BitConverter.ToSingle(payload, 2);
            return !float.IsNaN(multiplier)
                && !float.IsInfinity(multiplier)
                && multiplier >= BossHealthScaler.MinMultiplier
                && multiplier <= BossHealthScaler.MaxMultiplier;
        }
    }
}
