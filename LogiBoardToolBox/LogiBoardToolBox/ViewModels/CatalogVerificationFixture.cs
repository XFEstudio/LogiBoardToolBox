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
        var format=(byte)(model.Layout=="G502X"?10:3);var buttons=(byte)(model.Layout=="G502X"?11:model.ButtonNames.Length);
        var profiles=(byte)(model.Id=="G305"?1:5);
        var read=new MemorySnapshot{DeviceName=model.DisplayName,DeviceIdentity="catalog-preview:"+model.Id,Mode=1,ActiveSector=1,
            Description=[1,format,1,profiles,0,buttons,16,0,255,0x0A,4,0,0,0,0,0]};
        for(var i=0;i<16;i++)read.Sectors[i]=Enumerable.Repeat((byte)0xFF,255).ToArray();
        var macro=new MacroEditor{Name="板载取消刀",Script="key 0x31 down\ndelay 59\nkey 0x31 up\ndelay 198\nkey 0x1E down\ndelay 25\nkey 0x1E up\ndelay 10",SelectedRepeat=MacroEditor.RepeatChoices[1],UseStandardDelay=false};
        var bytecode=macro.Compile().Bytes;bytecode.CopyTo(read.Sectors[profiles+1],0);SectorChecksum.Seal(read.Sectors[profiles+1]);
        for(var i=1;i<=profiles;i++)
        {
            var data=read.Sectors[i]; data[0]=format==10?(byte)255:(byte)1;data[1]=1;data[2]=0;data[20]=3;data[21]=3;
            for(var d=0;d<5;d++)
            {System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(3+d*2),(ushort)(800*(d+1)));if(format==10)System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(86+d*2),(ushort)(800*(d+1)));}
            for(var b=0;b<3;b++)ButtonBinding.Mouse(b+1).CopyTo(data,32+b*4);
            ButtonBinding.Mouse(4).CopyTo(data,44);ButtonBinding.Macro((ushort)(profiles+1)).CopyTo(data,48);
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
}
