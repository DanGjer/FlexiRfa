//-----------------------------------------------------------------------------
// FlexiRfaCommand.cs
//
// Creates a new rotatable family from a template, generating the preset geometry
// and electrical connectors, then loads it into the active document.
//-----------------------------------------------------------------------------

using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Structure;

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
        var sourceFamilyName = sourceFamily.Name;
        var sourceTypeName = selectedInstance.Symbol.Name;
        var newFamilyName = $"{sourceFamilyName} Replacement";

        if (selectedInstance.HostFace is not null)
            return Result.Text.Failed($"'{sourceFamilyName}' is not rotatable: face-based RFAs are not supported.");

        if (FamilyNameExists(activeDocument, newFamilyName))
            return Result.Text.Failed($"A family named '{newFamilyName}' already exists in this document.");

        // Abort entirely (before any work starts) if the source family or any of its placed instances
        // are owned by another user in a workshared model - a partial run that then can't swap/delete
        // elements someone else is editing is worse than not starting at all.
        var ownershipError = CheckOwnership(activeDocument, sourceFamily);
        if (ownershipError is not null)
            return Result.Text.Failed(ownershipError);

        var application = activeDocument.Application;
        var workingDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(workingDirectory);
        var workingFamilyPath = Path.Combine(workingDirectory, $"{newFamilyName}.rfa");

        Document? familyDocument = null;
        Document? sourceDocument = null;
        var nestedSourceDocuments = new List<Document>();
        var copyResult = default((int Copied, int Failed, int Total, string Diagnostics));
        TransactionGroup? projectTransactionGroup = null;
        var projectTransactionGroupStarted = false;
        var projectTransactionGroupCommitted = false;

        try
        {
            File.Copy(args.TemplatePath, workingFamilyPath);
            familyDocument = application.OpenDocumentFile(workingFamilyPath);

            if (!familyDocument.IsFamilyDocument)
                return Result.Text.Failed("The selected template is not a family document.");

            sourceDocument = activeDocument.EditFamily(sourceFamily);

            // Guard against rotatifying an already-rotatable family - if the source already contains a
            // nested "3D Orientation Family" (the structural fingerprint of this same template), running
            // Rotatify again would be redundant and could produce a confusing doubly-nested structure.
            if (IsAlreadyRotatable(sourceDocument))
                return Result.Text.Failed($"'{sourceFamilyName}' already appears to be a rotatable family (it already contains a nested '3D Orientation Family'). Rotatify is meant for non-rotatable source families only.");

            // NOTE: geometry is nested as-authored (Transform.Identity placement, then rotated as one
            // rigid instance) - the rotatable template has its own "Placement_CW" instance parameter
            // (Wall/Ceiling/Floor) with a formula that rotates the whole assembly automatically.
            // Pre-rotating the geometry beyond the fixed yaw correction below would double up with that
            // mechanism - mounting is instead handled by setting Placement_CW per swapped instance in
            // ReplaceInstancesOfSourceFamily.
            // SEPARATE issue, same magnitude but a DIFFERENT axis: the template's nested
            // "magiFamilyGeom Geometry" family has a CONSTANT, fixed 180 deg yaw baked into its own
            // placement transform (BasisX=(-1,0,0), BasisY=(0,-1,0), BasisZ=(0,0,1) - every run, every
            // family, unconditionally). Correcting for it here, universally - this is not a per-source
            // guess like Placement_CW, it's undoing a constant property of the template itself.
            // HISTORY: earlier attempts copied individual GenericForm elements out of the source family
            // and tried to rotate/mirror each one - this hit a long tail of Revit-specific breakage
            // (Blend elements corrupting under rotation, forms joined to each other blocking on rotate,
            // sketches with labeled dimensions refusing to copy at all). REPLACED (2026-09-02) with
            // `NestSourceFamilyAsGeometry`, which loads the WHOLE source family as one nested instance
            // and rotates that single instance instead - none of the above applies to a single rigid
            // instance rotation, and the 2D symbol/3D geometry facing relationship is preserved for free
            // since both live inside the same untouched source family.
            var orientationAxis = XYZ.BasisZ;
            var orientationAngle = Math.PI;

            SetFamilyCategoryByName(familyDocument, sourceFamily.FamilyCategory?.Name);
            RenameCurrentType(familyDocument, sourceTypeName);

            // Type parameters (Width, Manufacturer, custom shared params, etc.) live on the TOP-LEVEL
            // family, matched by name - independent of the geometry/connector/instance steps below.
            var typeParamResult = CopyTypeParametersFromSource(selectedInstance.Symbol, familyDocument);

            var error = ReplaceOrientationGeometry(familyDocument, args, out var geometryHost, out var transformInfo,
                geometryDocument => copyResult = CopyFormsFromSource(sourceDocument, geometryDocument, nestedSourceDocuments, orientationAxis, orientationAngle));
            if (error is not null)
                return Result.Text.Failed(error);

            // Abort BEFORE touching the active document at all if geometry nesting produced nothing -
            // otherwise the steps below would happily load an empty family and swap real instances onto
            // it, which is far worse than just failing here.
            if (copyResult.Copied == 0)
                return Result.Text.Failed($"'{sourceFamilyName}' is unrotatable: no geometry could be copied. No project changes were made.{copyResult.Diagnostics}");

            if (copyResult.Failed > 0 && !args.BypassFailsafes)
                return Result.Text.Failed($"'{sourceFamilyName}' is unrotatable: {copyResult.Failed} geometry element(s) failed to copy. No project changes were made.{copyResult.Diagnostics}");

            try
            {
                using var regenerateTransaction = new Transaction(familyDocument, "Regenerate after geometry copy");
                regenerateTransaction.Start();
                familyDocument.Regenerate();
                regenerateTransaction.Commit();
            }
            catch (Exception ex)
            {
                return Result.Text.Failed($"Nested source geometry ({copyResult.Copied}), but the resulting family failed to regenerate: {ex.Message}{copyResult.Diagnostics}");
            }

            // Connectors must be created on the TOP-LEVEL family document (referencing nested symbol
            // geometry), same as ConnectorBuilder.RebuildConnectors in CreateNewFamily - creating them
            // on the innermost geometry document orphans them once the nested docs load back up.
            var connectorResult = CopyConnectorsFromSource(sourceDocument, familyDocument, Transform.CreateRotation(orientationAxis, orientationAngle));

            // The 2D plan symbol is a nested "Generic Annotations" family instance sitting directly in
            // the TOP-LEVEL source family (not inside the 3D geometry), so it's copied the same way.
            var symbolResult = CopyGenericAnnotationsFromSource(sourceDocument, familyDocument);

            if (connectorResult.Failed > 0 || symbolResult.Failed > 0)
                return Result.Text.Failed($"'{sourceFamilyName}' is unrotatable: a required connector or 2D symbol could not be copied. No project changes were made.{connectorResult.Diagnostics} {symbolResult.Diagnostics}");

            familyDocument.LoadFamily(activeDocument, new FamilyLoadOptions());

            var loadedFamily = new FilteredElementCollector(activeDocument)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .FirstOrDefault(f => f.Name.Equals(newFamilyName, StringComparison.OrdinalIgnoreCase));

            if (loadedFamily is null)
                return Result.Text.Failed($"'{sourceFamilyName}' is unrotatable: the loaded replacement family could not be found. No project changes were made.");

            if (sourceFamily.FamilyPlacementType != loadedFamily.FamilyPlacementType)
            {
                return Result.Text.Failed($"'{sourceFamilyName}' is unrotatable: the source family uses '{sourceFamily.FamilyPlacementType}' placement, but the rotatable template uses '{loadedFamily.FamilyPlacementType}'. Revit cannot replace these hosted instances safely.");
            }

            projectTransactionGroup = new TransactionGroup(activeDocument, "Rotatify replacement");
            projectTransactionGroup.Start();
            projectTransactionGroupStarted = true;

            // Parameters not embedded in the family itself (e.g. MagiCAD's "MC ..." params) are often
            // PROJECT parameters bound to categories like Electrical Fixtures - they only become available
            // on the type once the family is actually loaded into the project under that category.
            var loadedSymbol = loadedFamily?.GetFamilySymbolIds()
                .Select(activeDocument.GetElement)
                .OfType<FamilySymbol>()
                .FirstOrDefault();

            var projectBindingResult = loadedSymbol is not null
                ? ApplyProjectBoundParameters(activeDocument, loadedSymbol, typeParamResult.Unmatched)
                : (Copied: 0, Diagnostics: string.Empty);

            var replaceResult = ReplaceInstancesOfSourceFamily(activeDocument, sourceFamily, loadedFamily!, sourceTypeName);

            if (replaceResult.Failed > 0 || replaceResult.Replaced == 0)
            {
                projectTransactionGroup.RollBack();
                return Result.Text.Failed($"'{sourceFamilyName}' is unrotatable: {replaceResult.Failed} instance(s) could not be replaced. All project changes were rolled back.{replaceResult.Diagnostics}");
            }

            // Only delete the source family once every instance has actually been moved off it - a
            // partial replace (some instances failed the swap) must NOT delete the family they still use.
            // Once deleted, the "Replacement" suffix no longer makes sense - the new family takes over
            // the source's original name entirely.
            var deleteSourceResult = DeleteSourceFamilyIfUnused(activeDocument, sourceFamily, loadedFamily!);

            if (!deleteSourceResult.Deleted)
            {
                projectTransactionGroup.RollBack();
                return Result.Text.Failed($"'{sourceFamilyName}' is unrotatable: the source family could not be safely deleted after replacement. All project changes were rolled back. SourceId={sourceFamily.Id.Value}, ReplacementId={loadedFamily!.Id.Value}. {replaceResult.Diagnostics} {deleteSourceResult.Diagnostics}");
            }

            projectTransactionGroup.Assimilate();
            projectTransactionGroupCommitted = true;

            var geometryStatus = copyResult.Failed == 0
                ? $"All geometry copied ({copyResult.Copied}/{copyResult.Total})"
                : $"Geometry copied partially ({copyResult.Copied}/{copyResult.Total}; {copyResult.Failed} failed)";
            var connectorStatus = connectorResult.Failed == 0
                ? $"All connectors copied ({connectorResult.Copied})"
                : $"Connectors copied partially ({connectorResult.Copied}; {connectorResult.Failed} failed)";
            var symbolStatus = symbolResult.Failed == 0
                ? $"All 2D symbols copied ({symbolResult.Copied})"
                : $"2D symbols copied partially ({symbolResult.Copied}; {symbolResult.Failed} failed)";
            var chanceNote = args.BypassFailsafes && copyResult.Failed > 0
                ? " Continued despite geometry failures because 'Bypass failsafes' was selected."
                : string.Empty;
            var message = $"[ROTATIFY] Completed '{sourceFamilyName}'. {geometryStatus}. {connectorStatus}. {symbolStatus}. Type parameters copied: {typeParamResult.Copied}. Project parameters applied: {projectBindingResult.Copied}. Instances replaced: {replaceResult.Replaced}. Source family deleted and renamed successfully.{chanceNote}";
            return copyResult.Failed > 0 && copyResult.Copied == 0
                ? Result.Text.Failed(message)
                : Result.Text.Succeeded(message);
        }
        catch (Exception ex)
        {
            if (projectTransactionGroupStarted && projectTransactionGroup is not null && !projectTransactionGroupCommitted)
                projectTransactionGroup.RollBack();
            var innerMessage = ex.InnerException is not null ? $" | Inner: {ex.InnerException.Message}" : string.Empty;
            return Result.Text.Failed($"Rotatify mode failed: {ex.Message}{innerMessage} [DBG] Nested source geometry: {copyResult.Copied} before failure.{copyResult.Diagnostics} {ex.GetType().Name} at: {ex.StackTrace}");
        }
        finally
        {
            projectTransactionGroup?.Dispose();
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
        var placementSet = 0;
        var failures = new List<string>();

        foreach (var instance in instancesToReplace)
        {
            // Determine the instance's REAL mounting (Wall/Ceiling/Floor) from its host/hosted face
            // BEFORE swapping, then set it on the swapped instance so the template's own "Placement_CW"
            // formula (0=Wall +90 deg, 1=Ceiling +0 deg, 2=Floor +180 deg) orients it correctly - this
            // is what the template is actually designed to use, instead of us pre-rotating geometry.
            var mountingType = DetermineMountingType(instance);

            try
            {
                instance.Symbol = newSymbol;
                replaced++;

                if (mountingType is not null)
                {
                    var placementParam = instance.LookupParameter("Placement_CW");
                    if (placementParam is not null && !placementParam.IsReadOnly)
                    {
                        placementParam.Set(mountingType.Value);
                        placementSet++;
                    }
                }
            }
            catch (Exception ex)
            {
                failures.Add($"#{instance.Id} ({ex.Message})");
            }
        }

        transaction.Commit();

        var diagnostics = $"[ROTATIFY] Replaced {replaced}/{instancesToReplace.Count} instance(s) of '{sourceFamily.Name}' with '{newSymbol.Name}' (mounting set on {placementSet}).";
        if (failures.Count > 0)
            diagnostics += $" [DBG] {failures.Count} instance(s) failed to replace: {string.Join("; ", failures)}";

        return (replaced, failures.Count, diagnostics);
    }

    // Determines an instance's REAL mounting surface, physically, from its actual placement - not from
    // guessing at the source family's geometry proportions. Returns the value expected by the rotatable
    // template's "Placement_CW" parameter: 0 = Wall, 1 = Ceiling, 2 = Floor, or null if undeterminable.
    private static int? DetermineMountingType(FamilyInstance instance)
    {
        switch (instance.Host)
        {
            case Wall:
                return 0;
            case Ceiling:
                return 1;
            case Floor:
                return 2;
        }

        // Face-hosted (e.g. Generic Model face-based) instances aren't hosted by a category-typed
        // element - resolve the actual host face's normal instead. A face whose normal points mostly
        // down means the device hangs below it (ceiling); mostly up means it sits on top (floor);
        // otherwise it's roughly vertical, i.e. a wall.
        try
        {
            var hostFaceReference = instance.HostFace;
            if (hostFaceReference is not null)
            {
                var document = instance.Document;
                var hostElement = document.GetElement(hostFaceReference);
                if (hostElement?.GetGeometryObjectFromReference(hostFaceReference) is Face face)
                {
                    var normal = face.ComputeNormal(new UV(0.5, 0.5));
                    var verticalAlignment = normal.DotProduct(XYZ.BasisZ);
                    if (verticalAlignment < -0.5)
                        return 1; // Ceiling
                    if (verticalAlignment > 0.5)
                        return 2; // Floor

                    return 0; // Wall
                }
            }
        }
        catch
        {
            // fall through to the geometric fallback below
        }

        // Genuinely non-hosted (e.g. plain point-based) instances carry NO host metadata at all - this
        // is a best-effort guess rather than a reliable signal, since we're copying geometry as-authored
        // (Transform.Identity) and can't otherwise know which local axis the source treats as "depth".
        // If the instance's own local Z ends up roughly vertical in the world, assume that's the
        // mounting-normal direction (Ceiling if it points down, Floor if up); otherwise assume Wall.
        // Worst case the user has to manually correct Placement_CW - acceptable given there's no
        // reliable signal to work with for this class of family.
        var basisZ = instance.GetTransform().BasisZ;
        var verticalAlignmentFallback = basisZ.DotProduct(XYZ.BasisZ);
        if (Math.Abs(verticalAlignmentFallback) < 0.5)
            return 0; // Wall

        return verticalAlignmentFallback < 0 ? 1 : 2; // Ceiling if pointing down, Floor if pointing up
    }

    // Deletes the source family from the active project, but ONLY if zero placed instances still
    // reference it (any type) - a real, explicit safety gate, not an assumption based on the replace
    // step's reported counts, in case some instances were missed or failed silently elsewhere. Once
    // deleted, the source's original name is free again, so the "Replacement" family is renamed to take
    // it over completely - both steps share one transaction, so a rename failure rolls back the delete
    // too rather than leaving the project in a half-finished state.
    private static (bool Deleted, string Diagnostics) DeleteSourceFamilyIfUnused(Document activeDocument, Family sourceFamily, Family loadedFamily)
    {
        var sourceFamilyName = sourceFamily.Name;

        var remainingInstanceCount = new FilteredElementCollector(activeDocument)
            .OfClass(typeof(FamilyInstance))
            .WhereElementIsNotElementType()
            .Cast<FamilyInstance>()
            .Count(instance => instance.Symbol.Family.Id == sourceFamily.Id);

        if (remainingInstanceCount > 0)
            return (false, $"[ROTATIFY] Source family '{sourceFamilyName}' NOT deleted - {remainingInstanceCount} instance(s) still reference it.");

        using var transaction = new Transaction(activeDocument, "Delete rotatified source family");
        transaction.Start();

        try
        {
            activeDocument.Delete(sourceFamily.Id);
            loadedFamily.Name = sourceFamilyName;
            transaction.Commit();
            return (true, $"[ROTATIFY] Deleted source family '{sourceFamilyName}' (0 remaining instances) and renamed the new family to take over its name.");
        }
        catch (Exception ex)
        {
            transaction.RollBack();
            return (false, $"[ROTATIFY] Could not delete/rename source family '{sourceFamilyName}': {ex.Message}");
        }
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

                // Prefer a face closely matching the source connector's direction, but exact placement
                // isn't critical - what matters is that A connector exists with the right attributes.
                // Falls back to whichever planar face is the closest available match if nothing is
                // within the strict tolerance (e.g. geometry that ends up mirrored/rotated differently
                // than expected shouldn't leave the type with zero connectors).
                var hostFace = ConnectorBuilder.GetClosestFace(destinationSolid, correctedDirection)
                    ?? destinationSolid.Faces
                        .OfType<PlanarFace>()
                        .Where(f => f.Reference is not null)
                        .OrderByDescending(f => f.FaceNormal.DotProduct(correctedDirection))
                        .FirstOrDefault();

                if (hostFace is null)
                {
                    failures.Add($"#{sourceConnector.Id} ({systemType}): no destination face found at all");
                    continue;
                }

                var newConnector = ConnectorElement.CreateElectricalConnector(destinationDocument, systemType, hostFace.Reference);

                // Circuits validate against more than just SystemClassification (Voltage, Number of
                // Poles, Apparent Power/Load, Power Factor, Load Classification, etc.) - a bare connector
                // with Revit's defaults for these makes existing circuits consider the family "no longer
                // matching the properties for the Circuit" and prompt to disconnect. Copying every
                // settable parameter value keeps a swapped-in instance's circuit membership intact.
                CopyConnectorParameterValues(sourceConnector, newConnector);

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

    // Copies every settable parameter value (Voltage, Number of Poles, Apparent Power, Power Factor,
    // Load Classification, etc.) from the source connector onto the newly created destination
    // connector, matched by name. Without this, circuits built against the source instance consider
    // the swapped-in family "no longer matching the properties for the Circuit" and prompt to
    // disconnect, since `CreateElectricalConnector` only sets SystemClassification and leaves
    // everything else at Revit's bare defaults.
    private static void CopyConnectorParameterValues(ConnectorElement sourceConnector, ConnectorElement destinationConnector)
    {
        foreach (Parameter sourceParam in sourceConnector.Parameters)
        {
            if (!sourceParam.HasValue)
                continue;

            var destinationParam = destinationConnector.LookupParameter(sourceParam.Definition.Name);
            if (destinationParam is null || destinationParam.IsReadOnly || destinationParam.StorageType != sourceParam.StorageType)
                continue;

            try
            {
                switch (sourceParam.StorageType)
                {
                    case StorageType.Double:
                        destinationParam.Set(sourceParam.AsDouble());
                        break;
                    case StorageType.Integer:
                        destinationParam.Set(sourceParam.AsInteger());
                        break;
                    case StorageType.String:
                        var stringValue = sourceParam.AsString();
                        if (stringValue is not null)
                            destinationParam.Set(stringValue);
                        break;
                }
            }
            catch
            {
                // Best-effort - some connector parameters (e.g. read-only computed ones not caught by
                // IsReadOnly) can still reject a Set call; skipping one shouldn't abort the connector copy.
            }
        }
    }

    // Copies the nested "Generic Annotations" family instance(s) that act as the 2D/coarse-detail
    // plan symbol - a separate concept from the 3D GenericForm geometry, so it needs its own pass.
    // The 2D plan symbol is inherently flat/view-facing, unlike the 3D body geometry - it is inserted
    // as-is (no rotation correction; applying one broke the copy when tried).
    private static (int Copied, int Failed, string Diagnostics) CopyGenericAnnotationsFromSource(Document sourceDocument, Document destinationDocument)
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
    // destination document. Source parameters with no matching FAMILY parameter are returned as
    // `Unmatched` rather than dropped - many of those (e.g. MagiCAD's "MC ..." params) are actually
    // PROJECT parameters bound to the family's category, which only become settable once the family is
    // loaded into the project; see `ApplyProjectBoundParameters`.
    private static (int Copied, int Skipped, string Diagnostics, List<Parameter> Unmatched) CopyTypeParametersFromSource(FamilySymbol sourceSymbol, Document destinationDocument)
    {
        var destinationFamilyManager = destinationDocument.FamilyManager;
        var destinationParams = destinationFamilyManager.Parameters.Cast<FamilyParameter>().ToList();

        using var transaction = new Transaction(destinationDocument, "Copy type parameters from source family");
        transaction.Start();

        var copied = 0;
        var skipped = new List<string>();
        var unmatched = new List<Parameter>();

        foreach (var sourceParam in sourceSymbol.Parameters.Cast<Parameter>())
        {
            var destinationParam = destinationParams
                .FirstOrDefault(p => p.Definition.Name.Equals(sourceParam.Definition.Name, StringComparison.OrdinalIgnoreCase));

            if (destinationParam is null)
            {
                if (sourceParam.HasValue)
                    unmatched.Add(sourceParam);
                continue;
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

        var diagnostics = $"[ROTATIFY] Copied {copied} type parameter value(s) from source family.";
        if (skipped.Count > 0)
            diagnostics += $" [DBG] {skipped.Count} skipped: {string.Join("; ", skipped)}";

        return (copied, skipped.Count, diagnostics, unmatched);
    }

    // Second pass, run AFTER LoadFamily: sets values for source parameters that had no matching FAMILY
    // parameter (pass 1's `Unmatched`). These are typically PROJECT parameters bound to the family's
    // category (e.g. MagiCAD's "MC ..." params bound to Electrical Fixtures etc.) - Revit applies such
    // bindings automatically to any type of a bound category once it's actually loaded into the
    // project, so `loadedSymbol.LookupParameter(name)` only finds them at this point, not during
    // family authoring.
    private static (int Copied, string Diagnostics) ApplyProjectBoundParameters(Document activeDocument, FamilySymbol loadedSymbol, List<Parameter> unmatchedSourceParams)
    {
        if (unmatchedSourceParams.Count == 0)
            return (0, string.Empty);

        using var transaction = new Transaction(activeDocument, "Apply project-bound parameters to rotatified type");
        transaction.Start();

        if (!loadedSymbol.IsActive)
            loadedSymbol.Activate();

        var copied = 0;
        var failures = new List<string>();

        foreach (var sourceParam in unmatchedSourceParams)
        {
            var destinationParam = loadedSymbol.LookupParameter(sourceParam.Definition.Name);
            if (destinationParam is null)
            {
                failures.Add($"{sourceParam.Definition.Name} (no project-bound parameter found on the new type)");
                continue;
            }

            if (destinationParam.IsReadOnly)
            {
                failures.Add($"{sourceParam.Definition.Name} (project-bound parameter is read-only)");
                continue;
            }

            try
            {
                switch (sourceParam.StorageType)
                {
                    case StorageType.Double:
                        destinationParam.Set(sourceParam.AsDouble());
                        copied++;
                        break;
                    case StorageType.Integer:
                        destinationParam.Set(sourceParam.AsInteger());
                        copied++;
                        break;
                    case StorageType.String:
                        var stringValue = sourceParam.AsString();
                        if (stringValue is not null)
                        {
                            destinationParam.Set(stringValue);
                            copied++;
                        }
                        else
                        {
                            failures.Add($"{sourceParam.Definition.Name} (source string value is null)");
                        }
                        break;
                    default:
                        failures.Add($"{sourceParam.Definition.Name} (storage type {sourceParam.StorageType} not handled)");
                        break;
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{sourceParam.Definition.Name} ({ex.Message})");
            }
        }

        transaction.Commit();

        var diagnostics = $"[ROTATIFY] Applied {copied}/{unmatchedSourceParams.Count} project-bound parameter value(s) to the new type.";
        if (failures.Count > 0)
            diagnostics += $" [DBG] {failures.Count} failed: {string.Join("; ", failures)}";

        return (copied, diagnostics);
    }

    private static (int Copied, int Failed, int Total, string Diagnostics) CopyFormsFromSource(Document sourceDocument, Document destinationDocument, List<Document> nestedDocumentsToClose, XYZ orientationRotationAxis, double orientationRotationAngle)
    {
        var sources = new List<(Document Document, Transform Transform)>();
        FindAllFormsSources(sourceDocument, Transform.Identity, nestedDocumentsToClose, sources);

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
        var blendsFixedUpViaMirror = 0;
        var totalByType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var copiedByType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var rotation = Transform.CreateRotation(orientationRotationAxis, orientationRotationAngle);
        var isHalfTurn = Math.Abs(Math.Abs(orientationRotationAngle) - Math.PI) < 1e-9;
        var mirrorNormalA = isHalfTurn
            ? (orientationRotationAxis.CrossProduct(XYZ.BasisX).IsZeroLength()
                ? orientationRotationAxis.CrossProduct(XYZ.BasisY)
                : orientationRotationAxis.CrossProduct(XYZ.BasisX)).Normalize()
            : XYZ.Zero;
        var mirrorNormalB = isHalfTurn ? orientationRotationAxis.CrossProduct(mirrorNormalA).Normalize() : XYZ.Zero;

        foreach (var (formsDocument, transform) in sources)
        {
            if (formsDocument != sourceDocument)
                nestedLevelsUsed++;

            var sourceForms = new FilteredElementCollector(formsDocument)
                .OfClass(typeof(GenericForm))
                .Cast<GenericForm>()
                .Where(f => f.IsSolid)
                .ToList();

            foreach (var form in sourceForms)
            {
                var formType = form.GetType().Name;
                totalByType[formType] = totalByType.GetValueOrDefault(formType) + 1;
                try
                {
                    var copiedIds = ElementTransformUtils.CopyElements(
                        formsDocument,
                        new[] { form.Id },
                        destinationDocument,
                        form is Blend && isHalfTurn ? transform : rotation.Multiply(transform),
                        new CopyPasteOptions());
                    copied += copiedIds.Count;
                    copiedByType[formType] = copiedByType.GetValueOrDefault(formType) + copiedIds.Count;

                    if (form is Blend && isHalfTurn)
                    {
                        var idList = copiedIds.ToList();
                        ElementTransformUtils.MirrorElements(destinationDocument, idList, Plane.CreateByNormalAndOrigin(mirrorNormalA, XYZ.Zero), false);
                        ElementTransformUtils.MirrorElements(destinationDocument, idList, Plane.CreateByNormalAndOrigin(mirrorNormalB, XYZ.Zero), false);
                        blendsFixedUpViaMirror += copiedIds.Count;
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{form.GetType().Name} #{form.Id} ({ex.Message})");
                }
            }
        }

        transaction.Commit();

        var geometrySummary = string.Join(", ", totalByType
            .OrderBy(pair => pair.Key)
            .Select(pair => $"{pair.Key} {copiedByType.GetValueOrDefault(pair.Key)}/{pair.Value}"));
        var diagnostics = $" [ROTATIFY] Geometry copied: {copied}/{totalByType.Values.Sum()} form(s) ({geometrySummary}).";
        if (nestedLevelsUsed > 0)
            diagnostics += $" [DBG] source geometry also found nested {nestedLevelsUsed} level(s) deep.";
        if (blendsFixedUpViaMirror > 0)
            diagnostics += $" [DBG] rotated {blendsFixedUpViaMirror} Blend form(s) via mirror workaround.";
        if (failures.Count > 0)
            diagnostics += $" [DBG] {failures.Count} form(s) failed to copy: {string.Join("; ", failures)}";

        return (copied, failures.Count, totalByType.Values.Sum(), diagnostics);
    }

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
            try
            {
                var nestedDocument = document.EditFamily(nested.Symbol.Family);
                openedDocuments.Add(nestedDocument);
                FindAllFormsSources(nestedDocument, cumulativeTransform.Multiply(nested.GetTransform()), openedDocuments, results, depth + 1);
            }
            catch
            {
            }
        }
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

    // Aborts entirely (returns a non-null message) if the source family or any of its placed instances
    // are owned by another user - checked BEFORE any work starts, so a Rotatify run never gets partway
    // through only to discover it can't swap/delete elements someone else is actively editing. Not
    // applicable to non-workshared (standalone) documents, which have no ownership concept at all.
    private static string? CheckOwnership(Document activeDocument, Family sourceFamily)
    {
        if (!activeDocument.IsWorkshared)
            return null;

        var elementsToCheck = new FilteredElementCollector(activeDocument)
            .OfClass(typeof(FamilyInstance))
            .WhereElementIsNotElementType()
            .Cast<FamilyInstance>()
            .Where(instance => instance.Symbol.Family.Id == sourceFamily.Id)
            .Select(instance => (Id: instance.Id, Description: $"instance #{instance.Id}"))
            .Append((Id: sourceFamily.Id, Description: $"source family '{sourceFamily.Name}'"))
            .ToList();

        var blockers = new List<string>();

        foreach (var (id, description) in elementsToCheck)
        {
            var status = WorksharingUtils.GetCheckoutStatus(activeDocument, id, out var owner);
            if (status == CheckoutStatus.OwnedByOtherUser)
                blockers.Add($"{description} (owned by '{owner}')");
        }

        if (blockers.Count == 0)
            return null;

        return $"Rotatify mode aborted: {blockers.Count} element(s) are owned by another user and must be released before running this: {string.Join("; ", blockers)}";
    }

    // The rotatable template's nested "3D Orientation Family" instance is a reliable structural
    // fingerprint - any family built from this template has one, non-rotatable source families don't.
    private static bool IsAlreadyRotatable(Document sourceDocument) =>
        new FilteredElementCollector(sourceDocument)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .Any(instance => instance.Symbol.Family.Name.Equals("3D Orientation Family", StringComparison.OrdinalIgnoreCase));

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