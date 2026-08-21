//-----------------------------------------------------------------------------
// FlexiRfaCommand.cs
//
// Creates a new rotatable family from a template, generating the preset geometry
// and electrical connectors, then loads it into the active document.
//-----------------------------------------------------------------------------

namespace FlexiRfa;

public class FlexiRfaCommand : IRevitExtension<FlexiRfaArgs>
{
    public IExtensionResult Run(IRevitExtensionContext context, FlexiRfaArgs args, CancellationToken cancellationToken)
    {
        var activeDocument = context.UIApplication.ActiveUIDocument?.Document;
        if (activeDocument is null)
            return Result.Text.Failed("Revit has no active document open.");

        return CreateNewFamily(activeDocument, args);
    }

    private static IExtensionResult CreateNewFamily(Document activeDocument, FlexiRfaArgs args)
    {
        if (!File.Exists(args.TemplatePath))
            return Result.Text.Failed($"Template file not found: {args.TemplatePath}");

        if (string.IsNullOrWhiteSpace(args.NewFamilyName))
            return Result.Text.Failed("New family name is required.");

        if (FamilyNameExists(activeDocument, args.NewFamilyName))
            return Result.Text.Failed($"A family named '{args.NewFamilyName}' already exists in this document. Choose a different name or use 'Edit Existing Family' instead.");

        var application = activeDocument.Application;
        var workingDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(workingDirectory);
        var workingFamilyPath = Path.Combine(workingDirectory, $"{args.NewFamilyName}.rfa");

        Document? familyDocument = null;

        try
        {
            File.Copy(args.TemplatePath, workingFamilyPath);
            familyDocument = application.OpenDocumentFile(workingFamilyPath);

            if (!familyDocument.IsFamilyDocument)
                return Result.Text.Failed("The selected template is not a family document.");

            SetFamilyCategory(familyDocument, args);
            RenameCurrentType(familyDocument, args.NewFamilyName);

            var error = ReplaceOrientationGeometry(familyDocument, args, out var geometryHost, out var transformInfo);
            if (error is not null)
                return Result.Text.Failed(error);

            var connectorError = ConnectorBuilder.RebuildConnectors(familyDocument, args);
            if (connectorError is not null)
                return Result.Text.Failed(connectorError);

            familyDocument.LoadFamily(activeDocument, new FamilyLoadOptions());

            return Result.Text.Succeeded($"Created rotatable family '{args.NewFamilyName}' and loaded it into the active document. Geometry was written into '{geometryHost}'. {transformInfo}");
        }
        catch (Exception ex)
        {
            return Result.Text.Failed($"Failed to create rotatable family: {ex.Message}");
        }
        finally
        {
            familyDocument?.Close(false);
            TryDeleteDirectory(workingDirectory);
        }
    }

    private static bool FamilyNameExists(Document activeDocument, string familyName) =>
        new FilteredElementCollector(activeDocument)
            .OfClass(typeof(Family))
            .Cast<Family>()
            .Any(f => f.Name.Equals(familyName, StringComparison.OrdinalIgnoreCase));

    private static void SetFamilyCategory(Document familyDocument, FlexiRfaArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.FamilyCategory))
            return;

        using var categoryTransaction = new Transaction(familyDocument, "Set family category");
        categoryTransaction.Start();

        var category = familyDocument.Settings.Categories
            .Cast<Category>()
            .FirstOrDefault(c => c.Name.Equals(args.FamilyCategory, StringComparison.OrdinalIgnoreCase));
        if (category is not null)
            familyDocument.OwnerFamily.FamilyCategory = category;

        categoryTransaction.Commit();
    }

    private static void RenameCurrentType(Document familyDocument, string newTypeName)
    {
        var familyManager = familyDocument.FamilyManager;
        if (familyManager.CurrentType is null)
            return;

        using var renameTransaction = new Transaction(familyDocument, "Rename family type");
        renameTransaction.Start();
        familyManager.RenameCurrentType(newTypeName);
        renameTransaction.Commit();
    }

    // The nested "3D Orientation Family" is what the rotation parameters actually drive; geometry
    // added to the host family directly does not rotate, so the extrusion must live inside it.
    private static string? ReplaceOrientationGeometry(Document familyDocument, FlexiRfaArgs args, out string geometryHost, out string transformInfo)
    {
        geometryHost = "3D Orientation Family";
        transformInfo = string.Empty;

        var nestedInstance = new FilteredElementCollector(familyDocument)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .FirstOrDefault(fi => fi.Symbol.Family.Name.Equals("3D Orientation Family", StringComparison.OrdinalIgnoreCase));

        if (nestedInstance is null)
            return "Could not find the nested '3D Orientation Family' in the family.";

        var nestedTransform = nestedInstance.GetTransform();
        transformInfo = $"[DIAG] 3D Orientation Family transform: Origin={FormatXyz(nestedTransform.Origin)}, BasisX={FormatXyz(nestedTransform.BasisX)}, BasisY={FormatXyz(nestedTransform.BasisY)}, BasisZ={FormatXyz(nestedTransform.BasisZ)}.";

        var nestedDocument = familyDocument.EditFamily(nestedInstance.Symbol.Family);

        // Orientation_CW rotates this further-nested geometry component, so replacing it with loose
        // extrusions would break the rotation; the forms are swapped inside it instead.
        var geometryInstance = new FilteredElementCollector(nestedDocument)
            .OfClass(typeof(FamilyInstance))
            .OfCategory(BuiltInCategory.OST_GenericModel)
            .Cast<FamilyInstance>()
            .FirstOrDefault();

        if (geometryInstance is null)
        {
            GeometryBuilder.ReplaceForms(nestedDocument, args);
        }
        else
        {
            var geometryTransform = geometryInstance.GetTransform();
            transformInfo += $" [DIAG] {geometryInstance.Symbol.Family.Name} transform: Origin={FormatXyz(geometryTransform.Origin)}, BasisX={FormatXyz(geometryTransform.BasisX)}, BasisY={FormatXyz(geometryTransform.BasisY)}, BasisZ={FormatXyz(geometryTransform.BasisZ)}.";

            geometryHost = geometryInstance.Symbol.Family.Name;
            var geometryDocument = nestedDocument.EditFamily(geometryInstance.Symbol.Family);
            GeometryBuilder.ReplaceForms(geometryDocument, args);
            geometryDocument.LoadFamily(nestedDocument, new FamilyLoadOptions());
            geometryDocument.Close(false);
        }

        nestedDocument.LoadFamily(familyDocument, new FamilyLoadOptions());
        nestedDocument.Close(false);

        return null;
    }

    // DIAGNOSTIC: readable vector formatting for transform reporting.
    private static string FormatXyz(XYZ v) => $"({v.X:F2}, {v.Y:F2}, {v.Z:F2})";

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

// Always overwrite the existing project family when re-running the extension.
file sealed class FamilyLoadOptions : IFamilyLoadOptions
{
    public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
    {
        overwriteParameterValues = true;
        return true;
    }

    public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
    {
        source = FamilySource.Family;
        overwriteParameterValues = true;
        return true;
    }
}