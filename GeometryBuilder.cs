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

        if (args.Preset == RotatableFamilyPreset.ElectricalSocket)
            BuildBeveledSocket(geometryDocument, plateWidthMm: 85, plateHeightMm: 120, outletCentresMm: [(0, 25), (0, -25)]);
        else if (args.Preset == RotatableFamilyPreset.ElectricalSocketSingle)
            BuildBeveledSocket(geometryDocument, plateWidthMm: 85, plateHeightMm: 85, outletCentresMm: [(0, 0)]);
        else if (args.Preset == RotatableFamilyPreset.ElectricalSocketQuadruple)
            BuildBeveledSocket(geometryDocument, plateWidthMm: 140, plateHeightMm: 140, outletCentresMm: [(-27, 27), (27, 27), (-27, -27), (27, -27)]);
        else if (args.Preset == RotatableFamilyPreset.RectangularLightFixture)
            BuildRectangularLightFixture(geometryDocument, args.LightFixtureLength, args.LightFixtureWidth);
        else if (args.Preset == RotatableFamilyPreset.DataSocketDouble)
            BuildBeveledSocket(
                geometryDocument,
                plateWidthMm: 85,
                plateHeightMm: 85,
                outletCentresMm: [(0, 25), (0, -25)],
                bevelTopMm: 15,
                frontCapSizeMm: (65, 50),
                frontCapStartMm: 15,
                frontCapVerticalOffsetMm: -7.5,
                secondFrontCapSizeMm: (65, 5),
                secondFrontCapVerticalOffsetMm: 30,
                secondFrontCapStartMm: 15,
                frontPortSizeMm: (12, 12),
                frontPortCentresMm: [(-10, 8), (10, 8)],
                frontPortStartMm: 16,
                frontPortProudMm: 1,
                includeOutletVoids: false,
                includeRoundPins: false,
                includeEarthTabs: false);
        else if (args.Preset == RotatableFamilyPreset.DataOutletSingle)
            BuildBeveledSocket(
                geometryDocument,
                plateWidthMm: 85,
                plateHeightMm: 85,
                outletCentresMm: [(0, 25), (0, -25)],
                bevelTopMm: 15,
                frontCapSizeMm: (65, 50),
                frontCapStartMm: 15,
                frontCapVerticalOffsetMm: -7.5,
                secondFrontCapSizeMm: (65, 5),
                secondFrontCapVerticalOffsetMm: 30,
                secondFrontCapStartMm: 15,
                frontPortSizeMm: (12, 12),
                frontPortCentresMm: [(0, 8)],
                frontPortStartMm: 16,
                frontPortProudMm: 1,
                includeOutletVoids: false,
                includeRoundPins: false,
                includeEarthTabs: false);
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

    // Beveled socket: back plate, raised panel, mitred bevel between them, and one recessed outlet per
    // entry in outletCentresMm, each with contact pins and earth tabs.
    private static void BuildBeveledSocket(
        Document nestedDocument,
        double plateWidthMm,
        double plateHeightMm,
        (double X, double Y)[] outletCentresMm,
        double bevelTopMm = 9,
        (double Width, double Height)? frontCapSizeMm = null,
        double? frontCapStartMm = null,
        double frontCapVerticalOffsetMm = 0,
        (double Width, double Height)? secondFrontCapSizeMm = null,
        double? secondFrontCapStartMm = null,
        double secondFrontCapVerticalOffsetMm = 0,
        (double Width, double Height)? frontPortSizeMm = null,
        (double X, double Y)[]? frontPortCentresMm = null,
        double? frontPortStartMm = null,
        double frontPortProudMm = 1,
        bool includeOutletVoids = true,
        bool includeRoundPins = true,
        bool includeEarthTabs = true)
    {
        // The panel is inset uniformly from the plate; the bevel spans the difference.
        const double panelInsetMm = 10;
        const double plateDepthMm = 5;
        var panelWidthMm = plateWidthMm - 2 * panelInsetMm;
        var panelHeightMm = plateHeightMm - 2 * panelInsetMm;
        var plateDepth = UnitUtils.ConvertToInternalUnits(plateDepthMm, UnitTypeId.Millimeters);

        var sketchPlane = SketchPlane.Create(nestedDocument, ProfileFactory.GetHorizontalPlaneAtOrigin(nestedDocument));
        nestedDocument.FamilyCreate.NewExtrusion(true, ProfileFactory.BuildRectangleProfile(plateWidthMm, plateHeightMm), sketchPlane, plateDepth);

        const double panelDepthMm = 4;
        const double panelGapMm = 0;
        var panelDepth = UnitUtils.ConvertToInternalUnits(panelDepthMm, UnitTypeId.Millimeters);

        var panelGap = UnitUtils.ConvertToInternalUnits(panelGapMm, UnitTypeId.Millimeters);
        var panelSketchPlane = SketchPlane.Create(nestedDocument, ProfileFactory.GetHorizontalPlaneAtOrigin(nestedDocument, plateDepth + panelGap));
        var panel = nestedDocument.FamilyCreate.NewExtrusion(true, ProfileFactory.BuildRectangleProfile(panelWidthMm, panelHeightMm), panelSketchPlane, panelDepth);

        // Extrusion 3: bevel rising from extrusion 1's front face and dying into extrusion 2's side.
        // Both loops are positioned in world Z so the base starts on the plate face, not at the origin.
        var bevelBase = plateDepth;
        var bevelTop = UnitUtils.ConvertToInternalUnits(bevelTopMm, UnitTypeId.Millimeters);
        var bevelSketchPlane = SketchPlane.Create(nestedDocument, ProfileFactory.GetHorizontalPlaneAtOrigin(nestedDocument, bevelBase));
        var bevel = nestedDocument.FamilyCreate.NewBlend(
            true,
            BuildRectangleLoopAtDepth(panelWidthMm, panelHeightMm, bevelTop),
            BuildRectangleLoopAtDepth(plateWidthMm, plateHeightMm, bevelBase),
            bevelSketchPlane);

        // Extrusion 4: one 40mm opening per outlet. These must be true voids because the bevel is a solid
        // frustum filling the same Z range, so profile-loop holes in the panel alone would be plugged by it.
        const double outletDiameterMm = 40;
        var outletCentres = outletCentresMm
            .Select(c => new XYZ(
                UnitUtils.ConvertToInternalUnits(c.X, UnitTypeId.Millimeters),
                UnitUtils.ConvertToInternalUnits(c.Y, UnitTypeId.Millimeters),
                0))
            .ToArray();

        // Each opening is its own void: Revit reads disjoint loops in a single profile unreliably.
        // The span deliberately overshoots the whole assembly so no cut face is coincident with a solid face.
        var voidStart = UnitUtils.ConvertToInternalUnits(-5, UnitTypeId.Millimeters);
        var voidEnd = UnitUtils.ConvertToInternalUnits(20, UnitTypeId.Millimeters);
        var outletSketchPlane = SketchPlane.Create(nestedDocument, ProfileFactory.GetHorizontalPlaneAtOrigin(nestedDocument, voidStart));

        var combinable = new CombinableElementArray();
        combinable.Append(panel);
        combinable.Append(bevel);

        if (includeOutletVoids)
        {
            foreach (var centre in outletCentres)
            {
                var outletProfile = ProfileFactory.BuildCircleProfile(outletDiameterMm, centre.Y, centre.X);
                combinable.Append(nestedDocument.FamilyCreate.NewExtrusion(false, outletProfile, outletSketchPlane, voidEnd - voidStart));
            }
        }

        nestedDocument.CombineElements(combinable);

        if (frontCapSizeMm is { } cap)
        {
            // Front cap centered on the bevel front face.
            var frontCapSketchPlane = SketchPlane.Create(nestedDocument, ProfileFactory.GetHorizontalPlaneAtOrigin(nestedDocument));
            var frontCap = nestedDocument.FamilyCreate.NewExtrusion(true, ProfileFactory.BuildRectangleProfile(cap.Width, cap.Height), frontCapSketchPlane, UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Millimeters));
            var capStart = UnitUtils.ConvertToInternalUnits(frontCapStartMm ?? bevelTopMm, UnitTypeId.Millimeters);
            var capVerticalOffset = UnitUtils.ConvertToInternalUnits(frontCapVerticalOffsetMm, UnitTypeId.Millimeters);
            ElementTransformUtils.MoveElement(nestedDocument, frontCap.Id, new XYZ(0, capVerticalOffset, capStart));
        }

        if (secondFrontCapSizeMm is { } secondCap)
        {
            var secondFrontCapSketchPlane = SketchPlane.Create(nestedDocument, ProfileFactory.GetHorizontalPlaneAtOrigin(nestedDocument));
            var secondFrontCap = nestedDocument.FamilyCreate.NewExtrusion(true, ProfileFactory.BuildRectangleProfile(secondCap.Width, secondCap.Height), secondFrontCapSketchPlane, UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Millimeters));
            var secondCapStart = UnitUtils.ConvertToInternalUnits(secondFrontCapStartMm ?? frontCapStartMm ?? bevelTopMm, UnitTypeId.Millimeters);
            var secondCapVerticalOffset = UnitUtils.ConvertToInternalUnits(secondFrontCapVerticalOffsetMm, UnitTypeId.Millimeters);
            ElementTransformUtils.MoveElement(nestedDocument, secondFrontCap.Id, new XYZ(0, secondCapVerticalOffset, secondCapStart));
        }

        if (frontPortSizeMm is { } portSize && frontPortCentresMm is { Length: > 0 } portCentres)
        {
            var frontPortSketchPlane = SketchPlane.Create(nestedDocument, ProfileFactory.GetHorizontalPlaneAtOrigin(nestedDocument));
            var portStart = UnitUtils.ConvertToInternalUnits(frontPortStartMm ?? (frontCapStartMm ?? bevelTopMm), UnitTypeId.Millimeters);
            var portDepth = UnitUtils.ConvertToInternalUnits(frontPortProudMm, UnitTypeId.Millimeters);

            foreach (var centre in portCentres)
            {
                var portProfile = ProfileFactory.BuildRectangleProfile(portSize.Width, portSize.Height);
                var port = nestedDocument.FamilyCreate.NewExtrusion(true, portProfile, frontPortSketchPlane, portDepth);
                var x = UnitUtils.ConvertToInternalUnits(centre.X, UnitTypeId.Millimeters);
                var y = UnitUtils.ConvertToInternalUnits(centre.Y, UnitTypeId.Millimeters);
                ElementTransformUtils.MoveElement(nestedDocument, port.Id, new XYZ(x, y, portStart));
            }
        }

        // Extrusion 5: two contact pins per outlet. Created after the void combine so they survive it.
        // They rise from the recess floor to stand slightly proud of the bevel's front face.
        const double pinDiameterMm = 4;
        const double pinSpacingMm = 19;
        const double pinProudMm = 1.0;
        var pinOffset = UnitUtils.ConvertToInternalUnits(pinSpacingMm / 2, UnitTypeId.Millimeters);
        var pinSketchPlane = SketchPlane.Create(nestedDocument, ProfileFactory.GetHorizontalPlaneAtOrigin(nestedDocument, plateDepth));
        var pinDepth = bevelTop - plateDepth + UnitUtils.ConvertToInternalUnits(pinProudMm, UnitTypeId.Millimeters);

        if (includeRoundPins)
        {
            foreach (var centre in outletCentres)
            foreach (var horizontalOffset in new[] { pinOffset, -pinOffset })
            {
                var pinProfile = ProfileFactory.BuildCircleProfile(pinDiameterMm, centre.Y, centre.X + horizontalOffset);
                nestedDocument.FamilyCreate.NewExtrusion(true, pinProfile, pinSketchPlane, pinDepth);
            }
        }

        // Extrusion 6: earth contacts at the top and bottom rim of each recess. Solid, and standing proud
        // of the bevel's front face, since flush tabs merge invisibly into the surrounding material.
        const double earthTabWidthMm = 3;
        const double earthTabLengthMm = 3;
        const double earthTabProudMm = 5;
        var outletRadius = UnitUtils.ConvertToInternalUnits(outletDiameterMm / 2, UnitTypeId.Millimeters);
        var earthTabLength = UnitUtils.ConvertToInternalUnits(earthTabLengthMm, UnitTypeId.Millimeters);
        var earthDepth = bevelTop - plateDepth + UnitUtils.ConvertToInternalUnits(earthTabProudMm, UnitTypeId.Millimeters);

        if (includeEarthTabs)
        {
            foreach (var centre in outletCentres)
            foreach (var rimOffset in new[] { outletRadius, -outletRadius })
            {
                // Sit just inside the rim so the whole tab stands in open air rather than buried in the wall.
                var tabCentre = rimOffset - Math.Sign(rimOffset) * earthTabLength / 2;
                var earthProfile = new CurveArrArray();
                earthProfile.Append(ProfileFactory.BuildRectangleLoop(earthTabWidthMm, earthTabLengthMm, centre.Y + tabCentre, centre.X));
                nestedDocument.FamilyCreate.NewExtrusion(true, earthProfile, pinSketchPlane, earthDepth);
            }
        }
    }

    private static CurveArray BuildRectangleLoopAtDepth(double widthMm, double heightMm, double depth)
    {
        var loop = ProfileFactory.BuildRectangleLoop(widthMm, heightMm);
        if (Math.Abs(depth) < 1e-9)
            return loop;

        var translated = new CurveArray();
        var translation = Transform.CreateTranslation(new XYZ(0, 0, depth));
        foreach (Curve segment in loop)
            translated.Append(segment.CreateTransformed(translation));
        return translated;
    }

    // Wall plate with a raised inner panel carrying 1, 2 (stacked) or 4 (2x2 grid) recessed 40 mm outlets.
    private static void BuildElectricalSocket(Document nestedDocument, RotatableFamilyPreset preset)
    {
        const double outletDiameterMm = 40;
        const double pinDiameterMm = 5;

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

        // One shared bevelled frame runs around the whole plate: a step just inside the outer edge,
        // sloping (in two flat stages) down to a smaller inset panel that both outlet holes are cut into.
        // Kept modest so the inset panel still has enough room for a visibly wider dish around each
        // outlet without the dish reaching the panel's own edge.
        const double frameMarginMm = 6;
        const double frameBaseDepthMm = 8;
        const double frameCapDepthMm = 6;
        var frameBaseDepth = UnitUtils.ConvertToInternalUnits(frameBaseDepthMm, UnitTypeId.Millimeters);
        var frameCapDepth = UnitUtils.ConvertToInternalUnits(frameCapDepthMm, UnitTypeId.Millimeters);

        var frameBaseProfile = ProfileFactory.BuildRoundedRectangleProfile(plateWidthMm - frameMarginMm, plateHeightMm - frameMarginMm, 8);
        nestedDocument.FamilyCreate.NewExtrusion(true, frameBaseProfile, topSketchPlane, frameBaseDepth);

        var frameCapSketchPlane = SketchPlane.Create(nestedDocument, ProfileFactory.GetHorizontalPlaneAtOrigin(nestedDocument, backClosureDepth + backCutDepth + faceDepth + frameBaseDepth));
        var frameCapProfile = ProfileFactory.BuildRoundedRectangleProfile(plateWidthMm - frameMarginMm * 2, plateHeightMm - frameMarginMm * 2, 6);
        foreach (var (verticalOffset, horizontalOffset) in outletCentres)
            frameCapProfile.Append(ProfileFactory.BuildCircleLoop(outletDiameterMm, verticalOffset, horizontalOffset));
        nestedDocument.FamilyCreate.NewExtrusion(true, frameCapProfile, frameCapSketchPlane, frameCapDepth);

        // A raised collar sits around each outlet (not spanning the whole plate), shaped as a washer:
        // a ring between the dish diameter and the outlet hole itself. Kept as small, self-contained
        // shapes rather than one big rectangle with holes, since two full-size stacked rectangles with
        // differently-sized coaxial holes silently failed to cut here (likely a Revit auto-join quirk).
        // The dish is sized dynamically per preset: it must never reach the inset panel's own edge or an
        // adjacent outlet's dish, or the resulting profile self-intersects.
        var insetHalfWidthMm = (plateWidthMm - frameMarginMm * 2) / 2;
        var insetHalfHeightMm = (plateHeightMm - frameMarginMm * 2) / 2;
        var dishClearanceMm = outletCentres
            .Select(c => Math.Min(
                insetHalfWidthMm - Math.Abs(UnitUtils.ConvertFromInternalUnits(c.HorizontalOffset, UnitTypeId.Millimeters)),
                insetHalfHeightMm - Math.Abs(UnitUtils.ConvertFromInternalUnits(c.VerticalOffset, UnitTypeId.Millimeters))))
            .Min();
        // 2mm safety margin from the clearance limit; never smaller than the outlet itself (i.e. no collar at all)
        // if the preset's spacing leaves no room for a wider ring.
        var dishRadiusMm = Math.Min(outletDiameterMm / 2 + 10, Math.Max(outletDiameterMm / 2, dishClearanceMm - 2));
        var dishDiameterMm = dishRadiusMm * 2;
        const double surroundDepthMm = 6;
        var surroundDepth = UnitUtils.ConvertToInternalUnits(surroundDepthMm, UnitTypeId.Millimeters);

        var surroundSketchPlane = SketchPlane.Create(nestedDocument, ProfileFactory.GetHorizontalPlaneAtOrigin(nestedDocument, backClosureDepth + backCutDepth + faceDepth + frameBaseDepth + frameCapDepth));
        if (dishDiameterMm > outletDiameterMm)
        {
            foreach (var (verticalOffset, horizontalOffset) in outletCentres)
            {
                var collarProfile = new CurveArrArray();
                collarProfile.Append(ProfileFactory.BuildCircleLoop(dishDiameterMm, verticalOffset, horizontalOffset));
                collarProfile.Append(ProfileFactory.BuildCircleLoop(outletDiameterMm, verticalOffset, horizontalOffset));
                nestedDocument.FamilyCreate.NewExtrusion(true, collarProfile, surroundSketchPlane, surroundDepth);
            }
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
