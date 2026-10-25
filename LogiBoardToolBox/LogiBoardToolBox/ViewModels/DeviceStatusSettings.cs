using CommunityToolkit.Mvvm.ComponentModel;
using LogiBoardToolBox.Core.HidPlusPlus;

namespace LogiBoardToolBox.ViewModels;

public partial class MainPageViewModel
{
    [ObservableProperty] public partial string BatteryText { get; set; } = "—";
    [ObservableProperty] public partial string ChargingText { get; set; } = "等待读取";
    [ObservableProperty] public partial string FirmwareText { get; set; } = "等待读取";
    private async Task ReadDeviceStatusAsync()
    {
        try
        {
            var status = await DeviceStatusReader.ReadAsync(client!);
            BatteryText = status.Battery is int percent ? percent + "%" : "未提供";
            ChargingText = status.Charging switch { 0 => "电池供电", 1 => "正在充电", 2 => "缓慢充电", 3 => "已充满", 4 => "充电异常", _ => "" };
            FirmwareText = string.IsNullOrEmpty(status.Firmware) ? "设备未提供版本" : SelectedDevice?.Name + " : " + status.Firmware;
        }
        catch (Exception ex) { Log("读取设备状态：" + ex.Message); BatteryText = "—"; ChargingText = "状态暂不可用"; FirmwareText = "状态暂不可用"; }
    }
}
