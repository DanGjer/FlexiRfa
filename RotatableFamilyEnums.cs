//-----------------------------------------------------------------------------
// RotatableFamilyEnums.cs
//
// Enumerations used by FlexiRfaArgs to configure the rotatable family
// creation workflow.
//-----------------------------------------------------------------------------

namespace FlexiRfa;

public enum FlexiRfaMode
{
    [Description("Create New RFA")]
    CreateNew,

    [Description("Rotatify")]
    Rotatify,
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

    [Description("Electrical Socket Surface (Double)")]
    DoubleElectricalSocketSurface,

    [Description("Electrical Socket Surface (Single)")]
    SingleElectricalSocketSurface,

    [Description("Electrical Socket Surface (Quadruple)")]
    QuadrupleElectricalSocketSurface,

    [Description("Electrical Socket (Single)")]
    ElectricalSocketSingle,

    [Description("Electrical Socket (Quadruple)")]
    ElectricalSocketQuadruple,

    [Description("Light Fixture (Rectangular)")]
    RectangularLightFixture,

    [Description("Data Socket (Double RJ45)")]
    DataSocketDouble,

    [Description("Data Outlet (Single RJ45)")]
    DataOutletSingle,
}

public enum ExtrusionProfileShape
{
    [Description("Box")]
    Box,

    [Description("Cylinder")]
    Cylinder,
}
