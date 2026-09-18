namespace ShipWalk
{
    internal static class ClientFramePolicy
    {
        public static bool CanPrepare(bool singlePlayer, bool multiplayerClient, bool vessel,
            bool remoteShip, bool usableLocalShipBody, bool undocked)
            => (singlePlayer || multiplayerClient) && vessel && undocked
                && (multiplayerClient || (!remoteShip && usableLocalShipBody));

        // The first multiplayer slice predicts only the local character. Native
        // ship simulation and handoff remain with their existing network owner.
        public static bool OwnsShipPhysics(bool singlePlayer) => singlePlayer;

        public static bool SameSessionMode(bool networkFrame, bool singlePlayer, bool multiplayerClient)
            => networkFrame ? multiplayerClient : singlePlayer;
    }
}
