using LogiBoardToolBox.Controls;
using LogiBoardToolBox.Core.Devices;
using LogiBoardToolBox.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace LogiBoardToolBox.Utilities;

internal static partial class UiVerification
{
    private static async Task VerifyDeviceArtworkAsync(MainPage page, WorkspaceHost tabs, Func<string, Task> capture,
        Action<string, bool, string> check)
    {
        var vm = page.ViewModel;
        var map = Tree(page).OfType<MouseMapView>().Single();
        foreach (var model in MouseCatalog.Models)
        {
            vm.LoadCatalogVerificationFixture(model); vm.IsSideView = false; tabs.SelectedIndex = 0;
            await Ready(model.ImageAsset);
            var images = Tree(map).OfType<Image>().Where(i => i.Source is BitmapImage b &&
                b.UriSource?.AbsolutePath == model.ImageAsset && Visible(i)).ToArray();
            check(model.Id + "-decoded-artwork", images.Length == 1 && images[0].Source is BitmapImage bitmap &&
                bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0, model.ImageAsset);
            check(model.Id + "-no-placeholder", Tree(map).OfType<FrameworkElement>().Single(e => e.Name == "GenericSilhouette").Visibility == Visibility.Collapsed,
                "The actual resource decoded; URI presence alone is insufficient.");
            check(model.Id + "-correct-model", vm.DeviceModel.Id == model.Id, vm.DeviceModel.DisplayName);
            await capture(model.Id + "-artwork");
            if (model.HasLighting)
            {
                tabs.SelectedIndex = 3;
                await Task.Delay(80); page.UpdateLayout();
                check(model.Id + "-lighting-artwork", Tree(page).OfType<LightsyncMapView>().Any(surface =>
                    Tree(surface).OfType<Image>().Any(i => Visible(i) && i.Source is BitmapImage b &&
                        b.UriSource?.AbsolutePath == model.ImageAsset && b.PixelWidth > 0)), "LIGHTSYNC uses this same model's resource.");
            }
        }
        vm.LoadCatalogVerificationFixture(MouseCatalog.ById("G502Hero")!); tabs.SelectedIndex = 0;
        map.DeviceModel = MouseCatalog.ById("G502Hero")! with { ImageAsset = "/Assets/Devices/Catalog/missing-verification.png" };
        var hint = Tree(map).OfType<TextBlock>().Single(t => t.Name == "ArtworkHint");
        for (var i = 0; i < 100 && hint.Visibility != Visibility.Visible; i++) await Task.Delay(30);
        check("missing-artwork-explained", hint.Visibility == Visibility.Visible && hint.Text == "设备图片未能加载",
            "A missing resource gets an explicit state and diagnostic record, rather than a silent blank.");
        await capture("missing-artwork-state");
        map.DeviceModel = MouseCatalog.ById("G502Hero")!;
        await Ready(map.DeviceModel.ImageAsset);
        check("resource-failure-does-not-stick-to-next-model", hint.Visibility == Visibility.Collapsed, "Switching back to a valid resource recovers the image.");
        check("no-physical-input-engines", !vm.HostMacrosRunning && !vm.LightsyncRunning, "Isolated catalogue data, no HID enumeration or input hooks.");

        async Task Ready(string asset)
        {
            for (var i = 0; i < 150; i++)
            {
                page.UpdateLayout();
                if (Tree(map).OfType<Image>().Any(im => Visible(im) && im.Source is BitmapImage b && b.UriSource?.AbsolutePath == asset && b.PixelWidth > 0)) return;
                await Task.Delay(30);
            }
            throw new IOException("Image did not decode: " + asset);
        }
        bool Visible(FrameworkElement element)
        {
            DependencyObject? current = element;
            while (current is not null && !ReferenceEquals(current, page))
            {
                if (current is FrameworkElement e && e.Visibility != Visibility.Visible) return false;
                current = VisualTreeHelper.GetParent(current);
            }
            return true;
        }
    }
}
