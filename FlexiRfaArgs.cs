//-----------------------------------------------------------------------------
// FlexiRfaArgs.cs
//
// Input parameters for the rotatable family extension. Ported from the
// ProofOfConcept sample: creates a new rotatable family from a template, or
// edits the nested "3D Orientation Family" geometry of an existing one.
//-----------------------------------------------------------------------------

namespace FlexiRfa;

public class FlexiRfaArgs
{
    /// <summary>Sentinel value meaning "use the preset default" for a dimension field.</summary>
    public const double UnsetDimensionMm = -1;

    [OptionsField(Label = "Mode", ToolTip = "Create a new rotatable family or edit the selected one")]
    public RotatableFamilyMode Mode { get; set; } = RotatableFamilyMode.CreateNew;

    [FilePickerField(Label = "Family template", ToolTip = "The .rft template used to create the new family", FileExtensions = ["rft", "rfa"], Visibility = $"{nameof(Mode)} == 'CreateNew'")]
    [Required(ErrorMessage = "A family template is required.")]
    public string TemplatePath { get; set; } = @"O:\A005000\A008170\EL\Utvikling\Roterbare familier\Roterbar Familie Template.rfa";

    [TextField(Label = "New family name", Visibility = $"{nameof(Mode)} == 'CreateNew'")]
    [Required(ErrorMessage = "New family name is required.")]
    public string NewFamilyName { get; set; } = string.Empty;

    [RevitAutoFill(RevitAutoFillSource.Categories)]
    public string? FamilyCategory { get; set; }

    [OptionsField(Label = "Preset", ToolTip = "Preset dimensions for common fixture types")]
    public RotatableFamilyPreset Preset { get; set; } = RotatableFamilyPreset.Custom;

    [OptionsField(Label = "Profile shape", Visibility = $"{nameof(Preset)} == 'Custom'")]
    public ExtrusionProfileShape ProfileShape { get; set; } = ExtrusionProfileShape.Box;

    [DoubleField(Label = "Width (mm)", ToolTip = "Rectangular profile width", Visibility = $"{nameof(Preset)} == 'Custom' && {nameof(ProfileShape)} == 'Box'")]
    public double Width { get; set; } = UnsetDimensionMm;

    [DoubleField(Label = "Height (mm)", ToolTip = "Rectangular profile height", Visibility = $"{nameof(Preset)} == 'Custom' && {nameof(ProfileShape)} == 'Box'")]
    public double Height { get; set; } = UnsetDimensionMm;

    [DoubleField(Label = "Diameter (mm)", ToolTip = "Circular profile diameter", Visibility = $"{nameof(Preset)} == 'Custom' && {nameof(ProfileShape)} == 'Cylinder'")]
    public double Diameter { get; set; } = UnsetDimensionMm;

    [DoubleField(Label = "Depth (mm)", ToolTip = "Extrusion depth", Visibility = $"{nameof(Preset)} == 'Custom'")]
    public double Depth { get; set; } = UnsetDimensionMm;

    [DoubleField(Label = "Downlight diameter (mm)", ToolTip = "Recessed body diameter", Visibility = $"{nameof(Preset)} == 'Downlight'")]
    public double DownlightDiameter { get; set; } = 200;

    [DoubleField(Label = "Smoke detector diameter (mm)", ToolTip = "Sensor chamber diameter; the ceiling plate stays 25 mm wider", Visibility = $"{nameof(Preset)} == 'SmokeDetector'")]
    public double SmokeDetectorDiameter { get; set; } = 85;

    [DoubleField(Label = "Fixture length (mm)", ToolTip = "Overall length of the luminaire housing", Visibility = $"{nameof(Preset)} == 'RectangularLightFixture'")]
    public double LightFixtureLength { get; set; } = 1200;

    [DoubleField(Label = "Fixture width (mm)", ToolTip = "Overall width of the luminaire housing", Visibility = $"{nameof(Preset)} == 'RectangularLightFixture'")]
    public double LightFixtureWidth { get; set; } = 100;
}
