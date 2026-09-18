using UnityEngine;

namespace ShipWalk
{
    internal readonly struct RemotePoseArguments
    {
        public readonly Vector3 Position, Rotation;
        public RemotePoseArguments(Vector3 position, Vector3 rotation) { Position = position; Rotation = rotation; }
    }
}
