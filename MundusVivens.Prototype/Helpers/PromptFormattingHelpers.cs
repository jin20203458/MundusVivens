using MundusVivens.Prototype.Models;

namespace MundusVivens.Prototype.Helpers;

public static class PromptFormattingHelpers
{
    public static string GetConfidenceLabel(double confidence)
    {
        return confidence switch
        {
            >= 0.9 => "확실한 사실로 확신함",
            >= 0.7 => "상당히 신뢰하고 있음",
            >= 0.4 => "소문으로 들어 긴가민가함",
            _      => "매우 미심쩍어하며 의심함"
        };
    }

    public static string GetLikingLabel(int liking)
    {
        return liking switch
        {
            >= 60  => "매우 친밀함",
            >= 20  => "우호적임",
            >= -20 => "중립적임",
            >= -60 => "경계하고 비호감임",
            _      => "극도로 적대적임"
        };
    }

    public static string GetTrustLabel(int trust)
    {
        return trust switch
        {
            >= 80 => "높은 신뢰",
            >= 40 => "보통 신뢰",
            _     => "낮은 신뢰"
        };
    }

    public static string GetImportanceLabel(double importance)
    {
        return importance switch
        {
            >= 0.8 => "가장 아끼는 핵심 기억",
            >= 0.5 => "인상 깊은 중요 기억",
            _      => "일반적인 기억"
        };
    }
}
