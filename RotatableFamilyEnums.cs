//-----------------------------------------------------------------------------
// RotatableFamilyEnums.cs
//
// Enumerations used by FlexiRfaArgs to configure the rotatable family
// creation/editing workflow.
//-----------------------------------------------------------------------------

namespace FlexiRfa;

public enum RotatableFamilyMode
{
    [Description("Create New Family")]
    CreateNew,

    [Description("Edit Existing Family")]
    EditExisting,
}

public enum RotatableFamilyPreset
{
    [Description("Custom")]
    Custom,

    [Description("Downlight")]
    Downlight,

    [Description("Smoke Detector")]
    SmokeDetector,

    [Description("Electrical Socket (Double)")]
    ElectricalSocket,

    [Description("Electrical Socket (Single)")]
    ElectricalSocketSingle,

    [Description("Electrical Socket (Quadruple)")]
    ElectricalSocketQuadruple,

    [Description("Light Fixture (Rectangular)")]
    RectangularLightFixture,

    [Description("Data Socket (Double RJ45)")]
    DataSocketDouble,
}

public enum ExtrusionProfileShape
{
    [Description("Box")]
    Box,

    [Description("Cylinder")]
    Cylinder,
}
