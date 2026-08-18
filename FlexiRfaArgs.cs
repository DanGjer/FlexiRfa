//-----------------------------------------------------------------------------
// FlexiRfaArgs.cs
//
// Input parameters for the rotatable family extension.
//-----------------------------------------------------------------------------

namespace FlexiRfa;

public class FlexiRfaArgs
{
    private const double DefaultDimensionMm = 200;

    [FilePickerField(Label = "Family template", ToolTip = "The .rft template used to create the new family", FileExtensions = ["rft", "rfa"])]
    [Required(ErrorMessage = "A family template is required.")]
    public string TemplatePath { get; set; } = @"O:\A005000\A008170\EL\Utvikling\Roterbare familier\Roterbar Familie Template.rfa";

    [TextField(Label = "New family name")]
    [Required(ErrorMessage = "New family name is required.")]
    public string NewFamilyName { get; set; } = string.Empty;

    [OptionsField(Label = "Family category", ToolTip = "Revit category the generated family is assigned to", CollectorType = typeof(ElectricalCategoryCollector), CollectorSortOrder = SortOrder.SortByAscending)]
    public string? FamilyCategory { get; set; }

    [OptionsField(Label = "Preset", ToolTip = "Preset dimensions for common fixture types")]
    public RotatableFamilyPreset Preset { get; set; } = RotatableFamilyPreset.Custom;

    [OptionsField(Label = "Profile shape", Visibility = $"{nameof(Preset)} == 'Custom'")]
    public ExtrusionProfileShape ProfileShape { get; set; } = ExtrusionProfileShape.Box;

    [DoubleField(Label = "Width (mm)", ToolTip = "Rectangular profile width", Visibility = $"{nameof(Preset)} == 'Custom' && {nameof(ProfileShape)} == 'Box'")]
    public double Width { get; set; } = DefaultDimensionMm;

    [DoubleField(Label = "Height (mm)", ToolTip = "Rectangular profile height", Visibility = $"{nameof(Preset)} == 'Custom' && {nameof(ProfileShape)} == 'Box'")]
    public double Height { get; set; } = DefaultDimensionMm;

    [DoubleField(Label = "Diameter (mm)", ToolTip = "Circular profile diameter", Visibility = $"{nameof(Preset)} == 'Custom' && {nameof(ProfileShape)} == 'Cylinder'")]
    public double Diameter { get; set; } = DefaultDimensionMm;

    [DoubleField(Label = "Depth (mm)", ToolTip = "Extrusion depth", Visibility = $"{nameof(Preset)} == 'Custom'")]
    public double Depth { get; set; } = DefaultDimensionMm;

    [DoubleField(Label = "Downlight diameter (mm)", ToolTip = "Recessed body diameter", Visibility = $"{nameof(Preset)} == 'Downlight'")]
    public double DownlightDiameter { get; set; } = 200;

    [DoubleField(Label = "Smoke detector diameter (mm)", ToolTip = "Sensor chamber diameter; the ceiling plate stays 25 mm wider", Visibility = $"{nameof(Preset)} == 'SmokeDetector'")]
    public double SmokeDetectorDiameter { get; set; } = 85;

    [DoubleField(Label = "Fixture length (mm)", ToolTip = "Overall length of the luminaire housing", Visibility = $"{nameof(Preset)} == 'RectangularLightFixture'")]
    public double LightFixtureLength { get; set; } = 1200;

    [DoubleField(Label = "Fixture width (mm)", ToolTip = "Overall width of the luminaire housing", Visibility = $"{nameof(Preset)} == 'RectangularLightFixture'")]
    public double LightFixtureWidth { get; set; } = 100;

    private const string CustomConnectorVisibility = $"{nameof(Preset)} == 'Custom'";

    [BooleanField(Label = "Power connector", ToolTip = "Add a power circuit connector", Visibility = CustomConnectorVisibility)]
    public bool AddPowerConnector { get; set; }

    [BooleanField(Label = "Data connector", ToolTip = "Add a data connector", Visibility = CustomConnectorVisibility)]
    public bool AddDataConnector { get; set; }

    [BooleanField(Label = "Communication connector", ToolTip = "Add a communication connector", Visibility = CustomConnectorVisibility)]
    public bool AddCommunicationConnector { get; set; }

    [BooleanField(Label = "Fire alarm connector", ToolTip = "Add a fire alarm connector", Visibility = CustomConnectorVisibility)]
    public bool AddFireAlarmConnector { get; set; }

    [BooleanField(Label = "Security connector", ToolTip = "Add a security connector", Visibility = CustomConnectorVisibility)]
    public bool AddSecurityConnector { get; set; }

    [BooleanField(Label = "Controls connector", ToolTip = "Add a controls connector", Visibility = CustomConnectorVisibility)]
    public bool AddControlsConnector { get; set; }
}

// Narrows the category picker to the loadable-family categories electrical engineers actually use.
internal class ElectricalCategoryCollector : IRevitAutoFillCollector<FlexiRfaArgs>
{
    private static readonly BuiltInCategory[] ElectricalCategories =
    [
        BuiltInCategory.OST_ElectricalEquipment,
        BuiltInCategory.OST_ElectricalFixtures,
        BuiltInCategory.OST_LightingFixtures,
        BuiltInCategory.OST_LightingDevices,
        BuiltInCategory.OST_DataDevices,
        BuiltInCategory.OST_CommunicationDevices,
        BuiltInCategory.OST_FireAlarmDevices,
        BuiltInCategory.OST_SecurityDevices,
        BuiltInCategory.OST_NurseCallDevices,
        BuiltInCategory.OST_TelephoneDevices,
    ];

    public Dictionary<string, string> Get(UIApplication uiApplication, FlexiRfaArgs args)
    {
        var result = new Dictionary<string, string>();
        var document = uiApplication.ActiveUIDocument?.Document;
        if (document is null)
            return result;

        foreach (var builtInCategory in ElectricalCategories)
        {
            // Category names are localised, so they are read from the model rather than hard-coded.
            var name = Category.GetCategory(document, builtInCategory)?.Name;
            if (!string.IsNullOrWhiteSpace(name))
                result[name] = name;
        }

        return result;
    }
}
