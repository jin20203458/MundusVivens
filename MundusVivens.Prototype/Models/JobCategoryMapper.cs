using MundusVivens.Prototype.Protos;

namespace MundusVivens.Prototype.Models
{
    public static class JobCategoryMapper
    {
        public static ProtoJobCategory Map(string intent)
        {
            if (string.IsNullOrWhiteSpace(intent))
                return ProtoJobCategory.JobCategoryUnspecified;

            var s = intent.ToLower();
            if (s.Contains("취침") || s.Contains("수면") || s.Contains("휴식") || s.Contains("잠") || s.Contains("sleep") || s.Contains("rest"))
                return ProtoJobCategory.JobCategorySleep;
            if (s.Contains("식사") || s.Contains("밥") || s.Contains("음식") || s.Contains("먹") || s.Contains("eat") || s.Contains("drink"))
                return ProtoJobCategory.JobCategoryEat;
            if (s.Contains("대화") || s.Contains("사교") || s.Contains("만나") || s.Contains("social") || s.Contains("talk"))
                return ProtoJobCategory.JobCategorySocial;
            if (s.Contains("이동") || s.Contains("원정") || s.Contains("여행") || s.Contains("travel") || s.Contains("move"))
                return ProtoJobCategory.JobCategoryTravel;
            if (s.Contains("survival") || s.Contains("생존"))
                return ProtoJobCategory.JobCategorySurvival;

            return ProtoJobCategory.JobCategoryWork;
        }
    }
}
