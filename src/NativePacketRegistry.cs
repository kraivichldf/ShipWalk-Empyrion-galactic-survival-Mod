using System;
using System.Collections.Generic;
using System.Linq;

namespace ShipWalk
{
    internal static class NativePacketRegistry
    {
        // Native EnumNetPackageId.ModGameEvent, not a newly allocated packet ID.
        public const int ModEventId = 139;

        // Called once during mod initialization, before native connections start.
        // Keep this native-type mapping for the process lifetime: removing it on
        // mod disposal could make already queued packets disconnect the peer.
        public static void Register(Dictionary<int, Type> byId, Dictionary<Type, int> byType, Type packetType)
        {
            if (byId == null || byType == null || packetType == null)
                throw new InvalidOperationException("Native packet registry is unavailable.");
            lock (byId)
            {
                if ((byId.TryGetValue(ModEventId, out Type existingType) && existingType != packetType)
                    || (byType.TryGetValue(packetType, out int existingId) && existingId != ModEventId)
                    || byId.Any(pair => pair.Value == packetType && pair.Key != ModEventId)
                    || byType.Any(pair => pair.Value == ModEventId && pair.Key != packetType))
                    throw new InvalidOperationException("Native ModGameEvent packet registration conflicts with an existing mapping.");
                if (!byId.ContainsKey(ModEventId)) byId.Add(ModEventId, packetType);
                if (!byType.ContainsKey(packetType)) byType.Add(packetType, ModEventId);
            }
        }
    }
}
