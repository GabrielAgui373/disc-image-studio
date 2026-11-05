using System.Security.Cryptography;
using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;
using DiscImageStudio.Generation;
using DiscImageStudio.ImageIO;
using Xunit;

namespace DiscImageStudio.PortableArchitectureTests;

public sealed class TypedGenerationPipelineTests
{
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public async Task Golden_vectors_are_identical_for_file_and_stream_destinations()
    {
        using Fixture fixture = new();
        DiscGenerationService service = new(() => FixedTimestamp);
        IReadOnlyList<Vector> vectors = fixture.CreateVectors();
        List<string> mismatches = [];

        foreach (Vector vector in vectors)
        {
            GenerationPlan plan = await service.PlanAsync(vector.Request);
            using MemoryStream stream = new();
            await service.GenerateStreamAsync(plan, stream);

            string outputPath = Path.Combine(fixture.Root, vector.Name + vector.Extension);
            await service.GenerateFileAsync(plan, outputPath);
            byte[] fileBytes = await File.ReadAllBytesAsync(outputPath);
            byte[] streamBytes = stream.ToArray();
            string hash = Convert.ToHexString(SHA256.HashData(streamBytes)).ToLowerInvariant();

            Assert.Equal(plan.EstimatedBytes, streamBytes.LongLength);
            Assert.Equal(streamBytes, fileBytes);
            if (!hash.Equals(vector.Sha256, StringComparison.Ordinal))
            {
                mismatches.Add($"{vector.Name}={hash}");
            }
        }

        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
    }

    [Fact]
    public async Task Plans_expose_real_filesystems_ranges_sizes_and_capabilities()
    {
        using Fixture fixture = new();
        DiscGenerationService service = new(() => FixedTimestamp);
        Dictionary<string, GenerationPlan> plans = new(StringComparer.Ordinal);
        foreach (Vector vector in fixture.CreateVectors())
        {
            plans[vector.Name] = await service.PlanAsync(vector.Request);
        }

        Assert.Equal(GenerationFilesystem.CdDa, plans["cd-audio"].Filesystem);
        Assert.Equal(GenerationFilesystem.Iso9660Joliet, plans["cd-data"].Filesystem);
        Assert.Equal(GenerationFilesystem.None, plans["dvd-drawing"].Filesystem);
        Assert.Equal(GenerationFilesystem.None, plans["bluray-drawing"].Filesystem);
        Assert.Equal(GenerationFilesystem.Udf201, plans["bluray-udf"].Filesystem);
        Assert.Contains("ISO9660/Joliet", plans["cd-data"].Summary, StringComparison.Ordinal);
        Assert.Contains("UDF 2.01", plans["bluray-udf"].Summary, StringComparison.Ordinal);

        foreach (GenerationPlan plan in plans.Values)
        {
            Assert.True(plan.DrawingRange.SectorCount > 0);
            Assert.True(plan.EstimatedBytes > 0);
            Assert.True(plan.RequiredCapabilities.HasFlag(GenerationCapability.SequentialStream));
            Assert.Contains(plan.Sidecars, sidecar => sidecar.Kind == "metadata" && sidecar.Required);
        }

        Assert.True(plans["cd-data"].RequiredCapabilities.HasFlag(GenerationCapability.Filesystem));
        Assert.True(plans["bluray-udf"].RequiredCapabilities.HasFlag(GenerationCapability.Filesystem));
        Assert.Equal(1, plans["cd-data"].FileCount);
        Assert.Equal(1, plans["bluray-udf"].FileCount);
    }

    [Fact]
    public void Cli_adapter_produces_the_same_typed_request_as_direct_construction()
    {
        string[] arguments =
        [
            "cd-generate",
            "--input", "/tmp/source.png",
            "--output", "/tmp/output.raw",
            "--r0", "24.5",
            "--r1", "56.8",
            "--sectors", "2",
            "--interleave", "false",
            "--cue", "true",
            "--tone-dither", "0",
            "--tone-levels", "4",
            "--tone-brightness", "0",
            "--tone-gamma", "false",
        ];
        GenerateCdRequest expected = new(
            "/tmp/source.png",
            new CdDiscParameters(24.5, 56.8, 2),
            new ToneProfile(0, 4, 0, false),
            CdArtifactFormat.RawAudio,
            Interleave: false,
            WriteCue: true,
            AudioByteOrder: CdAudioByteOrder.BigEndian);

        Assert.True(TypedGenerationCommandLine.TryParse(arguments, out TypedGenerationCommand? command));
        Assert.Equal(expected, Assert.IsType<GenerateCdRequest>(command!.Request));
        Assert.Equal("/tmp/output.raw", command.OutputPath);
    }

    [Fact]
    public async Task Cli_request_and_direct_api_generate_identical_artifacts()
    {
        using Fixture fixture = new();
        DiscGenerationService service = new(() => FixedTimestamp);
        GenerateCdRequest direct = new(
            fixture.ImagePath,
            new CdDiscParameters(24.5, 56.8, 2),
            ToneProfile.LegacyBinary,
            CdArtifactFormat.RawAudio,
            Interleave: true,
            WriteCue: false,
            AudioByteOrder: CdAudioByteOrder.BigEndian);
        Assert.True(TypedGenerationCommandLine.TryParse(
            [
                "cd-generate",
                "--input", fixture.ImagePath,
                "--output", Path.Combine(fixture.Root, "cli.raw"),
                "--r0", "24.5",
                "--r1", "56.8",
                "--sectors", "2",
                "--interleave", "true",
                "--cue", "false",
            ],
            out TypedGenerationCommand? parsed));

        using MemoryStream directBytes = new();
        using MemoryStream cliBytes = new();
        await service.GenerateStreamAsync(await service.PlanAsync(direct), directBytes);
        await service.GenerateStreamAsync(await service.PlanAsync(parsed!.Request), cliBytes);

        Assert.Equal(directBytes.ToArray(), cliBytes.ToArray());
    }

    [Fact]
    public async Task Validation_errors_have_stable_codes_fields_and_parameters()
    {
        using Fixture fixture = new();
        DiscGenerationService service = new(() => FixedTimestamp);

        GenerationValidationException missing = await Assert.ThrowsAsync<GenerationValidationException>(
            () => service.PlanAsync(new GenerateCdRequest(
                Path.Combine(fixture.Root, "missing.png"),
                new CdDiscParameters(24.5, 56.8, 2),
                ToneProfile.LegacyBinary)));
        Assert.Equal("generation.source.not_found", missing.Error.Code);
        Assert.Equal("sourceImagePath", missing.Error.Field);
        Assert.True(missing.Error.Parameters.ContainsKey("path"));

        GenerationValidationException geometry = await Assert.ThrowsAsync<GenerationValidationException>(
            () => service.PlanAsync(new GenerateCdRequest(
                fixture.ImagePath,
                new CdDiscParameters(56.8, 24.5, 2),
                ToneProfile.LegacyBinary)));
        Assert.Equal("generation.plan.invalid", geometry.Error.Code);
        Assert.Equal("request", geometry.Error.Field);
        Assert.True(geometry.Error.Parameters.ContainsKey("reason"));

        GenerationValidationException placement = await Assert.ThrowsAsync<GenerationValidationException>(
            () => service.PlanAsync(new GenerateBluRayRequest(
                fixture.ImagePath,
                new BluRayDiscParameters(32),
                ToneProfile.LegacyBinary,
                FilePlacement: FilePlacement.OuterRing)));
        Assert.Equal("generation.files.outer_requires_directory", placement.Error.Code);

        GenerationValidationException capacity = await Assert.ThrowsAsync<GenerationValidationException>(
            () => service.PlanAsync(new GenerateCdRequest(
                fixture.ImagePath,
                new CdDiscParameters(24.5, 56.8, 20),
                ToneProfile.LegacyBinary,
                CdArtifactFormat.Mode1Iso,
                DataDirectory: fixture.DataDirectory)));
        Assert.Equal("generation.plan.invalid", capacity.Error.Code);

        GenerationPlan dataPlan = await service.PlanAsync(fixture.CreateVectors()[1].Request);
        GenerationValidationException output = await Assert.ThrowsAsync<GenerationValidationException>(
            () => service.GenerateFileAsync(
                dataPlan,
                Path.Combine(fixture.DataDirectory, "nested.iso")));
        Assert.Equal("generation.output.inside_source_directory", output.Error.Code);
    }

    [Fact]
    public async Task Progress_is_monotonic_and_cancellation_reaches_planning_and_writing()
    {
        using Fixture fixture = new();
        DiscGenerationService service = new(() => FixedTimestamp);
        GenerateCdRequest request = Assert.IsType<GenerateCdRequest>(fixture.CreateVectors()[0].Request);
        GenerationPlan plan = await service.PlanAsync(request);
        List<GenerationProgress> updates = [];
        using MemoryStream destination = new();
        await service.GenerateStreamAsync(plan, destination, new InlineProgress<GenerationProgress>(updates.Add));

        Assert.NotEmpty(updates);
        Assert.Equal(plan.EstimatedBytes, updates[^1].CompletedBytes);
        Assert.All(updates, update => Assert.Equal(plan.EstimatedBytes, update.TotalBytes));
        for (int index = 1; index < updates.Count; index++)
        {
            Assert.True(updates[index].CompletedBytes >= updates[index - 1].CompletedBytes);
        }

        using CancellationTokenSource planningCancellation = new();
        planningCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.PlanAsync(request, planningCancellation.Token));

        using CancellationTokenSource writingCancellation = new();
        using MemoryStream canceledOutput = new();
        InlineProgress<GenerationProgress> cancelAfterProgress = new(update =>
        {
            if (update.CompletedBytes > 0)
            {
                writingCancellation.Cancel();
            }
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GenerateStreamAsync(
            plan,
            canceledOutput,
            cancelAfterProgress,
            writingCancellation.Token));
        Assert.True(canceledOutput.Length < plan.EstimatedBytes);
    }

    [Fact]
    public void Cli_adapter_rejects_unknown_and_incompatible_options_with_stable_codes()
    {
        GenerationValidationException unknown = Assert.Throws<GenerationValidationException>(() =>
            TypedGenerationCommandLine.TryParse(
                ["cd-generate", "--input", "in.png", "--output", "out.raw", "--mystery", "1"],
                out _));
        Assert.Equal("generation.cli.unknown_option", unknown.Error.Code);
        Assert.Equal("mystery", unknown.Error.Field);

        GenerationValidationException incompatible = Assert.Throws<GenerationValidationException>(() =>
            TypedGenerationCommandLine.TryParse(
                [
                    "solve", "--image", "in.png", "--iso-output", "out.iso",
                    "--total-sectors", "16", "--algorithm", "legacy",
                ],
                out _));
        Assert.Equal("generation.cli.fixed_option", incompatible.Error.Code);
        Assert.Equal("algorithm", incompatible.Error.Field);

        GenerationValidationException preset = Assert.Throws<GenerationValidationException>(() =>
            TypedGenerationCommandLine.TryParse(
                [
                    "bd-generate", "--input", "in.png", "--output", "out.iso",
                    "--preset", "unknown-preset",
                ],
                out _));
        Assert.Equal("generation.cli.invalid_choice", preset.Error.Code);
        Assert.Equal("preset", preset.Error.Field);

        Assert.Contains("ISO9660/Joliet", TypedGenerationCommandLine.HelpText, StringComparison.Ordinal);
        Assert.Contains("CD-DA", TypedGenerationCommandLine.HelpText, StringComparison.Ordinal);
        Assert.Contains("UDF 2.01", TypedGenerationCommandLine.HelpText, StringComparison.Ordinal);
    }

    private sealed record Vector(
        string Name,
        string Extension,
        GenerateDiscRequest Request,
        string Sha256);

    private sealed class Fixture : IDisposable
    {
        internal Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"disc-generation-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            ImagePath = Path.Combine(Root, "source.png");
            RasterCodec.SavePng(
                ImagePath,
                2,
                2,
                [
                    0, 0, 0, 255,
                    255, 255, 255, 255,
                    64, 128, 192, 255,
                    200, 100, 25, 128,
                ],
                grayscale: false);
            DataDirectory = Path.Combine(Root, "payload");
            Directory.CreateDirectory(DataDirectory);
            string payloadPath = Path.Combine(DataDirectory, "payload.bin");
            File.WriteAllBytes(payloadPath, [1, 3, 5, 7, 9]);
            File.SetLastWriteTimeUtc(payloadPath, FixedTimestamp.UtcDateTime);
            Directory.SetLastWriteTimeUtc(DataDirectory, FixedTimestamp.UtcDateTime);
        }

        internal string Root { get; }

        internal string ImagePath { get; }

        internal string DataDirectory { get; }

        internal IReadOnlyList<Vector> CreateVectors()
            =>
            [
                new(
                    "cd-audio",
                    ".raw",
                    new GenerateCdRequest(
                        ImagePath,
                        new CdDiscParameters(24.5, 56.8, 2),
                        ToneProfile.LegacyBinary,
                        CdArtifactFormat.RawAudio,
                        Interleave: true,
                        WriteCue: false,
                        AudioByteOrder: CdAudioByteOrder.BigEndian),
                    "653507324205c52e431be26cef69d25a9a0488298b9c3b2fb79601bd7c668aac"),
                new(
                    "cd-data",
                    ".iso",
                    new GenerateCdRequest(
                        ImagePath,
                        new CdDiscParameters(24.5, 56.8, 128),
                        ToneProfile.LegacyBinary,
                        CdArtifactFormat.Mode1Iso,
                        DataDirectory: DataDirectory,
                        VolumeLabel: "CD_GOLDEN"),
                    "4c7e29860c368c7705d1e05072fc9f855610b9f8e62e5ee76ba5d56962f41f95"),
                new(
                    "dvd-drawing",
                    ".iso",
                    new GenerateDvdRequest(
                        ImagePath,
                        new DvdStreamingOptions(
                            16,
                            24,
                            58,
                            FastParallelism: 1,
                            Tone: ToneProfile.LegacyBinary)),
                    "ca7df4096c069023a859be03be23ac92028980d60a9f96c562a112c0dedbe28b"),
                new(
                    "bluray-drawing",
                    ".iso",
                    new GenerateBluRayRequest(
                        ImagePath,
                        new BluRayDiscParameters(32),
                        ToneProfile.LegacyBinary),
                    "d5f2b3c0a1ec4af9ede4639926bde7f2436a4bdb7d07006aa962b1e6ce338425"),
                new(
                    "bluray-udf",
                    ".iso",
                    new GenerateBluRayRequest(
                        ImagePath,
                        new BluRayDiscParameters(1024),
                        ToneProfile.LegacyBinary,
                        DataDirectory: DataDirectory,
                        VolumeLabel: "BD_GOLDEN"),
                    "f742f7e007aedff2d9370d3e7712359659f1f76a75567e9bed4c59be84f2f45b"),
            ];

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
