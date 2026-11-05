using System.IO;
using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;
using DiscImageStudio.Generation;
using DiscImageStudio.Localization;
using DiscImageStudio.ViewModels;

namespace DiscImageStudio.Services;

/// <summary>WPF form adapter: text buffers become typed generation requests here.</summary>
internal static class GenerationRequestFactory
{
    internal static GenerateDiscRequest CreateForProject(
        OpticalDiscFamily family,
        DiscParametersState state,
        string sourceImagePath,
        string? outputPath = null,
        bool directBurn = false)
        => family switch
        {
            OpticalDiscFamily.Dvd => CreateDvd(state, sourceImagePath),
            OpticalDiscFamily.BluRay => CreateBluRay(state, sourceImagePath),
            _ => CreateCd(state, sourceImagePath, outputPath, directBurn),
        };

    private static GenerateCdRequest CreateCd(
        DiscParametersState state,
        string sourceImagePath,
        string? outputPath,
        bool directBurn)
    {
        CdDiscParameters parameters = new(
            ParameterParser.ParsePositiveDouble(state.CdInnerRadius, L.T("CD 内半径")),
            ParameterParser.ParsePositiveDouble(state.CdOuterRadius, L.T("CD 外半径")),
            ParameterParser.ParsePositiveLong(state.CdSectors, L.T("CD 扇区数")));
        string? dataDirectory = OptionalDirectory(state.CdDataDirectory);
        CdArtifactFormat format = dataDirectory is not null
            ? CdArtifactFormat.Mode1Iso
            : directBurn
                ? CdArtifactFormat.RawAudio
                : string.Equals(
                    Path.GetExtension(outputPath),
                    ".wav",
                    StringComparison.OrdinalIgnoreCase)
                    ? CdArtifactFormat.WaveAudio
                    : CdArtifactFormat.RawAudio;
        return new GenerateCdRequest(
            sourceImagePath,
            parameters,
            state.Tone,
            format,
            state.CdInterleave,
            state.CdWriteCue,
            directBurn || format == CdArtifactFormat.WaveAudio
                ? CdAudioByteOrder.LittleEndian
                : CdAudioByteOrder.BigEndian,
            dataDirectory,
            string.IsNullOrWhiteSpace(state.CdVolumeLabel) ? "CD_IMAGE" : state.CdVolumeLabel.Trim(),
            state.CdFilesAtOuterRing ? FilePlacement.OuterRing : FilePlacement.InnerRing);
    }

    private static GenerateDvdRequest CreateDvd(
        DiscParametersState state,
        string sourceImagePath)
    {
        uint sectors = PositiveUInt32(state.DvdTotalSectors, L.T("DVD 总扇区数"));
        DvdStreamingOptions options = new(
            sectors,
            ParameterParser.ParsePositiveDouble(state.DvdInnerRadius, L.T("DVD 内半径")),
            ParameterParser.ParsePositiveDouble(state.DvdOuterRadius, L.T("DVD 外半径")),
            PitchLinear: ParameterParser.ParseDouble(state.DvdPitchLinear, L.T("DVD 相对一次项")),
            PitchQuadratic: ParameterParser.ParseDouble(state.DvdPitchQuadratic, L.T("DVD 相对二次项")),
            PitchCubic: ParameterParser.ParseDouble(state.DvdPitchCubic, L.T("DVD 相对三次项")),
            Tone: state.DvdTone);
        return new GenerateDvdRequest(
            sourceImagePath,
            options,
            OptionalDirectory(state.DvdDataDirectory),
            string.IsNullOrWhiteSpace(state.DvdVolumeLabel) ? "DISC_IMAGE" : state.DvdVolumeLabel.Trim(),
            state.DvdFilesAtOuterRing ? FilePlacement.OuterRing : FilePlacement.InnerRing);
    }

    private static GenerateBluRayRequest CreateBluRay(
        DiscParametersState state,
        string sourceImagePath)
    {
        BluRayDiscParameters parameters = new(
            PositiveUInt32(state.BdTotalSectors, L.T("蓝光总扇区数")),
            1,
            ParameterParser.ParsePositiveDouble(state.BdInnerRadius, L.T("蓝光内半径")),
            ParameterParser.ParsePositiveDouble(state.BdOuterRadius, L.T("蓝光外半径")));
        return new GenerateBluRayRequest(
            sourceImagePath,
            parameters,
            state.BluRayTone,
            state.BdInvertMapping,
            OptionalDirectory(state.BdDataDirectory),
            string.IsNullOrWhiteSpace(state.BdVolumeLabel) ? "BD_IMAGE" : state.BdVolumeLabel.Trim(),
            state.BdFilesAtOuterRing ? FilePlacement.OuterRing : FilePlacement.InnerRing);
    }

    private static uint PositiveUInt32(string value, string field)
    {
        long parsed = ParameterParser.ParsePositiveLong(value, field);
        return parsed <= uint.MaxValue
            ? (uint)parsed
            : throw new ArgumentOutOfRangeException(field, L.T("总扇区数超出引擎支持范围。"));
    }

    private static string? OptionalDirectory(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
