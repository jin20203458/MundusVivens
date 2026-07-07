using MundusVivens.Prototype.Protos;

namespace MundusVivens.Prototype.Models
{
    public static class EmotionMapper
    {
        public static ProtoEmotionCategory Map(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return ProtoEmotionCategory.EmotionCategoryUnspecified;

            if (raw.Contains("분노") || raw.Contains("화남") || raw.Contains("짜증") || raw.Contains("불쾌"))
                return ProtoEmotionCategory.EmotionCategoryAnger;
            if (raw.Contains("적대") || raw.Contains("경멸") || raw.Contains("미움") || raw.Contains("싫어"))
                return ProtoEmotionCategory.EmotionCategoryHostility;
            if (raw.Contains("공포") || raw.Contains("두려") || raw.Contains("불안") || raw.Contains("경계") || raw.Contains("무서"))
                return ProtoEmotionCategory.EmotionCategoryFear;

            return ProtoEmotionCategory.EmotionCategoryNeutral;
        }
    }
}
