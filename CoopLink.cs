using System;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using UnityEngine;

namespace Ryuka.Sulfur.DynamicPressure
{
    /// <summary>This machine's part in a co-op session.</summary>
    internal enum CoopRole
    {
        Offline,
        Host,
        Client
    }

    /// <summary>
    /// What the mod needs from a co-op session, in its own terms. Only <see cref="TogetherLink"/>
    /// knows which co-op mod is behind it; everything else talks to this.
    /// </summary>
    internal interface ICoopLink : IDisposable
    {
        CoopRole Role { get; }

        /// <summary>A short name for logs and the overlay.</summary>
        string Name { get; }

        /// <summary>Raised on the main thread with a payload the host sent. Messages from anyone else are dropped before this.</summary>
        event Action<byte[]> FromHost;

        /// <summary>Host only: sends <paramref name="payload"/> reliably to every client. False when there is no one to send to.</summary>
        bool SendToClients(byte[] payload);

        /// <summary>Promises that units <paramref name="owner"/> spawns exist on the host only, so the session copies them to clients.</summary>
        void DeclareSpawner(MonoBehaviour owner);

        /// <summary>Host: the session's id for a unit it copies to clients, or 0 when it has none yet.</summary>
        int GetSpawnId(Component unit);

        /// <summary>Client: the local copy of a host unit, or null when it has not arrived yet.</summary>
        Component ResolveSpawn(int spawnId);
    }

    internal sealed class OfflineLink : ICoopLink
    {
        public CoopRole Role
        {
            get { return CoopRole.Offline; }
        }

        public string Name
        {
            get { return "None"; }
        }

        public event Action<byte[]> FromHost
        {
            add { }
            remove { }
        }

        public bool SendToClients(byte[] payload)
        {
            return false;
        }

        public void DeclareSpawner(MonoBehaviour owner)
        {
        }

        public int GetSpawnId(Component unit)
        {
            return 0;
        }

        public Component ResolveSpawn(int spawnId)
        {
            return null;
        }

        public void Dispose()
        {
        }
    }

    internal static class CoopLinks
    {
        public const string TogetherGuid = "com.ryuka.sulfur.together";

        // The first release with a public way to name a copied unit (NetExternalSpawns.GetSpawnId).
        private static readonly Version TogetherMinimum = new Version(1, 4, 0);

        /// <summary>
        /// Picks the link for what is installed. SULFUR Together's types are only touched inside
        /// <see cref="CreateTogether"/>, so without it the assembly is never asked for.
        /// </summary>
        public static ICoopLink Create(ManualLogSource log)
        {
            PluginInfo together;
            if (!Chainloader.PluginInfos.TryGetValue(TogetherGuid, out together) || together == null)
            {
                return new OfflineLink();
            }

            Version version = together.Metadata.Version;
            if (version < TogetherMinimum)
            {
                log.LogWarning(
                    "SULFUR Together " + version + " is older than " + TogetherMinimum
                    + ". Dynamic Pressure won't sync in co-op, so each player may get enemies only they can see. Update SULFUR Together.");
                return new OfflineLink();
            }

            try
            {
                ICoopLink link = CreateTogether();
                log.LogInfo("Co-op: using SULFUR Together " + version + ".");
                return link;
            }
            catch (Exception e)
            {
                log.LogWarning("Co-op: couldn't hook into SULFUR Together " + version + ": " + e.GetType().Name + ": " + e.Message);
                return new OfflineLink();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ICoopLink CreateTogether()
        {
            return new TogetherLink();
        }
    }
}
