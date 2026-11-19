using System.Text.Json.Nodes;
using System.Threading.Channels;
using LogiBoardToolBox.Core.Devices;
using LogiBoardToolBox.Core.HidPlusPlus;
using LogiBoardToolBox.Core.Lighting;
using LogiBoardToolBox.Core.Macros;
using LogiBoardToolBox.Core.Onboard;
using XFEExtension.NetCore.XUnit.Assertions;
using XFEExtension.NetCore.XUnit.Attributes;

namespace LogiBoardToolBox.Test;

[TestFixture]
public class MultiDeviceTests
{
    [Test]
    public void PhysicalMouseIdsOverrideSharedReceiverAndAmbiguousNames()
    {
        Assert.Equal("GPW", MouseCatalog.Resolve([0x4079], "G502 X PLUS").Id);
        Assert.Equal("GPW", MouseCatalog.Resolve([0xC539], "Logitech G Pro Wireless Gaming Mouse").Id);
        Assert.Equal("Superlight", MouseCatalog.Resolve([0x4093], "PRO WIRELESS").Id);
        Assert.Equal("G305", MouseCatalog.Resolve([], "G304 LIGHTSPEED").Id);
        Assert.Equal("Superlight2", MouseCatalog.Resolve([], "PRO X SUPERLIGHT 2").Id);
        Assert.Equal(false, MouseCatalog.Resolve([0xC547], "Unrecognized mouse").IsKnown);
        Assert.Equal(false, MouseCatalog.ById("Superlight")!.HasLighting);
        Assert.Equal(false, MouseCatalog.ById("G305")!.HasLighting);
        var ids = MouseCatalog.ReadTransportProductIds([0,1,2,3,4,0,12,0x40,0x79,0xC0,0x88,0,0,0,0,0]);
        Assert.Equal("4079,C088", string.Join(',', ids.Select(i => i.ToString("X4"))));
        Assert.Equal(0, MouseCatalog.ReadTransportProductIds([0,1,2]).Count);
    }

    [Test]
    public void CatalogNamespacesAndBindingsAreUniqueAndGpwSidesAreNotG502Sides()
    {
        var importable = MouseCatalog.Models.Where(m => !string.IsNullOrEmpty(m.SlotPrefix)).ToArray();
        Assert.Equal(importable.Length, importable.Select(m => m.SlotPrefix).Distinct().Count());
        foreach (var model in MouseCatalog.Models)
        {
            Assert.Equal(model.Id, MouseCatalog.Resolve(model.ProductIds, model.DisplayName).Id);
            for (var i = 0; i < model.ButtonNames.Length && i < (model.Layout == "G502X" ? 11 : 16); i++)
            { Assert.Equal(true, model.TryGetButtonIndex("g" + (i+1), out var index)); Assert.Equal(i,index); }
        }
        var gpw = MouseCatalog.ById("GPW")!;
        Assert.Equal(true, gpw.ButtonNames[5].Contains("底部"));
        Assert.Equal(true, gpw.ButtonNames[6].Contains("右侧"));
        Assert.Equal(false, gpw.TryGetButtonIndex("scroll1",out _));
        Assert.Equal(true, MouseCatalog.ById("G502XPlus")!.TryGetButtonIndex("scroll1",out var scroll)); Assert.Equal(12,scroll);
    }

    [Test]
    public void GHubImportsUseEachModelsNamespaceAndPreserveEveryRecordedDelay()
    {
        foreach (var model in MouseCatalog.Models.Where(m => !string.IsNullOrEmpty(m.SlotPrefix)))
        {
            var import = GHubMacroImport.Read(ImportedJson(model.SlotPrefix,"g4"),model.SlotPrefix);
            Assert.Equal(model.SlotPrefix, import.DeviceKey); Assert.Equal(3,import.Bindings.Single().ButtonIndex);
            Assert.Equal("59,198,25,10",string.Join(',',import.Bindings.Single().RecordedDelays));
            Assert.Equal("43003140003B4400314000C643001E40001944001E40000A02FF",Convert.ToHexString(import.Bindings.Single().Compiled.Bytes));
        }
        foreach (var slot in new[] {"g4","g5","g7","g8"})
        { var b=GHubMacroImport.Read(ImportedJson("prowirelessmouse",slot),"prowirelessmouse").Bindings.Single(); Assert.Equal(int.Parse(slot[1..])-1,b.ButtonIndex); }
        Reject<NotSupportedException>(()=>GHubMacroImport.Read(ImportedJson("proxwirelessmouse","g8"),"proxwirelessmouse"));
        Reject<InvalidDataException>(()=>GHubMacroImport.Read(ImportedJson("g305","g4"),"prowirelessmouse"));
    }

    [Test]
    public async Task DifferentProfileCountsAndButtonCountsCommitTheSameHardwareBytecodeAndRestoreExactly()
    {
        foreach (var model in MouseCatalog.Models)
        {
            var original=Fixture.Create(model.Layout=="G502X"?(byte)10:(byte)3);
            // Deliberately vary capacity. These are simulated geometries, not claimed hardware dumps.
            var count=(byte)(model.Id=="G305"?1:model.Layout=="Compact"?3:5);
            // Identity/artwork-only entries deliberately have no claimed button
            // count; exercise those with an arbitrary six-button simulator.
            original.Description[3]=count; original.Description[5]=(byte)(model.Layout=="G502X"?11:model.ButtonNames.Length==0?6:model.ButtonNames.Length);
            original.Sectors[0][4*count]=0xFF;original.Sectors[0][4*count+1]=0xFF;SectorChecksum.Seal(original.Sectors[0]);
            original.DeviceName=model.DisplayName;
            using var fake=new FakeMouse(original); using var client=new HidPlusPlusClient(fake,1);
            var memory=new OnboardMemory(client,original.DeviceIdentity,original.DeviceName);
            var before=await memory.ReadAsync(); var profile=before.GetProfiles()[0].Copy();
            var macro=string.IsNullOrEmpty(model.SlotPrefix) ? MacroCompiler.Compile("tap A") : GHubMacroImport.Read(ImportedJson(model.SlotPrefix,"g4"),model.SlotPrefix).Bindings.Single().Compiled;
            var plan=MemoryWritePlan.Create(before,profile,[new(3,false,macro)],before.Capabilities.IsExperimental);
            using var scratch=new ScratchDirectory(); await memory.CommitAsync(plan,scratch.Path);
            var after=await memory.ReadAsync(); var binding=after.GetProfiles()[0].GetBinding(3);
            Assert.Equal(Convert.ToHexString(macro.Bytes),Convert.ToHexString(MacroAllocator.ReadMacroBytes(after,new(ButtonBinding.MacroSector(binding),ButtonBinding.MacroOffset(binding)))));
            Assert.Equal((byte)1,after.Mode); Assert.Equal((ushort)1,after.ActiveSector);
            foreach (var s in before.Sectors.Keys)
                Assert.Equal(Convert.ToHexString(plan.Changes.SingleOrDefault(c=>c.Sector==s)?.After??before.Sectors[s]),Convert.ToHexString(after.Sectors[s]));
            var restore=MemoryWritePlan.Restore(after,before,before.Capabilities.IsExperimental); await memory.CommitAsync(restore,scratch.Path);
            var restored=await memory.ReadAsync();
            foreach(var s in before.Sectors.Keys) Assert.Equal(Convert.ToHexString(before.Sectors[s]),Convert.ToHexString(restored.Sectors[s]));
        }
    }

    [Test]
    public void MacroAssignmentsRejectButtonsAndLayersNotAdvertisedByFirmware()
    {
        var snapshot=Fixture.Create();snapshot.Description[5]=5;snapshot.Description[9]=0;
        var macro=MacroCompiler.Compile("tap A");
        Reject<ArgumentException>(()=>MacroAllocator.Allocate(snapshot,[new(5,false,macro)]));
        Reject<ArgumentException>(()=>MacroAllocator.Allocate(snapshot,[new(3,true,macro)]));
        snapshot.Description[2]=0;
        Reject<NotSupportedException>(()=>MacroAllocator.Allocate(snapshot,[new(3,false,macro)]));
        var unknown=Fixture.Create(10);unknown.Description[5]=5;Assert.Equal(false,unknown.Capabilities.CanEditProfiles);
    }

    [Test]
    public void AnimationScreenAndAudioFramesUseActualZoneCount()
    {
        foreach(var count in new[]{1,2,8,12})
        {
            var s=new LightingSettings{Mode=LightsyncMode.Animation,Period=1000,Frames=[new(0,Enumerable.Repeat(new Rgb(255,0,0),8).ToArray()),new(500,new Rgb[8])]}.ForZoneCount(count);
            s.Validate(); Assert.Equal(count,LightingRenderer.Render(s,250).Length);Assert.Equal(new Rgb(128,0,0),LightingRenderer.Render(s,250)[0]);
            var colors=Enumerable.Repeat(new Rgb(1,2,3),count).ToArray();
            Assert.Equal(true,LightingRenderer.Render(s with{Mode=LightsyncMode.Preset,Effect=LightingEffect.ScreenSampler},0,colors).SequenceEqual(colors));
            Assert.Equal(count,LightingRenderer.Render(s with{Mode=LightsyncMode.Preset,Effect=LightingEffect.AudioVisualizer},0,audio:.5).Length);
        }
    }

    [Test]
    public async Task LegacyAndModernClusterLightsUseAdvertisedStaticIndicesAndNeverPersistSoftwareFrames()
    {
        foreach(var modern in new[]{false,true})
        {
            using var transport=new ClusterLights(modern);using var client=new HidPlusPlusClient(transport,1);
            var lights=new LightsyncDevice(client); var caps=await lights.ReadAsync();Assert.Equal(2,caps.ZoneCount);
            await lights.ClaimAsync();await lights.SendFrameAsync([new(10,20,30),new(40,50,60)]);
            var fn=modern?1:3;var frames=transport.Requests.Where(r=>r[2]==2&&r[3]>>4==fn).TakeLast(2).ToArray();
            Assert.Equal("00020A141E",Convert.ToHexString(frames[0].AsSpan(4,5)));Assert.Equal("010228323C",Convert.ToHexString(frames[1].AsSpan(4,5)));
            Assert.Equal(true,frames.All(r=>r[16]==(modern?1:0)));
            await lights.ReleaseAsync();
            Assert.Equal(false,transport.Requests.Any(r=>r[2]==2&&r[3]>>4==6));
            if(!modern)
            { var restores=transport.Requests.Where(r=>r[2]==2&&r[3]>>4==3).TakeLast(2).ToArray();Assert.Equal((byte)0xAA,restores[0][6]);Assert.Equal((byte)0,restores[0][16]);Assert.Equal((byte)0,transport.Requests.Last()[4]); }
        }
    }

    private static string ImportedJson(string prefix,string slot)
    {
        var root=JsonNode.Parse("""
        {"profiles/auto_switching/s_last_used_profile":"p","profiles":{"profiles":[{"id":"p","name":"Default","assignments":[{"slotId":"","cardId":"m"}]}]},
        "cards":{"cards":[{"id":"m","name":"Recorded","attribute":"MACRO_PLAYBACK","macro":{"type":"SEQUENCE","sequence":{"useRepeatActions":true,"heldSequence":{"components":[
        {"keyboard":{"hidUsage":"49","isDown":true}},{"delay":{"durationMs":59}},{"keyboard":{"hidUsage":"49"}},{"delay":{"durationMs":198}},
        {"keyboard":{"hidUsage":"30","isDown":true}},{"delay":{"durationMs":25}},{"keyboard":{"hidUsage":"30"}},{"delay":{"durationMs":10}}]}}}}]}}
        """)!;
        root["profiles"]!["profiles"]![0]!["assignments"]![0]!["slotId"]=prefix+"_"+slot+"_m1";
        return root.ToJsonString();
    }
    private static void Reject<T>(Action action) where T:Exception {try{action();}catch(T){return;}throw new Exception("Expected "+typeof(T).Name);}

    private sealed class ClusterLights(bool modern):IHidTransport
    {
        private readonly Channel<byte[]> replies=Channel.CreateUnbounded<byte[]>();
        public List<byte[]> Requests {get;}=[];
        public int InputReportLength=>20;public int OutputReportLength=>20;
        public Task WriteAsync(byte[] r,CancellationToken ct)
        {
            Requests.Add(r.ToArray());var b=new byte[20];r.AsSpan(0,4).CopyTo(b);var fn=r[3]>>4;
            if(r[2]==0){var id=(r[4]<<8)|r[5];b[4]=(byte)(id==(modern?0x8071:0x8070)?2:0);}
            else if(r[2]==2)
            {
                if(fn==0 && (!modern||r[4]==255)){b[modern?6:4]=2;b[modern?10:8]=1;}
                else if(fn==(modern?0:1) && (!modern||r[5]==255)){b[4]=r[4];b[modern?8:7]=3;}
                else if(fn==(modern?0:2)){b[4]=r[4];b[5]=r[5];b[7]=new byte[]{0,3,1}[r[5]];}
                else if(!modern&&fn==14){b[4]=r[4];b[5]=2;b[6]=0xAA;b[7]=0xBB;b[8]=0xCC;}
                else if(modern&&fn==5){b[4]=r[4];}
            }
            replies.Writer.TryWrite(b);return Task.CompletedTask;
        }
        public async Task<byte[]> ReadAsync(CancellationToken ct)=>await replies.Reader.ReadAsync(ct);
        public void Dispose()=>replies.Writer.TryComplete();
    }
}
