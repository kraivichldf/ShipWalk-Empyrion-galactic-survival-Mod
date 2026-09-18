using System;
using System.Numerics;

namespace ShipWalk
{
    // A value for one native block-contact query. No ambient scope or native
    // pose mutations; callbacks cannot leak this coordinate frame into seating.
    internal readonly struct LocalBlockCoordinates
    {
        private readonly LocalFramePose localGrid, nativeGrid;
        public readonly float Scale;
        public LocalBlockCoordinates(LocalFramePose nativeShip, LocalFramePose nativeGrid, float scale)
        {
            if (!nativeShip.Valid || !nativeGrid.Valid || !MotionMath.Finite(scale) || scale <= 0)
                throw new ArgumentException("Invalid ship-local block coordinates.");
            this.nativeGrid = nativeGrid; Scale = scale;
            localGrid = new LocalFramePose(nativeShip.ToLocalPoint(nativeGrid.Position),
                Quaternion.Normalize(Quaternion.Inverse(nativeShip.Rotation) * nativeGrid.Rotation));
        }
        public Vector3 Point(Vector3 localPoint) => localGrid.ToLocalPoint(localPoint) / Scale;
        private LocalBlockCoordinates(LocalFramePose local, LocalFramePose native, float scale, bool composed)
        { localGrid = local; nativeGrid = native; Scale = scale; }
        public LocalBlockCoordinates InParent(LocalFramePose child)
            => new LocalBlockCoordinates(new LocalFramePose(child.ToWorldPoint(localGrid.Position),
                Quaternion.Normalize(child.Rotation * localGrid.Rotation)), nativeGrid, Scale, true);
        public Vector3 NativePoint(Vector3 worldPoint) => nativeGrid.ToLocalPoint(worldPoint) / Scale;
        public void Bounds(Vector3 localMin, Vector3 localMax, out Vector3 min, out Vector3 max)
        {
            if (!MotionMath.Finite(localMin) || !MotionMath.Finite(localMax)
                || localMin.X > localMax.X || localMin.Y > localMax.Y || localMin.Z > localMax.Z)
                throw new ArgumentException("Invalid local contact bounds.");
            min = new Vector3(float.PositiveInfinity); max = new Vector3(float.NegativeInfinity);
            for (int i = 0; i < 8; i++)
            {
                Vector3 p = Point(new Vector3((i & 1) == 0 ? localMin.X : localMax.X,
                    (i & 2) == 0 ? localMin.Y : localMax.Y, (i & 4) == 0 ? localMin.Z : localMax.Z));
                min = Vector3.Min(min, p); max = Vector3.Max(max, p);
            }
        }
    }
}
