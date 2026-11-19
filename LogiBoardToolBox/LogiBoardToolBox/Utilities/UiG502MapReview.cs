using LogiBoardToolBox.Controls;
using LogiBoardToolBox.Core.Devices;
using LogiBoardToolBox.Core.Onboard;
using LogiBoardToolBox.Views;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace LogiBoardToolBox.Utilities;

internal static partial class UiVerification
{
    private static async Task VerifyClassicG502MapAsync(MainPage page, WorkspaceHost tabs, Func<string, Task> capture,
        Action<string, bool, string> check)
    {
        var index = Array.IndexOf(args, "--g502-map-replay") + 1;
        if (index >= args.Length) throw new ArgumentException("需要 G502 HERO 诊断快照。");
        var captured = await OnboardMemory.LoadBackupAsync(args[index]);
        var profile = captured.GetProfiles().Single(p => p.Sector == captured.ActiveSector);
        var vm = page.ViewModel;
        var map = Tree(page).OfType<MouseMapView>().Single();
        var image = Tree(map).OfType<Image>().Single(i => i.Name == "ModelImage");
        var canvas = Tree(map).OfType<Canvas>().Single(c => c.Name == "Callouts");
        tabs.SelectedIndex = 0;
        vm.LoadClassicG502DiagnosticSnapshot(captured);
        var replay = vm.AttachDiagnosticReplay();
        try
        {
            check("g502-hero-captured-slot", vm.DeviceModel.Id == "G502Hero" && vm.SelectedConfiguration?.Key == "board-1",
                "The reported G502 HERO and board slot 1 are loaded.");
            foreach (var i in new[] { 4, 5 })
                vm.StandardBindings[i].FriendlyName = "A"; // The diagnostic names the shared recorded macro.
            var normal = await VerifyPoints("G502Hero", false);
            check("g502-hero-normal-records", vm.StandardBindings.All(r => r.BuildBinding().SequenceEqual(profile.GetBinding(r.Index))),
                "The screenshot's macros, remapped DPI buttons, and both wheel tilts follow the original board records.");
            await capture("G502Hero-corrected");

            vm.ShowGShift = true;
            var shifted = await VerifyPoints("G502Hero-gshift", true);
            check("g502-hero-gshift-keeps-positions", shifted.SequenceEqual(normal) &&
                vm.ShiftBindings.All(r => r.BuildBinding().SequenceEqual(profile.GetBinding(r.Index, true))),
                "The G-SHIFT layer uses the same physical locations and its own captured bindings.");
            await capture("G502Hero-gshift-corrected");
            vm.ShowGShift = false;
            var root = (FrameworkElement)App.MainWindow.Content;
            var size = (root.Width, root.Height);
            try
            {
                var scale = App.MainWindow.DisplayScale;
                root.Width = 1584 / scale;
                root.Height = 891 / scale;
                page.UpdateLayout();
                await VerifyPoints("G502Hero-window", false);
                await capture("G502Hero-window-corrected");
            }
            finally
            {
                root.Width = size.Width;
                root.Height = size.Height;
                page.UpdateLayout();
            }
            await vm.FlushImmediateSaveAsync();
            check("g502-map-is-read-only", replay.WriteStarts == 0 && replay.Requests == 0 &&
                replay.Sectors.All(p => p.Value.SequenceEqual(captured.Sectors[p.Key])), "Map drawing and G-SHIFT navigation leave the replay storage untouched.");
        }
        finally { vm.DetachDiagnosticReplay(); }

        var factory = captured.Sectors[0x101];
        foreach (var id in new[] { "G502Core", "G502Spectrum", "G502Wireless" })
        {
            vm.LoadCatalogVerificationFixture(MouseCatalog.ById(id)!);
            foreach (var row in vm.StandardBindings)
            {
                row.SelectedAction = ButtonBindingEditor.ActionChoices[0];
                row.Value = "";
                row.RefreshOriginal(factory.AsSpan(32 + row.Index * 4, 4).ToArray());
            }
            if (id == "G502Wireless") vm.StandardBindings[8].RefreshOriginal(ButtonBinding.Special(12));
            await VerifyPoints(id, false);
            await capture(id + "-corrected");
        }

        async Task<Point[]> VerifyPoints(string scenario, bool gshift)
        {
            await Until(() => image.Visibility == Visibility.Visible && image.Source is BitmapImage bitmap &&
                bitmap.UriSource?.AbsolutePath == vm.DeviceModel.ImageAsset && bitmap.PixelWidth == 258, scenario + " artwork");
            page.UpdateLayout();
            var bounds = image.TransformToVisual(canvas).TransformBounds(new Rect(0, 0, image.ActualWidth, image.ActualHeight));
            var controls = canvas.Children.OfType<Button>().Where(b => b.Tag is ButtonBindingEditor)
                .ToLookup(b => ((ButtonBindingEditor)b.Tag).Index);
            var rows = gshift ? vm.ShiftBindings : vm.StandardBindings;
            check(scenario + "-eleven-editable-records", controls.Count == 11 && rows.Count == 11 &&
                rows.All(r => controls[r.Index].Count() == 2 && controls[r.Index].All(b => b.AllowDrop && ReferenceEquals(b.Tag, r))),
                "Each physical record has one marker and one editable action label for the current layer.");
            var regions = new (double Left, double Right, double Top, double Bottom)[]
            {
                (.31, .44, .17, .27), (.71, .86, .17, .27), (.52, .63, .28, .39),
                (.16, .25, .60, .69), (.12, .19, .46, .56), (.11, .17, .37, .44),
                (.17, .25, .34, .39), (.17, .25, .27, .33), (.53, .63, .48, .55),
                (.68, .75, .30, .35), (.41, .48, .30, .35)
            };
            var points = rows.Select(row =>
            {
                var marker = controls[row.Index].Single(b => b.Content is null);
                return new Point((Canvas.GetLeft(marker) + marker.Width / 2 - bounds.X) / bounds.Width,
                    (Canvas.GetTop(marker) + marker.Height / 2 - bounds.Y) / bounds.Height);
            }).ToArray();
            check(scenario + "-anchors-on-controls", points.Select((p, i) =>
            {
                var area = regions[i];
                return p.X >= area.Left && p.X <= area.Right && p.Y >= area.Top && p.Y <= area.Bottom;
            }).All(correct => correct),
                string.Join(" · ", points.Select((p, i) => $"#{i + 1} ({p.X:F3},{p.Y:F3})")));
            check(scenario + "-identifiable-physical-labels", rows.Skip(3).Take(6).All(row => controls[row.Index].Any(b =>
                    b.Content is StackPanel panel && panel.Children.OfType<TextBlock>().Any(t => t.Text.StartsWith($"G{row.Index + 1} · ")))) &&
                AutomationProperties.GetName(controls[9].First()).Contains("滚轮右倾") &&
                AutomationProperties.GetName(controls[10].First()).Contains("滚轮左倾"),
                "The physical G4–G9 labels stay visible with remapped actions; the two wheel directions remain distinct.");
            return points;
        }
    }
}
