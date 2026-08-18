//-----------------------------------------------------------------------------
// GeometryBuilder.cs
//
// Builds the extrusions for each preset inside the nested geometry family.
// Pockets and holes are made by appending loops to a solid layer's profile and
// stacking layers, not by void forms (which failed to cut reliably here).
//-----------------------------------------------------------------------------

namespace FlexiRfa;

internal static class GeometryBuilder
{
    internal static void ReplaceForms(Document geometryDocument, FlexiRfaArgs args)
    {
        using var transaction = new Transaction(geometryDocument, "Replace 3D orientation geometry");
        transaction.Start();

        var existingForms = new FilteredElementCollector(geometryDocument)
            .OfClass(typeof(GenericForm))
            .ToElementIds();
        if (existingForms.Count > 0)
            geometryDocument.Delete(existingForms);

        if (args.Preset is RotatableFamilyPreset.ElectricalSocket or RotatableFamilyPreset.ElectricalSocketSingle or RotatableFamilyPreset.ElectricalSocketQuadruple)
            BuildElectricalSocket(geometryDocument, args.Preset);
        else if (args.Preset == RotatableFamilyPreset.RectangularLightFixture)
            BuildRectangularLightFixture(geometryDocument, args.LightFixtureLength, args.LightFixtureWidth);
        else if (args.Preset == RotatableFamilyPreset.DataSocketDouble)
            BuildDataSocket(geometryDocument, portCount: 2);
        else if (args.Preset == RotatableFamilyPreset.DataOutletSingle)
            BuildDataSocket(geometryDocument, portCount: 1);
        else
            BuildPresetGeometry(geometryDocument, ResolveDimensions(args));

        transaction.Commit();
    }

    private static void BuildPresetGeometry(Document nestedDocument, DimensionSet dimensions)
    {
        // Height is the profile's length dimension; Depth is the vertical extrusion amount.
        var profile = dimensions.ProfileShape == ExtrusionProfileShape.Cylinder
            ? ProfileFactory.BuildCircleProfile(dimensions.Diameter)
            : ProfileFactory.BuildRectangleProfile(dimensions.Width, dimensions.Height);
        var extrusionHeight = UnitUtils.ConvertToInternalUnits(dimensions.Depth, UnitTypeId.Millimeters);
        var flangeHeight = dimensions.FlangeDepth.HasValue
            ? UnitUtils.ConvertToInternalUnits(dimensions.FlangeDepth.Value, UnitTypeId.Millimeters)
            : 0;

        if (dimensions.SurfaceMounted)
        {
            // The wide plate meets the ceiling at the origin and the body stacks away from it.
            var plateSketchPlane = SketchPlane.Create(nestedDocument, ProfileFactory.GetHorizontalPlaneAtOrigin(nestedDocument));
            nestedDocument.FamilyCreate.NewExtrusion(true, ProfileFactory.BuildCircleProfile(dimensions.FlangeDiameter!.Value), plateSketchPlane, flangeHeight);

            var bodySketchPlane = SketchPlane.Create(nestedDocument, ProfileFactory.GetHorizontalPlaneAtOrigin(nestedDocument, flangeHeight));
            nestedDocument.FamilyCreate.NewExtrusion(true, profile, bodySketchPlane, extrusionHeight);
            return;
        }

        var sketchPlane = SketchPlane.Create(nestedDocument, ProfileFactory.GetHorizontalPlaneAtOrigin(nestedDocument));
        nestedDocument.FamilyCreate.NewExtrusion(true, profile, sketchPlane, extrusionHeight);

        // Second, wider/shallower extrusion stacked on top of the body, e.g. a downlight's ceiling trim ring.
        if (dimensions.FlangeDiameter.HasValue && dimensions.FlangeDepth.HasValue)
        {
            var flangeSketchPlane = SketchPlane.Create(nestedDocument, ProfileFactory.GetHorizontalPlaneAtOrigin(nestedDocument, extrusionHeight));
            nestedDocument.FamilyCreate.NewExtrusion(true, ProfileFactory.BuildCircleProfile(dimensions.FlangeDiameter.Value), flangeSketchPlane, flangeHeight);
        }
    }

    // Wall plate with a raised inner panel carrying 1, 2 (stacked) or 4 (2x2 grid) recessed 40 mm outlets.
    private static void BuildElectricalSocket(Document nestedDocument, RotatableFamilyPreset preset)
    {
        const double outletDiameterMm = 40;
        const double pinDiameterMm = 5;
        const double padSizeMm = 55;

        var isQuadruple = preset == RotatableFamilyPreset.ElectricalSocketQuadruple;
        var plateWidthMm = isQuadruple ? 120 : 85;
        var plateHeightMm = preset == RotatableFamilyPreset.ElectricalSocketSingle ? 85 : 120;

        var faceDepthMm = 8;
        // 20 mm is the depth at which the pocket's wall actually reads under Revit's flat shading; shallower pads
        // render as a flat surface with only an outline.
        var panelOrPadDepthMm = 20;
        // The quadruple box is much deeper (device box), so most of the extra depth goes into the back layer.
        var backDepthMm = isQuadruple ? 40 - faceDepthMm - panelOrPadDepthMm : 2;
        // At least this much of the back layer stays a plain, uncut slab, so the pin holes never reach the visible
        // backside of the socket.
        var backClosureMm = Math.Min(2, backDepthMm);
        var backCutDepthMm = backDepthMm - backClosureMm;

        var backClosureDepth = UnitUtils.ConvertToInternalUnits(backClosureMm, UnitTypeId.Millimeters);
        var backCutDepth = UnitUtils.ConvertToInternalUnits(backCutDepthMm, UnitTypeId.Millimeters);
        var faceDepth = UnitUtils.ConvertToInternalUnits(faceDepthMm, UnitTypeId.Millimeters);
        var panelOrPadDepth = UnitUtils.ConvertToInternalUnits(panelOrPadDepthMm, UnitTypeId.Millimeters);
        var pinSpacing = UnitUtils.ConvertToInternalUnits(9.5, UnitTypeId.Millimeters);
        var outletCentres = GetOutletCentres(preset, plateHeightMm);

        // The outward-facing back slab is always a plain, uncut solid.
        var backSketchPlane = SketchPlane.Create(nestedDocument, ProfileFactory.GetHorizontalPlaneAtOrigin(nestedDocument));
        nestedDocument.FamilyCreate.NewExtrusion(true, ProfileFactory.BuildRoundedRectangleProfile(plateWidthMm, plateHeightMm, 8), backSketchPlane, backClosureDepth);

        // Pin holes cut through the remaining (inner) portion of the back layer, so they read as a real, deep pit
        // under flat shading without ever punching through to the visible backside.
        if (backCutDepthMm > 0)
        {
            var backInnerProfile = ProfileFactory.BuildRoundedRectangleProfile(plateWidthMm, plateHeightMm, 8);
            foreach (var (verticalOffset, horizontalOffset) in outletCentres)
            {
                foreach (var pinOffset in new[] { pinSpacing, -pinSpacing })
                    backInnerProfile.Append(ProfileFactory.BuildCircleLoop(pinDiameterMm, verticalOffset, horizontalOffset + pinOffset));
            }

            var backInnerSketchPlane = SketchPlane.Create(nestedDocument, ProfileFactory.GetHorizontalPlaneAtOrigin(nestedDocument, backClosureDepth));
            nestedDocument.FamilyCreate.NewExtrusion(true, backInnerProfile, backInnerSketchPlane, backCutDepth);
        }

        // Pin holes punch through this layer too, continuing the through-hole started in the back layer.
        var faceProfile = ProfileFactory.BuildRoundedRectangleProfile(plateWidthMm, plateHeightMm, 8);
        foreach (var (verticalOffset, horizontalOffset) in outletCentres)
        {
            foreach (var pinOffset in new[] { pinSpacing, -pinSpacing })
                faceProfile.Append(ProfileFactory.BuildCircleLoop(pinDiameterMm, verticalOffset, horizontalOffset + pinOffset));
        }

        var faceSketchPlane = SketchPlane.Create(nestedDocument, ProfileFactory.GetHorizontalPlaneAtOrigin(nestedDocument, backClosureDepth + backCutDepth));
        nestedDocument.FamilyCreate.NewExtrusion(true, faceProfile, faceSketchPlane, faceDepth);

        var topSketchPlane = SketchPlane.Create(nestedDocument, ProfileFactory.GetHorizontalPlaneAtOrigin(nestedDocument, backClosureDepth + backCutDepth + faceDepth));

        // Each outlet gets its own small raised pad with a hole cut through it, so the outlet itself reads as an
        // individual recessed pocket rather than sharing one continuous frame with its neighbours.
        foreach (var (verticalOffset, horizontalOffset) in outletCentres)
        {
            var padProfile = ProfileFactory.BuildRoundedRectangleProfile(padSizeMm, padSizeMm, 6, verticalOffset, horizontalOffset);
            padProfile.Append(ProfileFactory.BuildCircleLoop(outletDiameterMm, verticalOffset, horizontalOffset));
            nestedDocument.FamilyCreate.NewExtrusion(true, padProfile, topSketchPlane, panelOrPadDepth);
        }
    }

    // Surface-mounted batten luminaire: a shallow housing against the ceiling with a narrower diffuser below it.
    private static void BuildRectangularLightFixture(Document nestedDocument, double lengthMm, double widthMm)
    {
        const double housingDepthMm = 30;
        const double diffuserDepthMm = 25;
        const double diffuserInsetMm = 12;

        var length = lengthMm > 0 ? lengthMm : 1200;
        var width = widthMm > 0 ? widthMm : 100;
        var housingDepth = UnitUtils.ConvertToInternalUnits(housingDepthMm, UnitTypeId.Millimeters);
        var diffuserDepth = UnitUtils.ConvertToInternalUnits(diffuserDepthMm, UnitTypeId.Millimeters);

        var housingSketchPlane = SketchPlane.Create(nestedDocument, ProfileFactory.GetHorizontalPlaneAtOrigin(nestedDocument));
        nestedDocument.FamilyCreate.NewExtrusion(true, ProfileFactory.BuildRoundedRectangleProfile(width, length, 8), housingSketchPlane, housingDepth);

        var diffuserSketchPlane = SketchPlane.Create(nestedDocument, ProfileFactory.GetHorizontalPlaneAtOrigin(nestedDocument, housingDepth));
        nestedDocument.FamilyCreate.NewExtrusion(true, ProfileFactory.BuildRoundedRectangleProfile(width - diffuserInsetMm, length - diffuserInsetMm, 6), diffuserSketchPlane, diffuserDepth);
    }

    // Wall plate with a raised, stepped cover frame across the top and blind, tapered RJ45 pockets
    // in the lower half (wider flared entry over a narrower inner pocket).
    private static void BuildDataSocket(Document nestedDocument, int portCount)
    {
        const double plateWidthMm = 85;
        const double plateHeightMm = 85;

        const double backDepthMm = 17;
        const double innerPortDepthMm = 4;
        const double outerPortDepthMm = 5;

        const double hoodHeightMm = 28;
        const double hoodStepDepthMm = 2;
        const double lipHeightMm = 10;
        const double lipInsetMm = 4;
        const double lipStepDepthMm = 2;

        const double portWidthMm = 16;
        const double portHeightMm = 21;
        const double portSpacingMm = 6;
        const double portFlareMarginMm = 4;
        const double portVerticalOffsetMm = -10;

        var backDepth = UnitUtils.ConvertToInternalUnits(backDepthMm, UnitTypeId.Millimeters);
        var innerPortDepth = UnitUtils.ConvertToInternalUnits(innerPortDepthMm, UnitTypeId.Millimeters);
        var outerPortDepth = UnitUtils.ConvertToInternalUnits(outerPortDepthMm, UnitTypeId.Millimeters);
        var baseDepth = backDepth + innerPortDepth + outerPortDepth;
        var hoodStepDepth = UnitUtils.ConvertToInternalUnits(hoodStepDepthMm, UnitTypeId.Millimeters);
        var lipStepDepth = UnitUtils.ConvertToInternalUnits(lipStepDepthMm, UnitTypeId.Millimeters);

        var horizontalOffset = UnitUtils.ConvertToInternalUnits((portWidthMm + portSpacingMm) / 2, UnitTypeId.Millimeters);
        var verticalOffset = UnitUtils.ConvertToInternalUnits(portVerticalOffsetMm, UnitTypeId.Millimeters);
        // A lone port sits centred; a pair straddles the centre line.
        double[] portOffsets = portCount == 1 ? [0] : [-horizontalOffset, horizontalOffset];

        // Plain backing, no holes, so the pockets above never reach through to the back.
        var backSketchPlane = SketchPlane.Create(nestedDocument, ProfileFactory.GetHorizontalPlaneAtOrigin(nestedDocument));
        nestedDocument.FamilyCreate.NewExtrusion(true, ProfileFactory.BuildRoundedRectangleProfile(plateWidthMm, plateHeightMm, 8), backSketchPlane, backDepth);

        // Inner pocket layer: narrower square openings.
        var innerProfile = ProfileFactory.BuildRoundedRectangleProfile(plateWidthMm, plateHeightMm, 8);
        foreach (var portOffset in portOffsets)
            innerProfile.Append(ProfileFactory.BuildRectangleLoop(portWidthMm, portHeightMm, verticalOffset, portOffset));
        var innerSketchPlane = SketchPlane.Create(nestedDocument, ProfileFactory.GetHorizontalPlaneAtOrigin(nestedDocument, backDepth));
        nestedDocument.FamilyCreate.NewExtrusion(true, innerProfile, innerSketchPlane, innerPortDepth);

        // Outer pocket layer: wider, flared openings reaching the front face.
        var outerProfile = ProfileFactory.BuildRoundedRectangleProfile(plateWidthMm, plateHeightMm, 8);
        foreach (var portOffset in portOffsets)
            outerProfile.Append(ProfileFactory.BuildRectangleLoop(portWidthMm + portFlareMarginMm, portHeightMm + portFlareMarginMm, verticalOffset, portOffset));
        var outerSketchPlane = SketchPlane.Create(nestedDocument, ProfileFactory.GetHorizontalPlaneAtOrigin(nestedDocument, backDepth + innerPortDepth));
        nestedDocument.FamilyCreate.NewExtrusion(true, outerProfile, outerSketchPlane, outerPortDepth);

        // Raised hood across the top, stepped up in two stages like a hinged cable-entry flap.
        var hoodVerticalOffset = UnitUtils.ConvertToInternalUnits(plateHeightMm / 2 - hoodHeightMm / 2, UnitTypeId.Millimeters);
        var hoodSketchPlane = SketchPlane.Create(nestedDocument, ProfileFactory.GetHorizontalPlaneAtOrigin(nestedDocument, baseDepth));
        nestedDocument.FamilyCreate.NewExtrusion(true, ProfileFactory.BuildRoundedRectangleProfile(plateWidthMm, hoodHeightMm, 8, hoodVerticalOffset), hoodSketchPlane, hoodStepDepth);

        var lipVerticalOffset = UnitUtils.ConvertToInternalUnits(plateHeightMm / 2 - lipHeightMm / 2, UnitTypeId.Millimeters);
        var lipSketchPlane = SketchPlane.Create(nestedDocument, ProfileFactory.GetHorizontalPlaneAtOrigin(nestedDocument, baseDepth + hoodStepDepth));
        // Radius must be well below half lipHeightMm (5mm) to avoid zero-length segments.
        nestedDocument.FamilyCreate.NewExtrusion(true, ProfileFactory.BuildRoundedRectangleProfile(plateWidthMm - lipInsetMm * 2, lipHeightMm, 3, lipVerticalOffset), lipSketchPlane, lipStepDepth);
    }

    // Outlet centres in internal units: single sits at the origin, double stacks vertically, quadruple forms a 2x2 grid.
    private static IReadOnlyList<(double VerticalOffset, double HorizontalOffset)> GetOutletCentres(RotatableFamilyPreset preset, double plateHeightMm)
    {
        var quarterHeight = UnitUtils.ConvertToInternalUnits(plateHeightMm / 4, UnitTypeId.Millimeters);

        return preset switch
        {
            RotatableFamilyPreset.ElectricalSocketSingle => [(0d, 0d)],
            RotatableFamilyPreset.ElectricalSocketQuadruple =>
            [
                (quarterHeight, quarterHeight),
                (quarterHeight, -quarterHeight),
                (-quarterHeight, quarterHeight),
                (-quarterHeight, -quarterHeight),
            ],
            _ => [(quarterHeight, 0d), (-quarterHeight, 0d)],
        };
    }

    private readonly record struct DimensionSet(ExtrusionProfileShape ProfileShape, double Width, double Height, double Diameter, double Depth, double? FlangeDiameter = null, double? FlangeDepth = null, bool SurfaceMounted = false);

    // Preset dimensions win outright; the custom fields keep stale values while hidden, so they are ignored here.
    private static DimensionSet ResolveDimensions(FlexiRfaArgs args)
    {
        var preset = GetPresetDefaults(args.Preset);
        if (preset is null)
            return new DimensionSet(args.ProfileShape, args.Width, args.Height, args.Diameter, args.Depth);

        var diameter = preset.Value.Diameter;
        var flangeDiameter = preset.Value.FlangeDiameter;
        var presetDiameter = args.Preset switch
        {
            RotatableFamilyPreset.Downlight => args.DownlightDiameter,
            RotatableFamilyPreset.SmokeDetector => args.SmokeDetectorDiameter,
            _ => 0,
        };

        if (presetDiameter > 0)
        {
            // Keep the preset's lip width by growing the flange with the body.
            var lipWidth = preset.Value.FlangeDiameter!.Value - preset.Value.Diameter;
            diameter = presetDiameter;
            flangeDiameter = diameter + lipWidth;
        }

        return preset.Value with { Diameter = diameter, FlangeDiameter = flangeDiameter };
    }

    private static DimensionSet? GetPresetDefaults(RotatableFamilyPreset preset) => preset switch
    {
        // Narrower recessed body plus a wider, shallow trim ring sitting on top of it.
        RotatableFamilyPreset.Downlight => new DimensionSet(ExtrusionProfileShape.Cylinder, 0, 0, 150, 100, FlangeDiameter: 220, FlangeDepth: 15),
        // Shallow sensor chamber hanging below the wider plate that sits against the ceiling.
        RotatableFamilyPreset.SmokeDetector => new DimensionSet(ExtrusionProfileShape.Cylinder, 0, 0, 85, 20, FlangeDiameter: 110, FlangeDepth: 15, SurfaceMounted: true),
        _ => null,
    };
}
