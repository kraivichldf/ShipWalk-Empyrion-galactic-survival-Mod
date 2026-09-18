using System;
using System.Numerics;

namespace ShipWalk
{
    // Min/max are in ship-local metres. This is an occupancy envelope, not a
    // solid box collider: empty rooms remain empty and real floors stay distinct.
    internal readonly struct LocalVolume
    {
        public readonly Vector3 Min, Max;
        public Vector3 Size => Max - Min;
        public bool Valid => MotionMath.Finite(Min) && MotionMath.Finite(Max)
            && Max.X > Min.X && Max.Y > Min.Y && Max.Z > Min.Z;
        public LocalVolume(Vector3 min, Vector3 max) { Min = min; Max = max; }
        public bool Contains(Vector3 point, float padding = 0f)
            => Valid && MotionMath.Finite(point) && MotionMath.Finite(padding) && padding >= 0f
                && point.X >= Min.X - padding && point.X <= Max.X + padding
                && point.Y >= Min.Y - padding && point.Y <= Max.Y + padding
                && point.Z >= Min.Z - padding && point.Z <= Max.Z + padding;

        public static float DistanceSquared(Vector3 point, Vector3 center, Vector3 extents)
        {
            Vector3 distance = Vector3.Max(Vector3.Abs(point - center) - extents, Vector3.Zero);
            return distance.LengthSquared();
        }

        // The game's MinPos/MaxPos describe the grid's bounding faces. The
        // shared cell size is measured from StructToGlobalPos, covering CV/SV/HV.
        public static LocalVolume FromGrid(Vector3 minimum, Vector3 maximum, float cellSize)
        {
            if (!MotionMath.Finite(cellSize) || cellSize <= 0f) return default;
            return new LocalVolume(minimum * cellSize, maximum * cellSize);
        }

        // Enclose a transformed local box, including rotation/nonuniform scale.
        public static LocalVolume TransformBox(Vector3 center, Vector3 extents,
            Vector3 origin, Vector3 x, Vector3 y, Vector3 z)
        {
            Vector3 c = origin + x * center.X + y * center.Y + z * center.Z;
            Vector3 e = Vector3.Abs(x) * extents.X + Vector3.Abs(y) * extents.Y + Vector3.Abs(z) * extents.Z;
            return new LocalVolume(c - e, c + e);
        }
    }
}
