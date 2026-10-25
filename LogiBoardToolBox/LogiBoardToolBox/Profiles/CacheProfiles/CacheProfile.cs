using XFEExtension.NetCore.AutoConfig;
using XFEExtension.NetCore.WinUIHelper.Utilities.Helper;

namespace LogiBoardToolBox.Profiles.CacheProfiles
{
    public partial class CacheProfile : XFEProfile
    {
        public CacheProfile() => ProfilePath = $@"{AppPathHelper.CacheProfile}\{nameof(CacheProfile)}";

        /// <summary>
        /// 你的缓存内容
        /// </summary>
        [ProfileProperty]
        public static partial string CacheContent { get; set; } = string.Empty;
    }
}
