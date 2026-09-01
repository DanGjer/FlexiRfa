//-----------------------------------------------------------------------------
// FlexiRfaCommand.cs
//
// Creates a new rotatable family from a template, generating the preset geometry
// and electrical connectors, then loads it into the active document.
//-----------------------------------------------------------------------------

using Autodesk.Revit.DB.Electrical;

namespace FlexiRfa;

public class FlexiRfaCommand : IRevitExtension<FlexiRfaArgs>
{
    public IExtensionResult Run(IRevitExtensionContext context, FlexiRfaArgs args, CancellationToken cancellationToken)
    {
        var uiDocument = context.UIApplication.ActiveUIDocument;
        var activeDocument = uiDocument?.Document;
        if (uiDocument is null || activeDocument is null)
            return Result.Text.Failed("Revit has no active document open.");

        return args.Mode switch
        {
            FlexiRfaMode.CreateNew => CreateNewFamily(activeDocument, args),
            FlexiRfaMode.Rotatify => RunRotatifyMode(uiDocument, args),
            _ => Result.Text.Failed($"Unsupported mode: {args.Mode}"),
        };
    }

    // EXPERIMENT: duplicates the rotatable template and copies the selected instance's family forms
    // into the nested geometry host, to test whether an existing non-rotatable family can be made
    // rotatable by transplanting its geometry rather than editing the source family in place.
    private static IExtensionResult RunRotatifyMode(UIDocument uiDocument, FlexiRfaArgs args)
    {
        var activeDocument = uiDocument.Document;

        if (!File.Exists(args.TemplatePath))
            return Result.Text.Failed($"Template file not found: {args.TemplatePath}");

        var selectedInstance = uiDocument.Selection.GetElementIds()
            .Select(activeDocument.GetElement)
            .OfType<FamilyInstance>()
            .FirstOrDefault();

        if (selectedInstance is null)
            return Result.Text.Failed("Select an instance of the non-rotatable family in the model before running Rotatify mode.");

        var sourceFamily = selectedInstance.Symbol.Family;
        var sourceTypeName = selectedInstance.Symbol.Name;
        var newFamilyName = $"{sourceFamily.Name} Replacement";

        if (FamilyNameExists(activeDocument, newFamilyName))
            return Result.Text.Failed($"A family named '{newFamilyName}' already exists in this document.");

        var application = activeDocument.Application;
        var workingDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(workingDirectory);
        var workingFamilyPath = Path.Combine(workingDirectory, $"{newFamilyName}.rfa");

        Document? familyDocument = null;
        Document? sourceDocument = null;
        var nestedSourceDocuments = new List<Document>();
        var copyResult = default((int Copied, int Failed, string Diagnostics));

        try
        {
            File.Copy(args.TemplatePath, workingFamilyPath);
            familyDocument = application.OpenDocumentFile(workingFamilyPath);

            if (!familyDocument.IsFamilyDocument)
                return Result.Text.Failed("The selected template is not a family document.");

            sourceDocument = activeDocument.EditFamily(sourceFamily);

            // The rotatable template's default (0-rotation) orientation keeps its thinnest extent along
            // local Z. Some source families (e.g. wall sockets) instead keep their thinnest extent along
            // Y (the common wall-hosted convention: X=width along wall, Y=depth into wall, Z=vertical) -
            // detected from the source's own geometry bounding box, not guessed or hardcoded.
            var orientationCorrection = DetermineOrientationCorrection(sourceDocument, out var orientationDiagnostics);

            SetFamilyCategoryByName(familyDocument, sourceFamily.FamilyCategory?.Name);
            RenameCurrentType(familyDocument, sourceTypeName);

            // Type parameters (Width, Manufacturer, custom shared params, etc.) live on the TOP-LEVEL
            // family, matched by name - independent of the geometry/connector/instance steps below.
            var typeParamResult = CopyTypeParametersFromSource(selectedInstance.Symbol, familyDocument);

            var error = ReplaceOrientationGeometry(familyDocument, args, out var geometryHost, out var transformInfo,
                geometryDocument => copyResult = CopyFormsFromSource(sourceDocument, geometryDocument, nestedSourceDocuments, orientationCorrection));
            if (error is not null)
                return Result.Text.Failed(error);

            try
            {
                using var regenerateTransaction = new Transaction(familyDocument, "Regenerate after geometry copy");
                regenerateTransaction.Start();
                familyDocument.Regenerate();
                regenerateTransaction.Commit();
            }
            catch (Exception ex)
            {
                return Result.Text.Failed($"Copied {copyResult.Copied} form(s), but the resulting family failed to regenerate: {ex.Message}{copyResult.Diagnostics}");
            }

            // Connectors must be created on the TOP-LEVEL family document (referencing nested symbol
            // geometry), same as ConnectorBuilder.RebuildConnectors in CreateNewFamily - creating them
            // on the innermost geometry document orphans them once the nested docs load back up.
            var connectorResult = CopyConnectorsFromSource(sourceDocument, familyDocument, orientationCorrection);

            // The 2D plan symbol is a nested "Generic Annotations" family instance sitting directly in
            // the TOP-LEVEL source family (not inside the 3D geometry), so it's copied the same way.
            var symbolResult = CopyGenericAnnotationsFromSource(sourceDocument, familyDocument, orientationCorrection);

            familyDocument.LoadFamily(activeDocument, new FamilyLoadOptions());

            var loadedFamily = new FilteredElementCollector(activeDocument)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .FirstOrDefault(f => f.Name.Equals(newFamilyName, StringComparison.OrdinalIgnoreCase));

            var replaceResult = loadedFamily is null
                ? (Replaced: 0, Failed: 0, Diagnostics: $"[DBG] Could not find loaded family '{newFamilyName}' in the active document to replace instances with.")
                : ReplaceInstancesOfSourceFamily(activeDocument, sourceFamily, loadedFamily, sourceTypeName);

            var message = $"[ROTATIFY] Copied {copyResult.Copied} form(s) from '{sourceFamily.Name}' into '{geometryHost}' of '{newFamilyName}' (type '{sourceTypeName}') and loaded it into the active document. {transformInfo}{orientationDiagnostics}{copyResult.Diagnostics} {connectorResult.Diagnostics} {symbolResult.Diagnostics} {typeParamResult.Diagnostics} {replaceResult.Diagnostics}";
            return copyResult.Failed > 0 && copyResult.Copied == 0
                ? Result.Text.Failed(message)
                : Result.Text.Succeeded(message);
        }
        catch (Exception ex)
        {
            var innerMessage = ex.InnerException is not null ? $" | Inner: {ex.InnerException.Message}" : string.Empty;
            return Result.Text.Failed($"Rotatify mode failed: {ex.Message}{innerMessage} [DBG] Copied {copyResult.Copied} form(s) before failure.{copyResult.Diagnostics} {ex.GetType().Name} at: {ex.StackTrace}");
        }
        finally
        {
            foreach (var nestedDocument in nestedSourceDocuments)
                nestedDocument.Close(false);
            sourceDocument?.Close(false);
            familyDocument?.Close(false);
            TryDeleteDirectory(workingDirectory);
        }
    }

    // Swaps every placed instance of the source family (any type) onto the matching type of the newly
    // loaded rotatable family, preserving the instance's ElementId/location/instance-parameter values.
    // The source family itself is left untouched here - deleting it is a deliberate separate step.
    private static (int Replaced, int Failed, string Diagnostics) ReplaceInstancesOfSourceFamily(Document activeDocument, Family sourceFamily, Family loadedFamily, string preferredTypeName)
    {
        var newSymbol = loadedFamily.GetFamilySymbolIds()
            .Select(activeDocument.GetElement)
            .OfType<FamilySymbol>()
            .FirstOrDefault(symbol => symbol.Name.Equals(preferredTypeName, StringComparison.OrdinalIgnoreCase))
            ?? loadedFamily.GetFamilySymbolIds()
                .Select(activeDocument.GetElement)
                .OfType<FamilySymbol>()
                .FirstOrDefault();

        if (newSymbol is null)
            return (0, 0, "[DBG] Could not find a type on the loaded family to replace instances with.");

        using var transaction = new Transaction(activeDocument, "Replace source family instances");
        transaction.Start();

        if (!newSymbol.IsActive)
            newSymbol.Activate();

        var instancesToReplace = new FilteredElementCollector(activeDocument)
            .OfClass(typeof(FamilyInstance))
            .WhereElementIsNotElementType()
            .Cast<FamilyInstance>()
            .Where(instance => instance.Symbol.Family.Id == sourceFamily.Id)
            .ToList();

        var replaced = 0;
        var failures = new List<string>();

        foreach (var instance in instancesToReplace)
        {
            try
            {
                instance.Symbol = newSymbol;
                replaced++;
            }
            catch (Exception ex)
            {
                failures.Add($"#{instance.Id} ({ex.Message})");
            }
        }

        transaction.Commit();

        var diagnostics = $"[ROTATIFY] Replaced {replaced}/{instancesToReplace.Count} instance(s) of '{sourceFamily.Name}' with '{newSymbol.Name}'.";
        if (failures.Count > 0)
            diagnostics += $" [DBG] {failures.Count} instance(s) failed to replace: {string.Join("; ", failures)}";

        return (replaced, failures.Count, diagnostics);
    }

    // Recreates each ELECTRICAL connector found on the source family, hosted on the destination's
    // largest solid at whatever face most closely matches the source connector's direction. The
    // source connector's `SystemClassification` (e.g. PowerBalanced, Data, FireAlarm) is parsed
    // straight into `ElectricalSystemType` since the two enums share member names for electrical
    // domain values. Non-electrical connectors (duct/pipe/cable tray/conduit) are not handled yet.
    private static (int Copied, int Failed, string Diagnostics) CopyConnectorsFromSource(Document sourceDocument, Document destinationDocument, Transform orientationCorrection)
    {
        var sourceConnectors = new FilteredElementCollector(sourceDocument)
            .OfClass(typeof(ConnectorElement))
            .Cast<ConnectorElement>()
            .Where(c => c.Domain == Domain.DomainElectrical)
            .ToList();

        if (sourceConnectors.Count == 0)
            return (0, 0, string.Empty);

        var destinationSolid = ConnectorBuilder.GetLargestSolid(destinationDocument);
        if (destinationSolid is null)
            return (0, sourceConnectors.Count, "[DBG] Could not find destination geometry to host copied connectors on.");

        using var transaction = new Transaction(destinationDocument, "Copy connectors from source family");
        transaction.Start();

        var existingConnectorIds = new FilteredElementCollector(destinationDocument)
            .OfClass(typeof(ConnectorElement))
            .ToElementIds();
        if (existingConnectorIds.Count > 0)
            destinationDocument.Delete(existingConnectorIds);

        var copied = 0;
        var failures = new List<string>();

        foreach (var sourceConnector in sourceConnectors)
        {
            try
            {
                var systemType = Enum.Parse<ElectricalSystemType>(sourceConnector.SystemClassification.ToString());
                var correctedDirection = orientationCorrection.OfVector(sourceConnector.Direction);
                var hostFace = ConnectorBuilder.GetClosestFace(destinationSolid, correctedDirection);
                if (hostFace is null)
                {
                    failures.Add($"#{sourceConnector.Id} ({systemType}): no matching destination face found");
                    continue;
                }

                ConnectorElement.CreateElectricalConnector(destinationDocument, systemType, hostFace.Reference);
                copied++;
            }
            catch (Exception ex)
            {
                failures.Add($"#{sourceConnector.Id} ({ex.Message})");
            }
        }

        transaction.Commit();

        var diagnostics = $"[ROTATIFY] Copied {copied}/{sourceConnectors.Count} connector(s) from source family.";
        if (failures.Count > 0)
            diagnostics += $" [DBG] {failures.Count} connector(s) failed to copy: {string.Join("; ", failures)}";

        return (copied, failures.Count, diagnostics);
    }

    // Copies the nested "Generic Annotations" family instance(s) that act as the 2D/coarse-detail
    // plan symbol - a separate concept from the 3D GenericForm geometry, so it needs its own pass.
    // The 2D plan symbol is inherently flat/view-facing, unlike the 3D body geometry - it is NOT
    // rotated by `orientationCorrection` (that broke the copy when tried), so this parameter is unused
    // for now but kept for signature symmetry with the other Copy*FromSource methods.
    private static (int Copied, int Failed, string Diagnostics) CopyGenericAnnotationsFromSource(Document sourceDocument, Document destinationDocument, Transform orientationCorrection)
    {
        var sourceAnnotationInstances = new FilteredElementCollector(sourceDocument)
            .OfClass(typeof(FamilyInstance))
            .OfCategory(BuiltInCategory.OST_GenericAnnotation)
            .Cast<FamilyInstance>()
            .ToList();

        if (sourceAnnotationInstances.Count == 0)
            return (0, 0, string.Empty);

        var existingAnnotationIds = new FilteredElementCollector(destinationDocument)
            .OfClass(typeof(FamilyInstance))
            .OfCategory(BuiltInCategory.OST_GenericAnnotation)
            .ToElementIds();

        using var transaction = new Transaction(destinationDocument, "Copy 2D symbol from source family");
        transaction.Start();

        if (existingAnnotationIds.Count > 0)
            destinationDocument.Delete(existingAnnotationIds);

        var copied = 0;
        var failures = new List<string>();

        foreach (var instance in sourceAnnotationInstances)
        {
            try
            {
                var copiedIds = ElementTransformUtils.CopyElements(sourceDocument, new[] { instance.Id }, destinationDocument, Transform.Identity, new CopyPasteOptions());
                copied += copiedIds.Count;
            }
            catch (Exception ex)
            {
                failures.Add($"#{instance.Id} ({instance.Symbol.Family.Name}) ({ex.Message})");
            }
        }

        transaction.Commit();

        var diagnostics = $"[ROTATIFY] Copied {copied}/{sourceAnnotationInstances.Count} 2D symbol instance(s) from source family.";
        if (failures.Count > 0)
            diagnostics += $" [DBG] {failures.Count} 2D symbol instance(s) failed to copy: {string.Join("; ", failures)}";

        return (copied, failures.Count, diagnostics);
    }

    // Copies TYPE parameter VALUES (Width, Manufacturer, custom shared params, etc. - shared by every
    // instance of a type) from the source SYMBOL onto the destination's current type, matched by
    // parameter name. Reads from `sourceSymbol.Parameters` (the project-side ElementType) rather than
    // opening the source family's own FamilyManager - some shared parameters (e.g. MagiCAD's
    // "MC Default System Code") are visible on the Symbol but do NOT show up in
    // `FamilyManager.Parameters` when the family is opened via EditFamily; the Symbol side has full
    // visibility regardless of how the parameter was originally added. Instance parameters aren't
    // touched here - they live per-instance in the project and are preserved automatically by
    // `instance.Symbol = newSymbol` in ReplaceInstancesOfSourceFamily. ElementId-valued params
    // (materials, etc.) are skipped since an ElementId from the source document is meaningless in the
    // destination document.
    private static (int Copied, int Skipped, string Diagnostics) CopyTypeParametersFromSource(FamilySymbol sourceSymbol, Document destinationDocument)
    {
        var destinationFamilyManager = destinationDocument.FamilyManager;
        var destinationParams = destinationFamilyManager.Parameters.Cast<FamilyParameter>().ToList();

        using var transaction = new Transaction(destinationDocument, "Copy type parameters from source family");
        transaction.Start();

        var copied = 0;
        var added = 0;
        var skipped = new List<string>();

        foreach (var sourceParam in sourceSymbol.Parameters.Cast<Parameter>())
        {
            var destinationParam = destinationParams
                .FirstOrDefault(p => p.Definition.Name.Equals(sourceParam.Definition.Name, StringComparison.OrdinalIgnoreCase));

            // The destination template doesn't necessarily define every shared parameter the source
            // uses (e.g. MagiCAD-specific ones like "MC Default System Code") - add it on the fly using
            // the SAME shared parameter definition, rather than silently dropping the value.
            if (destinationParam is null)
            {
                if (!sourceParam.IsShared || sourceParam.Definition is not ExternalDefinition externalDefinition)
                    continue;

                try
                {
                    destinationParam = destinationFamilyManager.AddParameter(externalDefinition, sourceParam.Definition.GetGroupTypeId(), isInstance: false);
                    destinationParams.Add(destinationParam);
                    added++;
                }
                catch (Exception ex)
                {
                    skipped.Add($"{sourceParam.Definition.Name} (failed to add missing shared parameter: {ex.Message})");
                    continue;
                }
            }

            if (destinationParam.IsInstance)
            {
                skipped.Add($"{sourceParam.Definition.Name} (destination param is instance-bound)");
                continue;
            }

            if (destinationParam.IsDeterminedByFormula)
            {
                skipped.Add($"{sourceParam.Definition.Name} (destination value is formula-driven)");
                continue;
            }

            if (sourceParam.StorageType != destinationParam.StorageType)
            {
                skipped.Add($"{sourceParam.Definition.Name} (storage type mismatch: {sourceParam.StorageType} vs {destinationParam.StorageType})");
                continue;
            }

            try
            {
                if (!sourceParam.HasValue)
                    continue;

                switch (sourceParam.StorageType)
                {
                    case StorageType.Double:
                        destinationFamilyManager.Set(destinationParam, sourceParam.AsDouble());
                        copied++;
                        break;
                    case StorageType.Integer:
                        destinationFamilyManager.Set(destinationParam, sourceParam.AsInteger());
                        copied++;
                        break;
                    case StorageType.String:
                        var stringValue = sourceParam.AsString();
                        if (stringValue is null)
                            continue;
                        destinationFamilyManager.Set(destinationParam, stringValue);
                        copied++;
                        break;
                    default:
                        skipped.Add($"{sourceParam.Definition.Name} (ElementId-valued params, e.g. materials, aren't copied across documents)");
                        break;
                }
            }
            catch (Exception ex)
            {
                skipped.Add($"{sourceParam.Definition.Name} ({ex.Message})");
            }
        }

        transaction.Commit();

        var diagnostics = $"[ROTATIFY] Copied {copied} type parameter value(s) from source family ({added} newly added).";
        if (skipped.Count > 0)
            diagnostics += $" [DBG] {skipped.Count} skipped: {string.Join("; ", skipped)}";

        return (copied, skipped.Count, diagnostics);
    }

    // Copies native form geometry (extrusions, blends, revolves, sweeps, swept blends) from the
    // source family into the destination geometry family, replacing whatever forms already exist there.
    // Forms are copied one at a time - `CopyElements` fails the whole batch if even one form can't be
    // copied (e.g. a sketch anchored to a reference plane/face that doesn't exist in the destination),
    // so batching would hide which forms actually work.
    // Not every source family keeps its 3D geometry directly at the top level - some (e.g. MagiCAD
    // exports) nest the REAL body one or more levels deep inside a "Generic Models" sub-family (often
    // alongside a trivial/placeholder form at the top level too), mirroring how our own destination
    // nests geometry inside 3D Orientation Family -> geometry family. `FindAllFormsSources` therefore
    // collects forms from EVERY level rather than stopping at the first level that has any.
    private static (int Copied, int Failed, string Diagnostics) CopyFormsFromSource(Document sourceDocument, Document destinationDocument, List<Document> nestedDocumentsToClose, Transform orientationCorrection)
    {
        var sources = new List<(Document Document, Transform Transform)>();
        FindAllFormsSources(sourceDocument, orientationCorrection, nestedDocumentsToClose, sources);

        var existingForms = new FilteredElementCollector(destinationDocument)
            .OfClass(typeof(GenericForm))
            .ToElementIds();

        using var transaction = new Transaction(destinationDocument, "Copy forms from source family");
        transaction.Start();

        if (existingForms.Count > 0)
            destinationDocument.Delete(existingForms);

        var copied = 0;
        var failures = new List<string>();
        var nestedLevelsUsed = 0;

        foreach (var (formsDocument, transform) in sources)
        {
            if (formsDocument != sourceDocument)
                nestedLevelsUsed++;

            // Voids are excluded: a void copied without its exact original cut-partner solid (e.g.
            // because that solid failed to copy) produces invalid geometry that fails LoadFamily's
            // audit outright, as opposed to a per-form copy failure which is merely reported and skipped.
            var sourceForms = new FilteredElementCollector(formsDocument)
                .OfClass(typeof(GenericForm))
                .Cast<GenericForm>()
                .Where(f => f.IsSolid)
                .ToList();

            foreach (var form in sourceForms)
            {
                try
                {
                    var copiedIds = ElementTransformUtils.CopyElements(formsDocument, new[] { form.Id }, destinationDocument, transform, new CopyPasteOptions());
                    copied += copiedIds.Count;
                }
                catch (Exception ex)
                {
                    failures.Add($"{form.GetType().Name} #{form.Id} ({ex.Message})");
                }
            }
        }

        transaction.Commit();

        var diagnostics = nestedLevelsUsed > 0
            ? $" [DBG] source geometry also found nested {nestedLevelsUsed} level(s) deep."
            : string.Empty;
        if (failures.Count > 0)
            diagnostics += $" [DBG] {failures.Count} form(s) failed to copy: {string.Join("; ", failures)}";

        return (copied, failures.Count, diagnostics);
    }

    // Recursively collects EVERY document (the family itself, and any nested family instances) that
    // holds GenericForm geometry, walked depth-first - does NOT stop at the first match, since some
    // source families keep a trivial form at the top level alongside the real body nested deeper.
    // Skips "Generic Annotations" instances since those are the 2D symbol, handled separately. Nested
    // documents opened along the way are added to `openedDocuments` so the caller can close them.
    private static void FindAllFormsSources(Document document, Transform cumulativeTransform, List<Document> openedDocuments, List<(Document Document, Transform Transform)> results, int depth = 0)
    {
        if (depth > 5)
            return;

        if (new FilteredElementCollector(document).OfClass(typeof(GenericForm)).GetElementCount() > 0)
            results.Add((document, cumulativeTransform));

        var nestedInstances = new FilteredElementCollector(document)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .Where(fi => (BuiltInCategory)(fi.Category?.Id.Value ?? -1) != BuiltInCategory.OST_GenericAnnotation)
            .ToList();

        foreach (var nested in nestedInstances)
        {
            Document nestedDocument;
            try
            {
                nestedDocument = document.EditFamily(nested.Symbol.Family);
            }
            catch
            {
                continue;
            }

            openedDocuments.Add(nestedDocument);
            FindAllFormsSources(nestedDocument, cumulativeTransform.Multiply(nested.GetTransform()), openedDocuments, results, depth + 1);
        }
    }

    // Auto-detects whether the source family's real 3D geometry needs a 90 deg correction to match
    // the rotatable template's default convention (thinnest extent along local Z). Computed from the
    // ACTUAL combined bounding box of the source's solids (across every nested level), not guessed:
    // if the thinnest extent is instead along Y (the common wall-hosted convention: X=width along wall,
    // Y=depth into wall, Z=vertical), rotating 90 deg about X swaps Y and Z to match the template.
    private static Transform DetermineOrientationCorrection(Document sourceDocument, out string diagnostics)
    {
        var scratchOpenedDocuments = new List<Document>();
        var sources = new List<(Document Document, Transform Transform)>();
        FindAllFormsSources(sourceDocument, Transform.Identity, scratchOpenedDocuments, sources);

        XYZ? min = null;
        XYZ? max = null;

        foreach (var (formsDocument, transform) in sources)
        {
            var forms = new FilteredElementCollector(formsDocument)
                .OfClass(typeof(GenericForm))
                .Cast<GenericForm>()
                .Where(f => f.IsSolid);

            foreach (var form in forms)
            {
                var bbox = form.get_BoundingBox(null);
                if (bbox is null)
                    continue;

                foreach (var localCorner in EnumerateBoundingBoxCorners(bbox))
                {
                    var corner = transform.OfPoint(localCorner);
                    min = min is null ? corner : new XYZ(Math.Min(min.X, corner.X), Math.Min(min.Y, corner.Y), Math.Min(min.Z, corner.Z));
                    max = max is null ? corner : new XYZ(Math.Max(max.X, corner.X), Math.Max(max.Y, corner.Y), Math.Max(max.Z, corner.Z));
                }
            }
        }

        foreach (var scratchDocument in scratchOpenedDocuments)
            scratchDocument.Close(false);

        if (min is null || max is null)
        {
            diagnostics = string.Empty;
            return Transform.Identity;
        }

        var extentX = max.X - min.X;
        var extentY = max.Y - min.Y;
        var extentZ = max.Z - min.Z;

        if (extentY <= extentX && extentY <= extentZ)
        {
            diagnostics = $" [DBG] source geometry looks wall-mounted (thinnest along Y: X={extentX:F2} Y={extentY:F2} Z={extentZ:F2}); rotated 90 deg to match the template's default orientation.";
            return Transform.CreateRotation(XYZ.BasisX, Math.PI / 2);
        }

        diagnostics = $" [DBG] source geometry orientation matches the template default (X={extentX:F2} Y={extentY:F2} Z={extentZ:F2}); no rotation applied.";
        return Transform.Identity;
    }

    private static IEnumerable<XYZ> EnumerateBoundingBoxCorners(BoundingBoxXYZ bbox)
    {
        yield return new XYZ(bbox.Min.X, bbox.Min.Y, bbox.Min.Z);
        yield return new XYZ(bbox.Max.X, bbox.Min.Y, bbox.Min.Z);
        yield return new XYZ(bbox.Min.X, bbox.Max.Y, bbox.Min.Z);
        yield return new XYZ(bbox.Min.X, bbox.Min.Y, bbox.Max.Z);
        yield return new XYZ(bbox.Max.X, bbox.Max.Y, bbox.Min.Z);
        yield return new XYZ(bbox.Max.X, bbox.Min.Y, bbox.Max.Z);
        yield return new XYZ(bbox.Min.X, bbox.Max.Y, bbox.Max.Z);
        yield return new XYZ(bbox.Max.X, bbox.Max.Y, bbox.Max.Z);
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

            var error = ReplaceOrientationGeometry(familyDocument, args, out var geometryHost, out var transformInfo,
                geometryDocument => GeometryBuilder.ReplaceForms(geometryDocument, args));
            if (error is not null)
                return Result.Text.Failed(error);

            var connectorError = ConnectorBuilder.RebuildConnectors(familyDocument, args);
            if (connectorError is not null)
                return Result.Text.Failed(connectorError);

            familyDocument.LoadFamily(activeDocument, new FamilyLoadOptions());

            return Result.Text.Succeeded($"Created rotatable family '{args.NewFamilyName}' and loaded it into the active document. Geometry was written into '{geometryHost}'. {transformInfo} {GeometryBuilder.LastDebugInfo}");
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

    private static void SetFamilyCategory(Document familyDocument, FlexiRfaArgs args) =>
        SetFamilyCategoryByName(familyDocument, args.FamilyCategory);

    // Rotatify mode uses the source family's own category rather than letting the user pick one.
    private static void SetFamilyCategoryByName(Document familyDocument, string? categoryName)
    {
        if (string.IsNullOrWhiteSpace(categoryName))
            return;

        using var categoryTransaction = new Transaction(familyDocument, "Set family category");
        categoryTransaction.Start();

        var category = familyDocument.Settings.Categories
            .Cast<Category>()
            .FirstOrDefault(c => c.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase));
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
    // `buildGeometry` is invoked on whichever document ends up hosting the geometry (the nested
    // orientation family, or its own nested geometry family if one exists).
    private static string? ReplaceOrientationGeometry(Document familyDocument, FlexiRfaArgs args, out string geometryHost, out string transformInfo, Action<Document> buildGeometry)
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
            buildGeometry(nestedDocument);
        }
        else
        {
            var geometryTransform = geometryInstance.GetTransform();
            transformInfo += $" [DIAG] {geometryInstance.Symbol.Family.Name} transform: Origin={FormatXyz(geometryTransform.Origin)}, BasisX={FormatXyz(geometryTransform.BasisX)}, BasisY={FormatXyz(geometryTransform.BasisY)}, BasisZ={FormatXyz(geometryTransform.BasisZ)}.";

            geometryHost = geometryInstance.Symbol.Family.Name;
            var geometryDocument = nestedDocument.EditFamily(geometryInstance.Symbol.Family);
            buildGeometry(geometryDocument);
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