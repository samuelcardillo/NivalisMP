using System;
using System.IO;

namespace NivalisTogether.Dedicated
{
    // Built only from the worker's native prefab and portal colliders. These are
    // not request fields: the client supplies a bounded boat pose, never a hull.
    public sealed class BoatTriggerBox
    {
        public float X, Y, Z, Qx, Qy, Qz, Qw = 1, HalfX, HalfY, HalfZ;
    }
    public sealed class BoatTriggerContact
    {
        public BoatTriggerBox PortalBox;
        public BoatTriggerBox[] HullBoxes = Array.Empty<BoatTriggerBox>();
    }
    public static class BoatTriggerGeometry
    {
        private readonly struct V
        {
            internal readonly double X, Y, Z;
            internal V(double x, double y, double z) { X = x; Y = y; Z = z; }
            internal static double Dot(V a, V b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
            internal static V Cross(V a, V b) => new V(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        }
        public static bool Overlaps(BoatTriggerBox a, BoatTriggerBox b)
        {
            Validate(a); Validate(b);
            var aa = Axes(a); var bb = Axes(b);
            var delta = new V((double)b.X - a.X, (double)b.Y - a.Y, (double)b.Z - a.Z);
            // The complete separating-axis test: each face normal plus the nine
            // edge cross products. No radius, padding or world-axis AABB proxy.
            for (int i = 0; i < 3; i++)
            {
                if (Separated(aa[i], delta, a, aa, b, bb) || Separated(bb[i], delta, a, aa, b, bb)) return false;
                for (int j = 0; j < 3; j++)
                    if (Separated(V.Cross(aa[i], bb[j]), delta, a, aa, b, bb)) return false;
            }
            return true;
        }
        private static bool Separated(V axis, V delta, BoatTriggerBox a, V[] aa, BoatTriggerBox b, V[] bb)
        {
            // A zero cross product is a redundant axis. Projection works without
            // normalization, including very small axes of nearly parallel boxes.
            double ra = a.HalfX * Math.Abs(V.Dot(axis, aa[0])) + a.HalfY * Math.Abs(V.Dot(axis, aa[1])) + a.HalfZ * Math.Abs(V.Dot(axis, aa[2]));
            double rb = b.HalfX * Math.Abs(V.Dot(axis, bb[0])) + b.HalfY * Math.Abs(V.Dot(axis, bb[1])) + b.HalfZ * Math.Abs(V.Dot(axis, bb[2]));
            return Math.Abs(V.Dot(axis, delta)) > ra + rb;
        }
        private static V[] Axes(BoatTriggerBox value)
        {
            double norm = Math.Sqrt((double)value.Qx * value.Qx + (double)value.Qy * value.Qy + (double)value.Qz * value.Qz + (double)value.Qw * value.Qw);
            double x = value.Qx / norm, y = value.Qy / norm, z = value.Qz / norm, w = value.Qw / norm;
            return new[] {
                new V(1 - 2 * (y * y + z * z), 2 * (x * y + w * z), 2 * (x * z - w * y)),
                new V(2 * (x * y - w * z), 1 - 2 * (x * x + z * z), 2 * (y * z + w * x)),
                new V(2 * (x * z + w * y), 2 * (y * z - w * x), 1 - 2 * (x * x + y * y)) };
        }
        private static void Validate(BoatTriggerBox value)
        {
            if (value == null || !Finite(value.X) || !Finite(value.Y) || !Finite(value.Z) ||
                !Finite(value.Qx) || !Finite(value.Qy) || !Finite(value.Qz) || !Finite(value.Qw) ||
                !Finite(value.HalfX) || !Finite(value.HalfY) || !Finite(value.HalfZ) ||
                value.HalfX <= 0 || value.HalfY <= 0 || value.HalfZ <= 0)
                throw new InvalidDataException("The native boat passage geometry is invalid.");
            double squaredNorm = (double)value.Qx * value.Qx + (double)value.Qy * value.Qy + (double)value.Qz * value.Qz + (double)value.Qw * value.Qw;
            if (squaredNorm < 0.99 || squaredNorm > 1.01)
                throw new InvalidDataException("The native boat passage rotation is invalid.");
        }
        private static bool Finite(float value) => float.IsFinite(value) && Math.Abs(value) <= 100000;
    }
}
