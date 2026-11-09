using DiscImageStudio.Core.Calibration;

namespace DiscImageStudio.Application.Calibration;

/// <summary>
/// The Core fitters report warnings and failures as fixed Chinese sentences. This maps each to a
/// stable code the presentation layer localizes; an unknown sentence maps to <c>other</c> and the
/// caller can still show the original text. The table follows CalibrationCrossFitter and
/// CalibrationCubicFitter; a new message there needs a row here.
/// </summary>
public static class CalibrationFitMessages
{
    private static readonly (string Fragment, string Code)[] Table =
    [
        ("至少标出", "tooFewPoints"),
        ("覆盖范围太窄", "narrowCoverage"),
        ("范围偏窄", "narrowBand"),
        ("向内回退", "turnsBack"),
        ("无法判断是否漏掉整圈", "halfTurnGap"),
        ("绕圈判断可能不可靠", "halfTurnRisk"),
        ("曲线点较多", "thinned"),
        ("没有能覆盖全部观测点", "noValidModel"),
        ("超出原图案环带", "beyondBand"),
        ("曲线偏差较大", "largeResidual"),
        ("轨距系数接近搜索边界", "pitchBoundary"),
        ("靠近搜索边界", "searchBoundary"),
        ("两个半径的约束较相似", "similarConstraints"),
        ("形状接近的参数候选", "nearCandidates"),
        ("有限多起点", "limitedSearch"),
        ("没有遗漏整圈为前提", "unwrapAssumption"),
        ("需要至少 12 个不同半径", "pitchNeedsPoints"),
        ("已拟合 DVD 三次轨距", "pitchFitted"),
        ("没有带来稳定改善", "pitchNoGain"),
        ("无法稳定区分三个轨距系数", "pitchUnstable"),
        ("转角过大", "turnTooLarge"),
    ];

    /// <summary>The stable code of a fitter message (<c>other</c> when unknown).</summary>
    public static string Code(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        foreach ((string fragment, string code) in Table)
        {
            if (message.Contains(fragment, StringComparison.Ordinal))
            {
                return code;
            }
        }

        return "other";
    }

    /// <summary>Codes of all warnings of a result, in order, without duplicates.</summary>
    public static IReadOnlyList<string> Codes(CalibrationFitResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return [.. result.Warnings.Select(Code).Distinct()];
    }
}
