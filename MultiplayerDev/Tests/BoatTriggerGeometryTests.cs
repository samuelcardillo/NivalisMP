using System;
using System.IO;
using NivalisTogether.Dedicated;

public static class BoatTriggerGeometryTests
{
    private static int checks;
    private static BoatTriggerBox Box(float x = 0, float y = 0, float z = 0, float hx = 1, float hy = 1, float hz = 1, float yaw = 0)
        => new BoatTriggerBox { X = x, Y = y, Z = z, HalfX = hx, HalfY = hy, HalfZ = hz,
            Qy = MathF.Sin(yaw * MathF.PI / 360), Qw = MathF.Cos(yaw * MathF.PI / 360) };
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    private static void Contact(BoatTriggerBox a, BoatTriggerBox b, bool expected, string message)
    { Check(BoatTriggerGeometry.Overlaps(a, b) == expected, message); Check(BoatTriggerGeometry.Overlaps(b, a) == expected, message + " (symmetric)"); }
    private static void Reject(BoatTriggerBox a, string message)
    { bool rejected = false; try { BoatTriggerGeometry.Overlaps(a, Box()); } catch (InvalidDataException) { rejected = true; } Check(rejected, message); }
    public static int Run()
    {
        Contact(Box(), Box(), true, "coincident native boxes overlap");
        Contact(Box(), Box(2), true, "exact native face contact has no gap");
        Contact(Box(), Box(2.001f), false, "no extra proximity radius is added");
        Contact(Box(), Box(y: 2.001f), false, "vertical separation cannot enter the boat portal");
        Contact(Box(), Box(z: 2.001f), false, "forward separation rejects");
        Contact(Box(hx: 8, hy: 3, hz: 4), Box(hx: 0.01f, hy: 0.01f, hz: 0.01f), true, "contained small box remains contact");
        Contact(Box(7, 0, 7), Box(hx: 0.5f, hz: 10, yaw: 45), true, "rotated long hull touches even with center farther than four meters");
        Contact(Box(7, 0, -7), Box(hx: 0.5f, hz: 10, yaw: 45), false, "rotated hull world bounds cannot grant a corner near miss");
        Contact(Box(hx: 0.2f, hz: 10, yaw: 45), Box(2, 0, -2, hx: 0.2f, hz: 10, yaw: 45), false, "parallel narrow hulls remain separated inside overlapping world bounds");
        Contact(Box(hx: 0.2f, hz: 10, yaw: 45), Box(0.1f, 0, 0.1f, hx: 0.2f, hz: 10, yaw: 45.00001f), true, "nearly parallel axes remain stable");
        var skewA = Box(hx: 3, hy: 0.2f, hz: 0.2f);
        skewA.Qx = 0.69931265f; skewA.Qy = 0.6805245f; skewA.Qz = 0.12788672f; skewA.Qw = 0.17746323f;
        var skewB = Box(-0.14172843f, -1.093979f, -1.9268247f, hx: 3, hy: 0.2f, hz: 0.2f);
        skewB.Qx = -0.40528414f; skewB.Qy = -0.30328603f; skewB.Qz = -0.51021266f; skewB.Qw = 0.69530237f;
        Contact(skewA, skewB, false, "edge cross-product separation rejects skew boxes whose six face projections overlap");
        var nativePortal = Box(269.25f, -1.46f, -348.43f, 7.5f, 7.5f, 7.5f);
        nativePortal.Qy = 0.1467358172f; nativePortal.Qw = 0.9891756773f;
        var nativeHull = Box(256.37417399f, -13.16462015f, -342.82918110f, 7.589999319f, 12.352499035f, 2.744999722f);
        nativeHull.Qx = 0.00413761681f; nativeHull.Qy = 0.97789675642f; nativeHull.Qz = -0.00256375232f; nativeHull.Qw = -0.20903167485f;
        Contact(nativePortal, nativeHull, true, "recorded Docks-to-MountainTown native boat trigger touches before its center reaches the portal");
        nativeHull.X -= 100;
        Contact(nativePortal, nativeHull, false, "same recorded hull away from the passage has no overlap");
        Reject(null, "missing native geometry rejects");
        Reject(Box(hx: 0), "zero native extent rejects");
        Reject(Box(hy: -1), "negative native extent rejects");
        Reject(Box(hz: float.NaN), "nonfinite native extent rejects");
        Reject(Box(float.PositiveInfinity), "nonfinite native position rejects");
        Reject(Box(100001), "unbounded native position rejects");
        var invalidRotation = Box(); invalidRotation.Qw = 0; Reject(invalidRotation, "zero rotation rejects");
        invalidRotation.Qw = 2; Reject(invalidRotation, "scaled non-unit rotation rejects");
        invalidRotation.Qw = float.NaN; Reject(invalidRotation, "nonfinite rotation rejects");
        var signed = Box(yaw: 37); var negative = Box(yaw: 37);
        negative.Qy = -negative.Qy; negative.Qw = -negative.Qw;
        Contact(signed, negative, true, "opposite quaternion signs preserve the same native rotation");
        return checks;
    }
}
