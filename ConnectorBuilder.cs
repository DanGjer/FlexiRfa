//-----------------------------------------------------------------------------
// ConnectorBuilder.cs
//
// Creates the electrical connectors for a generated family. Connectors are
// always built from scratch, so templates need no pre-placed connectors.
//-----------------------------------------------------------------------------

using Autodesk.Revit.DB.Electrical;

namespace FlexiRfa;

internal static class ConnectorBuilder
{
    // Which side of the extrusion a connector is hosted on; Front/Back are the flat +Z/-Z faces,
    // Left/Right the -X/+X side faces, Top/Bottom the +Y/-Y side faces.
    private enum ConnectorSide { Front, Back, Left, Right, Top, Bottom }

    private readonly record struct ElectricalConnectorSpec(ElectricalSystemType SystemType, ConnectorSide Side);

    private static readonly IReadOnlyDictionary<RotatableFamilyPreset, ElectricalConnectorSpec[]> PresetConnectorSpecs = new Dictionary<RotatableFamilyPreset, ElectricalConnectorSpec[]>
    {
        [RotatableFamilyPreset.DataSocketDouble] =
        [
            new ElectricalConnectorSpec(ElectricalSystemType.Data, ConnectorSide.Left),
            new ElectricalConnectorSpec(ElectricalSystemType.Data, ConnectorSide.Right),
        ],
        [RotatableFamilyPreset.DataOutletSingle] =
        [
            new ElectricalConnectorSpec(ElectricalSystemType.Data, ConnectorSide.Right),
        ],
        [RotatableFamilyPreset.ElectricalSocket] =
        [
            new ElectricalConnectorSpec(ElectricalSystemType.PowerCircuit, ConnectorSide.Back),
        ],
        [RotatableFamilyPreset.Test] =
        [
            new ElectricalConnectorSpec(ElectricalSystemType.PowerCircuit, ConnectorSide.Back),
        ],
        [RotatableFamilyPreset.ElectricalSocketSingle] =
        [
            new ElectricalConnectorSpec(ElectricalSystemType.PowerCircuit, ConnectorSide.Back),
        ],
        [RotatableFamilyPreset.ElectricalSocketQuadruple] =
        [
            new ElectricalConnectorSpec(ElectricalSystemType.PowerCircuit, ConnectorSide.Back),
        ],
        [RotatableFamilyPreset.Downlight] =
        [
            new ElectricalConnectorSpec(ElectricalSystemType.PowerCircuit, ConnectorSide.Back),
        ],
        [RotatableFamilyPreset.SmokeDetector] =
        [
            new ElectricalConnectorSpec(ElectricalSystemType.FireAlarm, ConnectorSide.Back),
        ],
        [RotatableFamilyPreset.RectangularLightFixture] =
        [
            new ElectricalConnectorSpec(ElectricalSystemType.PowerCircuit, ConnectorSide.Back),
        ],
    };

    internal static string? RebuildConnectors(Document familyDocument, FlexiRfaArgs args)
    {
        using var transaction = new Transaction(familyDocument, "Rebuild preset connectors");
        transaction.Start();

        var existingConnectorIds = new FilteredElementCollector(familyDocument)
            .OfClass(typeof(ConnectorElement))
            .Cast<ConnectorElement>()
            .Select(c => c.Id)
            .ToList();
        if (existingConnectorIds.Count > 0)
            familyDocument.Delete(existingConnectorIds);

        var connectorSpecs = GetConnectorSpecs(args);
        if (connectorSpecs.Count == 0)
        {
            transaction.Commit();
            return null;
        }

        var extrusion = GetLargestSolid(familyDocument);
        if (extrusion is null)
        {
            transaction.RollBack();
            return "Could not find the extrusion geometry to host electrical connectors on.";
        }

        foreach (var spec in connectorSpecs)
        {
            var hostFace = GetExtrusionFace(extrusion, spec.Side);
            if (hostFace is null)
            {
                transaction.RollBack();
                return $"Could not find a planar {spec.Side} face on the extrusion to host an electrical connector on.";
            }

            ConnectorElement.CreateElectricalConnector(familyDocument, spec.SystemType, hostFace.Reference);
        }

        transaction.Commit();
        return null;
    }

    // Custom families get their connectors from the checkboxes; every other preset uses its fixed policy.
    // Each type owns a dedicated side, so a given connector always lands in the same place regardless
    // of which other types are ticked, and no two can share a face.
    private static IReadOnlyList<ElectricalConnectorSpec> GetConnectorSpecs(FlexiRfaArgs args)
    {
        if (args.Preset != RotatableFamilyPreset.Custom)
            return PresetConnectorSpecs.TryGetValue(args.Preset, out var presetSpecs) ? presetSpecs : [];

        var specs = new List<ElectricalConnectorSpec>();
        if (args.AddPowerConnector)
            specs.Add(new ElectricalConnectorSpec(ElectricalSystemType.PowerCircuit, ConnectorSide.Left));
        if (args.AddDataConnector)
            specs.Add(new ElectricalConnectorSpec(ElectricalSystemType.Data, ConnectorSide.Right));
        if (args.AddCommunicationConnector)
            specs.Add(new ElectricalConnectorSpec(ElectricalSystemType.Communication, ConnectorSide.Top));
        if (args.AddFireAlarmConnector)
            specs.Add(new ElectricalConnectorSpec(ElectricalSystemType.FireAlarm, ConnectorSide.Bottom));
        if (args.AddSecurityConnector)
            specs.Add(new ElectricalConnectorSpec(ElectricalSystemType.Security, ConnectorSide.Back));
        if (args.AddControlsConnector)
            specs.Add(new ElectricalConnectorSpec(ElectricalSystemType.Controls, ConnectorSide.Front));

        return specs;
    }

    private static Solid? GetLargestSolid(Document familyDocument)
    {
        var options = new Options { ComputeReferences = true };

        return new FilteredElementCollector(familyDocument)
            .WhereElementIsNotElementType()
            .Select(e => e.get_Geometry(options))
            .Where(g => g is not null)
            .SelectMany(g => EnumerateSolids(g!))
            .OrderByDescending(s => s.Volume)
            .FirstOrDefault();
    }

    // Picks the outermost planar face facing the requested side, so connectors sit on opposite ends of the extrusion.
    private static PlanarFace? GetExtrusionFace(Solid extrusion, ConnectorSide side)
    {
        var normal = side switch
        {
            ConnectorSide.Front => XYZ.BasisZ,
            ConnectorSide.Back => XYZ.BasisZ.Negate(),
            ConnectorSide.Left => XYZ.BasisX.Negate(),
            ConnectorSide.Right => XYZ.BasisX,
            ConnectorSide.Top => XYZ.BasisY,
            _ => XYZ.BasisY.Negate(),
        };

        return extrusion.Faces
            .OfType<PlanarFace>()
            .Where(f => f.Reference is not null && f.FaceNormal.DotProduct(normal) > 0.9)
            .OrderByDescending(f => f.Origin.DotProduct(normal))
            .FirstOrDefault();
    }

    private static IEnumerable<Solid> EnumerateSolids(GeometryElement geometry)
    {
        foreach (var obj in geometry)
        {
            switch (obj)
            {
                case Solid solid when solid.Volume > 0:
                    yield return solid;
                    break;
                case GeometryInstance instance:
                    // Symbol geometry keeps faces in the same space as the references Revit hosts connectors on;
                    // instance geometry is transformed and lands connectors off the face.
                    var instanceGeometry = instance.GetSymbolGeometry();
                    if (instanceGeometry is null)
                        break;

                    foreach (var nestedSolid in EnumerateSolids(instanceGeometry))
                        yield return nestedSolid;
                    break;
            }
        }
    }
}
