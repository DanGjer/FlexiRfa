//-----------------------------------------------------------------------------
// ProfileFactory.cs
//
// Reusable sketch-profile builders. Everything here is pure geometry: it takes
// millimetre dimensions and returns Revit curve loops in internal units.
//-----------------------------------------------------------------------------

namespace FlexiRfa;

internal static class ProfileFactory
{
    // Reference Level sits at the family origin, matching the "Center (Front/Back)" / "Center (Left/Right)" planes.
    internal static Plane GetHorizontalPlaneAtOrigin(Document familyDocument, double zOffset = 0)
    {
        var level = new FilteredElementCollector(familyDocument)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(l => Math.Abs(l.Elevation))
            .FirstOrDefault();

        var elevation = level?.Elevation ?? 0;
        return Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, elevation + zOffset));
    }

    internal static CurveArrArray BuildRectangleProfile(double widthMm, double depthMm)
    {
        var halfWidth = UnitUtils.ConvertToInternalUnits(widthMm, UnitTypeId.Millimeters) / 2;
        var halfDepth = UnitUtils.ConvertToInternalUnits(depthMm, UnitTypeId.Millimeters) / 2;

        var p0 = new XYZ(-halfWidth, -halfDepth, 0);
        var p1 = new XYZ(halfWidth, -halfDepth, 0);
        var p2 = new XYZ(halfWidth, halfDepth, 0);
        var p3 = new XYZ(-halfWidth, halfDepth, 0);

        var loop = new CurveArray();
        loop.Append(Line.CreateBound(p0, p1));
        loop.Append(Line.CreateBound(p1, p2));
        loop.Append(Line.CreateBound(p2, p3));
        loop.Append(Line.CreateBound(p3, p0));

        var profile = new CurveArrArray();
        profile.Append(loop);
        return profile;
    }

    internal static CurveArrArray BuildRoundedRectangleProfile(double widthMm, double depthMm, double cornerRadiusMm, double verticalOffset = 0, double horizontalOffset = 0)
    {
        var profile = new CurveArrArray();
        profile.Append(BuildRoundedRectangleLoop(widthMm, depthMm, cornerRadiusMm, verticalOffset, horizontalOffset));
        return profile;
    }

    // Bare loop variant, needed wherever a single CurveArray is required instead of a CurveArrArray
    // (e.g. Blend profiles, which don't support multiple/holed loops).
    internal static CurveArray BuildRoundedRectangleLoop(double widthMm, double depthMm, double cornerRadiusMm, double verticalOffset = 0, double horizontalOffset = 0)
    {
        var halfWidth = UnitUtils.ConvertToInternalUnits(widthMm, UnitTypeId.Millimeters) / 2;
        var halfDepth = UnitUtils.ConvertToInternalUnits(depthMm, UnitTypeId.Millimeters) / 2;
        var r = Math.Min(UnitUtils.ConvertToInternalUnits(cornerRadiusMm, UnitTypeId.Millimeters), Math.Min(halfWidth, halfDepth) - 1e-6);
        var centre = new XYZ(horizontalOffset, verticalOffset, 0);

        if (r <= 0)
            return BuildRectangleLoop(widthMm, depthMm, verticalOffset, horizontalOffset);

        var loop = new CurveArray();

        loop.Append(Line.CreateBound(centre + new XYZ(-halfWidth + r, -halfDepth, 0), centre + new XYZ(halfWidth - r, -halfDepth, 0)));
        loop.Append(CornerArc(centre + new XYZ(halfWidth - r, -halfDepth + r, 0), centre + new XYZ(halfWidth - r, -halfDepth, 0), centre + new XYZ(halfWidth, -halfDepth + r, 0)));

        loop.Append(Line.CreateBound(centre + new XYZ(halfWidth, -halfDepth + r, 0), centre + new XYZ(halfWidth, halfDepth - r, 0)));
        loop.Append(CornerArc(centre + new XYZ(halfWidth - r, halfDepth - r, 0), centre + new XYZ(halfWidth, halfDepth - r, 0), centre + new XYZ(halfWidth - r, halfDepth, 0)));

        loop.Append(Line.CreateBound(centre + new XYZ(halfWidth - r, halfDepth, 0), centre + new XYZ(-halfWidth + r, halfDepth, 0)));
        loop.Append(CornerArc(centre + new XYZ(-halfWidth + r, halfDepth - r, 0), centre + new XYZ(-halfWidth + r, halfDepth, 0), centre + new XYZ(-halfWidth, halfDepth - r, 0)));

        loop.Append(Line.CreateBound(centre + new XYZ(-halfWidth, halfDepth - r, 0), centre + new XYZ(-halfWidth, -halfDepth + r, 0)));
        loop.Append(CornerArc(centre + new XYZ(-halfWidth + r, -halfDepth + r, 0), centre + new XYZ(-halfWidth, -halfDepth + r, 0), centre + new XYZ(-halfWidth + r, -halfDepth, 0)));

        return loop;

        Arc CornerArc(XYZ arcCentre, XYZ start, XYZ end)
        {
            var mid = arcCentre + (start - arcCentre + (end - arcCentre)).Normalize().Multiply(r);
            return Arc.Create(start, end, mid);
        }
    }

    internal static CurveArrArray BuildCircleProfile(double diameterMm, double verticalOffset = 0, double horizontalOffset = 0)
    {
        var profile = new CurveArrArray();
        profile.Append(BuildCircleLoop(diameterMm, verticalOffset, horizontalOffset));
        return profile;
    }

    internal static CurveArray BuildCircleLoop(double diameterMm, double verticalOffset = 0, double horizontalOffset = 0)
    {
        var radius = UnitUtils.ConvertToInternalUnits(diameterMm, UnitTypeId.Millimeters) / 2;
        var centre = new XYZ(horizontalOffset, verticalOffset, 0);

        var loop = new CurveArray();
        loop.Append(Arc.Create(centre, radius, 0, Math.PI, XYZ.BasisX, XYZ.BasisY));
        loop.Append(Arc.Create(centre, radius, Math.PI, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY));
        return loop;
    }

    internal static CurveArray BuildRectangleLoop(double widthMm, double heightMm, double verticalOffset = 0, double horizontalOffset = 0)
    {
        var halfWidth = UnitUtils.ConvertToInternalUnits(widthMm, UnitTypeId.Millimeters) / 2;
        var halfHeight = UnitUtils.ConvertToInternalUnits(heightMm, UnitTypeId.Millimeters) / 2;
        var centre = new XYZ(horizontalOffset, verticalOffset, 0);

        var loop = new CurveArray();
        loop.Append(Line.CreateBound(centre + new XYZ(-halfWidth, -halfHeight, 0), centre + new XYZ(halfWidth, -halfHeight, 0)));
        loop.Append(Line.CreateBound(centre + new XYZ(halfWidth, -halfHeight, 0), centre + new XYZ(halfWidth, halfHeight, 0)));
        loop.Append(Line.CreateBound(centre + new XYZ(halfWidth, halfHeight, 0), centre + new XYZ(-halfWidth, halfHeight, 0)));
        loop.Append(Line.CreateBound(centre + new XYZ(-halfWidth, halfHeight, 0), centre + new XYZ(-halfWidth, -halfHeight, 0)));
        return loop;
    }
}
