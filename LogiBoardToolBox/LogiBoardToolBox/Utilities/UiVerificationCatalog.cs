using System.Runtime.InteropServices.WindowsRuntime;
using LogiBoardToolBox.Core.Diagnostics;
using LogiBoardToolBox.Core.Devices;
using LogiBoardToolBox.Controls;
using LogiBoardToolBox.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace LogiBoardToolBox.Utilities;

internal static partial class UiVerification
{
    internal static readonly List<string> CatalogUnhandledErrors = [];
    public static async Task RunCatalogReviewAsync(MainPage page,WorkspaceHost tabs)
    {
        if(running)return;running=true;Directory.CreateDirectory(OutputDirectory);
        var checks=new List<object>();var vm=page.ViewModel;
        GetWindowThreadProcessId(GetForegroundWindow(),out var foregroundStart);
        try
        {
            if (args.Contains("--workspace-review"))
            {
                await VerifyWorkspaceReviewAsync(page, tabs, Capture, Check);
                Check("no-unhandled-ui-errors", CatalogUnhandledErrors.Count == 0, string.Join("\n", CatalogUnhandledErrors));
                await SaveResult(true, null);
                return;
            }
            if (args.Contains("--runtime-check"))
            {
                await VerifyNativeRuntimeAsync(Check);
                Check("no-unhandled-ui-errors", CatalogUnhandledErrors.Count == 0, string.Join("\n", CatalogUnhandledErrors));
                await SaveResult(true, null);
                return;
            }
            if (args.Contains("--g304x-snapshot"))
            {
                await VerifyG304XDpiSaveAsync(page, tabs, Capture, Check);
                Check("no-unhandled-ui-errors", CatalogUnhandledErrors.Count == 0, string.Join("\n", CatalogUnhandledErrors));
                await SaveResult(true, null);
                return;
            }
            if (args.Contains("--live-dpi-replay"))
            {
                await VerifyLiveDpiAsync(page, tabs, Capture, Check);
                await VerifyMouseButtonPresentationAsync(page, tabs, Capture, Check);
                Check("no-unhandled-ui-errors", CatalogUnhandledErrors.Count == 0, string.Join("\n", CatalogUnhandledErrors));
                await SaveResult(true, null);
                return;
            }
            if (args.Contains("--pro2-lighting-replay"))
            {
                await VerifyPro2LightingAsync(page, tabs, Capture, Check);
                Check("no-unhandled-ui-errors", CatalogUnhandledErrors.Count == 0, string.Join("\n", CatalogUnhandledErrors));
                await SaveResult(true, null);
                return;
            }
            if (args.Contains("--g502-map-replay"))
            {
                await VerifyClassicG502MapAsync(page, tabs, Capture, Check);
                Check("no-unhandled-ui-errors", CatalogUnhandledErrors.Count == 0, string.Join("\n", CatalogUnhandledErrors));
                await SaveResult(true, null);
                return;
            }
            if(args.Contains("--artwork-only"))
            {
                await VerifyDeviceArtworkAsync(page,tabs,Capture,Check);
                Check("no-unhandled-ui-errors",CatalogUnhandledErrors.Count==0,string.Join("\n",CatalogUnhandledErrors));
                await SaveResult(true,null);return;
            }
            if(args.Contains("--lightspeed-only"))
            {
                await VerifyLightspeedMacroSaveAsync(page,tabs,Capture,Check);
                Check("no-unhandled-ui-errors",CatalogUnhandledErrors.Count==0,string.Join("\n",CatalogUnhandledErrors));
                await SaveResult(true,null);return;
            }
            if (args.Contains("--g102-snapshot"))
            {
                await VerifyG102MacroSaveAsync(page, tabs, Capture, Check);
                Check("no-unhandled-ui-errors", CatalogUnhandledErrors.Count == 0, string.Join("\n", CatalogUnhandledErrors));
                await SaveResult(true, null);
                return;
            }
            if(args.Contains("--lua-profile-only"))
            {
                await VerifyLuaProfileUpdateAsync(page,tabs,Capture,Check);
                Check("no-unhandled-ui-errors",CatalogUnhandledErrors.Count==0,string.Join("\n",CatalogUnhandledErrors));
                await SaveResult(true,null);return;
            }
            if(args.Contains("--native-toggle-only"))
            {
                await VerifyToggleAsync(page,tabs,Capture,Check);
                await SaveResult(true,null);return;
            }
            foreach(var model in MouseCatalog.Models)
            {
                vm.LoadCatalogVerificationFixture(model);tabs.SelectedIndex=0;
                Check(model.Id+"-identity-and-count",vm.DeviceModel.Id==model.Id && vm.StandardBindings.Count==(model.Layout=="G502X"?13:model.ButtonNames.Length==0?6:model.ButtonNames.Length),$"{vm.DeviceModel.Id}/{vm.DeviceModel.SlotPrefix}; bindings={vm.StandardBindings.Count}");
                Check(model.Id+"-lighting-capability",vm.HasRgbLighting==model.HasLighting,model.LightingZoneCount.ToString());
                await Capture(model.Id+"-assignments");
            }
            await VerifyLegacyAndBindingLabelsAsync(page,tabs,Capture,Check);
            await VerifyToggleAsync(page,tabs,Capture,Check);
            await VerifyOnboardLoopCadenceAsync(page,tabs,Check);
            vm.LoadCatalogVerificationFixture(MouseCatalog.ById("GPW")!);tabs.SelectedIndex=0;
            vm.ShowGShift=true;await Capture("GPW-gshift");vm.ShowGShift=false;
            await VerifyMacroSaveAsync(page,tabs,Capture,Check);
            tabs.SelectedIndex=2;await Capture("GPW-dpi");
            var beforeBrightness=vm.LightsyncBrightness;var beforePeriod=vm.LightsyncPeriod;
            tabs.SelectedIndex=3;await Capture("GPW-lighting");
            Check("GPW-lighting-uses-current-model",Tree(page).OfType<LightsyncMapView>().All(m=>m.DeviceModel?.Id=="GPW"),"Main lighting map and animation map follow the current mouse.");
            Check("opening-lights-does-not-edit-values",vm.LightsyncBrightness==beforeBrightness&&vm.LightsyncPeriod==beforePeriod&&beforeBrightness==100&&beforePeriod==4000,$"brightness={vm.LightsyncBrightness}, period={vm.LightsyncPeriod}");
            var effectBox=Tree(page).OfType<ComboBox>().Single(b=>b.Header as string=="效果");
            Check("lighting-effect-control-displays-selection",ReferenceEquals(effectBox.SelectedItem,vm.SelectedLightsyncEffect),effectBox.SelectedItem?.ToString()??"Empty selection");
            Check("lighting-sliders-display-values",Tree(page).OfType<PrecisionSlider>().Where(s=>s.Tag?.ToString()=="Brightness").All(s=>s.Value==100),"Brightness control must display the actual value.");
            vm.SelectedLightsyncMode=vm.LightsyncModes.Single(m=>m.Id=="Freestyle");await Capture("GPW-zones");
            Check("GPW-two-real-zones",vm.LedZones.Count==2 && vm.BuildLightsyncSettings().ZoneColors.Length==2,"Two-zone settings; no eight-zone assumptions.");
            page.OpenAnimationEditor(true);await Capture("GPW-animation");
            Check("GPW-animation-two-colors",vm.AnimationFrames.All(f=>f.Colors.Length==2),"The editor uses two colors per frame.");
            Invoke(page,"animation-cancel");
            tabs.SelectedIndex=4;await Capture("GPW-settings");
            await VerifyDiagnosticsAsync(page,tabs,Capture,Check);
            await VerifyLuaWorkspaceAsync(page,tabs,Capture,Check);
            vm.LoadCatalogVerificationFixture(MouseCatalog.ById("Superlight")!);tabs.SelectedIndex=0;
            Check("device-switch-clears-prior-bindings",vm.StandardBindings.Count==5 && vm.StandardBindings.All(b=>b.Index<5),"GPW right-side and bottom buttons are absent on SUPERLIGHT.");
            Check("non-rgb-model-hides-lightsync",Tree(page).OfType<Button>().Single(b=>b.Name=="LightsyncNav").Visibility==Visibility.Collapsed,"SUPERLIGHT has no LIGHTSYNC navigation.");
            Check("no-input-engines",!vm.HostMacrosRunning&&!vm.LightsyncRunning,"Offline fixtures only; no HID enumeration or input injection.");
            Check("no-unhandled-ui-errors",CatalogUnhandledErrors.Count==0,string.Join("\n",CatalogUnhandledErrors));
            await SaveResult(true,null);
        }
        catch(Exception error){await SaveResult(false,error.ToString());}
        finally {App.MainWindow.Close();}
        void Check(string name,bool passed,string detail)
        {checks.Add(new{Name=name,Passed=passed,Detail=detail});if(!passed)throw new InvalidOperationException(name+": "+detail);}
        async Task SaveResult(bool success,string? error)
        {
            GetWindowThreadProcessId(GetForegroundWindow(),out var foregroundEnd);
            await File.WriteAllTextAsync(Path.Combine(OutputDirectory,"result.json"),DiagnosticJson.Serialize(new{FunctionalChecksPassed=success,Error=error,HardwareAccess=false,InputGenerated=false,InputIntercepted=false,SimulatedDeviceData=true,Capture="WinUI RenderTargetBitmap",BuildSha256=await GetBuildHashAsync(),DynamicCodeSupported=System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported,ForegroundAtStart=foregroundStart,ForegroundAtEnd=foregroundEnd,Checks=checks},indented:true));
        }
        async Task Capture(string name)
        {
            await Task.Delay(400);page.UpdateLayout();var root=(FrameworkElement)App.MainWindow.Content;
            var bitmap=new RenderTargetBitmap();await bitmap.RenderAsync(root);var buffer=await bitmap.GetPixelsAsync();
            if(bitmap.PixelWidth<500||buffer.Length==0)throw new IOException("后台取图没有有效像素。");
            using var stream=new InMemoryRandomAccessStream();var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,buffer.ToArray());await encoder.FlushAsync();
            using var reader=new DataReader(stream.GetInputStreamAt(0));await reader.LoadAsync((uint)stream.Size);var bytes=new byte[(int)stream.Size];reader.ReadBytes(bytes);
            await File.WriteAllBytesAsync(Path.Combine(OutputDirectory,name+".png"),bytes);
            var nodes=Tree(page).OfType<FrameworkElement>().Where(e=>e.ActualWidth>0&&IsShown(e)).Select(e=>
            {
                var text=e switch {TextBlock t=>t.Text,Button b=>b.Content as string,TextBox b=>b.Text,
                    ToggleButton b=>b.Content as string,InfoBar { IsOpen: true } b=>b.Title+" · "+b.Message,
                    ToggleSwitch b=>(b.IsOn?b.OnContent:b.OffContent) as string,_=>null};
                var rect=e.TransformToVisual(root).TransformBounds(new(0,0,e.ActualWidth,e.ActualHeight));
                return new{Type=e.GetType().Name,e.Name,AutomationId=Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(e),Text=text,rect.X,rect.Y,rect.Width,rect.Height};
            }).Where(e=>!string.IsNullOrWhiteSpace(e.Text)).ToArray();
            var images=Tree(page).OfType<Image>().Select(i=>new{i.Name,Source=(i.Source as BitmapImage)?.UriSource?.ToString(),Visibility=i.Visibility.ToString()}).ToArray();
            await File.WriteAllTextAsync(Path.Combine(OutputDirectory,name+".layout.json"),DiagnosticJson.Serialize(new{Model=vm.DeviceModel.Id,SimulatedDeviceData=true,vm.IsBusy,vm.IsIdle,MacroEditors=Tree(page).OfType<MacroEditorView>().Select(e=>new{e.IsEnabled,e.Opacity}).ToArray(),Width=bitmap.PixelWidth,Height=bitmap.PixelHeight,Images=images,Nodes=nodes},indented:true));
        }
        static bool IsShown(FrameworkElement element)
        {
            for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
                if (current is UIElement { Visibility: Visibility.Collapsed }) return false;
            return true;
        }
    }
}
