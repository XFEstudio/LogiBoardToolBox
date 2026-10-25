using LogiBoardToolBox.Core.Devices;
using LogiBoardToolBox.Core.Lighting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace LogiBoardToolBox.ViewModels;

public partial class MainPageViewModel
{
    private LightsyncCapabilities? lightingCapabilities;
    public MouseModel DeviceModel => SelectedDevice?.Model ?? MouseCatalog.Unknown;
    public string DeviceImageAsset => DeviceModel.ImageAsset;
    private MouseModel? imageModel;
    private ImageSource? modelImage;
    public ImageSource? DeviceImageSource
    {
        get
        {
            var model = DeviceModel;
            if (!model.IsKnown) return null;
            if (!ReferenceEquals(imageModel, model))
            { modelImage = new BitmapImage(new Uri("ms-appx:///" + model.ImageAsset.TrimStart('/'))); imageModel = model; }
            return modelImage;
        }
    }
    public bool HasModelSideView => DeviceModel.HasSideView;
    public bool HasRgbLighting => lightingCapabilities is { } lighting ? lighting.ZoneCount > 0 : DeviceModel.HasLighting;
    public string ModelSupportNote => !DeviceModel.IsKnown ? "按设备能力读取；型号专属图与 G HUB 槽位尚未适配。"
        : DeviceModel.Id == "G502XPlus" ? "G502 X PLUS 板载宏已在本机实测。"
        : "已按型号适配；此型号的板载执行仍需对应实机验证。";
    public IEnumerable<EditorChoice> AvailableLightsyncEffects => LightsyncEffects.Where(e => e.Id != "Signature" || DeviceModel.Layout == "G502X");
    private void NotifyDeviceModel()
    {
        foreach (var property in new[] { nameof(DeviceModel), nameof(DeviceImageAsset), nameof(DeviceImageSource), nameof(HasModelSideView), nameof(HasRgbLighting), nameof(ModelSupportNote), nameof(AvailableLightsyncEffects) }) OnPropertyChanged(property);
        OnPropertyChanged(nameof(LedZones));
        RefreshLightsyncBindings();
    }
    public void RefreshLightsyncBindings()
    {
        foreach (var name in new[] { nameof(SelectedLightsyncEffect), nameof(SelectedLightsyncMode), nameof(LightsyncPeriod), nameof(LightsyncBrightness), nameof(AudioGain), nameof(LightsyncColor) }) OnPropertyChanged(name);
    }
    public async Task SelectDeviceAsync(Core.Windows.LogitechDevice device)
    {
        if (SelectedDevice?.Identity == device.Identity || !CanChangeDevice) return;
        try { await FlushImmediateSaveAsync(); }
        catch (Exception error) { ShowError(error); return; }
        await RunAsync(async () => { SelectedDevice = device; await ReadCoreAsync(); });
    }
    private async Task ReadLightingCapabilitiesAsync()
    {
        lightingCapabilities = null;
        try { lightingCapabilities = await new LightsyncDevice(client!).ReadAsync(); }
        catch (Exception error) { Log("灯区能力读取失败：" + error.Message); }
        ConfigureLightingZones(Math.Max(1, lightingCapabilities?.ZoneCount ?? DeviceModel.LightingZoneCount));
        if (DeviceModel.Layout != "G502X" && SelectedLightsyncEffect.Id == "Signature") SelectedLightsyncEffect = LightsyncEffects.First(e => e.Id == "Cycle");
        NotifyDeviceModel();
    }
}
