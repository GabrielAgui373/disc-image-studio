using System.Security.Cryptography;

namespace DiscImageStudio.Generation;

/// <summary>
/// Transactional publication of an artifact with its companions (Task 11.1).
/// <list type="number">
/// <item>The artifact is written into a hidden staging folder beside the destination (same
/// filesystem, so the final renames are atomic), hashed and counted on the way, flushed to disk
/// and checked against the plan's size.</item>
/// <item>The sidecars, the cue sheet and the integrity manifest are written into the same staging
/// folder under their final names, recording the final artifact path.</item>
/// <item>An existing artifact and its companions are moved aside as hidden backups.</item>
/// <item>Companions are moved into place first and the artifact last, so the final artifact path
/// never exists without its sidecars. Any failure removes what was moved and restores the
/// backups.</item>
/// <item>Backups and the staging folder are deleted; a canceled or failed run leaves no new file
/// at the final paths.</item>
/// </list>
/// </summary>
public sealed partial class DiscGenerationService
{
    /// <summary>Room reserved for sidecars, cue sheet and manifest next to the artifact.</summary>
    private const long CompanionMarginBytes = 256 * 1024;

    /// <summary>
    /// Checks a destination before a long generation: extension and source rules, existing
    /// files, permission to write and free space. Fast and side-effect free apart from a probe
    /// file that is deleted at once.
    /// </summary>
    public OutputPreflight Preflight(GenerationPlan plan, string outputPath, ArtifactReplacePolicy replacePolicy)
    {
        ArgumentNullException.ThrowIfNull(plan);
        List<GenerationValidationError> problems = [];
        string target;
        try
        {
            target = ValidateOutputPath(plan, outputPath);
        }
        catch (GenerationValidationException exception)
        {
            problems.Add(exception.Error);
            target = string.IsNullOrWhiteSpace(outputPath) ? string.Empty : Path.GetFullPath(outputPath);

            // The space on the chosen disk is still worth showing while the name gets fixed.
            string? folder = target.Length == 0 ? null : NearestExistingDirectory(Path.GetDirectoryName(target));
            long? free = folder is null ? null : AvailableBytes(folder);
            AddSpaceProblem(problems, RequiredBytes(plan), free);
            return new OutputPreflight(target, false, [], RequiredBytes(plan), free, problems);
        }

        bool exists = File.Exists(target);
        string[] companions = CompanionPaths(plan, target).Where(File.Exists).ToArray();
        if (exists && replacePolicy == ArtifactReplacePolicy.Fail)
        {
            problems.Add(Error("generation.output.exists", nameof(outputPath), ("path", target)));
        }

        if (Directory.Exists(target))
        {
            problems.Add(Error("generation.output.is_directory", nameof(outputPath), ("path", target)));
        }

        string? directory = Path.GetDirectoryName(target);
        string? existing = NearestExistingDirectory(directory);
        if (existing is null)
        {
            problems.Add(Error("generation.output.directory_unreachable", nameof(outputPath), ("path", directory)));
        }
        else if (!CanWriteIn(existing))
        {
            problems.Add(Error("generation.output.not_writable", nameof(outputPath), ("directory", existing)));
        }

        long required = RequiredBytes(plan);
        long? available = existing is null ? null : AvailableBytes(existing);
        AddSpaceProblem(problems, required, available);

        return new OutputPreflight(target, exists, companions, required, available, problems);
    }

    /// <summary>
    /// Generates <paramref name="plan"/> and publishes the artifact at <paramref name="outputPath"/>
    /// with its sidecars and, by default, an integrity manifest, as one unit.
    /// </summary>
    public Task<PublishedArtifact> PublishFileAsync(
        GenerationPlan plan,
        string outputPath,
        ArtifactPublishOptions? options = null,
        IProgress<GenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArtifactPublishOptions effective = options ?? new ArtifactPublishOptions();
        return Task.Run(() => Publish(plan, outputPath, effective, progress, cancellationToken), cancellationToken);
    }

    private PublishedArtifact Publish(
        GenerationPlan plan,
        string outputPath,
        ArtifactPublishOptions options,
        IProgress<GenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        string target = ValidateOutputPath(plan, outputPath);
        if (File.Exists(target) && options.ReplacePolicy == ArtifactReplacePolicy.Fail)
        {
            throw Validation("generation.output.exists", nameof(outputPath), ("path", target));
        }

        string directory = Path.GetDirectoryName(target)!;
        string name = Path.GetFileName(target);
        Directory.CreateDirectory(directory);
        string token = Guid.NewGuid().ToString("N");
        string staging = Path.Combine(directory, $".{name}.{token}.staging");
        Directory.CreateDirectory(staging);
        try
        {
            // 1. Artifact, hashed while it is written.
            string stagedArtifact = Path.Combine(staging, name);
            PublishedFile artifact;
            using (FileStream file = new(
                       stagedArtifact,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       1024 * 1024,
                       FileOptions.SequentialScan))
            using (HashingWriteStream hashing = new(file))
            {
                GenerateStream(plan, hashing, progress, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                file.Flush(flushToDisk: true);
                if (hashing.BytesWritten != plan.EstimatedBytes)
                {
                    throw new InvalidDataException(
                        $"generation.output.size_mismatch: wrote {hashing.BytesWritten} bytes, the plan expects {plan.EstimatedBytes}.");
                }

                artifact = new PublishedFile(target, "artifact", hashing.BytesWritten, hashing.HashHex());
            }

            // 2. Companions under their final names, recording the final artifact path.
            Report(progress, plan, "generation.sidecars");
            WriteSidecars(plan, target, stagedArtifact, cancellationToken);
            List<(string Staged, PublishedFile Final)> companions = [];
            foreach (string staged in Directory.EnumerateFiles(staging).Where(path => path != stagedArtifact).Order(StringComparer.Ordinal))
            {
                string finalPath = Path.Combine(directory, Path.GetFileName(staged));
                companions.Add((staged, Describe(staged, finalPath, CompanionKind(target, finalPath))));
            }

            string? manifestPath = null;
            if (options.WriteManifest)
            {
                manifestPath = ArtifactManifest.PathFor(target);
                string stagedManifest = Path.Combine(staging, Path.GetFileName(manifestPath));
                ArtifactManifest manifest = new(
                    ArtifactManifest.FormatName,
                    new ArtifactManifestFile(name, artifact.Kind, artifact.Bytes, artifact.Sha256),
                    [.. companions.Select(item => new ArtifactManifestFile(
                        Path.GetFileName(item.Final.Path), item.Final.Kind, item.Final.Bytes, item.Final.Sha256))],
                    plan.Family.ToString(),
                    plan.Filesystem.ToString(),
                    plan.DrawingRange.StartSector,
                    plan.DrawingRange.EndSectorExclusive,
                    _utcNow(),
                    ArtifactManifest.GeneratorName());
                File.WriteAllText(stagedManifest, manifest.ToJson());
                FlushToDisk(stagedManifest);
                companions.Add((stagedManifest, Describe(stagedManifest, manifestPath, "manifest")));
            }

            if (options.BeforeCommit is { } check)
            {
                Report(progress, plan, "generation.validate");
                check(
                    new StagedArtifactSet(
                        plan,
                        new StagedFile(stagedArtifact, artifact),
                        [.. companions.Select(item => new StagedFile(item.Staged, item.Final))]),
                    cancellationToken);
            }

            // 3–5. Swap in: nothing can be canceled from here on; the swap is a few renames.
            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, plan, "generation.publish");
            Commit(plan, target, directory, token, stagedArtifact, companions);
            return new PublishedArtifact(artifact, [.. companions.Select(item => item.Final)], manifestPath);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    private static void Commit(
        GenerationPlan plan,
        string target,
        string directory,
        string token,
        string stagedArtifact,
        List<(string Staged, PublishedFile Final)> companions)
    {
        // Everything the old artifact may have next to it, including companions the new plan
        // does not produce (a stale cue sheet would otherwise describe the new file).
        string[] existing = [.. new[] { target }
            .Concat(CompanionPaths(plan, target))
            .Concat(companions.Select(item => item.Final.Path))
            .Distinct(StringComparer.Ordinal)
            .Where(File.Exists)];
        List<(string Backup, string Original)> backups = [];
        List<string> placed = [];
        try
        {
            foreach (string path in existing)
            {
                string backup = Path.Combine(directory, $".{Path.GetFileName(path)}.{token}.backup");
                File.Move(path, backup);
                backups.Add((backup, path));
            }

            foreach ((string staged, PublishedFile final) in companions)
            {
                File.Move(staged, final.Path);
                placed.Add(final.Path);
            }

            File.Move(stagedArtifact, target);
            placed.Add(target);
        }
        catch
        {
            foreach (string path in placed)
            {
                TryDelete(path);
            }

            foreach ((string backup, string original) in backups)
            {
                try
                {
                    File.Move(backup, original, overwrite: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // The backup stays under its hidden name; nothing is lost, only misplaced.
                }
            }

            throw;
        }

        foreach ((string backup, _) in backups)
        {
            TryDelete(backup);
        }
    }

    /// <summary>The files publishing may write next to <paramref name="target"/>, from the plan.</summary>
    private static IEnumerable<string> CompanionPaths(GenerationPlan plan, string target)
    {
        foreach (GenerationSidecar sidecar in plan.Sidecars)
        {
            yield return CompanionPath(target, sidecar);
        }

        // A WAVE track may have been published with a cue sheet before; even when the new plan
        // writes none, the old sheet belongs to the replaced artifact and must go with it.
        if (Path.GetExtension(target).Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            yield return Path.ChangeExtension(target, ".cue");
        }

        yield return ArtifactManifest.PathFor(target);
    }

    // The metadata sidecar is appended to the artifact name; the cue sheet replaces its extension.
    private static string CompanionPath(string target, GenerationSidecar sidecar)
        => sidecar.Kind == "cue" ? Path.ChangeExtension(target, sidecar.Suffix) : target + sidecar.Suffix;

    private static string CompanionKind(string target, string path)
        => path.EndsWith(".cue", StringComparison.OrdinalIgnoreCase) && Path.ChangeExtension(target, ".cue") == path
            ? "cue"
            : "metadata";

    private static PublishedFile Describe(string stagedPath, string finalPath, string kind)
    {
        using FileStream stream = File.OpenRead(stagedPath);
        string hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        return new PublishedFile(finalPath, kind, stream.Length, hash);
    }

    private static void FlushToDisk(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Flush(flushToDisk: true);
    }

    private static void AddSpaceProblem(List<GenerationValidationError> problems, long required, long? available)
    {
        if (available is { } free && free < required)
        {
            problems.Add(Error(
                "generation.output.insufficient_space",
                "outputPath",
                ("requiredBytes", required),
                ("availableBytes", free)));
        }
    }

    private static long RequiredBytes(GenerationPlan plan) => checked(plan.EstimatedBytes + CompanionMarginBytes);

    private static string? NearestExistingDirectory(string? directory)
    {
        for (string? current = directory; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (Directory.Exists(current))
            {
                return current;
            }
        }

        return null;
    }

    private static bool CanWriteIn(string directory)
    {
        string probe = Path.Combine(directory, $".disc-image-studio-probe-{Guid.NewGuid():N}");
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            TryDelete(probe);
        }
    }

    private static long? AvailableBytes(string directory)
    {
        try
        {
            return new DriveInfo(directory).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static void Report(IProgress<GenerationProgress>? progress, GenerationPlan plan, string stage)
        => progress?.Report(new GenerationProgress(stage, plan.EstimatedBytes, plan.EstimatedBytes, TimeSpan.Zero, stage));

    private static GenerationValidationError Error(string code, string field, params (string Key, object? Value)[] parameters)
        => new(code, field, parameters.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal));

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover hidden file is harmless and never sits at a final path.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // As above: hidden, never at a final path.
        }
    }
}
