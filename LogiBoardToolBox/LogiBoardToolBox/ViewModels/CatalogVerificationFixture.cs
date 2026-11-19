using LogiBoardToolBox.Core.Devices;
using LogiBoardToolBox.Core.Lighting;
using LogiBoardToolBox.Core.Macros;
using LogiBoardToolBox.Core.Onboard;
using LogiBoardToolBox.Core.Windows;

namespace LogiBoardToolBox.ViewModels;

public partial class MainPageViewModel
{
    internal void LoadCatalogVerificationFixture(MouseModel model)
    {
        if (!Utilities.UiVerification.IsCatalogRequested) throw new InvalidOperationException("仅用于离线目录界面验收。");
        using var scope=PauseImmediateSave();
        SelectedDevice=new(new("catalog-preview",model.ProductIds[0],model.DisplayName,"PREVIEW",0xFF00,1,20,20),1,model.DisplayName,"PREVIEW",model.ProductIds);
        if(client is not null || memory is not null) throw new InvalidOperationException("离线验收不得持有物理设备句柄。");
        // Artwork-only models use an explicitly synthetic six-button fixture;
        // production views obtain the count from the mouse, not this preview.
        var format=(byte)(model.Layout=="G502X"?10:model.Id is "Pro2" or "Superlight2" or "Superlight2Dex"?7:3);var buttons=(byte)(model.Layout=="G502X"?11:model.ButtonNames.Length==0?6:model.ButtonNames.Length);
        var profiles=(byte)(model.Id=="G305"?1:5);
        var read=new MemorySnapshot{DeviceName=model.DisplayName,DeviceIdentity="catalog-preview:"+model.Id,Mode=1,ActiveSector=1,
            Description=[1,format,1,profiles,0,buttons,16,0,255,0x0A,4,0,0,0,0,0]};
        for(var i=0;i<16;i++)read.Sectors[i]=Enumerable.Repeat((byte)0xFF,255).ToArray();
        var macro=new MacroEditor{Name="板载取消刀",Script="key 0x31 down\ndelay 59\nkey 0x31 up\ndelay 198\nkey 0x1E down\ndelay 25\nkey 0x1E up\ndelay 10",SelectedRepeat=MacroEditor.RepeatChoices[1],UseStandardDelay=false};
        var bytecode=macro.Compile().Bytes;bytecode.CopyTo(read.Sectors[profiles+1],0);SectorChecksum.Seal(read.Sectors[profiles+1]);
        for(var i=1;i<=profiles;i++)
        {
            var data=read.Sectors[i]; data[0]=format==10?(byte)255:(byte)1;data[1]=1;data[2]=0;data[20]=3;data[21]=3;
            if(format==7){ data[0]=3;data[1]=3;data[2]=0;data[3]=0; }
            for(var d=0;d<5;d++)
            {System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(format==7?4+5*d:3+d*2),(ushort)(800*(d+1)));if(format==10)System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(86+d*2),(ushort)(800*(d+1)));if(format==7){System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6+5*d),(ushort)(800*(d+1)));data[8+5*d]=2;}}
            var bindingsOffset=format==7?48:32;
            for(var b=0;b<3;b++)ButtonBinding.Mouse(b+1).CopyTo(data,bindingsOffset+b*4);
            ButtonBinding.Mouse(4).CopyTo(data,bindingsOffset+12);ButtonBinding.Macro((ushort)(profiles+1)).CopyTo(data,bindingsOffset+16);
            for(var light=0;light<4;light++)OnboardLighting.Create(3,0,184,217,4000,100).CopyTo(data,208+light*11);
            SectorChecksum.Seal(data);read.Sectors[0][4*(i-1)]=0;read.Sectors[0][4*(i-1)+1]=(byte)i;read.Sectors[0][4*(i-1)+2]=1;
        }
        SectorChecksum.Seal(read.Sectors[0]);snapshot=read;snapshotDeviceIdentity=SelectedDevice.Identity;
        ConfigureLightingZones(Math.Max(1,model.LightingZoneCount));lightingCapabilities=new([],Enumerable.Range(1,model.LightingZoneCount).Select(i=>(byte)i).ToArray());
        Macros.Clear();Macros.Add(macro);SelectedMacro=macro;
        Profiles.Clear();foreach(var p in read.GetProfiles())Profiles.Add(p);SelectedProfile=Profiles[0];RebuildConfigurations();
        SelectedConfiguration=Configurations[0];DeviceSummary=read.Capabilities.Summary;
        ModeSummary="离线适配预览 · 模拟存储";StatusTitle="离线型号验收";Status="模拟配置与板载宏；没有连接或写入物理设备。";
        BatteryText="—";ChargingText="预览";FirmwareText="模拟设备";HasError=false;
        NotifyDeviceModel();UpdateCapabilities();
    }

    internal void LoadDiagnosticFailureFixture()
    {
        if (!Utilities.UiVerification.IsCatalogRequested || snapshot is null || client is not null || memory is not null)
            throw new InvalidOperationException("诊断验收仅使用隔离的模拟快照。");
        SelectedConfiguration = Configurations.Single(c => c.Onboard && c.Sector == SelectedProfile!.Sector);
        snapshot.Sectors[6][^1] ^= 1;
        Macros.Add(new() { Name = "测试", Script = "mouse 1 down\ndelay 59\nmouse 1 up\ndelay 198", UseStandardDelay = false });
        var row = StandardBindings[5]; row.SelectedAction = ButtonBindingEditor.ActionChoices.Single(c => c.Id == "macro");
        row.Value = "测试"; row.FriendlyName = "测试";
        PersistenceStatus = "保存失败 · 更改仍保留在编辑器中";
        try { _ = MacroAllocator.Allocate(snapshot, [new(row.Index, false, Macros[^1].Compile())]); }
        catch (MacroStorageException error) { ShowError(error); }
    }

    private Utilities.DiagnosticReplayTransport? verificationStorage;
    internal void LoadClassicG502DiagnosticSnapshot(MemorySnapshot captured)
    {
        var model = MouseCatalog.Resolve([], captured.DeviceName);
        if (!Utilities.UiVerification.IsCatalogRequested || model.Layout != "G502Classic" ||
            !captured.Capabilities.CanEditProfiles || captured.Capabilities.ButtonCount != 11 || captured.Mode != 1)
            throw new InvalidOperationException("需要经典 G502 的 11 键板载诊断快照。");
        LoadCatalogVerificationFixture(model);
        using var scope = PauseImmediateSave();
        snapshot = captured;
        Macros.Clear();
        SelectedMacro = null;
        ReloadDiagnosticProfiles(captured.ActiveSector);
        DeviceSummary = captured.Capabilities.Summary;
        NotifyDeviceModel();
        UpdateCapabilities();
    }

    internal void LoadAdvancedDpiDiagnosticSnapshot(MemorySnapshot captured, Core.HidPlusPlus.SensorPerformance capturedSensor, string modelId)
    {
        var model = MouseCatalog.ById(modelId);
        if (!Utilities.UiVerification.IsCatalogRequested || !captured.Capabilities.UsesAdvancedProfile || model?.DisplayName != captured.DeviceName)
            throw new InvalidOperationException("需要对应型号的格式 7 离线诊断快照。");
        LoadCatalogVerificationFixture(model);
        using var scope = PauseImmediateSave();
        snapshot = captured;
        snapshotDeviceIdentity = SelectedDevice!.Identity;
        Macros.Clear();
        SelectedMacro = null;
        LoadSensorPerformance(capturedSensor);
        ReloadDiagnosticProfiles(captured.Mode == 1 ? captured.ActiveSector : (ushort)1);
        DeviceSummary = captured.Capabilities.Summary;
        StatusTitle = "诊断数据离线回放";
        HasError = false;
        NotifyDeviceModel();
        UpdateCapabilities();
    }

    internal void LoadLegacyDiagnosticSnapshot(MemorySnapshot captured)
    {
        if (!Utilities.UiVerification.IsCatalogRequested || !captured.Capabilities.UsesLegacyMacroPages || client is not null || memory is not null)
            throw new InvalidOperationException("仅允许回放已核对的传统布局离线快照。");
        var g402 = captured.Capabilities.SectorSize == 1024;
        var lightspeed = captured.Capabilities.ProfileFormat == 3 && captured.Capabilities.SectorSize == 255;
        var g102 = captured.Capabilities.ProfileFormat == 4;
        var modelId = g402 ? "G402" : lightspeed ? "G502Wireless" : g102 ? "G203Lightsync" : "G502Hero";
        LoadCatalogVerificationFixture(MouseCatalog.ById(modelId)!);
        using var scope = PauseImmediateSave();
        snapshot = captured; snapshotDeviceIdentity = SelectedDevice!.Identity;
        Macros.Clear(); var macro = g402 ? new MacroEditor { Name = "跳标", Script = "key Space down\ndelay 40\nkey Space up\nkey Left down\ndelay 125\nkey Left up", SelectedRepeat = MacroEditor.RepeatChoices[1], UseStandardDelay = false }
            : lightspeed ? new MacroEditor { Name = "复制并粘贴", Script = "tap Ctrl+C\ndelay 80\ntap Ctrl+V", UseStandardDelay = false }
            : g102 ? new MacroEditor { Name = "滚轮上滚", Script = "wheel 1", UseStandardDelay = false }
            : new MacroEditor { Name = "测试", Script = "key D down\nkey D up", UseStandardDelay = true, StandardDelay = "50" };
        Macros.Add(macro); SelectedMacro = macro;
        refreshingProfiles = true; drafts.Clear();
        try
        {
            Profiles.Clear(); foreach (var profile in captured.GetProfiles()) Profiles.Add(profile);
            SelectedProfile = Profiles.Single(p => p.Sector == 1);
        }
        finally { refreshingProfiles = false; }
        RebuildConfigurations();
        if (g402)
        {
            var ranges = Core.HidPlusPlus.PerformanceController.DecodeRanges(Convert.FromHexString("00FCE0540FC00000"));
            sensor = new(false, 0, ranges, ranges, 3192, 3192, 0, 0, 0, [125, 250, 500, 1000], [], [125, 250, 500, 1000], 1000, 0);
        }
        DeviceSummary = captured.Capabilities.Summary; StatusTitle = "诊断数据离线回放";
        HasError = false; NotifyDeviceModel(); UpdateCapabilities();
    }

    internal void LoadBindingLabelsVerificationFixture()
    {
        LoadCatalogVerificationFixture(MouseCatalog.ById("G502Hero")!);
        using var scope = PauseImmediateSave();
        for (var index = 3; index <= 8; index++)
        {
            var row = StandardBindings[index]; row.SelectedAction = ButtonBindingEditor.ActionChoices[0]; row.Value = "";
            row.RefreshOriginal(index == 3 ? ButtonBinding.Consumer(0xEA) : index == 4 ? ButtonBinding.Consumer(0xE9) : [0x90, 0, 0xFF, 0xFF]);
        }
    }

    internal void LoadSaveRecoveryVerificationFixture(MemorySnapshot captured, MouseModel model)
    {
        LoadCatalogVerificationFixture(model);
        using var scope = PauseImmediateSave();
        SelectedDevice = SelectedDevice! with { Name = captured.DeviceName, DeviceIndex = model.Id == "G502X" ? (byte)0xFF : (byte)1 };
        snapshot = captured;
        snapshotDeviceIdentity = SelectedDevice!.Identity;
        Macros.Clear();
        SelectedMacro = null;
        ReloadDiagnosticProfiles(captured.ActiveSector);
        DeviceSummary = captured.Capabilities.Summary;
        NotifyDeviceModel();
        UpdateCapabilities();
    }

    internal async Task ReadDiagnosticReplayStorageAsync()
    {
        if (!Utilities.UiVerification.IsCatalogRequested || verificationStorage is null)
            throw new InvalidOperationException("仅用于板载存储离线回放。");
        await RefreshSnapshotKeepingEditsAsync();
    }

    internal Utilities.DiagnosticReplayTransport AttachDiagnosticReplay(byte onboardFeature = 2, byte dpiFeature = 0,
        IReadOnlyDictionary<ushort, byte[]>? rgbInfo = null, TimeSpan? timeout = null)
    {
        if (!Utilities.UiVerification.IsCatalogRequested || snapshot is null || client is not null || memory is not null)
            throw new InvalidOperationException("仅允许连接隔离的内存回放设备。");
        verificationStorage = new(snapshot, onboardFeature, dpiFeature, rgbInfo);
        var connection = new Core.HidPlusPlus.HidPlusPlusClient(verificationStorage, SelectedDevice?.DeviceIndex ?? 1);
        if (timeout is { } duration) connection.Timeout = duration;
        SetDeviceClient(connection);
        memory = new(connection, snapshot.DeviceIdentity, snapshot.DeviceName);
        SelectedConfiguration = Configurations.Single(c => c.Onboard && c.Sector == SelectedProfile!.Sector);
        return verificationStorage;
    }

    internal async Task<Utilities.DiagnosticReplayTransport> AttachDpiDiagnosticReplayAsync()
    {
        var replay = AttachDiagnosticReplay(0x0D, 0x09);
        await client!.GetFeatureAsync(0x8100);
        await client.GetFeatureAsync(0x2202);
        return replay;
    }
    internal async Task<Utilities.DiagnosticReplayTransport> AttachLightingDiagnosticReplayAsync(IReadOnlyDictionary<ushort, byte[]> rgbInfo)
    {
        var replay = AttachDiagnosticReplay(rgbInfo: rgbInfo);
        await ReadLightingCapabilitiesAsync();
        UpdateCapabilities();
        return replay;
    }
    internal void DetachDiagnosticReplay()
    {
        if (verificationStorage is null) return;
        SetDeviceClient(null); memory = null; verificationStorage = null; refreshBeforeRetry = false;
    }

    internal void LoadToggleDiagnosticSnapshot(MemorySnapshot captured)
    {
        LoadLegacyDiagnosticSnapshot(captured);
        using var scope = PauseImmediateSave();
        Macros.Clear();
        foreach (var name in new[] { "复制并粘贴", "左键速点" })
            Macros.Add(new MacroEditor { Name = name, Script = "mouse 1 down\ndelay 200\nmouse 1 up\ndelay 200", SelectedRepeat = MacroEditor.RepeatChoices[1] });
        SelectedMacro = Macros[1];
        ReloadDiagnosticProfiles();
    }

    internal async Task ReloadMacroLinksForVerificationAsync()
    {
        if (!Utilities.UiVerification.IsCatalogRequested || verificationStorage is null || memory is null)
            throw new InvalidOperationException("仅允许离线回放。");
        using var scope = PauseImmediateSave();
        snapshot = await memory.ReadAsync();
        await LoadOnboardMacroLinksAsync();
        ReloadDiagnosticProfiles();
    }

    private void ReloadDiagnosticProfiles(ushort selectedSector = 1)
    {
        using var scope = PauseImmediateSave();
        refreshingProfiles = true; drafts.Clear();
        try
        {
            Profiles.Clear(); foreach (var profile in snapshot!.GetProfiles()) Profiles.Add(profile);
            SelectedProfile = Profiles.FirstOrDefault(p => p.Sector == selectedSector) ?? Profiles[0];
        }
        finally { refreshingProfiles = false; }
        RebuildConfigurations();
    }
}
