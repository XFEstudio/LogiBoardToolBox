using XFEExtension.NetCore.AutoConfig;
using XFEExtension.NetCore.WinUIHelper.Utilities.Helper;

namespace LogiBoardToolBox.Profiles.CrossVersionProfiles
{
    public partial class SystemProfile : XFEProfile
    {
        public SystemProfile() => ProfilePath = $@"{AppPathHelper.LocalProfile}\{nameof(SystemProfile)}";

        /// <summary>
        /// 主题颜色
        /// </summary>
        [ProfileProperty]
        public static partial ElementTheme Theme { get; set; } = ElementTheme.Default;
        /// <summary>
        /// 是否自动启动
        /// </summary>
        [ProfileProperty]
        public static partial bool AutoStart { get; set; }

        static partial void SetThemeProperty(ref ElementTheme value) => AppThemeHelper.ChangeTheme(value);
    }
}
